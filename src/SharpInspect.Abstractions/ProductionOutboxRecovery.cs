using System.Collections.ObjectModel;

namespace SharpInspect.Abstractions;

/// <summary>
/// One governed recovery operation on an existing durable outbox obligation. The record is a
/// read model of an immutable stored fact; it grants a fresh bounded attempt allowance without
/// erasing any earlier attempt, failure, block or payload.
/// </summary>
public sealed record ProductionOutboxRecoveryRecord(Guid RecoveryId, Guid DeliveryId, int GrantedAttempts,
    string Reason, Guid? ActorPrincipalId, DateTimeOffset RecordedAtUtc, long AuditSequence, string ContentHash);

/// <summary>
/// One immutable corrective-delivery link. The corrective delivery has its own Outbox Delivery ID
/// and frozen bytes; this record only names the original item it corrects, so the original
/// payload, attempts and terminal state stay unchanged.
/// </summary>
public sealed record ProductionOutboxCorrectionRecord(Guid DeliveryId, Guid SourceDeliveryId, string Reason,
    string PayloadHash, Guid? ActorPrincipalId, DateTimeOffset RecordedAtUtc, long AuditSequence,
    string ContentHash);

/// <summary>
/// The verified governance read model of a schema-37 outbox store: the immutable recovery and
/// correction facts of one delivery, or of the whole bounded store when no delivery is selected.
/// </summary>
public sealed class ProductionOutboxGovernanceSnapshot
{
    public ProductionOutboxGovernanceSnapshot(bool available, string reason, long throughAuditSequence,
        IEnumerable<ProductionOutboxRecoveryRecord>? recoveries,
        IEnumerable<ProductionOutboxCorrectionRecord>? corrections)
    {
        if (throughAuditSequence < 0) throw new ArgumentOutOfRangeException(nameof(throughAuditSequence));
        Available = available;
        Reason = reason ?? string.Empty;
        ThroughAuditSequence = throughAuditSequence;
        Recoveries = new ReadOnlyCollection<ProductionOutboxRecoveryRecord>(
            (recoveries ?? Array.Empty<ProductionOutboxRecoveryRecord>()).ToArray());
        Corrections = new ReadOnlyCollection<ProductionOutboxCorrectionRecord>(
            (corrections ?? Array.Empty<ProductionOutboxCorrectionRecord>()).ToArray());
    }
    public bool Available { get; }
    public string Reason { get; }
    public long ThroughAuditSequence { get; }
    public ReadOnlyCollection<ProductionOutboxRecoveryRecord> Recoveries { get; }
    public ReadOnlyCollection<ProductionOutboxCorrectionRecord> Corrections { get; }
}

/// <summary>
/// The governed recovery surface for the schema-37 outbox extension. Implementations must run
/// each request through the authoritative writer transaction; merely constructing a command
/// grants no authority.
/// </summary>
public interface IProductionOutboxRecoveryService
{
    ValueTask<ProductionOutboxRecoveryResult> RecoverAsync(RecoverOutboxDeliveryCommand command,
        CancellationToken cancellationToken = default);
    ValueTask<ProductionOutboxCorrectionResult> CreateCorrectiveDeliveryAsync(
        CreateCorrectiveOutboxDeliveryCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only, bounded access to the schema-37 recovery and correction facts. Every snapshot is
/// reconstructed from the verified central audit and the immutable local rows; this capability
/// exposes no mutation surface.
/// </summary>
public interface IProductionOutboxGovernanceQuery
{
    ValueTask<ProductionOutboxGovernanceSnapshot> ReadAsync(Guid? deliveryId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Immediate typed outcome of one governed recovery submission.</summary>
public sealed record ProductionOutboxRecoveryResult(RuntimeCommandOutcome Outcome, Guid RecoveryId,
    Guid DeliveryId, int GrantedAttempts, int TotalGrantedAttempts, string Reason);

/// <summary>Immediate typed outcome of one corrective-delivery submission.</summary>
public sealed record ProductionOutboxCorrectionResult(RuntimeCommandOutcome Outcome, Guid DeliveryId,
    Guid SourceDeliveryId, string PayloadHash, string ContentHash);
