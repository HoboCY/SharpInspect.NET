using SharpInspect.Abstractions;
using SharpInspect.Runtime.Alarms;
using SharpInspect.Runtime.Algorithms;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    internal const string AlgorithmHungAlarmCode = "AlgorithmHung";
    internal const string AlgorithmHungAlarmSource = "Runtime.AlgorithmExecution";
    private const string AlgorithmHungBlocker = "AlgorithmHung";
    private const string AlgorithmHungMappingBlocker = "AlgorithmExecutionAlarmMappingUnavailable";
    private Task? _algorithmHungObservation;
    private Guid? _algorithmHungObservationCorrelation;

    // A runtime that does not host algorithm execution must keep its historical
    // alarm-policy contract. It still observes the process guard and blocks when
    // that guard is hung, but only an explicitly registered execution policy makes
    // the AlgorithmHung alarm mapping mandatory.
    private bool RequiresAlgorithmHungAlarmMapping => _algorithmExecutionRegistered;

    private bool IsAlgorithmHungAlarmMappingValid(AlarmPolicy? policy)
    {
        if (!RequiresAlgorithmHungAlarmMapping) return true;
        return policy is not null && policy.TryGetRule(AlgorithmHungAlarmCode, out var rule) &&
            rule is not null && rule.Source == AlgorithmHungAlarmSource && rule.IsLatched &&
            rule.ProductionImpact is ProductionImpact.BlockNewTriggers or ProductionImpact.FaultAbort;
    }

    private void ReconcileAlgorithmExecutionLocked()
    {
        if (_disposed || _shutdownRequested) return;

        var mappingValid = IsAlgorithmHungAlarmMappingValid(ConfiguredAlarmPolicy);
        var needsHungBlocker = _executionGuard.IsHung && !_snapshot.AdmissionBlockers.Contains(AlgorithmHungBlocker);
        var needsMappingBlocker = !mappingValid && !_snapshot.AdmissionBlockers.Contains(AlgorithmHungMappingBlocker);
        if (needsHungBlocker || needsMappingBlocker)
            PublishLocked(_snapshot);

        ScheduleAlgorithmHungObservationLocked();
    }

    private StationStateSnapshot ApplyAlgorithmExecutionStateLocked(StationStateSnapshot next)
    {
        var policy = ConfiguredAlarmPolicy;
        var mappingValid = IsAlgorithmHungAlarmMappingValid(policy);
        var hung = _executionGuard.IsHung;
        if (!hung && mappingValid) return next;

        var blockers = next.AdmissionBlockers
            .Where(code => code is not AlgorithmHungBlocker and not AlgorithmHungMappingBlocker)
            .ToList();
        if (!mappingValid)
        {
            blockers.Add(AlgorithmHungMappingBlocker);
            if (!blockers.Contains("AlarmAuthorityUnavailable", StringComparer.Ordinal))
                blockers.Add("AlarmAuthorityUnavailable");
        }
        if (hung) blockers.Add(AlgorithmHungBlocker);

        var alarmState = next.AlarmState;
        if (!mappingValid)
        {
            alarmState = new AlarmStateSnapshot(false, AlgorithmHungMappingBlocker,
                next.RuntimeEpoch, next.Revision, policy,
                alarmState?.Instances ?? Array.Empty<AlarmInstanceSnapshot>(),
                alarmState?.Plc ?? new AlarmPlcProjection(Array.Empty<AlarmPlcEntry>(), 0, 0, false));
        }

        return next with
        {
            Ready = false,
            ArmState = ProductionArmState.Disarmed,
            AlarmState = alarmState,
            AdmissionBlockers = new AdmissionBlockers(blockers)
        };
    }

    private void ScheduleAlgorithmHungObservationLocked()
    {
        if (!_executionGuard.IsHung || _algorithmHungObservationCorrelation.HasValue ||
            _disposed || _shutdownRequested || !_storeInitialization.IsCompletedSuccessfully ||
            ConfiguredAlarmPolicy is not { } policy || !IsAlgorithmHungAlarmMappingValid(policy) ||
            !_registeredAlarmSources.Contains(AlgorithmHungAlarmSource))
            return;

        var snapshot = _executionGuard.Snapshot;
        if (snapshot is null) return;
        _algorithmHungObservationCorrelation = snapshot.Correlation.Value;
        _algorithmHungObservation = Task.Run(() => ObserveAlgorithmHungAsync(snapshot));
    }

    private async Task ObserveAlgorithmHungAsync(AlgorithmHungSnapshot hung)
    {
        try
        {
            Guid epoch;
            long sequence;
            lock (_sync)
            {
                if (_disposed || _shutdownRequested) return;
                epoch = _snapshot.RuntimeEpoch;
                sequence = NextAlarmObservationSequenceLocked(AlgorithmHungAlarmCode);
            }

            var observation = new AlarmObservation(epoch, sequence, AlgorithmHungAlarmCode,
                AlgorithmHungAlarmSource, false, hung.ObservedAtUtc);
            var outcome = await ObserveAlarmAsync(observation, _lifetime.Token).ConfigureAwait(false);
            if (!outcome.Accepted)
                MarkAuditFault("AlgorithmHungAlarmUnavailable", alarmAuthorityUnavailable: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MarkAuditFault("AlgorithmHungAlarmUnavailable", alarmAuthorityUnavailable: true);
        }
    }
}
