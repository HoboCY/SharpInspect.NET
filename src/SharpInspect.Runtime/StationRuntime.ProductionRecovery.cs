using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private ProductionRecoverySafetyRegistry? _productionRecoverySafety;
    private string? _productionRecoveryConfigurationFailure;
    private ProductionRecoveryOwner? _productionRecoveryOwner;

    private static (ProductionRecoverySafetyRegistry? Registry, string? Failure) CreateProductionRecovery(
        ProductionInspectionOptions options,
        IEnumerable<IProductionRecoverySafetyProvider>? providers)
    {
        if (options.Recovery is not { } binding)
        {
            return (null, "ProductionRecoveryBindingUnavailable");
        }
        try
        {
            var matches = (providers ?? Array.Empty<IProductionRecoverySafetyProvider>())
                .Where(value => value.Binding.ContentHash == binding.SafetySource.ContentHash).Take(2).ToArray();
            if (matches.Length != 1)
            {
                return (null, matches.Length == 0
                    ? "ProductionRecoverySafetyProviderUnavailable" : "ProductionRecoverySafetyProviderAmbiguous");
            }
            return (new(binding, matches[0]), null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return (null, ProductionFailureReason(exception)); }
    }

    private async ValueTask<RuntimeCommandOutcome> SubmitProductionRecoveryAsync(
        ManualProductionRecoveryCommand command, CancellationToken token)
    {
        if (_authorization is null || _audit is not SqliteCommandStore store)
            return new(command.CorrelationId, CommandDisposition.Rejected,
                "ProductionRecoveryIdentityUnavailable", AuditPersistence.NotAttempted);
        if (_productionInspectionStoreOptions?.ProductionInspections is null)
            return new(command.CorrelationId, CommandDisposition.Rejected,
                "ProductionRecoveryProductionConfigurationUnavailable", AuditPersistence.NotAttempted);
        if (!store.ProductionRecoveryEnabled)
            return new(command.CorrelationId, CommandDisposition.Rejected,
                "ProductionRecoveryConfigurationRequired", AuditPersistence.NotAttempted);

        ProductionRecoveryOwner owner;
        Task? productionTask;
        string? forced;
        lock (_sync)
        {
            owner = new(command, _snapshot.RuntimeEpoch);
            forced = ProductionRecoveryPreparationFailureLocked();
            if (forced is null) _productionRecoveryOwner = owner;
            productionTask = _productionInspectionTask;
        }
        var entered = false;
        ProductionRecoveryAuthorizationResult? authorization = null;
        ProductionRecoveryObservation? observation = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        timeout.CancelAfter(_productionInspectionOptions?.Recovery?.OperationTimeout ?? _audit.CommitTimeout);
        try
        {
            try
            {
                await _storeInitialization.WaitAsync(_audit.CommitTimeout, timeout.Token).ConfigureAwait(false);
                if (forced is null)
                {
                    var page = await new SqliteProductionInspectionHistoryQuery(_productionInspectionStoreOptions!)
                        .QueryAsync(new(InspectionId: command.InspectionId, PageSize: 128), timeout.Token)
                        .ConfigureAwait(false);
                    if (!page.Available) forced = page.ReasonCode;
                    else if (!page.RecoveryRequired || page.Events.Count == 0)
                        forced = "ProductionRecoveryInspectionNotPending";
                    else
                    {
                        observation = CreateProductionRecoveryObservation(page.Events, owner.RuntimeEpoch);
                        var admission = page.Events[^1].Admission;
                        var profile = _productionInspectionOptions!.Profile;
                        if (admission.StationId != _productionInspectionOptions.StationId ||
                            admission.EndpointBindingHash != profile.EndpointBindingHash ||
                            admission.PlcProfileHash != profile.ContentHash ||
                            admission.PlcPolicyHash != profile.CommunicationBinding.Policy.ContentHash)
                            forced = "ProductionRecoveryFrozenEndpointMismatch";
                    }
                }
                if (forced is null)
                {
                    // A retired cycle signal does not prove the observer, socket,
                    // algorithm, camera, and identity provider have actually retired.
                    if (productionTask is null) forced = "ProductionRecoveryProductionOwnerUnavailable";
                    else await productionTask.WaitAsync(_productionInspectionOptions!.Recovery!.RetirementTimeout,
                        timeout.Token).ConfigureAwait(false);
                }
                if (forced is null)
                {
                    entered = await _commandGate.WaitAsync(_audit.CommitTimeout, timeout.Token).ConfigureAwait(false);
                    if (!entered) forced = "ProductionRecoveryRuntimeBusy";
                }
                if (forced is null)
                {
                    lock (_sync) forced = ProductionRecoveryOwnerFailureLocked(owner);
                    if (forced is null)
                    {
                        owner.Safety = await _productionRecoverySafety!.CaptureAsync(owner.RuntimeEpoch,
                            timeout.Token).ConfigureAwait(false);
                        if (!owner.Safety.Available) forced = owner.Safety.ReasonCode;
                    }
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { forced = RecoveryAttemptFailure(exception); }

            authorization = await _authorization.AuthorizeProductionRecoveryAsync(command, owner.RuntimeEpoch,
                owner.Safety, observation, forced, () => timeout.IsCancellationRequested
                    ? "ProductionRecoveryCancelled" : ProductionRecoveryFinalFailure(owner),
                CancellationToken.None).ConfigureAwait(false);
            if (authorization.Outcome.Disposition != CommandDisposition.Accepted ||
                authorization.Event?.Recovery is null || authorization.CommandFact is null)
            {
                if (authorization.Outcome.Audit == AuditPersistence.Unavailable)
                    MarkAuditFault("ProductionRecoveryAuthorizationAuditUnavailable");
                return authorization.Outcome;
            }

            // The only public transition into recovery work follows the committed
            // permission, Step-Up, disposition, and safety authorization facts.
            owner.Authorization = authorization;
            lock (_sync)
                PublishLocked(_snapshot with { Ready = false, Busy = false,
                    ArmState = ProductionArmState.Disarmed, Recovery = RecoveryState.InProgress,
                    Mode = ExclusiveMode.Maintenance,
                    LastCommand = new(command.CorrelationId, OperationState.Pending, "ProductionRecoveryAuthorized") });
            // Once admission commits, cancelling the caller's wait cannot revoke
            // the accepted physical operation. Shutdown and the owned deadline can.
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            operation.CancelAfter(_productionInspectionOptions!.Recovery!.OperationTimeout);
            await ExecuteProductionRecoveryAsync(owner, store, operation.Token).ConfigureAwait(false);
            return authorization.Outcome;
        }
        finally
        {
            if (entered) _commandGate.Release();
            lock (_sync)
            {
                if (ReferenceEquals(_productionRecoveryOwner, owner)) _productionRecoveryOwner = null;
            }
            owner.Retired.TrySetResult(true);
        }
    }

    private string? ProductionRecoveryPreparationFailureLocked()
    {
        if (_disposed || _shutdownRequested) return "ProductionRecoveryRuntimeStopped";
        if (LocalStopPendingLocked) return "ProductionRecoveryLocalStopInProgress";
        if (_productionRecoveryOwner is not null) return "ProductionRecoveryAlreadyInProgress";
        if (_productionInspectionOptions is null || _productionInspectionStoreOptions is null)
            return "ProductionRecoveryProductionConfigurationUnavailable";
        if (_productionRecoveryConfigurationFailure is { } reason) return reason;
        if (_productionRecoverySafety is null) return "ProductionRecoverySafetyProviderUnavailable";
        if (_snapshot.Ready || _snapshot.ArmState != ProductionArmState.Disarmed)
            return "ProductionRecoveryRequiresDisarmedStation";
        if (_snapshot.Mode != ExclusiveMode.None || _snapshot.LastCommand?.State == OperationState.Pending)
            return "ProductionRecoveryWorkflowConflict";
        if (_snapshot.Busy || _snapshot.CurrentExecution is { Kind: not ExecutionKind.Production } ||
            _productionInspectionOwner is { Aborted: false, Current: not null })
            return "ProductionRecoveryExecutionStillActive";
        return null;
    }

    private string? ProductionRecoveryOwnerFailureLocked(ProductionRecoveryOwner owner)
    {
        if (!ReferenceEquals(_productionRecoveryOwner, owner) || _disposed || _shutdownRequested ||
            owner.RuntimeEpoch != _snapshot.RuntimeEpoch)
            return "ProductionRecoveryOwnerRevoked";
        if (LocalStopPendingLocked) return "ProductionRecoveryLocalStopInProgress";
        if (_productionInspectionTask?.Status != TaskStatus.RanToCompletion)
            return "ProductionRecoveryProductionOwnerNotRetired";
        if (_productionInspectionOwner is { } old && (old.Camera is not null ||
            old.Execution.ActiveExecutionCount != 0 || old.PartIdentityOperation is not null ||
            old.Observer is not null || old.PhysicalPhaseId != 0))
            return "ProductionRecoveryPhysicalOwnerNotRetired";
        if (_snapshot.Ready || _snapshot.ArmState != ProductionArmState.Disarmed ||
            _snapshot.Mode is not (ExclusiveMode.None or ExclusiveMode.Maintenance))
            return "ProductionRecoveryStateChanged";
        if (!_storeReady || _auditFault) return "ProductionRecoveryAuditUnavailable";
        return null;
    }

    private string? ProductionRecoveryFinalFailure(ProductionRecoveryOwner owner)
    {
        // SQLite must not wait on a runtime lock while holding its writer and
        // identity lease. A contended final fence rolls this attempt back.
        if (!Monitor.TryEnter(_sync)) return "ProductionRecoveryCommitFenceBusy";
        try
        {
            return ProductionRecoveryOwnerFailureLocked(owner) ??
                (owner.Safety is null ? "ProductionRecoverySafetyCaptureMissing" :
                    _productionRecoverySafety!.FinalFailure(owner.Safety));
        }
        finally { Monitor.Exit(_sync); }
    }

    private string? ProductionRecoveryAuthorityFailure(ProductionRecoveryOwner owner)
    {
        // Physical work runs outside SQLite and its identity lease. Ordinary
        // snapshot publication must not be mistaken for authority revocation.
        lock (_sync)
            return ProductionRecoveryOwnerFailureLocked(owner) ??
                (owner.Safety is null ? "ProductionRecoverySafetyCaptureMissing" :
                    _productionRecoverySafety!.FinalFailure(owner.Safety));
    }

    private Task StartProductionRecoveryRequest(ProductionRecoveryOwner owner, Func<Task> start)
    {
        lock (_sync)
        {
            if (ProductionRecoveryOwnerFailureLocked(owner) is { } reason)
                throw new InvalidOperationException(reason);
            if (owner.Authorization?.Event?.Recovery is null || owner.Safety is null)
                throw new InvalidOperationException("ProductionRecoveryAuthorizationMissing");
            if (!_productionRecoverySafety!.StartIfCurrent(owner.Safety, _ => start(), out var operation))
                throw new PlcRequestRevokedException();
            return operation!;
        }
    }

    private static ProductionRecoveryObservation CreateProductionRecoveryObservation(
        IReadOnlyList<ProductionInspectionHistoryEvent> events, Guid runtimeEpoch)
    {
        var last = events[^1];
        if (last.Recovery is { } existing) return existing.Observation;
        var admission = last.Admission;
        var core = events.LastOrDefault(value => value.Core is not null)?.Core;
        var delivery = events.LastOrDefault(value => value.Kind is not
            (ProductionInspectionEventKind.FaultTerminated or ProductionInspectionEventKind.RecoveryRequired));
        var phase = delivery?.Kind switch
        {
            ProductionInspectionEventKind.Admitted => ProductionRecoveryDeliveryPhase.Admitted,
            ProductionInspectionEventKind.CoreCommitted => ProductionRecoveryDeliveryPhase.CoreCommitted,
            ProductionInspectionEventKind.PublicationPrepared => ProductionRecoveryDeliveryPhase.PublicationPrepared,
            ProductionInspectionEventKind.ResultValidRaised => ProductionRecoveryDeliveryPhase.ResultValidRaised,
            ProductionInspectionEventKind.ResultAcknowledged => ProductionRecoveryDeliveryPhase.ResultAcknowledged,
            ProductionInspectionEventKind.ResultValidCleared => ProductionRecoveryDeliveryPhase.ResultValidCleared,
            _ => ProductionRecoveryDeliveryPhase.Unknown
        };
        var uncertainty = admission.RuntimeEpoch != runtimeEpoch ? ProductionRecoveryUncertaintyKind.ProcessRestart :
            last.ReasonCode.Contains("Ack", StringComparison.OrdinalIgnoreCase) ? ProductionRecoveryUncertaintyKind.ResultAcknowledgementTimeout :
            last.ReasonCode.Contains("Epoch", StringComparison.OrdinalIgnoreCase) ? ProductionRecoveryUncertaintyKind.ControllerEpochChanged :
            last.ReasonCode.Contains("Communication", StringComparison.OrdinalIgnoreCase) ? ProductionRecoveryUncertaintyKind.CommunicationLost :
            ProductionRecoveryUncertaintyKind.PhysicalStopRequired;
        return new(phase, uncertainty, ProductionRecoveryAcknowledgementObservation.Unknown,
            admission.RuntimeEpoch, admission.ControllerCycle.ControllerEpoch, admission.ControllerCycle.CycleSequence,
            admission.ConnectionGeneration, admission.EndpointBindingHash, admission.PlcProfileHash,
            admission.PlcPolicyHash, core?.ContentHash, core?.PlcPayload?.ContentHash, core?.PlcPayload?.WireContentHash);
    }

    private static string RecoveryAttemptFailure(Exception exception) => exception switch
    {
        OperationCanceledException => "ProductionRecoveryCancelled",
        TimeoutException => "ProductionRecoveryTimedOut",
        _ => ProductionFailureReason(exception)
    };

    private sealed class ProductionRecoveryOwner
    {
        internal ProductionRecoveryOwner(ManualProductionRecoveryCommand command, Guid runtimeEpoch)
        { Command = command; RuntimeEpoch = runtimeEpoch; }
        internal ManualProductionRecoveryCommand Command { get; }
        internal Guid RuntimeEpoch { get; }
        internal ProductionRecoverySafetyCapture? Safety { get; set; }
        internal ProductionRecoveryAuthorizationResult? Authorization { get; set; }
        internal TaskCompletionSource<bool> Retired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
