using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// The schema-35 finalization ledger has its own strict binary wire domain. The row payload is
/// the exact reconstructable event envelope; the central audit entry stores a small canonical
/// binding of the same substantive fields (without the audit reference) so local facts and the
/// signed chain can be proved against each other in both directions.
/// </summary>
internal static class ProductionImageFinalizationStorageCodec
{
    internal const int MaximumEnvelopeBytes =
        ProductionImageFinalizationStoreOptions.MaximumPayloadBytesHardLimit;
    private static readonly byte[] EnvelopeMagic = Encoding.ASCII.GetBytes("SI-IMF-01\n");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>The fixed final file name rule: the manifest identity plus the PNG suffix.</summary>
    internal static string StableFinalFileName(Guid manifestId) => manifestId.ToString("N") + ".png";

    /// <summary>The unique temporary file name of one attempt inside the final root.</summary>
    internal static string UniqueTemporaryFileName(Guid manifestId, Guid attemptId) =>
        manifestId.ToString("N") + "." + attemptId.ToString("N") + ".tmp";

    /// <summary>
    /// Deterministic event identity: the first 16 bytes of the SHA-256 over the canonical
    /// substantive content. An identical re-delivery therefore collides on the same identity
    /// and is proved idempotent instead of inserting a second fact.
    /// </summary>
    internal static Guid DeriveEventId(long position, Guid workId, Guid manifestId, Guid inspectionId,
        string workContentHash, string manifestContentHash, long aggregateSequence, Guid? attemptId,
        int? attemptNumber, Guid runtimeEpoch, DateTimeOffset recordedAtUtc,
        ProductionImageFinalizationKind kind, string reasonCode,
        ProductionImageAttemptDescriptor? attempt, ProductionImageFailureDescriptor? failure,
        ProductionImageSuccessDescriptor? success)
    {
        var canonical = AuditCanonical.Encode("ImageFinalizationEventId",
            Number(position), workId.ToString("D"), manifestId.ToString("D"), inspectionId.ToString("D"),
            workContentHash.ToUpperInvariant(), manifestContentHash.ToUpperInvariant(), Number(aggregateSequence),
            attemptId?.ToString("D"), attemptNumber?.ToString(CultureInfo.InvariantCulture),
            runtimeEpoch.ToString("D"), recordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            SystemPrincipalId.Runtime, kind.ToString(), reasonCode, attempt?.ContentHash,
            failure?.ContentHash, success?.ContentHash);
        var digest = SHA256.HashData(canonical);
        var eventId = new Guid(digest.AsSpan(0, 16));
        if (eventId == Guid.Empty) throw new InvalidOperationException("ImageFinalizationEventIdInvalid");
        return eventId;
    }

    internal static byte[] EncodeEvent(ProductionImageFinalizationEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.AuditHash is null || value.AuditSequence < 1)
            throw new InvalidOperationException("ImageFinalizationAuditReferenceInvalid");
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true))
        {
            writer.Write(EnvelopeMagic);
            writer.Write(value.Position);
            WriteGuid(writer, value.EventId);
            WriteGuid(writer, value.WorkId);
            WriteGuid(writer, value.ManifestId);
            WriteGuid(writer, value.InspectionId);
            WriteHash(writer, value.WorkContentHash);
            WriteHash(writer, value.ManifestContentHash);
            writer.Write(value.AggregateSequence);
            WriteNullableGuid(writer, value.AttemptId);
            WriteNullableInt32(writer, value.AttemptNumber);
            WriteGuid(writer, value.RuntimeEpoch);
            writer.Write(value.RecordedAtUtc.UtcTicks);
            writer.Write((byte)value.Kind);
            WriteString(writer, value.ReasonCode, 256);
            writer.Write(value.SystemPrincipalId);
            if (value.Attempt is { } attempt)
            {
                WriteGuid(writer, attempt.AttemptId);
                writer.Write(attempt.AttemptNumber);
                writer.Write(attempt.AttemptLimit);
                writer.Write(attempt.RemainingAttempts);
                WriteHash(writer, attempt.FinalRootBindingHash);
                WriteString(writer, attempt.FinalFileName, 128);
                WriteString(writer, attempt.TemporaryFileName, 128);
            }
            if (value.Failure is { } failure)
            {
                writer.Write((byte)failure.Category);
                WriteNullableUtc(writer, failure.RetryAfterUtc);
            }
            if (value.Success is { } success)
            {
                WriteHash(writer, success.FinalRootBindingHash);
                WriteString(writer, success.FinalFileName, 128);
                writer.Write(success.EncodedByteLength);
                writer.Write(success.Width);
                writer.Write(success.Height);
                writer.Write((byte)success.PixelFormat);
                WriteNullableInt32(writer, success.ValidBits);
                WriteString(writer, success.CanonicalHashScheme, 128);
                writer.Write(success.CanonicalHashSchemeVersion);
                WriteHash(writer, success.CanonicalPixelHash);
            }
            writer.Write(value.AuditSequence);
            WriteString(writer, value.AuditHash, 64);
            WriteHash(writer, value.ContentHash);
        }
        var payload = stream.ToArray();
        if (payload.Length is < 1 or > MaximumEnvelopeBytes)
            throw new InvalidOperationException("ImageFinalizationPayloadCapacityInvalid");
        return payload;
    }

    internal static ProductionImageFinalizationEvent DecodeEvent(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length is < 1 or > MaximumEnvelopeBytes)
            throw Corrupt("ImageFinalizationPayloadCapacityInvalid");
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        var magic = reader.ReadBytes(EnvelopeMagic.Length);
        if (magic.Length != EnvelopeMagic.Length || !magic.AsSpan().SequenceEqual(EnvelopeMagic))
            throw Corrupt("ImageFinalizationEnvelopeInvalid");
        var position = reader.ReadInt64();
        var eventId = ReadGuid(reader);
        var workId = ReadGuid(reader);
        var manifestId = ReadGuid(reader);
        var inspectionId = ReadGuid(reader);
        var workHash = ReadHash(reader);
        var manifestHash = ReadHash(reader);
        var aggregateSequence = reader.ReadInt64();
        var attemptId = ReadNullableGuid(reader);
        var attemptNumber = ReadNullableInt32(reader);
        var runtimeEpoch = ReadGuid(reader);
        var recordedAtTicks = reader.ReadInt64();
        var kind = ReadEnum<ProductionImageFinalizationKind>(reader.ReadByte(),
            "ImageFinalizationKindInvalid");
        var reason = ReadString(reader, 256) ?? throw Corrupt("ImageFinalizationEventInvalid");
        if (reader.ReadString() != SystemPrincipalId.Runtime)
            throw Corrupt("ImageFinalizationPrincipalInvalid");
        ProductionImageAttemptDescriptor? attempt = null;
        if (kind is ProductionImageFinalizationKind.AttemptStarted or
            ProductionImageFinalizationKind.AttemptFailed or ProductionImageFinalizationKind.Succeeded)
            attempt = new ProductionImageAttemptDescriptor(ReadGuid(reader), reader.ReadInt32(),
                reader.ReadInt32(), reader.ReadInt32(), ReadHash(reader), ReadString(reader, 128) ??
                throw Corrupt("ImageFinalizationEventInvalid"),
                ReadString(reader, 128) ?? throw Corrupt("ImageFinalizationEventInvalid"));
        ProductionImageFailureDescriptor? failure = null;
        if (kind == ProductionImageFinalizationKind.AttemptFailed)
            failure = new ProductionImageFailureDescriptor(ReadEnum<ProductionImageFailureCategory>(
                reader.ReadByte(), "ImageFinalizationFailureCategoryInvalid"),
                ReadNullableUtc(reader));
        ProductionImageSuccessDescriptor? success = null;
        if (kind == ProductionImageFinalizationKind.Succeeded)
            success = new ProductionImageSuccessDescriptor(ReadHash(reader),
                ReadString(reader, 128) ?? throw Corrupt("ImageFinalizationEventInvalid"),
                reader.ReadInt64(), reader.ReadInt32(), reader.ReadInt32(),
                ReadEnum<VisionPixelFormat>(reader.ReadByte(), "ImageFinalizationPixelFormatInvalid"),
                ReadNullableInt32(reader), ReadString(reader, 128) ??
                throw Corrupt("ImageFinalizationEventInvalid"), reader.ReadInt32(), ReadHash(reader));
        var auditSequence = reader.ReadInt64();
        var auditHashText = ReadString(reader, 64);
        var contentHash = ReadHash(reader);
        if (stream.Position != stream.Length) throw Corrupt("ImageFinalizationEnvelopeTrailingBytes");
        var auditHash = auditSequence == 0 ? null : auditHashText is { Length: 64 }
            ? auditHashText
            : throw Corrupt("ImageFinalizationAuditReferenceInvalid");
        var value = new ProductionImageFinalizationEvent(position, eventId, workId, manifestId,
            inspectionId, workHash, manifestHash, aggregateSequence, attemptId, attemptNumber,
            runtimeEpoch, new DateTimeOffset(recordedAtTicks, TimeSpan.Zero), kind, reason,
            attempt, failure, success, auditSequence, auditHash, contentHash);
        if (deriveCheck(value) is false) throw Corrupt("ImageFinalizationEventIdentityMismatch");
        return value;

        static bool deriveCheck(ProductionImageFinalizationEvent value) =>
            DeriveEventId(value.Position, value.WorkId, value.ManifestId, value.InspectionId,
                value.WorkContentHash, value.ManifestContentHash, value.AggregateSequence,
                value.AttemptId, value.AttemptNumber, value.RuntimeEpoch, value.RecordedAtUtc,
                value.Kind, value.ReasonCode, value.Attempt, value.Failure, value.Success) ==
            value.EventId;
    }

    /// <summary>
    /// The central-audit metadata binding: the canonical substantive content plus the content
    /// hash, registered under the fixed principal. The audit reference is deliberately excluded
    /// because the entry's own sequence and hash are the reference.
    /// </summary>
    internal static byte[] EncodeAuditPayload(ProductionImageFinalizationEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return AuditCanonical.Encode("ImageFinalizationEventAuditV1", Number(value.Position),
            value.EventId.ToString("D"), value.WorkId.ToString("D"), value.ManifestId.ToString("D"),
            value.InspectionId.ToString("D"), value.WorkContentHash, value.ManifestContentHash,
            Number(value.AggregateSequence), value.AttemptId?.ToString("D"),
            value.AttemptNumber?.ToString(CultureInfo.InvariantCulture),
            value.RuntimeEpoch.ToString("D"),
            value.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture), value.SystemPrincipalId,
            value.Kind.ToString(), value.ReasonCode, value.Attempt?.ContentHash,
            value.Failure?.ContentHash, value.Success?.ContentHash, value.ContentHash);
    }

    internal static string PayloadHash(byte[] payload) =>
        ProductionInspectionStorageCodec.PayloadHash(payload);

    private static void WriteGuid(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray());

    private static void WriteNullableGuid(BinaryWriter writer, Guid? value)
    {
        writer.Write(value is not null);
        if (value is { } actual) WriteGuid(writer, actual);
    }

    private static void WriteNullableInt32(BinaryWriter writer, int? value)
    {
        writer.Write(value is not null);
        if (value is { } actual) writer.Write(actual);
    }

    private static void WriteNullableUtc(BinaryWriter writer, DateTimeOffset? value)
    {
        writer.Write(value is not null);
        if (value is { } actual) writer.Write(actual.UtcTicks);
    }

    private static void WriteHash(BinaryWriter writer, string value)
    {
        byte[] bytes;
        try { bytes = Convert.FromHexString(value); }
        catch (FormatException exception) { throw Corrupt("ImageFinalizationHashInvalid", exception); }
        if (bytes.Length != 32) throw Corrupt("ImageFinalizationHashInvalid");
        writer.Write(bytes);
    }

    private static void WriteString(BinaryWriter writer, string value, int limit)
    {
        var bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length is < 1 || bytes.Length > limit) throw Corrupt("ImageFinalizationTextInvalid");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static Guid ReadGuid(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16);
        if (bytes.Length != 16 || new Guid(bytes) == Guid.Empty)
            throw Corrupt("ImageFinalizationEventIdentityInvalid");
        return new Guid(bytes);
    }

    private static Guid? ReadNullableGuid(BinaryReader reader) =>
        reader.ReadBoolean() ? ReadGuid(reader) : null;

    private static int? ReadNullableInt32(BinaryReader reader) =>
        reader.ReadBoolean() ? reader.ReadInt32() : null;

    private static DateTimeOffset? ReadNullableUtc(BinaryReader reader) =>
        reader.ReadBoolean() ? new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero) : null;

    private static string ReadHash(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(32);
        if (bytes.Length != 32) throw Corrupt("ImageFinalizationHashInvalid");
        return Convert.ToHexString(bytes);
    }

    private static string? ReadString(BinaryReader reader, int limit)
    {
        var length = reader.ReadInt32();
        if (length is < 1 || length > limit) throw Corrupt("ImageFinalizationTextInvalid");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw Corrupt("ImageFinalizationTextInvalid");
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException exception) { throw Corrupt("ImageFinalizationTextInvalid", exception); }
    }

    private static T ReadEnum<T>(byte value, string reason) where T : struct, Enum
    {
        // Wire values are bytes; VisionPixelFormat's CLR underlying type is Int32.
        // Enum.IsDefined(Type, boxedByte) rejects that valid enum before validation.
        var decoded = (T)Enum.ToObject(typeof(T), value);
        return Enum.IsDefined(decoded) ? decoded : throw Corrupt(reason);
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static InvalidOperationException Corrupt(string reason) => new(reason);

    private static InvalidOperationException Corrupt(string reason, Exception inner) => new(reason, inner);
}
