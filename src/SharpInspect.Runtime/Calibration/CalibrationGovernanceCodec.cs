using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Calibration;

/// <summary>
/// Strict, bounded binary codec for the four calibration-governance ledger records.
/// The wire format is intentionally private to this package: every decoded value is
/// rebuilt through the public or assembly-internal domain constructor and then
/// re-encoded byte-for-byte before it is returned.
/// </summary>
internal static partial class CalibrationGovernanceCodec
{
    internal const int MaximumPayloadBytes = 512 * 1024;

    internal const string AcceptancePolicyPublished = "AcceptancePolicyPublished";
    internal const string CandidatePolicyEvaluated = "CandidatePolicyEvaluated";
    internal const string CalibrationProfilePublished = "CalibrationProfilePublished";
    internal const string PhysicalVerificationRecorded = "PhysicalVerificationRecorded";

    private const int Magic = 0x31435647; // GVC1, little-endian on the wire.
    private const byte FormatVersion = 1;
    private const int MaximumIdentifierBytes = 256;
    private const int MaximumTextBytes = 4096;
    private const int MaximumReasonBytes = 4096;

    private enum WireKind : byte
    {
        AcceptancePolicyPublished = 1,
        CandidatePolicyEvaluated = 2,
        CalibrationProfilePublished = 3,
        PhysicalVerificationRecorded = 4
    }

    internal static byte[] Encode(object record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var kind = KindOf(record);
        var writer = new Writer();
        writer.Int32(Magic);
        writer.Byte(FormatVersion);
        writer.Byte((byte)kind);
        switch (kind)
        {
            case WireKind.AcceptancePolicyPublished:
                WritePolicyRevision(writer, (CalibrationAcceptancePolicyRevision)record);
                break;
            case WireKind.CandidatePolicyEvaluated:
                WriteEvaluation(writer, (CalibrationPolicyEvaluationRecord)record);
                break;
            case WireKind.CalibrationProfilePublished:
                WriteProfile(writer, (PublishedCalibrationProfileVersion)record);
                break;
            case WireKind.PhysicalVerificationRecorded:
                WritePhysicalVerification(writer, (PhysicalCalibrationVerificationRecord)record);
                break;
            default:
                throw new ArgumentException("CalibrationGovernanceRecordTypeUnsupported", nameof(record));
        }

        writer.String(ContentHash(record));
        return writer.ToArray();
    }

    internal static object Decode(string kind, ReadOnlyMemory<byte> bytes)
    {
        if (!TryWireKind(kind, out var expectedKind))
            throw new ArgumentException("CalibrationGovernanceKindUnknown", nameof(kind));
        if (bytes.Length is < 1 or > MaximumPayloadBytes)
            throw new ArgumentException("CalibrationGovernancePayloadSizeInvalid", nameof(bytes));

        try
        {
            var reader = new Reader(bytes);
            if (reader.Int32() != Magic || reader.Byte() != FormatVersion)
                throw Invalid("CalibrationGovernancePayloadVersionUnsupported");
            if (reader.Byte() != (byte)expectedKind)
                throw Invalid("CalibrationGovernanceKindMismatch");

            object decoded = expectedKind switch
            {
                WireKind.AcceptancePolicyPublished => ReadPolicyRevision(reader),
                WireKind.CandidatePolicyEvaluated => ReadEvaluation(reader),
                WireKind.CalibrationProfilePublished => ReadProfile(reader),
                WireKind.PhysicalVerificationRecorded => ReadPhysicalVerification(reader),
                _ => throw Invalid("CalibrationGovernanceKindUnknown")
            };

            var savedHash = reader.String(64);
            reader.EnsureEnd();
            if (!string.Equals(savedHash, ContentHash(decoded), StringComparison.Ordinal))
                throw Invalid("CalibrationGovernanceContentHashMismatch");

            var canonical = Encode(decoded);
            if (!bytes.Span.SequenceEqual(canonical))
                throw Invalid("CalibrationGovernancePayloadCanonicalMismatch");
            return decoded;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
                                           not StackOverflowException)
        {
            throw new ArgumentException("CalibrationGovernancePayloadInvalid", nameof(bytes), exception);
        }
    }

    internal static string Kind(object record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return KindOf(record) switch
        {
            WireKind.AcceptancePolicyPublished => AcceptancePolicyPublished,
            WireKind.CandidatePolicyEvaluated => CandidatePolicyEvaluated,
            WireKind.CalibrationProfilePublished => CalibrationProfilePublished,
            WireKind.PhysicalVerificationRecorded => PhysicalVerificationRecorded,
            _ => throw new ArgumentException("CalibrationGovernanceRecordTypeUnsupported", nameof(record))
        };
    }

    internal static string ContentHash(object record) => record switch
    {
        CalibrationAcceptancePolicyRevision value => value.ContentHash,
        CalibrationPolicyEvaluationRecord value => value.ContentHash,
        PublishedCalibrationProfileVersion value => value.ContentHash,
        PhysicalCalibrationVerificationRecord value => value.ContentHash,
        _ => throw new ArgumentException("CalibrationGovernanceRecordTypeUnsupported", nameof(record))
    };

    private static WireKind KindOf(object record) => record switch
    {
        CalibrationAcceptancePolicyRevision => WireKind.AcceptancePolicyPublished,
        CalibrationPolicyEvaluationRecord => WireKind.CandidatePolicyEvaluated,
        PublishedCalibrationProfileVersion => WireKind.CalibrationProfilePublished,
        PhysicalCalibrationVerificationRecord => WireKind.PhysicalVerificationRecorded,
        _ => throw new ArgumentException("CalibrationGovernanceRecordTypeUnsupported", nameof(record))
    };

    private static bool TryWireKind(string kind, out WireKind value)
    {
        switch (kind)
        {
            case AcceptancePolicyPublished:
                value = WireKind.AcceptancePolicyPublished;
                return true;
            case CandidatePolicyEvaluated:
                value = WireKind.CandidatePolicyEvaluated;
                return true;
            case CalibrationProfilePublished:
                value = WireKind.CalibrationProfilePublished;
                return true;
            case PhysicalVerificationRecorded:
                value = WireKind.PhysicalVerificationRecorded;
                return true;
            default:
                value = default;
                return false;
        }
    }

    private static void WritePolicyRevision(Writer writer, CalibrationAcceptancePolicyRevision value)
    {
        writer.Int64(value.Position);
        writer.Guid(value.OperationId);
        WritePolicy(writer, value.Policy);
        WriteOptionalContract(writer, value.Previous);
        WriteActor(writer, value.Actor);
        writer.UtcTicks(value.RecordedAtUtc);
    }

    private static CalibrationAcceptancePolicyRevision ReadPolicyRevision(Reader reader) =>
        new(reader.Int64(), reader.Guid(), ReadPolicy(reader), ReadOptionalContract(reader),
            ReadActor(reader), reader.UtcDateTime());

    private static void WriteEvaluation(Writer writer, CalibrationPolicyEvaluationRecord value)
    {
        writer.Int64(value.Position);
        writer.Guid(value.EvaluationId);
        writer.Guid(value.OperationId);
        WriteCandidateReference(writer, value.Candidate);
        WriteContract(writer, value.Policy);
        writer.Count(value.Sections.Count, 6);
        foreach (var section in value.Sections)
            WriteSectionResult(writer, section);
        WriteReasons(writer, value.BindingFailures);
        WriteActor(writer, value.Actor);
        writer.UtcTicks(value.RecordedAtUtc);
    }

    private static CalibrationPolicyEvaluationRecord ReadEvaluation(Reader reader)
    {
        var position = reader.Int64();
        var evaluationId = reader.Guid();
        var operationId = reader.Guid();
        var candidate = ReadCandidateReference(reader);
        var policy = ReadContract(reader);
        var sections = new List<CalibrationGateSectionResult>(reader.Count(6));
        for (var i = 0; i < sections.Capacity; i++)
            sections.Add(ReadSectionResult(reader));
        var failures = ReadReasons(reader, 32);
        var actor = ReadActor(reader);
        return new(position, evaluationId, operationId, candidate, policy, sections, failures,
            actor, reader.UtcDateTime());
    }

    private static void WriteProfile(Writer writer, PublishedCalibrationProfileVersion value)
    {
        writer.Int64(value.Position);
        writer.Guid(value.OperationId);
        writer.Guid(value.ProfileId);
        writer.Int64(value.Version);
        WriteOptionalProfileReference(writer, value.Previous);
        WriteProfileContent(writer, value.Content);
        WriteCandidateReference(writer, value.SourceCandidate);
        WriteEvaluationReference(writer, value.Evaluation);
        WriteContract(writer, value.AcceptancePolicy);
        WriteActor(writer, value.Actor);
        writer.UtcTicks(value.RecordedAtUtc);
    }

    private static PublishedCalibrationProfileVersion ReadProfile(Reader reader) =>
        new(reader.Int64(), reader.Guid(), reader.Guid(), reader.Int64(), ReadOptionalProfileReference(reader),
            ReadProfileContent(reader), ReadCandidateReference(reader), ReadEvaluationReference(reader),
            ReadContract(reader), ReadActor(reader), reader.UtcDateTime());

    private static void WritePhysicalVerification(Writer writer,
        PhysicalCalibrationVerificationRecord value)
    {
        writer.Int64(value.Position);
        writer.Guid(value.VerificationId);
        writer.Guid(value.OperationId);
        WriteProfileReference(writer, value.Profile);
        WriteContract(writer, value.Policy);
        WriteSubmission(writer, value.Submission);
        writer.Count(value.GateResults.Count, 32);
        foreach (var result in value.GateResults)
            WriteMetricResult(writer, result);
        WriteReasons(writer, value.BindingFailures);
        WriteActor(writer, value.Actor);
        writer.UtcTicks(value.RecordedAtUtc);
        writer.NullableUtcTicks(value.ValidUntilUtc);
    }

    private static PhysicalCalibrationVerificationRecord ReadPhysicalVerification(Reader reader)
    {
        var position = reader.Int64();
        var verificationId = reader.Guid();
        var operationId = reader.Guid();
        var profile = ReadProfileReference(reader);
        var policy = ReadContract(reader);
        var submission = ReadSubmission(reader);
        var gates = new List<CalibrationMetricGateResult>(reader.Count(32));
        for (var i = 0; i < gates.Capacity; i++)
            gates.Add(ReadMetricResult(reader));
        var failures = ReadReasons(reader, 32);
        var actor = ReadActor(reader);
        var recordedAt = reader.UtcDateTime();
        var validUntil = reader.NullableUtcDateTime();
        return new(position, verificationId, operationId, profile, policy, submission, gates, failures,
            actor, recordedAt, validUntil);
    }

    private static void WritePolicy(Writer writer, CalibrationAcceptancePolicy value)
    {
        writer.String(value.Id);
        writer.String(value.Version);
        writer.Int32((int)value.Kind);
        writer.String(value.LogicalPurpose);
        WriteContract(writer, value.ProcedureContract);
        WriteContract(writer, value.InputContract);
        WriteContract(writer, value.CoefficientContract);
        WriteContract(writer, value.ExtractionReceiptContract);
        WriteContract(writer, value.ComputationEvidenceContract);
        WriteSection(writer, value.Sample);
        WriteSection(writer, value.Coverage);
        WriteSection(writer, value.PoseDiversity);
        WriteSection(writer, value.MaximumPerImageResidual);
        WriteSection(writer, value.MaximumPerPointResidual);
        WriteSection(writer, value.InvalidObservation);
        WritePhysicalRequirement(writer, value.PhysicalVerification);
    }

    private static CalibrationAcceptancePolicy ReadPolicy(Reader reader) =>
        new(reader.String(MaximumIdentifierBytes), reader.String(MaximumIdentifierBytes),
            reader.Enum<CalibrationKind>(), reader.String(MaximumIdentifierBytes),
            ReadContract(reader), ReadContract(reader), ReadContract(reader), ReadContract(reader),
            ReadContract(reader), ReadSection(reader), ReadSection(reader), ReadSection(reader),
            ReadSection(reader), ReadSection(reader), ReadSection(reader), ReadPhysicalRequirement(reader));

    private static void WriteContract(Writer writer, RecipeContractReference value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.String(value.Id);
        writer.String(value.Version);
        writer.String(value.ContentHash);
    }

    private static RecipeContractReference ReadContract(Reader reader) =>
        new(reader.String(MaximumIdentifierBytes), reader.String(MaximumIdentifierBytes), reader.String(64));

    private static void WriteOptionalContract(Writer writer, RecipeContractReference? value)
    {
        writer.Bool(value is not null);
        if (value is not null)
            WriteContract(writer, value);
    }

    private static RecipeContractReference? ReadOptionalContract(Reader reader) =>
        reader.Bool() ? ReadContract(reader) : null;

    private static void WriteFact(Writer writer, CalibrationPolicyFactReference value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.Int32((int)value.Kind);
        writer.String(value.Key);
        writer.NullableString(value.Unit);
    }

    private static CalibrationPolicyFactReference ReadFact(Reader reader)
    {
        var kind = reader.Enum<CalibrationPolicyFactKind>();
        var key = reader.String(MaximumIdentifierBytes);
        var unit = reader.NullableString(MaximumTextBytes);
        return kind switch
        {
            CalibrationPolicyFactKind.IncludedFrameCount =>
                ExactFact(CalibrationPolicyFactReference.IncludedFrameCount, key, unit),
            CalibrationPolicyFactKind.SufficientFeatureFrameCount =>
                ExactFact(CalibrationPolicyFactReference.SufficientFeatureFrameCount, key, unit),
            CalibrationPolicyFactKind.SelectionImageCoverage =>
                ExactFact(CalibrationPolicyFactReference.SelectionImageCoverage, key, unit),
            CalibrationPolicyFactKind.ExcludedFrameCount =>
                ExactFact(CalibrationPolicyFactReference.ExcludedFrameCount, key, unit),
            CalibrationPolicyFactKind.ProcedureMetric =>
                CalibrationPolicyFactReference.ProcedureMetric(key, unit),
            CalibrationPolicyFactKind.PhysicalVerificationMetric =>
                CalibrationPolicyFactReference.PhysicalVerificationMetric(key, unit),
            _ => throw Invalid("CalibrationPolicyFactKindUnsupported")
        };
    }

    private static CalibrationPolicyFactReference ExactFact(CalibrationPolicyFactReference expected,
        string key, string? unit) => expected.Key == key && expected.Unit == unit
            ? expected
            : throw Invalid("CalibrationPolicyFactCanonicalMismatch");

    private static void WriteMetricGate(Writer writer, CalibrationMetricGate value)
    {
        writer.String(value.GateId);
        WriteFact(writer, value.Fact);
        writer.Int32((int)value.Comparison);
        writer.Double(value.Threshold);
    }

    private static CalibrationMetricGate ReadMetricGate(Reader reader) =>
        new(reader.String(MaximumIdentifierBytes), ReadFact(reader),
            reader.Enum<CalibrationGateComparison>(), reader.Double());

    private static void WriteSection(Writer writer, CalibrationGateSection value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.Int32((int)value.Category);
        writer.Int32((int)value.Applicability);
        writer.Count(value.Gates.Count, 8);
        foreach (var gate in value.Gates)
            WriteMetricGate(writer, gate);
        writer.NullableString(value.NotApplicableReason);
    }

    private static CalibrationGateSection ReadSection(Reader reader)
    {
        var category = reader.Enum<CalibrationAcceptanceGateCategory>();
        var applicability = reader.Enum<CalibrationPolicyApplicability>();
        var gates = new List<CalibrationMetricGate>(reader.Count(8));
        for (var i = 0; i < gates.Capacity; i++)
            gates.Add(ReadMetricGate(reader));
        var reason = reader.NullableString(MaximumReasonBytes);
        return new(category, applicability, gates, reason);
    }

    private static void WritePhysicalRequirement(Writer writer,
        PhysicalCalibrationVerificationRequirement value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.Int32((int)value.Applicability);
        WriteOptionalContract(writer, value.ProcedureContract);
        WriteOptionalContract(writer, value.EvidenceContract);
        WriteOptionalContract(writer, value.IndependentReference);
        writer.NullableInt64(value.ValidityInterval?.Ticks);
        writer.Count(value.Gates.Count, 32);
        foreach (var gate in value.Gates)
            WriteMetricGate(writer, gate);
        writer.NullableString(value.NotApplicableReason);
    }

    private static PhysicalCalibrationVerificationRequirement ReadPhysicalRequirement(Reader reader)
    {
        var applicability = reader.Enum<CalibrationPolicyApplicability>();
        var procedure = ReadOptionalContract(reader);
        var evidence = ReadOptionalContract(reader);
        var independent = ReadOptionalContract(reader);
        var ticks = reader.NullableInt64();
        var gates = new List<CalibrationMetricGate>(reader.Count(32));
        for (var i = 0; i < gates.Capacity; i++)
            gates.Add(ReadMetricGate(reader));
        var reason = reader.NullableString(MaximumReasonBytes);
        return new(applicability, procedure, evidence, independent,
            ticks is { } value ? new TimeSpan(value) : null, gates, reason);
    }

    private static void WriteSectionResult(Writer writer, CalibrationGateSectionResult value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.Int32((int)value.Category);
        writer.Int32((int)value.Applicability);
        writer.Count(value.Gates.Count, 8);
        foreach (var gate in value.Gates)
            WriteMetricResult(writer, gate);
        writer.NullableString(value.NotApplicableReason);
    }

    private static CalibrationGateSectionResult ReadSectionResult(Reader reader)
    {
        var category = reader.Enum<CalibrationAcceptanceGateCategory>();
        var applicability = reader.Enum<CalibrationPolicyApplicability>();
        var gates = new List<CalibrationMetricGateResult>(reader.Count(8));
        for (var i = 0; i < gates.Capacity; i++)
            gates.Add(ReadMetricResult(reader));
        var reason = reader.NullableString(MaximumReasonBytes);
        return new(category, applicability, gates, reason);
    }

    private static void WriteMetricResult(Writer writer, CalibrationMetricGateResult value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.String(value.GateId);
        writer.Int32((int)value.Outcome);
        writer.NullableDouble(value.ActualValue);
        writer.NullableString(value.ActualUnit);
        writer.String(value.ReasonCode);
    }

    private static CalibrationMetricGateResult ReadMetricResult(Reader reader) =>
        new(reader.String(MaximumIdentifierBytes), reader.Enum<CalibrationGateOutcome>(),
            reader.NullableDouble(), reader.NullableString(MaximumTextBytes),
            reader.String(MaximumReasonBytes));

    private static void WriteCandidateReference(Writer writer, CalibrationCandidateReference value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.Guid(value.SessionId);
        writer.Guid(value.CandidateId);
        writer.String(value.CandidateContentHash);
    }

    private static CalibrationCandidateReference ReadCandidateReference(Reader reader) =>
        new(reader.Guid(), reader.Guid(), reader.String(64));

    private static void WriteEvaluationReference(Writer writer,
        CalibrationPolicyEvaluationReference value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.Guid(value.EvaluationId);
        writer.String(value.ContentHash);
    }

    private static CalibrationPolicyEvaluationReference ReadEvaluationReference(Reader reader) =>
        new(reader.Guid(), reader.String(64));

    private static void WriteProfileReference(Writer writer, CalibrationProfileReference value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.Guid(value.ProfileId);
        writer.Int64(value.Version);
        writer.String(value.ContentHash);
    }

    private static CalibrationProfileReference ReadProfileReference(Reader reader) =>
        new(reader.Guid(), reader.Int64(), reader.String(64));

    private static void WriteOptionalProfileReference(Writer writer,
        CalibrationProfileReference? value)
    {
        writer.Bool(value is not null);
        if (value is not null)
            WriteProfileReference(writer, value);
    }

    private static CalibrationProfileReference? ReadOptionalProfileReference(Reader reader) =>
        reader.Bool() ? ReadProfileReference(reader) : null;

    private static void WriteActor(Writer writer, CalibrationGovernanceActor value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.Guid(value.PrincipalId);
        writer.Guid(value.SessionId);
        writer.Int64(value.AuthorizationRevision);
    }

    private static CalibrationGovernanceActor ReadActor(Reader reader) =>
        new(reader.Guid(), reader.Guid(), reader.Int64());

    private static void WriteProfileContent(Writer writer, CalibrationProfileContent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteRequirement(writer, value.Requirement);
        WriteBindingTarget(writer, value.Device);
        WriteImagingReference(writer, value.ImagingSetup);
        WriteGeometry(writer, value.RequestedGeometry);
        WriteGeometry(writer, value.EffectiveGeometry);
        WriteCoefficientPayload(writer, value.Coefficients);
        WriteContract(writer, value.Procedure);
        writer.String(value.SourceEvidenceHash);
        writer.Guid(value.CreatedBy);
        writer.UtcTicks(value.CreatedAtUtc);
    }

    private static CalibrationProfileContent ReadProfileContent(Reader reader) =>
        new(ReadRequirement(reader), ReadBindingTarget(reader), ReadImagingReference(reader),
            ReadGeometry(reader), ReadGeometry(reader), ReadCoefficientPayload(reader),
            ReadContract(reader), reader.String(64), reader.Guid(), reader.UtcDateTime());

    private static void WriteRequirement(Writer writer, CalibrationRequirement value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.String(value.LogicalCameraRole);
        writer.Int32((int)value.Kind);
        writer.String(value.LogicalPurpose);
        WriteContract(writer, value.CoefficientContract);
        WriteContract(writer, value.AcceptancePolicy);
    }

    private static CalibrationRequirement ReadRequirement(Reader reader) =>
        new(reader.String(MaximumIdentifierBytes), reader.Enum<CalibrationKind>(),
            reader.String(MaximumIdentifierBytes), ReadContract(reader), ReadContract(reader));

    private static void WriteBindingTarget(Writer writer, CameraBindingTarget value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.String(value.Provider.Id);
        writer.String(value.Provider.Version);
        writer.String(value.Provider.AdapterPackageId);
        writer.String(value.Provider.AdapterVersion);
        writer.String(value.StableDeviceIdentity);
    }

    private static CameraBindingTarget ReadBindingTarget(Reader reader) =>
        new(new CameraProviderIdentity(reader.String(MaximumIdentifierBytes),
                reader.String(MaximumIdentifierBytes), reader.String(MaximumIdentifierBytes),
                reader.String(MaximumIdentifierBytes)), reader.String(MaximumIdentifierBytes));

    private static void WriteImagingReference(Writer writer, ImagingSetupRevisionReference value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.String(value.LogicalCameraRole);
        writer.Guid(value.RevisionId);
        writer.Int64(value.Revision);
        writer.String(value.RevisionHash);
    }

    private static ImagingSetupRevisionReference ReadImagingReference(Reader reader) =>
        new(reader.String(MaximumIdentifierBytes), reader.Guid(), reader.Int64(), reader.String(64));

    private static void WriteGeometry(Writer writer, CalibrationFrameGeometry value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.Int32(value.RegionOfInterest.OffsetX);
        writer.Int32(value.RegionOfInterest.OffsetY);
        writer.Int32(value.RegionOfInterest.Width);
        writer.Int32(value.RegionOfInterest.Height);
        writer.Int32(value.FrameWidth);
        writer.Int32(value.FrameHeight);
        writer.Int32((int)value.PixelFormat);
        writer.NullableInt32(value.ValidBits);
    }

    private static CalibrationFrameGeometry ReadGeometry(Reader reader)
    {
        var roi = new RegionOfInterest(reader.Int32(), reader.Int32(), reader.Int32(), reader.Int32());
        return new(roi, reader.Int32(), reader.Int32(), reader.Enum<VisionPixelFormat>(),
            reader.NullableInt32());
    }

    private static void WriteCoefficientPayload(Writer writer, CalibrationCoefficientPayload value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteContract(writer, value.Format);
        writer.Bytes(value.GetBytes(), CalibrationCoefficientPayload.MaximumBytes);
    }

    private static CalibrationCoefficientPayload ReadCoefficientPayload(Reader reader) =>
        new(ReadContract(reader), reader.Bytes(CalibrationCoefficientPayload.MaximumBytes));

    private static void WriteSubmission(Writer writer,
        PhysicalCalibrationVerificationSubmission value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteContract(writer, value.ProcedureContract);
        WriteContract(writer, value.IndependentReference);
        writer.UtcTicks(value.PerformedAtUtc);
        writer.Count(value.Metrics.Count, 32);
        foreach (var metric in value.Metrics)
        {
            writer.String(metric.Key);
            writer.Double(metric.Value);
            writer.NullableString(metric.Unit);
        }
        WriteEvidencePayload(writer, value.Evidence);
    }

    private static PhysicalCalibrationVerificationSubmission ReadSubmission(Reader reader)
    {
        var procedure = ReadContract(reader);
        var independent = ReadContract(reader);
        var performed = reader.UtcDateTime();
        var metrics = new List<CalibrationQualityMetric>(reader.Count(32));
        for (var i = 0; i < metrics.Capacity; i++)
            metrics.Add(new(reader.String(MaximumIdentifierBytes), reader.Double(),
                reader.NullableString(MaximumTextBytes)));
        return new(procedure, independent, performed, metrics, ReadEvidencePayload(reader));
    }

    private static void WriteEvidencePayload(Writer writer,
        PhysicalCalibrationVerificationEvidencePayload value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteContract(writer, value.Format);
        writer.Bytes(value.GetBytes(), PhysicalCalibrationVerificationEvidencePayload.MaximumBytes);
    }

    private static PhysicalCalibrationVerificationEvidencePayload ReadEvidencePayload(Reader reader) =>
        new(ReadContract(reader), reader.Bytes(PhysicalCalibrationVerificationEvidencePayload.MaximumBytes));

    private static void WriteReasons(Writer writer, IReadOnlyList<string> values)
    {
        writer.Count(values.Count, 32);
        foreach (var value in values)
            writer.String(value);
    }

    private static IReadOnlyList<string> ReadReasons(Reader reader, int maximum)
    {
        var values = new List<string>(reader.Count(maximum));
        for (var i = 0; i < values.Capacity; i++)
            values.Add(reader.String(MaximumReasonBytes));
        return values;
    }

    private static Exception Invalid(string code) => new InvalidDataException(code);

    private sealed class Writer
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly MemoryStream _stream = new();

        internal void Byte(byte value) => _stream.WriteByte(value);
        internal void Bool(bool value) => Byte(value ? (byte)1 : (byte)0);

        internal void Int32(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        internal void Int64(long value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        internal void Double(double value) => Int64(BitConverter.DoubleToInt64Bits(value));
        internal void Guid(Guid value) => _stream.Write(value.ToByteArray());

        internal void String(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var bytes = Utf8.GetBytes(value);
            Int32(bytes.Length);
            _stream.Write(bytes);
        }

        internal void NullableString(string? value)
        {
            Bool(value is not null);
            if (value is not null)
                String(value);
        }

        internal void Bytes(byte[] value, int maximum)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length > maximum)
                throw new ArgumentException("CalibrationGovernancePayloadSizeInvalid");
            Int32(value.Length);
            _stream.Write(value);
        }

        internal void Count(int value, int maximum)
        {
            if (value < 0 || value > maximum)
                throw new ArgumentException("CalibrationGovernanceCollectionSizeInvalid");
            Int32(value);
        }

        internal void NullableDouble(double? value)
        {
            Bool(value is not null);
            if (value is { } actual)
                Double(actual);
        }

        internal void NullableInt32(int? value)
        {
            Bool(value is not null);
            if (value is { } actual)
                Int32(actual);
        }

        internal void NullableInt64(long? value)
        {
            Bool(value is not null);
            if (value is { } actual)
                Int64(actual);
        }

        internal void UtcTicks(DateTimeOffset value) => Int64(value.ToUniversalTime().Ticks);
        internal void NullableUtcTicks(DateTimeOffset? value) => NullableInt64(value?.ToUniversalTime().Ticks);

        internal byte[] ToArray()
        {
            if (_stream.Length > MaximumPayloadBytes)
                throw new ArgumentException("CalibrationGovernancePayloadSizeInvalid");
            return _stream.ToArray();
        }
    }

    private sealed class Reader
    {
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private readonly ReadOnlyMemory<byte> _bytes;
        private int _offset;

        internal Reader(ReadOnlyMemory<byte> bytes) => _bytes = bytes;
        internal int Remaining => _bytes.Length - _offset;

        internal byte Byte() => Take(1)[0];

        internal bool Bool() => Byte() switch
        {
            0 => false,
            1 => true,
            _ => throw Invalid("CalibrationGovernanceBooleanInvalid")
        };

        internal int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        internal long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        internal double Double() => BitConverter.Int64BitsToDouble(Int64());

        internal Guid Guid() => new(Take(16).ToArray());

        internal T Enum<T>() where T : struct, Enum
        {
            var value = Int32();
            var parsed = (T)System.Enum.ToObject(typeof(T), value);
            if (Convert.ToInt64(parsed, CultureInfo.InvariantCulture) != value ||
                !System.Enum.IsDefined(typeof(T), parsed))
                throw Invalid("CalibrationGovernanceEnumInvalid");
            return parsed;
        }

        internal string String(int maximumBytes)
        {
            var length = Int32();
            if (length < 0 || length > maximumBytes || length > Remaining)
                throw Invalid("CalibrationGovernanceStringSizeInvalid");
            try
            {
                return Utf8.GetString(Take(length));
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("CalibrationGovernanceUtf8Invalid", exception);
            }
        }

        internal string? NullableString(int maximumBytes) => Bool() ? String(maximumBytes) : null;

        internal byte[] Bytes(int maximumBytes)
        {
            var length = Int32();
            if (length < 0 || length > maximumBytes || length > Remaining)
                throw Invalid("CalibrationGovernanceBytesSizeInvalid");
            return Take(length).ToArray();
        }

        internal int Count(int maximum)
        {
            var value = Int32();
            if (value < 0 || value > maximum || value > Remaining)
                throw Invalid("CalibrationGovernanceCollectionSizeInvalid");
            return value;
        }

        internal double? NullableDouble() => Bool() ? Double() : null;
        internal int? NullableInt32() => Bool() ? Int32() : null;
        internal long? NullableInt64() => Bool() ? Int64() : null;

        internal DateTimeOffset UtcDateTime()
        {
            try
            {
                return new DateTimeOffset(Int64(), TimeSpan.Zero);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new InvalidDataException("CalibrationGovernanceTimestampInvalid", exception);
            }
        }

        internal DateTimeOffset? NullableUtcDateTime() => Bool() ? UtcDateTime() : null;

        internal void EnsureEnd()
        {
            if (_offset != _bytes.Length)
                throw Invalid("CalibrationGovernancePayloadTrailingBytes");
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > Remaining)
                throw new EndOfStreamException("CalibrationGovernancePayloadTruncated");
            var result = _bytes.Span.Slice(_offset, count);
            _offset += count;
            return result;
        }
    }

}
