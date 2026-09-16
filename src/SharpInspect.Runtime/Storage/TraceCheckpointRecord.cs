using System.Text.Json;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

internal sealed record TraceCheckpointRecord(int FormatVersion, Guid OperationId, string ConfigurationHash,
    long PolicyVersion, string PublicationHash, string PolicySnapshotHash, string BudgetHash,
    DateTimeOffset StartedAtUtc, DateTimeOffset RecordedAtUtc, TraceCheckpointStatus Status,
    string ReasonCode, long BeforeWalBytes, long? AfterWalBytes, long? LogFrames,
    long? CheckpointedFrames, long? ElapsedTicks, string Actor = SystemPrincipalId.RetentionCleanup)
{
    internal byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this);
    internal TraceCheckpointObservation Observation() => new(Status, ReasonCode, RecordedAtUtc,
        PolicySnapshotHash, AfterWalBytes ?? BeforeWalBytes, LogFrames, CheckpointedFrames,
        ElapsedTicks is { } elapsed ? TimeSpan.FromTicks(elapsed) : null);
}

internal sealed record TraceCheckpointRow(long AuditSequence, string AuditHash, TraceCheckpointRecord Record);
