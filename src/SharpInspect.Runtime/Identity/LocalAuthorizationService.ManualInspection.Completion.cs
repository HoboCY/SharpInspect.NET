using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal async ValueTask<ManualInspectionCommandResult> CompleteManualInspectionCommandAsync(
        ManualInspectionCommand command, ManualInspectionSessionHeader header, CommandAuditFact originalFact,
        ManualInspectionCompletion completion, StoreDeadline deadline, Func<bool>? progressAllowed = null,
        Func<ManualInspectionCompletion, ManualInspectionCompletion>? terminalDecision = null)
    {
        try
        {
            // The original public Start has no session ID. Progress must read
            // the admitted session, and must never enter the admission replay path.
            var storeCommand = command is StartManualInspectionSessionCommand
                ? new ManualInspectionContinuationCommand(command.CorrelationId, command.Invocation,
                    header.SessionId, originalFact.CommandKind, command.AuthorizationTarget, header.ChangeReason)
                : command;
            var write = await _store.UpdateManualInspectionCommandAsync(storeCommand,
                (identity, state, _) => CompleteManualInspection(identity, state, command, header, originalFact,
                    terminalDecision?.Invoke(completion) ?? completion, progressAllowed),
                CancellationToken.None, deadline, allowReplay: false).ConfigureAwait(false);
            return write.Committed && write.Result is ManualInspectionCommandResult result ? result :
                new(new(originalFact.CorrelationId, CommandDisposition.Rejected, write.ReasonCode,
                    AuditPersistence.Unavailable, originalFact.AttemptId));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(new(originalFact.CorrelationId, CommandDisposition.Rejected, "ManualInspectionTerminalAuditUnavailable",
                AuditPersistence.Unavailable, originalFact.AttemptId));
        }
    }

    private IdentityUpdate CompleteManualInspection(IdentityAuthorityState identity, ManualInspectionCommandState state,
        ManualInspectionCommand command, ManualInspectionSessionHeader requestedHeader, CommandAuditFact originalFact,
        ManualInspectionCompletion completion, Func<bool>? progressAllowed)
    {
        IdentityUpdate Invalid(string reason) => new(new ManualInspectionCommandResult(new(originalFact.CorrelationId,
            CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, originalFact.AttemptId)),
            Array.Empty<IdentityAuditEvent>(), NoMutation: true);
        var current = state.Header;
        if (!completion.CompleteCommand && progressAllowed is not null && !progressAllowed())
            return Invalid("ManualInspectionProgressAuthorityEnded");
        if (!state.Enabled || current is null || current.SessionId != requestedHeader.SessionId ||
            originalFact.Phase != CommandAuditPhase.Outcome || originalFact.Disposition != CommandDisposition.Accepted ||
            originalFact.CommandKind != CommandKind(command) || command.CorrelationId != originalFact.CorrelationId ||
            originalFact.RuntimeEpoch != current.RuntimeEpoch || originalFact.AuthenticatedHumanPrincipalId !=
            current.ActorPrincipalId.ToString("D")) return Invalid("ManualInspectionCompletionContextInvalid");
        var originalEvent = state.Events.FirstOrDefault(value => value.AttemptId == originalFact.AttemptId &&
            value.CommandCorrelationId == originalFact.CorrelationId && value.CommandKind == originalFact.CommandKind);
        if (originalEvent is null || originalEvent.Terminal || originalEvent.AuthorizationTarget != command.AuthorizationTarget)
            return Invalid("ManualInspectionAcceptedCommandMissing");
        var previousTerminal = state.Events.LastOrDefault(value => value.AttemptId == originalFact.AttemptId &&
            value.CommandCorrelationId == originalFact.CorrelationId && value.Terminal);
        if (previousTerminal is not null)
            return new(new ManualInspectionCommandResult(new(originalFact.CorrelationId,
                CommandDisposition.Accepted, previousTerminal.ReasonCode, AuditPersistence.Persisted, originalFact.AttemptId),
                current, previousTerminal, state.Runs.LastOrDefault(value => value.AttemptId == originalFact.AttemptId),
                originalFact), Array.Empty<IdentityAuditEvent>(), NoMutation: true);
        if (current.ContentHash != requestedHeader.ContentHash || current.RecoveryRequired)
            return Invalid("ManualInspectionStateChanged");
        var start = originalFact.CommandKind == AuditedCommandKind.StartManualInspectionSession;
        var runCommand = originalFact.CommandKind == AuditedCommandKind.RunManualInspection;
        var sessionTerminal = completion.Phase is ManualInspectionSessionPhase.Closed or ManualInspectionSessionPhase.RecoveryBlocked;
        if (sessionTerminal != completion.CompleteOriginalStart ||
            (completion.CompleteOriginalStart && !completion.CompleteCommand) ||
            (start && completion.CompleteCommand != completion.CompleteOriginalStart) ||
            (sessionTerminal && runCommand) ||
            (sessionTerminal && completion.Restoration == ManualInspectionRestorationState.Pending) ||
            (completion.Phase == ManualInspectionSessionPhase.RecoveryBlocked !=
                (completion.Restoration == ManualInspectionRestorationState.RecoveryBlocked)))
            return Invalid("ManualInspectionCompletionPhaseInvalid");
        if (sessionTerminal && state.Runs.GroupBy(value => value.RunId).Any(group => !group.Last().Terminal))
            return Invalid("ManualInspectionRunTerminalMissing");
        if (completion.CompleteOriginalStart && !start && state.StartCommandFact is null)
            return Invalid("ManualInspectionStartAuditMissing");
        if (!runCommand && completion.Run is not null) return Invalid("ManualInspectionUnexpectedRunEvidence");
        if (runCommand)
        {
            var admitted = state.Runs.FirstOrDefault(value => value.AttemptId == originalFact.AttemptId);
            var completed = completion.Run;
            if (admitted is null || completed is null || completed.RunId != admitted.RunId ||
                completed.SessionId != current.SessionId || completed.RuntimeEpoch != current.RuntimeEpoch ||
                completed.CommandCorrelationId != originalFact.CorrelationId || completed.AttemptId != originalFact.AttemptId ||
                completed.PartIdentity != admitted.PartIdentity || completed.AdmittedAtUtc != admitted.AdmittedAtUtc ||
                completed.Terminal != completion.CompleteCommand)
                return Invalid("ManualInspectionRunCompletionInvalid");
        }
        if (!start && !runCommand && !sessionTerminal && completion.CompleteCommand &&
            completion.ReasonCode is not ("ManualInspectionExitEscalatedToAbort" or "ManualInspectionInterruptedByRestart"))
            return Invalid("ManualInspectionExitCompletionInvalid");
        var now = ManualInspectionTime(identity, state);
        var header = TransitionManualInspectionHeader(current, completion.Phase, completion.Restoration,
            completion.ReasonCode, completion.Phase == ManualInspectionSessionPhase.RecoveryBlocked);
        CommandAuditFact Terminal(CommandAuditFact accepted) => new(Guid.NewGuid(), accepted.AttemptId,
            accepted.CorrelationId, accepted.RuntimeEpoch, now, accepted.CommandKind, accepted.Source,
            accepted.ClaimedPrincipalId, accepted.ClaimedSessionId, accepted.ClaimedStepUpGrantId,
            completion.Succeeded ? CommandAuditPhase.Completed : CommandAuditPhase.Failed, null,
            completion.ReasonCode, accepted.AuthenticatedHumanPrincipalId);
        var terminal = completion.CompleteCommand ? Terminal(originalFact) : null;
        var startTerminal = completion.CompleteOriginalStart && !start ? Terminal(state.StartCommandFact!) : null;
        var kind = completion.CompleteCommand ? completion.Succeeded ? IdentityEventKind.ManualInspectionSessionCompleted :
            IdentityEventKind.ManualInspectionSessionFailed : IdentityEventKind.ManualInspectionSessionActionAuthorized;
        var authorization = AuthorizationEvent(identity, kind, completion.ReasonCode,
            ValidBinding(Binding(command)) ? Binding(command) : null, current.ActorPrincipalId, current.ActorSessionId,
            originalFact.ClaimedStepUpGrantId, originalFact.CorrelationId, current.ActorAuthorizationRevision,
            targetPrincipalId: current.ActorPrincipalId, capturedTime: now) with { OperationId = current.SessionId };
        var value = new ManualInspectionSessionEvent(Math.Max(1, state.Events.Count + 1L), header,
            originalFact.AttemptId, originalFact.CorrelationId, originalFact.CommandKind, completion.Phase,
            completion.Restoration, completion.ReasonCode, completion.CompleteCommand, current.ActorPrincipalId,
            current.ActorSessionId, current.ActorAuthorizationRevision, command.AuthorizationTarget, now,
            completion.Run?.RunId, completion.Run?.ContentHash);
        var result = new ManualInspectionCommandResult(new(originalFact.CorrelationId,
            completion.Succeeded ? CommandDisposition.Accepted : CommandDisposition.Rejected,
            completion.ReasonCode, AuditPersistence.Persisted, originalFact.AttemptId), header, value,
            completion.Run, terminal ?? originalFact);
        AuthorizationCommitGuard? progressGuard = null;
        if (!completion.CompleteCommand)
        {
            var actor = Find(identity, current.ActorPrincipalId);
            if (actor is not { Enabled: true } || !actor.Permissions.Contains(Permission.RunManualInspection) ||
                actor.AuthorizationRevision != current.ActorAuthorizationRevision ||
                _options.AuthorizationPolicy.ContentHash != current.AuthorizationPolicy.ContentHash ||
                _sessions is null || !_sessions.TryAcquireAuthorizationLease(current.ActorPrincipalId,
                    current.ActorSessionId, out var progressLease, out _))
                return Invalid("ManualInspectionProgressAuthorityEnded");
            progressGuard = new AuthorizationCommitGuard(progressLease!, () => { }, () => { });
        }
        return new(result, new[] { authorization }, terminal is null ? null : new[] { terminal }, progressGuard,
            ManualInspection: new ManualInspectionMutation(header, value, completion.Run,
                startTerminal is null ? null : new[] { startTerminal }));
    }
}
