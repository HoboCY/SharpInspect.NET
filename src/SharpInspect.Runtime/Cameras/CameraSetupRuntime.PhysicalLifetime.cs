namespace SharpInspect.Runtime.Cameras;

internal sealed partial class CameraSetupRuntime
{
    /// <summary>
    /// Read-only physical-lifetime snapshot for the maintenance witness. <c>TrackedWork</c> holds
    /// every late-tracked operation/physical task that must actually exit; <c>RetainedDevices</c>
    /// is the failed-retirement ownership set (a bounded dispose never released those handles).
    /// No provider call, cancellation or state change happens here.
    /// </summary>
    internal PhysicalLifetimeSnapshot CapturePhysicalLifetime()
    {
        lock (_stateSync)
            return new(_inFlight.Concat(_physicalInFlight).ToArray(),
                _retainedDevices.Count, _retiringDevices.Count);
    }

    internal sealed record PhysicalLifetimeSnapshot(
        IReadOnlyList<Task> TrackedWork, int RetainedDevices, int RetiringDevices);
}
