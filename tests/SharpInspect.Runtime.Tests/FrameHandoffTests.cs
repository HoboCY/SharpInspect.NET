using System.Threading.Channels;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Frames;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class FrameHandoffTests
{
    [Fact]
    public async Task V111_H01_PublishReadTransfersSameOwnerAndInvalidatesOnlyTheOriginalHandle()
    {
        var input = Input(ExecutionKind.Manual, width: 2, height: 1, stride: 3);
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));
        using var handoff = new FrameCallbackHandoff(1);
        var copied = pool.TryCopyFrame(input.Metadata, input.Provenance, new byte[] { 1, 2, 0xA0 });
        var original = copied.Lease!;
        var originalFrame = original.Frame;
        var originalProvenance = original.Provenance;
        var leaseId = original.LeaseId;

        Assert.True(handoff.TryPublish(original));
        Assert.False(original.IsReturned);
        Assert.Throws<InvalidOperationException>(() => original.Frame);
        Assert.Throws<InvalidOperationException>(() => original.Provenance);
        original.Dispose();
        Assert.True(originalFrame.IsLoanActive);
        Assert.Equal(new byte[] { 1, 2 }, originalFrame.GetRowSpan(0).ToArray());

        var transferred = await handoff.ReadAsync();
        Assert.Equal(leaseId, transferred.LeaseId);
        Assert.Same(originalFrame, transferred.Frame);
        Assert.Same(originalProvenance, transferred.Provenance);
        Assert.Equal(new byte[] { 1, 2 }, transferred.Frame.GetRowSpan(0).ToArray());
        Assert.Equal(1, pool.GetSnapshot().OutstandingLeases);

        transferred.Dispose();
        Assert.True(original.IsReturned);
        Assert.False(originalFrame.IsLoanActive);
        Assert.Throws<InvalidOperationException>(() => originalFrame.GetRowSpan(0).ToArray());
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
        Assert.Equal(0, handoff.RejectedCount);
    }

    [Fact]
    public async Task V111_H02_FullQueueConsumesRejectedOwnerAndAllowsCleanSlotReuse()
    {
        var input = Input(ExecutionKind.Manual, width: 2, height: 1, stride: 3);
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(2, 8, TimeSpan.FromSeconds(1)));
        using var handoff = new FrameCallbackHandoff(1);

        var first = pool.TryCopyFrame(input.Metadata, input.Provenance, new byte[] { 1, 2, 0xA0 }).Lease!;
        var second = pool.TryCopyFrame(input.Metadata, input.Provenance, new byte[] { 3, 4, 0xB0 }).Lease!;
        Assert.True(handoff.TryPublish(first));

        Assert.False(handoff.TryPublish(second));
        Assert.True(second.IsReturned);
        Assert.Throws<InvalidOperationException>(() => second.Frame);
        Assert.Equal(1, pool.GetSnapshot().OutstandingLeases);
        Assert.Equal(1, handoff.RejectedCount);
        Assert.False(handoff.TryPublish(second));
        Assert.Equal(2, handoff.RejectedCount);

        var received = await handoff.ReadAsync();
        received.Dispose();
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);

        var reused = pool.TryCopyFrame(input.Metadata, input.Provenance,
            new byte[] { 9, 8, 0xC0 }).Lease!;
        Assert.Equal(new byte[] { 9, 8 }, reused.Frame.GetRowSpan(0).ToArray());
        reused.Dispose();
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
    }

    [Fact]
    public async Task V111_H03_CancelledReadersDoNotConsumeOrReleaseAnotherFrame()
    {
        var input = Input(ExecutionKind.Manual, width: 2, height: 1, stride: 3);
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));
        using var handoff = new FrameCallbackHandoff(1);
        var first = pool.TryCopyFrame(input.Metadata, input.Provenance, new byte[] { 1, 2, 0xA0 }).Lease!;
        Assert.True(handoff.TryPublish(first));

        using var queuedCancellation = new CancellationTokenSource();
        queuedCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handoff.ReadAsync(queuedCancellation.Token).AsTask());
        Assert.Equal(1, pool.GetSnapshot().OutstandingLeases);

        var received = await handoff.ReadAsync();
        Assert.Equal(first.LeaseId, received.LeaseId);
        received.Dispose();
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);

        using var waitingCancellation = new CancellationTokenSource();
        var waiting = handoff.ReadAsync(waitingCancellation.Token).AsTask();
        waitingCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);

        var second = pool.TryCopyFrame(input.Metadata, input.Provenance,
            new byte[] { 7, 6, 0xB0 }).Lease!;
        Assert.True(handoff.TryPublish(second));
        var secondReceived = await handoff.ReadAsync();
        secondReceived.Dispose();
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
    }

    [Fact]
    public async Task V111_H04_OnlyOneReaderWaitsAndPublishDoesNotRunItsContinuationInline()
    {
        var input = Input(ExecutionKind.Manual, width: 2, height: 1, stride: 2);
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));
        using var handoff = new FrameCallbackHandoff(1);
        var pending = handoff.ReadAsync().AsTask();
        Assert.False(pending.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handoff.ReadAsync().AsTask());

        var lease = pool.TryCopyFrame(input.Metadata, input.Provenance, new byte[] { 4, 5 }).Lease!;
        var publishingThread = Environment.CurrentManagedThreadId;
        var continuationThread = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = pending.ContinueWith(_ => continuationThread.TrySetResult(Environment.CurrentManagedThreadId),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        Assert.True(handoff.TryPublish(lease));
        var received = await pending;
        var continuationThreadId = await continuationThread.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotEqual(publishingThread, continuationThreadId);
        received.Dispose();
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
    }

    [Fact]
    public async Task V111_H05_DisposeDrainsQueuedOwnersAndConsumesLaterPublishAttempts()
    {
        var input = Input(ExecutionKind.Manual, width: 2, height: 1, stride: 2);
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(2, 8, TimeSpan.FromSeconds(1)));
        var handoff = new FrameCallbackHandoff(2);
        var first = pool.TryCopyFrame(input.Metadata, input.Provenance, new byte[] { 1, 2 }).Lease!;
        var second = pool.TryCopyFrame(input.Metadata, input.Provenance, new byte[] { 3, 4 }).Lease!;
        Assert.True(handoff.TryPublish(first));
        Assert.True(handoff.TryPublish(second));

        handoff.Dispose();
        handoff.Dispose();
        Assert.True(first.IsReturned);
        Assert.True(second.IsReturned);
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);

        var afterShutdown = pool.TryCopyFrame(input.Metadata, input.Provenance, new byte[] { 5, 6 }).Lease!;
        Assert.False(handoff.TryPublish(afterShutdown));
        Assert.True(afterShutdown.IsReturned);
        Assert.Equal(1, handoff.RejectedCount);
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
        await Assert.ThrowsAsync<ChannelClosedException>(() => handoff.ReadAsync().AsTask());
    }

    private static FrameInput Input(ExecutionKind kind, int width, int height, int stride)
    {
        var correlation = new ExecutionCorrelationId(kind, Guid.NewGuid());
        var configuration = new EffectiveCameraConfiguration(
            ProductionAcquisitionMode.HardwareTrigger, 500, 1.5,
            new RegionOfInterest(0, 0, width, height), VisionPixelFormat.Mono8, null,
            500, 0, null);
        var metadata = new FrameMetadata(correlation, "TopCamera", width, height, stride,
            VisionPixelFormat.Mono8, null, Utc(10), configuration);
        var provenance = new FrameProvenance(correlation, "vendor-a", "1", "adapter-a", "1",
            "sdk-a", "1", null, "device-1", null, null, "Mono8", "normalized-v1",
            false, false, null, null, Milestones());
        return new FrameInput(metadata, provenance);
    }

    private static FrameAcquisitionMilestones Milestones() =>
        new(1_000_000, new FrameTimePoint(Utc(1), 10),
            new FrameTimePoint(Utc(2), 12), new FrameTimePoint(Utc(3), 14),
            new FrameTimePoint(Utc(4), 16));

    private static DateTimeOffset Utc(int second) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(second);

    private sealed record FrameInput(FrameMetadata Metadata, FrameProvenance Provenance);
}
