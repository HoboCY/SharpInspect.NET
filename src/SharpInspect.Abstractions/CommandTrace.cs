using System.Collections.ObjectModel;

namespace SharpInspect.Abstractions;

public enum CommandAuditPhase { Outcome, Completed, Failed }
public enum AuditedCommandKind
{
    ArmProduction = 0,
    GracefulProductionStop = 1,
    Unsupported = 2,
    RotateSigningKey = 3,
    RetireSigningKey = 4,
    CorrectHistoricalFact = 5,
    DeleteEvidence = 6,
    CreateHumanAccount = 7,
    DisableHumanCredential = 8,
    UnlockHumanCredential = 9,
    RebindHumanCredential = 10,
    SetHumanPermissions = 11
}
public enum AuditPersistence { NotAttempted, Persisted, Unavailable }

/// <summary>Immutable facts. Claimed identities are input attribution, never authenticated identities.</summary>
public sealed record CommandTraceRecord(long Position, Guid EventId, Guid AttemptId, Guid CorrelationId,
    Guid RuntimeEpoch, int EventVersion, int AggregateSequence, DateTimeOffset OccurredAtUtc,
    string SystemPrincipalId, string? AuthenticatedHumanPrincipalId, AuditedCommandKind CommandKind,
    CommandSource? Source, string? ClaimedPrincipalId, Guid? ClaimedSessionId, Guid? ClaimedStepUpGrantId,
    CommandAuditPhase Phase, CommandDisposition? Disposition, string ReasonCode);

/// <summary>Keyset pagination. An explicit upper position keeps a multi-page view stable during writes.</summary>
public sealed record CommandTraceFilter(Guid? CorrelationId = null, string? ClaimedPrincipalId = null,
    long AfterPosition = 0, long? ThroughPosition = null, int PageSize = 50);

public sealed record CommandTracePage(ReadOnlyCollection<CommandTraceRecord> Records,
    long ThroughPosition, long? NextAfterPosition);

/// <summary>Read-only capability; does not expose a connection, writer, arbitrary SQL or mutation delegate.</summary>
public interface ICommandTraceQuery
{
    ValueTask<CommandTracePage> QueryAsync(CommandTraceFilter filter, CancellationToken cancellationToken = default);
}
