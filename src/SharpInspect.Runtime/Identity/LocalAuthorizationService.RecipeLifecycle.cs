using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal async ValueTask<RecipeLifecycleResult> ApplyRecipeLifecycleAsync(RuntimeCommand command,
        Guid epoch, RecipeActivationReference? activeToClear, Func<string?>? claimRuntimeCommit,
        string? preflightFailure, StoreDeadline deadline, CancellationToken callerCancellation)
    {
        var attempt = Guid.NewGuid();
        RecipeLifecycleResult Unavailable(string reason) => new(new(command.CorrelationId,
            CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, attempt));
        try
        {
            var write = await _store.UpdateRecipeLifecycleCommandAsync(command,
                (identity, state, duplicate) => AuthorizeRecipeLifecycle(identity, state, command, epoch,
                    attempt, activeToClear, claimRuntimeCommit, preflightFailure, duplicate, callerCancellation),
                CancellationToken.None, deadline).ConfigureAwait(false);
            // Once queued, await the writer's definitive completion. A cancelled
            // caller must not receive a false negative for a durable retirement.
            return write.Committed && write.Result is RecipeLifecycleResult result ? result : Unavailable(write.ReasonCode);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Unavailable("RecipeLifecycleAuditUnavailable"); }
    }

    private IdentityUpdate AuthorizeRecipeLifecycle(IdentityAuthorityState identity, RecipeLifecycleCommandState state,
        RuntimeCommand command, Guid epoch, Guid attempt, RecipeActivationReference? activeToClear,
        Func<string?>? claimRuntimeCommit, string? preflightFailure, bool duplicate, CancellationToken cancellation)
    {
        SessionAuthorizationLease? lease = null;
        LocalAdministratorState? actor = null;
        StepUpGrant? grant = null;
        var transferred = false;
        try
        {
            var abandon = command as AbandonRecipeDraftCommand;
            var retire = command as RetireReleasedRecipeCommand;
            var kind = abandon is not null ? RecipeLifecycleKind.DraftAbandoned : RecipeLifecycleKind.ReleasedRetired;
            var reason = abandon is null && retire is null ? "RecipeLifecycleCommandInvalid" :
                state.Enabled && _store.RecipeLifecycleOptions is not null ? "Authorized" : "RecipeLifecycleConfigurationRequired";
            if (reason == "Authorized" && !TryLease(command.Invocation, out lease, out var leaseReason)) reason = leaseReason;
            if (lease is not null) actor = Find(identity, lease.Identity.PrincipalId);
            if (reason == "Authorized" && (actor is not { Enabled: true } ||
                    !actor.Permissions.Contains(RequiredPermission(command)))) reason = "PermissionDenied";
            if (reason == "Authorized" && duplicate) reason = "DuplicateCorrelationId";
            if (reason == "Authorized" && epoch == Guid.Empty) reason = "RecipeLifecycleRuntimeUnavailable";
            if (reason == "Authorized" && cancellation.IsCancellationRequested) reason = "RecipeLifecycleCancelled";
            if (reason == "Authorized" && preflightFailure is not null) reason = preflightFailure;

            RecipeDraftRevision? source = null;
            RecipeReleaseRecord? release = null;
            var current = state.Current;
            var clearsActive = false;
            if (reason == "Authorized" && abandon is not null)
            {
                source = state.DraftHistory.Where(value => value.DraftId == abandon.DraftId)
                    .OrderBy(value => value.Revision).LastOrDefault();
                if (RecipeLifecycleProjection.Abandonment(state.Lifecycle, abandon.DraftId) is not null)
                    reason = "RecipeDraftAbandoned";
                else if (source is null || source.Revision != abandon.ExpectedRevision ||
                    source.RevisionContentHash != abandon.ExpectedRevisionContentHash)
                    reason = "RecipeAbandonmentDraftRevisionConflict";
                else if (state.Releases.Any(value => value.Source.DraftId == abandon.DraftId))
                    reason = "RecipeDraftAlreadyUsedByRelease";
                else if (activeToClear is not null || claimRuntimeCommit is not null)
                    reason = "RecipeAbandonmentRuntimeAuthorityInvalid";
            }
            if (reason == "Authorized" && retire is not null)
            {
                release = state.Releases.SingleOrDefault(value => value.Recipe == retire.Recipe &&
                    value.ReleaseId == retire.ReleaseId && value.ContentHash == retire.ReleaseRecordContentHash);
                source = release?.Source;
                if (release is null) reason = "RecipeRetirementExactReleaseMissing";
                else if (RecipeLifecycleProjection.Retirement(state.Lifecycle, retire.Recipe, retire.ReleaseId,
                    retire.ReleaseRecordContentHash) is not null) reason = "RecipeRetired";
                else if (current?.Reference != retire.ExpectedActive) reason = "RecipeRetirementActiveChanged";
                else
                {
                    clearsActive = current?.ReleaseId == retire.ReleaseId;
                    if (clearsActive && (activeToClear != current!.Reference || claimRuntimeCommit is null))
                        reason = "RecipeRetirementQuiescenceAuthorityRequired";
                    else if (!clearsActive && (activeToClear is not null || claimRuntimeCommit is not null))
                        reason = "RecipeRetirementActiveChanged";
                }
            }
            if (reason == "Authorized" && command.Invocation.StepUpGrantId is null) reason = "StepUpRequired";
            if (reason == "Authorized") reason = CheckGrant(command, actor!, lease!.SessionId, reserve: true, out grant);

            var time = _utcNow().ToUniversalTime();
            if (time < identity.LastObservedUtc) time = identity.LastObservedUtc;
            if (source is not null && time < source.RecordedAtUtc) time = source.RecordedAtUtc;
            if (release is not null && time < release.ReleasedAtUtc) time = release.ReleasedAtUtc;
            if (state.Lifecycle.LastOrDefault() is { } previous && time < previous.RecordedAtUtc) time = previous.RecordedAtUtc;
            if (state.Activations.LastOrDefault() is { } previousActivation && time < previousActivation.RecordedAtUtc)
                time = previousActivation.RecordedAtUtc;
            if (state.Selections.LastOrDefault() is { } previousSelection && time < previousSelection.RecordedAtUtc)
                time = previousSelection.RecordedAtUtc;

            RecipeLifecycleRecord? record = null;
            if (reason == "Authorized")
            {
                var impacts = retire is null ? Array.Empty<RecipeRetirementMapImpact>() :
                    RecipeLifecycleProjection.MapImpacts(state.Selections, retire.Recipe,
                        retire.ReleaseId, retire.ReleaseRecordContentHash);
                record = new(state.Lifecycle.Count + 1L, Guid.NewGuid(), command.CorrelationId, epoch, kind,
                    state.Lifecycle.LastOrDefault()?.ContentHash,
                    new(source!.DraftId, source.Revision, source.RevisionContentHash), source.Content.ContentHash,
                    release?.Recipe, release?.ReleaseId, release?.ContentHash, current?.Reference,
                    clearsActive ? current!.Reference : null, impacts, actor!.PrincipalId, lease!.SessionId,
                    actor.AuthorizationRevision, command.Invocation.StepUpGrantId!.Value,
                    new(_options.AuthorizationPolicy.Id, _options.AuthorizationPolicy.Version,
                        _options.AuthorizationPolicy.ContentHash),
                    abandon?.AuthorizationTarget ?? retire!.AuthorizationTarget,
                    abandon?.Reason ?? retire!.Reason, time);
                if (cancellation.IsCancellationRequested) reason = "RecipeLifecycleCancelled";
                else if (clearsActive) reason = claimRuntimeCommit!() ?? "Authorized";
                if (reason == "RecipeRetirementRuntimeBusy")
                {
                    // No audit or lifecycle mutation on Runtime lock contention.
                    // Retry only after this transaction and its identity lease exit.
                    return new(new RecipeLifecycleResult(new(command.CorrelationId, CommandDisposition.Rejected,
                        reason, AuditPersistence.NotAttempted, attempt)), Array.Empty<IdentityAuditEvent>(), NoMutation: true);
                }
                if (reason != "Authorized") record = null;
                else reason = abandon is not null ? "RecipeDraftAbandoned" : "RecipeRetired";
            }

            var accepted = record is not null;
            var fact = new CommandAuditFact(Guid.NewGuid(), attempt, command.CorrelationId, epoch, time,
                CommandKind(command), Enum.IsDefined(command.Invocation.Source) ? command.Invocation.Source : null,
                command.Invocation.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
                command.Invocation.SessionId, command.Invocation.StepUpGrantId, CommandAuditPhase.Outcome,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected, reason, actor?.PrincipalId.ToString("D"));
            var eventKind = !accepted ? IdentityEventKind.ManagementRejected : abandon is not null
                ? IdentityEventKind.RecipeDraftAbandoned : IdentityEventKind.RecipeRetired;
            var authorization = AuthorizationEvent(identity, eventKind, reason, Binding(command), actor?.PrincipalId,
                lease?.SessionId, command.Invocation.StepUpGrantId, command.CorrelationId,
                actor?.AuthorizationRevision ?? 0, capturedTime: time) with
                { OperationId = accepted ? command.CorrelationId : null };
            var result = new RecipeLifecycleResult(new(command.CorrelationId, fact.Disposition!.Value,
                reason, AuditPersistence.Persisted, attempt), record);
            if (!accepted) return new(result, new[] { authorization }, new[] { fact });
            var guard = new AuthorizationCommitGuard(lease!, () =>
            { lock (_grantSync) if (grant is not null) grant.State = GrantState.Consumed; }, () =>
            { lock (_grantSync) if (grant is { State: GrantState.Reserved }) grant.State = GrantState.Active; });
            transferred = true;
            return new(result, new[] { authorization }, new[] { fact }, guard,
                RecipeLifecycle: new RecipeLifecycleMutation(record!));
        }
        finally
        {
            if (!transferred)
            {
                lock (_grantSync) if (grant is { State: GrantState.Reserved }) grant.State = GrantState.Active;
                lease?.Dispose();
            }
        }
    }
}
