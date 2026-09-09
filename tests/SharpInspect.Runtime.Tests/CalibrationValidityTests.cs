using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CalibrationValidityTests
{
    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string HashD = "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD";

    [Fact]
    public void V127_V01_ProjectorDistinguishesMissingCurrentAndExactExpiryWithoutChangingProfile()
    {
        var fixture = Fixture.Create();
        var coefficientHash = fixture.Profile.Content.Coefficients.ContentHash;
        var due = fixture.Verification.ValidUntilUtc!.Value;

        var missing = CalibrationValidityProjector.Project(fixture.Profile, fixture.Policy,
            Array.Empty<PhysicalCalibrationVerificationRecord>(), CalibrationCompatibilityState.Compatible,
            fixture.Verification.Submission.PerformedAtUtc);
        Assert.Equal(CalibrationVerificationState.Missing, missing.Verification);
        Assert.Null(missing.LatestVerification);
        Assert.Contains("CalibrationPhysicalVerificationMissing", missing.ReasonCodes);
        Assert.False(missing.CanAdmitNewProductionTrigger);

        var current = CalibrationValidityProjector.Project(fixture.Profile, fixture.Policy,
            new[] { fixture.Verification }, CalibrationCompatibilityState.Compatible, due.AddTicks(-1));
        Assert.Equal(CalibrationVerificationState.Current, current.Verification);
        Assert.Equal(due, current.ValidUntilUtc!.Value);

        var expired = CalibrationValidityProjector.Project(fixture.Profile, fixture.Policy,
            new[] { fixture.Verification }, CalibrationCompatibilityState.Compatible, due);
        Assert.Equal(CalibrationVerificationState.Expired, expired.Verification);
        Assert.Contains("CalibrationVerificationOverdue", expired.ReasonCodes);
        Assert.Equal(coefficientHash, fixture.Profile.Content.Coefficients.ContentHash);
        Assert.Equal(fixture.Profile.ContentHash, current.Profile.ContentHash);
        Assert.Equal(fixture.Profile.ContentHash, expired.Profile.ContentHash);
    }

    [Fact]
    public void V127_V02_LatestFailedVerificationOverridesAnOlderPassedVerification()
    {
        var fixture = Fixture.Create();
        var failed = fixture.CreateVerification(
            fixture.Verification.Submission.PerformedAtUtc.AddMinutes(5), passed: false,
            evidenceMarker: 9);

        var snapshot = CalibrationValidityProjector.Project(fixture.Profile, fixture.Policy,
            new[] { fixture.Verification, failed }, CalibrationCompatibilityState.Compatible,
            failed.Submission.PerformedAtUtc.AddMinutes(1));

        Assert.Equal(CalibrationVerificationState.Failed, snapshot.Verification);
        Assert.Equal(failed.Reference, snapshot.LatestVerification);
        Assert.Contains("CalibrationPhysicalVerificationFailed", snapshot.ReasonCodes);
        Assert.Null(snapshot.ValidUntilUtc);
    }

    [Fact]
    public void V127_V03_ValidityRequiresTheExactPolicyReferenceAndPreservesOlderPolicyIdentity()
    {
        var fixture = Fixture.Create();
        var newerPolicy = CreatePolicy("2", fixture.Policy.PhysicalVerification.ValidityInterval);
        var oldProfileHash = fixture.Profile.ContentHash;
        var oldCoefficientHash = fixture.Profile.Content.Coefficients.ContentHash;

        var exception = Assert.Throws<ArgumentException>(() => CalibrationValidityProjector.Project(
            fixture.Profile, newerPolicy, Array.Empty<PhysicalCalibrationVerificationRecord>(),
            CalibrationCompatibilityState.Compatible, fixture.Verification.Submission.PerformedAtUtc));

        Assert.Equal("CalibrationValidityReferenceMismatch", exception.Message);
        Assert.NotEqual(fixture.Policy.Reference, newerPolicy.Reference);
        Assert.Equal(fixture.Policy.Reference, fixture.Profile.AcceptancePolicy);
        Assert.Equal(oldProfileHash, fixture.Profile.ContentHash);
        Assert.Equal(oldCoefficientHash, fixture.Profile.Content.Coefficients.ContentHash);
    }

    [Fact]
    public async Task V127_V04_RegistryMissingProcedureAndProjectionRejectsRecomputedMetricMismatch()
    {
        var fixture = Fixture.Create();
        var missing = await new PhysicalCalibrationVerificationRegistry(
                Array.Empty<IPhysicalCalibrationVerificationProcedure>())
            .EvaluateAsync(fixture.Profile, fixture.Verification.Submission,
                TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal("PhysicalVerificationProcedureUnavailable", missing.Failure);
        Assert.Empty(missing.Metrics);

        var missingDecision = CalibrationGovernanceProjection.Decide(
            Command(fixture.Profile.Reference, fixture.Verification.Submission),
            new object[] { fixture.PolicyRevision, fixture.Profile }, null, fixture.Actor,
            fixture.Verification.Submission.PerformedAtUtc.AddMinutes(1), null, missing);
        var missingRecord = Assert.IsType<PhysicalCalibrationVerificationRecord>(missingDecision.Record);
        Assert.Equal("PhysicalCalibrationVerificationFailed", missingDecision.ReasonCode);
        Assert.False(missingRecord.Passed);
        Assert.Contains("PhysicalVerificationProcedureUnavailable", missingRecord.BindingFailures);

        var recomputed = new PhysicalVerificationComputation(
            fixture.Profile.ContentHash, fixture.Verification.Submission.ContentHash,
            new[] { new CalibrationQualityMetric("Scale", 0.75, "mm/pixel") }, null);
        var changedSubmission = fixture.CreateSubmission(
            fixture.Verification.Submission.PerformedAtUtc.AddMinutes(10), evidenceMarker: 10,
            scale: 0.70);
        var decision = CalibrationGovernanceProjection.Decide(
            Command(fixture.Profile.Reference, changedSubmission),
            new object[] { fixture.PolicyRevision, fixture.Profile }, null, fixture.Actor,
            changedSubmission.PerformedAtUtc.AddMinutes(1), null,
            new PhysicalVerificationComputation(fixture.Profile.ContentHash,
                changedSubmission.ContentHash, recomputed.Metrics, null));

        var record = Assert.IsType<PhysicalCalibrationVerificationRecord>(decision.Record);
        Assert.Equal("PhysicalCalibrationVerificationFailed", decision.ReasonCode);
        Assert.False(record.Passed);
        Assert.Null(record.ValidUntilUtc);
        Assert.Contains("PhysicalVerificationSubmittedMetricsMismatch", record.BindingFailures);
    }

    [Fact]
    public void V127_V08_PhysicalVerificationContextExposesOnlyBoundInputs()
    {
        var names = typeof(PhysicalCalibrationVerificationContext)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Evidence", "IndependentReference", "PerformedAtUtc", "Profile" }, names);
        Assert.DoesNotContain("Submission", names);
        Assert.DoesNotContain("Metrics", names);
        Assert.DoesNotContain(typeof(PhysicalCalibrationVerificationSubmission),
            typeof(PhysicalCalibrationVerificationContext)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.PropertyType));
        Assert.Equal(typeof(PhysicalCalibrationVerificationEvidencePayload),
            typeof(PhysicalCalibrationVerificationContext).GetProperty("Evidence")!.PropertyType);
    }

    [Fact]
    public async Task V127_V09_RegistryRejectsSubmittedMetricsThatDifferFromRecomputation()
    {
        var fixture = Fixture.Create();
        var procedure = new CapturingProcedure(fixture.Policy.ProcedureContract,
            fixture.Policy.ComputationEvidenceContract,
            new[] { new CalibrationQualityMetric("Scale", 0.70, "mm/pixel") });
        var registry = new PhysicalCalibrationVerificationRegistry(new[] { procedure });

        var result = await registry.EvaluateAsync(fixture.Profile, fixture.Verification.Submission,
            TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal("PhysicalVerificationSubmittedMetricsMismatch", result.Failure);
        Assert.Single(result.Metrics);
        Assert.Equal(0.70, result.Metrics[0].Value);
        Assert.Same(fixture.Profile, procedure.Profile);
        Assert.Equal(fixture.Verification.Submission.IndependentReference,
            procedure.IndependentReference);
        Assert.Equal(fixture.Verification.Submission.PerformedAtUtc, procedure.PerformedAtUtc);
        Assert.Equal(fixture.Verification.Submission.Evidence.Format, procedure.EvidenceFormat);
        Assert.True(fixture.Verification.Submission.Evidence.GetBytes()
            .SequenceEqual(procedure.EvidenceBytes));
    }

    [Fact]
    public void V127_V05_PhysicalProjectionRejectsReplayFutureAndValidityOverflow()
    {
        var fixture = Fixture.Create();
        var changedTime = fixture.CreateSubmission(
            fixture.Verification.Submission.PerformedAtUtc.AddMinutes(5), evidenceMarker: 4);
        var replay = CalibrationGovernanceProjection.Decide(
            Command(fixture.Profile.Reference, changedTime),
            new object[] { fixture.PolicyRevision, fixture.Profile, fixture.Verification }, null, fixture.Profile.Actor,
            fixture.Verification.RecordedAtUtc.AddMinutes(10), null, null);
        Assert.Null(replay.Record);
        Assert.Equal("PhysicalVerificationEvidenceAlreadyRecorded", replay.ReasonCode);

        var future = fixture.CreateSubmission(
            fixture.Verification.Submission.PerformedAtUtc.AddHours(4), evidenceMarker: 11);
        var futureDecision = CalibrationGovernanceProjection.Decide(
            Command(fixture.Profile.Reference, future),
            new object[] { fixture.PolicyRevision, fixture.Profile }, null, fixture.Profile.Actor,
            future.PerformedAtUtc.AddMinutes(-1), null, null);
        Assert.Null(futureDecision.Record);
        Assert.Equal("PhysicalVerificationFutureTimeRejected", futureDecision.ReasonCode);

        var overflowFixture = Fixture.Create(CreatePolicy("1", TimeSpan.FromTicks(1)));
        var overflowSubmission = overflowFixture.CreateSubmission(
            DateTimeOffset.MaxValue, evidenceMarker: 12);
        var overflowDecision = CalibrationGovernanceProjection.Decide(
            Command(overflowFixture.Profile.Reference, overflowSubmission),
            new object[] { overflowFixture.PolicyRevision, overflowFixture.Profile }, null, overflowFixture.Profile.Actor,
            DateTimeOffset.MaxValue, null, null);
        Assert.Null(overflowDecision.Record);
        Assert.Equal("PhysicalVerificationValidityOutOfRange", overflowDecision.ReasonCode);
    }

    [Fact]
    public async Task V127_V06_NonCooperativeRegistryProcedureRetainsItsSlotAfterTimeout()
    {
        var fixture = Fixture.Create();
        var procedure = new BlockingProcedure(fixture.Policy.ProcedureContract,
            fixture.Policy.ComputationEvidenceContract);
        var registry = new PhysicalCalibrationVerificationRegistry(new[]
        {
            procedure
        });

        var timedOut = await registry.EvaluateAsync(fixture.Profile, fixture.Verification.Submission,
            TimeSpan.FromMilliseconds(20), CancellationToken.None);
        Assert.Equal("PhysicalVerificationProcedureDeadlineExceeded", timedOut.Failure);

        var busy = await registry.EvaluateAsync(fixture.Profile, fixture.Verification.Submission,
            TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.Equal("PhysicalVerificationProcedureBusy", busy.Failure);

        procedure.Release();
        PhysicalVerificationComputation? completed = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            completed = await registry.EvaluateAsync(fixture.Profile, fixture.Verification.Submission,
                TimeSpan.FromSeconds(1), CancellationToken.None);
            if (completed.Failure != "PhysicalVerificationProcedureBusy")
                break;
            await Task.Delay(10);
        }

        Assert.NotNull(completed);
        Assert.Null(completed!.Failure);
        Assert.Single(completed.Metrics);
        Assert.True(procedure.CallCount >= 1);
    }

    [Fact]
    public void V127_V07_RunSnapshotCapturesImmutableDevelopmentBindingsOnly()
    {
        var fixture = Fixture.Create();
        var requirementHash = fixture.Profile.Content.Requirement.ContentHash;
        var binding = new CalibrationRunProfileBinding(requirementHash, fixture.Profile,
            fixture.Verification.Reference, fixture.Verification.ValidUntilUtc);
        var snapshot = new CalibrationRunSnapshot(
            fixture.Verification.RecordedAtUtc.AddMinutes(1), new[] { binding });
        var sameSnapshot = new CalibrationRunSnapshot(
            fixture.Verification.RecordedAtUtc.AddMinutes(1), new[] { binding });

        Assert.Equal(requirementHash, binding.RequirementContentHash);
        Assert.Equal(fixture.Profile.Reference, binding.Profile);
        Assert.Equal(fixture.Profile.Content.ImagingSetup, binding.ImagingSetup);
        Assert.Equal(fixture.Profile.AcceptancePolicy, binding.Policy);
        Assert.Equal(fixture.Profile.Evaluation, binding.Evaluation);
        Assert.Equal(fixture.Profile.SourceCandidate, binding.SourceCandidate);
        Assert.Equal(fixture.Verification.Reference, binding.Verification);
        Assert.Equal(fixture.Verification.ValidUntilUtc, binding.ValidUntilUtc);
        Assert.Equal(fixture.Verification.RecordedAtUtc.AddMinutes(1), snapshot.AcceptedAtUtc);
        Assert.Equal(binding.ContentHash, snapshot.Profiles[0].ContentHash);
        Assert.Equal(snapshot.ContentHash, sameSnapshot.ContentHash);
        Assert.Single(snapshot.Profiles);
        Assert.True(fixture.Profile.DevelopmentOnly);
        Assert.False(fixture.Profile.ProductionAuthority);
        Assert.False(fixture.Profile.CanActivate);
    }

    private static RecordPhysicalCalibrationVerificationCommand Command(
        CalibrationProfileReference profile, PhysicalCalibrationVerificationSubmission submission) =>
        new(Guid.NewGuid(), new CommandInvocation(CommandSource.PhysicalConsole,
            Fixture.ActorId.ToString("D"), Fixture.InteractiveSessionId, Fixture.GrantId),
            profile, submission, "fixture verification");

    private static CalibrationAcceptancePolicy CreatePolicy(string version = "1",
        TimeSpan? validityInterval = null)
    {
        var procedure = Contract("calibration-procedure");
        var input = Contract("calibration-input");
        var coefficient = Contract("calibration-coefficients");
        var receipt = Contract("calibration-receipt");
        var computationEvidence = Contract("calibration-evidence");
        var independent = Contract("independent-reference");

        return new CalibrationAcceptancePolicy(
            "calibration-policy", version, CalibrationKind.Intrinsic, "DevelopmentCalibration",
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
                notApplicableReason: "Intrinsic validity fixture has no pose diversity metric."),
            new CalibrationGateSection(CalibrationAcceptanceGateCategory.MaximumPerImageResidual,
                CalibrationPolicyApplicability.NotApplicable,
                notApplicableReason: "Intrinsic validity fixture has no per-image residual metric."),
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
                independent, validityInterval ?? TimeSpan.FromHours(1), new[]
                {
                    new CalibrationMetricGate("physical-scale",
                        CalibrationPolicyFactReference.PhysicalVerificationMetric("Scale", "mm/pixel"),
                        CalibrationGateComparison.MaximumInclusive, 1)
                }));
    }

    private static CalibrationGateSection RequiredSection(
        CalibrationAcceptanceGateCategory category, CalibrationMetricGate gate) =>
        new(category, CalibrationPolicyApplicability.Required, new[] { gate });

    private static RecipeContractReference Contract(string id) => new(id, "1", HashD);

    internal sealed class Fixture
    {
        private Fixture(CalibrationAcceptancePolicy policy,
            CalibrationCandidateReference candidate,
            CalibrationAcceptancePolicyRevision policyRevision,
            CalibrationPolicyEvaluationRecord evaluation,
            PublishedCalibrationProfileVersion profile,
            CalibrationGovernanceActor actor,
            PhysicalCalibrationVerificationRecord verification)
        {
            Policy = policy;
            Candidate = candidate;
            PolicyRevision = policyRevision;
            Evaluation = evaluation;
            Profile = profile;
            Actor = actor;
            Verification = verification;
        }

        internal static readonly Guid ActorId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        internal static readonly Guid InteractiveSessionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        internal static readonly Guid GrantId = Guid.Parse("55555555-5555-5555-5555-555555555555");

        internal CalibrationAcceptancePolicy Policy { get; }
        internal CalibrationCandidateReference Candidate { get; }
        internal CalibrationAcceptancePolicyRevision PolicyRevision { get; }
        internal CalibrationPolicyEvaluationRecord Evaluation { get; }
        internal PublishedCalibrationProfileVersion Profile { get; }
        internal CalibrationGovernanceActor Actor { get; }
        internal PhysicalCalibrationVerificationRecord Verification { get; }

        internal static Fixture Create(CalibrationAcceptancePolicy? policy = null)
        {
            policy ??= CreatePolicy();
            var candidate = new CalibrationCandidateReference(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"), HashA);
            var actor = new CalibrationGovernanceActor(ActorId, InteractiveSessionId, 9);
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
                    "Intrinsic validity fixture has no pose diversity metric."),
                new CalibrationGateSectionResult(CalibrationAcceptanceGateCategory.MaximumPerImageResidual,
                    CalibrationPolicyApplicability.NotApplicable, Array.Empty<CalibrationMetricGateResult>(),
                    "Intrinsic validity fixture has no per-image residual metric."),
                RequiredResult(CalibrationAcceptanceGateCategory.MaximumPerPointResidual,
                    new CalibrationMetricGateResult("point-residual", CalibrationGateOutcome.Passed,
                        0.2, "pixels", "CalibrationPointResidualWithinLimit")),
                RequiredResult(CalibrationAcceptanceGateCategory.InvalidObservation,
                    new CalibrationMetricGateResult("excluded-count", CalibrationGateOutcome.Passed,
                        0, "frames", "CalibrationNoInvalidObservation"))
            };
            var evaluation = new CalibrationPolicyEvaluationRecord(
                2, Guid.Parse("66666666-6666-6666-6666-666666666666"),
                Guid.Parse("77777777-7777-7777-7777-777777777777"), candidate,
                policy.Reference, sections, Array.Empty<string>(), actor,
                new DateTimeOffset(2026, 9, 9, 1, 2, 3, TimeSpan.Zero));
            var geometry = new CalibrationFrameGeometry(new RegionOfInterest(0, 0, 640, 480),
                640, 480, VisionPixelFormat.Mono8, null);
            var requirement = new CalibrationRequirement("TopCamera", CalibrationKind.Intrinsic,
                "DevelopmentCalibration", policy.CoefficientContract, policy.Reference);
            var device = new CameraBindingTarget(
                new CameraProviderIdentity("fixture-provider", "1", "fixture-adapter", "1"),
                "fixture-camera");
            var imaging = new ImagingSetupRevisionReference("TopCamera",
                Guid.Parse("88888888-8888-8888-8888-888888888888"), 1, HashB);
            var content = new CalibrationProfileContent(requirement, device, imaging, geometry, geometry,
                new CalibrationCoefficientPayload(policy.CoefficientContract, new byte[] { 1, 2, 3 }),
                policy.ProcedureContract, candidate.CandidateContentHash, ActorId,
                new DateTimeOffset(2026, 9, 9, 1, 3, 3, TimeSpan.Zero));
            var profile = new PublishedCalibrationProfileVersion(
                3, Guid.Parse("99999999-9999-9999-9999-999999999999"),
                Guid.Parse("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA"), 1, null, content,
                candidate, evaluation.Reference, policy.Reference, actor,
                new DateTimeOffset(2026, 9, 9, 1, 4, 3, TimeSpan.Zero));
            var performed = new DateTimeOffset(2026, 9, 9, 1, 5, 3, TimeSpan.Zero);
            var submission = new PhysicalCalibrationVerificationSubmission(
                policy.ProcedureContract, policy.PhysicalVerification.IndependentReference!, performed,
                new[] { new CalibrationQualityMetric("Scale", 0.75, "mm/pixel") },
                new PhysicalCalibrationVerificationEvidencePayload(
                    policy.ComputationEvidenceContract, new byte[] { 4, 5, 6 }));
            var verification = new PhysicalCalibrationVerificationRecord(
                4, Guid.Parse("BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB"),
                Guid.Parse("CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC"), profile.Reference,
                policy.Reference, submission,
                new[] { new CalibrationMetricGateResult("physical-scale",
                    CalibrationGateOutcome.Passed, 0.75, "mm/pixel", "PhysicalScaleWithinLimit") },
                Array.Empty<string>(), actor, performed.AddMinutes(1),
                performed.Add(policy.PhysicalVerification.ValidityInterval!.Value));
            var revision = new CalibrationAcceptancePolicyRevision(
                1, Guid.Parse("DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD"), policy, null, actor,
                new DateTimeOffset(2026, 9, 9, 1, 1, 3, TimeSpan.Zero));
            return new(policy, candidate, revision, evaluation, profile, actor, verification);
        }

        internal PhysicalCalibrationVerificationSubmission CreateSubmission(
            DateTimeOffset performedAtUtc, byte evidenceMarker, double scale = 0.75)
        {
            return new PhysicalCalibrationVerificationSubmission(
                Policy.ProcedureContract, Policy.PhysicalVerification.IndependentReference!,
                performedAtUtc, new[] { new CalibrationQualityMetric("Scale", scale, "mm/pixel") },
                new PhysicalCalibrationVerificationEvidencePayload(
                    Policy.ComputationEvidenceContract, new[] { evidenceMarker, (byte)5, (byte)6 }));
        }

        internal PhysicalCalibrationVerificationRecord CreateVerification(
            DateTimeOffset performedAtUtc, bool passed, byte evidenceMarker)
        {
            var submission = CreateSubmission(performedAtUtc, evidenceMarker,
                passed ? 0.75 : 1.5);
            var outcome = passed ? CalibrationGateOutcome.Passed : CalibrationGateOutcome.Failed;
            var failures = passed
                ? Array.Empty<string>()
                : new[] { "PhysicalVerificationProcedureFailed" };
            return new PhysicalCalibrationVerificationRecord(
                Verification.Position + 1, Guid.NewGuid(), Guid.NewGuid(), Profile.Reference,
                Policy.Reference, submission,
                new[] { new CalibrationMetricGateResult("physical-scale", outcome,
                    passed ? 0.75 : 1.5, "mm/pixel", passed
                        ? "PhysicalScaleWithinLimit" : "PhysicalVerificationProcedureFailed") },
                failures, Actor, performedAtUtc.AddMinutes(1),
                passed ? performedAtUtc.Add(Policy.PhysicalVerification.ValidityInterval!.Value) : null);
        }

        private static CalibrationGateSectionResult RequiredResult(
            CalibrationAcceptanceGateCategory category, CalibrationMetricGateResult result) =>
            new(category, CalibrationPolicyApplicability.Required, new[] { result });
    }

    private sealed class BlockingProcedure : IPhysicalCalibrationVerificationProcedure
    {
        private readonly TaskCompletionSource<bool> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal BlockingProcedure(RecipeContractReference procedureContract,
            RecipeContractReference evidenceContract)
        {
            ProcedureContract = procedureContract;
            EvidenceContract = evidenceContract;
        }

        public RecipeContractReference ProcedureContract { get; }
        public RecipeContractReference EvidenceContract { get; }
        internal int CallCount { get; private set; }

        public IReadOnlyList<CalibrationQualityMetric> Evaluate(
            PhysicalCalibrationVerificationContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            _release.Task.GetAwaiter().GetResult();
            return new[] { new CalibrationQualityMetric("Scale", 0.75, "mm/pixel") };
        }

        internal void Release() => _release.TrySetResult(true);
    }

    private sealed class CapturingProcedure : IPhysicalCalibrationVerificationProcedure
    {
        private readonly IReadOnlyList<CalibrationQualityMetric> _metrics;

        internal CapturingProcedure(RecipeContractReference procedureContract,
            RecipeContractReference evidenceContract, IReadOnlyList<CalibrationQualityMetric> metrics)
        {
            ProcedureContract = procedureContract;
            EvidenceContract = evidenceContract;
            _metrics = metrics;
        }

        public RecipeContractReference ProcedureContract { get; }
        public RecipeContractReference EvidenceContract { get; }
        internal PublishedCalibrationProfileVersion? Profile { get; private set; }
        internal RecipeContractReference? IndependentReference { get; private set; }
        internal DateTimeOffset PerformedAtUtc { get; private set; }
        internal RecipeContractReference? EvidenceFormat { get; private set; }
        internal byte[] EvidenceBytes { get; private set; } = Array.Empty<byte>();

        public IReadOnlyList<CalibrationQualityMetric> Evaluate(
            PhysicalCalibrationVerificationContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Profile = context.Profile;
            IndependentReference = context.IndependentReference;
            PerformedAtUtc = context.PerformedAtUtc;
            EvidenceFormat = context.Evidence.Format;
            EvidenceBytes = context.Evidence.GetBytes();
            return _metrics;
        }
    }
}
