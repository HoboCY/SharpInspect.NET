using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private RecipeActivationPhysicalPhaseClaim ClaimManualInspectionPhysicalPhase(ManualInspectionOwner owner)
    {
        lock (_sync)
        {
            // Graceful exit drains admitted work. Only Runtime abort authority
            // prevents another physical stage of that already accepted operation.
            if (!ReferenceEquals(_manualOwner, owner) || owner.Aborted || owner.AbortCancellation.IsCancellationRequested ||
                _shutdownRequested || _disposed)
                return RecipeActivationPhysicalPhaseClaim.Unavailable("ManualInspectionStopping");
            if (owner.PhysicalPhaseId != 0)
                return RecipeActivationPhysicalPhaseClaim.Unavailable("ManualInspectionPhysicalOperationInProgress");
            var id = checked(++_manualPhysicalSequence);
            owner.PhysicalPhaseId = id;
            return RecipeActivationPhysicalPhaseClaim.Granted(id, () =>
            {
                lock (_sync)
                    if (ReferenceEquals(_manualOwner, owner) && owner.PhysicalPhaseId == id)
                        owner.PhysicalPhaseId = 0;
            });
        }
    }

    private void PublishManualInspectionLocked(ManualInspectionOwner owner,
        ManualInspectionSessionPhase phase, string reason)
    {
        if (!ReferenceEquals(_manualOwner, owner)) return;
        _manualSnapshot = new(_snapshot.RuntimeEpoch, checked(++_manualRevision), owner.SessionId,
            phase, reason, DateTimeOffset.UtcNow, owner.ActorPrincipalId, owner.ActorSessionId,
            owner.Plan.Selection, owner.CurrentRunId, owner.LastRunId, owner.Restoration,
            _manualRecoveryBlocked, owner.ExitRequested,
            owner.PendingCommand?.CorrelationId ?? owner.StartFact.CorrelationId);
        PublishLocked(_snapshot with
        {
            Busy = owner.CurrentRunId.HasValue,
            CurrentExecution = owner.CurrentRunId is { } runId
                ? new ExecutionCorrelationId(ExecutionKind.Manual, runId) : null,
            Ready = false, ArmState = ProductionArmState.Disarmed
        });
    }
}
