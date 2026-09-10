using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Manual;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    internal Task WaitForManualInspectionStartupAsync() => _manualStartupTask ?? Task.CompletedTask;

    private async Task InitializeManualInspectionAsync()
    {
        ManualInspectionOwner? owner = null;
        try
        {
            await _storeInitialization.ConfigureAwait(false);
            await WaitForRecipeActivationStartupAsync().ConfigureAwait(false);
            await WaitForPreviewStartupAsync().ConfigureAwait(false);
            if (_audit is not SqliteCommandStore store) throw new InvalidOperationException("ManualInspectionStoreUnavailable");
            lock (_sync)
                if (_activationStartupBlocked || _previewRecoveryBlocked || _auditFault || !_storeReady)
                    throw new InvalidOperationException("ManualInspectionStartupDependencyUnavailable");
            var recovery = await store.ReadManualInspectionRecoveryStateAsync(CancellationToken.None).ConfigureAwait(false);
            if (!recovery.Available || recovery.RecoveryRequired || recovery.Header?.RecoveryRequired == true)
                throw new InvalidOperationException("ManualInspectionRecoveryRequired");
            if (recovery.Header is null)
            {
                lock (_sync) CompleteManualInspectionStartupLocked();
                return;
            }
            if (recovery.StartFact is null || recovery.Draft is null)
                throw new InvalidOperationException("ManualInspectionRecoveryEvidenceMissing");
            var header = recovery.Header;
            owner = new(new ManualRecipeExecutionPlan(header.Selection, recovery.Draft, recovery.Release),
                recovery.ActiveBaseline, header, recovery.StartFact)
            { ExitRequested = true, Aborted = true, ExitReason = "ManualInspectionInterruptedByRestart" };
            lock (_sync)
            {
                _manualOwner = owner;
                PublishManualInspectionLocked(owner, ManualInspectionSessionPhase.Restoring, "ManualInspectionStartupRestoring");
            }
            foreach (var run in recovery.RunRecords.GroupBy(value => value.RunId).Select(group => group.Last()).Where(value => !value.Terminal))
            {
                var fact = recovery.CommandFacts.SingleOrDefault(value => value.AttemptId == run.AttemptId &&
                    value.CommandKind == AuditedCommandKind.RunManualInspection && value.Phase == CommandAuditPhase.Outcome &&
                    value.Disposition == CommandDisposition.Accepted) ?? throw new InvalidOperationException("ManualInspectionRunAuditMissing");
                var admitted = recovery.SessionEvents.FirstOrDefault(value => value.AttemptId == fact.AttemptId && !value.Terminal)
                    ?? throw new InvalidOperationException("ManualInspectionRunAuthorizationMissing");
                owner.CurrentRun = run;
                owner.CurrentRunId = run.RunId;
                owner.PendingCommand = ManualInspectionRecoveryContinuation(header, admitted, fact);
                owner.PendingFact = fact;
                if (!await RecordManualInspectionRunTerminalAsync(owner, null, ExecutionStatus.Cancelled,
                    "ManualInspectionInterruptedByRestart", run.FrameMetadata, run.FrameProvenance,
                    run.StartedAtUtc, 0).ConfigureAwait(false))
                    throw new InvalidOperationException("ManualInspectionInterruptedRunTerminalUnavailable");
            }
            foreach (var fact in recovery.CommandFacts.Where(value => value.CommandKind ==
                AuditedCommandKind.ExitManualInspectionSession && value.Phase == CommandAuditPhase.Outcome &&
                value.Disposition == CommandDisposition.Accepted))
            {
                var admitted = recovery.SessionEvents.FirstOrDefault(value => value.AttemptId == fact.AttemptId && !value.Terminal)
                    ?? throw new InvalidOperationException("ManualInspectionExitAuthorizationMissing");
                if (!await _commandGate.WaitAsync(_audit.CommitTimeout).ConfigureAwait(false))
                    throw new InvalidOperationException("ManualInspectionRecoveryRuntimeBusy");
                try
                {
                    var completed = await _authorization!.CompleteManualInspectionCommandAsync(
                        ManualInspectionRecoveryContinuation(header, admitted, fact), owner.Header, fact,
                        new(false, "ManualInspectionInterruptedByRestart", ManualInspectionSessionPhase.Restoring,
                            ManualInspectionRestorationState.Pending, CompleteCommand: true), new StoreDeadline(_audit.CommitTimeout))
                        .ConfigureAwait(false);
                    if (completed.Outcome.Audit != AuditPersistence.Persisted || completed.Header is null)
                        throw new InvalidOperationException("ManualInspectionInterruptedExitTerminalUnavailable");
                    owner.Header = completed.Header;
                }
                finally { _commandGate.Release(); }
            }
            var restored = await _cameraSetupRuntime.RecoverManualAfterRestartAsync(header, recovery.Draft,
                recovery.ActiveBaseline, CancellationToken.None).ConfigureAwait(false);
            var restoration = !restored.Succeeded ? ManualInspectionRestorationState.RecoveryBlocked :
                recovery.ActiveBaseline is null ? ManualInspectionRestorationState.NoActiveBaselineClosed :
                    ManualInspectionRestorationState.Restored;
            await FinishManualInspectionAsync(owner, false, restored.Succeeded ?
                "ManualInspectionInterruptedSessionRestored" : restored.ReasonCode, restoration).ConfigureAwait(false);
            lock (_sync) CompleteManualInspectionStartupLocked();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_sync)
            {
                _manualStartupPending = false;
                _manualRecoveryBlocked = true;
                _manualSnapshot = new(_snapshot.RuntimeEpoch, checked(++_manualRevision), owner?.SessionId,
                    ManualInspectionSessionPhase.RecoveryBlocked, "ManualInspectionStartupRecoveryRequired", DateTimeOffset.UtcNow,
                    owner?.ActorPrincipalId, owner?.ActorSessionId, owner?.Plan.Selection, owner?.CurrentRunId,
                    owner?.LastRunId, ManualInspectionRestorationState.RecoveryBlocked, true, true, owner?.StartFact.CorrelationId);
                PublishLocked(_snapshot);
            }
            try { await LatchManualInspectionRecoveryAlarmAsync().ConfigureAwait(false); }
            finally { owner?.Retired.TrySetResult(true); }
        }
    }

    private void CompleteManualInspectionStartupLocked()
    {
        _manualStartupPending = false;
        PublishLocked(_snapshot with
        {
            Mode = _manualRecoveryBlocked || _manualOwner is not null ? ExclusiveMode.ManualInspection : ExclusiveMode.None,
            Ready = false, ArmState = ProductionArmState.Disarmed,
            AdmissionBlockers = new(_snapshot.AdmissionBlockers.Where(code => code != "ManualInspectionStartupRecoveryPending"))
        });
    }

    private static ManualInspectionContinuationCommand ManualInspectionRecoveryContinuation(
        ManualInspectionSessionHeader header, ManualInspectionSessionEvent admitted, CommandAuditFact fact) =>
        new(fact.CorrelationId, new(fact.Source ?? CommandSource.PhysicalConsole, fact.ClaimedPrincipalId,
            fact.ClaimedSessionId, fact.ClaimedStepUpGrantId), header.SessionId, fact.CommandKind,
            admitted.AuthorizationTarget, header.ChangeReason);
}
