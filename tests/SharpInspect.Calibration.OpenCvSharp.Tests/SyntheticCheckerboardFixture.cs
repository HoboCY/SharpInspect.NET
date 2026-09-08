using System;
using System.Collections.Generic;

namespace SharpInspect.Calibration.OpenCvSharp.Tests;

/// <summary>
/// A pose for the fixed synthetic checkerboard dataset.
///
/// Translation is the camera-space location of the checkerboard's (0, 0, 0)
/// corner, in millimetres.  Rotation is applied as Rz * Ry * Rx to points in
/// the board coordinate system.
/// </summary>
internal readonly record struct SyntheticCheckerboardPose(
    double RotationXDegrees,
    double RotationYDegrees,
    double RotationZDegrees,
    double TranslationXMillimeters,
    double TranslationYMillimeters,
    double TranslationZMillimeters)
{
    // Short aliases keep the pose convenient to use in numerical tests while
    // retaining unambiguous names in the frozen manifest.
    internal double RxDegrees => RotationXDegrees;
    internal double RyDegrees => RotationYDegrees;
    internal double RzDegrees => RotationZDegrees;
    internal double TxMillimeters => TranslationXMillimeters;
    internal double TyMillimeters => TranslationYMillimeters;
    internal double TzMillimeters => TranslationZMillimeters;
}

/// <summary>Pixel coordinates for one projected checkerboard corner.</summary>
internal readonly record struct SyntheticCheckerboardImagePoint(double X, double Y);

/// <summary>
/// Deterministic, dependency-free images for checkerboard intrinsic
/// calibration tests.
///
/// This fixture is deliberately independent of OpenCvSharp.  It renders the
/// inverse camera mapping using only the pinhole model, Brown-Conrady
/// distortion, rigid transforms, and System.Math.  Keep the constants, poses,
/// and rendering rule aligned with Fixtures/checkerboard-v1.json; the
/// manifest is frozen before numerical calibration is run.
/// </summary>
internal static class SyntheticCheckerboardFixture
{
    internal const string DatasetVersion = "checkerboard-v1";

    internal const int ImageWidth = 640;
    internal const int ImageHeight = 480;
    internal const int Width = ImageWidth;
    internal const int Height = ImageHeight;
    internal const int PixelCount = ImageWidth * ImageHeight;

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

    internal const int InnerCornerColumns = 9;
    internal const int InnerCornerRows = 6;
    internal const int InnerCornerCount = InnerCornerColumns * InnerCornerRows;
    internal const double SquareSizeMillimeters = 25d;

    // The ten-by-seven square board surrounds the 9-by-6 inner-corner grid by
    // one square on every side.
    internal const double BoardMinimumXMillimeters = -25d;
    internal const double BoardMaximumXMillimeters = 225d;
    internal const double BoardMinimumYMillimeters = -25d;
    internal const double BoardMaximumYMillimeters = 150d;

    internal const byte BlackValue = 0;
    internal const byte WhiteValue = 255;
    internal const byte BackgroundValue = 170;

    private const int InverseDistortionIterations = 5;
    private const double PixelSubsampleOffset = 0.25d;
    private const double MinimumPositiveDepth = 1e-9d;
    private const double MinimumJacobianDeterminant = 1e-18d;

    private static readonly IReadOnlyList<SyntheticCheckerboardPose> PoseTable =
        Array.AsReadOnly(new[]
        {
            new SyntheticCheckerboardPose(-18d, -22d, -15d, -100d, -62.5d, 680d),
            new SyntheticCheckerboardPose(18d, 22d, 15d, -100d, -62.5d, 680d),
            new SyntheticCheckerboardPose(-26d, 16d, 24d, -115d, -50d, 720d),
            new SyntheticCheckerboardPose(26d, -16d, -24d, -85d, -75d, 720d),
            new SyntheticCheckerboardPose(-15d, 28d, -20d, -65d, -85d, 760d),
            new SyntheticCheckerboardPose(15d, -28d, 20d, -135d, -40d, 760d),
            new SyntheticCheckerboardPose(-30d, -18d, 12d, -125d, -70d, 820d),
            new SyntheticCheckerboardPose(30d, 18d, -12d, -75d, -55d, 820d),
            new SyntheticCheckerboardPose(-21d, 24d, -27d, -145d, -35d, 860d),
            new SyntheticCheckerboardPose(21d, -24d, 27d, -55d, -90d, 860d),
            new SyntheticCheckerboardPose(-17d, -30d, 18d, -160d, -45d, 900d),
            new SyntheticCheckerboardPose(17d, 30d, -18d, -40d, -80d, 900d),
            new SyntheticCheckerboardPose(-28d, 20d, -15d, -100d, -72.5d, 640d),
            new SyntheticCheckerboardPose(28d, -20d, 15d, -100d, -30d, 640d),
            new SyntheticCheckerboardPose(-19d, 26d, 30d, -70d, -70d, 700d),
            new SyntheticCheckerboardPose(19d, -26d, -30d, -130d, -55d, 700d),
            new SyntheticCheckerboardPose(-25d, -15d, -22d, -155d, -62.5d, 780d),
            new SyntheticCheckerboardPose(25d, 15d, 22d, -45d, -62.5d, 780d),
            new SyntheticCheckerboardPose(-16d, 19d, -29d, -100d, -100d, 880d),
            new SyntheticCheckerboardPose(16d, -19d, 29d, -100d, -25d, 880d),
        });

    // Distorted image coordinates depend only on the camera and the fixed
    // four subpixel locations, so cache their inverse rays once.  This keeps
    // rendering twenty poses practical without changing any rendered sample.
    private static readonly Lazy<NormalizedPoint[]> DistortedSampleRays =
        new(BuildDistortedSampleRays);

    /// <summary>Exactly twenty fixed, varied checkerboard poses.</summary>
    internal static IReadOnlyList<SyntheticCheckerboardPose> Poses => PoseTable;

    /// <summary>
    /// Render one row-major Mono8 image.  Pixel coordinates are measured at
    /// integer pixel centres, matching OpenCV's calibration convention.
    /// Each pixel is the average of four fixed subpixel samples at +/-0.25.
    /// </summary>
    internal static byte[] Render(int index, bool distorted = true)
    {
        var pose = GetPose(index);
        var rotation = RotationMatrix.FromEulerDegrees(
            pose.RotationXDegrees, pose.RotationYDegrees, pose.RotationZDegrees);
        var boardOrigin = rotation.TransposeMultiply(new Vector3(
            -pose.TranslationXMillimeters,
            -pose.TranslationYMillimeters,
            -pose.TranslationZMillimeters));
        var pixels = new byte[PixelCount];
        var inverseRays = distorted ? DistortedSampleRays.Value : null;
        var outputIndex = 0;

        for (var y = 0; y < ImageHeight; y++)
        {
            for (var x = 0; x < ImageWidth; x++)
            {
                var sampleIndex = outputIndex * 4;
                var topLeft = inverseRays is null
                    ? ToNormalizedPoint(x - PixelSubsampleOffset, y - PixelSubsampleOffset)
                    : inverseRays[sampleIndex];
                var topRight = inverseRays is null
                    ? ToNormalizedPoint(x + PixelSubsampleOffset, y - PixelSubsampleOffset)
                    : inverseRays[sampleIndex + 1];
                var bottomLeft = inverseRays is null
                    ? ToNormalizedPoint(x - PixelSubsampleOffset, y + PixelSubsampleOffset)
                    : inverseRays[sampleIndex + 2];
                var bottomRight = inverseRays is null
                    ? ToNormalizedPoint(x + PixelSubsampleOffset, y + PixelSubsampleOffset)
                    : inverseRays[sampleIndex + 3];
                var intensity =
                    Sample(rotation, boardOrigin, topLeft) +
                    Sample(rotation, boardOrigin, topRight) +
                    Sample(rotation, boardOrigin, bottomLeft) +
                    Sample(rotation, boardOrigin, bottomRight);
                pixels[outputIndex++] = (byte)Math.Clamp(
                    (int)Math.Round(intensity / 4d, MidpointRounding.AwayFromZero), 0, 255);
            }
        }

        return pixels;
    }

    /// <summary>
    /// Return the exact projection of the 9-by-6 inner corners in row-major
    /// order.  These coordinates use the same independent projection math as
    /// the renderer and are suitable as corner-extraction reference points.
    /// </summary>
    internal static IReadOnlyList<SyntheticCheckerboardImagePoint> ProjectCorners(
        int index, bool distorted = true)
    {
        var pose = GetPose(index);
        var rotation = RotationMatrix.FromEulerDegrees(
            pose.RotationXDegrees, pose.RotationYDegrees, pose.RotationZDegrees);
        var corners = new SyntheticCheckerboardImagePoint[InnerCornerCount];
        var cornerIndex = 0;

        for (var row = 0; row < InnerCornerRows; row++)
        {
            for (var column = 0; column < InnerCornerColumns; column++)
            {
                corners[cornerIndex++] = ProjectPoint(
                    rotation,
                    pose,
                    column * SquareSizeMillimeters,
                    row * SquareSizeMillimeters,
                    distorted);
            }
        }

        return corners;
    }

    /// <summary>Alias for callers that prefer the noun-first form.</summary>
    internal static IReadOnlyList<SyntheticCheckerboardImagePoint> ProjectedCorners(
        int index, bool distorted = true) => ProjectCorners(index, distorted);

    /// <summary>Return a fresh uniform image for invalid-image test cases.</summary>
    internal static byte[] BlankImage(byte value = BackgroundValue)
    {
        var pixels = new byte[PixelCount];
        Array.Fill(pixels, value);
        return pixels;
    }

    private static SyntheticCheckerboardPose GetPose(int index)
    {
        if ((uint)index >= (uint)PoseTable.Count)
            throw new ArgumentOutOfRangeException(nameof(index), index,
                "The checkerboard fixture contains exactly twenty poses.");
        return PoseTable[index];
    }

    private static SyntheticCheckerboardImagePoint ProjectPoint(
        RotationMatrix rotation,
        SyntheticCheckerboardPose pose,
        double boardX,
        double boardY,
        bool distorted)
    {
        var cameraPoint = rotation.Multiply(new Vector3(boardX, boardY, 0d));
        var cameraZ = cameraPoint.Z + pose.TranslationZMillimeters;
        if (!(cameraZ > MinimumPositiveDepth))
            throw new InvalidOperationException("A checkerboard corner is behind the camera.");

        var normalizedX = (cameraPoint.X + pose.TranslationXMillimeters) / cameraZ;
        var normalizedY = (cameraPoint.Y + pose.TranslationYMillimeters) / cameraZ;
        var projected = distorted
            ? Distort(normalizedX, normalizedY)
            : new NormalizedPoint(normalizedX, normalizedY);
        return new SyntheticCheckerboardImagePoint(
            FocalLengthX * projected.X + PrincipalPointX,
            FocalLengthY * projected.Y + PrincipalPointY);
    }

    private static NormalizedPoint[] BuildDistortedSampleRays()
    {
        var rays = new NormalizedPoint[PixelCount * 4];
        var rayIndex = 0;
        for (var y = 0; y < ImageHeight; y++)
        {
            for (var x = 0; x < ImageWidth; x++)
            {
                rays[rayIndex++] = InvertDistortion(
                    ToNormalizedPoint(x - PixelSubsampleOffset, y - PixelSubsampleOffset));
                rays[rayIndex++] = InvertDistortion(
                    ToNormalizedPoint(x + PixelSubsampleOffset, y - PixelSubsampleOffset));
                rays[rayIndex++] = InvertDistortion(
                    ToNormalizedPoint(x - PixelSubsampleOffset, y + PixelSubsampleOffset));
                rays[rayIndex++] = InvertDistortion(
                    ToNormalizedPoint(x + PixelSubsampleOffset, y + PixelSubsampleOffset));
            }
        }

        return rays;
    }

    private static NormalizedPoint ToNormalizedPoint(double pixelX, double pixelY) =>
        new(
            (pixelX - PrincipalPointX) / FocalLengthX,
            (pixelY - PrincipalPointY) / FocalLengthY);

    private static byte Sample(
        RotationMatrix rotation,
        Vector3 boardOrigin,
        NormalizedPoint normalizedUndistorted)
    {
        var boardDirection = rotation.TransposeMultiply(new Vector3(
            normalizedUndistorted.X,
            normalizedUndistorted.Y,
            1d));
        if (Math.Abs(boardDirection.Z) < MinimumPositiveDepth)
            return BackgroundValue;

        // Transform the camera ray into board coordinates and intersect its
        // z=0 plane.  The origin is R^T * (-t), so no mutable pose state is
        // needed while rendering a row of pixels.
        var distance = -boardOrigin.Z / boardDirection.Z;
        if (!(distance > MinimumPositiveDepth))
            return BackgroundValue;

        var boardX = boardOrigin.X + distance * boardDirection.X;
        var boardY = boardOrigin.Y + distance * boardDirection.Y;
        if (boardX < BoardMinimumXMillimeters || boardX >= BoardMaximumXMillimeters ||
            boardY < BoardMinimumYMillimeters || boardY >= BoardMaximumYMillimeters)
            return BackgroundValue;

        var squareColumn = (int)Math.Floor(
            (boardX - BoardMinimumXMillimeters) / SquareSizeMillimeters);
        var squareRow = (int)Math.Floor(
            (boardY - BoardMinimumYMillimeters) / SquareSizeMillimeters);
        // The square at the minimum x/y board bounds is white.
        return ((squareColumn + squareRow) & 1) == 0 ? WhiteValue : BlackValue;
    }

    private static NormalizedPoint InvertDistortion(NormalizedPoint target)
    {
        var estimateX = target.X;
        var estimateY = target.Y;

        for (var iteration = 0; iteration < InverseDistortionIterations; iteration++)
        {
            var model = Distort(estimateX, estimateY);
            var residualX = model.X - target.X;
            var residualY = model.Y - target.Y;
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

    private readonly record struct Vector3(double X, double Y, double Z);

    private readonly struct RotationMatrix
    {
        private RotationMatrix(
            double m11, double m12, double m13,
            double m21, double m22, double m23,
            double m31, double m32, double m33)
        {
            M11 = m11;
            M12 = m12;
            M13 = m13;
            M21 = m21;
            M22 = m22;
            M23 = m23;
            M31 = m31;
            M32 = m32;
            M33 = m33;
        }

        private double M11 { get; }
        private double M12 { get; }
        private double M13 { get; }
        private double M21 { get; }
        private double M22 { get; }
        private double M23 { get; }
        private double M31 { get; }
        private double M32 { get; }
        private double M33 { get; }

        internal static RotationMatrix FromEulerDegrees(
            double rotationXDegrees,
            double rotationYDegrees,
            double rotationZDegrees)
        {
            var x = rotationXDegrees * Math.PI / 180d;
            var y = rotationYDegrees * Math.PI / 180d;
            var z = rotationZDegrees * Math.PI / 180d;
            var sinX = Math.Sin(x);
            var cosX = Math.Cos(x);
            var sinY = Math.Sin(y);
            var cosY = Math.Cos(y);
            var sinZ = Math.Sin(z);
            var cosZ = Math.Cos(z);

            // Rz * Ry * Rx.
            return new RotationMatrix(
                cosZ * cosY,
                cosZ * sinY * sinX - sinZ * cosX,
                cosZ * sinY * cosX + sinZ * sinX,
                sinZ * cosY,
                sinZ * sinY * sinX + cosZ * cosX,
                sinZ * sinY * cosX - cosZ * sinX,
                -sinY,
                cosY * sinX,
                cosY * cosX);
        }

        internal Vector3 Multiply(Vector3 point) => new(
            M11 * point.X + M12 * point.Y + M13 * point.Z,
            M21 * point.X + M22 * point.Y + M23 * point.Z,
            M31 * point.X + M32 * point.Y + M33 * point.Z);

        internal Vector3 TransposeMultiply(Vector3 point) => new(
            M11 * point.X + M21 * point.Y + M31 * point.Z,
            M12 * point.X + M22 * point.Y + M32 * point.Z,
            M13 * point.X + M23 * point.Y + M33 * point.Z);
    }
}
