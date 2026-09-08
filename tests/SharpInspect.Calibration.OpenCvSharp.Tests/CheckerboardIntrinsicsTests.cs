using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Frames;
using Xunit;

namespace SharpInspect.Calibration.OpenCvSharp.Tests;

public sealed class CheckerboardIntrinsicsTests
{
    [Fact]
    public async Task V125_E02_FrozenSyntheticCornersMatchIndependentProjection()
    {
        using var frame = CalibrationTestFrame.Create(SyntheticCheckerboardFixture.Render(0));
        var result = await new CheckerboardIntrinsicsProcedure().ExtractAsync(new(frame.Frame, frame.Input,
            Guid.NewGuid(), Guid.NewGuid(), frame.SourceHash));
        Assert.Equal(54, result.Features.Count);
        var expected = SyntheticCheckerboardFixture.ProjectCorners(0);
        var forward = result.Features.Select((p, i) => Distance(p, expected[i])).Max();
        var reverse = result.Features.Select((p, i) => Distance(p, expected[expected.Count - 1 - i])).Max();
        Assert.True(Math.Min(forward, reverse) <= 1, $"Forward={forward:R}, reverse={reverse:R}");
        static double Distance(CalibrationImageFeature feature, SyntheticCheckerboardImagePoint point) =>
            Math.Sqrt(Math.Pow(feature.PixelX - point.X, 2) + Math.Pow(feature.PixelY - point.Y, 2));
    }

    [Fact]
    public async Task V125_E01_BlankImageRetainsExplicitRejectionWithoutInventedCorners()
    {
        using var frame = CalibrationTestFrame.Create(new byte[640 * 480]);
        var procedure = new CheckerboardIntrinsicsProcedure();
        var result = await procedure.ExtractAsync(new(frame.Frame, frame.Input, Guid.NewGuid(),
            Guid.NewGuid(), frame.SourceHash));
        Assert.Empty(result.Features);
        Assert.Contains(result.Diagnostics, item => item.Key == "CheckerboardNotFound");
    }
}

internal sealed class CalibrationTestFrame : IDisposable
{
    private readonly FrameBufferPool _pool;
    private readonly FrameBufferLease _owner;
    private CalibrationTestFrame(FrameBufferPool pool, FrameBufferLease owner, byte[] pixels,
        CheckerboardIntrinsicsInput input)
    { _pool = pool; _owner = owner; Input = input; SourceHash = Convert.ToHexString(SHA256.HashData(pixels)); }
    public VisionFrame Frame => _owner.Frame;
    public CheckerboardIntrinsicsInput Input { get; }
    public string SourceHash { get; }
    public static CalibrationTestFrame Create(byte[] pixels, EffectiveCameraConfiguration? configuration = null)
    {
        var c = configuration ?? new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            1000, 0, new RegionOfInterest(0, 0, 640, 480), VisionPixelFormat.Mono8, null, 1000, 0, null);
        var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
        var metadata = new FrameMetadata(correlation, "calibration-camera", c.RegionOfInterest.Width,
            c.RegionOfInterest.Height, c.RegionOfInterest.Width, c.PixelFormat, c.ValidBits, DateTimeOffset.UtcNow, c);
        var provenance = new FrameProvenance(correlation, "test.provider", "1", "test.adapter", "1", "test.sdk", "1",
            null, "test-device", null, null, c.PixelFormat.ToString(), "synthetic-independent-render", false, false,
            null, null, new FrameAcquisitionMilestones(1, null, null, null, null));
        var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, pixels.Length, TimeSpan.FromSeconds(1)));
        var copy = pool.TryCopyFrame(metadata, provenance, pixels);
        if (!copy.Succeeded || copy.Lease is null) { pool.Dispose(); throw new InvalidOperationException(copy.ReasonCode); }
        return new CalibrationTestFrame(pool, copy.Lease, pixels,
            new CheckerboardIntrinsicsInput(9, 6, 25, "calibration-camera", c));
    }
    public void Dispose() { _owner.Dispose(); _pool.Dispose(); }
}
