using System.Collections.Concurrent;
using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Frames;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CameraAcquisitionRetirementTests
{
    [Fact]
    public async Task V119_T01_HealthProbeIsBoundedAndItsLateTaskBlocksRetirement()
    {
        var clock = new TestClock();
        var device = new RetirementDevice(clock);
        using var healthEntered = new ManualResetEventSlim();
        using var healthRelease = new ManualResetEventSlim();
        device.HealthHandler = () =>
        {
            healthEntered.Set();
            healthRelease.Wait();
            return Healthy(clock);
        };
        await using var service = CreateService(device, clock,
            protocolReadTimeout: TimeSpan.FromMilliseconds(25));

        try
        {
            var health = service.ReadHealthAsync();
            Assert.True(healthEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.Null(await health.WaitAsync(TimeSpan.FromSeconds(2)));

            var retirement = service.BeginRetirement();
            await Task.Delay(40);
            Assert.False(retirement.IsCompleted);
            Assert.Equal(0, device.StopCalls);

            healthRelease.Set();
            var result = await retirement.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(result.SafeToReplace);
            Assert.Equal("CameraDeviceRetired", result.ReasonCode);
            Assert.Equal(new[] { "stop", "dispose" }, device.Events.ToArray());
            Assert.Equal(1, device.HealthCalls);
        }
        finally
        {
            healthRelease.Set();
        }
    }

    [Fact]
    public async Task V119_T02_ProbeAfterRetirementLatchIsIgnoredWithoutCallingTheDevice()
    {
        var clock = new TestClock();
        var device = new RetirementDevice(clock);
        await using var service = CreateService(device, clock);

        var retirement = service.BeginRetirement();
        var health = await service.ReadHealthAsync();

        Assert.Null(health);
        Assert.Equal(0, device.HealthCalls);
        Assert.True((await retirement.WaitAsync(TimeSpan.FromSeconds(2))).SafeToReplace);
    }

    [Fact]
    public async Task V119_T03_HealthProbeAndAcquisitionAreAdmissionSerialized()
    {
        var clock = new TestClock();
        var device = new RetirementDevice(clock);
        using var healthEntered = new ManualResetEventSlim();
        using var healthRelease = new ManualResetEventSlim();
        device.HealthHandler = () =>
        {
            healthEntered.Set();
            healthRelease.Wait();
            return Healthy(clock);
        };
        await using var service = CreateService(device, clock,
            protocolReadTimeout: TimeSpan.FromSeconds(2));

        try
        {
            var healthTask = service.ReadHealthAsync();
            Assert.True(healthEntered.Wait(TimeSpan.FromSeconds(2)));
            var rejected = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
            Assert.False(rejected.Accepted);
            Assert.Equal("CameraAcquisitionBusy", rejected.ReasonCode);
            Assert.Equal(0, device.AcquireCalls);

            healthRelease.Set();
            Assert.NotNull(await healthTask.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            healthRelease.Set();
        }
    }

    [Fact]
    public async Task V119_T03b_LateHealthResultIsNotReusedAsNextProbe()
    {
        var clock = new TestClock();
        var device = new RetirementDevice(clock);
        using var firstEntered = new ManualResetEventSlim();
        using var firstRelease = new ManualResetEventSlim();
        using var firstCompleted = new ManualResetEventSlim();
        device.HealthHandler = () =>
        {
            if (device.HealthCalls == 1)
            {
                firstEntered.Set();
                firstRelease.Wait();
            }
            firstCompleted.Set();
            return Healthy(clock);
        };
        await using var service = CreateService(device, clock,
            protocolReadTimeout: TimeSpan.FromMilliseconds(25));

        var first = service.ReadHealthAsync();
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(2)));

        firstRelease.Set();
        Assert.True(firstCompleted.Wait(TimeSpan.FromSeconds(2)));
        await Task.Delay(50);
        Assert.NotNull(await service.ReadHealthAsync());
        Assert.Equal(2, device.HealthCalls);
    }

    [Fact]
    public async Task V119_T04_DisposeFaultReportsUnsafeReplacementAfterBoundedPublicDispose()
    {
        var clock = new TestClock();
        var device = new RetirementDevice(clock)
        {
            DisposeHandler = static () => ValueTask.FromException(
                new InvalidOperationException("FixtureDisposeFault"))
        };
        await using var service = CreateService(device, clock);

        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var result = await service.BeginRetirement().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.SafeToReplace);
        Assert.Equal("CameraDeviceDisposeFailed", result.ReasonCode);
        Assert.Equal(1, device.StopCalls);
        Assert.Equal(1, device.DisposeCalls);
    }

    [Fact]
    public async Task V119_T05_LateStopAndDisposeCompleteInOrderWithOneRetirementTask()
    {
        var clock = new TestClock();
        var device = new RetirementDevice(clock);
        var stopRelease = new TaskCompletionSource<CameraOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeRelease = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        device.StopHandler = () => new ValueTask<CameraOperationResult>(stopRelease.Task);
        device.DisposeHandler = () => new ValueTask(disposeRelease.Task);
        await using var service = CreateService(device, clock);

        var first = service.BeginRetirement();
        var second = service.BeginRetirement();
        Assert.Same(first, second);
        await EventuallyAsync(() => device.StopCalls == 1);
        Assert.False(first.IsCompleted);

        stopRelease.SetResult(CameraOperationResult.Success("FixtureStopped"));
        await EventuallyAsync(() => device.DisposeCalls == 1);
        Assert.False(first.IsCompleted);
        disposeRelease.SetResult(true);

        var result = await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.SafeToReplace);
        Assert.Equal("CameraDeviceRetired", result.ReasonCode);
        Assert.Equal(new[] { "stop", "dispose" }, device.Events.ToArray());
    }

    [Fact]
    public async Task V119_T06_UserHeldLeasePreventsStopUntilUnderlyingLeaseReturns()
    {
        var clock = new TestClock();
        var device = new RetirementDevice(clock);
        var lease = new RetirementLease();
        lease.SetReturned(false);
        device.AcquireHandler = (request, control, token) =>
            AcquireLeaseAsync(request, control, token, lease, device, clock);
        await using var service = CreateService(device, clock);

        var acquired = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
        var held = acquired.Outcome!.TakeFrame();
        Assert.NotNull(held);

        var retirement = service.BeginRetirement();
        await Task.Delay(40);
        Assert.False(retirement.IsCompleted);
        Assert.Equal(0, device.StopCalls);

        held!.Dispose();
        Assert.True(lease.DisposeCalls == 1);
        Assert.False(retirement.IsCompleted);
        lease.SetReturned(true);

        var result = await retirement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.SafeToReplace);
        Assert.Equal(1, device.StopCalls);
        Assert.Equal(1, device.DisposeCalls);

        // A duplicate caller Dispose cannot reopen the ownership gate or invoke the
        // underlying lease a second time.
        held.Dispose();
        Assert.Equal(1, lease.DisposeCalls);
    }

    [Fact]
    public async Task V119_T07_FailedLeaseDisposeKeepsOwnerAndPreventsStop()
    {
        var clock = new TestClock();
        var device = new RetirementDevice(clock);
        var lease = new RetirementLease()
        {
            DisposeHandler = static () => throw new InvalidOperationException("FixtureLeaseDisposeFault")
        };
        device.AcquireHandler = (request, control, token) =>
            AcquireLeaseAsync(request, control, token, lease, device, clock);
        await using var service = CreateService(device, clock);

        var acquired = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
        var transferred = acquired.Outcome!.TakeFrame();
        Assert.NotNull(transferred);
        Assert.Throws<InvalidOperationException>(() => transferred!.Dispose());
        transferred.Dispose();

        var result = await service.BeginRetirement().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.SafeToReplace);
        Assert.Equal("CameraLeaseReleaseFailed", result.ReasonCode);
        Assert.Equal(1, lease.DisposeCalls);
        Assert.Equal(0, device.StopCalls);
        Assert.Equal(0, device.DisposeCalls);
    }

    [Fact]
    public async Task V119_T08_ReadLeaseReturnIsObservedBeforeRetirement()
    {
        var clock = new TestClock();
        var device = new RetirementDevice(clock);
        var lease = new RetirementLease();
        lease.SetReturned(false);
        device.AcquireHandler = (request, control, token) =>
            AcquireLeaseAsync(request, control, token, lease, device, clock);
        await using var service = CreateService(device, clock);

        var acquired = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
        var transferred = acquired.Outcome!.TakeFrame()!;
        var retirement = service.BeginRetirement();

        transferred.Dispose();
        await Task.Delay(40);
        Assert.False(retirement.IsCompleted);
        Assert.Equal(0, device.StopCalls);

        lease.SetReturned(true);
        var result = await retirement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.SafeToReplace);
        Assert.Equal(1, device.StopCalls);
        Assert.Equal(1, device.DisposeCalls);
    }

    [Fact]
    public async Task V119_T09_FrameBufferReadLeaseBlocksRetirementAfterOuterDispose()
    {
        var clock = new TestClock();
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8,
            TimeSpan.FromSeconds(1)));
        var device = new RetirementDevice(clock);
        device.AcquireHandler = async (request, control, token) =>
        {
            control.AcknowledgePending(request);
            await control.WaitForBusyAsync(token);
            var point = clock.GetTimePoint();
            var configuration = Config();
            var metadata = new FrameMetadata(request.Correlation, request.LogicalCameraRole,
                1, 1, 1, VisionPixelFormat.Mono8, null, point.HostObservedAtUtc,
                configuration);
            var milestones = new FrameAcquisitionMilestones(clock.Frequency, point, point,
                point, point);
            var descriptor = device.Descriptor;
            var provenance = new FrameProvenance(request.Correlation,
                descriptor.Provider.Id, descriptor.Provider.Version,
                descriptor.Provider.AdapterPackageId, descriptor.Provider.AdapterVersion,
                "FixtureSdk", "1", null, descriptor.StableDeviceIdentity, null, null,
                "Mono8", "passthrough-v1", false, false, null, null, milestones);
            var copied = pool.TryCopyFrame(metadata, provenance, new byte[] { 1 });
            Assert.True(copied.Succeeded);
            return FrameAcquisitionResult.Success(copied.Lease!);
        };
        await using var service = CreateService(device, clock);

        var acquired = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
        var held = acquired.Outcome!.TakeFrame()!;
        var readMethod = typeof(VisionFrame).GetMethod("AcquireNativeRead",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var reader = (IDisposable)readMethod.Invoke(held.Frame, null)!;

        held.Dispose();
        Assert.False(held.IsReturned);
        Assert.Equal(1, pool.GetSnapshot().ActiveReaders);

        var retirement = service.BeginRetirement();
        await Task.Delay(40);
        Assert.False(retirement.IsCompleted);
        Assert.Equal(0, device.StopCalls);
        Assert.Equal(1, pool.GetSnapshot().OutstandingLeases);

        reader.Dispose();
        var result = await retirement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.SafeToReplace);
        Assert.True(held.IsReturned);
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
        Assert.Equal(1, device.StopCalls);
        Assert.Equal(1, device.DisposeCalls);
    }

    [Fact]
    public async Task V119_T10_CancelledLatePoolLeaseWaitsForNativeReaderBeforeRetirement()
    {
        var clock = new TestClock();
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));
        var device = new RetirementDevice(clock);
        var frameReady = new TaskCompletionSource<IFrameBufferLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.AcquireHandler = async (request, control, token) =>
        {
            control.AcknowledgePending(request);
            await control.WaitForBusyAsync(token);
            var point = clock.GetTimePoint();
            var metadata = new FrameMetadata(request.Correlation, request.LogicalCameraRole,
                1, 1, 1, VisionPixelFormat.Mono8, null, point.HostObservedAtUtc, Config());
            var milestones = new FrameAcquisitionMilestones(clock.Frequency, point, point, point, point);
            var descriptor = device.Descriptor;
            var provenance = new FrameProvenance(request.Correlation, descriptor.Provider.Id,
                descriptor.Provider.Version, descriptor.Provider.AdapterPackageId,
                descriptor.Provider.AdapterVersion, "FixtureSdk", "1", null,
                descriptor.StableDeviceIdentity, null, null, "Mono8", "passthrough-v1",
                false, false, null, null, milestones);
            var copied = pool.TryCopyFrame(metadata, provenance, new byte[] { 1 });
            Assert.True(copied.Succeeded);
            frameReady.TrySetResult(copied.Lease!);
            await releaseResult.Task;
            return FrameAcquisitionResult.Success(copied.Lease!);
        };
        await using var service = CreateService(device, clock);
        using var cancelled = new CancellationTokenSource();
        var acquisition = service.AcquireAsync(ExecutionKind.Qualification, "Primary", cancelled.Token).AsTask();
        var lease = await frameReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var readMethod = typeof(VisionFrame).GetMethod("AcquireNativeRead",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var reader = (IDisposable)readMethod.Invoke(lease.Frame, null)!;
        cancelled.Cancel();
        var outcome = await acquisition.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ExecutionStatus.Cancelled, outcome.Outcome!.ExecutionStatus);
        var retirement = service.BeginRetirement();
        releaseResult.TrySetResult(true);
        await Task.Delay(80);
        Assert.False(retirement.IsCompleted);
        Assert.Equal(0, device.StopCalls);
        Assert.Equal(1, pool.GetSnapshot().OutstandingLeases);
        reader.Dispose();
        Assert.True((await retirement.WaitAsync(TimeSpan.FromSeconds(2))).SafeToReplace);
        Assert.True(lease.IsReturned);
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
        Assert.Equal(1, device.StopCalls);
        Assert.Equal(1, device.DisposeCalls);
    }

    private static CameraAcquisitionService CreateService(RetirementDevice device,
        TestClock clock, TimeSpan? protocolReadTimeout = null) => new(device, Config(), clock,
        new CameraAcquisitionOptions(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(50),
            protocolReadTimeout: protocolReadTimeout));

    private static EffectiveCameraConfiguration Config() => new(
        ProductionAcquisitionMode.SoftwareTrigger, 10, 0,
        new RegionOfInterest(0, 0, 1, 1), VisionPixelFormat.Mono8, null, 100, 0, null);

    private static CameraHealthSnapshot Healthy(TestClock clock) => new(
        CameraProviderAvailability.Available, CameraConnectionState.Open,
        CameraConfigurationState.Applied, CameraAcquisitionState.Armed,
        clock.GetTimePoint());

    private static async Task<FrameAcquisitionResult> AcquireLeaseAsync(
        FrameAcquisitionRequest request, FrameAcquisitionControl control,
        CancellationToken cancellationToken, RetirementLease lease,
        RetirementDevice device, TestClock clock)
    {
        control.AcknowledgePending(request);
        await control.WaitForBusyAsync(cancellationToken);
        lease.Bind(device, clock, request);
        return FrameAcquisitionResult.Success(lease);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("FixtureEventuallyTimeout");
            await Task.Delay(5);
        }
    }

    private sealed class RetirementDevice : IControlledCameraDevice
    {
        private readonly Guid _protocolEpoch = Guid.NewGuid();

        internal RetirementDevice(TestClock clock) { }

        internal Func<CameraHealthSnapshot> HealthHandler { get; set; }
            = () => throw new InvalidOperationException("FixtureHealthHandlerMissing");
        internal Func<FrameAcquisitionRequest, FrameAcquisitionControl, CancellationToken,
            Task<FrameAcquisitionResult>> AcquireHandler { get; set; }
            = static (_, _, _) => Task.FromException<FrameAcquisitionResult>(
                new InvalidOperationException("FixtureAcquireHandlerMissing"));
        internal Func<ValueTask<CameraOperationResult>> StopHandler { get; set; }
            = static () => ValueTask.FromResult(CameraOperationResult.Success("FixtureStopped"));
        internal Func<ValueTask> DisposeHandler { get; set; }
            = static () => ValueTask.CompletedTask;
        internal ConcurrentQueue<string> Events { get; } = new();

        public CameraDeviceDescriptor Descriptor { get; } = new(
            new CameraProviderIdentity("FixtureProvider", "1", "FixtureAdapter", "1"),
            "FixtureDevice", "Fixture camera");
        public CameraCapabilities Capabilities { get; } = new(
            new[] { ProductionAcquisitionMode.SoftwareTrigger },
            new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
            new CameraDoubleCapability(1, 100, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(-10, 10, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0, 10, 1, CameraQuantizationMode.Exact),
            new CameraRoiCapabilities(1, 1, new(0, 0, 1), new(0, 0, 1),
                new(1, 1, 1), new(1, 1, 1)));

        public CameraHealthSnapshot GetHealthSnapshot()
        {
            Interlocked.Increment(ref _healthCalls);
            return HealthHandler();
        }

        private int _healthCalls;
        internal int HealthCalls => Volatile.Read(ref _healthCalls);

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            FrameAcquisitionControl control, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _acquireCalls);
            return new ValueTask<FrameAcquisitionResult>(AcquireHandler(request, control,
                cancellationToken));
        }

        public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
            RequestedCameraConfiguration requested, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraConfigurationResult.Failure("FixtureNotUsed"));

        public ValueTask<CameraOperationResult> StartAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraOperationResult.Success("FixtureStarted"));

        public ValueTask<CameraOperationResult> StopAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _stopCalls);
            Events.Enqueue("stop");
            return StopHandler();
        }

        public CameraProtocolSnapshot ReadProtocolObservations(long afterSequence,
            int maximumCount = 64) => new(_protocolEpoch, 1, 0, false,
            Array.Empty<CameraProtocolObservation>());

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            Events.Enqueue("dispose");
            return DisposeHandler();
        }

        private int _acquireCalls;
        private int _stopCalls;
        private int _disposeCalls;
        internal int AcquireCalls => Volatile.Read(ref _acquireCalls);
        internal int StopCalls => Volatile.Read(ref _stopCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
    }

    private sealed class RetirementLease : IFrameBufferLease
    {
        private VisionFrame _frame = null!;
        private FrameProvenance _provenance = null!;
        private Func<bool> _returned = static () => true;
        private int _disposeCalls;

        internal RetirementLease() => LeaseId = Guid.NewGuid();

        internal Action? DisposeHandler { get; set; }
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public Guid LeaseId { get; }
        public VisionFrame Frame => _frame;
        public FrameProvenance Provenance => _provenance;
        public bool IsReturned => _returned();

        internal RetirementLease Bind(RetirementDevice device, TestClock clock,
            FrameAcquisitionRequest request)
        {
            var configuration = Config();
            var metadata = new FrameMetadata(request.Correlation, request.LogicalCameraRole,
                1, 1, 1, VisionPixelFormat.Mono8, null,
                clock.GetTimePoint().HostObservedAtUtc, configuration);
            var point = clock.GetTimePoint();
            var milestones = new FrameAcquisitionMilestones(clock.Frequency, point, point,
                point, point);
            _frame = new TestFrame(metadata);
            _provenance = new FrameProvenance(request.Correlation,
                device.Descriptor.Provider.Id, device.Descriptor.Provider.Version,
                device.Descriptor.Provider.AdapterPackageId,
                device.Descriptor.Provider.AdapterVersion, "FixtureSdk", "1", null,
                device.Descriptor.StableDeviceIdentity, null, null, "Mono8", "passthrough-v1",
                false, false, null, null, milestones);
            return this;
        }

        internal void SetReturned(bool returned) => _returned = () => returned;

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCalls);
            DisposeHandler?.Invoke();
        }
    }

    private sealed class TestFrame : VisionFrame
    {
        internal TestFrame(FrameMetadata metadata) : base(metadata) { }
        public override bool IsLoanActive => true;
        public override ReadOnlySpan<byte> GetRowSpan(int row) => new byte[] { 1 };
    }

    private sealed class TestClock : IFrameAcquisitionClock
    {
        private long _timestamp;
        public long Frequency => 1_000;
        public FrameTimePoint GetTimePoint() => new(DateTimeOffset.UtcNow,
            Interlocked.Increment(ref _timestamp));
        public IDisposable Schedule(long dueTimestamp, FrameAcquisitionClockPhase phase,
            Action callback) => Noop.Instance;
        private sealed class Noop : IDisposable
        {
            internal static Noop Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
