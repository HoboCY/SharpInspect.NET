using System.Globalization;
using System.Security.Cryptography;

namespace SharpInspect.Abstractions;

/// <summary>Authorizes exactly N new attempts on an exact observed obligation without changing its bytes.</summary>
public sealed record RecoverOutboxDeliveryCommand : RuntimeCommand
{
    public RecoverOutboxDeliveryCommand(Guid correlationId, CommandInvocation invocation, Guid deliveryId,
        string expectedDeliveryContentHash, string expectedStateRevisionHash, int requestedAttempts, string reason)
        : base(correlationId, invocation)
    {
        RecipeActivationValidation.RequiredGuid(correlationId, nameof(correlationId));
        ArgumentNullException.ThrowIfNull(invocation);
        DeliveryId = RecipeActivationValidation.RequiredGuid(deliveryId, nameof(deliveryId));
        ExpectedDeliveryContentHash = OutboxValidation.Hash(expectedDeliveryContentHash);
        ExpectedStateRevisionHash = OutboxValidation.Hash(expectedStateRevisionHash);
        if (requestedAttempts is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(requestedAttempts));
        RequestedAttempts = requestedAttempts;
        Reason = OutboxOperationValidation.Reason(reason);
        AuthorizationTarget = Target(DeliveryId, ExpectedDeliveryContentHash, ExpectedStateRevisionHash, RequestedAttempts, Reason);
    }
    public Guid DeliveryId { get; }
    public string ExpectedDeliveryContentHash { get; }
    public string ExpectedStateRevisionHash { get; }
    public int RequestedAttempts { get; }
    public string Reason { get; }
    public string AuthorizationTarget { get; }
    internal static string Target(Guid id, string deliveryHash, string stateHash, int attempts, string reason) =>
        OutboxValidation.HashParts("sharpinspect-outbox-recovery-command-v1", id.ToString("D"), deliveryHash,
            stateHash, attempts.ToString(CultureInfo.InvariantCulture), reason);
}

/// <summary>Freezes explicit final bytes for a new linked delivery; the source obligation remains immutable.</summary>
public sealed record CreateCorrectiveOutboxDeliveryCommand : RuntimeCommand
{
    private readonly byte[] _payload;
    public CreateCorrectiveOutboxDeliveryCommand(Guid correlationId, CommandInvocation invocation, Guid sourceDeliveryId,
        string expectedSourceDeliveryContentHash, string expectedStateRevisionHash, string contentType,
        ReadOnlyMemory<byte> payloadBytes, string reason) : base(correlationId, invocation)
    {
        RecipeActivationValidation.RequiredGuid(correlationId, nameof(correlationId));
        ArgumentNullException.ThrowIfNull(invocation);
        SourceDeliveryId = RecipeActivationValidation.RequiredGuid(sourceDeliveryId, nameof(sourceDeliveryId));
        ExpectedSourceDeliveryContentHash = OutboxValidation.Hash(expectedSourceDeliveryContentHash);
        ExpectedStateRevisionHash = OutboxValidation.Hash(expectedStateRevisionHash);
        ContentType = AlgorithmContractValidation.BoundedText(contentType, nameof(contentType), 128);
        if (string.IsNullOrWhiteSpace(ContentType)) throw new ArgumentException("OutboxCorrectionContentTypeRequired");
        if (payloadBytes.Length is < 1 or > 8 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(payloadBytes));
        Reason = OutboxOperationValidation.Reason(reason);
        _payload = payloadBytes.ToArray();
        PayloadHash = Convert.ToHexString(SHA256.HashData(_payload));
        AuthorizationTarget = Target(SourceDeliveryId, ExpectedSourceDeliveryContentHash,
            ExpectedStateRevisionHash, ContentType, PayloadHash, Reason);
    }
    public Guid SourceDeliveryId { get; }
    public string ExpectedSourceDeliveryContentHash { get; }
    public string ExpectedStateRevisionHash { get; }
    public string ContentType { get; }
    public string PayloadHash { get; }
    public string Reason { get; }
    public string AuthorizationTarget { get; }
    public byte[] CopyPayload() => (byte[])_payload.Clone();
    internal ReadOnlyMemory<byte> PayloadBytes => _payload;
    internal static string Target(Guid id, string deliveryHash, string stateHash, string contentType,
        string payloadHash, string reason) => OutboxValidation.HashParts("sharpinspect-outbox-correction-command-v1",
        id.ToString("D"), deliveryHash, stateHash, contentType, payloadHash, reason);
    public override string ToString() => nameof(CreateCorrectiveOutboxDeliveryCommand);
}

internal static class OutboxOperationValidation
{
    internal static string Reason(string reason)
    {
        var result = AlgorithmContractValidation.BoundedText(reason, nameof(reason), 256);
        if (string.IsNullOrWhiteSpace(result)) throw new ArgumentException("OutboxOperationReasonRequired");
        return result;
    }
}
