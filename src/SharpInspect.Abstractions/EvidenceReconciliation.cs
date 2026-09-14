using System.Collections.ObjectModel;

namespace SharpInspect.Abstractions;

public enum EvidenceReconciliationPhase : byte { Startup = 1, HistoricalScrub = 2 }
public enum EvidenceReconciliationSubjectKind : byte { Image = 1, Outbox = 2, Orphan = 3 }
public enum EvidenceReconciliationFileArea : byte { Stage = 1, Final = 2 }

/// <summary>Closed immutable facts. A check never replaces an original lifecycle event.</summary>
public enum EvidenceReconciliationEventKind : byte
{
    RunStarted = 1,
    ImageVerified = 2,
    ImageFinalRecovered = 3,
    OutboxVerified = 4,
    QuarantineIntent = 5,
    Quarantined = 6,
    IntegrityFault = 7,
    PageCompleted = 8,
    RunCompleted = 9,
    WorkDeferred = 10
}

/// <summary>
/// Read-only subject identity. An orphan has its own OrphanId and file observations;
/// its InspectionId, ManifestId, WorkId and DeliveryId are always absent. Unknown
/// file length or hash is null, never a fabricated empty-content observation.
/// </summary>
public sealed record EvidenceReconciliationSubject(
    EvidenceReconciliationSubjectKind Kind,
    long? SourcePosition,
    Guid? InspectionId,
    Guid? ManifestId,
    Guid? WorkId,
    Guid? DeliveryId,
    Guid? OrphanId,
    string? ReferenceContentHash,
    string? ObservedLifecycleHash,
    EvidenceReconciliationFileArea? FileArea,
    string? SourceFileName,
    string? SourceRootBindingHash,
    string? QuarantineFileName,
    string? QuarantineRootBindingHash,
    string? FileIdentityHash,
    long? ByteLength,
    string? ObservedContentHash);

public sealed record EvidenceReconciliationRecord(long Position, Guid EventId, Guid RunId,
    Guid RuntimeEpoch, EvidenceReconciliationPhase Phase, EvidenceReconciliationEventKind Kind,
    EvidenceReconciliationSubject? Subject, DateTimeOffset RecordedAtUtc, string ReasonCode,
    long AuditSequence, string ContentHash);

/// <summary>
/// Replayed progress of one signed run. Source positions are keyset cursors against a
/// frozen upper bound. Scanned and verified counts remain separate: deferred work is
/// not represented as verified historical evidence.
/// </summary>
public sealed record EvidenceReconciliationProgress(Guid RunId, EvidenceReconciliationPhase Phase,
    long ThroughSourcePosition, long AfterSourcePosition, long ScannedItems, long VerifiedItems,
    long DeferredItems, long VerifiedBytes, bool Completed, bool IntegrityFault,
    long LastEventPosition, string LastEventContentHash, DateTimeOffset LastRecordedAtUtc);

public sealed record EvidenceReconciliationFilter(long AfterPosition = 0, int PageSize = 128,
    Guid? RunId = null);

/// <summary>One bounded page and its current verified progress projections.</summary>
public sealed class EvidenceReconciliationSnapshot
{
    public EvidenceReconciliationSnapshot(bool available, string reasonCode, long throughAuditSequence,
        IEnumerable<EvidenceReconciliationRecord>? records, long nextPosition, bool hasMore,
        EvidenceReconciliationProgress? latestStartup, EvidenceReconciliationProgress? scrubber,
        int pendingQuarantines, bool integrityFaultRecorded)
    {
        if (throughAuditSequence < 0 || nextPosition < 0 || pendingQuarantines < 0)
            throw new ArgumentOutOfRangeException(nameof(throughAuditSequence));
        Available = available;
        ReasonCode = reasonCode ?? string.Empty;
        ThroughAuditSequence = throughAuditSequence;
        Records = new ReadOnlyCollection<EvidenceReconciliationRecord>(
            (records ?? Array.Empty<EvidenceReconciliationRecord>()).ToArray());
        NextPosition = nextPosition;
        HasMore = hasMore;
        LatestStartup = latestStartup;
        Scrubber = scrubber;
        PendingQuarantines = pendingQuarantines;
        IntegrityFaultRecorded = integrityFaultRecorded;
    }
    public bool Available { get; }
    public string ReasonCode { get; }
    public long ThroughAuditSequence { get; }
    public ReadOnlyCollection<EvidenceReconciliationRecord> Records { get; }
    public long NextPosition { get; }
    public bool HasMore { get; }
    public EvidenceReconciliationProgress? LatestStartup { get; }
    public EvidenceReconciliationProgress? Scrubber { get; }
    public int PendingQuarantines { get; }
    public bool IntegrityFaultRecorded { get; }
}

/// <summary>Verified history only; reading this interface grants no recovery authority.</summary>
public interface IEvidenceReconciliationQuery
{
    ValueTask<EvidenceReconciliationSnapshot> ReadAsync(EvidenceReconciliationFilter filter,
        CancellationToken cancellationToken = default);
}
