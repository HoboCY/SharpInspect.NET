namespace SharpInspect.Abstractions;

/// <summary>The observed result of one policy-controlled attempt, never an arm command.</summary>
public enum ProductionArmAttemptOutcome : ushort
{
    Waiting = 1,
    Authorized = 2,
    ReadyConfirmed = 3,
    Rejected = 4,
    ActivatedButNotReady = 5,
    Failed = 6
}

/// <summary>Read-only policy/attempt projection. ReadyConfirmed requires the actual PLC write acknowledgement.</summary>
public sealed record ProductionArmPolicyStatus(
    RecipeContractReference StartupPolicy,
    RecipeContractReference PostActivationPolicy,
    Guid RuntimeEpoch,
    Guid AttemptId,
    ProductionArmCause Cause,
    ProductionArmAttemptOutcome Outcome,
    ProductionArmReason Reason,
    string ReasonCode,
    uint ControllerEpoch,
    uint RequestSequence,
    uint SelectionCode,
    ProductionAdmissionGate? BlockedGate,
    bool AuditCommitted,
    bool PlcStatusDelivered,
    DateTimeOffset ObservedAtUtc);
