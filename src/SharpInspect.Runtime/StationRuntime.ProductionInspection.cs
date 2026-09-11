using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Cycles;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private ProductionInspectionOptions? _productionInspectionOptions;
    private ProductionStoreOptions? _productionInspectionStoreOptions;
    private AlgorithmExecutionOptions? _productionInspectionExecutionOptions;
    private IFrameAcquisitionClock? _productionInspectionClock;
    private ProductionInspectionOwner? _productionInspectionOwner;
    private TraceStoragePolicySnapshot? _productionInspectionPolicy;
    private Task? _productionInspectionTask;
    private bool _productionInspectionStartupVerified;
    private bool _productionInspectionRecoveryBlocked;
    private long _productionPhysicalSequence;

    // Communication monitoring does not own the camera or block configuration.
    // Only a durably accepted production cycle owns inspection resources.
    private bool ProductionInspectionConfigurationBlockedLocked =>
        _productionInspectionOwner?.Current is not null;

    internal void ConfigureProductionInspections(ProductionInspectionOptions options,
        ProductionStoreOptions storeOptions, AlgorithmExecutionOptions executionOptions,
        IFrameAcquisitionClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storeOptions);
        ArgumentNullException.ThrowIfNull(executionOptions);
        ArgumentNullException.ThrowIfNull(clock);
        if (options.EvidenceRequirement != ProductionEvidenceRequirement.None)
            throw new ArgumentException("ProductionInspectionEvidenceRequirementUnavailable");
        if (storeOptions.ProductionInspections is null || storeOptions.ProductionAdmission is null ||
            storeOptions.PlcCommunication is null || storeOptions.TraceStoragePolicies is null ||
            storeOptions.RecipeActivations is null || storeOptions.LocalIdentity is null ||
            executionOptions.Policy.ContentHash != storeOptions.RecipeDrafts?.ExecutionPolicy.ContentHash)
            throw new ArgumentException("ProductionInspectionDependenciesUnavailable");
        lock (_sync)
        {
            if (_productionInspectionOptions is not null)
                throw new InvalidOperationException("ProductionInspectionAlreadyConfigured");
            if (_disposed || _shutdownRequested)
                throw new InvalidOperationException("ProductionInspectionRuntimeStopped");
            _productionInspectionOptions = options;
            _productionInspectionStoreOptions = storeOptions;
            _productionInspectionExecutionOptions = executionOptions;
            _productionInspectionClock = clock;
            _productionInspectionTask = Task.Run(RunProductionInspectionsAsync);
        }
    }

    private bool IsOwnedProductionProgressLocked(StationStateSnapshot state) =>
        _productionInspectionOwner is { Aborted: false, Current: { } current } owner &&
        owner.RuntimeEpoch == state.RuntimeEpoch && !owner.Cancellation.IsCancellationRequested &&
        state.Mode == ExclusiveMode.None && state.Recovery == RecoveryState.None &&
        state.CurrentExecution is { Kind: ExecutionKind.Production } correlation &&
        correlation.Value == current.CorrelationId;

    private RecipeActivationPhysicalPhaseClaim ClaimProductionPhysicalPhase(ProductionInspectionOwner owner)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_productionInspectionOwner, owner) || owner.Current is null ||
                owner.Aborted || owner.Cancellation.IsCancellationRequested || _disposed || _shutdownRequested)
                return RecipeActivationPhysicalPhaseClaim.Unavailable("ProductionInspectionRevoked");
            if (owner.PhysicalPhaseId != 0)
                return RecipeActivationPhysicalPhaseClaim.Unavailable("ProductionPhysicalOperationInProgress");
            var id = checked(++_productionPhysicalSequence);
            owner.PhysicalPhaseId = id;
            return RecipeActivationPhysicalPhaseClaim.Granted(id, () =>
            {
                lock (_sync) if (owner.PhysicalPhaseId == id) owner.PhysicalPhaseId = 0;
            });
        }
    }

    private Task RequireProductionContinuationAsync(ProductionInspectionOwner owner)
    {
        lock (_sync)
        {
            if (ProductionContinuationFailureLocked(owner) is { } reason)
                throw new OperationCanceledException(reason);
        }
        owner.Communication!.RequireHealthy();
        return Task.CompletedTask;
    }

    private string? ProductionContinuationFailureLocked(ProductionInspectionOwner owner) =>
        !ReferenceEquals(_productionInspectionOwner, owner) || owner.Current is not { } current ||
        owner.RuntimeEpoch != _snapshot.RuntimeEpoch || owner.Aborted || _disposed || _shutdownRequested ||
        owner.Cancellation.IsCancellationRequested || _auditFault || !_storeReady ||
        _snapshot.AlarmState?.Instances.Any(value => value.ProductionImpact == ProductionImpact.FaultAbort) == true ||
        owner.Health is not { Healthy: true } health ||
        health.ConnectionGeneration != current.ConnectionGeneration ||
        health.ControllerEpoch != current.ControllerCycle.ControllerEpoch ||
        _activeActivation?.Snapshot.ContentHash != current.ActivationSnapshot.ContentHash
            ? "ProductionInspectionContinuationRevoked" : null;

    private void AbortProductionInspectionLocked(ProductionInspectionOwner owner, string reason)
    {
        if (!ReferenceEquals(_productionInspectionOwner, owner)) return;
        owner.Aborted = true;
        owner.FailureReason = reason;
        owner.Observer?.StopAccepting();
        _productionInspectionRecoveryBlocked |= owner.Current is not null;
        // Cancellation callbacks can belong to providers; dispatch outside the runtime lock.
        _ = Task.Run(() => { try { owner.Cancellation.Cancel(); } catch (ObjectDisposedException) { } });
        PublishLocked(_snapshot with { Ready = false, Busy = false, ArmState = ProductionArmState.Disarmed,
            Recovery = _productionInspectionRecoveryBlocked ? RecoveryState.Required : _snapshot.Recovery });
    }

    private void RequestProductionInspectionAbort(string reason)
    {
        lock (_sync)
            if (_productionInspectionOwner is { } owner) AbortProductionInspectionLocked(owner, reason);
    }

    private void ProjectProductionProgressLocked(ProductionInspectionOwner owner)
    {
        if (!ReferenceEquals(_productionInspectionOwner, owner)) return;
        var phase = owner.Coordinator.Phase;
        PublishLocked(_snapshot with
        {
            Ready = false,
            Busy = owner.Current is not null && !owner.Aborted &&
                phase is not (InspectionCyclePhase.AwaitAckHigh or InspectionCyclePhase.AwaitAckLow or
                    InspectionCyclePhase.Completed or InspectionCyclePhase.FaultTerminated),
            CurrentExecution = owner.Current is { } current
                ? new ExecutionCorrelationId(ExecutionKind.Production, current.CorrelationId) : null,
            Handshake = phase switch
            {
                InspectionCyclePhase.AwaitAckHigh => HandshakePhase.AwaitingResultAck,
                InspectionCyclePhase.AwaitAckLow => HandshakePhase.AwaitingAckReset,
                InspectionCyclePhase.FaultTerminated => HandshakePhase.Unknown,
                _ => HandshakePhase.Idle
            }
        });
    }

    private async Task ShutdownProductionInspectionAsync()
    {
        Task? operation;
        lock (_sync)
        {
            if (_productionInspectionOwner is { } owner)
                AbortProductionInspectionLocked(owner, "ProductionInspectionRuntimeShutdown");
            operation = _productionInspectionTask;
        }
        if (operation is null) return;
        try { await operation.WaitAsync(_productionInspectionOptions!.RetirementTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { throw new InvalidOperationException("ProductionInspectionShutdownIncomplete"); }
    }

    private sealed class ProductionInspectionOwner
    {
        internal ProductionInspectionOwner(Guid runtimeEpoch, CancellationToken lifetime,
            AlgorithmExecutionOptions executionOptions)
        {
            RuntimeEpoch = runtimeEpoch;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            Execution = new AlgorithmExecutionService(executionOptions);
        }
        internal Guid RuntimeEpoch { get; }
        internal CancellationTokenSource Cancellation { get; }
        internal AlgorithmExecutionService Execution { get; }
        internal InspectionCycleCoordinator<PlcResultPayloadSnapshot> Coordinator { get; } = new();
        internal ProductionInspectionAdmission? Current { get; set; }
        internal bool AdmissionCommitted { get; set; }
        internal ProductionInspectionCore? Core { get; set; }
        internal TaskCompletionSource<bool>? CycleRetired { get; set; }
        internal PreparedAlgorithm? Prepared { get; set; }
        internal RecipeActivationCameraLease? Camera { get; set; }
        internal PlcCommunicationOwner? Communication { get; set; }
        internal PlcCommunicationHealth? Health { get; set; }
        internal InspectionCycleRequestObserver? Observer { get; set; }
        internal bool Aborted { get; set; }
        internal long PhysicalPhaseId { get; set; }
        internal string FailureReason { get; set; } = "ProductionInspectionInterrupted";
    }
}
