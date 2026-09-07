using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Frames;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class FrameBufferPoolTests
{
    [Fact]
    public void V111_B01_CopyUsesMinimumCoverageAndClearsEveryVisiblePaddingByteOnReuse()
    {
        var input = Input(ExecutionKind.Manual, width: 3, height: 2, stride: 5);
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 16, TimeSpan.FromSeconds(1)));

        var first = pool.TryCopyFrame(input.Metadata, input.Provenance,
            new byte[] { 1, 2, 3, 0xA1, 0xA2, 4, 5, 6 });
        var firstLease = Assert.IsType<FrameBufferLease>(first.Lease);
        firstLease.Dispose();

        var second = pool.TryCopyFrame(input.Metadata, input.Provenance,
            new byte[] { 10, 11, 12, 0xB1, 0xB2, 13, 14, 15 });
        var secondLease = Assert.IsType<FrameBufferLease>(second.Lease);
        var frame = secondLease.Frame;

        Assert.True(second.Succeeded, second.ReasonCode);
        Assert.Equal(new byte[] { 10, 11, 12 }, frame.GetRowSpan(0).ToArray());
        Assert.Equal(new byte[] { 13, 14, 15 }, frame.GetRowSpan(1).ToArray());
        Assert.Equal(1, pool.GetSnapshot().OutstandingLeases);

        var visible = ReadNativeBytes(frame, input.Metadata.StrideBytes * input.Metadata.Height);
        Assert.Equal(new byte[] { 10, 11, 12, 0, 0, 13, 14, 15, 0, 0 }, visible);

        secondLease.Dispose();
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
    }

    [Fact]
    public void V111_B02_ExactMinimumSourceCoverageSucceedsAndShortOrSmallPoolFailsBeforeClaim()
    {
        var input = Input(ExecutionKind.Manual, width: 3, height: 2, stride: 5);
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 10, TimeSpan.FromSeconds(1)));

        var shortSource = pool.TryCopyFrame(input.Metadata, input.Provenance,
            new byte[checked((int)input.Metadata.RequiredBufferLength - 1)]);
        Assert.False(shortSource.Succeeded);
        Assert.Equal("FrameBufferCoverageInvalid", shortSource.ReasonCode);
        Assert.Equal(ExecutionStatus.Error, shortSource.ExecutionStatus);
        Assert.Equal(InspectionDecision.Unknown, shortSource.Decision);
        Assert.Null(shortSource.Lease);
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);

        var exact = pool.TryCopyFrame(input.Metadata, input.Provenance, new byte[8]);
        Assert.True(exact.Succeeded, exact.ReasonCode);
        exact.Lease!.Dispose();

        using var tooSmall = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));
        var exceeds = tooSmall.TryCopyFrame(input.Metadata, input.Provenance, new byte[8]);
        Assert.False(exceeds.Succeeded);
        Assert.Equal("FrameExceedsPoolCapacity", exceeds.ReasonCode);
        Assert.Equal(0, tooSmall.GetSnapshot().OutstandingLeases);
    }

    [Fact]
    public void V111_B03_Mono16RejectsValuesAboveDeclaredBitsAndAcceptsFullSixteenBits()
    {
        foreach (var (validBits, maximum) in new[] { (10, 1023), (12, 4095) })
        {
            var input = Input(ExecutionKind.Manual, width: 2, height: 1, stride: 4,
                pixelFormat: VisionPixelFormat.Mono16, validBits);
            using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));

            var valid = pool.TryCopyFrame(input.Metadata, input.Provenance,
                UInt16Bytes((ushort)maximum, 0));
            Assert.True(valid.Succeeded, valid.ReasonCode);
            valid.Lease!.Dispose();

            var invalid = pool.TryCopyFrame(input.Metadata, input.Provenance,
                UInt16Bytes((ushort)(maximum + 1), 0));
            Assert.False(invalid.Succeeded);
            Assert.Equal("FrameMono16HighBitsInvalid", invalid.ReasonCode);
            Assert.Equal(ExecutionStatus.Error, invalid.ExecutionStatus);
            Assert.Equal(InspectionDecision.Unknown, invalid.Decision);
            Assert.Null(invalid.Lease);
            Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
        }

        var fullInput = Input(ExecutionKind.Manual, width: 2, height: 1, stride: 4,
            pixelFormat: VisionPixelFormat.Mono16, validBits: 16);
        using var fullPool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));
        var full = fullPool.TryCopyFrame(fullInput.Metadata, fullInput.Provenance,
            UInt16Bytes(ushort.MaxValue, 0));
        Assert.True(full.Succeeded, full.ReasonCode);
        Assert.Equal(UInt16Bytes(ushort.MaxValue, 0), full.Lease!.Frame.GetRowSpan(0).ToArray());
        full.Lease.Dispose();
    }

    [Fact]
    public void V111_B04_PreCancelledAcquisitionDoesNotClaimSlotAndTotalMemoryBoundIsChecked()
    {
        var input = Input(ExecutionKind.Manual, width: 2, height: 2, stride: 3);
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelled = pool.TryCopyFrame(input.Metadata, input.Provenance,
            new byte[(int)input.Metadata.RequiredBufferLength], cancellation.Token);
        Assert.False(cancelled.Succeeded);
        Assert.Equal("FrameAcquisitionCancelled", cancelled.ReasonCode);
        Assert.Equal(ExecutionStatus.Cancelled, cancelled.ExecutionStatus);
        Assert.Equal(InspectionDecision.Unknown, cancelled.Decision);
        Assert.Null(cancelled.Lease);
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);

        var recovered = pool.TryCopyFrame(input.Metadata, input.Provenance,
            new byte[(int)input.Metadata.RequiredBufferLength]);
        Assert.True(recovered.Succeeded, recovered.ReasonCode);
        recovered.Lease!.Dispose();

        Assert.Throws<ArgumentException>(() => new FrameBufferPoolOptions(
            capacity: 64, maximumFrameBytes: 8 * 1024 * 1024 + 1,
            callbackBudget: TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public async Task V111_B05_ConcurrentManualCopiesUseDistinctLeasesAndTrackPeak()
    {
        var input = Input(ExecutionKind.Manual, width: 2, height: 2, stride: 3);
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(4, 8, TimeSpan.FromSeconds(1)));

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(index =>
            Task.Run(() => pool.TryCopyFrame(input.Metadata, input.Provenance,
                new byte[] { (byte)index, 2, 0, (byte)(index + 3), 4 }))));

        Assert.All(results, result => Assert.True(result.Succeeded, result.ReasonCode));
        Assert.All(results, result => Assert.NotNull(result.Lease));
        Assert.Equal(4, results.Select(result => result.Lease!.LeaseId).Distinct().Count());
        var snapshot = pool.GetSnapshot();
        Assert.Equal(4, snapshot.OutstandingLeases);
        Assert.Equal(4, snapshot.PeakLeases);

        foreach (var result in results) result.Lease!.Dispose();
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
    }

    [Fact]
    public void V111_B06_ProductionExhaustionReturnsErrorUnknownAndRemainsLatchedAfterRelease()
    {
        var input = Input(ExecutionKind.Production, width: 2, height: 1, stride: 2);
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 4, TimeSpan.FromSeconds(1)));

        var first = pool.TryCopyFrame(input.Metadata, input.Provenance, new byte[] { 1, 2 });
        Assert.True(first.Succeeded, first.ReasonCode);

        var exhausted = pool.TryCopyFrame(input.Metadata, input.Provenance, new byte[] { 3, 4 });
        Assert.False(exhausted.Succeeded);
        Assert.Equal("FrameBufferExhausted", exhausted.ReasonCode);
        Assert.Equal(ExecutionStatus.Error, exhausted.ExecutionStatus);
        Assert.Equal(InspectionDecision.Unknown, exhausted.Decision);
        Assert.Equal(1, pool.GetSnapshot().ExhaustionCount);
        Assert.True(pool.GetSnapshot().ProductionFaultLatched);

        first.Lease!.Dispose();
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);

        var stillBlocked = pool.TryCopyFrame(input.Metadata, input.Provenance, new byte[] { 5, 6 });
        Assert.False(stillBlocked.Succeeded);
        Assert.Equal("FrameBufferExhausted", stillBlocked.ReasonCode);
        Assert.Null(stillBlocked.Lease);
        Assert.Equal(1, pool.GetSnapshot().ExhaustionCount);
        Assert.True(pool.ProductionFaultLatched);
    }

    [Fact]
    public void V111_B07_DisposeIsIdempotentAndStaleFrameCannotAffectReusedSlot()
    {
        var input = Input(ExecutionKind.Manual, width: 2, height: 1, stride: 3);
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));

        var first = pool.TryCopyFrame(input.Metadata, input.Provenance, new byte[] { 1, 2, 0xA0 });
        var firstLease = first.Lease!;
        var staleFrame = firstLease.Frame;
        var firstId = firstLease.LeaseId;
        firstLease.Dispose();
        firstLease.Dispose();

        Assert.False(staleFrame.IsLoanActive);
        Assert.Throws<InvalidOperationException>(() => staleFrame.GetRowSpan(0).ToArray());
        Assert.Throws<InvalidOperationException>(() => firstLease.Frame);
        Assert.Throws<InvalidOperationException>(() => firstLease.Provenance);
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);

        var second = pool.TryCopyFrame(input.Metadata, input.Provenance, new byte[] { 9, 8, 0xB0 });
        var secondLease = second.Lease!;
        Assert.NotEqual(firstId, secondLease.LeaseId);
        firstLease.Dispose();
        Assert.Equal(1, pool.GetSnapshot().OutstandingLeases);
        Assert.Equal(new byte[] { 9, 8 }, secondLease.Frame.GetRowSpan(0).ToArray());
        secondLease.Dispose();
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);

        pool.Dispose();
        pool.Dispose();
        Assert.True(pool.GetSnapshot().IsDisposed);
        var afterDispose = pool.TryCopyFrame(input.Metadata, input.Provenance, new byte[] { 7, 7, 7 });
        Assert.False(afterDispose.Succeeded);
        Assert.Equal("FramePoolDisposed", afterDispose.ReasonCode);
    }

    [Fact]
    public void V111_B08_CallbackBudgetFailureReleasesClaimedSlotAndIsCounted()
    {
        var input = Input(ExecutionKind.Manual, width: 2, height: 2, stride: 3);
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromTicks(1)));

        var result = pool.TryCopyFrame(input.Metadata, input.Provenance,
            new byte[(int)input.Metadata.RequiredBufferLength]);

        Assert.False(result.Succeeded);
        Assert.Equal("FrameCallbackBudgetExceeded", result.ReasonCode);
        Assert.Equal(ExecutionStatus.Error, result.ExecutionStatus);
        Assert.Equal(InspectionDecision.Unknown, result.Decision);
        Assert.Null(result.Lease);
        var snapshot = pool.GetSnapshot();
        Assert.Equal(0, snapshot.OutstandingLeases);
        Assert.Equal(1, snapshot.CallbackBudgetExceededCount);
    }

    [Fact]
    public async Task V111_B09_DisposeDuringPublicationDoesNotReleaseCopyingBufferEarly()
    {
        var input = Input(ExecutionKind.Manual, width: 3, height: 2, stride: 5);
        using var hookEntered = new ManualResetEventSlim(false);
        using var releaseHook = new ManualResetEventSlim(false);
        using var pool = new FrameBufferPool(
            new FrameBufferPoolOptions(1, 16, TimeSpan.FromSeconds(1)),
            beforePublicationForTesting: () =>
            {
                hookEntered.Set();
                releaseHook.Wait();
            });
        var copying = Task.Run(() => pool.TryCopyFrame(input.Metadata, input.Provenance,
            new byte[] { 1, 2, 3, 0xA1, 0xA2, 4, 5, 6 }));

        try
        {
            Assert.True(hookEntered.Wait(TimeSpan.FromSeconds(2)));
            pool.Dispose();
            var duringPublication = pool.GetSnapshot();
            Assert.True(duringPublication.IsDisposed);
            Assert.Equal(1, duringPublication.OutstandingLeases);

            releaseHook.Set();
            var result = await copying.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(result.Succeeded);
            Assert.Equal("FramePoolDisposed", result.ReasonCode);
            Assert.Null(result.Lease);
            Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
            Assert.False(pool.TryCopyFrame(input.Metadata, input.Provenance,
                new byte[] { 9, 8, 7, 0, 0, 6, 5, 4 }).Succeeded);
        }
        finally { releaseHook.Set(); }
    }

    [Fact]
    public async Task V111_B10_CancellationAtPublicationReleasesSlotAndNextLeaseHasNoPartialPixels()
    {
        var input = Input(ExecutionKind.Manual, width: 3, height: 2, stride: 5);
        using var hookEntered = new ManualResetEventSlim(false);
        using var releaseHook = new ManualResetEventSlim(false);
        var hookCount = 0;
        using var pool = new FrameBufferPool(
            new FrameBufferPoolOptions(1, 16, TimeSpan.FromSeconds(1)),
            beforePublicationForTesting: () =>
            {
                if (Interlocked.Increment(ref hookCount) != 1) return;
                hookEntered.Set();
                releaseHook.Wait();
            });
        using var cancellation = new CancellationTokenSource();
        var copying = Task.Run(() => pool.TryCopyFrame(input.Metadata, input.Provenance,
            new byte[] { 1, 2, 3, 0xA1, 0xA2, 4, 5, 6 }, cancellation.Token));

        try
        {
            Assert.True(hookEntered.Wait(TimeSpan.FromSeconds(2)));
            cancellation.Cancel();
            Assert.Equal(1, pool.GetSnapshot().OutstandingLeases);

            releaseHook.Set();
            var cancelled = await copying.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(cancelled.Succeeded);
            Assert.Equal("FrameAcquisitionCancelled", cancelled.ReasonCode);
            Assert.Equal(ExecutionStatus.Cancelled, cancelled.ExecutionStatus);
            Assert.Equal(InspectionDecision.Unknown, cancelled.Decision);
            Assert.Null(cancelled.Lease);
            Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);

            var next = pool.TryCopyFrame(input.Metadata, input.Provenance,
                new byte[] { 10, 11, 12, 0xB1, 0xB2, 13, 14, 15 });
            var nextLease = Assert.IsType<FrameBufferLease>(next.Lease);
            Assert.True(next.Succeeded, next.ReasonCode);
            Assert.Equal(new byte[] { 10, 11, 12, 0, 0, 13, 14, 15, 0, 0 },
                ReadNativeBytes(nextLease.Frame, input.Metadata.StrideBytes * input.Metadata.Height));
            nextLease.Dispose();
            Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
            Assert.Equal(2, hookCount);
        }
        finally { releaseHook.Set(); }
    }

    [Theory]
    [InlineData(10, 100, 200)]
    [InlineData(12, 1_000, 2_000)]
    [InlineData(16, 10_000, 20_000)]
    public void V111_B11_Mono16OddSourceStrideIsNormalizedToEvenDestinationStride(
        int validBits, ushort firstPixel, ushort secondPixel)
    {
        var input = Input(ExecutionKind.Manual, width: 2, height: 2, stride: 5,
            pixelFormat: VisionPixelFormat.Mono16, validBits);
        var originalMetadata = input.Metadata;
        var originalProvenance = input.Provenance;
        var source = OddStrideMono16Source(firstPixel, secondPixel, 0, 0);
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 12, TimeSpan.FromSeconds(1)));

        var copied = pool.TryCopyFrame(input.Metadata, input.Provenance, source);
        var lease = Assert.IsType<FrameBufferLease>(copied.Lease);
        var outputFrame = lease.Frame;

        Assert.True(copied.Succeeded, copied.ReasonCode);
        Assert.Equal(6, outputFrame.StrideBytes);
        Assert.Equal(4, outputFrame.Metadata.ValidRowBytes);
        Assert.Equal(10, outputFrame.Metadata.RequiredBufferLength);
        Assert.Equal(UInt16Bytes(firstPixel, secondPixel), outputFrame.GetRowSpan(0).ToArray());
        Assert.Equal(UInt16Bytes(0, 0), outputFrame.GetRowSpan(1).ToArray());
        Assert.Equal(new byte[]
        {
            source[0], source[1], source[2], source[3], 0, 0,
            source[5], source[6], source[7], source[8], 0, 0
        }, ReadNativeBytes(outputFrame, 12));

        Assert.Same(originalMetadata, input.Metadata);
        Assert.Equal(5, originalMetadata.StrideBytes);
        Assert.Equal(9, originalMetadata.RequiredBufferLength);
        Assert.Null(originalProvenance.PoolCopyEvidence);
        Assert.False(originalProvenance.NormalizationTransformed);

        var outputProvenance = lease.Provenance;
        var evidence = Assert.IsType<PoolCopyEvidence>(outputProvenance.PoolCopyEvidence);
        Assert.Equal(5, evidence.SourceStrideBytes);
        Assert.Equal(6, evidence.DestinationStrideBytes);
        Assert.True(evidence.UsedPreallocatedPixelBuffer);
        Assert.True(evidence.StrideChanged);
        Assert.False(evidence.InputNormalizationTransformed);
        Assert.True(evidence.IdentityPixelCopy);
        Assert.True(evidence.PaddingZeroed);
        Assert.True(outputProvenance.NormalizationTransformed);
        Assert.Equal(originalProvenance.ProviderId, outputProvenance.ProviderId);
        Assert.Equal(originalProvenance.NormalizationDetails, outputProvenance.NormalizationDetails);
        Assert.Equal(originalProvenance.Correlation, outputProvenance.Correlation);

        lease.Dispose();

        using var tooSmall = new FrameBufferPool(new FrameBufferPoolOptions(1, 10, TimeSpan.FromSeconds(1)));
        var rejected = tooSmall.TryCopyFrame(input.Metadata, input.Provenance, source);
        Assert.False(rejected.Succeeded);
        Assert.Equal("FrameExceedsPoolCapacity", rejected.ReasonCode);
        Assert.Equal(ExecutionStatus.Error, rejected.ExecutionStatus);
        Assert.Equal(InspectionDecision.Unknown, rejected.Decision);
        Assert.Null(rejected.Lease);
        Assert.Equal(0, tooSmall.GetSnapshot().OutstandingLeases);
    }

    [Fact]
    public void V111_B12_ProductionOversizeLayoutLatchesExhaustionWhileManualRemainsRecoverable()
    {
        var oversizeProduction = Input(ExecutionKind.Production, width: 2, height: 2, stride: 5,
            pixelFormat: VisionPixelFormat.Mono16, validBits: 10);
        var normalProduction = Input(ExecutionKind.Production, width: 2, height: 1, stride: 4,
            pixelFormat: VisionPixelFormat.Mono16, validBits: 10);
        var oversizeSource = OddStrideMono16Source(100, 200, 0, 0);
        using var productionPool = new FrameBufferPool(new FrameBufferPoolOptions(1, 10,
            TimeSpan.FromSeconds(1)));

        var oversize = productionPool.TryCopyFrame(oversizeProduction.Metadata,
            oversizeProduction.Provenance, oversizeSource);
        Assert.False(oversize.Succeeded);
        Assert.Equal("FrameBufferExhausted", oversize.ReasonCode);
        Assert.Equal(ExecutionStatus.Error, oversize.ExecutionStatus);
        Assert.Equal(InspectionDecision.Unknown, oversize.Decision);
        Assert.Null(oversize.Lease);
        Assert.Equal(1, productionPool.GetSnapshot().ExhaustionCount);
        Assert.True(productionPool.ProductionFaultLatched);
        Assert.Equal(0, productionPool.GetSnapshot().OutstandingLeases);

        var afterLatch = productionPool.TryCopyFrame(normalProduction.Metadata,
            normalProduction.Provenance, UInt16Bytes(1, 2));
        Assert.False(afterLatch.Succeeded);
        Assert.Equal("FrameBufferExhausted", afterLatch.ReasonCode);
        Assert.Null(afterLatch.Lease);
        Assert.Equal(1, productionPool.GetSnapshot().ExhaustionCount);
        Assert.True(productionPool.ProductionFaultLatched);

        var oversizeManual = Input(ExecutionKind.Manual, width: 2, height: 2, stride: 5,
            pixelFormat: VisionPixelFormat.Mono16, validBits: 10);
        var normalManual = Input(ExecutionKind.Manual, width: 2, height: 1, stride: 4,
            pixelFormat: VisionPixelFormat.Mono16, validBits: 10);
        using var manualPool = new FrameBufferPool(new FrameBufferPoolOptions(1, 10,
            TimeSpan.FromSeconds(1)));
        var manualRejected = manualPool.TryCopyFrame(oversizeManual.Metadata,
            oversizeManual.Provenance, oversizeSource);
        Assert.False(manualRejected.Succeeded);
        Assert.Equal("FrameExceedsPoolCapacity", manualRejected.ReasonCode);
        Assert.Equal(0, manualPool.GetSnapshot().ExhaustionCount);
        Assert.False(manualPool.ProductionFaultLatched);

        var manualNormal = manualPool.TryCopyFrame(normalManual.Metadata,
            normalManual.Provenance, UInt16Bytes(1, 2));
        Assert.True(manualNormal.Succeeded, manualNormal.ReasonCode);
        manualNormal.Lease!.Dispose();
        Assert.False(manualPool.ProductionFaultLatched);
    }

    private static FrameInput Input(ExecutionKind kind, int width, int height, int stride,
        VisionPixelFormat pixelFormat = VisionPixelFormat.Mono8, int? validBits = null)
    {
        var correlation = new ExecutionCorrelationId(kind, Guid.NewGuid());
        var configuration = new EffectiveCameraConfiguration(
            ProductionAcquisitionMode.HardwareTrigger, 500, 1.5,
            new RegionOfInterest(0, 0, width, height), pixelFormat, validBits,
            500, 0, null);
        var metadata = new FrameMetadata(correlation, "TopCamera", width, height, stride,
            pixelFormat, validBits, Utc(10), configuration);
        var provenance = new FrameProvenance(correlation, "vendor-a", "1", "adapter-a", "1",
            "sdk-a", "1", null, "device-1", null, null,
            pixelFormat.ToString(), "normalized-v1", false, false, null, null, Milestones());
        return new FrameInput(metadata, provenance);
    }

    private static byte[] UInt16Bytes(params ushort[] values)
    {
        var bytes = new byte[checked(values.Length * 2)];
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * 2, 2), values[index]);
        return bytes;
    }

    private static byte[] OddStrideMono16Source(ushort firstRowFirst, ushort firstRowSecond,
        ushort secondRowFirst, ushort secondRowSecond)
    {
        var bytes = new byte[9];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0, 2), firstRowFirst);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), firstRowSecond);
        bytes[4] = 0xEE;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(5, 2), secondRowFirst);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(7, 2), secondRowSecond);
        return bytes;
    }

    private static byte[] ReadNativeBytes(VisionFrame frame, int length)
    {
        var method = typeof(VisionFrame).GetMethod("AcquireNativeRead",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var native = method.Invoke(frame, null)!;
        try
        {
            var pointer = (IntPtr)native.GetType().GetProperty("DataPointer")!.GetValue(native)!;
            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return bytes;
        }
        finally { ((IDisposable)native).Dispose(); }
    }

    private static FrameAcquisitionMilestones Milestones() =>
        new(1_000_000, new FrameTimePoint(Utc(1), 10),
            new FrameTimePoint(Utc(2), 12), new FrameTimePoint(Utc(3), 14),
            new FrameTimePoint(Utc(4), 16));

    private static DateTimeOffset Utc(int second) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(second);

    private sealed record FrameInput(FrameMetadata Metadata, FrameProvenance Provenance);
}
