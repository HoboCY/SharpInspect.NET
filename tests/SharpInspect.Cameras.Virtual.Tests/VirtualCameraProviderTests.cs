using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using Xunit;

namespace SharpInspect.Cameras.Virtual.Tests;

public sealed class VirtualCameraProviderTests
{
    [Fact]
    public async Task V116_P01_DiscoveryAndOpenUseExactStableIdentityAndBoundedProviderIdentity()
    {
        var first = Scenario(device: "virtual:one");
        var second = Scenario(device: "virtual:two", id: "fixture-two");
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualCameraProvider(new[] { first, second }, clock);

        var discovery = await provider.DiscoverAsync();

        Assert.True(discovery.Succeeded);
        Assert.Equal(new[] { "virtual:one", "virtual:two" },
            discovery.Devices.Select(device => device.StableDeviceIdentity));
        Assert.Equal(VirtualCameraProvider.ProviderId, provider.Identity.Id);
        Assert.Equal(VirtualCameraProvider.AdapterPackageVersion, provider.Identity.AdapterVersion);

        var missing = await provider.OpenAsync("virtual:missing");
        Assert.False(missing.Succeeded);
        Assert.Equal("VirtualCameraDeviceMissing", missing.ReasonCode);

        var opened = await provider.OpenAsync("virtual:one");
        Assert.True(opened.Succeeded);
        var duplicate = await provider.OpenAsync("virtual:one");
        Assert.False(duplicate.Succeeded);
        Assert.Equal("VirtualCameraAlreadyOpen", duplicate.ReasonCode);

        await opened.Device!.DisposeAsync();
        var reopened = await provider.OpenAsync("virtual:one");
        Assert.True(reopened.Succeeded);
        await reopened.Device!.DisposeAsync();
    }

    [Fact]
    public async Task V116_P02_AllThreePublicPixelFormatsAcquireThroughPoolWithProvenance()
    {
        var formats = new[]
        {
            (VisionPixelFormat.Mono8, (int?)null, 1),
            (VisionPixelFormat.Mono16, (int?)12, 1),
            (VisionPixelFormat.Bgr24, (int?)null, 1)
        };

        foreach (var (format, validBits, padding) in formats)
        {
            var image = VirtualCameraImage.CreateSynthetic(
                "frame", 2, 2, format, validBits, 17u, padding);
            var scenario = Scenario(image: image);
            using var clock = new VirtualCameraClock(Utc());
            await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
            var device = await OpenAndConfigureAsync(provider, scenario, clock,
                format, validBits);
            Assert.True((await device.StartAsync()).Succeeded);

            var request = Request();
            var acquire = device.AcquireAsync(request).AsTask();
            clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
            var result = await acquire;

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Lease);
            var lease = result.Lease!;
            Assert.Equal(format, lease.Frame.Metadata.PixelFormat);
            Assert.Equal(validBits, lease.Frame.Metadata.ValidBits);
            Assert.Equal(request.Correlation, lease.Frame.Metadata.Correlation);
            Assert.Equal(scenario.StableDeviceIdentity, lease.Provenance.StableDeviceIdentity);
            Assert.Equal(image.ContentHash, scenario.Images[0].ContentHash);
            Assert.NotNull(lease.Provenance.PoolCopyEvidence);
            Assert.Contains("source=memory", lease.Provenance.NormalizationDetails,
                StringComparison.Ordinal);
            lease.Dispose();
            Assert.True(lease.IsReturned);
        }
    }

    [Fact]
    public async Task V116_P03_ReplayingTheSameScenarioAndClockProducesTheSameFrameEvidence()
    {
        static async Task<(string Pixels, string ImageHash, long Timestamp, DateTimeOffset Utc)> Run()
        {
            var scenario = Scenario(seed: 77);
            using var clock = new VirtualCameraClock(Utc());
            await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
            var device = await OpenAndConfigureAsync(provider, scenario, clock,
                VisionPixelFormat.Mono8, null);
            Assert.True((await device.StartAsync()).Succeeded);
            var acquire = device.AcquireAsync(Request(Guid.Parse("11111111-1111-1111-1111-111111111111"))).AsTask();
            clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
            var result = await acquire;
            Assert.NotNull(result.Lease);
            var lease = result.Lease!;
            var pixels = Convert.ToHexString(lease.Frame.GetRowSpan(0).ToArray()) +
                Convert.ToHexString(lease.Frame.GetRowSpan(1).ToArray());
            var evidence = (pixels, lease.Provenance.NormalizationDetails,
                clock.Timestamp, clock.UtcNow);
            lease.Dispose();
            return evidence;
        }

        var first = await Run();
        var second = await Run();

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task V116_P04_DelayedConfigurationFailureHonorsDelayClosesDeviceAndAllowsReopen()
    {
        var scenario = Scenario(configurations: new[]
        {
            new VirtualCameraConfigurationPlan(
                VirtualCameraConfigurationOutcome.ReadBackFailure,
                TimeSpan.FromMilliseconds(5)),
            new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success,
                TimeSpan.Zero)
        });
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var opened = await provider.OpenAsync(scenario.StableDeviceIdentity);
        Assert.NotNull(opened.Device);
        var device = opened.Device!;

        var applying = device.ApplyConfigurationAsync(Configuration(
            VisionPixelFormat.Mono8, null)).AsTask();
        Assert.False(applying.IsCompleted);
        clock.AdvanceBy(TimeSpan.FromMilliseconds(4));
        Assert.False(applying.IsCompleted);
        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        var failed = await applying;

        Assert.False(failed.Succeeded);
        Assert.Equal("VirtualCameraConfigurationReadBackFailed", failed.ReasonCode);
        var health = device.GetHealthSnapshot();
        Assert.Equal(CameraConnectionState.Closed, health.Connection);
        Assert.Equal(CameraConfigurationState.Unknown, health.Configuration);
        Assert.Equal(CameraAcquisitionState.Stopped, health.Acquisition);
        Assert.Equal("VirtualCameraConfigurationReadBackFailed", health.LastFault!.ReasonCode);
        Assert.Equal(0, clock.PendingEventCount);

        var reopened = await provider.OpenAsync(scenario.StableDeviceIdentity);
        Assert.True(reopened.Succeeded);
        var reapplied = await reopened.Device!.ApplyConfigurationAsync(
            Configuration(VisionPixelFormat.Mono8, null));
        Assert.True(reapplied.Succeeded);
    }

    [Fact]
    public async Task V116_P05_DelayedConfigurationPreservesCapabilityDifferences()
    {
        var scenario = Scenario(
            capabilities: Capabilities(VisionPixelFormat.Mono8, null, quantized: true),
            configurations: new[] { new VirtualCameraConfigurationPlan(
                VirtualCameraConfigurationOutcome.Success, TimeSpan.FromMilliseconds(3)) });
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var opened = await provider.OpenAsync(scenario.StableDeviceIdentity);
        Assert.NotNull(opened.Device);
        var device = opened.Device!;

        var applying = device.ApplyConfigurationAsync(Configuration(
            VisionPixelFormat.Mono8, null, exposure: 1.4)).AsTask();
        clock.AdvanceBy(TimeSpan.FromMilliseconds(3));
        var result = await applying;

        Assert.True(result.Succeeded);
        var difference = Assert.Single(result.Differences);
        Assert.Equal(CameraNumericSetting.ExposureTimeUs, difference.Setting);
        Assert.Equal(1.4, difference.Requested);
        Assert.Equal(1, difference.Effective);
        Assert.Equal(1, result.Effective!.ExposureTimeUs);
    }

    [Fact]
    public async Task V116_P06_StartAndSecondAcquireRejectPendingAttemptWithoutChangingWaitingState()
    {
        var scenario = Scenario(acquisitions: new[]
        {
            new VirtualCameraAcquisitionPlan(new[] { Signal("frame", 5) })
        });
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var device = await OpenAndConfigureAsync(provider, scenario, clock,
            VisionPixelFormat.Mono8, null);
        Assert.True((await device.StartAsync()).Succeeded);

        var first = device.AcquireAsync(Request()).AsTask();
        var second = await device.AcquireAsync(Request(Guid.NewGuid()));
        var startAgain = await device.StartAsync();

        Assert.False(second.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.AlreadyPending, second.Failure!.Kind);
        Assert.Equal("VirtualCameraAcquisitionPending", second.Failure.ReasonCode);
        Assert.False(startAgain.Succeeded);
        Assert.Equal("VirtualCameraAcquisitionPending", startAgain.ReasonCode);
        Assert.Equal(CameraAcquisitionState.WaitingForFrame,
            device.GetHealthSnapshot().Acquisition);

        clock.AdvanceBy(TimeSpan.FromMilliseconds(5));
        var completed = await first;
        Assert.True(completed.Succeeded);
        completed.Lease!.Dispose();
    }

    [Fact]
    public async Task V116_P07_TimeoutKeepsAttemptSingleShotAndDropsLateFrame()
    {
        var scenario = Scenario(acquisitions: new[]
        {
            new VirtualCameraAcquisitionPlan(new[] { Signal("frame", 10) })
        });
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var device = await OpenAndConfigureAsync(provider, scenario, clock,
            VisionPixelFormat.Mono8, null, timeoutMs: 5);
        Assert.True((await device.StartAsync()).Succeeded);

        var acquire = device.AcquireAsync(Request()).AsTask();
        clock.AdvanceBy(TimeSpan.FromMilliseconds(5));
        var timeout = await acquire;
        Assert.False(timeout.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.TimedOut, timeout.Failure!.Kind);
        Assert.Equal(CameraAcquisitionState.Armed, device.GetHealthSnapshot().Acquisition);

        clock.AdvanceBy(TimeSpan.FromMilliseconds(5));
        var health = device.GetHealthSnapshot();
        Assert.Equal("VirtualCameraLateFrameDropped", health.LastFault!.ReasonCode);
        Assert.Equal(1, provider.GetDiagnostics().Devices.Single().FramesDropped);
        Assert.Equal(1, provider.GetDiagnostics().Devices.Single().AcquisitionCursor);
    }

    [Fact]
    public async Task V116_P08_DisconnectAfterCompletedFrameStillClosesDevice()
    {
        var scenario = Scenario(acquisitions: new[]
        {
            new VirtualCameraAcquisitionPlan(new[]
            {
                Signal("frame", 1),
                new VirtualCameraSignal(TimeSpan.FromMilliseconds(2),
                    VirtualCameraSignalKind.Disconnect)
            })
        });
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var device = await OpenAndConfigureAsync(provider, scenario, clock,
            VisionPixelFormat.Mono8, null, timeoutMs: 10);
        Assert.True((await device.StartAsync()).Succeeded);

        var acquire = device.AcquireAsync(Request()).AsTask();
        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        var success = await acquire;
        Assert.True(success.Succeeded);
        success.Lease!.Dispose();

        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        var health = device.GetHealthSnapshot();
        Assert.Equal(CameraConnectionState.Closed, health.Connection);
        Assert.Equal(CameraConfigurationState.Unknown, health.Configuration);
        Assert.Equal("VirtualCameraDisconnected", health.LastFault!.ReasonCode);
        Assert.Equal(0, clock.PendingEventCount);
    }

    [Fact]
    public async Task V116_P09_NonCurrentFrameIsDroppedButCurrentFrameRemainsObservableWithFault()
    {
        var scenario = Scenario(acquisitions: new[]
        {
            new VirtualCameraAcquisitionPlan(new[]
            {
                new VirtualCameraSignal(TimeSpan.FromMilliseconds(1),
                    VirtualCameraSignalKind.Frame, "frame", VirtualFrameAssociation.Uncorrelated),
                Signal("frame", 1)
            })
        });
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var device = await OpenAndConfigureAsync(provider, scenario, clock,
            VisionPixelFormat.Mono8, null);
        Assert.True((await device.StartAsync()).Succeeded);
        var acquire = device.AcquireAsync(Request()).AsTask();

        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        var result = await acquire;

        Assert.True(result.Succeeded);
        Assert.Equal("VirtualCameraFrameAssociationDropped",
            device.GetHealthSnapshot().LastFault!.ReasonCode);
        Assert.Equal(1, provider.GetDiagnostics().Devices.Single().FramesDropped);
        result.Lease!.Dispose();
    }

    [Fact]
    public async Task V116_P10_SameOffsetCurrentFramesAreProtocolAmbiguous()
    {
        var scenario = Scenario(acquisitions: new[]
        {
            new VirtualCameraAcquisitionPlan(new[] { Signal("frame", 1), Signal("frame", 1) })
        });
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var device = await OpenAndConfigureAsync(provider, scenario, clock,
            VisionPixelFormat.Mono8, null);
        Assert.True((await device.StartAsync()).Succeeded);
        var acquire = device.AcquireAsync(Request()).AsTask();

        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        var result = await acquire;

        Assert.False(result.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.ProtocolViolation, result.Failure!.Kind);
        Assert.Equal("VirtualCameraFrameAmbiguous", result.Failure.ReasonCode);
        Assert.Equal(CameraAcquisitionState.Armed, device.GetHealthSnapshot().Acquisition);
        Assert.Equal(2, provider.GetDiagnostics().Devices.Single().FramesDropped);
    }

    [Fact]
    public async Task V116_P11_CancelAndStopInvalidatePendingCallbacksWithoutClockLeak()
    {
        var scenario = Scenario(acquisitions: new[]
        {
            new VirtualCameraAcquisitionPlan(new[] { Signal("frame", 5) }),
            new VirtualCameraAcquisitionPlan(new[] { Signal("frame", 5) })
        });
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var device = await OpenAndConfigureAsync(provider, scenario, clock,
            VisionPixelFormat.Mono8, null);
        Assert.True((await device.StartAsync()).Succeeded);

        using (var cancellation = new CancellationTokenSource())
        {
            var cancelled = device.AcquireAsync(Request(), cancellation.Token).AsTask();
            cancellation.Cancel();
            var result = await cancelled;
            Assert.False(result.Succeeded);
            Assert.Equal(CameraAcquisitionFailureKind.Cancelled, result.Failure!.Kind);
        }

        var stopped = device.AcquireAsync(Request(Guid.NewGuid())).AsTask();
        var stop = await device.StopAsync();
        var stoppedResult = await stopped;
        Assert.True(stop.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.Cancelled, stoppedResult.Failure!.Kind);
        Assert.Equal(0, clock.PendingEventCount);
        Assert.Equal(CameraAcquisitionState.Stopped, device.GetHealthSnapshot().Acquisition);
    }

    [Fact]
    public async Task V116_P12_ProviderDisposeDrainsCallbacksButKeepsPublishedLeaseAliveUntilReturn()
    {
        var scenario = Scenario(
            acquisitions: new[] { new VirtualCameraAcquisitionPlan(new[] { Signal("frame", 1) }) },
            unsolicited: new[] { new VirtualCameraSignal(TimeSpan.FromMilliseconds(10),
                VirtualCameraSignalKind.Frame, "frame", VirtualFrameAssociation.Uncorrelated) });
        using var clock = new VirtualCameraClock(Utc());
        var provider = new VirtualCameraProvider(new[] { scenario }, clock, poolCapacity: 1);
        try
        {
            var device = await OpenAndConfigureAsync(provider, scenario, clock,
                VisionPixelFormat.Mono8, null);
            Assert.True((await device.StartAsync()).Succeeded);
            var acquire = device.AcquireAsync(Request()).AsTask();
            clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
            var result = await acquire;
            Assert.NotNull(result.Lease);
            var lease = result.Lease!;

            await provider.DisposeAsync();
            Assert.Equal(0, clock.PendingEventCount);
            Assert.True(provider.GetDiagnostics().IsDisposed);
            Assert.Equal(2, lease.Frame.GetRowSpan(0).Length);
            Assert.False(lease.IsReturned);
            lease.Dispose();
            Assert.True(lease.IsReturned);
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public void V116_P13_ProviderBoundsScenarioEnumerationBeforeExtraAllocation()
    {
        var scenario = Scenario();
        static IEnumerable<VirtualCameraScenario> TooMany(VirtualCameraScenario value)
        {
            for (var index = 0; index < 5; index++)
                yield return value;
            throw new InvalidOperationException("VirtualProviderEnumeratedPastBound");
        }
        using var clock = new VirtualCameraClock(Utc());

        var exception = Assert.Throws<ArgumentException>(() =>
            new VirtualCameraProvider(TooMany(scenario), clock));
        Assert.Equal("scenarios", exception.ParamName);
    }

    [Fact]
    public async Task V116_P14_ProductionBufferFaultRequiresLeaseReturnBeforeExplicitPoolReset()
    {
        var scenario = Scenario(acquisitions: new[]
        {
            new VirtualCameraAcquisitionPlan(new[] { Signal("frame", 1) }),
            new VirtualCameraAcquisitionPlan(new[] { Signal("frame", 1) }),
            new VirtualCameraAcquisitionPlan(new[] { Signal("frame", 1) })
        });
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock,
            poolCapacity: 1);
        var device = await OpenAndConfigureAsync(provider, scenario, clock,
            VisionPixelFormat.Mono8, null);
        Assert.True((await device.StartAsync()).Succeeded);

        var first = device.AcquireAsync(ProductionRequest(1)).AsTask();
        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        var firstResult = await first;
        Assert.True(firstResult.Succeeded);
        var heldLease = firstResult.Lease!;

        var second = device.AcquireAsync(ProductionRequest(2)).AsTask();
        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        var exhausted = await second;
        Assert.False(exhausted.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.BufferUnavailable,
            exhausted.Failure!.Kind);
        Assert.Equal("FrameBufferExhausted", exhausted.Failure.ReasonCode);
        Assert.Equal(CameraConnectionState.Closed, device.GetHealthSnapshot().Connection);

        var blocked = await provider.OpenAsync(scenario.StableDeviceIdentity);
        Assert.False(blocked.Succeeded);
        Assert.Equal("VirtualCameraBufferUnavailable", blocked.ReasonCode);
        Assert.Equal(2, heldLease.Frame.GetRowSpan(0).Length);

        heldLease.Dispose();
        var reopened = await provider.OpenAsync(scenario.StableDeviceIdentity);
        Assert.True(reopened.Succeeded);
        var newDevice = reopened.Device!;
        var configured = await newDevice.ApplyConfigurationAsync(
            Configuration(VisionPixelFormat.Mono8, null));
        Assert.True(configured.Succeeded);
        Assert.True((await newDevice.StartAsync()).Succeeded);

        var remaining = newDevice.AcquireAsync(ProductionRequest(3)).AsTask();
        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        var remainingResult = await remaining;
        Assert.True(remainingResult.Succeeded);
        remainingResult.Lease!.Dispose();

        var diagnostics = provider.GetDiagnostics().Devices.Single();
        Assert.Equal(3, diagnostics.AcquisitionCursor);
        Assert.Equal(0, diagnostics.OutstandingLeases);
        Assert.True(diagnostics.InfrastructureFailures > 0);
    }

    private static async Task<ICameraDevice> OpenAndConfigureAsync(
        VirtualCameraProvider provider, VirtualCameraScenario scenario,
        VirtualCameraClock clock, VisionPixelFormat format, int? validBits,
        int timeoutMs = 20)
    {
        var opened = await provider.OpenAsync(scenario.StableDeviceIdentity);
        Assert.True(opened.Succeeded, opened.ReasonCode);
        Assert.NotNull(opened.Device);
        var device = opened.Device!;
        var configured = await device.ApplyConfigurationAsync(Configuration(
            format, validBits, timeoutMs: timeoutMs));
        Assert.True(configured.Succeeded, configured.ReasonCode);
        return device;
    }

    private static RequestedCameraConfiguration Configuration(VisionPixelFormat format,
        int? validBits, int timeoutMs = 20, double exposure = 2) =>
        new(ProductionAcquisitionMode.SoftwareTrigger, exposure, 0,
            new RegionOfInterest(0, 0, 2, 2), format, validBits, timeoutMs, 0, null);

    private static FrameAcquisitionRequest Request(Guid? id = null) =>
        new(new ExecutionCorrelationId(ExecutionKind.Manual,
            id ?? Guid.Parse("22222222-2222-2222-2222-222222222222")), "camera.primary");

    private static FrameAcquisitionRequest ProductionRequest(int number) =>
        new(new ExecutionCorrelationId(ExecutionKind.Production,
            Guid.Parse("33333333-3333-3333-3333-" + number.ToString("D12"))),
            "camera.primary");

    private static VirtualCameraSignal Signal(string imageId, int milliseconds) =>
        new(TimeSpan.FromMilliseconds(milliseconds), VirtualCameraSignalKind.Frame, imageId);

    private static VirtualCameraScenario Scenario(string id = "fixture", string device = "virtual:one",
        uint seed = 42, VirtualCameraImage? image = null,
        CameraCapabilities? capabilities = null,
        IEnumerable<VirtualCameraAcquisitionPlan>? acquisitions = null,
        IEnumerable<VirtualCameraConfigurationPlan>? configurations = null,
        IEnumerable<VirtualCameraOpenOutcome>? opens = null,
        IEnumerable<VirtualCameraSignal>? unsolicited = null)
    {
        image ??= VirtualCameraImage.CreateSynthetic("frame", 2, 2,
            VisionPixelFormat.Mono8, null, seed, 1);
        capabilities ??= Capabilities(image.PixelFormat, image.ValidBits);
        acquisitions ??= new[] { new VirtualCameraAcquisitionPlan(new[] { Signal("frame", 1) }) };
        return new VirtualCameraScenario(id, "1", seed, device, capabilities,
            new[] { image }, acquisitions, configurations, opens, unsolicited);
    }

    private static CameraCapabilities Capabilities(VisionPixelFormat format, int? validBits,
        bool quantized = false) => new(
        new[] { ProductionAcquisitionMode.SoftwareTrigger },
        new[] { format },
        format == VisionPixelFormat.Mono16 ? new[] { validBits!.Value } : Array.Empty<int>(),
        new(1, 10, 1, quantized ? CameraQuantizationMode.Nearest : CameraQuantizationMode.Exact,
            quantized ? 1 : 0),
        new(0, 10, 1, CameraQuantizationMode.Exact),
        new(0, 10, 1, CameraQuantizationMode.Exact),
        new(16, 16, new(0, 15, 1), new(0, 15, 1), new(1, 16, 1), new(1, 16, 1)));

    private static DateTimeOffset Utc() =>
        new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
}
