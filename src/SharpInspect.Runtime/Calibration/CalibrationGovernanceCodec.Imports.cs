using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Calibration;

// Reuse the canonical primitives, without changing GVC1 or its four legacy kinds.
internal static partial class CalibrationGovernanceCodec
{
    private const int ImportMagic = 0x314D4943; // CIM1

    internal static byte[] EncodeImport(CalibrationImportRecord record)
    {
        var writer = new Writer();
        writer.Int32(ImportMagic);
        writer.Byte(1);
        writer.Byte(ImportKind(record));
        writer.Int64(record.Position);
        writer.Guid(record.OperationId);
        WriteActor(writer, record.Actor);
        writer.UtcTicks(record.RecordedAtUtc);
        writer.String(record.Reason);
        writer.String(record.AuthorizationTarget);
        switch (record)
        {
            case ImportedCalibrationCandidate candidate:
                writer.Guid(candidate.CandidateId);
                writer.String(candidate.PackageHash);
                writer.Int32(candidate.PackageLength);
                writer.Guid(candidate.SourcePackageId);
                writer.String(candidate.SourceStationId);
                writer.String(candidate.SourceManifestHash);
                break;
            case ImportedCalibrationEvaluation evaluation:
                WriteImportReference(writer, evaluation.Candidate);
                WriteImportBinding(writer, evaluation.Binding);
                WriteProfileContent(writer, evaluation.Content);
                writer.Count(evaluation.Sections.Count, 6);
                foreach (var section in evaluation.Sections) WriteSectionResult(writer, section);
                WriteReasons(writer, evaluation.Failures);
                writer.Bytes(evaluation.ComputationEvidence.GetBytes(), CalibrationImportComputationEvidence.MaximumBytes);
                break;
            case ImportedCalibrationPhysicalVerification physical:
                WriteImportReference(writer, physical.Candidate);
                WriteImportEvidenceReference(writer, physical.Evaluation);
                WriteSubmission(writer, physical.Submission);
                if (physical.Witness is null)
                    throw new ArgumentException("CalibrationImportPhysicalWitnessRequired");
                WritePhysicalWitness(writer, physical.Witness);
                writer.Count(physical.Gates.Count, 32);
                foreach (var gate in physical.Gates) WriteMetricResult(writer, gate);
                WriteReasons(writer, physical.Failures);
                writer.NullableUtcTicks(physical.ValidUntilUtc);
                break;
            case PublishedImportedCalibrationProfile profile:
                writer.Guid(profile.ProfileId);
                WriteImportReference(writer, profile.Candidate);
                WriteImportEvidenceReference(writer, profile.Evaluation);
                writer.Bool(profile.PhysicalVerification is not null);
                if (profile.PhysicalVerification is { } verification) WriteImportEvidenceReference(writer, verification);
                WriteProfileContent(writer, profile.Content);
                break;
            default:
                throw new ArgumentException("CalibrationImportRecordUnsupported");
        }
        writer.String(record.ContentHash);
        return writer.ToArray();
    }

    internal static CalibrationImportRecord DecodeImport(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumPayloadBytes)
            throw new ArgumentException("CalibrationImportPayloadSizeInvalid");
        try
        {
            var reader = new Reader(bytes);
            if (reader.Int32() != ImportMagic || reader.Byte() != 1)
                throw new InvalidDataException("CalibrationImportFormatUnsupported");
            var kind = reader.Byte();
            var position = reader.Int64();
            var operation = reader.Guid();
            var actor = ReadActor(reader);
            var at = reader.UtcDateTime();
            var reason = reader.String(4096);
            var target = reader.String(64);
            CalibrationImportRecord record;
            switch (kind)
            {
                case 1:
                    record = new ImportedCalibrationCandidate(position, operation, actor, at,
                        reader.Guid(), reader.String(64), reader.Int32(), reader.Guid(),
                        reader.String(1024), reader.String(64), reason, target);
                    break;
                case 2:
                {
                    var candidate = ReadImportReference(reader);
                    var binding = ReadImportBinding(reader);
                    var content = ReadProfileContent(reader);
                    var count = reader.Count(6);
                    var sections = new CalibrationGateSectionResult[count];
                    for (var i = 0; i < count; i++) sections[i] = ReadSectionResult(reader);
                    record = new ImportedCalibrationEvaluation(position, operation, actor, at, candidate, binding,
                        content, sections, ReadReasons(reader, 64),
                        new CalibrationImportComputationEvidence(reader.Bytes(CalibrationImportComputationEvidence.MaximumBytes)), reason, target);
                    break;
                }
                case 3:
                {
                    var candidate = ReadImportReference(reader);
                    var evaluation = ReadImportEvidenceReference(reader);
                    var submission = ReadSubmission(reader);
                    var witness = ReadPhysicalWitness(reader);
                    var count = reader.Count(32);
                    var gates = new CalibrationMetricGateResult[count];
                    for (var i = 0; i < count; i++) gates[i] = ReadMetricResult(reader);
                    record = new ImportedCalibrationPhysicalVerification(position, operation, actor, at, candidate,
                        evaluation, submission, gates, ReadReasons(reader, 64), reader.NullableUtcDateTime(), reason, target,
                        witness);
                    break;
                }
                case 4:
                    record = new PublishedImportedCalibrationProfile(position, operation, actor, at, reader.Guid(),
                        ReadImportReference(reader), ReadImportEvidenceReference(reader),
                        reader.Bool() ? ReadImportEvidenceReference(reader) : null, ReadProfileContent(reader), reason, target);
                    break;
                default:
                    throw new InvalidDataException("CalibrationImportKindUnsupported");
            }
            if (reader.String(64) != record.ContentHash)
                throw new InvalidDataException("CalibrationImportRecordHashMismatch");
            reader.EnsureEnd();
            if (!bytes.Span.SequenceEqual(EncodeImport(record)))
                throw new InvalidDataException("CalibrationImportNonCanonical");
            return record;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException("CalibrationImportPayloadInvalid", exception);
        }
    }

    internal static string ImportRecordKind(CalibrationImportRecord record) => ImportKind(record) switch
    {
        1 => "ImportedCandidate",
        2 => "LocalRevalidation",
        3 => "LocalPhysicalVerification",
        4 => "LocalProfilePublication",
        _ => throw new ArgumentException("CalibrationImportRecordUnsupported")
    };

    private static byte ImportKind(CalibrationImportRecord record) => record switch
    {
        ImportedCalibrationCandidate => 1,
        ImportedCalibrationEvaluation => 2,
        ImportedCalibrationPhysicalVerification => 3,
        PublishedImportedCalibrationProfile => 4,
        _ => throw new ArgumentException("CalibrationImportRecordUnsupported")
    };

    private static void WriteImportReference(Writer writer, ImportedCalibrationCandidateReference value)
    {
        writer.Guid(value.CandidateId);
        writer.String(value.ContentHash);
    }
    private static ImportedCalibrationCandidateReference ReadImportReference(Reader reader) =>
        new(reader.Guid(), reader.String(64));
    private static void WriteImportEvidenceReference(Writer writer, CalibrationImportEvidenceReference value)
    {
        writer.Guid(value.OperationId);
        writer.String(value.ContentHash);
    }
    private static CalibrationImportEvidenceReference ReadImportEvidenceReference(Reader reader) =>
        new(reader.Guid(), reader.String(64));

    private static void WritePhysicalWitness(Writer writer, CalibrationImportPhysicalWitness value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.Guid(value.OperationId);
        writer.Guid(value.RuntimeEpoch);
        WriteImportBinding(writer, value.Binding);
        WriteImagingReference(writer, value.ImagingSetup);
        WriteGeometry(writer, value.RequestedGeometry);
        WriteGeometry(writer, value.EffectiveGeometry);
        writer.UtcTicks(value.StartedAtUtc);
        writer.UtcTicks(value.CompletedAtUtc);
        writer.String(value.ContentHash);
    }

    private static CalibrationImportPhysicalWitness ReadPhysicalWitness(Reader reader)
    {
        var witness = new CalibrationImportPhysicalWitness(reader.Guid(), reader.Guid(),
            ReadImportBinding(reader), ReadImagingReference(reader), ReadGeometry(reader), ReadGeometry(reader),
            reader.UtcDateTime(), reader.UtcDateTime());
        if (reader.String(64) != witness.ContentHash)
            throw new InvalidDataException("CalibrationImportPhysicalWitnessHashMismatch");
        return witness;
    }

    private static void WriteImportBinding(Writer writer, CameraBindingRevision binding)
    {
        writer.Int64(binding.Position);
        writer.String(binding.LogicalRole);
        writer.Int64(binding.Revision);
        writer.Guid(binding.OperationId);
        writer.NullableString(binding.PreviousRevisionHash);
        writer.String(binding.RevisionHash);
        WriteBindingTarget(writer, binding.Target);
        writer.Guid(binding.AuthorPrincipalId);
        writer.Guid(binding.AuthorSessionId);
        writer.Int64(binding.AuthorAuthorizationRevision);
        writer.String(binding.ChangeReason);
        writer.UtcTicks(binding.RecordedAtUtc);
    }
    private static CameraBindingRevision ReadImportBinding(Reader reader) => new(reader.Int64(), reader.String(256),
        reader.Int64(), reader.Guid(), reader.NullableString(64), reader.String(64), ReadBindingTarget(reader),
        reader.Guid(), reader.Guid(), reader.Int64(), reader.String(4096), reader.UtcDateTime());
}
