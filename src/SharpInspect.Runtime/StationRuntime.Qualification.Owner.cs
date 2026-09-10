using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Qualification;
using SharpInspect.Runtime.Cycles;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private sealed class StationQualificationOwner
    {
        internal StationQualificationOwner(StationQualificationSessionHeader header,
            StationQualificationSessionEvent lastEvent, CommandAuditFact startFact)
        { Header = header; LastEvent = lastEvent; StartFact = startFact; RecoveryAttempt = lastEvent.RecoveryAttempt; }

        internal StationQualificationSessionHeader Header { get; }
        internal StationQualificationSessionEvent LastEvent { get; set; }
        internal CommandAuditFact StartFact { get; }
        internal CommandAuditFact? ExitFact { get; set; }
        internal string? ExitAuthorizationTarget { get; set; }
        internal StationQualificationRecoveryAttempt? RecoveryAttempt { get; set; }
        internal QualificationFacilityRequest Request => new(Header.SessionId,
            RecoveryAttempt?.LeaseNonce ?? Header.LeaseNonce,
            RecoveryAttempt?.RuntimeEpoch ?? Header.RuntimeEpoch, Header.Plan);
        internal IStationQualificationFacilityLease? Facility { get; set; }
        internal CancellationTokenRegistration FacilityLostRegistration { get; set; }
        internal RecipeActivationCameraLease? Camera { get; set; }
        internal PreparedAlgorithm? Prepared { get; set; }
        internal AlgorithmExecutionService? Execution { get; set; }
        internal Task PreparationRetirement { get; set; } = Task.CompletedTask;
        internal StationQualificationRunRecord? CurrentRun { get; set; }
        internal QualificationRunId? CurrentRunId { get; set; }
        internal QualificationRunId? LastRunId { get; set; }
        internal long ObservationSequence { get; set; }
        internal long StimulusSequence { get; set; }
        internal int RunCount { get; set; }
        internal long PhysicalPhaseId { get; set; }
        internal bool ExitRequested { get; set; }
        internal bool Aborted { get; set; }
        internal bool RestartRecovery { get; set; }
        internal bool ResourcesRetired { get; set; }
        internal bool CycleExecuting { get; set; }
        internal bool ModbusRecoveryRequired { get; set; }
        internal bool CycleFaultTerminated { get; set; }
        internal QualificationCycleEvent? LastCycleEvent { get; set; }
        internal InspectionCycleCoordinator<StationQualificationPayload>? CycleCoordinator { get; set; }
        internal InspectionCycleRequestObserver? CycleObserver { get; set; }
        internal TraceStoragePolicySnapshot? CycleStoragePolicy { get; set; }
        internal string ExitReason { get; set; } = "StationQualificationExited";
        internal StationQualificationRestorationState Restoration { get; set; } = StationQualificationRestorationState.Pending;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal CancellationTokenSource StimulusCancellation { get; } = new();
        internal Task? Operation { get; set; }
        internal TaskCompletionSource<bool> Retired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
