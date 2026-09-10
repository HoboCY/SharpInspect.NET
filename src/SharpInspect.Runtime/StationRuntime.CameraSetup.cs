using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    /// <summary>
    /// Explicitly registered provider identities.  Reading this list has no
    /// discovery or device side effect.
    /// </summary>
    public IReadOnlyList<CameraProviderIdentity> Providers => _cameraSetupRuntime.Providers;

    public ValueTask<CameraDiscoveryResult> DiscoverAsync(CameraProviderIdentity provider,
        CommandInvocation invocation, CancellationToken cancellationToken = default) =>
        _cameraSetupRuntime.DiscoverAsync(provider, invocation, cancellationToken);

    public ValueTask<CameraSetupQueryResult> GetSetupAsync(string logicalRole,
        CommandInvocation invocation, CancellationToken cancellationToken = default) =>
        _cameraSetupRuntime.GetSetupAsync(logicalRole, invocation, cancellationToken);

    public ValueTask<CameraSetupOperationResult> RebindAsync(CameraRebindRequest request,
        CancellationToken cancellationToken = default) =>
        _cameraRecoveryService is not null
            ? ValueTask.FromResult(new CameraSetupOperationResult(false,
                "CameraRecoveryOwnsDevice", AuditPersistence.NotAttempted))
            : _cameraSetupRuntime.RebindAsync(request, cancellationToken);

    public ValueTask<CameraSetupOperationResult> ApplyDebugConfigurationAsync(
        CameraDebugConfigurationRequest request, CancellationToken cancellationToken = default) =>
        _cameraRecoveryService is not null
            ? ValueTask.FromResult(new CameraSetupOperationResult(false,
                "CameraRecoveryOwnsDevice", AuditPersistence.NotAttempted))
            : _cameraSetupRuntime.ApplyDebugConfigurationAsync(request, cancellationToken);

    private CameraSetupRuntime.CameraStationContext ReadCameraStationContext()
    {
        lock (_sync)
        {
            return new CameraSetupRuntime.CameraStationContext(_snapshot.RuntimeEpoch,
                _snapshot.Ready, _snapshot.ArmState, _snapshot.Busy, _snapshot.CurrentExecution,
                _snapshot.Evidence.PendingDeliveries, _snapshot.Handshake, _snapshot.Mode,
                _snapshot.Recovery, RecipeActivationConfigurationBlockedLocked || PreviewConfigurationBlockedLocked ||
                    _importPhysicalReservation is not null ||
                    _snapshot.LastCommand?.State == OperationState.Pending,
                _snapshot.ActiveRecipe, _shutdownRequested, _disposed);
        }
    }

    private void PublishCameraSetupLocked(string role, CameraSetupSnapshot setup)
    {
        lock (_sync)
        {
            if (_disposed || _shutdownRequested) return;
            var state = new CameraSetupState(role, setup.Binding?.Revision ?? 0,
                setup.Health.ProviderAvailability, setup.Health.Connection,
                setup.Health.Configuration, setup.Health.Acquisition, HealthState.Unknown,
                setup.Health.LastFault?.Classification, setup.Health.LastFault?.ReasonCode,
                setup.RequiresRecipeActivation);
            var blockers = _snapshot.AdmissionBlockers
                .Where(code => code is not "CameraBindingMissing" and not "CameraRecipeActivationRequired" and
                    not "CameraSetupUnavailable" and not "CameraConfigurationUnknown")
                .ToList();
            if (setup.Binding is null) blockers.Add("CameraBindingMissing");
            else blockers.Add("CameraRecipeActivationRequired");
            if (setup.Health.Configuration == CameraConfigurationState.Unknown)
                blockers.Add("CameraConfigurationUnknown");
            if (setup.Health.ProviderAvailability != CameraProviderAvailability.Available ||
                setup.Health.Connection != CameraConnectionState.Open)
                blockers.Add("CameraSetupUnavailable");
            var camera = new CameraHealth(
                setup.Health.Connection == CameraConnectionState.Open ? HealthState.Healthy :
                    setup.Health.Connection == CameraConnectionState.Closed ? HealthState.Unconfigured : HealthState.Unknown,
                setup.Health.Configuration == CameraConfigurationState.Applied ? HealthState.Healthy :
                    setup.Health.Configuration == CameraConfigurationState.Unconfigured ? HealthState.Unconfigured : HealthState.Unknown,
                setup.Health.Acquisition == CameraAcquisitionState.Stopped ? HealthState.Healthy : HealthState.Unknown,
                _snapshot.Camera.Buffers);
            PublishLocked(_snapshot with { CameraSetup = state, Camera = camera,
                Ready = false, ArmState = ProductionArmState.Disarmed,
                AdmissionBlockers = new AdmissionBlockers(blockers.Distinct(StringComparer.Ordinal)) });
        }
    }

    private void CancelCameraSetupOperations() => _cameraSetupRuntime.CancelOperations();
}
