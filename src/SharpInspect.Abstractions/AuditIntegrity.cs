namespace SharpInspect.Abstractions;

public enum AuditIntegrityState { NotConfigured, Verifying, Verified, Faulted }

/// <summary>A bounded observation, never a claim of machine compromise resistance.</summary>
public sealed record AuditIntegrityReport(AuditIntegrityState State, string ReasonCode,
    string? StationId, string? PolicyVersion, long ThroughSequence, long VerifiedFromSequence,
    long VerifiedThroughSequence, long? CheckpointSequence, long? AnchoredSequence,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// Requests a segment after the cursor. Verification may revisit a preceding signed checkpoint;
/// that trusted prefix counts against the configured verification budget. The requested length
/// must fit the budget after reserving one checkpoint interval. Every valid policy supports 200.
/// </summary>
public sealed record AuditVerificationRequest(long AfterSequence = 0, int MaximumEntries = 200);

public interface IAuditIntegrityQuery
{
    ValueTask<AuditIntegrityReport> VerifyAsync(AuditVerificationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AuditCheckpoint(Guid CheckpointId, string StationId, long Sequence,
    string HeadHash, int CanonicalizationVersion, int HashSchemeVersion, string PolicyVersion,
    string PolicyHash, string SigningKeyId, string PublicKeyBase64, DateTimeOffset AuditTime,
    string SignatureBase64);

/// <summary>A delivery receipt supplied by the explicitly configured external anchor.</summary>
public sealed record AuditAnchorReceipt(Guid CheckpointId, string StationId, long Sequence,
    string HeadHash, string RouteId, string ReceiptId, string PolicyHash, string SigningKeyId, string CheckpointHash,
    DateTimeOffset AcceptedAtUtc);

/// <summary>Hosts implement an idempotent route. No network endpoint is inferred by the framework.</summary>
public interface IExternalAuditAnchor
{
    ValueTask<AuditAnchorReceipt> DeliverAsync(AuditCheckpoint checkpoint, string idempotencyKey,
        CancellationToken cancellationToken);
    ValueTask<AuditAnchorReceipt?> ReadLatestAsync(string stationId, CancellationToken cancellationToken);
}
