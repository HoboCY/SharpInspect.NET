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

public sealed class CameraConformanceAdversarialTests
{
    private static readonly Guid ExecutionId =
        Guid.Parse("12220000-0000-0000-0000-0000000000A1");
    private static readonly string CandidateHash = new('C', 64);
    private static readonly string ContextHash = new('D', 64);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task V122_A03_ConfigurationFailureWithAppliedHealthIsMismatch()
    {
        var factory = new ConfigurationFailureFactory();
        var suite = new CameraConformanceSuite(factory);
        var scenario = FindScenario(suite, CameraConformanceCase.ConfigurationFailure);
        Task<ConformanceObservation>? observationTask = null;

        try
        {
            observationTask = scenario.ObserveAsync(
                Context(scenario, suite.CreateInputs()), CancellationToken.None);
            var observation = await observationTask.WaitAsync(TestTimeout);

            Assert.Equal(ConformanceObservationStatus.Observed, observation.Status);
            Assert.StartsWith("CameraContractMismatch:", observation.Observed,
                StringComparison.Ordinal);
            Assert.NotEqual(CameraConformanceSuite.ExpectedObservation, observation.Observed);
        }
        finally
        {
            await AwaitAndDisposeAsync(observationTask, factory.CreatedFixtures);
        }
    }

    [Theory]
    [InlineData(CameraConnectionState.Open, CameraConfigurationState.Unknown,
        CameraAcquisitionState.Stopped)]
    public async Task V122_A03_ConfigurationFailureWithOpenUnknownStoppedIsMismatch(
        CameraConnectionState connection, CameraConfigurationState configuration,
        CameraAcquisitionState acquisition)
    {
        var factory = new ConfigurationFailureFactory(connection, configuration, acquisition);
        var suite = new CameraConformanceSuite(factory);
        var scenario = FindScenario(suite, CameraConformanceCase.ConfigurationFailure);
        Task<ConformanceObservation>? observationTask = null;

        try
        {
            observationTask = scenario.ObserveAsync(
                Context(scenario, suite.CreateInputs()), CancellationToken.None);
            var observation = await observationTask.WaitAsync(TestTimeout);

            Assert.Equal(ConformanceObservationStatus.Observed, observation.Status);
            Assert.StartsWith("CameraContractMismatch:", observation.Observed,
                StringComparison.Ordinal);
            Assert.NotEqual(CameraConformanceSuite.ExpectedObservation, observation.Observed);
        }
        finally
        {
            await AwaitAndDisposeAsync(observationTask, factory.CreatedFixtures);
        }
    }

    [Fact]
    public async Task V122_A07_CancellationWithoutLateFrameIsBlocked()
    {
        var factory = new CancellationAsTimeoutFactory();
        var suite = new CameraConformanceSuite(factory);
        var scenario = FindScenario(suite, CameraConformanceCase.Cancellation);
        Task<ConformanceObservation>? observationTask = null;

        try
        {
            observationTask = scenario.ObserveAsync(
                Context(scenario, suite.CreateInputs()), CancellationToken.None);
            var observation = await observationTask.WaitAsync(TestTimeout);

            Assert.Equal(ConformanceObservationStatus.Blocked, observation.Status);
            Assert.Equal("CameraCancellationLateFrameUnavailable", observation.Reason);
            Assert.NotEqual(CameraConformanceSuite.ExpectedObservation, observation.Observed);
        }
        finally
        {
            await AwaitAndDisposeAsync(observationTask, factory.CreatedFixtures);
        }
    }

    [Fact]
    public async Task V122_A04_DeviceDisposeFailureRetainsFixtureOwner()
    {
        var factory = new DeviceDisposeFailureFactory();
        var suite = new CameraConformanceSuite(factory);
        var scenario = FindScenario(suite, CameraConformanceCase.DiscoveryIdentity);
        var request = new CameraConformanceFixtureRequest(
            CameraConformanceCase.DiscoveryIdentity,
            factory.Declaration.Configurations[0], 0);
        Task<ConformanceObservation>? observationTask = null;
        ICameraConformanceFixture? freshFixture = null;

        try
        {
            observationTask = scenario.ObserveAsync(
                Context(scenario, suite.CreateInputs()), CancellationToken.None);
            await factory.DeviceDisposeStarted.Task.WaitAsync(TestTimeout);

            Assert.Equal(1, factory.DeviceDisposeCalls);
            Assert.Equal(0, factory.ProviderDisposeCalls);
            Assert.Equal(0, factory.FixtureDisposeCalls);
            Assert.False(factory.FirstCleanupCompletion.IsCompleted);

            var pending = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                factory.CreateAsync(request).AsTask());
            Assert.StartsWith("AdversarialDisposeFailureCleanupPending", pending.Message,
                StringComparison.Ordinal);

            // Release the observer's bounded cleanup wait only after proving that
            // the failed device retirement still owns the fixture/provider.
            factory.CompleteFirstCleanup();
            var observation = await observationTask.WaitAsync(TestTimeout);
            Assert.Equal(ConformanceObservationStatus.Blocked, observation.Status);
            Assert.NotEqual(CameraConformanceSuite.ExpectedObservation, observation.Observed);

            // The test rig now performs the real fixture retirement.  A second
            // fixture from the same factory is legal only after that completion.
            var firstFixture = factory.FirstFixture;
            await firstFixture.DisposeAsync().AsTask().WaitAsync(TestTimeout);
            await firstFixture.CleanupCompletion.WaitAsync(TestTimeout);
            freshFixture = await factory.CreateAsync(request).AsTask().WaitAsync(TestTimeout);
            await freshFixture.DisposeAsync().AsTask().WaitAsync(TestTimeout);
            await freshFixture.CleanupCompletion.WaitAsync(TestTimeout);
        }
        finally
        {
            // All signals are idempotent so assertion failures cannot strand the
            // observer or leave an opened virtual provider behind.
            factory.CompleteFirstCleanup();
            var fixtures = factory.CreatedFixtures.ToList();
            if (freshFixture is not null)
                fixtures.Add(freshFixture);
            await AwaitAndDisposeAsync(observationTask, fixtures);
        }
    }

    [Fact]
    public async Task V122_A01_LateOpenRetiresBeforeFixtureCanBeReused()
    {
        var factory = new LateOpenFactory();
        var suite = new CameraConformanceSuite(factory);
        var scenario = FindScenario(suite, CameraConformanceCase.DiscoveryIdentity);
        var request = new CameraConformanceFixtureRequest(
            CameraConformanceCase.DiscoveryIdentity,
            factory.Declaration.Configurations[0], 0);
        Task<ConformanceObservation>? observationTask = null;
        ICameraConformanceFixture? freshFixture = null;

        try
        {
            observationTask = scenario.ObserveAsync(
                Context(scenario, suite.CreateInputs()), CancellationToken.None);
            await factory.OpenEntered.Task.WaitAsync(TestTimeout);

            // OpenAsync must stay pending beyond the observer's two-second public
            // operation budget.  The late result is then released explicitly.
            await Task.Delay(TimeSpan.FromMilliseconds(2_200)).WaitAsync(TestTimeout);
            Assert.False(factory.OpenReturned.Task.IsCompleted);

            factory.ReleaseOpen.TrySetResult(null);
            await factory.OpenReturned.Task.WaitAsync(TestTimeout);
            await factory.LateDeviceDisposeStarted.Task.WaitAsync(TestTimeout);

            Assert.False(factory.LateDeviceDisposed.Task.IsCompleted);
            var pending = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                factory.CreateAsync(request).AsTask());
            Assert.StartsWith("AdversarialFixtureCleanupPending", pending.Message,
                StringComparison.Ordinal);

            factory.ReleaseLateDeviceDispose.TrySetResult(null);
            await factory.LateDeviceDisposed.Task.WaitAsync(TestTimeout);
            await factory.FirstCleanupCompletion.WaitAsync(TestTimeout);

            var observation = await observationTask.WaitAsync(TestTimeout);
            Assert.Equal(ConformanceObservationStatus.Blocked, observation.Status);
            Assert.NotEqual(CameraConformanceSuite.ExpectedObservation, observation.Observed);
            Assert.Equal(1, factory.OpenCalls);

            freshFixture = await factory.CreateAsync(request).AsTask().WaitAsync(TestTimeout);
            await freshFixture.DisposeAsync().AsTask().WaitAsync(TestTimeout);
            await freshFixture.CleanupCompletion.WaitAsync(TestTimeout);
        }
        finally
        {
            // Every release is idempotent so a failed assertion cannot strand the
            // late operation or its fixture owner.
            factory.ReleaseOpen.TrySetResult(null);
            factory.ReleaseLateDeviceDispose.TrySetResult(null);
            var fixtures = factory.CreatedFixtures.ToList();
            if (freshFixture is not null)
                fixtures.Add(freshFixture);
            await AwaitAndDisposeAsync(observationTask, fixtures);
        }
    }

    [Fact]
    public async Task V122_A10_RecoveryBusyPendingDoesNotAdvanceUntilAcknowledged()
    {
        var factory = new RecoveryBusyFactory();
        var suite = new CameraConformanceSuite(factory);
        var scenario = FindScenario(suite, CameraConformanceCase.DisconnectRecovery);
        Task<ConformanceObservation>? observationTask = null;

        try
        {
            observationTask = scenario.ObserveAsync(
                Context(scenario, suite.CreateInputs()), CancellationToken.None);
            await factory.SecondOpenEntered.Task.WaitAsync(TestTimeout);
            factory.ReleaseSecondOpen.TrySetResult(null);
            await factory.SecondAcquireEntered.Task.WaitAsync(TestTimeout);

            // The delayed controlled device has entered AcquireAsync but has not
            // delegated to the adapter, so its FrameAcquisitionControl is still
            // pending and Recovery.Busy must remain non-busy.  A recovery driver
            // must not advance the fixture clock during this interval.
            await Task.Delay(TimeSpan.FromMilliseconds(100)).WaitAsync(TestTimeout);
            Assert.Equal(0, factory.AdvancesWhilePending);

            factory.ReleaseAcknowledge.TrySetResult(null);
            await factory.FirstAdvanceAfterRelease.Task.WaitAsync(TestTimeout);
            var observation = await observationTask.WaitAsync(TestTimeout);

            Assert.Equal(ConformanceObservationStatus.Observed, observation.Status);
            Assert.Equal(CameraConformanceSuite.ExpectedObservation, observation.Observed);
            Assert.Equal(2, factory.OpenCalls);
            Assert.Equal(1, factory.SecondAcquireCalls);
            Assert.Equal(1, factory.AdvancesAfterRelease);
        }
        finally
        {
            // The release signals are idempotent and keep cleanup bounded even
            // when a pre-release assertion fails.
            factory.ReleaseSecondOpen.TrySetResult(null);
            factory.ReleaseAcknowledge.TrySetResult(null);
            await AwaitAndDisposeAsync(observationTask, factory.CreatedFixtures);
        }
    }

    private static IConformanceScenario FindScenario(CameraConformanceSuite suite,
        CameraConformanceCase kind)
    {
        var definition = CameraConformanceSuite.Cases.Single(value => value.Case == kind);
        return suite.Scenarios.Single(value => value.TestId == definition.TestId);
    }

    private static ConformanceExecutionContext Context(IConformanceScenario scenario,
        IReadOnlyList<ConformanceEvidence> inputs) => new(
        ExecutionId,
        CandidateHash,
        ContextHash,
        scenario.TestId,
        "frozen-public-camera-contract-adversarial",
        CameraConformanceSuite.Cases.Single(value => value.TestId == scenario.TestId).Stimulus,
        inputs);

    private static async Task AwaitAndDisposeAsync(Task<ConformanceObservation>? observationTask,
        IEnumerable<ICameraConformanceFixture> fixtures)
    {
        if (observationTask is not null && !observationTask.IsCompleted)
        {
            try { await observationTask.WaitAsync(TestTimeout); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }

        foreach (var fixture in fixtures.Distinct())
        {
            try
            {
                await fixture.DisposeAsync().AsTask().WaitAsync(TestTimeout);
                await fixture.CleanupCompletion.WaitAsync(TestTimeout);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
    }

    private sealed class RecoveryBusyFactory : ICameraConformanceFixtureFactory
    {
        private readonly VirtualConformanceFixtureFactory _inner = new();
        private readonly List<RecoveryBusyFixture> _createdFixtures = new();
        private int _openCalls;
        private int _secondAcquireCalls;
        private int _advancesWhilePending;
        private int _advancesAfterRelease;

        public CameraConformanceFixtureDeclaration Declaration => _inner.Declaration;
        public IReadOnlyList<RecoveryBusyFixture> CreatedFixtures => _createdFixtures;
        public int OpenCalls => Volatile.Read(ref _openCalls);
        public int SecondAcquireCalls => Volatile.Read(ref _secondAcquireCalls);
        public int AdvancesWhilePending => Volatile.Read(ref _advancesWhilePending);
        public int AdvancesAfterRelease => Volatile.Read(ref _advancesAfterRelease);
        public TaskCompletionSource<object?> SecondOpenEntered { get; } = NewSignal();
        public TaskCompletionSource<object?> ReleaseSecondOpen { get; } = NewSignal();
        public TaskCompletionSource<object?> SecondAcquireEntered { get; } = NewSignal();
        public TaskCompletionSource<object?> ReleaseAcknowledge { get; } = NewSignal();
        public TaskCompletionSource<object?> FirstAdvanceAfterRelease { get; } = NewSignal();

        public async ValueTask<ICameraConformanceFixture> CreateAsync(
            CameraConformanceFixtureRequest request,
            CancellationToken cancellationToken = default)
        {
            var fixture = await _inner.CreateAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var provider = new RecoveryBusyProvider(this, fixture.Provider);
            var wrapped = new RecoveryBusyFixture(this, fixture, provider);
            _createdFixtures.Add(wrapped);
            return wrapped;
        }

        internal int CountOpen() => Interlocked.Increment(ref _openCalls);

        internal void SignalSecondAcquire()
        {
            Interlocked.Increment(ref _secondAcquireCalls);
            SecondAcquireEntered.TrySetResult(null);
        }

        internal void CountAdvance()
        {
            if (!SecondAcquireEntered.Task.IsCompleted)
                return;
            if (!ReleaseAcknowledge.Task.IsCompleted)
            {
                Interlocked.Increment(ref _advancesWhilePending);
                return;
            }

            if (Interlocked.Increment(ref _advancesAfterRelease) == 1)
                FirstAdvanceAfterRelease.TrySetResult(null);
        }

        private static TaskCompletionSource<object?> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RecoveryBusyFixture : ICameraConformanceFixture
    {
        private readonly RecoveryBusyFactory _owner;
        private readonly ICameraConformanceFixture _inner;
        private readonly RecoveryBusyProvider _provider;

        public RecoveryBusyFixture(RecoveryBusyFactory owner,
            ICameraConformanceFixture inner, RecoveryBusyProvider provider)
        {
            _owner = owner;
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
            if (stimulus.Kind == CameraConformanceStimulusKind.AdvanceOrWait)
                _owner.CountAdvance();
            return _inner.StimulateAsync(stimulus, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class RecoveryBusyProvider : ICameraProvider
    {
        private readonly RecoveryBusyFactory _owner;
        private readonly ICameraProvider _inner;
        private int _openCalls;

        public RecoveryBusyProvider(RecoveryBusyFactory owner, ICameraProvider inner)
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
            var call = Interlocked.Increment(ref _openCalls);
            _owner.CountOpen();
            if (call == 2)
            {
                _owner.SecondOpenEntered.TrySetResult(null);
                await _owner.ReleaseSecondOpen.Task.ConfigureAwait(false);
            }

            var result = await _inner.OpenAsync(stableDeviceIdentity, cancellationToken)
                .ConfigureAwait(false);
            if (call != 2 || !result.Succeeded || result.Device is not IControlledCameraDevice controlled)
                return result;

            return CameraOpenResult.Success(new RecoveryBusyDevice(_owner, controlled));
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class RecoveryBusyDevice : IControlledCameraDevice
    {
        private readonly RecoveryBusyFactory _owner;
        private readonly IControlledCameraDevice _inner;

        public RecoveryBusyDevice(RecoveryBusyFactory owner, IControlledCameraDevice inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public CameraDeviceDescriptor Descriptor => _inner.Descriptor;
        public CameraCapabilities Capabilities => _inner.Capabilities;
        public CameraHealthSnapshot GetHealthSnapshot() => _inner.GetHealthSnapshot();

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

        public async ValueTask<FrameAcquisitionResult> AcquireAsync(
            FrameAcquisitionRequest request, FrameAcquisitionControl control,
            CancellationToken cancellationToken = default)
        {
            _owner.SignalSecondAcquire();
            await _owner.ReleaseAcknowledge.Task.ConfigureAwait(false);
            return await _inner.AcquireAsync(request, control, cancellationToken)
                .ConfigureAwait(false);
        }

        public CameraProtocolSnapshot ReadProtocolObservations(long afterSequence,
            int maximumCount = 64) =>
            _inner.ReadProtocolObservations(afterSequence, maximumCount);

        public ValueTask<CameraOperationResult> StopAsync(
            CancellationToken cancellationToken = default) =>
            _inner.StopAsync(cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class DeviceDisposeFailureFactory : ICameraConformanceFixtureFactory
    {
        private readonly object _gate = new();
        private readonly VirtualConformanceFixtureFactory _inner = new();
        private readonly List<DeviceDisposeFailureFixture> _createdFixtures = new();
        private DeviceDisposeFailureFixture? _activeFixture;

        public CameraConformanceFixtureDeclaration Declaration => _inner.Declaration;
        public IReadOnlyList<ICameraConformanceFixture> CreatedFixtures => _createdFixtures;
        public DeviceDisposeFailureFixture FirstFixture => _createdFixtures[0];
        public Task FirstCleanupCompletion => FirstFixture.CleanupCompletion;
        public int DeviceDisposeCalls => Volatile.Read(ref _deviceDisposeCalls);
        public int ProviderDisposeCalls => Volatile.Read(ref _providerDisposeCalls);
        public int FixtureDisposeCalls => Volatile.Read(ref _fixtureDisposeCalls);
        public TaskCompletionSource<object?> DeviceDisposeStarted { get; } = NewSignal();

        private int _deviceDisposeCalls;
        private int _providerDisposeCalls;
        private int _fixtureDisposeCalls;

        public async ValueTask<ICameraConformanceFixture> CreateAsync(
            CameraConformanceFixtureRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_activeFixture is not null)
                    throw new InvalidOperationException(
                        "AdversarialDisposeFailureCleanupPending");
            }

            var fixture = await _inner.CreateAsync(request, cancellationToken)
                .ConfigureAwait(false);
            DeviceDisposeFailureFixture? wrapped = null;
            var provider = new DeviceDisposeFailureProvider(this, fixture.Provider);
            wrapped = new DeviceDisposeFailureFixture(this, fixture, provider,
                () => Release(wrapped!));
            lock (_gate)
            {
                _activeFixture = wrapped;
                _createdFixtures.Add(wrapped);
            }
            return wrapped;
        }

        public void CompleteFirstCleanup()
        {
            if (_createdFixtures.Count != 0)
                FirstFixture.CompleteCleanup();
        }

        internal void CountDeviceDispose()
        {
            Interlocked.Increment(ref _deviceDisposeCalls);
            DeviceDisposeStarted.TrySetResult(null);
        }

        internal void CountProviderDispose() =>
            Interlocked.Increment(ref _providerDisposeCalls);

        internal void CountFixtureDispose() =>
            Interlocked.Increment(ref _fixtureDisposeCalls);

        private void Release(DeviceDisposeFailureFixture fixture)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeFixture, fixture))
                    _activeFixture = null;
            }
        }

        private static TaskCompletionSource<object?> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class DeviceDisposeFailureFixture : ICameraConformanceFixture
    {
        private readonly DeviceDisposeFailureFactory _owner;
        private readonly ICameraConformanceFixture _inner;
        private readonly DeviceDisposeFailureProvider _provider;
        private readonly Action _release;
        private readonly TaskCompletionSource<object?> _cleanup = NewSignal();

        public DeviceDisposeFailureFixture(DeviceDisposeFailureFactory owner,
            ICameraConformanceFixture inner, DeviceDisposeFailureProvider provider,
            Action release)
        {
            _owner = owner;
            _inner = inner;
            _provider = provider;
            _release = release;
        }

        public ICameraProvider Provider => _provider;
        public IFrameAcquisitionClock Clock => _inner.Clock;
        public string StableDeviceIdentity => _inner.StableDeviceIdentity;
        public string LogicalCameraRole => _inner.LogicalCameraRole;
        public RequestedCameraConfiguration Configuration => _inner.Configuration;
        public Task CleanupCompletion => _cleanup.Task;

        public ValueTask<CameraConformanceStimulusReceipt> StimulateAsync(
            CameraConformanceStimulus stimulus,
            CancellationToken cancellationToken = default) =>
            _inner.StimulateAsync(stimulus, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            _owner.CountFixtureDispose();
            try
            {
                await _provider.DisposeAsync().ConfigureAwait(false);
                await _inner.DisposeAsync().ConfigureAwait(false);
                _cleanup.TrySetResult(null);
            }
            finally
            {
                _release();
            }
        }

        public void CompleteCleanup() => _cleanup.TrySetResult(null);

        private static TaskCompletionSource<object?> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class DeviceDisposeFailureProvider : ICameraProvider
    {
        private readonly DeviceDisposeFailureFactory _owner;
        private readonly ICameraProvider _inner;

        public DeviceDisposeFailureProvider(DeviceDisposeFailureFactory owner,
            ICameraProvider inner)
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
            return result.Succeeded && result.Device is not null
                ? CameraOpenResult.Success(new DeviceDisposeFailureDevice(_owner,
                    result.Device))
                : result;
        }

        public ValueTask DisposeAsync()
        {
            _owner.CountProviderDispose();
            return _inner.DisposeAsync();
        }
    }

    private sealed class DeviceDisposeFailureDevice : ICameraDevice
    {
        private readonly DeviceDisposeFailureFactory _owner;
        private readonly ICameraDevice _inner;

        public DeviceDisposeFailureDevice(DeviceDisposeFailureFactory owner,
            ICameraDevice inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public CameraDeviceDescriptor Descriptor => _inner.Descriptor;
        public CameraCapabilities Capabilities => _inner.Capabilities;
        public CameraHealthSnapshot GetHealthSnapshot() => _inner.GetHealthSnapshot();

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

        public ValueTask<CameraOperationResult> StopAsync(
            CancellationToken cancellationToken = default) =>
            _inner.StopAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            _owner.CountDeviceDispose();
            return ValueTask.FromException(new InvalidOperationException(
                "AdversarialDeviceDisposeFailed"));
        }
    }

    private sealed class ConfigurationFailureFactory : ICameraConformanceFixtureFactory
    {
        private readonly VirtualConformanceFixtureFactory _inner = new();
        private readonly List<ICameraConformanceFixture> _createdFixtures = new();
        private readonly CameraConnectionState _connection;
        private readonly CameraConfigurationState _configuration;
        private readonly CameraAcquisitionState _acquisition;

        public ConfigurationFailureFactory(
            CameraConnectionState connection = CameraConnectionState.Open,
            CameraConfigurationState configuration = CameraConfigurationState.Applied,
            CameraAcquisitionState acquisition = CameraAcquisitionState.Stopped)
        {
            _connection = connection;
            _configuration = configuration;
            _acquisition = acquisition;
        }

        public CameraConformanceFixtureDeclaration Declaration => _inner.Declaration;
        public IReadOnlyList<ICameraConformanceFixture> CreatedFixtures => _createdFixtures;

        public async ValueTask<ICameraConformanceFixture> CreateAsync(
            CameraConformanceFixtureRequest request,
            CancellationToken cancellationToken = default)
        {
            var fixture = await _inner.CreateAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var wrapped = new FixtureWrapper(fixture,
                new ConfigurationFailureProvider(fixture.Provider,
                    _connection, _configuration, _acquisition));
            _createdFixtures.Add(wrapped);
            return wrapped;
        }
    }

    private sealed class CancellationAsTimeoutFactory : ICameraConformanceFixtureFactory
    {
        private readonly VirtualConformanceFixtureFactory _inner = new();
        private readonly List<ICameraConformanceFixture> _createdFixtures = new();

        public CameraConformanceFixtureDeclaration Declaration => _inner.Declaration;
        public IReadOnlyList<ICameraConformanceFixture> CreatedFixtures => _createdFixtures;

        public async ValueTask<ICameraConformanceFixture> CreateAsync(
            CameraConformanceFixtureRequest request,
            CancellationToken cancellationToken = default)
        {
            var actualRequest = request.Case == CameraConformanceCase.Cancellation
                ? new CameraConformanceFixtureRequest(CameraConformanceCase.Timeout,
                    request.Configuration, request.AcquisitionCount)
                : request;
            var fixture = await _inner.CreateAsync(actualRequest, cancellationToken)
                .ConfigureAwait(false);
            var wrapped = new FixtureWrapper(fixture, fixture.Provider);
            _createdFixtures.Add(wrapped);
            return wrapped;
        }
    }

    private sealed class LateOpenFactory : ICameraConformanceFixtureFactory
    {
        private readonly object _gate = new();
        private readonly VirtualConformanceFixtureFactory _inner = new();
        private readonly List<ICameraConformanceFixture> _createdFixtures = new();
        private ICameraConformanceFixture? _activeFixture;

        public CameraConformanceFixtureDeclaration Declaration => _inner.Declaration;
        public IReadOnlyList<ICameraConformanceFixture> CreatedFixtures => _createdFixtures;
        public int OpenCalls => Volatile.Read(ref _openCalls);
        public TaskCompletionSource<object?> OpenEntered { get; } = NewSignal();
        public TaskCompletionSource<object?> OpenReturned { get; } = NewSignal();
        public TaskCompletionSource<object?> ReleaseOpen { get; } = NewSignal();
        public TaskCompletionSource<object?> LateDeviceDisposeStarted { get; } = NewSignal();
        public TaskCompletionSource<object?> LateDeviceDisposed { get; } = NewSignal();
        public TaskCompletionSource<object?> ReleaseLateDeviceDispose { get; } = NewSignal();
        public Task FirstCleanupCompletion =>
            _createdFixtures.Count == 0
                ? Task.CompletedTask
                : _createdFixtures[0].CleanupCompletion;

        public async ValueTask<ICameraConformanceFixture> CreateAsync(
            CameraConformanceFixtureRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_activeFixture is not null)
                {
                    if (_activeFixture.CleanupCompletion.IsCompletedSuccessfully)
                        _activeFixture = null;
                    else if (_activeFixture.CleanupCompletion.IsCompleted)
                        throw new InvalidOperationException("AdversarialFixtureCleanupFailed");
                    else
                        throw new InvalidOperationException("AdversarialFixtureCleanupPending");
                }
            }

            var fixture = await _inner.CreateAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var provider = new LateOpenProvider(this, fixture.Provider);
            LateOpenFixture? wrapped = null;
            wrapped = new LateOpenFixture(fixture, provider, () => Release(wrapped!));
            var created = wrapped!;
            lock (_gate)
            {
                _activeFixture = created;
                _createdFixtures.Add(created);
            }
            return created;
        }

        internal void CountOpen() => Interlocked.Increment(ref _openCalls);
        private int _openCalls;

        private void Release(ICameraConformanceFixture fixture)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeFixture, fixture))
                    _activeFixture = null;
            }
        }

        private static TaskCompletionSource<object?> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class LateOpenFixture : ICameraConformanceFixture
    {
        private readonly ICameraConformanceFixture _inner;
        private readonly LateOpenProvider _provider;
        private readonly Action _release;
        private readonly Task _cleanupCompletion;

        public LateOpenFixture(ICameraConformanceFixture inner,
            LateOpenProvider provider, Action release)
        {
            _inner = inner;
            _provider = provider;
            _release = release;
            _cleanupCompletion = CompleteCleanupAsync();
        }

        public ICameraProvider Provider => _provider;
        public IFrameAcquisitionClock Clock => _inner.Clock;
        public string StableDeviceIdentity => _inner.StableDeviceIdentity;
        public string LogicalCameraRole => _inner.LogicalCameraRole;
        public RequestedCameraConfiguration Configuration => _inner.Configuration;
        public Task CleanupCompletion => _cleanupCompletion;

        public ValueTask<CameraConformanceStimulusReceipt> StimulateAsync(
            CameraConformanceStimulus stimulus,
            CancellationToken cancellationToken = default) =>
            _inner.StimulateAsync(stimulus, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        private async Task CompleteCleanupAsync()
        {
            await Task.WhenAll(_inner.CleanupCompletion, _provider.ResourceRetired)
                .ConfigureAwait(false);
            _release();
        }
    }

    private sealed class FixtureWrapper : ICameraConformanceFixture
    {
        private readonly ICameraConformanceFixture _inner;
        private readonly ICameraProvider _provider;

        public FixtureWrapper(ICameraConformanceFixture inner, ICameraProvider provider)
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
            CancellationToken cancellationToken = default) =>
            _inner.StimulateAsync(stimulus, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ConfigurationFailureProvider : ICameraProvider
    {
        private readonly ICameraProvider _inner;
        private readonly CameraConnectionState _connection;
        private readonly CameraConfigurationState _configuration;
        private readonly CameraAcquisitionState _acquisition;

        public ConfigurationFailureProvider(ICameraProvider inner,
            CameraConnectionState connection, CameraConfigurationState configuration,
            CameraAcquisitionState acquisition)
        {
            _inner = inner;
            _connection = connection;
            _configuration = configuration;
            _acquisition = acquisition;
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
            return result.Succeeded && result.Device is not null
                ? CameraOpenResult.Success(new ConfigurationFailureDevice(result.Device,
                    _connection, _configuration, _acquisition))
                : result;
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class ConfigurationFailureDevice : ICameraDevice
    {
        private readonly ICameraDevice _inner;
        private readonly CameraConnectionState _connection;
        private readonly CameraConfigurationState _configuration;
        private readonly CameraAcquisitionState _acquisition;

        public ConfigurationFailureDevice(ICameraDevice inner,
            CameraConnectionState connection, CameraConfigurationState configuration,
            CameraAcquisitionState acquisition)
        {
            _inner = inner;
            _connection = connection;
            _configuration = configuration;
            _acquisition = acquisition;
        }

        public CameraDeviceDescriptor Descriptor => _inner.Descriptor;
        public CameraCapabilities Capabilities => _inner.Capabilities;

        public CameraHealthSnapshot GetHealthSnapshot()
        {
            var observedAt = _inner.GetHealthSnapshot().ObservedAt;
            return new CameraHealthSnapshot(CameraProviderAvailability.Available,
                _connection, _configuration, _acquisition, observedAt);
        }

        public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
            RequestedCameraConfiguration requested,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CameraConfigurationResult.Failure(
                "AdversarialConfigurationRejected"));
        }

        public ValueTask<CameraOperationResult> StartAsync(
            CancellationToken cancellationToken = default) =>
            _inner.StartAsync(cancellationToken);

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            CancellationToken cancellationToken = default) =>
            _inner.AcquireAsync(request, cancellationToken);

        public ValueTask<CameraOperationResult> StopAsync(
            CancellationToken cancellationToken = default) =>
            _inner.StopAsync(cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class LateOpenProvider : ICameraProvider
    {
        private readonly LateOpenFactory _owner;
        private readonly ICameraProvider _inner;
        private readonly object _gate = new();
        private readonly TaskCompletionSource<object?> _openCompleted = NewSignal();
        private readonly TaskCompletionSource<object?> _lateResourceRetired = NewSignal();
        private Task? _disposeTask;
        private bool _openStarted;
        private bool _lateDeviceCreated;

        public LateOpenProvider(LateOpenFactory owner, ICameraProvider inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public CameraProviderIdentity Identity => _inner.Identity;
        public Task ResourceRetired => _lateResourceRetired.Task;

        public ValueTask<CameraDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken = default) =>
            _inner.DiscoverAsync(cancellationToken);

        public async ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _openStarted = true;
                _owner.CountOpen();
            }
            _owner.OpenEntered.TrySetResult(null);
            try
            {
                await _owner.ReleaseOpen.Task.ConfigureAwait(false);
                var result = await _inner.OpenAsync(stableDeviceIdentity,
                    cancellationToken).ConfigureAwait(false);
                if (result.Succeeded && result.Device is not null)
                {
                    lock (_gate) _lateDeviceCreated = true;
                    result = CameraOpenResult.Success(new LateOpenDevice(this, result.Device));
                }
                return result;
            }
            finally
            {
                _owner.OpenReturned.TrySetResult(null);
                _openCompleted.TrySetResult(null);
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
                return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }

        internal void LateDeviceDisposedSuccessfully()
        {
            _lateResourceRetired.TrySetResult(null);
        }

        internal void SignalLateDeviceDisposeStarted() =>
            _owner.LateDeviceDisposeStarted.TrySetResult(null);

        internal Task WaitForLateDeviceDisposeRelease() =>
            _owner.ReleaseLateDeviceDispose.Task;

        internal void SignalLateDeviceDisposed() =>
            _owner.LateDeviceDisposed.TrySetResult(null);

        private async Task DisposeCoreAsync()
        {
            bool openStarted;
            lock (_gate)
            {
                openStarted = _openStarted;
            }

            if (!openStarted)
                _openCompleted.TrySetResult(null);
            await _openCompleted.Task.ConfigureAwait(false);
            bool lateDeviceCreated;
            lock (_gate) lateDeviceCreated = _lateDeviceCreated;
            await _inner.DisposeAsync().ConfigureAwait(false);
            if (!lateDeviceCreated)
                _lateResourceRetired.TrySetResult(null);
        }

        private static TaskCompletionSource<object?> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class LateOpenDevice : ICameraDevice
    {
        private readonly LateOpenProvider _owner;
        private readonly ICameraDevice _inner;
        private readonly object _gate = new();
        private Task? _disposeTask;

        public LateOpenDevice(LateOpenProvider owner, ICameraDevice inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public CameraDeviceDescriptor Descriptor => _inner.Descriptor;
        public CameraCapabilities Capabilities => _inner.Capabilities;

        public CameraHealthSnapshot GetHealthSnapshot() => _inner.GetHealthSnapshot();

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

        public ValueTask<CameraOperationResult> StopAsync(
            CancellationToken cancellationToken = default) =>
            _inner.StopAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            lock (_gate)
                return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }

        private async Task DisposeCoreAsync()
        {
            _owner.SignalLateDeviceDisposeStarted();
            await _owner.WaitForLateDeviceDisposeRelease().ConfigureAwait(false);
            await _inner.DisposeAsync().ConfigureAwait(false);
            _owner.LateDeviceDisposedSuccessfully();
            _owner.SignalLateDeviceDisposed();
        }
    }
}
