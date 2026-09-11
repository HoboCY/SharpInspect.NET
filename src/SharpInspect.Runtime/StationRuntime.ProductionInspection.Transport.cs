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
                WaitForManualInspectionStartupAsync(), WaitForStationQualificationStartupAsync())
                .WaitAsync(_lifetime.Token).ConfigureAwait(false);
            lock (_sync)
                if (!ProductionStartupDependenciesReconciledLocked())
                    throw new InvalidOperationException("ProductionInspectionStartupDependencyUnavailable");
            // A cold start reads the authoritative ledger before opening a socket.
            // An incomplete old cycle is never reconstructed as executable work.
            var history = new SqliteProductionInspectionHistoryQuery(_productionInspectionStoreOptions!);
            var current = await history.ReadCurrentAsync(_lifetime.Token).ConfigureAwait(false);
            if (!current.Available || current.RecoveryRequired)
                throw new InvalidOperationException(current.Available
                    ? "ProductionInspectionStartupRecoveryRequired" : current.ReasonCode);
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
            policy.ContentHash != options.TracePolicySnapshotHash || policy.Policy.RequiredRoutes.Count != 0 ||
            store.TraceStoragePolicies?.DeploymentScope?.RequiredRoutes.Count != 0)
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
            start => StartProductionModbusRequest(owner, start));
        var output = new InspectionCycleOutputLatch(channel.WriteStateAsync);
        var initialized = false;
        InspectionCycleRequestObserver? observer = null;
        try
        {
            await channel.ConnectAsync(token).ConfigureAwait(false);
            var initial = await channel.ReadRuntimeStateAsync(token).ConfigureAwait(false);
            if (initial.QualificationReady || initial.ProductionReady || initial.Busy || initial.ResultValid ||
                initial.CycleFault || initial.ProtocolViolation)
                throw new InvalidOperationException("ProductionInspectionInitialControllerStateNotClear");
            await output.ChangeAsync(token).ConfigureAwait(false);
            initialized = true;
            await communication.SynchronizeAsync(channel, token).ConfigureAwait(false);
            lock (_sync) ProjectProductionProgressLocked(owner);
            observer = owner.Observer = new(ct => communication.ReadAsync(channel, ct), profile.PollInterval,
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
            while (true)
            {
                token.ThrowIfCancellationRequested();
                observer.RequireHealthy();
                if (DateTimeOffset.UtcNow >= nextAdmissionRefresh)
                {
                    await RefreshProductionAdmissionAsync(token).ConfigureAwait(false);
                    nextAdmissionRefresh = DateTimeOffset.UtcNow.AddSeconds(1);
                }
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
                            if (CanAcceptProductionTriggerLocked(owner))
                                PublishLocked(_snapshot with { Ready = true });
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
                    accepting = true;
                }
                if (observer.TryTakeAccepted(out var signals) && signals is not null)
                {
                    observer.StopAccepting();
                    accepting = false;
                    try { await AdmitProductionTriggerAsync(owner, signals).ConfigureAwait(false); }
                    catch (OperationCanceledException exception) when (!token.IsCancellationRequested &&
                        owner.Current is null && exception.Message == "ProductionInspectionTriggerPermitRevoked")
                    {
                        observer.RejectPendingAdmission("ProductionTriggerPermitRevoked");
                        observer.CompleteCycle();
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
                    owner.CycleRetired?.TrySetResult(true);
                    observer.CompleteCycle();
                    lock (_sync)
                    {
                        owner.Current = null;
                        owner.AdmissionCommitted = false;
                        owner.Core = null;
                        owner.Prepared = null;
                        ProjectProductionProgressLocked(owner);
                    }
                }
                // Drain the bounded observer ring; rejected edges do not allocate InspectionIds.
                // Their protocol-fault flag prevents them being mistaken for queued work.
                while (observer.TakeRejected() is not null)
                    await output.ChangeAsync(token, violation: true).ConfigureAwait(false);
                await Task.Delay(profile.PollInterval, token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            observer?.StopAccepting();
            owner.Coordinator.SetPhase(InspectionCyclePhase.FaultTerminated);
            lock (_sync) AbortProductionInspectionLocked(owner, ProductionFailureReason(exception));
            // No Core receipt means no ordinary result, even if the storage call finished late.
            // A post-publication fault preserves the immutable payload and unresolved delivery.
            try
            {
                if (initialized) await output.ChangeAsync(CancellationToken.None, ready: false, busy: false,
                    valid: owner.Core is null ? false : null, fault: true).ConfigureAwait(false);
            }
            catch (Exception fault) when (fault is not OutOfMemoryException) { }
            if (owner.Current is not null)
                await RecordProductionFaultAsync(owner, owner.FailureReason).ConfigureAwait(false);
            if (observer is not null) await observer.DisposeAsync().ConfigureAwait(false);
            await RetireProductionCameraAsync(owner).ConfigureAwait(false);
            owner.CycleRetired?.TrySetResult(true);
            if (communication.Failure is not null && !_lifetime.IsCancellationRequested)
                await communication.RecoverAsync(channel, owner.Current is not null, _lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            if (observer is not null) await observer.DisposeAsync().ConfigureAwait(false);
            owner.Observer = null;
            await communication.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool CanAcceptProductionTriggerLocked(ProductionInspectionOwner owner) =>
        !LocalStopPendingLocked &&
        ReferenceEquals(_productionInspectionOwner, owner) && !owner.Aborted && owner.Current is null &&
        !_disposed && !_shutdownRequested && _productionInspectionStartupVerified &&
        !_productionInspectionRecoveryBlocked && owner.Health is { Healthy: true } &&
        _snapshot.ArmState == ProductionArmState.Armed && _snapshot.ProductionAdmission is { CanArm: true } &&
        _snapshot.Mode == ExclusiveMode.None && !_snapshot.Busy && _snapshot.Recovery == RecoveryState.None &&
        _snapshot.CurrentExecution is null && _activeActivation is { Algorithm.IsRetired: false } &&
        _activeActivation.Snapshot.ProductionAuthority;

    private Task StartProductionModbusRequest(ProductionInspectionOwner owner, Func<Task> start)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_productionInspectionOwner, owner) || owner.Aborted || _shutdownRequested || _disposed)
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
