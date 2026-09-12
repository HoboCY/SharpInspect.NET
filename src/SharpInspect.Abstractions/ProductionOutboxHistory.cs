using System.Collections.ObjectModel;

namespace SharpInspect.Abstractions;

/// <summary>
/// The single fixed principal attributed to every outbox lifecycle fact. Callers can never
/// supply a principal, so an external process can neither impersonate the sender nor claim a
/// delivery outcome on behalf of another actor.
/// </summary>
public static class ProductionOutboxPrincipals
{
    public const string PrincipalId = "SharpInspect.Outbox";
}

/// <summary>
/// One immutable outbox lifecycle fact: the frozen obligation binding, the attempt binding,
/// the recorded outcome and, for a success, the raw authenticated receiver receipt. Every
/// value is copied on construction and every read of the receipt bytes returns a private copy,
/// so a projection can never be edited into a different fact.
/// </summary>
public sealed class ProductionOutboxEvent
{
    private readonly byte[]? _receipt;

    internal ProductionOutboxEvent(long position, Guid eventId, Guid deliveryId, Guid inspectionId,
        string coreHash, string routeSetHash, string routeId, string routeVersion, string routeContentHash,
        long aggregateSequence, OutboxEventKind kind, Guid? attemptId, int? attemptNumber, Guid? runtimeEpoch,
        DateTimeOffset recordedAtUtc, string reasonCode, OutboxFailureCategory? failureCategory,
        DateTimeOffset? retryAfterUtc, string? connectionBindingHash, int? attemptBudget,
        string? receiptId, string? receiptHash, DateTimeOffset? acceptedAtUtc, ReadOnlySpan<byte> receipt,
        string? payloadHash, string contentHash, long auditSequence, string auditHash)
    {
        if (position < 1 || eventId == Guid.Empty || deliveryId == Guid.Empty || inspectionId == Guid.Empty)
            throw new ArgumentException("ProductionOutboxEventIdentityInvalid", nameof(position));
        if (aggregateSequence < 1 || !Enum.IsDefined(kind))
            throw new ArgumentException("ProductionOutboxEventIdentityInvalid", nameof(aggregateSequence));
        if (recordedAtUtc == default || recordedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("ProductionOutboxEventTimestampInvalid", nameof(recordedAtUtc));
        if (auditSequence < 1) throw new ArgumentOutOfRangeException(nameof(auditSequence));
        CoreHash = OutboxValidation.Hash(coreHash);
        RouteSetHash = OutboxValidation.Hash(routeSetHash);
        RouteId = OutboxValidation.Identifier(routeId);
        RouteVersion = OutboxValidation.Identifier(routeVersion);
        RouteContentHash = OutboxValidation.Hash(routeContentHash);
        ReasonCode = OutboxValidation.Identifier(reasonCode);
        PayloadHash = payloadHash is null ? null : OutboxValidation.Hash(payloadHash);
        ContentHash = OutboxValidation.Hash(contentHash);
        AuditSequence = auditSequence;
        AuditHash = OutboxValidation.Hash(auditHash);
        if (failureCategory is { } category && !Enum.IsDefined(category))
            throw new ArgumentOutOfRangeException(nameof(failureCategory));
        if ((receiptId is null) != (receiptHash is null) || (receiptId is null) != (acceptedAtUtc is null))
            throw new ArgumentException("ProductionOutboxReceiptBindingInvalid", nameof(receiptId));
        if (kind == OutboxEventKind.Succeeded &&
            (receiptId is null || receipt.Length is < 1 or > 16 * 1024))
            throw new ArgumentException("ProductionOutboxReceiptBindingInvalid", nameof(receiptId));
        if (kind != OutboxEventKind.Succeeded && receipt.Length != 0)
            throw new ArgumentException("ProductionOutboxReceiptBindingInvalid", nameof(receipt));
        if ((attemptId is null) != (kind == OutboxEventKind.Created))
            throw new ArgumentException("ProductionOutboxAttemptBindingInvalid", nameof(attemptId));
        if (kind == OutboxEventKind.Created && attemptNumber is not null)
            throw new ArgumentException("ProductionOutboxAttemptBindingInvalid", nameof(attemptNumber));
        Position = position;
        EventId = eventId;
        DeliveryId = deliveryId;
        InspectionId = inspectionId;
        AggregateSequence = aggregateSequence;
        Kind = kind;
        AttemptId = attemptId;
        AttemptNumber = attemptNumber;
        RuntimeEpoch = runtimeEpoch;
        RecordedAtUtc = recordedAtUtc;
        FailureCategory = failureCategory;
        RetryAfterUtc = retryAfterUtc;
        ConnectionBindingHash = connectionBindingHash is null ? null :
            OutboxValidation.Hash(connectionBindingHash);
        AttemptBudget = attemptBudget;
        ReceiptId = receiptId is null ? null : OutboxValidation.Identifier(receiptId);
        ReceiptHash = receiptHash is null ? null : OutboxValidation.Hash(receiptHash);
        AcceptedAtUtc = acceptedAtUtc;
        _receipt = kind == OutboxEventKind.Succeeded ? receipt.ToArray() : null;
    }

    public long Position { get; }
    public Guid EventId { get; }
    public Guid DeliveryId { get; }
    public Guid InspectionId { get; }
    public string CoreHash { get; }
    public string RouteSetHash { get; }
    public string RouteId { get; }
    public string RouteVersion { get; }
    public string RouteContentHash { get; }
    public long AggregateSequence { get; }
    public OutboxEventKind Kind { get; }
    public Guid? AttemptId { get; }
    public int? AttemptNumber { get; }
    public Guid? RuntimeEpoch { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public string ReasonCode { get; }
    public OutboxFailureCategory? FailureCategory { get; }
    public DateTimeOffset? RetryAfterUtc { get; }
    /// <summary>The exact transport connection binding the attempt was started with.</summary>
    public string? ConnectionBindingHash { get; }
    /// <summary>The persisted attempt budget of the delivery at the time of this fact.</summary>
    public int? AttemptBudget { get; }
    /// <summary>The receiver receipt identity recorded by the authenticated acceptance.</summary>
    public string? ReceiptId { get; }
    public string? ReceiptHash { get; }
    public DateTimeOffset? AcceptedAtUtc { get; }
    /// <summary>The fixed attribution of every outbox fact.</summary>
    public string PrincipalId => ProductionOutboxPrincipals.PrincipalId;
    /// <summary>The frozen delivery payload hash, or null for a preparation failure.</summary>
    public string? PayloadHash { get; }
    public string ContentHash { get; }
    public long AuditSequence { get; }
    public string AuditHash { get; }

    /// <summary>Every read returns a private copy; the stored receipt can never be mutated.</summary>
    public byte[]? CopyReceipt() => _receipt is null ? null : (byte[])_receipt.Clone();
}

/// <summary>
/// One verified pending (or failed) obligation: the frozen delivery plus the exact persisted
/// projection. Reading this value grants no send authority; only the Runtime worker can act.
/// </summary>
public sealed class OutboxPendingItem
{
    internal OutboxPendingItem(OutboxDelivery delivery, OutboxDeliveryState state, int attemptCount,
        Guid? activeAttemptId, Guid? activeRuntimeEpoch, bool retryEligible, DateTimeOffset? retryAfterUtc,
        bool permanentBlock, string? lastFailureReasonCode, long position, string contentHash)
    {
        Delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (attemptCount < 0) throw new ArgumentOutOfRangeException(nameof(attemptCount));
        if (retryAfterUtc is { } value && value.Offset != TimeSpan.Zero)
            throw new ArgumentException("ProductionOutboxTimestampInvalid", nameof(retryAfterUtc));
        if (position < 1) throw new ArgumentOutOfRangeException(nameof(position));
        if (permanentBlock && retryEligible)
            throw new ArgumentException("ProductionOutboxPermanentRetryConflict", nameof(permanentBlock));
        State = state;
        AttemptCount = attemptCount;
        ActiveAttemptId = activeAttemptId;
        ActiveRuntimeEpoch = activeRuntimeEpoch;
        RetryEligible = retryEligible;
        RetryAfterUtc = retryAfterUtc;
        PermanentBlock = permanentBlock;
        LastFailureReasonCode = lastFailureReasonCode is null ? null :
            OutboxValidation.Identifier(lastFailureReasonCode);
        Position = position;
        ContentHash = OutboxValidation.Hash(contentHash);
    }

    public OutboxDelivery Delivery { get; }
    public OutboxDeliveryState State { get; }
    public int AttemptCount { get; }
    /// <summary>The one active attempt, if any; it must be resolved before a new attempt.</summary>
    public Guid? ActiveAttemptId { get; }
    public Guid? ActiveRuntimeEpoch { get; }
    /// <summary>False once the attempt budget is exhausted or a permanent failure is recorded.</summary>
    public bool RetryEligible { get; }
    public DateTimeOffset? RetryAfterUtc { get; }
    /// <summary>True when the obligation is permanently blocked and no further send may occur.</summary>
    public bool PermanentBlock { get; }
    public string? LastFailureReasonCode { get; }
    /// <summary>The immutable delivery row position used as the page cursor.</summary>
    public long Position { get; }
    public string ContentHash { get; }
}

public sealed record OutboxPendingPage(bool Available, string ReasonCode,
    IReadOnlyList<OutboxPendingItem> Items, bool HasMore, long NextPosition,
    OutboxBacklogSnapshot? Backlog);

public sealed record OutboxHistoryPage(bool Available, string ReasonCode,
    IReadOnlyList<ProductionOutboxEvent> Events, bool HasMore, long NextPosition);

/// <summary>
/// Read-only, bounded access to the schema-36 production outbox. Every page is reconstructed
/// inside one frozen read-only snapshot after a full central-audit verification, so no caller
/// can obtain a mutation surface or an unverified backlog watermark.
/// </summary>
public interface IProductionOutboxQuery
{
    ValueTask<OutboxPendingPage> ReadPendingAsync(long afterPosition = 0, int? pageSize = null,
        CancellationToken cancellationToken = default);
    ValueTask<OutboxBacklogSnapshot> ReadBacklogAsync(CancellationToken cancellationToken = default);
    ValueTask<OutboxHistoryPage> ReadHistoryAsync(Guid? inspectionId = null, Guid? deliveryId = null,
        long afterPosition = 0, int? pageSize = null, CancellationToken cancellationToken = default);
}
