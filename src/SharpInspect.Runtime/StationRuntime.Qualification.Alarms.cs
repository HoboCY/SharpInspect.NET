using SharpInspect.Abstractions;
using SharpInspect.Runtime.Alarms;
using SharpInspect.Runtime.Qualification;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private static readonly string[] QualificationCycleCodes =
    {
        QualificationCycleAlarmCodes.TracePersistenceFailed,
        QualificationCycleAlarmCodes.ResultAckTimeout,
        QualificationCycleAlarmCodes.TriggerRejected,
        QualificationCycleAlarmCodes.Interrupted
    };

    private bool IsQualificationCycleAlarmMappingValid() => ConfiguredAlarmPolicy is { } policy &&
        QualificationCycleCodes.All(code => policy.TryGetRule(code, out var rule) && rule is not null &&
            rule.Source == QualificationCycleAlarmCodes.Source && rule.IsLatched &&
            rule.ProductionImpact == ProductionImpact.BlockNewTriggers &&
            (rule.ResetPrerequisites & (AlarmResetPrerequisites.RecoveryComplete |
                AlarmResetPrerequisites.NoActiveExecution | AlarmResetPrerequisites.NoPendingDelivery)) ==
            (AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution |
                AlarmResetPrerequisites.NoPendingDelivery));

    private async Task LatchQualificationCycleAlarmAsync(string code)
    {
        AlarmObservation observation;
        lock (_sync)
        {
            if (!QualificationCycleCodes.Contains(code, StringComparer.Ordinal) || !IsQualificationCycleAlarmMappingValid())
            { MarkAuditFault("QualificationCycleAlarmMappingUnavailable", alarmAuthorityUnavailable: true); return; }
            _registeredAlarmSources.Add(QualificationCycleAlarmCodes.Source);
            observation = new(_snapshot.RuntimeEpoch, NextAlarmObservationSequenceLocked(code), code,
                QualificationCycleAlarmCodes.Source, false, DateTimeOffset.UtcNow);
        }
        using var timeout = new CancellationTokenSource(_audit!.CommitTimeout);
        var entered = false;
        try
        {
            await _commandGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            var result = await ObserveAlarmCoreAsync(observation, timeout.Token,
                qualificationCycleDuringShutdown: true).ConfigureAwait(false);
            if (!result.Accepted) MarkAuditFault("QualificationCycleAlarmUnavailable", alarmAuthorityUnavailable: true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { MarkAuditFault("QualificationCycleAlarmUnavailable", alarmAuthorityUnavailable: true); }
        finally { if (entered) _commandGate.Release(); }
    }
}
