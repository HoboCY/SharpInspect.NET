using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Production;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// The production ledger has its own wire domain and uses only the explicitly
/// production-scoped structured result codec.
/// </summary>
internal static class ProductionInspectionStorageCodec
{
    internal const int MaximumStringBytes = 4 * 1024 * 1024;
    private const int LegacyEnvelopeVersion = 1;
    private const int PartIdentityEnvelopeVersion = 2;
    private const int MaximumEnvelopeBytes = 16 * 1024 * 1024;
    private const int MaximumSegments = 2048;
    private const int MaximumObligations = 64;
    private static readonly byte[] EnvelopeMagic = Encoding.ASCII.GetBytes("SI-PROD-CORE");

    internal static byte[] EncodeAdmission(ProductionInspectionAdmission value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var fields = new List<string?>
        {
            value.PartIdentityEvidence is null ? "ProductionInspectionAdmissionV1" :
                "ProductionInspectionAdmissionV2", value.InspectionId.ToString("D"),
        value.CorrelationId.ToString("D"), value.RuntimeEpoch.ToString("D"), value.StationId,
        value.AdmissionGeneration.ToString(CultureInfo.InvariantCulture),
        value.ControllerCycle.ControllerEpoch.ToString(CultureInfo.InvariantCulture),
        value.ControllerCycle.CycleSequence.ToString(CultureInfo.InvariantCulture),
        value.EvidenceRequirement.ToString(), value.ActivationReference.Position.ToString(CultureInfo.InvariantCulture),
        value.ActivationReference.ActivationId.ToString("D"), value.ActivationReference.ContentHash,
        value.ActivationSnapshot.ContentHash, value.EndpointBindingHash, value.PlcProfileHash,
        value.PlcPolicyHash, value.ConnectionGeneration.ToString(CultureInfo.InvariantCulture),
        value.ConnectionAttempt.ToString(CultureInfo.InvariantCulture),
        value.AcceptedAtUtc.ToString("O", CultureInfo.InvariantCulture),
        value.AcceptedMonotonicTimestamp.ToString(CultureInfo.InvariantCulture),
        value.TracePolicySnapshot.Version.ToString(CultureInfo.InvariantCulture),
        value.TracePolicySnapshot.ContentHash,
        value.RetentionObligations.Count.ToString(CultureInfo.InvariantCulture),
        string.Join("\n", value.RetentionObligations.Select(item => item.ContentHash)),
        value.ContentHash
        };
        if (value.PartIdentityEvidence is { } evidence)
            fields.Add(evidence.ContentHash);
        return AuditCanonical.Encode(fields[0]!, fields.Skip(1).ToArray());
    }

    internal static byte[] EncodeCore(ProductionInspectionCore value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var fields = new List<string?>
        {
        value.Admission.PartIdentityEvidence is null ? "ProductionInspectionCoreV1" :
            "ProductionInspectionCoreV2", value.Admission.InspectionId.ToString("D"),
        value.Admission.ContentHash, value.State.ToString(), value.ExecutionStatus.ToString(),
        value.Decision.ToString(), value.ReasonCode, value.AcquisitionFailureKind?.ToString(),
        value.AcquisitionFailureReasonCode, FrameHash(value.FrameMetadata),
        ProvenanceHash(value.FrameProvenance), value.PreparedAlgorithmInstanceId.ToString("D"),
        value.Algorithm?.Id, value.Algorithm?.Version, value.Configuration?.ContentHash,
        value.ResultSchema?.ContentHash, value.StructuredResultHash, value.Overlay?.ContentHash,
        TimingHash(value.Timing), value.PlcPayload?.ContentHash, value.StructuredResultHash,
        value.PartIdentity, value.CommittedAtUtc.ToString("O", CultureInfo.InvariantCulture),
        value.CommittedMonotonicTimestamp.ToString(CultureInfo.InvariantCulture),
        value.ExecutionAdmittedMonotonicTimestamp?.ToString(CultureInfo.InvariantCulture),
        value.ExecutionMonotonicFrequency?.ToString(CultureInfo.InvariantCulture),
        AcquisitionStartHash(value.AcquisitionStart),
        value.RetentionObligations.Count.ToString(CultureInfo.InvariantCulture),
        string.Join("\n", value.RetentionObligations.Select(item => item.ContentHash)),
        value.ContentHash
        };
        if (value.Admission.PartIdentityEvidence is { } evidence)
            fields.Add(evidence.ContentHash);
        return AuditCanonical.Encode(fields[0]!, fields.Skip(1).ToArray());
    }

    /// <summary>Central audit payload excludes final audit sequence/hash to avoid self-binding.</summary>
    internal static byte[] EncodeAuditBinding(ProductionInspectionHistoryEvent value) =>
        AuditCanonical.Encode("ProductionInspectionEventAuditV1",
            value.Position.ToString(CultureInfo.InvariantCulture), value.Kind.ToString(),
            value.InspectionId.ToString("D"), value.CorrelationId.ToString("D"),
            value.RuntimeEpoch.ToString("D"), value.Admission.ContentHash,
            value.Core?.ContentHash, value.ReasonCode,
            value.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            value.MonotonicTimestamp.ToString(CultureInfo.InvariantCulture));

    internal static byte[] Encode(ProductionInspectionHistoryEvent value) =>
        EncodeEventEnvelope(value);

    internal static string PayloadHash(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload));

    /// <summary>
    /// Durable production values use a strict binary envelope.  The central audit
    /// binding above remains a small canonical value and deliberately excludes the
    /// final audit reference; these envelopes are the reconstructable row payloads.
    /// </summary>
    internal static byte[] EncodeAdmissionEnvelope(ProductionInspectionAdmission value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        var version = value.PartIdentityEvidence is null ? LegacyEnvelopeVersion : PartIdentityEnvelopeVersion;
        using (var writer = NewWriter(stream, EnvelopeKind.Admission, version))
        {
            WriteGuid(writer, value.InspectionId);
            WriteGuid(writer, value.CorrelationId);
            WriteGuid(writer, value.RuntimeEpoch);
            WriteString(writer, value.StationId, 256);
            writer.Write(value.AdmissionGeneration);
            writer.Write(value.ControllerCycle.ControllerEpoch);
            writer.Write(value.ControllerCycle.CycleSequence);
            writer.Write((byte)value.EvidenceRequirement);
            if (version >= PartIdentityEnvelopeVersion)
                WritePartIdentityEvidence(writer, value.PartIdentityEvidence);
            writer.Write(value.ActivationReference.Position);
            WriteGuid(writer, value.ActivationReference.ActivationId);
            WriteString(writer, value.ActivationReference.ContentHash, 64);
            WriteBytes(writer, RecipeActivationStorageCodec.EncodeSnapshot(value.ActivationSnapshot),
                MaximumEnvelopeBytes);
            WriteBytes(writer, TraceStoragePolicyStorageCodec.EncodePublication(
                value.TracePolicySnapshot.Publication), MaximumEnvelopeBytes);
            WriteString(writer, value.EndpointBindingHash, 64);
            WriteString(writer, value.PlcProfileHash, 64);
            WriteString(writer, value.PlcPolicyHash, 64);
            writer.Write(value.ConnectionGeneration);
            writer.Write(value.ConnectionAttempt);
            writer.Write(value.AcceptedAtUtc.UtcTicks);
            writer.Write(value.AcceptedMonotonicTimestamp);
            WriteObligations(writer, value.RetentionObligations);
            WriteString(writer, value.ContentHash, 64);
        }
        return FinishEnvelope(stream);
    }

    internal static ProductionInspectionAdmission DecodeAdmissionEnvelope(
        ReadOnlyMemory<byte> payload,
        Func<CalibrationProfileReference, PublishedCalibrationProfileVersion?>? calibrationResolver = null)
    {
        using var reader = OpenReader(payload, EnvelopeKind.Admission, out var stream,
            out var envelopeVersion);
        var inspectionId = ReadGuid(reader);
        var correlationId = ReadGuid(reader);
        var runtimeEpoch = ReadGuid(reader);
        var stationId = ReadString(reader, 256) ?? throw Corrupt("ProductionInspectionStationMissing");
        var admissionGeneration = reader.ReadInt64();
        var controllerEpoch = reader.ReadUInt32();
        var cycleSequence = reader.ReadUInt32();
        var evidence = ReadEnum<ProductionEvidenceRequirement>(reader.ReadByte(),
            "ProductionInspectionEvidenceInvalid");
        var partIdentityEvidence = envelopeVersion >= PartIdentityEnvelopeVersion
            ? ReadPartIdentityEvidence(reader) : null;
        var activationPosition = reader.ReadInt64();
        var activationId = ReadGuid(reader);
        var activationHash = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionActivationHashMissing");
        var activation = RecipeActivationStorageCodec.DecodeSnapshot(
            ReadBytes(reader, MaximumEnvelopeBytes), calibrationResolver);
        var publication = TraceStoragePolicyStorageCodec.DecodePublication(
            ReadBytes(reader, MaximumEnvelopeBytes));
        var policySnapshot = new TraceStoragePolicySnapshot(publication);
        var endpointHash = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionEndpointHashMissing");
        var profileHash = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionProfileHashMissing");
        var policyHash = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionPolicyHashMissing");
        var connectionGeneration = reader.ReadInt64();
        var connectionAttempt = reader.ReadInt32();
        var acceptedAt = ReadUtc(reader, "ProductionInspectionAcceptedAtInvalid");
        var acceptedMonotonic = reader.ReadInt64();
        var obligations = ReadObligations(reader, policySnapshot);
        var expectedHash = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionAdmissionHashMissing");
        RequireEnd(stream, "ProductionInspectionAdmissionTrailingBytes");
        var value = new ProductionInspectionAdmission(inspectionId, correlationId, runtimeEpoch,
            stationId, admissionGeneration, new PlcControllerCycle(controllerEpoch, cycleSequence),
            evidence, new RecipeActivationReference(activationPosition, activationId, activationHash),
            activation, endpointHash, profileHash, policyHash, connectionGeneration,
            connectionAttempt, acceptedAt, acceptedMonotonic, policySnapshot, obligations,
            partIdentityEvidence);
        RequireHash(value.ContentHash, expectedHash, "ProductionInspectionAdmissionHashMismatch");
        return value;
    }

    internal static byte[] EncodeCoreEnvelope(ProductionInspectionCore value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        var version = value.Admission.PartIdentityEvidence is null ? LegacyEnvelopeVersion : PartIdentityEnvelopeVersion;
        using (var writer = NewWriter(stream, EnvelopeKind.Core, version))
        {
            WriteString(writer, value.Admission.ContentHash, 64);
            writer.Write((byte)value.State);
            writer.Write((byte)value.ExecutionStatus);
            writer.Write((byte)value.Decision);
            WriteString(writer, value.ReasonCode, 256);
            writer.Write(value.AcquisitionFailureKind.HasValue);
            if (value.AcquisitionFailureKind is { } failureKind)
                writer.Write((byte)failureKind);
            WriteNullableString(writer, value.AcquisitionFailureReasonCode, 256);
            WriteGuid(writer, value.PreparedAlgorithmInstanceId);
            WriteNullableString(writer, value.ResultSchema is null ? null :
                AlgorithmResultStorageCodec.EncodeProductionSchema(value.ResultSchema), MaximumStringBytes);
            WriteNullableString(writer, value.Timing is null ? null :
                AlgorithmResultStorageCodec.EncodeProductionTiming(value.Timing), 16384);
            WriteString(writer, value.StructuredResultJson, MaximumStringBytes);
            WriteNullableString(writer, value.StructuredResultHash, 64);
            WriteFrameAcquisitionStart(writer, value.AcquisitionStart);
            WriteFrameMetadata(writer, value.FrameMetadata);
            WriteFrameProvenance(writer, value.FrameProvenance);
            WritePlcPayload(writer, value.PlcPayload);
            WriteObligations(writer, value.RetentionObligations);
            writer.Write(value.CommittedAtUtc.UtcTicks);
            writer.Write(value.CommittedMonotonicTimestamp);
            writer.Write(value.ExecutionAdmittedMonotonicTimestamp.HasValue);
            if (value.ExecutionAdmittedMonotonicTimestamp is { } admitted)
            {
                writer.Write(admitted);
                writer.Write(value.ExecutionMonotonicFrequency!.Value);
            }
            WriteString(writer, value.ContentHash, 64);
        }
        return FinishEnvelope(stream);
    }

    internal static ProductionInspectionCore DecodeCoreEnvelope(
        ReadOnlyMemory<byte> payload, ProductionInspectionAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        using var reader = OpenReader(payload, EnvelopeKind.Core, out var stream, out _);
        var admissionHash = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionAdmissionHashMissing");
        RequireHash(admission.ContentHash, admissionHash, "ProductionInspectionAdmissionMissing");
        var state = ReadEnum<ProductionInspectionState>(reader.ReadByte(), "ProductionInspectionStateInvalid");
        var executionStatus = ReadEnum<ExecutionStatus>(reader.ReadByte(), "ProductionInspectionExecutionStatusInvalid");
        var decision = ReadEnum<InspectionDecision>(reader.ReadByte(), "ProductionInspectionDecisionInvalid");
        var reason = ReadString(reader, 256) ?? throw Corrupt("ProductionInspectionReasonMissing");
        CameraAcquisitionFailureKind? failureKind = null;
        if (reader.ReadBoolean())
            failureKind = ReadEnum<CameraAcquisitionFailureKind>(reader.ReadByte(),
                "ProductionInspectionAcquisitionFailureKindInvalid");
        var failureReason = ReadNullableString(reader, 256);
        var prepared = ReadGuid(reader);
        var schemaJson = ReadNullableString(reader, MaximumStringBytes);
        var timingJson = ReadNullableString(reader, 16384);
        var structuredJson = ReadNullableString(reader, MaximumStringBytes);
        var structuredHash = ReadNullableString(reader, 64);
        var acquisitionStart = ReadFrameAcquisitionStart(reader);
        var frame = ReadFrameMetadata(reader);
        var provenance = ReadFrameProvenance(reader);
        var plc = ReadPlcPayload(reader, admission);
        var obligations = ReadObligations(reader, admission.TracePolicySnapshot);
        var committedAt = ReadUtc(reader, "ProductionInspectionCommittedAtInvalid");
        var committedMonotonic = reader.ReadInt64();
        long? executionAdmitted = null;
        long? executionFrequency = null;
        if (reader.ReadBoolean())
        {
            executionAdmitted = reader.ReadInt64();
            executionFrequency = reader.ReadInt64();
        }
        var expectedHash = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionCoreHashMissing");
        RequireEnd(stream, "ProductionInspectionCoreTrailingBytes");

        var content = admission.ActivationSnapshot.Release.Source.Content;
        var algorithm = content.Algorithm.Algorithm;
        var configuration = content.Configuration;
        var resultSchema = schemaJson is null ? null : AlgorithmResultStorageCodec.DecodeProductionSchema(schemaJson);
        AlgorithmResult? result = null;
        AlgorithmExecutionTimingSnapshot? timing = timingJson is null ? null : AlgorithmResultStorageCodec.DecodeProductionTiming(timingJson);
        FrameOverlaySnapshot? overlay = null;
        if (structuredJson is not null)
        {
            if (structuredHash is null)
                throw Corrupt("ProductionInspectionStructuredResultHashMissing");
            var document = ProductionAlgorithmResultCodec.Decode(admission.InspectionId,
                structuredJson, structuredHash);
            if (frame is null || !string.Equals(FrameHash(frame), FrameHash(document.FrameMetadata),
                    StringComparison.Ordinal))
                throw Corrupt("ProductionInspectionStructuredFrameMismatch");
            algorithm = document.Algorithm;
            if (resultSchema?.ContentHash != document.ResultSchema.ContentHash || timingJson is null ||
                AlgorithmResultStorageCodec.EncodeProductionTiming(document.Timing) != timingJson)
                throw Corrupt("ProductionInspectionStructuredContractMismatch");
            resultSchema = document.ResultSchema;
            result = document.Result;
            timing = document.Timing;
            if (!string.Equals(configuration.ContentHash, document.ConfigurationContentHash,
                    StringComparison.Ordinal))
                throw Corrupt("ProductionInspectionStructuredConfigurationMismatch");
            overlay = new FrameOverlaySnapshot(admission.InspectionId, frame, resultSchema,
                result.OverlaySet, structuredHash);
        }

        var value = new ProductionInspectionCore(admission, state, executionStatus, decision,
            reason, failureKind, failureReason, frame, provenance, prepared, algorithm,
            configuration, resultSchema, result, overlay, timing, plc, structuredJson,
            structuredHash, admission.PartIdentityEvidence?.Value, committedAt, committedMonotonic,
            obligations, acquisitionStart,
            executionAdmitted, executionFrequency);
        RequireHash(value.ContentHash, expectedHash, "ProductionInspectionCoreHashMismatch");
        return value;
    }

    internal static byte[] EncodeEventEnvelope(ProductionInspectionHistoryEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        var version = value.Admission.PartIdentityEvidence is null ? LegacyEnvelopeVersion : PartIdentityEnvelopeVersion;
        using (var writer = NewWriter(stream, EnvelopeKind.Event, version))
        {
            writer.Write(value.Position);
            writer.Write((byte)value.Kind);
            WriteString(writer, value.ReasonCode, 256);
            writer.Write(value.RecordedAtUtc.UtcTicks);
            writer.Write(value.MonotonicTimestamp);
            writer.Write(value.AuditSequence);
            WriteNullableString(writer, value.AuditHash, 64);
            WriteBytes(writer, EncodeAdmissionEnvelope(value.Admission), MaximumEnvelopeBytes);
            writer.Write(value.Core is not null);
            if (value.Core is not null)
                WriteBytes(writer, EncodeCoreEnvelope(value.Core), MaximumEnvelopeBytes);
            WriteString(writer, value.ContentHash, 64);
        }
        return FinishEnvelope(stream);
    }

    internal static ProductionInspectionHistoryEvent DecodeEventEnvelope(
        ReadOnlyMemory<byte> payload,
        Func<CalibrationProfileReference, PublishedCalibrationProfileVersion?>? calibrationResolver = null)
    {
        using var reader = OpenReader(payload, EnvelopeKind.Event, out var stream, out _);
        var position = reader.ReadInt64();
        var kind = ReadEnum<ProductionInspectionEventKind>(reader.ReadByte(),
            "ProductionInspectionEventKindInvalid");
        var reason = ReadString(reader, 256) ?? throw Corrupt("ProductionInspectionReasonMissing");
        var recorded = ReadUtc(reader, "ProductionInspectionRecordedAtInvalid");
        var monotonic = reader.ReadInt64();
        var auditSequence = reader.ReadInt64();
        var auditHash = ReadNullableString(reader, 64);
        var admission = DecodeAdmissionEnvelope(ReadBytes(reader, MaximumEnvelopeBytes), calibrationResolver);
        ProductionInspectionCore? core = null;
        if (reader.ReadBoolean())
            core = DecodeCoreEnvelope(ReadBytes(reader, MaximumEnvelopeBytes), admission);
        var expectedHash = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionEventHashMissing");
        RequireEnd(stream, "ProductionInspectionEventTrailingBytes");
        var value = new ProductionInspectionHistoryEvent(position, kind, admission, core, reason,
            recorded, monotonic, auditSequence, auditHash);
        RequireHash(value.ContentHash, expectedHash, "ProductionInspectionEventHashMismatch");
        return value;
    }

    private enum EnvelopeKind : byte { Admission = 1, Core = 2, Event = 3 }

    private static BinaryWriter NewWriter(Stream stream, EnvelopeKind kind) =>
        NewWriter(stream, kind, LegacyEnvelopeVersion);

    private static BinaryWriter NewWriter(Stream stream, EnvelopeKind kind, int version)
    {
        var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true);
        writer.Write(EnvelopeMagic);
        writer.Write(version);
        writer.Write((byte)kind);
        return writer;
    }

    private static byte[] FinishEnvelope(MemoryStream stream)
    {
        if (stream.Length is <= 0 or > MaximumEnvelopeBytes)
            throw new InvalidOperationException("ProductionInspectionPayloadCapacityExceeded");
        return stream.ToArray();
    }

    private static BinaryReader OpenReader(ReadOnlyMemory<byte> payload, EnvelopeKind kind,
        out MemoryStream stream)
    {
        return OpenReader(payload, kind, out stream, out _);
    }

    private static BinaryReader OpenReader(ReadOnlyMemory<byte> payload, EnvelopeKind kind,
        out MemoryStream stream, out int version)
    {
        if (payload.Length is <= 0 or > MaximumEnvelopeBytes)
            throw Corrupt("ProductionInspectionPayloadCapacityExceeded");
        stream = new MemoryStream(payload.ToArray(), writable: false);
        var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        var magic = reader.ReadBytes(EnvelopeMagic.Length);
        version = reader.ReadInt32();
        if (!magic.AsSpan().SequenceEqual(EnvelopeMagic) ||
            version is not (LegacyEnvelopeVersion or PartIdentityEnvelopeVersion) ||
            reader.ReadByte() != (byte)kind)
            throw Corrupt("ProductionInspectionEnvelopeVersionInvalid");
        return reader;
    }

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private static void RequireEnd(Stream stream, string reason)
    {
        if (stream.Position != stream.Length) throw Corrupt(reason);
    }

    private static InvalidOperationException Corrupt(string reason) =>
        new(reason);

    private static void RequireHash(string actual, string expected, string reason)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw Corrupt(reason);
    }

    private static void WriteGuid(BinaryWriter writer, Guid value)
    {
        if (value == Guid.Empty) throw Corrupt("ProductionInspectionGuidInvalid");
        writer.Write(value.ToByteArray());
    }

    private static Guid ReadGuid(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16);
        if (bytes.Length != 16) throw Corrupt("ProductionInspectionGuidInvalid");
        var value = new Guid(bytes);
        if (value == Guid.Empty) throw Corrupt("ProductionInspectionGuidInvalid");
        return value;
    }

    private static void WriteString(BinaryWriter writer, string? value, int maximumBytes)
    {
        if (value is null)
        {
            writer.Write(-1);
            return;
        }
        var bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length > maximumBytes) throw Corrupt("ProductionInspectionStringCapacityExceeded");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteNullableString(BinaryWriter writer, string? value, int maximumBytes) =>
        WriteString(writer, value, maximumBytes);

    private static string? ReadString(BinaryReader reader, int maximumBytes)
    {
        var length = reader.ReadInt32();
        if (length == -1) return null;
        if (length < 0 || length > maximumBytes) throw Corrupt("ProductionInspectionStringCapacityExceeded");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw Corrupt("ProductionInspectionPayloadTruncated");
        return StrictUtf8.GetString(bytes);
    }

    private static string? ReadNullableString(BinaryReader reader, int maximumBytes) =>
        ReadString(reader, maximumBytes);

    private static void WriteBytes(BinaryWriter writer, byte[] value, int maximumBytes)
    {
        if (value is null || value.Length <= 0 || value.Length > maximumBytes)
            throw Corrupt("ProductionInspectionPayloadCapacityExceeded");
        writer.Write(value.Length);
        writer.Write(value);
    }

    private static byte[] ReadBytes(BinaryReader reader, int maximumBytes)
    {
        var length = reader.ReadInt32();
        if (length <= 0 || length > maximumBytes) throw Corrupt("ProductionInspectionPayloadCapacityExceeded");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw Corrupt("ProductionInspectionPayloadTruncated");
        return bytes;
    }

    private static void WritePossiblyEmptyBytes(BinaryWriter writer, byte[] value, int maximumBytes)
    {
        if (value is null || value.Length > maximumBytes)
            throw Corrupt("ProductionInspectionPayloadCapacityExceeded");
        writer.Write(value.Length);
        writer.Write(value);
    }

    private static byte[] ReadPossiblyEmptyBytes(BinaryReader reader, int maximumBytes)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > maximumBytes)
            throw Corrupt("ProductionInspectionPayloadCapacityExceeded");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw Corrupt("ProductionInspectionPayloadTruncated");
        return bytes;
    }

    private static void WriteNullableGuid(BinaryWriter writer, Guid? value)
    {
        writer.Write(value.HasValue);
        if (value is { } guid) WriteGuid(writer, guid);
    }

    private static Guid? ReadNullableGuid(BinaryReader reader) =>
        reader.ReadBoolean() ? ReadGuid(reader) : null;

    private static DateTimeOffset ReadUtc(BinaryReader reader, string reason)
    {
        try { return new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero); }
        catch (ArgumentOutOfRangeException exception) { throw new InvalidOperationException(reason, exception); }
    }

    private static T ReadEnum<T>(byte value, string reason) where T : struct, Enum
    {
        var typed = (T)Enum.ToObject(typeof(T), value);
        return Enum.IsDefined(typeof(T), typed) ? typed : throw Corrupt(reason);
    }

    private static void WritePartIdentityEvidence(BinaryWriter writer,
        PartIdentityEvidence? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        writer.Write((byte)value.State);
        WriteNullableString(writer, value.Value, 4096);
        WritePartIdentityObservation(writer, value.Observation);
        WriteString(writer, value.ContentHash, 64);
    }

    private static PartIdentityEvidence? ReadPartIdentityEvidence(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        var state = ReadEnum<PartIdentityEvidenceState>(reader.ReadByte(),
            "ProductionInspectionPartIdentityEvidenceStateInvalid");
        var value = ReadNullableString(reader, 4096);
        var observation = ReadPartIdentityObservation(reader);
        var evidence = new PartIdentityEvidence(state, value, observation);
        var expectedHash = ReadString(reader, 64) ??
            throw Corrupt("ProductionInspectionPartIdentityEvidenceHashMissing");
        RequireHash(evidence.ContentHash, expectedHash,
            "ProductionInspectionPartIdentityEvidenceHashMismatch");
        return evidence;
    }

    private static void WritePartIdentityBinding(BinaryWriter writer,
        PartIdentityProviderBinding value)
    {
        WriteString(writer, value.BindingId, 128);
        WriteString(writer, value.BindingVersion, 64);
        WriteString(writer, value.LogicalRole, 128);
        writer.Write((byte)value.SourceKind);
        WriteString(writer, value.ProviderId, 128);
        WriteString(writer, value.ProviderVersion, 64);
        WriteString(writer, value.SourceContractHash, 64);
        WriteString(writer, value.Format.Id, 128);
        WriteString(writer, value.Format.Version, 64);
        writer.Write(value.Format.MinimumLength);
        writer.Write(value.Format.MaximumLength);
        WriteString(writer, value.Format.AllowedCharacters, 256);
        WriteNullableString(writer, value.Format.RequiredPrefix, 256);
        WriteNullableString(writer, value.Format.RequiredSuffix, 256);
        writer.Write(value.FreshnessLimit.Ticks);
        writer.Write(value.LatchTimeout.Ticks);
        writer.Write(value.MaximumCallsPerCycle);
    }

    private static PartIdentityProviderBinding ReadPartIdentityBinding(BinaryReader reader)
    {
        var bindingId = ReadString(reader, 128) ?? throw Corrupt("ProductionInspectionPartIdentityBindingMissing");
        var bindingVersion = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionPartIdentityBindingVersionMissing");
        var logicalRole = ReadString(reader, 128) ?? throw Corrupt("ProductionInspectionPartIdentityRoleMissing");
        var sourceKind = ReadEnum<PartIdentityProviderSourceKind>(reader.ReadByte(),
            "ProductionInspectionPartIdentitySourceInvalid");
        var providerId = ReadString(reader, 128) ?? throw Corrupt("ProductionInspectionPartIdentityProviderMissing");
        var providerVersion = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionPartIdentityProviderVersionMissing");
        var sourceContract = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionPartIdentitySourceContractMissing");
        var format = new PartIdentityFormat(
            ReadString(reader, 128) ?? throw Corrupt("ProductionInspectionPartIdentityFormatMissing"),
            ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionPartIdentityFormatVersionMissing"),
            reader.ReadInt32(), reader.ReadInt32(),
            ReadString(reader, 256) ?? throw Corrupt("ProductionInspectionPartIdentityAllowedCharactersMissing"),
            ReadNullableString(reader, 256), ReadNullableString(reader, 256));
        return new PartIdentityProviderBinding(bindingId, bindingVersion, logicalRole, sourceKind,
            providerId, providerVersion, sourceContract, format,
            TimeSpan.FromTicks(reader.ReadInt64()), TimeSpan.FromTicks(reader.ReadInt64()),
            reader.ReadInt32());
    }

    private static void WritePartIdentityCycle(BinaryWriter writer,
        PartIdentityCycleBinding value)
    {
        WriteGuid(writer, value.RuntimeEpoch);
        WriteString(writer, value.EndpointBindingHash, 64);
        writer.Write(value.ConnectionGeneration);
        writer.Write(value.ControllerEpoch);
        writer.Write(value.CycleSequence);
    }

    private static PartIdentityCycleBinding ReadPartIdentityCycle(BinaryReader reader) =>
        new(ReadGuid(reader), ReadString(reader, 64) ??
            throw Corrupt("ProductionInspectionPartIdentityEndpointMissing"),
            reader.ReadInt64(), reader.ReadUInt32(), reader.ReadUInt32());

    private static void WritePartIdentityObservation(BinaryWriter writer,
        PartIdentityProviderObservation value)
    {
        WritePartIdentityBinding(writer, value.Binding);
        WritePartIdentityCycle(writer, value.Cycle);
        writer.Write((byte)value.Status);
        WriteNullableString(writer, value.Value, 4096);
        WriteString(writer, value.ReasonCode, 256);
        writer.Write(value.SourceSequence);
        writer.Write(value.ObservedAtUtc.UtcTicks);
        writer.Write(value.MonotonicTimestamp);
        writer.Write(value.MonotonicFrequency);
        WriteGuid(writer, value.SourceEpoch);
        writer.Write(value.SourceGeneration);
        WriteNullableGuid(writer, value.StageToken);
        writer.Write(value.StablePlcSnapshot is not null);
        if (value.StablePlcSnapshot is { } stable)
            WriteStablePartIdentitySnapshot(writer, stable);
        WriteString(writer, value.ContentHash, 64);
    }

    private static PartIdentityProviderObservation ReadPartIdentityObservation(BinaryReader reader)
    {
        var binding = ReadPartIdentityBinding(reader);
        var cycle = ReadPartIdentityCycle(reader);
        var status = ReadEnum<PartIdentityObservationStatus>(reader.ReadByte(),
            "ProductionInspectionPartIdentityObservationStatusInvalid");
        var value = ReadNullableString(reader, 4096);
        var reason = ReadString(reader, 256) ??
            throw Corrupt("ProductionInspectionPartIdentityObservationReasonMissing");
        var sequence = reader.ReadInt64();
        var observedAt = ReadUtc(reader, "ProductionInspectionPartIdentityObservationTimestampInvalid");
        var monotonic = reader.ReadInt64();
        var frequency = reader.ReadInt64();
        var sourceEpoch = ReadGuid(reader);
        var sourceGeneration = reader.ReadInt64();
        var stageToken = ReadNullableGuid(reader);
        var stable = reader.ReadBoolean() ? ReadStablePartIdentitySnapshot(reader, cycle) : null;
        var observation = new PartIdentityProviderObservation(binding, cycle, status, value, reason,
            sequence, observedAt, monotonic, frequency, sourceEpoch, sourceGeneration,
            stageToken, stable);
        RequireHash(observation.ContentHash,
            ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionPartIdentityObservationHashMissing"),
            "ProductionInspectionPartIdentityObservationHashMismatch");
        return observation;
    }

    private static void WriteStablePartIdentitySnapshot(BinaryWriter writer,
        PartIdentityStablePlcSnapshot value)
    {
        WriteString(writer, value.SourceContractHash, 64);
        writer.Write(value.Revision);
        writer.Write(value.State);
        writer.Write((byte)value.Status);
        WritePossiblyEmptyBytes(writer, value.GetRawUtf8Bytes(), 4096);
        writer.Write(value.SourceSequence);
        writer.Write(value.ObservedAtUtc.UtcTicks);
        writer.Write(value.MonotonicTimestamp);
        writer.Write(value.MonotonicFrequency);
        WriteString(writer, value.ReadEvidenceHash, 64);
        WriteString(writer, value.ContentHash, 64);
    }

    private static PartIdentityStablePlcSnapshot ReadStablePartIdentitySnapshot(
        BinaryReader reader, PartIdentityCycleBinding cycle)
    {
        var sourceContract = ReadString(reader, 64) ??
            throw Corrupt("ProductionInspectionPartIdentityStableSourceMissing");
        var revision = reader.ReadUInt32();
        var state = reader.ReadUInt16();
        var status = ReadEnum<PartIdentityObservationStatus>(reader.ReadByte(),
            "ProductionInspectionPartIdentityStableStatusInvalid");
        var raw = ReadPossiblyEmptyBytes(reader, 4096);
        var sequence = reader.ReadInt64();
        var observedAt = ReadUtc(reader, "ProductionInspectionPartIdentityStableTimestampInvalid");
        var monotonic = reader.ReadInt64();
        var frequency = reader.ReadInt64();
        var readEvidenceHash = ReadString(reader, 64) ??
            throw Corrupt("ProductionInspectionPartIdentityReadEvidenceMissing");
        var value = new PartIdentityStablePlcSnapshot(cycle, sourceContract, revision, state, status, raw, sequence, observedAt,
            monotonic, frequency, readEvidenceHash);
        var expectedHash = ReadString(reader, 64) ??
            throw Corrupt("ProductionInspectionPartIdentityStableHashMissing");
        RequireHash(value.ContentHash, expectedHash,
            "ProductionInspectionPartIdentityStableHashMismatch");
        return value;
    }

    private static void WriteObligations(BinaryWriter writer,
        IReadOnlyCollection<TraceRetentionObligation> values)
    {
        if (values.Count > MaximumObligations) throw Corrupt("ProductionInspectionObligationCapacityExceeded");
        writer.Write(values.Count);
        foreach (var value in values.OrderBy(item => item.ContentHash, StringComparer.Ordinal))
        {
            WriteString(writer, value.ArtifactContentHash, 64);
            writer.Write((byte)value.EvidenceClass);
            writer.Write((byte)value.StartsAt);
            writer.Write(value.Rule.MinimumRetention.Ticks);
            writer.Write(value.StartedAtUtc.UtcTicks);
            WriteString(writer, value.ContentHash, 64);
        }
    }

    private static IReadOnlyList<TraceRetentionObligation> ReadObligations(
        BinaryReader reader, TraceStoragePolicySnapshot snapshot)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > MaximumObligations)
            throw Corrupt("ProductionInspectionObligationCapacityExceeded");
        var values = new List<TraceRetentionObligation>(count);
        for (var index = 0; index < count; index++)
        {
            var artifact = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionArtifactHashMissing");
            var evidence = ReadEnum<TraceRetentionClass>(reader.ReadByte(),
                "ProductionInspectionRetentionClassInvalid");
            var startsAt = ReadEnum<RetentionStartEvent>(reader.ReadByte(),
                "ProductionInspectionRetentionStartInvalid");
            var retention = TimeSpan.FromTicks(reader.ReadInt64());
            var startedAt = ReadUtc(reader, "ProductionInspectionRetentionTimestampInvalid");
            var expectedHash = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionObligationHashMissing");
            var rule = snapshot.RetentionRules.SingleOrDefault(value => value.EvidenceClass == evidence &&
                value.StartsAt == startsAt && value.MinimumRetention == retention) ??
                throw Corrupt("ProductionInspectionRetentionRuleMissing");
            var obligation = new TraceRetentionObligation(artifact, snapshot, rule, startedAt);
            RequireHash(obligation.ContentHash, expectedHash, "ProductionInspectionObligationHashMismatch");
            values.Add(obligation);
        }
        return values;
    }

    private static void WriteFrameAcquisitionStart(BinaryWriter writer, FrameAcquisitionStart? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        writer.Write(value.BusyAt.HostObservedAtUtc.UtcTicks);
        writer.Write(value.BusyAt.MonotonicTimestamp);
        writer.Write(value.DeadlineTimestamp);
        writer.Write(value.MonotonicFrequency);
    }

    private static FrameAcquisitionStart? ReadFrameAcquisitionStart(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        var point = new FrameTimePoint(ReadUtc(reader, "ProductionInspectionAcquisitionStartTimestampInvalid"),
            reader.ReadInt64());
        return new FrameAcquisitionStart(point, reader.ReadInt64(), reader.ReadInt64());
    }

    private static void WriteFrameMetadata(BinaryWriter writer, FrameMetadata? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        WriteCorrelation(writer, value.Correlation);
        WriteString(writer, value.LogicalCameraRole, 64);
        writer.Write(value.Width); writer.Write(value.Height); writer.Write(value.StrideBytes);
        writer.Write((byte)value.PixelFormat); WriteNullableInt32(writer, value.ValidBits);
        writer.Write(value.HostCaptureUtc.UtcTicks);
        WriteEffectiveConfiguration(writer, value.EffectiveCameraConfiguration);
    }

    private static FrameMetadata? ReadFrameMetadata(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        var correlation = ReadCorrelation(reader);
        var role = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionCameraRoleMissing");
        var width = reader.ReadInt32(); var height = reader.ReadInt32(); var stride = reader.ReadInt32();
        var format = ReadEnum<VisionPixelFormat>(reader.ReadByte(), "ProductionInspectionPixelFormatInvalid");
        var validBits = ReadNullableInt32(reader);
        var captured = ReadUtc(reader, "ProductionInspectionFrameTimestampInvalid");
        var effective = ReadEffectiveConfiguration(reader);
        return new FrameMetadata(correlation, role, width, height, stride, format, validBits, captured, effective);
    }

    private static void WriteCorrelation(BinaryWriter writer, ExecutionCorrelationId value)
    {
        writer.Write((byte)value.Kind); WriteGuid(writer, value.Value);
    }

    private static ExecutionCorrelationId ReadCorrelation(BinaryReader reader) =>
        new(ReadEnum<ExecutionKind>(reader.ReadByte(), "ProductionInspectionCorrelationKindInvalid"), ReadGuid(reader));

    private static void WriteEffectiveConfiguration(BinaryWriter writer, EffectiveCameraConfiguration value)
    {
        writer.Write((byte)value.ProductionAcquisitionMode); writer.Write(value.ExposureTimeUs);
        writer.Write(value.GainDb); writer.Write(value.RegionOfInterest.OffsetX);
        writer.Write(value.RegionOfInterest.OffsetY); writer.Write(value.RegionOfInterest.Width);
        writer.Write(value.RegionOfInterest.Height); writer.Write((byte)value.PixelFormat);
        WriteNullableInt32(writer, value.ValidBits); writer.Write(value.AcquisitionTimeoutMs);
        writer.Write(value.TriggerDelayUs); writer.Write(value.WhiteBalanceRgb is not null);
        if (value.WhiteBalanceRgb is { } white)
        { writer.Write(white.Red); writer.Write(white.Green); writer.Write(white.Blue); }
    }

    private static EffectiveCameraConfiguration ReadEffectiveConfiguration(BinaryReader reader)
    {
        var mode = ReadEnum<ProductionAcquisitionMode>(reader.ReadByte(),
            "ProductionInspectionAcquisitionModeInvalid");
        var exposure = reader.ReadDouble(); var gain = reader.ReadDouble();
        var roi = new RegionOfInterest(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        var format = ReadEnum<VisionPixelFormat>(reader.ReadByte(), "ProductionInspectionPixelFormatInvalid");
        var validBits = ReadNullableInt32(reader); var timeout = reader.ReadInt32();
        var triggerDelay = reader.ReadDouble(); WhiteBalanceRgb? white = null;
        if (reader.ReadBoolean()) white = new WhiteBalanceRgb(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
        return new EffectiveCameraConfiguration(mode, exposure, gain, roi, format, validBits,
            timeout, triggerDelay, white);
    }

    private static void WriteFrameProvenance(BinaryWriter writer, FrameProvenance? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        WriteCorrelation(writer, value.Correlation);
        WriteString(writer, value.ProviderId, 64); WriteString(writer, value.ProviderVersion, 128);
        WriteString(writer, value.AdapterId, 64); WriteString(writer, value.AdapterVersion, 128);
        WriteString(writer, value.SdkId, 64); WriteString(writer, value.SdkVersion, 128);
        WriteNullableString(writer, value.NativeRuntimeVersion, 128);
        WriteString(writer, value.StableDeviceIdentity, 128);
        WriteNullableString(writer, value.ReportedModel, 128); WriteNullableString(writer, value.FirmwareVersion, 128);
        WriteString(writer, value.NativePixelFormatDescription, 256);
        WriteString(writer, value.NormalizationDetails, 2048);
        writer.Write(value.NormalizationAllocated); writer.Write(value.NormalizationTransformed);
        writer.Write(value.DeviceTimestamp is not null);
        if (value.DeviceTimestamp is { } device)
        {
            writer.Write(device.Value); WriteNullableInt64(writer, device.TickFrequency);
            WriteNullableString(writer, device.Unit, 64); WriteString(writer, device.ClockDomain, 64);
            WriteNullableInt64(writer, device.CounterRollover); writer.Write((byte)device.Synchronization);
        }
        writer.Write(value.FrameCounter.HasValue); if (value.FrameCounter is { } counter) writer.Write(counter);
        WriteMilestones(writer, value.Milestones);
        writer.Write(value.PoolCopyEvidence is not null);
        if (value.PoolCopyEvidence is { } copy)
        {
            writer.Write(copy.SourceStrideBytes); writer.Write(copy.DestinationStrideBytes);
            writer.Write(copy.InputNormalizationTransformed);
        }
    }

    private static FrameProvenance? ReadFrameProvenance(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        var correlation = ReadCorrelation(reader);
        var providerId = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionProviderMissing");
        var providerVersion = ReadString(reader, 128) ?? throw Corrupt("ProductionInspectionProviderVersionMissing");
        var adapterId = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionAdapterMissing");
        var adapterVersion = ReadString(reader, 128) ?? throw Corrupt("ProductionInspectionAdapterVersionMissing");
        var sdkId = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionSdkMissing");
        var sdkVersion = ReadString(reader, 128) ?? throw Corrupt("ProductionInspectionSdkVersionMissing");
        var nativeRuntime = ReadNullableString(reader, 128);
        var stableDevice = ReadString(reader, 128) ?? throw Corrupt("ProductionInspectionDeviceMissing");
        var model = ReadNullableString(reader, 128); var firmware = ReadNullableString(reader, 128);
        var nativePixel = ReadString(reader, 256) ?? throw Corrupt("ProductionInspectionNativePixelMissing");
        var normalization = ReadString(reader, 2048) ?? throw Corrupt("ProductionInspectionNormalizationMissing");
        var allocated = reader.ReadBoolean(); var transformed = reader.ReadBoolean();
        DeviceTimestamp? device = null;
        if (reader.ReadBoolean())
        {
            var value = reader.ReadInt64(); var frequency = ReadNullableInt64(reader);
            var unit = ReadNullableString(reader, 64);
            var domain = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionClockDomainMissing");
            var rollover = ReadNullableInt64(reader);
            var sync = ReadEnum<DeviceClockSynchronization>(reader.ReadByte(),
                "ProductionInspectionClockSynchronizationInvalid");
            device = new DeviceTimestamp(value, frequency, unit, domain, rollover, sync);
        }
        ulong? counter = reader.ReadBoolean() ? reader.ReadUInt64() : null;
        var milestones = ReadMilestones(reader);
        var provenance = new FrameProvenance(correlation, providerId, providerVersion, adapterId,
            adapterVersion, sdkId, sdkVersion, nativeRuntime, stableDevice, model, firmware,
            nativePixel, normalization, allocated, transformed, device, counter, milestones);
        if (reader.ReadBoolean())
        {
            var source = reader.ReadInt32(); var destination = reader.ReadInt32();
            _ = reader.ReadBoolean();
            provenance = provenance.WithPoolCopyEvidence(source, destination);
        }
        return provenance;
    }

    private static void WriteMilestones(BinaryWriter writer, FrameAcquisitionMilestones value)
    {
        writer.Write(value.MonotonicFrequency);
        WriteTimePoint(writer, value.TriggerAccepted); WriteTimePoint(writer, value.AcquisitionStarted);
        WriteTimePoint(writer, value.NativeFrameReceived); WriteTimePoint(writer, value.NormalizedFrameReady);
    }

    private static FrameAcquisitionMilestones ReadMilestones(BinaryReader reader) =>
        new(reader.ReadInt64(), ReadTimePoint(reader), ReadTimePoint(reader), ReadTimePoint(reader), ReadTimePoint(reader));

    private static void WriteTimePoint(BinaryWriter writer, FrameTimePoint? value)
    {
        writer.Write(value is not null);
        if (value is not null) { writer.Write(value.HostObservedAtUtc.UtcTicks); writer.Write(value.MonotonicTimestamp); }
    }

    private static FrameTimePoint? ReadTimePoint(BinaryReader reader) =>
        reader.ReadBoolean() ? new FrameTimePoint(ReadUtc(reader, "ProductionInspectionMilestoneTimestampInvalid"), reader.ReadInt64()) : null;

    private static void WriteNullableInt32(BinaryWriter writer, int? value)
    { writer.Write(value.HasValue); if (value.HasValue) writer.Write(value.Value); }
    private static int? ReadNullableInt32(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadInt32() : null;
    private static void WriteNullableInt64(BinaryWriter writer, long? value)
    { writer.Write(value.HasValue); if (value.HasValue) writer.Write(value.Value); }
    private static long? ReadNullableInt64(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadInt64() : null;

    private static void WritePlcPayload(BinaryWriter writer, PlcResultPayloadSnapshot? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        WriteString(writer, value.Binding.ContentHash, 64);
        WriteGuid(writer, value.InspectionId);
        writer.Write(value.Cycle.ControllerEpoch); writer.Write(value.Cycle.CycleSequence);
        writer.Write((byte)value.ExecutionStatus); writer.Write((byte)value.Decision);
        WriteNullableString(writer, value.ReasonCode, 256);
        if (value.Segments.Count > MaximumSegments) throw Corrupt("ProductionInspectionSegmentCapacityExceeded");
        writer.Write(value.Segments.Count);
        foreach (var segment in value.Segments)
        {
            writer.Write(segment.StartRegister); WriteBytes(writer, segment.RegisterBytes.ToArray(), 131072);
        }
        WriteString(writer, value.WireContentHash, 64); WriteString(writer, value.ContentHash, 64);
    }

    private static PlcResultPayloadSnapshot? ReadPlcPayload(BinaryReader reader,
        ProductionInspectionAdmission admission)
    {
        if (!reader.ReadBoolean()) return null;
        var bindingHash = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionPayloadBindingMissing");
        RequireHash(admission.ActivationSnapshot.PlcResultContract.ContentHash, bindingHash,
            "ProductionInspectionPayloadBindingMismatch");
        var inspectionId = ReadGuid(reader);
        var cycle = new PlcControllerCycle(reader.ReadUInt32(), reader.ReadUInt32());
        var status = ReadEnum<ExecutionStatus>(reader.ReadByte(), "ProductionInspectionPayloadStatusInvalid");
        var decision = ReadEnum<InspectionDecision>(reader.ReadByte(), "ProductionInspectionPayloadDecisionInvalid");
        var reason = ReadNullableString(reader, 256);
        var count = reader.ReadInt32();
        if (count <= 0 || count > MaximumSegments) throw Corrupt("ProductionInspectionSegmentCapacityExceeded");
        var segments = new List<PlcRegisterSegment>(count);
        for (var index = 0; index < count; index++)
            segments.Add(new PlcRegisterSegment(reader.ReadInt32(), ReadBytes(reader, 131072)));
        var wireHash = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionPayloadWireHashMissing");
        var contentHash = ReadString(reader, 64) ?? throw Corrupt("ProductionInspectionPayloadHashMissing");
        var value = new PlcResultPayloadSnapshot(admission.ActivationSnapshot.PlcResultContract,
            inspectionId, cycle, status, decision, reason, segments);
        RequireHash(value.WireContentHash, wireHash, "ProductionInspectionPayloadWireHashMismatch");
        RequireHash(value.ContentHash, contentHash, "ProductionInspectionPayloadHashMismatch");
        return value;
    }

    private static string? ResultHash(AlgorithmResult? value) => value is null ? null :
        AlgorithmContractValidationHash(value);

    private static string AlgorithmContractValidationHash(AlgorithmResult value) =>
        AlgorithmContractValidationBridge.HashParts(new string?[]
        {
            "production-algorithm-result-v1", value.Decision.ToString(), value.ReasonCode,
            value.Measurements.Count.ToString(CultureInfo.InvariantCulture),
            string.Join("\n", value.Measurements.Select(item => item.Key + "\0" + item.Unit + "\0" + item.Value))
        });

    private static string? FrameHash(FrameMetadata? value) => value is null ? null :
        ProductionInspectionCoreHash.Frame(value);

    private static string? ProvenanceHash(FrameProvenance? value) => value is null ? null :
        ProductionInspectionCoreHash.Provenance(value);

    private static string? TimingHash(AlgorithmExecutionTimingSnapshot? value) => value is null ? null :
        ProductionInspectionCoreHash.Timing(value);

    private static string? AcquisitionStartHash(FrameAcquisitionStart? value) => value is null ? null :
        AlgorithmContractValidationBridge.HashParts(new string?[]
        {
            "production-acquisition-start-v1",
            value.BusyAt.HostObservedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            value.BusyAt.MonotonicTimestamp.ToString(CultureInfo.InvariantCulture),
            value.DeadlineTimestamp.ToString(CultureInfo.InvariantCulture),
            value.MonotonicFrequency.ToString(CultureInfo.InvariantCulture)
        });

    // Keep the storage codec independent of the Core's private hash helpers.
    private static class AlgorithmContractValidationBridge
    {
        internal static string HashParts(IEnumerable<string?> values) =>
            AlgorithmContractValidation.HashParts(values);
    }

    private static class ProductionInspectionCoreHash
    {
        internal static string Frame(FrameMetadata value) =>
            AlgorithmContractValidationBridge.HashParts(new string?[]
            {
                "production-frame-metadata-v1", value.Correlation.Value.ToString("D"),
                value.LogicalCameraRole, value.Width.ToString(CultureInfo.InvariantCulture),
                value.Height.ToString(CultureInfo.InvariantCulture), value.StrideBytes.ToString(CultureInfo.InvariantCulture),
                value.PixelFormat.ToString(), value.ValidBits?.ToString(CultureInfo.InvariantCulture),
                value.HostCaptureUtc.ToString("O", CultureInfo.InvariantCulture),
                value.EffectiveCameraConfiguration.ProductionAcquisitionMode.ToString(),
                value.EffectiveCameraConfiguration.ExposureTimeUs.ToString("R", CultureInfo.InvariantCulture),
                value.EffectiveCameraConfiguration.GainDb.ToString("R", CultureInfo.InvariantCulture),
                value.EffectiveCameraConfiguration.RegionOfInterest.OffsetX.ToString(CultureInfo.InvariantCulture),
                value.EffectiveCameraConfiguration.RegionOfInterest.OffsetY.ToString(CultureInfo.InvariantCulture),
                value.EffectiveCameraConfiguration.RegionOfInterest.Width.ToString(CultureInfo.InvariantCulture),
                value.EffectiveCameraConfiguration.RegionOfInterest.Height.ToString(CultureInfo.InvariantCulture),
                value.EffectiveCameraConfiguration.PixelFormat.ToString(),
                value.EffectiveCameraConfiguration.ValidBits?.ToString(CultureInfo.InvariantCulture),
                value.EffectiveCameraConfiguration.AcquisitionTimeoutMs.ToString(CultureInfo.InvariantCulture),
                value.EffectiveCameraConfiguration.TriggerDelayUs.ToString("R", CultureInfo.InvariantCulture)
            });

        internal static string Provenance(FrameProvenance value) =>
            AlgorithmContractValidationBridge.HashParts(new string?[]
            {
                "production-frame-provenance-v1", value.Correlation.Value.ToString("D"), value.ProviderId,
                value.ProviderVersion, value.AdapterId, value.AdapterVersion, value.SdkId, value.SdkVersion,
                value.NativeRuntimeVersion, value.StableDeviceIdentity, value.ReportedModel,
                value.FirmwareVersion, value.NativePixelFormatDescription, value.NormalizationDetails,
                value.NormalizationAllocated ? "1" : "0", value.NormalizationTransformed ? "1" : "0",
                value.FrameCounter?.ToString(CultureInfo.InvariantCulture),
                DeviceTimestampHash(value.DeviceTimestamp),
                MilestonesHash(value.Milestones), PoolCopyHash(value.PoolCopyEvidence)
            });

        private static string? DeviceTimestampHash(DeviceTimestamp? value) => value is null ? null :
            AlgorithmContractValidationBridge.HashParts(new string?[]
            {
                "production-device-timestamp-v1", value.Value.ToString(CultureInfo.InvariantCulture),
                value.TickFrequency?.ToString(CultureInfo.InvariantCulture), value.Unit,
                value.ClockDomain, value.CounterRollover?.ToString(CultureInfo.InvariantCulture),
                value.Synchronization.ToString()
            });

        private static string MilestonesHash(FrameAcquisitionMilestones value) =>
            AlgorithmContractValidationBridge.HashParts(new string?[]
            {
                "production-frame-milestones-v1",
                value.MonotonicFrequency.ToString(CultureInfo.InvariantCulture),
                TimePointHash(value.TriggerAccepted), TimePointHash(value.AcquisitionStarted),
                TimePointHash(value.NativeFrameReceived), TimePointHash(value.NormalizedFrameReady)
            });

        private static string? TimePointHash(FrameTimePoint? value) => value is null ? null :
            AlgorithmContractValidationBridge.HashParts(new string?[]
            {
                value.HostObservedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                value.MonotonicTimestamp.ToString(CultureInfo.InvariantCulture)
            });

        private static string? PoolCopyHash(PoolCopyEvidence? value) => value is null ? null :
            AlgorithmContractValidationBridge.HashParts(new string?[]
            {
                "production-pool-copy-v1", value.SourceStrideBytes.ToString(CultureInfo.InvariantCulture),
                value.DestinationStrideBytes.ToString(CultureInfo.InvariantCulture),
                value.InputNormalizationTransformed ? "1" : "0", value.IdentityPixelCopy ? "1" : "0",
                value.PaddingZeroed ? "1" : "0"
            });

        internal static string Timing(AlgorithmExecutionTimingSnapshot value) =>
            AlgorithmContractValidationBridge.HashParts(new string?[]
            {
                "production-execution-timing-v1", value.PolicyId, value.PolicyVersion,
                value.PolicyContentHash, value.AlgorithmExecutionTimeout.Ticks.ToString(CultureInfo.InvariantCulture),
                value.CancellationGracePeriod.Ticks.ToString(CultureInfo.InvariantCulture)
            });
    }
}
