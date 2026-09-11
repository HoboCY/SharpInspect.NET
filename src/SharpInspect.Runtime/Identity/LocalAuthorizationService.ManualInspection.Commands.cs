using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal async ValueTask<ManualInspectionCommandResult> HandleManualInspectionCommandAsync(
        ManualInspectionCommand command, Guid epoch, Guid attemptId, ManualInspectionAdmissionInput? input,
        ManualInspectionSessionHeader? existing, string? forcedRejection, StoreDeadline deadline,
        CancellationToken cancellationToken)
    {
        ManualInspectionCommandResult Unavailable(string reason) => new(new(command.CorrelationId,
            CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, attemptId));
        try
        {
            var written = await _store.UpdateManualInspectionCommandAsync(command,
                (identity, state, actual) => AuthorizeManualInspection(identity, state, command,
                    epoch, attemptId, input, actual, existing, forcedRejection, cancellationToken),
                CancellationToken.None, deadline,
                replayAuthorization: (identity, state, replayCommand, header) =>
                    ReplayManualInspectionAuthorization(identity, state, replayCommand, header),
                allowReplay: true).ConfigureAwait(false);
            return written.Committed && written.Result is ManualInspectionCommandResult result
                ? result : Unavailable(written.ReasonCode);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Unavailable("ManualInspectionAdmissionAuditUnavailable"); }
    }

    /// <summary>
    /// A duplicate correlation may return a durable Manual result without
    /// reserving or consuming a new Step-Up grant. It still needs a fresh
    /// authenticated local authority check inside the serialized identity
    /// transaction; a previous accepted fact is not a current permission.
    /// </summary>
    private string ReplayManualInspectionAuthorization(
        IdentityAuthorityState identity, ManualInspectionCommandState state,
        ManualInspectionCommand command, ManualInspectionSessionHeader header)
    {
        SessionAuthorizationLease? lease = null;
        try
        {
            if (!state.Enabled) return "ManualInspectionConfigurationRequired";
            if (command.Invocation.Source != CommandSource.PhysicalConsole)
                return "LocalConsoleRequired";
            if (state.Header is null || state.Header.SessionId != header.SessionId)
                return "ManualInspectionStateChanged";
            if (!TryLease(command.Invocation, out lease, out var leaseReason)) return leaseReason;
            var actor = lease is null ? null : Find(identity, lease.Identity.PrincipalId);
            if (actor is not { Enabled: true } ||
                !actor.Permissions.Contains(Permission.RunManualInspection))
                return "PermissionDenied";
            if (actor.PrincipalId != header.ActorPrincipalId ||
                lease!.SessionId != header.ActorSessionId ||
                actor.AuthorizationRevision != header.ActorAuthorizationRevision ||
                _options.AuthorizationPolicy.Id != header.AuthorizationPolicy.Id ||
                _options.AuthorizationPolicy.Version != header.AuthorizationPolicy.Version ||
                _options.AuthorizationPolicy.ContentHash != header.AuthorizationPolicy.ContentHash)
                return "ManualInspectionAuthorityChanged";
            return "Authorized";
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private IdentityUpdate AuthorizeManualInspection(IdentityAuthorityState identity,
        ManualInspectionCommandState state, ManualInspectionCommand command, Guid epoch, Guid attemptId,
        ManualInspectionAdmissionInput? expectedInput, ManualInspectionAdmissionInput? actualInput,
        ManualInspectionSessionHeader? expectedHeader, string? forcedRejection, CancellationToken cancellationToken)
    {
        SessionAuthorizationLease? lease = null;
        StepUpGrant? reserved = null;
        LocalAdministratorState? actor = null;
        var transferred = false;
        try
        {
            var start = command is StartManualInspectionSessionCommand;
            var invocation = command.Invocation;
            var reason = state.Enabled ? "Authorized" : "ManualInspectionConfigurationRequired";
            if (reason == "Authorized" && invocation.Source != CommandSource.PhysicalConsole)
                reason = "LocalConsoleRequired";
            if (reason == "Authorized" && !TryLease(invocation, out lease, out var leaseReason)) reason = leaseReason;
            if (lease is not null) actor = Find(identity, lease.Identity.PrincipalId);
            if (reason == "Authorized" && (actor is not { Enabled: true } ||
                !actor.Permissions.Contains(Permission.RunManualInspection))) reason = "PermissionDenied";
            if (reason == "Authorized" && epoch == Guid.Empty) reason = "ManualInspectionRuntimeUnavailable";
            // Runtime rejection carries the exclusive owner and recovery fence.
            // Evaluate it after current authentication, before source-state details.
            if (reason == "Authorized" && forcedRejection is not null) reason = forcedRejection;
            if (reason == "Authorized" && command is StartManualInspectionSessionCommand or RunManualInspectionCommand &&
                state.LifecycleFailure is not null) reason = state.LifecycleFailure;
            var current = state.Header;
            if (reason == "Authorized" && !start && expectedHeader is not null &&
                current?.ContentHash != expectedHeader.ContentHash) reason = "ManualInspectionStateChanged";
            if (reason == "Authorized" && state.RecoveryRequired) reason = "ManualInspectionRecoveryRequired";
            if (reason == "Authorized" && start)
            {
                var requested = (StartManualInspectionSessionCommand)command;
                if (state.PendingHeader is not null) reason = "ManualInspectionSessionInProgress";
                else if (actualInput is null || expectedInput is null ||
                    !SameManualInspectionAdmission(expectedInput, actualInput)) reason = "ManualInspectionAdmissionChanged";
                else if (!ManualInspectionSourceMatches(requested.Selection, actualInput)) reason = "ManualInspectionSourceChanged";
                else if (requested.ExpectedActive != actualInput.ActiveBaseline?.Reference) reason = "ManualInspectionActiveChanged";
            }
            else if (reason == "Authorized")
            {
                if (current is null || ManualInspectionCommandSessionId(command) != current.SessionId)
                    reason = "ManualInspectionSessionNotFound";
                else if (!current.IsActive) reason = "ManualInspectionSessionClosed";
                else if (actor!.PrincipalId != current.ActorPrincipalId || lease!.SessionId != current.ActorSessionId ||
                    actor.AuthorizationRevision != current.ActorAuthorizationRevision || current.AuthorizationPolicy.ContentHash !=
                    _options.AuthorizationPolicy.ContentHash) reason = "ManualInspectionAuthorityChanged";
                else if (actualInput is null || !ManualInspectionSourceMatches(current.Selection, actualInput) ||
                    actualInput.CurrentBinding.Binding != current.CurrentBinding ||
                    actualInput.ActiveBaseline?.Reference != current.ActiveActivation)
                    reason = "ManualInspectionDependencyChanged";
                else if (command is RunManualInspectionCommand &&
                    (current.Phase != ManualInspectionSessionPhase.ReadyForRun ||
                        state.Runs.GroupBy(value => value.RunId).Any(group => !group.Last().Terminal)))
                    reason = "ManualInspectionRunInProgress";
            }
            if (reason == "Authorized" && cancellationToken.IsCancellationRequested) reason = "ManualInspectionCommandCancelled";
            if (reason == "Authorized") reason = CheckGrant(command, actor!, lease!.SessionId, reserve: true, out reserved);

            var accepted = reason == "Authorized";
            var now = ManualInspectionTime(identity, state);
            var header = current;
            ManualInspectionRunRecord? run = null;
            var finalReason = accepted ? start ? "ManualInspectionSessionStartAuthorized" :
                "ManualInspectionSessionActionAuthorized" : reason;
            if (accepted && start)
                header = CreateManualInspectionHeader((StartManualInspectionSessionCommand)command, epoch, attemptId,
                    actualInput!.Draft, actualInput.CurrentBinding.Binding!, actualInput.ActiveBaseline, actor!, lease!.SessionId, now);
            else if (accepted && command is RunManualInspectionCommand requestedRun)
            {
                header = TransitionManualInspectionHeader(header!, ManualInspectionSessionPhase.Acquiring,
                    ManualInspectionRestorationState.Pending, finalReason, false);
                run = new(1, Guid.NewGuid(), header.SessionId, epoch, command.CorrelationId, attemptId,
                    requestedRun.PartIdentity, ManualInspectionRunStatus.Admitted, InspectionDecision.Unknown,
                    null, "ManualInspectionRunAdmitted", now, null, null, null, null, null, null,
                    partIdentitySource: requestedRun.PartIdentity is null ? ManualInspectionPartIdentitySource.NotProduction :
                        ManualInspectionPartIdentitySource.HumanEntered,
                    partIdentityActorPrincipalId: requestedRun.PartIdentity is null ? null : header.ActorPrincipalId,
                    partIdentityActorSessionId: requestedRun.PartIdentity is null ? null : header.ActorSessionId);
            }
            var fact = new CommandAuditFact(Guid.NewGuid(), attemptId, command.CorrelationId, epoch, now,
                CommandKind(command), Enum.IsDefined(invocation.Source) ? invocation.Source : null,
                invocation.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
                invocation.SessionId, invocation.StepUpGrantId, CommandAuditPhase.Outcome,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected, finalReason,
                actor?.PrincipalId.ToString("D"));
            var authorization = AuthorizationEvent(identity, accepted ? start ?
                IdentityEventKind.ManualInspectionSessionStartAuthorized : IdentityEventKind.ManualInspectionSessionActionAuthorized :
                IdentityEventKind.ManagementRejected, finalReason, ValidBinding(Binding(command)) ? Binding(command) : null,
                actor?.PrincipalId, lease?.SessionId, invocation.StepUpGrantId, command.CorrelationId,
                actor?.AuthorizationRevision ?? 0, targetPrincipalId: actor?.PrincipalId, capturedTime: now)
                with { OperationId = accepted ? header?.SessionId : null };
            ManualInspectionSessionEvent? eventValue = accepted && header is not null ? new(
                Math.Max(1, state.Events.Count + 1L), header, attemptId, command.CorrelationId, CommandKind(command),
                header.Phase, header.Restoration, finalReason, false, actor!.PrincipalId, lease!.SessionId,
                actor.AuthorizationRevision, command.AuthorizationTarget, now, run?.RunId, run?.ContentHash) : null;
            var result = new ManualInspectionCommandResult(new(command.CorrelationId,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected, finalReason,
                AuditPersistence.Persisted, attemptId), accepted ? header : current, eventValue, run, accepted ? fact : null);
            if (!accepted || eventValue is null) return new(result, new[] { authorization }, new[] { fact });
            var guard = new AuthorizationCommitGuard(lease!, () =>
            {
                lock (_grantSync) if (reserved is not null) reserved.State = GrantState.Consumed;
            }, () =>
            {
                lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
            });
            transferred = true;
            return new(result, new[] { authorization }, new[] { fact }, guard,
                ManualInspection: new ManualInspectionMutation(header!, eventValue, run));
        }
        finally
        {
            if (!transferred)
            {
                lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
                lease?.Dispose();
            }
        }
    }

    private static bool ManualInspectionSourceMatches(ManualRecipeSelection selection, ManualInspectionAdmissionInput input) =>
        selection.Kind == ManualRecipeSourceKind.Draft
            ? input.Release is null && ManualRecipeSelection.FromDraft(input.Draft) == selection
            : input.Release is { } release && release.ReleaseId == selection.ReleaseId &&
                release.ContentHash == selection.ReleaseRecordContentHash && release.Recipe == selection.Recipe &&
                release.Source.RevisionContentHash == input.Draft.RevisionContentHash;

    private static bool SameManualInspectionAdmission(ManualInspectionAdmissionInput expected,
        ManualInspectionAdmissionInput actual) => expected.Draft.DraftId == actual.Draft.DraftId &&
        expected.Draft.Revision == actual.Draft.Revision &&
        expected.Draft.RevisionContentHash == actual.Draft.RevisionContentHash &&
        expected.Release?.ContentHash == actual.Release?.ContentHash &&
        expected.CurrentBinding.Binding == actual.CurrentBinding.Binding &&
        expected.ActiveBaseline?.Reference == actual.ActiveBaseline?.Reference;

    private DateTimeOffset ManualInspectionTime(IdentityAuthorityState identity, ManualInspectionCommandState state)
    {
        var now = _utcNow().ToUniversalTime();
        if (now < identity.LastObservedUtc) now = identity.LastObservedUtc;
        var last = state.Events.LastOrDefault()?.RecordedAtUtc;
        return last is { } observed && now < observed ? observed : now;
    }
}

internal sealed record ManualInspectionCommandResult(RuntimeCommandOutcome Outcome,
    ManualInspectionSessionHeader? Header = null, ManualInspectionSessionEvent? Event = null,
    ManualInspectionRunRecord? Run = null, CommandAuditFact? CommandFact = null);

internal sealed record ManualInspectionCompletion(bool Succeeded, string ReasonCode,
    ManualInspectionSessionPhase Phase, ManualInspectionRestorationState Restoration,
    ManualInspectionRunRecord? Run = null, bool CompleteOriginalStart = false, bool CompleteCommand = false);
