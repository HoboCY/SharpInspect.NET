using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Calibration.OpenCvSharp;

/// <summary>First-order physical scale of the image-to-plane mapping at one pixel.</summary>
public readonly record struct PlanarLocalScale(
    double J11,
    double J12,
    double J21,
    double J22,
    double MinimumMillimetersPerPixel,
    double MaximumMillimetersPerPixel,
    double AreaSquareMillimetersPerSquarePixel);

/// <summary>One immutable raw correspondence and its two directional residuals.</summary>
public sealed class PlanarPointResidual
{
    public PlanarPointResidual(
        int markerId,
        int cornerIndex,
        PlanarPoint observedPixels,
        PlanarPoint physicalMillimeters,
        PlanarPoint imageToPlaneResidualMillimeters,
        PlanarPoint planeToImageResidualPixels)
    {
        if (markerId is < 0 or > 49)
            throw new ArgumentException("PlanarResidualMarkerIdInvalid", nameof(markerId));
        if (cornerIndex is < 0 or > 3)
            throw new ArgumentException("PlanarResidualCornerIndexInvalid", nameof(cornerIndex));
        ValidateObservedPoint(observedPixels, nameof(observedPixels));
        ValidatePhysicalPoint(physicalMillimeters, nameof(physicalMillimeters));
        ValidateResidualPoint(imageToPlaneResidualMillimeters, nameof(imageToPlaneResidualMillimeters));
        ValidateResidualPoint(planeToImageResidualPixels, nameof(planeToImageResidualPixels));

        MarkerId = markerId;
        CornerIndex = cornerIndex;
        ObservedPixels = observedPixels;
        PhysicalMillimeters = physicalMillimeters;
        ImageToPlaneResidualMillimeters = imageToPlaneResidualMillimeters;
        PlaneToImageResidualPixels = planeToImageResidualPixels;
    }

    public int MarkerId { get; }
    public int CornerIndex { get; }
    public PlanarPoint ObservedPixels { get; }
    public PlanarPoint PhysicalMillimeters { get; }
    public PlanarPoint ImageToPlaneResidualMillimeters { get; }
    public PlanarPoint PlaneToImageResidualPixels { get; }

    public double ImageToPlaneResidualMagnitudeMillimeters =>
        Magnitude(ImageToPlaneResidualMillimeters);

    public double PlaneToImageResidualMagnitudePixels =>
        Magnitude(PlaneToImageResidualPixels);

    private static void ValidateObservedPoint(PlanarPoint point, string parameterName)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0d || point.Y < 0d)
            throw new ArgumentException("PlanarResidualPointInvalid", parameterName);
    }

    private static void ValidatePhysicalPoint(PlanarPoint point, string parameterName)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            Math.Abs(point.X) > 1_000_000d || Math.Abs(point.Y) > 1_000_000d)
            throw new ArgumentException("PlanarResidualPointInvalid", parameterName);
    }

    private static void ValidateResidualPoint(PlanarPoint point, string parameterName)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            !double.IsFinite(Magnitude(point)))
            throw new ArgumentException("PlanarResidualPointInvalid", parameterName);
    }

    private static double Magnitude(PlanarPoint point) =>
        Math.Sqrt(point.X * point.X + point.Y * point.Y);
}

/// <summary>
/// Mathematical evidence for one planar computation. This type carries no
/// acceptance or publication decision.
/// </summary>
public sealed class PlanarHomographyEvidence
{
    public const int MaximumResidualCount = 200;
    public const int MaximumMissingMarkerCount = 50;

    public PlanarHomographyEvidence(
        string inputPayloadHash,
        Guid frameId,
        string sourceHash,
        double constraintRankRatio,
        double inverseClosureError,
        IEnumerable<PlanarPointResidual> residuals,
        IEnumerable<int> missingMarkerIds)
    {
        InputPayloadHash = RequireHash(inputPayloadHash, nameof(inputPayloadHash),
            "PlanarEvidenceInputHashInvalid");
        if (frameId == Guid.Empty)
            throw new ArgumentException("PlanarEvidenceFrameIdInvalid", nameof(frameId));
        FrameId = frameId;
        SourceHash = RequireHash(sourceHash, nameof(sourceHash), "PlanarEvidenceSourceHashInvalid");
        if (!double.IsFinite(constraintRankRatio) || constraintRankRatio <= 0d || constraintRankRatio > 1d)
            throw new ArgumentException("PlanarEvidenceConstraintRankInvalid", nameof(constraintRankRatio));
        if (!double.IsFinite(inverseClosureError) || inverseClosureError < 0d)
            throw new ArgumentException("PlanarEvidenceInverseClosureInvalid", nameof(inverseClosureError));
        ConstraintRankRatio = constraintRankRatio == 0d ? 0d : constraintRankRatio;
        InverseClosureError = inverseClosureError == 0d ? 0d : inverseClosureError;

        ArgumentNullException.ThrowIfNull(residuals);
        var copiedResiduals = residuals.Take(MaximumResidualCount + 1).ToArray();
        if (copiedResiduals.Length < 4 || copiedResiduals.Length > MaximumResidualCount ||
            copiedResiduals.Any(value => value is null))
            throw new ArgumentException("PlanarEvidenceResidualsInvalid", nameof(residuals));
        if (copiedResiduals.Select(value => (value.MarkerId, value.CornerIndex)).Distinct().Count() !=
            copiedResiduals.Length)
            throw new ArgumentException("PlanarEvidenceResidualDuplicate", nameof(residuals));
        var markerGroups = copiedResiduals.GroupBy(value => value.MarkerId).ToArray();
        if (markerGroups.Any(group => group.Count() != 4 ||
                !group.Select(value => value.CornerIndex).OrderBy(value => value)
                    .SequenceEqual(new[] { 0, 1, 2, 3 })))
            throw new ArgumentException("PlanarEvidenceResidualCoverageInvalid", nameof(residuals));
        if (!copiedResiduals.SequenceEqual(copiedResiduals.OrderBy(value => value.MarkerId)
                .ThenBy(value => value.CornerIndex)))
            throw new ArgumentException("PlanarEvidenceResidualOrderInvalid", nameof(residuals));
        Residuals = Array.AsReadOnly(copiedResiduals);

        ArgumentNullException.ThrowIfNull(missingMarkerIds);
        var copiedMissing = missingMarkerIds.Take(MaximumMissingMarkerCount + 1).ToArray();
        if (copiedMissing.Length > MaximumMissingMarkerCount ||
            copiedMissing.Any(markerId => markerId is < 0 or > 49) ||
            copiedMissing.Distinct().Count() != copiedMissing.Length ||
            copiedMissing.Any(markerId => copiedResiduals.Any(residual => residual.MarkerId == markerId)) ||
            !copiedMissing.SequenceEqual(copiedMissing.OrderBy(markerId => markerId)))
            throw new ArgumentException("PlanarEvidenceMissingMarkersInvalid", nameof(missingMarkerIds));
        MissingMarkerIds = Array.AsReadOnly(copiedMissing);

        var millimeterSquares = copiedResiduals.Select(value =>
            SquareMagnitude(value.ImageToPlaneResidualMillimeters)).ToArray();
        var pixelSquares = copiedResiduals.Select(value =>
            SquareMagnitude(value.PlaneToImageResidualPixels)).ToArray();
        RmsMillimeters = RootMeanSquare(millimeterSquares);
        MaxMillimeters = MaximumMagnitude(millimeterSquares);
        RmsPixels = RootMeanSquare(pixelSquares);
        MaxPixels = MaximumMagnitude(pixelSquares);
    }

    public string InputPayloadHash { get; }
    public Guid FrameId { get; }
    public string SourceHash { get; }
    public double ConstraintRankRatio { get; }
    public double InverseClosureError { get; }
    public ReadOnlyCollection<PlanarPointResidual> Residuals { get; }
    public ReadOnlyCollection<int> MissingMarkerIds { get; }
    public double RmsMillimeters { get; }
    public double MaxMillimeters { get; }
    public double RmsPixels { get; }
    public double MaxPixels { get; }

    private static double SquareMagnitude(PlanarPoint point) =>
        point.X * point.X + point.Y * point.Y;

    private static double RootMeanSquare(IReadOnlyList<double> squares)
    {
        if (squares.Count == 0)
            return 0d;
        var sum = 0d;
        foreach (var square in squares)
        {
            sum += square;
            if (!double.IsFinite(sum))
                throw new ArgumentException("PlanarEvidenceResidualMagnitudeInvalid");
        }
        var result = Math.Sqrt(sum / squares.Count);
        if (!double.IsFinite(result))
            throw new ArgumentException("PlanarEvidenceResidualMagnitudeInvalid");
        return result;
    }

    private static double MaximumMagnitude(IReadOnlyList<double> squares)
    {
        if (squares.Count == 0)
            return 0d;
        var result = Math.Sqrt(squares.Max());
        if (!double.IsFinite(result))
            throw new ArgumentException("PlanarEvidenceResidualMagnitudeInvalid");
        return result;
    }

    internal static string RequireHash(string value, string parameterName, string reason)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')))
            throw new ArgumentException(reason, parameterName);
        return value;
    }
}

/// <summary>
/// A validated projective mapping for one exact planar input. The constructor
/// takes image-to-plane coefficients; the plane-to-image inverse is calculated
/// and retained beside them.
/// </summary>
public sealed class PlanarHomographyCoefficients
{
    public const int MatrixElementCount = 9;
    public const int MaximumHullPointCount = 1024;

    // .NET's double.Epsilon is the smallest subnormal, not machine epsilon.
    // Keep the relative projective guard explicit and scale-independent.
    private const double MachineEpsilon = 2.2204460492503131e-16d;
    private const double RelativeRankGuard = 64d * MachineEpsilon;
    private const double RelativeDenominatorGuard = 64d * MachineEpsilon;

    private readonly double[] _imageToPlane;
    private readonly double[] _planeToImage;
    private readonly PlanarPoint[] _imageHull;
    private readonly PlanarPoint[] _physicalHull;

    public PlanarHomographyCoefficients(
        string inputPayloadHash,
        PlanarHomographyInput input,
        IEnumerable<double> imageToPlane,
        IEnumerable<PlanarPoint> imageHull,
        IEnumerable<PlanarPoint> physicalHull)
    {
        InputPayloadHash = PlanarHomographyEvidence.RequireHash(inputPayloadHash,
            nameof(inputPayloadHash), "PlanarCoefficientInputHashInvalid");
        Input = input ?? throw new ArgumentNullException(nameof(input));
        var canonicalInputHash = new PlanarHomographyInputCodec().EncodePayload(Input).ContentHash;
        if (!string.Equals(InputPayloadHash, canonicalInputHash, StringComparison.Ordinal))
            throw new ArgumentException("PlanarCoefficientInputHashMismatch", nameof(inputPayloadHash));

        _imageToPlane = NormalizeMatrix(imageToPlane, nameof(imageToPlane));
        _planeToImage = InvertMatrix(_imageToPlane);
        _imageHull = CopyHull(imageHull, nameof(imageHull), requireImageRange: true,
            Input.ImageWidth, Input.ImageHeight);
        _physicalHull = CopyHull(physicalHull, nameof(physicalHull), requireImageRange: false, 0, 0);

        ValidateHorizon(_imageToPlane, _imageHull, "PlanarImageToPlaneHorizonInvalid");
        ValidateHorizon(_planeToImage, _physicalHull, "PlanarPlaneToImageHorizonInvalid");

        ImageToPlane = Array.AsReadOnly((double[])_imageToPlane.Clone());
        PlaneToImage = Array.AsReadOnly((double[])_planeToImage.Clone());
        ImageHull = Array.AsReadOnly((PlanarPoint[])_imageHull.Clone());
        PhysicalHull = Array.AsReadOnly((PlanarPoint[])_physicalHull.Clone());
    }

    public string InputPayloadHash { get; }
    public PlanarHomographyInput Input { get; }
    public ReadOnlyCollection<double> ImageToPlane { get; }
    public ReadOnlyCollection<double> PlaneToImage { get; }
    public ReadOnlyCollection<PlanarPoint> ImageHull { get; }
    public ReadOnlyCollection<PlanarPoint> PhysicalHull { get; }

    /// <summary>Map an image pixel to the physical plane when both values are in-domain.</summary>
    public bool TryImageToPlane(PlanarPoint imagePixel, out PlanarPoint physicalMillimeters)
    {
        if (!PlanarGeometry.Contains(_imageHull, imagePixel) ||
            !PlanarGeometry.TryProject(_imageToPlane, imagePixel, out physicalMillimeters))
        {
            physicalMillimeters = default;
            return false;
        }

        return true;
    }

    /// <summary>Map a physical plane point to image pixels when both values are in-domain.</summary>
    public bool TryPlaneToImage(PlanarPoint physicalMillimeters, out PlanarPoint imagePixel)
    {
        if (!PlanarGeometry.Contains(_physicalHull, physicalMillimeters) ||
            !PlanarGeometry.TryProject(_planeToImage, physicalMillimeters, out imagePixel))
        {
            imagePixel = default;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Return the full image-to-plane Jacobian and its singular scales at one
    /// image pixel. The point must be inside the retained image hull.
    /// </summary>
    public PlanarLocalScale GetLocalScale(PlanarPoint imagePixel)
    {
        if (!PlanarGeometry.Contains(_imageHull, imagePixel))
            throw new ArgumentException("PlanarLocalScaleOutsideImageHull", nameof(imagePixel));

        var a = _imageToPlane[0];
        var b = _imageToPlane[1];
        var c = _imageToPlane[2];
        var d = _imageToPlane[3];
        var e = _imageToPlane[4];
        var f = _imageToPlane[5];
        var g = _imageToPlane[6];
        var h = _imageToPlane[7];
        var i = _imageToPlane[8];
        var denominator = g * imagePixel.X + h * imagePixel.Y + i;
        var denominatorScale = Math.Abs(g * imagePixel.X) +
            Math.Abs(h * imagePixel.Y) + Math.Abs(i);
        if (!IsSafeDenominator(denominator, denominatorScale))
            throw new ArgumentException("PlanarLocalScaleHorizonInvalid", nameof(imagePixel));

        var numeratorX = a * imagePixel.X + b * imagePixel.Y + c;
        var numeratorY = d * imagePixel.X + e * imagePixel.Y + f;
        var denominatorSquared = denominator * denominator;
        var j11 = (a * denominator - g * numeratorX) / denominatorSquared;
        var j12 = (b * denominator - h * numeratorX) / denominatorSquared;
        var j21 = (d * denominator - g * numeratorY) / denominatorSquared;
        var j22 = (e * denominator - h * numeratorY) / denominatorSquared;
        if (new[] { j11, j12, j21, j22 }.Any(value => !double.IsFinite(value)))
            throw new ArgumentException("PlanarLocalScaleInvalid", nameof(imagePixel));

        // Scale the Jacobian before forming J^T J. This avoids overflow for a
        // safe point close to a projective horizon and avoids trace-
        // discriminant cancellation for a strongly anisotropic mapping.
        var jacobianScale = Math.Max(Math.Max(Math.Abs(j11), Math.Abs(j12)),
            Math.Max(Math.Abs(j21), Math.Abs(j22)));
        if (!double.IsFinite(jacobianScale) || jacobianScale <= 0d)
            throw new ArgumentException("PlanarLocalScaleSingular", nameof(imagePixel));
        var n11 = j11 / jacobianScale;
        var n12 = j12 / jacobianScale;
        var n21 = j21 / jacobianScale;
        var n22 = j22 / jacobianScale;
        var columnOneSquared = n11 * n11 + n21 * n21;
        var columnTwoSquared = n12 * n12 + n22 * n22;
        var cross = n11 * n12 + n21 * n22;
        var trace = columnOneSquared + columnTwoSquared;
        var discriminant = Math.Sqrt(Math.Max(0d,
            (columnOneSquared - columnTwoSquared) * (columnOneSquared - columnTwoSquared) +
            4d * cross * cross));
        var maximumSquared = (trace + discriminant) / 2d;
        var maximumNormalized = Math.Sqrt(Math.Max(0d, maximumSquared));
        var determinantNormalized = Math.Abs(n11 * n22 - n12 * n21);
        if (!double.IsFinite(maximumNormalized) || maximumNormalized <= 0d ||
            !double.IsFinite(determinantNormalized) || determinantNormalized <= 0d)
            throw new ArgumentException("PlanarLocalScaleInvalid", nameof(imagePixel));

        var minimumNormalized = determinantNormalized / maximumNormalized;
        var minimum = jacobianScale * minimumNormalized;
        var maximum = jacobianScale * maximumNormalized;
        var area = minimum * maximum;
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) ||
            !double.IsFinite(area) || minimum <= 0d || maximum < minimum || area <= 0d)
            throw new ArgumentException("PlanarLocalScaleInvalid", nameof(imagePixel));

        return new PlanarLocalScale(j11, j12, j21, j22, minimum, maximum, area);
    }

    internal static double[] NormalizeMatrix(IEnumerable<double> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var matrix = values.Take(MatrixElementCount + 1).ToArray();
        if (matrix.Length != MatrixElementCount || matrix.Any(value => !double.IsFinite(value)))
            throw new ArgumentException("PlanarCoefficientMatrixInvalid", parameterName);

        var maximum = matrix.Max(value => Math.Abs(value));
        if (!double.IsFinite(maximum) || maximum <= 0d)
            throw new ArgumentException("PlanarCoefficientMatrixRankInvalid", parameterName);

        // Projective matrices are scale-equivalent. Normalize by maxAbs and
        // make the first coefficient attaining that maximum positive. This
        // supports valid h33=0 matrices and gives one deterministic byte form.
        var firstMaximumIndex = Array.FindIndex(matrix, value => Math.Abs(value) == maximum);
        var sign = matrix[firstMaximumIndex] < 0d ? -1d : 1d;
        var normalized = matrix.Select(value =>
            CanonicalZero(sign * value / maximum)).ToArray();
        if (normalized.Any(value => !double.IsFinite(value)))
            throw new ArgumentException("PlanarCoefficientMatrixInvalid", parameterName);
        ValidateRank(normalized, parameterName);
        return normalized;
    }

    private static void ValidateRank(IReadOnlyList<double> matrix, string parameterName)
    {
        var determinant = Determinant(matrix);
        var expansionScale = DeterminantExpansionScale(matrix);
        if (!double.IsFinite(determinant) || !double.IsFinite(expansionScale) ||
            expansionScale <= 0d || Math.Abs(determinant) / expansionScale <= RelativeRankGuard)
            throw new ArgumentException("PlanarCoefficientMatrixRankInvalid", parameterName);
    }

    internal static double[] InvertMatrix(IReadOnlyList<double> matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (matrix.Count != MatrixElementCount || matrix.Any(value => !double.IsFinite(value)))
            throw new ArgumentException("PlanarCoefficientMatrixInvalid", nameof(matrix));
        var normalized = NormalizeMatrix(matrix, nameof(matrix));
        // The adjugate is projectively equivalent to the inverse. Avoid
        // dividing by a very small determinant before normalizing, which can
        // overflow even when the projective inverse is finite.
        var inverse = new[]
        {
            normalized[4] * normalized[8] - normalized[5] * normalized[7],
            normalized[2] * normalized[7] - normalized[1] * normalized[8],
            normalized[1] * normalized[5] - normalized[2] * normalized[4],
            normalized[5] * normalized[6] - normalized[3] * normalized[8],
            normalized[0] * normalized[8] - normalized[2] * normalized[6],
            normalized[2] * normalized[3] - normalized[0] * normalized[5],
            normalized[3] * normalized[7] - normalized[4] * normalized[6],
            normalized[1] * normalized[6] - normalized[0] * normalized[7],
            normalized[0] * normalized[4] - normalized[1] * normalized[3],
        };
        return NormalizeMatrix(inverse, nameof(matrix));
    }

    private static double Determinant(IReadOnlyList<double> matrix) =>
        matrix[0] * (matrix[4] * matrix[8] - matrix[5] * matrix[7]) -
        matrix[1] * (matrix[3] * matrix[8] - matrix[5] * matrix[6]) +
        matrix[2] * (matrix[3] * matrix[7] - matrix[4] * matrix[6]);

    private static double DeterminantExpansionScale(IReadOnlyList<double> matrix)
    {
        // Include all six products before subtraction. Taking absolute values
        // only after forming the minors hides cancellation inside a minor.
        return Math.Abs(matrix[0] * matrix[4] * matrix[8]) +
            Math.Abs(matrix[0] * matrix[5] * matrix[7]) +
            Math.Abs(matrix[1] * matrix[3] * matrix[8]) +
            Math.Abs(matrix[1] * matrix[5] * matrix[6]) +
            Math.Abs(matrix[2] * matrix[3] * matrix[7]) +
            Math.Abs(matrix[2] * matrix[4] * matrix[6]);
    }

    private static double CanonicalZero(double value) => value == 0d ? 0d : value;

    private static bool IsSafeDenominator(double denominator, double scale)
    {
        if (!double.IsFinite(denominator) || !double.IsFinite(scale) || scale <= 0d)
            return false;
        return Math.Abs(denominator) / scale > RelativeDenominatorGuard;
    }

    private static PlanarPoint[] CopyHull(
        IEnumerable<PlanarPoint> values,
        string parameterName,
        bool requireImageRange,
        int imageWidth,
        int imageHeight)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var supplied = values.Take(MaximumHullPointCount + 1).ToArray();
        if (supplied.Length > MaximumHullPointCount || !PlanarGeometry.IsStrictConvex(supplied))
            throw new ArgumentException("PlanarHullInvalid", parameterName);
        var hull = PlanarGeometry.NormalizeCcwhull(supplied);
        if (requireImageRange && hull.Any(point => point.X < 0d || point.X >= imageWidth ||
                point.Y < 0d || point.Y >= imageHeight))
            throw new ArgumentException("PlanarImageHullOutOfRange", parameterName);
        if (!requireImageRange && hull.Any(point => Math.Abs(point.X) > 1_000_000d ||
                Math.Abs(point.Y) > 1_000_000d))
            throw new ArgumentException("PlanarPhysicalHullOutOfRange", parameterName);
        return hull;
    }

    private static void ValidateHorizon(
        IReadOnlyList<double> matrix,
        IReadOnlyList<PlanarPoint> domain,
        string reason)
    {
        var signs = domain.Select(point =>
            matrix[6] * point.X + matrix[7] * point.Y + matrix[8]).ToArray();
        var scales = domain.Select(point => Math.Abs(matrix[6] * point.X) +
            Math.Abs(matrix[7] * point.Y) + Math.Abs(matrix[8])).ToArray();
        if (signs.Where((value, index) => !IsSafeDenominator(value, scales[index])).Any())
            throw new ArgumentException(reason);
        var positive = signs[0] > 0d;
        if (signs.Any(value => (value > 0d) != positive))
            throw new ArgumentException(reason);
    }
}

/// <summary>Strict little-endian v1 codecs for planar coefficients and evidence.</summary>
public static class PlanarHomographyResultCodec
{
    private const int Version = 1;
    private const int HashBytes = 32;
    private const int MaximumInputBytes = 64 * 1024;

    public static CalibrationCoefficientPayload EncodeCoefficients(
        PlanarHomographyCoefficients value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var inputBytes = new PlanarHomographyInputCodec().Encode(value.Input).ToArray();
        return new(PlanarHomographyContracts.Coefficients, Write(writer =>
        {
            writer.Write(Version);
            writer.Write(Convert.FromHexString(value.InputPayloadHash));
            writer.Write(inputBytes.Length);
            writer.Write(inputBytes);
            WriteMatrix(writer, value.ImageToPlane);
            WriteMatrix(writer, value.PlaneToImage);
            WriteHull(writer, value.ImageHull);
            WriteHull(writer, value.PhysicalHull);
        }));
    }

    public static PlanarHomographyCoefficients DecodeCoefficients(
        CalibrationCoefficientPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Format != PlanarHomographyContracts.Coefficients)
            throw new ArgumentException("PlanarCoefficientContractMismatch", nameof(payload));
        var supplied = payload.GetBytes();
        try
        {
            using var reader = Reader(supplied);
            if (reader.ReadInt32() != Version)
                throw new ArgumentException("PlanarCoefficientVersionUnsupported");
            var inputHash = ReadHash(reader, "PlanarCoefficientInputHashInvalid");
            var inputBytes = ReadBoundedBytes(reader, 1, MaximumInputBytes,
                "PlanarCoefficientInputPayloadInvalid");
            var inputCodec = new PlanarHomographyInputCodec();
            var input = inputCodec.Decode(inputBytes);
            var canonicalInput = inputCodec.EncodePayload(input);
            if (!string.Equals(inputHash, canonicalInput.ContentHash, StringComparison.Ordinal) ||
                !canonicalInput.GetBytes().AsSpan().SequenceEqual(inputBytes))
                throw new ArgumentException("PlanarCoefficientInputBindingInvalid");

            var imageToPlane = ReadMatrix(reader);
            var planeToImage = ReadMatrix(reader);
            var imageHull = ReadHull(reader);
            var physicalHull = ReadHull(reader);
            RequireEnd(reader);

            var result = new PlanarHomographyCoefficients(inputHash, input,
                imageToPlane, imageHull, physicalHull);
            if (!result.PlaneToImage.SequenceEqual(planeToImage))
                throw new ArgumentException("PlanarCoefficientInverseMismatch");
            RequireCanonical(supplied, EncodeCoefficients(result).GetBytes(),
                "PlanarCoefficientNoncanonical");
            return result;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or FormatException or OverflowException or
            InvalidOperationException)
        {
            throw new ArgumentException("PlanarCoefficientPayloadInvalid", ex);
        }
    }

    public static CalibrationComputationEvidencePayload EncodeEvidence(
        PlanarHomographyEvidence value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(PlanarHomographyContracts.Evidence, Write(writer =>
        {
            writer.Write(Version);
            writer.Write(Convert.FromHexString(value.InputPayloadHash));
            writer.Write(value.FrameId.ToByteArray());
            writer.Write(Convert.FromHexString(value.SourceHash));
            writer.Write(value.ConstraintRankRatio);
            writer.Write(value.InverseClosureError);
            writer.Write(value.Residuals.Count);
            foreach (var residual in value.Residuals)
            {
                writer.Write(residual.MarkerId);
                writer.Write(residual.CornerIndex);
                WritePoint(writer, residual.ObservedPixels);
                WritePoint(writer, residual.PhysicalMillimeters);
                WritePoint(writer, residual.ImageToPlaneResidualMillimeters);
                WritePoint(writer, residual.PlaneToImageResidualPixels);
            }
            writer.Write(value.MissingMarkerIds.Count);
            foreach (var markerId in value.MissingMarkerIds)
                writer.Write(markerId);
        }));
    }

    public static PlanarHomographyEvidence DecodeEvidence(
        CalibrationComputationEvidencePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Format != PlanarHomographyContracts.Evidence)
            throw new ArgumentException("PlanarEvidenceContractMismatch", nameof(payload));
        var supplied = payload.GetBytes();
        try
        {
            using var reader = Reader(supplied);
            if (reader.ReadInt32() != Version)
                throw new ArgumentException("PlanarEvidenceVersionUnsupported");
            var inputHash = ReadHash(reader, "PlanarEvidenceInputHashInvalid");
            var frameBytes = ReadExactly(reader, 16);
            var frameId = new Guid(frameBytes);
            var sourceHash = ReadHash(reader, "PlanarEvidenceSourceHashInvalid");
            var rankRatio = reader.ReadDouble();
            var closure = reader.ReadDouble();
            var residualCount = ReadCount(reader, 4, PlanarHomographyEvidence.MaximumResidualCount,
                "PlanarEvidenceResidualCountInvalid");
            var residuals = new PlanarPointResidual[residualCount];
            for (var index = 0; index < residuals.Length; index++)
            {
                var markerId = reader.ReadInt32();
                var cornerIndex = reader.ReadInt32();
                residuals[index] = new PlanarPointResidual(markerId, cornerIndex,
                    ReadPoint(reader), ReadPoint(reader), ReadPoint(reader), ReadPoint(reader));
            }
            var missingCount = ReadCount(reader, 0, PlanarHomographyEvidence.MaximumMissingMarkerCount,
                "PlanarEvidenceMissingMarkerCountInvalid");
            var missing = new int[missingCount];
            for (var index = 0; index < missing.Length; index++)
                missing[index] = reader.ReadInt32();
            RequireEnd(reader);

            var result = new PlanarHomographyEvidence(inputHash, frameId, sourceHash,
                rankRatio, closure, residuals, missing);
            RequireCanonical(supplied, EncodeEvidence(result).GetBytes(),
                "PlanarEvidenceNoncanonical");
            return result;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or FormatException or OverflowException or
            InvalidOperationException)
        {
            throw new ArgumentException("PlanarEvidencePayloadInvalid", ex);
        }
    }

    private static byte[] Write(Action<BinaryWriter> action)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        action(writer);
        writer.Flush();
        return stream.ToArray();
    }

    private static BinaryReader Reader(byte[] bytes) =>
        new(new MemoryStream(bytes, writable: false), Encoding.UTF8, leaveOpen: false);

    private static void WriteMatrix(BinaryWriter writer, IReadOnlyList<double> matrix)
    {
        if (matrix.Count != PlanarHomographyCoefficients.MatrixElementCount)
            throw new ArgumentException("PlanarCoefficientMatrixInvalid");
        foreach (var value in matrix)
            writer.Write(value);
    }

    private static double[] ReadMatrix(BinaryReader reader)
    {
        var matrix = new double[PlanarHomographyCoefficients.MatrixElementCount];
        for (var index = 0; index < matrix.Length; index++)
            matrix[index] = reader.ReadDouble();
        return matrix;
    }

    private static void WriteHull(BinaryWriter writer, IReadOnlyList<PlanarPoint> hull)
    {
        writer.Write(hull.Count);
        foreach (var point in hull)
            WritePoint(writer, point);
    }

    private static PlanarPoint[] ReadHull(BinaryReader reader)
    {
        var count = ReadCount(reader, 3, PlanarHomographyCoefficients.MaximumHullPointCount,
            "PlanarHullCountInvalid");
        var hull = new PlanarPoint[count];
        for (var index = 0; index < hull.Length; index++)
            hull[index] = ReadPoint(reader);
        return hull;
    }

    private static void WritePoint(BinaryWriter writer, PlanarPoint point)
    {
        writer.Write(point.X);
        writer.Write(point.Y);
    }

    private static PlanarPoint ReadPoint(BinaryReader reader) =>
        new(reader.ReadDouble(), reader.ReadDouble());

    private static string ReadHash(BinaryReader reader, string reason) =>
        PlanarHomographyEvidence.RequireHash(Convert.ToHexString(ReadExactly(reader, HashBytes)),
            "hash", reason);

    private static byte[] ReadBoundedBytes(BinaryReader reader, int minimum, int maximum, string reason)
    {
        var count = ReadCount(reader, minimum, maximum, reason);
        var bytes = ReadExactly(reader, count);
        return bytes;
    }

    private static int ReadCount(BinaryReader reader, int minimum, int maximum, string reason)
    {
        var count = reader.ReadInt32();
        if (count < minimum || count > maximum)
            throw new ArgumentException(reason);
        return count;
    }

    private static byte[] ReadExactly(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
            throw new EndOfStreamException();
        return bytes;
    }

    private static void RequireEnd(BinaryReader reader)
    {
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new ArgumentException("PlanarPayloadTrailingBytes");
    }

    private static void RequireCanonical(byte[] supplied, byte[] canonical, string reason)
    {
        if (!supplied.AsSpan().SequenceEqual(canonical))
            throw new ArgumentException(reason);
    }
}

/// <summary>Shared bounded projective geometry used by the planar procedure and result validation.</summary>
internal static class PlanarGeometry
{
    private const int MaximumPointCount = 4096;
    private const double MachineEpsilon = 2.2204460492503131e-16d;
    private const double RelativeDenominatorGuard = 64d * MachineEpsilon;
    private const double RelativeCrossGuard = 64d * MachineEpsilon;

    internal static PlanarPoint[] ConvexHull(IEnumerable<PlanarPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var source = points.Take(MaximumPointCount + 1).ToArray();
        if (source.Length > MaximumPointCount || source.Any(point => !Finite(point)))
            throw new ArgumentException("PlanarGeometryPointsInvalid", nameof(points));
        var unique = source.Distinct().OrderBy(point => point.X).ThenBy(point => point.Y).ToArray();
        if (unique.Length < 3)
            throw new ArgumentException("PlanarGeometryHullInsufficient", nameof(points));

        var lower = new List<PlanarPoint>();
        foreach (var point in unique)
        {
            while (lower.Count >= 2 && IsNonPositiveTurn(lower[^2], lower[^1], point))
                lower.RemoveAt(lower.Count - 1);
            lower.Add(point);
        }

        var upper = new List<PlanarPoint>();
        for (var index = unique.Length - 1; index >= 0; index--)
        {
            var point = unique[index];
            while (upper.Count >= 2 && IsNonPositiveTurn(upper[^2], upper[^1], point))
                upper.RemoveAt(upper.Count - 1);
            upper.Add(point);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        var hull = lower.ToArray();
        if (!IsStrictConvex(hull))
            throw new ArgumentException("PlanarGeometryHullDegenerate", nameof(points));
        return NormalizeCcwhull(hull);
    }

    internal static double Area(IReadOnlyList<PlanarPoint> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        if (polygon.Count < 3)
            return 0d;
        return Math.Abs(SignedArea(polygon));
    }

    internal static bool Contains(IReadOnlyList<PlanarPoint> polygon, PlanarPoint point)
    {
        if (polygon is null || polygon.Count < 3 || !Finite(point))
            return false;
        var signedArea = SignedArea(polygon);
        if (!double.IsFinite(signedArea) || signedArea == 0d)
            return false;
        var positive = signedArea > 0d;
        for (var index = 0; index < polygon.Count; index++)
        {
            var cross = Cross(polygon[index], polygon[(index + 1) % polygon.Count], point);
            var tolerance = CrossRoundoffTolerance(polygon[index],
                polygon[(index + 1) % polygon.Count], point);
            if (!double.IsFinite(cross) || (positive ? cross < -tolerance : cross > tolerance))
                return false;
        }
        return true;
    }

    internal static PlanarPoint Project(IReadOnlyList<double> matrix, PlanarPoint point)
    {
        if (!TryProject(matrix, point, out var result))
            throw new ArgumentException("PlanarProjectivePointInvalid");
        return result;
    }

    internal static bool TryProject(
        IReadOnlyList<double> matrix,
        PlanarPoint point,
        out PlanarPoint result)
    {
        result = default;
        if (matrix is null || matrix.Count != 9 || !Finite(point) || matrix.Any(value => !double.IsFinite(value)))
            return false;
        var denominator = matrix[6] * point.X + matrix[7] * point.Y + matrix[8];
        var denominatorScale = Math.Abs(matrix[6] * point.X) +
            Math.Abs(matrix[7] * point.Y) + Math.Abs(matrix[8]);
        if (!double.IsFinite(denominator) || !double.IsFinite(denominatorScale) ||
            denominatorScale <= 0d || Math.Abs(denominator) / denominatorScale <= RelativeDenominatorGuard)
            return false;
        var x = (matrix[0] * point.X + matrix[1] * point.Y + matrix[2]) / denominator;
        var y = (matrix[3] * point.X + matrix[4] * point.Y + matrix[5]) / denominator;
        if (!double.IsFinite(x) || !double.IsFinite(y))
            return false;
        result = new PlanarPoint(x, y);
        return true;
    }

    internal static bool IsStrictCcwhull(IReadOnlyList<PlanarPoint> polygon)
    {
        if (!IsStrictConvex(polygon))
            return false;
        return SignedArea(polygon) > 0d;
    }

    internal static bool IsStrictConvex(IReadOnlyList<PlanarPoint> polygon)
    {
        if (polygon is null || polygon.Count < 3 || polygon.Any(point => !Finite(point)) ||
            polygon.Distinct().Count() != polygon.Count)
            return false;
        var signs = new double[polygon.Count];
        for (var index = 0; index < polygon.Count; index++)
        {
            var first = polygon[index];
            var second = polygon[(index + 1) % polygon.Count];
            var third = polygon[(index + 2) % polygon.Count];
            signs[index] = Cross(first, second, third);
            if (!double.IsFinite(signs[index]) ||
                Math.Abs(signs[index]) <= CrossRoundoffTolerance(first, second, third))
                return false;
        }
        var positive = signs[0] > 0d;
        var area = SignedArea(polygon);
        if (!double.IsFinite(area) || area == 0d || !signs.All(value => (value > 0d) == positive))
            return false;
        // Equal turns alone also describe a self-intersecting pentagram.
        // Every other vertex must lie strictly in the same interior half-plane
        // for every edge, which verifies both convexity and a simple boundary.
        for (var edge = 0; edge < polygon.Count; edge++)
        {
            var next = (edge + 1) % polygon.Count;
            for (var vertex = 0; vertex < polygon.Count; vertex++)
            {
                if (vertex == edge || vertex == next) continue;
                var cross = Cross(polygon[edge], polygon[next], polygon[vertex]);
                var tolerance = CrossRoundoffTolerance(polygon[edge], polygon[next], polygon[vertex]);
                if (!double.IsFinite(cross) || (positive ? cross <= tolerance : cross >= -tolerance))
                    return false;
            }
        }
        return true;
    }

    internal static PlanarPoint[] NormalizeCcwhull(IReadOnlyList<PlanarPoint> polygon)
    {
        if (!IsStrictConvex(polygon))
            throw new ArgumentException("PlanarGeometryHullDegenerate", nameof(polygon));
        var normalized = polygon.Select(point => new PlanarPoint(
            CanonicalZero(point.X), CanonicalZero(point.Y))).ToArray();
        if (SignedArea(normalized) < 0d)
            Array.Reverse(normalized);

        var firstIndex = 0;
        for (var index = 1; index < normalized.Length; index++)
        {
            if (normalized[index].X < normalized[firstIndex].X ||
                (normalized[index].X == normalized[firstIndex].X &&
                    normalized[index].Y < normalized[firstIndex].Y))
                firstIndex = index;
        }
        if (firstIndex == 0)
            return normalized;
        var rotated = new PlanarPoint[normalized.Length];
        for (var index = 0; index < normalized.Length; index++)
            rotated[index] = normalized[(firstIndex + index) % normalized.Length];
        return rotated;
    }

    private static bool Finite(PlanarPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);

    private static double CanonicalZero(double value) => value == 0d ? 0d : value;

    private static bool IsNonPositiveTurn(PlanarPoint a, PlanarPoint b, PlanarPoint c)
    {
        var cross = Cross(a, b, c);
        return !double.IsFinite(cross) || cross <= CrossRoundoffTolerance(a, b, c);
    }

    private static double CrossRoundoffTolerance(PlanarPoint a, PlanarPoint b, PlanarPoint point)
    {
        var scale = Math.Abs((b.X - a.X) * (point.Y - a.Y)) +
            Math.Abs((b.Y - a.Y) * (point.X - a.X));
        return double.IsFinite(scale) ? scale * RelativeCrossGuard : double.PositiveInfinity;
    }

    private static double SignedArea(IReadOnlyList<PlanarPoint> polygon)
    {
        // Subtract one vertex from every term so a large physical translation
        // cannot erase a small but valid polygon area through cancellation.
        var origin = polygon[0];
        var twice = 0d;
        for (var index = 1; index < polygon.Count; index++)
        {
            var current = polygon[index];
            var next = polygon[(index + 1) % polygon.Count];
            twice += (current.X - origin.X) * (next.Y - origin.Y) -
                (current.Y - origin.Y) * (next.X - origin.X);
        }
        return twice / 2d;
    }

    private static double Cross(PlanarPoint a, PlanarPoint b, PlanarPoint c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
}
