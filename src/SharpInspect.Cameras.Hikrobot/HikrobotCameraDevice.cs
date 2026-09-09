using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Frames;

namespace SharpInspect.Cameras.Hikrobot;

/// <summary>
/// Managed owner of one opened Hikrobot device.  The native handle is supplied by
/// the adapter-private runtime and is never exposed to Runtime or a consumer.
/// </summary>
internal sealed class HikrobotCameraDevice : IControlledCameraDevice
{
    private const string SdkId = "Hikrobot.MVS";
    private const string NormalDisposeReason = "HikrobotDisposed";
    private const int MaximumProtocolObservations = 64;

    // This is a logical reservation in addition to the serialized SDK chain.
    // It rejects a competing lifecycle request while the first request is still
    // in vendor code or publishing its read-back state; it must never queue a
    // second Apply/Start/Stop behind the first one.
    private enum LifecycleOperation
    {
        None,
        Apply,
        Start,
        Stop
    }

    // Internal scheduling points used only by the adapter tests to hold the
    // callback worker at the candidate/finalization boundary.  They do not
    // participate in the production protocol.
    internal enum AcquisitionTestProbePoint
    {
        CallbackCandidatePublished,
        CallbackFinalizationAttempted,
        TriggerFailureBeforeSignal
    }

    private const string LifecycleOperationBusyReason = "HikrobotCameraOperationBusy";
    private const string SessionConsumedReason = "HikrobotSingleFrameSessionConsumed";

    private readonly IHikrobotSdkDevice _sdk;
    private readonly CameraProviderIdentity _identity;
    private readonly string _runtimeVersion;
    private readonly IFrameAcquisitionClock _clock;
    private readonly CameraCapabilities _capabilities;
    private readonly HikrobotSdkDescriptor _sdkDescriptor;
    private readonly FrameBufferPool _pool;
    private readonly byte[] _rawScratch;
    private readonly HikrobotProtocolRing _protocol;
    private readonly CameraDeviceDescriptor _descriptor;
    private readonly long _callbackBudgetTicks;
    private readonly object _sync = new();
    private readonly object _sdkSequenceSync = new();
    private Action? _finalizationProbe;
    private Action<AcquisitionTestProbePoint>? _acquisitionTestProbe;

    // Every SDK call is appended to this chain.  A completed predecessor, including
    // a faulted predecessor, must settle before the next call is allowed to enter
    // vendor code.  The chain is never cancelled by a caller-facing token.
    private Task _sdkTail = Task.CompletedTask;

    private PendingAcquisition? _pending;
    private ExecutionCorrelationId? _lastRetiredCorrelation;
    private Task? _rawTask;
    private Task? _pendingFinalizerTask;
    private Task? _pendingFinalizerWorkerTask;
    private Task? _disposeTask;
    private CameraConnectionState _connection = CameraConnectionState.Open;
    private CameraConfigurationState _configuration = CameraConfigurationState.Unconfigured;
    private CameraAcquisitionState _acquisition = CameraAcquisitionState.Stopped;
    private CameraProviderAvailability _providerAvailability = CameraProviderAvailability.Available;
    private CameraFault? _lastFault;
    private EffectiveCameraConfiguration? _effective;
    private Action<HikrobotNativeFrame>? _callback;
    private bool _started;
    private bool _retiring;
    private bool _disposed;
    private bool _sdkDisposed;
    private bool _retirementCompleted;
    private bool _rawBusy;
    private bool _sessionConsumed;
    private LifecycleOperation _lifecycleOperation;
    private Action? _pendingWorkerGate;

    /// <summary>Internal test seam for proving finalization leaves the SDK callback stack.</summary>
    internal Action? FinalizationProbe
    {
        get => _finalizationProbe;
        set => _finalizationProbe = value;
    }

    /// <summary>Internal test seam for pausing the worker before its Busy wait.</summary>
    internal Action? PendingWorkerGate
    {
        get => _pendingWorkerGate;
        set => _pendingWorkerGate = value;
    }

    /// <summary>Internal scheduling seam for trigger/callback precedence tests.</summary>
    internal Action<AcquisitionTestProbePoint>? AcquisitionTestProbe
    {
        get => _acquisitionTestProbe;
        set => _acquisitionTestProbe = value;
    }

    internal HikrobotCameraDevice(IHikrobotSdkDevice sdk,
        CameraProviderIdentity identity, string runtimeVersion,
        IFrameAcquisitionClock clock, FrameBufferPoolOptions poolOptions)
    {
        _sdk = sdk ?? throw new ArgumentNullException(nameof(sdk));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _runtimeVersion = RequireVersion(runtimeVersion);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (_clock.Frequency is < 1 or > 10_000_000_000)
            throw new ArgumentOutOfRangeException(nameof(clock));
        ArgumentNullException.ThrowIfNull(poolOptions);
        var callbackBudgetTicks = poolOptions.CallbackBudget.TotalSeconds * Stopwatch.Frequency;
        _callbackBudgetTicks = callbackBudgetTicks >= long.MaxValue
            ? long.MaxValue
            : Math.Max(1L, checked((long)Math.Ceiling(callbackBudgetTicks)));

        _sdkDescriptor = sdk.Descriptor ?? throw new ArgumentException(
            "HikrobotSdkDescriptorUnavailable", nameof(sdk));
        _capabilities = sdk.Capabilities ?? throw new ArgumentException(
            "HikrobotSdkCapabilitiesUnavailable", nameof(sdk));
        _descriptor = new CameraDeviceDescriptor(_identity,
            _sdkDescriptor.StableIdentity,
            "Hikrobot Camera " + _sdkDescriptor.StableIdentity,
            _sdkDescriptor.Model);

        // The pool and its one raw scratch slot are the only pixel allocations made
        // by this owner.  They are acquired before any callback can be registered.
        _pool = new FrameBufferPool(poolOptions);
        _rawScratch = GC.AllocateUninitializedArray<byte>(poolOptions.MaximumFrameBytes,
            pinned: true);
        _rawScratch.AsSpan().Clear();
        _protocol = new HikrobotProtocolRing(_clock);
    }

    public CameraDeviceDescriptor Descriptor => _descriptor;
    public CameraCapabilities Capabilities => _capabilities;

    /// <summary>
    /// Indicates that the physical owner has completed raw work and the SDK
    /// Stop-to-Dispose sequence successfully.  A closed health state alone is
    /// insufficient evidence that a provider slot can be reopened.
    /// </summary>
    internal bool RetirementCompleted
    {
        get { lock (_sync) return _retirementCompleted; }
    }

    public CameraHealthSnapshot GetHealthSnapshot()
    {
        lock (_sync)
        {
            return new CameraHealthSnapshot(_providerAvailability, _connection,
                _configuration, _acquisition, SafeTimePoint(), _lastFault);
        }
    }

    public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
        RequestedCameraConfiguration requested,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(CameraConfigurationResult.Failure(
                "HikrobotConfigurationCancelled"));

        var operation = ApplyConfigurationCoreAsync(requested);
        var registration = RegisterRetirementOnCancellation(cancellationToken,
            "HikrobotConfigurationCancelled", CameraFaultClassification.ConfigurationRejected);
        return new ValueTask<CameraConfigurationResult>(
           ObserveConfigurationAsync(operation, cancellationToken, registration));
    }

    public ValueTask<CameraOperationResult> StartAsync(
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(CameraOperationResult.Failure(
                "HikrobotStartCancelled"));

        var operation = StartCoreAsync();
        var registration = RegisterRetirementOnCancellation(cancellationToken,
            "HikrobotStartCancelled", CameraFaultClassification.ConnectionLost);
        return new ValueTask<CameraOperationResult>(
            ObserveOperationAsync(operation, cancellationToken, registration,
                "HikrobotStartCancelled"));
    }

    /// <summary>
    /// The unqualified interface path is intentionally unusable.  Runtime must
    /// supply its exact pending control so no production correlation can enter the
    /// adapter accidentally.
    /// </summary>
    public ValueTask<FrameAcquisitionResult> AcquireAsync(
        FrameAcquisitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(Failure(CameraAcquisitionFailureKind.ProtocolViolation,
            "HikrobotControlledAcquireRequired"));
    }

    public ValueTask<FrameAcquisitionResult> AcquireAsync(
        FrameAcquisitionRequest request, FrameAcquisitionControl control,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(control);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(Failure(CameraAcquisitionFailureKind.Cancelled,
                "HikrobotAcquisitionCancelled"));
        if (request.Correlation.Kind == ExecutionKind.Production)
            return ValueTask.FromResult(Failure(CameraAcquisitionFailureKind.ProtocolViolation,
                "ProductionAcquisitionUnavailable"));
        if (!ReferenceEquals(control.Request, request) || !ReferenceEquals(control.Clock, _clock))
            return ValueTask.FromResult(Failure(CameraAcquisitionFailureKind.ProtocolViolation,
                "HikrobotControlMismatch"));

        PendingAcquisition? pending;
        lock (_sync)
        {
            if (_disposed || _retiring)
                return ValueTask.FromResult(Failure(CameraAcquisitionFailureKind.Disconnected,
                    "HikrobotDeviceNotOpen"));
            if (_sessionConsumed && _pending is null)
                return ValueTask.FromResult(Failure(CameraAcquisitionFailureKind.NotStarted,
                    SessionConsumedReason));
            if (_connection != CameraConnectionState.Open)
                return ValueTask.FromResult(Failure(CameraAcquisitionFailureKind.Disconnected,
                    "HikrobotDeviceNotOpen"));
            if (_lifecycleOperation != LifecycleOperation.None)
                return ValueTask.FromResult(Failure(CameraAcquisitionFailureKind.AlreadyPending,
                    LifecycleOperationBusyReason));
            if (_configuration != CameraConfigurationState.Applied || _effective is null)
                return ValueTask.FromResult(Failure(CameraAcquisitionFailureKind.NotConfigured,
                    "HikrobotCameraNotConfigured"));
            if (_acquisition != CameraAcquisitionState.Armed)
                return ValueTask.FromResult(Failure(CameraAcquisitionFailureKind.NotStarted,
                    "HikrobotCameraNotStarted"));
            if (_pending is not null || _rawBusy)
                return ValueTask.FromResult(Failure(CameraAcquisitionFailureKind.AlreadyPending,
                    "HikrobotAcquisitionAlreadyPending"));
            if (_pendingFinalizerTask is { IsCompleted: true } &&
                (_pendingFinalizerWorkerTask is null ||
                 _pendingFinalizerWorkerTask.IsCompleted))
            {
                _pendingFinalizerTask = null;
                _pendingFinalizerWorkerTask = null;
            }
            if (_pendingFinalizerTask is not null ||
                _pendingFinalizerWorkerTask is not null)
                return ValueTask.FromResult(Failure(CameraAcquisitionFailureKind.AlreadyPending,
                    "HikrobotAcquisitionFinalizerPending"));
            if (control.IsClosed)
                return ValueTask.FromResult(Failure(CameraAcquisitionFailureKind.Cancelled,
                    "HikrobotControlClosed"));

            pending = new PendingAcquisition(request, control, _effective,
                cancellationToken);
            // This unqualified single-frame candidate never reuses a native
            // callback session for a later request. Vendor buffer flushing and
            // repeated acquisition require separate hardware qualification.
            _sessionConsumed = true;
            _pending = pending;
            _pendingFinalizerTask = pending.FinalizerTask;
            _pendingFinalizerWorkerTask = pending.FinalizerWorkerCompletion.Task;
            _acquisition = CameraAcquisitionState.WaitingForFrame;
            // Hardware has no software-command task to drain. Cancellation still
            // requires physical retirement before its result can be finalized.
            pending.TriggerSettled = _effective.ProductionAcquisitionMode ==
                ProductionAcquisitionMode.HardwareTrigger;
        }

        if (!control.AcknowledgePending(request))
        {
            SignalPendingFailure(pending, Failure(CameraAcquisitionFailureKind.Cancelled,
                control.IsClosed ? "HikrobotControlClosed" : "HikrobotPendingRejected"),
                settleTrigger: true);
            // The pending was installed before the Runtime acknowledgement.  A
            // closed control means its lifetime has already crossed the physical
            // ownership boundary, so retire this device as well; clearing this
            // managed record and reusing the callback owner could admit a late
            // hardware pulse under a new correlation.
            _ = BeginRetirement("HikrobotPendingRejected",
                CameraFaultClassification.ConnectionLost);
            return new ValueTask<FrameAcquisitionResult>(pending.Completion.Task);
        }

        var cancellationRegistration = cancellationToken.Register(static state =>
        {
            var value = (PendingCancellationState)state!;
            value.Device.CancelPending(value.Pending);
        }, new PendingCancellationState(this, pending));

        var disposeRegistration = false;
        lock (_sync)
        {
            if (pending.Completed)
                disposeRegistration = true;
            else
                pending.CancellationRegistration = cancellationRegistration;
        }
        if (disposeRegistration)
            cancellationRegistration.Dispose();

        _ = Task.Run(() => RunPendingAsync(pending));
        return new ValueTask<FrameAcquisitionResult>(pending.Completion.Task);
    }

    public ValueTask<CameraOperationResult> StopAsync(
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(CameraOperationResult.Failure(
                "HikrobotStopCancelled"));

        var operation = StopCoreAsync();
        var registration = RegisterRetirementOnCancellation(cancellationToken,
            "HikrobotStopCancelled", CameraFaultClassification.ConnectionLost);
        return new ValueTask<CameraOperationResult>(
            ObserveOperationAsync(operation, cancellationToken, registration,
                "HikrobotStopCancelled"));
    }

    public ValueTask DisposeAsync()
    {
        var disposal = BeginRetirement(NormalDisposeReason,
            CameraFaultClassification.ConnectionLost, normalDispose: true);
        return new ValueTask(disposal);
    }

    public CameraProtocolSnapshot ReadProtocolObservations(long afterSequence,
        int maximumCount = MaximumProtocolObservations) =>
        _protocol.Read(afterSequence, maximumCount);

    private async Task<CameraConfigurationResult> ApplyConfigurationCoreAsync(
        RequestedCameraConfiguration requested)
    {
        CameraConfigurationResult validation;
        try
        {
            validation = _capabilities.ValidateConfiguration(requested);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CameraConfigurationResult.Failure("HikrobotConfigurationValidationFailed");
        }

        if (!validation.Succeeded || validation.Effective is null)
            return validation;

        lock (_sync)
        {
            if (_disposed || _retiring)
                return CameraConfigurationResult.Failure("HikrobotDeviceDisposed");
            if (_connection != CameraConnectionState.Open)
                return CameraConfigurationResult.Failure("HikrobotDeviceNotOpen");
            if (_lifecycleOperation != LifecycleOperation.None ||
                _configuration == CameraConfigurationState.Applying)
                return CameraConfigurationResult.Failure(LifecycleOperationBusyReason);
            if (_pending is not null || _rawBusy || _acquisition != CameraAcquisitionState.Stopped)
                return CameraConfigurationResult.Failure("HikrobotConfigurationBusy");
            _lifecycleOperation = LifecycleOperation.Apply;
            _configuration = CameraConfigurationState.Applying;
            _effective = null;
        }

        EffectiveCameraConfiguration? reported = null;
        try
        {
            // The SDK seam returns the complete effective read-back.  It has no
            // separate differences channel; differences are deterministic from
            // the capability validation and are supplied only to the common
            // read-back validator, never treated as vendor evidence.
            reported = await EnqueueSdk(() => _sdk.Apply(requested))
                .ConfigureAwait(false);
            if (reported is null)
            {
                _ = BeginRetirement("HikrobotConfigurationReadBackMissing",
                    CameraFaultClassification.ConfigurationRejected);
                return CameraConfigurationResult.Failure(
                    "HikrobotConfigurationReadBackMissing");
            }

            var readBack = _capabilities.ValidateReadBack(requested,
                CameraConfigurationResult.Success(reported, validation.Differences));
            if (!readBack.Succeeded || readBack.Effective is null)
            {
                _ = BeginRetirement(readBack.ReasonCode,
                    CameraFaultClassification.ConfigurationRejected);
                return CameraConfigurationResult.Failure(readBack.ReasonCode);
            }

            lock (_sync)
            {
                if (_disposed || _retiring)
                    return CameraConfigurationResult.Failure("HikrobotDeviceDisposed");
                _effective = readBack.Effective;
                _configuration = CameraConfigurationState.Applied;
                _acquisition = CameraAcquisitionState.Stopped;
                _lifecycleOperation = LifecycleOperation.None;
            }
            return readBack;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var reason = SdkReason(exception, "HikrobotConfigurationApplyFailed");
            _ = BeginRetirement(reason, CameraFaultClassification.ConfigurationRejected);
            return CameraConfigurationResult.Failure(reason);
        }
    }

    private async Task<CameraOperationResult> StartCoreAsync()
    {
        lock (_sync)
        {
            if (_disposed || _retiring)
                return CameraOperationResult.Failure("HikrobotDeviceDisposed");
            if (_sessionConsumed)
                return CameraOperationResult.Failure(SessionConsumedReason);
            if (_connection != CameraConnectionState.Open)
                return CameraOperationResult.Failure("HikrobotDeviceNotOpen");
            if (_lifecycleOperation != LifecycleOperation.None)
                return CameraOperationResult.Failure(LifecycleOperationBusyReason);
            if (_configuration != CameraConfigurationState.Applied || _effective is null)
                return CameraOperationResult.Failure("HikrobotCameraNotConfigured");
            if (_pending is not null || _rawBusy)
                return CameraOperationResult.Failure("HikrobotAcquisitionPending");
            if (_started)
                return CameraOperationResult.Success("HikrobotCameraAlreadyStarted");

            _lifecycleOperation = LifecycleOperation.Start;
            _callback = OnNativeFrame;
        }

        try
        {
            await EnqueueSdk(() => _sdk.Start(_callback!)).ConfigureAwait(false);
            lock (_sync)
            {
                if (_disposed || _retiring)
                    return CameraOperationResult.Failure("HikrobotDeviceDisposed");
                _started = true;
                _acquisition = CameraAcquisitionState.Armed;
                _lifecycleOperation = LifecycleOperation.None;
            }
            return CameraOperationResult.Success("HikrobotCameraStarted");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var reason = SdkReason(exception, "HikrobotStartFailed");
            _ = BeginRetirement(reason, CameraFaultClassification.ConnectionLost);
            return CameraOperationResult.Failure(reason);
        }
    }

    private async Task<CameraOperationResult> StopCoreAsync()
    {
        Task? retirement = null;
        lock (_sync)
        {
            if (_retirementCompleted)
                return CameraOperationResult.Success("HikrobotCameraAlreadyStopped");
            if (_disposed)
                return CameraOperationResult.Failure("HikrobotDeviceDisposed");
            if (_retiring)
                return CameraOperationResult.Failure("HikrobotDeviceRetiring");
            if (_lifecycleOperation != LifecycleOperation.None)
                return CameraOperationResult.Failure(LifecycleOperationBusyReason);
            if (_pending is not null || _callback is not null)
            {
                // Once callbacks were registered, even an idle Stop consumes the
                // physical session. Stop/Start on the same handle is not proof
                // that queued old SDK callbacks have been flushed.
                _sessionConsumed = true;
                retirement = BeginRetirement("HikrobotCameraStopped",
                    CameraFaultClassification.ConnectionLost);
            }
            else
            {
                _lifecycleOperation = LifecycleOperation.Stop;
                _acquisition = CameraAcquisitionState.Stopped;
            }
        }

        if (retirement is not null)
        {
            await retirement.ConfigureAwait(false);
            lock (_sync)
                return _retirementCompleted
                    ? CameraOperationResult.Success("HikrobotCameraStopped")
                    : CameraOperationResult.Failure("HikrobotDeviceRetirementIncomplete");
        }

        try
        {
            await EnqueueSdk(() => _sdk.Stop()).ConfigureAwait(false);
            await AwaitRawQuiescenceAsync().ConfigureAwait(false);
            lock (_sync)
            {
                if (_disposed || _retiring)
                    return CameraOperationResult.Failure("HikrobotDeviceRetiring");
                _started = false;
                _lifecycleOperation = LifecycleOperation.None;
            }
            return CameraOperationResult.Success("HikrobotCameraStopped");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var reason = SdkReason(exception, "HikrobotStopFailed");
            _ = BeginRetirement(reason, CameraFaultClassification.ConnectionLost);
            return CameraOperationResult.Failure(reason);
        }
    }

    private async Task RunPendingAsync(PendingAcquisition pending)
    {
        try
        {
            _pendingWorkerGate?.Invoke();
            try
            {
                await pending.Control.WaitForBusyAsync(
                    pending.WaitCancellationSource.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                SignalPendingFailure(pending, Failure(CameraAcquisitionFailureKind.Cancelled,
                    "HikrobotControlClosed"), settleTrigger: true);
                TryFinalizePending(pending);
                return;
            }

            var trigger = false;
            bool cancelled;
            bool busy;
            lock (_sync)
            {
                if (!ReferenceEquals(_pending, pending) || pending.Completed)
                    return;
                cancelled = pending.CancelRequested;
                busy = pending.Control.IsBusy;
                if (!cancelled && busy)
                {
                    if (pending.Effective.ProductionAcquisitionMode ==
                        ProductionAcquisitionMode.SoftwareTrigger && !pending.FrameSignalled)
                    {
                        pending.TriggerSettled = false;
                        trigger = true;
                    }
                }
            }

            if (cancelled || !busy)
            {
                SignalPendingFailure(pending, Failure(CameraAcquisitionFailureKind.Cancelled,
                    "HikrobotControlClosed"), settleTrigger: true);
                TryFinalizePending(pending);
                return;
            }

            if (trigger)
            {
                Task? triggerTask = null;
                lock (_sync)
                {
                    // Keep the retirement check and task-chain publication in one
                    // lock.  If Dispose wins first, it cannot enqueue Stop/Dispose
                    // ahead of a trigger which the cancelled request would never
                    // observe.
                    if (!pending.CancelRequested && !_retiring &&
                        ReferenceEquals(_pending, pending) && !pending.Completed &&
                        pending.Control.IsBusy)
                    {
                        // Record this before entering the SDK task.  A synchronous
                        // vendor callback may arrive while TriggerSoftware is still
                        // on its stack; the milestone order remains valid without
                        // executing any SDK call from that callback.
                        pending.TriggerAccepted = SafeTimePoint();
                        triggerTask = EnqueueSdk(() => _sdk.TriggerSoftware());
                    }
                }
                if (triggerTask is null)
                {
                    SignalPendingFailure(pending, Failure(
                        CameraAcquisitionFailureKind.Cancelled, "HikrobotControlClosed"),
                        settleTrigger: true);
                    TryFinalizePending(pending);
                    return;
                }
                try
                {
                    await triggerTask.ConfigureAwait(false);
                    lock (_sync) pending.TriggerSettled = true;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    var reason = SdkReason(exception, "HikrobotTriggerFailed");
                    InvokeAcquisitionTestProbe(
                        AcquisitionTestProbePoint.TriggerFailureBeforeSignal);
                    SignalPendingFailure(pending, Failure(
                        CameraAcquisitionFailureKind.DeviceFault, reason),
                        settleTrigger: true);
                    _ = BeginRetirement(reason, CameraFaultClassification.AcquisitionFailed);
                    TryFinalizePending(pending);
                    return;
                }
            }

            var frameSignalled = false;
            lock (_sync) frameSignalled = pending.FrameSignalled;
            if (!frameSignalled)
                await pending.FrameReady.Task.ConfigureAwait(false);
            TryFinalizePending(pending);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var reason = SdkReason(exception, "HikrobotAcquisitionFailed");
            SignalPendingFailure(pending, Failure(CameraAcquisitionFailureKind.DeviceFault,
                reason), settleTrigger: true);
            TryFinalizePending(pending);
        }
    }

    private void OnNativeFrame(HikrobotNativeFrame frame)
    {
        // This method is the native callback boundary.  No SDK method, clock
        // scheduling, Runtime object, consumer, or synchronous continuation is
        // invoked from it.  All managed exceptions are contained before returning.
        try
        {
            PendingAcquisition? pending;
            bool busyAtReceipt;
            FrameTimePoint receivedAt;
            lock (_sync)
            {
                if (_disposed || _sdkDisposed)
                    return;
                pending = _pending;
                if (pending is null)
                {
                    RecordUnmatchedFrameLocked();
                    return;
                }
                if (pending.Completed || pending.CancelRequested || _retiring ||
                    !ReferenceEquals(_pending, pending))
                {
                    RecordProtocolLocked(CameraProtocolViolationKind.LateFrame,
                        "HikrobotLateFrameDropped", pending.Request.Correlation);
                    return;
                }
                if (pending.FrameSignalled || pending.RawInFlight || _rawBusy)
                {
                    RecordProtocolLocked(CameraProtocolViolationKind.ExtraFrame,
                        "HikrobotExtraFrameDropped", pending.Request.Correlation);
                    return;
                }

                busyAtReceipt = pending.Control.IsBusy;
                receivedAt = SafeTimePoint();
                if (!busyAtReceipt)
                {
                    var controlClosed = pending.Control.IsClosed || pending.CancelRequested;
                    RecordProtocolLocked(controlClosed
                            ? CameraProtocolViolationKind.LateFrame
                            : CameraProtocolViolationKind.EarlyFrame,
                        controlClosed ? "HikrobotLateFrameDropped" : "HikrobotFrameBeforeBusy",
                        pending.Request.Correlation);
                    pending.RawInFlight = false;
                    // A closed control precedes Runtime's asynchronous physical
                    // cancellation. The stack-external finalizer must initiate
                    // retirement even if no software command was dispatched.
                    // An in-flight command must also settle before completion.
                    if (pending.TriggerAccepted is null)
                        pending.TriggerSettled = true;
                    pending.FrameSignalled = true;
                    pending.Candidate = Failure(controlClosed
                            ? CameraAcquisitionFailureKind.Cancelled
                            : CameraAcquisitionFailureKind.ProtocolViolation,
                        controlClosed ? "HikrobotControlClosed" : "HikrobotFrameBeforeBusy");
                    pending.FrameReady.TrySetResult(true);
                    if (!controlClosed)
                        SetFaultLocked(CameraFaultClassification.ProtocolViolation,
                            "HikrobotFrameBeforeBusy");
                    // Finalization performs cancellation-registration disposal
                    // outside this lock below.
                }
                else
                {
                    pending.RawInFlight = true;
                    _rawBusy = true;
                }
            }

            if (!busyAtReceipt)
            {
                QueuePendingFinalizer(pending);
                return;
            }

            var rawCopyStarted = Stopwatch.GetTimestamp();
            var rawCopied = HikrobotPixelNormalizer.TryCopyRaw(frame, _rawScratch,
                receivedAt, busyAtReceipt, out var raw, out var copyReason);
            if (CallbackBudgetExceeded(rawCopyStarted))
            {
                lock (_sync)
                {
                    pending.RawInFlight = false;
                    _rawBusy = false;
                }
                SignalPendingFailure(pending, Failure(
                    CameraAcquisitionFailureKind.BufferUnavailable,
                    "HikrobotCallbackBudgetExceeded"),
                    protocolKind: CameraProtocolViolationKind.InvalidFrame,
                    recordFault: CameraFaultClassification.BufferFault);
                // Retirement and finalization are queued here; the callback never
                // enters SDK cleanup or disposes a registration on its own stack.
                QueuePendingFinalizer(pending, "HikrobotCallbackBudgetExceeded",
                    CameraFaultClassification.BufferFault);
                return;
            }

            if (!rawCopied)
            {
                lock (_sync)
                {
                    pending.RawInFlight = false;
                    _rawBusy = false;
                }
                SignalPendingFailure(pending, Failure(
                    CameraAcquisitionFailureKind.ProtocolViolation, copyReason),
                    protocolKind: CameraProtocolViolationKind.InvalidFrame,
                    recordFault: CameraFaultClassification.ProtocolViolation);
                QueuePendingFinalizer(pending);
                return;
            }

            // The bridge TCS prevents a very fast Task.Run completion from racing
            // the publication of _rawTask.  Its continuation cannot run inline on
            // the vendor callback stack.
            var handoff = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var task = Task.Run(async () =>
            {
                await handoff.Task.ConfigureAwait(false);
                ProcessRawFrame(pending, raw);
            });
            lock (_sync) _rawTask = task;
            handoff.TrySetResult(true);
        }
        catch (Exception)
        {
            // Never unwind into the native callback.  The exception text is not
            // returned through any public result or provenance field.
            try
            {
                PendingAcquisition? pending;
                lock (_sync) pending = _pending;
                if (pending is not null)
                {
                    lock (_sync)
                    {
                        pending.RawInFlight = false;
                        _rawBusy = false;
                    }
                    SignalPendingFailure(pending, Failure(
                        CameraAcquisitionFailureKind.DeviceFault,
                        "HikrobotCallbackBoundaryFailed"),
                        protocolKind: CameraProtocolViolationKind.InvalidFrame,
                        recordFault: CameraFaultClassification.DeviceFault);
                    QueuePendingFinalizer(pending);
                }
            }
            catch (Exception) { }
        }
    }

    private bool CallbackBudgetExceeded(long began) =>
        Stopwatch.GetTimestamp() - began >= _callbackBudgetTicks;

    private void ProcessRawFrame(PendingAcquisition pending,
        HikrobotRawFrame raw)
    {
        FrameBufferLease? lease = null;
        var releaseAsLate = false;
        FrameAcquisitionStart? start = null;
        try
        {
            lock (_sync)
            {
                var controlBusy = pending.Control.IsBusy;
                start = pending.Control.Start;
                if (!ReferenceEquals(_pending, pending) || pending.Completed ||
                    pending.CancelRequested || _retiring || !raw.BusyAtReceipt ||
                    !controlBusy || start is null)
                {
                    releaseAsLate = true;
                }
            }
            if (releaseAsLate)
            {
                RecordProtocol(CameraProtocolViolationKind.LateFrame,
                    "HikrobotLateFrameDropped", pending.Request.Correlation, raw.ReceivedAt);
                return;
            }

            if (!HikrobotPixelNormalizer.TryNormalize(_rawScratch, raw,
                pending.Effective, out var normalized, out var normalizeReason))
            {
                SignalPendingFailure(pending, Failure(
                    CameraAcquisitionFailureKind.ProtocolViolation, normalizeReason),
                    protocolKind: CameraProtocolViolationKind.InvalidFrame,
                    recordFault: CameraFaultClassification.ProtocolViolation);
                return;
            }

            var normalizedAt = SafeTimePoint();
            if (start is null)
            {
                SignalPendingFailure(pending, Failure(
                    CameraAcquisitionFailureKind.ProtocolViolation,
                    "HikrobotFrameStartMissing"),
                    protocolKind: CameraProtocolViolationKind.InvalidFrame,
                    recordFault: CameraFaultClassification.ProtocolViolation);
                return;
            }

            if (raw.DeviceTimestamp > long.MaxValue)
            {
                SignalPendingFailure(pending, Failure(
                    CameraAcquisitionFailureKind.ProtocolViolation,
                    "HikrobotDeviceTimestampInvalid"),
                    protocolKind: CameraProtocolViolationKind.InvalidFrame,
                    recordFault: CameraFaultClassification.ProtocolViolation);
                return;
            }

            FrameMetadata metadata;
            FrameProvenance provenance;
            try
            {
                metadata = new FrameMetadata(pending.Request.Correlation,
                    pending.Request.LogicalCameraRole, raw.Width, raw.Height,
                    normalized.StrideBytes, normalized.PixelFormat, normalized.ValidBits,
                    normalizedAt.HostObservedAtUtc, pending.Effective);
                var triggerAccepted = pending.TriggerAccepted;
                provenance = new FrameProvenance(pending.Request.Correlation,
                    _identity.Id, _identity.Version, _identity.AdapterPackageId,
                    _identity.AdapterVersion, SdkId, _runtimeVersion, _runtimeVersion,
                    _descriptor.StableDeviceIdentity, _sdkDescriptor.Model,
                    _sdkDescriptor.Firmware, NativeFormatName(raw.NativePixelFormat),
                    normalized.Details, false, normalized.Transformed,
                    new DeviceTimestamp((long)raw.DeviceTimestamp, null, "device-ticks",
                        "HikrobotDevice", null, DeviceClockSynchronization.Unknown),
                    raw.FrameCounter, new FrameAcquisitionMilestones(_clock.Frequency,
                        triggerAccepted, start.BusyAt, raw.ReceivedAt, normalizedAt));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                SignalPendingFailure(pending, Failure(
                    CameraAcquisitionFailureKind.ProtocolViolation,
                    "HikrobotFrameMetadataInvalid"),
                    protocolKind: CameraProtocolViolationKind.InvalidFrame,
                    recordFault: CameraFaultClassification.ProtocolViolation);
                return;
            }

            FrameCopyResult copy;
            try
            {
                copy = _pool.TryCopyFrame(metadata, provenance,
                    _rawScratch.AsSpan(0, normalized.DataLength));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                SignalPendingFailure(pending, Failure(
                    CameraAcquisitionFailureKind.BufferUnavailable,
                    "HikrobotBufferCopyFailed"),
                    protocolKind: CameraProtocolViolationKind.InvalidFrame,
                    recordFault: CameraFaultClassification.BufferFault);
                _ = BeginRetirement("HikrobotBufferCopyFailed", CameraFaultClassification.BufferFault);
                return;
            }

            if (!copy.Succeeded || copy.Lease is null)
            {
                SignalPendingFailure(pending, Failure(
                    CameraAcquisitionFailureKind.BufferUnavailable, copy.ReasonCode),
                    protocolKind: CameraProtocolViolationKind.InvalidFrame,
                    recordFault: CameraFaultClassification.BufferFault);
                _ = BeginRetirement(copy.ReasonCode, CameraFaultClassification.BufferFault);
                return;
            }

            var producedLease = copy.Lease!;
            lease = producedLease;
            lock (_sync)
            {
                if (!ReferenceEquals(_pending, pending) || pending.Completed ||
                    pending.CancelRequested || _retiring || !pending.Control.IsBusy ||
                    pending.FrameSignalled)
                {
                    releaseAsLate = true;
                }
                else
                {
                    pending.Candidate = FrameAcquisitionResult.Success(producedLease);
                    pending.FrameSignalled = true;
                    pending.FrameReady.TrySetResult(true);
                    lease = null;
                }
            }
            if (releaseAsLate && lease is not null)
            {
                DisposeLeaseSafe(lease);
                lease = null;
                RecordProtocol(CameraProtocolViolationKind.LateFrame,
                    "HikrobotLateFrameDropped", pending.Request.Correlation, raw.ReceivedAt);
            }
            else if (!releaseAsLate)
            {
                InvokeAcquisitionTestProbe(
                    AcquisitionTestProbePoint.CallbackCandidatePublished);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (lease is not null) DisposeLeaseSafe(lease);
            SignalPendingFailure(pending, Failure(CameraAcquisitionFailureKind.DeviceFault,
                "HikrobotFrameProcessingFailed"),
                protocolKind: CameraProtocolViolationKind.InvalidFrame,
                recordFault: CameraFaultClassification.DeviceFault);
        }
        finally
        {
            lock (_sync)
            {
                pending.RawInFlight = false;
                _rawBusy = false;
                if (_rawTask is { } current && current.IsCompleted)
                    _rawTask = null;
            }
            TryFinalizePending(pending);
            InvokeAcquisitionTestProbe(
                AcquisitionTestProbePoint.CallbackFinalizationAttempted);
        }
    }

    private void CancelPending(PendingAcquisition pending)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_pending, pending) || pending.Completed ||
                pending.CancelRequested)
                return;

            // Every installed pending owns the callback registration, even before
            // Busy is published. Runtime closes Control before its asynchronous
            // cancellation reaches this method, so inspecting IsBusy here would
            // miss a delayed hardware pulse. Never reuse this physical owner after
            // cancellation; the retirement proof is the only safe handoff.
            if (!_retiring)
            {
                _ = BeginRetirement("HikrobotAcquisitionCancelled",
                    CameraFaultClassification.ConnectionLost);
                return;
            }

            RequestPendingCancellationLocked(pending, "HikrobotAcquisitionCancelled");
        }
        TryFinalizePending(pending);
    }

    private void SignalPendingFailure(PendingAcquisition? pending,
        FrameAcquisitionResult result,
        CameraProtocolViolationKind? protocolKind = null,
        CameraFaultClassification? recordFault = null,
        bool settleTrigger = false)
    {
        if (pending is null) return;
        IFrameBufferLease? leaseToDispose = null;
        lock (_sync)
        {
            if (!ReferenceEquals(_pending, pending) || pending.Completed)
                return;
            if (pending.Candidate?.Succeeded == true)
                leaseToDispose = pending.Candidate.Lease;
            // A callback may have published a successful candidate while the
            // vendor trigger is still on its stack.  For a trigger failure,
            // publish the failure and settle the trigger in this same state
            // transition so the callback worker cannot finalize the success
            // between those two observations.
            pending.Candidate = result;
            pending.FrameSignalled = true;
            if (settleTrigger)
                pending.TriggerSettled = true;
            pending.FrameReady.TrySetResult(true);
            if (protocolKind is { } kind)
                RecordProtocolLocked(kind, result.Failure?.ReasonCode ?? result.ReasonCode,
                    pending.Request.Correlation);
            if (recordFault is { } classification)
                SetFaultLocked(classification, result.Failure?.ReasonCode ?? result.ReasonCode);
        }
        if (leaseToDispose is not null && !ReferenceEquals(leaseToDispose, result.Lease))
            DisposeLeaseSafe(leaseToDispose);
    }

    private void QueuePendingFinalizer(PendingAcquisition pending,
        string? retirementReason = null,
        CameraFaultClassification? retirementClassification = null)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_pending, pending) || pending.Completed ||
                Interlocked.Exchange(ref pending.FinalizerQueued, 1) != 0)
                return;
        }

        QueuePendingFinalizerCore(pending, retirementReason, retirementClassification);
    }

    private void QueuePendingFinalizerCore(PendingAcquisition pending,
        string? retirementReason, CameraFaultClassification? retirementClassification)
    {

        // The handoff TCS makes the ownership boundary explicit.  If the vendor
        // callback completes the bridge before the worker awaits it, the worker
        // continues on its own Task.Run stack; if it is still waiting, RCAA keeps
        // the continuation from running inline on the callback stack.
        var handoff = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task worker;
        try
        {
            worker = Task.Run(async () =>
            {
                try
                {
                    await handoff.Task.ConfigureAwait(false);
                    if (retirementReason is not null && retirementClassification is { } classification)
                        _ = BeginRetirement(retirementReason, classification,
                            queuePendingFinalizer: false);
                    TryFinalizePending(pending);
                }
                catch (Exception) { }
                finally
                {
                    pending.FinalizerWorkerCompletion.TrySetResult(true);
                }
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Keep the cleanup task pending if a worker cannot be scheduled.  The
            // device must remain reserved until registration/CTS cleanup is really
            // observed; a faulted worker task must not look like quiescence.
            _ = exception;
            return;
        }

        lock (_sync)
        {
            pending.FinalizerWorkerTask = worker;
            // The completion TCS is installed as a reservation at Acquire time.
            // Replace that placeholder with the actual worker before releasing the
            // handoff, so retirement can drain the task itself as well as its
            // completion signal.
            if (ReferenceEquals(_pendingFinalizerWorkerTask,
                    pending.FinalizerWorkerCompletion.Task))
                _pendingFinalizerWorkerTask = worker;
        }
        handoff.TrySetResult(true);
    }

    private void RequestPendingCancellationLocked(PendingAcquisition pending, string reason)
    {
        pending.CancelRequested = true;
        try { pending.WaitCancellationSource.Cancel(); }
        catch (Exception) { }
        if (pending.Candidate?.Succeeded == true)
            DisposeLeaseSafe(pending.Candidate.Lease!);
        if (pending.Candidate is null || pending.Candidate.Succeeded)
        {
            pending.Candidate = Failure(CameraAcquisitionFailureKind.Cancelled, reason);
            pending.FrameSignalled = true;
            pending.FrameReady.TrySetResult(true);
        }
    }

    private void TryFinalizePending(PendingAcquisition? pending)
    {
        if (pending is null) return;
        IFrameBufferLease? leaseToDispose;
        CancellationTokenRegistration registration;
        lock (_sync)
        {
            // Runtime closes its control before asynchronously dispatching the
            // physical cancellation. A frame/worker can observe that closed
            // control first; it must retain the same physical owner until drain.
            if (ReferenceEquals(_pending, pending) && !pending.Completed &&
                pending.Control.IsClosed && !_retiring)
            {
                _ = BeginRetirement("HikrobotControlClosed",
                    CameraFaultClassification.ConnectionLost);
                return;
            }
            if (!TryFinalizePendingLocked(pending, out leaseToDispose, out registration))
                return;
        }
        var probe = _finalizationProbe;
        if (probe is not null)
        {
            try { probe(); }
            catch (Exception) { }
        }
        try
        {
            if (leaseToDispose is not null) DisposeLeaseSafe(leaseToDispose);
            try { registration.Dispose(); }
            catch (Exception) { }
            try { pending.WaitCancellationSource.Dispose(); }
            catch (Exception) { }
        }
        finally
        {
            pending.FinalizerCompletion.TrySetResult(true);
            if (Volatile.Read(ref pending.FinalizerQueued) == 0)
                pending.FinalizerWorkerCompletion.TrySetResult(true);
        }
    }

    private bool TryFinalizePendingLocked(PendingAcquisition pending,
        out IFrameBufferLease? leaseToDispose)
    {
        return TryFinalizePendingLocked(pending, out leaseToDispose, out _);
    }

    private bool TryFinalizePendingLocked(PendingAcquisition pending,
        out IFrameBufferLease? leaseToDispose,
        out CancellationTokenRegistration registration)
    {
        leaseToDispose = null;
        registration = default;
        if (!ReferenceEquals(_pending, pending) || pending.Completed ||
            (_retiring && !_retirementCompleted) ||
            !pending.TriggerSettled || pending.RawInFlight || !pending.FrameSignalled ||
            pending.Candidate is null)
            return false;

        var result = pending.Candidate!;
        if (pending.CancelRequested && result.Succeeded)
        {
            leaseToDispose = result.Lease;
            result = Failure(CameraAcquisitionFailureKind.Cancelled,
                "HikrobotAcquisitionCancelled");
        }

        pending.Completed = true;
        _pending = null;
        _lastRetiredCorrelation = pending.Request.Correlation;
        _acquisition = CameraAcquisitionState.Stopped;
        if (!_retiring)
        {
            // Keep the physical provider slot until explicit successful Dispose.
            // A completed frame does not authorize another request on this handle.
            _connection = CameraConnectionState.Disconnected;
            _configuration = CameraConfigurationState.Unknown;
            _effective = null;
        }
        registration = pending.CancellationRegistration;
        pending.CancellationRegistration = default;
        pending.Completion.TrySetResult(result);
        return true;
    }

    private void RecordUnmatchedFrameLocked()
    {
        if (_lastRetiredCorrelation is { } oldCorrelation)
            RecordProtocolLocked(CameraProtocolViolationKind.LateFrame,
                "HikrobotLateFrameDropped", oldCorrelation);
        else
            RecordProtocolLocked(CameraProtocolViolationKind.EarlyFrame,
                "HikrobotUnsolicitedFrameDropped");
    }

    private void RecordProtocol(CameraProtocolViolationKind kind, string reason,
        ExecutionCorrelationId? correlation, FrameTimePoint observedAt)
    {
        lock (_sync) RecordProtocolLocked(kind, reason, correlation, observedAt);
    }

    private void RecordProtocolLocked(CameraProtocolViolationKind kind, string reason,
        ExecutionCorrelationId? correlation = null, FrameTimePoint? observedAt = null)
    {
        try { _protocol.Append(kind, reason, correlation, 0, observedAt); }
        catch (Exception) { }
    }

    private void SetFaultLocked(CameraFaultClassification classification, string reason)
    {
        _lastFault = new CameraFault(classification, SafeReason(reason,
            "HikrobotDeviceFault"));
    }

    private Task BeginRetirement(string reason, CameraFaultClassification classification,
        bool normalDispose = false, bool queuePendingFinalizer = true)
    {
        PendingAcquisition? pending;
        var queueFinalizer = false;
        lock (_sync)
        {
            // A completed successful retirement is final.  A completed failed
            // retirement still owns the physical SDK handle and must be retried;
            // reusing its old task would silently leave the provider slot held
            // forever.  An in-flight attempt remains the sole cleanup owner.
            if (_retirementCompleted)
                return _disposeTask!;
            if (_disposeTask is { IsCompleted: false } runningDisposal)
                return runningDisposal;

            _retiring = true;
            // The retirement owner takes over the physical call chain.  No new
            // lifecycle operation may reserve the device after this point.
            _lifecycleOperation = LifecycleOperation.None;
            // Closed is proof that physical cleanup completed.  During a
            // pending or failed retirement the handle remains owned by this
            // device, so expose the still-reserved slot as Disconnected.
            _connection = CameraConnectionState.Disconnected;
            _configuration = CameraConfigurationState.Unknown;
            _effective = null;
            _acquisition = CameraAcquisitionState.Stopped;
            _providerAvailability = normalDispose
                ? CameraProviderAvailability.Available
                : CameraProviderAvailability.Faulted;
            SetFaultLocked(classification, reason);
            pending = _pending;
            if (pending is not null && queuePendingFinalizer)
                queueFinalizer = Interlocked.Exchange(ref pending.FinalizerQueued, 1) == 0;
            if (pending is not null)
                RequestPendingCancellationLocked(pending, reason);

            // The shutdown task itself is scheduled away from a caller or callback
            // stack.  It owns the SDK Stop -> Dispose sequence until quiescence.
            var disposal = Task.Run(DisposeCoreAsync);
            _disposeTask = disposal;
            // Queue only one finalizer. It observes cancellation now, but the
            // returned acquire task remains pending until retirement succeeds.
            if (pending is not null && queueFinalizer)
                QueuePendingFinalizerCore(pending, null, null);
            return disposal;
        }
    }

    private async Task DisposeCoreAsync()
    {
        await AwaitRawQuiescenceAsync().ConfigureAwait(false);

        var stopCompleted = false;
        try
        {
            await EnqueueSdk(() =>
            {
                // Stop is part of the actual retirement proof even when the
                // managed Start continuation has not yet published _started.
                // The Hikrobot seam makes Stop idempotent for an unstarted
                // handle, while the serialized SDK chain preserves ordering.
                _sdk.Stop();
            }, allowDuringRetirement: true).ConfigureAwait(false);
            stopCompleted = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_sync) SetFaultLocked(CameraFaultClassification.ConnectionLost,
                SdkReason(exception, "HikrobotStopFailed"));
        }

        if (stopCompleted)
        {
            lock (_sync) _started = false;
        }

        // Native Stop unregisters the callback and waits for all in-flight callback
        // invocations.  A callback which was already handed off is still drained
        // before native Dispose is entered.
        await AwaitRawQuiescenceAsync().ConfigureAwait(false);
        await AwaitFinalizerWorkerQuiescenceAsync().ConfigureAwait(false);

        var disposeCompleted = false;
        try
        {
            await EnqueueSdk(() => _sdk.Dispose(),
                allowDuringRetirement: true).ConfigureAwait(false);
            disposeCompleted = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_sync) SetFaultLocked(CameraFaultClassification.ConnectionLost,
                SdkReason(exception, "HikrobotDisposeFailed"));
        }

        // Release adapter-owned pool roots before publishing the provider slot
        // as safely replaceable.  Outstanding consumer leases still retain
        // their own pinned slot through FrameBufferPool's ownership protocol.
        _pool.Dispose();

        PendingAcquisition? pendingToFinalize = null;
        if (stopCompleted && disposeCompleted)
        {
            lock (_sync)
            {
                _sdkDisposed = true;
                _callback = null;
                _started = false;
                _disposed = true;
                _connection = CameraConnectionState.Closed;
                _retirementCompleted = true;
                pendingToFinalize = _pending;
            }

            // Cancellation results remain owned by the device until the physical
            // Stop/Dispose proof above succeeds.  Only now may the adapter release
            // the Runtime task and finish its registration cleanup.
            TryFinalizePending(pendingToFinalize);
            await AwaitFinalizerQuiescenceAsync().ConfigureAwait(false);
        }
    }

    private async Task AwaitRawQuiescenceAsync()
    {
        while (true)
        {
            Task? task;
            lock (_sync) task = _rawTask;
            if (task is null) return;
            try { await task.ConfigureAwait(false); }
            catch (Exception) { }
            lock (_sync)
            {
                if (ReferenceEquals(_rawTask, task) && task.IsCompleted)
                    _rawTask = null;
            }
        }
    }

    private async Task AwaitFinalizerQuiescenceAsync()
    {
        while (true)
        {
            Task? finalizer;
            Task? worker;
            lock (_sync)
            {
                finalizer = _pendingFinalizerTask;
                worker = _pendingFinalizerWorkerTask;
            }
            if (finalizer is null && worker is null) return;
            if (finalizer is not null)
            {
                try { await finalizer.ConfigureAwait(false); }
                catch (Exception) { }
            }
            if (worker is not null)
            {
                try { await worker.ConfigureAwait(false); }
                catch (Exception) { }
            }
            lock (_sync)
            {
                if (ReferenceEquals(_pendingFinalizerTask, finalizer) &&
                    ReferenceEquals(_pendingFinalizerWorkerTask, worker) &&
                    (finalizer is null || finalizer.IsCompleted) &&
                    (worker is null || worker.IsCompleted))
                {
                    _pendingFinalizerTask = null;
                    _pendingFinalizerWorkerTask = null;
                    return;
                }
            }
        }
    }

    private async Task AwaitFinalizerWorkerQuiescenceAsync()
    {
        while (true)
        {
            Task? task;
            lock (_sync) task = _pendingFinalizerWorkerTask;
            if (task is null) return;
            try { await task.ConfigureAwait(false); }
            catch (Exception) { }
            lock (_sync)
            {
                if (ReferenceEquals(_pendingFinalizerWorkerTask, task) &&
                    task.IsCompleted)
                    return;
            }
        }
    }

    private CancellationTokenRegistration RegisterRetirementOnCancellation(
        CancellationToken cancellationToken, string reason,
        CameraFaultClassification classification)
    {
        if (!cancellationToken.CanBeCanceled)
            return default;
        return cancellationToken.Register(static state =>
        {
            var value = (RetirementRequest)state!;
            _ = value.Device.BeginRetirement(value.Reason, value.Classification);
        }, new RetirementRequest(this, reason, classification));
    }

    private static async Task<CameraConfigurationResult> ObserveConfigurationAsync(
        Task<CameraConfigurationResult> operation, CancellationToken cancellationToken,
        CancellationTokenRegistration registration)
    {
        try
        {
            return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CameraConfigurationResult.Failure("HikrobotConfigurationCancelled");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CameraConfigurationResult.Failure("HikrobotConfigurationFailed");
        }
        finally
        {
            registration.Dispose();
        }
    }

    private static async Task<CameraOperationResult> ObserveOperationAsync(
        Task<CameraOperationResult> operation, CancellationToken cancellationToken,
        CancellationTokenRegistration registration, string cancellationReason)
    {
        try
        {
            return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CameraOperationResult.Failure(cancellationReason);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CameraOperationResult.Failure("HikrobotOperationFailed");
        }
        finally
        {
            registration.Dispose();
        }
    }

    private Task<T> EnqueueSdk<T>(Func<T> call, bool allowDuringRetirement = false)
    {
        lock (_sdkSequenceSync)
        {
            var predecessor = _sdkTail;
            var task = Task.Run(async () =>
            {
                try { await predecessor.ConfigureAwait(false); }
                catch (Exception) { }
                if (!allowDuringRetirement)
                {
                    lock (_sync)
                    {
                        if (_retiring)
                            throw new HikrobotSdkException("HikrobotDeviceRetiring");
                    }
                }
                return call();
            });
            _sdkTail = task;
            return task;
        }
    }

    private Task EnqueueSdk(Action call, bool allowDuringRetirement = false) =>
        EnqueueSdk(() =>
    {
        call();
        return true;
    }, allowDuringRetirement);

    private FrameTimePoint SafeTimePoint()
    {
        try { return _clock.GetTimePoint(); }
        catch (Exception) { return new FrameTimePoint(DateTimeOffset.UtcNow, 0); }
    }

    private static string NativeFormatName(HikrobotNativePixelFormat format) => format switch
    {
        HikrobotNativePixelFormat.Mono8 => "Mono8",
        HikrobotNativePixelFormat.Mono10 => "Mono10",
        HikrobotNativePixelFormat.Mono12 => "Mono12",
        HikrobotNativePixelFormat.Mono16 => "Mono16",
        HikrobotNativePixelFormat.Rgb24 => "Rgb24",
        HikrobotNativePixelFormat.Bgr24 => "Bgr24",
        _ => "Unknown"
    };

    private static string RequireVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(char.IsControl))
            throw new ArgumentException("HikrobotRuntimeVersionInvalid", nameof(value));
        return value;
    }

    private static string SdkReason(Exception exception, string fallback) =>
        exception is HikrobotSdkException sdk
            ? SafeReason(sdk.ReasonCode, fallback) : fallback;

    private static string SafeReason(string reason, string fallback)
    {
        if (string.IsNullOrEmpty(reason) || reason.Length > 128)
            return fallback;
        foreach (var character in reason)
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                >= '0' and <= '9' or '.' or '_' or '-' or ':'))
                return fallback;
        return reason;
    }

    private static FrameAcquisitionResult Failure(CameraAcquisitionFailureKind kind,
        string reason) => FrameAcquisitionResult.FailureResult(
            new CameraAcquisitionFailure(kind, SafeReason(reason, "HikrobotDeviceFault")));

    private static void DisposeLeaseSafe(IFrameBufferLease lease)
    {
        try { lease.Dispose(); }
        catch (Exception) { }
    }

    private void InvokeAcquisitionTestProbe(AcquisitionTestProbePoint point)
    {
        var probe = _acquisitionTestProbe;
        if (probe is null) return;
        try { probe(point); }
        catch (Exception) { }
    }

    private sealed class PendingAcquisition
    {
        internal PendingAcquisition(FrameAcquisitionRequest request,
            FrameAcquisitionControl control, EffectiveCameraConfiguration effective,
            CancellationToken cancellationToken)
        {
            Request = request;
            Control = control;
            Effective = effective;
            WaitCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            FinalizerTask = Task.WhenAll(FinalizerCompletion.Task,
                FinalizerWorkerCompletion.Task);
        }

        internal FrameAcquisitionRequest Request { get; }
        internal FrameAcquisitionControl Control { get; }
        internal EffectiveCameraConfiguration Effective { get; }
        internal CancellationTokenSource WaitCancellationSource { get; }
        internal TaskCompletionSource<bool> FinalizerCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> FinalizerWorkerCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task FinalizerTask { get; }
        internal Task? FinalizerWorkerTask { get; set; }
        internal TaskCompletionSource<bool> FrameReady { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<FrameAcquisitionResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal FrameAcquisitionResult? Candidate { get; set; }
        internal FrameTimePoint? TriggerAccepted { get; set; }
        internal CancellationTokenRegistration CancellationRegistration { get; set; }
        internal bool TriggerSettled { get; set; }
        internal bool FrameSignalled { get; set; }
        internal bool CancelRequested { get; set; }
        internal bool Completed { get; set; }
        internal bool RawInFlight { get; set; }
        internal int FinalizerQueued;
    }

    private sealed record PendingCancellationState(HikrobotCameraDevice Device,
        PendingAcquisition Pending);

    private sealed record RetirementRequest(HikrobotCameraDevice Device, string Reason,
        CameraFaultClassification Classification);
}
