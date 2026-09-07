using System.Reflection;
using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class FrameMetadataTests
{
    [Fact]
    public void V111_M01_FrameMetadataExposesOnlyTypedVendorNeutralFacts()
    {
        var correlation = Correlation(ExecutionKind.Production);
        var configuration = MonoConfiguration(640, 480);
        var metadata = new FrameMetadata(correlation, "TopCamera", 640, 480, 672,
            VisionPixelFormat.Mono8, null, Utc(10), configuration);

        Assert.Equal(correlation, metadata.Correlation);
        Assert.Equal(correlation, metadata.ExecutionCorrelation);
        Assert.Equal("TopCamera", metadata.LogicalCameraRole);
        Assert.Equal(640, metadata.Width);
        Assert.Equal(480, metadata.Height);
        Assert.Equal(672, metadata.StrideBytes);
        Assert.Equal(640, metadata.ValidRowBytes);
        Assert.Equal(322_528L, metadata.RequiredBufferLength);
        Assert.Equal(322_560L, metadata.FullBufferLayoutLength);
        Assert.Equal(VisionPixelFormat.Mono8, metadata.PixelFormat);
        Assert.Null(metadata.ValidBits);
        Assert.Equal(Utc(10), metadata.HostCaptureUtc);
        Assert.Same(configuration, metadata.EffectiveCameraConfiguration);
        Assert.Null(metadata.GetType().GetProperty(nameof(FrameProvenance.ProviderId)));
        Assert.Null(metadata.GetType().GetProperty(nameof(FrameProvenance.StableDeviceIdentity)));
    }

    [Fact]
    public void V111_M02_EffectiveConfigurationCarriesEveryCommonProcessFieldWithoutDefaults()
    {
        var roi = new RegionOfInterest(10, 20, 640, 480);
        var whiteBalance = new WhiteBalanceRgb(1.125, 1.0, 0.875);
        var color = new EffectiveCameraConfiguration(ProductionAcquisitionMode.HardwareTrigger,
            2500.5, 6.25, roi, VisionPixelFormat.Bgr24, null, 250, 12.5, whiteBalance);

        Assert.Equal(ProductionAcquisitionMode.HardwareTrigger, color.ProductionAcquisitionMode);
        Assert.Equal(2500.5, color.ExposureTimeUs);
        Assert.Equal(6.25, color.GainDb);
        Assert.Same(roi, color.RegionOfInterest);
        Assert.Equal(VisionPixelFormat.Bgr24, color.PixelFormat);
        Assert.Null(color.ValidBits);
        Assert.Equal(250, color.AcquisitionTimeoutMs);
        Assert.Equal(12.5, color.TriggerDelayUs);
        Assert.Same(whiteBalance, color.WhiteBalanceRgb);
    }

    [Fact]
    public void V111_M03_FrameMetadataRequiresFormatSizeStrideAndUtcConsistency()
    {
        var correlation = Correlation(ExecutionKind.Manual);
        var mono16 = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            100, 0, new RegionOfInterest(0, 0, 320, 240), VisionPixelFormat.Mono16, 12,
            1000, 0, null);

        var metadata = new FrameMetadata(correlation, "TopCamera", 320, 240, 640,
            VisionPixelFormat.Mono16, 12, Utc(20), mono16);
        Assert.Equal(12, metadata.ValidBits);
        Assert.Equal(640, metadata.ValidRowBytes);
        Assert.Equal(153_600L, metadata.RequiredBufferLength);

        Assert.ThrowsAny<ArgumentException>(() => new FrameMetadata(correlation, "TopCamera", 320, 240,
            640, VisionPixelFormat.Mono16, 16, Utc(20), mono16));
        Assert.ThrowsAny<ArgumentException>(() => new FrameMetadata(correlation, "TopCamera", 320, 240,
            320, VisionPixelFormat.Mono16, 12, Utc(20), mono16));
        Assert.Throws<ArgumentException>(() => new FrameMetadata(correlation, "TopCamera", 320, 240,
            640, VisionPixelFormat.Mono16, 12, new DateTimeOffset(2026, 1, 1, 0, 0, 0,
                TimeSpan.FromHours(1)), mono16));
        Assert.Throws<ArgumentException>(() => new FrameMetadata(correlation, "TopCamera", 320, 240,
            640, VisionPixelFormat.Mono8, null, Utc(20), mono16));
    }

    [Fact]
    public void V111_M04_FormatSpecificBitsAndWhiteBalanceAreClosedAndFinite()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectiveCameraConfiguration(
            ProductionAcquisitionMode.HardwareTrigger, 0, 0,
            new RegionOfInterest(0, 0, 10, 10), VisionPixelFormat.Mono8, null, 100, 0, null));
        Assert.Throws<ArgumentException>(() => new EffectiveCameraConfiguration(
            ProductionAcquisitionMode.HardwareTrigger, 1, 0,
            new RegionOfInterest(0, 0, 10, 10), VisionPixelFormat.Mono8, null, 100, 0,
            new WhiteBalanceRgb(1, 2, 3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectiveCameraConfiguration(
            ProductionAcquisitionMode.HardwareTrigger, double.NaN, 0,
            new RegionOfInterest(0, 0, 10, 10), VisionPixelFormat.Mono16, 12, 100, 0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectiveCameraConfiguration(
            ProductionAcquisitionMode.HardwareTrigger, 1, double.PositiveInfinity,
            new RegionOfInterest(0, 0, 10, 10), VisionPixelFormat.Mono16, 12, 100, 0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectiveCameraConfiguration(
            ProductionAcquisitionMode.HardwareTrigger, 1, 0,
            new RegionOfInterest(0, 0, 10, 10), VisionPixelFormat.Mono16, null, 100, 0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WhiteBalanceRgb(double.PositiveInfinity, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WhiteBalanceRgb(1, 1, 1_000.1));
        Assert.Throws<ArgumentException>(() => new EffectiveCameraConfiguration(
            ProductionAcquisitionMode.HardwareTrigger, 1, 0,
            new RegionOfInterest(0, 0, 10, 10), VisionPixelFormat.Bgr24, 12, 100, 0,
            new WhiteBalanceRgb(1, 2, 3)));
    }

    [Fact]
    public void V111_M05_ProvenancePreservesCorrelationAndExplicitlyKeepsUnavailableDeviceFactsAbsent()
    {
        var correlation = Correlation(ExecutionKind.Qualification);
        var milestones = Milestones();
        var provenance = new FrameProvenance(correlation, "vendor-a", "provider-1.2",
            "adapter-a", "adapter-3.0", "vendor-sdk", "sdk-7.1", null, "device-001",
            "Model-X", null, "VendorMono12Packed", "mono16-right-aligned-v1",
            false, true, null, null, milestones);

        Assert.Equal(correlation, provenance.Correlation);
        Assert.Equal("vendor-a", provenance.ProviderId);
        Assert.Equal("provider-1.2", provenance.ProviderVersion);
        Assert.Equal("adapter-a", provenance.AdapterId);
        Assert.Equal("adapter-3.0", provenance.AdapterVersion);
        Assert.Equal("vendor-sdk", provenance.SdkId);
        Assert.Equal("sdk-7.1", provenance.SdkVersion);
        Assert.Null(provenance.NativeRuntimeVersion);
        Assert.Equal("device-001", provenance.StableDeviceIdentity);
        Assert.Equal("Model-X", provenance.ReportedModel);
        Assert.Null(provenance.FirmwareVersion);
        Assert.Null(provenance.DeviceTimestamp);
        Assert.Null(provenance.FrameCounter);
        Assert.Same(milestones, provenance.Milestones);
    }

    [Fact]
    public void V111_M06_DeviceClockAndCounterAreOptionalButNeverPartiallyInvented()
    {
        var deviceTimestamp = new DeviceTimestamp(1234, null, "device-ticks", "camera-clock",
            1_000_000, DeviceClockSynchronization.Unknown);
        var provenance = new FrameProvenance(Correlation(ExecutionKind.Production), "provider", "1",
            "adapter", "1", "sdk", "1", "runtime-1", "device", null, "firmware-1", "Mono8",
            "passthrough-v1", false, false, deviceTimestamp, 42, Milestones());

        Assert.Equal(1234, provenance.DeviceTimestamp!.Value);
        Assert.Equal("device-ticks", provenance.DeviceTimestamp.Unit);
        Assert.Equal("camera-clock", provenance.DeviceTimestamp.ClockDomain);
        Assert.Equal((ulong)42, provenance.FrameCounter);
        Assert.False(provenance.NormalizationAllocated);
        Assert.False(provenance.NormalizationTransformed);
        Assert.Throws<ArgumentException>(() => new DeviceTimestamp(1, null, null, "camera-clock",
            null, DeviceClockSynchronization.Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceTimestamp(1, 0, "ticks",
            "camera-clock", null, DeviceClockSynchronization.Unknown));
        Assert.Throws<ArgumentException>(() => new DeviceTimestamp(1, 1, "bad\uD800",
            "camera-clock", null, DeviceClockSynchronization.Unknown));
    }

    [Fact]
    public void V111_M09_PoolCopyEvidenceIsTypedBoundedAndPreservesUpstreamProvenance()
    {
        var correlation = Correlation(ExecutionKind.Production);
        var milestones = Milestones();
        var deviceTimestamp = new DeviceTimestamp(1234, null, "device-ticks", "camera-clock",
            1_000_000, DeviceClockSynchronization.Unknown);
        var details = new string('x', 2048);
        var original = new FrameProvenance(correlation, "provider", "provider-1",
            "adapter", "adapter-1", "sdk", "sdk-1", "runtime-1", "device", "model",
            "firmware", "Mono8", details, true, false, deviceTimestamp, 42, milestones);

        Assert.Null(original.PoolCopyEvidence);
        var sameStride = AddPoolCopyEvidence(original, 672, 672);
        Assert.NotSame(original, sameStride);
        Assert.Equal(672, sameStride.PoolCopyEvidence!.SourceStrideBytes);
        Assert.Equal(672, sameStride.PoolCopyEvidence.DestinationStrideBytes);
        Assert.True(sameStride.PoolCopyEvidence.UsedPreallocatedPixelBuffer);
        Assert.False(sameStride.PoolCopyEvidence.StrideChanged);
        Assert.False(sameStride.PoolCopyEvidence.InputNormalizationTransformed);
        Assert.True(sameStride.PoolCopyEvidence.IdentityPixelCopy);
        Assert.True(sameStride.PoolCopyEvidence.PaddingZeroed);
        Assert.False(sameStride.NormalizationTransformed);

        var changedStride = AddPoolCopyEvidence(original, 640, 672);
        Assert.True(changedStride.PoolCopyEvidence!.StrideChanged);
        Assert.False(changedStride.PoolCopyEvidence.InputNormalizationTransformed);
        Assert.True(changedStride.NormalizationTransformed);
        Assert.True(changedStride.NormalizationAllocated);
        Assert.Equal(details, changedStride.NormalizationDetails);
        Assert.Same(correlation, changedStride.Correlation);
        Assert.Same(deviceTimestamp, changedStride.DeviceTimestamp);
        Assert.Same(milestones, changedStride.Milestones);
        Assert.Equal(original.ProviderId, changedStride.ProviderId);
        Assert.Equal(original.ProviderVersion, changedStride.ProviderVersion);
        Assert.Equal(original.AdapterId, changedStride.AdapterId);
        Assert.Equal(original.SdkId, changedStride.SdkId);
        Assert.Equal(original.NativeRuntimeVersion, changedStride.NativeRuntimeVersion);
        Assert.Equal(original.StableDeviceIdentity, changedStride.StableDeviceIdentity);
        Assert.Equal(original.ReportedModel, changedStride.ReportedModel);
        Assert.Equal(original.FirmwareVersion, changedStride.FirmwareVersion);
        Assert.Equal(original.NativePixelFormatDescription, changedStride.NativePixelFormatDescription);
        Assert.Null(original.PoolCopyEvidence);
        Assert.False(original.NormalizationTransformed);

        var upstreamTransformed = new FrameProvenance(correlation, "provider", "provider-1",
            "adapter", "adapter-1", "sdk", "sdk-1", "runtime-1", "device", "model",
            "firmware", "Mono8", details, false, true, deviceTimestamp, 42, milestones);
        var preservedInputEvidence = AddPoolCopyEvidence(upstreamTransformed, 672, 672);
        Assert.False(preservedInputEvidence.PoolCopyEvidence!.StrideChanged);
        Assert.True(preservedInputEvidence.PoolCopyEvidence.InputNormalizationTransformed);
        Assert.True(preservedInputEvidence.NormalizationTransformed);
    }

    [Fact]
    public void V111_M07_MonotonicMilestonesAreOrderedWhileUtcRemainsAnObservation()
    {
        var trigger = new FrameTimePoint(Utc(100), 10);
        var native = new FrameTimePoint(Utc(99), 20);
        var ready = new FrameTimePoint(Utc(101), 30);
        var milestones = new FrameAcquisitionMilestones(1_000_000, trigger, null, native, ready);

        Assert.Equal(1_000_000, milestones.MonotonicFrequency);
        Assert.Equal(10, milestones.TriggerAccepted!.MonotonicTimestamp);
        Assert.Equal(Utc(99), milestones.NativeFrameReceived!.HostObservedAtUtc);
        Assert.Equal(30, milestones.NormalizedFrameReady!.MonotonicTimestamp);
        Assert.Throws<ArgumentException>(() => new FrameAcquisitionMilestones(1_000_000,
            ready, null, native, null));
    }

    [Fact]
    public void V111_M08_IdentityAndTextBoundsRejectUnsafeOrUnboundedValues()
    {
        Assert.ThrowsAny<ArgumentException>(() => new RegionOfInterest(0, 0, 0, 10));
        Assert.Throws<ArgumentException>(() => new FrameMetadata(Correlation(ExecutionKind.Production),
            "camera/name", 10, 10, 10, VisionPixelFormat.Mono8, null, Utc(1),
            MonoConfiguration(10, 10)));
        Assert.Throws<ArgumentException>(() => new FrameProvenance(Correlation(ExecutionKind.Production),
            "provider", "1", "adapter", "1", "sdk", "1", null, "device", null, null,
            "Mono8", new string('x', 2049), false, false, null, null, Milestones()));
        Assert.Throws<ArgumentException>(() => new FrameProvenance(Correlation(ExecutionKind.Production),
            "provider", "1", "adapter", "1", "sdk", "1", null, "device\u0001", null, null,
            "Mono8", "passthrough", false, false, null, null, Milestones()));
    }

    private static EffectiveCameraConfiguration MonoConfiguration(int width, int height) =>
        new(ProductionAcquisitionMode.HardwareTrigger, 500, 1.5,
            new RegionOfInterest(0, 0, width, height), VisionPixelFormat.Mono8,
            null, 500, 0, null);

    private static FrameAcquisitionMilestones Milestones() =>
        new(1_000_000, new FrameTimePoint(Utc(1), 10),
            new FrameTimePoint(Utc(2), 12), new FrameTimePoint(Utc(3), 14),
            new FrameTimePoint(Utc(4), 16));

    private static ExecutionCorrelationId Correlation(ExecutionKind kind) =>
        new(kind, Guid.Parse(kind switch
        {
            ExecutionKind.Production => "10000000-0000-0000-0000-000000000001",
            ExecutionKind.Manual => "10000000-0000-0000-0000-000000000002",
            _ => "10000000-0000-0000-0000-000000000003"
        }));

    private static FrameProvenance AddPoolCopyEvidence(FrameProvenance provenance,
        int sourceStrideBytes, int destinationStrideBytes)
    {
        var method = typeof(FrameProvenance).GetMethod("WithPoolCopyEvidence",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<FrameProvenance>(method!.Invoke(provenance,
            new object[] { sourceStrideBytes, destinationStrideBytes }));
    }

    private static DateTimeOffset Utc(int second) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(second);
}
