using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Strict private wire format for one released recipe.  A release keeps the full
/// canonical draft document in the payload so a later read never depends on a
/// mutable draft head or an algorithm factory.
/// </summary>
internal static class RecipeReleaseStorageCodec
{
    internal const int MaximumPayloadBytes = 8 * 1024 * 1024;
    internal const string Kind = "RecipeRelease";

    private const int Magic = 0x31525253; // SRR1, little endian on the wire.
    private const byte FormatVersion = 1;
    private const int MaximumStringBytes = 256 * 1024;
    private const int MaximumCheckCount = 256;
    private const int MaximumChangeCount = 4096;

    internal static byte[] Encode(RecipeReleaseRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            WriteRecord(writer, record);
            WriteString(writer, record.ContentHash, 64);
        }

        if (stream.Length is < 1 or > MaximumPayloadBytes)
            throw new InvalidOperationException("RecipeReleasePayloadCapacityExceeded");
        return stream.ToArray();
    }

    internal static RecipeReleaseRecord Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length is < 1 or > MaximumPayloadBytes)
            throw new InvalidOperationException("RecipeReleasePayloadCapacityExceeded");
        try
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
            if (reader.ReadInt32() != Magic || reader.ReadByte() != FormatVersion)
                throw new InvalidOperationException("RecipeReleasePayloadVersionUnsupported");
            var record = ReadRecord(reader);
            var savedHash = ReadString(reader, MaximumStringBytes);
            if (stream.Position != stream.Length)
                throw new InvalidOperationException("RecipeReleasePayloadTrailingBytes");
            if (!string.Equals(savedHash, record.ContentHash, StringComparison.Ordinal))
                throw new InvalidOperationException("RecipeReleaseContentHashMismatch");
            if (!payload.Span.SequenceEqual(Encode(record)))
                throw new InvalidOperationException("RecipeReleasePayloadCanonicalMismatch");
            return record;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
                                           not StackOverflowException and
                                           not InvalidOperationException { Message: "RecipeReleasePayloadCapacityExceeded" })
        {
            throw new InvalidOperationException("RecipeReleasePayloadInvalid", exception);
        }
    }

    internal static string ContentHash(RecipeReleaseRecord record) =>
        record?.ContentHash ?? throw new ArgumentNullException(nameof(record));

    private static void WriteRecord(BinaryWriter writer, RecipeReleaseRecord record)
    {
        writer.Write(record.Position);
        writer.Write(record.ReleaseId.ToByteArray());
        writer.Write(record.OperationId.ToByteArray());
        writer.Write(record.RecipeVersion);
        WriteDraftRevision(writer, record.Source);
        WritePolicy(writer, record.GovernancePolicy);

        WriteCount(writer, record.Checks.Count, MaximumCheckCount);
        foreach (var check in record.Checks)
        {
            WriteString(writer, check.GateId, MaximumStringBytes);
            WriteString(writer, check.Subject, MaximumStringBytes);
            writer.Write(check.Passed);
            WriteString(writer, check.ReasonCode, MaximumStringBytes);
            WriteOptionalContract(writer, check.Contract);
            WriteString(writer, check.ContentHash, 64);
        }

        WriteCount(writer, record.Changes.Count, MaximumChangeCount);
        foreach (var change in record.Changes)
        {
            WriteString(writer, change.Path, MaximumStringBytes);
            WriteOptionalString(writer, change.BeforeValue, 131072);
            WriteOptionalString(writer, change.AfterValue, 131072);
            writer.Write(change.AuthorPrincipalId.ToByteArray());
            writer.Write(change.SourceDraftId.ToByteArray());
            writer.Write(change.SourceRevision);
            WriteString(writer, change.ContentHash, 64);
        }

        writer.Write(record.ApproverPrincipalId.ToByteArray());
        writer.Write(record.ApproverSessionId.ToByteArray());
        writer.Write(record.ApproverAuthorizationRevision);
        writer.Write(record.StepUpGrantId.ToByteArray());
        WriteContract(writer, record.AuthorizationPolicy);
        WriteString(writer, record.ReleaseReason, MaximumStringBytes);
        WriteString(writer, record.AuthorizationTarget, 64);
        writer.Write(record.ReleasedAtUtc.UtcTicks);
    }

    private static RecipeReleaseRecord ReadRecord(BinaryReader reader)
    {
        var position = reader.ReadInt64();
        var releaseId = ReadGuid(reader);
        var operationId = ReadGuid(reader);
        var recipeVersion = reader.ReadInt64();
        var source = ReadDraftRevision(reader);
        var policy = ReadPolicy(reader);

        var checks = new List<RecipeReleaseValidationCheck>(ReadCount(reader, MaximumCheckCount));
        for (var index = 0; index < checks.Capacity; index++)
        {
            var check = new RecipeReleaseValidationCheck(ReadString(reader, MaximumStringBytes),
                ReadString(reader, MaximumStringBytes), reader.ReadBoolean(),
                ReadString(reader, MaximumStringBytes), ReadOptionalContract(reader));
            var savedHash = ReadString(reader, 64);
            if (!string.Equals(savedHash, check.ContentHash, StringComparison.Ordinal))
                throw new InvalidOperationException("RecipeReleaseCheckHashMismatch");
            checks.Add(check);
        }

        var changes = new List<RecipeReleaseChange>(ReadCount(reader, MaximumChangeCount));
        for (var index = 0; index < changes.Capacity; index++)
        {
            var change = new RecipeReleaseChange(ReadString(reader, MaximumStringBytes),
                ReadOptionalString(reader, 131072), ReadOptionalString(reader, 131072),
                ReadGuid(reader), ReadGuid(reader), reader.ReadInt64());
            var savedHash = ReadString(reader, 64);
            if (!string.Equals(savedHash, change.ContentHash, StringComparison.Ordinal))
                throw new InvalidOperationException("RecipeReleaseChangeHashMismatch");
            changes.Add(change);
        }

        var approverPrincipalId = ReadGuid(reader);
        var approverSessionId = ReadGuid(reader);
        var approverAuthorizationRevision = reader.ReadInt64();
        var stepUpGrantId = ReadGuid(reader);
        var authorizationPolicy = ReadContract(reader);
        var releaseReason = ReadString(reader, MaximumStringBytes);
        var authorizationTarget = ReadString(reader, 64);
        DateTimeOffset releasedAtUtc;
        try { releasedAtUtc = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero); }
        catch (ArgumentOutOfRangeException exception)
        { throw new InvalidOperationException("RecipeReleaseTimestampInvalid", exception); }

        return new RecipeReleaseRecord(position, releaseId, operationId, recipeVersion, source, policy,
            checks, changes, approverPrincipalId, approverSessionId, approverAuthorizationRevision,
            stepUpGrantId, authorizationPolicy, releaseReason, authorizationTarget, releasedAtUtc);
    }

    private static void WriteDraftRevision(BinaryWriter writer, RecipeDraftRevision revision)
    {
        writer.Write(revision.Position);
        writer.Write(revision.DraftId.ToByteArray());
        writer.Write(revision.Revision);
        writer.Write(revision.OperationId.ToByteArray());
        WriteOptionalString(writer, revision.PreviousRevisionContentHash, 64);
        WriteString(writer, revision.RevisionContentHash, 64);
        if (!RecipeDraftStorageCodec.TryEncodeContent(revision.Content, out var document, out var reason) ||
            document is null)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(reason)
                ? "RecipeReleaseSourceContentInvalid" : reason);
        WriteString(writer, document.PayloadHash, 64);
        WriteString(writer, document.PayloadJson, RecipeDraftStorageCodec.MaximumPayloadBytes);
        writer.Write(revision.AuthorPrincipalId.ToByteArray());
        writer.Write(revision.AuthorSessionId.ToByteArray());
        writer.Write(revision.AuthorAuthorizationRevision);
        WriteString(writer, revision.ChangeReason, MaximumStringBytes);
        writer.Write(revision.RecordedAtUtc.UtcTicks);
    }

    private static RecipeDraftRevision ReadDraftRevision(BinaryReader reader)
    {
        var position = reader.ReadInt64();
        var draftId = ReadGuid(reader);
        var revision = reader.ReadInt64();
        var operationId = ReadGuid(reader);
        var previousHash = ReadOptionalString(reader, 64);
        var revisionHash = ReadString(reader, 64);
        var payloadHash = ReadString(reader, 64);
        var payloadJson = ReadString(reader, RecipeDraftStorageCodec.MaximumPayloadBytes);
        if (!RecipeDraftStorageCodec.TryDecodeContent(payloadJson, payloadHash, out var content, out var reason) ||
            content is null)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(reason)
                ? "RecipeReleaseSourceContentInvalid" : reason);
        var authorPrincipalId = ReadGuid(reader);
        var authorSessionId = ReadGuid(reader);
        var authorizationRevision = reader.ReadInt64();
        var changeReason = ReadString(reader, MaximumStringBytes);
        DateTimeOffset recordedAtUtc;
        try { recordedAtUtc = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero); }
        catch (ArgumentOutOfRangeException exception)
        { throw new InvalidOperationException("RecipeReleaseTimestampInvalid", exception); }
        return new RecipeDraftRevision(position, draftId, revision, operationId, previousHash,
            revisionHash, content, authorPrincipalId, authorSessionId, authorizationRevision,
            changeReason, recordedAtUtc);
    }

    private static void WritePolicy(BinaryWriter writer, RecipeGovernancePolicy policy)
    {
        WriteString(writer, policy.Id, MaximumStringBytes);
        WriteString(writer, policy.Version, MaximumStringBytes);
        writer.Write((int)policy.Mode);
        WriteString(writer, policy.ContentHash, 64);
    }

    private static RecipeGovernancePolicy ReadPolicy(BinaryReader reader)
    {
        var policy = new RecipeGovernancePolicy(ReadString(reader, MaximumStringBytes),
            ReadString(reader, MaximumStringBytes), (RecipeGovernanceMode)reader.ReadInt32());
        var savedHash = ReadString(reader, 64);
        if (!string.Equals(savedHash, policy.ContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("RecipeReleasePolicyHashMismatch");
        return policy;
    }

    private static void WriteContract(BinaryWriter writer, RecipeContractReference contract)
    {
        WriteString(writer, contract.Id, MaximumStringBytes);
        WriteString(writer, contract.Version, MaximumStringBytes);
        WriteString(writer, contract.ContentHash, 64);
    }

    private static RecipeContractReference ReadContract(BinaryReader reader)
    {
        var contract = new RecipeContractReference(ReadString(reader, MaximumStringBytes),
            ReadString(reader, MaximumStringBytes), ReadString(reader, 64));
        return contract;
    }

    private static void WriteOptionalContract(BinaryWriter writer, RecipeContractReference? contract)
    {
        writer.Write(contract is not null);
        if (contract is not null) WriteContract(writer, contract);
    }

    private static RecipeContractReference? ReadOptionalContract(BinaryReader reader) =>
        reader.ReadBoolean() ? ReadContract(reader) : null;

    private static void WriteOptionalString(BinaryWriter writer, string? value, int maximumBytes)
    {
        writer.Write(value is not null);
        if (value is not null) WriteString(writer, value, maximumBytes);
    }

    private static string? ReadOptionalString(BinaryReader reader, int maximumBytes) =>
        reader.ReadBoolean() ? ReadString(reader, maximumBytes) : null;

    private static void WriteString(BinaryWriter writer, string value, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = StrictUtf8(value, maximumBytes);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader, int maximumBytes)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > maximumBytes ||
            (maximumBytes <= MaximumStringBytes && length > MaximumStringBytes))
            throw new InvalidOperationException("RecipeReleaseStringCapacityExceeded");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new InvalidOperationException("RecipeReleasePayloadTruncated");
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException exception)
        { throw new InvalidOperationException("RecipeReleaseUtf8Invalid", exception); }
    }

    private static byte[] StrictUtf8(string value, int maximumBytes)
    {
        try
        {
            var encoding = new UTF8Encoding(false, true);
            var bytes = encoding.GetBytes(value);
            if (bytes.Length > maximumBytes) throw new InvalidOperationException("RecipeReleaseStringCapacityExceeded");
            return bytes;
        }
        catch (EncoderFallbackException exception)
        { throw new InvalidOperationException("RecipeReleaseUtf8Invalid", exception); }
    }

    private static void WriteCount(BinaryWriter writer, int count, int maximum)
    {
        if (count < 0 || count > maximum) throw new InvalidOperationException("RecipeReleaseEntryCapacityExceeded");
        writer.Write(count);
    }

    private static int ReadCount(BinaryReader reader, int maximum)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > maximum) throw new InvalidOperationException("RecipeReleaseEntryCapacityExceeded");
        return count;
    }

    private static Guid ReadGuid(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16);
        if (bytes.Length != 16) throw new InvalidOperationException("RecipeReleasePayloadTruncated");
        var value = new Guid(bytes);
        if (value == Guid.Empty) throw new InvalidOperationException("RecipeReleaseIdentityInvalid");
        return value;
    }
}
