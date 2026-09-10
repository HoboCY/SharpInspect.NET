using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Manual;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private sealed class ManualInspectionOwner
    {
        internal ManualInspectionOwner(ManualRecipeExecutionPlan plan, RecipeActivationRecord? baseline,
            ManualInspectionSessionHeader header, CommandAuditFact startFact)
        {
            Plan = plan; Baseline = baseline; Header = header; SessionId = header.SessionId;
            ActorPrincipalId = header.ActorPrincipalId; ActorSessionId = header.ActorSessionId;
            ActorAuthorizationRevision = header.ActorAuthorizationRevision; AuthorizationPolicy = header.AuthorizationPolicy;
            StartFact = startFact;
        }

        internal ManualRecipeExecutionPlan Plan { get; }
        internal RecipeActivationRecord? Baseline { get; }
        internal ManualInspectionSessionHeader Header { get; set; }
        internal ManualInspectionRunRecord? CurrentRun { get; set; }
        internal Guid SessionId { get; }
        internal Guid ActorPrincipalId { get; }
        internal Guid ActorSessionId { get; }
        internal long ActorAuthorizationRevision { get; }
        internal RecipeContractReference AuthorizationPolicy { get; }
        internal CommandAuditFact StartFact { get; }
        internal RecipeActivationCameraLease? Camera { get; set; }
        internal PreparedAlgorithm? Prepared { get; set; }
        internal Task PreparationRetirement { get; set; } = Task.CompletedTask;
        internal AlgorithmExecutionService? Execution { get; set; }
        internal IFrameAcquisitionClock? Clock { get; set; }
        internal ManualInspectionCommand? PendingCommand { get; set; }
        internal CommandAuditFact? PendingFact { get; set; }
        internal ExitManualInspectionSessionCommand? ExitCommand { get; set; }
        internal CommandAuditFact? ExitFact { get; set; }
        internal List<(ExitManualInspectionSessionCommand Command, CommandAuditFact Fact)> PriorExitCommands { get; } = new();
        internal Guid? CurrentRunId { get; set; }
        internal Guid? LastRunId { get; set; }
        internal ManualInspectionRestorationState Restoration { get; set; } = ManualInspectionRestorationState.Pending;
        internal bool ExitRequested { get; set; }
        internal bool Aborted { get; set; }
        internal string ExitReason { get; set; } = "ManualInspectionExited";
        internal CancellationTokenSource AbortCancellation { get; } = new();
        internal Task? Operation { get; set; }
        internal Task? ExitWorker { get; set; }
        internal TaskCompletionSource<bool> Retired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal long PhysicalPhaseId { get; set; }
        internal int TerminalStarted;
    }
}
