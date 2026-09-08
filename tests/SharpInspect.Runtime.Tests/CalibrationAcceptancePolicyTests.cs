using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CalibrationAcceptancePolicyTests
{
    [Fact]
    public void V127_P01_CompletePolicyHasFrozenSectionsAndHashBindsThresholds()
    {
        var policy = Policy();
        var changed = Policy(sampleThreshold: 2);

        Assert.Equal(policy.ContentHash, policy.Reference.ContentHash);
        Assert.Equal(6, policy.Sections.Count);
        Assert.Equal(new[]
        {
            CalibrationAcceptanceGateCategory.Sample,
            CalibrationAcceptanceGateCategory.Coverage,
            CalibrationAcceptanceGateCategory.PoseDiversity,
            CalibrationAcceptanceGateCategory.MaximumPerImageResidual,
            CalibrationAcceptanceGateCategory.MaximumPerPointResidual,
            CalibrationAcceptanceGateCategory.InvalidObservation
        }, policy.Sections.Select(section => section.Category));
        Assert.Equal(CalibrationPolicyApplicability.NotApplicable, policy.PoseDiversity.Applicability);
        Assert.Equal("Pose diversity is emitted by a separate registered verifier.",
            policy.PoseDiversity.NotApplicableReason);
        Assert.NotEqual(policy.ContentHash, changed.ContentHash);
        Assert.NotEqual(policy.Sample.ContentHash, changed.Sample.ContentHash);
    }

    [Theory]
    [InlineData(CalibrationPolicyFactKind.IncludedFrameCount, 0d)]
    [InlineData(CalibrationPolicyFactKind.IncludedFrameCount, 1.5d)]
    [InlineData(CalibrationPolicyFactKind.IncludedFrameCount, 65d)]
    [InlineData(CalibrationPolicyFactKind.SufficientFeatureFrameCount, 0d)]
    [InlineData(CalibrationPolicyFactKind.ExcludedFrameCount, -1d)]
    [InlineData(CalibrationPolicyFactKind.ExcludedFrameCount, 0.5d)]
    [InlineData(CalibrationPolicyFactKind.ExcludedFrameCount, 65d)]
    [InlineData(CalibrationPolicyFactKind.SelectionImageCoverage, -0.01d)]
    [InlineData(CalibrationPolicyFactKind.SelectionImageCoverage, 1.01d)]
    public void V127_P02_TypedSessionFactThresholdsRejectOutOfDomainValues(
        CalibrationPolicyFactKind kind, double threshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CalibrationMetricGate(
            "typed-threshold", Fact(kind),
            kind == CalibrationPolicyFactKind.SelectionImageCoverage
                ? CalibrationGateComparison.MinimumInclusive
                : kind == CalibrationPolicyFactKind.ExcludedFrameCount
                    ? CalibrationGateComparison.MaximumInclusive
                    : CalibrationGateComparison.MinimumInclusive,
            threshold));
    }

    [Fact]
    public void V127_P03_MetricEvaluationRequiresExactKeyAndUnit()
    {
        var gate = new CalibrationMetricGate("rms-gate",
            CalibrationPolicyFactReference.ProcedureMetric("RmsPixels", "pixels"),
            CalibrationGateComparison.MaximumInclusive, 1);

        var missing = CalibrationAcceptancePolicyEvaluator.EvaluateMetric(gate,
            Array.Empty<CalibrationQualityMetric>());
        Assert.Equal(CalibrationGateOutcome.Failed, missing.Outcome);
        Assert.Equal("CalibrationPolicyMetricMissing", missing.ReasonCode);

        var unitMismatch = CalibrationAcceptancePolicyEvaluator.EvaluateMetric(gate,
            new[] { new CalibrationQualityMetric("RmsPixels", 0.2, "millimeters") });
        Assert.Equal(CalibrationGateOutcome.Failed, unitMismatch.Outcome);
        Assert.Equal("CalibrationPolicyMetricUnitMismatch", unitMismatch.ReasonCode);
        Assert.Equal(0.2, unitMismatch.ActualValue);
        Assert.Equal("millimeters", unitMismatch.ActualUnit);

        var passed = CalibrationAcceptancePolicyEvaluator.EvaluateMetric(gate,
            new[] { new CalibrationQualityMetric("RmsPixels", 1, "pixels") });
        Assert.Equal(CalibrationGateOutcome.Passed, passed.Outcome);
        Assert.Equal("CalibrationPolicyGatePassed", passed.ReasonCode);
    }

    [Fact]
    public void V127_P04_ValidCandidateEvaluatesAllSixSectionsAndSelectionIsRechecked()
    {
        var fixture = CandidateFixture.Create(Policy());
        var decision = new CalibrationAcceptancePolicyEvaluator().Evaluate(
            fixture.Policy, fixture.Session, fixture.Candidate);

        Assert.True(decision.Passed, string.Join(",", decision.BindingFailures));
        Assert.Equal(6, decision.Sections.Count);
        Assert.All(decision.Sections.Where(section =>
            section.Applicability == CalibrationPolicyApplicability.Required),
            section => Assert.All(section.Gates, gate =>
                Assert.Equal(CalibrationGateOutcome.Passed, gate.Outcome)));
        Assert.All(decision.Sections.Where(section =>
            section.Applicability == CalibrationPolicyApplicability.NotApplicable),
            section => Assert.True(section.Passed));

        var tamperedSelection = fixture.Session.Selection with { ImageCoverage = 0.75 };
        var tamperedSession = new CalibrationSessionEvidence(fixture.Session.Header,
            fixture.Session.State, fixture.Session.Frames, fixture.Session.Observations,
            fixture.Session.Exclusions, fixture.Session.Candidate, tamperedSelection,
            fixture.Session.TemporaryConfiguration);
        var rejected = new CalibrationAcceptancePolicyEvaluator().Evaluate(
            fixture.Policy, tamperedSession, fixture.Candidate);

        Assert.False(rejected.Passed);
        Assert.Contains("CalibrationCandidateSelectionMismatch", rejected.BindingFailures);
    }

    [Fact]
    public void V127_P05_ReceiptMustMatchThePolicyForIncludedObservations()
    {
        var policy = Policy();
        var missingReceipt = CandidateFixture.Create(policy, receipt: null, includeReceipt: false);
        var missingDecision = new CalibrationAcceptancePolicyEvaluator().Evaluate(
            policy, missingReceipt.Session, missingReceipt.Candidate);
        Assert.False(missingDecision.Passed);
        Assert.Contains("CalibrationPolicyReceiptMissing", missingDecision.BindingFailures);

        var wrongFormat = new CalibrationExtractionReceipt(Contract("wrong.receipt"), new byte[] { 9, 8, 7 });
        var wrongReceipt = CandidateFixture.Create(policy, wrongFormat);
        var wrongDecision = new CalibrationAcceptancePolicyEvaluator().Evaluate(
            policy, wrongReceipt.Session, wrongReceipt.Candidate);
        Assert.False(wrongDecision.Passed);
        Assert.Contains("CalibrationPolicyReceiptContractMismatch", wrongDecision.BindingFailures);
    }

    [Fact]
    public void V127_P06_PhysicalRequirementBindsIndependentReferenceAndRequiredShape()
    {
        var procedure = Contract("physical.procedure");
        var evidence = Contract("physical.evidence");
        var independent = Contract("physical.independent");
        var gates = new[]
        {
            new CalibrationMetricGate("physical-rms",
                CalibrationPolicyFactReference.PhysicalVerificationMetric("RmsMm", "mm"),
                CalibrationGateComparison.MaximumInclusive, 0.5)
        };
        var requirement = new PhysicalCalibrationVerificationRequirement(
            CalibrationPolicyApplicability.Required, procedure, evidence, independent,
            TimeSpan.FromHours(1), gates);
        var changedIndependent = new PhysicalCalibrationVerificationRequirement(
            CalibrationPolicyApplicability.Required, procedure, evidence,
            Contract("physical.independent.v2"), TimeSpan.FromHours(1), gates);

        Assert.Equal(independent, requirement.IndependentReference);
        Assert.NotEqual(requirement.ContentHash, changedIndependent.ContentHash);
        Assert.Throws<ArgumentException>(() => new PhysicalCalibrationVerificationRequirement(
            CalibrationPolicyApplicability.Required, procedure, evidence, null,
            TimeSpan.FromHours(1), gates));
        Assert.Throws<ArgumentException>(() => new PhysicalCalibrationVerificationRequirement(
            CalibrationPolicyApplicability.NotApplicable, null, null, null, null,
            Array.Empty<CalibrationMetricGate>(), null));
    }

    private static CalibrationAcceptancePolicy Policy(double sampleThreshold = 1)
    {
        var procedure = Contract("calibration.procedure");
        var input = Contract("calibration.input");
        var coefficients = Contract("calibration.coefficients");
        var receipt = Contract("calibration.receipt");
        var evidence = Contract("calibration.evidence");
        return new CalibrationAcceptancePolicy(
            "calibration.acceptance", "1", CalibrationKind.Intrinsic, "geometry",
            procedure, input, coefficients, receipt, evidence,
            new CalibrationGateSection(CalibrationAcceptanceGateCategory.Sample,
                CalibrationPolicyApplicability.Required, new[]
                {
                    new CalibrationMetricGate("sample-frames",
                        CalibrationPolicyFactReference.IncludedFrameCount,
                        CalibrationGateComparison.MinimumInclusive, sampleThreshold)
                }),
            new CalibrationGateSection(CalibrationAcceptanceGateCategory.Coverage,
                CalibrationPolicyApplicability.Required, new[]
                {
                    new CalibrationMetricGate("image-coverage",
                        CalibrationPolicyFactReference.SelectionImageCoverage,
                        CalibrationGateComparison.MinimumInclusive, 0)
                }),
            new CalibrationGateSection(CalibrationAcceptanceGateCategory.PoseDiversity,
                CalibrationPolicyApplicability.NotApplicable,
                notApplicableReason: "Pose diversity is emitted by a separate registered verifier."),
            new CalibrationGateSection(CalibrationAcceptanceGateCategory.MaximumPerImageResidual,
                CalibrationPolicyApplicability.NotApplicable,
                notApplicableReason: "Per-image residuals are covered by the retained evidence."),
            new CalibrationGateSection(CalibrationAcceptanceGateCategory.MaximumPerPointResidual,
                CalibrationPolicyApplicability.Required, new[]
                {
                    new CalibrationMetricGate("point-rms",
                        CalibrationPolicyFactReference.ProcedureMetric("PointResidual", "pixels"),
                        CalibrationGateComparison.MaximumInclusive, 1)
                }),
            new CalibrationGateSection(CalibrationAcceptanceGateCategory.InvalidObservation,
                CalibrationPolicyApplicability.Required, new[]
                {
                    new CalibrationMetricGate("excluded-frames",
                        CalibrationPolicyFactReference.ExcludedFrameCount,
                        CalibrationGateComparison.MaximumInclusive, 0)
                }),
            new PhysicalCalibrationVerificationRequirement(
                CalibrationPolicyApplicability.NotApplicable, null, null, null, null, null,
                "No independent physical verification is registered for this policy."));
    }

    private static CalibrationPolicyFactReference Fact(CalibrationPolicyFactKind kind) => kind switch
    {
        CalibrationPolicyFactKind.IncludedFrameCount =>
            CalibrationPolicyFactReference.IncludedFrameCount,
        CalibrationPolicyFactKind.SufficientFeatureFrameCount =>
            CalibrationPolicyFactReference.SufficientFeatureFrameCount,
        CalibrationPolicyFactKind.SelectionImageCoverage =>
            CalibrationPolicyFactReference.SelectionImageCoverage,
        CalibrationPolicyFactKind.ExcludedFrameCount =>
            CalibrationPolicyFactReference.ExcludedFrameCount,
        CalibrationPolicyFactKind.ProcedureMetric =>
            CalibrationPolicyFactReference.ProcedureMetric("metric", "unit"),
        CalibrationPolicyFactKind.PhysicalVerificationMetric =>
            CalibrationPolicyFactReference.PhysicalVerificationMetric("metric", "unit"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static RecipeContractReference Contract(string id) => new(id, "1", Hash);

    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Role = "TopCamera";
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ActorId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid InteractiveSessionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid FrameId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ObservationId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid CandidateId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid GrantId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private sealed class CandidateFixture
    {
        private CandidateFixture(CalibrationAcceptancePolicy policy,
            CalibrationSessionEvidence session, CalibrationCandidateReference candidate)
        {
            Policy = policy;
            Session = session;
            Candidate = candidate;
        }

        internal CalibrationAcceptancePolicy Policy { get; }
        internal CalibrationSessionEvidence Session { get; }
        internal CalibrationCandidateReference Candidate { get; }

        internal static CandidateFixture Create(CalibrationAcceptancePolicy policy,
            CalibrationExtractionReceipt? receipt = null, bool includeReceipt = true)
        {
            var requested = new RequestedCameraConfiguration(
                ProductionAcquisitionMode.SoftwareTrigger, 10, 0,
                new RegionOfInterest(0, 0, 4, 4), VisionPixelFormat.Mono8, null, 100, 0, null);
            var effective = new EffectiveCameraConfiguration(
                ProductionAcquisitionMode.SoftwareTrigger, 10, 0,
                new RegionOfInterest(0, 0, 4, 4), VisionPixelFormat.Mono8, null, 100, 0, null);
            var baselineRequested = new RequestedCameraConfiguration(
                ProductionAcquisitionMode.SoftwareTrigger, 5, 0,
                new RegionOfInterest(0, 0, 4, 4), VisionPixelFormat.Mono8, null, 100, 0, null);
            var baselineEffective = new EffectiveCameraConfiguration(
                ProductionAcquisitionMode.SoftwareTrigger, 5, 0,
                new RegionOfInterest(0, 0, 4, 4), VisionPixelFormat.Mono8, null, 100, 0, null);
            var descriptor = new CalibrationProcedureDescriptor(policy.ProcedureContract,
                policy.InputContract, policy.Kind);
            var input = new CalibrationProcedureInputPayload(policy.InputContract,
                new byte[] { 1, 2, 3 });
            var plan = new CalibrationSessionPlan(
                new CalibrationRequirement(Role, policy.Kind, policy.LogicalPurpose,
                    policy.CoefficientContract, policy.Reference), descriptor, input, requested,
                new CalibrationEvidenceSelectionPolicy("calibration.selection", "1", 1, 1, 0));
            var target = new CameraBindingTarget(
                new CameraProviderIdentity("Test.Provider", "1", "Test.Adapter", "1"),
                "test-device");
            var binding = new CameraBindingRevision(1, Role, 1,
                Guid.Parse("88888888-8888-8888-8888-888888888888"), null, Hash, target,
                ActorId, InteractiveSessionId, 0, "policy test binding", DateTimeOffset.UnixEpoch);
            var command = new StartCalibrationSessionCommand(
                Guid.Parse("99999999-9999-9999-9999-999999999999"),
                new CommandInvocation(CommandSource.PhysicalConsole, ActorId.ToString("D"),
                    InteractiveSessionId, GrantId), plan, binding.Revision, binding.RevisionHash,
                new ImagingSetupRevisionReference(Role,
                    Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 1, Hash),
                "policy test session");
            var header = new CalibrationSessionHeader(SessionId,
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), ActorId,
                InteractiveSessionId, 0, DateTimeOffset.UnixEpoch, command, binding,
                baselineRequested, baselineEffective, Hash);
            var frame = Frame(effective);
            if (includeReceipt)
                receipt ??= new CalibrationExtractionReceipt(policy.ExtractionReceiptContract,
                    new byte[] { 3, 2, 1 });
            var extraction = new CalibrationExtractionResult(
                new[] { new CalibrationImageFeature("corner-0", 1, 1) },
                Array.Empty<CalibrationProcedureDiagnostic>(), includeReceipt ? receipt : null);
            var observation = new CalibrationObservationEvidence(ObservationId, frame,
                descriptor, input.ContentHash, extraction);
            var frames = new[] { frame };
            var observations = new[] { observation };
            var exclusions = Array.Empty<CalibrationEvidenceExclusion>();
            var selection = CalibrationEvidenceSelection.Evaluate(header, frames,
                observations, exclusions);
            var computation = new CalibrationProcedureComputationResult(
                new CalibrationCoefficientPayload(policy.CoefficientContract, new byte[] { 4, 5, 6 }),
                new[] { new CalibrationQualityMetric("PointResidual", 0.25, "pixels") },
                Array.Empty<CalibrationProcedureDiagnostic>(),
                new CalibrationComputationEvidencePayload(policy.ComputationEvidenceContract,
                    new byte[] { 7, 8, 9 }));
            var candidateEvidence = new CalibrationCandidateEvidence(CandidateId, SessionId,
                header.ContentHash, selection.SelectionHash, computation,
                DateTimeOffset.UnixEpoch.AddSeconds(1));
            var state = new CalibrationSessionState(SessionId,
                CalibrationSessionPhase.CandidateRetained, CalibrationSessionOutcome.Pending,
                1, 1, 0, CandidateId, "CalibrationEvidenceSufficient", false);
            var session = new CalibrationSessionEvidence(header, state, frames, observations,
                exclusions, candidateEvidence, selection,
                new CalibrationTemporaryConfigurationEvidence(requested, effective));
            return new CandidateFixture(policy, session,
                new CalibrationCandidateReference(SessionId, CandidateId,
                    candidateEvidence.ContentHash));
        }

        private static CalibrationFrameEvidence Frame(EffectiveCameraConfiguration effective)
        {
            var pixels = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
            var pixelHash = Convert.ToHexString(SHA256.HashData(pixels));
            var correlation = new ExecutionCorrelationId(ExecutionKind.Calibration, FrameId);
            var metadata = new FrameMetadata(correlation, Role, 4, 4, 4,
                VisionPixelFormat.Mono8, null, DateTimeOffset.UnixEpoch, effective);
            var provenance = new FrameProvenance(correlation, "Test.Provider", "1",
                "Test.Adapter", "1", "Test.Sdk", "1", null, "test-device", null, null,
                "Mono8", "CanonicalRows", false, false, null, null,
                new FrameAcquisitionMilestones(1, null, null, null, null));
            return new CalibrationFrameEvidence(SessionId, FrameId, metadata, provenance,
                pixelHash, pixels.Length, pixelHash + ".bin");
        }
    }
}
