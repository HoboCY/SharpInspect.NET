using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using Xunit;

namespace SharpInspect.Cameras.Virtual.Tests;

public sealed class VirtualCameraPreviewTests
{
    private static readonly DateTimeOffset InitialUtc =
        new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task V133_V01_ExplicitPreviewStreamsImmutableLatestFramesWithoutProductionAcquire()
    {
        using var clock = new VirtualCameraClock(InitialUtc);
        var image = VirtualCameraImage.CreateSynthetic("preview", 2, 2,
            VisionPixelFormat.Mono8, null, 42, rowPaddingBytes: 2);
        var previewScenario = new VirtualCameraPreviewScenario(new[] { image });
        await using var provider = new VirtualCameraProvider(new[] { Scenario(image,
            previewScenario) }, clock);
        var opened = await provider.OpenAsync("virtual.preview");
        Assert.True(opened.Succeeded, opened.ReasonCode);
        await using var device = opened.Device!;
        var preview = Assert.IsAssignableFrom<ICameraPreviewDevice>(device);
        Assert.True(preview.PreviewCapabilities.Supported);

        var settings = new PreviewCameraProcessSettings(10, 1,
            new RegionOfInterest(0, 0, image.Width, image.Height), image.PixelFormat,
            image.ValidBits);
        var unconfigured = await preview.StartPreviewAsync(Guid.NewGuid(),
            new PreviewTuningConfiguration(settings));
        Assert.False(unconfigured.Succeeded);
        Assert.Equal("VirtualCameraPreviewConfigurationRequired", unconfigured.ReasonCode);
        Assert.Equal(CameraAcquisitionState.Stopped, device.GetHealthSnapshot().Acquisition);
        await ApplyPreviewBaselineAsync(device, image);
        var started = await preview.StartPreviewAsync(Guid.Parse(
            "13300000-0000-0000-0000-000000000001"),
            new PreviewTuningConfiguration(settings));
        Assert.True(started.Succeeded, started.ReasonCode);
        Assert.NotNull(started.Configuration);
        Assert.Equal(CameraAcquisitionState.Previewing, device.GetHealthSnapshot().Acquisition);

        var first = await preview.ReadLatestPreviewFrameAsync(0);
        Assert.True(first.Succeeded, first.ReasonCode);
        var frame = first.Frame!;
        Assert.Equal(1, frame.Sequence);
        Assert.Equal(image.Width, frame.Width);
        Assert.Equal(image.Height, frame.Height);
        Assert.Equal(image.StrideBytes, frame.StrideBytes);
        var originalByte = frame.GetRowSpan(0)[0];
        var row = frame.GetRowSpan(0).ToArray();
        row[0] ^= 0xFF;
        Assert.Equal(originalByte, frame.GetRowSpan(0)[0]);

        var noNew = await preview.ReadLatestPreviewFrameAsync(frame.Sequence);
        Assert.False(noNew.Succeeded);
        Assert.Equal("VirtualCameraPreviewNoNewFrame", noNew.ReasonCode);

        var productionRequest = new RequestedCameraConfiguration(
            ProductionAcquisitionMode.SoftwareTrigger, 10, 0,
            new RegionOfInterest(0, 0, image.Width, image.Height), image.PixelFormat,
            image.ValidBits, 20, 0, null);
        var apply = await device.ApplyConfigurationAsync(productionRequest);
        Assert.False(apply.Succeeded);
        Assert.Equal("VirtualCameraPreviewActive", apply.ReasonCode);
        var start = await device.StartAsync();
        Assert.False(start.Succeeded);
        Assert.Equal("VirtualCameraPreviewActive", start.ReasonCode);
        var acquire = await device.AcquireAsync(new FrameAcquisitionRequest(
            new ExecutionCorrelationId(ExecutionKind.Manual,
                Guid.Parse("13300000-0000-0000-0000-000000000002")), "Primary"));
        Assert.False(acquire.Succeeded);
        Assert.Equal("VirtualCameraPreviewActive", acquire.Failure!.ReasonCode);

        clock.AdvanceBy(VirtualCameraPreviewScenario.DefaultFrameInterval);
        var second = await preview.ReadLatestPreviewFrameAsync(frame.Sequence);
        Assert.True(second.Succeeded, second.ReasonCode);
        Assert.Equal(2L, second.Frame!.Sequence);
        Assert.Equal(0, provider.GetDiagnostics().Devices.Single().FramesProduced);

        var stopped = await preview.StopPreviewAsync();
        Assert.True(stopped.Succeeded, stopped.ReasonCode);
        Assert.Equal(CameraAcquisitionState.Stopped, device.GetHealthSnapshot().Acquisition);
        Assert.Equal(0, clock.PendingEventCount);
        var afterStop = await preview.ReadLatestPreviewFrameAsync(second.Frame.Sequence);
        Assert.False(afterStop.Succeeded);
        Assert.Equal("VirtualCameraPreviewNotStarted", afterStop.ReasonCode);
    }

    [Fact]
    public async Task V133_V02_AutomaticReadbackFreezesToConcreteOffConfiguration()
    {
        using var clock = new VirtualCameraClock(InitialUtc);
        var image = VirtualCameraImage.CreateSynthetic("preview.rgb", 2, 2,
            VisionPixelFormat.Bgr24, null, 43);
        var roi = new RegionOfInterest(0, 0, image.Width, image.Height);
        var whiteBalance = new WhiteBalanceRgb(1, 1, 1);
        var once = new PreviewCameraProcessSettings(5, 2, roi,
            VisionPixelFormat.Bgr24, null, whiteBalance);
        var continuous = new PreviewCameraProcessSettings(6, 3, roi,
            VisionPixelFormat.Bgr24, null, whiteBalance);
        var previewScenario = new VirtualCameraPreviewScenario(new[] { image }, once,
            continuous);
        await using var provider = new VirtualCameraProvider(new[] { Scenario(image,
            previewScenario) }, clock);
        var opened = await provider.OpenAsync("virtual.preview");
        Assert.True(opened.Succeeded, opened.ReasonCode);
        await using var device = opened.Device!;
        var preview = Assert.IsAssignableFrom<ICameraPreviewDevice>(device);
        Assert.True(preview.PreviewCapabilities.AutomaticExposure);
        Assert.True(preview.PreviewCapabilities.AutomaticGain);
        Assert.True(preview.PreviewCapabilities.AutomaticWhiteBalance);
        await ApplyPreviewBaselineAsync(device, image);

        var requested = new PreviewTuningConfiguration(
            new PreviewCameraProcessSettings(2, 1, roi, VisionPixelFormat.Bgr24,
                null, whiteBalance), PreviewAutomaticControlMode.Once);
        var started = await preview.StartPreviewAsync(Guid.Parse(
            "13300000-0000-0000-0000-000000000003"), requested);
        Assert.True(started.Succeeded, started.ReasonCode);
        Assert.Equal(5d, started.Configuration!.ProcessSettings.ExposureTimeUs);
        Assert.Equal(PreviewAutomaticControlMode.Once, started.Configuration.ExposureMode);

        var frozen = await preview.FreezeFixedPreviewConfigurationAsync();
        Assert.True(frozen.Succeeded, frozen.ReasonCode);
        Assert.Equal(PreviewAutomaticControlMode.Off, frozen.Configuration!.ExposureMode);
        Assert.Equal(PreviewAutomaticControlMode.Off, frozen.Configuration.GainMode);
        Assert.Equal(PreviewAutomaticControlMode.Off, frozen.Configuration.WhiteBalanceMode);
        Assert.Equal(5d, frozen.Configuration.ProcessSettings.ExposureTimeUs);

        var changed = await preview.ApplyPreviewTuningAsync(new PreviewTuningConfiguration(
            new PreviewCameraProcessSettings(2, 1, roi, VisionPixelFormat.Bgr24,
                null, whiteBalance), PreviewAutomaticControlMode.Continuous));
        Assert.True(changed.Succeeded, changed.ReasonCode);
        Assert.Equal(6d, changed.Configuration!.ProcessSettings.ExposureTimeUs);
        Assert.Equal(PreviewAutomaticControlMode.Continuous, changed.Configuration.ExposureMode);
        await preview.StopPreviewAsync();
    }

    [Fact]
    public async Task V133_V03_DefaultScenarioRetainsHashAndRejectsPreview()
    {
        using var clock = new VirtualCameraClock(InitialUtc);
        var image = VirtualCameraImage.CreateSynthetic("legacy", 2, 2,
            VisionPixelFormat.Mono8, null, 44);
        var first = Scenario(image, null);
        var second = Scenario(image, null);
        Assert.Equal(first.ContentHash, second.ContentHash);

        await using var provider = new VirtualCameraProvider(new[] { first }, clock);
        var opened = await provider.OpenAsync("virtual.preview");
        Assert.True(opened.Succeeded, opened.ReasonCode);
        await using var device = opened.Device!;
        var preview = Assert.IsAssignableFrom<ICameraPreviewDevice>(device);
        Assert.False(preview.PreviewCapabilities.Supported);
        var result = await preview.StartPreviewAsync(Guid.Parse(
            "13300000-0000-0000-0000-000000000004"),
            new PreviewTuningConfiguration(new PreviewCameraProcessSettings(10, 1,
                new RegionOfInterest(0, 0, 2, 2), VisionPixelFormat.Mono8, null)));
        Assert.False(result.Succeeded);
        Assert.Equal("VirtualCameraPreviewUnsupported", result.ReasonCode);
    }

    [Fact]
    public void V133_V04_PreviewScenarioRejectsFasterThanTenFramesPerSecond()
    {
        var image = VirtualCameraImage.CreateSynthetic("bounded", 2, 2,
            VisionPixelFormat.Mono8, null, 45);
        Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualCameraPreviewScenario(
            new[] { image }, frameInterval: TimeSpan.FromMilliseconds(99)));
    }

    [Fact]
    public void V133_V05_PreviewSourceImagesRespectAggregateMemoryBudget()
    {
        // Five 16 MiB images exercise the aggregate guard without creating the
        // former 64 x 16 MiB worst-case allocation.
        var images = Enumerable.Range(0, 5)
            .Select(index => VirtualCameraImage.CreateSynthetic("budget" + index,
                4096, 4096, VisionPixelFormat.Mono8, null, (uint)(100 + index)))
            .ToArray();

        Assert.Throws<ArgumentException>(() => new VirtualCameraPreviewScenario(images));
    }

    private static VirtualCameraScenario Scenario(VirtualCameraImage image,
        VirtualCameraPreviewScenario? preview)
    {
        var capabilities = new CameraCapabilities(
            new[] { ProductionAcquisitionMode.SoftwareTrigger },
            new[] { image.PixelFormat },
            image.PixelFormat == VisionPixelFormat.Mono16
                ? new[] { image.ValidBits!.Value } : Array.Empty<int>(),
            new(1, 100, 1, CameraQuantizationMode.Exact),
            new(0, 10, 1, CameraQuantizationMode.Exact),
            new(0, 10, 1, CameraQuantizationMode.Exact),
            new(2, 2, new(0, 1, 1), new(0, 1, 1), new(1, 2, 1), new(1, 2, 1)));
        if (preview is null)
            return new VirtualCameraScenario("preview.fixture", "1", 42,
                "virtual.preview", capabilities, new[] { image },
                Array.Empty<VirtualCameraAcquisitionPlan>());
        return VirtualCameraScenario.WithPreview("preview.fixture", "1", 42,
            "virtual.preview", capabilities, new[] { image },
            Array.Empty<VirtualCameraAcquisitionPlan>(), preview);
    }

    private static async Task ApplyPreviewBaselineAsync(ICameraDevice device, VirtualCameraImage image)
    {
        var result = await device.ApplyConfigurationAsync(new RequestedCameraConfiguration(
            ProductionAcquisitionMode.SoftwareTrigger, 10, 1,
            new RegionOfInterest(0, 0, image.Width, image.Height), image.PixelFormat,
            image.ValidBits, 20, 0, null));
        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.Equal(CameraConfigurationState.Applied, device.GetHealthSnapshot().Configuration);
    }
}
