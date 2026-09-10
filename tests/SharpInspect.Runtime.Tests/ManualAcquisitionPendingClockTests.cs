using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ManualAcquisitionPendingClockTests
{
    [Fact]
    public async Task V135_R05_ManualBusyUsesInstalledPendingTimeAfterClockAdvances()
    {
        var clock = new PendingClock();
        var device = new PendingDevice(clock);
        await using var service = CreateService(device, clock, timeoutMs: 20);
        service.EnableManualAcquisition();

        var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
        var acquisitionTask = service.AcquireManualAsync(correlation, "Primary").AsTask();
        try
        {
            var installed = await device.InstalledAt.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(installed, device.ControlInstalledAt);

            // Keep the device invocation from returning so the Runtime cannot open
            // Busy yet.  This is the race window exercised by the real clock pump.
            clock.AdvanceBy(1);
            device.AllowInvocationReturn();

            await EventuallyAsync(() => service.Busy?.Start is not null);
            var start = service.Busy!.Start!;
            Assert.Equal(installed.MonotonicTimestamp, start.BusyAt.MonotonicTimestamp);
            Assert.Equal(installed.MonotonicTimestamp + 20, start.DeadlineTimestamp);
            Assert.Equal(installed.HostObservedAtUtc, start.BusyAt.HostObservedAtUtc);

            device.CompleteSuccess(start);
            var attempt = await acquisitionTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(attempt.Accepted);
            Assert.NotNull(attempt.Outcome);
            Assert.True(attempt.Outcome!.Succeeded);
            Assert.Equal(start.BusyAt, device.BusyAt);
            attempt.Outcome.Dispose();
        }
        finally
        {
            device.AllowInvocationReturn();
            device.CompleteFailure();
        }
    }

    [Fact]
    public async Task V135_R06_ManualDeadlineRemainsBoundToInstalledPendingTime()
    {
        var clock = new PendingClock();
        var device = new PendingDevice(clock);
        await using var service = CreateService(device, clock, timeoutMs: 10);
        service.EnableManualAcquisition();

        var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
        var acquisitionTask = service.AcquireManualAsync(correlation, "Primary").AsTask();
        try
        {
            var installed = await device.InstalledAt.Task.WaitAsync(TimeSpan.FromSeconds(2));
            clock.AdvanceBy(1);
            device.AllowInvocationReturn();

            await EventuallyAsync(() => service.Busy?.Start is not null);
            var start = service.Busy!.Start!;
            Assert.Equal(installed.MonotonicTimestamp, start.BusyAt.MonotonicTimestamp);
            Assert.Equal(installed.MonotonicTimestamp + 10, start.DeadlineTimestamp);

            clock.AdvanceTo(start.DeadlineTimestamp + 1);
            var attempt = await acquisitionTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(attempt.Accepted);
            Assert.NotNull(attempt.Outcome);
            Assert.False(attempt.Outcome!.Succeeded);
            Assert.Equal(ExecutionStatus.Timeout, attempt.Outcome.ExecutionStatus);
            Assert.Equal("CameraAcquisitionTimeout", attempt.Outcome.ReasonCode);
        }
        finally
        {
            device.AllowInvocationReturn();
            device.CompleteFailure();
            await EventuallyAsync(() => service.Busy is null);
        }
    }

    [Fact]
    public void V135_R07_FuturePendingTimeIsRejectedAndDuplicateAckPreservesInstalledTime()
    {
        var clock = new PendingClock();
        var request = new FrameAcquisitionRequest(
            new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid()), "Primary");
        var control = new FrameAcquisitionControl(request, clock);
        var baseline = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var future = new FrameTimePoint(baseline.AddMinutes(1), 1);
        Assert.False(control.AcknowledgePending(request, future));
        Assert.Null(control.PendingInstalledAt);

        // The monotonic point is current/history even though the UTC observation
        // has a different offset; UTC is evidence, not ordering authority.
        var installed = new FrameTimePoint(baseline.AddMinutes(1), 0);
        Assert.True(control.AcknowledgePending(request, installed));

        var replacement = new FrameTimePoint(baseline, 0);
        Assert.False(control.AcknowledgePending(request, replacement));
        Assert.Equal(installed, control.PendingInstalledAt);
    }

    [Fact]
    public async Task V135_R08_PendingPastDeadlineCannotExtendTheBusyWindow()
    {
        var clock = new PendingClock();
        var device = new PendingDevice(clock);
        await using var service = CreateService(device, clock, timeoutMs: 10);
        service.EnableManualAcquisition();

        var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
        var acquisitionTask = service.AcquireManualAsync(correlation, "Primary").AsTask();
        try
        {
            var installed = await device.InstalledAt.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, installed.MonotonicTimestamp);

            // Busy has not been opened because the device invocation is still
            // held.  Passing the stored deadline before releasing it must remain
            // fail-closed; it must not move the deadline to current+10.
            clock.AdvanceTo(11);
            device.AllowInvocationReturn();

            var attempt = await acquisitionTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(attempt.Accepted);
            Assert.NotNull(attempt.Outcome);
            Assert.False(attempt.Outcome!.Succeeded);
            Assert.Equal(ExecutionStatus.Error, attempt.Outcome.ExecutionStatus);
            Assert.Equal("CameraAcquisitionDeadlineScheduleFailed", attempt.Outcome.ReasonCode);
            Assert.NotNull(attempt.Outcome.Start);
            Assert.Equal(installed.MonotonicTimestamp,
                attempt.Outcome.Start!.BusyAt.MonotonicTimestamp);
            Assert.Equal(installed.MonotonicTimestamp + 10,
                attempt.Outcome.Start.DeadlineTimestamp);
        }
        finally
        {
            device.AllowInvocationReturn();
            device.CompleteFailure();
            await EventuallyAsync(() => service.Busy is null);
        }
    }

    private static CameraAcquisitionService CreateService(PendingDevice device,
        PendingClock clock, int timeoutMs)
    {
        var configuration = new EffectiveCameraConfiguration(
            ProductionAcquisitionMode.SoftwareTrigger, 10, 0,
            new RegionOfInterest(0, 0, 1, 1), VisionPixelFormat.Mono8, null,
            timeoutMs, 0, null);
        device.Configuration = configuration;
        return new CameraAcquisitionService(device, configuration, clock,
            new CameraAcquisitionOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("PendingClockFixtureEventuallyTimeout");
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    private sealed class PendingDevice : IControlledCameraDevice
    {
        private readonly PendingClock _clock;
        private readonly ManualResetEventSlim _allowInvocationReturn = new(false);
        private readonly TaskCompletionSource<FrameAcquisitionResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private FrameAcquisitionRequest? _request;

        internal PendingDevice(PendingClock clock) => _clock = clock;

        internal TaskCompletionSource<FrameTimePoint> InstalledAt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal FrameTimePoint? ControlInstalledAt { get; private set; }
        internal FrameTimePoint? BusyAt { get; private set; }
        internal EffectiveCameraConfiguration Configuration { get; set; } = null!;
        private Guid ProtocolEpoch { get; } = Guid.NewGuid();

        public CameraDeviceDescriptor Descriptor { get; } = new(
            new CameraProviderIdentity("PendingClockProvider", "1", "PendingClockAdapter", "1"),
            "PendingClockDevice", "Pending clock test device");

        public CameraCapabilities Capabilities { get; } = new(
            new[] { ProductionAcquisitionMode.SoftwareTrigger },
            new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
            new CameraDoubleCapability(1, 100, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(-10, 10, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0, 10, 1, CameraQuantizationMode.Exact),
            new CameraRoiCapabilities(1, 1, new(0, 0, 1), new(0, 0, 1),
                new(1, 1, 1), new(1, 1, 1)));

        public CameraHealthSnapshot GetHealthSnapshot() => new(
            CameraProviderAvailability.Available, CameraConnectionState.Open,
            CameraConfigurationState.Applied, CameraAcquisitionState.Armed,
            _clock.GetTimePoint());

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            FrameAcquisitionControl control, CancellationToken cancellationToken = default)
        {
            _request = request;
            var installed = _clock.GetTimePoint();
            if (!control.AcknowledgePending(request, installed))
                return ValueTask.FromResult(FrameAcquisitionResult.FailureResult(
                    new CameraAcquisitionFailure(CameraAcquisitionFailureKind.ProtocolViolation,
                        "PendingClockControlRejected")));

            ControlInstalledAt = control.PendingInstalledAt;
            InstalledAt.TrySetResult(installed);
            _allowInvocationReturn.Wait();
            return new ValueTask<FrameAcquisitionResult>(_completion.Task);
        }

        public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
            RequestedCameraConfiguration requested, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraConfigurationResult.Failure("PendingClockConfigurationUnused"));

        public ValueTask<CameraOperationResult> StartAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraOperationResult.Success("PendingClockStarted"));

        public ValueTask<CameraOperationResult> StopAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraOperationResult.Success("PendingClockStopped"));

        public CameraProtocolSnapshot ReadProtocolObservations(long afterSequence,
            int maximumCount = 64) => new(ProtocolEpoch, 1, 0, false,
            Array.Empty<CameraProtocolObservation>());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        internal void AllowInvocationReturn() => _allowInvocationReturn.Set();

        internal void CompleteSuccess(FrameAcquisitionStart start)
        {
            BusyAt = start.BusyAt;
            var request = _request ?? throw new InvalidOperationException("PendingClockRequestMissing");
            var ready = new FrameTimePoint(start.BusyAt.HostObservedAtUtc,
                start.BusyAt.MonotonicTimestamp + 1);
            var metadata = new FrameMetadata(request.Correlation, request.LogicalCameraRole,
                1, 1, 1, VisionPixelFormat.Mono8, null,
                ready.HostObservedAtUtc, Configuration);
            var milestones = new FrameAcquisitionMilestones(_clock.Frequency,
                start.BusyAt, start.BusyAt, ready, ready);
            var provenance = new FrameProvenance(request.Correlation,
                Descriptor.Provider.Id, Descriptor.Provider.Version,
                Descriptor.Provider.AdapterPackageId, Descriptor.Provider.AdapterVersion,
                "PendingClockSdk", "1", null, Descriptor.StableDeviceIdentity,
                null, null, "Mono8", "passthrough-v1", false, false, null, null,
                milestones);
            _completion.TrySetResult(FrameAcquisitionResult.Success(
                new PendingLease(new PendingFrame(metadata), provenance)));
        }

        internal void CompleteFailure() => _completion.TrySetResult(
            FrameAcquisitionResult.FailureResult(new CameraAcquisitionFailure(
                CameraAcquisitionFailureKind.Cancelled, "PendingClockTestCleanup")));
    }

    private sealed class PendingLease : IFrameBufferLease
    {
        private int _returned;

        internal PendingLease(VisionFrame frame, FrameProvenance provenance)
        {
            Frame = frame;
            Provenance = provenance;
            LeaseId = Guid.NewGuid();
        }

        public Guid LeaseId { get; }
        public VisionFrame Frame { get; }
        public FrameProvenance Provenance { get; }
        public bool IsReturned => Volatile.Read(ref _returned) != 0;
        public void Dispose() => Interlocked.Exchange(ref _returned, 1);
    }

    private sealed class PendingFrame : VisionFrame
    {
        internal PendingFrame(FrameMetadata metadata) : base(metadata) { }
        public override bool IsLoanActive => true;
        public override ReadOnlySpan<byte> GetRowSpan(int row) => new byte[] { 42 };
    }

    private sealed class PendingClock : IFrameAcquisitionClock
    {
        private readonly object _sync = new();
        private readonly List<Scheduled> _scheduled = new();
        private long _now;
        private long _sequence;

        public long Frequency => 1_000;

        public FrameTimePoint GetTimePoint()
        {
            lock (_sync) return Point(_now);
        }

        public IDisposable Schedule(long dueTimestamp, FrameAcquisitionClockPhase phase,
            Action callback)
        {
            lock (_sync)
            {
                if (dueTimestamp < _now)
                    throw new ArgumentOutOfRangeException(nameof(dueTimestamp));
                var scheduled = new Scheduled(dueTimestamp, phase, ++_sequence, callback);
                _scheduled.Add(scheduled);
                return new ScheduleHandle(this, scheduled);
            }
        }

        internal void AdvanceBy(long amount) => AdvanceTo(Current + amount);

        internal void AdvanceTo(long timestamp)
        {
            if (timestamp < 0) throw new ArgumentOutOfRangeException(nameof(timestamp));
            while (true)
            {
                Scheduled? next;
                lock (_sync)
                {
                    next = _scheduled.Where(item => !item.Cancelled && item.Due <= timestamp)
                        .OrderBy(item => item.Due).ThenBy(item => item.Phase)
                        .ThenBy(item => item.Sequence).FirstOrDefault();
                    if (next is null)
                    {
                        _now = timestamp;
                        return;
                    }

                    _scheduled.Remove(next);
                    _now = next.Due;
                }

                next.Callback();
            }
        }

        private long Current
        {
            get { lock (_sync) return _now; }
        }

        private static FrameTimePoint Point(long timestamp) => new(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                .AddMilliseconds(timestamp), timestamp);

        private sealed class Scheduled
        {
            internal Scheduled(long due, FrameAcquisitionClockPhase phase, long sequence,
                Action callback)
            {
                Due = due;
                Phase = phase;
                Sequence = sequence;
                Callback = callback;
            }

            internal long Due { get; }
            internal FrameAcquisitionClockPhase Phase { get; }
            internal long Sequence { get; }
            internal Action Callback { get; }
            internal bool Cancelled { get; set; }
        }

        private sealed class ScheduleHandle : IDisposable
        {
            private readonly PendingClock _owner;
            private readonly Scheduled _scheduled;
            private int _disposed;

            internal ScheduleHandle(PendingClock owner, Scheduled scheduled)
            {
                _owner = owner;
                _scheduled = scheduled;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                lock (_owner._sync) _scheduled.Cancelled = true;
            }
        }
    }
}
