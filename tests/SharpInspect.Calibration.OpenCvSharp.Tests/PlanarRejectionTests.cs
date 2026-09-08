using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Calibration.OpenCvSharp.Tests;

public sealed class PlanarRejectionTests
{
    [Fact]
    public async Task V126_R01_UnknownDecodedMarkerInvalidatesTheWholeObservation()
    {
        var input = Input(marker => marker.Id != 3);
        using var frame = CalibrationTestFrame.Create(SyntheticPlanarFixture.Render());
        var result = await Extract(frame, input);
        Assert.Empty(result.Features);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Key == "PlanarUnknownMarkerDetected");
        Assert.NotNull(result.Receipt);
    }

    [Fact]
    public async Task V126_R02_RepeatedDecodedMarkerIdentityIsNotSilentlyChosenOrMerged()
    {
        var source = SyntheticPlanarFixture.Render();
        var doubled = new byte[source.Length * 2];
        for (var row = 0; row < 480; row++)
        {
            Buffer.BlockCopy(source, row * 640, doubled, row * 1280, 640);
            Buffer.BlockCopy(source, row * 640, doubled, row * 1280 + 640, 640);
        }
        var input = Input(width: 1280);
        using var frame = CalibrationTestFrame.Create(doubled, input.ExpectedConfiguration);
        var result = await Extract(frame, input);
        Assert.Empty(result.Features);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Key == "PlanarDuplicateMarkerDetected");
        Assert.NotNull(result.Receipt);
    }

    [Fact]
    public async Task V126_R03_NearlyCollinearDeclaredPlaneFailsRankBeforeBecomingCandidate()
    {
        var input = Input(physicalYScale: 1e-8);
        using var frame = CalibrationTestFrame.Create(SyntheticPlanarFixture.Render());
        var session = Guid.NewGuid(); var frameId = Guid.NewGuid();
        var procedure = new PlanarHomographyProcedure();
        var extraction = await procedure.ExtractAsync(new CalibrationExtractionContext<PlanarHomographyInput>(
            frame.Frame, input, session, frameId, frame.SourceHash));
        Assert.Equal(16, extraction.Features.Count);
        var error = await Assert.ThrowsAsync<CalibrationProcedureException>(() => procedure.ComputeAsync(
            new CalibrationComputationContext<PlanarHomographyInput>(input, new[]
            {
                new CalibrationObservationInput(frame.Frame, extraction.Features, session, frameId,
                    frame.SourceHash, extraction.Receipt)
            })).AsTask());
        Assert.Equal("PlanarConstraintRankInsufficient", error.ReasonCode);
    }

    [Fact]
    public async Task V126_R04_IncompleteCorrespondenceSetCannotReuseExtractionAuthority()
    {
        var input = Input();
        using var frame = CalibrationTestFrame.Create(SyntheticPlanarFixture.Render());
        var session = Guid.NewGuid(); var frameId = Guid.NewGuid();
        var procedure = new PlanarHomographyProcedure();
        var extraction = await procedure.ExtractAsync(new CalibrationExtractionContext<PlanarHomographyInput>(
            frame.Frame, input, session, frameId, frame.SourceHash));
        var error = await Assert.ThrowsAsync<CalibrationProcedureException>(() => procedure.ComputeAsync(
            new CalibrationComputationContext<PlanarHomographyInput>(input, new[]
            {
                new CalibrationObservationInput(frame.Frame, extraction.Features.Take(3), session, frameId,
                    frame.SourceHash, extraction.Receipt)
            })).AsTask());
        Assert.Equal("PlanarCorrespondenceCountInsufficient", error.ReasonCode);
    }

    private static ValueTask<CalibrationExtractionResult> Extract(CalibrationTestFrame frame, PlanarHomographyInput input) =>
        new PlanarHomographyProcedure().ExtractAsync(new CalibrationExtractionContext<PlanarHomographyInput>(
            frame.Frame, input, Guid.NewGuid(), Guid.NewGuid(), frame.SourceHash));

    private static PlanarHomographyInput Input(Func<SyntheticPlanarMarker, bool>? predicate = null,
        int width = 640, double physicalYScale = 1) => new("calibration-camera",
        new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 1000, 0,
            new RegionOfInterest(0, 0, width, 480), VisionPixelFormat.Mono8, null, 1000, 0, null),
        PlanarPixelDomain.RawRoiPixelCentersCorrectionDeclaredNotRequired,
        new ArucoPlanarTargetDefinition("Target", "Plane", SyntheticPlanarFixture.Markers.Where(predicate ?? (_ => true))
            .Select(marker => new ArucoPlanarMarkerDefinition(marker.Id, marker.PhysicalCorners.Select(point =>
                new PlanarPoint(point.X, point.Y * physicalYScale))))));
}
