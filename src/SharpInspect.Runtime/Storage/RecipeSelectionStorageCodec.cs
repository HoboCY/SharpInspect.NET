using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

/// <summary>Strict bounded canonical encoding for selection revisions, independently versioned from activations.</summary>
internal static partial class RecipeSelectionStorageCodec
{
    private const int Magic = 0x31565352; // RSV1
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(RecipeSelectionRevision value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(value.Position);
        GuidValue(writer, value.RevisionId);
        GuidValue(writer, value.OperationId);
        Text(writer, value.Policy.Id); Text(writer, value.Policy.Version); writer.Write((int)value.Policy.Mode);
        writer.Write(value.Map is not null);
        if (value.Map is { } map)
        {
            Text(writer, map.Id); Text(writer, map.Version); writer.Write(map.Entries.Count);
            foreach (var entry in map.Entries)
            {
                writer.Write(entry.SelectionCode); Recipe(writer, entry.Recipe);
                GuidValue(writer, entry.ReleaseId); Text(writer, entry.ReleaseRecordContentHash);
            }
        }
        writer.Write(value.Previous is not null);
        if (value.Previous is { } previous)
        { writer.Write(previous.Position); GuidValue(writer, previous.RevisionId); Text(writer, previous.ContentHash); }
        writer.Write(value.ReleaseHighWatermark);
        writer.Write(value.Validations.Count);
        foreach (var proof in value.Validations)
        {
            Recipe(writer, proof.Recipe); GuidValue(writer, proof.ReleaseId);
            Text(writer, proof.ReleaseRecordContentHash); Text(writer, proof.ValidationContentHash);
        }
        GuidValue(writer, value.ActorPrincipalId); GuidValue(writer, value.ActorSessionId);
        writer.Write(value.ActorAuthorizationRevision); GuidValue(writer, value.StepUpGrantId);
        Contract(writer, value.AuthorizationPolicy);
        Text(writer, value.ChangeReason); Text(writer, value.AuthorizationTarget);
        writer.Write(value.RecordedAtUtc.UtcTicks); Text(writer, value.ContentHash);
        writer.Flush();
        if (stream.Length > RecipeSelectionStoreOptions.MaximumPayloadBytes)
            throw new InvalidOperationException("RecipeSelectionPayloadCapacityExceeded");
        return stream.ToArray();
    }

    internal static RecipeSelectionRevision Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length is < 1 or > RecipeSelectionStoreOptions.MaximumPayloadBytes)
            throw new InvalidOperationException("RecipeSelectionPayloadCapacityExceeded");
        try
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Utf8, leaveOpen: true);
            if (reader.ReadInt32() != Magic) throw new InvalidOperationException("RecipeSelectionFormatUnsupported");
            var position = reader.ReadInt64(); var revision = GuidValue(reader); var operation = GuidValue(reader);
            var policy = new RecipeSelectionPolicy(Text(reader), Text(reader), (RecipeSelectionMode)reader.ReadInt32());
            RecipeSelectionMap? map = null;
            if (Boolean(reader))
            {
                var id = Text(reader); var version = Text(reader);
                var count = Count(reader, RecipeSelectionMap.MaximumEntries);
                var entries = new RecipeSelectionMapEntry[count];
                for (var i = 0; i < count; i++)
                    entries[i] = new(reader.ReadUInt32(), Recipe(reader), GuidValue(reader), Text(reader));
                map = new(id, version, entries);
            }
            RecipeSelectionReference? previous = Boolean(reader)
                ? new(reader.ReadInt64(), GuidValue(reader), Text(reader)) : null;
            var high = reader.ReadInt64();
            var proofCount = Count(reader, RecipeSelectionMap.MaximumEntries * 2);
            var proofs = new RecipeSelectionValidatedRelease[proofCount];
            for (var i = 0; i < proofCount; i++)
                proofs[i] = new(Recipe(reader), GuidValue(reader), Text(reader), Text(reader));
            var actor = GuidValue(reader); var session = GuidValue(reader); var authorizationRevision = reader.ReadInt64();
            var grant = GuidValue(reader); var authorization = Contract(reader);
            var reason = Text(reader); var target = Text(reader); var time = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            var hash = Text(reader);
            var result = new RecipeSelectionRevision(position, revision, operation, policy, map, previous, high,
                proofs, actor, session, authorizationRevision, grant, authorization, reason, target, time);
            if (stream.Position != stream.Length || hash != result.ContentHash || !payload.Span.SequenceEqual(Encode(result)))
                throw new InvalidOperationException("RecipeSelectionPayloadCanonicalMismatch");
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { throw new InvalidOperationException("RecipeSelectionPayloadInvalid", exception); }
    }

    private static int Count(BinaryReader reader, int maximum)
    {
        var value = reader.ReadInt32();
        if (value < 0 || value > maximum) throw new InvalidOperationException("RecipeSelectionCountInvalid");
        return value;
    }
    private static bool Boolean(BinaryReader reader) => reader.ReadByte() switch
    { 0 => false, 1 => true, _ => throw new InvalidOperationException("RecipeSelectionBooleanInvalid") };
    private static void Text(BinaryWriter writer, string value)
    {
        var bytes = Utf8.GetBytes(value);
        if (bytes.Length > 4096) throw new InvalidOperationException("RecipeSelectionStringCapacityExceeded");
        writer.Write(bytes.Length); writer.Write(bytes);
    }
    private static string Text(BinaryReader reader)
    {
        var length = Count(reader, 4096);
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return Utf8.GetString(bytes);
    }
    private static void GuidValue(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray());
    private static Guid GuidValue(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16);
        if (bytes.Length != 16) throw new EndOfStreamException();
        return new(bytes);
    }
    private static void Recipe(BinaryWriter writer, RecipeReference value)
    { Text(writer, value.Id); Text(writer, value.Version); Text(writer, value.ContentHash); }
    private static RecipeReference Recipe(BinaryReader reader) => new(Text(reader), Text(reader), Text(reader));
    private static void Contract(BinaryWriter writer, RecipeContractReference value)
    { Text(writer, value.Id); Text(writer, value.Version); Text(writer, value.ContentHash); }
    private static RecipeContractReference Contract(BinaryReader reader) => new(Text(reader), Text(reader), Text(reader));
}
