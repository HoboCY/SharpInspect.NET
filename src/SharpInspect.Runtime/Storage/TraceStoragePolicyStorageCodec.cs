using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Bounded, versioned bytes for the schema-25 policy ledger.  The policy is
/// stored as an explicit binary value rather than relying on a serializer's
/// property order.  The decoder reconstructs every public value and compares
/// the computed content hash before returning it.
/// </summary>
internal static class TraceStoragePolicyStorageCodec
{
    private const int FormatVersion = 1;
    private const int MaximumTextBytes = 16 * 1024;
    private const int MaximumDefinitionBytes = 8 * 1024 * 1024;
    private const int MaximumRules = 32;
    private const int MaximumRoutes = 64;

    internal static byte[] EncodePolicy(TraceStoragePolicyDefinition policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        using var stream = new MemoryStream();
        using (var writer = NewWriter(stream))
        {
            writer.Write(FormatVersion);
            WriteText(writer, policy.PolicyId);
            WriteText(writer, policy.Version);
            WriteText(writer, policy.ApprovalReference);
            WriteText(writer, policy.ApprovalVersion);
            WriteText(writer, policy.Rationale);
            writer.Write(policy.MinimumReserveBytes);
            writer.Write(policy.MinimumReservePercent);
            writer.Write(policy.EvidenceStageTimeout.Ticks);
            writer.Write(policy.TraceCommitTimeout.Ticks);
            writer.Write(policy.MaximumWalBytes);
            WriteBacklog(writer, policy.ImageBacklog);
            WriteBudget(writer, policy.Scrubber);
            WriteBudget(writer, policy.Checkpoint);

            writer.Write(policy.RetentionRules.Count);
            foreach (var rule in policy.RetentionRules)
            {
                writer.Write((byte)rule.EvidenceClass);
                writer.Write((byte)rule.StartsAt);
                writer.Write(rule.MinimumRetention.Ticks);
            }

            writer.Write(policy.RequiredRoutes.Count);
            foreach (var route in policy.RequiredRoutes)
            {
                WriteText(writer, route.RouteId);
                WriteText(writer, route.ContractVersion);
                WriteText(writer, route.ContractHash);
                WriteBacklog(writer, route.Limits);
            }
        }
        var bytes = stream.ToArray();
        if (bytes.Length > MaximumDefinitionBytes)
            throw new InvalidOperationException("TraceStoragePolicyPayloadTooLarge");
        return bytes;
    }

    internal static TraceStoragePolicyDefinition DecodePolicy(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumDefinitionBytes)
            throw new InvalidOperationException("TraceStoragePolicyPayloadInvalid");
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        try
        {
            if (reader.ReadInt32() != FormatVersion)
                throw new InvalidOperationException("TraceStoragePolicyFormatInvalid");
            var policyId = ReadText(reader);
            var version = ReadText(reader);
            var approvalReference = ReadText(reader);
            var approvalVersion = ReadText(reader);
            var rationale = ReadText(reader);
            var reserveBytes = reader.ReadInt64();
            var reservePercent = reader.ReadDecimal();
            var stageTimeout = TimeSpan.FromTicks(reader.ReadInt64());
            var commitTimeout = TimeSpan.FromTicks(reader.ReadInt64());
            var maximumWalBytes = reader.ReadInt64();
            var imageBacklog = ReadBacklog(reader);
            var scrubber = ReadBudget(reader);
            var checkpoint = ReadBudget(reader);

            var ruleCount = ReadCount(reader, MaximumRules, "TraceStorageRetentionRuleCountInvalid");
            var rules = new TraceRetentionRule[ruleCount];
            for (var index = 0; index < ruleCount; index++)
                rules[index] = new TraceRetentionRule((TraceRetentionClass)reader.ReadByte(),
                    (RetentionStartEvent)reader.ReadByte(), TimeSpan.FromTicks(reader.ReadInt64()));

            var routeCount = ReadCount(reader, MaximumRoutes, "TraceStorageRouteCountInvalid");
            var routes = new TraceStorageRouteLimit[routeCount];
            for (var index = 0; index < routeCount; index++)
                routes[index] = new TraceStorageRouteLimit(ReadText(reader), ReadText(reader),
                    ReadText(reader), ReadBacklog(reader));
            if (stream.Position != stream.Length)
                throw new InvalidOperationException("TraceStoragePolicyTrailingBytes");
            return new TraceStoragePolicyDefinition(policyId, version, approvalReference, approvalVersion,
                rationale, rules, reserveBytes, reservePercent, routes, imageBacklog, stageTimeout,
                commitTimeout, scrubber, checkpoint, maximumWalBytes);
        }
        catch (EndOfStreamException exception)
        { throw new InvalidOperationException("TraceStoragePolicyPayloadTruncated", exception); }
        catch (DecoderFallbackException exception)
        { throw new InvalidOperationException("TraceStoragePolicyPayloadUtf8Invalid", exception); }
        catch (OverflowException exception)
        { throw new InvalidOperationException("TraceStoragePolicyPayloadInvalid", exception); }
    }

    internal static byte[] EncodePublication(TraceStoragePolicyPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var policyBytes = EncodePolicy(publication.Policy);
        using var stream = new MemoryStream();
        using (var writer = NewWriter(stream))
        {
            writer.Write(FormatVersion);
            writer.Write(publication.Version);
            WriteGuid(writer, publication.OperationId);
            WriteGuid(writer, publication.PrincipalId);
            WriteGuid(writer, publication.SessionId);
            writer.Write(publication.AuthorizationRevision);
            WriteGuid(writer, publication.StepUpGrantId);
            writer.Write(publication.PublishedAtUtc.UtcTicks);
            WriteNullableText(writer, publication.PreviousContentHash);
            WriteBytes(writer, policyBytes, MaximumDefinitionBytes);
            WriteText(writer, publication.ContentHash);
        }
        var bytes = stream.ToArray();
        if (bytes.Length > MaximumDefinitionBytes)
            throw new InvalidOperationException("TraceStoragePolicyPayloadTooLarge");
        return bytes;
    }

    internal static TraceStoragePolicyPublication DecodePublication(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumDefinitionBytes)
            throw new InvalidOperationException("TraceStoragePolicyPayloadInvalid");
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        try
        {
            if (reader.ReadInt32() != FormatVersion)
                throw new InvalidOperationException("TraceStoragePolicyFormatInvalid");
            var version = reader.ReadInt64();
            var operationId = ReadGuid(reader);
            var principalId = ReadGuid(reader);
            var sessionId = ReadGuid(reader);
            var authorizationRevision = reader.ReadInt64();
            var grantId = ReadGuid(reader);
            var publishedAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            var previous = ReadNullableText(reader);
            var policy = DecodePolicy(ReadBytes(reader, MaximumDefinitionBytes));
            var expectedHash = ReadText(reader);
            if (stream.Position != stream.Length)
                throw new InvalidOperationException("TraceStoragePolicyTrailingBytes");
            var publication = new TraceStoragePolicyPublication(version, policy, operationId,
                principalId, sessionId, authorizationRevision, grantId, publishedAt, previous);
            if (!string.Equals(publication.ContentHash, expectedHash, StringComparison.Ordinal))
                throw new InvalidOperationException("TraceStoragePolicyPublicationHashMismatch");
            return publication;
        }
        catch (EndOfStreamException exception)
        { throw new InvalidOperationException("TraceStoragePolicyPayloadTruncated", exception); }
        catch (DecoderFallbackException exception)
        { throw new InvalidOperationException("TraceStoragePolicyPayloadUtf8Invalid", exception); }
        catch (OverflowException exception)
        { throw new InvalidOperationException("TraceStoragePolicyPayloadInvalid", exception); }
    }

    internal static byte[] EncodeEventPayload(long position, TraceStoragePolicyPublication publication,
        string snapshotHash, long commandSequence, string commandHash, long identitySequence,
        string identityHash, DateTimeOffset recordedAtUtc, byte[] publicationBytes)
    {
        if (position <= 0 || commandSequence <= 0 || identitySequence <= 0 ||
            !AuditCanonical.IsHash(snapshotHash) || !AuditCanonical.IsHash(commandHash) ||
            !AuditCanonical.IsHash(identityHash) || recordedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("TraceStoragePolicyEventBindingInvalid");
        var publicationHash = Convert.ToHexString(SHA256.HashData(publicationBytes));
        return AuditCanonical.Encode("TraceStoragePolicyEvent", position.ToString(CultureInfo.InvariantCulture),
            publication.Version.ToString(CultureInfo.InvariantCulture), publication.ContentHash,
            snapshotHash, publication.OperationId.ToString("D"), publication.PrincipalId.ToString("D"),
            publication.SessionId.ToString("D"), commandSequence.ToString(CultureInfo.InvariantCulture),
            commandHash, identitySequence.ToString(CultureInfo.InvariantCulture), identityHash,
            recordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), publicationHash,
            Convert.ToBase64String(publicationBytes));
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private static BinaryWriter NewWriter(Stream stream) => new(stream, StrictUtf8, leaveOpen: true);

    private static void WriteGuid(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray());

    private static Guid ReadGuid(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16);
        if (bytes.Length != 16) throw new EndOfStreamException();
        var value = new Guid(bytes);
        if (value == Guid.Empty) throw new InvalidOperationException("TraceStoragePolicyGuidInvalid");
        return value;
    }

    private static void WriteText(BinaryWriter writer, string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        var bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length > MaximumTextBytes) throw new InvalidOperationException("TraceStoragePolicyTextTooLarge");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteNullableText(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null) WriteText(writer, value);
    }

    private static string ReadText(BinaryReader reader)
    {
        var bytes = ReadBytes(reader, MaximumTextBytes);
        return StrictUtf8.GetString(bytes);
    }

    private static string? ReadNullableText(BinaryReader reader) => reader.ReadBoolean() ? ReadText(reader) : null;

    private static void WriteBytes(BinaryWriter writer, byte[] bytes, int maximum)
    {
        if (bytes is null || bytes.Length > maximum) throw new InvalidOperationException("TraceStoragePolicyPayloadInvalid");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static byte[] ReadBytes(BinaryReader reader, int maximum)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > maximum) throw new InvalidOperationException("TraceStoragePolicyPayloadInvalid");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return bytes;
    }

    private static int ReadCount(BinaryReader reader, int maximum, string reason)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > maximum) throw new InvalidOperationException(reason);
        return count;
    }

    private static void WriteBacklog(BinaryWriter writer, TraceBacklogLimits value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.Write(value.MaximumItems);
        writer.Write(value.MaximumBytes);
        writer.Write(value.MaximumOldestAge.Ticks);
    }

    private static TraceBacklogLimits ReadBacklog(BinaryReader reader) => new(reader.ReadInt64(),
        reader.ReadInt64(), TimeSpan.FromTicks(reader.ReadInt64()));

    private static void WriteBudget(BinaryWriter writer, TraceStorageMaintenanceBudget value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.Write(value.Interval.Ticks);
        writer.Write(value.MaximumRunTime.Ticks);
        writer.Write(value.MaximumBytes);
        writer.Write(value.MaximumItems);
    }

    private static TraceStorageMaintenanceBudget ReadBudget(BinaryReader reader) =>
        new(TimeSpan.FromTicks(reader.ReadInt64()), TimeSpan.FromTicks(reader.ReadInt64()),
            reader.ReadInt64(), reader.ReadInt32());
}
