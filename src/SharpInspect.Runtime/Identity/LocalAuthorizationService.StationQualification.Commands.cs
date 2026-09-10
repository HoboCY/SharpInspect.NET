using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal async ValueTask<StationQualificationTransactionResult> HandleStationQualificationCommandAsync(
        StationQualificationCommand command, Guid epoch, Guid attemptId, RecipeActivationRecord? baseline,
        StationQualificationSessionHeader? existing, StationQualificationSessionEvent? lastEvent,
        string? forcedRejection, StoreDeadline deadline, CancellationToken token)
    {
        StationQualificationTransactionResult Unavailable(string reason) => new(new(command.CorrelationId,
            CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, attemptId), null, null, false,
            lastEvent?.Position ?? 0, lastEvent?.ContentHash);
        try
        {
            var written = await _store.UpdateStationQualificationCommandAsync(command,
                (identity, state, actual, duplicate) => AuthorizeStationQualification(identity, state, command,
                    epoch, attemptId, baseline, actual, existing, lastEvent, forcedRejection, duplicate, token),
                CancellationToken.None, deadline).ConfigureAwait(false);
            return written.Committed && written.Result is StationQualificationTransactionResult result
                ? result : Unavailable(written.ReasonCode);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Unavailable("StationQualificationAdmissionAuditUnavailable"); }
    }

    private IdentityUpdate AuthorizeStationQualification(IdentityAuthorityState identity,
        StationQualificationCommandState state, StationQualificationCommand command, Guid epoch, Guid attemptId,
        RecipeActivationRecord? expectedBaseline, StationQualificationAdmissionInput? actualInput,
        StationQualificationSessionHeader? expectedHeader, StationQualificationSessionEvent? expectedPrevious,
        string? forcedRejection, bool duplicate, CancellationToken token)
    {
        SessionAuthorizationLease? lease = null;
        StepUpGrant? reserved = null;
        LocalAdministratorState? actor = null;
        var transferred = false;
        try
        {
            var invocation = command.Invocation;
            var previous = state.Events.LastOrDefault();
            var existing = state.Header;
            var reason = state.Enabled ? "Authorized" : "StationQualificationConfigurationRequired";
            if (reason == "Authorized" && invocation.Source != CommandSource.PhysicalConsole) reason = "LocalConsoleRequired";
            if (reason == "Authorized" && !TryLease(invocation, out lease, out var leaseReason)) reason = leaseReason;
            if (lease is not null) actor = Find(identity, lease.Identity.PrincipalId);
            if (reason == "Authorized" && (actor is not { Enabled: true } ||
                !actor.Permissions.Contains(Permission.RunStationQualification))) reason = "PermissionDenied";
            if (reason == "Authorized" && duplicate) reason = "DuplicateCorrelationId";
            if (reason == "Authorized" && forcedRejection is not null) reason = forcedRejection;
            if (reason == "Authorized" && epoch == Guid.Empty) reason = "StationQualificationRuntimeUnavailable";
            if (reason == "Authorized" && state.RecoveryRequired && command is StartStationQualificationSessionCommand)
                reason = "StationQualificationRecoveryRequired";
            if (reason == "Authorized" &&
                ((expectedPrevious is null) != (previous is null) ||
                 previous?.Position != expectedPrevious?.Position ||
                 previous?.ContentHash != expectedPrevious?.ContentHash))
                reason = "StationQualificationStateChanged";
            if (reason == "Authorized" && command is StartStationQualificationSessionCommand start &&
                (actualInput is null || actualInput.TargetBaseline.SuccessfulSnapshot is null ||
                 actualInput.TargetBaseline.Reference != start.Plan.TargetActivation))
                reason = "StationQualificationTargetBaselineUnavailable";
            if (reason == "Authorized" && command is StartStationQualificationSessionCommand requestedStart &&
                (expectedBaseline is null || actualInput is null ||
                 expectedBaseline.Reference != actualInput.TargetBaseline.Reference ||
                 actualInput.Plan.ContentHash != requestedStart.Plan.ContentHash ||
                 !SameStationQualificationCameraTarget(actualInput)))
                reason = "StationQualificationAdmissionChanged";
            if (reason == "Authorized" && command is StartStationQualificationSessionCommand &&
                state.PendingHeader is not null)
                reason = "StationQualificationSessionInProgress";
            if (reason == "Authorized" && command is ExitStationQualificationSessionCommand exit &&
                (existing is null || previous?.Terminal == true || existing.SessionId != exit.SessionId ||
                 actor!.PrincipalId != existing.ActorPrincipalId || lease!.SessionId != existing.ActorSessionId ||
                 actor.AuthorizationRevision != existing.ActorAuthorizationRevision ||
                 _options.AuthorizationPolicy.Id != existing.AuthorizationPolicy.Id ||
                 _options.AuthorizationPolicy.Version != existing.AuthorizationPolicy.Version ||
                 _options.AuthorizationPolicy.ContentHash != existing.AuthorizationPolicy.ContentHash ||
                 actualInput is null || !SameStationQualificationCameraTarget(actualInput)))
                reason = "StationQualificationAuthorityChanged";
            if (reason == "Authorized" && command is ExitStationQualificationSessionCommand &&
                state.Events.Any(value => value.CommandKind == AuditedCommandKind.ExitStationQualificationSession))
                reason = "StationQualificationExitAlreadyPending";
            if (reason == "Authorized" && expectedHeader is not null &&
                (existing is null || existing.ContentHash != expectedHeader.ContentHash))
                reason = "StationQualificationStateChanged";
            if (reason == "Authorized" && token.IsCancellationRequested) reason = "StationQualificationCommandCancelled";
            // Start always needs a fresh, single-use grant, regardless of whether
            // the deployment's general permission policy listed it as step-up.
            if (reason == "Authorized" && command is StartStationQualificationSessionCommand)
                reason = CheckGrant(command, actor!, lease!.SessionId, reserve: true, out reserved);
            var accepted = reason == "Authorized";
            var now = _utcNow().ToUniversalTime();
            if (now < identity.LastObservedUtc) now = identity.LastObservedUtc;
            if (previous is not null && now < previous.RecordedAtUtc) now = previous.RecordedAtUtc;
            var finalReason = accepted ? command is StartStationQualificationSessionCommand ?
                "StationQualificationStartAuthorized" : "StationQualificationExitAuthorized" : reason;
            var header = existing;
            if (accepted && command is StartStationQualificationSessionCommand requested)
                header = new(Guid.NewGuid(), epoch, Guid.NewGuid(), command.CorrelationId, attemptId, requested.Plan,
                    actualInput!.TargetBaseline, actor!.PrincipalId, lease!.SessionId, actor.AuthorizationRevision,
                    new(_options.AuthorizationPolicy.Id, _options.AuthorizationPolicy.Version, _options.AuthorizationPolicy.ContentHash),
                    invocation.StepUpGrantId, command.AuthorizationTarget, command.Reason, now);
            var fact = new CommandAuditFact(Guid.NewGuid(), attemptId, command.CorrelationId, epoch, now,
                CommandKind(command), Enum.IsDefined(invocation.Source) ? invocation.Source : null,
                invocation.PrincipalId is { Length: <= 256 } principal ? principal : null,
                invocation.SessionId, invocation.StepUpGrantId, CommandAuditPhase.Outcome,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected, finalReason,
                actor?.PrincipalId.ToString("D"));
            var authorization = AuthorizationEvent(identity, accepted ? IdentityEventKind.StationQualificationAuthorized :
                IdentityEventKind.ManagementRejected, finalReason, ValidBinding(Binding(command)) ? Binding(command) : null,
                actor?.PrincipalId, lease?.SessionId, invocation.StepUpGrantId, command.CorrelationId,
                actor?.AuthorizationRevision ?? 0, targetPrincipalId: actor?.PrincipalId, capturedTime: now)
                with { OperationId = accepted ? header?.SessionId : null };
            var next = accepted ? new StationQualificationSessionEvent((previous?.Position ?? 0) + 1,
                previous?.ContentHash, header!, command.CorrelationId, attemptId, CommandKind(command),
                command is StartStationQualificationSessionCommand ? StationQualificationSessionPhase.Admitted :
                    previous?.Phase ?? StationQualificationSessionPhase.Restoring,
                StationQualificationRestorationState.Pending, finalReason, false, now,
                commandAuthorizationTarget: command.AuthorizationTarget) : null;
            var result = new StationQualificationTransactionResult(new(command.CorrelationId,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected, finalReason,
                AuditPersistence.Persisted, attemptId), header, next, accepted,
                previous?.Position ?? 0, previous?.ContentHash, accepted ? fact : null);
            if (!accepted) return new(result, new[] { authorization }, new[] { fact });
            var guard = new AuthorizationCommitGuard(lease!, () =>
            { lock (_grantSync) if (reserved is not null) reserved.State = GrantState.Consumed; }, () =>
            { lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active; });
            transferred = true;
            return new(result, new[] { authorization }, new[] { fact }, guard);
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

    private static bool SameStationQualificationCameraTarget(StationQualificationAdmissionInput input)
    {
        var target = input.TargetBaseline.SuccessfulSnapshot?.CameraSetup;
        var current = input.CurrentBinding;
        return target is not null && current.Binding is not null && !current.HasPending &&
            string.Equals(target.LogicalRole, current.LogicalRole, StringComparison.Ordinal) &&
            target.Binding is { } targetBinding &&
            targetBinding.LogicalRole == current.Binding.LogicalRole &&
            targetBinding.Revision == current.Binding.Revision &&
            targetBinding.RevisionHash == current.Binding.RevisionHash &&
            targetBinding.Target.ContentHash == current.Binding.Target.ContentHash &&
            targetBinding.OperationId == current.Binding.OperationId;
    }
}
