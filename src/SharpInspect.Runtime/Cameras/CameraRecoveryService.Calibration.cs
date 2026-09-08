using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Cameras;

internal sealed class CameraCalibrationBeginResult
{
    internal CameraCalibrationBeginResult(bool succeeded, string reasonCode,
        CameraCalibrationLease? lease = null, Guid? sessionId = null)
    {
        Succeeded = succeeded;
        ReasonCode = reasonCode ?? throw new ArgumentNullException(nameof(reasonCode));
        if (succeeded != (lease is not null))
            throw new ArgumentException("CameraCalibrationBeginResultStateInvalid", nameof(lease));
        if (lease is not null && sessionId is not null && lease.SessionId != sessionId)
            throw new ArgumentException("CameraCalibrationBeginResultSessionInvalid",
                nameof(sessionId));
        Lease = lease;
        SessionId = lease?.SessionId ?? sessionId;
    }

    internal bool Succeeded { get; }
    internal string ReasonCode { get; }
    internal CameraCalibrationLease? Lease { get; }
    /// <summary>
    /// Identifies a retained calibration owner even when a bounded begin call
    /// failed after reserving the session and the lease could not be returned.
    /// </summary>
    internal Guid? SessionId { get; }

    internal static CameraCalibrationBeginResult Success(CameraCalibrationLease lease) =>
        new(true, "CameraCalibrationConfigurationStarted", lease);

    internal static CameraCalibrationBeginResult Failure(string reasonCode,
        Guid? sessionId = null) => new(false, reasonCode, sessionId: sessionId);
}

internal sealed class CameraCalibrationRestoreResult
{
    internal CameraCalibrationRestoreResult(bool succeeded, string reasonCode)
    {
        Succeeded = succeeded;
        ReasonCode = reasonCode ?? throw new ArgumentNullException(nameof(reasonCode));
    }

    internal bool Succeeded { get; }
    internal string ReasonCode { get; }
}

/// <summary>
/// Result of the pre-admission baseline observation.  The snapshot is only
/// present after the current owner has passed a real health read; callers must
/// persist this value before asking the service to change the camera.
/// </summary>
internal sealed class CameraCalibrationBaselineReadResult
{
    internal CameraCalibrationBaselineReadResult(bool succeeded, string reasonCode,
        CameraCalibrationBaselineSnapshot? snapshot = null)
    {
        Succeeded = succeeded;
        ReasonCode = reasonCode ?? throw new ArgumentNullException(nameof(reasonCode));
        if (succeeded != (snapshot is not null))
            throw new ArgumentException("CameraCalibrationBaselineResultStateInvalid",
                nameof(snapshot));
        Snapshot = snapshot;
    }

    internal bool Succeeded { get; }
    internal string ReasonCode { get; }
    internal CameraCalibrationBaselineSnapshot? Snapshot { get; }

    internal static CameraCalibrationBaselineReadResult Success(
        CameraCalibrationBaselineSnapshot snapshot) => new(true,
        "CameraCalibrationBaselineObserved", snapshot);

    internal static CameraCalibrationBaselineReadResult Failure(string reasonCode) =>
        new(false, reasonCode);
}

internal sealed record CameraCalibrationBaselineSnapshot(
    CameraBindingTarget Target,
    string LogicalCameraRole,
    RequestedCameraConfiguration Requested,
    EffectiveCameraConfiguration Effective,
    CameraHealthSnapshot Health);

public sealed partial class CameraRecoveryService
{
    // Drain, retire, cleanup, reopen, apply/read-back, start and health each have
    // their own operation bound. Shutdown must allow the complete restore sequence.
    internal TimeSpan CalibrationRestorationTimeout =>
        TimeSpan.FromTicks(checked(_options.OperationTimeout.Ticks * 12));
    private const string ReasonCalibrationActive = "CameraCalibrationActive";
    private const string ReasonCalibrationBusy = "CameraCalibrationBusy";
    private const string ReasonCalibrationCancelled = "CameraCalibrationCancelled";
    private const string ReasonCalibrationSessionRequired = "CameraCalibrationSessionRequired";
    private const string ReasonCalibrationSessionConflict = "CameraCalibrationSessionConflict";
    private const string ReasonCalibrationSourceUnavailable =
        "CameraCalibrationSourceUnavailable";
    private const string ReasonCalibrationRetirementFailed =
        "CameraCalibrationBaselineRetirementFailed";
    private const string ReasonCalibrationRetirementTimeout =
        "CameraCalibrationRetirementTimeout";
    private const string ReasonCalibrationOpenFailed = "CameraCalibrationOpenFailed";
    private const string ReasonCalibrationIdentityMismatch =
        "CameraCalibrationIdentityMismatch";
    private const string ReasonCalibrationNotControlled =
        "CameraCalibrationDeviceNotControlled";
    private const string ReasonCalibrationConfigurationInvalid =
        "CameraCalibrationConfigurationInvalid";
    private const string ReasonCalibrationApplyFailed = "CameraCalibrationApplyFailed";
    private const string ReasonCalibrationReadBackFailed =
        "CameraCalibrationReadBackFailed";
    private const string ReasonCalibrationStartFailed = "CameraCalibrationStartFailed";
    private const string ReasonCalibrationHealthFailed = "CameraCalibrationHealthFailed";
    private const string ReasonCalibrationBaselineMismatch =
        "CameraCalibrationBaselineConfigurationMismatch";
    private const string ReasonCalibrationBaselineRestoreFailed =
        "CameraCalibrationBaselineRestoreFailed";
    private const string ReasonCalibrationBaselineRestored =
        "CameraCalibrationPersistedBaselineRestored";
    private const string ReasonCalibrationAdmissionHeld =
        "CameraCalibrationAdmissionHeld";
    private const string ReasonCalibrationAdmissionAlreadyHeld =
        "CameraCalibrationAdmissionAlreadyHeld";
    private const string ReasonCalibrationAdmissionClosed =
        "CameraCalibrationAdmissionClosed";
    private const string ReasonCalibrationAdmissionNotHeld =
        "CameraCalibrationAdmissionNotHeld";
    private const string ReasonCalibrationAdmissionResources =
        "CameraCalibrationAdmissionResourcesOutstanding";
    private const string ReasonCalibrationRestored = "CameraCalibrationRestored";
    private const string ReasonCalibrationDisposed = "CameraCalibrationDisposed";

    private Guid? _calibrationSessionId;
    private Guid? _calibrationAdmissionHold;
    private string _calibrationAdmissionReason = ReasonCalibrationAdmissionNotHeld;
    private CameraCalibrationLease? _calibrationLease;
    private CameraCalibrationBaselineSnapshot? _calibrationBaseline;
    private CameraAcquisitionService? _calibrationAcquisition;
    private ICameraDevice? _calibrationRaw;
    private Task<CameraOpenResult>? _calibrationOpenTask;
    private bool _calibrationBlocked;
    private string _calibrationBlockedReason = ReasonCalibrationActive;
    private CameraRecoveryState _calibrationPriorState;
    private bool _calibrationPriorSourceHealthy;
    private CameraHealthSnapshot? _calibrationPriorHealth;

    /// <summary>
    /// Internal session ownership signal consumed by the station calibration
    /// command. It deliberately contains no production-ready state.
    /// </summary>
    internal bool IsCalibrationActive
    {
        get
        {
            lock (_sync)
                return _calibrationSessionId is not null || _calibrationAdmissionHold is not null;
        }
    }

    internal Guid? CalibrationSessionId
    {
        get { lock (_sync) return _calibrationSessionId ?? _calibrationAdmissionHold; }
    }

    internal string CalibrationReason
    {
        get
        {
            lock (_sync)
                return _calibrationSessionId is null
                    ? _calibrationAdmissionReason : _calibrationBlockedReason;
        }
    }

    /// <summary>
    /// Installs the durable-session admission fence without touching the camera.
    /// The caller must persist its session header before invoking this method.
    /// </summary>
    internal bool TryHoldCalibrationAdmission(Guid sessionId, out string reason)
    {
        if (sessionId == Guid.Empty)
        {
            reason = ReasonCalibrationSessionRequired;
            return false;
        }

        if (!_operationGate.Wait(0))
        {
            reason = ReasonCalibrationBusy;
            return false;
        }

        try
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    reason = ReasonDisposed;
                    return false;
                }

                if (_calibrationAdmissionHold is { } existingHold)
                {
                    if (existingHold == sessionId)
                    {
                        reason = ReasonCalibrationAdmissionAlreadyHeld;
                        return true;
                    }

                    reason = ReasonCalibrationSessionConflict;
                    return false;
                }

                if (_calibrationSessionId is not null)
                {
                    reason = ReasonCalibrationSessionConflict;
                    return false;
                }

                if (_protocolFaulted)
                {
                    reason = ReasonProtocolUnavailable;
                    return false;
                }

                if (_state is not (CameraRecoveryState.Healthy or CameraRecoveryState.Unknown) ||
                    _state == CameraRecoveryState.Healthy && !_sourceHealthy)
                {
                    reason = ReasonCalibrationSourceUnavailable;
                    return false;
                }

                if (_cycleId is not null || _restartReservation is not null ||
                    _candidate is not null || _candidateRaw is not null ||
                    _candidateRetirementTask is not null || _lateOpenTask is not null ||
                    !CanStartPhysicalLocked() || _current.HasPendingActualWork ||
                    _current.HasOutstandingLeases)
                {
                    reason = ReasonCalibrationBusy;
                    return false;
                }

                _calibrationAdmissionHold = sessionId;
                _calibrationAdmissionReason = ReasonCalibrationAdmissionHeld;
                _revision++;
                reason = ReasonCalibrationAdmissionHeld;
                return true;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Releases the durable-session fence only after the coordinator has committed
    /// the verified Restored state and the service has no calibration owner or
    /// physical work left to drain. A failed acknowledgement leaves the fence set.
    /// </summary>
    internal bool ConfirmCalibrationSessionClosed(Guid sessionId) =>
        ConfirmCalibrationSessionClosed(sessionId, out _);

    internal bool ConfirmCalibrationSessionClosed(Guid sessionId, out string reason)
    {
        if (sessionId == Guid.Empty)
        {
            reason = ReasonCalibrationSessionRequired;
            return false;
        }

        if (!_operationGate.Wait(0))
        {
            reason = ReasonCalibrationBusy;
            return false;
        }

        try
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    reason = ReasonDisposed;
                    return false;
                }

                if (_calibrationAdmissionHold != sessionId)
                {
                    reason = _calibrationAdmissionHold is null
                        ? ReasonCalibrationAdmissionNotHeld
                        : ReasonCalibrationSessionConflict;
                    return false;
                }

                if (_calibrationSessionId is not null || _calibrationLease is not null ||
                    _calibrationBaseline is not null || _calibrationAcquisition is not null ||
                    _calibrationRaw is not null || _calibrationOpenTask is not null ||
                    _lateOpenTask is not null || _candidate is not null ||
                    _candidateRaw is not null || _candidateRetirementTask is not null ||
                    _cycleId is not null || _cycleStepTask is not null ||
                    _restartReservation is not null || !CanStartPhysicalLocked() ||
                    _current.HasPendingActualWork || _current.HasOutstandingLeases)
                {
                    reason = ReasonCalibrationAdmissionResources;
                    return false;
                }

                if (_protocolFaulted || _state != CameraRecoveryState.Healthy || !_sourceHealthy)
                {
                    reason = ReasonCalibrationSourceUnavailable;
                    return false;
                }

                _calibrationAdmissionHold = null;
                _calibrationAdmissionReason = ReasonCalibrationAdmissionClosed;
                _revision++;
                reason = ReasonCalibrationAdmissionClosed;
                return true;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Reads and returns the exact baseline that is still owned by the recovery
    /// service.  The health member is from an actual current-owner probe, never
    /// from the cached projection.  This is the admission boundary: callers
    /// must durably retain the returned snapshot before requesting a temporary
    /// calibration configuration.
    /// </summary>
    internal async Task<CameraCalibrationBaselineReadResult>
        GetCalibrationBaselineSnapshot(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return CameraCalibrationBaselineReadResult.Failure(ReasonCalibrationCancelled);

        try
        {
            if (!await TryEnterOperationAsync(cancellationToken).ConfigureAwait(false))
                return CameraCalibrationBaselineReadResult.Failure(ReasonCalibrationBusy);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CameraCalibrationBaselineReadResult.Failure(ReasonCalibrationCancelled);
        }

        try
        {
            CameraAcquisitionService current;
            lock (_sync)
            {
                if (_disposed)
                    return CameraCalibrationBaselineReadResult.Failure(ReasonDisposed);
                if (_calibrationSessionId is not null || _calibrationAdmissionHold is not null)
                    return CameraCalibrationBaselineReadResult.Failure(
                        ReasonCalibrationSessionConflict);
                if (_protocolFaulted)
                    return CameraCalibrationBaselineReadResult.Failure(ReasonProtocolUnavailable);
                // A freshly constructed service starts in Unknown even though its
                // prepared seed is valid.  The real health probe below qualifies
                // that seed for admission; a prior RecoveryRequired state still
                // remains fail-closed.
                if (_state is not (CameraRecoveryState.Healthy or CameraRecoveryState.Unknown) ||
                    _state == CameraRecoveryState.Healthy && !_sourceHealthy)
                    return CameraCalibrationBaselineReadResult.Failure(
                        ReasonCalibrationSourceUnavailable);
                if (_cycleId is not null || _restartReservation is not null ||
                    _candidate is not null || _candidateRaw is not null ||
                    _lateOpenTask is not null || !CanStartPhysicalLocked() ||
                    _current.HasPendingActualWork || _current.HasOutstandingLeases)
                    return CameraCalibrationBaselineReadResult.Failure(ReasonCalibrationBusy);
                current = _current;
            }

            if (!await PullProtocolAsync(current, cancellationToken).ConfigureAwait(false))
                return CameraCalibrationBaselineReadResult.Failure(ReasonProtocolUnavailable);

            Task<CameraHealthSnapshot?> healthTask;
            lock (_sync)
            {
                if (_disposed || _calibrationSessionId is not null ||
                    _calibrationAdmissionHold is not null ||
                    !ReferenceEquals(_current, current) || !CanStartPhysicalLocked())
                    return CameraCalibrationBaselineReadResult.Failure(ReasonCalibrationBusy);
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
                return CameraCalibrationBaselineReadResult.Failure(ReasonHealthProbeTimeout);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CameraCalibrationBaselineReadResult.Failure(ReasonCalibrationCancelled);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return CameraCalibrationBaselineReadResult.Failure(ReasonCalibrationSourceUnavailable);
            }

            if (health is null || !IsHealthy(health))
                return CameraCalibrationBaselineReadResult.Failure(
                    ReasonCalibrationSourceUnavailable);

            lock (_sync)
            {
                if (_disposed || _calibrationSessionId is not null ||
                    _calibrationAdmissionHold is not null ||
                    !ReferenceEquals(_current, current))
                    return CameraCalibrationBaselineReadResult.Failure(ReasonCalibrationBusy);

                _health = health;
                _healthObservationRevision++;
                SetStateLocked(CameraRecoveryState.Healthy, true, ReasonHealthy);
                return CameraCalibrationBaselineReadResult.Success(
                    new CameraCalibrationBaselineSnapshot(_target, _logicalCameraRole,
                        _requested, current.Configuration, health));
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Retires the prepared baseline, configures one exact temporary owner and
    /// returns a lease. No persistent requested configuration is changed here.
    /// </summary>
    internal async Task<CameraCalibrationBeginResult> BeginCalibrationConfigurationAsync(
        Guid sessionId, RequestedCameraConfiguration temporaryRequested,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            return CameraCalibrationBeginResult.Failure("CameraCalibrationSessionIdRequired");
        if (temporaryRequested is null)
            return CameraCalibrationBeginResult.Failure("CameraCalibrationConfigurationRequired");
        if (cancellationToken.IsCancellationRequested)
            return CameraCalibrationBeginResult.Failure(ReasonCalibrationCancelled);

        try
        {
            if (!await TryEnterOperationAsync(cancellationToken).ConfigureAwait(false))
                return CameraCalibrationBeginResult.Failure(ReasonCalibrationBusy);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CameraCalibrationBeginResult.Failure(ReasonCalibrationCancelled);
        }

        try
        {
            CameraAcquisitionService current;
            lock (_sync)
            {
                if (_disposed)
                    return CameraCalibrationBeginResult.Failure(ReasonDisposed);
                if (_calibrationSessionId is not null)
                    return CameraCalibrationBeginResult.Failure(ReasonCalibrationSessionConflict);
                if (_calibrationAdmissionHold is { } heldSession && heldSession != sessionId)
                    return CameraCalibrationBeginResult.Failure(ReasonCalibrationSessionConflict);
                if (_protocolFaulted)
                    return CameraCalibrationBeginResult.Failure(ReasonProtocolUnavailable);
                if (_state != CameraRecoveryState.Healthy || !_sourceHealthy)
                    return CameraCalibrationBeginResult.Failure(ReasonCalibrationSourceUnavailable);
                if (_cycleId is not null || _restartReservation is not null ||
                    _candidate is not null || _candidateRaw is not null ||
                    _lateOpenTask is not null || !CanStartPhysicalLocked())
                    return CameraCalibrationBeginResult.Failure(ReasonCalibrationBusy);
                if (_current.HasPendingActualWork || _current.HasOutstandingLeases)
                    return CameraCalibrationBeginResult.Failure(ReasonCalibrationBusy);

                _calibrationSessionId = sessionId;
                _calibrationPriorState = _state;
                _calibrationPriorSourceHealthy = _sourceHealthy;
                _calibrationPriorHealth = _health;
                _calibrationBlocked = false;
                _calibrationBlockedReason = ReasonCalibrationActive;
                current = _current;
            }

            // Pull old protocol facts before the owner is retired. A child epoch
            // switch is explicit and never discards the recovery ring.
            if (!await PullProtocolAsync(current, cancellationToken).ConfigureAwait(false))
                return AbortCalibrationReservation(ReasonProtocolUnavailable);

            CameraHealthSnapshot? baselineHealth;
            Task<CameraHealthSnapshot?> baselineHealthTask;
            lock (_sync)
            {
                if (_disposed || !ReferenceEquals(_current, current) ||
                    _calibrationSessionId != sessionId)
                    return FailureForCalibrationOwner(ReasonDisposed);
                if (!CanStartPhysicalLocked())
                    return MarkCalibrationBlocked(ReasonCalibrationBusy);
                baselineHealthTask = StartPhysicalLocked(() => current.ReadHealthAsync());
            }

            try
            {
                baselineHealth = await baselineHealthTask.WaitAsync(_options.OperationTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return MarkCalibrationBlocked(ReasonHealthProbeTimeout);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return MarkCalibrationBlocked(ReasonCalibrationCancelled);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return AbortCalibrationReservation(ReasonCalibrationSourceUnavailable);
            }

            if (baselineHealth is null || !IsHealthy(baselineHealth))
                return AbortCalibrationReservation(ReasonCalibrationSourceUnavailable);

            lock (_sync)
            {
                _health = baselineHealth;
                _healthObservationRevision++;
                _calibrationBaseline = new CameraCalibrationBaselineSnapshot(_target,
                    _logicalCameraRole, _requested, current.Configuration, baselineHealth);
                _sourceHealthy = false;
                SetStateLocked(CameraRecoveryState.RecoveryRequired, false,
                    ReasonCalibrationActive);
            }

            CameraDeviceRetirement baselineRetirement;
            try
            {
                baselineRetirement = await RetireCalibrationAcquisitionAsync(current,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return MarkCalibrationBlocked(ReasonCalibrationCancelled);
            }

            if (!baselineRetirement.SafeToReplace)
                return MarkCalibrationBlocked(NormalizeReason(baselineRetirement.ReasonCode,
                    ReasonCalibrationRetirementFailed));

            lock (_sync) _sourceRetired = true;

            var prepared = await PrepareCalibrationAcquisitionAsync(temporaryRequested, true,
                cancellationToken).ConfigureAwait(false);
            if (prepared.Blocked)
                return MarkCalibrationBlocked(prepared.Reason);

            if (!prepared.Succeeded || prepared.Acquisition is null ||
                prepared.Effective is null || prepared.Health is null)
            {
                var cleanup = await CleanupCalibrationResourcesUnderGateAsync(true,
                    cancellationToken).ConfigureAwait(false);
                if (!cleanup.SafeToReplace)
                    return MarkCalibrationBlocked(NormalizeReason(cleanup.ReasonCode,
                        ReasonCalibrationRetirementFailed));

                var restored = await RestoreCalibrationUnderGateAsync(cancellationToken,
                    prepared.Reason).ConfigureAwait(false);
                return restored.Succeeded
                    ? CameraCalibrationBeginResult.Failure(prepared.Reason)
                    : FailureForCalibrationOwner(restored.ReasonCode);
            }

            CameraCalibrationLease lease;
            lock (_sync)
            {
                if (_calibrationSessionId != sessionId || _disposed)
                    return MarkCalibrationBlocked(ReasonDisposed);
                lease = new CameraCalibrationLease(this, sessionId,
                    _calibrationBaseline!, temporaryRequested, prepared.Effective);
                _calibrationLease = lease;
                _calibrationBlocked = false;
                _calibrationBlockedReason = ReasonCalibrationActive;
            }
            return CameraCalibrationBeginResult.Success(lease);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Restores a baseline recorded by a durable calibration session header.  The
    /// method always retires the currently owned acquisition first, then opens the
    /// exact binding and performs a complete apply/read-back/start/health proof.
    /// It never treats the currently prepared seed as proof of restoration and it
    /// never creates a temporary calibration owner.
    /// </summary>
    internal Task<CameraCalibrationRestoreResult> RestorePersistedCalibrationBaselineAsync(
        Guid sessionId, CameraCalibrationBaselineSnapshot persistedBaseline,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            return Task.FromResult(new CameraCalibrationRestoreResult(false,
                ReasonCalibrationSessionRequired));
        if (persistedBaseline is null)
            return Task.FromResult(new CameraCalibrationRestoreResult(false,
                ReasonCalibrationBaselineMismatch));
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(new CameraCalibrationRestoreResult(false,
                ReasonCalibrationCancelled));

        return RestorePersistedCalibrationBaselineCoreAsync(sessionId, persistedBaseline,
            cancellationToken);
    }

    private async Task<CameraCalibrationRestoreResult>
        RestorePersistedCalibrationBaselineCoreAsync(Guid sessionId,
            CameraCalibrationBaselineSnapshot persistedBaseline,
            CancellationToken cancellationToken)
    {
        try
        {
            if (!await TryEnterOperationAsync(cancellationToken).ConfigureAwait(false))
                return new CameraCalibrationRestoreResult(false, ReasonCalibrationBusy);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CameraCalibrationRestoreResult(false, ReasonCalibrationCancelled);
        }

        try
        {
            CameraAcquisitionService current;
            bool sourceRetired;
            bool newReservation;
            lock (_sync)
            {
                if (_disposed)
                    return new CameraCalibrationRestoreResult(false, ReasonCalibrationDisposed);
                if (_protocolFaulted)
                    return new CameraCalibrationRestoreResult(false, ReasonProtocolUnavailable);
                if (_calibrationAdmissionHold is { } heldSession && heldSession != sessionId)
                    return new CameraCalibrationRestoreResult(false,
                        ReasonCalibrationSessionConflict);
                if (_calibrationSessionId is { } activeSession && activeSession != sessionId)
                    return new CameraCalibrationRestoreResult(false,
                        ReasonCalibrationSessionConflict);
                if (_state is CameraRecoveryState.Recovering or CameraRecoveryState.Exhausted ||
                    _cycleId is not null || _restartReservation is not null ||
                    _candidate is not null || _candidateRaw is not null ||
                    _lateOpenTask is not null || !CanStartPhysicalLocked())
                    return new CameraCalibrationRestoreResult(false, ReasonCalibrationBusy);

                current = _current;
                sourceRetired = _sourceRetired;
                newReservation = _calibrationSessionId is null;
                if (!newReservation && _calibrationBaseline is { } existing &&
                    !BaselineValuesMatch(existing, persistedBaseline))
                    return new CameraCalibrationRestoreResult(false,
                        ReasonCalibrationBaselineMismatch);
            }

            if (!BaselineValuesMatchTarget(persistedBaseline))
                return new CameraCalibrationRestoreResult(false,
                    ReasonCalibrationBaselineMismatch);

            CameraConfigurationResult expected;
            try
            {
                expected = current.Capabilities.ValidateConfiguration(
                    persistedBaseline.Requested);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return new CameraCalibrationRestoreResult(false,
                    ReasonCalibrationBaselineMismatch);
            }
            if (!expected.Succeeded || expected.Effective is null ||
                !expected.Effective.Equals(persistedBaseline.Effective))
                return new CameraCalibrationRestoreResult(false,
                    ReasonCalibrationBaselineMismatch);

            lock (_sync)
            {
                if (_disposed || (_calibrationAdmissionHold is { } heldSession &&
                    heldSession != sessionId) ||
                    (_calibrationSessionId is { } activeSession &&
                    activeSession != sessionId))
                    return new CameraCalibrationRestoreResult(false, ReasonCalibrationBusy);

                if (newReservation)
                {
                    _calibrationSessionId = sessionId;
                    _calibrationPriorState = _state;
                    _calibrationPriorSourceHealthy = _sourceHealthy;
                    _calibrationPriorHealth = _health;
                }

                _calibrationBaseline = persistedBaseline;
                _calibrationLease = null;
                _calibrationBlocked = false;
                _calibrationBlockedReason = ReasonCalibrationActive;
                _sourceHealthy = false;
                SetStateLocked(CameraRecoveryState.RecoveryRequired, false,
                    ReasonCalibrationActive);
                current = _current;
                sourceRetired = _sourceRetired;
            }

            if (!sourceRetired)
            {
                if (!await PullProtocolAsync(current, cancellationToken).ConfigureAwait(false))
                    return MarkPersistedBaselineRestoreBlocked(ReasonProtocolUnavailable);

                var retirement = await RetireCalibrationAcquisitionAsync(current,
                    cancellationToken).ConfigureAwait(false);
                if (!retirement.SafeToReplace)
                    return MarkPersistedBaselineRestoreBlocked(NormalizeReason(
                        retirement.ReasonCode, ReasonCalibrationRetirementFailed));

                lock (_sync) _sourceRetired = true;
            }

            // A previous bounded restore may have left a temporary or late-open
            // owner. Prove that owner quiescent before opening the persisted seed.
            var stale = await CleanupCalibrationResourcesUnderGateAsync(true,
                cancellationToken).ConfigureAwait(false);
            if (!stale.SafeToReplace)
                return MarkPersistedBaselineRestoreBlocked(NormalizeReason(stale.ReasonCode,
                    ReasonCalibrationRetirementFailed));

            var prepared = await PrepareCalibrationAcquisitionAsync(
                persistedBaseline.Requested, false, cancellationToken).ConfigureAwait(false);
            if (prepared.Blocked)
                return MarkPersistedBaselineRestoreBlocked(prepared.Reason);

            if (!prepared.Succeeded || prepared.Acquisition is null ||
                prepared.Effective is null || prepared.Health is null ||
                !prepared.Effective.Equals(persistedBaseline.Effective))
            {
                var failedCleanup = await CleanupCalibrationResourcesUnderGateAsync(true,
                    cancellationToken).ConfigureAwait(false);
                var reason = !failedCleanup.SafeToReplace
                    ? NormalizeReason(failedCleanup.ReasonCode,
                        ReasonCalibrationBaselineRestoreFailed)
                    : NormalizeReason(prepared.Reason, ReasonCalibrationBaselineRestoreFailed);
                return MarkPersistedBaselineRestoreBlocked(reason);
            }

            if (!await PullProtocolAsync(prepared.Acquisition, cancellationToken)
                    .ConfigureAwait(false))
            {
                var protocolCleanup = await CleanupCalibrationResourcesUnderGateAsync(true,
                    cancellationToken).ConfigureAwait(false);
                var reason = !protocolCleanup.SafeToReplace
                    ? NormalizeReason(protocolCleanup.ReasonCode,
                        ReasonCalibrationBaselineRestoreFailed)
                    : ReasonProtocolUnavailable;
                return MarkPersistedBaselineRestoreBlocked(reason);
            }

            lock (_sync)
            {
                _current = prepared.Acquisition;
                _health = prepared.Health;
                _healthObservationRevision++;
                _sourceHealthy = true;
                _sourceRetired = false;
                _calibrationSessionId = null;
                _calibrationLease = null;
                _calibrationBaseline = null;
                _calibrationAcquisition = null;
                _calibrationRaw = null;
                _calibrationOpenTask = null;
                _calibrationBlocked = false;
                _calibrationBlockedReason = ReasonCalibrationBaselineRestored;
                SetStateLocked(CameraRecoveryState.Healthy, true,
                    ReasonCalibrationBaselineRestored);
            }
            return new CameraCalibrationRestoreResult(true,
                ReasonCalibrationBaselineRestored);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return MarkPersistedBaselineRestoreBlocked(ReasonCalibrationCancelled);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return MarkPersistedBaselineRestoreBlocked(ReasonCalibrationBaselineRestoreFailed);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Captures one Runtime-generated calibration frame through the temporary owner.</summary>
    internal async ValueTask<CameraAcquisitionAttempt> CaptureCalibrationAsync(
        CameraCalibrationLease lease, Guid frameId, CancellationToken cancellationToken)
    {
        if (frameId == Guid.Empty)
            return Rejected("CameraCalibrationFrameIdRequired");
        if (cancellationToken.IsCancellationRequested)
            return Rejected(ReasonCalibrationCancelled);

        try
        {
            if (!await TryEnterOperationAsync(cancellationToken).ConfigureAwait(false))
                return Rejected(ReasonCalibrationBusy);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Rejected(ReasonCalibrationCancelled);
        }

        try
        {
            CameraAcquisitionService acquisition;
            string role;
            lock (_sync)
            {
                if (_disposed)
                    return Rejected(ReasonDisposed);
                if (!ReferenceEquals(_calibrationLease, lease) ||
                    _calibrationSessionId != lease.SessionId ||
                    _calibrationBlocked || _calibrationAcquisition is null)
                    return Rejected(ReasonCalibrationSessionRequired);
                acquisition = _calibrationAcquisition;
                role = _logicalCameraRole;
            }

            return await acquisition.AcquireCalibrationAsync(
                new ExecutionCorrelationId(ExecutionKind.Calibration, frameId), role,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Starts or retries restoration through the same exclusive owner gate.</summary>
    internal Task<CameraCalibrationRestoreResult> RestoreCalibrationAsync(
        CameraCalibrationLease lease, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_disposed)
                return Task.FromResult(new CameraCalibrationRestoreResult(false,
                    ReasonCalibrationDisposed));
            if (!ReferenceEquals(_calibrationLease, lease) ||
                _calibrationSessionId != lease.SessionId)
                return Task.FromResult(new CameraCalibrationRestoreResult(false,
                    ReasonCalibrationSessionRequired));
        }

        return RestoreCalibrationCoreAsync(lease, cancellationToken);
    }

    private async Task<CameraCalibrationRestoreResult> RestoreCalibrationCoreAsync(
        CameraCalibrationLease lease, CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                if (!await TryEnterOperationAsync(cancellationToken).ConfigureAwait(false))
                    return new CameraCalibrationRestoreResult(false,
                        ReasonCalibrationBusy);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new CameraCalibrationRestoreResult(false,
                    ReasonCalibrationCancelled);
            }

            try
            {
                return await RestoreCalibrationUnderGateAsync(cancellationToken,
                    ReasonCalibrationRetirementFailed).ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CameraCalibrationRestoreResult(false, ReasonCalibrationCancelled);
        }
    }

    /// <summary>
    /// Called by Recovery DisposeCore while its operation gate is held. It never
    /// restores a baseline after shutdown; it only proves temporary owner cleanup.
    /// </summary>
    private async Task<bool> RetireCalibrationForShutdownAsync()
    {
        lock (_sync)
        {
            if (_calibrationSessionId is null && _calibrationAcquisition is null &&
                _calibrationRaw is null && _calibrationOpenTask is null)
                return true;
        }

        var cleanup = await CleanupCalibrationResourcesUnderGateAsync(false,
            CancellationToken.None).ConfigureAwait(false);
        if (!cleanup.SafeToReplace)
            return false;

        lock (_sync)
        {
            _calibrationSessionId = null;
            _calibrationLease = null;
            _calibrationBaseline = null;
            _calibrationBlocked = false;
            _calibrationBlockedReason = ReasonCalibrationDisposed;
        }
        return true;
    }

    private async Task<CameraDeviceRetirement> RetireCalibrationAcquisitionAsync(
        CameraAcquisitionService acquisition, CancellationToken cancellationToken)
    {
        Task<CameraDeviceRetirement> retirementTask;
        lock (_sync)
        {
            if (!CanStartPhysicalLocked())
                return new CameraDeviceRetirement(false, ReasonCalibrationRetirementTimeout);
            retirementTask = StartPhysicalLocked(() => acquisition.BeginRetirement());
        }

        try
        {
            return await retirementTask.WaitAsync(_options.OperationTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new CameraDeviceRetirement(false, ReasonCalibrationRetirementTimeout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CameraDeviceRetirement(false, ReasonCalibrationCancelled);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new CameraDeviceRetirement(false, ReasonCalibrationRetirementFailed);
        }
    }

    private async Task<CalibrationPreparationResult> PrepareCalibrationAcquisitionAsync(
        RequestedCameraConfiguration requested, bool calibrationOnly,
        CancellationToken cancellationToken)
    {
        CameraOpenResult opened;
        Task<CameraOpenResult> openTask;
        lock (_sync)
        {
            ReconcileCalibrationOpenLocked();
            if (!CanStartPhysicalLocked())
                return CalibrationPreparationResult.BlockedResult(ReasonCalibrationBusy);
            openTask = StartPhysicalLocked(() => _provider.OpenAsync(
                _target.StableDeviceIdentity, _lifetime.Token).AsTask());
            _calibrationOpenTask = openTask;
        }
        ObserveCalibrationOpen(openTask);

        try
        {
            opened = await openTask.WaitAsync(_options.OperationTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return CalibrationPreparationResult.BlockedResult(ReasonCalibrationOpenFailed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CalibrationPreparationResult.BlockedResult(ReasonCalibrationCancelled);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ClearCompletedCalibrationPhysical(openTask);
            lock (_sync) _calibrationOpenTask = null;
            return CalibrationPreparationResult.FailedResult(ReasonCalibrationOpenFailed);
        }

        ClearCompletedCalibrationPhysical(openTask);
        lock (_sync)
        {
            if (ReferenceEquals(_calibrationOpenTask, openTask))
                _calibrationOpenTask = null;
            if (opened.Succeeded && opened.Device is not null && _calibrationRaw is null)
                _calibrationRaw = opened.Device;
        }

        if (!opened.Succeeded || opened.Device is null)
            return CalibrationPreparationResult.FailedResult(ReasonCalibrationOpenFailed);

        ICameraDevice raw;
        lock (_sync) raw = _calibrationRaw ?? opened.Device;
        bool exactBinding;
        try { exactBinding = BindingMatches(raw.Descriptor, _target); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { exactBinding = false; }
        if (!exactBinding)
            return CalibrationPreparationResult.FailedResult(ReasonCalibrationIdentityMismatch);

        if (raw is not IControlledCameraDevice controlled)
            return CalibrationPreparationResult.FailedResult(ReasonCalibrationNotControlled);

        CameraConfigurationResult expected;
        try { expected = controlled.Capabilities.ValidateConfiguration(requested); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return CalibrationPreparationResult.FailedResult(ReasonCalibrationConfigurationInvalid); }
        if (!expected.Succeeded || expected.Effective is null)
            return CalibrationPreparationResult.FailedResult(ReasonCalibrationConfigurationInvalid);

        CameraConfigurationResult applied;
        Task<CameraConfigurationResult> applyTask;
        lock (_sync)
        {
            if (!CanStartPhysicalLocked())
                return CalibrationPreparationResult.BlockedResult(ReasonCalibrationBusy);
            applyTask = StartPhysicalLocked(() => controlled.ApplyConfigurationAsync(
                requested, _lifetime.Token).AsTask());
        }
        try
        {
            applied = await applyTask.WaitAsync(_options.OperationTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return CalibrationPreparationResult.BlockedResult(ReasonCalibrationApplyFailed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CalibrationPreparationResult.BlockedResult(ReasonCalibrationCancelled);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ClearCompletedCalibrationPhysical(applyTask);
            return CalibrationPreparationResult.FailedResult(ReasonCalibrationApplyFailed);
        }
        ClearCompletedCalibrationPhysical(applyTask);

        CameraConfigurationResult readBack;
        try { readBack = controlled.Capabilities.ValidateReadBack(requested, applied); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return CalibrationPreparationResult.FailedResult(ReasonCalibrationReadBackFailed); }
        if (!readBack.Succeeded || readBack.Effective is null)
            return CalibrationPreparationResult.FailedResult(ReasonCalibrationReadBackFailed);

        CameraAcquisitionService candidate;
        try
        {
            candidate = new CameraAcquisitionService(controlled, readBack.Effective,
                _clock, _acquisitionOptions);
            if (calibrationOnly)
                candidate.EnableCalibrationAcquisition();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return CalibrationPreparationResult.FailedResult(ReasonCalibrationConfigurationInvalid); }

        lock (_sync)
        {
            _calibrationRaw = null;
            _calibrationAcquisition = candidate;
        }

        CameraOperationResult started;
        Task<CameraOperationResult> startTask;
        lock (_sync)
        {
            if (!CanStartPhysicalLocked())
                return CalibrationPreparationResult.BlockedResult(ReasonCalibrationBusy);
            startTask = StartPhysicalLocked(() => controlled.StartAsync(_lifetime.Token).AsTask());
        }
        try
        {
            started = await startTask.WaitAsync(_options.OperationTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return CalibrationPreparationResult.BlockedResult(ReasonCalibrationStartFailed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CalibrationPreparationResult.BlockedResult(ReasonCalibrationCancelled);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ClearCompletedCalibrationPhysical(startTask);
            return CalibrationPreparationResult.FailedResult(ReasonCalibrationStartFailed);
        }
        ClearCompletedCalibrationPhysical(startTask);
        if (!started.Succeeded)
            return CalibrationPreparationResult.FailedResult(ReasonCalibrationStartFailed);

        CameraHealthSnapshot? health;
        Task<CameraHealthSnapshot?> healthTask;
        lock (_sync)
        {
            if (!CanStartPhysicalLocked())
                return CalibrationPreparationResult.BlockedResult(ReasonCalibrationBusy);
            healthTask = StartPhysicalLocked(() => candidate.ReadHealthAsync());
        }
        try
        {
            health = await healthTask.WaitAsync(_options.OperationTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return CalibrationPreparationResult.BlockedResult(ReasonCalibrationHealthFailed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CalibrationPreparationResult.BlockedResult(ReasonCalibrationCancelled);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ClearCompletedCalibrationPhysical(healthTask);
            return CalibrationPreparationResult.FailedResult(ReasonCalibrationHealthFailed);
        }
        ClearCompletedCalibrationPhysical(healthTask);
        if (health is null || !IsHealthy(health))
            return CalibrationPreparationResult.FailedResult(ReasonCalibrationHealthFailed);

        return CalibrationPreparationResult.SuccessResult(candidate, readBack.Effective, health);
    }

    private async Task<CameraDeviceRetirement> CleanupCalibrationResourcesUnderGateAsync(
        bool bounded, CancellationToken cancellationToken)
    {
        if (!await WaitForCalibrationPhysicalDrainAsync(bounded, cancellationToken)
                .ConfigureAwait(false))
            return new CameraDeviceRetirement(false, bounded
                ? ReasonCalibrationRetirementTimeout : ReasonCalibrationRetirementFailed);

        lock (_sync) ReconcileCalibrationOpenLocked();

        CameraAcquisitionService? acquisition;
        ICameraDevice? raw;
        lock (_sync)
        {
            acquisition = _calibrationAcquisition;
            raw = _calibrationRaw;
        }

        if (acquisition is not null)
        {
            Task<CameraDeviceRetirement> retirementTask;
            lock (_sync)
            {
                if (!CanStartPhysicalLocked())
                    return new CameraDeviceRetirement(false, ReasonCalibrationRetirementTimeout);
                retirementTask = StartPhysicalLocked(() => acquisition.BeginRetirement());
            }

            try
            {
                var retirement = bounded
                    ? await retirementTask.WaitAsync(_options.OperationTimeout,
                        cancellationToken).ConfigureAwait(false)
                    : await retirementTask.ConfigureAwait(false);
                if (!retirement.SafeToReplace)
                    return retirement;
                lock (_sync)
                {
                    if (ReferenceEquals(_calibrationAcquisition, acquisition))
                        _calibrationAcquisition = null;
                }
            }
            catch (TimeoutException)
            {
                return new CameraDeviceRetirement(false, ReasonCalibrationRetirementTimeout);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new CameraDeviceRetirement(false, ReasonCalibrationCancelled);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return new CameraDeviceRetirement(false, ReasonCalibrationRetirementFailed);
            }
            return new CameraDeviceRetirement(true, "CameraCalibrationOwnerRetired");
        }

        if (raw is not null)
        {
            Task<CameraDeviceRetirement> retirementTask;
            lock (_sync)
            {
                if (!CanStartPhysicalLocked())
                    return new CameraDeviceRetirement(false, ReasonCalibrationRetirementTimeout);
                retirementTask = StartPhysicalLocked(() => RetireRawPhysicalAsync(raw));
            }
            try
            {
                var retirement = bounded
                    ? await retirementTask.WaitAsync(_options.OperationTimeout,
                        cancellationToken).ConfigureAwait(false)
                    : await retirementTask.ConfigureAwait(false);
                if (retirement.SafeToReplace)
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_calibrationRaw, raw))
                            _calibrationRaw = null;
                    }
                }
                return retirement;
            }
            catch (TimeoutException)
            {
                return new CameraDeviceRetirement(false, ReasonCalibrationRetirementTimeout);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new CameraDeviceRetirement(false, ReasonCalibrationCancelled);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return new CameraDeviceRetirement(false, ReasonCalibrationRetirementFailed);
            }
        }

        return new CameraDeviceRetirement(true, "CameraCalibrationOwnerAbsent");
    }

    private async Task<CameraCalibrationRestoreResult> RestoreCalibrationUnderGateAsync(
        CancellationToken cancellationToken, string fallbackReason)
    {
        CameraCalibrationBaselineSnapshot baseline;
        lock (_sync)
        {
            if (_disposed)
                return new CameraCalibrationRestoreResult(false, ReasonCalibrationDisposed);
            if (_calibrationSessionId is null || _calibrationBaseline is not { } snapshot)
                return new CameraCalibrationRestoreResult(false,
                    ReasonCalibrationSessionRequired);
            baseline = snapshot;
        }

        var cleanup = await CleanupCalibrationResourcesUnderGateAsync(true, cancellationToken)
            .ConfigureAwait(false);
        if (!cleanup.SafeToReplace)
        {
            var reason = NormalizeReason(cleanup.ReasonCode, fallbackReason);
            lock (_sync) MarkCalibrationBlockedLocked(reason);
            return new CameraCalibrationRestoreResult(false, reason);
        }

        var prepared = await PrepareCalibrationAcquisitionAsync(baseline.Requested, false,
            cancellationToken).ConfigureAwait(false);
        if (prepared.Blocked)
        {
            lock (_sync) MarkCalibrationBlockedLocked(prepared.Reason);
            return new CameraCalibrationRestoreResult(false, prepared.Reason);
        }
        if (!prepared.Succeeded || prepared.Acquisition is null ||
            prepared.Effective is null || prepared.Health is null)
        {
            var failedCleanup = await CleanupCalibrationResourcesUnderGateAsync(true,
                cancellationToken).ConfigureAwait(false);
            var reason = !failedCleanup.SafeToReplace
                ? NormalizeReason(failedCleanup.ReasonCode, fallbackReason)
                : prepared.Reason;
            lock (_sync) MarkCalibrationBlockedLocked(reason);
            return new CameraCalibrationRestoreResult(false, reason);
        }

        if (!prepared.Effective.Equals(baseline.Effective))
        {
            _ = await CleanupCalibrationResourcesUnderGateAsync(true, cancellationToken)
                .ConfigureAwait(false);
            lock (_sync) MarkCalibrationBlockedLocked(ReasonCalibrationBaselineMismatch);
            return new CameraCalibrationRestoreResult(false, ReasonCalibrationBaselineMismatch);
        }

        lock (_sync)
        {
            _current = prepared.Acquisition;
            _health = prepared.Health;
            _healthObservationRevision++;
            _sourceHealthy = true;
            _sourceRetired = false;
            _calibrationSessionId = null;
            _calibrationLease = null;
            _calibrationBaseline = null;
            _calibrationAcquisition = null;
            _calibrationRaw = null;
            _calibrationOpenTask = null;
            _calibrationBlocked = false;
            _calibrationBlockedReason = ReasonCalibrationRestored;
            SetStateLocked(CameraRecoveryState.Healthy, true, ReasonCalibrationRestored);
        }
        return new CameraCalibrationRestoreResult(true, ReasonCalibrationRestored);
    }

    private CameraCalibrationBeginResult AbortCalibrationReservation(string reason)
    {
        Guid? sessionId;
        lock (_sync)
        {
            sessionId = _calibrationSessionId;
            if (_calibrationAcquisition is null && _calibrationRaw is null &&
                _calibrationOpenTask is null)
                ClearCalibrationReservationLocked(reason);
            else
                MarkCalibrationBlockedLocked(reason);
        }
        return CameraCalibrationBeginResult.Failure(reason, sessionId);
    }

    private CameraCalibrationBeginResult MarkCalibrationBlocked(string reason)
    {
        Guid? sessionId;
        lock (_sync)
        {
            MarkCalibrationBlockedLocked(reason);
            sessionId = _calibrationSessionId;
        }
        return CameraCalibrationBeginResult.Failure(reason, sessionId);
    }

    private CameraCalibrationBeginResult FailureForCalibrationOwner(string reason)
    {
        lock (_sync)
            return CameraCalibrationBeginResult.Failure(reason, _calibrationSessionId);
    }

    private void MarkCalibrationBlockedLocked(string reason)
    {
        _calibrationBlocked = true;
        _calibrationBlockedReason = NormalizeReason(reason, ReasonCalibrationActive);
        _sourceHealthy = false;
        SetStateLocked(CameraRecoveryState.RecoveryRequired, false, _calibrationBlockedReason);
    }

    private void ClearCalibrationReservationLocked(string reason)
    {
        _calibrationSessionId = null;
        _calibrationLease = null;
        _calibrationBaseline = null;
        _calibrationBlocked = false;
        _calibrationBlockedReason = NormalizeReason(reason, ReasonCalibrationActive);
        _state = _calibrationPriorState;
        _sourceHealthy = _calibrationPriorSourceHealthy;
        _health = _calibrationPriorHealth;
        _revision++;
    }

    private bool BaselineValuesMatchTarget(CameraCalibrationBaselineSnapshot baseline) =>
        baseline.Target == _target &&
        StringComparer.Ordinal.Equals(baseline.LogicalCameraRole, _logicalCameraRole);

    private static bool BaselineValuesMatch(CameraCalibrationBaselineSnapshot left,
        CameraCalibrationBaselineSnapshot right) =>
        left.Target == right.Target &&
        StringComparer.Ordinal.Equals(left.LogicalCameraRole, right.LogicalCameraRole) &&
        left.Requested.Equals(right.Requested) && left.Effective.Equals(right.Effective);

    private CameraCalibrationRestoreResult MarkPersistedBaselineRestoreBlocked(string reason)
    {
        var normalized = NormalizeReason(reason, ReasonCalibrationBaselineRestoreFailed);
        lock (_sync)
        {
            if (_calibrationSessionId is not null)
                MarkCalibrationBlockedLocked(normalized);
        }
        return new CameraCalibrationRestoreResult(false, normalized);
    }

    private async Task<bool> WaitForCalibrationPhysicalDrainAsync(bool bounded,
        CancellationToken cancellationToken)
    {
        Task? physical;
        lock (_sync) physical = _physicalTask;
        if (physical is null)
            return true;

        try
        {
            if (bounded)
                await physical.WaitAsync(_options.OperationTimeout, cancellationToken)
                    .ConfigureAwait(false);
            else
                await physical.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The task is settled; its fault is represented by the typed cleanup
            // result and is not exposed as provider exception text.
        }

        lock (_sync)
        {
            if (ReferenceEquals(_physicalTask, physical) && physical.IsCompleted)
            {
                _ = physical.Exception;
                _physicalTask = null;
            }
        }
        return true;
    }

    private void ClearCompletedCalibrationPhysical(Task task)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_physicalTask, task) && task.IsCompleted)
            {
                _ = task.Exception;
                _physicalTask = null;
            }
        }
    }

    private void ObserveCalibrationOpen(Task<CameraOpenResult> openTask)
    {
        _ = openTask.ContinueWith(static (completed, state) =>
        {
            ((CameraRecoveryService)state!).ReconcileCalibrationOpen(completed);
        }, this, CancellationToken.None, TaskContinuationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default);
    }

    private void ReconcileCalibrationOpen(Task<CameraOpenResult> openTask)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_calibrationOpenTask, openTask) || !openTask.IsCompleted)
                return;
            ReconcileCalibrationOpenLocked();
        }
    }

    private void ReconcileCalibrationOpenLocked()
    {
        var task = _calibrationOpenTask;
        if (task is null || !task.IsCompleted)
            return;
        try
        {
            if (_calibrationRaw is null && task.Status == TaskStatus.RanToCompletion &&
                task.Result is { Succeeded: true, Device: not null } result)
                _calibrationRaw = result.Device;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = task.Exception;
        }
        _calibrationOpenTask = null;
    }

    private readonly record struct CalibrationPreparationResult(
        bool Succeeded, bool Blocked, string Reason, CameraAcquisitionService? Acquisition,
        EffectiveCameraConfiguration? Effective, CameraHealthSnapshot? Health)
    {
        internal static CalibrationPreparationResult SuccessResult(
            CameraAcquisitionService acquisition, EffectiveCameraConfiguration effective,
            CameraHealthSnapshot health) => new(true, false, "CameraCalibrationReady",
                acquisition, effective, health);

        internal static CalibrationPreparationResult FailedResult(string reason) =>
            new(false, false, reason, null, null, null);

        internal static CalibrationPreparationResult BlockedResult(string reason) =>
            new(false, true, reason, null, null, null);
    }
}

/// <summary>Internal lease that keeps the Recovery calibration owner exclusive until restore.</summary>
internal sealed class CameraCalibrationLease : IAsyncDisposable
{
    private readonly CameraRecoveryService _owner;
    private readonly CameraCalibrationBaselineSnapshot _baseline;
    private readonly RequestedCameraConfiguration _temporaryRequested;
    private readonly EffectiveCameraConfiguration _temporaryEffective;
    private Task<CameraCalibrationRestoreResult>? _restoreTask;

    internal CameraCalibrationLease(CameraRecoveryService owner, Guid sessionId,
        CameraCalibrationBaselineSnapshot baseline,
        RequestedCameraConfiguration temporaryRequested,
        EffectiveCameraConfiguration temporaryEffective)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        SessionId = sessionId;
        _baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
        _temporaryRequested = temporaryRequested ??
            throw new ArgumentNullException(nameof(temporaryRequested));
        _temporaryEffective = temporaryEffective ??
            throw new ArgumentNullException(nameof(temporaryEffective));
    }

    internal Guid SessionId { get; }
    internal CameraCalibrationBaselineSnapshot Baseline => _baseline;
    internal CameraBindingTarget BaselineTarget => _baseline.Target;
    internal string LogicalCameraRole => _baseline.LogicalCameraRole;
    internal RequestedCameraConfiguration BaselineRequested => _baseline.Requested;
    internal EffectiveCameraConfiguration BaselineEffective => _baseline.Effective;
    internal CameraHealthSnapshot BaselineHealth => _baseline.Health;
    internal RequestedCameraConfiguration TemporaryRequested => _temporaryRequested;
    internal EffectiveCameraConfiguration TemporaryEffective => _temporaryEffective;

    internal ValueTask<CameraAcquisitionAttempt> CaptureAsync(Guid frameId,
        CancellationToken cancellationToken = default) =>
        _owner.CaptureCalibrationAsync(this, frameId, cancellationToken);

    internal Task<CameraCalibrationRestoreResult> RestoreAsync(
        CancellationToken cancellationToken = default)
    {
        lock (this)
        {
            if (_restoreTask is not null)
                return _restoreTask;
            _restoreTask = RestoreAndReleaseAsync(cancellationToken);
            return _restoreTask;
        }
    }

    private async Task<CameraCalibrationRestoreResult> RestoreAndReleaseAsync(
        CancellationToken cancellationToken)
    {
        var result = await _owner.RestoreCalibrationAsync(this, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            lock (this) _restoreTask = null;
        }
        return result;
    }

    public ValueTask DisposeAsync() => new(RestoreAsync(CancellationToken.None));
}
