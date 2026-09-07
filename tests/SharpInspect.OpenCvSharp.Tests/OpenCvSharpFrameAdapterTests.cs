using System.Runtime.InteropServices;
using OpenCvSharp;
using SharpInspect.Abstractions;
using SharpInspect.OpenCvSharp;
using SharpInspect.Runtime.Frames;
using Xunit;

namespace SharpInspect.OpenCvSharp.Tests;

public sealed class OpenCvSharpFrameAdapterTests
{
    [Fact]
    public async Task V111_O06_PublicVisionAlgorithm_canConsumeFrameThroughScopedMatAdapter()
    {
        using var fixture = TestFrame.Create(
            width: 2,
            height: 1,
            strideBytes: 4,
            pixelFormat: VisionPixelFormat.Mono8,
            validBits: null,
            bytes: new byte[] { 10, 30, 0xAA, 0xBB });
        var configuration = new AlgorithmConfigurationSnapshot(
            "test.schema",
            "1",
            new string('A', 64),
            "test-canonical-v1",
            new string('B', 64),
            Array.Empty<AlgorithmConfigurationEntry>());
        var context = new AlgorithmExecutionContext(
            fixture.Frame.Correlation,
            configuration,
            fixture.Frame,
            new NoopDiagnosticSink());

        await using var algorithm = new MeanAlgorithm();
        var result = await algorithm.ExecuteAsync(context);

        Assert.Equal(InspectionDecision.Pass, result.Decision);
        Assert.Single(result.Measurements);
        Assert.Equal(20d, result.Measurements[0].Value.AsFloat64());
        Assert.Equal(0, fixture.Pool.GetSnapshot().ActiveReaders);
    }

    [Fact]
    public void V111_O01_WithMat_usesFrameStrideAndPixelFormatWithoutCopying()
    {
        using var fixture = TestFrame.Create(
            width: 3,
            height: 2,
            strideBytes: 5,
            pixelFormat: VisionPixelFormat.Mono8,
            validBits: null,
            bytes: new byte[] { 1, 2, 3, 0xEE, 0xEE, 4, 5, 6, 0xDD, 0xDD });

        Mat? observed = null;
        fixture.Frame.WithMat(mat =>
        {
            observed = mat;
            Assert.True(fixture.Pool.GetSnapshot().ActiveReaders > 0);
            fixture.Owner.Dispose();
            Assert.False(fixture.Owner.IsReturned);
            Assert.Equal(2, mat.Rows);
            Assert.Equal(3, mat.Cols);
            Assert.Equal(MatType.CV_8UC1, mat.Type());
            Assert.Equal(5, mat.Step());
            Assert.Equal((byte)1, ReadByte(mat, 0, 0));
            Assert.Equal((byte)3, ReadByte(mat, 0, 2));
            Assert.Equal((byte)4, ReadByte(mat, 1, 0));
            Assert.Equal((byte)6, ReadByte(mat, 1, 2));
        });

        Assert.NotNull(observed);
        Assert.True(observed!.IsDisposed);
        Assert.Equal(0, fixture.Pool.GetSnapshot().ActiveReaders);
        Assert.True(fixture.Owner.IsReturned);
    }

    [Fact]
    public void V111_O02_WithMat_mapsBgr24ChannelsAndStride()
    {
        using var fixture = TestFrame.Create(
            width: 2,
            height: 1,
            strideBytes: 8,
            pixelFormat: VisionPixelFormat.Bgr24,
            validBits: null,
            bytes: new byte[] { 10, 20, 30, 40, 50, 60, 0xAA, 0xBB });

        fixture.Frame.WithMat(mat =>
        {
            Assert.Equal(MatType.CV_8UC3, mat.Type());
            Assert.Equal(8, mat.Step());
            Assert.Equal((byte)10, ReadByte(mat, 0, 0));
            Assert.Equal((byte)20, ReadByte(mat, 0, 1));
            Assert.Equal((byte)30, ReadByte(mat, 0, 2));
            Assert.Equal((byte)40, ReadByte(mat, 0, 3));
            Assert.Equal((byte)50, ReadByte(mat, 0, 4));
            Assert.Equal((byte)60, ReadByte(mat, 0, 5));
        });
    }

    [Theory]
    [InlineData(10, 1023)]
    [InlineData(12, 4095)]
    [InlineData(16, 65535)]
    public void V111_O03_CloneToDisplayMat_scalesMono16UsingDeclaredValidBits(int validBits, int maximum)
    {
        var samples = new[] { (ushort)0, (ushort)(maximum / 2), (ushort)maximum };
        var bytes = new byte[8];
        for (var index = 0; index < samples.Length; index++)
        {
            bytes[index * 2] = (byte)samples[index];
            bytes[index * 2 + 1] = (byte)(samples[index] >> 8);
        }

        using var fixture = TestFrame.Create(
            width: 3,
            height: 1,
            strideBytes: bytes.Length,
            pixelFormat: VisionPixelFormat.Mono16,
            validBits: validBits,
            bytes: bytes);
        using var display = fixture.Frame.CloneToDisplayMat();

        Assert.Equal(MatType.CV_8UC1, display.Type());
        Assert.Equal((byte)0, display.Get<byte>(0, 0));
        Assert.InRange(display.Get<byte>(0, 1), (byte)127, (byte)128);
        Assert.Equal((byte)255, display.Get<byte>(0, 2));
        Assert.Equal(0, fixture.Pool.GetSnapshot().ActiveReaders);
    }

    [Fact]
    public void V111_O04_WithMat_disposesHeaderAndReadHoldWhenCallbackThrows()
    {
        using var fixture = TestFrame.Create(
            width: 1,
            height: 1,
            strideBytes: 1,
            pixelFormat: VisionPixelFormat.Mono8,
            validBits: null,
            bytes: new byte[] { 7 });
        Mat? observed = null;

        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Frame.WithMat(mat =>
        {
            observed = mat;
            throw new InvalidOperationException("callback-failure");
        }));

        Assert.Equal("callback-failure", exception.Message);
        Assert.NotNull(observed);
        Assert.True(observed!.IsDisposed);
        Assert.Equal(0, fixture.Pool.GetSnapshot().ActiveReaders);
    }

    [Fact]
    public void V111_O05_CloneToMat_returnsIndependentStorageAfterBorrowEnds()
    {
        using var fixture = TestFrame.Create(
            width: 2,
            height: 1,
            strideBytes: 4,
            pixelFormat: VisionPixelFormat.Mono8,
            validBits: null,
            bytes: new byte[] { 11, 22, 0xAA, 0xBB });

        using var clone = fixture.Frame.CloneToMat();
        fixture.Owner.Dispose();

        var replacement = fixture.Pool.TryCopyFrame(
            fixture.Metadata,
            fixture.Provenance,
            new byte[] { 99, 88, 0xCC, 0xDD });
        Assert.True(replacement.Succeeded);
        using (replacement.Lease!)
        {
            Assert.Equal((byte)11, clone.Get<byte>(0, 0));
            Assert.Equal((byte)22, clone.Get<byte>(0, 1));
        }
    }

    [Fact]
    public void V111_O08_FullNativeLayout_coversPaddingAndRejectsShortPool()
    {
        var input = TestFrame.CreateInput(
            width: 3,
            height: 2,
            strideBytes: 5,
            pixelFormat: VisionPixelFormat.Mono8,
            validBits: null);
        var source = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        using (var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 10, TimeSpan.FromSeconds(1))))
        {
            var copied = pool.TryCopyFrame(input.Metadata, input.Provenance, source);
            Assert.True(copied.Succeeded);
            using var owner = copied.Lease!;
            owner.Frame.WithMat(mat =>
            {
                Assert.Equal(5, mat.Step());
                Assert.Equal(IntPtr.Add(mat.Data, 8), mat.DataEnd);
                Assert.Equal(IntPtr.Add(mat.Data, 10), mat.DataLimit);
                Assert.Equal((byte)0, ReadByte(mat, 0, 3));
                Assert.Equal((byte)0, ReadByte(mat, 0, 4));
                Assert.Equal((byte)0, ReadByte(mat, 1, 3));
                Assert.Equal((byte)0, ReadByte(mat, 1, 4));
            });
        }

        using var shortPool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));
        var rejected = shortPool.TryCopyFrame(input.Metadata, input.Provenance, source);
        Assert.False(rejected.Succeeded);
        Assert.Equal("FrameExceedsPoolCapacity", rejected.ReasonCode);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(16)]
    public void V111_O10_PoolNormalizesOddMono16StrideBeforeZeroCopyMat(int validBits)
    {
        var input = TestFrame.CreateInput(
            width: 2,
            height: 2,
            strideBytes: 5,
            pixelFormat: VisionPixelFormat.Mono16,
            validBits: validBits);
        // The source has a five-byte row layout. The fifth byte in each row is
        // source padding and must not become part of the published native frame.
        var source = new byte[]
        {
            100, 0, 200, 0, 0xEE,
            44, 1, 144, 1
        };

        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(
            capacity: 1,
            maximumFrameBytes: 12,
            callbackBudget: TimeSpan.FromSeconds(1)));
        var copied = pool.TryCopyFrame(input.Metadata, input.Provenance, source);
        Assert.True(copied.Succeeded);
        using var owner = copied.Lease!;

        Assert.Equal(6, owner.Frame.StrideBytes);
        Assert.Equal(12, owner.Frame.Metadata.FullBufferLayoutLength);
        var copyEvidence = owner.Provenance.PoolCopyEvidence;
        Assert.NotNull(copyEvidence);
        Assert.Equal(5, copyEvidence!.SourceStrideBytes);
        Assert.Equal(6, copyEvidence.DestinationStrideBytes);
        Assert.True(copyEvidence.UsedPreallocatedPixelBuffer);
        Assert.True(copyEvidence.StrideChanged);
        Assert.False(copyEvidence.InputNormalizationTransformed);
        Assert.True(owner.Provenance.NormalizationTransformed);
        Assert.True(copyEvidence.PaddingZeroed);
        owner.Frame.WithMat(mat =>
        {
            Assert.Equal(MatType.CV_16UC1, mat.Type());
            Assert.Equal(6, mat.Step());
            Assert.Equal(IntPtr.Add(mat.Data, 12), mat.DataLimit);
            Assert.Equal((ushort)100, mat.Get<ushort>(0, 0));
            Assert.Equal((ushort)200, mat.Get<ushort>(0, 1));
            Assert.Equal((ushort)300, mat.Get<ushort>(1, 0));
            Assert.Equal((ushort)400, mat.Get<ushort>(1, 1));
            Assert.Equal((byte)0, ReadByte(mat, 0, 4));
            Assert.Equal((byte)0, ReadByte(mat, 0, 5));
            Assert.Equal((byte)0, ReadByte(mat, 1, 4));
            Assert.Equal((byte)0, ReadByte(mat, 1, 5));
        });
    }

    [Fact]
    public void V111_O11_UnnormalizedMono16Stride_failsClosedBeforeNativeBorrow()
    {
        var input = TestFrame.CreateInput(
            width: 2,
            height: 2,
            strideBytes: 5,
            pixelFormat: VisionPixelFormat.Mono16,
            validBits: 10);
        var frame = new MetadataOnlyFrame(input.Metadata);

        var exception = Assert.Throws<InvalidOperationException>(() => frame.WithMat(_ =>
            throw new InvalidOperationException("callback-must-not-run")));

        Assert.Equal("FrameMono16StrideUnaligned", exception.Message);
    }

    [Fact]
    public async Task V111_O07_ReadHold_keepsOwnerSlotUntilCallbackReturns()
    {
        using var fixture = TestFrame.Create(
            width: 2,
            height: 1,
            strideBytes: 2,
            pixelFormat: VisionPixelFormat.Mono8,
            validBits: null,
            bytes: new byte[] { 11, 22 });
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Mat? captured = null;
        var callback = Task.Run(() => fixture.Frame.WithMat(mat =>
        {
            captured = mat;
            entered.SetResult(true);
            release.Task.GetAwaiter().GetResult();
            Assert.Equal((byte)11, mat.Get<byte>(0, 0));
        }));

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Owner.Dispose();
            Assert.False(fixture.Owner.IsReturned);
            var replacement = fixture.Pool.TryCopyFrame(
                fixture.Metadata,
                fixture.Provenance,
                new byte[] { 99, 88 });
            Assert.False(replacement.Succeeded);
            Assert.Equal("FrameBufferExhausted", replacement.ReasonCode);
            Assert.Equal(1, fixture.Pool.GetSnapshot().OutstandingLeases);
            Assert.Equal(1, fixture.Pool.GetSnapshot().ActiveReaders);
        }
        finally
        {
            release.TrySetResult(true);
            await callback;
        }

        Assert.NotNull(captured);
        Assert.True(captured!.IsDisposed);
        Assert.Equal(0, fixture.Pool.GetSnapshot().ActiveReaders);
        Assert.Equal(0, fixture.Pool.GetSnapshot().OutstandingLeases);
        Assert.True(fixture.Owner.IsReturned);
    }

    [Fact]
    public async Task V111_O09_PoolDispose_doesNotReclaimActiveReadBufferEarly()
    {
        using var fixture = TestFrame.Create(
            width: 2,
            height: 1,
            strideBytes: 2,
            pixelFormat: VisionPixelFormat.Mono8,
            validBits: null,
            bytes: new byte[] { 31, 41 });
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Mat? captured = null;
        var callback = Task.Run(() => fixture.Frame.WithMat(mat =>
        {
            captured = mat;
            entered.SetResult(true);
            release.Task.GetAwaiter().GetResult();
            Assert.Equal((byte)31, mat.Get<byte>(0, 0));
        }));

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Pool.Dispose();
            var replacement = fixture.Pool.TryCopyFrame(
                fixture.Metadata,
                fixture.Provenance,
                new byte[] { 99, 88 });
            Assert.False(replacement.Succeeded);
            Assert.Equal("FramePoolDisposed", replacement.ReasonCode);
            Assert.Equal(1, fixture.Pool.GetSnapshot().OutstandingLeases);
            Assert.Equal(1, fixture.Pool.GetSnapshot().ActiveReaders);
        }
        finally
        {
            release.TrySetResult(true);
            await callback;
        }

        Assert.NotNull(captured);
        Assert.True(captured!.IsDisposed);
        Assert.Equal(0, fixture.Pool.GetSnapshot().ActiveReaders);
        Assert.Equal(1, fixture.Pool.GetSnapshot().OutstandingLeases);
        fixture.Owner.Dispose();
        Assert.Equal(0, fixture.Pool.GetSnapshot().OutstandingLeases);
        Assert.True(fixture.Owner.IsReturned);
    }

    private static byte ReadByte(Mat mat, int row, int offset) => Marshal.ReadByte(mat.Ptr(row), offset);

    private sealed class MetadataOnlyFrame : VisionFrame
    {
        public MetadataOnlyFrame(FrameMetadata metadata) : base(metadata) { }

        public override bool IsLoanActive => true;

        public override ReadOnlySpan<byte> GetRowSpan(int row) => ReadOnlySpan<byte>.Empty;
    }

    private sealed class MeanAlgorithm : IVisionAlgorithm
    {
        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            var total = 0d;
            var count = 0;
            context.Frame.WithMat(mat =>
            {
                for (var row = 0; row < mat.Rows; row++)
                for (var column = 0; column < mat.Cols; column++)
                {
                    total += mat.Get<byte>(row, column);
                    count++;
                }
            });

            var mean = total / count;
            var result = new AlgorithmResult(
                InspectionDecision.Pass,
                reasonCode: null,
                new[] { new AlgorithmMeasurement("Mean", "count", AlgorithmScalarValue.FromFloat64(mean)) },
                new OutputOverlaySet("overlay", "1"));
            return ValueTask.FromResult(result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopDiagnosticSink : IAlgorithmDiagnosticSink
    {
        public AlgorithmDiagnosticEmission TryEmit(AlgorithmDiagnosticEvent diagnosticEvent) =>
            AlgorithmDiagnosticEmission.Accepted;
    }

    private sealed class TestFrame : IDisposable
    {
        private TestFrame(FrameBufferPool pool, FrameBufferLease owner,
            FrameMetadata metadata, FrameProvenance provenance)
        {
            Pool = pool;
            Owner = owner;
            Metadata = metadata;
            Provenance = provenance;
        }

        public FrameBufferPool Pool { get; }
        public FrameBufferLease Owner { get; }
        public VisionFrame Frame => Owner.Frame;
        public FrameMetadata Metadata { get; }
        public FrameProvenance Provenance { get; }

        public void Dispose()
        {
            Owner.Dispose();
            Pool.Dispose();
        }

        public static TestFrame Create(int width, int height, int strideBytes,
            VisionPixelFormat pixelFormat, int? validBits, byte[] bytes,
            int? maximumFrameBytes = null)
        {
            var input = CreateInput(width, height, strideBytes, pixelFormat, validBits);
            var pool = new FrameBufferPool(new FrameBufferPoolOptions(
                capacity: 1,
                maximumFrameBytes: maximumFrameBytes ?? Math.Max(bytes.Length, 64),
                callbackBudget: TimeSpan.FromSeconds(1)));
            var result = pool.TryCopyFrame(input.Metadata, input.Provenance, bytes);
            if (!result.Succeeded || result.Lease is null)
            {
                pool.Dispose();
                throw new InvalidOperationException(result.ReasonCode);
            }

            return new TestFrame(pool, result.Lease, input.Metadata, input.Provenance);
        }

        public static (FrameMetadata Metadata, FrameProvenance Provenance) CreateInput(
            int width, int height, int strideBytes, VisionPixelFormat pixelFormat, int? validBits)
        {
            var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
            var region = new RegionOfInterest(0, 0, width, height);
            var camera = new EffectiveCameraConfiguration(
                ProductionAcquisitionMode.SoftwareTrigger,
                exposureTimeUs: 1000,
                gainDb: 0,
                region,
                pixelFormat,
                validBits,
                acquisitionTimeoutMs: 1000,
                triggerDelayUs: 0,
                whiteBalanceRgb: pixelFormat == VisionPixelFormat.Bgr24
                    ? new WhiteBalanceRgb(1, 1, 1)
                    : null);
            var metadata = new FrameMetadata(
                correlation,
                "test-camera",
                width,
                height,
                strideBytes,
                pixelFormat,
                validBits,
                DateTimeOffset.UtcNow,
                camera);
            var milestones = new FrameAcquisitionMilestones(1, null, null, null, null);
            var provenance = new FrameProvenance(
                correlation,
                "test.provider",
                "1",
                "test.adapter",
                "1",
                "test.sdk",
                "1",
                nativeRuntimeVersion: null,
                stableDeviceIdentity: "test-device",
                reportedModel: null,
                firmwareVersion: null,
                nativePixelFormatDescription: pixelFormat.ToString(),
                normalizationDetails: "test-normalized",
                normalizationAllocated: true,
                normalizationTransformed: false,
                deviceTimestamp: null,
                frameCounter: null,
                milestones);
            return (metadata, provenance);
        }
    }
}
