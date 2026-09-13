using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Outbox;

internal sealed record OutboxVerifiedReceipt(string ReceiptId, DateTimeOffset AcceptedAtUtc, string ReceiptHash);

/// <summary>No public success token: only the physical sending owner creates a bound, one-use claim.</summary>
internal static class OutboxAcceptanceVerifier
{
    private static readonly object ClaimIssuer = new();
    internal static OutboxVerifiedReceipt VerifyReceipt(OutboxDelivery delivery, byte[] envelope)
    {
        var statement = OutboxReceiverProtocol.VerifyEnvelope(delivery.Route, envelope);
        try
        {
            using var document = JsonDocument.Parse(statement, new() { MaxDepth = 4 });
            var root = document.RootElement;
            var receiptId = root.GetProperty("receiptId").GetString()!;
            var acceptedAt = DateTimeOffset.ParseExact(root.GetProperty("acceptedAtUtc").GetString()!, "O",
                CultureInfo.InvariantCulture, DateTimeStyles.None);
            // 验签后还要重建并逐字节比对声明，避免把另一条投递或另一份内容的回执认作本次成功。
            if (!statement.AsSpan().SequenceEqual(
                    OutboxReceiverProtocol.CreateAcceptanceStatement(delivery, receiptId, acceptedAt)))
                throw new InvalidOperationException("OutboxAcceptanceBindingMismatch");
            return new(receiptId, acceptedAt, Convert.ToHexString(SHA256.HashData(envelope)));
        }
        catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or ArgumentException)
        { throw new InvalidOperationException("OutboxAcceptanceMalformed", error); }
    }

    internal static VerifiedAcceptanceClaim CreateClaim(OutboxDelivery delivery, Guid attemptId,
        Guid runtimeEpoch, byte[] rawAcceptance, OutboxAttemptAuthority authority)
    {
        if (attemptId == Guid.Empty || runtimeEpoch == Guid.Empty)
            throw new ArgumentException("OutboxAttemptIdentityInvalid");
        ArgumentNullException.ThrowIfNull(authority);
        var copy = (byte[])rawAcceptance.Clone();
        var receipt = VerifyReceipt(delivery, copy);
        // 验签本身也消耗时间；生成成功凭据前再次确认该次尝试仍有权提交结果。
        if (!authority.IsCurrent) throw new InvalidOperationException("OutboxAttemptOwnerRetired");
        return VerifiedAcceptanceClaim.Create(ClaimIssuer, delivery, attemptId, runtimeEpoch, copy, receipt, authority);
    }

    internal sealed class VerifiedAcceptanceClaim
    {
        private readonly byte[] _receipt;
        private readonly OutboxAttemptAuthority _authority;
        private int _consumed;
        internal static VerifiedAcceptanceClaim Create(object issuer, OutboxDelivery delivery, Guid attemptId,
            Guid runtimeEpoch, byte[] receipt, OutboxVerifiedReceipt verified, OutboxAttemptAuthority authority)
        {
            if (!ReferenceEquals(issuer, ClaimIssuer)) throw new InvalidOperationException("OutboxClaimIssuerInvalid");
            return new(delivery, attemptId, runtimeEpoch, receipt, verified, authority);
        }
        private VerifiedAcceptanceClaim(OutboxDelivery delivery, Guid attemptId, Guid runtimeEpoch,
            byte[] receipt, OutboxVerifiedReceipt verified, OutboxAttemptAuthority authority)
        {
            DeliveryId = delivery.DeliveryId;
            AttemptId = attemptId;
            RuntimeEpoch = runtimeEpoch;
            PayloadHash = delivery.Payload!.ContentHash;
            RouteHash = delivery.Route.ContentHash;
            ReceiptId = verified.ReceiptId;
            ReceiptHash = verified.ReceiptHash;
            AcceptedAtUtc = verified.AcceptedAtUtc;
            _receipt = (byte[])receipt.Clone();
            _authority = authority;
        }
        internal Guid DeliveryId { get; }
        internal Guid AttemptId { get; }
        internal Guid RuntimeEpoch { get; }
        internal string PayloadHash { get; }
        internal string RouteHash { get; }
        internal string ReceiptId { get; }
        internal string ReceiptHash { get; }
        internal DateTimeOffset AcceptedAtUtc { get; }
        internal byte[] CopyReceipt() => (byte[])_receipt.Clone();
        internal bool IsCurrent => Volatile.Read(ref _consumed) == 0 && _authority.IsCurrent;
        internal bool Matches(OutboxDelivery delivery, Guid runtimeEpoch, Guid attemptId) =>
            DeliveryId == delivery.DeliveryId &&
            RuntimeEpoch == runtimeEpoch && AttemptId == attemptId && RouteHash == delivery.Route.ContentHash &&
            PayloadHash == delivery.Payload?.ContentHash;
        internal bool TryConsume() => _authority.TryConsume(ref _consumed);
    }
}
