using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Cameras;

/// <summary>
/// A bounded, qualification-only frame acquisition boundary. The service owns the
/// physical camera call and keeps that call alive after a caller-facing timeout or
/// cancellation until its actual task has quiesced.
/// </summary>
public sealed partial class CameraAcquisitionService : IAsyncDisposable
{
    private const int MaximumProtocolReadCount = 64;
    private readonly object _sync = new();
    private readonly IControlledCameraDevice _device;
    private readonly CameraDeviceDescriptor _descriptor;
    private readonly CameraCapabilities _capabilities;
    private readonly EffectiveCameraConfiguration _configuration;
    private readonly IFrameAcquisitionClock _clock;
    private readonly CameraAcquisitionOptions _options;
    private readonly Guid _protocolEpoch = NewEpoch();
    private readonly Queue<CameraProtocolObservation> _protocol = new();

    private Attempt? _attempt;
    private bool _disposed;
    private Guid? _adapterEpoch;
    private long _adapterCursor;
    private Task<CameraProtocolSnapshot>? _protocolReadTask;
    private Task<CameraProtocolSnapshot>? _protocolTimeoutReportedTask;
    private bool _protocolRefreshQueued;
    private bool _protocolFaulted;
    private bool _admissionInProgress;
    private TaskCompletionSource<bool>? _admissionCompletion;
    private long _nextProtocolSequence = 1;
    private bool _calibrationEnabled;
    private bool _manualEnabled;
    private bool _qualificationSessionEnabled;

    public CameraAcquisitionService(IControlledCameraDevice device,
        EffectiveCameraConfiguration effectiveConfiguration, IFrameAcquisitionClock clock,
        CameraAcquisitionOptions options)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _descriptor = device.Descriptor ?? throw new ArgumentException(
            "CameraDeviceDescriptorUnavailable", nameof(device));
        _capabilities = device.Capabilities ?? throw new ArgumentException(
            "CameraCapabilitiesUnavailable", nameof(device));
        _configuration = effectiveConfiguration ?? throw new ArgumentNullException(nameof(effectiveConfiguration));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_clock.Frequency is < 1 or > 10_000_000_000)
            throw new ArgumentOutOfRangeException(nameof(clock));
    }

    /// <summary>The effective configuration captured when this service was created.</summary>
    public EffectiveCameraConfiguration Configuration => _configuration;

    /// <summary>Qualification acquisition is never a production-ready station gate.</summary>
    public bool Ready => false;

    public bool IsDisposed
    {
        get { lock (_sync) return _disposed; }
    }

    /// <summary>Once an adapter gap/read failure is observed, new acquisitions remain fail-closed.</summary>
    public bool ProtocolFaulted
    {
        get { lock (_sync) return _protocolFaulted; }
    }

    public bool IsBusy
    {
        get
        {
            lock (_sync) return _attempt?.IsBusyLocked() ?? false;
        }
    }

    /// <summary>
    /// Returns the current attempt, including cleanup after a bounded caller result.
    /// A null Start means the adapter has not yet acknowledged and opened Busy.
    /// </summary>
    public CameraAcquisitionBusySnapshot? Busy
    {
        get
        {
            lock (_sync) return _attempt?.GetBusySnapshotLocked();
        }
    }

    public CameraAcquisitionBusySnapshot? BusySnapshot => Busy;

    /// <summary>
    /// Accepts one qualification attempt. The caller does not choose its correlation
    /// identifier; an accepted call creates one before invoking the camera device.
    /// </summary>
    public ValueTask<CameraAcquisitionAttempt> AcquireAsync(ExecutionKind kind,
        string logicalCameraRole, CancellationToken cancellationToken = default) =>
        new(AcquireCoreAsync(kind, logicalCameraRole, cancellationToken, null));

    /// <summary>
    /// Internal acquisition path reserved for a Recovery-owned calibration lease.
    /// The caller supplies the Runtime-generated frame identifier as the
    /// calibration correlation value; it cannot select a production or manual
    /// correlation through this seam.
    /// </summary>
    internal ValueTask<CameraAcquisitionAttempt> AcquireCalibrationAsync(
        ExecutionCorrelationId correlation, string logicalCameraRole,
        CancellationToken cancellationToken = default) =>
        new(AcquireCoreAsync(ExecutionKind.Calibration, logicalCameraRole,
            cancellationToken, correlation));

    /// <summary>Enables the calibration-only correlation path for one temporary owner.</summary>
    internal void EnableCalibrationAcquisition()
    {
        lock (_sync)
        {
            if (_disposed || _attempt is not null || _admissionInProgress || _manualEnabled ||
                _qualificationSessionEnabled || _productionOwnedEnabled)
                throw new InvalidOperationException("CameraCalibrationAcquisitionUnavailable");
            _calibrationEnabled = true;
        }
    }

    /// <summary>
    /// Reads the service-owned bounded protocol ring. This method never calls the
    /// device; use <see cref="RefreshProtocolObservationsAsync"/> to pull the adapter ring.
    /// </summary>
    public CameraProtocolSnapshot ReadProtocolObservations(long afterSequence,
        int maximumCount = MaximumProtocolReadCount)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        ValidateProtocolCount(maximumCount);
        lock (_sync)
        {
            var through = _nextProtocolSequence - 1;
            if (afterSequence > through)
                throw new ArgumentOutOfRangeException(nameof(afterSequence),
                    "CameraProtocolCursorBeyondThroughSequence");

            var first = _protocol.Count == 0 ? 1 : _protocol.Peek().Sequence;
            var overflowed = afterSequence < first - 1;
            var firstRequested = overflowed ? first : afterSequence + 1;
            var observations = _protocol.Where(item => item.Sequence >= firstRequested)
                .Take(maximumCount).ToArray();
            return new CameraProtocolSnapshot(_protocolEpoch, first, through, overflowed,
                observations);
        }
    }

    /// <summary>
    /// Pulls at most one adapter ring read at a time. A synchronous or hung adapter
    /// read executes off the caller thread and does not prevent the bounded method
    /// from returning; its task remains the sole in-flight read until it finishes.
    /// </summary>
    public async ValueTask<CameraProtocolSnapshot> RefreshProtocolObservationsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<CameraProtocolSnapshot> readTask;
        lock (_sync)
        {
            if (_disposed)
                return ReadProtocolObservationsLocked(0, MaximumProtocolReadCount);
            if (HasPendingHealthReadLocked())
                return ReadProtocolObservationsLocked(0, MaximumProtocolReadCount);

            readTask = _protocolReadTask ??= StartProtocolReadLocked(_adapterCursor);
        }

        CameraProtocolSnapshot? source = null;
        Exception? failure = null;
        var timedOut = false;
        try
        {
            source = await readTask.WaitAsync(_options.ProtocolReadTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failure = exception;
        }

        lock (_sync)
        {
            if (timedOut && ReferenceEquals(_protocolTimeoutReportedTask, readTask))
                timedOut = false;
            else if (timedOut)
                _protocolTimeoutReportedTask = readTask;

            if (ReferenceEquals(_protocolReadTask, readTask) && readTask.IsCompleted)
            {
                _protocolReadTask = null;
                _protocolTimeoutReportedTask = null;
                if (failure is null && readTask.Status == TaskStatus.RanToCompletion)
                {
                    // WaitAsync can lose a completion/timeout race. Read the
                    // already completed task directly so a valid page at the
                    // deadline is not silently discarded.
                    try { source = readTask.GetAwaiter().GetResult(); }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    { failure = exception; }
                }

                if (failure is null && source is not null)
                    MergeAdapterSnapshotLocked(source);
                else
                {
                    _protocolFaulted = true;
                    AppendProtocolLocked(CameraProtocolViolationKind.ObservationGap,
                        "CameraProtocolReadFailed", null, null, 0);
                }
            }
            else if (timedOut)
            {
                _protocolFaulted = true;
                AppendProtocolLocked(CameraProtocolViolationKind.ObservationGap,
                    "CameraProtocolReadTimeout", null, null, 0);
            }

            return ReadProtocolObservationsLocked(0, MaximumProtocolReadCount);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var retirement = BeginRetirement();

        try
        {
            await retirement.WaitAsync(_options.ShutdownWaitTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The background owner retains the physical device and continues the
            // invocation -> acquire -> stop -> dispose order after this bounded wait.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A bounded public shutdown never exposes an adapter fault. BeginRetirement
            // retains the owner and reports the fault to its internal consumer.
        }
    }

    private async Task<CameraAcquisitionAttempt> AcquireCoreAsync(ExecutionKind kind,
        string logicalCameraRole, CancellationToken cancellationToken,
        ExecutionCorrelationId? suppliedCorrelation)
    {
        if (!Enum.IsDefined(typeof(ExecutionKind), kind))
            return Rejected("CameraAcquisitionExecutionKindInvalid");
        if (suppliedCorrelation is null && kind != ExecutionKind.Qualification)
            return Rejected("ProductionAcquisitionUnavailable");
        if (suppliedCorrelation is not null)
        {
            var correlationValid = suppliedCorrelation.Value != Guid.Empty &&
                suppliedCorrelation.Kind == kind &&
                kind is ExecutionKind.Calibration or ExecutionKind.Manual or ExecutionKind.Qualification or ExecutionKind.Production;
            if (!correlationValid)
                return Rejected(kind == ExecutionKind.Manual
                    ? "CameraManualCorrelationInvalid"
                    : kind == ExecutionKind.Qualification ? "CameraQualificationCorrelationInvalid"
                    : "CameraCalibrationCorrelationInvalid");
        }
        if (cancellationToken.IsCancellationRequested)
            return Rejected("CameraAcquisitionCancelledBeforeStart");

        try
        {
            // Validate the role before allocating the public correlation. An
            // accepted attempt creates exactly one ID and uses it for both request
            // and evidence; rejected calls never reach the device acquisition method.
            _ = FrameMetadataValidation.Identifier(logicalCameraRole,
                nameof(logicalCameraRole));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Rejected("CameraLogicalCameraRoleInvalid");
        }

        TaskCompletionSource<bool> admissionCompletion;
        lock (_sync)
        {
            if (_disposed) return Rejected("CameraAcquisitionServiceDisposed");
            if (kind == ExecutionKind.Production && !_productionOwnedEnabled)
                return Rejected("CameraProductionLeaseRequired");
            if (kind != ExecutionKind.Production && _productionOwnedEnabled)
                return Rejected("CameraProductionControlledOnly");
            if (kind == ExecutionKind.Calibration && !_calibrationEnabled)
                return Rejected("CameraCalibrationLeaseRequired");
            if (kind == ExecutionKind.Manual && !_manualEnabled)
                return Rejected("CameraManualLeaseRequired");
            if (kind == ExecutionKind.Qualification && suppliedCorrelation is not null && !_qualificationSessionEnabled)
                return Rejected("CameraQualificationSessionLeaseRequired");
            if (kind == ExecutionKind.Qualification && suppliedCorrelation is null && _qualificationSessionEnabled)
                return Rejected("CameraQualificationSessionControlledOnly");
            if (kind == ExecutionKind.Qualification && (_calibrationEnabled || _manualEnabled))
                return Rejected(_manualEnabled ? "CameraManualControlledOnly" :
                    "CameraCalibrationControlledOnly");
            if (_attempt is not null || _admissionInProgress || HasPendingHealthReadLocked())
            {
                AppendProtocolLocked(CameraProtocolViolationKind.TriggerWhileBusy,
                    "CameraAcquisitionBusy", null, null, 0);
                return Rejected("CameraAcquisitionBusy");
            }
            _admissionInProgress = true;
            admissionCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _admissionCompletion = admissionCompletion;
        }

        try
        {
            // The adapter ring is read before every new physical acquisition. A gap
            // or a failed read latches the service closed until a higher-level owner
            // replaces/reinitializes the service.
            try
            {
                await RefreshProtocolObservationsAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Rejected("CameraAcquisitionCancelledBeforeStart");
            }

            Attempt attempt;
            lock (_sync)
            {
                if (_disposed) return Rejected("CameraAcquisitionServiceDisposed");
                if (_protocolFaulted) return Rejected("CameraProtocolUnavailable");
                if (_attempt is not null)
                {
                    AppendProtocolLocked(CameraProtocolViolationKind.TriggerWhileBusy,
                        "CameraAcquisitionBusy", null, null, 0);
                    return Rejected("CameraAcquisitionBusy");
                }

                var correlation = suppliedCorrelation ?? new ExecutionCorrelationId(
                    ExecutionKind.Qualification, NewCorrelation());
                FrameAcquisitionRequest request;
                try
                {
                    request = new FrameAcquisitionRequest(correlation, logicalCameraRole);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    return Rejected("CameraLogicalCameraRoleInvalid");
                }

                attempt = new Attempt(this, request, cancellationToken);
                _attempt = attempt;
            }

            attempt.Start();
            var outcome = await attempt.Completion.Task.ConfigureAwait(false);
            return new CameraAcquisitionAttempt(true, outcome.ReasonCode,
                outcome.Correlation, outcome);
        }
        finally
        {
            lock (_sync)
            {
                _admissionInProgress = false;
                if (ReferenceEquals(_admissionCompletion, admissionCompletion))
                {
                    _admissionCompletion = null;
                    admissionCompletion.TrySetResult(true);
                }
            }
        }

    }

    private async Task WaitForAdmissionAndProtocolReadAsync()
    {
        while (true)
        {
            Task? admission;
            Task<CameraProtocolSnapshot>? protocolRead;
            Task<CameraHealthSnapshot?>? healthRead;
            lock (_sync)
            {
                admission = _admissionCompletion?.Task;
                protocolRead = _protocolReadTask;
                healthRead = _healthReadTask;
                if (admission is null && protocolRead is null && healthRead is null &&
                    !_admissionInProgress)
                    return;
            }

            if (admission is not null)
            {
                try { await admission.ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            }

            if (protocolRead is not null)
            {
                try { await protocolRead.ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
                lock (_sync)
                {
                    // A cancelled refresh may have no continuation left to clear
                    // its completed physical read. Disposal is the final owner, so
                    // it may retire that completed task without advancing the
                    // adapter cursor or inventing observations.
                    if (ReferenceEquals(_protocolReadTask, protocolRead) &&
                        protocolRead.IsCompleted)
                    {
                        _protocolReadTask = null;
                        _protocolTimeoutReportedTask = null;
                    }
                }
            }

            if (healthRead is not null)
            {
                try { await healthRead.ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
                lock (_sync)
                {
                    if (ReferenceEquals(_healthReadTask, healthRead) && healthRead.IsCompleted)
                        _healthReadTask = null;
                }
            }
        }
    }

    private Task<CameraProtocolSnapshot> StartProtocolReadLocked(long afterSequence)
    {
        return Task.Factory.StartNew(static state =>
        {
            var context = (ProtocolReadContext)state!;
            return context.Service._device.ReadProtocolObservations(context.AfterSequence,
                MaximumProtocolReadCount);
        }, new ProtocolReadContext(this, afterSequence), CancellationToken.None,
            TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
    }

    private static Task<CameraOperationResult> InvokeOperationAsync(
        Func<ValueTask<CameraOperationResult>> operation)
    {
        return Task.Factory.StartNew(static state =>
        {
            var call = (Func<ValueTask<CameraOperationResult>>)state!;
            try { return call().AsTask(); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { return Task.FromException<CameraOperationResult>(exception); }
        }, operation, CancellationToken.None, TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
    }

    private static Task InvokeDisposeAsync(IAsyncDisposable disposable)
    {
        return Task.Factory.StartNew(static state =>
        {
            var owner = (IAsyncDisposable)state!;
            try { return owner.DisposeAsync().AsTask(); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { return Task.FromException(exception); }
        }, disposable, CancellationToken.None, TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
    }

    private void MergeAdapterSnapshotLocked(CameraProtocolSnapshot source)
    {
        if (_adapterEpoch is not null && _adapterEpoch.Value != source.Epoch)
        {
            _protocolFaulted = true;
            AppendProtocolLocked(CameraProtocolViolationKind.ObservationGap,
                "CameraProtocolEpochChanged", null, null, 0);
            _adapterCursor = 0;
        }

        _adapterEpoch = source.Epoch;
        if (source.ThroughSequence < _adapterCursor)
        {
            _protocolFaulted = true;
            AppendProtocolLocked(CameraProtocolViolationKind.ObservationGap,
                "CameraProtocolSequenceRollback", null, null, 0);
            return;
        }

        if (source.Overflowed || source.FirstAvailableSequence > _adapterCursor + 1)
        {
            _protocolFaulted = true;
            AppendProtocolLocked(CameraProtocolViolationKind.ObservationGap,
                "CameraProtocolObservationGap", null, null, 0);
        }

        var hadNewObservation = false;
        foreach (var observation in source.Observations)
        {
            if (observation.Sequence <= _adapterCursor) continue;
            if (observation.Kind == CameraProtocolViolationKind.ObservationGap)
                _protocolFaulted = true;
            if (observation.Sequence > _adapterCursor + 1)
            {
                _protocolFaulted = true;
                AppendProtocolLocked(CameraProtocolViolationKind.ObservationGap,
                    "CameraProtocolObservationGap", observation.ObservedAt,
                    observation.Correlation, observation.DroppedFrames);
            }
            AppendProtocolLocked(observation.Kind, observation.ReasonCode,
                observation.ObservedAt, observation.Correlation, observation.DroppedFrames);
            // This is the only cursor advancement point. ThroughSequence alone is
            // intentionally insufficient because it may describe unread later pages.
            _adapterCursor = observation.Sequence;
            hadNewObservation = true;
        }

        // An empty page with a later through-sequence cannot prove that the
        // intervening facts were observed. A bounded page containing its next fact
        // remains valid and can be continued on the next refresh.
        if (source.ThroughSequence > _adapterCursor && !hadNewObservation)
        {
            _protocolFaulted = true;
            AppendProtocolLocked(CameraProtocolViolationKind.ObservationGap,
                "CameraProtocolObservationGap", null, null, 0);
        }
    }

    private CameraProtocolSnapshot ReadProtocolObservationsLocked(long afterSequence,
        int maximumCount)
    {
        var through = _nextProtocolSequence - 1;
        var first = _protocol.Count == 0 ? 1 : _protocol.Peek().Sequence;
        var overflowed = afterSequence < first - 1;
        var firstRequested = overflowed ? first : afterSequence + 1;
        var observations = _protocol.Where(item => item.Sequence >= firstRequested)
            .Take(maximumCount).ToArray();
        return new CameraProtocolSnapshot(_protocolEpoch, first, through, overflowed,
            observations);
    }

    private void AppendProtocolLocked(CameraProtocolViolationKind kind, string reasonCode,
        FrameTimePoint? observedAt, ExecutionCorrelationId? correlation, int droppedFrames)
    {
        if (_nextProtocolSequence >= long.MaxValue) return;
        var point = observedAt ?? SafeTimePoint();
        CameraProtocolObservation observation;
        try
        {
            observation = new CameraProtocolObservation(_nextProtocolSequence++, kind,
                reasonCode, point, correlation, Math.Min(droppedFrames, 64));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Runtime-generated reason codes and timestamps are closed values. If a
            // faulty clock/adapter violates that boundary, preserve fail-closed state
            // without allowing a diagnostic exception to replace the attempt outcome.
            return;
        }

        _protocol.Enqueue(observation);
        while (_protocol.Count > _options.ProtocolCapacity)
            _protocol.Dequeue();
    }

    private void QueueProtocolRefreshLocked()
    {
        if (_disposed || _protocolRefreshQueued || _protocolReadTask is not null)
            return;
        _protocolRefreshQueued = true;
        _ = Task.Run(async () =>
        {
            try { await RefreshProtocolObservationsAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            finally
            {
                lock (_sync) _protocolRefreshQueued = false;
            }
        });
    }

    private FrameTimePoint SafeTimePoint()
    {
        try { return _clock.GetTimePoint(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new FrameTimePoint(DateTimeOffset.UtcNow, 0);
        }
    }

    private static void ValidateProtocolCount(int maximumCount)
    {
        if (maximumCount is < 1 or > MaximumProtocolReadCount)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
    }

    private static CameraAcquisitionAttempt Rejected(string reasonCode) =>
        new(false, reasonCode, null, null);

    private static Guid NewCorrelation()
    {
        Guid value;
        do { value = Guid.NewGuid(); } while (value == Guid.Empty);
        return value;
    }

    private static Guid NewEpoch() => NewCorrelation();

    private sealed class ProtocolReadContext
    {
        internal ProtocolReadContext(CameraAcquisitionService service, long afterSequence)
        {
            Service = service;
            AfterSequence = afterSequence;
        }
        internal CameraAcquisitionService Service { get; }
        internal long AfterSequence { get; }
    }

    private sealed class Attempt
    {
        private readonly CameraAcquisitionService _service;
        private readonly FrameAcquisitionRequest _request;
        private readonly FrameAcquisitionControl _control;
        private readonly CancellationToken _callerCancellation;
        private readonly CancellationTokenSource _physicalCancellation = new();
        private CancellationTokenRegistration _callerRegistration;
        private Task<Task<FrameAcquisitionResult>>? _invocationTask;
        private Task<FrameAcquisitionResult>? _acquireTask;
        private CancellationTokenSource? _pendingTimeoutCancellation;
        private IDisposable? _deadlineHandle;
        private Task? _cancelTask;
        private Task? _leaseDisposalTask;
        private FrameAcquisitionStart? _start;
        private bool _invocationReturned;
        private bool _innerCompleted;
        private bool _opened;
        private bool _terminal;
        private bool _cancelSignalled;
        private bool _physicalCompleted;
        private bool _callerRegistrationSet;
        private bool _resultConsumed;

        internal Attempt(CameraAcquisitionService service, FrameAcquisitionRequest request,
            CancellationToken callerCancellation)
        {
            _service = service;
            _request = request;
            _callerCancellation = callerCancellation;
            _control = new FrameAcquisitionControl(request, service._clock);
        }

        internal TaskCompletionSource<CameraAcquisitionOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> PhysicalCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Start()
        {
            try
            {
                _pendingTimeoutCancellation = new CancellationTokenSource();
                _ = WatchPendingInstallAsync(_pendingTimeoutCancellation.Token);

                _callerRegistration = _callerCancellation.Register(static state =>
                    ((Attempt)state!).RequestCancellation(), this);
                _callerRegistrationSet = true;
                lock (_service._sync)
                {
                    if (_terminal)
                    {
                        _invocationReturned = true;
                        _innerCompleted = true;
                        CompletePhysicalLocked();
                        return;
                    }
                }
                _invocationTask = Task.Factory.StartNew(static state =>
                {
                    var attempt = (Attempt)state!;
                    return attempt.InvokeAcquire();
                }, this, CancellationToken.None, TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);
                _ = _invocationTask.ContinueWith(static (task, state) =>
                    ((Attempt)state!).OnInvocationReturned(task), this,
                    CancellationToken.None, TaskContinuationOptions.None,
                    TaskScheduler.Default);
                _ = ObserveAdmissionAsync();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                lock (_service._sync)
                {
                    _invocationReturned = true;
                    _innerCompleted = true;
                    LatchFailureLocked(CameraAcquisitionFailureKind.DeviceFault,
                        "CameraAcquisitionStartFailed", signalCancellation: true);
                    CompletePhysicalLocked();
                }
            }
        }

        private async Task WatchPendingInstallAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(_service._options.PendingInstallTimeout, cancellationToken)
                    .ConfigureAwait(false);
                OnPendingDeadline();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        internal CameraAcquisitionBusySnapshot GetBusySnapshotLocked()
        {
            var cleanup = _terminal && !_physicalCompleted;
            var reason = _terminal
                ? (cleanup ? "CameraAcquisitionCleanupPending" : Completion.Task.IsCompleted
                    ? Completion.Task.GetAwaiter().GetResult().ReasonCode
                    : "CameraAcquisitionCompleted")
                : (_opened ? "CameraAcquisitionBusy" : "CameraAcquisitionPending");
            return new CameraAcquisitionBusySnapshot(_request.Correlation,
                _request.LogicalCameraRole, _start, _opened && !_terminal, cleanup, reason);
        }

        internal bool IsBusyLocked() => _opened && !_terminal;

        internal void RequestCancellation()
        {
            Task? cancelTask = null;
            lock (_service._sync)
            {
                if (_terminal) return;
                LatchFailureLocked(CameraAcquisitionFailureKind.Cancelled,
                    "CameraAcquisitionCancelled", signalCancellation: true);
                cancelTask = _cancelTask;
                CompletePhysicalLocked();
            }
            _ = cancelTask;
        }

        private Task<FrameAcquisitionResult> InvokeAcquire()
        {
            lock (_service._sync)
            {
                if (_terminal)
                {
                    return Task.FromResult(FrameAcquisitionResult.FailureResult(
                        new CameraAcquisitionFailure(CameraAcquisitionFailureKind.Cancelled,
                            "CameraAcquisitionCancelled")));
                }
            }

            try
            {
                return _service._device.AcquireAsync(_request, _control,
                    _physicalCancellation.Token).AsTask();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return Task.FromException<FrameAcquisitionResult>(exception);
            }
        }

        private async Task ObserveAdmissionAsync()
        {
            try
            {
                var invocation = _invocationTask;
                if (invocation is null) return;
                await Task.WhenAll(_control.PendingInstalled, invocation).ConfigureAwait(false);
                TryOpenIfReady();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                lock (_service._sync)
                {
                    if (!_terminal)
                        LatchFailureLocked(CameraAcquisitionFailureKind.DeviceFault,
                            "CameraAcquisitionPendingFailed", signalCancellation: true);
                    CompletePhysicalLocked();
                }
            }
        }

        private void OnInvocationReturned(Task<Task<FrameAcquisitionResult>> completed)
        {
            Task<FrameAcquisitionResult>? inner = null;
            Exception? exception = null;
            lock (_service._sync)
            {
                _invocationReturned = true;
                try
                {
                    inner = completed.GetAwaiter().GetResult();
                    if (inner is null)
                        throw new InvalidOperationException("CameraAcquisitionTaskMissing");
                    _acquireTask = inner;
                }
                catch (Exception caught) when (caught is not OutOfMemoryException)
                {
                    exception = caught;
                    _innerCompleted = true;
                    if (!_terminal)
                        LatchFailureLocked(CameraAcquisitionFailureKind.DeviceFault,
                            "CameraAcquisitionDeviceFault", signalCancellation: false);
                }
                CompletePhysicalLocked();
            }

            if (inner is not null)
            {
                _ = inner.ContinueWith(static (task, state) =>
                    ((Attempt)state!).OnAcquireCompleted(task), this,
                    CancellationToken.None, TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }

            if (exception is not null)
                _ = exception;
            TryOpenIfReady();
        }

        private void OnAcquireCompleted(Task<FrameAcquisitionResult> completed)
        {
            lock (_service._sync)
            {
                _innerCompleted = true;
                ProcessCompletedTaskLocked(completed, beforeBusy: !_opened);
                CompletePhysicalLocked();
            }
        }

        private void OnPendingDeadline()
        {
            lock (_service._sync)
            {
                if (_terminal || _opened) return;
                if (_invocationReturned && _acquireTask is not null &&
                    _control.PendingInstalled.IsCompletedSuccessfully)
                {
                    TryOpenIfReadyLocked();
                    return;
                }

                LatchFailureLocked(CameraAcquisitionFailureKind.TimedOut,
                    "CameraAcquisitionPendingInstallTimeout", signalCancellation: true);
                CompletePhysicalLocked();
            }
        }

        private void TryOpenIfReady()
        {
            lock (_service._sync) TryOpenIfReadyLocked();
        }

        private void TryOpenIfReadyLocked()
        {
            if (_terminal || _opened || !_invocationReturned || _acquireTask is null ||
                !_control.PendingInstalled.IsCompletedSuccessfully)
                return;

            if (_acquireTask.IsCompleted)
            {
                _innerCompleted = true;
                ProcessCompletedTaskLocked(_acquireTask, beforeBusy: true);
                CompletePhysicalLocked();
                return;
            }

            try
            {
                var busyAt = _control.PendingInstalledAt;
                if (busyAt is null)
                {
                    LatchFailureLocked(CameraAcquisitionFailureKind.ProtocolViolation,
                        "CameraAcquisitionPendingTimestampMissing", signalCancellation: true);
                    CompletePhysicalLocked();
                    return;
                }

                var deadline = AddDuration(busyAt.MonotonicTimestamp,
                    TimeSpan.FromMilliseconds(_service._configuration.AcquisitionTimeoutMs),
                    _service._clock.Frequency);
                var start = new FrameAcquisitionStart(busyAt, deadline,
                    _service._clock.Frequency);
                // A completion can race while we are constructing the immutable start;
                // a second check keeps a pre-Busy frame classified as EarlyFrame.
                if (_acquireTask.IsCompleted)
                {
                    _innerCompleted = true;
                    ProcessCompletedTaskLocked(_acquireTask, beforeBusy: true);
                    CompletePhysicalLocked();
                    return;
                }

                if (!_control.TryOpen(start))
                {
                    LatchFailureLocked(CameraAcquisitionFailureKind.ProtocolViolation,
                        "CameraAcquisitionControlOpenFailed", signalCancellation: true);
                    CompletePhysicalLocked();
                    return;
                }

                _opened = true;
                _start = start;
                _pendingTimeoutCancellation?.Cancel();
                _pendingTimeoutCancellation = null;
                try
                {
                    _deadlineHandle = _service._clock.Schedule(start.DeadlineTimestamp,
                        FrameAcquisitionClockPhase.Deadline, OnDeadline);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    LatchFailureLocked(CameraAcquisitionFailureKind.DeviceFault,
                        "CameraAcquisitionDeadlineScheduleFailed", signalCancellation: true);
                    CompletePhysicalLocked();
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LatchFailureLocked(CameraAcquisitionFailureKind.DeviceFault,
                    "CameraAcquisitionBusyStartFailed", signalCancellation: true);
                CompletePhysicalLocked();
            }
        }

        private void OnDeadline()
        {
            lock (_service._sync)
            {
                if (_terminal || !_opened || _start is null) return;
                if (_acquireTask is { IsCompleted: true } completed)
                {
                    _innerCompleted = true;
                    ProcessCompletedTaskLocked(completed, beforeBusy: false);
                    CompletePhysicalLocked();
                    return;
                }

                LatchFailureLocked(CameraAcquisitionFailureKind.TimedOut,
                    "CameraAcquisitionTimeout", signalCancellation: true);
                CompletePhysicalLocked();
            }
        }

        private void ProcessCompletedTaskLocked(Task<FrameAcquisitionResult> task,
            bool beforeBusy)
        {
            // The deadline callback and the task continuation can both observe the
            // same completed task. Extract and adjudicate its result exactly once;
            // otherwise a successful lease transferred at the deadline could be
            // mistaken for a late duplicate and disposed by the continuation.
            if (_resultConsumed) return;
            _resultConsumed = true;

            if (!task.IsCompletedSuccessfully)
            {
                _ = task.Exception;
                if (!_terminal)
                    LatchFailureLocked(task.IsCanceled || IsCancellationFault(task)
                            ? CameraAcquisitionFailureKind.Cancelled
                            : CameraAcquisitionFailureKind.DeviceFault,
                        task.IsCanceled || IsCancellationFault(task)
                            ? "CameraAcquisitionCancelled"
                            : beforeBusy ? "CameraAcquisitionBeforeBusyFailed" :
                                "CameraAcquisitionDeviceFault", signalCancellation: false);
                return;
            }

            FrameAcquisitionResult? result;
            try { result = task.Result; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (!_terminal)
                    LatchFailureLocked(CameraAcquisitionFailureKind.DeviceFault,
                        "CameraAcquisitionDeviceFault", signalCancellation: false);
                return;
            }
            ProcessResultLocked(result, beforeBusy);
        }

        private void ProcessResultLocked(FrameAcquisitionResult? result, bool beforeBusy)
        {
            // A faulty adapter can complete its task with a null reference despite
            // the non-nullable contract. Handle this before the terminal/late path
            // so the fault never dereferences a malformed result.
            if (result is null)
            {
                if (!_terminal)
                    LatchFailureLocked(CameraAcquisitionFailureKind.DeviceFault,
                        "CameraAcquisitionResultMissing", signalCancellation: false);
                return;
            }

            if (_terminal)
            {
                DisposeLateLeaseLocked(result.Lease);
                return;
            }

            if (result.Succeeded)
            {
                var lease = result.Lease!;
                if (beforeBusy)
                {
                    DisposeLateLeaseLocked(lease);
                    _service.AppendProtocolLocked(CameraProtocolViolationKind.EarlyFrame,
                        "CameraEarlyFrame", null, _request.Correlation, 0);
                    LatchFailureLocked(CameraAcquisitionFailureKind.ProtocolViolation,
                        "CameraEarlyFrame", signalCancellation: false);
                    return;
                }

                if (!TryValidateLease(lease, out var reason, out var readyTimestamp))
                {
                    DisposeLateLeaseLocked(lease);
                    var protocolKind = ValidationProtocolKind(reason);
                    var outcomeReason = protocolKind == CameraProtocolViolationKind.CorrelationMismatch
                        ? "CameraCorrelationMismatch"
                        : "CameraInvalidFrame";
                    _service.AppendProtocolLocked(protocolKind, reason, null,
                        _request.Correlation, 0);
                    LatchFailureLocked(CameraAcquisitionFailureKind.ProtocolViolation,
                        outcomeReason, signalCancellation: false);
                    return;
                }

                if (_start is null || readyTimestamp < _start.BusyAt.MonotonicTimestamp)
                {
                    DisposeLateLeaseLocked(lease);
                    _service.AppendProtocolLocked(CameraProtocolViolationKind.EarlyFrame,
                        "CameraFrameReadyBeforeBusy", null, _request.Correlation, 0);
                    LatchFailureLocked(CameraAcquisitionFailureKind.ProtocolViolation,
                        "CameraFrameReadyBeforeBusy", signalCancellation: false);
                    return;
                }

                if (readyTimestamp > _start.DeadlineTimestamp)
                {
                    DisposeLateLeaseLocked(lease);
                    _service.AppendProtocolLocked(CameraProtocolViolationKind.LateFrame,
                        "CameraLateFrame", null, _request.Correlation, 0);
                    LatchFailureLocked(CameraAcquisitionFailureKind.TimedOut,
                        "CameraAcquisitionTimeout", signalCancellation: false);
                    return;
                }

                LatchSuccessLocked(lease);
                return;
            }

            var failure = result.Failure ??
                new CameraAcquisitionFailure(CameraAcquisitionFailureKind.DeviceFault,
                    "CameraAcquisitionResultMissingFailure");
            if (_start is not null && _service._clock.GetTimePoint().MonotonicTimestamp >
                _start.DeadlineTimestamp && failure.Kind is not CameraAcquisitionFailureKind.Cancelled)
            {
                LatchFailureLocked(CameraAcquisitionFailureKind.TimedOut,
                    "CameraAcquisitionTimeout", signalCancellation: true);
                return;
            }

            LatchFailureLocked(failure.Kind, failure.ReasonCode, signalCancellation: false);
        }

        private bool TryValidateLease(IFrameBufferLease lease, out string reason,
            out long readyTimestamp)
        {
            reason = "CameraFrameValidationFailed";
            readyTimestamp = -1;
            try
            {
                if (lease.Frame is not { } frame || lease.Provenance is not { } provenance)
                {
                    reason = "CameraFrameEvidenceMissing";
                    return false;
                }

                var metadata = frame.Metadata;
                if (metadata.Correlation != _request.Correlation)
                {
                    reason = "CameraFrameCorrelationMismatch";
                    return false;
                }
                if (!string.Equals(metadata.LogicalCameraRole, _request.LogicalCameraRole,
                    StringComparison.Ordinal))
                {
                    reason = "CameraFrameRoleMismatch";
                    return false;
                }
                if (!Equals(metadata.EffectiveCameraConfiguration, _service._configuration))
                {
                    reason = "CameraFrameConfigurationMismatch";
                    return false;
                }
                if (provenance.Correlation != _request.Correlation)
                {
                    reason = "CameraProvenanceCorrelationMismatch";
                    return false;
                }

                var descriptor = _service._descriptor;
                if (!string.Equals(provenance.ProviderId, descriptor.Provider.Id,
                    StringComparison.Ordinal) ||
                    !string.Equals(provenance.ProviderVersion, descriptor.Provider.Version,
                        StringComparison.Ordinal) ||
                    !string.Equals(provenance.AdapterId, descriptor.Provider.AdapterPackageId,
                        StringComparison.Ordinal) ||
                    !string.Equals(provenance.AdapterVersion, descriptor.Provider.AdapterVersion,
                        StringComparison.Ordinal) ||
                    !string.Equals(provenance.StableDeviceIdentity,
                        descriptor.StableDeviceIdentity, StringComparison.Ordinal))
                {
                    reason = "CameraFrameSourceMismatch";
                    return false;
                }

                var milestones = provenance.Milestones;
                if (milestones.MonotonicFrequency != _service._clock.Frequency ||
                    milestones.NormalizedFrameReady is not { } ready)
                {
                    reason = "CameraFrameMilestonesInvalid";
                    return false;
                }

                readyTimestamp = ready.MonotonicTimestamp;
                return true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                reason = "CameraFrameValidationFailed";
                return false;
            }
        }

        private static CameraProtocolViolationKind ValidationProtocolKind(string reason) =>
            reason is "CameraFrameCorrelationMismatch" or "CameraProvenanceCorrelationMismatch" or
                "CameraFrameRoleMismatch"
                ? CameraProtocolViolationKind.CorrelationMismatch
                : CameraProtocolViolationKind.InvalidFrame;

        private void LatchSuccessLocked(IFrameBufferLease lease)
        {
            if (_terminal)
            {
                DisposeLateLeaseLocked(lease);
                return;
            }

            var trackedLease = new TrackedLease(_service, lease);
            _service.TrackLeaseLocked(trackedLease);
            _terminal = true;
            CloseHandlesLocked();
            _control.Close();
            Completion.TrySetResult(new CameraAcquisitionOutcome(_request.Correlation,
                _request.LogicalCameraRole, _start, trackedLease, null, "CameraFrameAcquired",
                ExecutionStatus.Success));
        }

        private void LatchFailureLocked(CameraAcquisitionFailureKind kind, string reasonCode,
            bool signalCancellation)
        {
            if (_terminal) return;
            _terminal = true;
            CloseHandlesLocked();
            _control.Close();
            if (signalCancellation) QueueCancellationLocked();
            var failure = new CameraAcquisitionFailure(kind, reasonCode);
            Completion.TrySetResult(new CameraAcquisitionOutcome(_request.Correlation,
                _request.LogicalCameraRole, _start, null, failure, reasonCode,
                ToExecutionStatus(kind)));
        }

        private static ExecutionStatus ToExecutionStatus(CameraAcquisitionFailureKind kind) =>
            kind switch
            {
                CameraAcquisitionFailureKind.Cancelled => ExecutionStatus.Cancelled,
                CameraAcquisitionFailureKind.TimedOut => ExecutionStatus.Timeout,
                _ => ExecutionStatus.Error
            };

        private void QueueCancellationLocked()
        {
            if (_cancelSignalled) return;
            _cancelSignalled = true;
            _cancelTask = Task.Run(() =>
            {
                try { _physicalCancellation.Cancel(); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            });
            _ = _cancelTask.ContinueWith(static (_, state) =>
                ((Attempt)state!).OnCancellationCompleted(), this,
                CancellationToken.None, TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        private void OnCancellationCompleted()
        {
            lock (_service._sync) CompletePhysicalLocked();
        }

        private void CloseHandlesLocked()
        {
            var pendingTimeoutCancellation = _pendingTimeoutCancellation;
            _pendingTimeoutCancellation = null;
            if (pendingTimeoutCancellation is not null)
            {
                try { pendingTimeoutCancellation.Cancel(); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
                pendingTimeoutCancellation.Dispose();
            }
            _deadlineHandle?.Dispose();
            _deadlineHandle = null;
        }

        private static bool IsCancellationFault(Task<FrameAcquisitionResult> task) =>
            task.Exception?.GetBaseException() is OperationCanceledException;

        private void CompletePhysicalLocked()
        {
            if (_physicalCompleted || !_invocationReturned || !_innerCompleted ||
                (_cancelTask is not null && !_cancelTask.IsCompleted) ||
                (_leaseDisposalTask is not null && !_leaseDisposalTask.IsCompletedSuccessfully))
                return;

            _physicalCompleted = true;
            CloseHandlesLocked();
            if (ReferenceEquals(_service._attempt, this))
                _service._attempt = null;
            PhysicalCompletion.TrySetResult(true);
            // Only pull provider protocol facts after every physical acquire and
            // any late lease disposal has quiesced. This avoids invoking a device
            // read concurrently with an adapter callback or a lease owner.
            _service.QueueProtocolRefreshLocked();
            if (_callerRegistrationSet)
            {
                var registration = _callerRegistration;
                _callerRegistrationSet = false;
                _ = Task.Run(() => registration.Dispose());
            }
            _ = Task.Run(() => _physicalCancellation.Dispose());
        }

        private void DisposeLateLeaseLocked(IFrameBufferLease? lease)
        {
            if (lease is null) return;
            var owned = new TrackedLease(_service, lease);
            _service.TrackLeaseLocked(owned);
            // Lease disposal is adapter/owner code. Never invoke it while holding
            // the service gate; a provider may synchronously re-enter Runtime or
            // block while returning its pool slot.
            var disposal = Task.Factory.StartNew(async state =>
            {
                var trackedLease = (TrackedLease)state!;
                trackedLease.Dispose();
                // Outer Dispose may leave native readers alive. Late/invalid
                // frames need the same actual return proof as transferred frames.
                if (!await trackedLease.ReturnCompletion.ConfigureAwait(false))
                    throw new InvalidOperationException("CameraLeaseReleaseFailed");
            }, owned, CancellationToken.None, TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap();
            _leaseDisposalTask = _leaseDisposalTask is null
                ? disposal
                : Task.WhenAll(_leaseDisposalTask, disposal);
            var tracked = _leaseDisposalTask;
            _ = tracked.ContinueWith(static (task, state) =>
            {
                // Observe a disposal fault while retaining the faulted task as a
                // permanent ownership barrier. A failed or hung lease cleanup must
                // keep the attempt blocked and must never permit Stop/next acquire.
                _ = task.Exception;
                ((Attempt)state!).OnLeaseDisposalCompleted();
            }, this, CancellationToken.None, TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        private void OnLeaseDisposalCompleted()
        {
            lock (_service._sync) CompletePhysicalLocked();
        }

        private static long AddDuration(long timestamp, TimeSpan duration, long frequency)
        {
            var ticks = Math.Ceiling(duration.TotalSeconds * frequency);
            if (!double.IsFinite(ticks) || ticks < 1 || ticks > long.MaxValue - timestamp)
                throw new ArgumentOutOfRangeException(nameof(duration));
            return checked(timestamp + (long)ticks);
        }
    }
}
