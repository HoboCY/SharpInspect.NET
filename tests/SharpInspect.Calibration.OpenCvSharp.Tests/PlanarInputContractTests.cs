using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Calibration.OpenCvSharp.Tests;

public sealed class PlanarInputContractTests
{
    [Fact]
    public void V126_I01_InputBindsExplicitRawDomainTargetCoordinatesAndFullFrameConfiguration()
    {
        var input = Input();
        var codec = new PlanarHomographyInputCodec();
        var payload = codec.EncodePayload(input);
        var decoded = codec.Decode(payload);
        Assert.Equal(input.ExpectedConfiguration, decoded.ExpectedConfiguration);
        Assert.Equal(input.LogicalCameraRole, decoded.LogicalCameraRole);
        Assert.Equal(PlanarPixelDomain.RawRoiPixelCentersCorrectionDeclaredNotRequired, decoded.PixelDomain);
        Assert.Equal("Millimeter", decoded.Target.PhysicalUnit);
        Assert.Equal("DICT_4X4_50", decoded.Target.DictionaryName);
        Assert.Equal("FixturePlane", decoded.Target.PlaneCoordinateFrameId);
        Assert.Equal(payload.ContentHash, codec.EncodePayload(decoded).ContentHash);
        Assert.Equal(input.Target.Markers[0].PhysicalCornersMillimeters, decoded.Target.Markers[0].PhysicalCornersMillimeters);
        var changedCoordinates = new ArucoPlanarTargetDefinition("FixtureTarget", "FixturePlane",
            new[] { new ArucoPlanarMarkerDefinition(0, new[] { new PlanarPoint(0, 0), new(21, 0), new(21, 20), new(0, 20) }) });
        Assert.NotEqual(payload.ContentHash, codec.EncodePayload(new PlanarHomographyInput(input.LogicalCameraRole,
            input.ExpectedConfiguration, input.PixelDomain, changedCoordinates)).ContentHash);
        Assert.NotEqual(payload.ContentHash, codec.EncodePayload(new PlanarHomographyInput("SideCamera",
            input.ExpectedConfiguration, input.PixelDomain, input.Target)).ContentHash);
    }

    [Fact]
    public void V126_I02_NoDefaultOrSilentFallbackWhenIntrinsicCorrectionIsRequired()
    {
        var input = Input();
        foreach (var domain in new[] { PlanarPixelDomain.Unknown, (PlanarPixelDomain)99 })
            Assert.Throws<ArgumentException>(() => new PlanarHomographyInput(input.LogicalCameraRole,
                input.ExpectedConfiguration, domain, input.Target));
        var error = Assert.Throws<CalibrationProcedureException>(() => new PlanarHomographyInput(input.LogicalCameraRole,
            input.ExpectedConfiguration, PlanarPixelDomain.ExactIntrinsicCorrectionRequired, input.Target));
        Assert.Equal("PlanarIntrinsicCorrectionUnsupported", error.ReasonCode);
        Assert.Contains("raw-roi", PlanarHomographyContracts.Coefficients.Id);
    }

    [Fact]
    public void V126_I03_TargetCopiesCallerDataAndUsesDecodedMarkerOrder()
    {
        var points = new[] { new PlanarPoint(0, 0), new(20, 0), new(20, 20), new(0, 20) };
        var marker = new ArucoPlanarMarkerDefinition(7, points);
        points[0] = new(100, 100);
        var markers = new[] { marker };
        var target = new ArucoPlanarTargetDefinition("Target", "Plane", markers);
        markers[0] = new ArucoPlanarMarkerDefinition(8, marker.PhysicalCornersMillimeters);
        Assert.Equal(7, target.Markers[0].MarkerId);
        Assert.Equal(new PlanarPoint(0, 0), target.Markers[0].PhysicalCornersMillimeters[0]);
        // A physical frame may point Y up; retain the supplied decoded corner order.
        var yUp = new ArucoPlanarMarkerDefinition(1, new[] { new PlanarPoint(0, 0), new(20, 0), new(20, -20), new(0, -20) });
        Assert.Equal(-20, yUp.PhysicalCornersMillimeters[2].Y);
    }

    [Fact]
    public void V126_I04_InvalidTargetCannotEnterCanonicalSessionInput()
    {
        var input = Input(); var marker = input.Target.Markers[0];
        Assert.Throws<ArgumentException>(() => new ArucoPlanarTargetDefinition("Target", "Plane", new[] { marker, marker }));
        Assert.Throws<ArgumentException>(() => new ArucoPlanarTargetDefinition("Target", "Plane", Array.Empty<ArucoPlanarMarkerDefinition>()));
        Assert.Throws<ArgumentException>(() => new ArucoPlanarMarkerDefinition(50, marker.PhysicalCornersMillimeters));
        Assert.Throws<ArgumentException>(() => new ArucoPlanarMarkerDefinition(1, new[] { new PlanarPoint(0, 0), new(1, 0), new(2, 0), new(3, 0) }));
        Assert.Throws<ArgumentException>(() => new ArucoPlanarMarkerDefinition(1, new[] { new PlanarPoint(0, 0), new(20, 20), new(20, 0), new(0, 20) }));
        foreach (var number in new[] { double.NaN, double.PositiveInfinity, 1_000_001d })
            Assert.Throws<ArgumentException>(() => new ArucoPlanarMarkerDefinition(1,
                new[] { new PlanarPoint(number, 0), new(20, 0), new(20, 20), new(0, 20) }));
        foreach (var identifier in new[] { "", "bad id", "line\nbreak", "\uD800", new string('x', 65) })
        {
            Assert.Throws<ArgumentException>(() => new ArucoPlanarTargetDefinition(identifier, "Plane", new[] { marker }));
            Assert.Throws<ArgumentException>(() => new ArucoPlanarTargetDefinition("Target", identifier, new[] { marker }));
        }
    }

    [Fact]
    public void V126_I05_UnknownTruncatedTrailingAndNoncanonicalPayloadsAreRejected()
    {
        var codec = new PlanarHomographyInputCodec(); var bytes = codec.Encode(Input()).ToArray();
        var version = (byte[])bytes.Clone(); version[0] = 2;
        Assert.Throws<ArgumentException>(() => codec.Decode(version));
        Assert.Throws<ArgumentException>(() => codec.Decode(bytes[..^1]));
        Assert.Throws<ArgumentException>(() => codec.Decode(bytes.Concat(new byte[] { 0 }).ToArray()));
        var malformedLength = (byte[])bytes.Clone(); malformedLength[4] = 127;
        Assert.ThrowsAny<ArgumentException>(() => codec.Decode(malformedLength));
    }

    internal static PlanarHomographyInput Input() => new("TopCamera",
        new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 1234.5, 2.5,
            new RegionOfInterest(24, 12, 640, 480), VisionPixelFormat.Bgr24, null, 1100, 6,
            new WhiteBalanceRgb(1.5, 2, 2.5)), PlanarPixelDomain.RawRoiPixelCentersCorrectionDeclaredNotRequired,
        new ArucoPlanarTargetDefinition("FixtureTarget", "FixturePlane", new[]
        {
            new ArucoPlanarMarkerDefinition(0, new[] { new PlanarPoint(0, 0), new(20, 0), new(20, 20), new(0, 20) })
        }));
}
