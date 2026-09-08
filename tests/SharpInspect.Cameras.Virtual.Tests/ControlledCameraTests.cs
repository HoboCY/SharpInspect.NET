using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using Xunit;

namespace SharpInspect.Cameras.Virtual.Tests;

public sealed class ControlledCameraTests
{
    [Fact]
    public async Task V118_V05_SoftwareControlledAcquisitionWaitsForBusyAndReturnsOneFrame()
    {
        using var clock = new VirtualCameraClock(Utc());
        var scenario = Scenario(ProductionAcquisitionMode.SoftwareTrigger,
            new[] { Signal(10) });
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var device = await OpenAndConfigureAsync(provider, scenario,
            ProductionAcquisitionMode.SoftwareTrigger);
        Assert.True((await device.StartAsync()).Succeeded);

        var request = Request();
        var control = CreateControl(request, clock);
        var acquisition = device.AcquireAsync(request, control).AsTask();

        Assert.False(acquisition.IsCompleted);
        Assert.Equal(2, clock.PendingEventCount);
        OpenControl(control, clock, TimeSpan.FromMilliseconds(20));
        WaitForScheduledCallbacks(clock);
        clock.AdvanceBy(TimeSpan.FromMilliseconds(10));

        var result = await acquisition.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.Equal(TimeSpan.FromMilliseconds(10).Ticks,
            result.Lease!.Provenance.Milestones.NativeFrameReceived!.MonotonicTimestamp);
        Assert.Equal(TimeSpan.Zero.Ticks,
            result.Lease.Provenance.Milestones.AcquisitionStarted!.MonotonicTimestamp);
        result.Lease.Dispose();
    }

    [Fact]
    public async Task V118_V06_HardwarePulseRequiresExactBusyCorrelationAndOnePulse()
    {
        using var clock = new VirtualCameraClock(Utc());
        var scenario = Scenario(ProductionAcquisitionMode.HardwareTrigger,
            new[] { Signal(10) });
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var device = await OpenAndConfigureAsync(provider, scenario,
            ProductionAcquisitionMode.HardwareTrigger);
        Assert.True((await device.StartAsync()).Succeeded);

        var request = Request();
        var control = CreateControl(request, clock);
        var acquisition = device.AcquireAsync(request, control).AsTask();
        OpenControl(control, clock, TimeSpan.FromMilliseconds(20));

        var wrong = provider.PulseHardwareTrigger(scenario.StableDeviceIdentity,
            Request().Correlation);
        Assert.False(wrong.Succeeded);
        Assert.Equal("VirtualCameraPulseCorrelationMismatch", wrong.ReasonCode);
        var accepted = provider.PulseHardwareTrigger(scenario.StableDeviceIdentity,
            request.Correlation);
        Assert.True(accepted.Succeeded, accepted.ReasonCode);
        var duplicate = provider.PulseHardwareTrigger(scenario.StableDeviceIdentity,
            request.Correlation);
        Assert.False(duplicate.Succeeded);
        Assert.Equal("VirtualCameraPulseDuplicate", duplicate.ReasonCode);

        WaitForScheduledCallbacks(clock);
        clock.AdvanceBy(TimeSpan.FromMilliseconds(10));
        var result = await acquisition.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Succeeded, result.ReasonCode);
        result.Lease!.Dispose();

        var terminal = provider.PulseHardwareTrigger(scenario.StableDeviceIdentity,
            request.Correlation);
        Assert.False(terminal.Succeeded);
        Assert.Equal("VirtualCameraPulseAfterTerminal", terminal.ReasonCode);

        var observations = device.ReadProtocolObservations(0);
        Assert.Contains(observations.Observations,
            item => item.Kind == CameraProtocolViolationKind.CorrelationMismatch);
        Assert.Contains(observations.Observations,
            item => item.Kind == CameraProtocolViolationKind.DuplicateHardwarePulse);
        Assert.Contains(observations.Observations,
            item => item.Kind == CameraProtocolViolationKind.EarlyHardwarePulse);
        Assert.DoesNotContain(observations.Observations,
            item => item.Kind == CameraProtocolViolationKind.LateFrame);
    }

    [Fact]
    public async Task V118_V07_EarlyPulseAndUnpulsedHardwareFrameAreProtocolFailuresWithoutLease()
    {
        using var clock = new VirtualCameraClock(Utc());
        var scenario = Scenario(ProductionAcquisitionMode.HardwareTrigger,
            new[] { Signal(5) });
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var device = await OpenAndConfigureAsync(provider, scenario,
            ProductionAcquisitionMode.HardwareTrigger);
        Assert.True((await device.StartAsync()).Succeeded);

        var request = Request();
        var control = CreateControl(request, clock);
        var acquisition = device.AcquireAsync(request, control).AsTask();
        var early = provider.PulseHardwareTrigger(scenario.StableDeviceIdentity,
            request.Correlation);
        Assert.False(early.Succeeded);
        Assert.Equal("VirtualCameraPulseBeforeBusy", early.ReasonCode);

        OpenControl(control, clock, TimeSpan.FromMilliseconds(20));
        WaitForScheduledCallbacks(clock);
        clock.AdvanceBy(TimeSpan.FromMilliseconds(5));
        var result = await acquisition.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.ProtocolViolation, result.Failure!.Kind);
        Assert.Equal("VirtualCameraHardwareFrameBeforePulse", result.ReasonCode);
        Assert.Null(result.Lease);

        var observations = device.ReadProtocolObservations(0);
        Assert.Contains(observations.Observations,
            item => item.Kind == CameraProtocolViolationKind.EarlyHardwarePulse);
        Assert.Contains(observations.Observations,
            item => item.Kind == CameraProtocolViolationKind.EarlyFrame);
    }

    [Fact]
    public async Task V118_V08_BusyTimestampMustEqualInstalledPendingTime()
    {
        using var clock = new VirtualCameraClock(Utc());
        var scenario = Scenario(ProductionAcquisitionMode.SoftwareTrigger,
            Array.Empty<VirtualCameraSignal>());
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var device = await OpenAndConfigureAsync(provider, scenario,
            ProductionAcquisitionMode.SoftwareTrigger);
        Assert.True((await device.StartAsync()).Succeeded);

        var request = Request();
        var control = CreateControl(request, clock);
        var acquisition = device.AcquireAsync(request, control).AsTask();
        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        OpenControl(control, clock, TimeSpan.FromMilliseconds(20));

        var result = await acquisition.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.ProtocolViolation, result.Failure!.Kind);
        Assert.Equal("VirtualCameraBusyTimestampMismatch", result.ReasonCode);
        Assert.Equal(0, clock.PendingEventCount);
    }

    [Fact]
    public async Task V118_V09_CancellationReleasesTheOnlyPendingSlotAndDoesNotReviveIt()
    {
        using var clock = new VirtualCameraClock(Utc());
        var scenario = Scenario(ProductionAcquisitionMode.SoftwareTrigger,
            Array.Empty<VirtualCameraSignal>(),
            new[] { Signal(1) });
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var device = await OpenAndConfigureAsync(provider, scenario,
            ProductionAcquisitionMode.SoftwareTrigger);
        Assert.True((await device.StartAsync()).Succeeded);

        using var cancellation = new CancellationTokenSource();
        var firstRequest = Request();
        var firstControl = CreateControl(firstRequest, clock);
        var first = device.AcquireAsync(firstRequest, firstControl, cancellation.Token).AsTask();
        cancellation.Cancel();
        var cancelled = await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(cancelled.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.Cancelled, cancelled.Failure!.Kind);
        Assert.Equal(0, clock.PendingEventCount);

        var secondRequest = Request();
        var secondControl = CreateControl(secondRequest, clock);
        var second = device.AcquireAsync(secondRequest, secondControl).AsTask();
        OpenControl(secondControl, clock, TimeSpan.FromMilliseconds(20));
        WaitForScheduledCallbacks(clock);
        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        var completed = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(completed.Succeeded, completed.ReasonCode);
        completed.Lease!.Dispose();
    }

    [Fact]
    public async Task V118_V10_ProtocolRingReportsCursorGapAndTruthfulThroughAcrossPages()
    {
        using var clock = new VirtualCameraClock(Utc());
        var scenario = Scenario(ProductionAcquisitionMode.HardwareTrigger,
            new[] { Signal(1) });
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var device = await OpenAndConfigureAsync(provider, scenario,
            ProductionAcquisitionMode.HardwareTrigger);
        Assert.True((await device.StartAsync()).Succeeded);

        var request = Request();
        for (var index = 0; index < 70; index++)
        {
            var pulse = provider.PulseHardwareTrigger(scenario.StableDeviceIdentity,
                request.Correlation);
            Assert.False(pulse.Succeeded);
            Assert.Equal("VirtualCameraPulseWithoutPending", pulse.ReasonCode);
        }

        var firstPage = device.ReadProtocolObservations(0, 10);
        Assert.True(firstPage.Overflowed);
        Assert.Equal(7, firstPage.FirstAvailableSequence);
        Assert.Equal(70, firstPage.ThroughSequence);
        Assert.Equal(10, firstPage.Observations.Count);
        Assert.Equal(7, firstPage.Observations[0].Sequence);
        Assert.Equal(16, firstPage.Observations[^1].Sequence);
        Assert.All(firstPage.Observations, observation =>
        {
            Assert.Equal(CameraProtocolViolationKind.EarlyHardwarePulse, observation.Kind);
            Assert.Null(observation.Correlation);
        });

        var secondPage = device.ReadProtocolObservations(firstPage.Observations[^1].Sequence, 64);
        Assert.False(secondPage.Overflowed);
        Assert.Equal(70, secondPage.ThroughSequence);
        Assert.Equal(54, secondPage.Observations.Count);
        Assert.Equal(17, secondPage.Observations[0].Sequence);
        Assert.Equal(70, secondPage.Observations[^1].Sequence);
        Assert.All(secondPage.Observations, observation =>
        {
            Assert.Equal(CameraProtocolViolationKind.EarlyHardwarePulse, observation.Kind);
            Assert.Null(observation.Correlation);
        });

        // Reusing the same correlation for a later request must not turn an
        // idle pulse into an authorization for that pending acquisition.
        var control = CreateControl(request, clock);
        var acquisition = device.AcquireAsync(request, control).AsTask();
        OpenControl(control, clock, TimeSpan.FromMilliseconds(20));
        WaitForScheduledCallbacks(clock);
        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));

        var result = await acquisition.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.ProtocolViolation, result.Failure!.Kind);
        Assert.Equal("VirtualCameraHardwareFrameBeforePulse", result.ReasonCode);
        Assert.Null(result.Lease);
    }

    [Fact]
    public async Task V118_V15_ClosedControlIsCancelledWithoutProtocolObservation()
    {
        using var clock = new VirtualCameraClock(Utc());
        var scenario = Scenario(ProductionAcquisitionMode.SoftwareTrigger,
            Array.Empty<VirtualCameraSignal>());
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var device = await OpenAndConfigureAsync(provider, scenario,
            ProductionAcquisitionMode.SoftwareTrigger);
        Assert.True((await device.StartAsync()).Succeeded);

        var request = Request();
        var control = CreateControl(request, clock);
        CloseControl(control);
        var before = device.ReadProtocolObservations(0);

        var result = await device.AcquireAsync(request, control).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.Cancelled, result.Failure!.Kind);
        Assert.Equal("VirtualCameraControlClosed", result.ReasonCode);
        Assert.Null(result.Lease);
        var after = device.ReadProtocolObservations(0);
        Assert.Equal(before.ThroughSequence, after.ThroughSequence);
    }

    [Fact]
    public async Task V118_V16_InvalidIdlePulseCorrelationIsRejectedWithoutRingEvidence()
    {
        using var clock = new VirtualCameraClock(Utc());
        var scenario = Scenario(ProductionAcquisitionMode.SoftwareTrigger,
            Array.Empty<VirtualCameraSignal>());
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var opened = await provider.OpenAsync(scenario.StableDeviceIdentity);
        var device = Assert.IsAssignableFrom<IControlledCameraDevice>(opened.Device);
        var before = device.ReadProtocolObservations(0);

        var emptyValue = new ExecutionCorrelationId(ExecutionKind.Qualification, Guid.Empty);
        var invalidKind = new ExecutionCorrelationId((ExecutionKind)999, Guid.NewGuid());
        var emptyResult = provider.PulseHardwareTrigger(scenario.StableDeviceIdentity,
            emptyValue);
        var invalidKindResult = provider.PulseHardwareTrigger(scenario.StableDeviceIdentity,
            invalidKind);

        Assert.False(emptyResult.Succeeded);
        Assert.Equal("VirtualCameraPulseCorrelationInvalid", emptyResult.ReasonCode);
        Assert.False(invalidKindResult.Succeeded);
        Assert.Equal("VirtualCameraPulseCorrelationInvalid", invalidKindResult.ReasonCode);
        var after = device.ReadProtocolObservations(0);
        Assert.Equal(before.ThroughSequence, after.ThroughSequence);
    }

    [Fact]
    public async Task V118_V17_ClosingAckedControlBeforeBusyCancelsWithoutProtocolObservation()
    {
        using var clock = new VirtualCameraClock(Utc());
        var scenario = Scenario(ProductionAcquisitionMode.SoftwareTrigger,
            Array.Empty<VirtualCameraSignal>());
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var device = await OpenAndConfigureAsync(provider, scenario,
            ProductionAcquisitionMode.SoftwareTrigger);
        Assert.True((await device.StartAsync()).Succeeded);

        var request = Request();
        var control = CreateControl(request, clock);
        var acquisition = device.AcquireAsync(request, control).AsTask();
        CloseControl(control);

        var result = await acquisition.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.Cancelled, result.Failure!.Kind);
        Assert.Equal("VirtualCameraControlClosed", result.ReasonCode);
        Assert.Null(result.Lease);
        Assert.Equal(0, clock.PendingEventCount);
        Assert.Equal(0, device.ReadProtocolObservations(0).ThroughSequence);
    }

    [Fact]
    public async Task V118_V18_ClosingBusyControlBeforeContinuationCancelsWithoutProtocolObservation()
    {
        using var clock = new VirtualCameraClock(Utc());
        var scenario = Scenario(ProductionAcquisitionMode.SoftwareTrigger,
            Array.Empty<VirtualCameraSignal>());
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var device = await OpenAndConfigureAsync(provider, scenario,
            ProductionAcquisitionMode.SoftwareTrigger);
        Assert.True((await device.StartAsync()).Succeeded);

        var request = Request();
        var control = CreateControl(request, clock);
        var acquisition = device.AcquireAsync(request, control).AsTask();
        OpenThenCloseControlUnderLock(control, clock, TimeSpan.FromMilliseconds(20));

        var result = await acquisition.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.Cancelled, result.Failure!.Kind);
        Assert.Equal("VirtualCameraControlClosed", result.ReasonCode);
        Assert.Null(result.Lease);
        Assert.Equal(1, clock.PendingEventCount);
        Assert.Equal(0, device.ReadProtocolObservations(0).ThroughSequence);
    }

    [Fact]
    public async Task V118_V19_LateFrameFromClosedBusyRequestCannotAffectNextRequest()
    {
        using var clock = new VirtualCameraClock(Utc());
        var scenario = Scenario(ProductionAcquisitionMode.SoftwareTrigger,
            new[] { Signal(5) }, new[] { Signal(10) });
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var device = await OpenAndConfigureAsync(provider, scenario,
            ProductionAcquisitionMode.SoftwareTrigger);
        Assert.True((await device.StartAsync()).Succeeded);

        using var cancellation = new CancellationTokenSource();
        var requestA = Request();
        var controlA = CreateControl(requestA, clock);
        var acquisitionA = device.AcquireAsync(requestA, controlA, cancellation.Token).AsTask();
        OpenControl(controlA, clock, TimeSpan.FromMilliseconds(20));
        CloseControl(controlA);
        cancellation.Cancel();

        var resultA = await acquisitionA.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(resultA.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.Cancelled, resultA.Failure!.Kind);
        Assert.Null(resultA.Lease);

        var requestB = Request();
        var controlB = CreateControl(requestB, clock);
        var acquisitionB = device.AcquireAsync(requestB, controlB).AsTask();
        OpenControl(controlB, clock, TimeSpan.FromMilliseconds(20));
        WaitForScheduledCallbacks(clock);

        clock.AdvanceTo(TimeSpan.FromMilliseconds(5).Ticks);
        Assert.False(acquisitionB.IsCompleted);
        var afterLateFrame = device.ReadProtocolObservations(0);
        var lateFrame = Assert.Single(afterLateFrame.Observations,
            item => item.Kind == CameraProtocolViolationKind.LateFrame);
        Assert.Equal(requestA.Correlation, lateFrame.Correlation);
        Assert.DoesNotContain(afterLateFrame.Observations,
            item => item.Correlation == requestB.Correlation);

        clock.AdvanceTo(TimeSpan.FromMilliseconds(10).Ticks);
        var resultB = await acquisitionB.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(resultB.Succeeded, resultB.ReasonCode);
        Assert.Equal(requestB.Correlation, resultB.Lease!.Provenance.Correlation);
        resultB.Lease.Dispose();
    }

    [Fact]
    public async Task V118_V11_DisconnectCompletesTheControlledPendingTaskAsDisconnected()
    {
        using var clock = new VirtualCameraClock(Utc());
        var scenario = Scenario(ProductionAcquisitionMode.SoftwareTrigger,
            new[] { new VirtualCameraSignal(TimeSpan.FromMilliseconds(5),
                VirtualCameraSignalKind.Disconnect) });
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var device = await OpenAndConfigureAsync(provider, scenario,
            ProductionAcquisitionMode.SoftwareTrigger);
        Assert.True((await device.StartAsync()).Succeeded);

        var request = Request();
        var control = CreateControl(request, clock);
        var acquisition = device.AcquireAsync(request, control).AsTask();
        OpenControl(control, clock, TimeSpan.FromMilliseconds(20));
        WaitForScheduledCallbacks(clock);
        clock.AdvanceBy(TimeSpan.FromMilliseconds(5));

        var result = await acquisition.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.Succeeded);
        Assert.Equal(CameraAcquisitionFailureKind.Disconnected, result.Failure!.Kind);
        Assert.Equal(CameraConnectionState.Closed, device.GetHealthSnapshot().Connection);
    }

    [Fact]
    public async Task V118_V12_FrameAtExactDeadlineWinsWithoutBusyContinuationScheduling()
    {
        using var clock = new VirtualCameraClock(Utc());
        var scenario = Scenario(ProductionAcquisitionMode.SoftwareTrigger,
            new[] { Signal(10) });
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var device = await OpenAndConfigureAsync(provider, scenario,
            ProductionAcquisitionMode.SoftwareTrigger, timeoutMilliseconds: 10);
        Assert.True((await device.StartAsync()).Succeeded);

        var request = Request();
        var control = CreateControl(request, clock);
        var acquisition = device.AcquireAsync(request, control).AsTask();
        Assert.False(acquisition.IsCompleted);
        OpenControl(control, clock, TimeSpan.FromMilliseconds(10));

        // The controlled adapter has already registered both callbacks at
        // acquisition admission.  Advancing immediately after Busy therefore
        // exercises the clock phase ordering without waiting for its Busy waiter.
        clock.AdvanceBy(TimeSpan.FromMilliseconds(10));

        var result = await acquisition.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.Equal(TimeSpan.FromMilliseconds(10).Ticks,
            result.Lease!.Provenance.Milestones.NativeFrameReceived!.MonotonicTimestamp);
        result.Lease.Dispose();
    }

    [Fact]
    public async Task V118_V13_BusyUtcEvidenceMayChangeWhenMonotonicTimeIsUnchanged()
    {
        using var clock = new VirtualCameraClock(Utc());
        var scenario = Scenario(ProductionAcquisitionMode.SoftwareTrigger,
            new[] { Signal(1) });
        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock);
        var device = await OpenAndConfigureAsync(provider, scenario,
            ProductionAcquisitionMode.SoftwareTrigger);
        Assert.True((await device.StartAsync()).Succeeded);

        var request = Request();
        var control = CreateControl(request, clock);
        var acquisition = device.AcquireAsync(request, control).AsTask();
        clock.SetUtc(Utc().AddHours(1));
        OpenControl(control, clock, TimeSpan.FromMilliseconds(20));
        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));

        var result = await acquisition.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.Equal(TimeSpan.Zero.Ticks,
            result.Lease!.Provenance.Milestones.AcquisitionStarted!.MonotonicTimestamp);
        Assert.Equal(Utc().AddHours(1),
            result.Lease.Provenance.Milestones.AcquisitionStarted.HostObservedAtUtc);
        result.Lease.Dispose();
    }

    private static async Task<IControlledCameraDevice> OpenAndConfigureAsync(
        VirtualCameraProvider provider, VirtualCameraScenario scenario,
        ProductionAcquisitionMode mode, int timeoutMilliseconds = 20)
    {
        var opened = await provider.OpenAsync(scenario.StableDeviceIdentity);
        Assert.True(opened.Succeeded, opened.ReasonCode);
        var device = Assert.IsAssignableFrom<IControlledCameraDevice>(opened.Device);
        var configured = await device.ApplyConfigurationAsync(
            Configuration(mode, timeoutMilliseconds));
        Assert.True(configured.Succeeded, configured.ReasonCode);
        return device;
    }

    private static FrameAcquisitionControl CreateControl(FrameAcquisitionRequest request,
        VirtualCameraClock clock)
    {
        var constructor = typeof(FrameAcquisitionControl).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            new[] { typeof(FrameAcquisitionRequest), typeof(IFrameAcquisitionClock) },
            modifiers: null);
        Assert.NotNull(constructor);
        return (FrameAcquisitionControl)constructor!.Invoke(new object?[]
            { request, (IFrameAcquisitionClock)clock });
    }

    private static void OpenControl(FrameAcquisitionControl control,
        VirtualCameraClock clock, TimeSpan timeout)
    {
        var method = typeof(FrameAcquisitionControl).GetMethod("TryOpen",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var busyAt = clock.GetTimePoint();
        var start = new FrameAcquisitionStart(busyAt,
            checked(busyAt.MonotonicTimestamp + timeout.Ticks),
            ((IFrameAcquisitionClock)clock).Frequency);
        Assert.True((bool)method!.Invoke(control, new object[] { start })!);
        Assert.True(control.IsBusy);
    }

    private static void CloseControl(FrameAcquisitionControl control)
    {
        var method = typeof(FrameAcquisitionControl).GetMethod("Close",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(control, Array.Empty<object>());
        Assert.True(control.IsClosed);
    }

    private static void OpenThenCloseControlUnderLock(FrameAcquisitionControl control,
        VirtualCameraClock clock, TimeSpan timeout)
    {
        var syncField = typeof(FrameAcquisitionControl).GetField("_sync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(syncField);
        var sync = syncField!.GetValue(control);
        Assert.NotNull(sync);
        var openMethod = typeof(FrameAcquisitionControl).GetMethod("TryOpen",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var closeMethod = typeof(FrameAcquisitionControl).GetMethod("Close",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(openMethod);
        Assert.NotNull(closeMethod);
        var busyAt = clock.GetTimePoint();
        var start = new FrameAcquisitionStart(busyAt,
            checked(busyAt.MonotonicTimestamp + timeout.Ticks),
            ((IFrameAcquisitionClock)clock).Frequency);

        lock (sync!)
        {
            Assert.True((bool)openMethod!.Invoke(control, new object[] { start })!);
            closeMethod!.Invoke(control, Array.Empty<object>());
        }

        Assert.True(control.IsClosed);
    }

    private static void WaitForScheduledCallbacks(VirtualCameraClock clock)
    {
        Assert.True(SpinWait.SpinUntil(() => clock.PendingEventCount > 0,
            TimeSpan.FromSeconds(2)), "controlled acquisition did not register callbacks");
    }

    private static FrameAcquisitionRequest Request() => new(
        new ExecutionCorrelationId(ExecutionKind.Qualification, Guid.NewGuid()),
        "camera.primary");

    private static RequestedCameraConfiguration Configuration(ProductionAcquisitionMode mode,
        int timeoutMilliseconds = 20) =>
        new(mode, 10, 0, new RegionOfInterest(0, 0, 2, 2), VisionPixelFormat.Mono8,
            null, timeoutMilliseconds, 0, null);

    private static VirtualCameraSignal Signal(int milliseconds) => new(
        TimeSpan.FromMilliseconds(milliseconds), VirtualCameraSignalKind.Frame, "frame");

    private static VirtualCameraScenario Scenario(ProductionAcquisitionMode mode,
        IEnumerable<VirtualCameraSignal> firstSignals,
        IEnumerable<VirtualCameraSignal>? secondSignals = null)
    {
        var acquisitions = new List<VirtualCameraAcquisitionPlan>
        {
            new(firstSignals)
        };
        if (secondSignals is not null)
            acquisitions.Add(new VirtualCameraAcquisitionPlan(secondSignals));
        return new VirtualCameraScenario("controlled." + mode, "1", 42,
            "virtual:controlled", Capabilities(mode),
            new[] { VirtualCameraImage.CreateSynthetic("frame", 2, 2,
                VisionPixelFormat.Mono8, null, 42, 1) }, acquisitions);
    }

    private static CameraCapabilities Capabilities(ProductionAcquisitionMode mode) => new(
        new[] { mode }, new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
        new(1, 100, 1, CameraQuantizationMode.Exact),
        new(0, 10, 1, CameraQuantizationMode.Exact),
        new(0, 10, 1, CameraQuantizationMode.Exact),
        new(2, 2, new(0, 1, 1), new(0, 1, 1), new(1, 2, 1), new(1, 2, 1)));

    private static DateTimeOffset Utc() =>
        new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
}
