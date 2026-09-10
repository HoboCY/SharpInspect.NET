using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Cameras;

public sealed partial class CameraAcquisitionService
{
    // Only the session coordinator can bind an already durably issued run identity.
    // The existing public development probe cannot submit into this owner.
    internal void EnableQualificationSessionAcquisition()
    {
        lock (_sync)
        {
            if (_disposed || _attempt is not null || _admissionInProgress ||
                _calibrationEnabled || _manualEnabled || _qualificationSessionEnabled)
                throw new InvalidOperationException("CameraQualificationSessionAcquisitionUnavailable");
            _qualificationSessionEnabled = true;
        }
    }

    internal ValueTask<CameraAcquisitionAttempt> AcquireQualificationSessionAsync(
        ExecutionCorrelationId correlation, string logicalCameraRole,
        CancellationToken cancellationToken = default)
    {
        if (correlation is null || correlation.Kind != ExecutionKind.Qualification ||
            correlation.Value == Guid.Empty)
            return ValueTask.FromResult(Rejected("CameraQualificationCorrelationInvalid"));
        return new(AcquireCoreAsync(ExecutionKind.Qualification, logicalCameraRole,
            cancellationToken, correlation));
    }
}
