using SharpInspect.Abstractions;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private bool _cameraNetworkMaintenanceActive;

    public ValueTask<CameraNetworkOperationResult> ChangeNetworkConfigurationAsync(
        CameraNetworkChangeRequest request, CancellationToken cancellationToken = default) =>
        _cameraSetupRuntime.ChangeNetworkConfigurationAsync(request, cancellationToken);

    public ValueTask<CameraNetworkQueryResult> GetNetworkMaintenanceAsync(CameraBindingTarget target,
        CommandInvocation invocation, CancellationToken cancellationToken = default) =>
        _cameraSetupRuntime.GetNetworkMaintenanceAsync(target, invocation, cancellationToken);

    private async ValueTask<string?> TryReserveCameraNetworkMaintenance(CancellationToken cancellationToken)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_disposed || _shutdownRequested) return "RuntimeStopped";
                // Registered acquisition/recovery singletons retain physical ownership even when
                // disconnected or exhausted. Maintenance must not dispose and replace their owners.
                if (_cameraAcquisitionService is not null || _cameraRecoveryService is not null)
                    return "CameraProductionOwnerRegistered";
                if (_cameraNetworkMaintenanceActive) return "CameraNetworkMaintenanceInProgress";
                if (_snapshot.Ready || _snapshot.ArmState != ProductionArmState.Disarmed)
                    return "CameraNetworkRequiresDisarmedStation";
                if (_snapshot.Busy || _snapshot.CurrentExecution is not null)
                    return "CameraNetworkInspectionConflict";
                if (_snapshot.Evidence.PendingDeliveries != 0 ||
                    _snapshot.Handshake is HandshakePhase.AwaitingResultAck or HandshakePhase.AwaitingAckReset)
                    return "CameraNetworkDeliveryConflict";
                if (_snapshot.Mode is not ExclusiveMode.None and not ExclusiveMode.Maintenance ||
                    _snapshot.Recovery == RecoveryState.InProgress ||
                    _snapshot.LastCommand?.State == OperationState.Pending || _snapshot.ActiveRecipe is not null)
                    return "CameraNetworkWorkflowConflict";
                _cameraNetworkMaintenanceActive = true;
                PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                    AdmissionBlockers = new AdmissionBlockers(_snapshot.AdmissionBlockers
                        .Append("CameraNetworkMaintenanceInProgress").Distinct(StringComparer.Ordinal)) });
                return null;
            }
        }
        finally { _commandGate.Release(); }
    }

    private void ReleaseCameraNetworkMaintenance()
    {
        lock (_sync)
        {
            _cameraNetworkMaintenanceActive = false;
            if (_disposed || _shutdownRequested) return;
            PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                AdmissionBlockers = new AdmissionBlockers(_snapshot.AdmissionBlockers
                    .Where(code => code != "CameraNetworkMaintenanceInProgress")) });
        }
    }

    private void PublishCameraNetworkMaintenance(CameraNetworkSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_disposed || _shutdownRequested) return;
            // Device identity and IP values stay in the authorized maintenance view/audit.
            var blockers = _snapshot.AdmissionBlockers.Where(code =>
                code is not "CameraNetworkReconciliationRequired" and not "CameraRecipeActivationRequired").ToList();
            blockers.Add("CameraRecipeActivationRequired");
            if (snapshot.State is CameraNetworkMaintenanceState.Pending or CameraNetworkMaintenanceState.Unknown)
                blockers.Add("CameraNetworkReconciliationRequired");
            PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                AdmissionBlockers = new AdmissionBlockers(blockers.Distinct(StringComparer.Ordinal)) });
        }
    }
}
