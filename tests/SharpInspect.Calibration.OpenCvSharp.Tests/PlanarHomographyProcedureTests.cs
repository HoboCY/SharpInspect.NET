using System.Security.Cryptography;
using SharpInspect.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace SharpInspect.Calibration.OpenCvSharp.Tests;

public sealed class PlanarHomographyProcedureTests
{
    private readonly ITestOutputHelper _output;
    public PlanarHomographyProcedureTests(ITestOutputHelper output) => _output = output;
    private static readonly Guid SessionId =
        Guid.Parse("12600000-0000-0000-0000-000000000001");

    [Fact]
    public async Task V126_C01_Frozen_four_marker_dataset_recovers_forward_inverse_mapping_and_evidence()
    {
        using var sample = await PlanarSample.CreateAsync(SyntheticPlanarFixture.Render());
        Assert.Equal(SyntheticPlanarFixture.MarkerCount * 4, sample.Extraction.Features.Count);
        Assert.Equal(sample.Extraction.Features.Count,
            sample.Extraction.Features.Select(feature => feature.StableFeatureId)
                .Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(sample.Extraction.Receipt);

        var independentCornerError = sample.Extraction.Features.Select((feature, index) =>
            Distance(new PlanarPoint(feature.PixelX, feature.PixelY),
                ToPlanarPoint(SyntheticPlanarFixture.ExpectedForward(
                    SyntheticPlanarFixture.PhysicalCorners[index])))).Max();
        Assert.InRange(independentCornerError, 0d,
            SyntheticPlanarFixture.CornerExtractionMaxErrorPixels);

        var result = await new PlanarHomographyProcedure().ComputeAsync(
            new CalibrationComputationContext<PlanarHomographyInput>(sample.Input,
                new[] { sample.Observation }));
        var coefficients = PlanarHomographyResultCodec.DecodeCoefficients(result.Coefficients);
        Assert.NotNull(result.Evidence);
        var evidence = PlanarHomographyResultCodec.DecodeEvidence(result.Evidence!);

        Assert.Equal(SyntheticPlanarFixture.MarkerCount * 4, evidence.Residuals.Count);
        Assert.Empty(evidence.MissingMarkerIds);
        Assert.True(double.IsFinite(evidence.ConstraintRankRatio));
        Assert.True(double.IsFinite(evidence.InverseClosureError));
        // Closure measures a point's round trip through the two matrices,
        // independently of the nonzero observed-vs-fitted residual.
        Assert.InRange(evidence.InverseClosureError, 0d, 1e-12);
        Assert.True(double.IsFinite(evidence.RmsMillimeters));
        Assert.True(double.IsFinite(evidence.MaxMillimeters));
        Assert.True(double.IsFinite(evidence.RmsPixels));
        Assert.True(double.IsFinite(evidence.MaxPixels));

        foreach (var physical in SyntheticPlanarFixture.PhysicalCorners)
        {
            var expected = SyntheticPlanarFixture.ExpectedForward(physical);
            Assert.True(coefficients.TryPlaneToImage(
                new PlanarPoint(physical.X, physical.Y), out var projected));
            Assert.InRange(Distance(projected, new PlanarPoint(expected.X, expected.Y)),
                0d, SyntheticPlanarFixture.ForwardPredictionMaxErrorPixels);
        }

        foreach (var physical in HeldOutPhysicalGrid())
        {
            var expected = SyntheticPlanarFixture.ExpectedForward(physical);
            Assert.True(coefficients.TryPlaneToImage(
                new PlanarPoint(physical.X, physical.Y), out var projected));
            Assert.InRange(Distance(projected, ToPlanarPoint(expected)),
                0d, SyntheticPlanarFixture.ForwardPredictionMaxErrorPixels);
            Assert.True(coefficients.TryImageToPlane(
                ToPlanarPoint(expected), out var recovered));
            Assert.InRange(Distance(recovered, ToPlanarPoint(physical)),
                0d, SyntheticPlanarFixture.InversePredictionMaxErrorMillimeters);
        }

        var independentSquaredMillimeters = 0d;
        var independentSquaredPixels = 0d;
        foreach (var residual in evidence.Residuals)
        {
            Assert.True(TryIndependentProject(coefficients.ImageToPlane,
                residual.ObservedPixels, out var reconstructedPhysical));
            Assert.True(TryIndependentProject(coefficients.PlaneToImage,
                residual.PhysicalMillimeters, out var reconstructedPixels));
            var physicalResidual = new PlanarPoint(
                residual.PhysicalMillimeters.X - reconstructedPhysical.X,
                residual.PhysicalMillimeters.Y - reconstructedPhysical.Y);
            var pixelResidual = new PlanarPoint(
                residual.ObservedPixels.X - reconstructedPixels.X,
                residual.ObservedPixels.Y - reconstructedPixels.Y);
            Assert.InRange(Math.Abs(physicalResidual.X -
                residual.ImageToPlaneResidualMillimeters.X), 0d, 1e-9d);
            Assert.InRange(Math.Abs(physicalResidual.Y -
                residual.ImageToPlaneResidualMillimeters.Y), 0d, 1e-9d);
            Assert.InRange(Math.Abs(pixelResidual.X -
                residual.PlaneToImageResidualPixels.X), 0d, 1e-9d);
            Assert.InRange(Math.Abs(pixelResidual.Y -
                residual.PlaneToImageResidualPixels.Y), 0d, 1e-9d);
            independentSquaredMillimeters += physicalResidual.X * physicalResidual.X +
                physicalResidual.Y * physicalResidual.Y;
            independentSquaredPixels += pixelResidual.X * pixelResidual.X +
                pixelResidual.Y * pixelResidual.Y;
        }

        Assert.Equal(Math.Sqrt(independentSquaredMillimeters /
            evidence.Residuals.Count), evidence.RmsMillimeters, 9);
        Assert.Equal(Math.Sqrt(independentSquaredPixels /
            evidence.Residuals.Count), evidence.RmsPixels, 9);

        var metrics = result.QualityMetrics.ToDictionary(metric => metric.Key,
            StringComparer.Ordinal);
        Assert.Contains("RmsMillimeters", metrics.Keys);
        Assert.Contains("RmsPixels", metrics.Keys);
        Assert.Contains("MissingMarkerCount", metrics.Keys);
        Assert.InRange(result.QualityMetrics.Count, 3,
            CalibrationProcedureComputationResult.MaximumMetricCount);
        _output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            dataset = SyntheticPlanarFixture.DatasetVersion,
            cornerMaximumErrorPixels = independentCornerError,
            evidence.RmsMillimeters, evidence.RmsPixels, evidence.MaxMillimeters, evidence.MaxPixels,
            evidence.ConstraintRankRatio, evidence.InverseClosureError,
            imageToPlane = coefficients.ImageToPlane, planeToImage = coefficients.PlaneToImage,
            inputHash = evidence.InputPayloadHash, coefficientHash = result.Coefficients.ContentHash,
            evidenceHash = result.Evidence!.ContentHash
        }));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    public async Task V126_C02_Rotated_images_keep_marker_corner_correspondence(int rotation)
    {
        using var sample = await PlanarSample.CreateAsync(
            RotatePixels(SyntheticPlanarFixture.Render(), rotation), frameOrdinal: rotation);
        var result = await new PlanarHomographyProcedure().ComputeAsync(
            new CalibrationComputationContext<PlanarHomographyInput>(sample.Input,
                new[] { sample.Observation }));
        var coefficients = PlanarHomographyResultCodec.DecodeCoefficients(result.Coefficients);

        foreach (var physical in SyntheticPlanarFixture.PhysicalCorners)
        {
            var expected = RotatePixel(SyntheticPlanarFixture.ExpectedForward(physical), rotation);
            Assert.True(coefficients.TryPlaneToImage(
                new PlanarPoint(physical.X, physical.Y), out var projected));
            Assert.InRange(Distance(projected, new PlanarPoint(expected.X, expected.Y)),
                0d, SyntheticPlanarFixture.ForwardPredictionMaxErrorPixels);
        }
    }

    [Fact]
    public async Task V126_C03_Partial_target_preserves_missing_marker_and_computes_three_marker_mapping()
    {
        const int missingMarkerId = 2;
        using var sample = await PlanarSample.CreateAsync(MaskMarker(missingMarkerId));
        Assert.Equal((SyntheticPlanarFixture.MarkerCount - 1) * 4,
            sample.Extraction.Features.Count);

        var result = await new PlanarHomographyProcedure().ComputeAsync(
            new CalibrationComputationContext<PlanarHomographyInput>(sample.Input,
                new[] { sample.Observation }));
        var coefficients = PlanarHomographyResultCodec.DecodeCoefficients(result.Coefficients);
        var evidence = PlanarHomographyResultCodec.DecodeEvidence(result.Evidence!);

        Assert.Contains(missingMarkerId, evidence.MissingMarkerIds);
        Assert.Equal((SyntheticPlanarFixture.MarkerCount - 1) * 4, evidence.Residuals.Count);
        // Missing the bottom-right marker removes its unsupported plane corner;
        // the procedure must not reuse the complete target's coverage rectangle.
        Assert.False(coefficients.TryPlaneToImage(new PlanarPoint(100, 80), out _));
        var unsupportedPixel = SyntheticPlanarFixture.ExpectedForward(100, 80);
        Assert.False(coefficients.TryImageToPlane(ToPlanarPoint(unsupportedPixel), out _));
        foreach (var marker in SyntheticPlanarFixture.Markers.Where(marker =>
                     marker.Id != missingMarkerId))
        foreach (var physical in marker.PhysicalCorners)
        {
            var expected = SyntheticPlanarFixture.ExpectedForward(physical);
            Assert.True(coefficients.TryPlaneToImage(
                new PlanarPoint(physical.X, physical.Y), out var projected));
            Assert.InRange(Distance(projected, new PlanarPoint(expected.X, expected.Y)),
                0d, SyntheticPlanarFixture.ForwardPredictionMaxErrorPixels);
        }
    }

    [Fact]
    public async Task V126_C03b_Single_detected_marker_is_the_four_point_minimum()
    {
        using var sample = await PlanarSample.CreateAsync(MaskMarkersExcept(0),
            frameOrdinal: 31);
        Assert.Equal(4, sample.Extraction.Features.Count);

        var result = await new PlanarHomographyProcedure().ComputeAsync(
            new CalibrationComputationContext<PlanarHomographyInput>(sample.Input,
                new[] { sample.Observation }));
        var evidence = PlanarHomographyResultCodec.DecodeEvidence(result.Evidence!);

        Assert.Equal(4, evidence.Residuals.Count);
        Assert.Equal(new[] { 1, 2, 3 }, evidence.MissingMarkerIds);
        Assert.True(double.IsFinite(evidence.ConstraintRankRatio));
    }

    [Fact]
    public async Task V126_C04_Receipt_binding_rejects_feature_context_and_pixel_mutations()
    {
        using var sample = await PlanarSample.CreateAsync(SyntheticPlanarFixture.Render());
        var cases = new[]
        {
            ReceiptMutation.Missing,
            ReceiptMutation.TamperedBytes,
            ReceiptMutation.Coordinate,
            ReceiptMutation.SourceHash,
            ReceiptMutation.FrameId,
            ReceiptMutation.SessionId,
            ReceiptMutation.Input,
            ReceiptMutation.PixelBytes
        };

        foreach (var mutation in cases)
        {
            var input = sample.Input;
            var source = sample.Observation;
            var observations = new[] { source };
            CalibrationTestFrame? changedFrame = null;
            var receipt = source.Receipt ?? throw new InvalidOperationException(
                "PlanarExtractionReceiptMissing");
            try
            {
                switch (mutation)
                {
                    case ReceiptMutation.Missing:
                        observations[0] = new CalibrationObservationInput(source.Frame,
                            source.Features, source.SessionId, source.FrameId, source.SourceHash);
                        break;
                    case ReceiptMutation.TamperedBytes:
                        var bytes = receipt.GetBytes();
                        bytes[0] ^= 0x01;
                        observations[0] = new CalibrationObservationInput(source.Frame,
                            source.Features, source.SessionId, source.FrameId, source.SourceHash,
                            new CalibrationExtractionReceipt(receipt.Format, bytes));
                        break;
                    case ReceiptMutation.Coordinate:
                        var forged = source.Features.ToArray();
                        forged[0] = new CalibrationImageFeature(forged[0].StableFeatureId,
                            forged[0].PixelX + 1d, forged[0].PixelY);
                        observations[0] = new CalibrationObservationInput(source.Frame, forged,
                            source.SessionId, source.FrameId, source.SourceHash, receipt);
                        break;
                    case ReceiptMutation.SourceHash:
                        observations[0] = new CalibrationObservationInput(source.Frame,
                            source.Features, source.SessionId, source.FrameId,
                            AlterHash(source.SourceHash), receipt);
                        break;
                    case ReceiptMutation.FrameId:
                        observations[0] = new CalibrationObservationInput(source.Frame,
                            source.Features, source.SessionId,
                            Guid.Parse("12600000-0000-0000-0000-999999999999"),
                            source.SourceHash, receipt);
                        break;
                    case ReceiptMutation.SessionId:
                        observations[0] = new CalibrationObservationInput(source.Frame,
                            source.Features,
                            Guid.Parse("12600000-0000-0000-0000-888888888888"),
                            source.FrameId, source.SourceHash, receipt);
                        break;
                    case ReceiptMutation.Input:
                        input = CreateInput(targetDefinitionId: "other-planar-target");
                        break;
                    case ReceiptMutation.PixelBytes:
                        var changedPixels = SyntheticPlanarFixture.Render();
                        changedPixels[0] ^= 0x01;
                        changedFrame = CalibrationTestFrame.Create(changedPixels);
                        observations[0] = new CalibrationObservationInput(changedFrame.Frame,
                            source.Features, source.SessionId, source.FrameId, source.SourceHash,
                            receipt);
                        break;
                    default:
                        throw new InvalidOperationException("ReceiptMutationNotHandled");
                }

                var exception = await Assert.ThrowsAsync<CalibrationProcedureException>(() =>
                    new PlanarHomographyProcedure().ComputeAsync(
                        new CalibrationComputationContext<PlanarHomographyInput>(input,
                            observations)).AsTask());
                Assert.Equal("PlanarExtractionReceiptInvalid", exception.ReasonCode);
            }
            finally
            {
                changedFrame?.Dispose();
            }
        }
    }

    [Fact]
    public async Task V126_C05_Rejects_blank_image_and_multiple_selected_observations()
    {
        using var blank = await PlanarSample.CreateAsync(SyntheticPlanarFixture.BlankImage(),
            frameOrdinal: 10);
        Assert.Empty(blank.Extraction.Features);
        Assert.Contains(blank.Extraction.Diagnostics,
            diagnostic => diagnostic.Key == "PlanarMarkersNotFound");

        using var first = await PlanarSample.CreateAsync(SyntheticPlanarFixture.Render(),
            frameOrdinal: 11);
        using var second = await PlanarSample.CreateAsync(
            SyntheticPlanarFixture.RenderVariant(), frameOrdinal: 12);
        var exception = await Assert.ThrowsAsync<CalibrationProcedureException>(() =>
            new PlanarHomographyProcedure().ComputeAsync(
                new CalibrationComputationContext<PlanarHomographyInput>(first.Input,
                    new[] { first.Observation, second.Observation })).AsTask());

        Assert.Equal("PlanarObservationCountInvalid", exception.ReasonCode);
    }

    [Fact]
    public async Task V126_C06_Rejects_configuration_and_cancellation_before_native_work()
    {
        using var frame = CalibrationTestFrame.Create(SyntheticPlanarFixture.Render());
        var changedConfiguration = CreateConfiguration(exposureTimeUs: 1001);
        var changedInput = CreateInput(changedConfiguration);
        var frameId = Guid.Parse("12600000-0000-0000-0000-000000000020");
        var configurationException = await Assert.ThrowsAsync<CalibrationProcedureException>(() =>
            new PlanarHomographyProcedure().ExtractAsync(
                new CalibrationExtractionContext<PlanarHomographyInput>(frame.Frame,
                    changedInput, SessionId, frameId, frame.SourceHash)).AsTask());
        Assert.Equal("PlanarFrameConfigurationMismatch", configurationException.ReasonCode);

        using var sample = await PlanarSample.CreateAsync(SyntheticPlanarFixture.Render(),
            frameOrdinal: 21);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new PlanarHomographyProcedure().ExtractAsync(
                new CalibrationExtractionContext<PlanarHomographyInput>(sample.Frame.Frame,
                    sample.Input, sample.SessionId, sample.FrameId, sample.Frame.SourceHash),
                cancellation.Token).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new PlanarHomographyProcedure().ComputeAsync(
                new CalibrationComputationContext<PlanarHomographyInput>(sample.Input,
                    new[] { sample.Observation }), cancellation.Token).AsTask());
    }

    [Fact]
    public void V126_C07_Input_requires_declared_raw_roi_domain()
    {
        var configuration = CreateConfiguration();
        Assert.Throws<CalibrationProcedureException>(() =>
            CreateInput(configuration, PlanarPixelDomain.ExactIntrinsicCorrectionRequired));
        Assert.Throws<ArgumentException>(() =>
            CreateInput(configuration, PlanarPixelDomain.Unknown));
    }

    [Fact]
    public async Task V126_C08_Managed_row_span_is_read_once_per_row_and_receipt_remains_usable()
    {
        var input = CreateInput();
        using var singleRead = SingleReadPlanarFrame.Create(SyntheticPlanarFixture.Render());
        var frameId = Guid.Parse("12600000-0000-0000-0000-000000000030");
        var extraction = await new PlanarHomographyProcedure().ExtractAsync(
            new CalibrationExtractionContext<PlanarHomographyInput>(singleRead.Frame,
                input, SessionId, frameId, singleRead.SourceHash));
        Assert.Equal(SyntheticPlanarFixture.MarkerCount * 4, extraction.Features.Count);
        Assert.NotNull(extraction.Receipt);

        using var stableFrame = CalibrationTestFrame.Create(SyntheticPlanarFixture.Render());
        var observation = new CalibrationObservationInput(stableFrame.Frame,
            extraction.Features, SessionId, frameId, stableFrame.SourceHash, extraction.Receipt);
        var result = await new PlanarHomographyProcedure().ComputeAsync(
            new CalibrationComputationContext<PlanarHomographyInput>(input,
                new[] { observation }));
        Assert.NotNull(result.Coefficients);
    }

    private static PlanarHomographyInput CreateInput(
        EffectiveCameraConfiguration? configuration = null,
        PlanarPixelDomain domain = PlanarPixelDomain.RawRoiPixelCentersCorrectionDeclaredNotRequired,
        string targetDefinitionId = "synthetic-planar-v1")
    {
        var markers = SyntheticPlanarFixture.Markers.Select(marker =>
            new ArucoPlanarMarkerDefinition(marker.Id,
                marker.PhysicalCorners.Select(point => new PlanarPoint(point.X, point.Y))));
        var target = new ArucoPlanarTargetDefinition(targetDefinitionId,
            "synthetic-plane", markers);
        return new PlanarHomographyInput("calibration-camera",
            configuration ?? CreateConfiguration(), domain, target);
    }

    private static EffectiveCameraConfiguration CreateConfiguration(double exposureTimeUs = 1000)
    {
        return new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            exposureTimeUs, 0, new RegionOfInterest(0, 0, SyntheticPlanarFixture.ImageWidth,
                SyntheticPlanarFixture.ImageHeight), VisionPixelFormat.Mono8, null, 1000, 0, null);
    }

    private static IReadOnlyList<SyntheticPlanarPoint> HeldOutPhysicalGrid()
    {
        var points = new List<SyntheticPlanarPoint>();
        for (var row = 1; row < 4; row++)
        for (var column = 1; column < 5; column++)
            points.Add(new SyntheticPlanarPoint(column * 20d, row * 20d));
        return points;
    }

    private static byte[] MaskMarker(int markerId)
    {
        var marker = SyntheticPlanarFixture.Markers.Single(value => value.Id == markerId);
        var projected = marker.PhysicalCorners.Select(
            point => SyntheticPlanarFixture.ExpectedForward(point)).ToArray();
        var pixels = SyntheticPlanarFixture.Render();
        var minimumX = Math.Max(0, (int)Math.Floor(projected.Min(point => point.X)) - 4);
        var maximumX = Math.Min(SyntheticPlanarFixture.ImageWidth - 1,
            (int)Math.Ceiling(projected.Max(point => point.X)) + 4);
        var minimumY = Math.Max(0, (int)Math.Floor(projected.Min(point => point.Y)) - 4);
        var maximumY = Math.Min(SyntheticPlanarFixture.ImageHeight - 1,
            (int)Math.Ceiling(projected.Max(point => point.Y)) + 4);
        for (var y = minimumY; y <= maximumY; y++)
        for (var x = minimumX; x <= maximumX; x++)
            pixels[y * SyntheticPlanarFixture.ImageWidth + x] = SyntheticPlanarFixture.BackgroundValue;
        return pixels;
    }

    private static byte[] MaskMarkersExcept(int markerIdToKeep)
    {
        var pixels = SyntheticPlanarFixture.Render();
        foreach (var marker in SyntheticPlanarFixture.Markers.Where(marker =>
                     marker.Id != markerIdToKeep))
        {
            var projected = marker.PhysicalCorners.Select(
                point => SyntheticPlanarFixture.ExpectedForward(point)).ToArray();
            var minimumX = Math.Max(0, (int)Math.Floor(projected.Min(point => point.X)) - 4);
            var maximumX = Math.Min(SyntheticPlanarFixture.ImageWidth - 1,
                (int)Math.Ceiling(projected.Max(point => point.X)) + 4);
            var minimumY = Math.Max(0, (int)Math.Floor(projected.Min(point => point.Y)) - 4);
            var maximumY = Math.Min(SyntheticPlanarFixture.ImageHeight - 1,
                (int)Math.Ceiling(projected.Max(point => point.Y)) + 4);
            for (var y = minimumY; y <= maximumY; y++)
            for (var x = minimumX; x <= maximumX; x++)
                pixels[y * SyntheticPlanarFixture.ImageWidth + x] =
                    SyntheticPlanarFixture.BackgroundValue;
        }
        return pixels;
    }

    private static byte[] RotatePixels(byte[] source, int rotation)
    {
        if (rotation is not (90 or 180)) throw new ArgumentOutOfRangeException(nameof(rotation));
        var pixels = new byte[source.Length];
        Array.Fill(pixels, SyntheticPlanarFixture.BackgroundValue);
        for (var y = 0; y < SyntheticPlanarFixture.ImageHeight; y++)
        for (var x = 0; x < SyntheticPlanarFixture.ImageWidth; x++)
        {
            var point = RotatePixel(new SyntheticPlanarPoint(x, y), rotation);
            var targetX = (int)Math.Round(point.X, MidpointRounding.AwayFromZero);
            var targetY = (int)Math.Round(point.Y, MidpointRounding.AwayFromZero);
            if ((uint)targetX < SyntheticPlanarFixture.ImageWidth &&
                (uint)targetY < SyntheticPlanarFixture.ImageHeight)
                pixels[targetY * SyntheticPlanarFixture.ImageWidth + targetX] =
                    source[y * SyntheticPlanarFixture.ImageWidth + x];
        }
        return pixels;
    }

    private static SyntheticPlanarPoint RotatePixel(SyntheticPlanarPoint point, int rotation) =>
        rotation switch
        {
            180 => new SyntheticPlanarPoint(SyntheticPlanarFixture.ImageWidth - 1 - point.X,
                SyntheticPlanarFixture.ImageHeight - 1 - point.Y),
            90 => new SyntheticPlanarPoint(
                (SyntheticPlanarFixture.ImageWidth - 1) / 2d +
                (point.Y - (SyntheticPlanarFixture.ImageHeight - 1) / 2d),
                (SyntheticPlanarFixture.ImageHeight - 1) / 2d -
                (point.X - (SyntheticPlanarFixture.ImageWidth - 1) / 2d)),
            _ => throw new ArgumentOutOfRangeException(nameof(rotation))
        };

    private static double Distance(PlanarPoint left, PlanarPoint right) =>
        Math.Sqrt(Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));

    private static PlanarPoint ToPlanarPoint(SyntheticPlanarPoint point) =>
        new(point.X, point.Y);

    private static bool TryIndependentProject(IReadOnlyList<double> matrix,
        PlanarPoint point, out PlanarPoint projected)
    {
        if (matrix.Count != 9 || !double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            projected = default;
            return false;
        }

        var denominator = matrix[6] * point.X + matrix[7] * point.Y + matrix[8];
        if (!double.IsFinite(denominator) || Math.Abs(denominator) < 1e-12d)
        {
            projected = default;
            return false;
        }

        projected = new PlanarPoint(
            (matrix[0] * point.X + matrix[1] * point.Y + matrix[2]) / denominator,
            (matrix[3] * point.X + matrix[4] * point.Y + matrix[5]) / denominator);
        return double.IsFinite(projected.X) && double.IsFinite(projected.Y);
    }

    private static string AlterHash(string sourceHash) =>
        (sourceHash[0] == '0' ? '1' : '0') + sourceHash[1..];

    private enum ReceiptMutation
    {
        Missing,
        TamperedBytes,
        Coordinate,
        SourceHash,
        FrameId,
        SessionId,
        Input,
        PixelBytes
    }

    private sealed class PlanarSample : IDisposable
    {
        private PlanarSample(CalibrationTestFrame frame, PlanarHomographyInput input,
            CalibrationExtractionResult extraction, Guid sessionId, Guid frameId)
        {
            Frame = frame;
            Input = input;
            Extraction = extraction;
            SessionId = sessionId;
            FrameId = frameId;
        }

        public CalibrationTestFrame Frame { get; }
        public PlanarHomographyInput Input { get; }
        public CalibrationExtractionResult Extraction { get; }
        public Guid SessionId { get; }
        public Guid FrameId { get; }

        public string SourceHash => Frame.SourceHash;

        public CalibrationObservationInput Observation => new(Frame.Frame,
            Extraction.Features, SessionId, FrameId, SourceHash, Extraction.Receipt);

        public static async Task<PlanarSample> CreateAsync(byte[] pixels,
            PlanarHomographyInput? input = null, int frameOrdinal = 1)
        {
            input ??= CreateInput();
            var frame = CalibrationTestFrame.Create(pixels, input.ExpectedConfiguration);
            var sessionId = PlanarHomographyProcedureTests.SessionId;
            var frameId = Guid.Parse($"12600000-0000-0000-0000-{frameOrdinal:D12}");
            try
            {
                var extraction = await new PlanarHomographyProcedure().ExtractAsync(
                    new CalibrationExtractionContext<PlanarHomographyInput>(frame.Frame,
                        input, sessionId, frameId, frame.SourceHash));
                return new PlanarSample(frame, input, extraction, sessionId, frameId);
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }

        public void Dispose() => Frame.Dispose();
    }

    private sealed class SingleReadPlanarFrame : VisionFrame, IDisposable
    {
        private readonly byte[] _pixels;
        private readonly int[] _rowReads;
        private int _active = 1;

        private SingleReadPlanarFrame(FrameMetadata metadata, byte[] pixels, string sourceHash)
            : base(metadata)
        {
            _pixels = pixels;
            _rowReads = new int[metadata.Height];
            SourceHash = sourceHash;
        }

        public VisionFrame Frame => this;
        public string SourceHash { get; }
        public override bool IsLoanActive => Volatile.Read(ref _active) != 0;

        public override ReadOnlySpan<byte> GetRowSpan(int row)
        {
            if (!IsLoanActive) throw new ObjectDisposedException(nameof(SingleReadPlanarFrame));
            if ((uint)row >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(row));
            if (Interlocked.Increment(ref _rowReads[row]) != 1)
                throw new InvalidOperationException("SingleReadPlanarFrameReadTwice");
            return _pixels.AsSpan(checked(row * Metadata.ValidRowBytes), Metadata.ValidRowBytes);
        }

        public static SingleReadPlanarFrame Create(byte[] pixels)
        {
            ArgumentNullException.ThrowIfNull(pixels);
            var configuration = CreateConfiguration();
            var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
            var metadata = new FrameMetadata(correlation, "calibration-camera",
                SyntheticPlanarFixture.ImageWidth, SyntheticPlanarFixture.ImageHeight,
                SyntheticPlanarFixture.ImageWidth, VisionPixelFormat.Mono8, null,
                DateTimeOffset.UtcNow, configuration);
            return new SingleReadPlanarFrame(metadata, (byte[])pixels.Clone(),
                Convert.ToHexString(SHA256.HashData(pixels)));
        }

        public void Dispose() => Interlocked.Exchange(ref _active, 0);
    }
}
