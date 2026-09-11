namespace SharpInspect.Abstractions;

public enum RuntimeLifecycle { Starting, Running, Stopped }
public enum ExclusiveMode { None, Preview, ManualInspection, Calibration, Qualification, Maintenance }
public enum ProductionArmState { Disarmed, Armed }
public enum HealthState { Unknown, Unconfigured, Healthy, Degraded, Faulted }
public enum HandshakePhase { Idle, AwaitingResultAck, AwaitingAckReset, Unknown }
public enum RecoveryState { None, Required, InProgress }
public enum QualificationMatch { Missing, Matches, Mismatch, Expired }
public enum InteractiveSessionState { Unauthenticated, Authenticated, Locked }
public enum ExecutionKind { Production, Manual, Qualification, Calibration }
public enum OperationState { Pending, Completed, Failed }

public sealed record ExecutionCorrelationId(ExecutionKind Kind, Guid Value);
public sealed record RecipeReference(string Id, string Version, string ContentHash);
public sealed record SubsystemHealth(HealthState State, string ReasonCode);
public sealed record CameraHealth(HealthState Connection, HealthState Configuration,
    HealthState Acquisition, HealthState Buffers);
public sealed record PlcHealth(HealthState Connection, HealthState Heartbeat, HealthState Synchronization);
public sealed record EvidenceHealth(HealthState State, int PendingRequiredImages, int PendingDeliveries);
public sealed record QualificationState(QualificationMatch Framework, QualificationMatch Provider,
    QualificationMatch Performance, QualificationMatch StationAcceptance);
public sealed record PerformanceHealth(HealthState State, bool BudgetViolation);
public sealed record AlarmSummary(int ActiveCount, int LatchedCount, bool BlocksProduction);
public sealed record InteractiveSession(InteractiveSessionState State, string? PrincipalId, Guid? SessionId);
public sealed record CommandProgress(Guid CorrelationId, OperationState State, string ReasonCode);

/// <summary>A complete immutable projection. Health, arming, and product execution are separate facts.</summary>
public sealed record StationStateSnapshot(
    Guid RuntimeEpoch,
    long Revision,
    DateTimeOffset ObservedAtUtc,
    RuntimeLifecycle Lifecycle,
    ExclusiveMode Mode,
    ProductionArmState ArmState,
    bool Ready,
    bool Busy,
    HandshakePhase Handshake,
    RecoveryState Recovery,
    RecipeReference? ActiveRecipe,
    ExecutionCorrelationId? CurrentExecution,
    CameraHealth Camera,
    PlcHealth Plc,
    SubsystemHealth Store,
    EvidenceHealth Evidence,
    QualificationState Qualification,
    PerformanceHealth Performance,
    AlarmSummary Alarms,
    InteractiveSession Session,
    CommandProgress? LastCommand,
    AdmissionBlockers AdmissionBlockers,
    AuditIntegrityReport? AuditIntegrity = null,
    AlarmStateSnapshot? AlarmState = null,
    CameraSetupState? CameraSetup = null,
    CameraRecoverySnapshot? CameraRecovery = null,
    CalibrationSessionState? CalibrationSession = null)
{
    /// <summary>Read-only admission evidence attached without changing the legacy constructor ABI.</summary>
    public ProductionAdmissionReport? ProductionAdmission { get; init; }

    /// <summary>Observed mutual liveness and synchronization; absent bindings never imply health.</summary>
    public PlcCommunicationHealth? PlcCommunication { get; init; }

    /// <summary>Retained pending inspections and their immutable recovery anchors.</summary>
    public ProductionRecoveryPendingPage? ProductionRecovery { get; init; }
}

/// <summary>Defensively copies all values; callers cannot mutate a published blocker list.</summary>
public sealed class AdmissionBlockers : System.Collections.ObjectModel.ReadOnlyCollection<string>
{
    public AdmissionBlockers(IEnumerable<string> values) : base(Array.AsReadOnly(values.ToArray())) { }
}
