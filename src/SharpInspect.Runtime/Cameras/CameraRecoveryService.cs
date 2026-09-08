using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Cameras;

/// <summary>
/// Owns one bound camera recovery cycle. The dedicated provider is transferred
/// once to this service when construction succeeds and is released only after every opened
/// device owner has completed its actual stop/dispose chain. The clock remains
/// caller-owned. This service is qualification-only and never grants production
/// readiness.
/// </summary>
public sealed class CameraRecoveryService : IAsyncDisposable
{
    private const int MaximumProtocolReadCount = 64;

    private const string ReasonDisposed = "CameraRecoveryDisposed";
    private const string ReasonBusy = "CameraRecoveryBusy";
    private const string ReasonUnknown = "CameraHealthUnknown";
    private const string ReasonHealthy = "CameraSourceHealthy";
    private const string ReasonUnavailable = "CameraRecoveryUnavailable";
    private const string ReasonProtocolUnavailable = "CameraProtocolUnavailable";
    private const string ReasonProtocolGap = "CameraProtocolObservationGap";
    private const string ReasonProtocolReadTimeout = "CameraProtocolReadTimeout";
    private const string ReasonProtocolReadFailed = "CameraProtocolReadFailed";
    private const string ReasonHealthUnavailable = "CameraHealthUnavailable";
    private const string ReasonHealthProbeTimeout = "CameraHealthProbeTimeout";
    private const string ReasonConnectionLost = "CameraConnectionLost";
    private const string ReasonProviderUnavailable = "CameraProviderUnavailable";
    private const string ReasonConnectionUnavailable = "CameraConnectionUnavailable";
    private const string ReasonConfigurationUnavailable = "CameraConfigurationUnavailable";
    private const string ReasonAcquisitionUnavailable = "CameraAcquisitionUnavailable";
    private const string ReasonCycleFailed = "CameraRecoveryFailed";
    private const string ReasonCandidateOpenFailed = "CameraRecoveryCandidateOpenFailed";
    private const string ReasonCandidateIdentityMismatch =
        "CameraRecoveryCandidateIdentityMismatch";
    private const string ReasonCandidateNotControlled =
        "CameraRecoveryCandidateNotControlled";
    private const string ReasonConfigurationInvalid =
        "CameraRecoveryConfigurationInvalid";
    private const string ReasonConfigurationApplyFailed =
        "CameraRecoveryConfigurationApplyFailed";
    private const string ReasonReadBackFailed = "CameraRecoveryReadBackFailed";
    private const string ReasonStartFailed = "CameraRecoveryStartFailed";
    private const string ReasonCandidateHealthFailed = "CameraRecoveryHealthFailed";
    private const string ReasonRetirementUnsafe = "CameraRecoveryRetirementUnsafe";
    private const string ReasonRetirementTimeout = "CameraRecoveryRetirementTimeout";
    private const string ReasonOperationTimeout = "CameraRecoveryOperationTimeout";
    private const string ReasonScheduleFailed = "CameraRecoveryScheduleFailed";
    private const string ReasonRetryScheduled = "CameraRecoveryRetryScheduled";
    private const string ReasonCompleted = "CameraRecoveryCompleted";
    private const string ReasonRestartNotExhausted = "CameraRecoveryRestartNotExhausted";
    private const string ReasonCycleMismatch = "CameraRecoveryCycleMismatch";
    private const string ReasonRestartAlreadyReserved =
        "CameraRecoveryRestartAlreadyReserved";
    private const string ReasonRestartCommitted = "CameraRecoveryRestartCommitted";

    private readonly object _sync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly ICameraProvider _provider;
    private readonly CameraBindingTarget _target;
    private readonly string _logicalCameraRole;
    private readonly RequestedCameraConfiguration _requested;
    private readonly CameraAcquisitionService _initialAcquisition;
    private readonly CameraAcquisitionOptions _acquisitionOptions;
    private readonly IFrameAcquisitionClock _clock;
    private readonly CameraRecoveryOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Guid _recoveryEpoch = NewGuid();
    private readonly Queue<CameraRecoveryEvent> _events = new();
    private readonly Queue<CameraProtocolObservation> _protocol = new();

    private CameraAcquisitionService _current;
    private CameraAcquisitionService? _candidate;
    private ICameraDevice? _candidateRaw;
    private CameraHealthSnapshot? _health;
    private CameraRecoveryState _state = CameraRecoveryState.Unknown;
    private bool _sourceHealthy;
    private string _reasonCode = ReasonUnknown;
    private long _healthObservationRevision = 1;
    private long _revision;
    private long _nextEventSequence = 1;
    private long _nextProtocolSequence = 1;
    private Guid? _cycleId;
    private int _attemptCount;
    private FrameTimePoint? _nextAttemptAt;
    private IDisposable? _scheduledAttempt;
    private Task? _cycleStepTask;
    private Task? _physicalTask;
    private Task? _disposeTask;
    private bool _disposed;
    private bool _sourceRetired;
    private bool _protocolFaulted;
    private CameraAcquisitionService? _protocolOwner;
    private Guid? _childProtocolEpoch;
    private long _childProtocolCursor;
    private CameraRecoveryRestartReservation? _restartReservation;
    private bool _providerDisposeAttempted;
    private bool _providerDisposed;
    private bool _candidateRetirementFailed;
    private string _candidateRetirementFailureReason = ReasonRetirementUnsafe;
    private Task<CameraDeviceRetirement>? _candidateRetirementTask;
    private Task<CameraOpenResult>? _lateOpenTask;

    /// <summary>
    /// Creates a recovery engine for one exact provider and stable device binding.
    /// The initial acquisition service is the prepared seed and remains
    /// the first owner until a complete candidate has passed configuration,
    /// read-back, start, health and protocol qualification.
    /// </summary>
    public CameraRecoveryService(ICameraProvider provider, CameraBindingTarget target,
        string logicalRole, RequestedCameraConfiguration requested,
        CameraAcquisitionService initialAcquisition, IFrameAcquisitionClock clock,
        CameraRecoveryOptions options)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _logicalCameraRole = FrameMetadataValidation.Identifier(logicalRole,
            nameof(logicalRole));
        _requested = requested ?? throw new ArgumentNullException(nameof(requested));
        _initialAcquisition = initialAcquisition ??
            throw new ArgumentNullException(nameof(initialAcquisition));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (initialAcquisition.IsDisposed)
            throw new ArgumentException("CameraRecoveryInitialAcquisitionDisposed",
                nameof(initialAcquisition));
        if (!ReferenceEquals(clock, initialAcquisition.Clock))
            throw new ArgumentException("CameraRecoveryClockMismatch", nameof(clock));

        var providerIdentity = provider.Identity ?? throw new ArgumentException(
            "CameraProviderIdentityUnavailable", nameof(provider));
        EnsureProviderIdentity(providerIdentity, target.Provider, nameof(provider));
        EnsureBinding(initialAcquisition.Descriptor, target,
            "CameraRecoveryInitialDescriptorMismatch");

        CameraConfigurationResult expected;
        try { expected = initialAcquisition.Capabilities.ValidateConfiguration(requested); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new ArgumentException("CameraRecoveryRequestedConfigurationInvalid",
                nameof(requested), exception);
        }

        if (!expected.Succeeded || expected.Effective is null ||
            !initialAcquisition.Configuration.Equals(expected.Effective))
            throw new ArgumentException("CameraRecoveryInitialConfigurationMismatch",
                nameof(initialAcquisition));

        _acquisitionOptions = initialAcquisition.Options;
        _current = initialAcquisition;
    }

    /// <summary>Returns a safe immutable projection.</summary>
    public CameraRecoverySnapshot GetSnapshot()
    {
        lock (_sync)
            return BuildSnapshotLocked();
    }

    /// <summary>Recovery qualification never grants production readiness.</summary>
    public bool Ready => false;

    /// <summary>
    /// Monotonic proof counter for Station heartbeats. It advances only after an
    /// actual non-null health probe completes (including a typed unhealthy result),
    /// never when a cached snapshot is returned while the operation gate is busy.
    /// </summary>
    internal long HealthObservationRevision
    {
        get { lock (_sync) return _healthObservationRevision; }
    }

    /// <summary>
    /// Reads the bounded recovery event ring. A cursor gap is represented by
    /// <see cref="CameraRecoveryEventPage.Overflowed"/> and must be handled as a
    /// fail-closed observation failure by the station.
    /// </summary>
    public CameraRecoveryEventPage ReadEvents(long afterSequence,
        int maximumCount = CameraRecoveryOptions.MaximumEventReadCount)
    {
        if (afterSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (maximumCount is < 1 or > CameraRecoveryOptions.MaximumEventReadCount)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));

        lock (_sync)
        {
            var through = _nextEventSequence - 1;
            if (afterSequence > through)
                throw new ArgumentOutOfRangeException(nameof(afterSequence),
                    "CameraRecoveryCursorBeyondThroughSequence");

            var first = _events.Count == 0 ? 1 : _events.Peek().Sequence;
            var overflowed = afterSequence < first - 1;
            var firstRequested = overflowed ? first : afterSequence + 1;
            var events = _events.Where(item => item.Sequence >= firstRequested)
                .Take(maximumCount).ToArray();
            return new CameraRecoveryEventPage(_recoveryEpoch, first, through,
                overflowed, events);
        }
    }

    /// <summary>
    /// Reads actual health on the current owner. Only a disconnected or typed
    /// ConnectionLost observation starts an automatic bounded recovery cycle.
    /// </summary>
    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_disposed || _state is CameraRecoveryState.Recovering or
                CameraRecoveryState.Exhausted)
                return;
            if (_protocolFaulted)
            {
                SetStateLocked(CameraRecoveryState.RecoveryRequired, false,
                    ReasonProtocolUnavailable);
                return;
            }
        }

        if (!await TryEnterOperationAsync(cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            CameraAcquisitionService current;
            lock (_sync)
            {
                if (_disposed || _state is CameraRecoveryState.Recovering or
                    CameraRecoveryState.Exhausted)
                    return;
                current = _current;
            }

            if (!await PullProtocolAsync(current, cancellationToken).ConfigureAwait(false))
                return;

            Task<CameraHealthSnapshot?> healthTask;
            lock (_sync)
            {
                if (_disposed || _protocolFaulted || !ReferenceEquals(_current, current))
                    return;
                if (!CanStartPhysicalLocked())
                {
                    SetStateLocked(CameraRecoveryState.RecoveryRequired, false,
                        ReasonBusy);
                    return;
                }
                healthTask = StartPhysicalLocked(() => current.ReadHealthAsync());
            }

            CameraHealthSnapshot? health;
            try
            {
                health = await healthTask.WaitAsync(_options.OperationTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                lock (_sync)
                {
                    SetStateLocked(CameraRecoveryState.RecoveryRequired, false,
                        ReasonHealthProbeTimeout);
                }
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                lock (_sync)
                {
                    SetStateLocked(CameraRecoveryState.RecoveryRequired, false,
                        ReasonHealthUnavailable);
                }
                return;
            }

            lock (_sync)
            {
                if (_disposed || !ReferenceEquals(_current, current))
                    return;

                _health = health;
                if (health is null)
                {
                    SetStateLocked(CameraRecoveryState.RecoveryRequired, false,
                        ReasonHealthUnavailable);
                    return;
                }

                _healthObservationRevision++;

                var sourceHealthy = IsHealthy(health);
                var connectionLost = IsConnectionLost(health);
                if (connectionLost)
                {
                    SetStateLocked(CameraRecoveryState.RecoveryRequired, false,
                        ReasonConnectionLost);
                    if (!_protocolFaulted && _state != CameraRecoveryState.Exhausted)
                        BeginAutomaticCycleLocked(ReasonConnectionLost);
                    return;
                }

                if (sourceHealthy)
                {
                    SetStateLocked(CameraRecoveryState.Healthy, true, ReasonHealthy);
                    return;
                }

                SetStateLocked(CameraRecoveryState.RecoveryRequired, false,
                    HealthReason(health));
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Delegates one qualification acquisition to the current healthy owner.
    /// Production acquisition is always rejected without allocating an ID.
    /// </summary>
    public async ValueTask<CameraAcquisitionAttempt> AcquireAsync(ExecutionKind kind,
        string logicalCameraRole, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(typeof(ExecutionKind), kind))
            return Rejected("CameraAcquisitionExecutionKindInvalid");
        if (kind != ExecutionKind.Qualification)
            return Rejected("ProductionAcquisitionUnavailable");
        if (cancellationToken.IsCancellationRequested)
            return Rejected("CameraAcquisitionCancelledBeforeStart");

        try
        {
            _ = FrameMetadataValidation.Identifier(logicalCameraRole,
                nameof(logicalCameraRole));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Rejected("CameraLogicalCameraRoleInvalid");
        }

        if (!StringComparer.Ordinal.Equals(logicalCameraRole, _logicalCameraRole))
            return Rejected("CameraRecoveryLogicalRoleMismatch");

        CameraRecoverySnapshot snapshot;
        lock (_sync) snapshot = BuildSnapshotLocked();
        if (snapshot.State == CameraRecoveryState.Unknown || !snapshot.SourceHealthy)
        {
            try { await RefreshAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { return Rejected("CameraAcquisitionCancelledBeforeStart"); }
        }

        if (!await TryEnterOperationAsync(cancellationToken).ConfigureAwait(false))
            return Rejected(ReasonBusy);

        try
        {
            CameraAcquisitionService current;
            lock (_sync)
            {
                if (_disposed) return Rejected(ReasonDisposed);
                if (_protocolFaulted) return Rejected(ReasonProtocolUnavailable);
                if (_state != CameraRecoveryState.Healthy || !_sourceHealthy ||
                    _cycleId is not null && _state == CameraRecoveryState.Recovering)
                    return Rejected("CameraRecoverySourceUnavailable");
                current = _current;
            }

            if (!await PullProtocolAsync(current, cancellationToken).ConfigureAwait(false))
                return Rejected(ReasonProtocolUnavailable);

            lock (_sync)
            {
                if (_disposed) return Rejected(ReasonDisposed);
                if (_protocolFaulted || _state != CameraRecoveryState.Healthy ||
                    !_sourceHealthy || !ReferenceEquals(_current, current))
                    return Rejected("CameraRecoverySourceUnavailable");
            }

            Task<CameraAcquisitionAttempt> acquisitionTask;
            lock (_sync)
            {
                if (!CanStartPhysicalLocked()) return Rejected(ReasonBusy);
                acquisitionTask = StartPhysicalLocked(() => current.AcquireAsync(
                    ExecutionKind.Qualification, logicalCameraRole,
                    cancellationToken).AsTask());
            }

            try
            {
                return await acquisitionTask.WaitAsync(_options.OperationTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return Rejected(ReasonOperationTimeout);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Rejected("CameraAcquisitionCancelled");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return Rejected("CameraAcquisitionFailed");
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Pulls and aggregates the child T18 protocol ring into the stable recovery
    /// epoch. This remains internal so the public recovery surface cannot become a
    /// second station protocol API.
    /// </summary>
    internal async ValueTask<CameraProtocolSnapshot> RefreshProtocolObservationsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CameraAcquisitionService current;
        lock (_sync)
        {
            current = _current;
            if (_protocolFaulted)
                return ReadProtocolObservationsLocked(0, MaximumProtocolReadCount);
        }

        if (!await TryEnterOperationAsync(cancellationToken).ConfigureAwait(false))
            return ReadProtocolObservations(0, MaximumProtocolReadCount);
        try
        {
            await PullProtocolAsync(current, cancellationToken).ConfigureAwait(false);
            return ReadProtocolObservations(0, MaximumProtocolReadCount);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Reads the recovery-owned aggregated protocol ring.</summary>
    internal CameraProtocolSnapshot ReadProtocolObservations(long afterSequence,
        int maximumCount = MaximumProtocolReadCount)
    {
        if (afterSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (maximumCount is < 1 or > MaximumProtocolReadCount)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        lock (_sync) return ReadProtocolObservationsLocked(afterSequence, maximumCount);
    }

    /// <summary>
    /// Reserves one authorized restart after exhaustion. The reservation pins the
    /// exhausted cycle until Commit succeeds or the reservation is disposed.
    /// </summary>
    internal CameraRecoveryRestartReservation? TryReserveRestart(Guid expectedCycleId,
        out string reason)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                reason = ReasonDisposed;
                return null;
            }
            if (_state != CameraRecoveryState.Exhausted)
            {
                reason = ReasonRestartNotExhausted;
                return null;
            }
            if (_cycleId != expectedCycleId || expectedCycleId == Guid.Empty)
            {
                reason = ReasonCycleMismatch;
                return null;
            }
            if (_restartReservation is not null)
            {
                reason = ReasonRestartAlreadyReserved;
                return null;
            }

            var reservation = new CameraRecoveryRestartReservation(this, expectedCycleId);
            _restartReservation = reservation;
            reason = "CameraRecoveryRestartReserved";
            return reservation;
        }
    }

    /// <summary>Disposes the engine after actual device/provider work has drained.</summary>
    public async ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_sync)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _sourceHealthy = false;
                _state = CameraRecoveryState.Disposed;
                _reasonCode = ReasonDisposed;
                _nextAttemptAt = null;
                _revision++;
                _restartReservation = null;
                _disposeTask = Task.Run(DisposeCoreAsync);
            }

            disposeTask = _disposeTask;
        }

        _lifetime.Cancel();
        try { _scheduledAttempt?.Dispose(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }

        try
        {
            await disposeTask.WaitAsync(_options.ShutdownWaitTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The unbounded owner task remains reachable through _disposeTask and
            // retains every late provider/device operation after this return.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Provider and adapter exception text never crosses this boundary.
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await WaitForCycleAndPhysicalDrainAsync().ConfigureAwait(false);
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                CameraAcquisitionService? candidate;
                ICameraDevice? raw;
                CameraAcquisitionService current;
                lock (_sync)
                {
                    ReconcileLateOpenedDeviceLocked();
                    candidate = _candidate;
                    raw = _candidateRaw;
                    current = _current;
                }

                var candidateRetired = true;
                if (candidate is not null || raw is not null)
                    candidateRetired = (await CleanupCandidateAsync(bounded: false).ConfigureAwait(false))
                        .SafeToReplace;

                var currentRetired = true;
                try { await PullProtocolAsync(current, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }

                Task<CameraDeviceRetirement> retirementTask;
                lock (_sync)
                {
                    retirementTask = StartPhysicalLocked(() => current.BeginRetirement());
                }

                try
                {
                    var retirement = await retirementTask.ConfigureAwait(false);
                    currentRetired = retirement.SafeToReplace;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    currentRetired = false;
                }

                if (!candidateRetired || !currentRetired)
                    return;

                await DisposeProviderAsync().ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Keep all owner fields alive. A later caller cannot reopen a disposed
            // engine, and a provider fault remains an actual-owner responsibility.
        }
    }

    private async Task DisposeProviderAsync()
    {
        lock (_sync)
        {
            if (_providerDisposed || _providerDisposeAttempted)
                return;
            _providerDisposeAttempted = true;
        }

        Task disposeTask;
        lock (_sync) disposeTask = StartPhysicalLocked(() => _provider.DisposeAsync().AsTask());
        try
        {
            await disposeTask.ConfigureAwait(false);
            lock (_sync) _providerDisposed = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The provider remains strongly reachable and is never retried
            // concurrently. Its actual fault is represented only by a stable owner
            // failure, never by SDK exception text.
        }
    }

    private async Task WaitForCycleAndPhysicalDrainAsync()
    {
        while (true)
        {
            Task? cycle;
            Task? physical;
            lock (_sync)
            {
                cycle = _cycleStepTask;
                physical = _physicalTask;
            }

            if (cycle is not null)
            {
                try { await cycle.ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
                continue;
            }

            if (physical is not null)
            {
                try { await physical.ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
                lock (_sync)
                {
                    if (ReferenceEquals(_physicalTask, physical) && physical.IsCompleted)
                        _physicalTask = null;
                }
                continue;
            }

            return;
        }
    }

    private async Task<CameraDeviceRetirement> CleanupCandidateAsync(bool bounded = true)
    {
        CameraAcquisitionService? candidate;
        ICameraDevice? raw;
        lock (_sync)
        {
            ReconcileLateOpenedDeviceLocked();
            ReconcileCompletedCandidateRetirementLocked();
            if (_candidateRetirementFailed)
                return new CameraDeviceRetirement(false, _candidateRetirementFailureReason);
            candidate = _candidate;
            raw = _candidateRaw;
        }

        if (candidate is not null)
        {
            try { await PullProtocolAsync(candidate, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }

            Task<CameraDeviceRetirement> retirementTask;
            lock (_sync)
            {
                if (!CanStartPhysicalLocked())
                    return new CameraDeviceRetirement(false, ReasonRetirementTimeout);
                retirementTask = StartPhysicalLocked(() => candidate.BeginRetirement());
                _candidateRetirementTask = retirementTask;
            }
            ObserveCandidateRetirement(candidate, retirementTask);
            try
            {
                var retirement = bounded
                    ? await retirementTask.WaitAsync(_options.OperationTimeout).ConfigureAwait(false)
                    : await retirementTask.ConfigureAwait(false);
                if (retirement.SafeToReplace)
                {
                    lock (_sync)
                    {
                        _candidate = null;
                        _candidateRaw = null;
                        _candidateRetirementTask = null;
                        _candidateRetirementFailed = false;
                        _candidateRetirementFailureReason = ReasonRetirementUnsafe;
                    }
                }
                else
                {
                    lock (_sync)
                    {
                        _candidateRetirementFailed = true;
                        _candidateRetirementFailureReason = NormalizeReason(
                            retirement.ReasonCode, ReasonRetirementUnsafe);
                        _candidateRetirementTask = null;
                    }
                }
                return retirement;
            }
            catch (TimeoutException)
            {
                return new CameraDeviceRetirement(false, ReasonRetirementTimeout);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                lock (_sync)
                {
                    _candidateRetirementFailed = true;
                    _candidateRetirementFailureReason = ReasonRetirementUnsafe;
                    _candidateRetirementTask = null;
                }
                return new CameraDeviceRetirement(false, ReasonRetirementUnsafe);
            }
        }

        if (raw is not null)
        {
            var retirement = await RetireRawAsync(raw, bounded).ConfigureAwait(false);
            if (retirement.SafeToReplace)
            {
                lock (_sync)
                {
                    _candidateRaw = null;
                    _candidateRetirementTask = null;
                    _candidateRetirementFailed = false;
                    _candidateRetirementFailureReason = ReasonRetirementUnsafe;
                }
            }
            else
            {
                lock (_sync)
                {
                    _candidateRetirementFailed = true;
                    _candidateRetirementFailureReason = NormalizeReason(
                        retirement.ReasonCode, ReasonRetirementUnsafe);
                    _candidateRetirementTask = null;
                }
            }
            return retirement;
        }

        return new CameraDeviceRetirement(true, "CameraCandidateAbsent");
    }

    private async Task<CameraDeviceRetirement> RetireRawAsync(ICameraDevice raw, bool bounded)
    {
        Task<CameraDeviceRetirement> retirementTask;
        lock (_sync)
        {
            if (!CanStartPhysicalLocked())
                return new CameraDeviceRetirement(false, ReasonRetirementTimeout);
            retirementTask = StartPhysicalLocked(() => RetireRawPhysicalAsync(raw));
            _candidateRetirementTask = retirementTask;
        }
        ObserveRawRetirement(raw, retirementTask);
        try
        {
            return bounded
                ? await retirementTask.WaitAsync(_options.OperationTimeout).ConfigureAwait(false)
                : await retirementTask.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new CameraDeviceRetirement(false, ReasonRetirementTimeout);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_sync)
            {
                _candidateRetirementFailed = true;
                _candidateRetirementFailureReason = ReasonRetirementUnsafe;
                _candidateRetirementTask = null;
            }
            return new CameraDeviceRetirement(false, ReasonRetirementUnsafe);
        }
    }

    private static async Task<CameraDeviceRetirement> RetireRawPhysicalAsync(ICameraDevice raw)
    {
        CameraOperationResult? stopResult = null;
        try { stopResult = await raw.StopAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }

        var stopSucceeded = stopResult?.Succeeded == true;
        var disposeSucceeded = true;
        try { await raw.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { disposeSucceeded = false; }

        if (!stopSucceeded || !disposeSucceeded)
            return new CameraDeviceRetirement(false, ReasonRetirementUnsafe);
        return new CameraDeviceRetirement(true, "CameraDeviceRetired");
    }

    private void ObserveCandidateRetirement(CameraAcquisitionService candidate,
        Task<CameraDeviceRetirement> retirementTask)
    {
        _ = retirementTask.ContinueWith(completed =>
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_candidate, candidate)) return;
                if (completed.Status == TaskStatus.RanToCompletion &&
                    completed.Result.SafeToReplace)
                {
                    _candidate = null;
                    _candidateRaw = null;
                    _candidateRetirementTask = null;
                    _candidateRetirementFailed = false;
                    _candidateRetirementFailureReason = ReasonRetirementUnsafe;
                }
                else
                {
                    _candidateRetirementFailed = true;
                    _candidateRetirementFailureReason = ReasonRetirementUnsafe;
                    _candidateRetirementTask = null;
                }
            }
            TryResumeCycleAfterRetirement();
        }, CancellationToken.None, TaskContinuationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default);
    }

    private void ObserveRawRetirement(ICameraDevice raw,
        Task<CameraDeviceRetirement> retirementTask)
    {
        _ = retirementTask.ContinueWith(completed =>
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_candidateRaw, raw)) return;
                if (completed.Status == TaskStatus.RanToCompletion &&
                    completed.Result.SafeToReplace)
                {
                    _candidateRaw = null;
                    _candidateRetirementTask = null;
                    _candidateRetirementFailed = false;
                    _candidateRetirementFailureReason = ReasonRetirementUnsafe;
                }
                else
                {
                    _candidateRetirementFailed = true;
                    _candidateRetirementFailureReason = ReasonRetirementUnsafe;
                    _candidateRetirementTask = null;
                }
            }
            TryResumeCycleAfterRetirement();
        }, CancellationToken.None, TaskContinuationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default);
    }

    private void ObserveSourceRetirement(CameraAcquisitionService current,
        Task<CameraDeviceRetirement> retirementTask)
    {
        _ = retirementTask.ContinueWith(completed =>
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_current, current)) return;
                if (completed.Status == TaskStatus.RanToCompletion &&
                    completed.Result.SafeToReplace)
                {
                    _sourceRetired = true;
                }
            }
            TryResumeCycleAfterRetirement();
        }, CancellationToken.None, TaskContinuationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default);
    }

    private void TryResumeCycleAfterRetirement()
    {
        lock (_sync)
        {
            ReconcileLateOpenedDeviceLocked();
            ReconcileCompletedCandidateRetirementLocked();
            if (_disposed || _protocolFaulted || _state != CameraRecoveryState.RecoveryRequired ||
                _cycleId is not { } cycleId || !_sourceRetired ||
                _candidateRetirementFailed || _candidateRetirementTask is not null ||
                _lateOpenTask is not null ||
                !CanStartPhysicalLocked())
                return;

            if (_attemptCount >= _options.MaximumAttempts)
            {
                SetStateLocked(CameraRecoveryState.Exhausted, false, ReasonCycleFailed);
                AppendEventLocked(CameraRecoveryEventKind.CycleExhausted, cycleId,
                    _attemptCount, ReasonCycleFailed);
                return;
            }

            SetStateLocked(CameraRecoveryState.Recovering, false,
                ReasonRetryScheduled);
            ScheduleNextAttemptLocked(cycleId, GetTimePoint().MonotonicTimestamp);
        }
    }

    private void ObserveTimedOutRecoveryOperation(Task operationTask)
    {
        _ = operationTask.ContinueWith(_ => TryResumeCycleAfterRetirement(),
            CancellationToken.None, TaskContinuationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default);
    }

    private void ReconcileLateOpenedDeviceLocked()
    {
        var openTask = _lateOpenTask;
        if (openTask is null || !openTask.IsCompleted)
            return;

        _lateOpenTask = null;
        try
        {
            if (openTask.Status == TaskStatus.RanToCompletion &&
                openTask.Result is { Succeeded: true, Device: not null } result &&
                _candidateRaw is null)
                _candidateRaw = result.Device;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A late provider fault has no replacement owner to retain.
        }
    }

    private void ReconcileCompletedCandidateRetirementLocked()
    {
        var retirementTask = _candidateRetirementTask;
        if (retirementTask is null || !retirementTask.IsCompleted)
            return;

        if (retirementTask.Status == TaskStatus.RanToCompletion)
        {
            CameraDeviceRetirement retirement;
            try { retirement = retirementTask.GetAwaiter().GetResult(); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _candidateRetirementFailed = true;
                _candidateRetirementFailureReason = ReasonRetirementUnsafe;
                _candidateRetirementTask = null;
                return;
            }

            if (retirement.SafeToReplace)
            {
                _candidate = null;
                _candidateRaw = null;
                _candidateRetirementFailed = false;
                _candidateRetirementFailureReason = ReasonRetirementUnsafe;
            }
            else
            {
                _candidateRetirementFailed = true;
                _candidateRetirementFailureReason = NormalizeReason(
                    retirement.ReasonCode, ReasonRetirementUnsafe);
            }
        }
        else
        {
            _candidateRetirementFailed = true;
            _candidateRetirementFailureReason = ReasonRetirementUnsafe;
        }

        _candidateRetirementTask = null;
    }

    private async Task<bool> PullProtocolAsync(CameraAcquisitionService owner,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_protocolFaulted)
                return false;
            if (!ReferenceEquals(_protocolOwner, owner))
            {
                // The old child was pulled by its caller before this explicit
                // switch. A new child starts a new adapter epoch/cursor, while the
                // recovery epoch and runtime sequence remain stable.
                _protocolOwner = owner;
                _childProtocolEpoch = null;
                _childProtocolCursor = 0;
            }
        }

        CameraProtocolSnapshot refreshed;
        Task<CameraProtocolSnapshot> refreshTask;
        lock (_sync)
        {
            if (!CanStartPhysicalLocked())
                return false;
            refreshTask = StartPhysicalLocked(() => owner.RefreshProtocolObservationsAsync()
                .AsTask());
        }

        try
        {
            refreshed = await refreshTask.WaitAsync(_options.OperationTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (_sync) LatchProtocolFaultLocked(ReasonProtocolReadTimeout);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_sync) LatchProtocolFaultLocked(ReasonProtocolReadFailed);
            return false;
        }

        if (!MergeProtocolPage(owner, refreshed))
            return false;

        while (true)
        {
            long cursor;
            long through;
            lock (_sync)
            {
                if (_protocolFaulted) return false;
                cursor = _childProtocolCursor;
                through = refreshed.ThroughSequence;
            }

            if (cursor >= through)
                return true;

            CameraProtocolSnapshot page;
            try
            {
                page = owner.ReadProtocolObservations(cursor, MaximumProtocolReadCount);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                lock (_sync) LatchProtocolFaultLocked(ReasonProtocolReadFailed);
                return false;
            }

            if (!MergeProtocolPage(owner, page))
                return false;
            if (page.ThroughSequence <= cursor && page.Observations.Count == 0)
            {
                lock (_sync) LatchProtocolFaultLocked(ReasonProtocolGap);
                return false;
            }
        }
    }

    private bool MergeProtocolPage(CameraAcquisitionService owner,
        CameraProtocolSnapshot source)
    {
        lock (_sync)
        {
            if (_protocolFaulted || !ReferenceEquals(_protocolOwner, owner))
                return false;

            if (_childProtocolEpoch is null)
                _childProtocolEpoch = source.Epoch;
            else if (_childProtocolEpoch.Value != source.Epoch)
            {
                LatchProtocolFaultLocked("CameraProtocolEpochChanged");
                return false;
            }

            if (source.ThroughSequence < _childProtocolCursor || source.Overflowed ||
                source.FirstAvailableSequence > _childProtocolCursor + 1)
            {
                LatchProtocolFaultLocked(ReasonProtocolGap);
                return false;
            }

            var advanced = false;
            foreach (var observation in source.Observations)
            {
                if (observation.Sequence <= _childProtocolCursor)
                    continue;
                if (observation.Sequence > _childProtocolCursor + 1)
                {
                    LatchProtocolFaultLocked(ReasonProtocolGap, observation.ObservedAt,
                        observation.Correlation, observation.DroppedFrames);
                    return false;
                }

                AppendProtocolLocked(observation.Kind, observation.ReasonCode,
                    observation.ObservedAt, observation.Correlation,
                    observation.DroppedFrames);
                _childProtocolCursor = observation.Sequence;
                advanced = true;
                if (observation.Kind == CameraProtocolViolationKind.ObservationGap)
                {
                    LatchProtocolFaultLocked(ReasonProtocolGap, observation.ObservedAt,
                        observation.Correlation, observation.DroppedFrames);
                    return false;
                }
            }

            if (source.ThroughSequence > _childProtocolCursor && !advanced)
            {
                LatchProtocolFaultLocked(ReasonProtocolGap);
                return false;
            }

            return true;
        }
    }

    private async Task<RecoveryAttemptResult> PerformRecoveryAttemptAsync()
    {
        if (IsDisposed())
            return RecoveryAttemptResult.BlockedResult(ReasonDisposed);

        await WaitForPhysicalDrainAsync().ConfigureAwait(false);

        // A prior bounded attempt may have left a candidate owner behind while
        // its actual retirement task was still draining. Reconcile that owner
        // before opening another physical device, including after an authorized
        // restart of an exhausted cycle.
        var staleCandidateRetirement = await CleanupCandidateAsync()
            .ConfigureAwait(false);
        if (!staleCandidateRetirement.SafeToReplace)
            return RecoveryAttemptResult.BlockedResult(NormalizeReason(
                staleCandidateRetirement.ReasonCode, ReasonRetirementUnsafe));

        CameraAcquisitionService current;
        lock (_sync) current = _current;
        if (!await PullProtocolAsync(current, CancellationToken.None).ConfigureAwait(false))
            return RecoveryAttemptResult.BlockedResult(ReasonProtocolUnavailable);

        bool sourceRetired;
        lock (_sync) sourceRetired = _sourceRetired;
        if (!sourceRetired)
        {
            Task<CameraDeviceRetirement> retirementTask;
            lock (_sync) retirementTask = StartPhysicalLocked(() => current.BeginRetirement());
            CameraDeviceRetirement retirement;
            try
            {
                retirement = await retirementTask.WaitAsync(_options.OperationTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                ObserveSourceRetirement(current, retirementTask);
                return RecoveryAttemptResult.BlockedResult(ReasonRetirementTimeout);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return RecoveryAttemptResult.BlockedResult(ReasonRetirementUnsafe);
            }

            if (!retirement.SafeToReplace)
                return RecoveryAttemptResult.BlockedResult(
                    NormalizeReason(retirement.ReasonCode, ReasonRetirementUnsafe));
            lock (_sync) _sourceRetired = true;
        }

        CameraOpenResult opened;
        Task<CameraOpenResult> openTask;
        lock (_sync)
        {
            openTask = StartPhysicalLocked(() => _provider.OpenAsync(
                _target.StableDeviceIdentity, _lifetime.Token).AsTask());
            _lateOpenTask = openTask;
        }
        RetainLateOpenedDevice(openTask);
        try
        {
            opened = await openTask.WaitAsync(_options.OperationTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return RecoveryAttemptResult.BlockedResult(ReasonCandidateOpenFailed);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return RecoveryAttemptResult.FailedResult(ReasonCandidateOpenFailed);
        }

        if (!opened.Succeeded || opened.Device is null)
            return await CandidateFailureAsync(ReasonCandidateOpenFailed).ConfigureAwait(false);

        lock (_sync)
        {
            _lateOpenTask = null;
            _candidateRaw = opened.Device;
            _candidateRetirementTask = null;
            _candidateRetirementFailed = false;
            _candidateRetirementFailureReason = ReasonRetirementUnsafe;
        }
        bool exactBinding;
        try { exactBinding = BindingMatches(opened.Device.Descriptor, _target); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { exactBinding = false; }
        if (!exactBinding)
            return await CandidateFailureAsync(ReasonCandidateIdentityMismatch).ConfigureAwait(false);

        if (opened.Device is not IControlledCameraDevice controlled)
            return await CandidateFailureAsync(ReasonCandidateNotControlled).ConfigureAwait(false);

        CameraConfigurationResult expected;
        try { expected = controlled.Capabilities.ValidateConfiguration(_requested); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return await CandidateFailureAsync(ReasonConfigurationInvalid).ConfigureAwait(false);
        }
        if (!expected.Succeeded || expected.Effective is null)
            return await CandidateFailureAsync(ReasonConfigurationInvalid).ConfigureAwait(false);

        CameraConfigurationResult applied;
        Task<CameraConfigurationResult> applyTask;
        lock (_sync)
        {
            applyTask = StartPhysicalLocked(() => controlled.ApplyConfigurationAsync(
                _requested, _lifetime.Token).AsTask());
        }
        try
        {
            applied = await applyTask.WaitAsync(_options.OperationTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveTimedOutRecoveryOperation(applyTask);
            return RecoveryAttemptResult.BlockedResult(ReasonConfigurationApplyFailed);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return await CandidateFailureAsync(ReasonConfigurationApplyFailed).ConfigureAwait(false);
        }
        if (!applied.Succeeded)
            return await CandidateFailureAsync(ReasonConfigurationApplyFailed).ConfigureAwait(false);

        CameraConfigurationResult readBack;
        try { readBack = controlled.Capabilities.ValidateReadBack(_requested, applied); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return await CandidateFailureAsync(ReasonReadBackFailed).ConfigureAwait(false);
        }
        if (!readBack.Succeeded || readBack.Effective is null)
            return await CandidateFailureAsync(ReasonReadBackFailed).ConfigureAwait(false);

        CameraAcquisitionService candidate;
        try
        {
            candidate = new CameraAcquisitionService(controlled, readBack.Effective,
                _clock, _acquisitionOptions);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return await CandidateFailureAsync(ReasonConfigurationInvalid).ConfigureAwait(false);
        }
        lock (_sync) _candidate = candidate;

        CameraOperationResult started;
        Task<CameraOperationResult> startTask;
        lock (_sync)
        {
            startTask = StartPhysicalLocked(() => controlled.StartAsync(_lifetime.Token).AsTask());
        }
        try
        {
            started = await startTask.WaitAsync(_options.OperationTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveTimedOutRecoveryOperation(startTask);
            return RecoveryAttemptResult.BlockedResult(ReasonStartFailed);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return await CandidateFailureAsync(ReasonStartFailed).ConfigureAwait(false);
        }
        if (!started.Succeeded)
            return await CandidateFailureAsync(ReasonStartFailed).ConfigureAwait(false);

        CameraHealthSnapshot? health;
        Task<CameraHealthSnapshot?> healthTask;
        lock (_sync)
        {
            healthTask = StartPhysicalLocked(() => candidate.ReadHealthAsync());
        }
        try
        {
            health = await healthTask.WaitAsync(_options.OperationTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveTimedOutRecoveryOperation(healthTask);
            return RecoveryAttemptResult.BlockedResult(ReasonCandidateHealthFailed);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return await CandidateFailureAsync(ReasonCandidateHealthFailed).ConfigureAwait(false);
        }
        if (health is null || !IsHealthy(health))
            return await CandidateFailureAsync(ReasonCandidateHealthFailed).ConfigureAwait(false);

        lock (_sync) _healthObservationRevision++;

        if (!await PullProtocolAsync(candidate, CancellationToken.None).ConfigureAwait(false))
            return RecoveryAttemptResult.BlockedResult(ReasonProtocolUnavailable);

        return RecoveryAttemptResult.SuccessResult(candidate, health);
    }

    private async Task<RecoveryAttemptResult> CandidateFailureAsync(string reason)
    {
        var retirement = await CleanupCandidateAsync().ConfigureAwait(false);
        lock (_sync)
        {
            if (_protocolFaulted)
                return RecoveryAttemptResult.BlockedResult(ReasonProtocolUnavailable);
        }
        if (!retirement.SafeToReplace)
            return RecoveryAttemptResult.BlockedResult(
                NormalizeReason(retirement.ReasonCode, ReasonRetirementUnsafe));
        return RecoveryAttemptResult.FailedResult(reason);
    }

    private async Task WaitForPhysicalDrainAsync()
    {
        Task? physical;
        lock (_sync) physical = _physicalTask;
        if (physical is null) return;
        try { await physical.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        lock (_sync)
        {
            if (ReferenceEquals(_physicalTask, physical) && physical.IsCompleted)
                _physicalTask = null;
        }
    }

    private async Task RunCycleStepAsync(Guid cycleId)
    {
        if (!await TryEnterOperationAsync(CancellationToken.None).ConfigureAwait(false))
        {
            lock (_sync)
            {
                if (IsCurrentCycleLocked(cycleId))
                    ScheduleNextAttemptLocked(cycleId, GetTimePoint().MonotonicTimestamp);
            }
            return;
        }

        try
        {
            int attemptNumber;
            lock (_sync)
            {
                if (!IsCurrentCycleLocked(cycleId)) return;
                _attemptCount++;
                attemptNumber = _attemptCount;
                _nextAttemptAt = null;
                AppendEventLocked(CameraRecoveryEventKind.AttemptStarted, cycleId,
                    attemptNumber, "CameraRecoveryAttemptStarted");
            }

            RecoveryAttemptResult result;
            try { result = await PerformRecoveryAttemptAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (IsDisposed())
            {
                result = RecoveryAttemptResult.BlockedResult(ReasonDisposed);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                result = RecoveryAttemptResult.FailedResult(ReasonCycleFailed);
            }

            lock (_sync)
            {
                if (!IsCurrentCycleLocked(cycleId)) return;
                if (result.Succeeded)
                {
                    _scheduledAttempt?.Dispose();
                    _scheduledAttempt = null;
                    _current = result.Candidate!;
                    _candidate = null;
                    _candidateRaw = null;
                    _candidateRetirementTask = null;
                    _candidateRetirementFailed = false;
                    _candidateRetirementFailureReason = ReasonRetirementUnsafe;
                    _sourceRetired = false;
                    _health = result.Health;
                    _sourceHealthy = true;
                    _nextAttemptAt = null;
                    SetStateLocked(CameraRecoveryState.Healthy, true, ReasonCompleted);
                    AppendEventLocked(CameraRecoveryEventKind.CycleCompleted, cycleId,
                        attemptNumber, ReasonCompleted);
                    return;
                }

                AppendEventLocked(CameraRecoveryEventKind.AttemptFailed, cycleId,
                    attemptNumber, NormalizeReason(result.Reason, ReasonCycleFailed));
                if (result.Blocked)
                {
                    _scheduledAttempt?.Dispose();
                    _scheduledAttempt = null;
                    SetStateLocked(CameraRecoveryState.RecoveryRequired, false,
                        NormalizeReason(result.Reason, ReasonRetirementUnsafe));
                    _nextAttemptAt = null;
                    // A bounded wait may have returned before the owned physical
                    // task settled. The helper only schedules once that task and
                    // any late owner handoff have actually settled.
                    TryResumeCycleAfterRetirement();
                    return;
                }

                if (_attemptCount >= _options.MaximumAttempts)
                {
                    _scheduledAttempt?.Dispose();
                    _scheduledAttempt = null;
                    _nextAttemptAt = null;
                    SetStateLocked(CameraRecoveryState.Exhausted, false, ReasonCycleFailed);
                    AppendEventLocked(CameraRecoveryEventKind.CycleExhausted, cycleId,
                        attemptNumber, ReasonCycleFailed);
                    return;
                }

                SetStateLocked(CameraRecoveryState.Recovering, false,
                    NormalizeReason(result.Reason, ReasonCycleFailed));
                ScheduleNextAttemptLocked(cycleId, GetTimePoint().MonotonicTimestamp);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void BeginAutomaticCycleLocked(string reason)
    {
        if (_disposed || _protocolFaulted || _state == CameraRecoveryState.Exhausted ||
            _cycleId is not null && _state == CameraRecoveryState.Recovering)
            return;

        _cycleId = NewGuid();
        _attemptCount = 0;
        _sourceHealthy = false;
        _sourceRetired = false;
        _nextAttemptAt = null;
        AppendEventLocked(CameraRecoveryEventKind.SourceDisconnected, _cycleId.Value, 0,
            NormalizeReason(reason, ReasonConnectionLost));
        SetStateLocked(CameraRecoveryState.Recovering, false,
            NormalizeReason(reason, ReasonConnectionLost));
        AppendEventLocked(CameraRecoveryEventKind.CycleStarted, _cycleId.Value, 0,
            "CameraRecoveryCycleStarted");
        ScheduleNextAttemptLocked(_cycleId.Value, GetTimePoint().MonotonicTimestamp);
    }

    internal bool CommitRestart(CameraRecoveryRestartReservation reservation, out string reason)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                reason = ReasonDisposed;
                return false;
            }
            if (!ReferenceEquals(_restartReservation, reservation) ||
                _state != CameraRecoveryState.Exhausted || _cycleId != reservation.CycleId)
            {
                reason = ReasonCycleMismatch;
                return false;
            }

            _restartReservation = null;
            _cycleId = NewGuid();
            _attemptCount = 0;
            _sourceHealthy = false;
            _sourceRetired = true;
            _nextAttemptAt = null;
            SetStateLocked(CameraRecoveryState.Recovering, false, ReasonRestartCommitted);
            AppendEventLocked(CameraRecoveryEventKind.CycleStarted, _cycleId.Value, 0,
                ReasonRestartCommitted);
            ScheduleNextAttemptLocked(_cycleId.Value, GetTimePoint().MonotonicTimestamp);
            reason = ReasonRestartCommitted;
            return true;
        }
    }

    internal void ReleaseRestartReservation(CameraRecoveryRestartReservation reservation)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_restartReservation, reservation))
                _restartReservation = null;
        }
    }

    private void ScheduleNextAttemptLocked(Guid cycleId, long settledAt)
    {
        if (_disposed || !IsCurrentCycleLocked(cycleId)) return;
        var due = AddInterval(settledAt, _options.RetryInterval);
        if (_attemptCount == 0)
            due = settledAt;

        try
        {
            _scheduledAttempt?.Dispose();
            _scheduledAttempt = _clock.Schedule(due,
                FrameAcquisitionClockPhase.FrameObservation,
                () => QueueScheduledStep(cycleId));
            var now = GetTimePoint();
            _nextAttemptAt = new FrameTimePoint(now.HostObservedAtUtc, due);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SetStateLocked(CameraRecoveryState.RecoveryRequired, false,
                ReasonScheduleFailed);
            _nextAttemptAt = null;
        }
    }

    private void QueueScheduledStep(Guid cycleId)
    {
        try
        {
            _ = Task.Run(() => QueueScheduledStepOnWorker(cycleId));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_sync)
            {
                if (IsCurrentCycleLocked(cycleId))
                    SetStateLocked(CameraRecoveryState.RecoveryRequired, false,
                        ReasonScheduleFailed);
            }
        }
    }

    private void QueueScheduledStepOnWorker(Guid cycleId)
    {
        Task step;
        lock (_sync)
        {
            if (_disposed || !IsCurrentCycleLocked(cycleId) ||
                _cycleStepTask is { IsCompleted: false })
                return;
            _scheduledAttempt = null;
            _nextAttemptAt = null;
            step = RunCycleStepAsync(cycleId);
            _cycleStepTask = step;
        }

        _ = step.ContinueWith(completed =>
        {
            lock (_sync)
            {
                if (ReferenceEquals(_cycleStepTask, completed))
                    _cycleStepTask = null;
            }
            _ = completed.Exception;
        }, CancellationToken.None, TaskContinuationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default);
    }

    private bool IsCurrentCycleLocked(Guid cycleId) =>
        !_disposed && _cycleId == cycleId && _state == CameraRecoveryState.Recovering;

    private void SetStateLocked(CameraRecoveryState state, bool sourceHealthy,
        string reason)
    {
        _state = state;
        _sourceHealthy = sourceHealthy && state == CameraRecoveryState.Healthy;
        _reasonCode = NormalizeReason(reason, ReasonUnavailable);
        _revision++;
    }

    private void AppendEventLocked(CameraRecoveryEventKind kind, Guid cycleId,
        int attemptNumber, string reason)
    {
        if (_nextEventSequence >= long.MaxValue) return;
        var timestamp = GetTimePoint();
        var item = new CameraRecoveryEvent(_recoveryEpoch, _nextEventSequence++, cycleId,
            attemptNumber, kind, timestamp, NormalizeReason(reason, ReasonCycleFailed));
        _events.Enqueue(item);
        while (_events.Count > CameraRecoveryOptions.EventCapacity)
            _events.Dequeue();
        _revision++;
    }

    private void AppendProtocolLocked(CameraProtocolViolationKind kind, string reason,
        FrameTimePoint? observedAt, ExecutionCorrelationId? correlation, int droppedFrames)
    {
        if (_nextProtocolSequence >= long.MaxValue) return;
        CameraProtocolObservation item;
        try
        {
            item = new CameraProtocolObservation(_nextProtocolSequence++, kind,
                NormalizeReason(reason, ReasonProtocolGap), observedAt ?? GetTimePoint(),
                correlation, Math.Min(droppedFrames, 64));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _protocolFaulted = true;
            return;
        }

        _protocol.Enqueue(item);
        while (_protocol.Count > CameraRecoveryOptions.EventCapacity)
            _protocol.Dequeue();
        _revision++;
    }

    private void LatchProtocolFaultLocked(string reason,
        FrameTimePoint? observedAt = null, ExecutionCorrelationId? correlation = null,
        int droppedFrames = 0)
    {
        _protocolFaulted = true;
        AppendProtocolLocked(CameraProtocolViolationKind.ObservationGap, reason,
            observedAt, correlation, droppedFrames);
        if (!_disposed)
            SetStateLocked(CameraRecoveryState.RecoveryRequired, false,
                ReasonProtocolUnavailable);
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
        return new CameraProtocolSnapshot(_recoveryEpoch, first, through, overflowed,
            observations);
    }

    private CameraRecoverySnapshot BuildSnapshotLocked() => new(_logicalCameraRole,
        _recoveryEpoch, _revision, _cycleId, _state, _attemptCount,
        _options.MaximumAttempts, _options.RetryInterval, _nextAttemptAt,
        _sourceHealthy, _health, _reasonCode, _healthObservationRevision);

    private bool IsDisposed()
    {
        lock (_sync) return _disposed;
    }

    private async ValueTask<bool> TryEnterOperationAsync(CancellationToken cancellationToken)
    {
        try { return await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw; }
    }

    private Task<T> StartPhysicalLocked<T>(Func<Task<T>> operation)
    {
        ClearCompletedPhysicalLocked();
        if (_physicalTask is not null)
            throw new InvalidOperationException("CameraRecoveryPhysicalCallInFlight");
        var task = InvokeOffThread(operation);
        _physicalTask = task;
        ObserveFault(task);
        return task;
    }

    private Task StartPhysicalLocked(Func<Task> operation)
    {
        ClearCompletedPhysicalLocked();
        if (_physicalTask is not null)
            throw new InvalidOperationException("CameraRecoveryPhysicalCallInFlight");
        var task = InvokeOffThread(operation);
        _physicalTask = task;
        ObserveFault(task);
        return task;
    }

    private void ClearCompletedPhysicalLocked()
    {
        if (_physicalTask is { IsCompleted: true })
        {
            _ = _physicalTask.Exception;
            _physicalTask = null;
        }
    }

    private bool CanStartPhysicalLocked()
    {
        ClearCompletedPhysicalLocked();
        return _physicalTask is null;
    }

    private static Task<T> InvokeOffThread<T>(Func<Task<T>> operation)
    {
        // The provider may complete its task inline on an SDK callback stack.
        // Keep that stack limited to this result transfer: every Runtime waiter
        // observes a RunContinuationsAsynchronously bridge task.
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Factory.StartNew(async state =>
        {
            try
            {
                var result = await ((Func<Task<T>>)state!).Invoke()
                    .ConfigureAwait(false);
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                completion.TrySetException(exception);
            }
        }, operation, CancellationToken.None, TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
        return completion.Task;
    }

    private static Task InvokeOffThread(Func<Task> operation)
    {
        // See the generic overload: the bridge prevents Runtime continuations
        // from re-entering provider completion code synchronously.
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Factory.StartNew(async state =>
        {
            try
            {
                await ((Func<Task>)state!).Invoke().ConfigureAwait(false);
                completion.TrySetResult(true);
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                completion.TrySetException(exception);
            }
        }, operation, CancellationToken.None, TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
        return completion.Task;
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(completed => _ = completed.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void RetainLateOpenedDevice(Task<CameraOpenResult> openTask)
    {
        _ = openTask.ContinueWith(completed =>
        {
            try
            {
                lock (_sync)
                {
                    if (!ReferenceEquals(_lateOpenTask, openTask))
                        return;
                    ReconcileLateOpenedDeviceLocked();
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A late provider fault is retained only as owner state; it never
                // crosses the public recovery contract.
            }
            TryResumeCycleAfterRetirement();
        }, CancellationToken.None, TaskContinuationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default);
    }

    private static bool IsHealthy(CameraHealthSnapshot health) =>
        health.ProviderAvailability == CameraProviderAvailability.Available &&
        health.Connection == CameraConnectionState.Open &&
        health.Configuration == CameraConfigurationState.Applied &&
        health.Acquisition == CameraAcquisitionState.Armed &&
        health.LastFault is null;

    private static bool IsConnectionLost(CameraHealthSnapshot health) =>
        health.Connection == CameraConnectionState.Disconnected ||
        health.LastFault?.Classification == CameraFaultClassification.ConnectionLost;

    private static string HealthReason(CameraHealthSnapshot health) =>
        health.LastFault is { } fault
            ? fault.ReasonCode
            : health.ProviderAvailability != CameraProviderAvailability.Available
            ? ReasonProviderUnavailable
            : health.Connection != CameraConnectionState.Open
                ? ReasonConnectionUnavailable
                : health.Configuration != CameraConfigurationState.Applied
                    ? ReasonConfigurationUnavailable
                    : health.Acquisition != CameraAcquisitionState.Armed
                        ? ReasonAcquisitionUnavailable
                        : ReasonUnavailable;

    private long AddInterval(long settledAt, TimeSpan interval)
    {
        var delta = interval.TotalSeconds * _clock.Frequency;
        if (!double.IsFinite(delta) || delta >= long.MaxValue - settledAt)
            return long.MaxValue - 1;
        return checked(settledAt + Math.Max(1, (long)Math.Ceiling(delta)));
    }

    private FrameTimePoint GetTimePoint()
    {
        try { return _clock.GetTimePoint(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new FrameTimePoint(DateTimeOffset.UtcNow, 0); }
    }

    private static bool BindingMatches(CameraDeviceDescriptor descriptor,
        CameraBindingTarget target) =>
        descriptor is not null &&
        SameProvider(descriptor.Provider, target.Provider) &&
        StringComparer.Ordinal.Equals(descriptor.StableDeviceIdentity,
            target.StableDeviceIdentity);

    private static void EnsureBinding(CameraDeviceDescriptor descriptor,
        CameraBindingTarget target, string reason)
    {
        if (!BindingMatches(descriptor, target))
            throw new ArgumentException(reason);
    }

    private static void EnsureProviderIdentity(CameraProviderIdentity actual,
        CameraProviderIdentity expected, string parameterName)
    {
        if (!SameProvider(actual, expected))
            throw new ArgumentException("CameraProviderIdentityMismatch", parameterName);
    }

    private static bool SameProvider(CameraProviderIdentity actual,
        CameraProviderIdentity expected) =>
        actual is not null && expected is not null &&
        StringComparer.Ordinal.Equals(actual.Id, expected.Id) &&
        StringComparer.Ordinal.Equals(actual.Version, expected.Version) &&
        StringComparer.Ordinal.Equals(actual.AdapterPackageId, expected.AdapterPackageId) &&
        StringComparer.Ordinal.Equals(actual.AdapterVersion, expected.AdapterVersion);

    private static string NormalizeReason(string? reason, string fallback)
    {
        if (string.IsNullOrEmpty(reason)) return fallback;
        try { return CameraContractValidation.Reason(reason, nameof(reason)); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return fallback; }
    }

    private static CameraAcquisitionAttempt Rejected(string reason) =>
        new(false, reason, null, null);

    private static Guid NewGuid()
    {
        Guid value;
        do { value = Guid.NewGuid(); } while (value == Guid.Empty);
        return value;
    }

    private readonly record struct RecoveryAttemptResult(bool Succeeded, bool Blocked,
        string Reason, CameraAcquisitionService? Candidate, CameraHealthSnapshot? Health)
    {
        internal static RecoveryAttemptResult SuccessResult(CameraAcquisitionService candidate,
            CameraHealthSnapshot health) => new(true, false, ReasonCompleted, candidate, health);
        internal static RecoveryAttemptResult FailedResult(string reason) =>
            new(false, false, reason, null, null);
        internal static RecoveryAttemptResult BlockedResult(string reason) =>
            new(false, true, reason, null, null);
    }
}

/// <summary>Internal one-shot authorization seam for an exhausted cycle.</summary>
internal sealed class CameraRecoveryRestartReservation : IDisposable
{
    private readonly CameraRecoveryService _owner;
    private int _completed;

    internal CameraRecoveryRestartReservation(CameraRecoveryService owner, Guid cycleId)
    {
        _owner = owner;
        CycleId = cycleId;
    }

    internal Guid CycleId { get; }

    internal bool Commit(out string reason)
    {
        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
        {
            reason = "CameraRecoveryRestartReservationConsumed";
            return false;
        }

        if (_owner.CommitRestart(this, out reason))
            return true;

        return false;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _completed, 1, 0) == 0)
            _owner.ReleaseRestartReservation(this);
    }
}
