using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Hikrobot;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Frames;
using Xunit;

namespace SharpInspect.Cameras.Hikrobot.Tests;

public sealed class HikrobotCameraDeviceTests
{
    [Fact]
    public async Task V121_C01_SoftwareMono8UsesControlledAcquireAndTransfersLease()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 42 });
        await using var service = fixture.CreateService();

        var attempt = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");

        Assert.True(attempt.Accepted);
        Assert.True(attempt.Outcome!.Succeeded);
        var lease = attempt.Outcome.TakeFrame();
        Assert.NotNull(lease);
        Assert.Equal(new byte[] { 42 }, lease!.Frame.GetRowSpan(0).ToArray());
        Assert.Equal("Hikrobot.MVS", lease.Provenance.SdkId);
        Assert.Equal(fixture.Sdk.Descriptor.StableIdentity,
            lease.Provenance.StableDeviceIdentity);
        Assert.Equal(1, fixture.Sdk.TriggerCalls);
        Assert.Equal(1, fixture.Sdk.MaximumConcurrentCalls);
        lease.Dispose();
    }

    [Fact]
    public async Task V121_C02_Mono10Mono12AndMono16BecomeRightAlignedMono16()
    {
        var cases = new[]
        {
            (10, HikrobotNativePixelFormat.Mono10, (ushort)0x155),
            (12, HikrobotNativePixelFormat.Mono12, (ushort)0xA55),
            (16, HikrobotNativePixelFormat.Mono16, (ushort)0xA55A)
        };

        foreach (var (validBits, nativeFormat, sourceValue) in cases)
        {
            var source = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(source, sourceValue);
            var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono16, nativeFormat,
                source, validBits);
            await using var service = fixture.CreateService();
            var attempt = await service.AcquireAsync(ExecutionKind.Qualification, "Mono");

            Assert.True(attempt.Outcome!.Succeeded);
            var lease = attempt.Outcome.TakeFrame();
            Assert.NotNull(lease);
            Assert.Equal(sourceValue, BinaryPrimitives.ReadUInt16LittleEndian(
                lease!.Frame.GetRowSpan(0)));
            Assert.Equal(validBits, lease.Frame.ValidBits);
            Assert.False(lease.Provenance.NormalizationTransformed);
            lease.Dispose();
        }
    }

    [Fact]
    public async Task V121_C03_Rgb24IsReorderedToBgr24WithoutPixelHeapFallback()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Bgr24,
            HikrobotNativePixelFormat.Rgb24, new byte[] { 1, 2, 3 });
        await using var service = fixture.CreateService();

        var attempt = await service.AcquireAsync(ExecutionKind.Qualification, "Color");

        Assert.True(attempt.Outcome!.Succeeded);
        var lease = attempt.Outcome.TakeFrame();
        Assert.NotNull(lease);
        Assert.Equal(new byte[] { 3, 2, 1 }, lease!.Frame.GetRowSpan(0).ToArray());
        Assert.Equal(VisionPixelFormat.Bgr24, lease.Frame.PixelFormat);
        Assert.Equal("rgb24-to-bgr24", lease.Provenance.NormalizationDetails);
        Assert.False(lease.Provenance.NormalizationAllocated);
        lease.Dispose();
    }

    [Fact]
    public async Task V121_C04_EarlyExtraAndLateFramesAreBoundedFactsOnly()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 9 }, emitOnTriggerTwice: true);
        fixture.Sdk.EmitAfterStop();
        await using var service = fixture.CreateService();

        // The Start callback is already registered, so this is an unsolicited early
        // frame and cannot become the first request's frame.
        fixture.Sdk.Emit(HikrobotNativePixelFormat.Mono8, 1, 1, 1, new byte[] { 8 },
            10, 10);
        var attempt = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
        Assert.True(attempt.Outcome!.Succeeded);
        attempt.Outcome.Dispose();

        fixture.Sdk.EmitAfterStop();
        await EventuallyAsync(() => fixture.Device.ReadProtocolObservations(0)
            .Observations.Any(item => item.Kind == CameraProtocolViolationKind.EarlyFrame));
        var observations = fixture.Device.ReadProtocolObservations(0).Observations;
        Assert.Contains(observations, item => item.Kind == CameraProtocolViolationKind.ExtraFrame);
        Assert.Contains(observations, item => item.Kind == CameraProtocolViolationKind.LateFrame);
        Assert.All(observations, item =>
        {
            if (item.Correlation is not null)
                Assert.Equal(ExecutionKind.Qualification, item.Correlation.Kind);
        });
    }

    [Fact]
    public async Task V121_C05_ProductionAndUncontrolledPathsFailClosedWithoutSdkCalls()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 1 });
        var request = new FrameAcquisitionRequest(
            new ExecutionCorrelationId(ExecutionKind.Production, Guid.NewGuid()), "Primary");

        var ordinary = await fixture.Device.AcquireAsync(request);

        Assert.False(ordinary.Succeeded);
        Assert.Equal("HikrobotControlledAcquireRequired", ordinary.ReasonCode);
        Assert.Equal(0, fixture.Sdk.TriggerCalls);

        var startCallsBeforeControlled = fixture.Sdk.StartCalls;
        var controlledProductionRequest = new FrameAcquisitionRequest(
            new ExecutionCorrelationId(ExecutionKind.Production, Guid.NewGuid()), "Primary");
        var exactControl = CreateExactControl(controlledProductionRequest, fixture.Clock);
        var controlled = await fixture.Device.AcquireAsync(controlledProductionRequest,
            exactControl);

        Assert.False(controlled.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.ProtocolViolation,
            controlled.Failure!.Kind);
        Assert.Equal("ProductionAcquisitionUnavailable", controlled.ReasonCode);
        Assert.Equal(startCallsBeforeControlled, fixture.Sdk.StartCalls);
        Assert.Equal(0, fixture.Sdk.TriggerCalls);
        Assert.False(exactControl.IsBusy);
        await fixture.Device.DisposeAsync();
    }

    [Fact]
    public async Task V121_C06_PoolExhaustionIsBufferFaultAndNoSecondPixelBufferAppears()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 7 }, poolCapacity: 1);
        // Occupy the real bounded pool before this handle's sole acquisition.
        var pool = (FrameBufferPool)typeof(HikrobotCameraDevice).GetField("_pool",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Device)!;
        var correlation = new ExecutionCorrelationId(ExecutionKind.Qualification, Guid.NewGuid());
        var time = fixture.Clock.GetTimePoint();
        var effective = fixture.Device.Capabilities.ValidateConfiguration(fixture.Requested).Effective!;
        var metadata = new FrameMetadata(correlation, "HeldPoolSlot", 1, 1, 1,
            VisionPixelFormat.Mono8, null, time.HostObservedAtUtc, effective);
        var provenance = new FrameProvenance(correlation, "fixture", "1", "fixture", "1",
            "fixture", "1", null, "fixture", null, null, "Mono8", "fixture", false,
            false, null, null, new FrameAcquisitionMilestones(fixture.Clock.Frequency,
                null, time, time, time));
        using var held = pool.TryCopyFrame(metadata, provenance, new byte[] { 7 }).Lease;
        Assert.NotNull(held);

        await using var secondService = fixture.CreateService();
        var second = await secondService.AcquireAsync(ExecutionKind.Qualification, "Second");

        Assert.True(second.Accepted);
        Assert.False(second.Outcome!.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.BufferUnavailable,
            second.Outcome.FailureKind);
        Assert.Equal(CameraFaultClassification.BufferFault,
            fixture.Device.GetHealthSnapshot().LastFault!.Classification);
        Assert.Equal(1, pool.GetSnapshot().PeakLeases);
        Assert.Equal(1, pool.GetSnapshot().ExhaustionCount);
    }

    [Fact]
    public async Task V121_C07_ReadBackMismatchRetiresWholeDeviceAndClearsEffective()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 1 }, configure: false);
        fixture.Sdk.ApplyOverride = requested => new EffectiveCameraConfiguration(
            requested.ProductionAcquisitionMode, requested.ExposureTimeUs,
            requested.GainDb, requested.RegionOfInterest, VisionPixelFormat.Bgr24,
            null, requested.AcquisitionTimeoutMs, requested.TriggerDelayUs, null);

        var result = await fixture.Device.ApplyConfigurationAsync(fixture.Requested);

        Assert.False(result.Succeeded);
        Assert.Equal("CameraReadBackEffectiveMismatch", result.ReasonCode);
        await EventuallyAsync(() => fixture.Sdk.DisposeCalls == 1);
        var health = fixture.Device.GetHealthSnapshot();
        Assert.Equal(CameraConnectionState.Closed, health.Connection);
        Assert.Equal(CameraConfigurationState.Unknown, health.Configuration);
        Assert.Null(result.Effective);
    }

    [Fact]
    public async Task V121_C08_CancelledApplyRetainsActualSdkCallUntilItSettles()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 1 }, configure: false);
        fixture.Sdk.ApplyRelease.Reset();
        using var cancellation = new CancellationTokenSource();
        var apply = fixture.Device.ApplyConfigurationAsync(fixture.Requested,
            cancellation.Token).AsTask();
        Assert.True(fixture.Sdk.ApplyEntered.Wait(TimeSpan.FromSeconds(2)));

        cancellation.Cancel();
        var result = await apply.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.Succeeded);
        Assert.Equal("HikrobotConfigurationCancelled", result.ReasonCode);
        Assert.Equal(0, fixture.Sdk.DisposeCalls);

        fixture.Sdk.ApplyRelease.Set();
        await EventuallyAsync(() => fixture.Sdk.DisposeCalls == 1);
        Assert.Equal(1, fixture.Sdk.MaximumConcurrentCalls);
        await fixture.Device.DisposeAsync();
    }

    [Fact]
    public async Task V121_C09_FailedRetirementKeepsDeviceSlotReserved()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 1 });
        fixture.Sdk.FailStop = true;

        await fixture.Device.DisposeAsync();

        Assert.False(fixture.Device.RetirementCompleted);
        Assert.Equal(CameraConnectionState.Disconnected,
            fixture.Device.GetHealthSnapshot().Connection);
        Assert.Equal(1, fixture.Sdk.StopCalls);
        Assert.Equal(1, fixture.Sdk.DisposeCalls);
    }

    [Fact]
    public async Task V121_C10_SecondApplyIsRejectedWhileFirstSdkApplyIsBlocked()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 1 }, configure: false);
        fixture.Sdk.ApplyRelease.Reset();

        var first = fixture.Device.ApplyConfigurationAsync(fixture.Requested).AsTask();
        Assert.True(fixture.Sdk.ApplyEntered.Wait(TimeSpan.FromSeconds(2)));

        var second = await fixture.Device.ApplyConfigurationAsync(fixture.Requested);

        Assert.False(second.Succeeded);
        Assert.Equal("HikrobotCameraOperationBusy", second.ReasonCode);
        Assert.Equal(1, fixture.Sdk.ApplyCalls);
        Assert.Equal(0, fixture.Sdk.DisposeCalls);
        Assert.Equal(CameraConnectionState.Open,
            fixture.Device.GetHealthSnapshot().Connection);

        fixture.Sdk.ApplyRelease.Set();
        var completed = await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(completed.Succeeded, completed.ReasonCode);
        await fixture.Device.DisposeAsync();
    }

    [Fact]
    public async Task V121_C11_SecondStartIsRejectedWhileFirstSdkStartIsBlocked()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 1 }, configure: false);
        var applied = await fixture.Device.ApplyConfigurationAsync(fixture.Requested);
        Assert.True(applied.Succeeded, applied.ReasonCode);

        fixture.Sdk.StartRelease.Reset();
        var first = fixture.Device.StartAsync().AsTask();
        Assert.True(fixture.Sdk.StartEntered.Wait(TimeSpan.FromSeconds(2)));

        var second = await fixture.Device.StartAsync();

        Assert.False(second.Succeeded);
        Assert.Equal("HikrobotCameraOperationBusy", second.ReasonCode);
        Assert.Equal(1, fixture.Sdk.StartCalls);
        Assert.Equal(0, fixture.Sdk.DisposeCalls);
        Assert.Equal(CameraConnectionState.Open,
            fixture.Device.GetHealthSnapshot().Connection);

        fixture.Sdk.StartRelease.Set();
        var completed = await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(completed.Succeeded, completed.ReasonCode);
        await fixture.Device.DisposeAsync();
    }

    [Fact]
    public async Task V121_C12_ApplyIsRejectedWhileStartIsBlockedWithoutExtraSdkCall()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 1 }, configure: false);
        var applied = await fixture.Device.ApplyConfigurationAsync(fixture.Requested);
        Assert.True(applied.Succeeded, applied.ReasonCode);

        fixture.Sdk.StartRelease.Reset();
        var first = fixture.Device.StartAsync().AsTask();
        Assert.True(fixture.Sdk.StartEntered.Wait(TimeSpan.FromSeconds(2)));

        var competingApply = await fixture.Device.ApplyConfigurationAsync(fixture.Requested);

        Assert.False(competingApply.Succeeded);
        Assert.Equal("HikrobotCameraOperationBusy", competingApply.ReasonCode);
        Assert.Equal(1, fixture.Sdk.ApplyCalls);
        Assert.Equal(1, fixture.Sdk.StartCalls);
        Assert.Equal(0, fixture.Sdk.DisposeCalls);
        Assert.Equal(CameraConnectionState.Open,
            fixture.Device.GetHealthSnapshot().Connection);

        fixture.Sdk.StartRelease.Set();
        var completed = await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(completed.Succeeded, completed.ReasonCode);
        await fixture.Device.DisposeAsync();
    }

    [Fact]
    public async Task V121_C13_CallbackRawCopyBudgetIsTypedBufferFaultAndRetires()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[64],
            callbackBudget: TimeSpan.FromTicks(1));
        fixture.Sdk.TriggerWidth = 64;
        fixture.Sdk.TriggerStrideBytes = 64;
        await using var service = fixture.CreateService();

        var attempt = await service.AcquireAsync(ExecutionKind.Qualification, "Budget");

        Assert.True(attempt.Accepted);
        Assert.False(attempt.Outcome!.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.BufferUnavailable,
            attempt.Outcome.FailureKind);
        Assert.Equal("HikrobotCallbackBudgetExceeded", attempt.Outcome.ReasonCode);
        Assert.Equal(CameraFaultClassification.BufferFault,
            fixture.Device.GetHealthSnapshot().LastFault!.Classification);
        await EventuallyAsync(() => fixture.Device.RetirementCompleted);
        Assert.Equal(CameraConnectionState.Closed,
            fixture.Device.GetHealthSnapshot().Connection);
    }

    [Fact]
    public async Task V121_C14_Mono10HighPaddingBitsAreRejectedWithoutAlignmentGuess()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono16,
            HikrobotNativePixelFormat.Mono10, new byte[] { 0x55, 0xFC },
            validBits: 10);
        await using var service = fixture.CreateService();

        var attempt = await service.AcquireAsync(ExecutionKind.Qualification, "Padding");

        Assert.True(attempt.Accepted);
        Assert.False(attempt.Outcome!.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.ProtocolViolation,
            attempt.Outcome.FailureKind);
        Assert.Equal("HikrobotMono16HighBitsInvalid", attempt.Outcome.ReasonCode);
        Assert.Equal(CameraConnectionState.Disconnected,
            fixture.Device.GetHealthSnapshot().Connection);
        await fixture.Device.DisposeAsync();
    }

    [Fact]
    public async Task V121_C15_NativeMono16CannotSatisfyTenOrTwelveBitConfiguration()
    {
        foreach (var validBits in new[] { 10, 12 })
        {
            var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono16,
                HikrobotNativePixelFormat.Mono16, new byte[] { 0x55, 0x01 },
                validBits: validBits);
            await using var service = fixture.CreateService();

            var attempt = await service.AcquireAsync(ExecutionKind.Qualification,
                "NativeMismatch");

            Assert.True(attempt.Accepted);
            Assert.False(attempt.Outcome!.Succeeded);
            Assert.Equal(CameraAcquisitionFailureKind.ProtocolViolation,
                attempt.Outcome.FailureKind);
            Assert.Equal("HikrobotMono16ValidBitsMismatch", attempt.Outcome.ReasonCode);
            await fixture.Device.DisposeAsync();
        }
    }

    [Fact]
    public async Task V121_C16_CompletedFailedRetirementCanBeRetriedToClosed()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 1 });
        fixture.Sdk.FailDispose = true;

        await fixture.Device.DisposeAsync();

        Assert.False(fixture.Device.RetirementCompleted);
        Assert.Equal(CameraConnectionState.Disconnected,
            fixture.Device.GetHealthSnapshot().Connection);
        Assert.Equal(1, fixture.Sdk.StopCalls);
        Assert.Equal(1, fixture.Sdk.DisposeCalls);

        fixture.Sdk.FailDispose = false;
        await fixture.Device.DisposeAsync();

        Assert.True(fixture.Device.RetirementCompleted);
        Assert.Equal(CameraConnectionState.Closed,
            fixture.Device.GetHealthSnapshot().Connection);
        Assert.Equal(2, fixture.Sdk.StopCalls);
        Assert.Equal(2, fixture.Sdk.DisposeCalls);
    }

    [Fact]
    public async Task V121_A01_HardwareAcquireNeverCallsSoftwareTrigger()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 11 }, hardware: true);
        await using var service = fixture.CreateService(
            HikrobotTestData.Requested(VisionPixelFormat.Mono8, mode:
                ProductionAcquisitionMode.HardwareTrigger));

        var acquire = service.AcquireAsync(ExecutionKind.Qualification, "Hardware").AsTask();
        await EventuallyAsync(() => service.Busy?.IsBusy == true);
        fixture.Sdk.Emit(HikrobotNativePixelFormat.Mono8, 1, 1, 1, new byte[] { 11 }, 1, 1);
        var attempt = await acquire.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(attempt.Outcome!.Succeeded);
        Assert.Equal(0, fixture.Sdk.TriggerCalls);
        attempt.Outcome.Dispose();
    }

    [Fact]
    public async Task V121_A02_CancelledTriggerKeepsSdkCallOwnedUntilItSettles()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 5 });
        fixture.Sdk.TriggerRelease.Reset();
        await using var service = fixture.CreateService();
        using var cancellation = new CancellationTokenSource();
        var acquire = service.AcquireAsync(ExecutionKind.Qualification, "Primary",
            cancellation.Token).AsTask();
        Assert.True(fixture.Sdk.TriggerEntered.Wait(TimeSpan.FromSeconds(2)));

        cancellation.Cancel();
        var cancelled = await acquire.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(cancelled.Accepted);
        Assert.Equal(ExecutionStatus.Cancelled, cancelled.Outcome!.ExecutionStatus);
        Assert.True(service.Busy!.CleanupPending);
        Assert.Equal(0, fixture.Sdk.DisposeCalls);

        fixture.Sdk.TriggerRelease.Set();
        await EventuallyAsync(() => service.Busy is null);
        Assert.Equal(1, fixture.Sdk.MaximumConcurrentCalls);
    }

    [Fact]
    public async Task V121_A03_HardwareInvalidFrameFinalizesOutsideNativeCallback()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 11 }, hardware: true);
        var finalizationOnCallback = new ConcurrentQueue<bool>();
        fixture.Device.FinalizationProbe = () =>
            finalizationOnCallback.Enqueue(HikrobotTestSdk.IsInNativeCallback);
        await using var service = fixture.CreateService(
            HikrobotTestData.Requested(VisionPixelFormat.Mono8, mode:
                ProductionAcquisitionMode.HardwareTrigger));

        var acquire = service.AcquireAsync(ExecutionKind.Qualification, "HardwareInvalid")
            .AsTask();
        await EventuallyAsync(() => service.Busy?.IsBusy == true);
        // A truncated frame exercises the callback's invalid-raw branch.  The
        // callback records the result and queues the owned finalizer; it must not
        // complete the Runtime operation on the simulated vendor stack.
        fixture.Sdk.Emit(HikrobotNativePixelFormat.Mono8, 2, 1, 2,
            new byte[] { 11 }, 1, 1);
        var attempt = await acquire.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(attempt.Accepted);
        Assert.False(attempt.Outcome!.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.ProtocolViolation,
            attempt.Outcome.FailureKind);
        Assert.Equal("HikrobotFrameBytesUnavailable", attempt.Outcome.ReasonCode);
        await EventuallyAsync(() => finalizationOnCallback.Count > 0);
        Assert.DoesNotContain(true, finalizationOnCallback);
    }

    [Fact]
    public async Task V121_A04_TriggerFailureAfterCallbackFinalizesOutsideNativeCallback()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 5 });
        fixture.Sdk.FailTriggerAfterEmit = true;
        var finalizationOnCallback = new ConcurrentQueue<bool>();
        fixture.Device.FinalizationProbe = () =>
            finalizationOnCallback.Enqueue(HikrobotTestSdk.IsInNativeCallback);
        await using var service = fixture.CreateService();

        var attempt = await service.AcquireAsync(ExecutionKind.Qualification, "SettledFailure");

        Assert.True(attempt.Accepted);
        Assert.False(attempt.Outcome!.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.DeviceFault,
            attempt.Outcome.FailureKind);
        Assert.Equal("HikrobotFixtureTriggerFailedAfterCallback",
            attempt.Outcome.ReasonCode);
        await EventuallyAsync(() => finalizationOnCallback.Count > 0);
        Assert.DoesNotContain(true, finalizationOnCallback);
    }

    [Fact]
    public async Task V121_C17_HardwareFrameUsesControlStartBeforeWorkerContinuation()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 21 }, hardware: true);
        using var workerEntered = new ManualResetEventSlim();
        using var workerRelease = new ManualResetEventSlim();
        fixture.Device.PendingWorkerGate = () =>
        {
            workerEntered.Set();
            workerRelease.Wait();
        };
        await using var service = fixture.CreateService(
            HikrobotTestData.Requested(VisionPixelFormat.Mono8, mode:
                ProductionAcquisitionMode.HardwareTrigger));

        try
        {
            var acquire = service.AcquireAsync(ExecutionKind.Qualification,
                "HardwareBeforeWorker").AsTask();
            Assert.True(workerEntered.Wait(TimeSpan.FromSeconds(2)));
            await EventuallyAsync(() => service.Busy?.IsBusy == true);
            var start = service.Busy!.Start;
            Assert.NotNull(start);

            // Busy is already open, but RunPendingAsync is still held before its
            // continuation. The frame must use Control.Start, not a worker cache.
            fixture.Sdk.Emit(HikrobotNativePixelFormat.Mono8, 1, 1, 1,
                new byte[] { 21 }, 1, 1);
            // Keep RunPending blocked until the actual frame outcome proves
            // normalization/publication completed without its cached start.
            var attempt = await acquire.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(attempt.Accepted);
            Assert.True(attempt.Outcome!.Succeeded);
            Assert.Same(start, attempt.Outcome.Start);
            var lease = attempt.Outcome.TakeFrame();
            Assert.NotNull(lease);
            Assert.Equal(start!.BusyAt, lease!.Provenance.Milestones.AcquisitionStarted);
            lease.Dispose();
        }
        finally
        {
            workerRelease.Set();
            fixture.Device.PendingWorkerGate = null;
        }
    }

    [Fact]
    public async Task V121_C18_CancelAfterSettledTriggerRetiresBeforeReuseAndDropsLateFrame()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 31 });
        fixture.Sdk.EmitOnTrigger = false;
        fixture.Sdk.DisposeRelease.Reset();
        await using var service = fixture.CreateService();
        using var cancellation = new CancellationTokenSource();

        try
        {
            var acquire = service.AcquireAsync(ExecutionKind.Qualification, "CancelledNoFrame",
                cancellation.Token).AsTask();
            await EventuallyAsync(() => fixture.Sdk.TriggerCalls == 1);
            cancellation.Cancel();

            var cancelled = await acquire.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(cancelled.Accepted);
            Assert.Equal(ExecutionStatus.Cancelled, cancelled.Outcome!.ExecutionStatus);
            Assert.True(fixture.Sdk.DisposeEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(service.Busy!.CleanupPending);
            Assert.Equal(1, fixture.Sdk.DisposeCalls);
            Assert.False(fixture.Device.RetirementCompleted);

            // The old physical owner is still blocked in Dispose, so a second
            // service cannot take over the callback registration or correlation.
            await using var replacement = fixture.CreateService();
            var second = await replacement.AcquireAsync(ExecutionKind.Qualification,
                "NewCorrelation");
            Assert.True(second.Accepted);
            Assert.False(second.Outcome!.Succeeded);
            Assert.Equal(CameraAcquisitionFailureKind.Disconnected,
                second.Outcome.FailureKind);
            Assert.Equal(1, fixture.Sdk.TriggerCalls);

            fixture.Sdk.Emit(HikrobotNativePixelFormat.Mono8, 1, 1, 1,
                new byte[] { 31 }, 99, 99);
            await Task.Delay(10);
            Assert.Equal(1, fixture.Sdk.TriggerCalls);
            Assert.Null(second.Outcome.TakeFrame());

            fixture.Sdk.DisposeRelease.Set();
            await EventuallyAsync(() => fixture.Device.RetirementCompleted &&
                service.Busy is null);
            Assert.Equal(CameraConnectionState.Closed,
                fixture.Device.GetHealthSnapshot().Connection);
        }
        finally
        {
            fixture.Sdk.DisposeRelease.Set();
        }
    }

    [Fact]
    public async Task V121_C19_TimeoutAfterSettledSoftwareTriggerRetiresBeforeReuse()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 41 });
        fixture.Sdk.EmitOnTrigger = false;
        fixture.Sdk.DisposeRelease.Reset();
        await using var service = fixture.CreateService();

        try
        {
            var acquire = service.AcquireAsync(ExecutionKind.Qualification,
                "TimeoutNoFrame").AsTask();
            await EventuallyAsync(() => fixture.Sdk.TriggerCalls == 1 &&
                service.Busy?.IsBusy == true);
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(101));

            var timedOut = await acquire.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(timedOut.Accepted);
            Assert.Equal(ExecutionStatus.Timeout, timedOut.Outcome!.ExecutionStatus);
            Assert.True(fixture.Sdk.DisposeEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(service.Busy!.CleanupPending);

            await using var replacement = fixture.CreateService();
            var second = await replacement.AcquireAsync(ExecutionKind.Qualification,
                "TimeoutReplacement");
            Assert.True(second.Accepted);
            Assert.False(second.Outcome!.Succeeded);
            Assert.Equal(CameraAcquisitionFailureKind.Disconnected,
                second.Outcome.FailureKind);
            Assert.Equal(1, fixture.Sdk.TriggerCalls);

            fixture.Sdk.DisposeRelease.Set();
            await EventuallyAsync(() => fixture.Device.RetirementCompleted &&
                service.Busy is null);
        }
        finally
        {
            fixture.Sdk.DisposeRelease.Set();
        }
    }

    [Fact]
    public async Task V121_C20_CancelledHardwarePendingDropsDelayedPulseWithoutReuse()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 51 }, hardware: true);
        fixture.Sdk.DisposeRelease.Reset();
        await using var service = fixture.CreateService(
            HikrobotTestData.Requested(VisionPixelFormat.Mono8, mode:
                ProductionAcquisitionMode.HardwareTrigger));
        using var cancellation = new CancellationTokenSource();

        try
        {
            var acquire = service.AcquireAsync(ExecutionKind.Qualification,
                "HardwareDelayedPulse", cancellation.Token).AsTask();
            await EventuallyAsync(() => service.Busy?.IsBusy == true);
            cancellation.Cancel();

            var cancelled = await acquire.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(cancelled.Accepted);
            Assert.Equal(ExecutionStatus.Cancelled, cancelled.Outcome!.ExecutionStatus);
            Assert.True(fixture.Sdk.DisposeEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(service.Busy!.CleanupPending);

            await using var replacement = fixture.CreateService(
                HikrobotTestData.Requested(VisionPixelFormat.Mono8, mode:
                    ProductionAcquisitionMode.HardwareTrigger));
            var second = await replacement.AcquireAsync(ExecutionKind.Qualification,
                "HardwareReplacement");
            Assert.True(second.Accepted);
            Assert.False(second.Outcome!.Succeeded);
            Assert.Equal(CameraAcquisitionFailureKind.Disconnected,
                second.Outcome.FailureKind);

            fixture.Sdk.Emit(HikrobotNativePixelFormat.Mono8, 1, 1, 1,
                new byte[] { 51 }, 199, 199);
            Assert.Null(second.Outcome.TakeFrame());

            fixture.Sdk.DisposeRelease.Set();
            await EventuallyAsync(() => fixture.Device.RetirementCompleted &&
                service.Busy is null);
        }
        finally
        {
            fixture.Sdk.DisposeRelease.Set();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V121_C21_StopWithPendingRetainsPhysicalOwnerAndDropsOldFrame(bool hardware)
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 61 }, hardware: hardware);
        fixture.Sdk.EmitOnTrigger = false;
        fixture.Sdk.StopRelease.Reset();
        fixture.Sdk.DisposeRelease.Reset();
        await using var service = fixture.CreateService();
        Task<CameraOperationResult>? stop = null;
        try
        {
            var acquire = service.AcquireAsync(ExecutionKind.Qualification, "StoppedExposure").AsTask();
            await EventuallyAsync(() => service.Busy?.IsBusy == true &&
                (hardware || fixture.Sdk.TriggerCalls == 1));
            stop = fixture.Device.StopAsync().AsTask();
            Assert.True(fixture.Sdk.StopEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(stop.IsCompleted);
            Assert.False(acquire.IsCompleted);
            Assert.NotNull(service.Busy);

            await using var replacement = fixture.CreateService();
            var second = await replacement.AcquireAsync(ExecutionKind.Qualification, "RejectedReplacement");
            Assert.False(second.Outcome!.Succeeded);
            Assert.Equal(CameraAcquisitionFailureKind.Disconnected, second.Outcome.FailureKind);
            fixture.Sdk.Emit(HikrobotNativePixelFormat.Mono8, 1, 1, 1,
                new byte[] { 61 }, 299, 299);
            Assert.False(acquire.IsCompleted);
            Assert.Null(second.Outcome.TakeFrame());
            Assert.Equal(hardware ? 0 : 1, fixture.Sdk.TriggerCalls);

            fixture.Sdk.StopRelease.Set();
            Assert.True(fixture.Sdk.DisposeEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(stop.IsCompleted);
            Assert.False(acquire.IsCompleted);
            Assert.False(fixture.Device.RetirementCompleted);
            fixture.Sdk.DisposeRelease.Set();
            Assert.True((await stop.WaitAsync(TimeSpan.FromSeconds(2))).Succeeded);
            var cancelled = await acquire.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ExecutionStatus.Cancelled, cancelled.Outcome!.ExecutionStatus);
            Assert.Null(cancelled.Outcome.TakeFrame());
            await EventuallyAsync(() => fixture.Device.RetirementCompleted && service.Busy is null);
        }
        finally
        {
            fixture.Sdk.StopRelease.Set();
            fixture.Sdk.DisposeRelease.Set();
            if (stop is not null)
            {
                try { await stop.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception) { }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V121_C22_ClosedControlRetiresBeforePhysicalCancellationArrives(bool callbackFirst)
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 62 }, hardware: true);
        using var workerEntered = new ManualResetEventSlim();
        using var workerRelease = new ManualResetEventSlim();
        fixture.Device.PendingWorkerGate = () =>
        {
            workerEntered.Set();
            workerRelease.Wait();
        };
        fixture.Sdk.StopRelease.Reset();
        fixture.Sdk.DisposeRelease.Reset();
        try
        {
            var request = new FrameAcquisitionRequest(
                new ExecutionCorrelationId(ExecutionKind.Qualification, Guid.NewGuid()), "ClosedControl");
            var control = CreateExactControl(request, fixture.Clock);
            var acquire = fixture.Device.AcquireAsync(request, control).AsTask();
            Assert.True(workerEntered.Wait(TimeSpan.FromSeconds(2)));
            if (callbackFirst)
                Assert.True((bool)typeof(FrameAcquisitionControl).GetMethod("TryOpen",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control,
                    new object[] { new FrameAcquisitionStart(fixture.Clock.GetTimePoint(),
                        fixture.Clock.Frequency, fixture.Clock.Frequency) })!);
            // No cancellation token is signalled: this is the gap between Runtime
            // Close and its separately scheduled physical cancellation dispatch.
            typeof(FrameAcquisitionControl).GetMethod("Close",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, null);
            if (callbackFirst)
                fixture.Sdk.Emit(HikrobotNativePixelFormat.Mono8, 1, 1, 1,
                    new byte[] { 62 }, 300, 300);
            else
                workerRelease.Set();

            Assert.True(fixture.Sdk.StopEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(acquire.IsCompleted);
            await using var replacement = fixture.CreateService();
            var second = await replacement.AcquireAsync(ExecutionKind.Qualification, "RejectedReplacement");
            Assert.Equal(CameraAcquisitionFailureKind.Disconnected, second.Outcome!.FailureKind);
            Assert.Null(second.Outcome.TakeFrame());
            fixture.Sdk.StopRelease.Set();
            Assert.True(fixture.Sdk.DisposeEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(acquire.IsCompleted);
            fixture.Sdk.DisposeRelease.Set();
            var result = await acquire.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(CameraAcquisitionFailureKind.Cancelled, result.Failure!.Kind);
            Assert.Null(result.Lease);
            Assert.True(fixture.Device.RetirementCompleted);
        }
        finally
        {
            workerRelease.Set();
            fixture.Sdk.StopRelease.Set();
            fixture.Sdk.DisposeRelease.Set();
            fixture.Device.PendingWorkerGate = null;
            await fixture.Device.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V121_C23_CompletedSessionCannotAssignDelayedSecondFrameToNewRequest(bool hardware)
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 63 }, hardware: hardware);
        await using var service = fixture.CreateService();
        var acquisition = service.AcquireAsync(ExecutionKind.Qualification, "OnlyFrame").AsTask();
        if (hardware)
        {
            await EventuallyAsync(() => service.Busy?.IsBusy == true);
            fixture.Sdk.Emit(HikrobotNativePixelFormat.Mono8, 1, 1, 1,
                new byte[] { 63 }, 301, 301);
        }
        var first = await acquisition.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(first.Outcome!.Succeeded);
        using var lease = first.Outcome.TakeFrame();
        var firstCorrelation = lease!.Frame.Correlation;
        var nativeCalls = fixture.Sdk.CallEvents.Count;
        var restart = await fixture.Device.StartAsync();
        Assert.False(restart.Succeeded);
        Assert.Equal("HikrobotSingleFrameSessionConsumed", restart.ReasonCode);

        await using var replacement = fixture.CreateService();
        var second = await replacement.AcquireAsync(ExecutionKind.Qualification, "RejectedNextFrame");
        Assert.False(second.Outcome!.Succeeded);
        Assert.Equal("HikrobotSingleFrameSessionConsumed", second.Outcome.ReasonCode);
        Assert.Null(second.Outcome.TakeFrame());
        // The duplicate arrives only AFTER the first result and next request.
        fixture.Sdk.Emit(HikrobotNativePixelFormat.Mono8, 1, 1, 1,
            new byte[] { 99 }, 302, 302);
        Assert.Equal(new byte[] { 63 }, lease.Frame.GetRowSpan(0).ToArray());
        Assert.Equal(nativeCalls, fixture.Sdk.CallEvents.Count);
        Assert.Contains(fixture.Device.ReadProtocolObservations(0).Observations,
            item => item.Kind == CameraProtocolViolationKind.LateFrame &&
                item.Correlation == firstCorrelation);
        Assert.Equal(CameraConnectionState.Disconnected, fixture.Device.GetHealthSnapshot().Connection);
        Assert.Equal(CameraConfigurationState.Unknown, fixture.Device.GetHealthSnapshot().Configuration);
        Assert.Equal(CameraAcquisitionState.Stopped, fixture.Device.GetHealthSnapshot().Acquisition);
        Assert.False(fixture.Device.RetirementCompleted);
    }

    [Fact]
    public async Task V121_C24_IdleStopAfterCallbackRegistrationCannotRestartSameHandle()
    {
        var fixture = await CreateFixtureAsync(VisionPixelFormat.Mono8,
            HikrobotNativePixelFormat.Mono8, new byte[] { 64 }, hardware: true);
        fixture.Sdk.StopRelease.Reset();
        fixture.Sdk.DisposeRelease.Reset();
        try
        {
            var stop = fixture.Device.StopAsync().AsTask();
            Assert.True(fixture.Sdk.StopEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.False((await fixture.Device.StartAsync()).Succeeded);
            await using var service = fixture.CreateService();
            var rejected = await service.AcquireAsync(ExecutionKind.Qualification, "AfterIdleStop");
            Assert.Equal(CameraAcquisitionFailureKind.Disconnected, rejected.Outcome!.FailureKind);
            fixture.Sdk.EmitAfterStop();
            Assert.Null(rejected.Outcome.TakeFrame());
            fixture.Sdk.StopRelease.Set();
            Assert.True(fixture.Sdk.DisposeEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(stop.IsCompleted);
            Assert.False((await fixture.Device.StartAsync()).Succeeded);
            Assert.Equal(1, fixture.Sdk.StartCalls);
            fixture.Sdk.DisposeRelease.Set();
            Assert.True((await stop.WaitAsync(TimeSpan.FromSeconds(2))).Succeeded);
            Assert.True(fixture.Device.RetirementCompleted);
            Assert.False((await fixture.Device.StartAsync()).Succeeded);
            Assert.Equal(1, fixture.Sdk.StartCalls);
        }
        finally
        {
            fixture.Sdk.StopRelease.Set();
            fixture.Sdk.DisposeRelease.Set();
            await fixture.Device.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task<Fixture> CreateFixtureAsync(VisionPixelFormat format,
        HikrobotNativePixelFormat nativeFormat, byte[] bytes, int validBits = 16,
        bool emitOnTriggerTwice = false, int poolCapacity = 2, bool hardware = false,
        bool configure = true, TimeSpan? callbackBudget = null)
    {
        var capabilities = HikrobotTestData.Capabilities(format,
            format == VisionPixelFormat.Mono16 ? validBits : null, hardware);
        var sdk = new HikrobotTestSdk(capabilities)
        {
            TriggerPixelFormat = nativeFormat,
            TriggerBytes = bytes,
            TriggerStrideBytes = format == VisionPixelFormat.Bgr24 ? 3 :
                format == VisionPixelFormat.Mono16 ? 2 : 1,
            EmitTwiceOnTrigger = emitOnTriggerTwice
        };
        var clock = new HikrobotTestClock();
        var device = new HikrobotCameraDevice(sdk, HikrobotTestData.Identity(), "4.8.1.2",
            clock, HikrobotTestData.Pool(poolCapacity, callbackBudget: callbackBudget));
        var requested = HikrobotTestData.Requested(format,
            format == VisionPixelFormat.Mono16 ? validBits : null,
            hardware ? ProductionAcquisitionMode.HardwareTrigger :
                ProductionAcquisitionMode.SoftwareTrigger);
        if (configure)
        {
            var result = await device.ApplyConfigurationAsync(requested);
            Assert.True(result.Succeeded, result.ReasonCode);
            var started = await device.StartAsync();
            Assert.True(started.Succeeded, started.ReasonCode);
        }

        return new Fixture(device, sdk, clock, requested);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("HikrobotFixtureEventuallyTimeout");
            await Task.Delay(5);
        }
    }

    private static FrameAcquisitionControl CreateExactControl(
        FrameAcquisitionRequest request, IFrameAcquisitionClock clock) =>
        (FrameAcquisitionControl)Activator.CreateInstance(
            typeof(FrameAcquisitionControl), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { request, clock }, null)!;

    private sealed record Fixture(HikrobotCameraDevice Device, HikrobotTestSdk Sdk,
        HikrobotTestClock Clock, RequestedCameraConfiguration Requested)
    {
        internal CameraAcquisitionService CreateService(
            RequestedCameraConfiguration? requested = null)
        {
            var effective = Device.Capabilities.ValidateConfiguration(
                requested ?? Requested).Effective!;
            return new CameraAcquisitionService(Device, effective, Clock,
                new CameraAcquisitionOptions(TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1)));
        }
    }
}
