using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime;

namespace SharpInspect.Runtime.Storage;

/// <summary>One typed, immutable terminal fact for a recovery admission.</summary>
internal sealed record CameraRecoveryTerminalRecord(
    long Position, Guid EventId, Guid AttemptId, Guid CorrelationId, Guid RuntimeEpoch,
    Guid ExpectedCycleId, Guid? NewCycleId, string LogicalRole, bool Started,
    string ReasonCode, Guid ActorPrincipalId, Guid SessionId, long AuthorizationRevision,
    DateTimeOffset RecordedAtUtc)
{
    internal static CameraRecoveryTerminalRecord Create(long position, Guid eventId,
        CommandAuditFact admission, Guid? newCycleId, bool started, string reasonCode,
        CameraRecoveryAuthorizationAudit authorization, DateTimeOffset recordedAtUtc) => new(
        position, eventId, admission.AttemptId, admission.CorrelationId, admission.RuntimeEpoch,
        authorization.ExpectedCycleId, newCycleId, authorization.ActionTargetId, started,
        reasonCode, authorization.ActorPrincipalId, authorization.SessionId,
        authorization.AuthorizationRevision, recordedAtUtc);
}

/// <summary>Canonical serialization for the schema-11 recovery terminal ledger.</summary>
internal static class CameraRecoveryStorageCodec
{
    internal const int FormatVersion = 1;
    internal const int MaximumPayloadBytes = CameraRecoveryStoreOptions.MaximumPayloadBytesHardLimit;
    internal const int MaximumEncodedPayloadChars = MaximumPayloadBytes * 2;

    internal static byte[] Encode(CameraRecoveryTerminalRecord value)
    {
        Validate(value);
        return AuditCanonical.Encode("CameraRecoveryTerminal", FormatVersion.ToString(CultureInfo.InvariantCulture),
            value.Position.ToString(CultureInfo.InvariantCulture), value.EventId.ToString("D"),
            value.AttemptId.ToString("D"), value.CorrelationId.ToString("D"),
            value.RuntimeEpoch.ToString("D"), value.ExpectedCycleId.ToString("D"),
            value.NewCycleId?.ToString("D"), value.LogicalRole, value.Started ? "1" : "0",
            value.ReasonCode, value.ActorPrincipalId.ToString("D"), value.SessionId.ToString("D"),
            value.AuthorizationRevision.ToString(CultureInfo.InvariantCulture),
            value.RecordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    internal static void Validate(CameraRecoveryTerminalRecord value)
    {
        if (value.Position < 1 || value.EventId == Guid.Empty || value.AttemptId == Guid.Empty ||
            value.CorrelationId == Guid.Empty || value.RuntimeEpoch == Guid.Empty ||
            value.ExpectedCycleId == Guid.Empty || value.ActorPrincipalId == Guid.Empty ||
            value.SessionId == Guid.Empty || value.AuthorizationRevision < 0 ||
            !StableIdentifier(value.LogicalRole, 64) || !StableIdentifier(value.ReasonCode) ||
            (value.Started && (value.NewCycleId is null || value.NewCycleId == Guid.Empty)) ||
            (!value.Started && value.NewCycleId is not null))
            throw new InvalidOperationException("CameraRecoveryTerminalInvalid");
        if (value.RecordedAtUtc == default)
            throw new InvalidOperationException("CameraRecoveryTerminalInvalid");
    }

    internal static string PayloadHash(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload));

    internal static bool StableIdentifier(string? value, int maximum = 128) =>
        value is { Length: > 0 } && value.Length <= maximum &&
        value.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or
            (>= '0' and <= '9') or '.' or '_' or '-');

    /// <summary>Reads the position field without accepting a second serialization format.</summary>
    internal static long ReadPosition(byte[] payload)
    {
        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(stream, new System.Text.UTF8Encoding(false, true));
            if (ReadInt(reader) != AuditCanonical.CanonicalizationVersion || ReadValue(reader) != "CameraRecoveryTerminal")
                throw new InvalidOperationException("CameraRecoveryPayloadInvalid");
            var count = ReadInt(reader);
            if (count != 15) throw new InvalidOperationException("CameraRecoveryPayloadInvalid");
            _ = ReadValue(reader); // format version
            var value = ReadValue(reader);
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var position) || position < 1)
                throw new InvalidOperationException("CameraRecoveryPayloadInvalid");
            return position;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) when (ex is EndOfStreamException or FormatException or
            System.Text.DecoderFallbackException)
        { throw new InvalidOperationException("CameraRecoveryPayloadInvalid", ex); }
    }

    private static int ReadInt(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length != 4) throw new InvalidOperationException("CameraRecoveryPayloadInvalid");
        return System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes);
    }

    private static string? ReadValue(BinaryReader reader)
    {
        var marker = reader.ReadByte();
        if (marker == 0) return null;
        if (marker != 1) throw new InvalidOperationException("CameraRecoveryPayloadInvalid");
        var length = ReadInt(reader);
        if (length is < 0 or > 1024) throw new InvalidOperationException("CameraRecoveryPayloadInvalid");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new InvalidOperationException("CameraRecoveryPayloadInvalid");
        return new System.Text.UTF8Encoding(false, true).GetString(bytes);
    }
}
