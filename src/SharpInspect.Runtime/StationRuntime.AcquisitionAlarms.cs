using SharpInspect.Abstractions;
using SharpInspect.Runtime.Alarms;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    internal const string CameraAcquisitionAlarmSource = "Runtime.CameraAcquisition";
    private const string CameraAcquisitionMappingBlocker = "CameraAcquisitionAlarmMappingUnavailable";
    private const string CameraAcquisitionProtocolBlocker = "CameraAcquisitionProtocolFault";
    private const AlarmResetPrerequisites CameraAcquisitionResetPrerequisites =
        AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution;
    private Task? _cameraAcquisitionObservation;
    private Guid? _cameraAcquisitionProtocolEpoch;
    private long _cameraAcquisitionProtocolCursor;
    private bool _cameraAcquisitionProtocolFault;
    private bool CameraAcquisitionRegistered =>
        _cameraAcquisitionService is not null || _cameraRecoveryService is not null;

    internal static string CameraAcquisitionAlarmCode(CameraProtocolViolationKind kind) => kind switch
    {
        CameraProtocolViolationKind.EarlyFrame => "CameraEarlyFrame",
        CameraProtocolViolationKind.ExtraFrame => "CameraExtraFrame",
        CameraProtocolViolationKind.LateFrame => "CameraLateFrame",
        CameraProtocolViolationKind.CorrelationMismatch => "CameraCorrelationMismatch",
        CameraProtocolViolationKind.EarlyHardwarePulse => "CameraEarlyHardwarePulse",
        CameraProtocolViolationKind.DuplicateHardwarePulse => "CameraDuplicateHardwarePulse",
        CameraProtocolViolationKind.TriggerWhileBusy => "CameraTriggerWhileBusy",
        CameraProtocolViolationKind.InvalidFrame => "CameraInvalidFrame",
        CameraProtocolViolationKind.ObservationGap => "CameraObservationGap",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private bool IsCameraAcquisitionAlarmMappingValid(AlarmPolicy? policy)
    {
        if (!CameraAcquisitionRegistered) return true;
        if (policy is null) return false;
        return Enum.GetValues<CameraProtocolViolationKind>().All(kind =>
            policy.TryGetRule(CameraAcquisitionAlarmCode(kind), out var rule) && rule is not null &&
            rule.Source == CameraAcquisitionAlarmSource && rule.IsLatched &&
            rule.ProductionImpact == ProductionImpact.BlockNewTriggers &&
            (rule.ResetPrerequisites & CameraAcquisitionResetPrerequisites) == CameraAcquisitionResetPrerequisites);
    }

    private StationStateSnapshot ApplyCameraAcquisitionStateLocked(StationStateSnapshot next)
    {
        if (!CameraAcquisitionRegistered) return next;
        var valid = IsCameraAcquisitionAlarmMappingValid(ConfiguredAlarmPolicy);
        if (valid && !_cameraAcquisitionProtocolFault) return next;
        var blockers = next.AdmissionBlockers.Where(code => code is not
            CameraAcquisitionMappingBlocker and not CameraAcquisitionProtocolBlocker).ToList();
        var alarmState = next.AlarmState;
        if (!valid)
        {
            blockers.Add(CameraAcquisitionMappingBlocker);
            if (!blockers.Contains("AlarmAuthorityUnavailable", StringComparer.Ordinal))
                blockers.Add("AlarmAuthorityUnavailable");
            alarmState = new AlarmStateSnapshot(false, CameraAcquisitionMappingBlocker,
                next.RuntimeEpoch, next.Revision, ConfiguredAlarmPolicy,
                alarmState?.Instances ?? Array.Empty<AlarmInstanceSnapshot>(),
                alarmState?.Plc ?? new AlarmPlcProjection(Array.Empty<AlarmPlcEntry>(), 0, 0, false));
        }
        if (_cameraAcquisitionProtocolFault) blockers.Add(CameraAcquisitionProtocolBlocker);
        return next with { Ready = false, ArmState = ProductionArmState.Disarmed,
            AlarmState = alarmState, AdmissionBlockers = new AdmissionBlockers(blockers) };
    }

    // This path only reads the component's bounded facts. A vendor callback can
    // neither register this source nor call the station's observation boundary.
    private void ScheduleCameraAcquisitionObservationsLocked()
    {
        if (!CameraAcquisitionRegistered || _disposed || _shutdownRequested ||
            !_storeInitialization.IsCompletedSuccessfully ||
            _cameraAcquisitionObservation is { IsCompleted: false } ||
            !IsCameraAcquisitionAlarmMappingValid(ConfiguredAlarmPolicy) ||
            !_registeredAlarmSources.Contains(CameraAcquisitionAlarmSource)) return;
        _cameraAcquisitionObservation = Task.Run(ObserveCameraAcquisitionAsync);
    }

    private async Task ObserveCameraAcquisitionAsync()
    {
        try
        {
            await RefreshCameraProtocolAsync(_lifetime.Token).ConfigureAwait(false);
            var batch = ReadCameraProtocol(_cameraAcquisitionProtocolCursor, 64);
            var changedEpoch = _cameraAcquisitionProtocolEpoch.HasValue &&
                _cameraAcquisitionProtocolEpoch != batch.Epoch;
            if (changedEpoch || batch.Overflowed)
            {
                if (!await PersistCameraProtocolAlarmAsync(CameraProtocolViolationKind.ObservationGap,
                    DateTimeOffset.UtcNow).ConfigureAwait(false)) return;
                _cameraAcquisitionProtocolCursor = batch.FirstAvailableSequence - 1;
                if (changedEpoch)
                    batch = ReadCameraProtocol(_cameraAcquisitionProtocolCursor, 64);
            }
            _cameraAcquisitionProtocolEpoch = batch.Epoch;
            foreach (var observation in batch.Observations)
            {
                if (observation.Sequence <= _cameraAcquisitionProtocolCursor) continue;
                if (!await PersistCameraProtocolAlarmAsync(observation.Kind,
                    observation.ObservedAt.HostObservedAtUtc).ConfigureAwait(false)) return;
                // Advancing to ThroughSequence here would silently lose paged facts.
                _cameraAcquisitionProtocolCursor = observation.Sequence;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MarkAuditFault("CameraProtocolObservationUnavailable", alarmAuthorityUnavailable: true);
        }
    }

    private async Task<CameraProtocolSnapshot> RefreshCameraProtocolAsync(CancellationToken cancellationToken) =>
        _cameraRecoveryService is not null
            ? await _cameraRecoveryService.RefreshProtocolObservationsAsync(cancellationToken).ConfigureAwait(false)
            : await _cameraAcquisitionService!.RefreshProtocolObservationsAsync(cancellationToken).ConfigureAwait(false);

    private CameraProtocolSnapshot ReadCameraProtocol(long afterSequence, int maximumCount) =>
        _cameraRecoveryService is not null
            ? _cameraRecoveryService.ReadProtocolObservations(afterSequence, maximumCount)
            : _cameraAcquisitionService!.ReadProtocolObservations(afterSequence, maximumCount);

    private async Task<bool> PersistCameraProtocolAlarmAsync(CameraProtocolViolationKind kind,
        DateTimeOffset observedAtUtc)
    {
        AlarmObservation observation;
        lock (_sync)
        {
            if (_disposed || _shutdownRequested) return false;
            // No automatic success/reconnect clears this latch. Governed recovery
            // and formal acquisition admission are later capabilities.
            _cameraAcquisitionProtocolFault = true;
            PublishLocked(_snapshot);
            var code = CameraAcquisitionAlarmCode(kind);
            observation = new AlarmObservation(_snapshot.RuntimeEpoch,
                NextAlarmObservationSequenceLocked(code), code, CameraAcquisitionAlarmSource,
                false, observedAtUtc);
        }
        var result = await ObserveAlarmAsync(observation, _lifetime.Token).ConfigureAwait(false);
        if (result.Accepted) return true;
        MarkAuditFault("CameraProtocolAlarmUnavailable", alarmAuthorityUnavailable: true);
        return false;
    }
}
