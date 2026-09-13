using SharpInspect.Abstractions;
using SharpInspect.Runtime.Manual;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async ValueTask<RuntimeCommandOutcome> SubmitManualInspectionAsync(ManualInspectionCommand command,
        CancellationToken cancellationToken)
    {
        var attempt = Guid.NewGuid();
        RuntimeCommandOutcome Unavailable(string reason) => new(command.CorrelationId,
            CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, attempt);
        if (_manualOptions is null || _authorization is null || _audit is not SqliteCommandStore store)
            return new(command.CorrelationId, CommandDisposition.Rejected, "ManualInspectionUnavailable");
        if (Interlocked.Increment(ref _queuedCommands) > 64)
        {
            Interlocked.Decrement(ref _queuedCommands);
            return Unavailable("CommandQueueFull");
        }
        var entered = false;
        var reserved = false;
        var deadline = new StoreDeadline(store.CommitTimeout);
        try
        {
            await _storeInitialization.WaitAsync(PositiveRemaining(deadline), cancellationToken).ConfigureAwait(false);
            if (!await _commandGate.WaitAsync(PositiveRemaining(deadline), cancellationToken).ConfigureAwait(false))
                return Unavailable("ManualInspectionRuntimeBusy");
            entered = true;
            ManualInspectionOwner? owner;
            Guid epoch;
            string? rejection;
            lock (_sync)
            {
                epoch = _snapshot.RuntimeEpoch;
                owner = _manualOwner;
                rejection = command is StartManualInspectionSessionCommand ? CheckManualInspectionStartLocked() :
                    CheckManualInspectionCommandLocked(command, owner);
                if (command is StartManualInspectionSessionCommand && rejection is null)
                {
                    _manualAdmissionPending = reserved = true;
                    _manualAdmissionStopRequested = false;
                    _manualAdmissionAbortRequested = false;
                }
            }
            var access = await _authorization.GetManualInspectionAccessAsync(command.Invocation, cancellationToken)
                .ConfigureAwait(false);
            if (!access.CanRun) rejection = access.ReasonCode;
            ManualInspectionAdmissionInput? input = null;
            ManualRecipeExecutionPlan? plan = null;
            if (rejection is null && command is StartManualInspectionSessionCommand start)
                (input, plan, rejection) = await PrepareManualInspectionAdmissionAsync(start, store, cancellationToken)
                    .ConfigureAwait(false);
            lock (_sync)
            {
                if (rejection is null && command is not ExitManualInspectionSessionCommand && (_manualAdmissionStopRequested || _shutdownRequested ||
                    Volatile.Read(ref _pendingLocalStops) != 0)) rejection = "ManualInspectionStopping";
                if (rejection is null && command is not StartManualInspectionSessionCommand)
                    rejection = CheckManualInspectionCommandLocked(command, owner);
            }
            var admitted = await _authorization.HandleManualInspectionCommandAsync(command, epoch, attempt,
                input, owner?.Header, rejection, deadline, cancellationToken).ConfigureAwait(false);
            if (admitted.Outcome.Disposition != CommandDisposition.Accepted) return admitted.Outcome;
            if (admitted.CommandFact is { } replay && replay.AttemptId != attempt) return admitted.Outcome;
            if (admitted.Header is null || admitted.CommandFact is null)
            {
                lock (_sync) _manualRecoveryBlocked = true;
                MarkAuditFault("ManualInspectionAdmissionEvidenceUnavailable");
                return Unavailable("ManualInspectionAdmissionEvidenceUnavailable");
            }
            lock (_sync)
            {
                if (command is StartManualInspectionSessionCommand)
                {
                    if (input is null || plan is null) throw new InvalidOperationException("ManualInspectionAdmissionInputMissing");
                    owner = new(plan, input.ActiveBaseline, admitted.Header, admitted.CommandFact)
                    { PendingCommand = command, PendingFact = admitted.CommandFact };
                    _manualOwner = owner;
                    _manualAdmissionPending = false;
                    PublishManualInspectionLocked(owner, ManualInspectionSessionPhase.Admitted, "ManualInspectionAdmitted");
                    owner.Operation = Task.Run(() => ExecuteManualInspectionStartAsync(owner));
                    if (_sessions is not null) ScheduleManualInspectionSessionExitLocked(_sessions.Current);
                    if (_manualAdmissionStopRequested || Volatile.Read(ref _pendingLocalStops) != 0 || _shutdownRequested)
                        RequestManualInspectionExitLocked(owner, "ManualInspectionStopping",
                            _manualAdmissionAbortRequested || _shutdownRequested);
                }
                else if (owner is null || !ReferenceEquals(owner, _manualOwner))
                    throw new InvalidOperationException("ManualInspectionAdmittedOwnerMissing");
                else if (command is ExitManualInspectionSessionCommand exit)
                {
                    owner.Header = admitted.Header;
                    if (owner.ExitCommand is { } previousExit && owner.ExitFact is { } previousExitFact)
                        owner.PriorExitCommands.Add((previousExit, previousExitFact));
                    owner.ExitCommand = exit;
                    owner.ExitFact = admitted.CommandFact;
                    RequestManualInspectionExitLocked(owner, exit.Mode == ManualInspectionExitMode.Abort ?
                        "ManualInspectionAborted" : "ManualInspectionExited", exit.Mode == ManualInspectionExitMode.Abort);
                }
                else if (command is RunManualInspectionCommand run)
                {
                    if (admitted.Run is null) throw new InvalidOperationException("ManualInspectionRunAdmissionMissing");
                    owner.Header = admitted.Header;
                    owner.CurrentRun = admitted.Run;
                    owner.CurrentRunId = admitted.Run.RunId;
                    owner.PendingCommand = run;
                    owner.PendingFact = admitted.CommandFact;
                    PublishManualInspectionLocked(owner, ManualInspectionSessionPhase.Acquiring, "ManualInspectionAcquiring");
                    owner.Operation = Task.Run(() => ExecuteManualInspectionRunAsync(owner));
                }
            }
            // 持久准入后的调用方等待取消，不能转化为已接受物理操作的取消令牌。
            return admitted.Outcome;
        }
        catch (OperationCanceledException) { return Unavailable("ManualInspectionCommandCancelled"); }
        catch (TimeoutException) { return Unavailable("ManualInspectionCommandDeadlineExceeded"); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (reserved) MarkAuditFault("ManualInspectionAdmissionUnavailable");
            return Unavailable("ManualInspectionCommandUnavailable");
        }
        finally
        {
            if (reserved) lock (_sync)
            {
                _manualAdmissionPending = false;
                if (_manualOwner is null && !_manualRecoveryBlocked)
                    PublishLocked(_snapshot with
                    {
                        Mode = ExclusiveMode.None,
                        AdmissionBlockers = new(_snapshot.AdmissionBlockers.Where(code =>
                            code != "ManualInspectionSessionInProgress"))
                    });
            }
            if (entered) _commandGate.Release();
            Interlocked.Decrement(ref _queuedCommands);
        }
    }

    private string? CheckManualInspectionCommandLocked(ManualInspectionCommand command, ManualInspectionOwner? owner)
    {
        if (_disposed || _shutdownRequested) return "RuntimeStopped";
        if (owner is null) return "ManualInspectionSessionNotFound";
        var sessionId = command switch
        {
            RunManualInspectionCommand run => run.SessionId,
            ExitManualInspectionSessionCommand exit => exit.SessionId,
            _ => Guid.Empty
        };
        if (owner.SessionId != sessionId) return "ManualInspectionSessionNotFound";
        if (command.Invocation.SessionId != owner.ActorSessionId) return "ManualInspectionSessionActorChanged";
        if (_manualRecoveryBlocked) return "ManualInspectionRecoveryRequired";
        if (command is ExitManualInspectionSessionCommand exitCommand)
            return owner.ExitFact is not null && !(owner.ExitCommand?.Mode == ManualInspectionExitMode.Graceful &&
                exitCommand.Mode == ManualInspectionExitMode.Abort) ? "ManualInspectionExitAlreadyPending" : null;
        if (owner.ExitRequested) return "ManualInspectionStopping";
        if (owner.CurrentRunId.HasValue || owner.PendingCommand is not null) return "ManualInspectionRunInProgress";
        if (_manualSnapshot?.Phase != ManualInspectionSessionPhase.ReadyForRun || owner.Prepared is null ||
            owner.Camera is null || owner.Execution is null) return "ManualInspectionNotReadyForRun";
        return null;
    }

    private async Task<(ManualInspectionAdmissionInput? Input, ManualRecipeExecutionPlan? Plan, string? Reason)>
        PrepareManualInspectionAdmissionAsync(StartManualInspectionSessionCommand command, SqliteCommandStore store,
            CancellationToken token)
    {
        var resolved = await _manualResolver!.ResolveAsync(command.Selection, token).ConfigureAwait(false);
        if (resolved.Plan is null) return (null, null, resolved.ReasonCode);
        var plan = resolved.Plan;
        var camera = await store.ReadCameraSetupAsync(plan.Content.CameraRole, token).ConfigureAwait(false);
        if (!camera.Result.Available || camera.State.Binding is null || camera.State.HasPending)
            return (null, null, camera.Result.ReasonCode);
        if (_manualStoreOptions!.RecipeActivations is null)
        {
            lock (_sync)
                if (command.ExpectedActive is not null || _activeActivation is not null)
                    return (null, null, "ManualInspectionActiveBaselineUnavailable");
            return (new(plan.Draft, plan.Release, camera.State, null), plan, null);
        }
        var query = new SqliteRecipeActivationQuery(_manualStoreOptions!);
        var current = await query.ReadCurrentAsync(token).ConfigureAwait(false);
        if (!current.Available || current.RecoveryRequired) return (null, null, current.ReasonCode);
        RecipeActivationRecord? baseline = current.Record;
        if (command.ExpectedActive is { } expected)
        {
            var exact = await query.ReadAsync(expected, token).ConfigureAwait(false);
            if (!exact.Available || exact.Record?.SuccessfulSnapshot is null) return (null, null, exact.ReasonCode);
            baseline = exact.Record;
            lock (_sync)
                if (current.Record?.Reference != expected &&
                    _activeActivation?.Snapshot.ContentHash != baseline.SuccessfulSnapshot.ContentHash)
                    return (null, null, "ManualInspectionActiveBaselineConflict");
        }
        else
        {
            lock (_sync)
                if (baseline is not null || _activeActivation is not null)
                    return (null, null, "ManualInspectionActiveBaselineRequired");
        }
        return (new(plan.Draft, plan.Release, camera.State, baseline), plan, null);
    }
}
