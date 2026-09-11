using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cycles;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private ManualMaintenanceArmCapability? _manualMaintenanceArm;

    private async Task<bool> PrepareManualMaintenanceArmAsync(ArmProductionCommand command,
        AdmissionCapture capture, IReadOnlyDictionary<string, string> heads, StoreDeadline deadline)
    {
        ProductionArmMaintenanceEvidence? maintenance;
        ProductionInspectionOwner? owner;
        ProductionDeploymentManifest? deployment;
        lock (_sync)
        {
            maintenance = _productionArmMaintenanceEvidence?.Current;
            owner = _productionInspectionOwner;
            deployment = _productionInspectionOptions?.Deployment;
        }
        // Unknown maintenance cannot be marked complete by a human Arm. Normal
        // human admission keeps its existing gates; no automatic clearance is inferred.
        if (maintenance?.State == ProductionArmMaintenanceState.InProgress) return false;
        if (maintenance?.State != ProductionArmMaintenanceState.ManualArmRequired) return true;
        if (owner is null || deployment is null || _productionInspectionStoreOptions?.ProductionArming is null ||
            maintenance.StationId != _productionInspectionOptions!.StationId || maintenance.DeploymentHash != deployment.ContentHash)
            return false;
        var history = await new SqliteProductionAdmissionHistoryQuery(_productionInspectionStoreOptions)
            .ReadAsync(command.CorrelationId, CancellationToken.None).AsTask()
            .WaitAsync(PositiveRemaining(deadline)).ConfigureAwait(false);
        if (!history.Available || history.Latest is not { Kind: ProductionAdmissionEventKind.Completed } completed ||
            completed.RuntimeEpoch != capture.RuntimeEpoch || completed.AdmissionGeneration != capture.Generation ||
            completed.CorrelationId != command.CorrelationId || !completed.Report.CanArm)
            return false;
        ManualMaintenanceArmCapability attempt;
        lock (_sync)
        {
            if (!ProductionArmLiveFenceLocked(capture) ||
                !ReferenceEquals(_productionArmMaintenanceEvidence?.Current, maintenance) ||
                _manualMaintenanceArm is { Terminal: false }) return false;
            attempt = new(owner, deployment, maintenance, completed, capture, heads, _productionArmStopGeneration,
                owner.Health?.ConnectionGeneration ?? 0, _currentRecipeActivationReference,
                new StoreDeadline(_productionInspectionOptions!.Profile.TransportTimeout + _audit!.CommitTimeout));
            _manualMaintenanceArm = attempt;
        }
        var started = await WriteManualMaintenanceArmAsync(attempt, ProductionArmEventKind.Attempted,
            ProductionArmReason.None, "ProductionArmManualMaintenanceAttempted").ConfigureAwait(false);
        attempt.AttemptCommitted = started.Committed;
        if (!started.Committed) return false;
        var authorized = await WriteManualMaintenanceArmAsync(attempt, ProductionArmEventKind.Authorized,
            ProductionArmReason.None, "ProductionArmManualMaintenanceAuthorized").ConfigureAwait(false);
        attempt.AuthorizationEvent = authorized.Event;
        attempt.Authorized = authorized.Committed;
        var verified = authorized.Committed && authorized.Event is not null && await WaitForProductionAuditVerifiedAsync(deadline).ConfigureAwait(false) &&
            await VerifyProductionAdmissionHeadsAsync(heads, deadline).ConfigureAwait(false);
        lock (_sync)
        {
            attempt.Prepared = verified && ManualMaintenanceReadyPermitLocked(owner);
            return attempt.Prepared;
        }
    }

    private bool ManualMaintenanceReadyPermitLocked(ProductionInspectionOwner owner)
    {
        if (_manualMaintenanceArm is not { Terminal: false } attempt || !ReferenceEquals(attempt.Owner, owner)) return true;
        return attempt.Authorized && (attempt.PhysicalReadyObserved || attempt.ReadyDeadline is { Expired: false }) &&
            ProductionArmLiveFenceLocked(attempt.Capture) &&
            ReferenceEquals(_productionArmMaintenanceEvidence?.Current, attempt.Maintenance) &&
            _productionArmStopGeneration == attempt.StopGeneration &&
            _currentRecipeActivationReference == attempt.Activation &&
            owner.Health is { Healthy: true } health && health.ConnectionGeneration == attempt.ConnectionGeneration;
    }

    private async Task ConfirmManualMaintenanceReadyAsync(ProductionInspectionOwner owner,
        InspectionCycleOutputLatch output)
    {
        ManualMaintenanceArmCapability? attempt;
        bool valid;
        lock (_sync)
        {
            attempt = _manualMaintenanceArm;
            if (attempt is not { Terminal: false, Authorized: true } || !ReferenceEquals(attempt.Owner, owner)) return;
            valid = attempt.PhysicalReadyObserved && attempt.ReadyReceipt is not null;
        }
        if (!valid)
        {
            await FailManualMaintenanceArmAsync(attempt, output, "ProductionArmManualMaintenanceChanged").ConfigureAwait(false);
            return;
        }
        var recorded = await WriteManualMaintenanceArmAsync(attempt, ProductionArmEventKind.ReadyConfirmed,
            ProductionArmReason.None, "ProductionArmManualMaintenancePhysicalReadyConfirmed").ConfigureAwait(false);
        attempt.ReadyFactCommitted = recorded.Committed;
        var deadline = new StoreDeadline(_audit!.CommitTimeout);
        var status = recorded.Committed ? await RecordProductionArmStatusUnavailableAsync(recorded.Event!,
            "ProductionArmManualStatusNotPublished").ConfigureAwait(false) : recorded;
        var verified = recorded.Committed && status.Committed && await WaitForProductionAuditVerifiedAsync(deadline).ConfigureAwait(false);
        var stillCurrent = false;
        lock (_sync)
        {
            if (!_disposed) PublishLocked(_snapshot);
            valid = verified;
            if (valid)
            {
                attempt.Terminal = true;
                attempt.ReadyAuditPending = false;
                stillCurrent = ProductionArmLiveFenceLocked(attempt.Capture) &&
                    _productionArmStopGeneration == attempt.StopGeneration &&
                    ReferenceEquals(_productionArmMaintenanceEvidence?.Current, attempt.Maintenance) &&
                    _currentRecipeActivationReference == attempt.Activation;
                PublishLocked(_snapshot with { Ready = stillCurrent,
                    ArmState = stillCurrent ? ProductionArmState.Armed : ProductionArmState.Disarmed });
            }
        }
        if (!valid) await FailManualMaintenanceArmAsync(attempt, output,
            "ProductionArmManualMaintenanceReadyUnconfirmed").ConfigureAwait(false);
        else if (!stillCurrent)
            await ClearConfirmedProductionArmReadyAsync(output, auditUnavailable: false).ConfigureAwait(false);
    }

    private async Task FailManualMaintenanceArmAsync(ManualMaintenanceArmCapability attempt,
        InspectionCycleOutputLatch? output, string reason)
    {
        lock (_sync)
        {
            if (attempt.Terminal) return;
            attempt.Terminal = true;
            attempt.ReadyAuditPending = false;
            attempt.Owner.Observer?.RejectPendingAdmission(reason);
            if (!_disposed) PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed });
        }
        if (attempt.AttemptCommitted && !attempt.ReadyFactCommitted)
        {
            var failed = await WriteManualMaintenanceArmAsync(attempt, ProductionArmEventKind.Failed,
                ProductionArmReason.AuthorityChanged, reason).ConfigureAwait(false);
            if (!failed.Committed) MarkAuditFault("ProductionArmManualTerminalAuditUnavailable");
            else
            {
                var status = await RecordProductionArmStatusUnavailableAsync(failed.Event!,
                    "ProductionArmManualStatusNotPublished").ConfigureAwait(false);
                if (!status.Committed) MarkAuditFault("ProductionArmManualStatusAuditUnavailable");
            }
        }
        if (attempt.PhysicalReadyObserved)
        {
            MarkAuditFault("ProductionArmPhysicalReadyAuditUnavailable");
            lock (_sync)
            {
                _productionInspectionRecoveryBlocked = true;
                if (!_disposed) PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                    Recovery = RecoveryState.Required });
            }
        }
        if (output is null) return;
        try
        {
            using var cleanup = new CancellationTokenSource(_productionInspectionOptions!.Profile.TransportTimeout);
            await output.ChangeAsync(cleanup.Token, ready: false, requireOwner: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_sync)
            {
                _productionInspectionRecoveryBlocked = true;
                if (!_disposed) PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                    Recovery = RecoveryState.Required });
            }
        }
    }

    private async Task<bool> HasVerifiedManualMaintenanceArmAsync(ProductionArmMaintenanceEvidence maintenance,
        CancellationToken token)
    {
        if (maintenance.State == ProductionArmMaintenanceState.ManualArmConfirmed) return true;
        if (maintenance.State != ProductionArmMaintenanceState.ManualArmRequired ||
            _productionInspectionStoreOptions?.ProductionArming is null) return false;
        var query = new SqliteProductionArmHistoryQuery(_productionInspectionStoreOptions);
        long after = 0;
        long? through = null;
        do
        {
            var page = await query.QueryAsync(new(AfterPosition: after, ThroughPosition: through, PageSize: 128), token)
                .ConfigureAwait(false);
            if (!page.Available) return false;
            through ??= page.ThroughPosition;
            if (page.Events.Any(value => value.Cause == ProductionArmCause.ManualMaintenanceArm &&
                    value.Kind == ProductionArmEventKind.ReadyConfirmed && value.StationId == maintenance.StationId &&
                    value.DeploymentHash == maintenance.DeploymentHash && value.MaintenanceHeadHash == maintenance.JournalHeadHash))
                return true;
            if (page.NextAfterPosition is not { } next) return false;
            if (next <= after) return false;
            after = next;
        } while (true);
    }

    private ValueTask<ProductionArmWriteResult> WriteManualMaintenanceArmAsync(ManualMaintenanceArmCapability attempt,
        ProductionArmEventKind kind, ProductionArmReason reason, string reasonCode)
    {
        var admitted = attempt.Admission;
        var request = new ProductionArmWriteRequest(attempt.AttemptId, attempt.Owner.RuntimeEpoch,
            ProductionArmCause.ManualMaintenanceArm, kind, attempt.Maintenance.StationId,
            attempt.Deployment.StartupProduction.Reference, attempt.Deployment.PostActivationArm.Reference,
            attempt.Deployment.ContentHash, null, attempt.Activation, attempt.Maintenance.JournalHeadHash,
            attempt.Capture.Generation, admitted.Report, attempt.Heads, attempt.Heads, reason, reasonCode,
            admitted.CorrelationId, admitted.ActorPrincipalId, admitted.ActorSessionId, attempt.ReadyReceipt);
        return ((SqliteCommandStore)_audit!).AppendProductionArmEventAsync(request,
            new StoreDeadline(_audit.CommitTimeout), CancellationToken.None);
    }

    private sealed class ManualMaintenanceArmCapability
    {
        internal ManualMaintenanceArmCapability(ProductionInspectionOwner owner, ProductionDeploymentManifest deployment,
            ProductionArmMaintenanceEvidence maintenance, ProductionAdmissionHistoryEvent admission,
            AdmissionCapture capture, IReadOnlyDictionary<string, string> heads, long stopGeneration,
            long connectionGeneration, RecipeActivationReference? activation, StoreDeadline readyDeadline)
        {
            Owner = owner; Deployment = deployment; Maintenance = maintenance; Admission = admission;
            Capture = capture; Heads = heads; StopGeneration = stopGeneration;
            ConnectionGeneration = connectionGeneration; Activation = activation; ReadyDeadline = readyDeadline;
        }
        internal Guid AttemptId { get; } = Guid.NewGuid();
        internal ProductionInspectionOwner Owner { get; }
        internal ProductionDeploymentManifest Deployment { get; }
        internal ProductionArmMaintenanceEvidence Maintenance { get; }
        internal ProductionAdmissionHistoryEvent Admission { get; }
        internal AdmissionCapture Capture { get; }
        internal IReadOnlyDictionary<string, string> Heads { get; }
        internal long StopGeneration { get; }
        internal long ConnectionGeneration { get; }
        internal RecipeActivationReference? Activation { get; }
        internal StoreDeadline ReadyDeadline { get; }
        internal bool AttemptCommitted { get; set; }
        internal bool Authorized { get; set; }
        internal bool Prepared { get; set; }
        internal bool ReadyFactCommitted { get; set; }
        internal bool PhysicalReadyObserved { get; set; }
        internal bool ReadyAuditPending { get; set; }
        internal ProductionArmHistoryEvent? AuthorizationEvent { get; set; }
        internal ProductionArmReadyReceipt? ReadyReceipt { get; set; }
        internal bool Terminal { get; set; }
    }
}
