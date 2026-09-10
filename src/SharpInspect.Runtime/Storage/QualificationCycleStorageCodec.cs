using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

/// <summary>Bounded binary codec for schema-26 cycle rows.</summary>
internal static class QualificationCycleStorageCodec
{
    private const int FormatVersion = 1;
    private const int MaximumTextBytes = 16 * 1024;
    private const int MaximumPolicyBytes = 8 * 1024 * 1024;
    private const int MaximumPayloadBytes = 16 * 1024 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    internal static byte[] Encode(QualificationCycleEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true))
        {
            writer.Write(FormatVersion);
            writer.Write(value.Position);
            WriteNullableText(writer, value.PreviousHash);
            WriteGuid(writer, value.SessionId);
            writer.Write(value.RunId is not null);
            if (value.RunId is not null) WriteGuid(writer, value.RunId.Value);
            WriteText(writer, value.EndpointBindingHash);
            WriteText(writer, value.ProfileHash);
            writer.Write(value.ControllerEpoch.HasValue);
            if (value.ControllerEpoch is { } controllerEpoch) writer.Write(controllerEpoch);
            writer.Write(value.CycleSequence.HasValue);
            if (value.CycleSequence is { } cycleSequence) writer.Write(cycleSequence);
            writer.Write((byte)value.Kind);
            writer.Write((byte)value.EvidenceMode);
            writer.Write(value.PolicySnapshot is not null);
            if (value.PolicySnapshot is not null)
                WriteBytes(writer, TraceStoragePolicyStorageCodec.EncodePublication(
                    value.PolicySnapshot.Publication), MaximumPolicyBytes);
            writer.Write(value.QualificationPosition);
            WriteText(writer, value.QualificationHash);
            WriteNullableText(writer, value.RunContentHash);
            WriteText(writer, value.ReasonCode);
            writer.Write(value.RecordedAtUtc.UtcTicks);
            writer.Write(value.Terminal);
            writer.Write(value.AuditSequence);
            WriteNullableText(writer, value.AuditHash);
            WriteText(writer, value.ContentHash);
        }
        var bytes = stream.ToArray();
        if (bytes.Length > MaximumPayloadBytes)
            throw new InvalidOperationException("QualificationCyclePayloadTooLarge");
        return bytes;
    }

    internal static byte[] EncodeAuditPayload(QualificationCycleEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var projected = new QualificationCycleEvent(value.Position, value.PreviousHash,
            value.SessionId, value.RunId, value.EndpointBindingHash, value.ProfileHash,
            value.Kind, value.PolicySnapshot, value.QualificationPosition,
            value.QualificationHash, value.RunContentHash, value.ReasonCode,
            value.RecordedAtUtc, value.Terminal, 0, null,
            value.ControllerEpoch, value.CycleSequence);
        return Encode(projected);
    }

    internal static QualificationCycleEvent Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumPayloadBytes)
            throw new InvalidOperationException("QualificationCyclePayloadInvalid");
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        try
        {
            if (reader.ReadInt32() != FormatVersion)
                throw new InvalidOperationException("QualificationCycleFormatInvalid");
            var position = reader.ReadInt64();
            var previous = ReadNullableText(reader);
            var sessionId = ReadGuid(reader);
            var runId = reader.ReadBoolean() ? new QualificationRunId(ReadGuid(reader)) : null;
            var endpoint = ReadText(reader);
            var profile = ReadText(reader);
            var controllerEpoch = reader.ReadBoolean() ? (uint?)reader.ReadUInt32() : null;
            var cycleSequence = reader.ReadBoolean() ? (uint?)reader.ReadUInt32() : null;
            var kind = (QualificationCycleEventKind)reader.ReadByte();
            if ((QualificationEvidenceCaptureMode)reader.ReadByte() != QualificationEvidenceCaptureMode.None)
                throw new InvalidOperationException("QualificationEvidenceNoneRequired");
            var policy = reader.ReadBoolean()
                ? new TraceStoragePolicySnapshot(TraceStoragePolicyStorageCodec.DecodePublication(
                    ReadBytes(reader, MaximumPolicyBytes))) : null;
            var qualificationPosition = reader.ReadInt64();
            var qualificationHash = ReadText(reader);
            var runContent = ReadNullableText(reader);
            var reason = ReadText(reader);
            var recorded = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            var terminal = reader.ReadBoolean();
            var auditSequence = reader.ReadInt64();
            var auditHash = ReadNullableText(reader);
            var contentHash = ReadText(reader);
            if (stream.Position != stream.Length)
                throw new InvalidOperationException("QualificationCycleTrailingBytes");
            var value = new QualificationCycleEvent(position, previous, sessionId, runId,
                endpoint, profile, kind, policy, qualificationPosition, qualificationHash,
                runContent, reason, recorded, terminal, auditSequence, auditHash,
                controllerEpoch, cycleSequence);
            if (!string.Equals(value.ContentHash, contentHash, StringComparison.Ordinal))
                throw new InvalidOperationException("QualificationCycleContentHashMismatch");
            return value;
        }
        catch (EndOfStreamException exception)
        { throw new InvalidOperationException("QualificationCyclePayloadTruncated", exception); }
        catch (DecoderFallbackException exception)
        { throw new InvalidOperationException("QualificationCyclePayloadUtf8Invalid", exception); }
        catch (OverflowException exception)
        { throw new InvalidOperationException("QualificationCyclePayloadInvalid", exception); }
    }

    private static void WriteGuid(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray());

    private static Guid ReadGuid(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16);
        if (bytes.Length != 16) throw new EndOfStreamException();
        var value = new Guid(bytes);
        if (value == Guid.Empty) throw new InvalidOperationException("QualificationCycleGuidInvalid");
        return value;
    }

    private static void WriteText(BinaryWriter writer, string value)
    {
        var bytes = StrictUtf8.GetBytes(value ?? throw new ArgumentNullException(nameof(value)));
        if (bytes.Length > MaximumTextBytes)
            throw new InvalidOperationException("QualificationCycleTextTooLarge");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteNullableText(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null) WriteText(writer, value);
    }

    private static string ReadText(BinaryReader reader) => StrictUtf8.GetString(ReadBytes(reader,
        MaximumTextBytes));

    private static string? ReadNullableText(BinaryReader reader) => reader.ReadBoolean() ? ReadText(reader) : null;

    private static void WriteBytes(BinaryWriter writer, byte[] bytes, int maximum)
    {
        if (bytes.Length > maximum) throw new InvalidOperationException("QualificationCyclePayloadInvalid");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static byte[] ReadBytes(BinaryReader reader, int maximum)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > maximum) throw new InvalidOperationException("QualificationCyclePayloadInvalid");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return bytes;
    }
}
