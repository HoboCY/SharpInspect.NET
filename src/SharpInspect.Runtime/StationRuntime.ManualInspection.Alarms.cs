using SharpInspect.Abstractions;
using SharpInspect.Runtime.Alarms;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private const string ManualInspectionAlarmCode = "ManualInspectionRecoveryRequired";
    private const string ManualInspectionAlarmSource = "Runtime.ManualInspection";

    private bool IsManualInspectionAlarmMappingValid() => ConfiguredAlarmPolicy is { } policy &&
        policy.TryGetRule(ManualInspectionAlarmCode, out var rule) && rule is not null &&
        rule.Source == ManualInspectionAlarmSource && rule.IsLatched &&
        rule.ProductionImpact == ProductionImpact.BlockNewTriggers &&
        (rule.ResetPrerequisites & (AlarmResetPrerequisites.RecoveryComplete |
            AlarmResetPrerequisites.NoActiveExecution)) == (AlarmResetPrerequisites.RecoveryComplete |
            AlarmResetPrerequisites.NoActiveExecution);

    private async Task LatchManualInspectionRecoveryAlarmAsync()
    {
        AlarmObservation observation;
        lock (_sync)
        {
            _manualRecoveryBlocked = true;
            if (!IsManualInspectionAlarmMappingValid())
            { MarkAuditFault("ManualInspectionAlarmMappingUnavailable", alarmAuthorityUnavailable: true); return; }
            _registeredAlarmSources.Add(ManualInspectionAlarmSource);
            observation = new(_snapshot.RuntimeEpoch, NextAlarmObservationSequenceLocked(ManualInspectionAlarmCode),
                ManualInspectionAlarmCode, ManualInspectionAlarmSource, false, DateTimeOffset.UtcNow);
            PublishLocked(_snapshot);
        }
        using var timeout = new CancellationTokenSource(_audit!.CommitTimeout);
        var entered = false;
        try
        {
            await _commandGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            var result = await ObserveAlarmCoreAsync(observation, timeout.Token,
                manualRestorationDuringShutdown: true).ConfigureAwait(false);
            if (!result.Accepted) MarkAuditFault("ManualInspectionRecoveryAlarmUnavailable", alarmAuthorityUnavailable: true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { MarkAuditFault("ManualInspectionRecoveryAlarmUnavailable", alarmAuthorityUnavailable: true); }
        finally { if (entered) _commandGate.Release(); }
    }
}
