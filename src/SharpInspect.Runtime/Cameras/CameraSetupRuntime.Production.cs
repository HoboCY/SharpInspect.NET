using SharpInspect.Abstractions;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Recipes;

namespace SharpInspect.Runtime.Cameras;

internal sealed partial class CameraSetupRuntime
{
    internal string? ProductionProviderBinaryHash(CameraProviderIdentity identity)
    {
        var entry = FindProvider(identity);
        return entry is null ? null : Admission.ProductionConfigurationBuilder.AssemblyHash(entry.Provider.GetType().Assembly);
    }

    internal async ValueTask<RecipeActivationCameraLease> ReserveProductionAcquisitionAsync(
        CameraSetupSnapshot baseline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var lease = await ReserveRecipeActivationAsync(baseline.LogicalRole, cancellationToken).ConfigureAwait(false);
        try
        {
            if (lease.Available) lease.ClaimProductionOwnership(baseline);
            return lease;
        }
        catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
    }

    // The operation gate is already reserved. Borrow the exact activated device;
    // a production cycle never closes/reopens or reapplies the camera configuration.
    internal IControlledCameraDevice BorrowProductionDevice(CameraSetupSnapshot baseline)
    {
        lock (_stateSync)
        {
            if (_disposed || !_slots.TryGetValue(baseline.LogicalRole, out var slot) ||
                !IsActivationBaselineExact(baseline, slot.Snapshot) ||
                slot.Snapshot is not { Health.Configuration: CameraConfigurationState.Applied,
                    Health.Connection: CameraConnectionState.Open, Effective: not null } ||
                slot.Device is not IControlledCameraDevice controlled ||
                _retiringDevices.Contains(controlled) || _retainedDevices.Contains(controlled))
                throw new InvalidOperationException("CameraProductionBaselineUnavailable");
            return controlled;
        }
    }
}

internal sealed partial class RecipeActivationCameraLease
{
    private bool _productionOwned;

    internal void ClaimProductionOwnership(CameraSetupSnapshot baseline)
    {
        lock (this)
        {
            if (_disposed || _committed || _previewOwned || _manualOwned || _qualificationOwned)
                throw new InvalidOperationException("CameraProductionOwnerConflict");
            var device = _owner.BorrowProductionDevice(baseline);
            _productionOwned = true;
            _candidateDevice = device;
            _candidateSnapshot = baseline;
            _candidatePrepared = true;
        }
    }

    internal ValueTask<ManualCameraAcquisitionResult> AcquireProductionFrameAsync(
        ExecutionCorrelationId correlation, FrameBufferPool framePool, IFrameAcquisitionClock clock,
        CancellationToken cancellationToken = default,
        Func<RecipeActivationPhysicalPhaseClaim>? physicalPhaseFactory = null) =>
        AcquireOwnedFrameAsync(ExecutionKind.Production, correlation, framePool, clock,
            cancellationToken, physicalPhaseFactory);
}
