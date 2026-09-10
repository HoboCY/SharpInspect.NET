using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpInspect.Abstractions;
using SharpInspect.CameraConformance.Probe;
using SharpInspect.Cameras.Conformance;
using Xunit;

namespace SharpInspect.Cameras.Conformance.Tests;

/// <summary>
/// The public recovery observer may see one transient unavailable health read
/// after the fixture restores the same device.  These tests exercise that
/// condition through the provider/device contracts only.
/// </summary>
public sealed class CameraConformanceRecoveryHealthProbeTests
{
    private static readonly Guid ExecutionId =
        Guid.Parse("12220000-0000-0000-0000-00000000C10A");
    private static readonly string CandidateHash = new('1', 64);
    private static readonly string ContextHash = new('2', 64);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task V122_C10_TransientUnavailableHealthIsReprobedAndStable()
    {
        var first = await ObserveAsync(HealthProbeMode.Transient);
        var second = await ObserveAsync(HealthProbeMode.Transient);

        Assert.Equal(ConformanceObservationStatus.Observed, first.Status);
        Assert.Equal(CameraConformanceSuite.ExpectedObservation, first.Observed);
        Assert.Equal(first.Observed, second.Observed);
        Assert.Equal(first.Reason, second.Reason);
        Assert.Equal(first.Evidence.Count, second.Evidence.Count);
        for (var index = 0; index < first.Evidence.Count; index++)
        {
            Assert.Equal(first.Evidence[index].Name, second.Evidence[index].Name);
            Assert.Equal(first.Evidence[index].Classification,
                second.Evidence[index].Classification);
            Assert.Equal(first.Evidence[index].GetBytes(),
                second.Evidence[index].GetBytes());
        }
    }

    [Fact]
    public async Task V122_C10_PersistentUnavailableHealthIsBoundedAndBlocked()
    {
        var observation = await ObserveAsync(HealthProbeMode.Persistent);

        Assert.Equal(ConformanceObservationStatus.Blocked, observation.Status);
        Assert.Equal("CameraRecoveryHealthTimeout", observation.Reason);
        Assert.NotEqual(CameraConformanceSuite.ExpectedObservation, observation.Observed);
    }

    [Fact]
    public async Task V122_C10_CancellationDuringHealthReprobeRemainsBlocked()
    {
        var factory = new HealthProbeFactory(HealthProbeMode.Persistent);
        var suite = new CameraConformanceSuite(factory);
        var scenario = FindScenario(suite);
        using var cancellation = new CancellationTokenSource();
        Task<ConformanceObservation>? observationTask = null;

        try
        {
            observationTask = scenario.ObserveAsync(Context(scenario, suite.CreateInputs()),
                cancellation.Token);
            await factory.RestoreSeen.Task.WaitAsync(TestTimeout);
            cancellation.Cancel();
            var observation = await observationTask.WaitAsync(TestTimeout);

            Assert.Equal(ConformanceObservationStatus.Blocked, observation.Status);
            Assert.Equal("CameraConformanceCancelled", observation.Reason);
            Assert.NotEqual(CameraConformanceSuite.ExpectedObservation, observation.Observed);
        }
        finally
        {
            cancellation.Cancel();
            await factory.DisposeFixturesAsync();
            if (observationTask is not null && !observationTask.IsCompleted)
            {
                try { await observationTask.WaitAsync(TestTimeout); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            }
        }
    }

    private static async Task<ConformanceObservation> ObserveAsync(
        HealthProbeMode mode)
    {
        var factory = new HealthProbeFactory(mode);
        var suite = new CameraConformanceSuite(factory);
        var scenario = FindScenario(suite);
        try
        {
            return await scenario.ObserveAsync(Context(scenario, suite.CreateInputs()),
                CancellationToken.None).WaitAsync(TestTimeout);
        }
        finally
        {
            await factory.DisposeFixturesAsync();
        }
    }

    private static IConformanceScenario FindScenario(CameraConformanceSuite suite) =>
        suite.Scenarios.Single(value => value.TestId == "V122-C10");

    private static ConformanceExecutionContext Context(IConformanceScenario scenario,
        IReadOnlyList<ConformanceEvidence> inputs) => new(
        ExecutionId, CandidateHash, ContextHash, scenario.TestId,
        "frozen-public-camera-contract-health-reprobe",
        CameraConformanceSuite.Cases.Single(value => value.TestId == scenario.TestId).Stimulus,
        inputs);

    private enum HealthProbeMode { Transient, Persistent }

    private sealed class HealthProbeFactory : ICameraConformanceFixtureFactory
    {
        private readonly VirtualConformanceFixtureFactory _inner = new();
        private readonly List<HealthProbeFixture> _createdFixtures = new();
        private readonly HealthProbeMode _mode;
        private int _healthFailureConsumed;
        private int _restoreSeen;

        internal HealthProbeFactory(HealthProbeMode mode) => _mode = mode;

        public CameraConformanceFixtureDeclaration Declaration => _inner.Declaration;
        internal TaskCompletionSource<object?> RestoreSeen { get; } = NewSignal();

        public async ValueTask<ICameraConformanceFixture> CreateAsync(
            CameraConformanceFixtureRequest request,
            CancellationToken cancellationToken = default)
        {
            var fixture = await _inner.CreateAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var provider = new HealthProbeProvider(this, fixture.Provider);
            var wrapped = new HealthProbeFixture(fixture, provider);
            _createdFixtures.Add(wrapped);
            return wrapped;
        }

        internal bool ShouldFailHealth()
        {
            if (Volatile.Read(ref _restoreSeen) == 0)
                return false;
            if (_mode == HealthProbeMode.Persistent)
                return true;
            return Interlocked.Exchange(ref _healthFailureConsumed, 1) == 0;
        }

        internal void MarkRestoreSeen()
        {
            Interlocked.Exchange(ref _restoreSeen, 1);
            RestoreSeen.TrySetResult(null);
        }

        internal async Task DisposeFixturesAsync()
        {
            foreach (var fixture in _createdFixtures.Distinct())
            {
                try { await fixture.DisposeAsync().AsTask().WaitAsync(TestTimeout); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
                try { await fixture.CleanupCompletion.WaitAsync(TestTimeout); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            }
        }

        private static TaskCompletionSource<object?> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class HealthProbeFixture : ICameraConformanceFixture
    {
        private readonly ICameraConformanceFixture _inner;
        private readonly HealthProbeProvider _provider;
        private readonly object _gate = new();
        private Task? _disposeTask;

        internal HealthProbeFixture(ICameraConformanceFixture inner,
            HealthProbeProvider provider)
        {
            _inner = inner;
            _provider = provider;
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
            if (stimulus.Kind == CameraConformanceStimulusKind.RestoreSameDevice)
                _provider.MarkRestoreSeen();
            return _inner.StimulateAsync(stimulus, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
                return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }

        private async Task DisposeCoreAsync()
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class HealthProbeProvider : ICameraProvider
    {
        private readonly HealthProbeFactory _owner;
        private readonly ICameraProvider _inner;

        internal HealthProbeProvider(HealthProbeFactory owner, ICameraProvider inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public CameraProviderIdentity Identity => _inner.Identity;

        public ValueTask<CameraDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken = default) =>
            _inner.DiscoverAsync(cancellationToken);

        public async ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
            CancellationToken cancellationToken = default)
        {
            var result = await _inner.OpenAsync(stableDeviceIdentity, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded || result.Device is not IControlledCameraDevice controlled)
                return result;
            return CameraOpenResult.Success(new HealthProbeDevice(_owner, controlled));
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        internal void MarkRestoreSeen() => _owner.MarkRestoreSeen();
    }

    private sealed class HealthProbeDevice : IControlledCameraDevice
    {
        private readonly HealthProbeFactory _owner;
        private readonly IControlledCameraDevice _inner;

        internal HealthProbeDevice(HealthProbeFactory owner, IControlledCameraDevice inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public CameraDeviceDescriptor Descriptor => _inner.Descriptor;
        public CameraCapabilities Capabilities => _inner.Capabilities;

        public CameraHealthSnapshot GetHealthSnapshot()
        {
            if (_owner.ShouldFailHealth())
                throw new InvalidOperationException("ControlledHealthProbeUnavailable");
            return _inner.GetHealthSnapshot();
        }

        public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
            RequestedCameraConfiguration requested,
            CancellationToken cancellationToken = default) =>
            _inner.ApplyConfigurationAsync(requested, cancellationToken);

        public ValueTask<CameraOperationResult> StartAsync(
            CancellationToken cancellationToken = default) =>
            _inner.StartAsync(cancellationToken);

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            CancellationToken cancellationToken = default) =>
            _inner.AcquireAsync(request, cancellationToken);

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            FrameAcquisitionControl control,
            CancellationToken cancellationToken = default) =>
            _inner.AcquireAsync(request, control, cancellationToken);

        public CameraProtocolSnapshot ReadProtocolObservations(long afterSequence,
            int maximumCount = 64) =>
            _inner.ReadProtocolObservations(afterSequence, maximumCount);

        public ValueTask<CameraOperationResult> StopAsync(
            CancellationToken cancellationToken = default) =>
            _inner.StopAsync(cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
