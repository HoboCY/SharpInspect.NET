using System;
using System.Collections.Generic;

namespace SharpInspect.Calibration.OpenCvSharp.Tests;

/// <summary>One point in the frozen planar fixture; units are millimetres or pixels by context.</summary>
internal readonly record struct SyntheticPlanarPoint(double X, double Y)
{
    internal double PixelX => X;
    internal double PixelY => Y;
    internal double XMillimeters => X;
    internal double YMillimeters => Y;
}

/// <summary>
/// One fixed DICT_4X4_50 marker.  <see cref="TopLeft"/> is the physical
/// top-left corner and the two bytes are the rotation-zero dictionary bytes.
/// </summary>
internal readonly record struct SyntheticPlanarMarker(
    int Id,
    double TopLeftXMillimeters,
    double TopLeftYMillimeters,
    byte CodeByte0,
    byte CodeByte1)
{
    internal SyntheticPlanarPoint TopLeft =>
        new(TopLeftXMillimeters, TopLeftYMillimeters);

    internal SyntheticPlanarPoint PhysicalTopLeft => TopLeft;

    internal byte[] CodeBytes => new[] { CodeByte0, CodeByte1 };

    internal byte[] DictionaryBytes => CodeBytes;

    internal IReadOnlyList<SyntheticPlanarPoint> PhysicalCorners =>
        Array.AsReadOnly(new[]
        {
            TopLeft,
            new SyntheticPlanarPoint(
                TopLeftXMillimeters + SyntheticPlanarFixture.MarkerSideMillimeters,
                TopLeftYMillimeters),
            new SyntheticPlanarPoint(
                TopLeftXMillimeters + SyntheticPlanarFixture.MarkerSideMillimeters,
                TopLeftYMillimeters + SyntheticPlanarFixture.MarkerSideMillimeters),
            new SyntheticPlanarPoint(
                TopLeftXMillimeters,
                TopLeftYMillimeters + SyntheticPlanarFixture.MarkerSideMillimeters),
        });

    internal IReadOnlyList<SyntheticPlanarPoint> Corners => PhysicalCorners;
}

/// <summary>
/// Frozen, dependency-free ArUco planar image data for homography tests.
///
/// The renderer uses only the fixed plane-to-ideal-pixel homography and a
/// Brown-Conrady inverse solved with Newton iterations.  It does not call
/// OpenCV detection, projection, or homography routines.  Pixel centres are
/// integer coordinates and every output pixel is the mean of four samples at
/// the fixed +/-0.25 offsets.  Keep this file aligned with
/// Fixtures/planar-v1.json; freeze both before solving any T26 homography.
/// </summary>
internal static class SyntheticPlanarFixture
{
    internal const string DatasetVersion = "planar-v1";
    internal const string DatasetKind = "synthetic-aruco-planar-homography";
    internal const string DictionaryName = "DICT_4X4_50";

    internal const int ImageWidth = 640;
    internal const int ImageHeight = 480;
    internal const int Width = ImageWidth;
    internal const int Height = ImageHeight;
    internal const int PixelCount = ImageWidth * ImageHeight;

    internal const int MarkerCount = 4;
    internal const int MarkerBitSideCells = 4;
    internal const int MarkerCellSide = MarkerBitSideCells + 2;
    internal const int MarkerCells = MarkerCellSide;
    internal const double MarkerSideMillimeters = 20d;
    internal const double MarkerSizeMillimeters = MarkerSideMillimeters;
    internal const double PlaneWidthMillimeters = 100d;
    internal const double PlaneHeightMillimeters = 80d;
    internal const double BoardWidthMillimeters = PlaneWidthMillimeters;
    internal const double BoardHeightMillimeters = PlaneHeightMillimeters;

    internal const byte BlackValue = 0;
    internal const byte WhiteValue = 255;
    internal const byte BackgroundValue = WhiteValue;
    internal const byte VariantBackgroundValue = 224;

    internal const double FocalLengthX = 800d;
    internal const double FocalLengthY = 820d;
    internal const double PrincipalPointX = 319.5d;
    internal const double PrincipalPointY = 239.5d;
    internal const double Fx = FocalLengthX;
    internal const double Fy = FocalLengthY;
    internal const double Cx = PrincipalPointX;
    internal const double Cy = PrincipalPointY;

    internal const double K1 = -0.12d;
    internal const double K2 = 0.035d;
    internal const double P1 = 0.001d;
    internal const double P2 = -0.0008d;
    internal const double K3 = 0d;

    // H maps (physical X mm, physical Y mm, 1) to ideal ROI pixels.
    internal const double H11 = 4.2d;
    internal const double H12 = 0.35d;
    internal const double H13 = 100d;
    internal const double H21 = 0.15d;
    internal const double H22 = 3.8d;
    internal const double H23 = 75d;
    internal const double H31 = 0.0012d;
    internal const double H32 = 0.0008d;
    internal const double H33 = 1d;

    internal const double HomographyH11 = H11;
    internal const double HomographyH12 = H12;
    internal const double HomographyH13 = H13;
    internal const double HomographyH21 = H21;
    internal const double HomographyH22 = H22;
    internal const double HomographyH23 = H23;
    internal const double HomographyH31 = H31;
    internal const double HomographyH32 = H32;
    internal const double HomographyH33 = H33;

    internal const double PixelSubsampleOffset = 0.25d;
    internal const double CornerExtractionMaxErrorPixels = 1d;
    internal const double ForwardPredictionMaxErrorPixels = 1.5d;
    internal const double InversePredictionMaxErrorMillimeters = 0.5d;
    internal const double RoiMinimumXMillimeters = 0d;
    internal const double RoiMaximumXMillimeters = PlaneWidthMillimeters;
    internal const double RoiMinimumYMillimeters = 0d;
    internal const double RoiMaximumYMillimeters = PlaneHeightMillimeters;

    private const int InverseDistortionIterations = 10;
    private const double MinimumJacobianDeterminant = 1e-18d;
    private const double MinimumHomographyDenominator = 1e-12d;
    private const double RoiToleranceMillimeters = 1e-9d;

    private static readonly IReadOnlyList<SyntheticPlanarMarker> MarkerTable =
        Array.AsReadOnly(new[]
        {
            // DICT_4X4_1000_BYTES[0..3][0], from OpenCV 4.11.0.  The first
            // two bytes are the rotation-zero 4x4 payload, read row-major,
            // most-significant bit first.
            new SyntheticPlanarMarker(0, 0d, 0d, 0xB5, 0x32),
            new SyntheticPlanarMarker(1, 80d, 0d, 0x0F, 0x9A),
            new SyntheticPlanarMarker(2, 80d, 60d, 0x33, 0x2D),
            new SyntheticPlanarMarker(3, 0d, 60d, 0x99, 0x46),
        });

    private static readonly IReadOnlyList<SyntheticPlanarPoint> BoardCornerTable =
        Array.AsReadOnly(new[]
        {
            new SyntheticPlanarPoint(RoiMinimumXMillimeters, RoiMinimumYMillimeters),
            new SyntheticPlanarPoint(RoiMaximumXMillimeters, RoiMinimumYMillimeters),
            new SyntheticPlanarPoint(RoiMaximumXMillimeters, RoiMaximumYMillimeters),
            new SyntheticPlanarPoint(RoiMinimumXMillimeters, RoiMaximumYMillimeters),
        });

    private static readonly IReadOnlyList<SyntheticPlanarPoint> PhysicalCornerTable =
        BuildPhysicalCorners();

    private static readonly IReadOnlyList<double> HomographyTable =
        Array.AsReadOnly(new[]
        {
            H11, H12, H13,
            H21, H22, H23,
            H31, H32, H33,
        });

    /// <summary>Markers in stable ID order 0, 1, 2, 3.</summary>
    internal static IReadOnlyList<SyntheticPlanarMarker> Markers => MarkerTable;

    /// <summary>The physical rectangle corners in TL, TR, BR, BL order.</summary>
    internal static IReadOnlyList<SyntheticPlanarPoint> BoardCorners => BoardCornerTable;

    /// <summary>
    /// All marker corners in marker ID order, with each marker in TL, TR, BR,
    /// BL order.  This is the stable correspondence order for the fixture.
    /// </summary>
    internal static IReadOnlyList<SyntheticPlanarPoint> PhysicalCorners => PhysicalCornerTable;

    internal static IReadOnlyList<SyntheticPlanarPoint> PhysicalMarkerCorners =>
        PhysicalCornerTable;

    internal static IReadOnlyList<double> Homography => HomographyTable;

    /// <summary>
    /// Render a row-major Mono8 image.  The default is the ideal image.  When
    /// <paramref name="distorted"/> is true, each sample is undistorted with
    /// the fixed Brown-Conrady model before applying H^-1 to the plane.
    /// </summary>
    internal static byte[] Render(bool distorted = false) =>
        RenderCore(BackgroundValue, distorted);

    /// <summary>
    /// Render the same target and H with a different uniform background.  The
    /// variant is useful for proving duplicate-session exclusion without
    /// changing marker identity or physical correspondences.
    /// </summary>
    internal static byte[] RenderWithBackground(
        byte backgroundValue,
        bool distorted = false) => RenderCore(backgroundValue, distorted);

    internal static byte[] RenderBackgroundVariant(bool distorted = false) =>
        RenderWithBackground(VariantBackgroundValue, distorted);

    internal static byte[] RenderVariant(bool distorted = false) =>
        RenderBackgroundVariant(distorted);

    /// <summary>Return a fresh uniform image for invalid-image test cases.</summary>
    internal static byte[] BlankImage(byte value = BackgroundValue)
    {
        var pixels = new byte[PixelCount];
        Array.Fill(pixels, value);
        return pixels;
    }

    /// <summary>
    /// Apply the frozen plane-to-ideal-pixel homography.  The overload with
    /// <paramref name="distorted"/> additionally applies Brown-Conrady to the
    /// ideal pixel, while the default remains the H-only expected value.
    /// </summary>
    internal static SyntheticPlanarPoint ExpectedForward(
        SyntheticPlanarPoint physical,
        bool distorted = false) =>
        ExpectedForward(physical.X, physical.Y, distorted);

    internal static SyntheticPlanarPoint ExpectedForward(
        double physicalXMillimeters,
        double physicalYMillimeters,
        bool distorted = false)
    {
        var denominator = H31 * physicalXMillimeters +
            H32 * physicalYMillimeters + H33;
        if (Math.Abs(denominator) < MinimumHomographyDenominator)
            throw new InvalidOperationException("The fixture homography has a zero projection denominator.");

        var idealX = (H11 * physicalXMillimeters + H12 * physicalYMillimeters + H13) /
            denominator;
        var idealY = (H21 * physicalXMillimeters + H22 * physicalYMillimeters + H23) /
            denominator;
        return distorted
            ? DistortPixel(idealX, idealY)
            : new SyntheticPlanarPoint(idealX, idealY);
    }

    /// <summary>
    /// Apply H^-1 to an ideal pixel.  The mathematical inverse is available
    /// for any finite pixel; callers that enforce the calibrated plane ROI
    /// should use <see cref="TryExpectedInverse(SyntheticPlanarPoint, out SyntheticPlanarPoint)"/>.
    /// </summary>
    internal static SyntheticPlanarPoint ExpectedInverse(SyntheticPlanarPoint idealPixel) =>
        ExpectedInverse(idealPixel.X, idealPixel.Y);

    internal static SyntheticPlanarPoint ExpectedInverse(double idealPixelX, double idealPixelY)
    {
        if (!double.IsFinite(idealPixelX) || !double.IsFinite(idealPixelY))
            throw new ArgumentException("The ideal pixel must contain finite coordinates.");

        // Cross-multiply the inverse projective map.  The denominator is the
        // same scalar as det(H) times the third homogeneous coordinate, so a
        // zero value is a genuine point at infinity.
        var determinant =
            H11 * (H22 * H33 - H23 * H32) -
            H12 * (H21 * H33 - H23 * H31) +
            H13 * (H21 * H32 - H22 * H31);
        if (Math.Abs(determinant) < MinimumHomographyDenominator)
            throw new InvalidOperationException("The fixture homography is singular.");

        var inverse11 = (H22 * H33 - H23 * H32) / determinant;
        var inverse12 = (H13 * H32 - H12 * H33) / determinant;
        var inverse13 = (H12 * H23 - H13 * H22) / determinant;
        var inverse21 = (H23 * H31 - H21 * H33) / determinant;
        var inverse22 = (H11 * H33 - H13 * H31) / determinant;
        var inverse23 = (H13 * H21 - H11 * H23) / determinant;
        var inverse31 = (H21 * H32 - H22 * H31) / determinant;
        var inverse32 = (H12 * H31 - H11 * H32) / determinant;
        var inverse33 = (H11 * H22 - H12 * H21) / determinant;

        var denominator = inverse31 * idealPixelX + inverse32 * idealPixelY + inverse33;
        if (Math.Abs(denominator) < MinimumHomographyDenominator)
            throw new ArgumentOutOfRangeException(nameof(idealPixelX),
                "The ideal pixel maps to infinity under the fixture homography.");

        return new SyntheticPlanarPoint(
            (inverse11 * idealPixelX + inverse12 * idealPixelY + inverse13) / denominator,
            (inverse21 * idealPixelX + inverse22 * idealPixelY + inverse23) / denominator);
    }

    /// <summary>Return true only when an H-inverted point is inside the calibrated rectangle.</summary>
    internal static bool IsWithinRoi(SyntheticPlanarPoint physical) =>
        double.IsFinite(physical.X) && double.IsFinite(physical.Y) &&
        physical.X >= RoiMinimumXMillimeters - RoiToleranceMillimeters &&
        physical.X <= RoiMaximumXMillimeters + RoiToleranceMillimeters &&
        physical.Y >= RoiMinimumYMillimeters - RoiToleranceMillimeters &&
        physical.Y <= RoiMaximumYMillimeters + RoiToleranceMillimeters;

    /// <summary>Try H^-1 and reject points outside the calibrated plane ROI.</summary>
    internal static bool TryExpectedInverse(
        SyntheticPlanarPoint idealPixel,
        out SyntheticPlanarPoint physical)
    {
        try
        {
            physical = ExpectedInverse(idealPixel);
            if (IsWithinRoi(physical))
                return true;
        }
        catch (ArgumentException)
        {
            // Rejection is the expected result for non-finite or singular input.
        }
        catch (InvalidOperationException)
        {
            // Rejection is the expected result for a singular fixture map.
        }

        physical = default;
        return false;
    }

    private static IReadOnlyList<SyntheticPlanarPoint> BuildPhysicalCorners()
    {
        var corners = new List<SyntheticPlanarPoint>(MarkerTable.Count * 4);
        foreach (var marker in MarkerTable)
            corners.AddRange(marker.PhysicalCorners);
        return corners.AsReadOnly();
    }

    private static byte[] RenderCore(byte backgroundValue, bool distorted)
    {
        var pixels = new byte[PixelCount];
        var outputIndex = 0;
        for (var y = 0; y < ImageHeight; y++)
        {
            for (var x = 0; x < ImageWidth; x++)
            {
                var intensity =
                    Sample(x - PixelSubsampleOffset, y - PixelSubsampleOffset,
                        backgroundValue, distorted) +
                    Sample(x + PixelSubsampleOffset, y - PixelSubsampleOffset,
                        backgroundValue, distorted) +
                    Sample(x - PixelSubsampleOffset, y + PixelSubsampleOffset,
                        backgroundValue, distorted) +
                    Sample(x + PixelSubsampleOffset, y + PixelSubsampleOffset,
                        backgroundValue, distorted);
                pixels[outputIndex++] = (byte)Math.Clamp(
                    (int)Math.Round(intensity / 4d, MidpointRounding.AwayFromZero), 0, 255);
            }
        }

        return pixels;
    }

    private static int Sample(
        double outputPixelX,
        double outputPixelY,
        byte backgroundValue,
        bool distorted)
    {
        var idealPixel = distorted
            ? UndistortPixel(outputPixelX, outputPixelY)
            : new SyntheticPlanarPoint(outputPixelX, outputPixelY);
        var physical = ExpectedInverse(idealPixel);

        foreach (var marker in MarkerTable)
        {
            var localX = physical.X - marker.TopLeftXMillimeters;
            var localY = physical.Y - marker.TopLeftYMillimeters;
            if (localX < 0d || localX >= MarkerSideMillimeters ||
                localY < 0d || localY >= MarkerSideMillimeters)
                continue;

            var cellSize = MarkerSideMillimeters / MarkerCellSide;
            var cellColumn = (int)Math.Floor(localX / cellSize);
            var cellRow = (int)Math.Floor(localY / cellSize);
            if ((uint)cellColumn >= MarkerCellSide || (uint)cellRow >= MarkerCellSide)
                return WhiteValue;
            if (cellColumn == 0 || cellColumn == MarkerCellSide - 1 ||
                cellRow == 0 || cellRow == MarkerCellSide - 1)
                return BlackValue;

            var bitRow = cellRow - 1;
            var bitColumn = cellColumn - 1;
            var bitIndex = bitRow * MarkerBitSideCells + bitColumn;
            var bit = bitIndex < 8
                ? (marker.CodeByte0 >> (7 - bitIndex)) & 1
                : (marker.CodeByte1 >> (15 - bitIndex)) & 1;
            return bit == 1 ? WhiteValue : BlackValue;
        }

        return backgroundValue;
    }

    private static SyntheticPlanarPoint DistortPixel(double idealPixelX, double idealPixelY)
    {
        var normalizedX = (idealPixelX - PrincipalPointX) / FocalLengthX;
        var normalizedY = (idealPixelY - PrincipalPointY) / FocalLengthY;
        var distorted = Distort(normalizedX, normalizedY);
        return new SyntheticPlanarPoint(
            FocalLengthX * distorted.X + PrincipalPointX,
            FocalLengthY * distorted.Y + PrincipalPointY);
    }

    private static SyntheticPlanarPoint UndistortPixel(double distortedPixelX, double distortedPixelY)
    {
        var targetX = (distortedPixelX - PrincipalPointX) / FocalLengthX;
        var targetY = (distortedPixelY - PrincipalPointY) / FocalLengthY;
        var undistorted = InvertDistortion(targetX, targetY);
        return new SyntheticPlanarPoint(
            FocalLengthX * undistorted.X + PrincipalPointX,
            FocalLengthY * undistorted.Y + PrincipalPointY);
    }

    private static NormalizedPoint InvertDistortion(double targetX, double targetY)
    {
        var estimateX = targetX;
        var estimateY = targetY;
        for (var iteration = 0; iteration < InverseDistortionIterations; iteration++)
        {
            var model = Distort(estimateX, estimateY);
            var residualX = model.X - targetX;
            var residualY = model.Y - targetY;
            if (Math.Abs(residualX) + Math.Abs(residualY) < 1e-14d)
                break;

            var radiusSquared = estimateX * estimateX + estimateY * estimateY;
            var radiusSquaredSquared = radiusSquared * radiusSquared;
            var radial = 1d + K1 * radiusSquared + K2 * radiusSquaredSquared +
                K3 * radiusSquaredSquared * radiusSquared;
            var radialDerivativeFactor = 2d *
                (K1 + 2d * K2 * radiusSquared + 3d * K3 * radiusSquaredSquared);
            var radialDerivativeX = estimateX * radialDerivativeFactor;
            var radialDerivativeY = estimateY * radialDerivativeFactor;

            var jacobian11 = radial + estimateX * radialDerivativeX +
                2d * P1 * estimateY + 6d * P2 * estimateX;
            var jacobian12 = estimateX * radialDerivativeY +
                2d * P1 * estimateX + 2d * P2 * estimateY;
            var jacobian21 = estimateY * radialDerivativeX +
                2d * P1 * estimateX + 2d * P2 * estimateY;
            var jacobian22 = radial + estimateY * radialDerivativeY +
                6d * P1 * estimateY + 2d * P2 * estimateX;
            var determinant = jacobian11 * jacobian22 - jacobian12 * jacobian21;
            if (Math.Abs(determinant) < MinimumJacobianDeterminant)
                break;

            var deltaX = (residualX * jacobian22 - residualY * jacobian12) / determinant;
            var deltaY = (jacobian11 * residualY - jacobian21 * residualX) / determinant;
            estimateX -= deltaX;
            estimateY -= deltaY;
        }

        return new NormalizedPoint(estimateX, estimateY);
    }

    private static NormalizedPoint Distort(double normalizedX, double normalizedY)
    {
        var radiusSquared = normalizedX * normalizedX + normalizedY * normalizedY;
        var radiusSquaredSquared = radiusSquared * radiusSquared;
        var radial = 1d + K1 * radiusSquared + K2 * radiusSquaredSquared +
            K3 * radiusSquaredSquared * radiusSquared;
        return new NormalizedPoint(
            normalizedX * radial + 2d * P1 * normalizedX * normalizedY +
            P2 * (radiusSquared + 2d * normalizedX * normalizedX),
            normalizedY * radial + P1 * (radiusSquared + 2d * normalizedY * normalizedY) +
            2d * P2 * normalizedX * normalizedY);
    }

    private readonly record struct NormalizedPoint(double X, double Y);
}
