using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cycles;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async Task RunProductionInspectionsAsync()
    {
        var options = _productionInspectionOptions!;
        ProductionInspectionOwner? owner = null;
        try
        {
            await _storeInitialization.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            await Task.WhenAll(WaitForRecipeActivationStartupAsync(), WaitForPreviewStartupAsync(),
                WaitForManualInspectionStartupAsync(), WaitForStationQualificationStartupAsync(), WaitForRecipeSelectionStartupAsync())
                .WaitAsync(_lifetime.Token).ConfigureAwait(false);
            await FinalizeInterruptedRecipeChangesAsync(_lifetime.Token).ConfigureAwait(false);
            await FinalizeInterruptedProductionArmAttemptsAsync(_lifetime.Token).ConfigureAwait(false);
            if (_outboxWorker is { } outboxWorker)
                await outboxWorker.Startup.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            if (options.ImageStage is not null)
            {
                await ReadProductionInspectionPolicyAsync(_lifetime.Token).ConfigureAwait(false);
                await VerifyProductionImageStartupAsync(_lifetime.Token).ConfigureAwait(false);
            }
            lock (_sync)
                if (!ProductionStartupDependenciesReconciledLocked())
                    throw new InvalidOperationException("ProductionInspectionStartupDependencyUnavailable");
            // A cold start reads the authoritative ledger before opening a socket.
            // An incomplete old cycle is never reconstructed as executable work.
            var history = new SqliteProductionInspectionHistoryQuery(_productionInspectionStoreOptions!);
            var current = await history.ReadCurrentAsync(_lifetime.Token).ConfigureAwait(false);
            if (!current.Available || current.RecoveryRequired)
            {
                await RefreshProductionRecoveryAsync(_lifetime.Token).ConfigureAwait(false);
                throw new InvalidOperationException(current.Available
                    ? "ProductionInspectionStartupRecoveryRequired" : current.ReasonCode);
            }
            var keys = new HashSet<PlcControllerCycle>();
            long after = 0;
            long? through = null;
            do
            {
                var page = await history.QueryAsync(new(AfterPosition: after,
                    ThroughPosition: through, PageSize: 128), _lifetime.Token).ConfigureAwait(false);
                if (!page.Available || page.RecoveryRequired)
                    throw new InvalidOperationException("ProductionInspectionHistoryUnavailable");
                through ??= page.ThroughPosition;
                foreach (var entry in page.Events)
                    if (entry.Admission.EndpointBindingHash == options.Profile.EndpointBindingHash)
                        keys.Add(entry.Admission.ControllerCycle);
                if (page.NextAfterPosition is not { } next) break;
                if (next <= after) throw new InvalidOperationException("ProductionInspectionHistoryCursorInvalid");
                after = next;
            } while (true);
            await ReadProductionInspectionPolicyAsync(_lifetime.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (_disposed || _shutdownRequested) return;
                owner = new(_snapshot.RuntimeEpoch, _lifetime.Token, _productionInspectionExecutionOptions!);
                _productionInspectionOwner = owner;
                _productionInspectionStartupVerified = true;
                PublishLocked(_snapshot with { Recovery = RecoveryState.None,
                    Evidence = _snapshot.Evidence with { State = HealthState.Healthy },
                    AdmissionBlockers = new AdmissionBlockers(_snapshot.AdmissionBlockers
                        .Where(value => value != "StartupRecoveryNotVerified")) });
            }
            await RunProductionProtocolAsync(owner, keys).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_sync)
            {
                _productionInspectionStartupVerified = false;
                _productionInspectionRecoveryBlocked = true;
                if (owner is not null) AbortProductionInspectionLocked(owner, ProductionFailureReason(exception));
                else PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                    Recovery = RecoveryState.Required,
                    AdmissionBlockers = new AdmissionBlockers(_snapshot.AdmissionBlockers
                        .Append(ProductionFailureReason(exception)).Distinct(StringComparer.Ordinal)) });
            }
        }
        finally
        {
            if (owner is not null)
            {
                await RetireProductionCameraAsync(owner).ConfigureAwait(false);
                await RetirePartIdentityOperationAsync(owner).ConfigureAwait(false);
                if (owner.ExecutionCancellationTask is { } cancellation)
                    await cancellation.ConfigureAwait(false);
                owner.ExecutionCancellation.Dispose();
                owner.Cancellation.Dispose();
            }
        }
    }

    private async Task<TraceStoragePolicySnapshot> ReadProductionInspectionPolicyAsync(CancellationToken token)
    {
        var options = _productionInspectionOptions!;
        var store = _productionInspectionStoreOptions!;
        var read = await new SqliteTraceStoragePolicyQuery(store).ReadAsync(null, token).ConfigureAwait(false);
        if (!read.Available || read.Snapshot is not { } policy || policy.Version != options.TracePolicyVersion ||
            policy.ContentHash != options.TracePolicySnapshotHash || !Outbox.ProductionOutboxBinding.RoutesMatch(store, policy))
            throw new InvalidOperationException("ProductionInspectionStoragePolicyUnavailable");
        lock (_sync) _productionInspectionPolicy = policy;
        return policy;
    }

    private async Task RunProductionProtocolAsync(ProductionInspectionOwner owner,
        IReadOnlyCollection<PlcControllerCycle> knownKeys)
    {
        var profile = _productionInspectionOptions!.Profile;
        var token = owner.Cancellation.Token;
        var communication = owner.Communication = CreateProductionCommunicationOwner(owner);
        await using var channel = new ModbusQualificationChannel(profile,
            start => StartProductionCycleWrite(owner, start),
            start => StartProductionModbusRequest(owner, start), ShouldReadProductionPartIdentity);
        var output = new InspectionCycleOutputLatch(channel.WriteStateAsync, channel.WriteRecipeChangeOwnedStateAsync);
        var initialized = false;
        InspectionCycleRequestObserver? observer = null;
        try
        {
            await channel.ConnectAsync(token).ConfigureAwait(false);
            var initial = await channel.ReadRuntimeStateAsync(token).ConfigureAwait(false);
            if (initial.QualificationReady || initial.ProductionReady || initial.Busy || initial.ResultValid ||
                initial.CycleFault || initial.ProtocolViolation)
            {
                // Residual runtime-owned state is an unresolved session even without an
                // InspectionId in this process. Preserve the recovery requirement on refusal.
                lock (_sync)
                    if (ReferenceEquals(_productionInspectionOwner, owner))
                        _productionInspectionRecoveryBlocked = true;
                throw new InvalidOperationException("ProductionInspectionInitialControllerStateNotClear");
            }
            await output.ChangeAsync(token).ConfigureAwait(false);
            initialized = true;
            try { await communication.SynchronizeAsync(channel, token).ConfigureAwait(false); }
            catch (Exception exception) when (profile.RecipeChange is not null &&
                exception is not OutOfMemoryException && !token.IsCancellationRequested)
            {
                // Dedicated fields can prevent initial synchronization before a handshake or
                // InspectionId exists. Keep that failed session in recovery without clearing it.
                lock (_sync)
                    if (ReferenceEquals(_productionInspectionOwner, owner) && !_disposed &&
                        !_shutdownRequested && !_lifetime.IsCancellationRequested && !token.IsCancellationRequested)
                        _productionInspectionRecoveryBlocked = true;
                throw;
            }
            await InitializeRecipeChangeHandshakeAsync(owner, channel, owner.Health!.ControllerEpoch!.Value, token).ConfigureAwait(false);
            lock (_sync) ProjectProductionProgressLocked(owner);
            observer = owner.Observer = new(ct => ReadProductionAndRecipeChangeAsync(owner, channel, output, ct), profile.PollInterval,
                knownKeys, () => owner.Coordinator.SetPhase(InspectionCyclePhase.Accepted),
                reason => { lock (_sync) AbortProductionInspectionLocked(owner, reason); },
                token, communication.RequireHealthy);
            observer.Start();
            while (observer.Latest.Sequence == 0)
            {
                observer.RequireHealthy();
                await Task.Delay(profile.PollInterval, token).ConfigureAwait(false);
            }
            var accepting = false;
            var nextAdmissionRefresh = DateTimeOffset.MinValue;
            lock (_sync) ConsiderStartupProductionArmLocked(owner);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                observer.RequireHealthy();
                bool closing;
                lock (_sync) closing = _shutdownRequested && owner.Current is null;
                if (closing)
                {
                    observer.StopAccepting();
                    await output.ChangeAsync(token, ready: false).ConfigureAwait(false);
                    break;
                }
                if (DateTimeOffset.UtcNow >= nextAdmissionRefresh)
                {
                    await RefreshProductionAdmissionAsync(token).ConfigureAwait(false);
                    nextAdmissionRefresh = DateTimeOffset.UtcNow.AddSeconds(1);
                }
                await ProcessAutomaticProductionArmAsync(owner, channel, output, token).ConfigureAwait(false);
                ManualMaintenanceArmCapability? invalidManual;
                lock (_sync) invalidManual = _manualMaintenanceArm is { Terminal: false, Prepared: true } manual &&
                    ReferenceEquals(manual.Owner, owner) && !ManualMaintenanceReadyPermitLocked(owner) ? manual : null;
                if (invalidManual is not null)
                    await FailManualMaintenanceArmAsync(invalidManual, output, "ProductionArmManualReadyPermitRevoked")
                        .ConfigureAwait(false);
                bool eligible;
                lock (_sync) eligible = CanAcceptProductionTriggerLocked(owner);
                if (!observer.IsAccepting && !observer.HasPendingAdmission) accepting = false;
                if (!eligible)
                {
                    observer.StopAccepting();
                    if (accepting) await output.ChangeAsync(token, ready: false).ConfigureAwait(false);
                    lock (_sync)
                        if (_snapshot.Ready) PublishLocked(_snapshot with { Ready = false });
                    accepting = false;
                }
                else if (!accepting)
                {
                    owner.Coordinator.SetPhase(InspectionCyclePhase.AwaitRequest);
                    try { await observer.EnableAcceptingAsync(async () =>
                    {
                        lock (_sync)
                            if (!CanAcceptProductionTriggerLocked(owner))
                                throw new OperationCanceledException("ProductionInspectionTriggerPermitRevoked");
                        await output.ChangeAsync(token, ready: true, busy: false, valid: false).ConfigureAwait(false);
                        lock (_sync)
                        {
                            if (!CanAcceptProductionTriggerLocked(owner))
                                throw new OperationCanceledException("ProductionInspectionTriggerPermitRevoked");
                            ObservePhysicalProductionArmReadyLocked(owner);
                            PublishLocked(_snapshot with { Ready = true });
                        }
                    }, token).ConfigureAwait(false); }
                    catch (OperationCanceledException exception) when (!token.IsCancellationRequested &&
                        owner.Current is null && (exception is PlcRequestRevokedException ||
                            exception.Message is "InspectionCycleAdmissionChanged" or
                                "ProductionInspectionTriggerPermitRevoked"))
                    {
                        observer.RejectPendingAdmission("ProductionTriggerPermitRevoked");
                        await output.ChangeAsync(token, ready: false).ConfigureAwait(false);
                        continue;
                    }
                    await ConfirmAutomaticProductionArmReadyAsync(owner, channel, output).ConfigureAwait(false);
                    await ConfirmManualMaintenanceReadyAsync(owner, output).ConfigureAwait(false);
                    accepting = observer.IsAccepting;
                }
                if (observer.TryTakeAccepted(out var signals) && signals is not null)
                {
                    observer.StopAccepting();
                    accepting = false;
                    try { await AdmitProductionTriggerAsync(owner, signals).ConfigureAwait(false); }
                    catch (PartIdentityAdmissionRejectedException exception)
                    {
                        await RecordRejectedProductionTriggerAsync(owner, signals, exception.Message, exception.Attempt).ConfigureAwait(false);
                        observer.CompleteCycle();
                        if (owner.PartIdentityOperation is { IsCompleted: false })
                            throw new InvalidOperationException("PartIdentitySourceRetirementRequired");
                        owner.PartIdentityAttempt = null;
                        owner.PartIdentityOperation = null;
                        owner.Coordinator.SetPhase(InspectionCyclePhase.AwaitRequest);
                        await output.ChangeAsync(token, ready: false, violation: true).ConfigureAwait(false);
                        continue;
                    }
                    catch (OperationCanceledException exception) when (!token.IsCancellationRequested &&
                        owner.Current is null && exception.Message == "ProductionInspectionTriggerPermitRevoked")
                    {
                        await RecordRejectedProductionTriggerAsync(owner, signals, exception.Message,
                            owner.PartIdentityAttempt).ConfigureAwait(false);
                        observer.RejectPendingAdmission("ProductionTriggerPermitRevoked");
                        observer.CompleteCycle();
                        owner.PartIdentityAttempt = null;
                        await output.ChangeAsync(token, ready: false).ConfigureAwait(false);
                        continue;
                    }
                    channel.ValidatePayloadBinding(owner.Current!.ActivationSnapshot.PlcResultContract);
                    await output.ChangeAsync(token, ready: false, busy: true, valid: false).ConfigureAwait(false);
                    await ExecuteProductionInspectionAsync(owner, (receipt, ct) =>
                        owner.Coordinator.PublishAndAcknowledgeAsync(receipt, owner.Current!.ControllerCycle,
                            observer, output, channel.WritePayloadAsync,
                            fact => RecordProductionDeliveryAsync(owner, fact),
                            profile.AcknowledgementTimeout, profile.PollInterval, ct)).ConfigureAwait(false);
                    if (owner.Coordinator.Phase == InspectionCyclePhase.FaultTerminated)
                        throw new InvalidOperationException("ProductionInspectionCoreNotCommitted");
                    await RetireProductionCameraAsync(owner).ConfigureAwait(false);
                    observer.CompleteCycle();
                    Task? cancellation;
                    CancellationTokenSource retiredCancellation;
                    lock (_sync)
                    {
                        // Atomically detach this cycle's cancellation source before exposing
                        // idle state. A concurrent Abort can never cancel the next cycle.
                        cancellation = owner.ExecutionCancellationTask;
                        retiredCancellation = owner.ExecutionCancellation;
                        owner.CycleRetired?.TrySetResult(!owner.FaultAbortRequested && !_shutdownRequested);
                        owner.Current = null;
                        owner.AdmissionCommitted = false;
                        owner.Core = null;
                        owner.Prepared = null;
                        owner.PartIdentityAttempt = null;
                        owner.PartIdentityOperation = null;
                        owner.ExecutionCancellation = CancellationTokenSource.CreateLinkedTokenSource(owner.Cancellation.Token);
                        owner.ExecutionCancellationTask = null;
                        owner.FaultAbortRequested = false;
                        owner.RetirementDeadline = null;
                        ProjectProductionProgressLocked(owner);
                    }
                    if (cancellation is not null) await cancellation.ConfigureAwait(false);
                    retiredCancellation.Dispose();
                }
                // Drain the bounded observer ring; rejected edges do not allocate InspectionIds.
                // Their protocol-fault flag prevents them being mistaken for queued work.
                while (observer.TakeRejected() is { } rejected)
                {
                    if (rejected.Signals is { } rejectedSignals)
                        await RecordRejectedProductionTriggerAsync(owner, rejectedSignals, rejected.ReasonCode).ConfigureAwait(false);
                    await output.ChangeAsync(token, violation: true).ConfigureAwait(false);
                }
                await Task.Delay(profile.PollInterval, token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var deliveryCompleted = owner.Coordinator.Phase == InspectionCyclePhase.Completed;
            if (_automaticProductionArm is { Terminal: false } interrupted && ReferenceEquals(interrupted.Owner, owner))
                await RejectAutomaticProductionArmAsync(interrupted, channel, output,
                    ProductionArmReason.Interrupted, "ProductionArmOwnerInterrupted").ConfigureAwait(false);
            if (_manualMaintenanceArm is { Terminal: false } interruptedManual && ReferenceEquals(interruptedManual.Owner, owner))
                await FailManualMaintenanceArmAsync(interruptedManual, output, "ProductionArmManualOwnerInterrupted")
                    .ConfigureAwait(false);
            observer?.RejectPendingAdmission(ProductionFailureReason(exception));
            if (!deliveryCompleted) owner.Coordinator.SetPhase(InspectionCyclePhase.FaultTerminated);
            lock (_sync) AbortProductionInspectionLocked(owner, ProductionFailureReason(exception));
            if (observer is not null)
                await observer.DisposeAsync().ConfigureAwait(false);
            // Publish the fault before durable rejection writes can wait on storage.
            // A post-publication fault preserves the immutable payload and unresolved delivery.
            try
            {
                if (initialized) await output.ChangeAsync(CancellationToken.None, ready: false, busy: false,
                    valid: owner.Core is null ? false : null, fault: true).ConfigureAwait(false);
            }
            catch (Exception fault) when (fault is not OutOfMemoryException) { }
            if (observer is not null)
            {
                while (observer.TakeRejected() is { } rejected)
                {
                    if (rejected.Signals is not { } rejectedSignals) continue;
                    try { await RecordRejectedProductionTriggerAsync(owner, rejectedSignals, rejected.ReasonCode).ConfigureAwait(false); }
                    catch (Exception failure) when (failure is not OutOfMemoryException)
                    { MarkAuditFault("PartIdentityRejectionJournalUnavailable", alarmAuthorityUnavailable: true); break; }
                }
            }
            if (owner.Current is null && owner.PartIdentityAttempt is { RejectionRecorded: false } pendingIdentity)
            {
                try { await RecordRejectedProductionTriggerAsync(owner, pendingIdentity.Signals,
                    ProductionFailureReason(exception), pendingIdentity).ConfigureAwait(false); }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                { MarkAuditFault("PartIdentityRejectionJournalUnavailable", alarmAuthorityUnavailable: true); }
            }
            if (owner.Current is not null && !deliveryCompleted)
            {
                await RecordProductionFaultAsync(owner, owner.FailureReason).ConfigureAwait(false);
                await RefreshProductionRecoveryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await RetireProductionCameraAsync(owner).ConfigureAwait(false);
            owner.CycleRetired?.TrySetResult(false);
            if (communication.Failure is not null && !_shutdownRequested && !_lifetime.IsCancellationRequested)
                await communication.RecoverAsync(channel, owner.Current is not null, _lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            if (observer is not null) await observer.DisposeAsync().ConfigureAwait(false);
            await RetireRecipeChangeHandshakeAsync(owner).ConfigureAwait(false);
            owner.Observer = null;
            await communication.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool CanAcceptProductionTriggerLocked(ProductionInspectionOwner owner) =>
        !LocalStopPendingLocked &&
        ProductionOutboxBacklogFailureLocked() is null &&
        AutomaticProductionArmReadyPermitLocked(owner) &&
        ManualMaintenanceReadyPermitLocked(owner) &&
        _activationReservation is null && !_recipeSelectionChangeInProgress && owner.RecipeChangeActivation is null &&
        (_productionInspectionStoreOptions?.RecipeSelections is null || _recipeSelectionStartupVerified) &&
        ReferenceEquals(_productionInspectionOwner, owner) && !owner.Aborted && owner.Current is null &&
        !_disposed && !_shutdownRequested && _productionInspectionStartupVerified &&
        !_productionInspectionRecoveryBlocked && owner.Health is { Healthy: true } &&
        _snapshot.ArmState == ProductionArmState.Armed && _snapshot.ProductionAdmission is { CanArm: true } &&
        _snapshot.Mode == ExclusiveMode.None && !_snapshot.Busy && _snapshot.Recovery == RecoveryState.None &&
        _snapshot.CurrentExecution is null && _activeActivation is { Algorithm.IsRetired: false } &&
        _activeActivation.Snapshot.ProductionAuthority &&
        ProductionPartIdentityReadyLocked(_activeActivation.Snapshot.Release.Source.Content.PartIdentityRequirement);

    private Task StartProductionModbusRequest(ProductionInspectionOwner owner, Func<Task> start)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_productionInspectionOwner, owner) || owner.Aborted || _disposed ||
                _shutdownRequested && _productionShutdownDeadline is not { Expired: false })
                throw new PlcRequestRevokedException();
            if (owner.ArmStatusWrite is { } status &&
                (owner.Health is not { Healthy: true } health || health.ControllerEpoch != status.ControllerEpoch ||
                    health.ConnectionGeneration != status.ConnectionGeneration))
                throw new PlcRequestRevokedException();
            return start();
        }
    }

    private Task StartProductionCycleWrite(ProductionInspectionOwner owner, Func<Task> start)
    {
        lock (_sync)
        {
            if (owner.Current is null)
            {
                if (!CanAcceptProductionTriggerLocked(owner)) throw new PlcRequestRevokedException();
            }
            else if (ProductionContinuationFailureLocked(owner) is not null)
                throw new PlcRequestRevokedException();
            return owner.Communication!.StartCycleWrite(start);
        }
    }

    private PlcCommunicationOwner CreateProductionCommunicationOwner(ProductionInspectionOwner owner)
    {
        var profile = _productionInspectionOptions!.Profile;
        return new(profile, owner.RuntimeEpoch, health =>
        {
            lock (_sync)
            {
                if (_disposed || !ReferenceEquals(_productionInspectionOwner, owner)) return;
                owner.Health = health;
                PublishLocked(_snapshot with { PlcCommunication = health,
                    Plc = new(health.TransportReachable ? HealthState.Healthy : HealthState.Faulted,
                        health.ControllerHeartbeatFresh && health.RuntimeHeartbeatObserved ? HealthState.Healthy : HealthState.Faulted,
                        health.Synchronized ? HealthState.Healthy : HealthState.Degraded) });
            }
        }, reason => { lock (_sync) AbortProductionInspectionLocked(owner, reason); }, async transition =>
        {
            var request = new PlcCommunicationWriteRequest(owner.RuntimeEpoch, profile.EndpointBindingHash,
                profile.ContentHash, profile.CommunicationBinding.Policy.ContentHash, transition.Generation,
                transition.Attempt, Enum.Parse<PlcCommunicationEventKind>(transition.Kind), transition.ReasonCode,
                transition.ControllerEpoch, DateTimeOffset.UtcNow, transition.ObservedAt, transition.RecoveryCycleId,
                RunId: owner.Current?.InspectionId, CycleSequence: owner.Current?.ControllerCycle.CycleSequence);
            var stored = await ((SqliteCommandStore)_audit!).AppendPlcCommunicationEventAsync(request,
                new StoreDeadline(_audit!.CommitTimeout), CancellationToken.None).ConfigureAwait(false);
            if (!stored.Committed) throw new InvalidOperationException(stored.ReasonCode);
        }, () => Interlocked.Increment(ref _plcCommunicationGeneration));
    }

    private static string ProductionFailureReason(Exception exception) =>
        exception.Message.Length is > 0 and <= 128 &&
        exception.Message.All(character => char.IsLetterOrDigit(character) || character == '_')
            ? exception.Message : "ProductionInspectionInterrupted";
}
