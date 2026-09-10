using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpInspect.Abstractions;
using SharpInspect.CameraConformance.Probe;
using SharpInspect.Cameras.Conformance;
using Xunit;

namespace SharpInspect.Cameras.Conformance.Tests;

/// <summary>
/// Regression coverage for cancellation ownership.  The wrapper uses only the
/// public controlled-device surface: the public cancellation result is allowed
/// to complete while the adapter's physical cancellation callback remains
/// pending.  No provider or Runtime private state is inspected.
/// </summary>
public sealed class CameraConformanceCancellationRetirementTests
{
    private static readonly Guid ExecutionId =
        Guid.Parse("12220000-0000-0000-0000-00000000C07A");
    private static readonly string CandidateHash = new('E', 64);
    private static readonly string ContextHash = new('F', 64);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CleanupHold = TimeSpan.FromMilliseconds(75);

    [Fact]
    public async Task V122_T03_C07_CancellationQuiescesBeforeReuse()
    {
        var factory = new CancellationRetirementFactory();
        var suite = new CameraConformanceSuite(factory);
        var scenario = FindCancellationScenario(suite);
        Task<ConformanceObservation>? observationTask = null;

        try
        {
            observationTask = scenario.ObserveAsync(Context(scenario, suite.CreateInputs()),
                CancellationToken.None);

            // The physical cancellation callback must have entered its
            // retirement barrier before we inspect the late frame.
            await factory.CancellationCallbackEntered.Task.WaitAsync(TestTimeout);

            // The late-frame observation is read through the public protocol
            // ring while the physical AcquireAsync callback is still held.
            await factory.LateFrameObserved.Task.WaitAsync(TestTimeout);
            Assert.False(factory.CancellationCallbackReturned.Task.IsCompleted);
            Assert.Equal(1, factory.PhysicalAcquireCalls);
            Assert.False(factory.SecondPhysicalAcquireStarted.Task.IsCompleted);

            // Keep CleanupPending alive for a deterministic 75ms after the
            // public late-frame observation.  Reuse must remain blocked during
            // this interval and can proceed only after the callback retires.
            await Task.Delay(CleanupHold).WaitAsync(TestTimeout);
            Assert.False(factory.CancellationCallbackReturned.Task.IsCompleted);
            Assert.Equal(1, factory.PhysicalAcquireCalls);
            Assert.False(factory.SecondPhysicalAcquireStarted.Task.IsCompleted);

            factory.ReleaseCleanup.TrySetResult(null);
            await factory.SecondPhysicalAcquireStarted.Task.WaitAsync(TestTimeout);
            var observation = await observationTask.WaitAsync(TestTimeout);

            Assert.Equal(ConformanceObservationStatus.Observed, observation.Status);
            Assert.Equal(CameraConformanceSuite.ExpectedObservation, observation.Observed);
            Assert.True(factory.CancellationCallbackReturned.Task.IsCompletedSuccessfully);
            Assert.Equal(2, factory.PhysicalAcquireCalls);

            var facts = ReadFacts(observation);
            Assert.True(facts.GetProperty("cancellationLateFrameObserved").GetBoolean());
            Assert.True(facts.GetProperty("cancellationAttemptQuiescedBeforeReuse").GetBoolean());
        }
        finally
        {
            await ReleaseAndDisposeAsync(observationTask, factory);
        }
    }

    [Fact]
    public async Task V122_T03_C07_NeverQuiescingCancellationIsBoundedAndBlocked()
    {
        var factory = new CancellationRetirementFactory();
        var suite = new CameraConformanceSuite(factory);
        var scenario = FindCancellationScenario(suite);
        Task<ConformanceObservation>? observationTask = null;

        try
        {
            observationTask = scenario.ObserveAsync(Context(scenario, suite.CreateInputs()),
                CancellationToken.None);
            await factory.CancellationCallbackEntered.Task.WaitAsync(TestTimeout);
            await factory.LateFrameObserved.Task.WaitAsync(TestTimeout);

            // The observer's public quiescence budget is two seconds.  Leave
            // the callback unreleased through that budget, then release only
            // so the test can drain the fixture and inspect the bounded result.
            await Task.Delay(TimeSpan.FromMilliseconds(2_200)).WaitAsync(TestTimeout);
            Assert.False(factory.CancellationCallbackReturned.Task.IsCompleted);
            Assert.Equal(1, factory.PhysicalAcquireCalls);
            Assert.False(factory.SecondPhysicalAcquireStarted.Task.IsCompleted);

            factory.ReleaseCleanup.TrySetResult(null);
            await DisposeFixturesAsync(factory);
            var observation = await observationTask.WaitAsync(TestTimeout);

            Assert.Equal(ConformanceObservationStatus.Blocked, observation.Status);
            Assert.Equal("CameraCancellationQuiescenceUnavailable", observation.Reason);
            Assert.NotEqual(CameraConformanceSuite.ExpectedObservation, observation.Observed);
            Assert.Equal(1, factory.PhysicalAcquireCalls);
            Assert.False(factory.SecondPhysicalAcquireStarted.Task.IsCompleted);
        }
        finally
        {
            await ReleaseAndDisposeAsync(observationTask, factory);
        }
    }

    private static IConformanceScenario FindCancellationScenario(CameraConformanceSuite suite)
    {
        var definition = CameraConformanceSuite.Cases.Single(value =>
            value.Case == CameraConformanceCase.Cancellation);
        return suite.Scenarios.Single(value => value.TestId == definition.TestId);
    }

    private static ConformanceExecutionContext Context(IConformanceScenario scenario,
        IReadOnlyList<ConformanceEvidence> inputs) => new(
        ExecutionId,
        CandidateHash,
        ContextHash,
        scenario.TestId,
        "frozen-public-camera-contract-cancellation-retirement",
        CameraConformanceSuite.Cases.Single(value => value.TestId == scenario.TestId).Stimulus,
        inputs);

    private static JsonElement ReadFacts(ConformanceObservation observation)
    {
        var evidence = Assert.Single(observation.Evidence);
        using var document = JsonDocument.Parse(evidence.GetBytes());
        return document.RootElement.Clone();
    }

    private static async Task ReleaseAndDisposeAsync(
        Task<ConformanceObservation>? observationTask,
        CancellationRetirementFactory factory)
    {
        factory.ReleaseCleanup.TrySetResult(null);
        await DisposeFixturesAsync(factory);

        if (observationTask is not null && !observationTask.IsCompleted)
        {
            try { await observationTask.WaitAsync(TestTimeout); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
    }

    private static async Task DisposeFixturesAsync(CancellationRetirementFactory factory)
    {
        foreach (var fixture in factory.CreatedFixtures.Distinct())
        {
            try { await fixture.DisposeAsync().AsTask().WaitAsync(TestTimeout); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }

            try { await fixture.CleanupCompletion.WaitAsync(TestTimeout); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
    }

    private sealed class CancellationRetirementFactory : ICameraConformanceFixtureFactory
    {
        private readonly VirtualConformanceFixtureFactory _inner = new();
        private readonly List<ICameraConformanceFixture> _createdFixtures = new();
        private int _physicalAcquireCalls;

        public CameraConformanceFixtureDeclaration Declaration => _inner.Declaration;
        public IReadOnlyList<ICameraConformanceFixture> CreatedFixtures => _createdFixtures;
        public int PhysicalAcquireCalls => Volatile.Read(ref _physicalAcquireCalls);
        public TaskCompletionSource<object?> LateFrameObserved { get; } = NewSignal();
        public TaskCompletionSource<object?> CancellationCallbackEntered { get; } = NewSignal();
        public TaskCompletionSource<object?> CancellationCallbackReturned { get; } = NewSignal();
        public TaskCompletionSource<object?> SecondPhysicalAcquireStarted { get; } = NewSignal();
        public TaskCompletionSource<object?> ReleaseCleanup { get; } = NewSignal();

        public async ValueTask<ICameraConformanceFixture> CreateAsync(
            CameraConformanceFixtureRequest request,
            CancellationToken cancellationToken = default)
        {
            var fixture = await _inner.CreateAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var provider = new CancellationRetirementProvider(this, fixture.Provider);
            var wrapped = new CancellationRetirementFixture(fixture, provider);
            _createdFixtures.Add(wrapped);
            return wrapped;
        }

        internal int CountPhysicalAcquire()
        {
            var count = Interlocked.Increment(ref _physicalAcquireCalls);
            if (count == 2)
                SecondPhysicalAcquireStarted.TrySetResult(null);
            return count;
        }

        internal void SignalLateFrame() => LateFrameObserved.TrySetResult(null);

        internal async Task HoldCancellationCallbackAsync()
        {
            CancellationCallbackEntered.TrySetResult(null);
            // The cancellation token belongs to the physical operation.  It
            // must not cancel this callback's retirement barrier.
            await ReleaseCleanup.Task.ConfigureAwait(false);
            CancellationCallbackReturned.TrySetResult(null);
        }

        private static TaskCompletionSource<object?> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CancellationRetirementFixture : ICameraConformanceFixture
    {
        private readonly ICameraConformanceFixture _inner;
        private readonly CancellationRetirementProvider _provider;

        internal CancellationRetirementFixture(ICameraConformanceFixture inner,
            CancellationRetirementProvider provider)
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

    private sealed class CancellationRetirementProvider : ICameraProvider
    {
        private readonly CancellationRetirementFactory _owner;
        private readonly ICameraProvider _inner;

        internal CancellationRetirementProvider(CancellationRetirementFactory owner,
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
            if (!result.Succeeded || result.Device is not IControlledCameraDevice controlled)
                return result;

            return CameraOpenResult.Success(new CancellationRetirementDevice(_owner,
                controlled));
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class CancellationRetirementDevice : IControlledCameraDevice
    {
        private readonly CancellationRetirementFactory _owner;
        private readonly IControlledCameraDevice _inner;
        private CancellationTokenRegistration _physicalCancellationRegistration;

        internal CancellationRetirementDevice(CancellationRetirementFactory owner,
            IControlledCameraDevice inner)
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

        public ValueTask<FrameAcquisitionResult> AcquireAsync(
            FrameAcquisitionRequest request,
            FrameAcquisitionControl control,
            CancellationToken cancellationToken = default)
        {
            if (_owner.CountPhysicalAcquire() == 1)
                _physicalCancellationRegistration = cancellationToken.Register(() =>
                    _owner.HoldCancellationCallbackAsync().GetAwaiter().GetResult());
            // Do not dispose the registration on this method's return: disposing
            // an executing callback would itself hold the provider result hostage.
            return _inner.AcquireAsync(request, control, cancellationToken);
        }

        public CameraProtocolSnapshot ReadProtocolObservations(long afterSequence,
            int maximumCount = 64)
        {
            var snapshot = _inner.ReadProtocolObservations(afterSequence, maximumCount);
            if (snapshot.Observations.Any(value =>
                value.Kind == CameraProtocolViolationKind.LateFrame && value.DroppedFrames > 0))
                _owner.SignalLateFrame();
            return snapshot;
        }

        public ValueTask<CameraOperationResult> StopAsync(
            CancellationToken cancellationToken = default) =>
            _inner.StopAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _physicalCancellationRegistration.DisposeAsync().ConfigureAwait(false);
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}
