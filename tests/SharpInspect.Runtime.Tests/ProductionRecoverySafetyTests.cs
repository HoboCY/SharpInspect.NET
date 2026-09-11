using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Production;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ProductionRecoverySafetyTests
{
    [Fact]
    public async Task V144_C01_ValidPhysicalStopCaptureIsFreshAndCurrent()
    {
        var fixture = CreateFixture();
        await using var registry = fixture.Registry;

        var capture = await registry.CaptureAsync(Guid.NewGuid());

        Assert.True(capture.Available, capture.ReasonCode);
        Assert.NotNull(capture.Observation);
        Assert.Equal(ProductionRecoverySafetyObservationStatus.SafeLineStopped,
            capture.Observation!.Status);
        Assert.True(registry.IsCurrent(capture));
        Assert.Null(registry.FinalFailure(capture));
    }

    [Theory]
    [InlineData(ProductionRecoverySafetyObservationStatus.LineNotStopped,
        "ProductionRecoverySafetyLineNotStopped")]
    [InlineData(ProductionRecoverySafetyObservationStatus.Unavailable,
        "ProductionRecoverySafetySourceUnavailable")]
    [InlineData(ProductionRecoverySafetyObservationStatus.Stale,
        "ProductionRecoverySafetyObservationStale")]
    [InlineData(ProductionRecoverySafetyObservationStatus.Invalid,
        "ProductionRecoverySafetyObservationInvalid")]
    public async Task V144_C02_NonStopOrUnavailableEvidenceFailsClosed(
        ProductionRecoverySafetyObservationStatus status, string expectedReason)
    {
        var fixture = CreateFixture(status: status);
        await using var registry = fixture.Registry;

        var capture = await registry.CaptureAsync(Guid.NewGuid());

        Assert.False(capture.Available);
        Assert.Equal(expectedReason, capture.ReasonCode);
        Assert.Null(capture.Observation);
        Assert.False(registry.IsCurrent(capture));
    }

    [Fact]
    public async Task V144_C03_SourceInvalidationRevokesCaptureAndFinalFence()
    {
        var fixture = CreateFixture();
        await using var registry = fixture.Registry;
        var capture = await registry.CaptureAsync(Guid.NewGuid());
        Assert.True(capture.Available, capture.ReasonCode);

        fixture.Provider.RestartSource();

        Assert.False(registry.IsCurrent(capture));
        Assert.Equal("ProductionRecoverySafetySourceChanged", registry.FinalFailure(capture));
        var starts = 0;
        var accepted = registry.StartIfCurrent(capture, _ =>
        {
            starts++;
            return Task.CompletedTask;
        }, out var operation);
        Assert.False(accepted);
        Assert.Null(operation);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task V144_C04_ObservationEndpointMismatchFailsClosed()
    {
        var fixture = CreateFixture();
        var wrongBinding = CreateProviderBinding(new string('B', 64));
        fixture.Provider.ObservationBinding = wrongBinding;
        await using var registry = fixture.Registry;

        var capture = await registry.CaptureAsync(Guid.NewGuid());

        Assert.False(capture.Available);
        Assert.Equal("ProductionRecoverySafetyEndpointBindingMismatch", capture.ReasonCode);
    }

    [Fact]
    public void V144_C05_ActualProviderTypeAndAssemblyMustMatchBinding()
    {
        var provider = new TestProvider(CreateProviderBinding(),
            new ProductionRecoverySafetySourceState(Guid.NewGuid(), 1, true));
        var forged = new ProductionRecoverySafetyProviderBinding(
            "safety", "1", "station-a", Endpoint,
            "physical-stop", "1", ConfigurationHash,
            typeof(TestProvider), new string('B', 64), TimeSpan.FromSeconds(1));
        var binding = new ProductionRecoveryBinding("station-a", Endpoint, forged,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        Assert.Throws<ArgumentException>(() => new ProductionRecoverySafetyRegistry(binding, provider));
    }

    [Fact]
    public async Task V144_C06_TimeoutRetainsUnretiredObserveAndPreventsOverlap()
    {
        var fixture = CreateFixture(operationTimeout: TimeSpan.FromMilliseconds(20),
            retirementTimeout: TimeSpan.FromSeconds(1));
        var pending = new TaskCompletionSource<ProductionRecoverySafetyObservation>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.Pending = pending;
        await using var registry = fixture.Registry;

        var first = await registry.CaptureAsync(Guid.NewGuid());
        var second = await registry.CaptureAsync(Guid.NewGuid());

        Assert.False(first.Available);
        Assert.Equal("ProductionRecoverySafetyObservationTimeout", first.ReasonCode);
        Assert.False(second.Available);
        Assert.Equal("ProductionRecoverySafetyObservationInProgress", second.ReasonCode);
        Assert.Equal(1, fixture.Provider.ObserveCount);

        pending.SetResult(fixture.Provider.CreateObservation());
        await registry.DisposeAsync();
    }

    [Fact]
    public async Task V144_C07_SynchronousProviderSetupCannotBlockRegistryGate()
    {
        var fixture = CreateFixture(operationTimeout: TimeSpan.FromMilliseconds(20),
            retirementTimeout: TimeSpan.FromSeconds(1));
        var entered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.SynchronousSetupEntered = entered;
        fixture.Provider.SynchronousSetupRelease = release;
        await using var registry = fixture.Registry;

        var captureTask = registry.CaptureAsync(Guid.NewGuid()).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var capture = await captureTask;

        Assert.False(capture.Available);
        Assert.Equal("ProductionRecoverySafetyObservationTimeout", capture.ReasonCode);
        release.SetResult(null);
        await registry.DisposeAsync();
    }

    [Fact]
    public async Task V144_C10_DisposeWaitsForCancellationCallbackAfterObservationCompletes()
    {
        var fixture = CreateFixture(operationTimeout: TimeSpan.FromMilliseconds(20),
            retirementTimeout: TimeSpan.FromSeconds(1));
        var pending = new TaskCompletionSource<ProductionRecoverySafetyObservation>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRelease = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observationReturned = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.Pending = pending;
        fixture.Provider.CancellationCallbackEntered = callbackEntered;
        fixture.Provider.CancellationCallbackRelease = callbackRelease;
        fixture.Provider.ObservationReturned = observationReturned;
        await using var registry = fixture.Registry;

        var capture = await registry.CaptureAsync(Guid.NewGuid());

        Assert.False(capture.Available);
        Assert.Equal("ProductionRecoverySafetyObservationTimeout", capture.ReasonCode);
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        pending.SetResult(fixture.Provider.CreateObservation());
        await observationReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var disposing = registry.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        var duringDispose = await registry.CaptureAsync(Guid.NewGuid());
        Assert.False(duringDispose.Available);
        Assert.Equal("ProductionRecoverySafetyRegistryDisposing", duringDispose.ReasonCode);

        callbackRelease.SetResult(null);
        await disposing.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task V144_C11_FinalFenceUsesCachedSourceWhileCaptureGetterIsBlocked()
    {
        var fixture = CreateFixture(operationTimeout: TimeSpan.FromMilliseconds(20),
            retirementTimeout: TimeSpan.FromSeconds(1));
        await using var registry = fixture.Registry;
        var capture = await registry.CaptureAsync(Guid.NewGuid());
        Assert.True(capture.Available, capture.ReasonCode);

        var sourceGetterEntered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceGetterRelease = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.BlockSourceGetter = true;
        fixture.Provider.SourceGetterEntered = sourceGetterEntered;
        fixture.Provider.SourceGetterRelease = sourceGetterRelease;
        var readsBeforeCapture = fixture.Provider.SourceReadCount;

        var blockedCaptureTask = registry.CaptureAsync(Guid.NewGuid()).AsTask();
        await sourceGetterEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var readsAfterGetterEntered = fixture.Provider.SourceReadCount;
        Assert.Equal(readsBeforeCapture + 1, readsAfterGetterEntered);

        try
        {
            var blockedCapture = await blockedCaptureTask.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(blockedCapture.Available);
            Assert.Equal("ProductionRecoverySafetyObservationTimeout", blockedCapture.ReasonCode);

            // These are the final runtime fences. They must use the immutable cache;
            // a provider Source getter is allowed to block until hardware retirement.
            Assert.True(await Task.Run(() => registry.IsCurrent(capture))
                .WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Null(await Task.Run(() => registry.FinalFailure(capture))
                .WaitAsync(TimeSpan.FromSeconds(1)));
            var started = await Task.Run(() =>
            {
                var accepted = registry.StartIfCurrent(capture, _ => Task.CompletedTask,
                    out var operation);
                return (accepted, operation);
            }).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(started.accepted);
            Assert.NotNull(started.operation);
            Assert.Equal(readsAfterGetterEntered, fixture.Provider.SourceReadCount);

            var overlapping = await registry.CaptureAsync(Guid.NewGuid());
            Assert.False(overlapping.Available);
            Assert.Equal("ProductionRecoverySafetyObservationInProgress",
                overlapping.ReasonCode);
        }
        finally
        {
            sourceGetterRelease.SetResult(null);
        }

        // Once the source transition notification has completed, the same
        // capture must remain fenced even though the getter is no longer blocked.
        fixture.Provider.RestartSource();
        var startsAfterChange = 0;
        var acceptedAfterChange = registry.StartIfCurrent(capture, _ =>
        {
            startsAfterChange++;
            return Task.CompletedTask;
        }, out var operationAfterChange);
        Assert.False(acceptedAfterChange);
        Assert.Null(operationAfterChange);
        Assert.Equal(0, startsAfterChange);
    }

    [Fact]
    public async Task V144_C08_StartIfCurrentFencesPhysicalStarterAgainstLaterInvalidation()
    {
        var fixture = CreateFixture();
        await using var registry = fixture.Registry;
        var capture = await registry.CaptureAsync(Guid.NewGuid());
        var started = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var accepted = registry.StartIfCurrent(capture, _ =>
        {
            started.SetResult(null);
            return Task.CompletedTask;
        }, out var operation);

        Assert.True(accepted);
        Assert.NotNull(operation);
        Assert.True(started.Task.IsCompleted);
        fixture.Provider.RestartSource();
        Assert.False(registry.IsCurrent(capture));
    }

    [Fact]
    public void V144_C09_ObservationContractHasNoSoftwareReadyOrBusyFlags()
    {
        Assert.Null(typeof(ProductionRecoverySafetyObservation).GetProperty("Ready"));
        Assert.Null(typeof(ProductionRecoverySafetyObservation).GetProperty("Busy"));
        Assert.Null(typeof(ProductionRecoverySafetyObservation).GetProperty("ProductionReady"));
    }

    private static Fixture CreateFixture(
        ProductionRecoverySafetyObservationStatus status =
            ProductionRecoverySafetyObservationStatus.SafeLineStopped,
        TimeSpan? operationTimeout = null, TimeSpan? retirementTimeout = null)
    {
        var source = new ProductionRecoverySafetySourceState(Guid.NewGuid(), 1, true);
        var binding = CreateProviderBinding();
        var provider = new TestProvider(binding, source);
        provider.ObservationStatus = status;
        var recovery = new ProductionRecoveryBinding("station-a", Endpoint, binding,
            operationTimeout ?? TimeSpan.FromSeconds(1),
            retirementTimeout ?? TimeSpan.FromSeconds(1));
        return new Fixture(provider, new ProductionRecoverySafetyRegistry(recovery, provider));
    }

    private static ProductionRecoverySafetyProviderBinding CreateProviderBinding(
        string? endpoint = null) => new(
        "safety", "1", "station-a", endpoint ?? Endpoint,
        "physical-stop", "1", ConfigurationHash, typeof(TestProvider),
        ProductionRecoverySafetyRegistry.ComputeProviderAssemblyHash(typeof(TestProvider)),
        TimeSpan.FromSeconds(1));

    private static string Endpoint => new('A', 64);
    private static string ConfigurationHash => new('C', 64);

    private sealed record Fixture(TestProvider Provider, ProductionRecoverySafetyRegistry Registry);

    private sealed class TestProvider : IProductionRecoverySafetyProvider
    {
        private readonly object _gate = new();
        private readonly object _transitionGate = new();
        private ProductionRecoverySafetySourceState _source;
        private int _observeCount;
        private int _sourceReadCount;

        internal TestProvider(ProductionRecoverySafetyProviderBinding binding,
            ProductionRecoverySafetySourceState source)
        {
            Binding = binding;
            _source = source;
        }

        public ProductionRecoverySafetyProviderBinding Binding { get; }
        public ProductionRecoverySafetySourceState Source
        {
            get
            {
                Interlocked.Increment(ref _sourceReadCount);
                if (BlockSourceGetter && SourceGetterEntered is not null &&
                    SourceGetterRelease is not null)
                {
                    SourceGetterEntered.TrySetResult(null);
                    SourceGetterRelease.Task.GetAwaiter().GetResult();
                }
                lock (_gate) return _source;
            }
        }

        public event EventHandler<ProductionRecoverySafetySourceChangedEventArgs>? SourceChanged;
        internal ProductionRecoverySafetyObservationStatus ObservationStatus { get; set; }
        internal ProductionRecoverySafetyProviderBinding? ObservationBinding { get; set; }
        internal TaskCompletionSource<ProductionRecoverySafetyObservation>? Pending { get; set; }
        internal TaskCompletionSource<object?>? SynchronousSetupEntered { get; set; }
        internal TaskCompletionSource<object?>? SynchronousSetupRelease { get; set; }
        internal TaskCompletionSource<object?>? CancellationCallbackEntered { get; set; }
        internal TaskCompletionSource<object?>? CancellationCallbackRelease { get; set; }
        internal TaskCompletionSource<object?>? ObservationReturned { get; set; }
        internal bool BlockSourceGetter { get; set; }
        internal TaskCompletionSource<object?>? SourceGetterEntered { get; set; }
        internal TaskCompletionSource<object?>? SourceGetterRelease { get; set; }
        internal int ObserveCount => Volatile.Read(ref _observeCount);
        internal int SourceReadCount => Volatile.Read(ref _sourceReadCount);

        public ValueTask<ProductionRecoverySafetyObservation> ObserveAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _observeCount);
            if (SynchronousSetupEntered is not null && SynchronousSetupRelease is not null)
            {
                SynchronousSetupEntered.TrySetResult(null);
                SynchronousSetupRelease.Task.GetAwaiter().GetResult();
            }
            var pending = Pending;
            if (pending is not null)
            {
                if (CancellationCallbackEntered is not null && CancellationCallbackRelease is not null)
                {
                    cancellationToken.Register(() =>
                    {
                        CancellationCallbackEntered.TrySetResult(null);
                        CancellationCallbackRelease.Task.GetAwaiter().GetResult();
                    });
                }
                return AwaitPendingAsync(pending.Task);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CreateObservation());
        }

        private async ValueTask<ProductionRecoverySafetyObservation> AwaitPendingAsync(
            Task<ProductionRecoverySafetyObservation> pending)
        {
            var observation = await pending.ConfigureAwait(false);
            ObservationReturned?.TrySetResult(null);
            return observation;
        }

        internal ProductionRecoverySafetyObservation CreateObservation()
        {
            // Keep the observation helper independent from the registry's source
            // getter so the test can block the registry's bounded read specifically.
            ProductionRecoverySafetySourceState source;
            lock (_gate) source = _source;
            var status = ObservationStatus;
            var timestamp = Stopwatch.GetTimestamp();
            if (status == ProductionRecoverySafetyObservationStatus.Stale)
                timestamp -= Stopwatch.Frequency * 10;
            return new ProductionRecoverySafetyObservation(ObservationBinding ?? Binding, status,
                status == ProductionRecoverySafetyObservationStatus.SafeLineStopped
                    ? "PhysicalLineStopped" : "PhysicalSafetyUnavailable",
                source.SourceEpoch, source.SourceGeneration, DateTimeOffset.UtcNow,
                timestamp, Stopwatch.Frequency);
        }

        internal void RestartSource()
        {
            lock (_transitionGate)
            {
                ProductionRecoverySafetySourceState source;
                lock (_gate)
                {
                    source = new ProductionRecoverySafetySourceState(Guid.NewGuid(),
                        _source.SourceGeneration + 1, true);
                }
                try
                {
                    // The event is the source transition linearization point. The
                    // old value remains visible until every handler has returned.
                    SourceChanged?.Invoke(this,
                        new ProductionRecoverySafetySourceChangedEventArgs(source,
                            "SourceRestarted"));
                }
                finally
                {
                    lock (_gate) _source = source;
                }
            }
        }
    }
}
