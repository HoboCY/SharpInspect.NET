using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private IProductionArmMaintenanceEvidenceProvider? _productionArmMaintenanceEvidence;
    private AutomaticProductionArmCapability? _automaticProductionArm;
    private bool _startupProductionArmConsidered;
    private long _productionArmStopGeneration;

    internal void ConfigureProductionArmMaintenanceEvidenceProvider(IProductionArmMaintenanceEvidenceProvider? provider)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_productionArmMaintenanceEvidence, provider)) return;
            if (_productionArmMaintenanceEvidence is { } previous) previous.Changed -= ProductionArmMaintenanceChanged;
            _productionArmMaintenanceEvidence = provider;
            if (provider is not null) provider.Changed += ProductionArmMaintenanceChanged;
            ProductionArmMaintenanceChanged();
        }
    }

    private void ProductionArmMaintenanceChanged()
    {
        lock (_sync)
        {
            _admissionGeneration = checked(_admissionGeneration + 1);
            if (!_disposed && !_shutdownRequested)
                PublishUnavailableProductionAdmissionLocked("ProductionArmMaintenanceEvidenceChanged");
        }
    }

    // The same final live fence is used by the ordinary human Arm and the
    // internally authorized system attempt. Neither can turn a stale report into Ready.
    private bool ProductionArmLiveFenceLocked(AdmissionCapture capture) =>
        !_shutdownRequested && !_disposed && !StationQualificationConfigurationBlockedLocked &&
        _snapshot.ProductionAdmission?.CanArm == true && _snapshot.RuntimeEpoch == capture.RuntimeEpoch &&
        _admissionGeneration == capture.Generation &&
        string.Equals(_admissionStateHash, capture.StateHash, StringComparison.Ordinal) &&
        !LocalStopPendingLocked && _snapshot.Mode == ExclusiveMode.None && _snapshot.Recovery == RecoveryState.None;

    private void ConsiderStartupProductionArmLocked(ProductionInspectionOwner owner)
    {
        if (_startupProductionArmConsidered) return;
        _startupProductionArmConsidered = true;
        if (_productionInspectionOptions?.Deployment is not { StartupProduction.Mode: StartupProductionMode.AutomaticArm } deployment)
            return;
        // The once-only start-up cause and the dedicated PLC activation cause are
        // arbitrated inside this one Runtime lock: whoever owns the Runtime first wins
        // and the loser is refused, never queued, preempted or replayed. A PLC request
        // the observer already reserved (or latched on the owner) therefore owns the
        // whole start-up decision; spending the start-up cause here would collide with
        // the post-activation attempt that closes that same episode.
        if (_recipeChangeInProgress || owner.RecipeChangeRequest is not null ||
            _activationReservation is { PlcOwned: true } || _automaticProductionArm is { Terminal: false })
            return;
        _automaticProductionArm = CreateAutomaticProductionArmLocked(owner, deployment,
            ProductionArmCause.Startup, null, _currentRecipeActivationReference);
    }

    private void ConsiderPostActivationProductionArmLocked(ProductionInspectionOwner owner,
        RecipeChangeRequestEvidence request, RecipeChangeDecision decision)
    {
        if (decision.Outcome != RecipeChangeOutcome.Succeeded || decision.Activation is null ||
            _productionInspectionOptions?.Deployment is not
                { PostActivationArm.Mode: PostActivationArmMode.AutomaticRearmAfterPlcActivation } deployment)
            return;
        if (_automaticProductionArm is { Terminal: false })
            throw new InvalidOperationException("ProductionArmAttemptAlreadyPending");
        _automaticProductionArm = CreateAutomaticProductionArmLocked(owner, deployment,
            ProductionArmCause.PlcActivation, request, decision.Activation);
    }

    private AutomaticProductionArmCapability CreateAutomaticProductionArmLocked(ProductionInspectionOwner owner,
        ProductionDeploymentManifest deployment, ProductionArmCause cause,
        RecipeChangeRequestEvidence? request, RecipeActivationReference? activation) =>
        new(owner, _productionInspectionOptions!.StationId, deployment, cause, request, activation,
            _productionArmMaintenanceEvidence, _productionArmMaintenanceEvidence?.Current,
            _productionArmStopGeneration, owner.Health?.ConnectionGeneration ?? 0,
            owner.Health?.ControllerEpoch ?? 0, owner.ArmInputSequence,
            _productionInspectionOptions.Profile.CommunicationBinding.Policy);

    private void ObserveProductionArmInputs(ProductionInspectionOwner owner, ModbusControllerSignals signals,
        bool dedicatedClear)
    {
        lock (_sync)
        {
            owner.ArmInputSequence = checked(owner.ArmInputSequence + 1);
            owner.ArmInputsClear = dedicatedClear && !signals.Trigger && !signals.ResultAck;
            var sequenceUnchanged = owner.ArmInputCycleSequence == signals.CycleSequence;
            owner.ArmInputCycleSequence = signals.CycleSequence;
            if (_automaticProductionArm is { Terminal: false } attempt && ReferenceEquals(attempt.Owner, owner))
                attempt.Stability.Observe(owner.ArmInputSequence, Stopwatch.GetTimestamp(),
                    owner.ArmInputsClear && sequenceUnchanged && owner.Health is { Healthy: true } &&
                    signals.ControllerEpoch == attempt.ControllerEpoch);
        }
    }

    private ProductionArmReason AutomaticProductionArmSourceFailureLocked(AutomaticProductionArmCapability attempt)
    {
        var owner = attempt.Owner;
        if (_disposed || _shutdownRequested || _lifetime.IsCancellationRequested ||
            owner.Cancellation.IsCancellationRequested || LocalStopPendingLocked ||
            _productionArmStopGeneration != attempt.StopGeneration)
            return ProductionArmReason.Cancelled;
        if (!ReferenceEquals(_productionInspectionOwner, owner) || owner.Aborted ||
            owner.RuntimeEpoch != _snapshot.RuntimeEpoch ||
            owner.Health is not { Healthy: true } health || health.ConnectionGeneration != attempt.ConnectionGeneration ||
            health.ControllerEpoch != attempt.ControllerEpoch ||
            _productionInspectionOptions?.Deployment?.ContentHash != attempt.Deployment.ContentHash ||
            _currentRecipeActivationReference != attempt.Activation ||
            !ReferenceEquals(_productionArmMaintenanceEvidence, attempt.MaintenanceProvider) ||
            !ReferenceEquals(_productionArmMaintenanceEvidence?.Current, attempt.Maintenance))
            return ProductionArmReason.AuthorityChanged;
        if (_snapshot.Mode != ExclusiveMode.None || _snapshot.Busy || owner.Current is not null ||
            _activationReservation is not null || _recipeSelectionChangeInProgress ||
            _recipeChangeInProgress || _snapshot.Recovery != RecoveryState.None)
            return ProductionArmReason.RuntimeBusy;
        return ProductionArmReason.None;
    }

    private bool AutomaticProductionArmReadyPermitLocked(ProductionInspectionOwner owner)
    {
        if (_automaticProductionArm is not { Terminal: false } attempt || !ReferenceEquals(attempt.Owner, owner))
            return true;
        return attempt.Authorized && attempt.Capture is { } capture && ProductionArmLiveFenceLocked(capture) &&
            AutomaticProductionArmSourceFailureLocked(attempt) == ProductionArmReason.None &&
            (attempt.PhysicalReadyObserved || attempt.ReadyDeadline is { Expired: false } &&
                owner.ArmInputsClear && attempt.Stability.IsStable(Stopwatch.GetTimestamp()));
    }

    private void ObservePhysicalProductionArmReadyLocked(ProductionInspectionOwner owner)
    {
        // Called while the observer's sample gate still excludes a controller
        // trigger. The zero-input prerequisite ends at this acknowledged write.
        if (_automaticProductionArm is { Terminal: false, Authorized: true } automatic && ReferenceEquals(automatic.Owner, owner))
        {
            automatic.ReadyReceipt = new(automatic.AttemptId, owner.RuntimeEpoch,
                automatic.AuthorizationEvent!.ContentHash, automatic.Activation, automatic.Maintenance!.JournalHeadHash,
                automatic.Capture!.Value.Generation, automatic.ControllerEpoch, automatic.ConnectionGeneration, DateTimeOffset.UtcNow);
            automatic.PhysicalReadyObserved = true;
            automatic.ReadyAuditPending = true;
        }
        if (_manualMaintenanceArm is { Terminal: false, Authorized: true } manual && ReferenceEquals(manual.Owner, owner))
        {
            manual.ReadyReceipt = new(manual.AttemptId, owner.RuntimeEpoch, manual.AuthorizationEvent!.ContentHash,
                manual.Activation, manual.Maintenance.JournalHeadHash, manual.Capture.Generation,
                owner.Health!.ControllerEpoch!.Value, manual.ConnectionGeneration, DateTimeOffset.UtcNow);
            manual.PhysicalReadyObserved = true;
            manual.ReadyAuditPending = true;
        }
    }

    private bool PreserveProductionArmReadyObservationLocked(StationStateSnapshot next, ProductionAdmissionReport? admission)
    {
        // Hold an already-observed candidate only while writing the receipt of
        // this acknowledged Ready. No Run is admitted until the owner finishes
        // its bounded audit wait. Any material change still revokes the candidate.
        if (_audit?.Integrity?.State is not (AuditIntegrityState.Verifying or AuditIntegrityState.Verified) ||
            next.ArmState != ProductionArmState.Armed || next.Mode != ExclusiveMode.None || next.Recovery != RecoveryState.None ||
            next.CurrentExecution is not null || next.Busy || _disposed || _shutdownRequested || LocalStopPendingLocked ||
            _lastVerifiedStoreIntegrityGate is not { Status: ProductionAdmissionGateStatus.Passed } ||
            admission is null || !admission.Gates.All(gate => gate.Status is ProductionAdmissionGateStatus.Passed or
                ProductionAdmissionGateStatus.NotApplicable || ProductionAdmissionEngine.IsTransientAuditRecheck(gate))) return false;
        if (_automaticProductionArm is { PhysicalReadyObserved: true, ReadyAuditPending: true, Capture: { } capture } automatic &&
            capture.RuntimeEpoch == next.RuntimeEpoch && capture.Generation == _admissionGeneration &&
            capture.StateHash == _admissionStateHash && AutomaticProductionArmSourceFailureLocked(automatic) == ProductionArmReason.None)
            return true;
        return _manualMaintenanceArm is { PhysicalReadyObserved: true, ReadyAuditPending: true } manual &&
            manual.Capture.RuntimeEpoch == next.RuntimeEpoch && manual.Capture.Generation == _admissionGeneration &&
            manual.Capture.StateHash == _admissionStateHash && manual.StopGeneration == _productionArmStopGeneration &&
            ReferenceEquals(_productionArmMaintenanceEvidence?.Current, manual.Maintenance) &&
            _currentRecipeActivationReference == manual.Activation && ReferenceEquals(_productionInspectionOwner, manual.Owner) &&
            manual.Owner.Health is { Healthy: true } health && health.ConnectionGeneration == manual.ConnectionGeneration;
    }

    private void ProjectAutomaticProductionArmLocked(AutomaticProductionArmCapability attempt,
        ProductionArmAttemptOutcome outcome, ProductionArmReason reason, string reasonCode,
        bool auditCommitted, bool delivered = false)
    {
        if (_disposed) return;
        var blocked = reason == ProductionArmReason.AdmissionGateBlocked ? attempt.Report?.Gates.FirstOrDefault(gate => gate.Status is not
            (ProductionAdmissionGateStatus.Passed or ProductionAdmissionGateStatus.NotApplicable))?.Gate : null;
        PublishLocked(_snapshot with { ProductionArming = new(attempt.Deployment.StartupProduction.Reference,
            attempt.Deployment.PostActivationArm.Reference, attempt.Owner.RuntimeEpoch, attempt.AttemptId,
            attempt.Cause, outcome, reason, reasonCode, attempt.ControllerEpoch,
            attempt.Request?.RequestSequence ?? 0, attempt.Request?.SelectionCode ?? 0,
            blocked, auditCommitted, delivered, DateTimeOffset.UtcNow) });
    }

    /// <summary>
    /// Created only by the two event handlers above. Caller commands, identity
    /// claims and public status records cannot create or spend this capability.
    /// </summary>
    private sealed class AutomaticProductionArmCapability
    {
        private int _claimed;
        internal AutomaticProductionArmCapability(ProductionInspectionOwner owner, string stationId,
            ProductionDeploymentManifest deployment, ProductionArmCause cause, RecipeChangeRequestEvidence? request,
            RecipeActivationReference? activation, IProductionArmMaintenanceEvidenceProvider? maintenanceProvider,
            ProductionArmMaintenanceEvidence? maintenance, long stopGeneration, long connectionGeneration,
            uint controllerEpoch, long inputSequence, PlcCommunicationPolicy communicationPolicy)
        {
            Owner = owner; StationId = stationId; Deployment = deployment; Cause = cause; Request = request;
            Activation = activation; MaintenanceProvider = maintenanceProvider; Maintenance = maintenance;
            StopGeneration = stopGeneration; ConnectionGeneration = connectionGeneration; ControllerEpoch = controllerEpoch;
            StartedAt = Stopwatch.GetTimestamp();
            Stability = new(communicationPolicy, StartedAt, inputSequence, Stopwatch.Frequency);
        }
        internal Guid AttemptId { get; } = Guid.NewGuid();
        internal ProductionInspectionOwner Owner { get; }
        internal string StationId { get; }
        internal ProductionDeploymentManifest Deployment { get; }
        internal ProductionArmCause Cause { get; }
        internal RecipeChangeRequestEvidence? Request { get; }
        internal RecipeActivationReference? Activation { get; }
        internal IProductionArmMaintenanceEvidenceProvider? MaintenanceProvider { get; }
        internal ProductionArmMaintenanceEvidence? Maintenance { get; }
        internal long StopGeneration { get; }
        internal long ConnectionGeneration { get; }
        internal uint ControllerEpoch { get; }
        internal long StartedAt { get; }
        internal ProductionArmInputStability Stability { get; }
        internal AdmissionCapture? Capture { get; set; }
        internal ProductionAdmissionReport? Report { get; set; }
        internal ProductionArmInputStabilityEvidence? InputStability { get; set; }
        internal IReadOnlyDictionary<string, string> Heads { get; set; } = new Dictionary<string, string>();
        internal bool AttemptCommitted { get; set; }
        internal bool Authorized { get; set; }
        internal StoreDeadline? ReadyDeadline { get; set; }
        internal bool ReadyFactCommitted { get; set; }
        internal bool PhysicalReadyObserved { get; set; }
        internal bool ReadyAuditPending { get; set; }
        internal ProductionArmHistoryEvent? AuthorizationEvent { get; set; }
        internal ProductionArmReadyReceipt? ReadyReceipt { get; set; }
        internal bool Terminal { get; set; }
        internal bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
    }
}
