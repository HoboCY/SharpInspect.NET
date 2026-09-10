using SharpInspect.Abstractions;
using SharpInspect.Runtime.Qualification;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Cycles;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private RecipeActivationPhysicalPhaseClaim ClaimStationQualificationPhysicalPhase(StationQualificationOwner owner)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_stationQualificationOwner, owner) || owner.Aborted ||
                (owner.ExitRequested && owner.CurrentRunId is null) || owner.Cancellation.IsCancellationRequested ||
                _shutdownRequested || _disposed)
                return RecipeActivationPhysicalPhaseClaim.Unavailable("StationQualificationStopping");
            if (owner.PhysicalPhaseId != 0)
                return RecipeActivationPhysicalPhaseClaim.Unavailable("StationQualificationPhysicalOperationInProgress");
            var id = checked(++_stationQualificationPhysicalSequence);
            owner.PhysicalPhaseId = id;
            return RecipeActivationPhysicalPhaseClaim.Granted(id, () =>
            {
                lock (_sync)
                    if (ReferenceEquals(_stationQualificationOwner, owner) && owner.PhysicalPhaseId == id)
                        owner.PhysicalPhaseId = 0;
            });
        }
    }

    private void PublishStationQualificationLocked(StationQualificationOwner owner,
        StationQualificationSessionPhase phase, string reason)
    {
        if (!ReferenceEquals(_stationQualificationOwner, owner)) return;
        _stationQualificationSnapshot = new(_snapshot.RuntimeEpoch, checked(++_stationQualificationRevision), owner.Header.SessionId,
            phase, reason, DateTimeOffset.UtcNow, owner.Header.ActorPrincipalId, owner.Header.ActorSessionId,
            owner.Header.Plan, owner.CurrentRunId, owner.LastRunId, owner.Restoration,
            owner.ExitRequested, _stationQualificationRecoveryBlocked, owner.ExitFact?.CorrelationId ?? owner.StartFact.CorrelationId);
        PublishLocked(_snapshot with
        {
            Busy = owner.CurrentRunId is not null && !owner.CycleFaultTerminated &&
                owner.CycleCoordinator?.Phase is not (InspectionCyclePhase.AwaitAckHigh or InspectionCyclePhase.AwaitAckLow or InspectionCyclePhase.Completed),
            CurrentExecution = owner.CycleFaultTerminated ? null : owner.CurrentRunId?.Correlation,
            Handshake = owner.CycleCoordinator?.Phase switch
            {
                InspectionCyclePhase.AwaitAckHigh => HandshakePhase.AwaitingResultAck,
                InspectionCyclePhase.AwaitAckLow => HandshakePhase.AwaitingAckReset,
                _ => owner.ModbusRecoveryRequired && owner.CycleFaultTerminated ? HandshakePhase.Unknown : HandshakePhase.Idle
            },
            Ready = false, ArmState = ProductionArmState.Disarmed
        });
    }

    private async Task<T> ObserveStationQualificationOperationAsync<T>(StationQualificationOwner owner,
        Task<T> actualOperation, string operationName)
    {
        try { return await actualOperation.WaitAsync(_stationQualificationOptions!.OperationTimeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            lock (_sync)
            {
                _stationQualificationRecoveryBlocked = true;
                owner.Aborted = owner.ExitRequested = true;
                owner.ExitReason = operationName + "Timeout";
                owner.Restoration = StationQualificationRestorationState.RecoveryBlocked;
                PublishStationQualificationLocked(owner, StationQualificationSessionPhase.RecoveryBlocked, owner.ExitReason);
            }
            CancelStationQualification(owner, abort: true);
            // A timeout does not retire a facility callback. Retain ownership and
            // wait for the real operation before attempting target restoration.
            try { await actualOperation.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            throw new TimeoutException(operationName + "Timeout");
        }
    }

    private async Task<T> StartStationQualificationFacilityOperationAsync<T>(StationQualificationOwner owner,
        Func<Task<T>> operation, string operationName)
    {
        using var phase = ClaimStationQualificationPhysicalPhase(owner);
        if (!phase.Available) throw new OperationCanceledException("StationQualificationFacilityOperationRevoked");
        return await ObserveStationQualificationOperationAsync(owner, operation(), operationName).ConfigureAwait(false);
    }

    private async Task<QualificationFacilityObservation> ObserveStationQualificationFacilityAsync(
        StationQualificationOwner owner, bool isolated, bool targetController, CancellationToken token)
    {
        var observation = await ObserveStationQualificationOperationAsync(owner,
            owner.Facility!.ObserveAsync(token).AsTask(), "StationQualificationObservation").ConfigureAwait(false);
        var rejection = StationQualificationFacilityValidator.ValidateObservation(owner.Request, observation,
            owner.ObservationSequence, DateTimeOffset.UtcNow, _stationQualificationOptions!.ObservationFreshness,
            isolated, targetController);
        if (rejection is not null) throw new InvalidOperationException(rejection);
        owner.ObservationSequence = observation.Sequence;
        return observation;
    }
}
