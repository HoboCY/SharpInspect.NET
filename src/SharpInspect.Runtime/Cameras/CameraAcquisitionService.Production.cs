using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Cameras;

public sealed partial class CameraAcquisitionService
{
    private bool _productionOwnedEnabled;

    internal void EnableProductionOwnedAcquisition()
    {
        lock (_sync)
        {
            if (_disposed || _attempt is not null || _admissionInProgress || _calibrationEnabled ||
                _manualEnabled || _qualificationSessionEnabled || _productionOwnedEnabled)
                throw new InvalidOperationException("CameraProductionAcquisitionUnavailable");
            _productionOwnedEnabled = true;
        }
    }

    // Only the Runtime owner can submit its already durably accepted InspectionId.
    // The public acquisition API continues to reject caller-created production runs.
    internal ValueTask<CameraAcquisitionAttempt> AcquireProductionOwnedAsync(
        ExecutionCorrelationId correlation, string logicalCameraRole,
        CancellationToken cancellationToken = default)
    {
        if (correlation is null || correlation.Kind != ExecutionKind.Production || correlation.Value == Guid.Empty)
            return ValueTask.FromResult(Rejected("CameraProductionCorrelationInvalid"));
        return new(AcquireCoreAsync(ExecutionKind.Production, logicalCameraRole, cancellationToken, correlation));
    }
}
