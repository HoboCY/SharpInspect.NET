using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Strict, bounded canonical encoding for one immutable schema-33 Recipe lifecycle
/// transition. The payload is self-describing and round-trips byte-for-byte; the
/// decoded record is always rebuilt through the contract's own internal constructor,
/// so a payload can never introduce a transition the public contract would reject.
/// Every stored row is re-decoded and re-encoded during reads, so a mutated,
/// truncated or non-canonical payload fails the read instead of being projected.
/// </summary>
internal static class RecipeLifecycleStorageCodec
{
    private const int Magic = 0x31434C52; // RLC1
    private const byte FormatVersion = 1;
    private const int MaximumTextBytes = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(RecipeLifecycleRecord value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Utf8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(value.Position);
            GuidValue(writer, value.TransitionId);
            GuidValue(writer, value.OperationId);
            GuidValue(writer, value.RuntimeEpoch);
            writer.Write((byte)value.Kind);
            OptionalText(writer, value.PreviousHash);
            GuidValue(writer, value.SourceDraft.DraftId);
            writer.Write(value.SourceDraft.Revision);
            Text(writer, value.SourceDraft.RevisionContentHash);
            Text(writer, value.SourceContentHash);
            writer.Write(value.Recipe is not null);
            if (value.Recipe is { } recipe) Recipe(writer, recipe);
            OptionalGuid(writer, value.ReleaseId);
            OptionalText(writer, value.ReleaseRecordContentHash);
            OptionalReference(writer, value.ObservedActive);
            OptionalReference(writer, value.ClearedActive);
            writer.Write(value.MapImpacts.Count);
            foreach (var impact in value.MapImpacts)
            {
                writer.Write(impact.Selection.Position);
                GuidValue(writer, impact.Selection.RevisionId);
                Text(writer, impact.Selection.ContentHash);
                Contract(writer, impact.Map);
                writer.Write(impact.AffectedCodes.Count);
                foreach (var code in impact.AffectedCodes) writer.Write(code);
            }
            GuidValue(writer, value.ActorPrincipalId);
            GuidValue(writer, value.ActorSessionId);
            writer.Write(value.ActorAuthorizationRevision);
            GuidValue(writer, value.StepUpGrantId);
            Contract(writer, value.AuthorizationPolicy);
            Text(writer, value.AuthorizationTarget);
            Text(writer, value.Reason);
            writer.Write(value.RecordedAtUtc.UtcTicks);
            Text(writer, value.ContentHash);
        }
        if (stream.Length > RecipeLifecycleStoreOptions.MaximumPayloadBytesHardLimit)
            throw new InvalidOperationException("RecipeLifecyclePayloadCapacityExceeded");
        return stream.ToArray();
    }

    internal static RecipeLifecycleRecord Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length is < 1 or > RecipeLifecycleStoreOptions.MaximumPayloadBytesHardLimit)
            throw new InvalidOperationException("RecipeLifecyclePayloadCapacityExceeded");
        try
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Utf8, leaveOpen: true);
            if (reader.ReadInt32() != Magic || reader.ReadByte() != FormatVersion)
                throw new InvalidOperationException("RecipeLifecycleFormatUnsupported");
            var position = reader.ReadInt64();
            var transitionId = GuidValue(reader);
            var operationId = GuidValue(reader);
            var runtimeEpoch = GuidValue(reader);
            var kind = EnumValue<RecipeLifecycleKind>(reader.ReadByte(), "RecipeLifecycleKindInvalid");
            var previousHash = Boolean(reader) ? Text(reader) : null;
            var draftId = GuidValue(reader);
            var sourceRevision = reader.ReadInt64();
            var sourceRevisionContentHash = Text(reader);
            var sourceContentHash = Text(reader);
            RecipeReference? recipe = Boolean(reader) ? Recipe(reader) : null;
            Guid? releaseId = Boolean(reader) ? GuidValue(reader) : null;
            var releaseRecordContentHash = Boolean(reader) ? Text(reader) : null;
            var observedActive = ReadReference(reader);
            var clearedActive = ReadReference(reader);
            var impactCount = Count(reader, RecipeSelectionMap.MaximumEntries);
            var impacts = new RecipeRetirementMapImpact[impactCount];
            for (var index = 0; index < impactCount; index++)
            {
                var selection = new RecipeSelectionReference(reader.ReadInt64(), GuidValue(reader), Text(reader));
                var map = Contract(reader);
                var codeCount = Count(reader, RecipeSelectionMap.MaximumEntries);
                var codes = new uint[codeCount];
                for (var codeIndex = 0; codeIndex < codeCount; codeIndex++) codes[codeIndex] = reader.ReadUInt32();
                impacts[index] = new RecipeRetirementMapImpact(selection, map, codes);
            }
            var actorPrincipalId = GuidValue(reader);
            var actorSessionId = GuidValue(reader);
            var actorAuthorizationRevision = reader.ReadInt64();
            var stepUpGrantId = GuidValue(reader);
            var authorizationPolicy = Contract(reader);
            var authorizationTarget = Text(reader);
            var reason = Text(reader);
            var recordedAtUtc = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            var contentHash = Text(reader);
            var value = new RecipeLifecycleRecord(position, transitionId, operationId, runtimeEpoch, kind,
                previousHash, new RecipeDraftRevisionReference(draftId, sourceRevision, sourceRevisionContentHash),
                sourceContentHash, recipe, releaseId, releaseRecordContentHash, observedActive, clearedActive,
                impacts, actorPrincipalId, actorSessionId, actorAuthorizationRevision, stepUpGrantId,
                authorizationPolicy, authorizationTarget, reason, recordedAtUtc);
            if (stream.Position != stream.Length)
                throw new InvalidOperationException("RecipeLifecyclePayloadTrailingBytes");
            if (!string.Equals(value.ContentHash, contentHash, StringComparison.Ordinal))
                throw new InvalidOperationException("RecipeLifecycleContentHashMismatch");
            if (!payload.Span.SequenceEqual(Encode(value)))
                throw new InvalidOperationException("RecipeLifecyclePayloadCanonicalMismatch");
            return value;
        }
        catch (InvalidOperationException exception) when (
            exception.Message.StartsWith("RecipeLifecycle", StringComparison.Ordinal))
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidOperationException("RecipeLifecyclePayloadInvalid", exception);
        }
    }

    private static int Count(BinaryReader reader, int maximum)
    {
        var value = reader.ReadInt32();
        if (value < 0 || value > maximum) throw new InvalidOperationException("RecipeLifecycleCountInvalid");
        return value;
    }

    private static bool Boolean(BinaryReader reader) => reader.ReadByte() switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidOperationException("RecipeLifecycleBooleanInvalid")
    };

    private static void OptionalText(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null) Text(writer, value);
    }

    private static void OptionalGuid(BinaryWriter writer, Guid? value)
    {
        writer.Write(value is not null);
        if (value is { } identifier) GuidValue(writer, identifier);
    }

    private static void OptionalReference(BinaryWriter writer, RecipeActivationReference? value)
    {
        writer.Write(value is not null);
        if (value is { } reference)
        {
            writer.Write(reference.Position);
            GuidValue(writer, reference.ActivationId);
            Text(writer, reference.ContentHash);
        }
    }

    private static RecipeActivationReference? ReadReference(BinaryReader reader) =>
        Boolean(reader) ? new RecipeActivationReference(reader.ReadInt64(), GuidValue(reader), Text(reader)) : null;

    private static void Text(BinaryWriter writer, string value)
    {
        var bytes = Utf8.GetBytes(value);
        if (bytes.Length > MaximumTextBytes) throw new InvalidOperationException("RecipeLifecycleStringCapacityExceeded");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string Text(BinaryReader reader)
    {
        var length = Count(reader, MaximumTextBytes);
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return Utf8.GetString(bytes);
    }

    private static void GuidValue(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray());

    private static Guid GuidValue(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16);
        if (bytes.Length != 16) throw new EndOfStreamException();
        return new Guid(bytes);
    }

    private static void Recipe(BinaryWriter writer, RecipeReference value)
    {
        Text(writer, value.Id);
        Text(writer, value.Version);
        Text(writer, value.ContentHash);
    }

    private static RecipeReference Recipe(BinaryReader reader) => new(Text(reader), Text(reader), Text(reader));

    private static void Contract(BinaryWriter writer, RecipeContractReference value)
    {
        Text(writer, value.Id);
        Text(writer, value.Version);
        Text(writer, value.ContentHash);
    }

    private static RecipeContractReference Contract(BinaryReader reader) =>
        new(Text(reader), Text(reader), Text(reader));

    private static T EnumValue<T>(byte value, string reason) where T : struct, Enum =>
        Enum.IsDefined(typeof(T), value) ? (T)Enum.ToObject(typeof(T), value) :
        throw new InvalidOperationException(reason);
}
