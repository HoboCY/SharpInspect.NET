using SharpInspect.Abstractions;

namespace SharpInspect.Cameras.Virtual;

/// <summary>
/// Stable result returned by the development-only hardware-trigger seam.  The
/// result intentionally exposes only a bounded reason identifier; the adapter
/// never returns a vendor exception or diagnostic text through this boundary.
/// </summary>
public sealed class VirtualHardwareTriggerResult
{
    private VirtualHardwareTriggerResult(bool succeeded, string reasonCode)
    {
        Succeeded = succeeded;
        ReasonCode = reasonCode;
    }

    public bool Succeeded { get; }
    public string ReasonCode { get; }

    public static VirtualHardwareTriggerResult Success() =>
        new(true, "VirtualCameraPulseAccepted");

    internal static VirtualHardwareTriggerResult Failure(string reasonCode) =>
        new(false, reasonCode);
}

public sealed partial class VirtualCameraProvider
{
    /// <summary>
    /// Supplies one external hardware-trigger pulse to the exact pending
    /// controlled acquisition.  A pulse never invokes a frame callback inline.
    /// </summary>
    public VirtualHardwareTriggerResult PulseHardwareTrigger(
        string stableDeviceIdentity, ExecutionCorrelationId expectedCorrelation)
    {
        ArgumentNullException.ThrowIfNull(stableDeviceIdentity);
        ArgumentNullException.ThrowIfNull(expectedCorrelation);
        if (!Enum.IsDefined(typeof(ExecutionKind), expectedCorrelation.Kind) ||
            expectedCorrelation.Value == Guid.Empty)
            return VirtualHardwareTriggerResult.Failure("VirtualCameraPulseCorrelationInvalid");

        VirtualCameraSession? session;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return VirtualHardwareTriggerResult.Failure("VirtualCameraProviderDisposed");
            if (!_sessions.TryGetValue(stableDeviceIdentity, out session))
                return VirtualHardwareTriggerResult.Failure("VirtualCameraDeviceMissing");
        }

        VirtualCameraDevice? device;
        lock (session.Gate) device = session.Active;
        return device is null
            ? VirtualHardwareTriggerResult.Failure("VirtualCameraPulseDeviceMissing")
            : device.TryPulseHardwareTrigger(expectedCorrelation);
    }
}

internal sealed partial class VirtualCameraDevice
{
    private const int MaximumProtocolObservations = 64;

    public ValueTask<FrameAcquisitionResult> AcquireAsync(
        FrameAcquisitionRequest request, FrameAcquisitionControl control,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(control);

        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(Failure(CameraAcquisitionFailureKind.Cancelled,
                "VirtualCameraAcquisitionCancelled"));
        if (!ReferenceEquals(control.Clock, _clock))
            return ValueTask.FromResult(Failure(CameraAcquisitionFailureKind.ProtocolViolation,
                "VirtualCameraControlClockMismatch"));
        if (!ReferenceEquals(control.Request, request))
            return ValueTask.FromResult(Failure(CameraAcquisitionFailureKind.ProtocolViolation,
                "VirtualCameraControlRequestMismatch"));

        PendingAcquisition? pending = null;
        FrameAcquisitionResult? immediateFailure = null;
        IDisposable[] failureHandles = Array.Empty<IDisposable>();
        lock (_gate)
        {
            if (_disposed)
                immediateFailure = Failure(CameraAcquisitionFailureKind.Disconnected,
                    "VirtualCameraDisposed");
            else if (_connection != CameraConnectionState.Open)
                immediateFailure = Failure(CameraAcquisitionFailureKind.Disconnected,
                    "VirtualCameraNotOpen");
            else if (_pendingConfiguration is not null)
                immediateFailure = Failure(CameraAcquisitionFailureKind.DeviceFault,
                    "VirtualCameraConfigurationPending");
            else if (_pendingAcquisition is not null)
                immediateFailure = Failure(CameraAcquisitionFailureKind.AlreadyPending,
                    "VirtualCameraAcquisitionPending");
            else if (_effective is null || _configuration != CameraConfigurationState.Applied)
                immediateFailure = Failure(CameraAcquisitionFailureKind.NotConfigured,
                    "VirtualCameraNotConfigured");
            else if (_acquisition != CameraAcquisitionState.Armed)
                immediateFailure = Failure(CameraAcquisitionFailureKind.NotStarted,
                    "VirtualCameraNotStarted");
            else if (control.IsClosed)
                immediateFailure = Failure(CameraAcquisitionFailureKind.Cancelled,
                    "VirtualCameraControlClosed");
            else
            {
                var plan = TakeAcquisitionPlan();
                if (plan is null)
                    immediateFailure = Failure(CameraAcquisitionFailureKind.DeviceFault,
                        "VirtualCameraAcquisitionPlanExhausted");
                else
                {
                    var accepted = _clock.GetTimePoint();
                    pending = new PendingAcquisition(request, plan)
                    {
                        Controlled = true,
                        Control = control,
                        AcceptedTimePoint = accepted,
                        StartTimePoint = accepted,
                        CancellationToken = cancellationToken
                    };

                    // Register the complete script while the exact pending request
                    // is installed.  Runtime may open Busy and advance the clock
                    // immediately; waiting for the Busy continuation to register
                    // these callbacks would let a same-timestamp deadline win by
                    // scheduling the observation too late.
                    try
                    {
                        ScheduleControlledSignalsLocked(pending);
                    }
                    catch (Exception exception) when (exception is ArgumentException or
                        InvalidOperationException or OverflowException)
                    {
                        pending.Completed = true;
                        pending.IgnoreLateSignals = true;
                        CloseLocked(new CameraFault(CameraFaultClassification.DeviceFault,
                            "VirtualCameraAcquisitionScheduleFailed"));
                        _provider.CountInfrastructureFailure(_session);
                        failureHandles = pending.Handles.ToArray();
                        pending.Completion.TrySetResult(Failure(
                            CameraAcquisitionFailureKind.DeviceFault,
                            "VirtualCameraAcquisitionScheduleFailed"));
                        immediateFailure = pending.Completion.Task.Result;
                        pending = null;
                    }

                    if (pending is not null)
                    {
                        _pendingAcquisition = pending;
                        _acquisition = CameraAcquisitionState.WaitingForFrame;

                        // The adapter acknowledges only after the exact request is
                        // installed.  Script callbacks are already registered, but
                        // each callback below requires the matching Busy state before
                        // it can publish a frame.
                        if (!control.AcknowledgePending(request, accepted))
                        {
                            var controlClosed = control.IsClosed;
                            if (!controlClosed)
                                RecordProtocolLocked(CameraProtocolViolationKind.CorrelationMismatch,
                                    "VirtualCameraControlPendingRejected", request.Correlation);
                            failureHandles = CompleteAcquisitionLocked(pending,
                                Failure(controlClosed
                                    ? CameraAcquisitionFailureKind.Cancelled
                                    : CameraAcquisitionFailureKind.ProtocolViolation,
                                    controlClosed
                                        ? "VirtualCameraControlClosed"
                                        : "VirtualCameraControlPendingRejected"), false, true);
                            immediateFailure = pending.Completion.Task.IsCompletedSuccessfully
                                ? pending.Completion.Task.Result : null;
                        }
                    }
                }
            }
        }

        if (immediateFailure is not null)
        {
            DisposeHandles(failureHandles);
            return ValueTask.FromResult(immediateFailure);
        }

        // The registration belongs to the pending operation, rather than to
        // this method's stack.  It therefore remains active through the Busy
        // wait and is disposed exactly once after the completion TCS is set.
        var registration = cancellationToken.Register(static state =>
        {
            var tuple = ((VirtualCameraDevice Device, PendingAcquisition Pending))state!;
            tuple.Device.CancelAcquisition(tuple.Pending);
        }, (this, pending!));
        var disposeRegistration = false;
        lock (_gate)
        {
            if (pending!.Completed)
                disposeRegistration = true;
            else
                pending.CancellationRegistration = registration;
        }
        if (disposeRegistration)
            registration.Dispose();
        AttachCancellationCleanup(pending!);

        _ = AwaitControlledBusyAsync(pending!);

        // Do not wrap the pending task in an async state machine.  It is the
        // sole frame ownership/completion channel for this acquisition.
        return new ValueTask<FrameAcquisitionResult>(pending!.Completion.Task);
    }

    public CameraProtocolSnapshot ReadProtocolObservations(long afterSequence,
        int maximumCount = MaximumProtocolObservations)
    {
        if (afterSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (maximumCount is < 1 or > MaximumProtocolObservations)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));

        lock (_gate)
        {
            var through = _protocolSequence;
            var first = _protocolObservations.Count == 0
                ? 1
                : _protocolObservations.Peek().Sequence;
            var overflowed = afterSequence < first - 1;
            var observations = _protocolObservations
                .Where(item => item.Sequence > afterSequence)
                .Take(maximumCount)
                .ToArray();
            return new CameraProtocolSnapshot(_protocolEpoch, first, through,
                overflowed, observations);
        }
    }

    internal VirtualHardwareTriggerResult TryPulseHardwareTrigger(
        ExecutionCorrelationId expectedCorrelation)
    {
        ArgumentNullException.ThrowIfNull(expectedCorrelation);
        lock (_gate)
        {
            var pending = _pendingAcquisition;
            if (_disposed || _connection != CameraConnectionState.Open)
                return VirtualHardwareTriggerResult.Failure("VirtualCameraPulseDeviceNotOpen");
            if (pending is null || pending.Completed)
            {
                var reason = pending?.Completed == true || _retiredAcquisitions.Count != 0
                    ? "VirtualCameraPulseAfterTerminal"
                    : "VirtualCameraPulseWithoutPending";
                RecordProtocolLocked(CameraProtocolViolationKind.EarlyHardwarePulse, reason);
                return VirtualHardwareTriggerResult.Failure(reason);
            }
            if (pending.Control is not { } control)
            {
                RecordProtocolLocked(CameraProtocolViolationKind.EarlyHardwarePulse,
                    "VirtualCameraPulseControlRequired", pending.Request.Correlation);
                return VirtualHardwareTriggerResult.Failure("VirtualCameraPulseControlRequired");
            }
            if (!Equals(pending.Request.Correlation, expectedCorrelation))
            {
                RecordProtocolLocked(CameraProtocolViolationKind.CorrelationMismatch,
                    "VirtualCameraPulseCorrelationMismatch", pending.Request.Correlation);
                return VirtualHardwareTriggerResult.Failure("VirtualCameraPulseCorrelationMismatch");
            }
            if (_effective?.ProductionAcquisitionMode != ProductionAcquisitionMode.HardwareTrigger)
            {
                RecordProtocolLocked(CameraProtocolViolationKind.EarlyHardwarePulse,
                    "VirtualCameraPulseModeUnsupported", pending.Request.Correlation);
                return VirtualHardwareTriggerResult.Failure("VirtualCameraPulseModeUnsupported");
            }
            if (!control.IsBusy || control.Start is not { } start)
            {
                RecordProtocolLocked(CameraProtocolViolationKind.EarlyHardwarePulse,
                    "VirtualCameraPulseBeforeBusy", pending.Request.Correlation);
                return VirtualHardwareTriggerResult.Failure("VirtualCameraPulseBeforeBusy");
            }
            if (start.BusyAt.MonotonicTimestamp != pending.AcceptedTimePoint.MonotonicTimestamp ||
                start.MonotonicFrequency != VirtualCameraClock.Frequency)
            {
                RecordProtocolLocked(CameraProtocolViolationKind.CorrelationMismatch,
                    "VirtualCameraBusyTimestampMismatch", pending.Request.Correlation);
                SetFaultLocked(CameraFaultClassification.ProtocolViolation,
                    "VirtualCameraBusyTimestampMismatch");
                return VirtualHardwareTriggerResult.Failure("VirtualCameraBusyTimestampMismatch");
            }
            if (pending.HardwarePulseGranted)
            {
                RecordProtocolLocked(CameraProtocolViolationKind.DuplicateHardwarePulse,
                    "VirtualCameraPulseDuplicate", pending.Request.Correlation);
                return VirtualHardwareTriggerResult.Failure("VirtualCameraPulseDuplicate");
            }

            pending.HardwarePulseGranted = true;
            return VirtualHardwareTriggerResult.Success();
        }
    }

    private async Task AwaitControlledBusyAsync(PendingAcquisition pending)
    {
        try
        {
            var start = await pending.Control!.WaitForBusyAsync(pending.CancellationToken)
                .ConfigureAwait(false);
            IDisposable[] handles = Array.Empty<IDisposable>();
            lock (_gate)
            {
                if (!ReferenceEquals(_pendingAcquisition, pending) || pending.Completed)
                    return;

                if (!pending.Control.IsBusy)
                {
                    var preserveLateSignals = pending.Control.Start is not null;
                    handles = CompleteAcquisitionLocked(pending, Failure(
                        CameraAcquisitionFailureKind.Cancelled, "VirtualCameraControlClosed"),
                        false, !preserveLateSignals);
                }
                else if (pending.AcceptedTimePoint.MonotonicTimestamp !=
                    start.BusyAt.MonotonicTimestamp ||
                    start.MonotonicFrequency != VirtualCameraClock.Frequency)
                {
                    RecordProtocolLocked(CameraProtocolViolationKind.CorrelationMismatch,
                        "VirtualCameraBusyTimestampMismatch", pending.Request.Correlation);
                    SetFaultLocked(CameraFaultClassification.ProtocolViolation,
                        "VirtualCameraBusyTimestampMismatch");
                    handles = CompleteAcquisitionLocked(pending, Failure(
                        CameraAcquisitionFailureKind.ProtocolViolation,
                        "VirtualCameraBusyTimestampMismatch"), false, true);
                }
                else
                {
                    pending.StartTimePoint = start.BusyAt;
                }
            }
            DisposeHandles(handles);
        }
        catch (OperationCanceledException)
        {
            CompleteControlledBusyCancellation(pending);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            CompleteControlledBusyFailure(pending, CameraAcquisitionFailureKind.DeviceFault,
                "VirtualCameraControlUnavailable", CameraProtocolViolationKind.CorrelationMismatch);
        }
    }

    private void CompleteControlledBusyCancellation(PendingAcquisition pending)
    {
        IDisposable[] handles;
        lock (_gate)
        {
            if (!ReferenceEquals(_pendingAcquisition, pending) || pending.Completed)
                return;
            // Once Busy has been accepted, keep the retired script callbacks so
            // a frame that arrives after the control closes is recorded as a
            // late frame tied to this old pending request.  Before Busy there
            // is no physical frame to observe, so cancellation can retire the
            // whole schedule without protocol evidence.
            var preserveLateSignals = pending.Control?.Start is not null;
            handles = CompleteAcquisitionLocked(pending, Failure(
                CameraAcquisitionFailureKind.Cancelled, "VirtualCameraControlClosed"),
                false, !preserveLateSignals);
        }
        DisposeHandles(handles);
    }

    private void ScheduleControlledSignalsLocked(PendingAcquisition pending)
    {
        var accepted = pending.AcceptedTimePoint.MonotonicTimestamp;
        foreach (var batch in pending.Plan.Signals.GroupBy(signal => signal.Offset.Ticks)
                     .OrderBy(group => group.Key))
        {
            var signals = batch.ToArray();
            var due = checked(accepted + batch.Key);
            pending.Handles.Add(_clock.Schedule(due,
                FrameAcquisitionClockPhase.FrameObservation,
                () => OnAcquisitionSignalBatch(pending, signals)));
        }

        var timeoutTicks = checked((long)_effective!.AcquisitionTimeoutMs *
            TimeSpan.TicksPerMillisecond);
        pending.Handles.Add(_clock.Schedule(checked(accepted + timeoutTicks),
            FrameAcquisitionClockPhase.Deadline,
            () => OnAcquisitionTimeout(pending)));
    }

    private void CompleteControlledBusyFailure(PendingAcquisition pending,
        CameraAcquisitionFailureKind kind, string reason, CameraProtocolViolationKind protocolKind)
    {
        IDisposable[] handles = Array.Empty<IDisposable>();
        lock (_gate)
        {
            if (!ReferenceEquals(_pendingAcquisition, pending) || pending.Completed)
                return;
            RecordProtocolLocked(protocolKind, reason, pending.Request.Correlation);
            handles = CompleteAcquisitionLocked(pending, Failure(kind, reason), false, true);
        }
        DisposeHandles(handles);
    }

    private static void AttachCancellationCleanup(PendingAcquisition pending)
    {
        _ = pending.Completion.Task.ContinueWith(static (_, state) =>
        {
            var operation = (PendingAcquisition)state!;
            var registration = Interlocked.Exchange(ref operation.CancellationRegistration, null);
            registration?.Dispose();
        }, pending, CancellationToken.None, TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void RecordProtocolLocked(CameraProtocolViolationKind kind, string reason,
        ExecutionCorrelationId? correlation = null, int droppedFrames = 0)
    {
        if (_protocolSequence == long.MaxValue) return;
        var sequence = ++_protocolSequence;
        _protocolObservations.Enqueue(new CameraProtocolObservation(sequence, kind, reason,
            _clock.GetTimePoint(), correlation, droppedFrames));
        while (_protocolObservations.Count > MaximumProtocolObservations)
            _protocolObservations.Dequeue();
    }
}
