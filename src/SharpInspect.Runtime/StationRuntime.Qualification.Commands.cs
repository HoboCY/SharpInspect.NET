using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async ValueTask<RuntimeCommandOutcome> SubmitStationQualificationAsync(StationQualificationCommand command,
        CancellationToken token)
    {
        var attempt = Guid.NewGuid();
        RuntimeCommandOutcome Unavailable(string reason) => new(command.CorrelationId,
            CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, attempt);
        if (_stationQualificationOptions is null || _authorization is null || _audit is not SqliteCommandStore store)
            return Unavailable("StationQualificationUnavailable");
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
            await _storeInitialization.WaitAsync(PositiveRemaining(deadline), token).ConfigureAwait(false);
            if (!await _commandGate.WaitAsync(PositiveRemaining(deadline), token).ConfigureAwait(false))
                return Unavailable("StationQualificationRuntimeBusy");
            entered = true;
            StationQualificationOwner? owner;
            Guid epoch;
            string? rejection;
            lock (_sync)
            {
                owner = _stationQualificationOwner;
                epoch = _snapshot.RuntimeEpoch;
                rejection = command is StartStationQualificationSessionCommand start
                    ? CheckStationQualificationStartLocked(start) : CheckStationQualificationExitLocked(command, owner);
                if (rejection is null && command is StartStationQualificationSessionCommand)
                {
                    _stationQualificationAdmissionPending = reserved = true;
                    _stationQualificationAdmissionStopRequested = false;
                    PublishLocked(_snapshot);
                }
            }
            RecipeActivationRecord? baseline = null;
            var lastEvent = owner?.LastEvent;
            if (command is StartStationQualificationSessionCommand)
            {
                if (rejection is null && _qualificationModbusProfile is not null)
                {
                    try { await ReadQualificationCyclePolicyAsync(token).ConfigureAwait(false); }
                    catch (InvalidOperationException) { rejection = "QualificationCycleStoragePolicyUnavailable"; }
                }
                var current = await new SqliteStationQualificationHistoryQuery(_stationQualificationStoreOptions!)
                    .ReadCurrentAsync(token).ConfigureAwait(false);
                lastEvent = current.LastEvent;
                if (rejection is null && (!current.Available || current.RecoveryRequired)) rejection = current.ReasonCode;
                if (rejection is null)
                {
                    var activationQuery = new SqliteRecipeActivationQuery(_stationQualificationStoreOptions!);
                    var activation = await activationQuery.ReadAsync(
                        ((StartStationQualificationSessionCommand)command).Plan.TargetActivation, token).ConfigureAwait(false);
                    var currentActivation = await activationQuery.ReadCurrentAsync(token).ConfigureAwait(false);
                    baseline = activation.Record;
                    if (!activation.Available || !currentActivation.Available || currentActivation.RecoveryRequired ||
                        baseline?.SuccessfulSnapshot is null || !baseline.Outcome.Succeeded ||
                        baseline.Reference != ((StartStationQualificationSessionCommand)command).Plan.TargetActivation)
                        rejection = "StationQualificationTargetBaselineUnavailable";
                }
            }
            lock (_sync)
            {
                if (rejection is null && command is StartStationQualificationSessionCommand &&
                    (_stationQualificationAdmissionStopRequested || _shutdownRequested || Volatile.Read(ref _pendingLocalStops) != 0))
                    rejection = "StationQualificationStopping";
                if (rejection is null && command is ExitStationQualificationSessionCommand)
                    rejection = CheckStationQualificationExitLocked(command, owner);
            }
            var result = await _authorization.HandleStationQualificationCommandAsync(command, epoch, attempt,
                baseline, owner?.Header, lastEvent, rejection, deadline, token).ConfigureAwait(false);
            if (!result.Accepted || result.Outcome.Disposition != CommandDisposition.Accepted) return result.Outcome;
            if (result.Header is null || result.Event is null || result.CommandFact is null)
                throw new InvalidOperationException("StationQualificationAdmissionEvidenceMissing");
            lock (_sync)
            {
                if (command is StartStationQualificationSessionCommand)
                {
                    owner = new(result.Header, result.Event, result.CommandFact);
                    _stationQualificationOwner = owner;
                    _stationQualificationAdmissionPending = false;
                    PublishStationQualificationLocked(owner, StationQualificationSessionPhase.Admitted, "StationQualificationAdmitted");
                    if (_sessions is not null) ScheduleStationQualificationSessionExitLocked(_sessions.Current);
                    if (_stationQualificationAdmissionStopRequested || _shutdownRequested || Volatile.Read(ref _pendingLocalStops) != 0)
                        RequestStationQualificationExitLocked(owner, "StationQualificationStopping", abort: true);
                    owner.Operation = Task.Run(() => ExecuteStationQualificationAsync(owner));
                }
                else
                {
                    if (owner is null || !ReferenceEquals(owner, _stationQualificationOwner))
                        throw new InvalidOperationException("StationQualificationOwnerMissing");
                    owner.LastEvent = result.Event;
                    owner.ExitFact = result.CommandFact;
                    var exit = (ExitStationQualificationSessionCommand)command;
                    owner.ExitAuthorizationTarget = exit.AuthorizationTarget;
                    RequestStationQualificationExitLocked(owner, exit.Abort ? "StationQualificationAborted" :
                        "StationQualificationExited", exit.Abort);
                }
            }
            return result.Outcome;
        }
        catch (OperationCanceledException) { return Unavailable("StationQualificationCommandCancelled"); }
        catch (TimeoutException) { return Unavailable("StationQualificationCommandDeadlineExceeded"); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (reserved) MarkAuditFault("StationQualificationAdmissionUnavailable");
            return Unavailable("StationQualificationCommandUnavailable");
        }
        finally
        {
            if (reserved) lock (_sync)
            {
                _stationQualificationAdmissionPending = false;
                if (_stationQualificationOwner is null && !_stationQualificationRecoveryBlocked)
                    ClearStationQualificationProjectionLocked();
            }
            if (entered) _commandGate.Release();
            Interlocked.Decrement(ref _queuedCommands);
        }
    }

    private string? CheckStationQualificationExitLocked(StationQualificationCommand command, StationQualificationOwner? owner)
    {
        if (_disposed || _shutdownRequested) return "RuntimeStopped";
        if (command is not ExitStationQualificationSessionCommand exit) return "StationQualificationCommandInvalid";
        if (owner is null || owner.Header.SessionId != exit.SessionId) return "StationQualificationSessionNotFound";
        if (owner.Header.ActorSessionId != command.Invocation.SessionId) return "StationQualificationSessionActorChanged";
        if (_stationQualificationRecoveryBlocked || owner.Retired.Task.IsCompleted) return "StationQualificationRecoveryRequired";
        if (owner.ExitFact is not null) return "StationQualificationExitAlreadyPending";
        return null;
    }

    private void ClearStationQualificationProjectionLocked() => PublishLocked(_snapshot with
    {
        Mode = ExclusiveMode.None, Ready = false, ArmState = ProductionArmState.Disarmed,
        Busy = false, CurrentExecution = null,
        AdmissionBlockers = new(_snapshot.AdmissionBlockers.Where(code => code is not
            "StationQualificationSessionInProgress" and not "StationQualificationStartupRecoveryPending"))
    });
}
