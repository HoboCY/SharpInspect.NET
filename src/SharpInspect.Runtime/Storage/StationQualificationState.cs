using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;

namespace SharpInspect.Runtime.Storage;

/// <summary>Exact dependency snapshot read inside the authoritative writer transaction.</summary>
internal sealed record StationQualificationAdmissionInput(
    StationQualificationPlan Plan,
    RecipeActivationRecord TargetBaseline,
    CameraSetupStoreSnapshot CurrentBinding);

/// <summary>
/// Bounded state read from the schema-23 ledger.  A pending header is projected
/// from the last event of each session, never from a caller-selected row.
/// </summary>
internal sealed record StationQualificationCommandState(
    bool Enabled,
    StationQualificationSessionHeader? Header,
    IReadOnlyList<StationQualificationSessionEvent> Events,
    IReadOnlyList<StationQualificationRunRecord> Runs,
    RecipeActivationRecord? TargetBaseline,
    CameraSetupStoreSnapshot? CurrentBinding,
    bool RecoveryRequired,
    StationQualificationSessionHeader? PendingHeader = null,
    CommandAuditFact? StartCommandFact = null,
    IReadOnlyList<CommandAuditFact>? PendingCommandFacts = null)
{
    internal IReadOnlyList<CommandAuditFact> CommandFacts => PendingCommandFacts ??
        Array.Empty<CommandAuditFact>();
}

/// <summary>One append operation for the independent qualification ledger.</summary>
internal sealed record StationQualificationMutation(
    StationQualificationSessionHeader Header,
    StationQualificationSessionEvent Event,
    IReadOnlyList<CommandAuditFact>? AdditionalTerminalFacts = null,
    bool CompleteOriginalStart = false,
    bool CompleteCommand = true);

/// <summary>
/// Progress/terminal request supplied by the Runtime after a physical facility
/// operation.  It carries the CAS cursor from the previous sealed event and the
/// original header; it cannot change ownership or the frozen target baseline.
/// </summary>
internal sealed record StationQualificationProgressRequest(
    StationQualificationSessionHeader Header,
    long ExpectedLastPosition,
    string? ExpectedLastHash,
    Guid CommandCorrelationId,
    Guid AttemptId,
    AuditedCommandKind CommandKind,
    StationQualificationSessionPhase Phase,
    StationQualificationRestorationState Restoration,
    string ReasonCode,
    bool Terminal,
    QualificationFacilityObservation? Observation = null,
    StationQualificationRunRecord? Run = null,
    CommandAuditFact? CommandFact = null,
    bool CompleteOriginalStart = false,
    bool CompleteCommand = true,
    Func<StationQualificationRunRecord, StationQualificationRunRecord>? FinalizeRun = null,
    StationQualificationRecoveryAttempt? RecoveryAttempt = null,
    string? CommandAuthorizationTarget = null,
    Func<IdentityAuthorityState, StationQualificationProgressRequest,
        StationQualificationProgressAuthorization>? AuthorizeProgress = null);

/// <summary>Result returned by a station qualification storage transaction.</summary>
internal sealed record StationQualificationTransactionResult(
    RuntimeCommandOutcome Outcome,
    StationQualificationSessionHeader? Header,
    StationQualificationSessionEvent? Event,
    bool Accepted,
    long ExpectedLastPosition,
    string? ExpectedLastHash,
    CommandAuditFact? CommandFact = null);

/// <summary>Durable startup state for a previously admitted qualification session.</summary>
internal sealed record StationQualificationRecoveryState(
    bool Available,
    string ReasonCode,
    StationQualificationSessionEvent? LastEvent = null,
    StationQualificationSessionHeader? Header = null,
    CommandAuditFact? StartFact = null,
    RecipeActivationRecord? TargetBaseline = null,
    IReadOnlyList<StationQualificationSessionEvent>? Events = null,
    IReadOnlyList<StationQualificationRunRecord>? Runs = null,
    IReadOnlyList<CommandAuditFact>? PendingCommandFacts = null,
    bool RecoveryRequired = false,
    bool RecoverablePending = false)
{
    internal IReadOnlyList<StationQualificationSessionEvent> SessionEvents => Events ??
        Array.Empty<StationQualificationSessionEvent>();

    internal IReadOnlyList<StationQualificationRunRecord> RunRecords => Runs ??
        Array.Empty<StationQualificationRunRecord>();

    internal IReadOnlyList<CommandAuditFact> CommandFacts => PendingCommandFacts ??
        Array.Empty<CommandAuditFact>();
}
