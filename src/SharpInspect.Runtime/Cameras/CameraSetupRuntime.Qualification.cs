using SharpInspect.Abstractions;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Recipes;

namespace SharpInspect.Runtime.Cameras;

internal sealed partial class CameraSetupRuntime
{
    internal ValueTask<RecipeActivationCameraRestoreResult> RecoverQualificationAfterRestartAsync(
        RecipeActivationRecord target, CancellationToken cancellationToken = default)
    {
        if (target.SuccessfulSnapshot is not { CameraSetup.Binding: not null } baseline ||
            baseline.Release.Source.Content.CameraRole != baseline.CameraSetup.Binding.LogicalRole)
            return ValueTask.FromResult(new RecipeActivationCameraRestoreResult(false,
                "StationQualificationRecoveryBaselineMissing", null, false));
        return RecoverNonProductionCameraAfterRestartAsync(baseline.CameraSetup.Binding,
            baseline.Release.Source.Content, baseline, "StationQualification", cancellationToken);
    }

    internal async ValueTask<RecipeActivationCameraLease> ReserveQualificationAcquisitionAsync(
        string logicalRole, CancellationToken cancellationToken = default)
    {
        var lease = await ReserveRecipeActivationAsync(logicalRole, cancellationToken).ConfigureAwait(false);
        if (lease.Available) lease.ClaimQualificationOwnership();
        return lease;
    }
}

internal sealed partial class RecipeActivationCameraLease
{
    private bool _qualificationOwned;

    internal void ClaimQualificationOwnership()
    {
        lock (this)
        {
            if (_disposed || _committed || _previewOwned || _manualOwned || _productionOwned)
                throw new InvalidOperationException("CameraQualificationOwnerConflict");
            _qualificationOwned = true;
        }
    }

    internal ValueTask<ManualCameraAcquisitionResult> AcquireQualificationFrameAsync(
        ExecutionCorrelationId correlation, FrameBufferPool framePool,
        IFrameAcquisitionClock clock, CancellationToken cancellationToken = default,
        Func<RecipeActivationPhysicalPhaseClaim>? physicalPhaseFactory = null) =>
        AcquireOwnedFrameAsync(ExecutionKind.Qualification, correlation,
            framePool, clock, cancellationToken, physicalPhaseFactory);
}
