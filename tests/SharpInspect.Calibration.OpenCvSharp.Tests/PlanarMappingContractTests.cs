using System;
using System.Linq;
using SharpInspect.Abstractions;
using SharpInspect.Calibration.OpenCvSharp;
using Xunit;

namespace SharpInspect.Calibration.OpenCvSharp.Tests;

public sealed class PlanarMappingContractTests
{
    private static readonly double[] FrozenH =
    {
        4.2d, 0.35d, 100d,
        0.15d, 3.8d, 75d,
        0.0012d, 0.0008d, 1d,
    };

    [Fact]
    public void V126_M01_FrozenHUsesIndependentForwardAndInverseCorrespondence()
    {
        var coefficients = CreateCoefficients();
        var physical = new PlanarPoint(37.5d, 42.25d);
        var expectedImage = IndependentProject(FrozenH, physical);

        Assert.True(coefficients.TryPlaneToImage(physical, out var image));
        AssertPointClose(expectedImage, image, 1e-10d);
        Assert.True(coefficients.TryImageToPlane(expectedImage, out var recovered));
        AssertPointClose(physical, recovered, 1e-9d);

        Assert.Equal(FrozenH.Length, coefficients.ImageToPlane.Count);
        Assert.Equal(FrozenH.Length, coefficients.PlaneToImage.Count);
        Assert.All(coefficients.ImageToPlane, value => Assert.True(double.IsFinite(value)));
        Assert.All(coefficients.PlaneToImage, value => Assert.True(double.IsFinite(value)));
    }

    [Fact]
    public void V126_M02_HullAndMatrixInputsAreDefensivelyCopied()
    {
        var input = CreateInput();
        var inputHash = new PlanarHomographyInputCodec().EncodePayload(input).ContentHash;
        var matrix = IndependentInverse(FrozenH);
        var imageHull = ImageHull();
        var physicalHull = PhysicalHull();
        var coefficients = new PlanarHomographyCoefficients(inputHash, input, matrix, imageHull, physicalHull);

        matrix[0] = 999d;
        imageHull[0] = new PlanarPoint(-100d, -100d);
        physicalHull[0] = new PlanarPoint(-100d, -100d);

        Assert.Same(input, coefficients.Input);
        Assert.Equal(Normalize(IndependentInverse(FrozenH))[0], coefficients.ImageToPlane[0]);
        Assert.Equal(ImageHull()[0], coefficients.ImageHull[0]);
        Assert.Equal(PhysicalHull()[0], coefficients.PhysicalHull[0]);
    }

    [Fact]
    public void V126_M03_ProjectiveLocalScaleIsFiniteAndVariesAcrossImage()
    {
        var coefficients = CreateCoefficients();
        var first = coefficients.GetLocalScale(new PlanarPoint(120d, 100d));
        var second = coefficients.GetLocalScale(new PlanarPoint(430d, 300d));

        Assert.All(new[] { first.J11, first.J12, first.J21, first.J22,
            first.MinimumMillimetersPerPixel, first.MaximumMillimetersPerPixel,
            first.AreaSquareMillimetersPerSquarePixel, second.J11, second.J12,
            second.J21, second.J22, second.MinimumMillimetersPerPixel,
            second.MaximumMillimetersPerPixel, second.AreaSquareMillimetersPerSquarePixel },
            value => Assert.True(double.IsFinite(value)));
        Assert.True(first.MinimumMillimetersPerPixel > 0d);
        Assert.True(first.MaximumMillimetersPerPixel >= first.MinimumMillimetersPerPixel);
        Assert.True(first.AreaSquareMillimetersPerSquarePixel > 0d);
        Assert.True(Math.Abs(first.MinimumMillimetersPerPixel - second.MinimumMillimetersPerPixel) > 1e-6d);
    }

    [Fact]
    public void V126_M04_MappingsRejectOutsideAndNonFinitePointsAndScaleRejectsOutside()
    {
        var coefficients = CreateCoefficients();
        var outsideImage = new PlanarPoint(639d, 479d);
        var outsidePlane = new PlanarPoint(100.1d, 40d);

        Assert.False(coefficients.TryImageToPlane(outsideImage, out _));
        Assert.False(coefficients.TryPlaneToImage(outsidePlane, out _));
        Assert.False(coefficients.TryImageToPlane(new PlanarPoint(double.NaN, 100d), out _));
        Assert.False(coefficients.TryPlaneToImage(new PlanarPoint(10d, double.PositiveInfinity), out _));
        Assert.Throws<ArgumentException>(() => coefficients.GetLocalScale(outsideImage));
    }

    [Fact]
    public void V126_M05_ConstructorRejectsSingularHorizonNonFiniteAndInvalidHulls()
    {
        var input = CreateInput();
        var inputHash = InputHash(input);
        var imageHull = ImageHull();
        var physicalHull = PhysicalHull();

        var singular = new double[]
        {
            1d, 0d, 0d,
            0d, 1d, 0d,
            0d, 0d, 0d,
        };
        Assert.Throws<ArgumentException>(() => new PlanarHomographyCoefficients(
            inputHash, input, singular, imageHull, physicalHull));

        var horizon = new double[]
        {
            1d, 0d, 0d,
            0d, 1d, 0d,
            -0.003d, 0d, 1d,
        };
        Assert.Throws<ArgumentException>(() => new PlanarHomographyCoefficients(
            inputHash, input, horizon, imageHull, physicalHull));

        var nonFinite = FrozenH.ToArray();
        nonFinite[0] = double.NaN;
        Assert.Throws<ArgumentException>(() => new PlanarHomographyCoefficients(
            inputHash, input, nonFinite, imageHull, physicalHull));

        var collinear = new[]
        {
            new PlanarPoint(0d, 0d), new PlanarPoint(10d, 0d),
            new PlanarPoint(20d, 0d), new PlanarPoint(30d, 0d),
        };
        Assert.Throws<ArgumentException>(() => new PlanarHomographyCoefficients(
            inputHash, input, FrozenH, collinear, physicalHull));

        var outOfImage = imageHull.ToArray();
        outOfImage[0] = new PlanarPoint(-1d, imageHull[0].Y);
        Assert.Throws<ArgumentException>(() => new PlanarHomographyCoefficients(
            inputHash, input, FrozenH, outOfImage, physicalHull));
    }

    [Fact]
    public void V126_M06_CoefficientCodecBindsCompleteInputAndIsCanonical()
    {
        var coefficients = CreateCoefficients();
        var payload = PlanarHomographyResultCodec.EncodeCoefficients(coefficients);
        var decoded = PlanarHomographyResultCodec.DecodeCoefficients(payload);

        Assert.Equal(PlanarHomographyContracts.Coefficients, payload.Format);
        Assert.Equal(coefficients.InputPayloadHash, decoded.InputPayloadHash);
        Assert.Equal(coefficients.ImageToPlane, decoded.ImageToPlane);
        Assert.Equal(coefficients.PlaneToImage, decoded.PlaneToImage);
        Assert.Equal(coefficients.ImageHull, decoded.ImageHull);
        Assert.Equal(coefficients.PhysicalHull, decoded.PhysicalHull);
        Assert.Equal(coefficients.Input.Target.TargetDefinitionId,
            decoded.Input.Target.TargetDefinitionId);
        Assert.Equal(coefficients.Input.Target.PlaneCoordinateFrameId,
            decoded.Input.Target.PlaneCoordinateFrameId);
        Assert.Equal(payload.ContentHash,
            PlanarHomographyResultCodec.EncodeCoefficients(decoded).ContentHash);

        var bytes = payload.GetBytes();
        bytes[bytes.Length / 2] ^= 0x01;
        Assert.Throws<ArgumentException>(() => PlanarHomographyResultCodec.DecodeCoefficients(
            new CalibrationCoefficientPayload(payload.Format, bytes)));
        Assert.Throws<ArgumentException>(() => PlanarHomographyResultCodec.DecodeCoefficients(
            new CalibrationCoefficientPayload(PlanarHomographyContracts.Input, payload.GetBytes())));
    }

    [Fact]
    public void V126_M07_EvidenceRetainsRawResidualsAndCanonicalCodecRoundTrips()
    {
        var input = CreateInput();
        var residuals = new[]
        {
            new PlanarPointResidual(0, 0, new PlanarPoint(100.5d, 75.25d),
                new PlanarPoint(0d, 0d), new PlanarPoint(0.3d, -0.4d),
                new PlanarPoint(1.2d, -1.6d)),
            new PlanarPointResidual(0, 1, new PlanarPoint(179.5d, 76.1d),
                new PlanarPoint(20d, 0d), new PlanarPoint(-0.6d, 0.8d),
                new PlanarPoint(-0.5d, 1.5d)),
            new PlanarPointResidual(0, 2, new PlanarPoint(183.7d, 148.1d),
                new PlanarPoint(20d, 20d), new PlanarPoint(0.1d, -0.2d),
                new PlanarPoint(0.25d, -0.75d)),
            new PlanarPointResidual(0, 3, new PlanarPoint(105.3d, 148.6d),
                new PlanarPoint(0d, 20d), new PlanarPoint(0.2d, -0.1d),
                new PlanarPoint(-0.4d, 0.3d)),
        };
        var evidence = new PlanarHomographyEvidence(InputHash(input),
            Guid.Parse("12600000-0000-0000-0000-000000000007"), new string('A', 64),
            0.25d, 0.001d, residuals, new[] { 1, 2, 3 });

        Assert.Equal(4, evidence.Residuals.Count);
        Assert.Equal(new[] { 1, 2, 3 }, evidence.MissingMarkerIds);
        Assert.Equal(Math.Sqrt((0.3d * 0.3d + 0.4d * 0.4d + 0.6d * 0.6d + 0.8d * 0.8d +
            0.1d * 0.1d + 0.2d * 0.2d + 0.2d * 0.2d + 0.1d * 0.1d) / 4d),
            evidence.RmsMillimeters, 12);
        Assert.Equal(Math.Sqrt(0.6d * 0.6d + 0.8d * 0.8d), evidence.MaxMillimeters, 12);
        Assert.Equal(Math.Sqrt((1.2d * 1.2d + 1.6d * 1.6d + 0.5d * 0.5d + 1.5d * 1.5d +
            0.25d * 0.25d + 0.75d * 0.75d + 0.4d * 0.4d + 0.3d * 0.3d) / 4d),
            evidence.RmsPixels, 12);
        Assert.Equal(Math.Sqrt(1.2d * 1.2d + 1.6d * 1.6d), evidence.MaxPixels, 12);

        var payload = PlanarHomographyResultCodec.EncodeEvidence(evidence);
        var decoded = PlanarHomographyResultCodec.DecodeEvidence(payload);
        Assert.Equal(evidence.InputPayloadHash, decoded.InputPayloadHash);
        Assert.Equal(evidence.FrameId, decoded.FrameId);
        Assert.Equal(evidence.SourceHash, decoded.SourceHash);
        Assert.Equal(evidence.ConstraintRankRatio, decoded.ConstraintRankRatio);
        Assert.Equal(evidence.InverseClosureError, decoded.InverseClosureError);
        Assert.Equal(evidence.Residuals.Count, decoded.Residuals.Count);
        Assert.Equal(evidence.MissingMarkerIds, decoded.MissingMarkerIds);
        Assert.Equal(payload.ContentHash,
            PlanarHomographyResultCodec.EncodeEvidence(decoded).ContentHash);

        var bytes = payload.GetBytes();
        bytes[^1] ^= 0x01;
        Assert.Throws<ArgumentException>(() => PlanarHomographyResultCodec.DecodeEvidence(
            new CalibrationComputationEvidencePayload(payload.Format, bytes)));
    }

    [Fact]
    public void V126_M08_CoefficientInputHashMustBeTheCanonicalInputIdentity()
    {
        var input = CreateInput();
        var wrongHash = new string('F', 64);
        Assert.Throws<ArgumentException>(() => new PlanarHomographyCoefficients(
            wrongHash, input, FrozenH, ImageHull(), PhysicalHull()));
    }

    [Fact]
    public void V126_M09_EvidenceRejectsEmptyIncompleteUnsortedAndOverflowingResidualSets()
    {
        const string inputHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const string sourceHash = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        var frameId = Guid.Parse("12600000-0000-0000-0000-000000000009");
        var valid = ResidualsForMarkers(1, 0.1d);

        Assert.Throws<ArgumentException>(() => new PlanarHomographyEvidence(
            inputHash, frameId, sourceHash, 0.5d, 0.01d,
            Array.Empty<PlanarPointResidual>(), Array.Empty<int>()));
        Assert.Throws<ArgumentException>(() => new PlanarHomographyEvidence(
            inputHash, frameId, sourceHash, 0.5d, 0.01d,
            valid.Take(3), Array.Empty<int>()));
        Assert.Throws<ArgumentException>(() => new PlanarHomographyEvidence(
            inputHash, frameId, sourceHash, 0.5d, 0.01d,
            valid.Reverse(), Array.Empty<int>()));
        Assert.Throws<ArgumentException>(() => new PlanarHomographyEvidence(
            inputHash, frameId, sourceHash, 0.5d, 0.01d,
            valid, new[] { 2, 1 }));
        Assert.Throws<ArgumentException>(() => new PlanarPointResidual(
            0, 0, new PlanarPoint(-1d, 0d), new PlanarPoint(0d, 0d),
            new PlanarPoint(0d, 0d), new PlanarPoint(0d, 0d)));

        // Each individual squared norm is finite, while aggregating all 200
        // residuals in an ordinary double sum overflows and must be rejected.
        var overflowing = ResidualsForMarkers(50, 1e154d);
        Assert.Throws<ArgumentException>(() => new PlanarHomographyEvidence(
            inputHash, frameId, sourceHash, 0.5d, 0.01d,
            overflowing, Array.Empty<int>()));
    }

    [Fact]
    public void V126_M10_RankAndScaleAcceptLargeTranslationsAndSmallPositiveJacobian()
    {
        var input = CreateInput();
        var inputHash = InputHash(input);
        var translated = FrozenH.ToArray();
        translated[2] = 1_000_000d;
        translated[5] = -1_000_000d;
        var translatedCoefficients = new PlanarHomographyCoefficients(
            inputHash, input, translated, ImageHull(), PhysicalHull());

        var probe = new PlanarPoint(300d, 200d);
        Assert.True(translatedCoefficients.TryImageToPlane(probe, out var translatedResult));
        AssertPointClose(IndependentProject(translated, probe), translatedResult, 1e-6d);

        var smallScale = new[]
        {
            1e-8d, 0d, 0d,
            0d, 1e-8d, 0d,
            0d, 0d, 1d,
        };
        var smallScaleCoefficients = new PlanarHomographyCoefficients(
            inputHash, input, smallScale, ImageHull(), PhysicalHull());
        var scale = smallScaleCoefficients.GetLocalScale(new PlanarPoint(300d, 200d));
        Assert.InRange(scale.MinimumMillimetersPerPixel, 0.999e-8d, 1.001e-8d);
        Assert.InRange(scale.MaximumMillimetersPerPixel, 0.999e-8d, 1.001e-8d);
        Assert.InRange(scale.AreaSquareMillimetersPerSquarePixel, 0.999e-16d, 1.001e-16d);
    }

    private static PlanarHomographyCoefficients CreateCoefficients()
    {
        var input = CreateInput();
        return new PlanarHomographyCoefficients(InputHash(input), input,
            Normalize(IndependentInverse(FrozenH)), ImageHull(), PhysicalHull());
    }

    private static string InputHash(PlanarHomographyInput input) =>
        new PlanarHomographyInputCodec().EncodePayload(input).ContentHash;

    private static PlanarHomographyInput CreateInput()
    {
        var configuration = new EffectiveCameraConfiguration(
            ProductionAcquisitionMode.SoftwareTrigger, 1000d, 0d,
            new RegionOfInterest(0, 0, SyntheticPlanarFixture.ImageWidth,
                SyntheticPlanarFixture.ImageHeight), VisionPixelFormat.Mono8, null,
            1000, 0d, null);
        var markers = SyntheticPlanarFixture.Markers.Select(marker =>
            new ArucoPlanarMarkerDefinition(marker.Id, marker.PhysicalCorners.Select(
                point => new PlanarPoint(point.X, point.Y))));
        var target = new ArucoPlanarTargetDefinition("planar-target-v1", "plane-frame-v1", markers);
        return new PlanarHomographyInput("TopCamera", configuration,
            PlanarPixelDomain.RawRoiPixelCentersCorrectionDeclaredNotRequired, target);
    }

    private static PlanarPoint[] PhysicalHull() =>
        SyntheticPlanarFixture.BoardCorners.Select(
            point => new PlanarPoint(point.X, point.Y)).ToArray();

    private static PlanarPoint[] ImageHull() =>
        PhysicalHull().Select(point => IndependentProject(FrozenH, point)).ToArray();

    private static PlanarPoint IndependentProject(IReadOnlyList<double> matrix, PlanarPoint point)
    {
        var denominator = matrix[6] * point.X + matrix[7] * point.Y + matrix[8];
        return new PlanarPoint(
            (matrix[0] * point.X + matrix[1] * point.Y + matrix[2]) / denominator,
            (matrix[3] * point.X + matrix[4] * point.Y + matrix[5]) / denominator);
    }

    private static double[] IndependentInverse(IReadOnlyList<double> matrix)
    {
        var a = matrix[0]; var b = matrix[1]; var c = matrix[2];
        var d = matrix[3]; var e = matrix[4]; var f = matrix[5];
        var g = matrix[6]; var h = matrix[7]; var i = matrix[8];
        var determinant = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        return new[]
        {
            (e * i - f * h) / determinant,
            (c * h - b * i) / determinant,
            (b * f - c * e) / determinant,
            (f * g - d * i) / determinant,
            (a * i - c * g) / determinant,
            (c * d - a * f) / determinant,
            (d * h - e * g) / determinant,
            (b * g - a * h) / determinant,
            (a * e - b * d) / determinant,
        };
    }

    private static double[] Normalize(IReadOnlyList<double> matrix)
    {
        var maximum = matrix.Max(value => Math.Abs(value));
        var firstMaximum = Array.FindIndex(matrix.ToArray(), value => Math.Abs(value) == maximum);
        var sign = matrix[firstMaximum] < 0d ? -1d : 1d;
        return matrix.Select(value => sign * value / maximum).ToArray();
    }

    private static PlanarPointResidual[] ResidualsForMarkers(int markerCount, double residualMagnitude)
    {
        return Enumerable.Range(0, markerCount).SelectMany(markerId =>
            Enumerable.Range(0, 4).Select(cornerIndex => new PlanarPointResidual(
                markerId, cornerIndex,
                new PlanarPoint(100d + markerId, 100d + cornerIndex),
                new PlanarPoint(markerId, cornerIndex),
                new PlanarPoint(residualMagnitude, 0d),
                new PlanarPoint(residualMagnitude, 0d)))).ToArray();
    }

    private static void AssertPointClose(PlanarPoint expected, PlanarPoint actual, double tolerance)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0d, tolerance);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0d, tolerance);
    }
}
