using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal ValueTask<RecipeSelectionAccess> GetRecipeSelectionAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken) => QueryAsync(async token =>
        {
            if (!_store.RecipeSelectionEnabled) return new RecipeSelectionAccess(false, "RecipeSelectionConfigurationRequired");
            if (invocation.Source != CommandSource.PhysicalConsole) return new(false, "PhysicalConsoleRequired");
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            if (!TryLease(invocation, out var lease, out var reason)) return new(false, reason);
            using (lease)
            {
                var actor = Find(state, lease!.Identity.PrincipalId);
                return actor is { Enabled: true } && actor.Permissions.Contains(Permission.ManageRecipeSelectionMap)
                    ? new(true, "RecipeSelectionAccessAvailable") : new(false, "PermissionDenied");
            }
        }, reason => new(false, reason), cancellationToken);

    internal async ValueTask<RecipeSelectionChangeResult> ChangeRecipeSelectionAsync(ChangeRecipeSelectionCommand command,
        Guid epoch, RecipeSelectionPreparation preparation, Func<string?> runtimeBlocker, StoreDeadline deadline,
        CancellationToken callerCancellation)
    {
        var attempt = Guid.NewGuid();
        RecipeSelectionChangeResult Unavailable(string reason) => new(new(command.CorrelationId,
            CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, attempt));
        try
        {
            var written = await _store.UpdateRecipeSelectionCommandAsync(command,
                (identity, selections, duplicate) => AuthorizeRecipeSelectionChange(identity, selections, command,
                    epoch, attempt, preparation, runtimeBlocker, duplicate, callerCancellation),
                CancellationToken.None, deadline).ConfigureAwait(false);
            return written.Committed && written.Result is RecipeSelectionChangeResult result
                ? result : Unavailable(written.ReasonCode);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Unavailable("RecipeSelectionAuditUnavailable"); }
    }

    private IdentityUpdate AuthorizeRecipeSelectionChange(IdentityAuthorityState state, RecipeSelectionCommandState selections,
        ChangeRecipeSelectionCommand command, Guid epoch, Guid attempt, RecipeSelectionPreparation preparation,
        Func<string?> runtimeBlocker, bool duplicate, CancellationToken cancellationToken)
    {
        SessionAuthorizationLease? lease = null;
        LocalAdministratorState? actor = null;
        StepUpGrant? grant = null;
        var transferred = false;
        try
        {
            var reason = selections.Enabled ? "Authorized" : "RecipeSelectionConfigurationRequired";
            if (reason == "Authorized" && command.Invocation.Source != CommandSource.PhysicalConsole) reason = "PhysicalConsoleRequired";
            if (reason == "Authorized" && !TryLease(command.Invocation, out lease, out var leaseReason)) reason = leaseReason;
            if (lease is not null) actor = Find(state, lease.Identity.PrincipalId);
            if (reason == "Authorized" && (actor is not { Enabled: true } ||
                !actor.Permissions.Contains(Permission.ManageRecipeSelectionMap))) reason = "PermissionDenied";
            if (reason == "Authorized" && duplicate) reason = "DuplicateCorrelationId";
            if (reason == "Authorized" && epoch == Guid.Empty) reason = "RecipeSelectionRuntimeUnavailable";
            if (reason == "Authorized" && cancellationToken.IsCancellationRequested) reason = "RecipeSelectionChangeCancelled";
            if (reason == "Authorized" && command.ExpectedCurrent != selections.Revisions.LastOrDefault()?.Reference)
                reason = "RecipeSelectionCurrentConflict";
            if (reason == "Authorized" && preparation.Failure is not null) reason = preparation.Failure;
            if (reason == "Authorized" && (preparation.ReleaseHighWatermark is null ||
                preparation.ReleaseHighWatermark != selections.ReleaseHighWatermark)) reason = "RecipeSelectionReleaseSnapshotChanged";
            if (reason == "Authorized" && preparation.PlcContract != selections.PlcContract)
                reason = "RecipeSelectionPlcContractChanged";
            if (reason == "Authorized" && selections.Revisions.Any(value =>
                value.Policy.Id == command.Policy.Id && value.Policy.Version == command.Policy.Version &&
                value.Policy.ContentHash != command.Policy.ContentHash)) reason = "RecipeSelectionPolicyVersionConflict";
            if (reason == "Authorized" && command.Map is { } proposal && selections.Revisions.Any(value =>
                value.Map?.Id == proposal.Id && value.Map.Version == proposal.Version && value.Map.ContentHash != proposal.ContentHash))
                reason = "RecipeSelectionMapVersionConflict";
            if (reason == "Authorized") reason = runtimeBlocker() ?? "Authorized";
            if (reason == "Authorized" && command.Invocation.StepUpGrantId is null) reason = "StepUpRequired";
            if (reason == "Authorized") reason = CheckGrant(command, actor!, lease!.SessionId, true, out grant);
            var time = _utcNow().ToUniversalTime();
            if (time < state.LastObservedUtc) time = state.LastObservedUtc;
            if (selections.Revisions.LastOrDefault() is { } previous && time < previous.RecordedAtUtc) time = previous.RecordedAtUtc;
            RecipeSelectionRevision? revision = null;
            if (reason == "Authorized")
            {
                revision = new(selections.Revisions.Count + 1L, Guid.NewGuid(), command.CorrelationId, command.Policy,
                    command.Map, command.ExpectedCurrent, preparation.ReleaseHighWatermark!.Value, preparation.Validations,
                    actor!.PrincipalId, lease!.SessionId, actor.AuthorizationRevision, command.Invocation.StepUpGrantId!.Value,
                    new(_options.AuthorizationPolicy.Id, _options.AuthorizationPolicy.Version, _options.AuthorizationPolicy.ContentHash),
                    command.ChangeReason, command.AuthorizationTarget, time);
                reason = "RecipeSelectionChanged";
            }
            var accepted = revision is not null;
            var fact = new CommandAuditFact(Guid.NewGuid(), attempt, command.CorrelationId, epoch, time,
                AuditedCommandKind.ChangeRecipeSelection, Enum.IsDefined(command.Invocation.Source) ? command.Invocation.Source : null,
                command.Invocation.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
                command.Invocation.SessionId, command.Invocation.StepUpGrantId, CommandAuditPhase.Outcome,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected, reason, actor?.PrincipalId.ToString("D"));
            var identity = AuthorizationEvent(state, accepted ? IdentityEventKind.RecipeSelectionChanged : IdentityEventKind.ManagementRejected,
                reason, Binding(command), actor?.PrincipalId, lease?.SessionId, command.Invocation.StepUpGrantId,
                command.CorrelationId, actor?.AuthorizationRevision ?? 0, capturedTime: time) with
                { OperationId = accepted ? command.CorrelationId : null };
            var result = new RecipeSelectionChangeResult(new(command.CorrelationId, fact.Disposition!.Value,
                reason, AuditPersistence.Persisted, attempt), revision);
            if (!accepted) return new(result, new[] { identity }, new[] { fact });
            var guard = new AuthorizationCommitGuard(lease!, () =>
            {
                lock (_grantSync) if (grant is not null) grant.State = GrantState.Consumed;
            }, () =>
            {
                lock (_grantSync) if (grant is { State: GrantState.Reserved }) grant.State = GrantState.Active;
            });
            transferred = true;
            return new(result, new[] { identity }, new[] { fact }, guard, RecipeSelection: new(revision!));
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
