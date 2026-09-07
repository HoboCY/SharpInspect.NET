using System.Diagnostics;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime;

internal sealed record CommandAuditFact(Guid EventId, Guid AttemptId, Guid CorrelationId, Guid RuntimeEpoch,
    DateTimeOffset OccurredAtUtc, AuditedCommandKind CommandKind, CommandSource? Source,
    string? ClaimedPrincipalId, Guid? ClaimedSessionId, Guid? ClaimedStepUpGrantId,
    CommandAuditPhase Phase, CommandDisposition? Disposition, string ReasonCode,
    string? AuthenticatedHumanPrincipalId = null);

internal sealed record StoreWriteResult(bool Committed, string ReasonCode, CommandAuditFact? Fact = null);

/// <summary>The same monotonic deadline covers queue admission, locks and the transaction.</summary>
internal sealed class StoreDeadline
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly TimeSpan _timeout;
    public StoreDeadline(TimeSpan timeout) => _timeout = timeout;
    public TimeSpan Remaining => _timeout - TimeSpan.FromSeconds(
        (Stopwatch.GetTimestamp() - _started) / (double)Stopwatch.Frequency);
    public bool Expired => Remaining <= TimeSpan.Zero;
}

internal interface ICommandAuditWriter
{
    AuditIntegrityReport? Integrity => null;
    Task<StoreWriteResult> Initialization { get; }
    TimeSpan CommitTimeout { get; }
    ValueTask<StoreWriteResult> AppendAsync(CommandAuditFact fact, StoreDeadline deadline,
        CancellationToken cancellationToken = default);
}
