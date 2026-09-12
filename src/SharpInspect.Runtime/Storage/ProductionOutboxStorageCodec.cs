using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

/// <summary>One decoded immutable outbox delivery obligation row.</summary>
internal sealed record ProductionOutboxStoredDelivery(long Position, OutboxDelivery Delivery,
    string ContentHash, Guid CreatedEventId, long CreatedAuditSequence, string CreatedAuditHash);

/// <summary>One decoded immutable outbox lifecycle fact row.</summary>
internal sealed record ProductionOutboxStoredEvent(long Position, ProductionOutboxEvent Event,
    long AggregateSequence, OutboxEventKind Kind, Guid? AttemptId, int? AttemptNumber);

/// <summary>
/// The schema-36 outbox wire domain. Every stored fact is a canonical binding over its typed
/// columns: the row content hash is the hash of that binding and the signed central entry
/// carries exactly the same bytes, so a row and its signed metadata entry prove each other in
/// both directions without a second encoding. Route reconstruction re-derives the exact
/// deployment route (and therefore its content hash) from the persisted columns, so a success
/// receipt can never be verified against an edited route.
/// </summary>
internal static class ProductionOutboxStorageCodec
{
    /// <summary>Deterministic event identity over the substantive fact content.</summary>
    internal static Guid DeriveEventId(long position, Guid deliveryId, long aggregateSequence,
        OutboxEventKind kind, Guid? attemptId)
    {
        var canonical = AuditCanonical.Encode("ProductionOutboxEventIdV1", Number(position),
            deliveryId.ToString("D"), Number(aggregateSequence), kind.ToString(),
            attemptId?.ToString("D"));
        var digest = SHA256.HashData(canonical);
        var eventId = new Guid(digest.AsSpan(0, 16));
        if (eventId == Guid.Empty) throw new InvalidOperationException("ProductionOutboxEventIdInvalid");
        return eventId;
    }

    internal static string PayloadHash(byte[] payload) =>
        ProductionInspectionStorageCodec.PayloadHash(payload);

    internal static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    /// <summary>The canonical signed binding of one outbox fact. It never includes the audit
    /// reference, because the entry's own sequence and hash are that reference.</summary>
    internal static byte[] EncodeAuditBinding(ProductionOutboxEventBindings value) =>
        AuditCanonical.Encode("ProductionOutboxEventAuditV1", Number(value.Position),
            value.EventId.ToString("D"), value.DeliveryId.ToString("D"), value.InspectionId.ToString("D"),
            value.CoreHash, value.RouteSetHash, value.RouteId, value.RouteVersion,
            value.RouteContentHash, Number(value.AggregateSequence), value.Kind.ToString(),
            value.AttemptId?.ToString("D"), value.AttemptNumber?.ToString(CultureInfo.InvariantCulture),
            value.RuntimeEpoch?.ToString("D"),
            value.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ProductionOutboxPrincipals.PrincipalId, value.ReasonCode, value.FailureCategory?.ToString(),
            value.RetryAfterUtc?.ToString("O", CultureInfo.InvariantCulture), value.ConnectionBindingHash,
            value.AttemptBudget?.ToString(CultureInfo.InvariantCulture), value.PayloadHash, value.ReceiptId,
            value.ReceiptHash, value.AcceptedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            value.ReceiptContentHash, value.ContentHash);

    /// <summary>The event hashes its substantive fields with an empty self-hash slot. The
    /// central audit binding subsequently includes the resulting content hash.</summary>
    internal static string EventContentHash(ProductionOutboxEventBindings value) =>
        Hash(EncodeAuditBinding(value with { ContentHash = string.Empty }));

    /// <summary>The exact binding fields of one decoded persisted fact.</summary>
    internal static ProductionOutboxEventBindings Bindings(ProductionOutboxEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var receipt = value.CopyReceipt();
        return new ProductionOutboxEventBindings(value.Position, value.EventId, value.DeliveryId,
            value.InspectionId, value.CoreHash, value.RouteSetHash, value.RouteId, value.RouteVersion,
            value.RouteContentHash, value.AggregateSequence, value.Kind, value.AttemptId,
            value.AttemptNumber, value.RuntimeEpoch, value.RecordedAtUtc, value.ReasonCode,
            value.FailureCategory, value.RetryAfterUtc, value.ConnectionBindingHash, value.AttemptBudget,
            value.PayloadHash, value.ReceiptId, value.ReceiptHash, value.AcceptedAtUtc,
            receipt is null ? null : Hash(receipt), value.ContentHash);
    }

    /// <summary>
    /// Recomputes the frozen delivery content hash from the persisted route and payload
    /// columns and requires the reconstructed delivery to be exactly that obligation.
    /// </summary>
    internal static OutboxDelivery ReconstructDelivery(Guid deliveryId, Guid inspectionId, string coreHash,
        long position, string routeId, string routeVersion, string routeContentHash, int criticality,
        string destinationIdentity, string payloadContractId, string payloadContractVersion,
        string payloadContractHash, string contentType, string receiverContractId,
        string receiverContractVersion, string receiverContractHash, string receiverPublicKeyBase64,
        string adapterContractId, string adapterContractVersion, string adapterContractHash,
        int routeMaximumPayloadBytes, byte[]? payloadBytes, string? payloadContentHash,
        long? payloadByteLength, string? preparationFailure, DateTimeOffset createdAtUtc, int maximumAttempts,
        string deliveryContentHash)
    {
        if (position < 1 || maximumAttempts is < 1 or > 32)
            throw new InvalidOperationException("ProductionOutboxDeliveryRowInvalid");
        var route = new OutboxRouteDefinition(routeId, routeVersion,
            (OutboxRouteCriticality)criticality, destinationIdentity,
            new OutboxContractReference(payloadContractId, payloadContractVersion, payloadContractHash),
            contentType,
            new OutboxContractReference(receiverContractId, receiverContractVersion, receiverContractHash),
            receiverPublicKeyBase64,
            new OutboxContractReference(adapterContractId, adapterContractVersion, adapterContractHash),
            routeMaximumPayloadBytes);
        AuditChainDatabase.Require(route.Criticality == (OutboxRouteCriticality)criticality &&
            route.ContentHash == routeContentHash, "ProductionOutboxRouteBindingMismatch");
        OutboxPayloadSnapshot? snapshot = null;
        if (payloadBytes is not null)
        {
            if (payloadBytes.Length is < 1 || payloadContentHash is null || payloadByteLength is null ||
                payloadBytes.LongLength != payloadByteLength.Value)
                throw new InvalidOperationException("ProductionOutboxPayloadBindingMismatch");
            snapshot = new OutboxPayloadSnapshot(route.PayloadContract, contentType, payloadBytes);
            AuditChainDatabase.Require(snapshot.ContentHash == payloadContentHash,
                "ProductionOutboxPayloadBindingMismatch");
        }
        var delivery = new OutboxDelivery(deliveryId, inspectionId, coreHash, route, snapshot,
            preparationFailure, createdAtUtc, maximumAttempts);
        AuditChainDatabase.Require(delivery.ContentHash == deliveryContentHash,
            "ProductionOutboxDeliveryBindingMismatch");
        return delivery;
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The exact substantive fields of one stored outbox fact.</summary>
internal sealed record ProductionOutboxEventBindings(long Position, Guid EventId, Guid DeliveryId,
    Guid InspectionId, string CoreHash, string RouteSetHash, string RouteId, string RouteVersion,
    string RouteContentHash, long AggregateSequence, OutboxEventKind Kind, Guid? AttemptId,
    int? AttemptNumber, Guid? RuntimeEpoch, DateTimeOffset RecordedAtUtc, string ReasonCode,
    OutboxFailureCategory? FailureCategory, DateTimeOffset? RetryAfterUtc, string? ConnectionBindingHash,
    int? AttemptBudget, string? PayloadHash, string? ReceiptId, string? ReceiptHash,
    DateTimeOffset? AcceptedAtUtc, string? ReceiptContentHash, string ContentHash);
