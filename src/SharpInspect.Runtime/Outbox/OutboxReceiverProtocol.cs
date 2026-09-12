using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Outbox;

/// <summary>
/// Version-one receiver wire contract. A receiver commits the delivery identity, exact payload
/// hash and business effect atomically before signing acceptance. Its implementation must retain
/// deduplication for the delivery lifetime. Signatures authenticate that assertion and identity;
/// they do not independently prove the receiver's storage or hardware durability.
/// </summary>
public static class OutboxReceiverProtocol
{
    public const int MaximumEvidenceBytes = 16 * 1024;
    public const string ContentType = "application/json; charset=utf-8";
    public static OutboxContractReference ReceiverContract { get; } = Contract(
        "SharpInspect.Outbox.DurableAcceptance", "1",
        "ECDSA-P256-SHA256-P1363;canonical-json-v1;destination-wide-DeliveryId;" +
        "same-id-same-hash-same-receipt;same-id-different-hash-permanent-conflict;" +
        "commit-dedup-and-effect-before-ack;retain-dedup-for-delivery-lifetime");
    public static OutboxContractReference CorePayloadContract { get; } = Contract(
        "SharpInspect.Outbox.CoreInspection", "1",
        "utf8-json-v1;ordered:contract,version,deliveryId,inspectionId,stationId,coreHash," +
        "routeId,routeVersion,routeHash,destinationIdentity,acceptedAtUtc,committedAtUtc," +
        "controllerEpoch,cycleSequence,executionStatus,decision,reasonCode,partIdentity," +
        "recipeActivationHash,tracePolicyHash,structuredResultHash,structuredResultJson,plcPayloadHash;" +
        "Guid-D;utc-O;invariant-integers;structuredResultJson-is-frozen-json-string;no-artifact-bytes-or-urls");

    /// <summary>Bytes a compatible receiver signs for its offline, deployment-pinned capability.</summary>
    public static byte[] CreateCapabilityStatement(OutboxRouteDefinition route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return Encode(writer =>
        {
            writer.WriteString("kind", "capability");
            WriteRoute(writer, route);
            writer.WriteString("deduplicationScope", "destination-delivery-id");
            writer.WriteString("duplicate", "same-hash-same-receipt");
            writer.WriteString("conflict", "permanent-rejection");
            writer.WriteString("acceptance", "after-durable-dedup-and-effect-commit");
            writer.WriteString("retention", "delivery-lifetime");
        });
    }

    /// <summary>
    /// Bytes signed only after the receiving transaction commits. Duplicate requests with the
    /// same identity and hash must return the original receipt identity and acceptance time.
    /// </summary>
    public static byte[] CreateAcceptanceStatement(OutboxDelivery delivery, string receiptId,
        DateTimeOffset acceptedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var payload = delivery.Payload ?? throw new ArgumentException("OutboxPayloadMissing", nameof(delivery));
        receiptId = OutboxValidation.Identifier(receiptId);
        if (acceptedAtUtc == default || acceptedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("OutboxAcceptanceTimestampInvalid", nameof(acceptedAtUtc));
        return Encode(writer =>
        {
            writer.WriteString("kind", "acceptance");
            WriteRoute(writer, delivery.Route);
            writer.WriteString("deliveryId", delivery.DeliveryId.ToString("D"));
            writer.WriteString("inspectionId", delivery.InspectionId.ToString("D"));
            writer.WriteString("payloadHash", payload.ContentHash);
            writer.WriteNumber("payloadBytes", payload.ByteLength);
            writer.WriteString("receiptId", receiptId);
            writer.WriteString("acceptedAtUtc", acceptedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        });
    }

    /// <summary>Pack a canonical statement and its 64-byte P1363 ECDSA-P256/SHA-256 signature.</summary>
    public static byte[] CreateSignedEnvelope(ReadOnlySpan<byte> statement, ReadOnlySpan<byte> signature)
    {
        if (statement.Length is < 1 or > 8 * 1024 || signature.Length != 64)
            throw new ArgumentException("OutboxSignedEnvelopeInvalid");
        var body = Convert.ToBase64String(statement);
        var proof = Convert.ToBase64String(signature);
        var result = Encode(writer =>
        {
            writer.WriteNumber("version", 1);
            writer.WriteString("statement", body);
            writer.WriteString("signature", proof);
        });
        if (result.Length > MaximumEvidenceBytes) throw new ArgumentException("OutboxSignedEnvelopeTooLarge");
        return result;
    }

    internal static byte[] VerifyEnvelope(OutboxRouteDefinition route, ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length is < 1 or > MaximumEvidenceBytes)
            throw new InvalidOperationException("OutboxAcceptanceSizeInvalid");
        try
        {
            using var document = JsonDocument.Parse(envelope.ToArray(), new() { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != 1)
                throw new InvalidOperationException("OutboxAcceptanceVersionUnsupported");
            var statement = Convert.FromBase64String(root.GetProperty("statement").GetString()!);
            var signature = Convert.FromBase64String(root.GetProperty("signature").GetString()!);
            if (!CreateSignedEnvelope(statement, signature).AsSpan().SequenceEqual(envelope))
                throw new InvalidOperationException("OutboxAcceptanceEnvelopeNonCanonical");
            using var verifier = ECDsa.Create();
            var key = Convert.FromBase64String(route.ReceiverPublicKeyBase64);
            verifier.ImportSubjectPublicKeyInfo(key, out var consumed);
            if (consumed != key.Length || !verifier.VerifyData(statement, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new InvalidOperationException("OutboxAcceptanceSignatureInvalid");
            return statement;
        }
        catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or
                                         ArgumentException or CryptographicException)
        { throw new InvalidOperationException("OutboxAcceptanceMalformed", error); }
    }

    internal static void VerifyCapability(OutboxRouteDefinition route, ReadOnlySpan<byte> envelope)
    {
        if (!OutboxValidation.SameContract(route.ReceiverContract, ReceiverContract))
            throw new InvalidOperationException("OutboxReceiverContractUnsupported");
        var statement = VerifyEnvelope(route, envelope);
        if (!statement.AsSpan().SequenceEqual(CreateCapabilityStatement(route)))
            throw new InvalidOperationException("OutboxReceiverCapabilityMismatch");
    }

    private static void WriteRoute(Utf8JsonWriter writer, OutboxRouteDefinition route)
    {
        writer.WriteString("destinationIdentity", route.DestinationIdentity);
        writer.WriteString("routeId", route.RouteId);
        writer.WriteString("routeVersion", route.Version);
        writer.WriteString("routeHash", route.ContentHash);
        writer.WriteString("receiverContract", route.ReceiverContract.Id);
        writer.WriteString("receiverVersion", route.ReceiverContract.Version);
        writer.WriteString("receiverHash", route.ReceiverContract.ContentHash);
        writer.WriteString("payloadContract", route.PayloadContract.Id);
        writer.WriteString("payloadVersion", route.PayloadContract.Version);
        writer.WriteString("payloadContractHash", route.PayloadContract.ContentHash);
        writer.WriteString("contentType", route.ContentType);
    }

    internal static byte[] Encode(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static OutboxContractReference Contract(string id, string version, string specification) =>
        new(id, version, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(specification))));
}
