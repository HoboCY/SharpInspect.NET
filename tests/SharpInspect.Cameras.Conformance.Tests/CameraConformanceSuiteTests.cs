using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpInspect.Abstractions;
using SharpInspect.CameraConformance.Probe;
using SharpInspect.Cameras.Conformance;
using SharpInspect.Runtime.Conformance;
using Xunit;

namespace SharpInspect.Cameras.Conformance.Tests;

public sealed class CameraConformanceSuiteTests
{
    private static readonly Guid ExecutionId =
        Guid.Parse("12220000-0000-0000-0000-000000000001");
    private static readonly string CandidateHash = new('A', 64);
    private static readonly string ContextHash = new('B', 64);

    [Fact]
    public void V122_T01_CoreScenarioRegistryAndFrozenInputsAreBounded()
    {
        var factory = new VirtualConformanceFixtureFactory();
        var suite = new CameraConformanceSuite(factory);

        Assert.Equal(15, suite.Scenarios.Count);
        Assert.Equal(CameraConformanceSuite.Cases.Select(value => value.TestId),
            suite.Scenarios.Select(value => value.TestId));
        Assert.All(suite.Scenarios, scenario => Assert.True(IsSha256(scenario.ScenarioHash)));

        var declaration = suite.Declaration;
        Assert.Equal("SharpInspect.Virtual", declaration.Provider.Id);
        Assert.Equal("1", declaration.Provider.Version);
        Assert.Equal("SharpInspect.NET.Cameras.Virtual", declaration.Provider.AdapterPackageId);
        Assert.Equal("0.1.0-dev.1", declaration.Provider.AdapterVersion);
        Assert.Equal("virtual-conformance-camera", declaration.StableDeviceIdentity);
        Assert.Equal(5, declaration.Configurations.Count);
        Assert.Contains(declaration.Configurations,
            value => value.ProductionAcquisitionMode == ProductionAcquisitionMode.SoftwareTrigger);
        Assert.Contains(declaration.Configurations,
            value => value.ProductionAcquisitionMode == ProductionAcquisitionMode.HardwareTrigger);
        Assert.Equal(new[] { VisionPixelFormat.Mono8, VisionPixelFormat.Mono16,
            VisionPixelFormat.Bgr24 }, declaration.Configurations.Select(value => value.PixelFormat)
            .Distinct().OrderBy(value => value));
        Assert.Equal(new[] { 10, 12, 16 }, declaration.Configurations
            .Where(value => value.PixelFormat == VisionPixelFormat.Mono16)
            .Select(value => value.ValidBits!.Value).OrderBy(value => value));
        Assert.Equal(2, declaration.PoolCapacity);
        Assert.Equal(TimeSpan.FromMilliseconds(10), declaration.FrameDelay);
        Assert.Equal(5, declaration.CanonicalFrameHashes.Count);
        Assert.All(declaration.CanonicalFrameHashes, value => Assert.True(IsSha256(value)));
        Assert.Empty(declaration.OptionalExtensions);

        var firstInputs = suite.CreateInputs();
        var secondInputs = suite.CreateInputs();
        Assert.Equal(new[] { CameraConformanceSuite.FixtureInputName,
            CameraConformanceSuite.CatalogInputName }, firstInputs.Select(value => value.Name));
        Assert.Equal(firstInputs.Count, secondInputs.Count);
        for (var index = 0; index < firstInputs.Count; index++)
            Assert.True(firstInputs[index].GetBytes().SequenceEqual(secondInputs[index].GetBytes()));
    }

    [Fact]
    public async Task V122_T02_TamperedMandatoryInputCannotProduceAnObservation()
    {
        var suite = new CameraConformanceSuite(new VirtualConformanceFixtureFactory());
        var scenario = suite.Scenarios[0];
        var sourceInputs = suite.CreateInputs();
        var tamperedInputs = sourceInputs.Select((input, index) => index == 0
            ? new ConformanceEvidence(input.Name, input.GetBytes().Concat(new byte[] { 0x7F }).ToArray())
            : input).ToArray();

        var exception = await Record.ExceptionAsync(() => scenario.ObserveAsync(
            Context(scenario, tamperedInputs), CancellationToken.None));

        Assert.NotNull(exception);
        Assert.Equal("CameraConformanceInputHashMismatch", exception!.Message);
        Assert.True(sourceInputs[0].GetBytes().SequenceEqual(suite.CreateInputs()[0].GetBytes()));
    }

    [Fact]
    public async Task V122_T03_RepeatedVirtualCasesHaveStablePublicEvidence()
    {
        var first = await ObserveAllAsync(new CameraConformanceSuite(
            new VirtualConformanceFixtureFactory()));
        var second = await ObserveAllAsync(new CameraConformanceSuite(
            new VirtualConformanceFixtureFactory()));

        Assert.Equal(15, first.Length);
        Assert.Equal(first.Length, second.Length);
        for (var index = 0; index < first.Length; index++)
        {
            if (index < 12)
            {
                AssertContractSatisfied(index, "first", first[index]);
                AssertContractSatisfied(index, "second", second[index]);
            }
            else
            {
                Assert.Equal(ConformanceObservationStatus.Blocked, first[index].Status);
                Assert.Equal(ConformanceObservationStatus.Blocked, second[index].Status);
            }

            Assert.Equal(first[index].Observed, second[index].Observed);
            Assert.Equal(first[index].Reason, second[index].Reason);
            Assert.Equal(ConformanceEvidenceClassification.PublicTestData, first[index].Classification);
            Assert.Equal(ConformanceEvidenceClassification.PublicTestData, second[index].Classification);
            AssertPublicEvidenceEqual(first[index].Evidence, second[index].Evidence);
        }
    }

    private static void AssertContractSatisfied(int index, string run,
        ConformanceObservation observation) => Assert.True(
        observation.Status == ConformanceObservationStatus.Observed &&
        observation.Observed == CameraConformanceSuite.ExpectedObservation,
        $"V122-C{index + 1:D2} {run}: {observation.Status} {observation.Reason} " +
        observation.Observed + " " + string.Join(" ", observation.Evidence.Select(value =>
            System.Text.Encoding.UTF8.GetString(value.GetBytes()))));

    [Fact]
    public void V122_T04_OptionalRegistrationAndExclusionProofAreFrozen()
    {
        var extensionScenario = new TestScenario("V122-X-optional");
        var extension = new CameraConformanceExtension(
            "optional",
            "ADR-0111",
            "An optional camera extension is explicitly registered.",
            extensionScenario);

        var declaredFactory = new DelegatingFactory(
            new VirtualConformanceFixtureFactory(),
            declaration => CopyDeclaration(declaration, optionalExtensions: new[] { "optional" }));
        var declaredSuite = new CameraConformanceSuite(declaredFactory, new[] { extension });
        Assert.Equal(16, declaredSuite.Scenarios.Count);
        Assert.Contains(declaredSuite.Scenarios, value => value.TestId == extensionScenario.TestId);
        Assert.NotNull(declaredSuite.CreateProfile());

        var undeclaredSuite = new CameraConformanceSuite(
            new VirtualConformanceFixtureFactory(), new[] { extension });
        var missingProof = Assert.Throws<ArgumentException>(() => undeclaredSuite.CreateProfile());
        Assert.StartsWith("CameraConformanceFrozenExclusionProofRequired", missingProof.Message,
            StringComparison.Ordinal);
        var proof = ConformanceBindings.HashText("frozen-optional-proof");
        Assert.NotNull(undeclaredSuite.CreateProfile(new Dictionary<string, string>
        {
            [extensionScenario.TestId] = proof
        }));

        var coreIdScenario = new TestScenario("V122-C01");
        var coreIdException = Assert.Throws<ArgumentException>(() => new CameraConformanceExtension(
            "core-id", "ADR-0111", "Core IDs cannot be optional extension IDs.", coreIdScenario));
        Assert.StartsWith("CameraConformanceExtensionTestIdRequired", coreIdException.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task V122_T05_FactoryBlocksOverlapUntilFixtureCleanupCompletes()
    {
        var factory = new VirtualConformanceFixtureFactory();
        var request = new CameraConformanceFixtureRequest(
            CameraConformanceCase.DiscoveryIdentity,
            factory.Declaration.Configurations[0],
            1);
        var first = await factory.CreateAsync(request);
        try
        {
            Assert.False(first.CleanupCompletion.IsCompleted);
            var pending = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                factory.CreateAsync(request).AsTask());
            Assert.Equal("VirtualConformanceFixtureCleanupPending", pending.Message);
        }
        finally
        {
            await DisposeFixtureAsync(first);
        }

        var second = await factory.CreateAsync(request);
        await DisposeFixtureAsync(second);
    }

    [Fact]
    public async Task V122_T06_WrongProviderFixtureCannotObserveSatisfiedContract()
    {
        var inner = new VirtualConformanceFixtureFactory();
        var wrapper = new DelegatingFactory(inner,
            providerOverride: new CameraProviderIdentity("Wrong.Provider", "1", "Wrong.Package", "1"));
        var suite = new CameraConformanceSuite(wrapper);
        var scenario = suite.Scenarios[0];

        try
        {
            var observation = await scenario.ObserveAsync(Context(scenario, suite.CreateInputs()),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(CameraConformanceSuite.ExpectedObservation, observation.Observed);
        }
        finally
        {
            if (wrapper.LastFixture is not null)
                await DisposeFixtureAsync(wrapper.LastFixture);
        }
    }

    [Fact]
    public async Task V122_T07_UnavailableHardwarePulseBlocksHardwareScenario()
    {
        var inner = new VirtualConformanceFixtureFactory();
        var wrapper = new DelegatingFactory(inner, suppressHardwarePulse: true);
        var suite = new CameraConformanceSuite(wrapper);
        var definition = CameraConformanceSuite.Cases.Single(value =>
            value.Case == CameraConformanceCase.HardwareAcquisition);
        var scenario = suite.Scenarios.Single(value => value.TestId == definition.TestId);

        try
        {
            var observation = await scenario.ObserveAsync(Context(scenario, suite.CreateInputs()),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ConformanceObservationStatus.Blocked, observation.Status);
            Assert.NotEqual(CameraConformanceSuite.ExpectedObservation, observation.Observed);
        }
        finally
        {
            if (wrapper.LastFixture is not null)
                await DisposeFixtureAsync(wrapper.LastFixture);
        }
    }

    private static ConformanceExecutionContext Context(IConformanceScenario scenario,
        IReadOnlyList<ConformanceEvidence> inputs) => new(
        ExecutionId,
        CandidateHash,
        ContextHash,
        scenario.TestId,
        "frozen-public-camera-contract",
        CameraConformanceSuite.Cases.Single(value => value.TestId == scenario.TestId).Stimulus,
        inputs);

    private static async Task<ConformanceObservation[]> ObserveAllAsync(
        CameraConformanceSuite suite)
    {
        var observations = new List<ConformanceObservation>(suite.Scenarios.Count);
        foreach (var scenario in suite.Scenarios)
            observations.Add(await scenario.ObserveAsync(Context(scenario, suite.CreateInputs()),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        return observations.ToArray();
    }

    private static void AssertPublicEvidenceEqual(IReadOnlyList<ConformanceEvidence> expected,
        IReadOnlyList<ConformanceEvidence> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Name, actual[index].Name);
            Assert.Equal(expected[index].Classification, actual[index].Classification);
            Assert.True(expected[index].GetBytes().SequenceEqual(actual[index].GetBytes()));
        }
    }

    private static CameraConformanceFixtureDeclaration CopyDeclaration(
        CameraConformanceFixtureDeclaration source,
        CameraProviderIdentity? provider = null,
        IEnumerable<string>? optionalExtensions = null) => new(
        source.FixtureId,
        source.FixtureVersion,
        provider ?? source.Provider,
        source.StableDeviceIdentity,
        source.Configurations,
        source.CanonicalFrameHashes,
        source.PoolCapacity,
        source.Seed,
        source.FrameDelay,
        source.PublicFixtureData,
        optionalExtensions ?? source.OptionalExtensions, source.ChallengeFrameHashes);

    private static async Task DisposeFixtureAsync(ICameraConformanceFixture fixture)
    {
        await fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static bool IsSha256(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private sealed class TestScenario : IConformanceScenario
    {
        public TestScenario(string testId) => TestId = testId;
        public string TestId { get; }
        public string ScenarioHash => ConformanceBindings.HashText("V122-test-scenario:" + TestId);

        public Task<ConformanceObservation> ObserveAsync(ConformanceExecutionContext context,
            CancellationToken cancellationToken) => Task.FromResult(new ConformanceObservation(
            "optional-observation", new[]
            {
                new ConformanceEvidence("optional-observation", Array.Empty<byte>())
            }));
    }

    private sealed class DelegatingFactory : ICameraConformanceFixtureFactory
    {
        private readonly ICameraConformanceFixtureFactory _inner;
        private readonly bool _suppressHardwarePulse;
        private readonly CameraProviderIdentity? _providerOverride;

        public DelegatingFactory(ICameraConformanceFixtureFactory inner,
            Func<CameraConformanceFixtureDeclaration,
                CameraConformanceFixtureDeclaration>? declarationTransform = null,
            bool suppressHardwarePulse = false,
            CameraProviderIdentity? providerOverride = null)
        {
            _inner = inner;
            _suppressHardwarePulse = suppressHardwarePulse;
            _providerOverride = providerOverride;
            Declaration = declarationTransform?.Invoke(inner.Declaration) ?? inner.Declaration;
        }

        public CameraConformanceFixtureDeclaration Declaration { get; }
        public ICameraConformanceFixture? LastFixture { get; private set; }

        public async ValueTask<ICameraConformanceFixture> CreateAsync(
            CameraConformanceFixtureRequest request,
            CancellationToken cancellationToken = default)
        {
            var fixture = await _inner.CreateAsync(request, cancellationToken).ConfigureAwait(false);
            var wrapped = new DelegatingFixture(fixture, _suppressHardwarePulse, _providerOverride);
            LastFixture = wrapped;
            return wrapped;
        }
    }

    private sealed class DelegatingFixture : ICameraConformanceFixture
    {
        private readonly ICameraConformanceFixture _inner;
        private readonly bool _suppressHardwarePulse;
        private readonly ICameraProvider _provider;

        public DelegatingFixture(ICameraConformanceFixture inner, bool suppressHardwarePulse,
            CameraProviderIdentity? providerOverride)
        {
            _inner = inner;
            _suppressHardwarePulse = suppressHardwarePulse;
            _provider = providerOverride is null
                ? inner.Provider
                : new ProviderIdentityOverride(inner.Provider, providerOverride);
        }

        public ICameraProvider Provider => _provider;
        public IFrameAcquisitionClock Clock => _inner.Clock;
        public string StableDeviceIdentity => _inner.StableDeviceIdentity;
        public string LogicalCameraRole => _inner.LogicalCameraRole;
        public RequestedCameraConfiguration Configuration => _inner.Configuration;
        public Task CleanupCompletion => _inner.CleanupCompletion;

        public ValueTask<CameraConformanceStimulusReceipt> StimulateAsync(
            CameraConformanceStimulus stimulus,
            CancellationToken cancellationToken = default)
        {
            if (_suppressHardwarePulse &&
                stimulus.Kind == CameraConformanceStimulusKind.HardwarePulse)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new CameraConformanceStimulusReceipt(
                    CameraConformanceStimulusStatus.Unavailable,
                    "TestHardwarePulseSuppressed",
                    Clock.GetTimePoint()));
            }

            return _inner.StimulateAsync(stimulus, cancellationToken);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class ProviderIdentityOverride : ICameraProvider
    {
        private readonly ICameraProvider _inner;

        public ProviderIdentityOverride(ICameraProvider inner, CameraProviderIdentity identity)
        {
            _inner = inner;
            Identity = identity;
        }

        public CameraProviderIdentity Identity { get; }

        public ValueTask<CameraDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken = default) => _inner.DiscoverAsync(cancellationToken);

        public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
            CancellationToken cancellationToken = default) =>
            _inner.OpenAsync(stableDeviceIdentity, cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
