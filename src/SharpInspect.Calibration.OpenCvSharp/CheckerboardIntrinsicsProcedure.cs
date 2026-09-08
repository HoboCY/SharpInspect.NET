using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using global::OpenCvSharp;
using SharpInspect.Abstractions;

namespace SharpInspect.Calibration.OpenCvSharp;

public sealed class CheckerboardIntrinsicsProcedure : ICalibrationProcedure<CheckerboardIntrinsicsInput>
{
    private const double RankRatioFloor = 1e-5;
    private const double MinimumHullAreaFraction = 1e-8;
    private static readonly byte[] ExtractionReceiptKey = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] ExtractionReceiptDomain =
        Encoding.ASCII.GetBytes("sharpinspect-checkerboard-extraction-receipt-v1");

    public CalibrationProcedureDescriptor Descriptor { get; } = new(CheckerboardIntrinsicsContracts.Procedure,
        CheckerboardIntrinsicsContracts.Input, CalibrationKind.Intrinsic);

    public ICalibrationInputCodec<CheckerboardIntrinsicsInput> InputCodec { get; } =
        new CheckerboardIntrinsicsInputCodec();

    public ValueTask<CalibrationExtractionResult> ExtractAsync(
        CalibrationExtractionContext<CheckerboardIntrinsicsInput> context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFrame(context.Frame, context.Input);
        var inputPayload = InputCodec.EncodePayload(context.Input);

        // The procedure receives only the public managed row contract.  In particular,
        // it does not ask VisionFrame for the internal native-read hold: Runtime may
        // supply a CalibrationBorrowedFrame which intentionally has no such bridge.
        using var image = CreateDisplayImage(context.Frame, cancellationToken, out var pixelHash);
        using var gray = NewMat(MatType.CV_8UC1, cancellationToken);
        Mat searchImage = image;
        if (context.Frame.PixelFormat == VisionPixelFormat.Bgr24)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
            cancellationToken.ThrowIfCancellationRequested();
            searchImage = gray;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var found = Cv2.FindChessboardCornersSB(searchImage,
            new Size(context.Input.InnerColumns, context.Input.InnerRows), out var corners,
            ChessboardFlags.NormalizeImage | ChessboardFlags.Exhaustive | ChessboardFlags.Accuracy);
        cancellationToken.ThrowIfCancellationRequested();
        if (!found || corners.Length != context.Input.CornerCount)
        {
            var receipt = CreateExtractionReceipt(context.SessionId, context.FrameId,
                context.SourceHash, inputPayload, pixelHash, Array.Empty<CalibrationImageFeature>());
            return ValueTask.FromResult(new CalibrationExtractionResult(null,
                new[] { new CalibrationProcedureDiagnostic("CheckerboardNotFound") }, receipt));
        }

        var features = corners.Select((point, index) =>
            new CalibrationImageFeature(FeatureId(index), point.X, point.Y)).ToArray();
        var extractionReceipt = CreateExtractionReceipt(context.SessionId, context.FrameId,
            context.SourceHash, inputPayload, pixelHash, features);
        return ValueTask.FromResult(new CalibrationExtractionResult(features,
            new[] { new CalibrationProcedureDiagnostic("CheckerboardDetected"),
                new CalibrationProcedureDiagnostic("CornerOrder", "DetectorRowMajorSymmetricOrigin") },
            extractionReceipt));
    }

    public ValueTask<CalibrationProcedureComputationResult> ComputeAsync(
        CalibrationComputationContext<CheckerboardIntrinsicsInput> context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var input = context.Input;
        if (context.Observations.Count < 3)
            throw Failure("CheckerboardViewsInsufficient");

        var inputPayload = InputCodec.EncodePayload(input);
        ValidateEvidenceCapacity(input, context.Observations.Count);

        var sourceHashes = new HashSet<string>(StringComparer.Ordinal);
        var imageHashes = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<ValidatedView>(context.Observations.Count);

        // Everything in this pass is managed validation.  It must finish before any
        // candidate is sent to native OpenCV, so malformed/duplicate/degenerate data
        // cannot be hidden by a robust estimator.
        foreach (var observation in context.Observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateFrame(observation.Frame, input);
            if (!sourceHashes.Add(observation.SourceHash))
                throw Failure("CheckerboardDuplicateSourceHash");

            var imageHash = ComputePixelHash(observation.Frame, cancellationToken);
            if (!imageHashes.Add(imageHash))
                throw Failure("CheckerboardDuplicateImage");

            var validatedFeatures = ValidateFeatures(observation, input);
            ValidateExtractionReceipt(observation, inputPayload, imageHash,
                validatedFeatures.Features);
            var coverage = ComputeCoverage(validatedFeatures.ImagePoints,
                input.ImageWidth, input.ImageHeight);
            if (coverage.HullAreaFraction < MinimumHullAreaFraction)
                throw Failure("CheckerboardViewDegenerate");
            candidates.Add(new ValidatedView(observation, validatedFeatures.Features,
                validatedFeatures.ImagePoints, coverage));
        }

        var rankRatio = ComputeConstraintRankRatio(input, candidates, cancellationToken);
        var objectPoints = CreateObjectPoints(input);
        var objectPointSets = candidates.Select(_ => objectPoints).ToArray();
        var imagePointSets = candidates.Select(candidate => candidate.ImagePoints).ToArray();
        var cameraMatrix = new[,]
        {
            { (double)input.ImageWidth, 0d, input.ImageWidth / 2d },
            { 0d, (double)input.ImageHeight, input.ImageHeight / 2d },
            { 0d, 0d, 1d }
        };
        var distortion = new double[5];
        Vec3d[] rotations;
        Vec3d[] translations;
        double nativeRms;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            nativeRms = Cv2.CalibrateCamera(objectPointSets, imagePointSets,
                new Size(input.ImageWidth, input.ImageHeight), cameraMatrix, distortion,
                out rotations, out translations, CalibrationFlags.None,
                TermCriteria.Both(100, 1e-10));
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OpenCvSharpException)
        {
            throw Failure("CheckerboardCalibrationFailed");
        }

        if (!double.IsFinite(nativeRms) || nativeRms < 0 || rotations.Length != candidates.Count ||
            translations.Length != candidates.Count || !ValidCalibration(cameraMatrix, distortion))
            throw Failure("CheckerboardCalibrationResultInvalid");

        var coefficients = new CheckerboardIntrinsicsCoefficients(input.ImageWidth, input.ImageHeight,
            cameraMatrix[0, 0], cameraMatrix[1, 1], cameraMatrix[0, 2], cameraMatrix[1, 2],
            distortion[0], distortion[1], distortion[2], distortion[3], distortion[4]);
        var evidenceViews = new List<CheckerboardViewEvidence>(candidates.Count);
        var totalSquaredResidual = 0d;
        var totalPointCount = 0;
        var maximumViewRms = 0d;
        var maximumPointResidual = 0d;

        for (var viewIndex = 0; viewIndex < candidates.Count; viewIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rotation = ToArray(rotations[viewIndex]);
            var translation = ToArray(translations[viewIndex]);
            ValidatePose(rotation, translation);

            double[,] rotationMatrix;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Cv2.Rodrigues(rotation, out rotationMatrix, out _);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OpenCvSharpException)
            {
                throw Failure("CheckerboardPoseReconstructionFailed");
            }
            ValidatePositiveDepth(rotationMatrix, translation, objectPoints);

            Point2f[] projected;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Cv2.ProjectPoints(objectPoints, rotation, translation, cameraMatrix, distortion,
                    out projected, out _);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OpenCvSharpException)
            {
                throw Failure("CheckerboardProjectionFailed");
            }
            if (projected.Length != objectPoints.Length)
                throw Failure("CheckerboardProjectionResultInvalid");

            var residuals = new List<CheckerboardPointResidual>(projected.Length);
            var viewSquaredResidual = 0d;
            for (var pointIndex = 0; pointIndex < projected.Length; pointIndex++)
            {
                var observed = candidates[viewIndex].Features[pointIndex];
                var dx = observed.PixelX - projected[pointIndex].X;
                var dy = observed.PixelY - projected[pointIndex].Y;
                if (!double.IsFinite(dx) || !double.IsFinite(dy))
                    throw Failure("CheckerboardResidualInvalid");
                var squared = dx * dx + dy * dy;
                viewSquaredResidual += squared;
                totalSquaredResidual += squared;
                var magnitude = Math.Sqrt(squared);
                if (magnitude > maximumPointResidual) maximumPointResidual = magnitude;
                residuals.Add(new CheckerboardPointResidual(pointIndex, dx, dy));
            }

            var viewRms = Math.Sqrt(viewSquaredResidual / projected.Length);
            if (!double.IsFinite(viewRms)) throw Failure("CheckerboardResidualInvalid");
            if (viewRms > maximumViewRms) maximumViewRms = viewRms;
            totalPointCount += projected.Length;
            var coverage = candidates[viewIndex].Coverage;
            evidenceViews.Add(new CheckerboardViewEvidence(
                candidates[viewIndex].Observation.FrameId,
                candidates[viewIndex].Observation.SourceHash,
                rotation, translation, coverage.MinimumX, coverage.MinimumY,
                coverage.MaximumX, coverage.MaximumY, coverage.HullAreaFraction,
                viewRms, residuals));
        }

        if (totalPointCount == 0) throw Failure("CheckerboardPointSetEmpty");
        var aggregateRms = Math.Sqrt(totalSquaredResidual / totalPointCount);
        if (!double.IsFinite(aggregateRms)) throw Failure("CheckerboardResidualInvalid");

        CheckerboardIntrinsicsEvidence evidence;
        try
        {
            evidence = new CheckerboardIntrinsicsEvidence(inputPayload.ContentHash,
                aggregateRms, rankRatio, evidenceViews);
        }
        catch (ArgumentException)
        {
            throw Failure("CheckerboardEvidenceInvalid");
        }

        CalibrationComputationEvidencePayload evidencePayload;
        try
        {
            evidencePayload = CheckerboardIntrinsicsResultCodec.EncodeEvidence(evidence);
        }
        catch (ArgumentException)
        {
            throw Failure("CheckerboardEvidenceSizeExceeded");
        }

        var metrics = new[]
        {
            new CalibrationQualityMetric("RmsPixels", aggregateRms, "pixels"),
            new CalibrationQualityMetric("ValidViewCount", candidates.Count, "views"),
            new CalibrationQualityMetric("PointCount", totalPointCount, "points"),
            new CalibrationQualityMetric("MaxViewRmsPixels", maximumViewRms, "pixels"),
            new CalibrationQualityMetric("MaxPointResidualPixels", maximumPointResidual, "pixels"),
            new CalibrationQualityMetric("ConstraintRankRatio", rankRatio, null),
            new CalibrationQualityMetric("CoverageMinimumX", evidenceViews.Min(view => view.MinimumX), "normalized"),
            new CalibrationQualityMetric("CoverageMinimumY", evidenceViews.Min(view => view.MinimumY), "normalized"),
            new CalibrationQualityMetric("CoverageMaximumX", evidenceViews.Max(view => view.MaximumX), "normalized"),
            new CalibrationQualityMetric("CoverageMaximumY", evidenceViews.Max(view => view.MaximumY), "normalized"),
            new CalibrationQualityMetric("CoverageMinimumHullAreaFraction",
                evidenceViews.Min(view => view.HullAreaFraction), "fraction"),
            new CalibrationQualityMetric("CoverageMaximumHullAreaFraction",
                evidenceViews.Max(view => view.HullAreaFraction), "fraction"),
            new CalibrationQualityMetric("Fx", coefficients.Fx, "pixels"),
            new CalibrationQualityMetric("Fy", coefficients.Fy, "pixels"),
            new CalibrationQualityMetric("Cx", coefficients.Cx, "pixels"),
            new CalibrationQualityMetric("Cy", coefficients.Cy, "pixels"),
            new CalibrationQualityMetric("K1", coefficients.K1, null),
            new CalibrationQualityMetric("K2", coefficients.K2, null),
            new CalibrationQualityMetric("P1", coefficients.P1, null),
            new CalibrationQualityMetric("P2", coefficients.P2, null),
            new CalibrationQualityMetric("K3", coefficients.K3, null)
        };

        return ValueTask.FromResult(new CalibrationProcedureComputationResult(
            CheckerboardIntrinsicsResultCodec.EncodeCoefficients(coefficients), metrics,
            new[] { new CalibrationProcedureDiagnostic("CheckerboardCalibrationComputed") },
            evidencePayload));
    }

    internal static string FeatureId(int index) =>
        "corner-" + index.ToString("D4", CultureInfo.InvariantCulture);

    private static CalibrationExtractionReceipt CreateExtractionReceipt(Guid sessionId,
        Guid frameId, string sourceHash, CalibrationProcedureInputPayload inputPayload,
        string pixelHash, IReadOnlyList<CalibrationImageFeature> features) =>
        new(CheckerboardIntrinsicsContracts.ExtractionReceipt,
            ComputeExtractionReceiptSignature(sessionId, frameId, sourceHash, inputPayload,
                pixelHash, features));

    private static void ValidateExtractionReceipt(CalibrationObservationInput observation,
        CalibrationProcedureInputPayload inputPayload, string pixelHash,
        IReadOnlyList<CalibrationImageFeature> features)
    {
        var receipt = observation.Receipt;
        if (receipt is null || receipt.Format != CheckerboardIntrinsicsContracts.ExtractionReceipt ||
            receipt.Length != 32)
            throw Failure("CheckerboardExtractionReceiptInvalid");

        var expected = ComputeExtractionReceiptSignature(observation.SessionId,
            observation.FrameId, observation.SourceHash, inputPayload, pixelHash, features);
        var actual = receipt.GetBytes();
        if (actual.Length != expected.Length ||
            !CryptographicOperations.FixedTimeEquals(actual, expected))
            throw Failure("CheckerboardExtractionReceiptInvalid");
    }

    private static byte[] ComputeExtractionReceiptSignature(Guid sessionId, Guid frameId,
        string sourceHash, CalibrationProcedureInputPayload inputPayload, string pixelHash,
        IReadOnlyList<CalibrationImageFeature> features)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(ExtractionReceiptDomain);
        WriteContract(writer, CheckerboardIntrinsicsContracts.Procedure);
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

    private static void WriteContract(BinaryWriter writer, RecipeContractReference contract)
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

    private static CalibrationProcedureException Failure(string reasonCode) =>
        new(reasonCode);

    private static Mat NewMat(MatType type, CancellationToken cancellationToken, int? rows = null, int? columns = null)
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

    private static Mat CreateDisplayImage(VisionFrame frame, CancellationToken cancellationToken,
        out string pixelHash)
    {
        pixelHash = string.Empty;
        var type = frame.PixelFormat switch
        {
            VisionPixelFormat.Mono8 => MatType.CV_8UC1,
            VisionPixelFormat.Mono16 => MatType.CV_8UC1,
            VisionPixelFormat.Bgr24 => MatType.CV_8UC3,
            _ => throw Failure("CheckerboardPixelFormatUnsupported")
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
                    var validBits = frame.ValidBits ?? throw Failure("CheckerboardMono16ValidBitsMissing");
                    if (validBits is not (10 or 12 or 16))
                        throw Failure("CheckerboardMono16ValidBitsInvalid");
                    var maximum = (uint)((1 << validBits) - 1);
                    var display = new byte[frame.Width];
                    for (var column = 0; column < frame.Width; column++)
                    {
                        var offset = checked(column * 2);
                        var sample = (uint)(row[offset] | (row[offset + 1] << 8));
                        if (sample > maximum) throw Failure("CheckerboardMono16HighBitsInvalid");
                        display[column] = (byte)((sample * 255u + maximum / 2u) / maximum);
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
        if (!frame.IsLoanActive) throw Failure("CheckerboardFrameLoanInactive");
        try
        {
            var source = frame.GetRowSpan(row);
            if (source.Length < frame.Metadata.ValidRowBytes)
                throw Failure("CheckerboardFrameRowUnavailable");
            return source[..frame.Metadata.ValidRowBytes];
        }
        catch (CalibrationProcedureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            throw Failure("CheckerboardFrameRowUnavailable");
        }
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

    private static void ValidateFrame(VisionFrame frame, CheckerboardIntrinsicsInput input)
    {
        if (!frame.IsLoanActive) throw Failure("CheckerboardFrameLoanInactive");
        if (frame.LogicalCameraRole != input.LogicalCameraRole ||
            frame.EffectiveCameraConfiguration != input.ExpectedConfiguration ||
            frame.Width != input.ImageWidth || frame.Height != input.ImageHeight)
            throw Failure("CheckerboardFrameConfigurationMismatch");
        if ((long)frame.Width * frame.Height > 16_777_216)
            throw Failure("CheckerboardImageCapacityExceeded");
        var minimumBytes = frame.PixelFormat switch
        {
            VisionPixelFormat.Mono8 => frame.Width,
            VisionPixelFormat.Mono16 => checked(frame.Width * 2),
            VisionPixelFormat.Bgr24 => checked(frame.Width * 3),
            _ => throw Failure("CheckerboardPixelFormatUnsupported")
        };
        if (frame.Metadata.ValidRowBytes != minimumBytes)
            throw Failure("CheckerboardFrameLayoutInvalid");
    }

    private static string ComputePixelHash(VisionFrame frame, CancellationToken cancellationToken)
    {
        var length = checked(frame.Metadata.ValidRowBytes * frame.Height);
        var pixels = new byte[length];
        for (var row = 0; row < frame.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadValidRow(frame, row).CopyTo(pixels.AsSpan(checked(row * frame.Metadata.ValidRowBytes),
                frame.Metadata.ValidRowBytes));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private static ValidatedFeatures ValidateFeatures(CalibrationObservationInput observation,
        CheckerboardIntrinsicsInput input)
    {
        var features = observation.Features;
        if (features.Count != input.CornerCount)
            throw Failure("CheckerboardFeatureCountInvalid");

        for (var index = 0; index < features.Count; index++)
        {
            if (features[index].StableFeatureId != FeatureId(index))
                throw Failure("CheckerboardFeatureOrderInvalid");
        }

        var originalFeatures = features.ToArray();
        var points = new Point2f[features.Count];
        for (var listIndex = 0; listIndex < features.Count; listIndex++)
        {
            var feature = features[listIndex];
            if (feature.PixelX < 0 || feature.PixelX >= input.ImageWidth ||
                feature.PixelY < 0 || feature.PixelY >= input.ImageHeight ||
                !double.IsFinite(feature.PixelX) || !double.IsFinite(feature.PixelY))
                throw Failure("CheckerboardFeatureOutsideImage");
            var x = (float)feature.PixelX;
            var y = (float)feature.PixelY;
            if (!float.IsFinite(x) || !float.IsFinite(y))
                throw Failure("CheckerboardFeatureOutsideImage");
            points[listIndex] = new Point2f(x, y);
        }
        return new ValidatedFeatures(originalFeatures, points);
    }

    private static Coverage ComputeCoverage(IReadOnlyList<Point2f> points, int width, int height)
    {
        var minX = points.Min(point => (double)point.X);
        var minY = points.Min(point => (double)point.Y);
        var maxX = points.Max(point => (double)point.X);
        var maxY = points.Max(point => (double)point.Y);
        if (!(maxX > minX) || !(maxY > minY)) throw Failure("CheckerboardViewDegenerate");

        var hull = ConvexHull(points.Select(point => new PixelPoint(point.X, point.Y)).ToArray());
        var area = PolygonArea(hull);
        var areaFraction = area / (width * (double)height);
        if (!double.IsFinite(areaFraction) || areaFraction <= 0)
            throw Failure("CheckerboardViewDegenerate");
        return new Coverage(minX / width, minY / height, maxX / width, maxY / height, areaFraction);
    }

    private static PixelPoint[] ConvexHull(PixelPoint[] points)
    {
        var sorted = points.OrderBy(point => point.X).ThenBy(point => point.Y).ToArray();
        if (sorted.Length <= 1) return sorted;
        var lower = new List<PixelPoint>();
        foreach (var point in sorted)
        {
            while (lower.Count >= 2 && Cross(lower[^1] - lower[^2], point - lower[^1]) <= 0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(point);
        }
        var upper = new List<PixelPoint>();
        for (var index = sorted.Length - 1; index >= 0; index--)
        {
            var point = sorted[index];
            while (upper.Count >= 2 && Cross(upper[^1] - upper[^2], point - upper[^1]) <= 0)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(point);
        }
        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower.ToArray();
    }

    private static double Cross(PixelPoint left, PixelPoint right) =>
        left.X * right.Y - left.Y * right.X;

    private static double PolygonArea(IReadOnlyList<PixelPoint> polygon)
    {
        if (polygon.Count < 3) return 0;
        var sum = 0d;
        for (var index = 0; index < polygon.Count; index++)
        {
            var next = polygon[(index + 1) % polygon.Count];
            sum += polygon[index].X * next.Y - next.X * polygon[index].Y;
        }
        return Math.Abs(sum) / 2d;
    }

    private static void ValidateEvidenceCapacity(CheckerboardIntrinsicsInput input, int viewCount)
    {
        // This is the exact v1 binary size: fixed header + fixed view fields +
        // (ushort index, float64 dx, float64 dy) for each retained corner.
        var bytes = checked(4L + 32L + 8L + 8L + 4L +
            viewCount * (16L + 32L + 6L * sizeof(double) + 6L * sizeof(double) + 4L +
                input.CornerCount * (sizeof(ushort) + 2L * sizeof(double))));
        if (bytes > CalibrationComputationEvidencePayload.MaximumBytes)
            throw Failure("CheckerboardEvidenceSizeExceeded");
    }

    private static Point3f[] CreateObjectPoints(CheckerboardIntrinsicsInput input)
    {
        var points = new Point3f[input.CornerCount];
        var index = 0;
        for (var row = 0; row < input.InnerRows; row++)
        for (var column = 0; column < input.InnerColumns; column++)
            points[index++] = new Point3f((float)(column * input.SquareSizeMillimeters),
                (float)(row * input.SquareSizeMillimeters), 0f);
        return points;
    }

    private static double ComputeConstraintRankRatio(CheckerboardIntrinsicsInput input,
        IReadOnlyList<ValidatedView> views, CancellationToken cancellationToken)
    {
        var rows = new double[checked(views.Count * 2), 6];
        var boardWidth = (input.InnerColumns - 1) * input.SquareSizeMillimeters;
        var boardHeight = (input.InnerRows - 1) * input.SquareSizeMillimeters;
        var boardScale = Math.Max(boardWidth, boardHeight);
        var boardCenterX = boardWidth / 2d;
        var boardCenterY = boardHeight / 2d;
        var imageScale = Math.Max(input.ImageWidth, input.ImageHeight);
        var imageCenterX = input.ImageWidth / 2d;
        var imageCenterY = input.ImageHeight / 2d;
        var objectPoints = CreateObjectPoints(input);

        for (var viewIndex = 0; viewIndex < views.Count; viewIndex++)
        {
            var source = new Point2d[objectPoints.Length];
            var target = new Point2d[objectPoints.Length];
            for (var pointIndex = 0; pointIndex < objectPoints.Length; pointIndex++)
            {
                var objectPoint = objectPoints[pointIndex];
                var imagePoint = views[viewIndex].ImagePoints[pointIndex];
                source[pointIndex] = new Point2d((objectPoint.X - boardCenterX) / boardScale,
                    (objectPoint.Y - boardCenterY) / boardScale);
                target[pointIndex] = new Point2d((imagePoint.X - imageCenterX) / imageScale,
                    (imagePoint.Y - imageCenterY) / imageScale);
            }

            Mat homography;
            using var homographyMask = NewMat(MatType.CV_8UC1, cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                homography = Cv2.FindHomography(source, target, HomographyMethods.None,
                    0d, homographyMask, 2000, 0.995d);
            }
            catch (OpenCvSharpException)
            {
                throw Failure("CheckerboardHomographyFailed");
            }
            using (homography)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var homographyEmpty = homography.Empty();
                cancellationToken.ThrowIfCancellationRequested();
                var homographyRows = homography.Rows;
                cancellationToken.ThrowIfCancellationRequested();
                var homographyColumns = homography.Cols;
                cancellationToken.ThrowIfCancellationRequested();
                if (homographyEmpty || homographyRows != 3 || homographyColumns != 3)
                    throw Failure("CheckerboardHomographyInvalid");
                var h11 = GetMatDouble(homography, 0, 0, cancellationToken);
                var h21 = GetMatDouble(homography, 1, 0, cancellationToken);
                var h31 = GetMatDouble(homography, 2, 0, cancellationToken);
                var h12 = GetMatDouble(homography, 0, 1, cancellationToken);
                var h22 = GetMatDouble(homography, 1, 1, cancellationToken);
                var h32 = GetMatDouble(homography, 2, 1, cancellationToken);
                var first = new[]
                {
                    h11 * h12,
                    h11 * h22 + h12 * h21,
                    h21 * h22,
                    h31 * h12 + h11 * h32,
                    h31 * h22 + h21 * h32,
                    h31 * h32
                };
                var second = new[]
                {
                    h11 * h11 - h12 * h12,
                    2d * (h11 * h21 - h12 * h22),
                    h21 * h21 - h22 * h22,
                    2d * (h11 * h31 - h12 * h32),
                    2d * (h21 * h31 - h22 * h32),
                    h31 * h31 - h32 * h32
                };
                for (var column = 0; column < 6; column++)
                {
                    rows[viewIndex * 2, column] = first[column];
                    rows[viewIndex * 2 + 1, column] = second[column];
                    if (!double.IsFinite(first[column]) || !double.IsFinite(second[column]))
                        throw Failure("CheckerboardConstraintInvalid");
                }
            }
        }

        using var matrix = NewMat(MatType.CV_64FC1, cancellationToken, rows.GetLength(0), 6);
        for (var row = 0; row < rows.GetLength(0); row++)
        for (var column = 0; column < 6; column++)
            SetMatDouble(matrix, row, column, rows[row, column], cancellationToken);

        SVD svd;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            svd = new SVD(matrix, SVD.Flags.NoUV);
        }
        catch (OpenCvSharpException)
        {
            throw Failure("CheckerboardConstraintSvdFailed");
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
                throw Failure("CheckerboardConstraintSvdFailed");
            }
            using (singularValues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var copied = singularValues.GetArray(out double[] values);
                cancellationToken.ThrowIfCancellationRequested();
                if (!copied || values is null || values.Length < 2 ||
                    values.Any(value => !double.IsFinite(value) || value < 0))
                    throw Failure("CheckerboardConstraintSvdInvalid");
                Array.Sort(values);
                var largest = values[^1];
                var secondSmallest = values[1];
                var ratio = secondSmallest / largest;
                if (!double.IsFinite(ratio) || largest <= 0 || ratio < RankRatioFloor)
                    throw Failure("CheckerboardConstraintRankInsufficient");
                return ratio;
            }
        }
    }

    private static double GetMatDouble(Mat mat, int row, int column,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = mat.Get<double>(row, column);
        cancellationToken.ThrowIfCancellationRequested();
        return value;
    }

    private static void SetMatDouble(Mat mat, int row, int column, double value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        mat.Set(row, column, value);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool ValidCalibration(double[,] cameraMatrix, double[] distortion)
    {
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
            if (!double.IsFinite(cameraMatrix[row, column])) return false;
        if (cameraMatrix[0, 0] <= 0 || cameraMatrix[1, 1] <= 0 ||
            distortion.Length < 5 || distortion.Any(value => !double.IsFinite(value))) return false;
        return true;
    }

    private static double[] ToArray(Vec3d value) => new[] { value[0], value[1], value[2] };

    private static void ValidatePose(double[] rotation, double[] translation)
    {
        if (rotation.Length != 3 || translation.Length != 3 ||
            rotation.Concat(translation).Any(value => !double.IsFinite(value)))
            throw Failure("CheckerboardPoseInvalid");
    }

    private static void ValidatePositiveDepth(double[,] rotation, double[] translation,
        IReadOnlyList<Point3f> objectPoints)
    {
        if (rotation.GetLength(0) != 3 || rotation.GetLength(1) != 3)
            throw Failure("CheckerboardPoseInvalid");
        foreach (var point in objectPoints)
        {
            var depth = rotation[2, 0] * point.X + rotation[2, 1] * point.Y +
                rotation[2, 2] * point.Z + translation[2];
            if (!double.IsFinite(depth) || depth <= 0)
                throw Failure("CheckerboardPointDepthInvalid");
        }
    }

    private readonly record struct PixelPoint(double X, double Y)
    {
        public static PixelPoint operator -(PixelPoint left, PixelPoint right) =>
            new(left.X - right.X, left.Y - right.Y);
    }

    private readonly record struct Coverage(double MinimumX, double MinimumY,
        double MaximumX, double MaximumY, double HullAreaFraction);

    private readonly record struct ValidatedFeatures(CalibrationImageFeature[] Features,
        Point2f[] ImagePoints);

    private sealed record ValidatedView(CalibrationObservationInput Observation,
        CalibrationImageFeature[] Features, Point2f[] ImagePoints, Coverage Coverage);
}
