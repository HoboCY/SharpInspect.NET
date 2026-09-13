using SharpInspect.Abstractions;
using SharpInspect.Runtime.Preview;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async ValueTask<RuntimeCommandOutcome> SubmitPreviewAsync(PreviewSessionCommand command,
        CancellationToken cancellationToken)
    {
        var attempt = Guid.NewGuid();
        RuntimeCommandOutcome Unavailable(string reason) => new(command.CorrelationId,
            CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, attempt);
        if (command.CorrelationId == Guid.Empty || command.Invocation is null)
            return Unavailable("InvalidCommandContext");
        if (_previewOptions is null || _authorization is null || _audit is not SqliteCommandStore store)
            return new(command.CorrelationId, CommandDisposition.Rejected, "PreviewUnavailable");
        if (Interlocked.Increment(ref _queuedCommands) > 64)
        {
            Interlocked.Decrement(ref _queuedCommands);
            return Unavailable("CommandQueueFull");
        }
        var entered = false;
        var reservedAdmission = false;
        var deadline = new StoreDeadline(store.CommitTimeout);
        try
        {
            await _storeInitialization.WaitAsync(PositiveRemaining(deadline), cancellationToken).ConfigureAwait(false);
            if (!await _commandGate.WaitAsync(PositiveRemaining(deadline), cancellationToken).ConfigureAwait(false))
                return Unavailable("PreviewRuntimeBusy");
            entered = true;
            PreviewSessionOwner? owner;
            Guid epoch;
            string? rejection;
            lock (_sync)
            {
                epoch = _snapshot.RuntimeEpoch;
                owner = _previewOwner;
                rejection = command is StartPreviewSessionCommand
                    ? CheckPreviewStartLocked() : CheckPreviewCommandLocked(command, owner);
                if (command is StartPreviewSessionCommand && rejection is null)
                {
                    _previewAdmissionPending = reservedAdmission = true;
                    _previewAdmissionStopRequested = false;
                }
            }
            var access = await _authorization.GetPreviewAccessAsync(command.Invocation, cancellationToken)
                .ConfigureAwait(false);
            if (!access.CanRun) rejection = access.ReasonCode;
            PreviewSessionAdmissionInput? input = null;
            if (rejection is null && command is StartPreviewSessionCommand start)
                (input, rejection) = await PreparePreviewAdmissionAsync(start, store, cancellationToken)
                    .ConfigureAwait(false);

            // 保存沿用 Draft 事务及 EditRecipeDraft 权限；Preview 准入不能占用该事务的关联 ID 或授权凭据。
            if (command is SavePreviewToDraftCommand save && rejection is null && owner is not null)
            {
                Task<RuntimeCommandOutcome>? saving = null;
                lock (_sync)
                {
                    rejection = CheckPreviewCommandLocked(command, owner);
                    if (rejection is null)
                    {
                        owner.PendingCommand = save;
                        PublishPreviewLocked(owner, PreviewSessionPhase.SavingDraft, "PreviewDraftSaveInProgress");
                        saving = Task.Run(() => SavePreviewDraftAsync(owner, save, cancellationToken));
                        owner.Operation = saving;
                    }
                }
                if (saving is null) return Unavailable(rejection ?? "PreviewDraftSaveUnavailable");
                _commandGate.Release();
                entered = false;
                return await saving.ConfigureAwait(false);
            }

            var admitted = await _authorization.HandlePreviewCommandAsync(command, epoch, attempt,
                input, owner?.Header.SessionId == command.PreviewSessionId ? owner.Header : null,
                rejection, deadline, cancellationToken).ConfigureAwait(false);
            if (admitted.Outcome.Disposition != CommandDisposition.Accepted) return admitted.Outcome;
            if (admitted.CommandFact is { } replay && replay.AttemptId != attempt)
                return admitted.Outcome;
            if (admitted.Header is null || admitted.CommandFact is null)
            {
                MarkAuditFault("PreviewAdmissionEvidenceUnavailable");
                lock (_sync) _previewRecoveryBlocked = true;
                return Unavailable("PreviewAdmissionEvidenceUnavailable");
            }
            lock (_sync)
            {
                if (command is StartPreviewSessionCommand)
                {
                    if (input is null) throw new InvalidOperationException("PreviewAdmissionInputMissing");
                    owner = new(admitted.Header, admitted.CommandFact, input.Draft, input.ActiveBaseline)
                    { PendingCommand = command, PendingFact = admitted.CommandFact };
                    _previewOwner = owner;
                    _previewAdmissionPending = false;
                    PublishPreviewLocked(owner, PreviewSessionPhase.Admitted, "PreviewAdmitted");
                    owner.Operation = Task.Run(() => ExecutePreviewStartAsync(owner));
                    if (_sessions is not null) SchedulePreviewSessionExitLocked(_sessions.Current);
                    if (_previewAdmissionStopRequested || Volatile.Read(ref _pendingLocalStops) != 0 ||
                        _shutdownRequested || cancellationToken.IsCancellationRequested)
                        RequestPreviewExitLocked(owner, "PreviewStartCancelled");
                    else
                        owner.CallerCancellation = cancellationToken.Register(() => RequestPreviewExit(owner, "PreviewCancelled"));
                }
                else if (owner is null || !ReferenceEquals(owner, _previewOwner))
                    throw new InvalidOperationException("PreviewAdmittedOwnerMissing");
                else if (command is ExitPreviewSessionCommand exit)
                {
                    owner.Header = admitted.Header;
                    owner.ExitCommand = exit;
                    owner.ExitFact = admitted.CommandFact;
                    RequestPreviewExitLocked(owner, exit.Cancel ? "PreviewCancelled" : "PreviewSessionExited");
                }
                else
                {
                    owner.Header = admitted.Header;
                    owner.PendingCommand = command;
                    owner.PendingFact = admitted.CommandFact;
                    owner.FrozenSettings = null;
                    owner.Operation = command switch
                    {
                        ApplyPreviewTuningCommand tune => Task.Run(() => RunPreviewActionAsync(owner,
                            () => ExecutePreviewTuningAsync(owner, tune), cancellationToken)),
                        FreezePreviewSettingsCommand => Task.Run(() => RunPreviewActionAsync(owner,
                            () => ExecutePreviewFreezeAsync(owner), cancellationToken)),
                        _ => throw new InvalidOperationException("PreviewCommandUnsupported")
                    };
                }
            }
            return admitted.Outcome;
        }
        catch (OperationCanceledException) { return Unavailable("PreviewCommandCancelled"); }
        catch (TimeoutException) { return Unavailable("PreviewCommandDeadlineExceeded"); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (reservedAdmission) MarkAuditFault("PreviewAdmissionUnavailable");
            return Unavailable("PreviewCommandUnavailable");
        }
        finally
        {
            if (reservedAdmission) lock (_sync) _previewAdmissionPending = false;
            if (entered) _commandGate.Release();
            Interlocked.Decrement(ref _queuedCommands);
        }
    }

    private async Task RunPreviewActionAsync(PreviewSessionOwner owner, Func<Task> operation,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => RequestPreviewExit(owner, "PreviewCancelled"));
        await operation().ConfigureAwait(false);
    }

    private async Task<RuntimeCommandOutcome> SavePreviewDraftAsync(PreviewSessionOwner owner,
        SavePreviewToDraftCommand command, CancellationToken cancellationToken)
    {
        RecipeDraftRevision? committed = null;
        RuntimeCommandOutcome Result(bool saved, string reason) => new(command.CorrelationId,
            saved ? CommandDisposition.Accepted : CommandDisposition.Rejected, reason,
            saved ? AuditPersistence.Persisted : AuditPersistence.NotAttempted);
        using var registration = cancellationToken.Register(() => RequestPreviewExit(owner, "PreviewCancelled"));
        try
        {
            if (!await PausePreviewReaderAsync(owner).ConfigureAwait(false))
            {
                RequestPreviewExit(owner, "PreviewReaderRetirementFailed");
                return Result(false, "PreviewReaderRetirementFailed");
            }
            RecipeDraftContent content;
            lock (_sync)
            {
                if (owner.ExitRequested) return Result(false, "PreviewSessionStopping");
                if (owner.FrozenSettings is not { } frozen || frozen.ContentHash != command.FrozenSettingsContentHash ||
                    PreviewDraftReference.FromRevision(owner.Draft) != command.ExpectedDraft)
                    return Result(false, "PreviewFrozenDraftConflict");
                content = PreviewDraftSettings.Merge(owner.Draft.Content, frozen);
            }
            var saved = await _previewDrafts!.SaveAsync(new(command.CorrelationId, command.ExpectedDraft.DraftId,
                command.ExpectedDraft.Revision, command.ExpectedDraft.RevisionContentHash, content,
                command.ChangeReason, command.Invocation, command.Invocation.StepUpGrantId), cancellationToken)
                .ConfigureAwait(false);
            if (!saved.Saved || saved.Revision is null) return Result(false, saved.ReasonCode);
            committed = saved.Revision;
            // Draft 事务此时已经持久化；即使后续会话台账无法追加事件，也要保留保存成功事实。
            lock (_sync)
            {
                owner.Draft = committed;
                owner.LastSavedDraft = PreviewDraftReference.FromRevision(committed);
                PublishPreviewLocked(owner, PreviewSessionPhase.SavingDraft, "PreviewDraftSaved");
            }
            if (!await _commandGate.WaitAsync(_audit!.CommitTimeout).ConfigureAwait(false))
                throw new InvalidOperationException("PreviewSaveAuditRuntimeBusy");
            try
            {
                var recorded = await _authorization!.RecordPreviewDraftSavedAsync(owner.Header, owner.StartFact,
                    command, committed, new StoreDeadline(_audit.CommitTimeout), CancellationToken.None)
                    .ConfigureAwait(false);
                if (recorded.Outcome.Audit != AuditPersistence.Persisted || recorded.Header is null)
                    throw new InvalidOperationException("PreviewSaveAuditUnavailable");
                lock (_sync) owner.Header = recorded.Header;
            }
            finally { _commandGate.Release(); }
            return Result(true, "PreviewDraftSaved");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (committed is not null) MarkAuditFault("PreviewSavedDraftAuditUnavailable");
            RequestPreviewExit(owner, committed is not null ? "PreviewSavedDraftAuditUnavailable" : "PreviewDraftSaveUnavailable");
            return Result(committed is not null, committed is not null
                ? "PreviewDraftSavedPreviewAuditUnavailable" : "PreviewDraftSaveUnavailable");
        }
        finally
        {
            bool exiting;
            lock (_sync)
            {
                owner.PendingCommand = null;
                owner.PendingFact = null;
                exiting = owner.ExitRequested;
                if (!exiting)
                {
                    PublishPreviewLocked(owner, PreviewSessionPhase.Streaming,
                        committed is null ? "PreviewDraftUnchanged" : "PreviewDraftSaved");
                    ResumePreviewReaderLocked(owner);
                }
            }
            if (exiting) await RestorePreviewSessionAsync(owner).ConfigureAwait(false);
        }
    }

    private string? CheckPreviewCommandLocked(PreviewSessionCommand command, PreviewSessionOwner? owner)
    {
        if (_disposed || _shutdownRequested) return "RuntimeStopped";
        if (command.Invocation.Source != CommandSource.PhysicalConsole) return "LocalConsoleRequired";
        if (_previewRecoveryBlocked) return "PreviewRecoveryRequired";
        if (owner is null || owner.Header.SessionId != command.PreviewSessionId) return "PreviewSessionNotCurrent";
        if (command.Invocation.SessionId != owner.Header.ActorSessionId) return "PreviewSessionActorChanged";
        if (command is ExitPreviewSessionCommand)
            return owner.ExitCommand is not null ? "PreviewExitAlreadyPending" : null;
        if (owner.ExitRequested) return "PreviewSessionStopping";
        if (owner.PendingCommand is not null) return "PreviewCommandInProgress";
        if (_previewSnapshot?.Phase != PreviewSessionPhase.Streaming) return "PreviewNotStreaming";
        if (command is SavePreviewToDraftCommand save &&
            (owner.FrozenSettings is null || owner.FrozenSettings.ContentHash != save.FrozenSettingsContentHash ||
                PreviewDraftReference.FromRevision(owner.Draft) != save.ExpectedDraft))
            return "PreviewFrozenDraftConflict";
        return null;
    }

    private async Task<(PreviewSessionAdmissionInput? Input, string? Reason)> PreparePreviewAdmissionAsync(
        StartPreviewSessionCommand command, SqliteCommandStore store, CancellationToken token)
    {
        var draft = await _previewDrafts!.ReadAsync(command.Draft.DraftId, command.Draft.Revision, token)
            .ConfigureAwait(false);
        if (!draft.Available || draft.Revision is null) return (null, draft.ReasonCode);
        if (PreviewDraftReference.FromRevision(draft.Revision) != command.Draft) return (null, "PreviewDraftConflict");
        var camera = await store.ReadCameraSetupAsync(draft.Revision.Content.CameraRole, token).ConfigureAwait(false);
        if (!camera.Result.Available || camera.State.Binding is null || camera.State.HasPending)
            return (null, camera.Result.ReasonCode);
        var query = new SqliteRecipeActivationQuery(_previewStoreOptions!);
        var current = await query.ReadCurrentAsync(token).ConfigureAwait(false);
        if (!current.Available || current.RecoveryRequired) return (null, current.ReasonCode);
        RecipeActivationRecord? baseline = current.Record;
        if (command.ExpectedActive is { } expected)
        {
            var exact = await query.ReadAsync(expected, token).ConfigureAwait(false);
            if (!exact.Available || exact.Record?.SuccessfulSnapshot is null)
                return (null, exact.ReasonCode);
            baseline = exact.Record;
            lock (_sync)
            {
                if (current.Record?.Reference != expected &&
                    _activeActivation?.Snapshot.ContentHash != baseline.SuccessfulSnapshot.ContentHash)
                    return (null, "PreviewActiveBaselineConflict");
            }
        }
        else
        {
            lock (_sync)
                if (baseline is not null || _activeActivation is not null)
                    return (null, "PreviewActiveBaselineRequired");
        }
        return (new(draft.Revision, camera.State, baseline), null);
    }
}
