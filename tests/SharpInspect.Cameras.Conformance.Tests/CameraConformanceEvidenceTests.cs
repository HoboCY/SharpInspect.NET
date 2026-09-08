using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.CameraConformance.Probe;
using SharpInspect.Cameras.Conformance;
using SharpInspect.Runtime.Conformance;
using Xunit;

namespace SharpInspect.Cameras.Conformance.Tests;

[CollectionDefinition("CameraConformanceLedger", DisableParallelization = true)]
public sealed class CameraConformanceLedgerCollection { }

[Collection("CameraConformanceLedger")]
public sealed class CameraConformanceEvidenceTests
{
    [Fact]
    public async Task V122_E01_OnlyFrozenCandidateBoundOptionalProofRecordsNotApplicable()
    {
        using var fixture = new EvidenceFixture();
        var record = await fixture.ExecuteAsync("V122-X-preview");
        Assert.Equal(ConformanceOutcome.NotApplicable, record.Outcome);
        Assert.False(fixture.Extension.Invoked);
        var aggregate = fixture.Facility.Aggregate(fixture.Candidate.Sha256, fixture.Context.Sha256, QualificationLayer.Provider);
        Assert.False(aggregate.CanIssueQualification);
        Assert.False(aggregate.AllSelectedCasesSatisfied);
        Assert.Equal(15, aggregate.Gates.Count(value => value.Outcome == ConformanceOutcome.NotRun));
        using var query = new ConformanceQuery(fixture.Options);
        var stored = Assert.Single(query.GetExecutions());
        Assert.Equal(record.TestExecutionId, stored.Record!.TestExecutionId);
        Assert.Equal("V122-EXT-preview", Assert.Single(stored.Reservation.RequirementIds));
    }

    [Fact]
    public async Task V122_E02_ProofForAnotherCandidateCannotWaiveOptionalCase()
    {
        using var fixture = new EvidenceFixture(wrongProof: true);
        var record = await fixture.ExecuteAsync("V122-X-preview");
        Assert.Equal(ConformanceOutcome.Blocked, record.Outcome);
        Assert.Equal("ConformanceExclusionProofMissing", record.ReasonCode);
        Assert.False(fixture.Extension.Invoked);
    }

    [Fact]
    public async Task V122_E03_ClaimedExtensionRunsAndMissingObservationStaysBlocked()
    {
        using var fixture = new EvidenceFixture(claimed: true);
        var record = await fixture.ExecuteAsync("V122-X-preview");
        Assert.Equal(ConformanceOutcome.Blocked, record.Outcome);
        Assert.True(fixture.Extension.Invoked);
        Assert.Contains("PublicPreviewObservationUnavailable", record.Observed);
    }

    [Fact]
    public async Task V122_E04_MandatoryInstrumentationGapIsAnImmutableBlockedRecord()
    {
        using var fixture = new EvidenceFixture();
        var record = await fixture.ExecuteAsync("V122-C13");
        Assert.Equal(ConformanceOutcome.Blocked, record.Outcome);
        using var query = new ConformanceQuery(fixture.Options);
        var stored = Assert.Single(query.GetExecutions());
        Assert.Equal("V122-R13", Assert.Single(stored.Reservation.RequirementIds));
        Assert.Equal(fixture.Candidate.Sha256, stored.Reservation.CandidateHash);
        Assert.Equal(fixture.Context.Sha256, stored.Reservation.ContextHash);
        Assert.Contains(record.Outputs, output => output.Name == "public-observation");
        foreach (var output in record.Outputs)
            Assert.Equal(output.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(query.ReadArtifact(output.Sha256))));
        Assert.Equal(0, fixture.Factory.Created);
    }

    [Fact]
    public async Task V122_E05_ChangedCallerInputIsInvalidHarnessBeforeOpeningCamera()
    {
        using var fixture = new EvidenceFixture();
        var inputs = fixture.Inputs.Select(value => value.Name == CameraConformanceSuite.FixtureInputName
            ? new ConformanceEvidence(value.Name, Encoding.UTF8.GetBytes("changed")) : value).ToArray();
        var record = await fixture.ExecuteAsync("V122-C01", inputs);
        Assert.Equal(ConformanceOutcome.InvalidHarness, record.Outcome);
        Assert.Equal("ConformanceInputChanged", record.ReasonCode);
        Assert.Equal(0, fixture.Factory.Created);
    }

    [Fact]
    public async Task V122_E06_ChangedFactoryDeclarationInvalidatesScenarioBeforeOpeningCamera()
    {
        using var fixture = new EvidenceFixture();
        var original = fixture.Factory.Declaration;
        fixture.Factory.Declaration = new CameraConformanceFixtureDeclaration(original.FixtureId, "changed",
            original.Provider, original.StableDeviceIdentity, original.Configurations, original.CanonicalFrameHashes,
            original.PoolCapacity, original.Seed, original.FrameDelay, original.PublicFixtureData);
        var record = await fixture.ExecuteAsync("V122-C01");
        Assert.Equal(ConformanceOutcome.InvalidHarness, record.Outcome);
        Assert.Equal("ConformanceScenarioChanged", record.ReasonCode);
        Assert.Equal(0, fixture.Factory.Created);
    }

    [Fact]
    public async Task V122_E07_ChangedDependencyLockBlocksBeforeOpeningCamera()
    {
        using var fixture = new EvidenceFixture();
        File.AppendAllText(fixture.DependencyLockPath, "changed");
        var record = await fixture.ExecuteAsync("V122-C01");
        Assert.Equal(ConformanceOutcome.Blocked, record.Outcome);
        Assert.Equal("ConformanceCandidateBytesChanged", record.ReasonCode);
        Assert.Equal(0, fixture.Factory.Created);
    }

    private sealed class EvidenceFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "SharpInspectConformanceTests", "V122-" + Guid.NewGuid().ToString("N"));
        private readonly List<ConformanceFileBinding> _files = new();
        public EvidenceFixture(bool claimed = false, bool wrongProof = false)
        {
            Directory.CreateDirectory(Path.Combine(_root, "candidate"));
            Factory = new CountingFactory(claimed);
            Extension = new ExtensionScenario();
            var suite = new CameraConformanceSuite(Factory, new[]
            { new CameraConformanceExtension("preview", "ADR-0060", "Optional preview requires a separately registered public observer.", Extension) });
            Options = new ConformanceLedgerOptions(Path.Combine(_root, "ledger.sqlite"), Path.Combine(_root, "keys"), "camera-conformance-test");
            Facility = new ConformanceFacility(Options, suite.Scenarios);
            var package = Write("candidate/Packages", "provider", "candidate/provider.dll", File.ReadAllBytes(typeof(VirtualConformanceFixtureFactory).Assembly.Location));
            var dependencyLock = Write("candidate/DependencyLocks", "lock", "candidate/lock", Encoding.UTF8.GetBytes("test-fixture-lock-v1"));
            DependencyLockPath = Path.Combine(_root, "candidate", "lock");
            var build = Write("candidate/BuildConfiguration", "build", "candidate/build", Encoding.UTF8.GetBytes("net6.0-development-test"));
            var manifest = Write("candidate/EmbeddedAssets", "candidate-file-manifest", "manifest.json",
                Encoding.UTF8.GetBytes(ConformanceBindings.CaptureCandidateManifest(Path.Combine(_root, "candidate"))));
            Candidate = ConformanceDocuments.FreezeCandidate(new ReleaseCandidateDefinition("V122-test",
                Array.Empty<FingerprintComponent>(), Array.Empty<FingerprintComponent>(), new[] { package },
                Array.Empty<FingerprintComponent>(), new[] { manifest }, new[] { dependencyLock }, new[] { build }));
            var proof = Encoding.UTF8.GetBytes($"excluded:{Extension.TestId}:unreachable:{(wrongProof ? new string('0', 64) : Candidate.Sha256)}");
            var proofHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(proof));
            var profile = ConformanceDocuments.FreezeProfile(suite.CreateProfile(new Dictionary<string, string> { [Extension.TestId] = proofHash }));
            Inputs = suite.CreateInputs().Concat(claimed ? Array.Empty<ConformanceEvidence>() :
                new[] { new ConformanceEvidence("applicability-proof", proof) }).ToArray();
            var datasets = Inputs.Select(input => Write("context/Datasets", input.Name, "context/" + input.Name, input.GetBytes())).ToArray();
            Context = ConformanceDocuments.FreezeContext(new QualificationContextDefinition(profile.Sha256,
                ConformanceBindings.CaptureHarnesses(suite.Scenarios), suite.Scenarios.Select(ConformanceBindings.DescribeScenario).ToArray(),
                datasets, Array.Empty<FingerprintComponent>(), Array.Empty<FingerprintComponent>(), Array.Empty<FingerprintComponent>(),
                new[] { new FingerprintComponent("observed-environment", ConformanceBindings.HashText(ConformanceBindings.CaptureEnvironmentJson())) }));
            Facility.Freeze(profile, Candidate, Context);
        }
        public CountingFactory Factory { get; }
        public ExtensionScenario Extension { get; }
        public ConformanceLedgerOptions Options { get; }
        public ConformanceFacility Facility { get; }
        public FrozenConformanceDocument Candidate { get; }
        public FrozenConformanceDocument Context { get; }
        public IReadOnlyList<ConformanceEvidence> Inputs { get; }
        public string DependencyLockPath { get; }
        public Task<TestExecutionRecord> ExecuteAsync(string testId, IReadOnlyList<ConformanceEvidence>? inputs = null) =>
            Facility.ExecuteAsync(new ConformanceExecutionRequest(Candidate.Sha256, Context.Sha256, testId,
                new ConformanceBindings(_root, _files, Path.Combine(_root, "candidate")), inputs ?? Inputs));
        private FingerprintComponent Write(string category, string name, string relative, byte[] bytes)
        {
            var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            _files.Add(new ConformanceFileBinding(category, name, path));
            return new FingerprintComponent(name, ConformanceBindings.HashFile(path));
        }
        public void Dispose() => Facility.Dispose();
    }

    private sealed class CountingFactory : ICameraConformanceFixtureFactory
    {
        private readonly VirtualConformanceFixtureFactory _inner = new();
        public CountingFactory(bool claimed)
        {
            var source = _inner.Declaration;
            Declaration = new CameraConformanceFixtureDeclaration(source.FixtureId, source.FixtureVersion,
                source.Provider, source.StableDeviceIdentity, source.Configurations, source.CanonicalFrameHashes,
                source.PoolCapacity, source.Seed, source.FrameDelay, source.PublicFixtureData,
                claimed ? new[] { "preview" } : Array.Empty<string>(), source.ChallengeFrameHashes);
        }
        public CameraConformanceFixtureDeclaration Declaration { get; set; }
        public int Created { get; private set; }
        public ValueTask<ICameraConformanceFixture> CreateAsync(CameraConformanceFixtureRequest request, CancellationToken cancellationToken = default)
        { Created++; return _inner.CreateAsync(request, cancellationToken); }
    }
    private sealed class ExtensionScenario : IConformanceScenario
    {
        public string TestId => "V122-X-preview";
        public string ScenarioHash => ConformanceBindings.HashText("camera-preview-observer-unavailable-v1");
        public bool Invoked { get; private set; }
        public Task<ConformanceObservation> ObserveAsync(ConformanceExecutionContext context, CancellationToken cancellationToken)
        {
            Invoked = true;
            return Task.FromResult(new ConformanceObservation("PublicPreviewObservationUnavailable",
                status: ConformanceObservationStatus.Blocked, reason: "PublicPreviewObservationUnavailable"));
        }
    }
}
