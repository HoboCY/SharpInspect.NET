using System.Collections.Concurrent;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CameraAcquisitionServiceTests
{
    [Fact]
    public async Task V118_S01_ProductionIsRejectedAndQualificationGetsOneRuntimeCorrelation()
    {
        var clock = new ManualAcquisitionClock();
        var device = new ControlledDevice(clock);
        await using var service = CreateService(device, clock);

        var production = await service.AcquireAsync(ExecutionKind.Production, "Primary");
        Assert.False(production.Accepted);
        Assert.Null(production.Correlation);
        Assert.Equal(0, device.AcquireCalls);

        device.SetHandler(static async (request, control, token, clock, device) =>
        {
            control.AcknowledgePending(request);
            var start = await control.WaitForBusyAsync(token);
            return FrameResult(device, request, clock, start.BusyAt.MonotonicTimestamp + 1);
        });

        var accepted = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
        Assert.True(accepted.Accepted);
        Assert.NotNull(accepted.Correlation);
        Assert.Equal(ExecutionKind.Qualification, accepted.Correlation!.Kind);
        Assert.Equal(accepted.Correlation, accepted.Outcome!.Correlation);
        Assert.Equal(1, device.AcquireCalls);
        accepted.Outcome.Dispose();
    }

    [Fact]
    public async Task V118_S02_BusyRejectsSecondCallAndRecordsTriggerWhileBusy()
    {
        var clock = new ManualAcquisitionClock();
        var device = new ControlledDevice(clock);
        var completion = new TaskCompletionSource<FrameAcquisitionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        device.SetHandler((request, control, token, _, _) =>
        {
            control.AcknowledgePending(request);
            return completion.Task;
        });
        await using var service = CreateService(device, clock);

        var firstTask = service.AcquireAsync(ExecutionKind.Qualification, "Primary").AsTask();
        await EventuallyAsync(() => service.Busy?.IsBusy == true);
        Assert.True(service.IsBusy);
        var second = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
        Assert.False(second.Accepted);
        Assert.Equal("CameraAcquisitionBusy", second.ReasonCode);
        Assert.Equal(1, device.AcquireCalls);

        await EventuallyAsync(() => device.LastRequest is not null);
        completion.SetResult(FrameResult(device, device.LastRequest!, clock, 1));
        await firstTask;
        Assert.Contains(service.ReadProtocolObservations(0).Observations,
            observation => observation.Kind == CameraProtocolViolationKind.TriggerWhileBusy);
    }

    [Fact]
    public async Task V118_S03_ValidFrameTransfersLeaseAndUnknownDecisionIsExplicit()
    {
        var clock = new ManualAcquisitionClock();
        var device = new ControlledDevice(clock);
        device.SetHandler(async (request, control, token, _, _) =>
        {
            control.AcknowledgePending(request);
            var start = await control.WaitForBusyAsync(token);
            return FrameResult(device, request, clock, start.BusyAt.MonotonicTimestamp + 2);
        });
        await using var service = CreateService(device, clock);

        var attempt = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
        Assert.True(attempt.Accepted);
        var outcome = attempt.Outcome;
        Assert.NotNull(outcome);
        Assert.True(outcome!.Succeeded);
        Assert.Equal(ExecutionStatus.Success, outcome.ExecutionStatus);
        Assert.Equal(InspectionDecision.Unknown, outcome.Decision);
        Assert.False(outcome.IsUnknown);
        var lease = outcome.TakeFrame();
        Assert.NotNull(lease);
        Assert.Null(outcome.Lease);
        lease!.Dispose();
    }

    [Fact]
    public async Task V118_S04_InvalidFrameIsDisposedAndRecordedWithoutPublishingLease()
    {
        var clock = new ManualAcquisitionClock();
        var device = new ControlledDevice(clock);
        device.SetHandler(async (request, control, token, _, _) =>
        {
            control.AcknowledgePending(request);
            var start = await control.WaitForBusyAsync(token);
            return FrameResult(device, request, clock, start.BusyAt.MonotonicTimestamp + 1,
                metadataCorrelation: new ExecutionCorrelationId(ExecutionKind.Qualification,
                    Guid.NewGuid()));
        });
        await using var service = CreateService(device, clock);

        var attempt = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
        Assert.True(attempt.Accepted);
        Assert.Equal(ExecutionStatus.Error, attempt.Outcome!.ExecutionStatus);
        Assert.Equal("CameraCorrelationMismatch", attempt.Outcome.ReasonCode);
        Assert.True(attempt.Outcome.IsUnknown);
        await EventuallyAsync(() => device.LastLease!.DisposeCount == 1);
        var protocol = service.ReadProtocolObservations(0);
        Assert.Contains(protocol.Observations, item => item.Kind == CameraProtocolViolationKind.CorrelationMismatch);
    }

    [Fact]
    public async Task V118_S05_PendingTimeoutIsHostBoundedAndKeepsPhysicalCleanupPending()
    {
        var clock = new ManualAcquisitionClock();
        var device = new ControlledDevice(clock);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        device.SetHandler((request, control, token, _, _) =>
        {
            entered.Set();
            release.Wait();
            control.AcknowledgePending(request);
            return Task.FromResult(FrameAcquisitionResult.FailureResult(
                new CameraAcquisitionFailure(CameraAcquisitionFailureKind.Cancelled,
                    "FixtureCancelled")));
        });
        await using var service = new CameraAcquisitionService(device, Config(100), clock,
            new CameraAcquisitionOptions(TimeSpan.FromMilliseconds(40), TimeSpan.FromSeconds(1)));

        var started = service.AcquireAsync(ExecutionKind.Qualification, "Primary").AsTask();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        var attempt = await started.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(attempt.Accepted);
        Assert.Equal(ExecutionStatus.Timeout, attempt.Outcome!.ExecutionStatus);
        Assert.True(service.Busy!.CleanupPending);
        Assert.False(service.IsBusy);
        var rejected = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
        Assert.False(rejected.Accepted);
        Assert.Equal("CameraAcquisitionBusy", rejected.ReasonCode);

        release.Set();
        await EventuallyAsync(() => service.Busy is null);
    }

    [Fact]
    public async Task V118_S06_DeadlineTimeoutDisposesLateLeaseAndAllowsNextAttemptAfterQuiescence()
    {
        var clock = new ManualAcquisitionClock();
        var device = new ControlledDevice(clock);
        var firstCompletion = new TaskCompletionSource<FrameAcquisitionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        device.SetHandler((request, control, token, _, _) =>
        {
            control.AcknowledgePending(request);
            return firstCompletion.Task;
        });
        await using var service = new CameraAcquisitionService(device, Config(10), clock,
            new CameraAcquisitionOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));

        var firstTask = service.AcquireAsync(ExecutionKind.Qualification, "Primary").AsTask();
        await EventuallyAsync(() => service.Busy?.Start is not null);
        var start = service.Busy!.Start!;
        clock.AdvanceTo(start.DeadlineTimestamp);
        var timeout = await firstTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ExecutionStatus.Timeout, timeout.Outcome!.ExecutionStatus);
        Assert.Null(timeout.Outcome.Lease);
        Assert.True(service.Busy!.CleanupPending);

        var late = FrameResult(device, device.LastRequest!, clock, start.DeadlineTimestamp + 1,
            configuration: Config(10));
        var lateLease = device.LastLease!;
        firstCompletion.SetResult(late);
        await EventuallyAsync(() => service.Busy is null && lateLease.DisposeCount == 1);

        device.SetHandler(async (request, control, token, _, _) =>
        {
            control.AcknowledgePending(request);
            var nextStart = await control.WaitForBusyAsync(token);
            return FrameResult(device, request, clock, nextStart.BusyAt.MonotonicTimestamp + 1,
                configuration: Config(10));
        });
        var next = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
        Assert.True(next.Accepted);
        Assert.NotEqual(timeout.Correlation, next.Correlation);
        next.Outcome!.Dispose();
    }

    [Fact]
    public async Task V118_S07_DeadlineTieReadsCompletedTaskBeforeTimeout()
    {
        var clock = new ManualAcquisitionClock();
        var device = new ControlledDevice(clock);
        var completion = new TaskCompletionSource<FrameAcquisitionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        device.SetHandler((request, control, token, _, _) =>
        {
            control.AcknowledgePending(request);
            return completion.Task;
        });
        await using var service = new CameraAcquisitionService(device, Config(20), clock,
            new CameraAcquisitionOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));

        var task = service.AcquireAsync(ExecutionKind.Qualification, "Primary").AsTask();
        await EventuallyAsync(() => service.Busy?.Start is not null);
        var start = service.Busy!.Start!;
        completion.SetResult(FrameResult(device, device.LastRequest!, clock,
            start.DeadlineTimestamp, configuration: Config(20)));
        var tieLease = device.LastLease!;
        clock.AdvanceTo(start.DeadlineTimestamp);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Outcome!.Succeeded);
        Assert.Equal(ExecutionStatus.Success, result.Outcome.ExecutionStatus);
        Assert.Equal(0, tieLease.DisposeCount);
        result.Outcome.Dispose();
        Assert.Equal(1, tieLease.DisposeCount);
    }

    [Fact]
    public async Task V118_S08_CancellationReturnsBoundedUnknownAndLateSuccessIsDisposed()
    {
        var clock = new ManualAcquisitionClock();
        var device = new ControlledDevice(clock);
        var completion = new TaskCompletionSource<FrameAcquisitionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        device.SetHandler((request, control, token, _, _) =>
        {
            control.AcknowledgePending(request);
            return completion.Task;
        });
        await using var service = new CameraAcquisitionService(device, Config(100), clock,
            new CameraAcquisitionOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        using var cancellation = new CancellationTokenSource();

        var task = service.AcquireAsync(ExecutionKind.Qualification, "Primary",
            cancellation.Token).AsTask();
        await EventuallyAsync(() => service.Busy?.Start is not null);
        cancellation.Cancel();
        var cancelled = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ExecutionStatus.Cancelled, cancelled.Outcome!.ExecutionStatus);
        Assert.True(cancelled.Outcome.IsUnknown);
        Assert.True(service.Busy!.CleanupPending);

        var late = FrameResult(device, device.LastRequest!, clock,
            service.Busy!.Start!.BusyAt.MonotonicTimestamp + 1,
            configuration: Config(100));
        var lateLease = device.LastLease!;
        completion.SetResult(late);
        await EventuallyAsync(() => service.Busy is null && lateLease.DisposeCount == 1);
    }

    [Fact]
    public async Task V118_S09_DisposeWaitsForPhysicalAcquireThenStopsAndDisposesInOrder()
    {
        var clock = new ManualAcquisitionClock();
        var device = new ControlledDevice(clock);
        var completion = new TaskCompletionSource<FrameAcquisitionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        device.SetHandler((request, control, token, _, _) =>
        {
            control.AcknowledgePending(request);
            return completion.Task;
        });
        await using var service = new CameraAcquisitionService(device, Config(100), clock,
            new CameraAcquisitionOptions(TimeSpan.FromMilliseconds(25),
                TimeSpan.FromMilliseconds(50)));
        var task = service.AcquireAsync(ExecutionKind.Qualification, "Primary").AsTask();
        await EventuallyAsync(() => service.Busy?.Start is not null);

        await service.DisposeAsync();
        Assert.Empty(device.Events);
        Assert.True(service.IsDisposed);

        completion.SetResult(FrameAcquisitionResult.FailureResult(
            new CameraAcquisitionFailure(CameraAcquisitionFailureKind.Cancelled,
                "FixtureCancelled")));
        await task.WaitAsync(TimeSpan.FromSeconds(2));
        await EventuallyAsync(() => device.DisposeCount == 1);
        Assert.Equal(new[] { "stop", "dispose" }, device.Events.ToArray());
    }

    [Fact]
    public async Task V118_S10_ProtocolRefreshIsBoundedAndCursorOnlyAdvancesOnReturnedFacts()
    {
        var clock = new ManualAcquisitionClock();
        var device = new ControlledDevice(clock);
        device.SetProtocol(new CameraProtocolSnapshot(device.ProtocolEpoch, 1, 2, false,
            new[] { Protocol(1, CameraProtocolViolationKind.InvalidFrame),
                Protocol(2, CameraProtocolViolationKind.LateFrame) }));
        await using var service = new CameraAcquisitionService(device, Config(), clock,
            new CameraAcquisitionOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 2));

        var refreshed = await service.RefreshProtocolObservationsAsync();
        Assert.Equal(2, refreshed.ThroughSequence);
        Assert.Equal(2, refreshed.Observations.Count);
        Assert.Equal(CameraProtocolViolationKind.InvalidFrame, refreshed.Observations[0].Kind);
        Assert.Equal(CameraProtocolViolationKind.LateFrame, refreshed.Observations[1].Kind);
        var again = await service.RefreshProtocolObservationsAsync();
        Assert.Equal(2, again.ThroughSequence);
        Assert.Equal(2, device.ProtocolReadCalls);

        device.SetProtocol(new CameraProtocolSnapshot(device.ProtocolEpoch, 3, 3, true,
            new[] { Protocol(3, CameraProtocolViolationKind.EarlyFrame) }));
        await service.RefreshProtocolObservationsAsync();
        Assert.True(service.ProtocolFaulted);
        var rejected = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
        Assert.False(rejected.Accepted);
        Assert.Equal("CameraProtocolUnavailable", rejected.ReasonCode);
    }

    [Fact]
    public async Task V118_S11_LateLeaseCleanupBlocksNextAdmissionUntilOwnerReturns()
    {
        var clock = new ManualAcquisitionClock();
        var device = new ControlledDevice(clock);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        device.SetHandler(async (request, control, token, _, _) =>
        {
            control.AcknowledgePending(request);
            var start = await control.WaitForBusyAsync(token);
            return FrameResult(device, request, clock, start.BusyAt.MonotonicTimestamp + 1,
                metadataCorrelation: new ExecutionCorrelationId(ExecutionKind.Qualification,
                    Guid.NewGuid()), disposeAction: () =>
                    {
                        entered.Set();
                        release.Wait();
                    });
        });
        await using var service = new CameraAcquisitionService(device, Config(), clock,
            new CameraAcquisitionOptions(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(50)));

        try
        {
            var failed = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
            Assert.True(failed.Accepted);
            Assert.Equal(ExecutionStatus.Error, failed.Outcome!.ExecutionStatus);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(service.Busy!.CleanupPending);
            var rejected = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
            Assert.False(rejected.Accepted);
            Assert.Equal("CameraAcquisitionBusy", rejected.ReasonCode);
            Assert.Equal(0, device.StopCount);
        }
        finally
        {
            release.Set();
        }

        await EventuallyAsync(() => service.Busy is null);
    }

    [Fact]
    public async Task V118_S12_FailedLeaseReturnRetainsOwnerAndShutdownDoesNotAdvanceToStop()
    {
        var clock = new ManualAcquisitionClock();
        var device = new ControlledDevice(clock);
        device.SetHandler(async (request, control, token, _, _) =>
        {
            control.AcknowledgePending(request);
            var start = await control.WaitForBusyAsync(token);
            return FrameResult(device, request, clock, start.BusyAt.MonotonicTimestamp + 1,
                metadataCorrelation: new(ExecutionKind.Qualification, Guid.NewGuid()),
                disposeAction: () => throw new InvalidOperationException("FixtureLeaseReturnFailed"));
        });
        var service = new CameraAcquisitionService(device, Config(), clock,
            new CameraAcquisitionOptions(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(50)));
        var result = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
        Assert.Equal(ExecutionStatus.Error, result.Outcome!.ExecutionStatus);
        await EventuallyAsync(() => device.LastLease!.DisposeCount == 1);
        Assert.True(service.Busy!.CleanupPending);
        var rejected = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
        Assert.False(rejected.Accepted);
        Assert.Null(rejected.Correlation);
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(service.Busy!.CleanupPending);
        Assert.Equal(1, device.AcquireCalls);
        Assert.Equal(0, device.StopCount);
        Assert.Equal(0, device.DisposeCount);
    }

    [Fact]
    public async Task V118_S13_ProvenanceCorrelationMismatchIsClassifiedSeparately()
    {
        var clock = new ManualAcquisitionClock();
        var device = new ControlledDevice(clock);
        device.SetHandler(async (request, control, token, _, _) =>
        {
            control.AcknowledgePending(request);
            var start = await control.WaitForBusyAsync(token);
            return FrameResult(device, request, clock, start.BusyAt.MonotonicTimestamp + 1,
                provenanceCorrelation: new ExecutionCorrelationId(ExecutionKind.Qualification,
                    Guid.NewGuid()));
        });
        await using var service = CreateService(device, clock);

        var result = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
        Assert.Equal("CameraCorrelationMismatch", result.Outcome!.ReasonCode);
        Assert.Null(result.Outcome.Lease);
        await EventuallyAsync(() => device.LastLease!.DisposeCount == 1);
        Assert.Contains(service.ReadProtocolObservations(0).Observations,
            item => item.Kind == CameraProtocolViolationKind.CorrelationMismatch);
    }

    [Fact]
    public async Task V118_S14_FrameRoleMismatchIsClassifiedAsCorrelationMismatch()
    {
        var clock = new ManualAcquisitionClock();
        var device = new ControlledDevice(clock);
        device.SetHandler(async (request, control, token, _, _) =>
        {
            control.AcknowledgePending(request);
            var start = await control.WaitForBusyAsync(token);
            return FrameResult(device, request, clock, start.BusyAt.MonotonicTimestamp + 1,
                metadataRole: "Secondary");
        });
        await using var service = CreateService(device, clock);

        var result = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
        Assert.Equal("CameraCorrelationMismatch", result.Outcome!.ReasonCode);
        Assert.Null(result.Outcome.Lease);
        await EventuallyAsync(() => device.LastLease!.DisposeCount == 1);
        Assert.Contains(service.ReadProtocolObservations(0).Observations,
            item => item.Kind == CameraProtocolViolationKind.CorrelationMismatch);
    }

    [Fact]
    public async Task V118_S15_AdapterObservationGapFactLatchesProtocolFault()
    {
        var clock = new ManualAcquisitionClock();
        var device = new ControlledDevice(clock);
        device.SetProtocol(new CameraProtocolSnapshot(device.ProtocolEpoch, 1, 2, false,
            new[] { Protocol(1, CameraProtocolViolationKind.EarlyFrame),
                Protocol(2, CameraProtocolViolationKind.ObservationGap) }));
        await using var service = CreateService(device, clock);

        await service.RefreshProtocolObservationsAsync();

        Assert.True(service.ProtocolFaulted);
        Assert.Contains(service.ReadProtocolObservations(0).Observations,
            item => item.Kind == CameraProtocolViolationKind.ObservationGap);
        var rejected = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
        Assert.False(rejected.Accepted);
        Assert.Equal("CameraProtocolUnavailable", rejected.ReasonCode);
        Assert.Equal(0, device.AcquireCalls);
    }

    [Fact]
    public async Task V118_S16_SameEpochProtocolThroughRollbackLatchesWithoutAdvancingCursor()
    {
        var clock = new ManualAcquisitionClock();
        var device = new ControlledDevice(clock);
        device.SetProtocol(new CameraProtocolSnapshot(device.ProtocolEpoch, 1, 2, false,
            new[] { Protocol(1, CameraProtocolViolationKind.EarlyFrame),
                Protocol(2, CameraProtocolViolationKind.LateFrame) }));
        await using var service = CreateService(device, clock);

        var initial = await service.RefreshProtocolObservationsAsync();
        Assert.Equal(2, initial.ThroughSequence);
        device.SetProtocol(new CameraProtocolSnapshot(device.ProtocolEpoch, 1, 0, false,
            Array.Empty<CameraProtocolObservation>()));
        var rollback = await service.RefreshProtocolObservationsAsync();

        Assert.True(service.ProtocolFaulted);
        Assert.Contains(rollback.Observations,
            item => item.ReasonCode == "CameraProtocolSequenceRollback");
        var rejected = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
        Assert.False(rejected.Accepted);
        Assert.Equal("CameraProtocolUnavailable", rejected.ReasonCode);
        Assert.Equal(0, device.AcquireCalls);
    }

    private static CameraAcquisitionService CreateService(ControlledDevice device,
        ManualAcquisitionClock clock) => new(device, Config(), clock,
        new CameraAcquisitionOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));

    private static EffectiveCameraConfiguration Config(int timeoutMs = 100) =>
        new(ProductionAcquisitionMode.SoftwareTrigger, 10, 0,
            new RegionOfInterest(0, 0, 1, 1), VisionPixelFormat.Mono8, null,
            timeoutMs, 0, null);

    private static FrameAcquisitionResult FrameResult(ControlledDevice device,
        FrameAcquisitionRequest request, ManualAcquisitionClock clock, long ready,
        EffectiveCameraConfiguration? configuration = null,
        ExecutionCorrelationId? metadataCorrelation = null,
        Action? disposeAction = null,
        ExecutionCorrelationId? provenanceCorrelation = null,
        string? metadataRole = null)
    {
        configuration ??= Config();
        metadataRole ??= request.LogicalCameraRole;
        var metadata = new FrameMetadata(request.Correlation, request.LogicalCameraRole,
            1, 1, 1, VisionPixelFormat.Mono8, null,
            clock.GetTimePoint().HostObservedAtUtc, configuration);
        if (metadataCorrelation is not null)
        {
            metadata = new FrameMetadata(metadataCorrelation, metadataRole,
                1, 1, 1, VisionPixelFormat.Mono8, null,
                clock.GetTimePoint().HostObservedAtUtc, configuration);
        }
        else if (metadataRole != request.LogicalCameraRole)
        {
            metadata = new FrameMetadata(request.Correlation, metadataRole,
                1, 1, 1, VisionPixelFormat.Mono8, null,
                clock.GetTimePoint().HostObservedAtUtc, configuration);
        }
        var point = new FrameTimePoint(clock.GetTimePoint().HostObservedAtUtc, ready);
        var milestones = new FrameAcquisitionMilestones(clock.Frequency, point, point, point, point);
        var provenance = new FrameProvenance(provenanceCorrelation ?? request.Correlation,
            device.Descriptor.Provider.Id,
            device.Descriptor.Provider.Version, "FixtureAdapter", "1", "FixtureSdk", "1", null,
            device.Descriptor.StableDeviceIdentity, null, null, "Mono8", "passthrough-v1",
            false, false, null, null, milestones);
        var lease = new TestLease(new TestFrame(metadata), provenance, disposeAction);
        device.LastLease = lease;
        return FrameAcquisitionResult.Success(lease);
    }

    private static CameraProtocolObservation Protocol(long sequence,
        CameraProtocolViolationKind kind) => new(sequence, kind, "FixtureProtocolFault",
        new(DateTimeOffset.UtcNow, sequence));

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("FixtureEventuallyTimeout");
            await Task.Delay(5);
        }
    }

    private sealed class ControlledDevice : IControlledCameraDevice
    {
        private readonly ManualAcquisitionClock _clock;
        private Func<FrameAcquisitionRequest, FrameAcquisitionControl, CancellationToken,
            ManualAcquisitionClock, ControlledDevice, Task<FrameAcquisitionResult>> _handler =
            static (_, _, _, _, _) => Task.FromException<FrameAcquisitionResult>(
                new InvalidOperationException("FixtureAcquireHandlerMissing"));
        private CameraProtocolSnapshot? _protocol;

        internal ControlledDevice(ManualAcquisitionClock clock) => _clock = clock;

        internal int AcquireCalls { get; private set; }
        internal int StopCount { get; private set; }
        internal int DisposeCount { get; private set; }
        internal FrameAcquisitionRequest? LastRequest { get; private set; }
        internal TestLease? LastLease { get; set; }
        internal Guid ProtocolEpoch { get; } = Guid.NewGuid();
        internal int ProtocolReadCalls { get; private set; }
        internal ConcurrentQueue<string> Events { get; } = new();

        internal void SetHandler(Func<FrameAcquisitionRequest, FrameAcquisitionControl,
            CancellationToken, ManualAcquisitionClock, ControlledDevice,
            Task<FrameAcquisitionResult>> handler) => _handler = handler;

        internal void SetProtocol(CameraProtocolSnapshot protocol) => _protocol = protocol;

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
            AcquireCalls++;
            LastRequest = request;
            return new ValueTask<FrameAcquisitionResult>(_handler(request, control,
                cancellationToken, _clock, this));
        }

        public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
            RequestedCameraConfiguration requested, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraConfigurationResult.Failure("FixtureNotUsed"));

        public ValueTask<CameraOperationResult> StartAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraOperationResult.Success("FixtureStarted"));

        public ValueTask<CameraOperationResult> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            Events.Enqueue("stop");
            return ValueTask.FromResult(CameraOperationResult.Success("FixtureStopped"));
        }

        public CameraProtocolSnapshot ReadProtocolObservations(long afterSequence,
            int maximumCount = 64)
        {
            ProtocolReadCalls++;
            var protocol = _protocol ?? new CameraProtocolSnapshot(ProtocolEpoch,
                1, 0, false, Array.Empty<CameraProtocolObservation>());
            return new CameraProtocolSnapshot(protocol.Epoch, protocol.FirstAvailableSequence,
                protocol.ThroughSequence, protocol.Overflowed,
                protocol.Observations.Where(item => item.Sequence > afterSequence)
                    .Take(maximumCount));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Events.Enqueue("dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestLease : IFrameBufferLease
    {
        private int _disposed;

        internal TestLease(VisionFrame frame, FrameProvenance provenance,
            Action? disposeAction = null)
        {
            Frame = frame;
            Provenance = provenance;
            LeaseId = Guid.NewGuid();
            _disposeAction = disposeAction;
        }

        private readonly Action? _disposeAction;
        internal int DisposeCount { get; private set; }
        public Guid LeaseId { get; }
        public VisionFrame Frame { get; }
        public FrameProvenance Provenance { get; }
        public bool IsReturned => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                DisposeCount++;
                _disposeAction?.Invoke();
            }
        }
    }

    private sealed class TestFrame : VisionFrame
    {
        internal TestFrame(FrameMetadata metadata) : base(metadata) { }
        public override bool IsLoanActive => true;
        public override ReadOnlySpan<byte> GetRowSpan(int row) => new byte[] { 42 };
    }

    private sealed class ManualAcquisitionClock : IFrameAcquisitionClock
    {
        private readonly object _sync = new();
        private readonly List<Scheduled> _scheduled = new();
        private long _now;
        private long _sequence;

        public long Frequency => 1_000;
        public FrameTimePoint GetTimePoint() => new(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(_now), _now);

        public IDisposable Schedule(long dueTimestamp, FrameAcquisitionClockPhase phase,
            Action callback)
        {
            lock (_sync)
            {
                if (dueTimestamp < _now) throw new ArgumentOutOfRangeException(nameof(dueTimestamp));
                var scheduled = new Scheduled(dueTimestamp, phase, ++_sequence, callback);
                _scheduled.Add(scheduled);
                return new Handle(this, scheduled);
            }
        }

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

        private sealed class Scheduled
        {
            internal Scheduled(long due, FrameAcquisitionClockPhase phase, long sequence,
                Action callback)
            {
                Due = due; Phase = phase; Sequence = sequence; Callback = callback;
            }
            internal long Due { get; }
            internal FrameAcquisitionClockPhase Phase { get; }
            internal long Sequence { get; }
            internal Action Callback { get; }
            internal bool Cancelled { get; set; }
        }

        private sealed class Handle : IDisposable
        {
            private readonly ManualAcquisitionClock _owner;
            private Scheduled? _scheduled;
            internal Handle(ManualAcquisitionClock owner, Scheduled scheduled)
            { _owner = owner; _scheduled = scheduled; }
            public void Dispose()
            {
                var scheduled = Interlocked.Exchange(ref _scheduled, null);
                if (scheduled is null) return;
                lock (_owner._sync) scheduled.Cancelled = true;
            }
        }
    }
}
