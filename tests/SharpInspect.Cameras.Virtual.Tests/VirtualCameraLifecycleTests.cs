using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Cameras.Virtual.Tests;

public sealed class VirtualCameraLifecycleTests
{
    private static readonly DateTimeOffset InitialUtc = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task V116_L01_PublishedOwnershipSurvivesBothDeviceAndProviderDisposal()
    {
        using var clock = new VirtualCameraClock(InitialUtc);
        var image = Image();
        var concrete = new VirtualCameraProvider(new[] { Scenario("one", image,
            new[] { Frame(1), Frame(5) }) }, clock, poolCapacity: 1);
        await using ICameraProvider provider = concrete;
        var device = await OpenConfigured(provider, "one");
        var task = device.AcquireAsync(Request(1)).AsTask();
        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Succeeded);
        using var lease = result.Lease!;
        var frame = lease.Frame;
        var expected = image.GetRowSpan(0).ToArray();

        await device.DisposeAsync();
        await provider.DisposeAsync();
        Assert.False(lease.IsReturned);
        Assert.Equal(expected, frame.GetRowSpan(0).ToArray());
        Assert.Equal(1, concrete.GetDiagnostics().Devices.Single().OutstandingLeases);
        Assert.Equal(0, clock.PendingEventCount);

        lease.Dispose(); lease.Dispose();
        Assert.True(lease.IsReturned);
        Assert.Equal(0, concrete.GetDiagnostics().Devices.Single().OutstandingLeases);
        Assert.Throws<InvalidOperationException>(() => frame.GetRowSpan(0));
    }

    [Fact]
    public async Task V116_L02_DisconnectAfterPublishedFrameIsStillADeviceFactAndClosesOldSchedules()
    {
        using var clock = new VirtualCameraClock(InitialUtc);
        var concrete = new VirtualCameraProvider(new[] { Scenario("one", Image(), new[]
        {
            Frame(1), new VirtualCameraSignal(TimeSpan.FromMilliseconds(2), VirtualCameraSignalKind.Disconnect),
            Frame(5)
        }) }, clock);
        await using ICameraProvider provider = concrete;
        var device = await OpenConfigured(provider, "one");
        var task = device.AcquireAsync(Request(1)).AsTask();
        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Succeeded);
        result.Lease!.Dispose();
        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        var health = device.GetHealthSnapshot();
        Assert.NotEqual(CameraConnectionState.Open, health.Connection);
        Assert.Equal(CameraConfigurationState.Unknown, health.Configuration);
        Assert.Equal(CameraAcquisitionState.Stopped, health.Acquisition);
        Assert.Equal(CameraFaultClassification.ConnectionLost, health.LastFault!.Classification);

        await provider.DisposeAsync();
        Assert.Equal(0, clock.PendingEventCount);
        clock.AdvanceBy(TimeSpan.FromMilliseconds(20));
        Assert.Equal(1, concrete.GetDiagnostics().Devices.Single().FramesProduced);
        Assert.Equal(0, concrete.GetDiagnostics().Devices.Single().OutstandingLeases);
        await device.DisposeAsync();
    }

    [Fact]
    public async Task V116_L03_OneProviderKeepsTwoDeviceCorrelationsAndStopBoundariesIndependent()
    {
        using var clock = new VirtualCameraClock(InitialUtc);
        var concrete = new VirtualCameraProvider(new[]
        {
            Scenario("one", Image(), new[] { Frame(5) }),
            Scenario("two", Image(), new[] { Frame(5) })
        }, clock, poolCapacity: 1);
        await using ICameraProvider provider = concrete;
        var before = concrete.GetDiagnostics();
        var firstDiscovery = await provider.DiscoverAsync();
        var secondDiscovery = await provider.DiscoverAsync();
        Assert.Equal(firstDiscovery.Devices, secondDiscovery.Devices);
        Assert.All(concrete.GetDiagnostics().Devices, row => Assert.Equal(0, row.OpenCount));
        Assert.Equal(0, clock.PendingEventCount);
        await using var one = await OpenConfigured(provider, "one");
        await using var two = await OpenConfigured(provider, "two");
        var requestOne = Request(1); var requestTwo = Request(2);
        var first = one.AcquireAsync(requestOne).AsTask();
        var second = two.AcquireAsync(requestTwo).AsTask();
        Assert.True((await one.StopAsync()).Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.Cancelled,
            (await first.WaitAsync(TimeSpan.FromSeconds(10))).Failure!.Kind);
        Assert.Equal(CameraAcquisitionState.WaitingForFrame, two.GetHealthSnapshot().Acquisition);
        clock.AdvanceBy(TimeSpan.FromMilliseconds(5));
        var result = await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Succeeded);
        using var lease = result.Lease!;
        Assert.Equal(requestTwo.Correlation, lease.Frame.Metadata.Correlation);
        Assert.Equal(requestTwo.Correlation, lease.Provenance.Correlation);
        Assert.Equal("two", lease.Provenance.StableDeviceIdentity);
        lease.Dispose();
        Assert.All(concrete.GetDiagnostics().Devices, row => Assert.Equal(0, row.OutstandingLeases));
        Assert.Equal(0, before.InfrastructureFailures);
        Assert.Equal(0, concrete.GetDiagnostics().InfrastructureFailures);
    }

    [Fact]
    public async Task V116_L04_DisconnectFromCompletedAttemptTerminatesTheCurrentAttemptImmediately()
    {
        using var clock = new VirtualCameraClock(InitialUtc);
        var baseScenario = Scenario("one", Image(), new[] { Frame(1) });
        var scenario = new VirtualCameraScenario("disconnect.overlap", "1", 42, "one",
            baseScenario.Capabilities, baseScenario.Images, new[]
            {
                new VirtualCameraAcquisitionPlan(new[] { Frame(1),
                    new VirtualCameraSignal(TimeSpan.FromMilliseconds(3), VirtualCameraSignalKind.Disconnect) }),
                new VirtualCameraAcquisitionPlan(new[] { Frame(5) })
            });
        var concrete = new VirtualCameraProvider(new[] { scenario }, clock);
        await using ICameraProvider provider = concrete;
        await using var device = await OpenConfigured(provider, "one");
        var first = device.AcquireAsync(Request(1)).AsTask();
        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        (await first.WaitAsync(TimeSpan.FromSeconds(10))).Lease!.Dispose();
        var second = device.AcquireAsync(Request(2)).AsTask();
        clock.AdvanceBy(TimeSpan.FromMilliseconds(2));
        // A disconnect is already observed at t=3; no further virtual time is
        // needed to finish the sole current attempt. Real time only guards a hang.
        var disconnected = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CameraAcquisitionFailureKind.Disconnected, disconnected.Failure!.Kind);
        Assert.Equal(CameraConnectionState.Closed, device.GetHealthSnapshot().Connection);
        Assert.Equal(0, clock.PendingEventCount);
        clock.AdvanceBy(TimeSpan.FromMilliseconds(20));
        Assert.Equal(CameraAcquisitionState.Stopped, device.GetHealthSnapshot().Acquisition);
        Assert.Equal(1, concrete.GetDiagnostics().Devices.Single().FramesProduced);
    }

    [Fact]
    public async Task V116_L05_CancelledConfigurationClosesAndExplicitReopenAppliesTheNextWholePlan()
    {
        using var clock = new VirtualCameraClock(InitialUtc);
        var baseline = Scenario("one", Image(), new[] { Frame(1) });
        var scenario = new VirtualCameraScenario("configuration.cancel", "1", 42, "one",
            baseline.Capabilities, baseline.Images, baseline.Acquisitions, new[]
            {
                new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success, TimeSpan.FromMilliseconds(10)),
                new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero)
            });
        var concrete = new VirtualCameraProvider(new[] { scenario }, clock);
        await using ICameraProvider provider = concrete;
        await using var first = (await provider.OpenAsync("one")).Device!;
        var requested = new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            10, 0, new(0, 0, 2, 2), VisionPixelFormat.Mono8, null, 10, 0, null);
        using (var before = new CancellationTokenSource())
        {
            before.Cancel();
            Assert.False((await first.ApplyConfigurationAsync(requested, before.Token)).Succeeded);
            Assert.Equal(0, concrete.GetDiagnostics().Devices.Single().ConfigurationCursor);
        }
        using var cancellation = new CancellationTokenSource();
        var applying = first.ApplyConfigurationAsync(requested, cancellation.Token).AsTask();
        Assert.Equal(CameraConfigurationState.Applying, first.GetHealthSnapshot().Configuration);
        cancellation.Cancel();
        Assert.False((await applying.WaitAsync(TimeSpan.FromSeconds(10))).Succeeded);
        Assert.Equal(CameraConnectionState.Closed, first.GetHealthSnapshot().Connection);
        Assert.Equal(CameraConfigurationState.Unknown, first.GetHealthSnapshot().Configuration);
        Assert.Equal(0, clock.PendingEventCount);
        Assert.False((await first.StartAsync()).Succeeded);
        await using var reopened = await OpenConfigured(provider, "one");
        Assert.Equal(2, concrete.GetDiagnostics().Devices.Single().ConfigurationCursor);
        Assert.Equal(CameraConfigurationState.Applied, reopened.GetHealthSnapshot().Configuration);
        Assert.Equal(CameraConfigurationState.Unknown, first.GetHealthSnapshot().Configuration);
    }

    [Fact]
    public async Task V116_L06_FrameAtDeadlineWinsItsRegisteredTieWithoutRunningConsumerInline()
    {
        using var clock = new VirtualCameraClock(InitialUtc);
        var concrete = new VirtualCameraProvider(new[] { Scenario("one", Image(), new[] { Frame(10) }) }, clock);
        await using ICameraProvider provider = concrete;
        await using var device = await OpenConfigured(provider, "one");
        var acquire = device.AcquireAsync(Request(1)).AsTask();
        var advancingThread = Environment.CurrentManagedThreadId;
        var advanceReturned = 0;
        var consumerInline = false;
        var consumed = acquire.ContinueWith(_ =>
        {
            consumerInline = Environment.CurrentManagedThreadId == advancingThread &&
                Volatile.Read(ref advanceReturned) == 0;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        clock.AdvanceBy(TimeSpan.FromMilliseconds(10));
        Volatile.Write(ref advanceReturned, 1);
        var result = await acquire.WaitAsync(TimeSpan.FromSeconds(10));
        await consumed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(consumerInline);
        Assert.True(result.Succeeded);
        using var lease = result.Lease!;
        Assert.Equal(TimeSpan.FromMilliseconds(10).Ticks,
            lease.Provenance.Milestones.NativeFrameReceived!.MonotonicTimestamp);
        Assert.Equal(InitialUtc, lease.Provenance.Milestones.AcquisitionStarted!.HostObservedAtUtc);
        Assert.Equal(InitialUtc.AddMilliseconds(10), lease.Frame.Metadata.HostCaptureUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V116_L07_ScheduleFailurePublishesHealthAndDrainsOnlyItsPartialSchedules(bool disposeClock)
    {
        using var clock = new VirtualCameraClock(InitialUtc);
        var concrete = new VirtualCameraProvider(new[] { Scenario("one", Image(), new[] { Frame(1) }) }, clock);
        await using ICameraProvider provider = concrete;
        await using var device = await OpenConfigured(provider, "one");
        var unrelated = new List<IDisposable>();
        if (disposeClock) clock.Dispose();
        else
            for (var index = 0; index < 511; index++)
                unrelated.Add(clock.Schedule(TimeSpan.FromSeconds(1).Ticks, static () => { }));
        try
        {
            var result = await device.AcquireAsync(Request(1));
            Assert.Equal(CameraAcquisitionFailureKind.DeviceFault, result.Failure!.Kind);
            Assert.Equal("VirtualCameraAcquisitionScheduleFailed", result.ReasonCode);
            var health = device.GetHealthSnapshot();
            Assert.NotNull(health.LastFault);
            Assert.Equal(CameraFaultClassification.DeviceFault, health.LastFault!.Classification);
            Assert.Equal(result.ReasonCode, health.LastFault.ReasonCode);
            Assert.Equal(CameraConnectionState.Closed, health.Connection);
            Assert.Equal(CameraConfigurationState.Unknown, health.Configuration);
            Assert.Equal(CameraAcquisitionState.Stopped, health.Acquisition);
            Assert.Equal(disposeClock ? 0 : 511, clock.PendingEventCount);
            Assert.Equal(1, concrete.GetDiagnostics().InfrastructureFailures);
            Assert.Equal(1, concrete.GetDiagnostics().Devices.Single().AcquisitionCursor);
            Assert.Equal(0, concrete.GetDiagnostics().Devices.Single().OutstandingLeases);
            Assert.Equal(CameraAcquisitionFailureKind.Disconnected, (await device.AcquireAsync(Request(2))).Failure!.Kind);
        }
        finally { foreach (var handle in unrelated) handle.Dispose(); }
        Assert.Equal(0, clock.PendingEventCount);
    }

    private static async Task<ICameraDevice> OpenConfigured(ICameraProvider provider, string identity)
    {
        var open = await provider.OpenAsync(identity);
        Assert.True(open.Succeeded);
        var device = open.Device!;
        Assert.True((await device.ApplyConfigurationAsync(new(ProductionAcquisitionMode.SoftwareTrigger,
            10, 0, new(0, 0, 2, 2), VisionPixelFormat.Mono8, null, 10, 0, null))).Succeeded);
        Assert.True((await device.StartAsync()).Succeeded);
        return device;
    }
    private static FrameAcquisitionRequest Request(int number) => new(new(ExecutionKind.Qualification,
        Guid.Parse("00000000-0000-0000-0000-" + number.ToString("D12"))), "Primary");
    private static VirtualCameraSignal Frame(int milliseconds) => new(TimeSpan.FromMilliseconds(milliseconds),
        VirtualCameraSignalKind.Frame, "image");
    private static VirtualCameraImage Image() => VirtualCameraImage.CreateSynthetic("image", 2, 2,
        VisionPixelFormat.Mono8, null, 42);
    private static VirtualCameraScenario Scenario(string identity, VirtualCameraImage image,
        IEnumerable<VirtualCameraSignal> signals) => new("lifecycle." + identity, "1", 42, identity,
        new(new[] { ProductionAcquisitionMode.SoftwareTrigger }, new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
            new(10, 100, 10, CameraQuantizationMode.Exact), new(0, 10, 1, CameraQuantizationMode.Exact),
            new(0, 10, 1, CameraQuantizationMode.Exact),
            new(2, 2, new(0, 1, 1), new(0, 1, 1), new(1, 2, 1), new(1, 2, 1))),
        new[] { image }, new[] { new VirtualCameraAcquisitionPlan(signals) });
}
