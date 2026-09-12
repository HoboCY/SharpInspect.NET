using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;

namespace SharpInspect.Abstractions;

public enum OutboxRouteCriticality : byte { Required = 1, BestEffort = 2 }
public enum OutboxDeliveryState : byte { Pending = 1, Failed = 2, Succeeded = 3 }
public enum OutboxFailureCategory : byte { Transient = 1, UnknownOutcome = 2, Permanent = 3 }
public enum OutboxEventKind : byte { Created = 1, AttemptStarted = 2, AttemptFailed = 3, Succeeded = 4 }

/// <summary>A versioned data or receiver contract; its hash identifies the complete contract.</summary>
public sealed class OutboxContractReference
{
    public OutboxContractReference(string id, string version, string contentHash)
    {
        Id = OutboxValidation.Identifier(id);
        Version = OutboxValidation.Identifier(version);
        ContentHash = OutboxValidation.Hash(contentHash);
    }
    public string Id { get; }
    public string Version { get; }
    public string ContentHash { get; }
}

/// <summary>
/// Frozen deployment routing. The receiving identity and verification key are immutable;
/// network addresses and credentials belong to a separately registered transport.
/// This is never part of a Recipe and does not, by itself, enable external sending.
/// </summary>
public sealed class OutboxRouteDefinition
{
    public OutboxRouteDefinition(string routeId, string version, OutboxRouteCriticality criticality,
        string destinationIdentity, OutboxContractReference payloadContract, string contentType,
        OutboxContractReference receiverContract, string receiverPublicKeyBase64,
        OutboxContractReference adapterContract, int maximumPayloadBytes)
    {
        RouteId = OutboxValidation.Identifier(routeId);
        Version = OutboxValidation.Identifier(version);
        if (!Enum.IsDefined(criticality)) throw new ArgumentOutOfRangeException(nameof(criticality));
        Criticality = criticality;
        DestinationIdentity = OutboxValidation.Identifier(destinationIdentity);
        PayloadContract = payloadContract ?? throw new ArgumentNullException(nameof(payloadContract));
        ReceiverContract = receiverContract ?? throw new ArgumentNullException(nameof(receiverContract));
        AdapterContract = adapterContract ?? throw new ArgumentNullException(nameof(adapterContract));
        if (contentType is null || contentType.Length is < 1 or > 128 ||
            contentType.Any(value => value < 32 || value > 126))
            throw new ArgumentException("OutboxContentTypeInvalid", nameof(contentType));
        ContentType = contentType;
        if (receiverPublicKeyBase64 is null || receiverPublicKeyBase64.Length is < 32 or > 2048)
            throw new ArgumentException("OutboxReceiverPublicKeyInvalid", nameof(receiverPublicKeyBase64));
        try
        {
            var key = Convert.FromBase64String(receiverPublicKeyBase64);
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(key, out var consumed);
            if (consumed != key.Length || verifier.KeySize != 256)
                throw new ArgumentException("OutboxReceiverPublicKeyInvalid", nameof(receiverPublicKeyBase64));
            ReceiverPublicKeyBase64 = Convert.ToBase64String(key);
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        { throw new ArgumentException("OutboxReceiverPublicKeyInvalid", nameof(receiverPublicKeyBase64), error); }
        if (maximumPayloadBytes is < 1 or > OutboxPayloadSnapshot.MaximumAllowedBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        MaximumPayloadBytes = maximumPayloadBytes;
        ContentHash = OutboxValidation.HashParts("sharpinspect-outbox-route-v1", RouteId, Version,
            Criticality.ToString(), DestinationIdentity, PayloadContract.Id, PayloadContract.Version,
            PayloadContract.ContentHash, ContentType, ReceiverContract.Id, ReceiverContract.Version,
            ReceiverContract.ContentHash, ReceiverPublicKeyBase64, AdapterContract.Id,
            AdapterContract.Version, AdapterContract.ContentHash,
            maximumPayloadBytes.ToString(CultureInfo.InvariantCulture));
    }
    public string RouteId { get; }
    public string Version { get; }
    public OutboxRouteCriticality Criticality { get; }
    public string DestinationIdentity { get; }
    public OutboxContractReference PayloadContract { get; }
    public string ContentType { get; }
    public OutboxContractReference ReceiverContract { get; }
    public string ReceiverPublicKeyBase64 { get; }
    public OutboxContractReference AdapterContract { get; }
    public int MaximumPayloadBytes { get; }
    public string ContentHash { get; }
}

/// <summary>Exact wire bytes. Every read returns a private copy; retries cannot mutate the original.</summary>
public sealed class OutboxPayloadSnapshot
{
    public const int MaximumAllowedBytes = 8 * 1024 * 1024;
    private readonly byte[] _bytes;
    internal OutboxPayloadSnapshot(OutboxContractReference contract, string contentType,
        ReadOnlySpan<byte> bytes)
    {
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
        if (bytes.Length is < 1 or > MaximumAllowedBytes)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        ContentType = contentType ?? throw new ArgumentNullException(nameof(contentType));
        _bytes = bytes.ToArray();
        ContentHash = Convert.ToHexString(SHA256.HashData(_bytes));
    }
    public OutboxContractReference Contract { get; }
    public string ContentType { get; }
    public int ByteLength => _bytes.Length;
    public string ContentHash { get; }
    public byte[] CopyBytes() => (byte[])_bytes.Clone();
    internal ReadOnlySpan<byte> Bytes => _bytes;
}

/// <summary>
/// One immutable route obligation, created with its Core. A preparation failure has no payload
/// and can never be sent or automatically re-encoded. It is only permitted for BestEffort.
/// </summary>
public sealed class OutboxDelivery
{
    internal OutboxDelivery(Guid deliveryId, Guid inspectionId, string coreHash,
        OutboxRouteDefinition route, OutboxPayloadSnapshot? payload, string? preparationFailure,
        DateTimeOffset createdAtUtc, int maximumAttempts)
    {
        if (deliveryId == Guid.Empty || inspectionId == Guid.Empty)
            throw new ArgumentException("OutboxDeliveryIdentityRequired");
        if (createdAtUtc == default || createdAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("OutboxTimestampInvalid", nameof(createdAtUtc));
        if (maximumAttempts is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        DeliveryId = deliveryId;
        InspectionId = inspectionId;
        CoreHash = OutboxValidation.Hash(coreHash);
        Route = route ?? throw new ArgumentNullException(nameof(route));
        if ((payload is null) == (preparationFailure is null))
            throw new ArgumentException("OutboxPreparationResultInvalid");
        if (preparationFailure is not null && route.Criticality != OutboxRouteCriticality.BestEffort)
            throw new ArgumentException("OutboxRequiredPayloadMissing");
        if (payload is not null && (payload.ByteLength > route.MaximumPayloadBytes ||
            !OutboxValidation.SameContract(payload.Contract, route.PayloadContract) ||
            payload.ContentType != route.ContentType))
            throw new ArgumentException("OutboxPayloadRouteMismatch");
        Payload = payload;
        PreparationFailure = preparationFailure is null ? null : OutboxValidation.Identifier(preparationFailure);
        CreatedAtUtc = createdAtUtc;
        MaximumAttempts = maximumAttempts;
        ContentHash = OutboxValidation.HashParts("sharpinspect-outbox-delivery-v1", deliveryId.ToString("D"),
            inspectionId.ToString("D"), CoreHash, route.ContentHash, payload?.ContentHash,
            payload?.ByteLength.ToString(CultureInfo.InvariantCulture), PreparationFailure,
            createdAtUtc.ToString("O", CultureInfo.InvariantCulture),
            maximumAttempts.ToString(CultureInfo.InvariantCulture));
    }
    public Guid DeliveryId { get; }
    public Guid InspectionId { get; }
    public string CoreHash { get; }
    public OutboxRouteDefinition Route { get; }
    public OutboxPayloadSnapshot? Payload { get; }
    public string? PreparationFailure { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public int MaximumAttempts { get; }
    public string ContentHash { get; }
}

/// <summary>Only observation data crosses the transport boundary; it confers no store authority.</summary>
public sealed class OutboxTransportObservation
{
    private readonly byte[]? _acceptance;
    public OutboxTransportObservation(ReadOnlySpan<byte> rawAcceptance)
    {
        if (rawAcceptance.Length is < 1 or > 16 * 1024)
            throw new ArgumentOutOfRangeException(nameof(rawAcceptance));
        _acceptance = rawAcceptance.ToArray();
    }
    public OutboxTransportObservation(OutboxFailureCategory failure, string reasonCode)
    {
        if (!Enum.IsDefined(failure)) throw new ArgumentOutOfRangeException(nameof(failure));
        Failure = failure;
        ReasonCode = OutboxValidation.Identifier(reasonCode);
    }
    public OutboxFailureCategory? Failure { get; }
    public string? ReasonCode { get; }
    public byte[]? CopyAcceptance() => _acceptance is null ? null : (byte[])_acceptance.Clone();
}

/// <summary>
/// Explicitly registered, trusted in-process transport. Send only the supplied frozen bytes.
/// Return receiver evidence after its durable acceptance, or a typed failure observation.
/// The Runtime checks the evidence and exclusively owns its persisted success transition.
/// </summary>
public interface IOutboxRouteTransport
{
    OutboxContractReference Contract { get; }
    ValueTask<OutboxTransportObservation> SendAsync(OutboxDelivery delivery, Guid attemptId,
        CancellationToken cancellationToken = default);
}

/// <summary>Pending includes failed obligations until succeeded or explicitly governed later.</summary>
public sealed record OutboxRouteBacklog(string RouteId, string RouteHash, OutboxRouteCriticality Criticality,
    long PendingCount, long PendingBytes, DateTimeOffset? OldestCreatedAtUtc, bool PermanentBlock, long FailedCount = 0);

public sealed class OutboxBacklogSnapshot
{
    public OutboxBacklogSnapshot(long throughAuditSequence, IReadOnlyList<OutboxRouteBacklog> routes)
    {
        if (throughAuditSequence < 0) throw new ArgumentOutOfRangeException(nameof(throughAuditSequence));
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.Count > 64 || routes.Any(value => value is null))
            throw new ArgumentException("OutboxBacklogInvalid", nameof(routes));
        ThroughAuditSequence = throughAuditSequence;
        Routes = Array.AsReadOnly(routes.ToArray());
    }
    public long ThroughAuditSequence { get; }
    public ReadOnlyCollection<OutboxRouteBacklog> Routes { get; }
}

internal static class OutboxValidation
{
    internal static string Identifier(string value) => AlgorithmConfigurationValidation.Identifier(value, nameof(value));
    internal static string Hash(string value) => AlgorithmConfigurationValidation.Hash(value, nameof(value)).ToUpperInvariant();
    internal static string HashParts(params string?[] values) => AlgorithmContractValidation.HashParts(values);
    internal static bool SameContract(OutboxContractReference left, OutboxContractReference right) =>
        left.Id == right.Id && left.Version == right.Version && left.ContentHash == right.ContentHash;
}
