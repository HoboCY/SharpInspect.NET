using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    internal Task WaitForStationQualificationStartupAsync() => _stationQualificationStartupTask ?? Task.CompletedTask;

    private async Task InitializeStationQualificationAsync()
    {
        StationQualificationOwner? owner = null;
        try
        {
            await _storeInitialization.ConfigureAwait(false);
            await WaitForRecipeActivationStartupAsync().ConfigureAwait(false);
            await WaitForPreviewStartupAsync().ConfigureAwait(false);
            await WaitForManualInspectionStartupAsync().ConfigureAwait(false);
            if (_audit is not SqliteCommandStore store) throw new InvalidOperationException("StationQualificationStoreUnavailable");
            lock (_sync)
                if (_activationStartupBlocked || _previewRecoveryBlocked || _manualRecoveryBlocked || !_storeReady || _auditFault)
                    throw new InvalidOperationException("StationQualificationStartupDependencyUnavailable");
            var recovered = await store.ReadStationQualificationRecoveryStateAsync(CancellationToken.None).ConfigureAwait(false);
            var cycleRecoveryRequired = false;
            if (_stationQualificationStoreOptions?.QualificationCycles is not null)
            {
                var cycles = await new SqliteQualificationCycleHistoryQuery(_stationQualificationStoreOptions)
                    .ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                if (!cycles.Available) throw new InvalidOperationException("QualificationCycleStartupHistoryUnavailable");
                cycleRecoveryRequired = cycles.RecoveryRequired;
            }
            if (!recovered.Available || (recovered.RecoveryRequired && !recovered.RecoverablePending))
                throw new InvalidOperationException("StationQualificationStartupRecoveryRequired");
            if (recovered.Header is null)
            {
                if (cycleRecoveryRequired) throw new InvalidOperationException("QualificationCycleRecoveryRequired");
                lock (_sync)
                {
                    _stationQualificationStartupPending = false;
                    ClearStationQualificationProjectionLocked();
                }
                return;
            }
            if (!recovered.RecoverablePending || recovered.StartFact is null || recovered.LastEvent is null || recovered.TargetBaseline is null ||
                recovered.TargetBaseline.ContentHash != recovered.Header.TargetBaseline.ContentHash)
                throw new InvalidOperationException("StationQualificationRecoveryEvidenceMissing");
            owner = new(recovered.Header, recovered.LastEvent, recovered.StartFact)
            {
                RestartRecovery = true, ExitRequested = true, Aborted = true,
                ExitReason = "StationQualificationInterruptedByRestart",
                ModbusRecoveryRequired = cycleRecoveryRequired || recovered.SessionEvents.Any(value =>
                    value.ReasonCode == "QualificationModbusTransportSelected")
            };
            owner.ExitFact = recovered.CommandFacts.LastOrDefault(fact => fact.CommandKind == AuditedCommandKind.ExitStationQualificationSession &&
                fact.Phase == CommandAuditPhase.Outcome && fact.Disposition == CommandDisposition.Accepted);
            if (owner.ExitFact is { } exitFact)
                owner.ExitAuthorizationTarget = recovered.SessionEvents.FirstOrDefault(value =>
                    value.CommandCorrelationId == exitFact.CorrelationId && value.AttemptId == exitFact.AttemptId)?.CommandAuthorizationTarget;
            lock (_sync)
            {
                _stationQualificationOwner = owner;
                PublishStationQualificationLocked(owner, StationQualificationSessionPhase.Restoring, owner.ExitReason);
            }
            var pendingRuns = recovered.RunRecords.GroupBy(run => run.RunId.Value)
                .Select(group => group.Last()).Where(run => !run.Terminal).ToArray();
            if (pendingRuns.Length > 1) throw new InvalidOperationException("StationQualificationPendingRunConflict");
            if (pendingRuns.SingleOrDefault() is { } run)
            {
                owner.CurrentRun = run;
                owner.CurrentRunId = run.RunId;
            }
            await RestoreStationQualificationAsync(owner).ConfigureAwait(false);
            lock (_sync)
            {
                _stationQualificationStartupPending = false;
                if (_stationQualificationOwner is null && !_stationQualificationRecoveryBlocked) ClearStationQualificationProjectionLocked();
                else PublishLocked(_snapshot);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_sync)
            {
                _stationQualificationStartupPending = false;
                _stationQualificationRecoveryBlocked = true;
                _stationQualificationSnapshot = new(_snapshot.RuntimeEpoch, ++_stationQualificationRevision,
                    owner?.Header.SessionId, StationQualificationSessionPhase.RecoveryBlocked,
                    "StationQualificationStartupRecoveryRequired", DateTimeOffset.UtcNow,
                    owner?.Header.ActorPrincipalId, owner?.Header.ActorSessionId, owner?.Header.Plan,
                    owner?.CurrentRunId, owner?.LastRunId, StationQualificationRestorationState.RecoveryBlocked,
                    true, true, owner?.StartFact.CorrelationId);
                PublishLocked(_snapshot);
            }
        }
    }
}
