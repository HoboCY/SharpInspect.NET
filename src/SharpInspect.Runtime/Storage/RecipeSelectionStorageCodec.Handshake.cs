using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

internal static partial class RecipeSelectionStorageCodec
{
    internal const int MaximumHandshakePayloadBytes = 64 * 1024;
    private const int HandshakeMagic = 0x31484352; // RCH1

    internal static byte[] EncodeHandshake(RecipeChangeHistoryEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
        writer.Write(HandshakeMagic); writer.Write(value.Position); GuidValue(writer, value.EventId);
        WriteRequest(writer, value.Request);
        writer.Write((int)value.Kind);
        writer.Write(value.Outcome is not null);
        if (value.Outcome is { } outcome) writer.Write((ushort)outcome);
        writer.Write(value.Reason is not null);
        if (value.Reason is { } reason) writer.Write((ushort)reason);
        Text(writer, value.ReasonCode);
        writer.Write(value.Activation is not null);
        if (value.Activation is { } activation)
        { writer.Write(activation.Position); GuidValue(writer, activation.ActivationId); Text(writer, activation.ContentHash); }
        writer.Write(value.RecordedAtUtc.UtcTicks); Text(writer, value.ContentHash);
        writer.Flush();
        if (stream.Length > MaximumHandshakePayloadBytes) throw new InvalidOperationException("RecipeChangePayloadCapacityExceeded");
        return stream.ToArray();
    }

    internal static RecipeChangeHistoryEvent DecodeHandshake(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length is < 1 or > MaximumHandshakePayloadBytes)
            throw new InvalidOperationException("RecipeChangePayloadCapacityExceeded");
        try
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Utf8, leaveOpen: true);
            if (reader.ReadInt32() != HandshakeMagic) throw new InvalidOperationException("RecipeChangeFormatUnsupported");
            var position = reader.ReadInt64(); var eventId = GuidValue(reader); var request = ReadRequest(reader);
            var kind = (RecipeChangeEventKind)reader.ReadInt32();
            RecipeChangeOutcome? outcome = Boolean(reader) ? (RecipeChangeOutcome)reader.ReadUInt16() : null;
            RecipeChangeReason? reason = Boolean(reader) ? (RecipeChangeReason)reader.ReadUInt16() : null;
            var reasonCode = Text(reader);
            RecipeActivationReference? activation = Boolean(reader)
                ? new(reader.ReadInt64(), GuidValue(reader), Text(reader)) : null;
            var time = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero); var hash = Text(reader);
            var result = new RecipeChangeHistoryEvent(position, eventId, request, kind, outcome, reason, reasonCode, activation, time);
            if (stream.Position != stream.Length || result.ContentHash != hash || !payload.Span.SequenceEqual(EncodeHandshake(result)))
                throw new InvalidOperationException("RecipeChangePayloadCanonicalMismatch");
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { throw new InvalidOperationException("RecipeChangePayloadInvalid", exception); }
    }

    private static void WriteRequest(BinaryWriter writer, RecipeChangeRequestEvidence value)
    {
        GuidValue(writer, value.RuntimeEpoch); Text(writer, value.EndpointContentHash); Contract(writer, value.ProtocolProfile);
        writer.Write(value.ControllerEpoch); writer.Write(value.RequestSequence); writer.Write(value.SelectionCode);
        writer.Write(value.SelectionRevision is not null);
        if (value.SelectionRevision is { } revision)
        { writer.Write(revision.Position); GuidValue(writer, revision.RevisionId); Text(writer, revision.ContentHash); }
        Contract(writer, value.SelectionPolicy); writer.Write(value.SelectionMap is not null);
        if (value.SelectionMap is { } map) Contract(writer, map);
        writer.Write(value.Target is not null);
        if (value.Target is { } target)
        {
            writer.Write(target.SelectionCode); Recipe(writer, target.Recipe); GuidValue(writer, target.ReleaseId);
            Text(writer, target.ReleaseRecordContentHash);
        }
        writer.Write(value.ObservedAtUtc.UtcTicks); Text(writer, value.SystemPrincipalId);
        Text(writer, value.RequestIdentityHash); Text(writer, value.ContentHash);
    }

    private static RecipeChangeRequestEvidence ReadRequest(BinaryReader reader)
    {
        var runtime = GuidValue(reader); var endpoint = Text(reader); var profile = Contract(reader);
        var epoch = reader.ReadUInt32(); var sequence = reader.ReadUInt32(); var code = reader.ReadUInt32();
        RecipeSelectionReference? revision = Boolean(reader) ? new(reader.ReadInt64(), GuidValue(reader), Text(reader)) : null;
        var policy = Contract(reader); var map = Boolean(reader) ? Contract(reader) : null;
        RecipeSelectionMapEntry? target = Boolean(reader) ? new(reader.ReadUInt32(), Recipe(reader), GuidValue(reader), Text(reader)) : null;
        var time = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero); var principal = Text(reader);
        var identity = Text(reader); var hash = Text(reader);
        var request = new RecipeChangeRequestEvidence(runtime, endpoint, profile, epoch, sequence, code, revision, policy, map, target, time);
        if (principal != request.SystemPrincipalId || identity != request.RequestIdentityHash || hash != request.ContentHash)
            throw new InvalidOperationException("RecipeChangeRequestEvidenceMismatch");
        return request;
    }
}
