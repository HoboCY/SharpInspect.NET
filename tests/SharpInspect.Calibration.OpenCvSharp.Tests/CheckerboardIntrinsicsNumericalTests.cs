using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace SharpInspect.Calibration.OpenCvSharp.Tests;

/// <summary>
/// Numerical contract tests for the frozen checkerboard-v1 data set.  The data and
/// expected bounds are owned by SyntheticCheckerboardFixture; this file only joins
/// those immutable pixels to the procedure input contract.
/// </summary>
public sealed class CheckerboardIntrinsicsNumericalTests
{
    private readonly ITestOutputHelper _output;

    public CheckerboardIntrinsicsNumericalTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task V125_C02_Extract_managed_frame_uses_public_row_span_without_native_borrow()
    {
        var pixels = SyntheticCheckerboardFixture.Render(0);
        using var frame = ManagedRowSpanFrame.Create(pixels);
        var procedure = new CheckerboardIntrinsicsProcedure();
        var result = await procedure.ExtractAsync(
            new CalibrationExtractionContext<CheckerboardIntrinsicsInput>(frame.Frame, frame.Input,
                frame.SessionId, frame.FrameId, frame.SourceHash));

        Assert.Equal(frame.Input.CornerCount, result.Features.Count);
        var expected = SyntheticCheckerboardFixture.ProjectCorners(0);
        var forward = result.Features.Select((feature, index) => Distance(feature, expected[index])).Max();
        var reverse = result.Features.Select((feature, index) =>
            Distance(feature, expected[expected.Count - index - 1])).Max();
        Assert.True(Math.Min(forward, reverse) <= 1d,
            $"Forward={forward:R}, reverse={reverse:R}");

        using var pooled = CalibrationTestFrame.Create(pixels);
        var pooledResult = await procedure.ExtractAsync(new CalibrationExtractionContext<CheckerboardIntrinsicsInput>(
            pooled.Frame, pooled.Input, frame.SessionId, Guid.Parse("12500000-0000-0000-0000-000000000002"),
            pooled.SourceHash));
        Assert.Equal(result.Features.Count, pooledResult.Features.Count);
        for (var index = 0; index < result.Features.Count; index++)
        {
            Assert.Equal(result.Features[index].StableFeatureId, pooledResult.Features[index].StableFeatureId);
            Assert.InRange(Math.Abs(result.Features[index].PixelX - pooledResult.Features[index].PixelX), 0d, 1e-6);
            Assert.InRange(Math.Abs(result.Features[index].PixelY - pooledResult.Features[index].PixelY), 0d, 1e-6);
        }

        static double Distance(CalibrationImageFeature feature, SyntheticCheckerboardImagePoint point) =>
            Math.Sqrt(Math.Pow(feature.PixelX - point.X, 2) + Math.Pow(feature.PixelY - point.Y, 2));
    }

    [Fact]
    public async Task V125_C03_Compute_frozen_twenty_view_dataset_recovers_intrinsics_and_evidence()
    {
        using var dataset = await FrozenDataset.CreateAsync(Enumerable.Range(0, SyntheticCheckerboardFixture.Poses.Count));
        var procedure = new CheckerboardIntrinsicsProcedure();

        Assert.Equal(SyntheticCheckerboardFixture.Poses.Count, dataset.Observations.Count);
        Assert.All(dataset.Observations.Select((observation, index) => (observation, index)), sample =>
        {
            var expected = SyntheticCheckerboardFixture.ProjectCorners(dataset.PoseIndexes[sample.index]);
            var forward = sample.observation.Features.Select((feature, cornerIndex) => Distance(feature, expected[cornerIndex])).Max();
            var reverse = sample.observation.Features.Select((feature, cornerIndex) =>
                Distance(feature, expected[expected.Count - 1 - cornerIndex])).Max();
            Assert.True(Math.Min(forward, reverse) <= 1d,
                $"Pose={dataset.PoseIndexes[sample.index]}, forward={forward:R}, reverse={reverse:R}");
        });

        var result = await procedure.ComputeAsync(new CalibrationComputationContext<CheckerboardIntrinsicsInput>(
            dataset.Input, dataset.Observations));

        var coefficients = CheckerboardIntrinsicsResultCodec.DecodeCoefficients(result.Coefficients);
        Assert.InRange(Math.Abs(coefficients.Fx - SyntheticCheckerboardFixture.Fx), 0d, 20d);
        Assert.InRange(Math.Abs(coefficients.Fy - SyntheticCheckerboardFixture.Fy), 0d, 20d);
        Assert.InRange(Math.Abs(coefficients.Cx - SyntheticCheckerboardFixture.Cx), 0d, 10d);
        Assert.InRange(Math.Abs(coefficients.Cy - SyntheticCheckerboardFixture.Cy), 0d, 10d);
        Assert.InRange(Math.Abs(coefficients.K1 - SyntheticCheckerboardFixture.K1), 0d, 0.08d);
        var maximumDistortionFunctionError = 0d;
        foreach (var radius in new[] { 0d, 0.05d, 0.1d, 0.15d, 0.2d, 0.25d })
        for (var angleIndex = 0; angleIndex < 16; angleIndex++)
        {
            var angle = angleIndex * Math.PI / 8d;
            var x = radius * Math.Cos(angle);
            var y = radius * Math.Sin(angle);
            var expectedDistorted = Distort(x, y, SyntheticCheckerboardFixture.K1,
                SyntheticCheckerboardFixture.K2, SyntheticCheckerboardFixture.P1,
                SyntheticCheckerboardFixture.P2, SyntheticCheckerboardFixture.K3);
            var recoveredDistorted = Distort(x, y, coefficients.K1, coefficients.K2,
                coefficients.P1, coefficients.P2, coefficients.K3);
            var pixelError = Math.Sqrt(
                Math.Pow((recoveredDistorted.X - expectedDistorted.X) * SyntheticCheckerboardFixture.Fx, 2) +
                Math.Pow((recoveredDistorted.Y - expectedDistorted.Y) * SyntheticCheckerboardFixture.Fy, 2));
            if (pixelError > maximumDistortionFunctionError)
                maximumDistortionFunctionError = pixelError;
            Assert.InRange(pixelError, 0d, 2d);
        }

        Assert.NotNull(result.Evidence);
        var decodedEvidence = CheckerboardIntrinsicsResultCodec.DecodeEvidence(result.Evidence!);
        Assert.Equal(procedure.InputCodec.EncodePayload(dataset.Input).ContentHash,
            decodedEvidence.InputPayloadHash);
        Assert.Equal(SyntheticCheckerboardFixture.Poses.Count, decodedEvidence.Views.Count);
        Assert.Equal(SyntheticCheckerboardFixture.Poses.Count * SyntheticCheckerboardFixture.InnerCornerCount,
            decodedEvidence.PointCount);
        Assert.True(double.IsFinite(decodedEvidence.RmsPixels));
        Assert.True(double.IsFinite(decodedEvidence.ConstraintRankRatio));
        _output.WriteLine($"coefficients Fx={Format(coefficients.Fx)}, Fy={Format(coefficients.Fy)}, " +
            $"Cx={Format(coefficients.Cx)}, Cy={Format(coefficients.Cy)}, " +
            $"K1={Format(coefficients.K1)}, K2={Format(coefficients.K2)}, " +
            $"P1={Format(coefficients.P1)}, P2={Format(coefficients.P2)}, K3={Format(coefficients.K3)}");
        _output.WriteLine($"evidence RmsPixels={Format(decodedEvidence.RmsPixels)}, " +
            $"ConstraintRankRatio={Format(decodedEvidence.ConstraintRankRatio)}, " +
            $"FrozenDistortionFunctionMaxPixelError={Format(maximumDistortionFunctionError)}");
        Assert.All(decodedEvidence.Views, view =>
        {
            Assert.True(double.IsFinite(view.RmsPixels));
            Assert.All(view.Residuals, point =>
            {
                Assert.True(double.IsFinite(point.DeltaX));
                Assert.True(double.IsFinite(point.DeltaY));
            });
        });
        AssertIndependentReconstruction(dataset.Input, dataset.Observations, coefficients,
            decodedEvidence);

        var metrics = result.QualityMetrics.ToDictionary(metric => metric.Key, StringComparer.Ordinal);
        foreach (var key in new[] { "Fx", "Fy", "Cx", "Cy", "K1", "K2", "P1", "P2", "K3" })
            Assert.Contains(key, metrics.Keys);
        Assert.Equal(coefficients.Fx, metrics["Fx"].Value);
        Assert.Equal(coefficients.Fy, metrics["Fy"].Value);
        Assert.Equal(coefficients.Cx, metrics["Cx"].Value);
        Assert.Equal(coefficients.Cy, metrics["Cy"].Value);
        Assert.Equal(coefficients.K1, metrics["K1"].Value);
        Assert.Equal(coefficients.K2, metrics["K2"].Value);
        Assert.Equal(coefficients.P1, metrics["P1"].Value);
        Assert.Equal(coefficients.P2, metrics["P2"].Value);
        Assert.Equal(coefficients.K3, metrics["K3"].Value);
        Assert.InRange(result.QualityMetrics.Count, 9, CalibrationProcedureComputationResult.MaximumMetricCount);

        static double Distance(CalibrationImageFeature feature, SyntheticCheckerboardImagePoint point) =>
            Math.Sqrt(Math.Pow(feature.PixelX - point.X, 2) + Math.Pow(feature.PixelY - point.Y, 2));

        static (double X, double Y) Distort(double x, double y, double k1, double k2,
            double p1, double p2, double k3)
        {
            var radiusSquared = x * x + y * y;
            var radial = 1d + k1 * radiusSquared + k2 * radiusSquared * radiusSquared +
                k3 * radiusSquared * radiusSquared * radiusSquared;
            return (x * radial + 2d * p1 * x * y + p2 * (radiusSquared + 2d * x * x),
                y * radial + p1 * (radiusSquared + 2d * y * y) + 2d * p2 * x * y);
        }
    }

    [Fact]
    public async Task V125_C04_Compute_excluding_one_frozen_frame_retains_the_other_nineteen_views()
    {
        using var dataset = await FrozenDataset.CreateAsync(Enumerable.Range(1, SyntheticCheckerboardFixture.Poses.Count - 1));
        var result = await new CheckerboardIntrinsicsProcedure().ComputeAsync(
            new CalibrationComputationContext<CheckerboardIntrinsicsInput>(dataset.Input, dataset.Observations));

        Assert.NotNull(result.Evidence);
        var evidence = CheckerboardIntrinsicsResultCodec.DecodeEvidence(result.Evidence!);
        Assert.Equal(SyntheticCheckerboardFixture.Poses.Count - 1, evidence.Views.Count);
        Assert.Equal((SyntheticCheckerboardFixture.Poses.Count - 1) * SyntheticCheckerboardFixture.InnerCornerCount,
            evidence.PointCount);
    }

    [Fact]
    public async Task V125_C05_Compute_rejects_fewer_than_three_views_with_stable_reason()
    {
        using var dataset = await FrozenDataset.CreateAsync(new[] { 0, 1 });
        var exception = await Assert.ThrowsAsync<CalibrationProcedureException>(() =>
            new CheckerboardIntrinsicsProcedure().ComputeAsync(
                new CalibrationComputationContext<CheckerboardIntrinsicsInput>(dataset.Input,
                    dataset.Observations)).AsTask());

        Assert.Equal("CheckerboardViewsInsufficient", exception.ReasonCode);
    }

    [Fact]
    public async Task V125_C06_Compute_rejects_duplicate_source_hash_before_native_candidate()
    {
        using var dataset = await FrozenDataset.CreateAsync(new[] { 0, 1, 2 });
        var observations = dataset.Observations.ToArray();
        observations[1] = new CalibrationObservationInput(observations[1].Frame, observations[1].Features,
            observations[1].SessionId, observations[1].FrameId, observations[0].SourceHash,
            observations[1].Receipt);

        var exception = await Assert.ThrowsAsync<CalibrationProcedureException>(() =>
            new CheckerboardIntrinsicsProcedure().ComputeAsync(
                new CalibrationComputationContext<CheckerboardIntrinsicsInput>(dataset.Input, observations)).AsTask());

        Assert.Equal("CheckerboardDuplicateSourceHash", exception.ReasonCode);
    }

    [Fact]
    public async Task V125_C07_Compute_rejects_duplicate_image_even_when_source_hash_differs()
    {
        using var dataset = await FrozenDataset.CreateAsync(new[] { 0, 0, 1 });
        var observations = dataset.Observations.ToArray();
        observations[1] = new CalibrationObservationInput(observations[1].Frame, observations[1].Features,
            observations[1].SessionId, observations[1].FrameId, new string('A', 64),
            observations[1].Receipt);

        var exception = await Assert.ThrowsAsync<CalibrationProcedureException>(() =>
            new CheckerboardIntrinsicsProcedure().ComputeAsync(
                new CalibrationComputationContext<CheckerboardIntrinsicsInput>(dataset.Input, observations)).AsTask());

        Assert.Equal("CheckerboardDuplicateImage", exception.ReasonCode);
    }

    [Fact]
    public async Task V125_C08_Compute_rejects_wrong_count_order_and_bounds_before_receipt_validation()
    {
        using var dataset = await FrozenDataset.CreateAsync(new[] { 0, 1, 2 });
        var baseline = dataset.Observations[0];
        var cases = new[]
        {
            (Array.Empty<CalibrationImageFeature>(), "CheckerboardFeatureCountInvalid"),
            (SwapFirstTwo(baseline.Features), "CheckerboardFeatureOrderInvalid"),
            (MoveFirstOutside(baseline.Features), "CheckerboardFeatureOutsideImage")
        };

        foreach (var testCase in cases)
        {
            var observations = dataset.Observations.ToArray();
            observations[0] = new CalibrationObservationInput(baseline.Frame, testCase.Item1,
                baseline.SessionId, baseline.FrameId, baseline.SourceHash, baseline.Receipt);
            var exception = await Assert.ThrowsAsync<CalibrationProcedureException>(() =>
                new CheckerboardIntrinsicsProcedure().ComputeAsync(
                    new CalibrationComputationContext<CheckerboardIntrinsicsInput>(dataset.Input,
                        observations)).AsTask());
            Assert.Equal(testCase.Item2, exception.ReasonCode);
        }

        static CalibrationImageFeature[] SwapFirstTwo(IReadOnlyList<CalibrationImageFeature> features)
        {
            var copy = features.ToArray();
            (copy[0], copy[1]) = (copy[1], copy[0]);
            return copy;
        }

        static CalibrationImageFeature[] MoveFirstOutside(IReadOnlyList<CalibrationImageFeature> features)
        {
            var copy = features.ToArray();
            copy[0] = new CalibrationImageFeature(copy[0].StableFeatureId, -1, copy[0].PixelY);
            return copy;
        }
    }

    [Fact]
    public async Task V125_C09_Compute_rejects_forged_coordinates_after_receipt_validation()
    {
        using var dataset = await FrozenDataset.CreateAsync(new[] { 0, 1, 2 });
        var source = dataset.Observations[0];
        var forgedFeatures = source.Features.ToArray();
        forgedFeatures[0] = new CalibrationImageFeature(forgedFeatures[0].StableFeatureId,
            forgedFeatures[0].PixelX + 10, forgedFeatures[0].PixelY);
        var observations = dataset.Observations.ToArray();
        observations[0] = new CalibrationObservationInput(source.Frame, forgedFeatures,
            source.SessionId, source.FrameId, source.SourceHash, source.Receipt);

        var exception = await Assert.ThrowsAsync<CalibrationProcedureException>(() =>
            new CheckerboardIntrinsicsProcedure().ComputeAsync(
                new CalibrationComputationContext<CheckerboardIntrinsicsInput>(dataset.Input,
                    observations)).AsTask());

        Assert.Equal("CheckerboardExtractionReceiptInvalid", exception.ReasonCode);
    }

    [Fact]
    public async Task V125_C10_Compute_rejects_blank_frame_without_result()
    {
        using var dataset = await FrozenDataset.CreateAsync(new[] { 0, 1 });
        using var blank = CalibrationTestFrame.Create(SyntheticCheckerboardFixture.BlankImage());
        var blankId = Guid.Parse("12500000-0000-0000-0000-000000000003");
        var observations = dataset.Observations.Append(new CalibrationObservationInput(blank.Frame,
            Array.Empty<CalibrationImageFeature>(), dataset.Observations[0].SessionId, blankId,
            blank.SourceHash)).ToArray();

        var exception = await Assert.ThrowsAsync<CalibrationProcedureException>(() =>
            new CheckerboardIntrinsicsProcedure().ComputeAsync(
                new CalibrationComputationContext<CheckerboardIntrinsicsInput>(dataset.Input,
                    observations)).AsTask());

        Assert.Equal("CheckerboardFeatureCountInvalid", exception.ReasonCode);
    }

    [Fact]
    public async Task V125_C11_Compute_rejects_forged_degenerate_view_receipt()
    {
        using var dataset = await FrozenDataset.CreateAsync(new[] { 0, 1, 2 });
        var source = dataset.Observations[0];
        var degenerate = source.Features.Select(feature => new CalibrationImageFeature(
            feature.StableFeatureId, 100, 100)).ToArray();
        var observations = dataset.Observations.ToArray();
        observations[0] = new CalibrationObservationInput(source.Frame, degenerate,
            source.SessionId, source.FrameId, source.SourceHash, source.Receipt);

        var exception = await Assert.ThrowsAsync<CalibrationProcedureException>(() =>
            new CheckerboardIntrinsicsProcedure().ComputeAsync(
                new CalibrationComputationContext<CheckerboardIntrinsicsInput>(dataset.Input,
                    observations)).AsTask());

        Assert.Equal("CheckerboardExtractionReceiptInvalid", exception.ReasonCode);
    }

    [Fact]
    public async Task V125_C12_Compute_rejects_subpixel_forgery_without_pixel_tolerance()
    {
        using var dataset = await FrozenDataset.CreateAsync(new[] { 0, 1, 2 });
        var source = dataset.Observations[0];
        var forgedFeatures = source.Features.ToArray();
        forgedFeatures[0] = new CalibrationImageFeature(forgedFeatures[0].StableFeatureId,
            forgedFeatures[0].PixelX + 0.1d, forgedFeatures[0].PixelY);
        var observations = dataset.Observations.ToArray();
        observations[0] = new CalibrationObservationInput(source.Frame, forgedFeatures,
            source.SessionId, source.FrameId, source.SourceHash, source.Receipt);

        var exception = await Assert.ThrowsAsync<CalibrationProcedureException>(() =>
            new CheckerboardIntrinsicsProcedure().ComputeAsync(
                new CalibrationComputationContext<CheckerboardIntrinsicsInput>(dataset.Input,
                    observations)).AsTask());

        Assert.Equal("CheckerboardExtractionReceiptInvalid", exception.ReasonCode);
    }

    [Fact]
    public async Task V125_C13_Compute_rejects_double_change_swallowed_by_float_conversion()
    {
        using var dataset = await FrozenDataset.CreateAsync(new[] { 0, 1, 2 });
        var source = dataset.Observations[0];
        var forgedFeatures = source.Features.ToArray();
        var original = forgedFeatures[0];
        var quantizedAwayFromOriginal = original.PixelX + 1e-7d;
        Assert.NotEqual(original.PixelX, quantizedAwayFromOriginal);
        Assert.Equal((float)original.PixelX, (float)quantizedAwayFromOriginal);
        forgedFeatures[0] = new CalibrationImageFeature(original.StableFeatureId,
            quantizedAwayFromOriginal, original.PixelY);
        var observations = dataset.Observations.ToArray();
        observations[0] = new CalibrationObservationInput(source.Frame, forgedFeatures,
            source.SessionId, source.FrameId, source.SourceHash, source.Receipt);

        var exception = await Assert.ThrowsAsync<CalibrationProcedureException>(() =>
            new CheckerboardIntrinsicsProcedure().ComputeAsync(
                new CalibrationComputationContext<CheckerboardIntrinsicsInput>(dataset.Input,
                    observations)).AsTask());

        Assert.Equal("CheckerboardExtractionReceiptInvalid", exception.ReasonCode);
    }

    [Fact]
    public async Task V125_C14_Compute_rejects_three_translated_frontoparallel_views()
    {
        using var dataset = await FrontoparallelDataset.CreateAsync();
        var exception = await Assert.ThrowsAsync<CalibrationProcedureException>(() =>
            new CheckerboardIntrinsicsProcedure().ComputeAsync(
                new CalibrationComputationContext<CheckerboardIntrinsicsInput>(dataset.Input,
                    dataset.Observations)).AsTask());

        Assert.Equal("CheckerboardConstraintRankInsufficient", exception.ReasonCode);
    }

    [Fact]
    public async Task V125_C15_Diagnostic_repeated_frontoparallel_extraction_reports_exact_deltas()
    {
        using var dataset = await FrontoparallelDataset.CreateAsync();
        var observation = dataset.Observations[0];
        var procedure = new CheckerboardIntrinsicsProcedure();
        var context = new CalibrationExtractionContext<CheckerboardIntrinsicsInput>(
            observation.Frame, dataset.Input, observation.SessionId, observation.FrameId,
            observation.SourceHash);
        var baselineHash = ComputeRowSpanHash(observation.Frame);
        var first = await procedure.ExtractAsync(context);
        var afterFirstHash = ComputeRowSpanHash(observation.Frame);
        var second = await procedure.ExtractAsync(context);
        var afterSecondHash = ComputeRowSpanHash(observation.Frame);

        Assert.Equal(dataset.Input.CornerCount, observation.Features.Count);
        Assert.Equal(dataset.Input.CornerCount, first.Features.Count);
        Assert.Equal(dataset.Input.CornerCount, second.Features.Count);
        Assert.Equal(baselineHash, afterFirstHash);
        Assert.Equal(baselineHash, afterSecondHash);

        _output.WriteLine($"frameId={observation.FrameId}, sourceHash={observation.SourceHash}");
        _output.WriteLine($"rowSpanSha256 baseline={baselineHash}, afterFirst={afterFirstHash}, afterSecond={afterSecondHash}");
        WriteExtractionComparison("stored->extract1", observation.Features, first.Features);
        WriteExtractionComparison("extract1->extract2", first.Features, second.Features);
    }

    [Fact]
    public async Task V125_C16_Compute_rejects_descending_feature_ids_even_when_coordinates_are_reversed()
    {
        using var dataset = await FrozenDataset.CreateAsync(new[] { 0, 1, 2 });
        var baseline = dataset.Observations[0];
        var descending = baseline.Features.Reverse().ToArray();
        var observations = dataset.Observations.ToArray();
        observations[0] = new CalibrationObservationInput(baseline.Frame, descending,
            baseline.SessionId, baseline.FrameId, baseline.SourceHash, baseline.Receipt);

        var exception = await Assert.ThrowsAsync<CalibrationProcedureException>(() =>
            new CheckerboardIntrinsicsProcedure().ComputeAsync(
                new CalibrationComputationContext<CheckerboardIntrinsicsInput>(dataset.Input,
                    observations)).AsTask());

        Assert.Equal("CheckerboardFeatureOrderInvalid", exception.ReasonCode);
    }

    [Fact]
    public async Task V125_C17_Compute_rejects_full_coordinate_reverse_with_ascending_ids()
    {
        using var dataset = await FrozenDataset.CreateAsync(
            Enumerable.Range(0, SyntheticCheckerboardFixture.Poses.Count));
        var observations = dataset.Observations.Select(observation =>
            new CalibrationObservationInput(observation.Frame,
                ReverseCoordinatesKeepAscendingIds(observation.Features),
                observation.SessionId, observation.FrameId, observation.SourceHash,
                observation.Receipt)).ToArray();

        var exception = await Assert.ThrowsAsync<CalibrationProcedureException>(() =>
            new CheckerboardIntrinsicsProcedure().ComputeAsync(
                new CalibrationComputationContext<CheckerboardIntrinsicsInput>(dataset.Input,
                    observations)).AsTask());

        Assert.Equal("CheckerboardExtractionReceiptInvalid", exception.ReasonCode);
    }

    [Fact]
    public async Task V125_C19_Compute_rejects_receipt_binding_mutations_table_driven()
    {
        using var dataset = await FrozenDataset.CreateAsync(new[] { 0, 1, 2 });
        var cases = new[]
        {
            (ReceiptMutation.Missing, "missing", "CheckerboardExtractionReceiptInvalid"),
            (ReceiptMutation.WrongFormat, "wrong format", "CheckerboardExtractionReceiptInvalid"),
            (ReceiptMutation.WrongLength, "wrong length", "CheckerboardExtractionReceiptInvalid"),
            (ReceiptMutation.TamperedBytes, "tampered bytes", "CheckerboardExtractionReceiptInvalid"),
            (ReceiptMutation.SourceHash, "source hash", "CheckerboardExtractionReceiptInvalid"),
            (ReceiptMutation.FrameId, "frame id", "CheckerboardExtractionReceiptInvalid"),
            (ReceiptMutation.SessionId, "session id", "CheckerboardExtractionReceiptInvalid"),
            (ReceiptMutation.PixelBytes, "valid-row pixel", "CheckerboardExtractionReceiptInvalid"),
            (ReceiptMutation.Input, "input", "CheckerboardExtractionReceiptInvalid"),
            (ReceiptMutation.Configuration, "configuration", "CheckerboardFrameConfigurationMismatch")
        };

        foreach (var testCase in cases)
        {
            var input = dataset.Input;
            var observations = dataset.Observations.ToArray();
            var source = observations[0];
            var sourceReceipt = source.Receipt ??
                throw new InvalidOperationException("FrozenDatasetReceiptMissing");
            CalibrationTestFrame? changedFrame = null;

            try
            {
                switch (testCase.Item1)
                {
                    case ReceiptMutation.Missing:
                        observations[0] = new CalibrationObservationInput(source.Frame,
                            source.Features, source.SessionId, source.FrameId, source.SourceHash);
                        break;
                    case ReceiptMutation.WrongFormat:
                        observations[0] = new CalibrationObservationInput(source.Frame,
                            source.Features, source.SessionId, source.FrameId, source.SourceHash,
                            new CalibrationExtractionReceipt(
                                new RecipeContractReference("other-extraction", "1", new string('0', 64)),
                                sourceReceipt.GetBytes()));
                        break;
                    case ReceiptMutation.WrongLength:
                        observations[0] = new CalibrationObservationInput(source.Frame,
                            source.Features, source.SessionId, source.FrameId, source.SourceHash,
                            new CalibrationExtractionReceipt(sourceReceipt.Format,
                                sourceReceipt.GetBytes()[..31]));
                        break;
                    case ReceiptMutation.TamperedBytes:
                        var tampered = sourceReceipt.GetBytes();
                        tampered[0] ^= 0x01;
                        observations[0] = new CalibrationObservationInput(source.Frame,
                            source.Features, source.SessionId, source.FrameId, source.SourceHash,
                            new CalibrationExtractionReceipt(sourceReceipt.Format, tampered));
                        break;
                    case ReceiptMutation.SourceHash:
                        observations[0] = new CalibrationObservationInput(source.Frame,
                            source.Features, source.SessionId, source.FrameId,
                            AlterSourceHash(source.SourceHash), sourceReceipt);
                        break;
                    case ReceiptMutation.FrameId:
                        observations[0] = new CalibrationObservationInput(source.Frame,
                            source.Features, source.SessionId,
                            Guid.Parse("12500000-0000-0000-0000-999999999999"), source.SourceHash,
                            sourceReceipt);
                        break;
                    case ReceiptMutation.SessionId:
                        var changedSessionId = Guid.Parse("12500000-0000-0000-0000-888888888888");
                        for (var index = 0; index < observations.Length; index++)
                        {
                            var observation = observations[index];
                            observations[index] = new CalibrationObservationInput(observation.Frame,
                                observation.Features, changedSessionId, observation.FrameId,
                                observation.SourceHash, observation.Receipt);
                        }
                        break;
                    case ReceiptMutation.PixelBytes:
                        var changedPixels = SyntheticCheckerboardFixture.Render(0);
                        changedPixels[0] ^= 0x01;
                        changedFrame = CalibrationTestFrame.Create(changedPixels);
                        observations[0] = new CalibrationObservationInput(changedFrame.Frame,
                            source.Features, source.SessionId, source.FrameId, source.SourceHash,
                            sourceReceipt);
                        break;
                    case ReceiptMutation.Input:
                        input = new CheckerboardIntrinsicsInput(input.InnerColumns, input.InnerRows,
                            input.SquareSizeMillimeters + 0.5d, input.LogicalCameraRole,
                            input.ExpectedConfiguration);
                        break;
                    case ReceiptMutation.Configuration:
                        var configuration = input.ExpectedConfiguration;
                        var changedConfiguration = new EffectiveCameraConfiguration(
                            configuration.ProductionAcquisitionMode, configuration.ExposureTimeUs + 1d,
                            configuration.GainDb, configuration.RegionOfInterest, configuration.PixelFormat,
                            configuration.ValidBits, configuration.AcquisitionTimeoutMs,
                            configuration.TriggerDelayUs, configuration.WhiteBalanceRgb);
                        input = new CheckerboardIntrinsicsInput(input.InnerColumns, input.InnerRows,
                            input.SquareSizeMillimeters, input.LogicalCameraRole, changedConfiguration);
                        break;
                    default:
                        throw new InvalidOperationException("ReceiptMutationNotHandled");
                }

                var exception = await Assert.ThrowsAsync<CalibrationProcedureException>(() =>
                    new CheckerboardIntrinsicsProcedure().ComputeAsync(
                        new CalibrationComputationContext<CheckerboardIntrinsicsInput>(input,
                            observations)).AsTask());

                _output.WriteLine($"{testCase.Item2}: {exception.ReasonCode}");
                Assert.Equal(testCase.Item3, exception.ReasonCode);
            }
            finally
            {
                changedFrame?.Dispose();
            }
        }

        static string AlterSourceHash(string sourceHash) =>
            (sourceHash[0] == '0' ? '1' : '0') + sourceHash[1..];
    }

    [Fact]
    public async Task V125_C20_Extract_reads_each_managed_row_once_and_receipt_computes_on_same_pixels()
    {
        using var dataset = await FrozenDataset.CreateAsync(new[] { 0, 1, 2 });
        using var singleReadFrame = SingleReadRowSpanFrame.Create(
            SyntheticCheckerboardFixture.Render(0));
        var source = dataset.Observations[0];
        var extraction = await new CheckerboardIntrinsicsProcedure().ExtractAsync(
            new CalibrationExtractionContext<CheckerboardIntrinsicsInput>(
                singleReadFrame.Frame, singleReadFrame.Input, source.SessionId,
                source.FrameId, singleReadFrame.SourceHash));

        Assert.Equal(singleReadFrame.Input.CornerCount, extraction.Features.Count);
        Assert.NotNull(extraction.Receipt);

        var observations = dataset.Observations.ToArray();
        observations[0] = new CalibrationObservationInput(source.Frame, extraction.Features,
            source.SessionId, source.FrameId, source.SourceHash, extraction.Receipt);
        var result = await new CheckerboardIntrinsicsProcedure().ComputeAsync(
            new CalibrationComputationContext<CheckerboardIntrinsicsInput>(dataset.Input,
                observations));

        Assert.NotNull(result.Coefficients);
        Assert.NotNull(result.Evidence);
    }

    private static void AssertIndependentReconstruction(
        CheckerboardIntrinsicsInput input,
        IReadOnlyList<CalibrationObservationInput> observations,
        CheckerboardIntrinsicsCoefficients coefficients,
        CheckerboardIntrinsicsEvidence evidence)
    {
        const double projectionComparisonTolerancePixels = 1e-4d;
        Assert.Equal(observations.Count, evidence.Views.Count);

        var totalSquaredResidual = 0d;
        var totalPointCount = 0;
        for (var viewIndex = 0; viewIndex < observations.Count; viewIndex++)
        {
            var observation = observations[viewIndex];
            var view = evidence.Views[viewIndex];
            Assert.Equal(observation.FrameId, view.FrameId);
            Assert.Equal(observation.SourceHash, view.SourceHash);
            Assert.Equal(input.CornerCount, view.Residuals.Count);

            var viewSquaredResidual = 0d;
            for (var pointIndex = 0; pointIndex < input.CornerCount; pointIndex++)
            {
                var observed = observation.Features[pointIndex];
                var projected = ProjectBrown5(input, coefficients,
                    view.RotationRadians, view.TranslationMillimeters, pointIndex);
                var expectedDeltaX = observed.PixelX - projected.X;
                var expectedDeltaY = observed.PixelY - projected.Y;
                var residual = view.Residuals[pointIndex];

                Assert.Equal(pointIndex, residual.CornerIndex);
                Assert.InRange(Math.Abs(expectedDeltaX - residual.DeltaX), 0d,
                    projectionComparisonTolerancePixels);
                Assert.InRange(Math.Abs(expectedDeltaY - residual.DeltaY), 0d,
                    projectionComparisonTolerancePixels);

                var squaredResidual = expectedDeltaX * expectedDeltaX + expectedDeltaY * expectedDeltaY;
                Assert.True(double.IsFinite(squaredResidual));
                viewSquaredResidual += squaredResidual;
                totalSquaredResidual += squaredResidual;
                totalPointCount++;
            }

            var viewRms = Math.Sqrt(viewSquaredResidual / input.CornerCount);
            Assert.InRange(Math.Abs(viewRms - view.RmsPixels), 0d,
                projectionComparisonTolerancePixels);
        }

        Assert.True(totalPointCount > 0);
        var aggregateRms = Math.Sqrt(totalSquaredResidual / totalPointCount);
        Assert.InRange(Math.Abs(aggregateRms - evidence.RmsPixels), 0d,
            projectionComparisonTolerancePixels);
    }

    private static (double X, double Y) ProjectBrown5(
        CheckerboardIntrinsicsInput input,
        CheckerboardIntrinsicsCoefficients coefficients,
        IReadOnlyList<double> rotationVector,
        IReadOnlyList<double> translation,
        int pointIndex)
    {
        var rotation = Rodrigues(rotationVector);
        var column = pointIndex % input.InnerColumns;
        var row = pointIndex / input.InnerColumns;
        var objectX = column * input.SquareSizeMillimeters;
        var objectY = row * input.SquareSizeMillimeters;
        var cameraX = rotation[0, 0] * objectX + rotation[0, 1] * objectY + translation[0];
        var cameraY = rotation[1, 0] * objectX + rotation[1, 1] * objectY + translation[1];
        var cameraZ = rotation[2, 0] * objectX + rotation[2, 1] * objectY + translation[2];
        if (!double.IsFinite(cameraX) || !double.IsFinite(cameraY) ||
            !double.IsFinite(cameraZ) || cameraZ <= 0d)
            throw new InvalidOperationException("IndependentProjectionDepthInvalid");

        var x = cameraX / cameraZ;
        var y = cameraY / cameraZ;
        var radiusSquared = x * x + y * y;
        var radiusFourth = radiusSquared * radiusSquared;
        var radial = 1d + coefficients.K1 * radiusSquared +
            coefficients.K2 * radiusFourth + coefficients.K3 * radiusFourth * radiusSquared;
        var distortedX = x * radial + 2d * coefficients.P1 * x * y +
            coefficients.P2 * (radiusSquared + 2d * x * x);
        var distortedY = y * radial + coefficients.P1 * (radiusSquared + 2d * y * y) +
            2d * coefficients.P2 * x * y;
        return (coefficients.Fx * distortedX + coefficients.Cx,
            coefficients.Fy * distortedY + coefficients.Cy);
    }

    private static double[,] Rodrigues(IReadOnlyList<double> vector)
    {
        Assert.Equal(3, vector.Count);
        var x = vector[0];
        var y = vector[1];
        var z = vector[2];
        var thetaSquared = x * x + y * y + z * z;
        var theta = Math.Sqrt(thetaSquared);
        var a = thetaSquared < 1e-12d
            ? 1d - thetaSquared / 6d + thetaSquared * thetaSquared / 120d
            : Math.Sin(theta) / theta;
        var b = thetaSquared < 1e-12d
            ? 0.5d - thetaSquared / 24d + thetaSquared * thetaSquared / 720d
            : (1d - Math.Cos(theta)) / thetaSquared;
        var skew = new[,]
        {
            { 0d, -z, y },
            { z, 0d, -x },
            { -y, x, 0d }
        };
        var skewSquared = new double[3, 3];
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
        for (var inner = 0; inner < 3; inner++)
            skewSquared[row, column] += skew[row, inner] * skew[inner, column];

        var result = new double[3, 3];
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
            result[row, column] = (row == column ? 1d : 0d) +
                a * skew[row, column] + b * skewSquared[row, column];
        return result;
    }

    private static CalibrationImageFeature[] ReverseCoordinatesKeepAscendingIds(
        IReadOnlyList<CalibrationImageFeature> features)
    {
        var reversed = new CalibrationImageFeature[features.Count];
        for (var index = 0; index < features.Count; index++)
        {
            var source = features[features.Count - index - 1];
            reversed[index] = new CalibrationImageFeature(features[index].StableFeatureId,
                source.PixelX, source.PixelY);
        }
        return reversed;
    }

    private enum ReceiptMutation
    {
        Missing,
        WrongFormat,
        WrongLength,
        TamperedBytes,
        SourceHash,
        FrameId,
        SessionId,
        PixelBytes,
        Input,
        Configuration
    }

    private void WriteExtractionComparison(string label,
        IReadOnlyList<CalibrationImageFeature> left,
        IReadOnlyList<CalibrationImageFeature> right)
    {
        var forward = CompareExtraction(left, right, reverse: false);
        var reverse = CompareExtraction(left, right, reverse: true);
        _output.WriteLine($"{label} forward: maxAbsDx={Format(forward.MaxAbsDx)}, " +
            $"maxAbsDy={Format(forward.MaxAbsDy)}, maxAbs={Format(Math.Max(forward.MaxAbsDx, forward.MaxAbsDy))}, " +
            $"firstMismatch={forward.FirstMismatch}");
        _output.WriteLine($"{label} reverse: maxAbsDx={Format(reverse.MaxAbsDx)}, " +
            $"maxAbsDy={Format(reverse.MaxAbsDy)}, maxAbs={Format(Math.Max(reverse.MaxAbsDx, reverse.MaxAbsDy))}, " +
            $"firstMismatch={reverse.FirstMismatch}");
    }

    private static ExtractionComparison CompareExtraction(
        IReadOnlyList<CalibrationImageFeature> left,
        IReadOnlyList<CalibrationImageFeature> right,
        bool reverse)
    {
        if (left.Count != right.Count)
            return new ExtractionComparison(double.NaN, double.NaN,
                $"count left={left.Count}, right={right.Count}");

        var maxAbsDx = 0d;
        var maxAbsDy = 0d;
        var firstMismatch = "none";
        for (var leftIndex = 0; leftIndex < left.Count; leftIndex++)
        {
            var rightIndex = reverse ? right.Count - leftIndex - 1 : leftIndex;
            var leftFeature = left[leftIndex];
            var rightFeature = right[rightIndex];
            var dx = Math.Abs(leftFeature.PixelX - rightFeature.PixelX);
            var dy = Math.Abs(leftFeature.PixelY - rightFeature.PixelY);
            if (dx > maxAbsDx) maxAbsDx = dx;
            if (dy > maxAbsDy) maxAbsDy = dy;
            if (firstMismatch == "none" &&
                (dx != 0d || dy != 0d || leftFeature.StableFeatureId != rightFeature.StableFeatureId))
            {
                firstMismatch = $"leftIndex={leftIndex}, rightIndex={rightIndex}, " +
                    $"leftId={leftFeature.StableFeatureId}, rightId={rightFeature.StableFeatureId}, " +
                    $"left=({Format(leftFeature.PixelX)},{Format(leftFeature.PixelY)}), " +
                    $"right=({Format(rightFeature.PixelX)},{Format(rightFeature.PixelY)}), " +
                    $"absDelta=({Format(dx)},{Format(dy)})";
            }
        }
        return new ExtractionComparison(maxAbsDx, maxAbsDy, firstMismatch);
    }

    private static string ComputeRowSpanHash(VisionFrame frame)
    {
        var rowBytes = frame.Metadata.ValidRowBytes;
        var pixels = new byte[checked(rowBytes * frame.Height)];
        for (var rowIndex = 0; rowIndex < frame.Height; rowIndex++)
        {
            var row = frame.GetRowSpan(rowIndex);
            if (row.Length < rowBytes) throw new InvalidOperationException("DiagnosticRowUnavailable");
            row[..rowBytes].CopyTo(pixels.AsSpan(checked(rowIndex * rowBytes), rowBytes));
        }
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private readonly record struct ExtractionComparison(double MaxAbsDx, double MaxAbsDy,
        string FirstMismatch);

    private sealed class FrozenDataset : IDisposable
    {
        private readonly IReadOnlyList<CalibrationTestFrame> _frames;

        private FrozenDataset(IReadOnlyList<CalibrationTestFrame> frames,
            CheckerboardIntrinsicsInput input, IReadOnlyList<CalibrationObservationInput> observations,
            IReadOnlyList<int> poseIndexes)
        {
            _frames = frames;
            Input = input;
            Observations = observations;
            PoseIndexes = poseIndexes;
        }

        public CheckerboardIntrinsicsInput Input { get; }
        public IReadOnlyList<CalibrationObservationInput> Observations { get; }
        public IReadOnlyList<int> PoseIndexes { get; }

        public static async Task<FrozenDataset> CreateAsync(IEnumerable<int> poseIndexes)
        {
            var selectedPoseIndexes = poseIndexes.ToArray();
            var frames = new List<CalibrationTestFrame>();
            var observations = new List<CalibrationObservationInput>();
            var sessionId = Guid.Parse("12500000-0000-0000-0000-000000000001");
            CheckerboardIntrinsicsInput? input = null;
            try
            {
                foreach (var poseIndex in selectedPoseIndexes)
                {
                    var frame = CalibrationTestFrame.Create(SyntheticCheckerboardFixture.Render(poseIndex));
                    frames.Add(frame);
                    input ??= frame.Input;
                    var frameId = Guid.Parse($"12500000-0000-0000-0000-{frames.Count:D12}");
                    var extraction = await new CheckerboardIntrinsicsProcedure().ExtractAsync(
                        new CalibrationExtractionContext<CheckerboardIntrinsicsInput>(frame.Frame, frame.Input,
                            sessionId, frameId, frame.SourceHash));
                    if (extraction.Features.Count != frame.Input.CornerCount)
                        throw new InvalidOperationException("FrozenDatasetFeatureExtractionFailed");
                    observations.Add(new CalibrationObservationInput(frame.Frame, extraction.Features,
                        sessionId, frameId, frame.SourceHash, extraction.Receipt));
                }

                if (input is null) throw new InvalidOperationException("FrozenDatasetEmpty");
                return new FrozenDataset(frames, input, observations, selectedPoseIndexes);
            }
            catch
            {
                foreach (var frame in frames) frame.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var frame in _frames) frame.Dispose();
        }
    }

    private sealed class FrontoparallelDataset : IDisposable
    {
        private readonly IReadOnlyList<CalibrationTestFrame> _frames;

        private FrontoparallelDataset(IReadOnlyList<CalibrationTestFrame> frames,
            CheckerboardIntrinsicsInput input, IReadOnlyList<CalibrationObservationInput> observations)
        {
            _frames = frames;
            Input = input;
            Observations = observations;
        }

        public CheckerboardIntrinsicsInput Input { get; }
        public IReadOnlyList<CalibrationObservationInput> Observations { get; }

        public static async Task<FrontoparallelDataset> CreateAsync()
        {
            var frames = new List<CalibrationTestFrame>();
            var observations = new List<CalibrationObservationInput>();
            var sessionId = Guid.Parse("12500000-0000-0000-0000-000000000014");
            CheckerboardIntrinsicsInput? input = null;
            try
            {
                foreach (var offsetX in new[] { 0, 20, 40 })
                {
                    var frame = CalibrationTestFrame.Create(CreatePixels(offsetX));
                    frames.Add(frame);
                    input ??= frame.Input;
                    var frameId = Guid.Parse($"12500000-0000-0000-0000-{frames.Count + 100:D12}");
                    var extraction = await new CheckerboardIntrinsicsProcedure().ExtractAsync(
                        new CalibrationExtractionContext<CheckerboardIntrinsicsInput>(frame.Frame,
                            frame.Input, sessionId, frameId, frame.SourceHash));
                    if (extraction.Features.Count != frame.Input.CornerCount)
                        throw new InvalidOperationException("FrontoparallelDatasetFeatureExtractionFailed");
                    observations.Add(new CalibrationObservationInput(frame.Frame, extraction.Features,
                        sessionId, frameId, frame.SourceHash, extraction.Receipt));
                }

                if (input is null) throw new InvalidOperationException("FrontoparallelDatasetEmpty");
                return new FrontoparallelDataset(frames, input, observations);
            }
            catch
            {
                foreach (var frame in frames) frame.Dispose();
                throw;
            }
        }

        private static byte[] CreatePixels(int offsetX)
        {
            const int width = 640;
            const int height = 480;
            const int square = 30;
            const int boardX = 130;
            const int boardY = 100;
            var pixels = new byte[width * height];
            Array.Fill(pixels, SyntheticCheckerboardFixture.BackgroundValue);
            for (var row = 0; row < 7; row++)
            for (var column = 0; column < 10; column++)
            {
                var value = ((row + column) & 1) == 0
                    ? SyntheticCheckerboardFixture.WhiteValue
                    : SyntheticCheckerboardFixture.BlackValue;
                for (var y = 0; y < square; y++)
                for (var x = 0; x < square; x++)
                {
                    var pixelX = boardX + offsetX + column * square + x;
                    var pixelY = boardY + row * square + y;
                    pixels[pixelY * width + pixelX] = value;
                }
            }
            return pixels;
        }

        public void Dispose()
        {
            foreach (var frame in _frames) frame.Dispose();
        }
    }

    private sealed class SingleReadRowSpanFrame : VisionFrame, IDisposable
    {
        private readonly byte[] _pixels;
        private readonly int[] _rowReads;
        private int _active = 1;

        private SingleReadRowSpanFrame(FrameMetadata metadata, byte[] pixels,
            CheckerboardIntrinsicsInput input, string sourceHash)
            : base(metadata)
        {
            _pixels = pixels;
            _rowReads = new int[metadata.Height];
            Input = input;
            SourceHash = sourceHash;
        }

        public CheckerboardIntrinsicsInput Input { get; }
        public VisionFrame Frame => this;
        public string SourceHash { get; }
        public override bool IsLoanActive => Volatile.Read(ref _active) != 0;

        public override ReadOnlySpan<byte> GetRowSpan(int row)
        {
            if (!IsLoanActive) throw new ObjectDisposedException(nameof(SingleReadRowSpanFrame));
            if ((uint)row >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(row));
            if (Interlocked.Increment(ref _rowReads[row]) != 1)
                throw new InvalidOperationException("SingleReadRowSpanFrameReadTwice");
            return _pixels.AsSpan(checked(row * Metadata.ValidRowBytes), Metadata.ValidRowBytes);
        }

        public static SingleReadRowSpanFrame Create(byte[] pixels)
        {
            ArgumentNullException.ThrowIfNull(pixels);
            const int width = SyntheticCheckerboardFixture.ImageWidth;
            const int height = SyntheticCheckerboardFixture.ImageHeight;
            if (pixels.Length != width * height)
                throw new ArgumentException("SingleReadRowSpanFramePixelCountInvalid", nameof(pixels));

            var configuration = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
                1000, 0, new RegionOfInterest(0, 0, width, height), VisionPixelFormat.Mono8,
                null, 1000, 0, null);
            var input = new CheckerboardIntrinsicsInput(
                SyntheticCheckerboardFixture.InnerCornerColumns,
                SyntheticCheckerboardFixture.InnerCornerRows,
                SyntheticCheckerboardFixture.SquareSizeMillimeters,
                "calibration-camera", configuration);
            var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
            var metadata = new FrameMetadata(correlation, input.LogicalCameraRole, width, height,
                width, VisionPixelFormat.Mono8, null, DateTimeOffset.UtcNow, configuration);
            var copy = (byte[])pixels.Clone();
            return new SingleReadRowSpanFrame(metadata, copy, input,
                Convert.ToHexString(SHA256.HashData(pixels)));
        }

        public void Dispose() => Interlocked.Exchange(ref _active, 0);
    }

    private sealed class ManagedRowSpanFrame : VisionFrame, IDisposable
    {
        private readonly byte[] _pixels;
        private int _active = 1;

        private ManagedRowSpanFrame(FrameMetadata metadata, byte[] pixels,
            CheckerboardIntrinsicsInput input, Guid sessionId, Guid frameId, string sourceHash)
            : base(metadata)
        {
            _pixels = pixels;
            Input = input;
            SessionId = sessionId;
            FrameId = frameId;
            SourceHash = sourceHash;
        }

        public CheckerboardIntrinsicsInput Input { get; }
        public Guid SessionId { get; }
        public Guid FrameId { get; }
        public string SourceHash { get; }
        public VisionFrame Frame => this;
        public override bool IsLoanActive => Volatile.Read(ref _active) != 0;

        public override ReadOnlySpan<byte> GetRowSpan(int row)
        {
            if (!IsLoanActive) throw new ObjectDisposedException(nameof(ManagedRowSpanFrame));
            if ((uint)row >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(row));
            return _pixels.AsSpan(checked(row * Metadata.ValidRowBytes), Metadata.ValidRowBytes);
        }

        public static ManagedRowSpanFrame Create(byte[] pixels)
        {
            ArgumentNullException.ThrowIfNull(pixels);
            var configuration = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
                1000, 0, new RegionOfInterest(0, 0, SyntheticCheckerboardFixture.ImageWidth,
                    SyntheticCheckerboardFixture.ImageHeight), VisionPixelFormat.Mono8, null, 1000, 0, null);
            var input = new CheckerboardIntrinsicsInput(SyntheticCheckerboardFixture.InnerCornerColumns,
                SyntheticCheckerboardFixture.InnerCornerRows, SyntheticCheckerboardFixture.SquareSizeMillimeters,
                "calibration-camera", configuration);
            var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
            var metadata = new FrameMetadata(correlation, input.LogicalCameraRole, input.ImageWidth,
                input.ImageHeight, input.ImageWidth, VisionPixelFormat.Mono8, null,
                DateTimeOffset.UtcNow, configuration);
            var sessionId = Guid.Parse("12500000-0000-0000-0000-000000000001");
            var frameId = Guid.Parse("12500000-0000-0000-0000-000000000001");
            var sourceHash = Convert.ToHexString(SHA256.HashData(pixels));
            return new ManagedRowSpanFrame(metadata, (byte[])pixels.Clone(), input,
                sessionId, frameId, sourceHash);
        }

        public void Dispose() => Interlocked.Exchange(ref _active, 0);
    }
}
