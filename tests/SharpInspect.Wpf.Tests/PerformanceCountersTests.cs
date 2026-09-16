using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

/// <summary>
/// Deterministic checks for <see cref="WpfPerformanceCounters"/> and its presentation hooks.
/// Every test binds an isolated counters instance; the process-wide <c>Shared</c> singleton is
/// never asserted on, so unrelated presentation work cannot make these tests flaky.
/// Success counts and bytes describe completed work only; failed attempts keep their own
/// counters while their elapsed ticks stay merged into the same totals.
/// </summary>
public sealed class PerformanceCountersTests
{
    [Fact]
    public void V157_U01_ReadPerformanceIsPureAndReportsTheRealMonotonicFrequency()
    {
        var counters = new WpfPerformanceCounters();

        var first = counters.ReadPerformance();

        // 计数已初始化；尚未发生任何工作是有效零，而不是“未知/未配置”。
        Assert.Equal(0, first.SnapshotsReceived);
        Assert.Equal(0, first.SnapshotsApplied);
        Assert.Equal(0, first.SnapshotsCoalesced);
        Assert.Equal(0, first.SnapshotApplyFailures);
        Assert.Equal(0, first.SnapshotApplyTicks);
        Assert.Equal(0, first.ImageCopies);
        Assert.Equal(0, first.ImageCopyBytes);
        Assert.Equal(0, first.ImageCopyFailures);
        Assert.Equal(0, first.ImageCopyTicks);
        Assert.Equal(0, first.RenderCount);
        Assert.Equal(0, first.RenderFailures);
        Assert.Equal(0, first.RenderTicks);
        Assert.Equal(Stopwatch.Frequency, first.MonotonicFrequency);
        Assert.True(first.MonotonicFrequency > 0);

        // 读取本身不改变任何计数。
        Assert.Equal(first, counters.ReadPerformance());
    }

    [Fact]
    public async Task V157_U02_ReadingCountersNeverInvokesTheDispatcherAndDeduplicationIsConsistent()
    {
        var epoch = Guid.NewGuid();
        var runtime = new TestRuntime { FullSnapshot = Snapshot(epoch, 1, ready: false) };
        var dispatcher = new CountingDispatcher();
        var counters = new WpfPerformanceCounters();
        await using var viewModel = new StationShellViewModel(runtime, dispatcher, new FrozenClock(),
            null, 32, null, counters);

        await viewModel.StartAsync();

        var applied = counters.ReadPerformance();
        Assert.Equal(1, applied.SnapshotsReceived);
        Assert.Equal(1, applied.SnapshotsApplied);
        Assert.Equal(0, applied.SnapshotsCoalesced);
        Assert.True(applied.SnapshotApplyTicks >= 0);

        // 相同代次、相同修订的重复快照被明确合并丢弃：不再进入 dispatcher，也不计入应用。
        await viewModel.ApplySnapshotAsync(Snapshot(epoch, 1, ready: true));

        var deduplicated = counters.ReadPerformance();
        Assert.Equal(2, deduplicated.SnapshotsReceived);
        Assert.Equal(1, deduplicated.SnapshotsApplied);
        Assert.Equal(1, deduplicated.SnapshotsCoalesced);

        var dispatcherCalls = dispatcher.InvocationCount;
        Assert.Equal(1, dispatcherCalls);
        for (var index = 0; index < 8; index++)
        {
            var read = counters.ReadPerformance();
            Assert.Equal(deduplicated, read);
        }

        // 读取性能快照不会调用 dispatcher，也不会渲染或改变计数。
        Assert.Equal(dispatcherCalls, dispatcher.InvocationCount);
        Assert.Equal(SnapshotFreshness.Fresh, viewModel.Freshness);
    }

    [Fact]
    public async Task V157_U03_DropOldestEvictionAndDuplicateRevisionsAreCountedExactly()
    {
        var epoch = Guid.NewGuid();
        var runtime = new TestRuntime { FullSnapshot = Snapshot(epoch, 1, ready: false) };
        var dispatcher = new PausableDispatcher();
        var counters = new WpfPerformanceCounters();
        await using var viewModel = new StationShellViewModel(runtime, dispatcher, new FrozenClock(),
            null, 1, null, counters);
        await viewModel.StartAsync();

        // 队列容量为 1：先让 rev2 离开队列并阻塞在暂停的 dispatcher 上。
        dispatcher.Pause = true;
        runtime.Publish(Snapshot(epoch, 2, ready: true));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await dispatcher.Queued.Task.WaitAsync(timeout.Token);

        // rev3 入队后 rev4 写入：DropOldest 丢弃的必须是 rev3。
        runtime.Publish(Snapshot(epoch, 3, ready: true));
        runtime.Publish(Snapshot(epoch, 4, ready: true));
        await EventuallyAsync(() => counters.ReadPerformance().SnapshotsCoalesced == 1);

        dispatcher.Resume();
        // 同时等待计数落账与状态可见，避免在 apply 通知完成前读取计数。
        await EventuallyAsync(() => counters.ReadPerformance().SnapshotsApplied == 3 &&
            viewModel.State.Revision == 4);

        var dropped = counters.ReadPerformance();
        Assert.Equal(4, dropped.SnapshotsReceived);
        Assert.Equal(3, dropped.SnapshotsApplied);
        Assert.Equal(1, dropped.SnapshotsCoalesced);
        Assert.Equal(0, dropped.SnapshotsReceived - dropped.SnapshotsApplied - dropped.SnapshotsCoalesced);

        // 同一个修订再次到达：按明确的重复修订合并丢弃计数。
        runtime.Publish(Snapshot(epoch, 4, ready: true));
        await EventuallyAsync(() => counters.ReadPerformance().SnapshotsCoalesced == 2);

        var final = counters.ReadPerformance();
        Assert.Equal(5, final.SnapshotsReceived);
        Assert.Equal(3, final.SnapshotsApplied);
        Assert.Equal(2, final.SnapshotsCoalesced);
        Assert.Equal(0, final.SnapshotsReceived - final.SnapshotsApplied - final.SnapshotsCoalesced);
    }

    [Fact]
    public void V157_U04_CopyCountersCountOnlySuccessfulCopiesWithExactDisplayBytes()
    {
        var counters = new WpfPerformanceCounters();
        var metadata = Frame(VisionPixelFormat.Mono8, null, stride: 4, width: 2, height: 2);
        var frame = new TestVisionFrame(metadata,
            new byte[checked((int)metadata.FullBufferLayoutLength)]);

        RunSta(() =>
        {
            FramePreviewImage.CopyFromFrame(frame, counters);
            PreviewDisplayImage.CopyFromFrame(new CameraPreviewFrame(Guid.NewGuid(), 1,
                new FrameTimePoint(DateTimeOffset.UnixEpoch, 1), 1, 1, 4,
                VisionPixelFormat.Bgr24, null, new byte[] { 4, 5, 6, 0xEE }), counters);
            return 0;
        });

        var copied = counters.ReadPerformance();
        Assert.Equal(2, copied.ImageCopies);
        Assert.Equal(7, copied.ImageCopyBytes); // 2x2 Gray8 显示缓冲 4 字节 + 1x1 Bgr24 显示缓冲 3 字节
        Assert.True(copied.ImageCopyTicks >= 0);
        Assert.Equal(0, copied.ImageCopyFailures);

        // 失败工作不进入成功次数与成功字节，但保留自己的失败次数，elapsed 并入合计。
        RunSta(() =>
        {
            var inactive = new TestVisionFrame(metadata,
                new byte[checked((int)metadata.FullBufferLayoutLength)], loanActive: false);
            Assert.Throws<InvalidOperationException>(() =>
                FramePreviewImage.CopyFromFrame(inactive, counters));
            return 0;
        });

        var failed = counters.ReadPerformance();
        Assert.Equal(copied.ImageCopies, failed.ImageCopies);       // 成功次数不变
        Assert.Equal(copied.ImageCopyBytes, failed.ImageCopyBytes); // 成功字节不变
        Assert.Equal(1, failed.ImageCopyFailures);
        Assert.True(failed.ImageCopyTicks >= copied.ImageCopyTicks); // 失败尝试的 elapsed 并入合计
    }

    [Fact]
    public void V157_U05_OverlayRenderCountsOnlyCompletedDrawing()
    {
        var counters = new WpfPerformanceCounters();
        var snapshot = OverlaySnapshot(new OverlayPrimitive[]
        {
            new OverlayMarker(new OverlayPoint(1, 1), OverlayMarkerKind.Cross, 1,
                new OverlayStyle(new OverlayColor(255, 255, 255), strokeWidth: 1))
        });

        RunSta(() =>
        {
            var presenter = new FrameOverlayPresenter
            {
                Snapshot = snapshot,
                PerformanceCounters = counters
            };
            presenter.RenderPreview();
            presenter.RenderPreview();
            return 0;
        });

        var rendered = counters.ReadPerformance();
        Assert.Equal(2, rendered.RenderCount);
        Assert.True(rendered.RenderTicks >= 0);
        Assert.Equal(0, rendered.RenderFailures);

        // 无法渲染时抛出的失败路径不进入成功次数，但保留自己的失败次数与 elapsed。
        RunSta(() =>
        {
            var unavailable = new FrameOverlayPresenter { PerformanceCounters = counters };
            Assert.Throws<InvalidOperationException>(() => unavailable.RenderPreview());
            return 0;
        });

        var failed = counters.ReadPerformance();
        Assert.Equal(rendered.RenderCount, failed.RenderCount); // 成功次数不变
        Assert.Equal(1, failed.RenderFailures);
        Assert.True(failed.RenderTicks >= rendered.RenderTicks); // 失败尝试的 elapsed 并入合计
    }

    [Fact]
    public async Task V157_U06_DispatcherFailureBeforeActionAddsNeitherFailureNorElapsedTicks()
    {
        var counters = new WpfPerformanceCounters();
        await using var viewModel = new StationShellViewModel(new TestRuntime(),
            new ThrowingDispatcher(), new FrozenClock(), null, 32, null, counters);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewModel.ApplySnapshotAsync(Snapshot(Guid.NewGuid(), 1, ready: true)));

        // dispatcher 在动作之前失败：apply 的测量区间从未进入，失败次数与 elapsed 都是 0。
        var snapshot = counters.ReadPerformance();
        Assert.Equal(1, snapshot.SnapshotsReceived);
        Assert.Equal(0, snapshot.SnapshotsApplied);
        Assert.Equal(0, snapshot.SnapshotApplyFailures);
        Assert.Equal(0, snapshot.SnapshotApplyTicks);
        Assert.Equal(0, snapshot.SnapshotsCoalesced);
    }

    [Fact]
    public void V157_U07_PartiallyCopiedMono16RowsFailWithoutSuccessCopiesOrBytes()
    {
        var counters = new WpfPerformanceCounters();

        RunSta(() =>
        {
            // 两个复制入口都先完成第一行、再在第二行的非法 10 位高位样本上失败。
            var metadata = Frame(VisionPixelFormat.Mono16, validBits: 10, stride: 4, width: 2, height: 2);
            var frameBuffer = Buffer(metadata);
            WriteSample(frameBuffer, metadata, row: 0, column: 0, value: 0x0001);
            WriteSample(frameBuffer, metadata, row: 1, column: 1, value: 0x0400); // 1024 > 10 位上限 1023
            Assert.Throws<InvalidOperationException>(() =>
                FramePreviewImage.CopyFromFrame(new TestVisionFrame(metadata, frameBuffer), counters));

            var previewBuffer = Buffer(metadata);
            WriteSample(previewBuffer, metadata, row: 0, column: 0, value: 0x0001);
            WriteSample(previewBuffer, metadata, row: 1, column: 1, value: 0x0400);
            Assert.Throws<InvalidOperationException>(() =>
                PreviewDisplayImage.CopyFromFrame(new CameraPreviewFrame(Guid.NewGuid(), 1,
                    new FrameTimePoint(DateTimeOffset.UnixEpoch, 1), 2, 2, 4,
                    VisionPixelFormat.Mono16, 10, previewBuffer), counters));
            return 0;
        });

        // 部分复制不产生成功次数与成功字节，但失败次数与已发生的 elapsed 必须落账。
        var failed = counters.ReadPerformance();
        Assert.Equal(0, failed.ImageCopies);
        Assert.Equal(0, failed.ImageCopyBytes);
        Assert.Equal(2, failed.ImageCopyFailures);
        Assert.True(failed.ImageCopyTicks > 0, "部分复制已完成可测量的工作，失败 elapsed 必须计入。");
    }

    [Fact]
    public async Task V157_U08_ApplyNotificationFailureAfterVisibleWorkCountsFailureAndElapsed()
    {
        var epoch = Guid.NewGuid();
        var runtime = new TestRuntime { FullSnapshot = Snapshot(epoch, 1, ready: true) };
        var counters = new WpfPerformanceCounters();
        await using var viewModel = new StationShellViewModel(runtime, new CountingDispatcher(),
            new FrozenClock(), null, 32, null, counters);
        await viewModel.StartAsync();
        var baseline = counters.ReadPerformance();

        // 处理器在状态更新之后抛出：apply 的可观察工作已经完成，但通知失败。
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(StationShellViewModel.CurrentSnapshot))
                throw new InvalidOperationException("ProbePropertyChangedFailure");
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewModel.ApplySnapshotAsync(Snapshot(epoch, 2, ready: true)));
        Assert.Equal("ProbePropertyChangedFailure", error.Message);

        // RawSnapshot 已更新到 rev2，证明失败位于可见工作之后，而不是守卫丢弃。
        Assert.Equal(2, viewModel.State.Revision);

        var failed = counters.ReadPerformance();
        Assert.Equal(baseline.SnapshotsReceived + 1, failed.SnapshotsReceived);
        Assert.Equal(baseline.SnapshotsApplied, failed.SnapshotsApplied); // 失败不计成功
        Assert.Equal(baseline.SnapshotApplyFailures + 1, failed.SnapshotApplyFailures);
        Assert.True(failed.SnapshotApplyTicks > baseline.SnapshotApplyTicks,
            "失败 apply 的 elapsed 必须并入合计。");
    }

    [Fact]
    public async Task V157_U09_FeedDropOldestClassifiesMarkerAndSnapshotDropsExactly()
    {
        var epoch = Guid.NewGuid();
        var runtime = new TestRuntime { FullSnapshot = Snapshot(epoch, 1, ready: true) };
        var dispatcher = new PausableDispatcher();
        var counters = new WpfPerformanceCounters();
        await using var viewModel = new StationShellViewModel(runtime, dispatcher, new FrozenClock(),
            null, 1, null, counters);
        await viewModel.StartAsync();

        // 暂停 dispatcher：渲染循环阻塞在 rev2，容量 1 的展示队列之后只由 pump 写入。
        dispatcher.Pause = true;
        runtime.Publish(Snapshot(epoch, 2, ready: true));
        await EventuallyAsync(() => dispatcher.PendingCount == 1);

        // 第一个断连标记先占据唯一队列位，rev3 写入把它挤出；标记丢弃不计 coalesced。
        runtime.DisconnectFeed();
        await EventuallyAsync(() => viewModel.Freshness == SnapshotFreshness.Discontinuous);
        runtime.ReopenFeed();
        runtime.Publish(Snapshot(epoch, 3, ready: true));
        await EventuallyAsync(() => counters.ReadPerformance().SnapshotsReceived == 3);

        // 恢复渲染并刷新到 rev9；dispatcher 归零、计数落账后再读，避免读到回调执行中的中间值。
        runtime.FullSnapshot = Snapshot(epoch, 9, ready: true);
        dispatcher.Resume();
        await EventuallyAsync(() => viewModel.State.Revision == 9 &&
            counters.ReadPerformance().SnapshotsApplied == 2);
        Assert.Equal(0, counters.ReadPerformance().SnapshotsCoalesced); // 被挤出的只有标记

        // 第二个断连标记到达时队列里是 rev11：被挤出的是快照，此时渲染仍阻塞在 rev10。
        dispatcher.Pause = true;
        runtime.Publish(Snapshot(epoch, 10, ready: true));
        await EventuallyAsync(() => dispatcher.PendingCount == 1);
        runtime.Publish(Snapshot(epoch, 11, ready: true));
        await EventuallyAsync(() => counters.ReadPerformance().SnapshotsReceived == 6);
        runtime.DisconnectFeed();
        runtime.ReopenFeed();

        // 保持 dispatcher 暂停时读取：渲染尚未消费任何标记，标记丢弃不会改变成功计数。
        await EventuallyAsync(() => counters.ReadPerformance().SnapshotsCoalesced == 1);
        var mixed = counters.ReadPerformance();
        Assert.Equal(6, mixed.SnapshotsReceived);  // 初始全量 + rev2/rev3 + 刷新9 + rev10/rev11
        Assert.Equal(2, mixed.SnapshotsApplied);   // 初始 + 刷新9；断连令排队的 rev2 失效，rev10 仍阻塞
        Assert.Equal(1, mixed.SnapshotsCoalesced); // 两次丢弃中只有快照那次计入
        Assert.Equal(0, mixed.SnapshotApplyFailures);

        // 释放前恢复 dispatcher，避免渲染任务永久阻塞在暂停的动作上；
        // 同时给出刷新目标，使标记触发的重连刷新走正常应用路径。
        runtime.FullSnapshot = Snapshot(epoch, 12, ready: true);
        dispatcher.Resume();
        await EventuallyAsync(() => counters.ReadPerformance().SnapshotsApplied >= 3);
    }

    private sealed class FrozenClock : IMonotonicClock
    {
        public long GetTimestamp() => 0;
        public TimeSpan ElapsedSince(long timestamp) => TimeSpan.Zero;
    }

    private sealed class CountingDispatcher : IUiDispatcher
    {
        private int _invocations;

        public int InvocationCount => Volatile.Read(ref _invocations);
        public bool CheckAccess => true;

        public ValueTask InvokeAsync(Action action)
        {
            Interlocked.Increment(ref _invocations);
            action();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingDispatcher : IUiDispatcher
    {
        public bool CheckAccess => true;
        public ValueTask InvokeAsync(Action action) =>
            throw new InvalidOperationException("ProbeDispatcherFailure");
    }

    private sealed class PausableDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<(Action Action, TaskCompletionSource<bool> Completion)> _actions = new();
        public TaskCompletionSource<bool> Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Pause { get; set; }
        public bool CheckAccess => true;
        public int PendingCount => _actions.Count;

        public ValueTask InvokeAsync(Action action)
        {
            if (!Pause)
            {
                action();
                return ValueTask.CompletedTask;
            }
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _actions.Enqueue((action, completion));
            Queued.TrySetResult(true);
            return new ValueTask(completion.Task);
        }

        public void Resume()
        {
            Pause = false;
            while (_actions.TryDequeue(out var item))
            {
                try { item.Action(); item.Completion.TrySetResult(true); }
                catch (Exception error) { item.Completion.TrySetException(error); }
            }
        }
    }

    private sealed class TestRuntime : IStationRuntime
    {
        private readonly object _feedSync = new();
        private Channel<StationStateSnapshot> _snapshots = Channel.CreateUnbounded<StationStateSnapshot>();

        public StationStateSnapshot FullSnapshot { get; set; } = Snapshot(Guid.NewGuid(), 1, ready: false);

        public ValueTask<StationStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(FullSnapshot);
        }

        public async IAsyncEnumerable<StationStateSnapshot> WatchSnapshotsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Channel<StationStateSnapshot> snapshots;
            lock (_feedSync) snapshots = _snapshots;
            await foreach (var snapshot in snapshots.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return snapshot;
        }

        public ValueTask<RuntimeCommandOutcome> SubmitAsync(RuntimeCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new RuntimeCommandOutcome(command.CorrelationId,
                CommandDisposition.Rejected, "Unconfigured"));
        }

        public void Publish(StationStateSnapshot snapshot)
        {
            lock (_feedSync) _snapshots.Writer.TryWrite(snapshot);
        }

        /// <summary>Ends the current watch stream so the pump queues one disconnection marker.</summary>
        public void DisconnectFeed()
        {
            lock (_feedSync) _snapshots.Writer.TryComplete();
        }

        /// <summary>Replaces the completed stream so a later publish becomes observable again.</summary>
        public void ReopenFeed()
        {
            lock (_feedSync) _snapshots = Channel.CreateUnbounded<StationStateSnapshot>();
        }
    }

    private sealed class TestVisionFrame : VisionFrame
    {
        private readonly byte[] _buffer;
        private readonly bool _loanActive;

        public TestVisionFrame(FrameMetadata metadata, byte[] buffer, bool loanActive = true)
            : base(metadata)
        {
            _buffer = buffer;
            _loanActive = loanActive;
        }

        public override bool IsLoanActive => _loanActive;

        public override ReadOnlySpan<byte> GetRowSpan(int row) =>
            _buffer.AsSpan(checked(row * StrideBytes), Metadata.ValidRowBytes);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static StationStateSnapshot Snapshot(Guid epoch, long revision, bool ready) =>
        new(epoch, revision, DateTimeOffset.UtcNow, RuntimeLifecycle.Running, ExclusiveMode.None,
            ready ? ProductionArmState.Armed : ProductionArmState.Disarmed, ready, false,
            HandshakePhase.Idle, RecoveryState.None, new RecipeReference("r1", "1", "hash"), null,
            new CameraHealth(HealthState.Healthy, HealthState.Healthy, HealthState.Healthy, HealthState.Healthy),
            new PlcHealth(HealthState.Healthy, HealthState.Healthy, HealthState.Healthy),
            new SubsystemHealth(HealthState.Healthy, "Ready"), new EvidenceHealth(HealthState.Healthy, 0, 0),
            new QualificationState(QualificationMatch.Matches, QualificationMatch.Matches,
                QualificationMatch.Matches, QualificationMatch.Matches),
            new PerformanceHealth(HealthState.Healthy, false), new AlarmSummary(0, 0, false),
            new InteractiveSession(InteractiveSessionState.Authenticated, "operator", Guid.NewGuid()),
            null, new AdmissionBlockers(Array.Empty<string>()));

    private static FrameOverlaySnapshot OverlaySnapshot(IEnumerable<OverlayPrimitive> primitives)
    {
        var contract = new OverlayContract("Test.Overlay", "1", maximumElements: 64,
            maximumTotalPoints: 256, maximumPointsPerElement: 64, maximumTextLength: 65_536);
        var schema = new AlgorithmResultSchema("Test.Result", "1",
            Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>(), contract);
        return new FrameOverlaySnapshot(Guid.NewGuid(),
            Frame(VisionPixelFormat.Mono8, null, stride: 4, width: 4, height: 2), schema,
            new OutputOverlaySet(contract, primitives), new string('A', 64));
    }

    private static FrameMetadata Frame(VisionPixelFormat pixelFormat, int? validBits, int stride,
        int width = 4, int height = 2)
    {
        var bytesPerPixel = pixelFormat switch
        {
            VisionPixelFormat.Mono8 => 1,
            VisionPixelFormat.Mono16 => 2,
            VisionPixelFormat.Bgr24 => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat))
        };
        var camera = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            1000, 0, new RegionOfInterest(0, 0, width, height), pixelFormat, validBits, 1000, 0,
            pixelFormat == VisionPixelFormat.Bgr24 ? new WhiteBalanceRgb(1, 1, 1) : null);
        return new FrameMetadata(new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid()),
            "Primary", width, height, Math.Max(stride, width * bytesPerPixel), pixelFormat, validBits,
            DateTimeOffset.UtcNow, camera);
    }

    private static byte[] Buffer(FrameMetadata metadata) =>
        new byte[checked((int)metadata.FullBufferLayoutLength)];

    private static void WriteSample(byte[] buffer, FrameMetadata metadata, int row, int column,
        ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(checked(row * metadata.StrideBytes + column * sizeof(ushort)), sizeof(ushort)),
            value);

    private static T RunSta<T>(Func<T> callback)
    {
        T? value = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { value = callback(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));
        if (thread.IsAlive) throw new TimeoutException("STA callback did not complete");
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
        return value!;
    }
}
