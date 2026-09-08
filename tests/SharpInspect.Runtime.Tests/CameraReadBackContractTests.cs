using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CameraReadBackContractTests
{
    [Fact]
    public void V117_C01_ExactReadBackMatchesCanonicalExpectation()
    {
        var capabilities = CreateCapabilities();
        var requested = Request(exposureTimeUs: 20, gainDb: 1, triggerDelayUs: 10,
            roi: new RegionOfInterest(8, 4, 320, 240));
        var expected = capabilities.ValidateConfiguration(requested);
        var reported = Reported(expected.Effective!, expected.Differences,
            "ProviderReadBackComplete");

        var validated = capabilities.ValidateReadBack(requested, reported);

        Assert.True(validated.Succeeded);
        Assert.Equal("CameraConfigurationApplied", validated.ReasonCode);
        Assert.Equal(expected.Effective, validated.Effective);
        Assert.Equal(expected.Differences, validated.Differences);
    }

    [Fact]
    public void V117_C02_QuantizedReadBackRequiresCompleteTypedDifferences()
    {
        var capabilities = CreateCapabilities(
            exposure: new CameraDoubleCapability(10, 100, 1,
                CameraQuantizationMode.Nearest, quantizationTolerance: 0.5),
            gain: new CameraDoubleCapability(-10, 10, 1,
                CameraQuantizationMode.Nearest, quantizationTolerance: 0.5),
            trigger: new CameraDoubleCapability(0, 100, 5,
                CameraQuantizationMode.Nearest, quantizationTolerance: 2.5));
        var requested = Request(exposureTimeUs: 10.4, gainDb: 0.4, triggerDelayUs: 1.4);
        var expected = capabilities.ValidateConfiguration(requested);
        Assert.True(expected.Succeeded);
        Assert.Equal(3, expected.Differences.Count);

        var reported = Reported(expected.Effective!, expected.Differences.Reverse(),
            "ProviderReadBackQuantized");
        var validated = capabilities.ValidateReadBack(requested, reported);

        Assert.True(validated.Succeeded);
        Assert.Equal(expected.Effective, validated.Effective);
        Assert.Equal(expected.Differences, validated.Differences);
    }

    [Fact]
    public void V117_C03_EveryEffectiveFieldDriftFailsWithoutHalfSuccess()
    {
        var capabilities = CreateCapabilities();
        var requested = Request();
        var expected = capabilities.ValidateConfiguration(requested);
        Assert.True(expected.Succeeded);
        var baseline = expected.Effective!;
        var variants = new[]
        {
            Rebuild(baseline, mode: ProductionAcquisitionMode.HardwareTrigger),
            Rebuild(baseline, exposure: 11),
            Rebuild(baseline, gain: 1),
            Rebuild(baseline, roi: new RegionOfInterest(4, 0, 640, 480)),
            Rebuild(baseline, pixelFormat: VisionPixelFormat.Bgr24),
            Rebuild(baseline, acquisitionTimeoutMs: 101),
            Rebuild(baseline, triggerDelayUs: 5)
        };

        foreach (var variant in variants)
        {
            var result = capabilities.ValidateReadBack(requested,
                Reported(variant, expected.Differences));
            Assert.False(result.Succeeded);
            Assert.Equal("CameraReadBackEffectiveMismatch", result.ReasonCode);
            Assert.Null(result.Effective);
            Assert.Empty(result.Differences);
        }

        var monoRequested = Request(pixelFormat: VisionPixelFormat.Mono16, validBits: 16);
        var monoExpected = capabilities.ValidateConfiguration(monoRequested);
        Assert.True(monoExpected.Succeeded);
        var validBitsVariant = Rebuild(monoExpected.Effective!, replaceValidBits: true,
            validBits: 12);
        var validBitsResult = capabilities.ValidateReadBack(monoRequested,
            Reported(validBitsVariant, monoExpected.Differences));
        Assert.False(validBitsResult.Succeeded);
        Assert.Equal("CameraReadBackEffectiveMismatch", validBitsResult.ReasonCode);

        var bgrRequested = Request(pixelFormat: VisionPixelFormat.Bgr24,
            whiteBalance: new WhiteBalanceRgb(1, 1, 1));
        var bgrExpected = capabilities.ValidateConfiguration(bgrRequested);
        Assert.True(bgrExpected.Succeeded);
        var whiteBalanceVariant = Rebuild(bgrExpected.Effective!, replaceWhiteBalance: true,
            whiteBalance: new WhiteBalanceRgb(1.5, 1, 1));
        var whiteBalanceResult = capabilities.ValidateReadBack(bgrRequested,
            Reported(whiteBalanceVariant, bgrExpected.Differences));
        Assert.False(whiteBalanceResult.Succeeded);
        Assert.Equal("CameraReadBackEffectiveMismatch", whiteBalanceResult.ReasonCode);
    }

    [Fact]
    public void V117_C04_DifferenceOrderIsIgnoredButMissingOrForgedValuesFail()
    {
        var capabilities = CreateCapabilities(
            exposure: new CameraDoubleCapability(10, 100, 1,
                CameraQuantizationMode.Nearest, quantizationTolerance: 0.5),
            gain: new CameraDoubleCapability(-10, 10, 1,
                CameraQuantizationMode.Nearest, quantizationTolerance: 0.5),
            trigger: new CameraDoubleCapability(0, 100, 5,
                CameraQuantizationMode.Nearest, quantizationTolerance: 2.5));
        var requested = Request(exposureTimeUs: 10.4, gainDb: 0.4, triggerDelayUs: 1.4);
        var expected = capabilities.ValidateConfiguration(requested);
        Assert.True(expected.Succeeded);

        var reordered = capabilities.ValidateReadBack(requested,
            Reported(expected.Effective!, expected.Differences.Reverse()));
        Assert.True(reordered.Succeeded);

        var missing = capabilities.ValidateReadBack(requested,
            Reported(expected.Effective!, expected.Differences.Take(2)));
        Assert.False(missing.Succeeded);
        Assert.Equal("CameraReadBackDifferencesMismatch", missing.ReasonCode);
        Assert.Null(missing.Effective);
        Assert.Empty(missing.Differences);

        var forgedDifferences = expected.Differences.Select(difference =>
            difference.Setting == CameraNumericSetting.GainDb
                ? new CameraConfigurationDifference(difference.Setting,
                    difference.Requested + 1, difference.Effective)
                : difference).ToArray();
        var forged = capabilities.ValidateReadBack(requested,
            Reported(expected.Effective!, forgedDifferences));
        Assert.False(forged.Succeeded);
        Assert.Equal("CameraReadBackDifferencesMismatch", forged.ReasonCode);
        Assert.Null(forged.Effective);
    }

    [Fact]
    public void V117_C05_ProviderFailurePreservesTypedReasonAndNoEffectiveValue()
    {
        var capabilities = CreateCapabilities();
        var requested = Request();
        var reported = CameraConfigurationResult.Failure("ProviderReadBackTimeout");

        var result = capabilities.ValidateReadBack(requested, reported);

        Assert.False(result.Succeeded);
        Assert.Equal("ProviderReadBackTimeout", result.ReasonCode);
        Assert.Null(result.Effective);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void V117_C06_UnknownCapabilityIsRejectedBeforeProviderSuccess()
    {
        var capabilities = CreateCapabilities(
            formats: new[] { VisionPixelFormat.Mono8 }, bits: Array.Empty<int>());
        var requested = Request(pixelFormat: VisionPixelFormat.Bgr24);
        var reported = Reported(ToEffective(requested), Array.Empty<CameraConfigurationDifference>());

        var result = capabilities.ValidateReadBack(requested, reported);

        Assert.False(result.Succeeded);
        Assert.Equal("CameraConfigurationPixelFormatUnsupported", result.ReasonCode);
        Assert.Null(result.Effective);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void V117_C07_RequestRoiOutsideCapabilityFailsEvenWithSuccessfulReport()
    {
        var capabilities = CreateCapabilities();
        var requested = Request(roi: new RegionOfInterest(636, 0, 8, 240));
        var reported = Reported(ToEffective(requested), Array.Empty<CameraConfigurationDifference>());

        var result = capabilities.ValidateReadBack(requested, reported);

        Assert.False(result.Succeeded);
        Assert.Equal("CameraConfigurationRoiBoundsInvalid", result.ReasonCode);
        Assert.Null(result.Effective);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void V117_C08_InvalidReadBackRoiIsRejectedAsEffectiveMismatch()
    {
        var capabilities = CreateCapabilities();
        var requested = Request();
        var expected = capabilities.ValidateConfiguration(requested);
        Assert.True(expected.Succeeded);
        var invalidReadBack = Rebuild(expected.Effective!,
            roi: new RegionOfInterest(639, 0, 320, 240));

        var result = capabilities.ValidateReadBack(requested,
            Reported(invalidReadBack, expected.Differences));

        Assert.False(result.Succeeded);
        Assert.Equal("CameraReadBackEffectiveMismatch", result.ReasonCode);
        Assert.Null(result.Effective);
        Assert.Empty(result.Differences);
    }

    private static CameraConfigurationResult Reported(EffectiveCameraConfiguration effective,
        IEnumerable<CameraConfigurationDifference> differences, string reasonCode = "ProviderReadBack") =>
        new(true, reasonCode, effective, differences);

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
        int acquisitionTimeoutMs = 100,
        WhiteBalanceRgb? whiteBalance = null) => new(mode, exposureTimeUs, gainDb,
            roi ?? new RegionOfInterest(0, 0, 640, 480), pixelFormat, validBits,
            acquisitionTimeoutMs, triggerDelayUs, whiteBalance);

    private static EffectiveCameraConfiguration ToEffective(
        RequestedCameraConfiguration request) => new(request.ProductionAcquisitionMode,
        request.ExposureTimeUs, request.GainDb, request.RegionOfInterest, request.PixelFormat,
        request.ValidBits, request.AcquisitionTimeoutMs, request.TriggerDelayUs,
        request.WhiteBalanceRgb);

    private static EffectiveCameraConfiguration Rebuild(
        EffectiveCameraConfiguration baseline,
        ProductionAcquisitionMode? mode = null,
        double? exposure = null,
        double? gain = null,
        RegionOfInterest? roi = null,
        VisionPixelFormat? pixelFormat = null,
        int? acquisitionTimeoutMs = null,
        double? triggerDelayUs = null,
        bool replaceValidBits = false,
        int? validBits = null,
        bool replaceWhiteBalance = false,
        WhiteBalanceRgb? whiteBalance = null) => new(
            mode ?? baseline.ProductionAcquisitionMode,
            exposure ?? baseline.ExposureTimeUs,
            gain ?? baseline.GainDb,
            roi ?? baseline.RegionOfInterest,
            pixelFormat ?? baseline.PixelFormat,
            replaceValidBits ? validBits : baseline.ValidBits,
            acquisitionTimeoutMs ?? baseline.AcquisitionTimeoutMs,
            triggerDelayUs ?? baseline.TriggerDelayUs,
            replaceWhiteBalance ? whiteBalance : baseline.WhiteBalanceRgb);
}
