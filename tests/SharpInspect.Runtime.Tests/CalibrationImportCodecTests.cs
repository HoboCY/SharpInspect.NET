using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CalibrationImportCodecTests
{
    [Fact]
    public void V134_D01_AllImportRecordKindsRoundTripThroughStrictCodec()
    {
        var fixture = CalibrationImportTestDataFactory.Create();

        foreach (var record in fixture.Records)
        {
            var encoded = CalibrationGovernanceCodec.EncodeImport(record);
            var decoded = CalibrationGovernanceCodec.DecodeImport(encoded);

            Assert.Equal(record.GetType(), decoded.GetType());
            Assert.Equal(record.ContentHash, decoded.ContentHash);
            Assert.Equal(encoded, CalibrationGovernanceCodec.EncodeImport(decoded));
        }
    }

    [Fact]
    public void V134_D02_ImportCodecRejectsUnknownTrailingAndTamperedPayloads()
    {
        var fixture = CalibrationImportTestDataFactory.Create();
        var encoded = CalibrationGovernanceCodec.EncodeImport(fixture.Candidate);

        var unknown = (byte[])encoded.Clone();
        unknown[5] = 0x7F;
        Assert.Throws<InvalidDataException>(() => CalibrationGovernanceCodec.DecodeImport(unknown));

        var trailing = encoded.Concat(new byte[] { 0x7F }).ToArray();
        Assert.Throws<InvalidDataException>(() => CalibrationGovernanceCodec.DecodeImport(trailing));

        var tampered = (byte[])encoded.Clone();
        tampered[^1] ^= 0x01;
        Assert.Throws<InvalidDataException>(() => CalibrationGovernanceCodec.DecodeImport(tampered));
    }

    [Fact]
    public void V134_D03_ImportRecordsRemainDevelopmentOnlyAndNoActivationAuthority()
    {
        var fixture = CalibrationImportTestDataFactory.Create();

        Assert.False(fixture.Candidate.CanPublish);
        Assert.False(fixture.Candidate.CanActivate);
        Assert.True(fixture.Evaluation.Passed);
        Assert.True(fixture.Physical.Passed);
        Assert.True(fixture.Publication.DevelopmentOnly);
        Assert.False(fixture.Publication.ProductionAuthority);
        Assert.False(fixture.Publication.CanActivate);
        Assert.Equal(fixture.Evaluation.Content.SourceEvidenceHash,
            fixture.Evaluation.ComputationEvidenceHash);
        Assert.NotEqual(fixture.Candidate.PackageHash, fixture.Evaluation.Content.SourceEvidenceHash);
    }

    [Fact]
    public void V134_D04_PhysicalImportCodecBindsWitnessAndRejectsLegacyNoWitnessRecord()
    {
        var fixture = CalibrationImportTestDataFactory.Create();
        var encoded = CalibrationGovernanceCodec.EncodeImport(fixture.Physical);
        var decoded = Assert.IsType<ImportedCalibrationPhysicalVerification>(
            CalibrationGovernanceCodec.DecodeImport(encoded));

        Assert.NotNull(decoded.Witness);
        Assert.Equal(fixture.Physical.Witness!.ContentHash, decoded.Witness!.ContentHash);
        Assert.Equal(fixture.Physical.Witness.Binding, decoded.Witness.Binding);
        Assert.Equal(fixture.Physical.Witness.RequestedGeometry, decoded.Witness.RequestedGeometry);

        var legacy = new ImportedCalibrationPhysicalVerification(3, fixture.Physical.OperationId,
            fixture.Physical.Actor, fixture.Physical.RecordedAtUtc, fixture.Physical.Candidate,
            fixture.Physical.Evaluation, fixture.Physical.Submission, fixture.Physical.Gates,
            fixture.Physical.Failures, fixture.Physical.ValidUntilUtc, fixture.Physical.Reason,
            fixture.Physical.AuthorizationTarget);
        Assert.Throws<ArgumentException>(() => CalibrationGovernanceCodec.EncodeImport(legacy));
    }
}

internal static class CalibrationImportTestDataFactory
{
    internal const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    internal const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    internal const string HashC = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
    internal static readonly string HashD = new('D', 64);

    internal static CalibrationImportFixture Create()
    {
        var governanceFixture = CalibrationGovernanceCodecTests.CreateFixture();
        var policy = governanceFixture.Policy;
        var actor = governanceFixture.Evaluation.Actor;
        var candidateTime = new DateTimeOffset(2026, 9, 9, 1, 10, 0, TimeSpan.Zero);
        var candidateId = Guid.Parse("10101010-1010-1010-1010-101010101010");
        var candidateOperation = Guid.Parse("20202020-2020-2020-2020-202020202020");
        var sourcePackageId = Guid.Parse("30303030-3030-3030-3030-303030303030");
        var candidateReason = "Retain external calibration package for local review";
        var candidateTarget = HashParts("sharpinspect-calibration-import-command-v1", HashA, candidateReason);
        var candidate = new ImportedCalibrationCandidate(1, candidateOperation, actor, candidateTime,
            candidateId, HashA, 3, sourcePackageId, "source-station", HashB, candidateReason, candidateTarget);

        var requirement = new CalibrationRequirement("TopCamera", policy.Kind, policy.LogicalPurpose,
            policy.CoefficientContract, policy.Reference);
        var binding = CreateBinding(governanceFixture.Profile.Content.Device, actor, candidateTime);
        var imaging = governanceFixture.Profile.Content.ImagingSetup;
        var geometry = governanceFixture.Profile.Content.RequestedGeometry;
        var input = new CalibrationProcedureInputPayload(policy.InputContract, new byte[] { 1, 2, 3 });
        var coefficients = governanceFixture.Profile.Content.Coefficients;
        var feature = new CalibrationImageFeature("corner-1", 1.5, 2.5);
        var receipt = new CalibrationExtractionReceipt(policy.ExtractionReceiptContract, new byte[] { 7, 8 });
        var frameId = Guid.Parse("40404040-4040-4040-4040-404040404040");
        var observationId = Guid.Parse("50505050-5050-5050-5050-505050505050");
        var selection = new CalibrationSelectionEvaluation(true, "CalibrationEvidenceSufficient", 1, 1,
            0.25, HashC);
        var qualityMetrics = new[] { new CalibrationQualityMetric("Rms", 0.2, "pixels") };
        var diagnostics = Array.Empty<CalibrationProcedureDiagnostic>();
        var procedure = new CalibrationProcedureDescriptor(policy.ProcedureContract, policy.InputContract,
            policy.Kind);
        var observations = new[]
        {
            new
            {
                FrameId = frameId,
                SourceHash = HashB,
                ObservationId = observationId,
                InputHash = input.ContentHash,
                Features = new[] { feature },
                Diagnostics = Array.Empty<CalibrationProcedureDiagnostic>(),
                Receipt = new
                {
                    receipt.Format,
                    receipt.ContentHash,
                    Bytes = Convert.ToBase64String(receipt.GetBytes())
                }
            }
        };
        var computationEvidencePayload = new CalibrationComputationEvidencePayload(
            policy.ComputationEvidenceContract, new byte[] { 4, 5, 6 });
        var document = new
        {
            Format = "sharpinspect-local-import-recomputation-v1",
            Candidate = candidate.Reference,
            candidate.PackageHash,
            SourceManifestHash = candidate.SourceManifestHash,
            LocalRequirementHash = requirement.ContentHash,
            LocalPolicy = policy.Reference,
            BindingHash = binding.RevisionHash,
            ImagingSetup = imaging,
            Procedure = procedure,
            Input = new
            {
                input.ContentHash,
                Bytes = Convert.ToBase64String(input.GetBytes())
            },
            Observations = observations,
            Selection = selection,
            Coefficients = new
            {
                coefficients.Format,
                coefficients.ContentHash,
                Bytes = Convert.ToBase64String(coefficients.GetBytes())
            },
            QualityMetrics = qualityMetrics,
            Diagnostics = diagnostics,
            Evidence = new
            {
                computationEvidencePayload.Format,
                computationEvidencePayload.ContentHash,
                Bytes = Convert.ToBase64String(computationEvidencePayload.GetBytes())
            }
        };
        var computationEvidence = new CalibrationImportComputationEvidence(
            JsonSerializer.SerializeToUtf8Bytes(document));
        var evaluationTime = candidateTime.AddMinutes(1);
        var content = new CalibrationProfileContent(requirement, binding.Target, imaging, geometry, geometry,
            coefficients, policy.ProcedureContract, computationEvidence.ContentHash, actor.PrincipalId,
            evaluationTime);
        var sections = BuildSections(policy, selection, qualityMetrics);
        var evaluationReason = "Recompute imported candidate under local policy";
        var evaluationTarget = HashParts("sharpinspect-calibration-import-revalidate-command-v1",
            candidate.Reference.CandidateId.ToString("D"), candidate.Reference.ContentHash,
            requirement.ContentHash, binding.RevisionHash, imaging.RevisionHash, evaluationReason);
        var evaluation = new ImportedCalibrationEvaluation(2,
            Guid.Parse("60606060-6060-6060-6060-606060606060"), actor, evaluationTime,
            candidate.Reference, binding, content, sections, Array.Empty<string>(), computationEvidence,
            evaluationReason, evaluationTarget);

        var submissionTime = evaluationTime.AddMinutes(1);
        var submission = new PhysicalCalibrationVerificationSubmission(policy.ProcedureContract,
            policy.PhysicalVerification.IndependentReference!, submissionTime,
            new[] { new CalibrationQualityMetric("Scale", 0.75, "mm/pixel") },
            new PhysicalCalibrationVerificationEvidencePayload(policy.PhysicalVerification.EvidenceContract!,
                new byte[] { 9, 10, 11 }));
        var physicalGates = policy.PhysicalVerification.Gates.Select(gate =>
            CalibrationAcceptancePolicyEvaluator.EvaluateMetric(gate, submission.Metrics)).ToArray();
        var physicalTime = submissionTime.AddMinutes(1);
        var physicalReason = "Record independent physical verification";
        var physicalTarget = HashParts("sharpinspect-calibration-import-verify-command-v1",
            candidate.Reference.CandidateId.ToString("D"), candidate.Reference.ContentHash,
            evaluation.Reference.OperationId.ToString("D"), evaluation.Reference.ContentHash,
            submission.ContentHash, physicalReason);
        var physicalOperation = Guid.Parse("70707070-7070-7070-7070-707070707070");
        var witness = new CalibrationImportPhysicalWitness(physicalOperation,
            Guid.Parse("A7A7A7A7-A7A7-A7A7-A7A7-A7A7A7A7A7A7"), binding, imaging, geometry, geometry,
            submissionTime, submissionTime.AddSeconds(1));
        var physical = new ImportedCalibrationPhysicalVerification(3,
            physicalOperation, actor, physicalTime,
            candidate.Reference, evaluation.Reference, submission, physicalGates, Array.Empty<string>(),
            submissionTime.Add(policy.PhysicalVerification.ValidityInterval!.Value), physicalReason,
            physicalTarget, witness);

        var publicationTime = physicalTime.AddMinutes(1);
        var profileId = Guid.Parse("80808080-8080-8080-8080-808080808080");
        var publicationEvidence = HashParts("sharpinspect-imported-calibration-publication-evidence-v1",
            candidate.ContentHash, evaluation.ContentHash, physical.ContentHash);
        var publicationContent = new CalibrationProfileContent(requirement, binding.Target, imaging, geometry,
            geometry, coefficients, policy.ProcedureContract, publicationEvidence, actor.PrincipalId,
            publicationTime);
        var publicationReason = "Publish local development profile";
        var publicationTarget = HashParts("sharpinspect-calibration-import-publish-command-v1",
            profileId.ToString("D"), candidate.Reference.CandidateId.ToString("D"),
            candidate.Reference.ContentHash, evaluation.Reference.OperationId.ToString("D"),
            evaluation.Reference.ContentHash, physical.Reference.OperationId.ToString("D"),
            physical.Reference.ContentHash, publicationReason);
        var publication = new PublishedImportedCalibrationProfile(4,
            Guid.Parse("90909090-9090-9090-9090-909090909090"), actor, publicationTime, profileId,
            candidate.Reference, evaluation.Reference, physical.Reference, publicationContent,
            publicationReason, publicationTarget);

        return new(policy, governanceFixture.PolicyRevision, candidate, evaluation, physical, publication,
            new CalibrationImportRecord[] { candidate, evaluation, physical, publication },
            new object[] { governanceFixture.PolicyRevision });
    }

    internal static string HashParts(params string?[] values) =>
        AlgorithmContractValidation.HashParts(values);

    internal static CameraBindingRevision CreateBinding(CameraBindingTarget target,
        CalibrationGovernanceActor actor, DateTimeOffset recordedAtUtc)
    {
        var operationId = Guid.Parse("A0A0A0A0-A0A0-A0A0-A0A0-A0A0A0A0A0A0");
        var role = "TopCamera";
        var revision = 1L;
        var reason = "fixture binding";
        var revisionHash = CameraBindingHash("camera-binding-revision-v1", role,
            revision.ToString(CultureInfo.InvariantCulture), operationId.ToString("D"), null,
            target.ContentHash, actor.PrincipalId.ToString("D"), actor.SessionId.ToString("D"),
            actor.AuthorizationRevision.ToString(CultureInfo.InvariantCulture), reason,
            recordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return new CameraBindingRevision(1, role, revision, operationId, null, revisionHash, target,
            actor.PrincipalId, actor.SessionId, actor.AuthorizationRevision, reason, recordedAtUtc);
    }

    private static string CameraBindingHash(params string?[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var value in values)
        {
            var bytes = value is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, value is null ? -1 : bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IReadOnlyList<CalibrationGateSectionResult> BuildSections(
        CalibrationAcceptancePolicy policy, CalibrationSelectionEvaluation selection,
        IReadOnlyList<CalibrationQualityMetric> qualityMetrics)
    {
        var selectionMetrics = new[]
        {
            new CalibrationQualityMetric(CalibrationPolicyFactReference.IncludedFrameCount.Key,
                selection.IncludedFrameCount, CalibrationPolicyFactReference.IncludedFrameCount.Unit),
            new CalibrationQualityMetric(CalibrationPolicyFactReference.SufficientFeatureFrameCount.Key,
                selection.SufficientFeatureFrameCount,
                CalibrationPolicyFactReference.SufficientFeatureFrameCount.Unit),
            new CalibrationQualityMetric(CalibrationPolicyFactReference.SelectionImageCoverage.Key,
                selection.ImageCoverage, CalibrationPolicyFactReference.SelectionImageCoverage.Unit),
            new CalibrationQualityMetric(CalibrationPolicyFactReference.ExcludedFrameCount.Key, 0,
                CalibrationPolicyFactReference.ExcludedFrameCount.Unit)
        };
        return policy.Sections.Select(section => section.Applicability == CalibrationPolicyApplicability.NotApplicable
            ? new CalibrationGateSectionResult(section.Category, section.Applicability,
                Array.Empty<CalibrationMetricGateResult>(), section.NotApplicableReason)
            : new CalibrationGateSectionResult(section.Category, section.Applicability,
                section.Gates.Select(gate => CalibrationAcceptancePolicyEvaluator.EvaluateMetric(gate,
                    gate.Fact.Kind is CalibrationPolicyFactKind.IncludedFrameCount or
                    CalibrationPolicyFactKind.SufficientFeatureFrameCount or
                    CalibrationPolicyFactKind.SelectionImageCoverage or
                    CalibrationPolicyFactKind.ExcludedFrameCount
                        ? selectionMetrics : qualityMetrics)).ToArray())).ToArray();
    }
}

internal sealed record CalibrationImportFixture(CalibrationAcceptancePolicy Policy,
    CalibrationAcceptancePolicyRevision PolicyRevision, ImportedCalibrationCandidate Candidate,
    ImportedCalibrationEvaluation Evaluation, ImportedCalibrationPhysicalVerification Physical,
    PublishedImportedCalibrationProfile Publication, IReadOnlyList<CalibrationImportRecord> Records,
    IReadOnlyList<object> Governance);
