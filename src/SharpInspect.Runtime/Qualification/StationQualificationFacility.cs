using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Qualification;

/// <summary>
/// Explicitly registered trusted-host composition boundary.  Runtime receives a
/// lease from this interface only after it has admitted the frozen plan.  No
/// public method accepts a run identity, production result, or qualification flag.
/// </summary>
public interface IStationQualificationFacility
{
    QualificationHarnessIdentity Identity { get; }

    ValueTask<IStationQualificationFacilityLease> OpenAsync(
        QualificationFacilityRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// A privately-held facility lease.  The Runtime owns its lifetime and validates
/// every observation against the exact request before using it.
/// Disposing a lease retires its callbacks and test stimulus authority. It must
/// preserve disabled production outputs and any established physical isolation;
/// only an explicit ReleaseIsolationAsync may change those routes. A fresh
/// recovery lease must observe and restore the existing physical configuration.
/// </summary>
public interface IStationQualificationFacilityLease : IAsyncDisposable
{
    CancellationToken FacilityLost { get; }

    ValueTask<QualificationFacilityObservation> ObserveAsync(
        CancellationToken cancellationToken = default);

    ValueTask<QualificationFacilityOperationResult> ApplyIsolationAsync(
        CancellationToken cancellationToken = default);

    ValueTask<QualificationFacilityStimulus> WaitForStimulusAsync(
        CancellationToken cancellationToken = default);

    ValueTask<QualificationFacilityOperationResult> WriteQualificationResultAsync(
        StationQualificationPayload payload, CancellationToken cancellationToken = default);

    ValueTask<QualificationFacilityOperationResult> RestoreControllerAsync(
        QualificationControllerConfiguration targetConfiguration,
        CancellationToken cancellationToken = default);

    ValueTask<QualificationFacilityOperationResult> ReleaseIsolationAsync(
        CancellationToken cancellationToken = default);
}
