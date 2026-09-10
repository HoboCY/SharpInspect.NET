using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

internal sealed record QualificationCycleStoredRow(long Position, string? PreviousHash,
    QualificationCycleEvent Event, byte[] Payload, string PayloadHash,
    long AuditSequence, string AuditHash);

internal sealed record QualificationCycleRecoveryState(
    bool Available,
    string ReasonCode,
    IReadOnlyList<QualificationCycleEvent>? Events = null,
    QualificationCycleEvent? LastEvent = null,
    long LatestPosition = 0,
    string? LatestHash = null,
    bool RecoveryRequired = false,
    bool RecoverablePending = false)
{
    internal IReadOnlyList<QualificationCycleEvent> EventRecords => Events ??
        Array.Empty<QualificationCycleEvent>();
}

internal sealed record QualificationCycleWriteResult(
    RuntimeCommandOutcome Outcome,
    QualificationCycleEvent? Event,
    bool Accepted,
    long ExpectedLastPosition,
    string? ExpectedLastHash);
