using System.Collections;
using System.Reflection;
using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Cameras.Virtual.Tests;

public sealed class CameraContractTests
{
    [Fact]
    public void V116_C01_IdentitiesAndFrameRequestsAreBoundedAndValidated()
    {
        var provider = new CameraProviderIdentity("virtual", "1", "sharpinspect.virtual", "1");
        var descriptor = new CameraDeviceDescriptor(provider, "cam:01", "前置相机", "型号-1");
        var correlation = new ExecutionCorrelationId(ExecutionKind.Production, Guid.NewGuid());
        var request = new FrameAcquisitionRequest(correlation, "front_camera");

        Assert.Same(correlation, request.Correlation);
        Assert.Equal("front_camera", request.LogicalCameraRole);
        Assert.Equal(new[] { "Correlation", "LogicalCameraRole" },
            typeof(FrameAcquisitionRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name).OrderBy(name => name));
        Assert.Equal("cam:01", descriptor.StableDeviceIdentity);

        Assert.Throws<ArgumentException>(() => new CameraProviderIdentity("", "1", "pkg", "1"));
        Assert.Throws<ArgumentException>(() => new CameraProviderIdentity(
            new string('x', 129), "1", "pkg", "1"));
        Assert.Throws<ArgumentException>(() => new CameraProviderIdentity("bad\n", "1", "pkg", "1"));
        Assert.Throws<ArgumentException>(() => new CameraProviderIdentity("bad id", "1", "pkg", "1"));
        Assert.Throws<ArgumentException>(() => new CameraDeviceDescriptor(
            provider, "", "Front camera"));
        Assert.Throws<ArgumentException>(() => new CameraDeviceDescriptor(
            provider, "cam-02", "\n"));
        Assert.Throws<ArgumentException>(() => new CameraDeviceDescriptor(
            provider, "cam-02", new string('x', 257)));
        Assert.Throws<ArgumentException>(() => new FrameAcquisitionRequest(
            new ExecutionCorrelationId((ExecutionKind)99, Guid.NewGuid()), "front_camera"));
        Assert.Throws<ArgumentException>(() => new FrameAcquisitionRequest(
            new ExecutionCorrelationId(ExecutionKind.Production, Guid.Empty), "front_camera"));
        Assert.Throws<ArgumentException>(() => new FrameAcquisitionRequest(correlation, "front camera"));
    }

    [Fact]
    public void V116_C02_DiscoveryAndResultCollectionsAreDefensiveAndClosed()
    {
        var provider = new CameraProviderIdentity("virtual", "1", "sharpinspect.virtual", "1");
        var first = new CameraDeviceDescriptor(provider, "cam-01", "Front camera");
        var second = new CameraDeviceDescriptor(provider, "cam-02", "Rear camera");
        var devices = new List<CameraDeviceDescriptor> { first };
        var discovery = CameraDiscoveryResult.Success(devices);

        devices.Add(second);
        Assert.Single(discovery.Devices);
        Assert.Equal("cam-01", discovery.Devices[0].StableDeviceIdentity);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<CameraDeviceDescriptor>)discovery.Devices).Add(second));
        Assert.Throws<ArgumentException>(() => new CameraDiscoveryResult(false,
            "CameraDiscoveryFailed", new[] { first }));
        Assert.Throws<ArgumentException>(() => new CameraDiscoveryResult(true,
            "CameraDiscoverySucceeded", new[] { first, first }));
        var tooMany = Enumerable.Range(0, 65).Select(index =>
            new CameraDeviceDescriptor(provider, "cam-" + index, "Camera " + index));
        Assert.Throws<ArgumentException>(() => new CameraDiscoveryResult(true,
            "CameraDiscoverySucceeded", tooMany));
        Assert.Throws<ArgumentException>(() => new CameraOpenResult(true, "CameraOpenSucceeded"));
        Assert.Throws<ArgumentException>(() => CameraOperationResult.Failure("bad reason"));

        var failure = new CameraAcquisitionFailure(CameraAcquisitionFailureKind.DeviceFault,
            "CameraDeviceFault");
        var failureResult = FrameAcquisitionResult.FailureResult(failure);
        Assert.False(failureResult.Succeeded);
        Assert.Null(failureResult.Lease);
        Assert.Same(failure, failureResult.Failure);
        Assert.Throws<ArgumentException>(() => new FrameAcquisitionResult(null, null));
        Assert.Throws<ArgumentException>(() => new FrameAcquisitionResult(
            new TestLease(), failure));
    }

    [Fact]
    public void V116_C03_HealthSnapshotKeepsAxesIndependentButRejectsImpossibleCombinations()
    {
        var observed = new FrameTimePoint(DateTimeOffset.UtcNow, 42);
        var healthy = new CameraHealthSnapshot(CameraProviderAvailability.Available,
            CameraConnectionState.Open, CameraConfigurationState.Applied,
            CameraAcquisitionState.WaitingForFrame, observed);
        Assert.Equal(CameraProviderAvailability.Available, healthy.ProviderAvailability);
        Assert.Equal(CameraConnectionState.Open, healthy.Connection);
        Assert.Equal(CameraConfigurationState.Applied, healthy.Configuration);
        Assert.Equal(CameraAcquisitionState.WaitingForFrame, healthy.Acquisition);
        Assert.Same(observed, healthy.ObservedAt);

        var fault = new CameraFault(CameraFaultClassification.ConnectionLost,
            "CameraConnectionLost", "provider-timeout");
        Assert.Equal(CameraFaultClassification.ConnectionLost, fault.Classification);
        Assert.Equal("CameraConnectionLost", fault.ReasonCode);

        Assert.Throws<ArgumentException>(() => new CameraHealthSnapshot(
            CameraProviderAvailability.Available, CameraConnectionState.Disconnected,
            CameraConfigurationState.Applied, CameraAcquisitionState.Stopped, observed));
        Assert.Throws<ArgumentException>(() => new CameraHealthSnapshot(
            CameraProviderAvailability.Available, CameraConnectionState.Disconnected,
            CameraConfigurationState.Unknown, CameraAcquisitionState.Armed, observed));
        Assert.Throws<ArgumentException>(() => new CameraHealthSnapshot(
            CameraProviderAvailability.Available, CameraConnectionState.Open,
            CameraConfigurationState.Applying, CameraAcquisitionState.Armed, observed));
        Assert.Throws<ArgumentException>(() => new CameraHealthSnapshot(
            CameraProviderAvailability.DependencyMissing, CameraConnectionState.Open,
            CameraConfigurationState.Applied, CameraAcquisitionState.Previewing, observed));
    }

    [Fact]
    public void V116_C04_CapabilityCollectionsAreDefensiveAndContentHashBindsAllSettings()
    {
        var modes = new List<ProductionAcquisitionMode>
        {
            ProductionAcquisitionMode.HardwareTrigger,
            ProductionAcquisitionMode.SoftwareTrigger
        };
        var formats = new List<VisionPixelFormat>
        {
            VisionPixelFormat.Bgr24,
            VisionPixelFormat.Mono16,
            VisionPixelFormat.Mono8
        };
        var bits = new List<int> { 16, 10, 12 };
        var capabilities = CreateCapabilities(modes, formats, bits);
        var equivalent = CreateCapabilities(
            new[] { ProductionAcquisitionMode.SoftwareTrigger,
                ProductionAcquisitionMode.HardwareTrigger },
            new[] { VisionPixelFormat.Mono8, VisionPixelFormat.Bgr24,
                VisionPixelFormat.Mono16 }, new[] { 10, 12, 16 });

        modes.Clear();
        formats.Clear();
        bits.Clear();
        Assert.Equal(new[] { ProductionAcquisitionMode.SoftwareTrigger,
            ProductionAcquisitionMode.HardwareTrigger }, capabilities.AcquisitionModes);
        Assert.Equal(new[] { VisionPixelFormat.Mono8, VisionPixelFormat.Mono16,
            VisionPixelFormat.Bgr24 }, capabilities.PixelFormats);
        Assert.Equal(new[] { 10, 12, 16 }, capabilities.Mono16ValidBits);
        Assert.Equal(equivalent.ContentHash, capabilities.ContentHash);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ProductionAcquisitionMode>)capabilities.AcquisitionModes)
                .Add(ProductionAcquisitionMode.SoftwareTrigger));

        var readOnlyChanged = CreateCapabilities(exposure: new CameraDoubleCapability(
            10, 100, 10, CameraQuantizationMode.Exact, readable: false));
        var toleranceChanged = CreateCapabilities(exposure: new CameraDoubleCapability(
            10, 100, 10, CameraQuantizationMode.Exact, quantizationTolerance: 0.1));
        Assert.NotEqual(capabilities.ContentHash, readOnlyChanged.ContentHash);
        Assert.NotEqual(capabilities.ContentHash, toleranceChanged.ContentHash);

        Assert.Throws<ArgumentException>(() => CreateCapabilities(
            formats: new[] { VisionPixelFormat.Mono8 },
            bits: new[] { 10 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CameraDoubleCapability(
            1, 10, 1, (CameraQuantizationMode)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CameraAcquisitionFailure(
            (CameraAcquisitionFailureKind)99, "CameraFailure"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CameraFault(
            (CameraFaultClassification)99, "CameraFault"));
        Assert.Throws<ArgumentException>(() => new CameraCapabilities(
            new[] { (ProductionAcquisitionMode)99 },
            new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
            new CameraDoubleCapability(1, 10, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0, 10, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0, 10, 1, CameraQuantizationMode.Exact),
            CreateRoi()));
        Assert.Throws<ArgumentException>(() => new CameraCapabilities(
            new[] { ProductionAcquisitionMode.SoftwareTrigger },
            new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
            new CameraDoubleCapability(1, 10, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0, 10, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0, 10, 1, CameraQuantizationMode.Exact),
            CreateRoi(), CreateWhiteBalance()));
    }

    [Fact]
    public void V116_C05_ExactConfigurationProducesCompleteEffectiveValues()
    {
        var capabilities = CreateCapabilities();
        var requested = Request(pixelFormat: VisionPixelFormat.Mono8,
            exposureTimeUs: 20, gainDb: 1, triggerDelayUs: 10,
            roi: new RegionOfInterest(8, 4, 320, 240));

        var result = capabilities.ValidateConfiguration(requested);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Effective);
        Assert.Empty(result.Differences);
        Assert.Equal(requested.ExposureTimeUs, result.Effective!.ExposureTimeUs);
        Assert.Equal(requested.GainDb, result.Effective.GainDb);
        Assert.Equal(requested.TriggerDelayUs, result.Effective.TriggerDelayUs);
        Assert.Equal(requested.RegionOfInterest, result.Effective.RegionOfInterest);
        Assert.Equal(requested.PixelFormat, result.Effective.PixelFormat);
        Assert.Null(result.Effective.ValidBits);
        Assert.Null(result.Effective.WhiteBalanceRgb);
    }

    [Fact]
    public void V116_C06_ExplicitQuantizationReportsOnlyTypedDifferences()
    {
        var capabilities = CreateCapabilities(exposure: new CameraDoubleCapability(
            10, 100, 1, CameraQuantizationMode.Nearest, quantizationTolerance: 0.5));
        var requested = Request(exposureTimeUs: 10.4);
        var result = capabilities.ValidateConfiguration(requested);

        Assert.True(result.Succeeded);
        var difference = Assert.Single(result.Differences);
        Assert.Equal(CameraNumericSetting.ExposureTimeUs, difference.Setting);
        Assert.Equal(10.4, difference.Requested, 10);
        Assert.Equal(10, difference.Effective, 10);
        Assert.Equal(10, result.Effective!.ExposureTimeUs);
        Assert.Throws<ArgumentException>(() => new CameraConfigurationResult(false,
            "CameraConfigurationFailed", null, new[] { difference }));
        Assert.Throws<ArgumentException>(() => new CameraConfigurationResult(true,
            "CameraConfigurationApplied", result.Effective, new[] { difference, difference,
                new CameraConfigurationDifference(CameraNumericSetting.GainDb, 1, 2),
                new CameraConfigurationDifference(CameraNumericSetting.TriggerDelayUs, 1, 2),
                new CameraConfigurationDifference(CameraNumericSetting.WhiteBalanceRed, 1, 2),
                new CameraConfigurationDifference(CameraNumericSetting.WhiteBalanceGreen, 1, 2),
                new CameraConfigurationDifference(CameraNumericSetting.WhiteBalanceBlue, 1, 2) }));
    }

    [Fact]
    public void V116_C07_QuantizationOutsideToleranceAndClampingAreRejected()
    {
        var outsideTolerance = CreateCapabilities(exposure: new CameraDoubleCapability(
            10, 100, 1, CameraQuantizationMode.Nearest, quantizationTolerance: 0.25));
        var rejected = outsideTolerance.ValidateConfiguration(Request(exposureTimeUs: 10.6));
        Assert.False(rejected.Succeeded);
        Assert.Null(rejected.Effective);
        Assert.Empty(rejected.Differences);
        Assert.Equal("CameraConfigurationExposureTimeUsQuantizationToleranceExceeded",
            rejected.ReasonCode);

        var wouldClamp = CreateCapabilities(exposure: new CameraDoubleCapability(
            0.1, 1, 2, CameraQuantizationMode.Ceiling));
        var noClamp = wouldClamp.ValidateConfiguration(Request(exposureTimeUs: 0.5));
        Assert.False(noClamp.Succeeded);
        Assert.Null(noClamp.Effective);
        Assert.Equal("CameraConfigurationExposureTimeUsQuantizationOutOfRange", noClamp.ReasonCode);
    }

    [Fact]
    public void V116_C08_UnsupportedReadonlyRoiMono16AndWhiteBalanceAreRejected()
    {
        var readonlyGain = CreateCapabilities(gain: new CameraDoubleCapability(
            -10, 10, 1, CameraQuantizationMode.Exact, readable: true, writable: false));
        var readonlyResult = readonlyGain.ValidateConfiguration(Request(gainDb: 1));
        Assert.False(readonlyResult.Succeeded);
        Assert.Equal("CameraConfigurationGainDbNotReadWrite", readonlyResult.ReasonCode);

        var badRoi = CreateCapabilities().ValidateConfiguration(Request(
            roi: new RegionOfInterest(3, 4, 320, 240)));
        Assert.False(badRoi.Succeeded);
        Assert.Equal("CameraConfigurationRoiOffsetXUnsupported", badRoi.ReasonCode);

        var outsideSensor = CreateCapabilities().ValidateConfiguration(Request(
            roi: new RegionOfInterest(636, 4, 8, 240)));
        Assert.False(outsideSensor.Succeeded);
        Assert.Equal("CameraConfigurationRoiBoundsInvalid", outsideSensor.ReasonCode);

        var mono16Bits = CreateCapabilities(bits: new[] { 10, 12 });
        var unsupportedBits = mono16Bits.ValidateConfiguration(Request(
            pixelFormat: VisionPixelFormat.Mono16, validBits: 16));
        Assert.False(unsupportedBits.Succeeded);
        Assert.Equal("CameraConfigurationMono16ValidBitsUnsupported", unsupportedBits.ReasonCode);

        var noWhiteBalance = CreateCapabilities(
            formats: new[] { VisionPixelFormat.Bgr24 }, bits: Array.Empty<int>(),
            includeWhiteBalance: false);
        var unsupportedWhiteBalance = noWhiteBalance.ValidateConfiguration(Request(
            pixelFormat: VisionPixelFormat.Bgr24, whiteBalance: new WhiteBalanceRgb(1, 1, 1)));
        Assert.False(unsupportedWhiteBalance.Succeeded);
        Assert.Equal("CameraConfigurationWhiteBalanceUnsupported", unsupportedWhiteBalance.ReasonCode);

        Assert.Throws<ArgumentException>(() => Request(pixelFormat: VisionPixelFormat.Mono8,
            validBits: 10));
    }

    [Fact]
    public void V116_C09_BgrWhiteBalanceIsConditionalAndCanBeEffective()
    {
        var capabilities = CreateCapabilities();
        var requested = Request(pixelFormat: VisionPixelFormat.Bgr24,
            whiteBalance: new WhiteBalanceRgb(1, 1.5, 2));
        var result = capabilities.ValidateConfiguration(requested);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Effective!.WhiteBalanceRgb);
        Assert.Equal(1.5, result.Effective.WhiteBalanceRgb!.Green);
        Assert.Equal(2, result.Effective.WhiteBalanceRgb.Blue);

        var noCapability = CreateCapabilities(includeWhiteBalance: false)
            .ValidateConfiguration(requested);
        Assert.False(noCapability.Succeeded);
        Assert.Equal("CameraConfigurationWhiteBalanceUnsupported", noCapability.ReasonCode);
    }

    [Fact]
    public void V116_C10_ClosedResultStatesNeverExposePartialObjects()
    {
        var effective = ToEffectiveForTest(Request());
        var difference = new CameraConfigurationDifference(CameraNumericSetting.GainDb, 1, 2);

        Assert.Throws<ArgumentException>(() => new CameraConfigurationResult(false,
            "CameraConfigurationFailed", effective));
        Assert.Throws<ArgumentException>(() => new CameraConfigurationResult(true,
            "CameraConfigurationApplied", effective, new[] { difference, difference }));
        Assert.Throws<ArgumentException>(() => new CameraOpenResult(false,
            "CameraOpenFailed", new TestDevice()));
        Assert.Null(CameraOpenResult.Failure("CameraOpenFailed").Device);
        Assert.False(CameraConfigurationResult.Failure("CameraConfigurationFailed").Succeeded);
        Assert.False(CameraOperationResult.Failure("CameraOperationFailed").Succeeded);
    }

    [Fact]
    public void V116_C11_CameraIdentityMatchesFrameProvenanceBoundaries()
    {
        var provider = new CameraProviderIdentity("provider", "1:2", "adapter", "2:1");
        var correlation = new ExecutionCorrelationId(ExecutionKind.Production, Guid.NewGuid());
        var modelAtUtf8Limit = new string('é', 64); // 128 UTF-8 bytes.
        var descriptor = new CameraDeviceDescriptor(provider, "device:01", "前置相机",
            modelAtUtf8Limit);

        Assert.Equal(modelAtUtf8Limit, descriptor.ReportedModel);
        Assert.Throws<ArgumentException>(() => new CameraProviderIdentity(
            "provider:01", "1", "adapter", "1"));
        Assert.Throws<ArgumentException>(() => new CameraProviderIdentity(
            new string('p', 65), "1", "adapter", "1"));
        Assert.Throws<ArgumentException>(() => new CameraProviderIdentity(
            "provider", "1", "adapter:01", "1"));
        Assert.Throws<ArgumentException>(() => new CameraDeviceDescriptor(
            provider, "device:01", "前置相机", new string('é', 65)));
        Assert.Throws<ArgumentException>(() => new FrameAcquisitionRequest(
            correlation, "front:camera"));
        Assert.Throws<ArgumentException>(() => new FrameAcquisitionRequest(
            correlation, new string('r', 65)));
    }

    [Fact]
    public void V116_C12_ExactGridUsesSmallUlpBoundAndHonorsExplicitTolerance()
    {
        Assert.Throws<ArgumentException>(() => new CameraDoubleCapability(
            1, 2, 5e-16, CameraQuantizationMode.Exact));

        var ordinaryDecimal = CreateCapabilities(exposure: new CameraDoubleCapability(
            0.1, 1, 0.1, CameraQuantizationMode.Exact));
        var ordinaryResult = ordinaryDecimal.ValidateConfiguration(Request(exposureTimeUs: 0.3));
        Assert.True(ordinaryResult.Succeeded);
        Assert.Equal(0.3, ordinaryResult.Effective!.ExposureTimeUs, 12);

        var positiveHalfStep = CreateCapabilities(exposure: new CameraDoubleCapability(
            1, 2, 1e-12, CameraQuantizationMode.Exact));
        var positiveRejected = positiveHalfStep.ValidateConfiguration(
            Request(exposureTimeUs: 1.5000000000005));
        Assert.False(positiveRejected.Succeeded);
        Assert.Equal("CameraConfigurationExposureTimeUsQuantizationRequired",
            positiveRejected.ReasonCode);

        var negativeHalfStep = CreateCapabilities(gain: new CameraDoubleCapability(
            -1, 1, 1e-12, CameraQuantizationMode.Exact));
        var negativeRejected = negativeHalfStep.ValidateConfiguration(
            Request(gainDb: -0.4999999999995));
        Assert.False(negativeRejected.Succeeded);
        Assert.Equal("CameraConfigurationGainDbQuantizationRequired",
            negativeRejected.ReasonCode);

        var fineGrid = CreateCapabilities(exposure: new CameraDoubleCapability(
            1, 2, 1e-12, CameraQuantizationMode.Exact));
        var fineGridResult = fineGrid.ValidateConfiguration(Request(exposureTimeUs: 1.1));
        Assert.True(fineGridResult.Succeeded);

        var nearQuarterStep = CreateCapabilities(exposure: new CameraDoubleCapability(
            10, 100, 1, CameraQuantizationMode.Exact));
        var nearQuarterRejected = nearQuarterStep.ValidateConfiguration(
            Request(exposureTimeUs: 10.249999999));
        Assert.False(nearQuarterRejected.Succeeded);
        Assert.Equal("CameraConfigurationExposureTimeUsQuantizationRequired",
            nearQuarterRejected.ReasonCode);

        var explicitZeroTolerance = CreateCapabilities(exposure: new CameraDoubleCapability(
            10, 100, 1, CameraQuantizationMode.Nearest, quantizationTolerance: 0));
        var hiddenEpsilonRejected = explicitZeroTolerance.ValidateConfiguration(
            Request(exposureTimeUs: 10.1));
        Assert.False(hiddenEpsilonRejected.Succeeded);
        Assert.Equal("CameraConfigurationExposureTimeUsQuantizationToleranceExceeded",
            hiddenEpsilonRejected.ReasonCode);
    }

    private static CameraCapabilities CreateCapabilities(
        IEnumerable<ProductionAcquisitionMode>? modes = null,
        IEnumerable<VisionPixelFormat>? formats = null,
        IEnumerable<int>? bits = null,
        CameraDoubleCapability? exposure = null,
        CameraDoubleCapability? gain = null,
        CameraDoubleCapability? trigger = null,
        CameraRoiCapabilities? roi = null,
        bool includeWhiteBalance = true)
    {
        var formatArray = (formats ?? new[] { VisionPixelFormat.Mono8,
            VisionPixelFormat.Mono16, VisionPixelFormat.Bgr24 }).ToArray();
        var bitArray = bits ?? (formatArray.Contains(VisionPixelFormat.Mono16)
            ? new[] { 10, 12, 16 } : Array.Empty<int>());
        return new CameraCapabilities(
            modes ?? new[] { ProductionAcquisitionMode.SoftwareTrigger,
                ProductionAcquisitionMode.HardwareTrigger },
            formatArray,
            bitArray,
            exposure ?? new CameraDoubleCapability(10, 100, 10, CameraQuantizationMode.Exact),
            gain ?? new CameraDoubleCapability(-10, 10, 1, CameraQuantizationMode.Exact),
            trigger ?? new CameraDoubleCapability(0, 100, 5, CameraQuantizationMode.Exact),
            roi ?? CreateRoi(),
            includeWhiteBalance && formatArray.Contains(VisionPixelFormat.Bgr24)
                ? CreateWhiteBalance() : null);
    }

    private static CameraRoiCapabilities CreateRoi() => new(640, 480,
        new CameraIntCapability(0, 636, 4),
        new CameraIntCapability(0, 476, 4),
        new CameraIntCapability(4, 640, 4),
        new CameraIntCapability(4, 480, 4));

    private static CameraWhiteBalanceCapabilities CreateWhiteBalance() =>
        new(new CameraDoubleCapability(0.5, 2, 0.5, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0.5, 2, 0.5, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0.5, 2, 0.5, CameraQuantizationMode.Exact));

    private static RequestedCameraConfiguration Request(
        ProductionAcquisitionMode mode = ProductionAcquisitionMode.SoftwareTrigger,
        double exposureTimeUs = 10,
        double gainDb = 0,
        double triggerDelayUs = 0,
        RegionOfInterest? roi = null,
        VisionPixelFormat pixelFormat = VisionPixelFormat.Mono8,
        int? validBits = null,
        WhiteBalanceRgb? whiteBalance = null) => new(mode, exposureTimeUs, gainDb,
            roi ?? new RegionOfInterest(0, 0, 640, 480), pixelFormat, validBits, 100,
            triggerDelayUs, whiteBalance);

    private sealed class TestLease : IFrameBufferLease
    {
        public Guid LeaseId { get; } = Guid.NewGuid();
        public VisionFrame Frame => null!;
        public FrameProvenance Provenance => null!;
        public bool IsReturned => false;
        public void Dispose() { }
    }

    private sealed class TestDevice : ICameraDevice
    {
        private static readonly CameraProviderIdentity Provider =
            new("test", "1", "test", "1");
        public CameraDeviceDescriptor Descriptor { get; } =
            new(Provider, "test-device", "Test device");
        public CameraCapabilities Capabilities { get; } = CreateCapabilities();
        public CameraHealthSnapshot GetHealthSnapshot() => new(
            CameraProviderAvailability.Available, CameraConnectionState.Open,
            CameraConfigurationState.Unconfigured, CameraAcquisitionState.Stopped,
            new FrameTimePoint(DateTimeOffset.UtcNow, 1));
        public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
            RequestedCameraConfiguration requested, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Capabilities.ValidateConfiguration(requested));
        public ValueTask<CameraOperationResult> StartAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraOperationResult.Success());
        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(FrameAcquisitionResult.FailureResult(
                new CameraAcquisitionFailure(CameraAcquisitionFailureKind.NotStarted,
                    "CameraNotStarted")));
        public ValueTask<CameraOperationResult> StopAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraOperationResult.Success());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static EffectiveCameraConfiguration ToEffectiveForTest(
        RequestedCameraConfiguration request) => new(request.ProductionAcquisitionMode,
        request.ExposureTimeUs, request.GainDb, request.RegionOfInterest, request.PixelFormat,
        request.ValidBits, request.AcquisitionTimeoutMs, request.TriggerDelayUs,
        request.WhiteBalanceRgb);
}
