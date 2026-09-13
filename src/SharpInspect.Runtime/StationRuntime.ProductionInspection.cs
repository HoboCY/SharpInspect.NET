using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Cycles;
using SharpInspect.Runtime.Images;
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
    private StoreDeadline? _productionShutdownDeadline;
    private bool _productionInspectionStartupVerified;
    private bool _productionInspectionRecoveryBlocked;
    private long _productionPhysicalSequence;
    private PartIdentityBindingRegistry? _partIdentityRegistry;
    private ProductionImageStager? _productionImageStager;

    // 通信监视器不拥有相机，也不阻塞配置；只有已持久接受的生产周期才拥有检查资源。
    private bool ProductionInspectionConfigurationBlockedLocked =>
        _productionInspectionOwner?.Current is not null || _productionRecoveryOwner is not null;

    internal void ConfigureProductionInspections(ProductionInspectionOptions options,
        ProductionStoreOptions storeOptions, AlgorithmExecutionOptions executionOptions,
        IFrameAcquisitionClock clock, PartIdentityBindingRegistry? partIdentityRegistry = null,
        IEnumerable<IProductionRecoverySafetyProvider>? recoverySafetyProviders = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storeOptions);
        ArgumentNullException.ThrowIfNull(executionOptions);
        ArgumentNullException.ThrowIfNull(clock);
        if (options.EvidenceRequirement != ProductionEvidenceRequirement.None)
            throw new ArgumentException("ProductionInspectionEvidenceRequirementUnavailable");
        if ((options.ImageStage is null) != (storeOptions.ImageEvidence is null) ||
            options.ImageStage is { } imageStage && (storeOptions.ImageEvidence!.Stage.ContentHash != imageStage.ContentHash ||
                storeOptions.RecipeReleases?.EvidenceCapturePolicies is null))
            throw new ArgumentException("ProductionImageEvidenceConfigurationMismatch");
        if (storeOptions.ProductionInspections is null || storeOptions.ProductionAdmission is null ||
            storeOptions.PlcCommunication is null || storeOptions.TraceStoragePolicies is null ||
            storeOptions.RecipeActivations is null || storeOptions.LocalIdentity is null ||
            executionOptions.Policy.ContentHash != storeOptions.RecipeDrafts?.ExecutionPolicy.ContentHash)
            throw new ArgumentException("ProductionInspectionDependenciesUnavailable");
        if (options.Deployment is { } armDeployment)
        {
            if ((armDeployment.StartupProduction.Mode == StartupProductionMode.AutomaticArm ||
                    armDeployment.PostActivationArm.Mode == PostActivationArmMode.AutomaticRearmAfterPlcActivation) &&
                storeOptions.ProductionArming is null)
                throw new ArgumentException("ProductionArmStoreConfigurationRequired");
            if (armDeployment.PostActivationArm.Mode == PostActivationArmMode.AutomaticRearmAfterPlcActivation &&
                (options.Profile.RecipeChange is null || options.Profile.ProductionArmStatus is null ||
                    storeOptions.RecipeSelections is null))
                throw new ArgumentException("ProductionArmPlcHandshakeAndStatusConfigurationRequired");
        }
        var recovery = CreateProductionRecovery(options, recoverySafetyProviders);
        var attached = false;
        try
        {
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
                _partIdentityRegistry = partIdentityRegistry;
                _productionImageStager = options.ImageStage is null ? null : new ProductionImageStager(options.ImageStage);
                _productionRecoverySafety = recovery.Registry;
                _productionRecoveryConfigurationFailure = recovery.Failure;
                attached = true;
                if (partIdentityRegistry is not null) partIdentityRegistry.SourceChanged += PartIdentitySourceChanged;
                _productionInspectionTask = Task.Run(RunProductionInspectionsAsync);
            }
        }
        finally { if (!attached) recovery.Registry?.Dispose(); }
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
                owner.Aborted || owner.FaultAbortRequested || owner.Cancellation.IsCancellationRequested || _disposed || _shutdownRequested)
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
        owner.RuntimeEpoch != _snapshot.RuntimeEpoch || owner.Aborted || _disposed ||
        _shutdownRequested && _productionShutdownDeadline is not { Expired: false } ||
        owner.Cancellation.IsCancellationRequested || _auditFault || !_storeReady ||
        owner.Health is not { Healthy: true } health ||
        health.ConnectionGeneration != current.ConnectionGeneration ||
        health.ControllerEpoch != current.ControllerCycle.ControllerEpoch ||
        _activeActivation?.Snapshot.ContentHash != current.ActivationSnapshot.ContentHash
            ? "ProductionInspectionContinuationRevoked" : null;

    private void AbortProductionInspectionLocked(ProductionInspectionOwner owner, string reason)
    {
        if (!ReferenceEquals(_productionInspectionOwner, owner)) return;
        owner.Aborted = true;
        owner.RecipeChangeActivation?.Revoke();
        owner.FailureReason = reason;
        owner.Observer?.StopAccepting();
        _productionInspectionRecoveryBlocked |= owner.Current is not null;
        // 取消回调可能进入供应商代码，必须在 Runtime 锁外异步派发。
        _ = Task.Run(() => { try { owner.Cancellation.Cancel(); } catch (ObjectDisposedException) { } });
        PublishLocked(_snapshot with { Ready = false, Busy = false, ArmState = ProductionArmState.Disarmed,
            Recovery = _productionInspectionRecoveryBlocked ? RecoveryState.Required : _snapshot.Recovery });
    }

    private void RequestProductionInspectionAbort(string reason)
    {
        lock (_sync)
        {
            if (_productionInspectionOwner is not { } owner || owner.Aborted) return;
            owner.Observer?.StopAccepting();
            if (owner.Current is not null && !owner.FaultAbortRequested)
            {
                owner.FaultAbortRequested = true;
                // Fault Abort 只取消计算；已接受周期仍保留持久化和健康通信权威，以固定结果完成收尾。
                owner.Execution.RequestProductionCancellation(new(ExecutionKind.Production,
                    owner.Current.CorrelationId));
                var cancellation = owner.ExecutionCancellation;
                owner.ExecutionCancellationTask = Task.Run(() =>
                {
                    try { cancellation.Cancel(); }
                    catch (ObjectDisposedException) { }
                });
            }
            PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed });
        }
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
        if (_productionArmMaintenanceEvidence is { } maintenance)
            maintenance.Changed -= ProductionArmMaintenanceChanged;
        if (_partIdentityRegistry is not null) _partIdentityRegistry.SourceChanged -= PartIdentitySourceChanged;
        Task? operation;
        lock (_sync)
        {
            // 启动阶段没有已接受工作可收尾，可能仍在等待其他服务的生命周期；已有协议所有者则使用共享预算排空。
            if (_productionInspectionOwner is null) _lifetime.Cancel();
            operation = _productionInspectionTask;
        }
        if (operation is null) return;
        try
        {
            var remaining = _productionShutdownDeadline!.Remaining;
            if (!operation.IsCompleted && remaining <= TimeSpan.Zero) throw new TimeoutException();
            await operation.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (_sync)
                if (_productionInspectionOwner is { } owner)
                    AbortProductionInspectionLocked(owner, "ProductionInspectionShutdownIncomplete");
            throw new InvalidOperationException("ProductionInspectionShutdownIncomplete");
        }
    }

    private sealed class ProductionInspectionOwner
    {
        internal ProductionInspectionOwner(Guid runtimeEpoch, CancellationToken lifetime,
            AlgorithmExecutionOptions executionOptions)
        {
            RuntimeEpoch = runtimeEpoch;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            ExecutionCancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation.Token);
            Execution = new AlgorithmExecutionService(executionOptions);
        }
        internal Guid RuntimeEpoch { get; }
        internal CancellationTokenSource Cancellation { get; }
        internal CancellationTokenSource ExecutionCancellation { get; set; }
        internal Task? ExecutionCancellationTask { get; set; }
        internal bool FaultAbortRequested { get; set; }
        internal StoreDeadline? RetirementDeadline { get; set; }
        internal AlgorithmExecutionService Execution { get; }
        internal InspectionCycleCoordinator<PlcResultPayloadSnapshot> Coordinator { get; } = new();
        internal ProductionInspectionAdmission? Current { get; set; }
        internal bool AdmissionCommitted { get; set; }
        internal ProductionInspectionCore? Core { get; set; }
        internal RetainedProductionFrame? ImageInput { get; set; }
        internal TaskCompletionSource<bool>? CycleRetired { get; set; }
        internal PreparedAlgorithm? Prepared { get; set; }
        internal RecipeActivationCameraLease? Camera { get; set; }
        internal PlcCommunicationOwner? Communication { get; set; }
        internal PlcCommunicationHealth? Health { get; set; }
        internal InspectionCycleRequestObserver? Observer { get; set; }
        internal bool Aborted { get; set; }
        internal long PhysicalPhaseId { get; set; }
        internal string FailureReason { get; set; } = "ProductionInspectionInterrupted";
        internal PartIdentityLatchAttempt? PartIdentityAttempt { get; set; }
        internal PlcRecipeChangeHandshake? RecipeChange { get; set; }
        internal RecipeChangeRequestEvidence? RecipeChangeRequest { get; set; }
        internal PlcRecipeActivationCapability? RecipeChangeActivation { get; set; }
        internal RecipeChangeDecision? RecipeChangeDecision { get; set; }
        internal bool RecipeChangeFaultRecorded { get; set; }
        internal TaskCompletionSource<bool>? RecipeChangeReadyCleared { get; set; }
        internal TaskCompletionSource<bool>? RecipeRetirementReadyCleared { get; set; }
        internal ActivationReservation? RecipeRetirementReservation { get; set; }
        internal bool RecipeChangeObservedProductionRequest { get; set; }
        internal long ArmInputSequence { get; set; }
        internal uint ArmInputCycleSequence { get; set; }
        internal bool ArmInputsClear { get; set; }
        internal AutomaticProductionArmCapability? ArmStatusWrite { get; set; }
        internal Task<PartIdentityProviderObservation>? PartIdentityOperation { get; set; }
    }
}
