using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal ValueTask<RecipeReleaseAccess> GetRecipeReleaseAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken) => QueryAsync(async token =>
        {
            var policy = _store.RecipeReleaseOptions?.Policy;
            if (policy is null) return new RecipeReleaseAccess(false, "RecipeReleaseConfigurationRequired", null);
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            if (!TryLease(invocation, out var lease, out var reason)) return new(false, reason, policy);
            using (lease)
            {
                var actor = Find(state, lease!.Identity.PrincipalId);
                return actor is { Enabled: true } && actor.Permissions.Contains(Permission.ReleaseRecipe)
                    ? new(true, "RecipeReleaseAccessAvailable", policy) : new(false, "PermissionDenied", policy);
            }
        }, reason => new(false, reason, _store.RecipeReleaseOptions?.Policy), cancellationToken);

    internal async ValueTask<RecipeReleaseResult> ReleaseRecipeAsync(ReleaseRecipeCommand command,
        Guid epoch, RecipeReleasePreparation preparation, StoreDeadline deadline, CancellationToken callerCancellation)
    {
        var attempt = Guid.NewGuid();
        RecipeReleaseResult Unavailable(string reason) => new(new(command.CorrelationId,
            CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, attempt));
        try
        {
            // Cancellation is evaluated and audited in the transaction. Once admitted to
            // the writer, its completion is the commit truth, including a late cancellation.
            var committed = await _store.UpdateRecipeReleaseCommandAsync(command,
                (state, release, duplicate) => AuthorizeRecipeRelease(state, release, command,
                    epoch, attempt, preparation, duplicate, callerCancellation),
                CancellationToken.None, deadline).ConfigureAwait(false);
            return committed.Committed && committed.Result is RecipeReleaseResult result
                ? result : Unavailable(committed.ReasonCode);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Unavailable("RecipeReleaseAuditUnavailable"); }
    }

    private IdentityUpdate AuthorizeRecipeRelease(IdentityAuthorityState state, RecipeReleaseCommandState release,
        ReleaseRecipeCommand command, Guid epoch, Guid attempt, RecipeReleasePreparation preparation,
        bool duplicate, CancellationToken callerCancellation)
    {
        SessionAuthorizationLease? lease = null;
        LocalAdministratorState? actor = null;
        StepUpGrant? reserved = null;
        var transferred = false;
        try
        {
            var options = _store.RecipeReleaseOptions;
            var reason = release.Enabled && options is not null ? "Authorized" : "RecipeReleaseConfigurationRequired";
            if (reason == "Authorized" && !TryLease(command.Invocation, out lease, out var leaseReason)) reason = leaseReason;
            if (lease is not null) actor = Find(state, lease.Identity.PrincipalId);
            if (reason == "Authorized" && (actor is not { Enabled: true } || !actor.Permissions.Contains(Permission.ReleaseRecipe)))
                reason = "PermissionDenied";
            if (reason == "Authorized" && duplicate) reason = "DuplicateCorrelationId";
            if (reason == "Authorized" && epoch == Guid.Empty) reason = "RecipeReleaseRuntimeUnavailable";
            if (reason == "Authorized" && callerCancellation.IsCancellationRequested) reason = "RecipeReleaseCancelled";
            if (reason == "Authorized" && RecipeLifecycleProjection.Abandonment(
                release.Lifecycle ?? Array.Empty<RecipeLifecycleRecord>(), command.DraftId) is not null)
                reason = "RecipeDraftAbandoned";
            if (reason == "Authorized" && command.GovernancePolicy != options!.Policy.Reference)
                reason = "RecipeReleaseGovernancePolicyMismatch";
            var source = release.DraftHistory.Where(value => value.DraftId == command.DraftId)
                .OrderByDescending(value => value.Revision).FirstOrDefault();
            if (reason == "Authorized" && (source is null || source.Revision != command.ExpectedRevision ||
                    source.RevisionContentHash != command.ExpectedRevisionContentHash))
                reason = "RecipeReleaseDraftRevisionConflict";
            if (reason == "Authorized" && release.Releases.Any(value => value.Source.DraftId == command.DraftId &&
                    value.Source.Revision == command.ExpectedRevision))
                reason = "RecipeReleaseDraftAlreadyReleased";
            if (reason == "Authorized" && preparation.Failure is not null) reason = preparation.Failure;
            if (reason == "Authorized" && preparation.ValidatedSource != new RecipeDraftRevisionReference(
                    source!.DraftId, source.Revision, source.RevisionContentHash))
                reason = "RecipeReleaseValidationSourceMismatch";

            IReadOnlyList<RecipeReleaseChange> changes = Array.Empty<RecipeReleaseChange>();
            IReadOnlyList<RecipeReleaseValidationCheck> checks = Array.Empty<RecipeReleaseValidationCheck>();
            if (reason == "Authorized")
            {
                try
                {
                    changes = RecipeReleaseProjection.Contributions(release.DraftHistory, source!);
                    checks = RecipeReleaseProjection.ValidationChecks(source!.Content).Concat(
                        RecipeReleaseProjection.ValidateDependencies(source.Content, options!, _store.RecipeDraftOptions!,
                            release.CalibrationPolicies)).ToArray();
                    reason = checks.FirstOrDefault(value => !value.Passed)?.ReasonCode ?? "Authorized";
                    if (reason == "Authorized" && options!.Policy.Mode == RecipeGovernanceMode.MakerCheckerRelease &&
                        changes.Any(value => value.AuthorPrincipalId == actor!.PrincipalId))
                        reason = "RecipeReleaseMakerCheckerConflict";
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { reason = "RecipeReleaseEvidenceUnavailable"; }
            }
            if (reason == "Authorized" && command.Invocation.StepUpGrantId is null) reason = "StepUpRequired";
            if (reason == "Authorized") reason = CheckGrant(command, actor!, lease!.SessionId, reserve: true, out reserved);

            var time = _utcNow().ToUniversalTime();
            if (time < state.LastObservedUtc) time = state.LastObservedUtc;
            if (source is not null && time < source.RecordedAtUtc) time = source.RecordedAtUtc;
            if (release.Releases.LastOrDefault() is { } previous && time < previous.ReleasedAtUtc)
                time = previous.ReleasedAtUtc;
            RecipeReleaseRecord? record = null;
            if (reason == "Authorized")
            {
                var version = checked(release.Releases.Where(value => value.Source.Content.RecipeKey == source!.Content.RecipeKey)
                    .Select(value => value.RecipeVersion).DefaultIfEmpty(0).Max() + 1);
                record = new(release.Releases.Count + 1L, Guid.NewGuid(), command.CorrelationId, version, source!,
                    options!.Policy, checks, changes, actor!.PrincipalId, lease!.SessionId, actor.AuthorizationRevision,
                    command.Invocation.StepUpGrantId!.Value, new(_options.AuthorizationPolicy.Id,
                        _options.AuthorizationPolicy.Version, _options.AuthorizationPolicy.ContentHash),
                    command.ReleaseReason, command.AuthorizationTarget, time);
                reason = "RecipeReleased";
            }
            var accepted = record is not null;
            var fact = new CommandAuditFact(Guid.NewGuid(), attempt, command.CorrelationId, epoch, time,
                AuditedCommandKind.ReleaseRecipe, Enum.IsDefined(command.Invocation.Source) ? command.Invocation.Source : null,
                command.Invocation.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
                command.Invocation.SessionId, command.Invocation.StepUpGrantId, CommandAuditPhase.Outcome,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected, reason, actor?.PrincipalId.ToString("D"));
            var binding = Binding(command);
            var identity = AuthorizationEvent(state, accepted ? IdentityEventKind.RecipeReleased : IdentityEventKind.ManagementRejected,
                reason, binding, actor?.PrincipalId, lease?.SessionId, command.Invocation.StepUpGrantId, command.CorrelationId,
                actor?.AuthorizationRevision ?? 0, capturedTime: time) with { OperationId = accepted ? command.CorrelationId : null };
            var outcome = new RuntimeCommandOutcome(command.CorrelationId, fact.Disposition!.Value, reason, AuditPersistence.Persisted, attempt);
            var result = new RecipeReleaseResult(outcome, record is null ? null : new ReleasedRecipe(record));
            if (!accepted) return new IdentityUpdate(result, new[] { identity }, new[] { fact });
            var guard = new AuthorizationCommitGuard(lease!, () =>
            {
                lock (_grantSync) if (reserved is not null) reserved.State = GrantState.Consumed;
            }, () =>
            {
                lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
            });
            transferred = true;
            return new IdentityUpdate(result, new[] { identity }, new[] { fact }, guard,
                RecipeRelease: new RecipeReleaseMutation(record!));
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
}

internal sealed record RecipeReleasePreparation(RecipeDraftRevisionReference? ValidatedSource, string? Failure);
