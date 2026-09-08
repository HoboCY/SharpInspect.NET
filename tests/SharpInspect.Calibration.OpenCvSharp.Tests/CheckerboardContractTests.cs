using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Calibration.OpenCvSharp.Tests;

public sealed class CheckerboardContractTests
{
    [Fact]
    public void V125_I01_InputRetainsBoardUnitsRoleAndCompleteReadBackConfiguration()
    {
        var configuration = new EffectiveCameraConfiguration(ProductionAcquisitionMode.HardwareTrigger,
            1520.25, 3.5, new RegionOfInterest(24, 12, 640, 480), VisionPixelFormat.Bgr24, null,
            1530, 8.5, new WhiteBalanceRgb(1.5, 2, 2.5));
        var original = new CheckerboardIntrinsicsInput(9, 6, 25, "TopCamera", configuration);
        var codec = new CheckerboardIntrinsicsInputCodec();
        var payload = codec.EncodePayload(original);
        var decoded = codec.Decode(payload);
        Assert.Equal(9, decoded.InnerColumns); Assert.Equal(6, decoded.InnerRows);
        Assert.Equal(25, decoded.SquareSizeMillimeters); Assert.Equal("TopCamera", decoded.LogicalCameraRole);
        Assert.Equal(configuration, decoded.ExpectedConfiguration);
        Assert.Equal(payload.ContentHash, codec.EncodePayload(decoded).ContentHash);
        var changed = new CheckerboardIntrinsicsInput(9, 6, 24.9, "TopCamera", configuration);
        Assert.NotEqual(payload.ContentHash, codec.EncodePayload(changed).ContentHash);
        var bytes = payload.GetBytes(); bytes[0] = 2;
        Assert.Throws<ArgumentException>(() => codec.Decode(bytes));
        Assert.Throws<ArgumentException>(() => codec.Decode(payload.GetBytes().Concat(new byte[] { 0 }).ToArray()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void V125_I02_InvalidPhysicalScaleCannotBecomeCanonicalInput(double square)
    {
        var configuration = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 1000, 0,
            new RegionOfInterest(0, 0, 640, 480), VisionPixelFormat.Mono8, null, 1000, 0, null);
        Assert.Throws<ArgumentException>(() => new CheckerboardIntrinsicsInput(9, 6, square, "TopCamera", configuration));
    }

    [Fact]
    public void V125_I04_InputRoleUsesTheSameIdentifierGrammarAsFrames()
    {
        var configuration = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 1000, 0,
            new RegionOfInterest(0, 0, 640, 480), VisionPixelFormat.Mono8, null, 1000, 0, null);
        foreach (var role in new[] { "", "Top Camera", "Top\nCamera", "\uD800", new string('a', 65) })
            Assert.Throws<ArgumentException>(() => new CheckerboardIntrinsicsInput(9, 6, 25, role, configuration));
        var input = new CheckerboardIntrinsicsInput(9, 6, 25, "Top.Camera_1-A", configuration);
        Assert.Equal(input.LogicalCameraRole, new CheckerboardIntrinsicsInputCodec().Decode(
            new CheckerboardIntrinsicsInputCodec().Encode(input)).LogicalCameraRole);
    }

    [Fact]
    public void V125_I03_CoefficientCodecSeparatesIntrinsicValuesFromSessionEvidence()
    {
        var value = new CheckerboardIntrinsicsCoefficients(640, 480, 800, 820, 319.5, 239.5,
            -0.12, 0.035, 0.001, -0.0008, 0);
        var payload = CheckerboardIntrinsicsResultCodec.EncodeCoefficients(value);
        var decoded = CheckerboardIntrinsicsResultCodec.DecodeCoefficients(payload);
        Assert.Equal(84, payload.Length);
        Assert.Equal(800, decoded.Fx); Assert.Equal(820, decoded.Fy);
        Assert.Equal(-0.12, decoded.K1); Assert.Equal(-0.0008, decoded.P2);
        var matrix = decoded.GetCameraMatrix(); matrix[0, 0] = 1;
        var distortion = decoded.GetDistortionCoefficients(); distortion[0] = 1;
        Assert.Equal(800, decoded.GetCameraMatrix()[0, 0]); Assert.Equal(-0.12, decoded.GetDistortionCoefficients()[0]);
        Assert.Throws<ArgumentException>(() => CheckerboardIntrinsicsResultCodec.DecodeCoefficients(
            new CalibrationCoefficientPayload(CheckerboardIntrinsicsContracts.Input, payload.GetBytes())));
        Assert.Throws<ArgumentException>(() => CheckerboardIntrinsicsResultCodec.DecodeCoefficients(
            new CalibrationCoefficientPayload(payload.Format, payload.GetBytes().Concat(new byte[] { 0 }).ToArray())));
        Assert.Throws<ArgumentException>(() => new CheckerboardIntrinsicsCoefficients(640, 480,
            double.NaN, 820, 319.5, 239.5, 0, 0, 0, 0, 0));
    }
}
