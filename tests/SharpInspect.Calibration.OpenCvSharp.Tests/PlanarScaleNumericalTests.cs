using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Calibration.OpenCvSharp.Tests;

public sealed class PlanarScaleNumericalTests
{
    [Fact]
    public void V126_J01_LocalJacobianMatchesIndependentFiniteDifferencesAtTwoPositions()
    {
        var forward = SyntheticPlanarFixture.Homography.ToArray();
        var inverse = Invert(forward);
        var physicalHull = SyntheticPlanarFixture.BoardCorners.Select(p => new PlanarPoint(p.X, p.Y)).ToArray();
        var imageHull = physicalHull.Select(p => Project(forward, p)).ToArray();
        var input = Input();
        var coefficients = new PlanarHomographyCoefficients(new PlanarHomographyInputCodec().EncodePayload(input).ContentHash,
            input, inverse, imageHull, physicalHull);
        var scales = new List<PlanarLocalScale>();
        foreach (var physical in new[] { new PlanarPoint(10, 10), new PlanarPoint(80, 60) })
        {
            var image = Project(forward, physical);
            var scale = coefficients.GetLocalScale(image);
            scales.Add(scale);
            const double step = 0.001;
            var right = Project(inverse, image with { X = image.X + step });
            var left = Project(inverse, image with { X = image.X - step });
            var down = Project(inverse, image with { Y = image.Y + step });
            var up = Project(inverse, image with { Y = image.Y - step });
            Assert.InRange(Math.Abs(scale.J11 - (right.X - left.X) / (2 * step)), 0, 1e-8);
            Assert.InRange(Math.Abs(scale.J21 - (right.Y - left.Y) / (2 * step)), 0, 1e-8);
            Assert.InRange(Math.Abs(scale.J12 - (down.X - up.X) / (2 * step)), 0, 1e-8);
            Assert.InRange(Math.Abs(scale.J22 - (down.Y - up.Y) / (2 * step)), 0, 1e-8);
            var determinant = Math.Abs(scale.J11 * scale.J22 - scale.J12 * scale.J21);
            Assert.InRange(Math.Abs(determinant - scale.AreaSquareMillimetersPerSquarePixel), 0, 1e-12);
            Assert.InRange(Math.Abs(determinant - scale.MinimumMillimetersPerPixel * scale.MaximumMillimetersPerPixel), 0, 1e-12);
            for (var direction = 0; direction < 36; direction++)
            {
                var angle = direction * Math.PI / 18; var x = Math.Cos(angle); var y = Math.Sin(angle);
                var dx = scale.J11 * x + scale.J12 * y; var dy = scale.J21 * x + scale.J22 * y;
                Assert.InRange(Math.Sqrt(dx * dx + dy * dy), scale.MinimumMillimetersPerPixel - 1e-12,
                    scale.MaximumMillimetersPerPixel + 1e-12);
            }
        }
        Assert.True(Math.Abs(scales[0].MaximumMillimetersPerPixel - scales[1].MaximumMillimetersPerPixel) > 0.01);
    }

    [Fact]
    public void V126_J02_InvertibleZeroH33IsAllowedWhenItsHorizonIsOutsideBothSupports()
    {
        var input = Input();
        var matrix = new[] { 0d, 0d, 1d, 0d, 1d, 0d, 1d, 0d, 0d };
        var imageHull = new[] { new PlanarPoint(10, 10), new(20, 10), new(20, 20), new(10, 20) };
        var physicalHull = imageHull.Select(p => Project(matrix, p)).Reverse().ToArray();
        var coefficients = new PlanarHomographyCoefficients(new PlanarHomographyInputCodec().EncodePayload(input).ContentHash,
            input, matrix, imageHull, physicalHull);
        Assert.True(coefficients.TryImageToPlane(new(15, 15), out var physical));
        Assert.InRange(Math.Abs(physical.X - 1d / 15), 0, 1e-12);
        Assert.InRange(Math.Abs(physical.Y - 1), 0, 1e-12);
        Assert.True(coefficients.TryPlaneToImage(physical, out var image));
        Assert.InRange(Math.Abs(image.X - 15), 0, 1e-12);
        Assert.InRange(Math.Abs(image.Y - 15), 0, 1e-12);
    }

    [Fact]
    public void V126_J03_SelfIntersectingStarCannotMasqueradeAsConvexSupport()
    {
        var ring = Enumerable.Range(0, 5).Select(index =>
            new PlanarPoint(200 + 50 * Math.Cos(index * 2 * Math.PI / 5),
                200 + 50 * Math.Sin(index * 2 * Math.PI / 5))).ToArray();
        var star = new[] { ring[0], ring[2], ring[4], ring[1], ring[3] };
        var physical = new[] { new PlanarPoint(0, 0), new(100, 0), new(100, 80), new(0, 80) };
        var input = Input();
        Assert.Throws<ArgumentException>(() => new PlanarHomographyCoefficients(
            new PlanarHomographyInputCodec().EncodePayload(input).ContentHash,
            input, new[] { 1d, 0d, 0d, 0d, 1d, 0d, 0d, 0d, 1d }, star, physical));
    }

    [Fact]
    public void V126_J04_NearCancellationInsideAMinorIsRejectedByRelativeRankGuard()
    {
        var input = Input();
        var nearlySingular = new[] { 1d, 0d, 0d, 0d, 1d, 1d, 0d, 1d, 1d + 1e-15 };
        var imageHull = new[] { new PlanarPoint(10, 10), new(20, 10), new(20, 20), new(10, 20) };
        var physicalHull = new[] { new PlanarPoint(0, 0), new(1, 0), new(1, 0.5), new(0, 0.5) };
        Assert.Throws<ArgumentException>(() => new PlanarHomographyCoefficients(
            new PlanarHomographyInputCodec().EncodePayload(input).ContentHash,
            input, nearlySingular, imageHull, physicalHull));
    }

    private static PlanarHomographyInput Input() => new("TopCamera",
        new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 1000, 0,
            new RegionOfInterest(0, 0, 640, 480), VisionPixelFormat.Mono8, null, 1000, 0, null),
        PlanarPixelDomain.RawRoiPixelCentersCorrectionDeclaredNotRequired,
        new ArucoPlanarTargetDefinition("Fixture", "Plane", SyntheticPlanarFixture.Markers.Select(marker =>
            new ArucoPlanarMarkerDefinition(marker.Id, marker.PhysicalCorners.Select(p => new PlanarPoint(p.X, p.Y))))));

    private static PlanarPoint Project(IReadOnlyList<double> h, PlanarPoint point)
    {
        var w = h[6] * point.X + h[7] * point.Y + h[8];
        return new((h[0] * point.X + h[1] * point.Y + h[2]) / w,
            (h[3] * point.X + h[4] * point.Y + h[5]) / w);
    }
    private static double[] Invert(IReadOnlyList<double> h)
    {
        // Adjugate suffices for homogeneous projection; no production matrix utility is used.
        return new[]
        {
            h[4]*h[8]-h[5]*h[7], h[2]*h[7]-h[1]*h[8], h[1]*h[5]-h[2]*h[4],
            h[5]*h[6]-h[3]*h[8], h[0]*h[8]-h[2]*h[6], h[2]*h[3]-h[0]*h[5],
            h[3]*h[7]-h[4]*h[6], h[1]*h[6]-h[0]*h[7], h[0]*h[4]-h[1]*h[3]
        };
    }
}
