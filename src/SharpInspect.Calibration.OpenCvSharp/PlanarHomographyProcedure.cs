using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using global::OpenCvSharp;
using global::OpenCvSharp.Aruco;
using SharpInspect.Abstractions;

namespace SharpInspect.Calibration.OpenCvSharp;

/// <summary>
/// Extracts native ArUco correspondences from one borrowed frame and computes a
/// non-robust projective mapping for the declared physical plane.
/// </summary>
public sealed class PlanarHomographyProcedure : ICalibrationProcedure<PlanarHomographyInput>
{
    private const double RankRatioFloor = 1e-5d;
    private const long MaximumImagePixels = 16_777_216L;

    private static readonly byte[] ExtractionReceiptKey =
        RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] ExtractionReceiptDomain =
        Encoding.ASCII.GetBytes("sharpinspect-planar-extraction-receipt-v1");

    public CalibrationProcedureDescriptor Descriptor { get; } =
        new(PlanarHomographyContracts.Procedure, PlanarHomographyContracts.Input,
            CalibrationKind.PlanarHomography);

    public ICalibrationInputCodec<PlanarHomographyInput> InputCodec { get; } =
        new PlanarHomographyInputCodec();

    public ValueTask<CalibrationExtractionResult> ExtractAsync(
        CalibrationExtractionContext<PlanarHomographyInput> context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFrame(context.Frame, context.Input);
        var inputPayload = InputCodec.EncodePayload(context.Input);

        using var image = CreateDisplayImage(context.Frame, cancellationToken,
            out var pixelHash);
        using var gray = NewMat(MatType.CV_8UC1, cancellationToken,
            context.Frame.Height, context.Frame.Width);
        Mat searchImage = image;
        if (context.Frame.PixelFormat == VisionPixelFormat.Bgr24)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
            }
            catch (OpenCvSharpException)
            {
                return EmptyExtraction(context, inputPayload, pixelHash,
                    "PlanarMarkerDetectionFailed");
            }

            cancellationToken.ThrowIfCancellationRequested();
            searchImage = gray;
        }

        Point2f[][] corners;
        int[] ids;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var dictionary = CvAruco.GetPredefinedDictionary(
                PredefinedDictionaryName.Dict4X4_50);
            var parameters = CreateDetectorParameters();
            CvAruco.DetectMarkers(searchImage, dictionary, out corners, out ids,
                parameters, out _);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OpenCvSharpException)
        {
            return EmptyExtraction(context, inputPayload, pixelHash,
                "PlanarMarkerDetectionFailed");
        }

        if (ids is null || corners is null || ids.Length != corners.Length)
            return EmptyExtraction(context, inputPayload, pixelHash,
                "PlanarMarkerDetectionInvalid");
        if (ids.Length == 0)
            return EmptyExtraction(context, inputPayload, pixelHash,
                "PlanarMarkersNotFound");

        var targetMarkers = context.Input.Target.Markers.ToDictionary(
            marker => marker.MarkerId);
        var seenMarkerIds = new HashSet<int>();
        var detected = new List<DetectedMarker>(ids.Length);
        for (var index = 0; index < ids.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var markerId = ids[index];
            if (!targetMarkers.ContainsKey(markerId))
                return EmptyExtraction(context, inputPayload, pixelHash,
                    "PlanarUnknownMarkerDetected");
            if (!seenMarkerIds.Add(markerId))
                return EmptyExtraction(context, inputPayload, pixelHash,
                    "PlanarDuplicateMarkerDetected");

            var markerCorners = corners[index];
            if (markerCorners is null || markerCorners.Length != 4)
                return EmptyExtraction(context, inputPayload, pixelHash,
                    "PlanarMarkerCornersInvalid");

            for (var cornerIndex = 0; cornerIndex < markerCorners.Length; cornerIndex++)
            {
                var point = markerCorners[cornerIndex];
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) ||
                    point.X < 0f || point.X >= context.Input.ImageWidth ||
                    point.Y < 0f || point.Y >= context.Input.ImageHeight)
                    return EmptyExtraction(context, inputPayload, pixelHash,
                        "PlanarMarkerCornersInvalid");
            }
            if (!PlanarGeometry.IsStrictConvex(markerCorners.Select(point =>
                    new PlanarPoint(point.X, point.Y)).ToArray()))
                return EmptyExtraction(context, inputPayload, pixelHash,
                    "PlanarMarkerCornersInvalid");

            // Keep OpenCV's decoded clockwise/canonical corner order. The
            // order is part of the correspondence contract; no image sorting
            // is performed here.
            detected.Add(new DetectedMarker(markerId, markerCorners));
        }

        detected.Sort((left, right) => left.MarkerId.CompareTo(right.MarkerId));
        var features = new List<CalibrationImageFeature>(checked(detected.Count * 4));
        foreach (var marker in detected)
        {
            for (var cornerIndex = 0; cornerIndex < marker.Corners.Length; cornerIndex++)
            {
                var point = marker.Corners[cornerIndex];
                features.Add(new CalibrationImageFeature(
                    FeatureId(marker.MarkerId, cornerIndex), point.X, point.Y));
            }
        }

        var receipt = CreateExtractionReceipt(context.SessionId, context.FrameId,
            context.SourceHash, inputPayload, pixelHash, features);
        return ValueTask.FromResult(new CalibrationExtractionResult(features,
            new[] { new CalibrationProcedureDiagnostic("PlanarMarkersDetected",
                detected.Count.ToString(CultureInfo.InvariantCulture)) }, receipt));
    }

    public ValueTask<CalibrationProcedureComputationResult> ComputeAsync(
        CalibrationComputationContext<PlanarHomographyInput> context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Observations.Count != 1)
            throw Failure("PlanarObservationCountInvalid");

        var input = context.Input;
        var observation = context.Observations[0];
        ValidateFrame(observation.Frame, input);
        var inputPayload = InputCodec.EncodePayload(input);
        var correspondences = ValidateFeatures(observation, input);

        // The receipt is the extraction authority. Pixel hashing is deliberately
        // the only frame read in ComputeAsync and occurs before HMAC verification.
        var pixelHash = ComputePixelHash(observation.Frame, cancellationToken);
        ValidateExtractionReceipt(observation, inputPayload, pixelHash,
            correspondences.Select(value => value.Feature).ToArray());

        var physicalPoints = correspondences.Select(value => value.Physical).ToArray();
        var imagePoints = correspondences.Select(value => value.Observed).ToArray();
        if (physicalPoints.Length < 4)
            throw Failure("PlanarCorrespondenceCountInsufficient");

        var rankRatio = ComputeNormalizedDltRankRatio(physicalPoints, imagePoints,
            cancellationToken);
        if (!double.IsFinite(rankRatio) || rankRatio < RankRatioFloor)
            throw Failure("PlanarConstraintRankInsufficient");

        var planeToImage = ComputeHomography(physicalPoints, imagePoints,
            cancellationToken);
        double[] imageToPlane;
        try
        {
            imageToPlane = PlanarHomographyCoefficients.InvertMatrix(planeToImage);
        }
        catch (ArgumentException)
        {
            throw Failure("PlanarHomographyMatrixInvalid");
        }
        var imageHull = PlanarGeometry.ConvexHull(imagePoints);
        var physicalHull = PlanarGeometry.ConvexHull(physicalPoints);

        PlanarHomographyCoefficients coefficients;
        try
        {
            coefficients = new PlanarHomographyCoefficients(inputPayload.ContentHash,
                input, imageToPlane, imageHull, physicalHull);
        }
        catch (ArgumentException)
        {
            throw Failure("PlanarHomographyInvalid");
        }

        var inverseClosureError = ComputeInverseClosure(coefficients,
            correspondences, cancellationToken);
        if (!double.IsFinite(inverseClosureError))
            throw Failure("PlanarInverseClosureInvalid");

        var residuals = new List<PlanarPointResidual>(correspondences.Count);
        foreach (var correspondence in correspondences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PlanarGeometry.TryProject(coefficients.ImageToPlane,
                    correspondence.Observed, out var reconstructedPhysical) ||
                !PlanarGeometry.TryProject(coefficients.PlaneToImage,
                    correspondence.Physical, out var reconstructedImage))
                throw Failure("PlanarHomographyProjectionInvalid");

            var imageToPlaneResidual = new PlanarPoint(
                correspondence.Physical.X - reconstructedPhysical.X,
                correspondence.Physical.Y - reconstructedPhysical.Y);
            var planeToImageResidual = new PlanarPoint(
                correspondence.Observed.X - reconstructedImage.X,
                correspondence.Observed.Y - reconstructedImage.Y);
            if (!Finite(imageToPlaneResidual) || !Finite(planeToImageResidual))
                throw Failure("PlanarResidualInvalid");

            residuals.Add(new PlanarPointResidual(correspondence.MarkerId,
                correspondence.CornerIndex, correspondence.Observed,
                correspondence.Physical, imageToPlaneResidual, planeToImageResidual));
        }

        var missingMarkerIds = input.Target.Markers
            .Select(marker => marker.MarkerId)
            .Except(correspondences.Select(value => value.MarkerId))
            .OrderBy(markerId => markerId)
            .ToArray();

        PlanarHomographyEvidence evidence;
        CalibrationComputationEvidencePayload evidencePayload;
        try
        {
            evidence = new PlanarHomographyEvidence(inputPayload.ContentHash,
                observation.FrameId, observation.SourceHash, rankRatio,
                inverseClosureError, residuals, missingMarkerIds);
            evidencePayload = PlanarHomographyResultCodec.EncodeEvidence(evidence);
        }
        catch (ArgumentException)
        {
            throw Failure("PlanarEvidenceInvalid");
        }

        var imageHullArea = PlanarGeometry.Area(coefficients.ImageHull);
        var physicalHullArea = PlanarGeometry.Area(coefficients.PhysicalHull);
        var imageArea = input.ImageWidth * (double)input.ImageHeight;
        var metrics = new[]
        {
            new CalibrationQualityMetric("RmsMillimeters", evidence.RmsMillimeters,
                "millimeters"),
            new CalibrationQualityMetric("RmsPixels", evidence.RmsPixels, "pixels"),
            new CalibrationQualityMetric("MaxMillimeters", evidence.MaxMillimeters,
                "millimeters"),
            new CalibrationQualityMetric("MaxPixels", evidence.MaxPixels, "pixels"),
            new CalibrationQualityMetric("PointCount", residuals.Count, "points"),
            new CalibrationQualityMetric("MissingMarkerCount", missingMarkerIds.Length,
                "markers"),
            new CalibrationQualityMetric("ConstraintRankRatio", rankRatio),
            new CalibrationQualityMetric("InverseClosureError", inverseClosureError,
                "relative-distance"),
            new CalibrationQualityMetric("ImageHullAreaPixels", imageHullArea,
                "square-pixels"),
            new CalibrationQualityMetric("PhysicalHullAreaSquareMillimeters",
                physicalHullArea, "square-millimeters"),
            new CalibrationQualityMetric("ImageHullAreaFraction",
                imageHullArea / imageArea, "fraction")
        };

        CalibrationCoefficientPayload coefficientPayload;
        try
        {
            coefficientPayload = PlanarHomographyResultCodec.EncodeCoefficients(
                coefficients);
        }
        catch (ArgumentException)
        {
            throw Failure("PlanarCoefficientEncodingInvalid");
        }

        return ValueTask.FromResult(new CalibrationProcedureComputationResult(
            coefficientPayload, metrics, new[] {
                new CalibrationProcedureDiagnostic("PlanarHomographyComputed") },
            evidencePayload));
    }

    internal static string FeatureId(int markerId, int cornerIndex) =>
        "marker-" + markerId.ToString("D2", CultureInfo.InvariantCulture) +
        "-corner-" + cornerIndex.ToString(CultureInfo.InvariantCulture);

    private static DetectorParameters CreateDetectorParameters()
    {
        // Every detector field is assigned explicitly. This list is part of
        // the v1 procedure descriptor and is kept independent of library defaults.
        return new DetectorParameters
        {
            AdaptiveThreshWinSizeMin = 3,
            AdaptiveThreshWinSizeMax = 23,
            AdaptiveThreshWinSizeStep = 10,
            AdaptiveThreshConstant = 7d,
            MinMarkerPerimeterRate = 0.03d,
            MaxMarkerPerimeterRate = 4d,
            PolygonalApproxAccuracyRate = 0.03d,
            MinCornerDistanceRate = 0.05d,
            MinDistanceToBorder = 3,
            MinMarkerDistanceRate = 0.05d,
            CornerRefinementMethod = CornerRefineMethod.Subpix,
            CornerRefinementWinSize = 5,
            CornerRefinementMaxIterations = 30,
            CornerRefinementMinAccuracy = 0.001d,
            MarkerBorderBits = 1,
            PerspectiveRemovePixelPerCell = 8,
            PerspectiveRemoveIgnoredMarginPerCell = 0.13d,
            MaxErroneousBitsInBorderRate = 0.35d,
            MinOtsuStdDev = 5d,
            ErrorCorrectionRate = 0.6d,
            AprilTagQuadDecimate = 0f,
            AprilTagQuadSigma = 0f,
            AprilTagMinClusterPixels = 5,
            AprilTagMaxNmaxima = 10,
            AprilTagCriticalRad = 0.17453292f,
            AprilTagMaxLineFitMse = 10f,
            AprilTagMinWhiteBlackDiff = 5,
            AprilTagDeglitch = 0,
            DetectInvertedMarker = false,
            UseAruco3Detection = false,
            MinSideLengthCanonicalImg = 32,
            MinMarkerLengthRatioOriginalImg = 0f
        };
    }

    private static ValueTask<CalibrationExtractionResult> EmptyExtraction(
        CalibrationExtractionContext<PlanarHomographyInput> context,
        CalibrationProcedureInputPayload inputPayload, string pixelHash,
        string diagnosticKey)
    {
        var receipt = CreateExtractionReceipt(context.SessionId, context.FrameId,
            context.SourceHash, inputPayload, pixelHash,
            Array.Empty<CalibrationImageFeature>());
        return ValueTask.FromResult(new CalibrationExtractionResult(
            Array.Empty<CalibrationImageFeature>(),
            new[] { new CalibrationProcedureDiagnostic(diagnosticKey) }, receipt));
    }

    private static List<PlanarCorrespondence> ValidateFeatures(
        CalibrationObservationInput observation, PlanarHomographyInput input)
    {
        if (observation.Features.Count < 4)
            throw Failure("PlanarCorrespondenceCountInsufficient");

        var markers = input.Target.Markers.ToDictionary(marker => marker.MarkerId);
        var correspondences = new List<PlanarCorrespondence>(observation.Features.Count);
        var previousMarkerId = -1;
        var expectedCornerIndex = 0;
        var seen = new HashSet<(int MarkerId, int CornerIndex)>();
        foreach (var feature in observation.Features)
        {
            if (!TryParseFeatureId(feature.StableFeatureId,
                    out var markerId, out var cornerIndex))
                throw Failure("PlanarFeatureOrderInvalid");
            if (!markers.TryGetValue(markerId, out var marker))
                throw Failure("PlanarFeatureUnknownMarker");
            if (markerId < previousMarkerId ||
                (markerId == previousMarkerId && cornerIndex != expectedCornerIndex))
                throw Failure("PlanarFeatureOrderInvalid");
            if (markerId != previousMarkerId)
            {
                if (cornerIndex != 0)
                    throw Failure("PlanarFeatureOrderInvalid");
                previousMarkerId = markerId;
                expectedCornerIndex = 0;
            }
            if (cornerIndex != expectedCornerIndex || !seen.Add((markerId, cornerIndex)))
                throw Failure("PlanarFeatureOrderInvalid");
            if (!double.IsFinite(feature.PixelX) || !double.IsFinite(feature.PixelY) ||
                feature.PixelX < 0d || feature.PixelX >= input.ImageWidth ||
                feature.PixelY < 0d || feature.PixelY >= input.ImageHeight)
                throw Failure("PlanarFeatureOutsideImage");

            var physical = marker.PhysicalCornersMillimeters[cornerIndex];
            var observed = new PlanarPoint(feature.PixelX, feature.PixelY);
            correspondences.Add(new PlanarCorrespondence(markerId, cornerIndex,
                feature, observed, physical));
            expectedCornerIndex++;
            if (expectedCornerIndex == 4)
                expectedCornerIndex = 0;
        }

        if (correspondences.Count < 4 || correspondences.Count % 4 != 0 ||
            correspondences.GroupBy(value => value.MarkerId)
                .Any(group => group.Count() != 4))
            throw Failure("PlanarFeatureGroupInvalid");
        return correspondences;
    }

    private static bool TryParseFeatureId(string value, out int markerId,
        out int cornerIndex)
    {
        markerId = 0;
        cornerIndex = 0;
        if (string.IsNullOrEmpty(value) || !value.StartsWith("marker-",
                StringComparison.Ordinal))
            return false;
        var separator = value.IndexOf("-corner-", StringComparison.Ordinal);
        if (separator != 9 || separator + 8 >= value.Length)
            return false;
        if (!int.TryParse(value.AsSpan(7, 2), NumberStyles.None,
                CultureInfo.InvariantCulture, out markerId) ||
            !int.TryParse(value.AsSpan(separator + 8), NumberStyles.None,
                CultureInfo.InvariantCulture, out cornerIndex) ||
            markerId is < 0 or > 49 || cornerIndex is < 0 or > 3)
            return false;
        return string.Equals(value, FeatureId(markerId, cornerIndex),
            StringComparison.Ordinal);
    }

    private static double ComputeNormalizedDltRankRatio(
        IReadOnlyList<PlanarPoint> physical, IReadOnlyList<PlanarPoint> image,
        CancellationToken cancellationToken)
    {
        if (physical.Count != image.Count || physical.Count < 4)
            throw Failure("PlanarCorrespondenceCountInsufficient");
        var physicalNormalization = NormalizePoints(physical);
        var imageNormalization = NormalizePoints(image);
        var rows = new double[checked(physical.Count * 2), 9];
        for (var index = 0; index < physical.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = ApplyNormalization(physicalNormalization, physical[index]);
            var target = ApplyNormalization(imageNormalization, image[index]);
            var row = index * 2;
            rows[row, 0] = -source.X;
            rows[row, 1] = -source.Y;
            rows[row, 2] = -1d;
            rows[row, 6] = target.X * source.X;
            rows[row, 7] = target.X * source.Y;
            rows[row, 8] = target.X;
            rows[row + 1, 3] = -source.X;
            rows[row + 1, 4] = -source.Y;
            rows[row + 1, 5] = -1d;
            rows[row + 1, 6] = target.Y * source.X;
            rows[row + 1, 7] = target.Y * source.Y;
            rows[row + 1, 8] = target.Y;
        }

        using var matrix = NewMat(MatType.CV_64FC1, cancellationToken,
            rows.GetLength(0), rows.GetLength(1));
        for (var row = 0; row < rows.GetLength(0); row++)
        for (var column = 0; column < rows.GetLength(1); column++)
            SetMatDouble(matrix, row, column, rows[row, column], cancellationToken);

        SVD svd;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            svd = new SVD(matrix, SVD.Flags.NoUV);
        }
        catch (OpenCvSharpException)
        {
            throw Failure("PlanarConstraintSvdFailed");
        }

        using (svd)
        {
            Mat singularValues;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                singularValues = svd.W();
            }
            catch (OpenCvSharpException)
            {
                throw Failure("PlanarConstraintSvdFailed");
            }

            using (singularValues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var copied = singularValues.GetArray(out double[] values);
                cancellationToken.ThrowIfCancellationRequested();
                if (!copied || values is null || values.Length < 8 ||
                    values.Any(value => !double.IsFinite(value) || value < 0d))
                    throw Failure("PlanarConstraintSvdInvalid");

                Array.Sort(values);
                Array.Reverse(values);
                var largest = values[0];
                // With exactly four correspondences the 8x9 matrix has only
                // eight returned singular values. For five or more points the
                // ninth value is the null-space value; index 7 is still the
                // smallest value that must be non-zero for rank eight.
                var eighth = values[7];
                var ratio = eighth / largest;
                if (!double.IsFinite(ratio) || largest <= 0d)
                    throw Failure("PlanarConstraintSvdInvalid");
                return ratio;
            }
        }
    }

    private static double[] ComputeHomography(
        IReadOnlyList<PlanarPoint> physical, IReadOnlyList<PlanarPoint> image,
        CancellationToken cancellationToken)
    {
        using var mask = NewMat(MatType.CV_8UC1, cancellationToken);
        Mat homography;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            homography = Cv2.FindHomography(
                physical.Select(point => new Point2d(point.X, point.Y)),
                image.Select(point => new Point2d(point.X, point.Y)),
                HomographyMethods.None, 0d, mask, 2000, 0.995d);
        }
        catch (OpenCvSharpException)
        {
            throw Failure("PlanarHomographyFailed");
        }

        using (homography)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (homography is null)
                throw Failure("PlanarHomographyInvalid");
            var empty = homography.Empty();
            cancellationToken.ThrowIfCancellationRequested();
            var rows = homography.Rows;
            cancellationToken.ThrowIfCancellationRequested();
            var columns = homography.Cols;
            cancellationToken.ThrowIfCancellationRequested();
            if (empty || rows != 3 || columns != 3)
                throw Failure("PlanarHomographyInvalid");
            var values = new double[9];
            for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                values[row * 3 + column] = homography.Get<double>(row, column);
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (values.Any(value => !double.IsFinite(value)))
                throw Failure("PlanarHomographyInvalid");
            try
            {
                return PlanarHomographyCoefficients.NormalizeMatrix(values,
                    "planeToImage");
            }
            catch (ArgumentException)
            {
                throw Failure("PlanarHomographyMatrixInvalid");
            }
        }
    }

    private static double ComputeInverseClosure(
        PlanarHomographyCoefficients coefficients,
        IReadOnlyList<PlanarCorrespondence> correspondences,
        CancellationToken cancellationToken)
    {
        var maximum = 0d;
        foreach (var correspondence in correspondences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PlanarGeometry.TryProject(coefficients.PlaneToImage,
                    correspondence.Physical, out var image) ||
                !PlanarGeometry.TryProject(coefficients.ImageToPlane, image,
                    out var physical) ||
                !PlanarGeometry.TryProject(coefficients.ImageToPlane,
                    correspondence.Observed, out var recoveredPhysical) ||
                !PlanarGeometry.TryProject(coefficients.PlaneToImage,
                    recoveredPhysical, out var recoveredImage))
                throw Failure("PlanarInverseClosureInvalid");

            // Compare each complete round trip with its own starting point.
            // Comparing the predicted image with the observed image would
            // incorrectly report fitting residual as inverse closure error.
            maximum = Math.Max(maximum, Distance(correspondence.Observed, recoveredImage) /
                Math.Max(1d, Distance(default, correspondence.Observed)));
            maximum = Math.Max(maximum, Distance(correspondence.Physical, physical) /
                Math.Max(1d, Distance(default, correspondence.Physical)));
        }
        return maximum;
    }

    private static Normalization NormalizePoints(IReadOnlyList<PlanarPoint> points)
    {
        var centerX = points.Average(point => point.X);
        var centerY = points.Average(point => point.Y);
        var meanDistance = points.Average(point => Math.Sqrt(
            Math.Pow(point.X - centerX, 2) + Math.Pow(point.Y - centerY, 2)));
        if (!double.IsFinite(meanDistance) || meanDistance <= 0d)
            throw Failure("PlanarConstraintGeometryDegenerate");
        var scale = Math.Sqrt(2d) / meanDistance;
        return new Normalization(centerX, centerY, scale);
    }

    private static PlanarPoint ApplyNormalization(Normalization normalization,
        PlanarPoint point) => new((point.X - normalization.CenterX) * normalization.Scale,
        (point.Y - normalization.CenterY) * normalization.Scale);

    private static Mat CreateDisplayImage(VisionFrame frame,
        CancellationToken cancellationToken, out string pixelHash)
    {
        pixelHash = string.Empty;
        var type = frame.PixelFormat switch
        {
            VisionPixelFormat.Mono8 => MatType.CV_8UC1,
            VisionPixelFormat.Mono16 => MatType.CV_8UC1,
            VisionPixelFormat.Bgr24 => MatType.CV_8UC3,
            _ => throw Failure("PlanarPixelFormatUnsupported")
        };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var image = NewMat(type, cancellationToken, frame.Height, frame.Width);
        try
        {
            for (var rowIndex = 0; rowIndex < frame.Height; rowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = ReadValidRow(frame, rowIndex).ToArray();
                hash.AppendData(row);
                if (frame.PixelFormat == VisionPixelFormat.Mono16)
                {
                    var validBits = frame.ValidBits ??
                        throw Failure("PlanarMono16ValidBitsMissing");
                    if (validBits is not (10 or 12 or 16))
                        throw Failure("PlanarMono16ValidBitsInvalid");
                    var maximum = (uint)((1 << validBits) - 1);
                    var display = new byte[frame.Width];
                    for (var column = 0; column < frame.Width; column++)
                    {
                        var offset = checked(column * 2);
                        var sample = (uint)(row[offset] | (row[offset + 1] << 8));
                        if (sample > maximum)
                            throw Failure("PlanarMono16HighBitsInvalid");
                        display[column] = (byte)((sample * 255u + maximum / 2u) /
                            maximum);
                    }
                    CopyRowToMat(image, rowIndex, display, cancellationToken);
                }
                else
                {
                    CopyRowToMat(image, rowIndex, row, cancellationToken);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            pixelHash = Convert.ToHexString(hash.GetHashAndReset());
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static ReadOnlySpan<byte> ReadValidRow(VisionFrame frame, int row)
    {
        if (!frame.IsLoanActive)
            throw Failure("PlanarFrameLoanInactive");
        try
        {
            var source = frame.GetRowSpan(row);
            if (source.Length < frame.Metadata.ValidRowBytes)
                throw Failure("PlanarFrameRowUnavailable");
            return source[..frame.Metadata.ValidRowBytes];
        }
        catch (CalibrationProcedureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ObjectDisposedException or
            InvalidOperationException)
        {
            throw Failure("PlanarFrameRowUnavailable");
        }
    }

    private static string ComputePixelHash(VisionFrame frame,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var row = 0; row < frame.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowBytes = ReadValidRow(frame, row).ToArray();
            hash.AppendData(rowBytes);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void CopyRowToMat(Mat destination, int row, byte[] bytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pointer = destination.Ptr(row);
        cancellationToken.ThrowIfCancellationRequested();
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void ValidateFrame(VisionFrame frame, PlanarHomographyInput input)
    {
        if (!frame.IsLoanActive)
            throw Failure("PlanarFrameLoanInactive");
        if (frame.LogicalCameraRole != input.LogicalCameraRole ||
            frame.EffectiveCameraConfiguration != input.ExpectedConfiguration ||
            frame.Width != input.ImageWidth || frame.Height != input.ImageHeight)
            throw Failure("PlanarFrameConfigurationMismatch");
        if ((long)frame.Width * frame.Height > MaximumImagePixels)
            throw Failure("PlanarImageCapacityExceeded");
        var minimumBytes = frame.PixelFormat switch
        {
            VisionPixelFormat.Mono8 => frame.Width,
            VisionPixelFormat.Mono16 => checked(frame.Width * 2),
            VisionPixelFormat.Bgr24 => checked(frame.Width * 3),
            _ => throw Failure("PlanarPixelFormatUnsupported")
        };
        if (frame.Metadata.ValidRowBytes != minimumBytes)
            throw Failure("PlanarFrameLayoutInvalid");
        if (frame.PixelFormat == VisionPixelFormat.Mono16 &&
            frame.ValidBits is not (10 or 12 or 16))
            throw Failure("PlanarMono16ValidBitsInvalid");
    }

    private static CalibrationExtractionReceipt CreateExtractionReceipt(Guid sessionId,
        Guid frameId, string sourceHash, CalibrationProcedureInputPayload inputPayload,
        string pixelHash, IReadOnlyList<CalibrationImageFeature> features) =>
        new(PlanarHomographyContracts.ExtractionReceipt,
            ComputeExtractionReceiptSignature(sessionId, frameId, sourceHash,
                inputPayload, pixelHash, features));

    private static void ValidateExtractionReceipt(
        CalibrationObservationInput observation,
        CalibrationProcedureInputPayload inputPayload, string pixelHash,
        IReadOnlyList<CalibrationImageFeature> features)
    {
        var receipt = observation.Receipt;
        if (receipt is null || receipt.Format != PlanarHomographyContracts.ExtractionReceipt ||
            receipt.Length != 32)
            throw Failure("PlanarExtractionReceiptInvalid");

        var expected = ComputeExtractionReceiptSignature(observation.SessionId,
            observation.FrameId, observation.SourceHash, inputPayload, pixelHash,
            features);
        var actual = receipt.GetBytes();
        if (actual.Length != expected.Length ||
            !CryptographicOperations.FixedTimeEquals(actual, expected))
            throw Failure("PlanarExtractionReceiptInvalid");
    }

    private static byte[] ComputeExtractionReceiptSignature(Guid sessionId,
        Guid frameId, string sourceHash, CalibrationProcedureInputPayload inputPayload,
        string pixelHash, IReadOnlyList<CalibrationImageFeature> features)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(ExtractionReceiptDomain);
        WriteContract(writer, PlanarHomographyContracts.Procedure);
        WriteContract(writer, inputPayload.InputContract);
        WriteText(writer, inputPayload.CanonicalBytesHash);
        WriteText(writer, inputPayload.ContentHash);
        var inputBytes = inputPayload.GetBytes();
        writer.Write(inputBytes.Length);
        writer.Write(inputBytes);
        writer.Write(sessionId.ToByteArray());
        writer.Write(frameId.ToByteArray());
        WriteText(writer, sourceHash);
        WriteText(writer, pixelHash);
        writer.Write(features.Count);
        foreach (var feature in features)
            WriteText(writer, feature.ContentHash);
        writer.Flush();
        using var hmac = new HMACSHA256(ExtractionReceiptKey);
        return hmac.ComputeHash(stream.ToArray());
    }

    private static void WriteContract(BinaryWriter writer,
        RecipeContractReference contract)
    {
        WriteText(writer, contract.Id);
        WriteText(writer, contract.Version);
        WriteText(writer, contract.ContentHash);
    }

    private static void WriteText(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static Mat NewMat(MatType type, CancellationToken cancellationToken,
        int? rows = null, int? columns = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mat = rows.HasValue && columns.HasValue
            ? new Mat(rows.Value, columns.Value, type)
            : new Mat();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return mat;
        }
        catch
        {
            mat.Dispose();
            throw;
        }
    }

    private static void SetMatDouble(Mat matrix, int row, int column, double value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        matrix.Set(row, column, value);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static double Distance(PlanarPoint left, PlanarPoint right) =>
        Math.Sqrt(Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));

    private static bool Finite(PlanarPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);

    private static CalibrationProcedureException Failure(string reasonCode) =>
        new(reasonCode);

    private readonly record struct DetectedMarker(int MarkerId, Point2f[] Corners);

    private readonly record struct PlanarCorrespondence(int MarkerId, int CornerIndex,
        CalibrationImageFeature Feature, PlanarPoint Observed, PlanarPoint Physical);

    private readonly record struct Normalization(double CenterX, double CenterY,
        double Scale);
}
