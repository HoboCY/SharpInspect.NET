using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Conformance;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ConformanceApplicabilityTests
{
    [Fact]
    public async Task V107_N01_FrozenOptionalProofProducesNotApplicableWithoutInvokingScenario()
    {
        using var fixture = new OptionalConformanceFixture(ConformanceClaim.DevelopmentOnly);

        var result = await fixture.ExecuteOptionalAsync();

        Assert.Equal(ConformanceOutcome.NotApplicable, result.Outcome);
        Assert.Equal("FrozenOptionalExclusion", result.ReasonCode);
        Assert.False(fixture.OptionalScenario.Invoked);

        using var query = new ConformanceQuery(fixture.Options);
        var execution = Assert.Single(query.GetExecutions());
        Assert.Equal(ConformanceOutcome.NotApplicable, execution.Outcome);
        Assert.Equal(result.TestExecutionId, execution.Record!.TestExecutionId);
    }

    [Fact]
    public async Task V107_N02_FormalClaimIsRejectedBeforeReservation()
    {
        using var fixture = new OptionalConformanceFixture(ConformanceClaim.FrameworkRelease);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteOptionalAsync());

        Assert.Equal("FormalConformanceExecutionNotAvailable", error.Message);
        using var query = new ConformanceQuery(fixture.Options);
        Assert.Empty(query.GetExecutions());
        Assert.False(fixture.OptionalScenario.Invoked);
    }

    [Fact]
    public Task V107_N03_A_MissingApplicabilityInputIsRejectedBeforeObserve() =>
        AssertInputChangedAsync(Array.Empty<ConformanceEvidence>());

    [Fact]
    public Task V107_N03_B_MultipleApplicabilityInputsAreRejectedBeforeObserve() =>
        AssertInputChangedAsync(new[]
        {
            new ConformanceEvidence("applicability-proof", Encoding.UTF8.GetBytes("proof")),
            new ConformanceEvidence("unexpected-input", Encoding.UTF8.GetBytes("extra"))
        });

    [Fact]
    public Task V107_N03_C_SameNameWrongApplicabilityInputIsRejectedBeforeObserve() =>
        AssertInputChangedAsync(new[]
        {
            new ConformanceEvidence("applicability-proof", Encoding.UTF8.GetBytes("wrong-proof"))
        });

    private static async Task AssertInputChangedAsync(IReadOnlyList<ConformanceEvidence> inputs)
    {
        using var fixture = new OptionalConformanceFixture(ConformanceClaim.DevelopmentOnly);

        var result = await fixture.ExecuteOptionalAsync(inputs);

        Assert.Equal(ConformanceOutcome.InvalidHarness, result.Outcome);
        Assert.Equal("ConformanceInputChanged", result.ReasonCode);
        Assert.False(fixture.OptionalScenario.Invoked);
    }

    private sealed class OptionalConformanceFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "SharpInspectConformanceTests",
            "V107-N-" + Guid.NewGuid().ToString("N"));
        private readonly string _candidateDirectory;
        private readonly List<ConformanceFileBinding> _bindings = new();
        private readonly string _proof;

        public OptionalConformanceFixture(ConformanceClaim claim)
        {
            Directory.CreateDirectory(_root);
            _candidateDirectory = Path.Combine(_root, "candidate");
            Directory.CreateDirectory(_candidateDirectory);
            Options = new ConformanceLedgerOptions(
                Path.Combine(_root, "ledger.sqlite"), Path.Combine(_root, "keys"),
                "conformance-applicability-" + Guid.NewGuid().ToString("N"));
            OptionalScenario = new ControlledScenario("V107-N01-optional");
            SupportScenario = new ControlledScenario("V107-N01-support");
            Facility = new ConformanceFacility(Options, new IConformanceScenario[] { OptionalScenario, SupportScenario });

            var packagePath = WriteCandidate("package", "candidate package bytes");
            var lockPath = WriteCandidate("dependency-lock", "candidate dependency lock");
            var buildPath = WriteCandidate("build-configuration", "candidate build configuration");
            var candidateManifestPath = Write("candidate-manifest.json",
                ConformanceBindings.CaptureCandidateManifest(_candidateDirectory));
            var candidate = ConformanceDocuments.FreezeCandidate(new ReleaseCandidateDefinition(
                "V107-N-candidate", Array.Empty<FingerprintComponent>(), Array.Empty<FingerprintComponent>(),
                new[] { Bind("candidate/Packages", "package", packagePath) }, Array.Empty<FingerprintComponent>(),
                new[] { Bind("candidate/EmbeddedAssets", "candidate-file-manifest", candidateManifestPath) },
                new[] { Bind("candidate/DependencyLocks", "lock", lockPath) },
                new[] { Bind("candidate/BuildConfiguration", "build", buildPath) }));
            Candidate = candidate;

            var proofText = $"excluded:{OptionalScenario.TestId}:unreachable:{candidate.Sha256}";
            _proof = Write("applicability-proof", proofText);
            var proofHash = ConformanceBindings.HashText(proofText);
            var requirement = new ConformanceRequirement(
                "V107-N-OPTIONAL", "ADR-0111", "An excluded optional case has objective evidence.", false);
            var optionalCase = new VerificationCase(OptionalScenario.TestId, QualificationLayer.Framework,
                VerificationMethod.Executable, new[] { requirement.RequirementId }, false,
                "feature is outside this development claim", proofHash, "isolated development environment",
                "candidate is frozen", "submit the governed exclusion proof", "NotApplicable is recorded",
                "public test evidence", "exact-text-v1");
            var supportCase = new VerificationCase(SupportScenario.TestId, QualificationLayer.Framework,
                VerificationMethod.Executable, new[] { requirement.RequirementId }, true,
                "supporting framework case", string.Empty, "isolated development environment",
                "candidate is frozen", "run the supporting case", "support is observable", "public test evidence",
                "exact-text-v1");
            Profile = ConformanceDocuments.FreezeProfile(new ConformanceProfile(
                "V107-N-profile", 1, claim, "optional applicability proof only", new[] { "current-process" },
                new[] { "conformance" }, new[] { QualificationLayer.Framework }, "retain immutable evidence",
                "development authority", new[] { requirement }, new[] { optionalCase, supportCase }));

            var thresholdPath = Write("threshold", "optional threshold");
            var calculationPath = Write("calculation-rules", "optional calculation rules");
            var context = ConformanceDocuments.FreezeContext(new QualificationContextDefinition(
                Profile.Sha256,
                ConformanceBindings.CaptureHarnesses(new IConformanceScenario[] { OptionalScenario, SupportScenario }),
                new[] { ConformanceBindings.DescribeScenario(OptionalScenario),
                    ConformanceBindings.DescribeScenario(SupportScenario) },
                new[] { Bind("context/Datasets", "applicability-proof", _proof) },
                Array.Empty<FingerprintComponent>(), new[] { Bind("context/Thresholds", "threshold", thresholdPath) },
                new[] { Bind("context/CalculationRules", "calculation", calculationPath) },
                new[] { new FingerprintComponent("observed-environment",
                    ConformanceBindings.HashText(ConformanceBindings.CaptureEnvironmentJson())) }));
            Context = context;
            Facility.Freeze(Profile, Candidate, Context);
        }

        public ConformanceLedgerOptions Options { get; }
        public ConformanceFacility Facility { get; }
        public ControlledScenario OptionalScenario { get; }
        private ControlledScenario SupportScenario { get; }
        public FrozenConformanceDocument Profile { get; }
        public FrozenConformanceDocument Candidate { get; }
        private FrozenConformanceDocument Context { get; }

        public Task<TestExecutionRecord> ExecuteOptionalAsync(IReadOnlyList<ConformanceEvidence>? inputs = null)
        {
            var expectedProof = $"excluded:{OptionalScenario.TestId}:unreachable:{Candidate.Sha256}";
            var evidence = new ConformanceEvidence("applicability-proof", Encoding.UTF8.GetBytes(expectedProof));
            var selectedInputs = inputs ?? new[] { evidence };
            var bindings = new ConformanceBindings(_root, _bindings, _candidateDirectory);
            return Facility.ExecuteAsync(new ConformanceExecutionRequest(
                Candidate.Sha256, Context.Sha256, OptionalScenario.TestId, bindings, selectedInputs));
        }

        private string Write(string name, string content)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllText(path, content, new UTF8Encoding(false, true));
            return path;
        }

        private string WriteCandidate(string name, string content)
        {
            var path = Path.Combine(_candidateDirectory, name);
            File.WriteAllText(path, content, new UTF8Encoding(false, true));
            return path;
        }

        private FingerprintComponent Bind(string category, string name, string path)
        {
            _bindings.Add(new ConformanceFileBinding(category, name, path));
            return new FingerprintComponent(name, ConformanceBindings.HashFile(path));
        }

        public void Dispose() => Facility.Dispose();
    }

    private sealed class ControlledScenario : IConformanceScenario
    {
        public ControlledScenario(string testId) => TestId = testId;
        public string TestId { get; }
        public string ScenarioHash => ConformanceBindings.HashText("scenario:" + TestId);
        public bool Invoked { get; private set; }

        public Task<ConformanceObservation> ObserveAsync(ConformanceExecutionContext context,
            CancellationToken cancellationToken)
        {
            Invoked = true;
            return Task.FromResult(new ConformanceObservation("support-observed"));
        }
    }
}
