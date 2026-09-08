using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CalibrationGovernanceCodecTests
{
    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string HashD = "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD";

    [Fact]
    public void V127_D01_AllGovernanceKindsRoundTripThroughStrictCodec()
    {
        var fixture = CreateFixture();
        var records = new object[]
        {
            fixture.PolicyRevision,
            fixture.Evaluation,
            fixture.Profile,
            fixture.Verification
        };

        foreach (var record in records)
        {
            var kind = CalibrationGovernanceCodec.Kind(record);
            var encoded = CalibrationGovernanceCodec.Encode(record);
            var decoded = CalibrationGovernanceCodec.Decode(kind, encoded);

            Assert.Equal(record.GetType(), decoded.GetType());
            Assert.Equal(CalibrationGovernanceCodec.ContentHash(record),
                CalibrationGovernanceCodec.ContentHash(decoded));
            Assert.Equal(encoded, CalibrationGovernanceCodec.Encode(decoded));
        }
    }

    [Fact]
    public void V127_D02_StrictCodecRejectsUnknownTrailingAndTamperedPayloads()
    {
        var fixture = CreateFixture();
        var encoded = CalibrationGovernanceCodec.Encode(fixture.Evaluation);

        Assert.Throws<ArgumentException>(() => CalibrationGovernanceCodec.Decode(
            "UnknownGovernanceKind", encoded));

        var trailing = encoded.Concat(new byte[] { 0x7F }).ToArray();
        Assert.Throws<ArgumentException>(() => CalibrationGovernanceCodec.Decode(
            CalibrationGovernanceCodec.CandidatePolicyEvaluated, trailing));

        var tampered = (byte[])encoded.Clone();
        tampered[^1] ^= 0x01;
        Assert.Throws<ArgumentException>(() => CalibrationGovernanceCodec.Decode(
            CalibrationGovernanceCodec.CandidatePolicyEvaluated, tampered));
    }

    [Fact]
    public void V127_D03_RecordsRetainDevelopmentOnlyAndIndependentEvidenceBindings()
    {
        var fixture = CreateFixture();

        Assert.True(fixture.Evaluation.Passed);
        Assert.True(fixture.Evaluation.CanCreateDevelopmentProfile);
        Assert.False(fixture.Evaluation.ProductionAuthority);
        Assert.True(fixture.Profile.DevelopmentOnly);
        Assert.Equal("DevelopmentOnly", fixture.Profile.EvidencePurpose);
        Assert.False(fixture.Profile.ProductionAuthority);
        Assert.False(fixture.Profile.CanActivate);
        Assert.True(fixture.Verification.Passed);
        Assert.Equal(fixture.Policy.Reference, fixture.Profile.AcceptancePolicy);
        Assert.Equal(fixture.Candidate.CandidateContentHash,
            fixture.Profile.Content.SourceEvidenceHash);
        Assert.Equal(fixture.Verification.Submission.IndependentReference,
            fixture.Policy.PhysicalVerification.IndependentReference);

        var bytes = fixture.Verification.Submission.Evidence.GetBytes();
        bytes[0] = 0;
        Assert.NotEqual(bytes[0], fixture.Verification.Submission.Evidence.GetBytes()[0]);
    }

    internal static Fixture CreateFixture()
    {
        var procedure = Contract("calibration-procedure");
        var input = Contract("calibration-input");
        var coefficient = Contract("calibration-coefficients");
        var receipt = Contract("calibration-receipt");
        var computationEvidence = Contract("calibration-evidence");
        var independent = Contract("independent-reference");

        var policy = new CalibrationAcceptancePolicy(
            "calibration-policy", "1", CalibrationKind.Intrinsic, "DevelopmentCalibration",
            procedure, input, coefficient, receipt, computationEvidence,
            RequiredSection(CalibrationAcceptanceGateCategory.Sample,
                new CalibrationMetricGate("sample-count",
                    CalibrationPolicyFactReference.IncludedFrameCount,
                    CalibrationGateComparison.MinimumInclusive, 1)),
            RequiredSection(CalibrationAcceptanceGateCategory.Coverage,
                new CalibrationMetricGate("coverage",
                    CalibrationPolicyFactReference.SelectionImageCoverage,
                    CalibrationGateComparison.MinimumInclusive, 0)),
            new CalibrationGateSection(CalibrationAcceptanceGateCategory.PoseDiversity,
                CalibrationPolicyApplicability.NotApplicable,
                notApplicableReason: "Intrinsic fixture has no pose diversity metric."),
            new CalibrationGateSection(CalibrationAcceptanceGateCategory.MaximumPerImageResidual,
                CalibrationPolicyApplicability.NotApplicable,
                notApplicableReason: "Intrinsic fixture has no per-image residual metric."),
            RequiredSection(CalibrationAcceptanceGateCategory.MaximumPerPointResidual,
                new CalibrationMetricGate("point-residual",
                    CalibrationPolicyFactReference.ProcedureMetric("Rms", "pixels"),
                    CalibrationGateComparison.MaximumInclusive, 1)),
            RequiredSection(CalibrationAcceptanceGateCategory.InvalidObservation,
                new CalibrationMetricGate("excluded-count",
                    CalibrationPolicyFactReference.ExcludedFrameCount,
                    CalibrationGateComparison.MaximumInclusive, 0)),
            new PhysicalCalibrationVerificationRequirement(
                CalibrationPolicyApplicability.Required, procedure, computationEvidence,
                independent, TimeSpan.FromHours(1), new[]
                {
                    new CalibrationMetricGate("physical-scale",
                        CalibrationPolicyFactReference.PhysicalVerificationMetric("Scale", "mm/pixel"),
                        CalibrationGateComparison.MaximumInclusive, 1)
                }));

        var candidate = new CalibrationCandidateReference(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"), HashA);
        var sections = new[]
        {
            RequiredResult(CalibrationAcceptanceGateCategory.Sample,
                new CalibrationMetricGateResult("sample-count", CalibrationGateOutcome.Passed,
                    1, "frames", "CalibrationSampleSufficient")),
            RequiredResult(CalibrationAcceptanceGateCategory.Coverage,
                new CalibrationMetricGateResult("coverage", CalibrationGateOutcome.Passed,
                    0.25, "fraction", "CalibrationCoverageSufficient")),
            new CalibrationGateSectionResult(CalibrationAcceptanceGateCategory.PoseDiversity,
                CalibrationPolicyApplicability.NotApplicable, Array.Empty<CalibrationMetricGateResult>(),
                "Intrinsic fixture has no pose diversity metric."),
            new CalibrationGateSectionResult(CalibrationAcceptanceGateCategory.MaximumPerImageResidual,
                CalibrationPolicyApplicability.NotApplicable, Array.Empty<CalibrationMetricGateResult>(),
                "Intrinsic fixture has no per-image residual metric."),
            RequiredResult(CalibrationAcceptanceGateCategory.MaximumPerPointResidual,
                new CalibrationMetricGateResult("point-residual", CalibrationGateOutcome.Passed,
                    0.2, "pixels", "CalibrationPointResidualWithinLimit")),
            RequiredResult(CalibrationAcceptanceGateCategory.InvalidObservation,
                new CalibrationMetricGateResult("excluded-count", CalibrationGateOutcome.Passed,
                    0, "frames", "CalibrationNoInvalidObservation"))
        };
        var actor = new CalibrationGovernanceActor(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Guid.Parse("44444444-4444-4444-4444-444444444444"), 9);
        var evaluation = new CalibrationPolicyEvaluationRecord(
            2, Guid.Parse("55555555-5555-5555-5555-555555555555"),
            Guid.Parse("66666666-6666-6666-6666-666666666666"), candidate,
            policy.Reference, sections, Array.Empty<string>(), actor,
            new DateTimeOffset(2026, 9, 9, 1, 2, 3, TimeSpan.Zero));
        var geometry = new CalibrationFrameGeometry(new RegionOfInterest(0, 0, 640, 480),
            640, 480, VisionPixelFormat.Mono8, null);
        var requirement = new CalibrationRequirement("TopCamera", CalibrationKind.Intrinsic,
            "DevelopmentCalibration", coefficient, policy.Reference);
        var device = new CameraBindingTarget(
            new CameraProviderIdentity("fixture-provider", "1", "fixture-adapter", "1"),
            "fixture-camera");
        var imaging = new ImagingSetupRevisionReference("TopCamera",
            Guid.Parse("77777777-7777-7777-7777-777777777777"), 1, HashB);
        var content = new CalibrationProfileContent(requirement, device, imaging, geometry, geometry,
            new CalibrationCoefficientPayload(coefficient, new byte[] { 1, 2, 3 }), procedure,
            candidate.CandidateContentHash,
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            new DateTimeOffset(2026, 9, 9, 1, 3, 3, TimeSpan.Zero));
        var profile = new PublishedCalibrationProfileVersion(
            3, Guid.Parse("88888888-8888-8888-8888-888888888888"),
            Guid.Parse("99999999-9999-9999-9999-999999999999"), 1, null, content, candidate,
            evaluation.Reference, policy.Reference, actor,
            new DateTimeOffset(2026, 9, 9, 1, 4, 3, TimeSpan.Zero));
        var performed = new DateTimeOffset(2026, 9, 9, 1, 5, 3, TimeSpan.Zero);
        var submission = new PhysicalCalibrationVerificationSubmission(
            procedure, independent, performed,
            new[] { new CalibrationQualityMetric("Scale", 0.75, "mm/pixel") },
            new PhysicalCalibrationVerificationEvidencePayload(computationEvidence,
                new byte[] { 4, 5, 6 }));
        var verification = new PhysicalCalibrationVerificationRecord(
            4, Guid.Parse("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA"),
            Guid.Parse("BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB"), profile.Reference,
            policy.Reference, submission,
            new[] { new CalibrationMetricGateResult("physical-scale", CalibrationGateOutcome.Passed,
                0.75, "mm/pixel", "PhysicalScaleWithinLimit") }, Array.Empty<string>(), actor,
            new DateTimeOffset(2026, 9, 9, 1, 6, 3, TimeSpan.Zero), performed.AddHours(1));
        var revision = new CalibrationAcceptancePolicyRevision(
            1, Guid.Parse("CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC"), policy, null, actor,
            new DateTimeOffset(2026, 9, 9, 1, 1, 3, TimeSpan.Zero));
        return new(policy, candidate, revision, evaluation, profile, verification);
    }

    private static CalibrationGateSection RequiredSection(
        CalibrationAcceptanceGateCategory category, CalibrationMetricGate gate) =>
        new(category, CalibrationPolicyApplicability.Required, new[] { gate });

    private static CalibrationGateSectionResult RequiredResult(
        CalibrationAcceptanceGateCategory category, CalibrationMetricGateResult result) =>
        new(category, CalibrationPolicyApplicability.Required, new[] { result });

    private static RecipeContractReference Contract(string id) =>
        new(id, "1", HashD);

    internal sealed record Fixture(CalibrationAcceptancePolicy Policy,
        CalibrationCandidateReference Candidate,
        CalibrationAcceptancePolicyRevision PolicyRevision,
        CalibrationPolicyEvaluationRecord Evaluation,
        PublishedCalibrationProfileVersion Profile,
        PhysicalCalibrationVerificationRecord Verification);
}
