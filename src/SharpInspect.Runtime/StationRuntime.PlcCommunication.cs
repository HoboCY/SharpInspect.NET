using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Qualification;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private long _plcCommunicationGeneration;

    private Task StartQualificationModbusRequest(StationQualificationOwner owner, Func<Task> start)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_stationQualificationOwner, owner) || owner.Aborted ||
                owner.ExitRequested && owner.CurrentRunId is null || _shutdownRequested || _disposed)
                throw new PlcRequestRevokedException();
            // Hold the same lock as Exit through the actual start of the send.
            // An already admitted graceful drain remains eligible to read Ack.
            return start();
        }
    }

    private PlcCommunicationOwner CreateQualificationCommunicationOwner(StationQualificationOwner owner,
        ModbusQualificationProfile profile) => new(profile, owner.Request.RuntimeEpoch,
        health =>
        {
            lock (_sync)
            {
                if (_disposed || !ReferenceEquals(_stationQualificationOwner, owner)) return;
                PublishLocked(_snapshot with
                {
                    PlcCommunication = health,
                    Plc = new(health.TransportReachable ? HealthState.Healthy : HealthState.Faulted,
                        health.ControllerHeartbeatFresh && health.RuntimeHeartbeatObserved ? HealthState.Healthy : HealthState.Faulted,
                        health.Synchronized ? HealthState.Healthy : HealthState.Degraded),
                    Ready = false,
                    ArmState = ProductionArmState.Disarmed
                });
            }
        },
        reason =>
        {
            lock (_sync) RequestStationQualificationExitLocked(owner, reason, abort: true);
        },
        async transition =>
        {
            PlcCommunicationWriteRequest request;
            lock (_sync)
            {
                request = new(owner.Request.RuntimeEpoch, profile.EndpointBindingHash, profile.ContentHash,
                    profile.CommunicationBinding!.Policy.ContentHash, transition.Generation, transition.Attempt,
                    Enum.Parse<PlcCommunicationEventKind>(transition.Kind), transition.ReasonCode,
                    transition.ControllerEpoch, DateTimeOffset.UtcNow, transition.ObservedAt,
                    transition.RecoveryCycleId, owner.Header.SessionId, owner.CurrentRunId?.Value,
                    owner.CurrentRun?.CycleSequence);
            }
            try
            {
                var committed = await ((SqliteCommandStore)_audit!).AppendPlcCommunicationEventAsync(request,
                    new StoreDeadline(_audit!.CommitTimeout), CancellationToken.None).ConfigureAwait(false);
                if (!committed.Committed) throw new InvalidOperationException(committed.ReasonCode);
            }
            catch
            {
                MarkAuditFault("PlcCommunicationAuditUnavailable", alarmAuthorityUnavailable: true);
                throw;
            }
        }, () => Interlocked.Increment(ref _plcCommunicationGeneration));
}
