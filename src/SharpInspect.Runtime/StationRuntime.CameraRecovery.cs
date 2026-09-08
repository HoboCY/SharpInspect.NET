using SharpInspect.Abstractions;
using SharpInspect.Runtime.Alarms;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    internal const string CameraRecoveryAlarmSource = "Runtime.CameraRecovery";
    internal const string CameraDisconnectedAlarmCode = "CameraDisconnected";
    internal const string CameraRecoveryFailedAlarmCode = "CameraRecoveryFailed";
    private Task? _cameraRecoveryObservation;
    private Guid? _cameraRecoveryEventEpoch;
    private long _cameraRecoveryEventCursor;
    private long _cameraRecoveryHealthRevision;
    private bool _cameraRecoveryObservationFault;

    private bool IsCameraRecoveryAlarmMappingValid(AlarmPolicy? policy)
    {
        if (_cameraRecoveryService is null) return true;
        if (policy is null) return false;
        const AlarmResetPrerequisites required = AlarmResetPrerequisites.RecoveryComplete |
            AlarmResetPrerequisites.NoActiveExecution | AlarmResetPrerequisites.NoPendingDelivery;
        return new[] { CameraDisconnectedAlarmCode, CameraRecoveryFailedAlarmCode }.All(code =>
            policy.TryGetRule(code, out var rule) && rule is not null &&
            rule.Source == CameraRecoveryAlarmSource &&
            rule.ProductionImpact == ProductionImpact.BlockNewTriggers &&
            (code != CameraRecoveryFailedAlarmCode || rule.IsLatched) &&
            (rule.ResetPrerequisites & required) == required);
    }

    private StationStateSnapshot ApplyCameraRecoveryStateLocked(StationStateSnapshot next)
    {
        if (_cameraRecoveryService is null) return next;
        var recovery = _cameraRecoveryService.GetSnapshot();
        var blockers = next.AdmissionBlockers.Where(code => code is not
            "CameraRecoveryQualificationOnly" and not "CameraRecoveryUnavailable" and not
            "CameraRecoveryInProgress" and not "CameraRecoveryAlarmMappingUnavailable" and not
            "CameraRecoveryObservationUnavailable").ToList();
        blockers.Add("CameraRecoveryQualificationOnly");
        if (!recovery.SourceHealthy || recovery.State != CameraRecoveryState.Healthy)
            blockers.Add("CameraRecoveryUnavailable");
        if (recovery.State is CameraRecoveryState.RecoveryRequired or CameraRecoveryState.Recovering)
            blockers.Add("CameraRecoveryInProgress");
        if (_cameraRecoveryObservationFault) blockers.Add("CameraRecoveryObservationUnavailable");
        var alarms = next.AlarmState;
        if (!IsCameraRecoveryAlarmMappingValid(ConfiguredAlarmPolicy))
        {
            blockers.Add("CameraRecoveryAlarmMappingUnavailable");
            if (!blockers.Contains("AlarmAuthorityUnavailable", StringComparer.Ordinal))
                blockers.Add("AlarmAuthorityUnavailable");
            alarms = new AlarmStateSnapshot(false, "CameraRecoveryAlarmMappingUnavailable",
                next.RuntimeEpoch, next.Revision, ConfiguredAlarmPolicy,
                alarms?.Instances ?? Array.Empty<AlarmInstanceSnapshot>(),
                alarms?.Plc ?? new AlarmPlcProjection(Array.Empty<AlarmPlcEntry>(), 0, 0, false));
        }
        var camera = new CameraHealth(HealthState.Unknown, HealthState.Unknown, HealthState.Unknown,
            next.Camera.Buffers);
        if (recovery.Health is { } health)
            camera = new CameraHealth(
                health.ProviderAvailability != CameraProviderAvailability.Available ? HealthState.Faulted :
                    health.Connection switch { CameraConnectionState.Open => HealthState.Healthy,
                        CameraConnectionState.Disconnected => HealthState.Faulted, _ => HealthState.Unknown },
                health.Configuration == CameraConfigurationState.Applied ? HealthState.Healthy : HealthState.Unknown,
                health.Acquisition == CameraAcquisitionState.Armed ? HealthState.Healthy : HealthState.Unknown,
                camera.Buffers);
        return next with { CameraRecovery = recovery, Camera = camera, AlarmState = alarms,
            Ready = false, ArmState = ProductionArmState.Disarmed,
            AdmissionBlockers = new AdmissionBlockers(blockers) };
    }

    private void ScheduleCameraRecoveryObservationsLocked()
    {
        if (_cameraRecoveryService is null || _disposed || _shutdownRequested ||
            _cameraRecoveryObservationFault || !_storeInitialization.IsCompletedSuccessfully ||
            _cameraRecoveryObservation is { IsCompleted: false } ||
            !IsCameraRecoveryAlarmMappingValid(ConfiguredAlarmPolicy) ||
            !_registeredAlarmSources.Contains(CameraRecoveryAlarmSource)) return;
        _cameraRecoveryObservation = Task.Run(ObserveCameraRecoveryAsync);
    }

    private async Task ObserveCameraRecoveryAsync()
    {
        try
        {
            await _cameraRecoveryService!.RefreshAsync(_lifetime.Token).ConfigureAwait(false);
            var page = _cameraRecoveryService.ReadEvents(_cameraRecoveryEventCursor, 64);
            if ((_cameraRecoveryEventEpoch.HasValue && _cameraRecoveryEventEpoch != page.RecoveryEpoch) ||
                page.Overflowed || page.ThroughSequence < _cameraRecoveryEventCursor)
            {
                await FailCameraRecoveryObservationAsync().ConfigureAwait(false);
                return;
            }
            _cameraRecoveryEventEpoch = page.RecoveryEpoch;
            foreach (var fact in page.Events)
            {
                if (fact.Sequence != _cameraRecoveryEventCursor + 1)
                {
                    await FailCameraRecoveryObservationAsync().ConfigureAwait(false);
                    return;
                }
                var accepted = fact.Kind switch
                {
                    CameraRecoveryEventKind.SourceDisconnected =>
                        await PersistCameraRecoveryAlarmAsync(CameraDisconnectedAlarmCode, false,
                            fact.Timestamp.HostObservedAtUtc).ConfigureAwait(false),
                    CameraRecoveryEventKind.CycleExhausted =>
                        await PersistCameraRecoveryAlarmAsync(CameraRecoveryFailedAlarmCode, false,
                            fact.Timestamp.HostObservedAtUtc).ConfigureAwait(false),
                    _ => true
                };
                if (!accepted) return;
                _cameraRecoveryEventCursor = fact.Sequence;
            }
            // Renew source health only after this heartbeat has actually observed
            // the owned camera. A historical reconnect event is not fresh health.
            var current = _cameraRecoveryService.GetSnapshot();
            if (current.SourceHealthy && current.State == CameraRecoveryState.Healthy &&
                current.HealthObservationRevision > _cameraRecoveryHealthRevision &&
                page.ThroughSequence == _cameraRecoveryEventCursor &&
                await PublishRecoveredCameraSourceAsync(DateTimeOffset.UtcNow).ConfigureAwait(false))
                _cameraRecoveryHealthRevision = current.HealthObservationRevision;
            else
            {
                bool healthyObservationExpired;
                lock (_sync)
                    healthyObservationExpired = ConfiguredAlarmPolicy is { } policy &&
                        _alarmObservations.TryGetValue(CameraDisconnectedAlarmCode, out var prior) &&
                        prior.Healthy && !IsFresh(prior, policy);
                // A failed or skipped probe cannot leave a previously healthy
                // source usable indefinitely. This observer owns both expiry and
                // recovery transitions, so their source sequences stay ordered.
                if (!current.SourceHealthy || current.State != CameraRecoveryState.Healthy ||
                    healthyObservationExpired)
                    await PersistCameraRecoveryAlarmAsync(CameraDisconnectedAlarmCode, false,
                        DateTimeOffset.UtcNow).ConfigureAwait(false);
            }
            lock (_sync) if (!_disposed && !_shutdownRequested) PublishLocked(_snapshot);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_sync) _cameraRecoveryObservationFault = true;
            MarkAuditFault("CameraRecoveryObservationUnavailable", alarmAuthorityUnavailable: true);
        }
    }

    private async Task FailCameraRecoveryObservationAsync()
    {
        lock (_sync) _cameraRecoveryObservationFault = true;
        await PersistCameraRecoveryAlarmAsync(CameraRecoveryFailedAlarmCode, false,
            DateTimeOffset.UtcNow).ConfigureAwait(false);
        MarkAuditFault("CameraRecoveryObservationGap", alarmAuthorityUnavailable: true);
    }

    private async Task<bool> PublishRecoveredCameraSourceAsync(DateTimeOffset observedAtUtc)
    {
        if (!await PersistCameraRecoveryAlarmAsync(CameraDisconnectedAlarmCode, true,
            observedAtUtc).ConfigureAwait(false)) return false;
        return await PersistCameraRecoveryAlarmAsync(CameraRecoveryFailedAlarmCode, true,
            observedAtUtc).ConfigureAwait(false);
    }

    private async Task<bool> PersistCameraRecoveryAlarmAsync(string code, bool healthy,
        DateTimeOffset observedAtUtc)
    {
        AlarmObservation observation;
        lock (_sync)
        {
            if (_disposed || _shutdownRequested) return false;
            if (_alarmObservations.TryGetValue(code, out var prior) && prior.Healthy == healthy &&
                ConfiguredAlarmPolicy is { } policy && IsFresh(prior, policy)) return true;
            observation = new AlarmObservation(_snapshot.RuntimeEpoch, NextAlarmObservationSequenceLocked(code),
                code, CameraRecoveryAlarmSource, healthy, observedAtUtc);
        }
        var result = await ObserveAlarmAsync(observation, _lifetime.Token).ConfigureAwait(false);
        if (result.Accepted) return true;
        MarkAuditFault("CameraRecoveryAlarmUnavailable", alarmAuthorityUnavailable: true);
        return false;
    }
}
