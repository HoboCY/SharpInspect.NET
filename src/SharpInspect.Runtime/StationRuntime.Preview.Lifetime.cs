using SharpInspect.Abstractions;
using SharpInspect.Runtime.Alarms;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private const string PreviewAlarmCode = "PreviewRecoveryRequired";
    private const string PreviewAlarmSource = "Runtime.Preview";

    private bool IsPreviewAlarmMappingValid() => ConfiguredAlarmPolicy is { } policy &&
        policy.TryGetRule(PreviewAlarmCode, out var rule) && rule is not null &&
        rule.Source == PreviewAlarmSource && rule.IsLatched &&
        rule.ProductionImpact == ProductionImpact.BlockNewTriggers &&
        (rule.ResetPrerequisites & (AlarmResetPrerequisites.RecoveryComplete |
            AlarmResetPrerequisites.NoActiveExecution)) == (AlarmResetPrerequisites.RecoveryComplete |
            AlarmResetPrerequisites.NoActiveExecution);

    private void SchedulePreviewSessionExitLocked(InteractiveSession session)
    {
        if (_previewOwner is not { } owner) return;
        if (session.State != InteractiveSessionState.Authenticated ||
            session.SessionId != owner.Header.ActorSessionId ||
            session.PrincipalId != owner.Header.ActorPrincipalId.ToString("D"))
            RequestPreviewExitLocked(owner, "PreviewInteractiveAuthorityEnded");
    }

    private void RequestPreviewStop(string reason)
    {
        lock (_sync)
        {
            if (_previewAdmissionPending) _previewAdmissionStopRequested = true;
            if (_previewOwner is { } owner) RequestPreviewExitLocked(owner, reason);
        }
    }

    private void RequestPreviewExit(PreviewSessionOwner owner, string reason)
    { lock (_sync) RequestPreviewExitLocked(owner, reason); }

    private void RequestPreviewExitLocked(PreviewSessionOwner owner, string reason)
    {
        if (!ReferenceEquals(_previewOwner, owner) || owner.Retired.Task.IsCompleted) return;
        if (!owner.ExitRequested)
        {
            owner.ExitRequested = true;
            owner.ExitReason = reason;
            PublishPreviewLocked(owner, PreviewSessionPhase.Restoring, reason);
            // Adapter callbacks may run synchronously from Cancel. Never invoke them under _sync.
            var readerCancellation = owner.ReadCancellation;
            _ = Task.Run(() =>
            {
                try { owner.ExitCancellation.Cancel(); } catch (ObjectDisposedException) { }
                try { readerCancellation?.Cancel(); } catch (ObjectDisposedException) { }
            });
        }
        if (owner.ExitWorker is not null) return;
        var preceding = owner.Operation;
        owner.ExitWorker = Task.Run(async () =>
        {
            try
            {
                if (preceding is not null) await preceding.ConfigureAwait(false);
                await RestorePreviewSessionAsync(owner).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                await FinishPreviewSessionAsync(owner, false, "PreviewExitUnavailable",
                    PreviewRestorationState.RecoveryBlocked).ConfigureAwait(false);
            }
        });
    }

    private async Task<bool> CheckPreviewAuthorityAsync(PreviewSessionOwner owner)
    {
        var access = await _authorization!.GetPreviewOwnerAccessAsync(owner.Header,
            owner.ExitCancellation.Token).ConfigureAwait(false);
        if (access.CanRun) return true;
        RequestPreviewExit(owner, access.ReasonCode);
        return false;
    }

    private async Task<bool> RecordPreviewProgressAsync(PreviewSessionOwner owner,
        PreviewSessionPhase phase, string reason, bool completeCommand = false)
    {
        if (!await _commandGate.WaitAsync(_audit!.CommitTimeout).ConfigureAwait(false))
        { RequestPreviewExit(owner, "PreviewProgressRuntimeBusy"); return false; }
        try
        {
        PreviewSessionCommand? command;
        CommandAuditFact fact;
        lock (_sync)
        {
            if ((owner.ExitRequested && phase != PreviewSessionPhase.Restoring) ||
                !ReferenceEquals(owner, _previewOwner)) return false;
            command = owner.PendingCommand;
            fact = owner.PendingFact ?? owner.StartFact;
        }
        var completion = new PreviewSessionCompletion(true, reason, phase, owner.Restoration,
            owner.Configuration, owner.LastSavedDraft, owner.FrozenSettings?.ContentHash,
            CompleteOriginalStart: false, CompleteCommand: completeCommand && fact != owner.StartFact);
        var deadline = new StoreDeadline(_audit!.CommitTimeout);
        var result = command is not null
            ? await _authorization!.CompletePreviewCommandAsync(command, owner.Header, fact, completion,
                deadline, CancellationToken.None).ConfigureAwait(false)
            : await _authorization!.CompletePreviewCommandAsync(owner.Header, fact, completion,
                deadline, CancellationToken.None).ConfigureAwait(false);
        if (result.Outcome.Audit != AuditPersistence.Persisted || result.Header is null)
        {
            MarkAuditFault("PreviewProgressAuditUnavailable");
            RequestPreviewExit(owner, "PreviewProgressAuditUnavailable");
            return false;
        }
        lock (_sync)
        {
            owner.Header = result.Header;
            if (phase == PreviewSessionPhase.Streaming)
            {
                owner.PendingCommand = null;
                owner.PendingFact = null;
            }
            PublishPreviewLocked(owner, phase, reason);
        }
        return true;
        }
        finally { _commandGate.Release(); }
    }

    private async Task FinishPreviewSessionAsync(PreviewSessionOwner owner, bool succeeded,
        string reason, PreviewRestorationState restoration)
    {
        if (owner.Retired.Task.IsCompleted) return;
        if (Interlocked.CompareExchange(ref owner.TerminalStarted, 1, 0) != 0)
        { await owner.Retired.Task.ConfigureAwait(false); return; }
        if (!await _commandGate.WaitAsync(_audit!.CommitTimeout).ConfigureAwait(false))
        {
            lock (_sync)
            {
                owner.Restoration = PreviewRestorationState.RecoveryBlocked;
                _previewRecoveryBlocked = true;
                PublishPreviewLocked(owner, PreviewSessionPhase.RecoveryBlocked, "PreviewTerminalRuntimeBusy");
            }
            MarkAuditFault("PreviewTerminalRuntimeBusy");
            try { await LatchPreviewRecoveryAlarmAsync().ConfigureAwait(false); }
            finally { owner.Retired.TrySetResult(true); }
            return;
        }
        bool blocked;
        try
        {
        var auditSucceeded = true;
        // A pending tune/freeze gets its own terminal before the exit (if present)
        // atomically terminates the original session admission.
        if (owner.PendingCommand is { } pending && owner.PendingFact is { } pendingFact &&
            pendingFact.CorrelationId != owner.StartFact.CorrelationId)
        {
            var pendingResult = await _authorization!.CompletePreviewCommandAsync(pending, owner.Header,
                pendingFact, new(false, reason, PreviewSessionPhase.Restoring, restoration,
                    owner.Configuration, owner.LastSavedDraft, owner.FrozenSettings?.ContentHash,
                    CompleteOriginalStart: false, CompleteCommand: true),
                new StoreDeadline(_audit!.CommitTimeout), CancellationToken.None).ConfigureAwait(false);
            auditSucceeded = pendingResult.Outcome.Audit == AuditPersistence.Persisted && pendingResult.Header is not null;
            if (pendingResult.Header is not null) owner.Header = pendingResult.Header;
        }
        var terminalPhase = restoration == PreviewRestorationState.RecoveryBlocked
            ? PreviewSessionPhase.RecoveryBlocked : PreviewSessionPhase.Closed;
        var terminal = new PreviewSessionCompletion(succeeded, reason, terminalPhase, restoration,
            owner.Configuration, owner.LastSavedDraft, owner.FrozenSettings?.ContentHash);
        var final = owner.ExitCommand is { } exit && owner.ExitFact is { } exitFact
            ? await _authorization!.CompletePreviewCommandAsync(exit, owner.Header, exitFact, terminal,
                new StoreDeadline(_audit!.CommitTimeout), CancellationToken.None).ConfigureAwait(false)
            : await _authorization!.CompletePreviewCommandAsync(owner.Header, owner.StartFact, terminal,
                new StoreDeadline(_audit!.CommitTimeout), CancellationToken.None).ConfigureAwait(false);
        auditSucceeded &= final.Outcome.Audit == AuditPersistence.Persisted && final.Header is not null;
        if (final.Header is not null) owner.Header = final.Header;
        blocked = !auditSucceeded || restoration == PreviewRestorationState.RecoveryBlocked;
        if (!auditSucceeded) MarkAuditFault("PreviewTerminalAuditUnavailable");
        lock (_sync)
        {
            owner.Restoration = blocked ? PreviewRestorationState.RecoveryBlocked : restoration;
            _previewRecoveryBlocked |= blocked;
            owner.PendingCommand = null;
            owner.PendingFact = null;
            PublishPreviewLocked(owner, blocked ? PreviewSessionPhase.RecoveryBlocked : terminalPhase,
                !auditSucceeded ? "PreviewTerminalAuditUnavailable" : reason);
            if (!blocked && ReferenceEquals(_previewOwner, owner))
            {
                _previewOwner = null;
                PublishLocked(_snapshot with { Mode = ExclusiveMode.None, Ready = false,
                    ArmState = ProductionArmState.Disarmed,
                    AdmissionBlockers = new(_snapshot.AdmissionBlockers.Where(value => value is not
                        "PreviewSessionInProgress" and not "PreviewStartupRecoveryPending")) });
            }
        }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            blocked = true;
            MarkAuditFault("PreviewTerminalPersistenceFailed");
            lock (_sync)
            {
                _previewRecoveryBlocked = true;
                owner.Restoration = PreviewRestorationState.RecoveryBlocked;
                PublishPreviewLocked(owner, PreviewSessionPhase.RecoveryBlocked, "PreviewTerminalPersistenceFailed");
            }
        }
        finally { _commandGate.Release(); }
        try { if (blocked) await LatchPreviewRecoveryAlarmAsync().ConfigureAwait(false); }
        finally
        {
            owner.CallerCancellation.Dispose();
            owner.Retired.TrySetResult(true);
        }
    }

    private async Task LatchPreviewRecoveryAlarmAsync()
    {
        AlarmObservation observation;
        lock (_sync)
        {
            _previewRecoveryBlocked = true;
            if (!IsPreviewAlarmMappingValid())
            { MarkAuditFault("PreviewAlarmMappingUnavailable", alarmAuthorityUnavailable: true); return; }
            _registeredAlarmSources.Add(PreviewAlarmSource);
            observation = new(_snapshot.RuntimeEpoch, NextAlarmObservationSequenceLocked(PreviewAlarmCode),
                PreviewAlarmCode, PreviewAlarmSource, false, DateTimeOffset.UtcNow);
            PublishLocked(_snapshot);
        }
        // Actual retirement may finish after ordinary command admission has stopped.
        // This private path can only assert the mapped unhealthy Preview source.
        using var timeout = new CancellationTokenSource(_audit!.CommitTimeout);
        var entered = false;
        try
        {
            await _commandGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            var outcome = await ObserveAlarmCoreAsync(observation, timeout.Token,
                previewRestorationDuringShutdown: true).ConfigureAwait(false);
            if (!outcome.Accepted) MarkAuditFault("PreviewRecoveryAlarmUnavailable", alarmAuthorityUnavailable: true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { MarkAuditFault("PreviewRecoveryAlarmUnavailable", alarmAuthorityUnavailable: true); }
        finally { if (entered) _commandGate.Release(); }
    }

    private async Task ShutdownPreviewAsync()
    {
        Task[] pending;
        var timeout = _previewOptions?.ShutdownTimeout ?? TimeSpan.FromSeconds(5);
        if (!await _commandGate.WaitAsync(timeout).ConfigureAwait(false))
            throw new InvalidOperationException("PreviewShutdownAdmissionIncomplete");
        try
        {
            RequestPreviewStop("PreviewRuntimeShutdown");
            lock (_sync)
                pending = new[] { _previewStartupTask, _previewOwner?.Retired.Task }
                    .Where(task => task is not null).Select(task => task!).ToArray();
        }
        finally { _commandGate.Release(); }
        try
        {
            await Task.WhenAll(pending).WaitAsync(timeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException) { throw new InvalidOperationException("PreviewShutdownIncomplete"); }
    }
}
