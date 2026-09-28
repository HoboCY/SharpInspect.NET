using SharpInspect.Runtime.Deployment;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal StationDeploymentLock? DeploymentOwnership { get; private set; }

    private void AcquireStationOwnership()
    {
        if (_options.StationInstallation is not { } installation)
        {
            StationInstallationRegistry.RequireUnregisteredDatabase(_options.DatabasePath);
            return;
        }
        if (!string.Equals(_options.DatabasePath, installation.DatabasePath, StringComparison.OrdinalIgnoreCase) ||
            _options.AuditIntegrityPolicy?.StationId != installation.StationId)
            throw new InvalidOperationException("StationInstallationStoreBindingMismatch");
        DeploymentOwnership = StationDeploymentLock.Acquire(installation, StationDeploymentOwner.Runtime, _options.CommitTimeout);
        try { StationMaintenanceHandoffStore.RequireRuntimeEntry(DeploymentOwnership); }
        catch { DeploymentOwnership.Dispose(); DeploymentOwnership = null; throw; }
    }

    internal void RequireProductionOwnership()
    {
        if (DeploymentOwnership is not { IsHeld: true, Owner: StationDeploymentOwner.Runtime })
            throw new InvalidOperationException("StationDeploymentOwnershipRequired");
    }
}
