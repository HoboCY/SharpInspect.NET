using System.Globalization;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Canonical bounded representation of one schema-22 admission history row.
/// It stores the report and its attribution; it never interprets a report as
/// a qualification or authorization decision.
/// </summary>
internal static class ProductionAdmissionStorageCodec
{
    private const int Magic = 0x31504150; // PAP1
    private const byte FormatVersion = 1;
    private const int MaximumStringBytes = 16 * 1024;
    private const int MaximumHeads = 64;
    private const int MaximumReportGates = 32;
    private const int MaximumEvidenceHashes = 6;

    internal static byte[] Encode(ProductionAdmissionHistoryEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(value.Position);
            writer.Write((byte)value.Kind);
            WriteReport(writer, value.Report);
            WriteGuid(writer, value.CorrelationId);
            WriteGuid(writer, value.AttemptId);
            WriteGuid(writer, value.RuntimeEpoch);
            writer.Write(value.AdmissionGeneration);
            WriteGuid(writer, value.ActorPrincipalId);
            WriteGuid(writer, value.ActorSessionId);
            writer.Write(value.ActorAuthorizationRevision);
            WritePolicy(writer, value.AuthorizationPolicy);
            WriteNullableGuid(writer, value.StepUpGrantId);
            WriteHeads(writer, value.ExpectedDurableHeads);
            WriteHeads(writer, value.CurrentDurableHeads);
            WriteString(writer, value.AuthorizationTarget, 128);
            WriteString(writer, value.ReasonCode, MaximumStringBytes);
            WriteNullableInt64(writer, value.CommandAuditSequence);
            WriteString(writer, value.CommandAuditHash, 64);
            WriteNullableInt64(writer, value.AuthorizationAuditSequence);
            WriteString(writer, value.AuthorizationAuditHash, 64);
            writer.Write(value.AuditSequence);
            WriteString(writer, value.AuditHash, 64);
            WriteString(writer, value.PayloadHash, 64);
            WriteString(writer, value.ContentHash, 64);
        }
        if (stream.Length is < 1 or > ProductionAdmissionStoreOptions.MaximumPayloadBytesHardLimit)
            throw new InvalidOperationException("ProductionAdmissionPayloadCapacityExceeded");
        return stream.ToArray();
    }

    /// <summary>Canonical bytes bound into the central AuditEntryEnvelopeV18.</summary>
    internal static byte[] EncodeAuditBinding(ProductionAdmissionHistoryEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.CommandAuditSequence is not { } commandSequence ||
            value.CommandAuditHash is not { } commandHash ||
            value.AuthorizationAuditSequence is not { } authorizationSequence ||
            value.AuthorizationAuditHash is not { } authorizationHash)
            throw new InvalidOperationException("ProductionAdmissionAuditReferenceMissing");
        var fields = new List<string?>
        {
            "ProductionAdmissionLedgerEntryV1",
            Number(value.Position), value.Kind.ToString(), value.Report.ContentHash,
            value.CorrelationId.ToString("D"), value.AttemptId.ToString("D"), value.RuntimeEpoch.ToString("D"),
            Number(value.AdmissionGeneration), value.ActorPrincipalId.ToString("D"),
            value.ActorSessionId.ToString("D"), Number(value.ActorAuthorizationRevision),
            value.AuthorizationPolicy.Id, value.AuthorizationPolicy.Version,
            value.AuthorizationPolicy.ContentHash, value.StepUpGrantId?.ToString("D"),
            value.AuthorizationTarget, value.ReasonCode, Number(commandSequence), commandHash,
            Number(authorizationSequence), authorizationHash
        };
        AddHeads(fields, "expected-heads", value.ExpectedDurableHeads);
        AddHeads(fields, "current-heads", value.CurrentDurableHeads);
        return AuditCanonical.Encode("ProductionAdmissionLedgerEntryV1", fields.Skip(1).ToArray());
    }

    internal static ProductionAdmissionStorageValue Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length is < 1 or > ProductionAdmissionStoreOptions.MaximumPayloadBytesHardLimit)
            throw new InvalidOperationException("ProductionAdmissionPayloadCapacityExceeded");
        try
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
            if (reader.ReadInt32() != Magic || reader.ReadByte() != FormatVersion)
                throw new InvalidOperationException("ProductionAdmissionPayloadVersionUnsupported");
            var position = reader.ReadInt64();
            var kind = ReadEnum<ProductionAdmissionEventKind>(reader.ReadByte(),
                "ProductionAdmissionEventKindInvalid");
            var report = ReadReport(reader);
            var correlation = ReadGuid(reader);
            var attempt = ReadGuid(reader);
            var epoch = ReadGuid(reader);
            var generation = reader.ReadInt64();
            var actorPrincipal = ReadGuid(reader);
            var actorSession = ReadGuid(reader);
            var actorRevision = reader.ReadInt64();
            var policy = ReadPolicy(reader);
            var stepUp = ReadNullableGuid(reader);
            var expected = ReadHeads(reader);
            var current = ReadHeads(reader);
            var authorizationTarget = ReadString(reader, 128) ??
                throw new InvalidOperationException("ProductionAdmissionAuthorizationTargetMissing");
            var reason = ReadString(reader, MaximumStringBytes) ??
                throw new InvalidOperationException("ProductionAdmissionReasonMissing");
            var commandSequence = ReadNullableInt64(reader);
            var commandHash = ReadString(reader, 64);
            var authorizationSequence = ReadNullableInt64(reader);
            var authorizationHash = ReadString(reader, 64);
            var auditSequence = reader.ReadInt64();
            var auditHash = ReadString(reader, 64);
            var payloadHash = ReadString(reader, 64);
            var contentHash = ReadString(reader, 64) ??
                throw new InvalidOperationException("ProductionAdmissionContentHashMissing");
            var value = new ProductionAdmissionHistoryEvent(position, kind, report, correlation, attempt, epoch,
                generation, actorPrincipal, actorSession, actorRevision, policy, stepUp, expected, current,
                authorizationTarget, reason, commandSequence, commandHash, authorizationSequence,
                authorizationHash, auditSequence, auditHash, payloadHash, contentHash);
            if (stream.Position != stream.Length)
                throw new InvalidOperationException("ProductionAdmissionPayloadTrailingBytes");
            if (!payload.Span.SequenceEqual(Encode(value)))
                throw new InvalidOperationException("ProductionAdmissionPayloadCanonicalMismatch");
            return new ProductionAdmissionStorageValue(value, payload.ToArray());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not InvalidOperationException
            { Message: "ProductionAdmissionPayloadCapacityExceeded" })
        {
            throw new InvalidOperationException("ProductionAdmissionPayloadInvalid", exception);
        }
    }

    internal static string PayloadHash(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(payload));

    /// <summary>The unchanged schema-22 report bytes, reusable by the schema-32 arm ledger.</summary>
    internal static void WriteReport(BinaryWriter writer, ProductionAdmissionReport report)
    {
        WriteGuid(writer, report.RuntimeEpoch);
        writer.Write(report.SnapshotRevision);
        writer.Write(report.AdmissionGeneration);
        writer.Write(report.ObservedAtUtc.UtcDateTime.Ticks);
        WriteString(writer, report.PerformanceConfigurationFingerprint, 64);
        WriteString(writer, report.StationAcceptanceFingerprint, 64);
        WriteString(writer, report.FrameworkQualificationFingerprint, 64);
        WriteString(writer, report.ProviderQualificationFingerprint, 64);
        WriteString(writer, report.ProfileHash, 64);
        if (report.Gates.Count > MaximumReportGates) throw new InvalidOperationException("ProductionAdmissionGateSetInvalid");
        writer.Write(report.Gates.Count);
        foreach (var gate in report.Gates)
        {
            writer.Write((byte)gate.Gate);
            writer.Write((byte)gate.Status);
            WriteString(writer, gate.ReasonCode, MaximumStringBytes);
            WriteString(writer, gate.ExpectedFingerprint, 64);
            WriteString(writer, gate.ObservedFingerprint, 64);
            if (gate.EvidenceRecordHashes.Count > MaximumEvidenceHashes)
                throw new InvalidOperationException("ProductionAdmissionEvidenceRecordHashLimitExceeded");
            writer.Write(gate.EvidenceRecordHashes.Count);
            foreach (var hash in gate.EvidenceRecordHashes) WriteString(writer, hash, 64);
        }
        WriteString(writer, report.ContentHash, 64);
    }

    internal static ProductionAdmissionReport ReadReport(BinaryReader reader)
    {
        var epoch = ReadGuid(reader);
        var revision = reader.ReadInt64();
        var generation = reader.ReadInt64();
        var observedAt = new DateTimeOffset(new DateTime(reader.ReadInt64(), DateTimeKind.Utc));
        var performance = ReadString(reader, 64);
        var station = ReadString(reader, 64);
        var framework = ReadString(reader, 64);
        var provider = ReadString(reader, 64);
        var profile = ReadString(reader, 64);
        var count = reader.ReadInt32();
        if (count is < 1 or > MaximumReportGates) throw new InvalidOperationException("ProductionAdmissionGateSetInvalid");
        var gates = new List<ProductionAdmissionGateResult>(count);
        for (var index = 0; index < count; index++)
        {
            var gate = ReadEnum<ProductionAdmissionGate>(reader.ReadByte(), "ProductionAdmissionGateInvalid");
            var status = ReadEnum<ProductionAdmissionGateStatus>(reader.ReadByte(), "ProductionAdmissionGateStatusInvalid");
            var reason = ReadString(reader, MaximumStringBytes) ?? throw new InvalidOperationException("ProductionAdmissionReasonMissing");
            var expected = ReadString(reader, 64);
            var observed = ReadString(reader, 64);
            var evidenceCount = reader.ReadInt32();
            if (evidenceCount is < 0 or > MaximumEvidenceHashes)
                throw new InvalidOperationException("ProductionAdmissionEvidenceRecordHashLimitExceeded");
            var evidence = new List<string>(evidenceCount);
            for (var evidenceIndex = 0; evidenceIndex < evidenceCount; evidenceIndex++)
                evidence.Add(ReadString(reader, 64) ?? throw new InvalidOperationException("ProductionAdmissionEvidenceRecordHashInvalid"));
            gates.Add(new ProductionAdmissionGateResult(gate, status, reason, expected, observed,
                evidence.FirstOrDefault(), evidence.Skip(1)));
        }
        var contentHash = ReadString(reader, 64) ?? throw new InvalidOperationException("ProductionAdmissionContentHashMissing");
        var report = new ProductionAdmissionReport(epoch, revision, generation, observedAt,
            performance, station, framework, provider, profile, gates);
        if (!string.Equals(report.ContentHash, contentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("ProductionAdmissionReportContentHashMismatch");
        return report;
    }

    private static void WritePolicy(BinaryWriter writer, RecipeContractReference policy)
    {
        WriteString(writer, policy.Id, 128);
        WriteString(writer, policy.Version, 128);
        WriteString(writer, policy.ContentHash, 64);
    }

    private static RecipeContractReference ReadPolicy(BinaryReader reader) => new(
        ReadString(reader, 128) ?? throw new InvalidOperationException("ProductionAdmissionPolicyMissing"),
        ReadString(reader, 128) ?? throw new InvalidOperationException("ProductionAdmissionPolicyMissing"),
        ReadString(reader, 64) ?? throw new InvalidOperationException("ProductionAdmissionPolicyMissing"));

    private static void WriteHeads(BinaryWriter writer, IReadOnlyDictionary<string, string> heads)
    {
        if (heads.Count > MaximumHeads) throw new InvalidOperationException("ProductionAdmissionDurableHeadLimitExceeded");
        writer.Write(heads.Count);
        foreach (var pair in heads) { WriteString(writer, pair.Key, 128); WriteString(writer, pair.Value, 64); }
    }

    private static ReadOnlyDictionary<string, string> ReadHeads(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count is < 0 or > MaximumHeads) throw new InvalidOperationException("ProductionAdmissionDurableHeadLimitExceeded");
        var copied = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var key = ReadString(reader, 128) ?? throw new InvalidOperationException("ProductionAdmissionDurableHeadInvalid");
            var hash = ReadString(reader, 64) ?? throw new InvalidOperationException("ProductionAdmissionDurableHeadInvalid");
            copied.Add(key, hash);
        }
        return new ReadOnlyDictionary<string, string>(copied);
    }

    private static void AddHeads(List<string?> fields, string prefix, IReadOnlyDictionary<string, string> heads)
    {
        fields.Add(prefix);
        foreach (var pair in heads) fields.AddRange(new[] { pair.Key, pair.Value });
    }

    private static void WriteGuid(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray());
    private static void WriteNullableGuid(BinaryWriter writer, Guid? value)
    { writer.Write(value.HasValue); if (value is { } guid) WriteGuid(writer, guid); }
    private static Guid ReadGuid(BinaryReader reader) => new(reader.ReadBytes(16));
    private static Guid? ReadNullableGuid(BinaryReader reader) => reader.ReadBoolean() ? ReadGuid(reader) : null;
    private static void WriteNullableInt64(BinaryWriter writer, long? value)
    { writer.Write(value.HasValue); if (value is { } number) writer.Write(number); }
    private static long? ReadNullableInt64(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadInt64() : null;

    private static void WriteString(BinaryWriter writer, string? value, int maxBytes)
    {
        writer.Write(value is not null);
        if (value is null) return;
        var bytes = new UTF8Encoding(false, true).GetBytes(value);
        if (bytes.Length > maxBytes) throw new InvalidOperationException("ProductionAdmissionFieldCapacityExceeded");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string? ReadString(BinaryReader reader, int maxBytes)
    {
        if (!reader.ReadBoolean()) return null;
        var length = reader.ReadInt32();
        if (length is < 0 or > 16 * 1024 * 1024 || length > maxBytes)
            throw new InvalidOperationException("ProductionAdmissionFieldCapacityExceeded");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new InvalidOperationException("ProductionAdmissionPayloadTruncated");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static T ReadEnum<T>(byte value, string reason) where T : struct, Enum =>
        Enum.IsDefined(typeof(T), value) ? (T)Enum.ToObject(typeof(T), value) :
        throw new InvalidOperationException(reason);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

internal sealed record ProductionAdmissionStorageValue(ProductionAdmissionHistoryEvent Event, byte[] Payload);
