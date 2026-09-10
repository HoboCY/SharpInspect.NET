using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal DateTimeOffset RecipeTransferUtcNow => _utcNow().ToUniversalTime();

    internal async ValueTask<RecipeTransferAccess> GetRecipeTransferAccessAsync(CommandInvocation invocation,
        Permission permission, CancellationToken cancellationToken)
    {
        if (!_store.RecipeTransferEnabled || permission is not (Permission.ImportRecipe or Permission.ExportRecipe or
                Permission.ManageRecipeTrustStore or Permission.ManageRecipeSigningKeys))
            return new(false, "RecipeTransferUnavailable", false);
        var stepUp = permission is Permission.ManageRecipeTrustStore or Permission.ManageRecipeSigningKeys ||
            _options.AuthorizationPolicy.RequiresStepUp(permission);
        try
        {
            var state = await _store.ReadIdentityAsync(cancellationToken).ConfigureAwait(false);
            if (!TryLease(invocation, out var lease, out var reason)) return new(false, reason, stepUp);
            using (lease)
            {
                var actor = Find(state, lease!.Identity.PrincipalId);
                return actor is { Enabled: true } && actor.Permissions.Contains(permission)
                    ? new(true, "RecipeTransferAccessAvailable", stepUp) : new(false, "PermissionDenied", stepUp);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, "RecipeTransferAuthorizationUnavailable", stepUp); }
    }

    internal async ValueTask<RecipeTransferResult> ExecuteRecipeTransferAsync(RecipeTransferCommand command,
        RecipeTransferPreparedOperation preparation, string? forcedRejection, Guid epoch,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        var attempt = Guid.NewGuid();
        try
        {
            return await _store.ExecuteRecipeTransferAsync(command, preparation, (state, transfer, duplicate) =>
                AuthorizeRecipeTransfer(state, command, duplicate, forcedRejection, epoch, attempt, cancellationToken),
                deadline, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(new(command.CorrelationId, CommandDisposition.Rejected, "RecipeTransferAuditUnavailable", AuditPersistence.Unavailable, attempt)); }
    }

    private RecipeTransferEvaluation AuthorizeRecipeTransfer(IdentityAuthorityState state,
        RecipeTransferCommand command, bool duplicate, string? forced, Guid epoch, Guid attempt,
        CancellationToken cancellationToken)
    {
        SessionAuthorizationLease? lease = null;
        StepUpGrant? reserved = null;
        var transferred = false;
        try
        {
            var now = RecipeTransferUtcNow;
            if (now < state.LastObservedUtc) now = state.LastObservedUtc;
            var reason = TryLease(command.Invocation, out lease, out var leaseReason) ? "Authorized" : leaseReason;
            var actor = lease is null ? null : Find(state, lease.Identity.PrincipalId);
            if (reason == "Authorized" && (actor is not { Enabled: true } || !actor.Permissions.Contains(RequiredPermission(command))))
                reason = "PermissionDenied";
            if (reason == "Authorized" && forced is not null) reason = forced;
            if (reason == "Authorized" && cancellationToken.IsCancellationRequested) reason = "RecipeTransferCancelled";
            // Exact replay is additionally checked against persisted command/actor/package facts by the writer.
            if (reason == "Authorized" && !duplicate) reason = CheckGrant(command, actor!, lease!.SessionId, true, out reserved);
            var accepted = reason == "Authorized";
            var binding = Binding(command);
            var acceptedReason = duplicate ? "RecipeTransferReplayAuthorized" : "RecipeTransferAuthorized";
            var fact = new CommandAuditFact(Guid.NewGuid(), attempt, command.CorrelationId, epoch, now,
                CommandKind(command), Enum.IsDefined(command.Invocation.Source) ? command.Invocation.Source : null,
                command.Invocation.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
                command.Invocation.SessionId, command.Invocation.StepUpGrantId, CommandAuditPhase.Outcome,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected,
                accepted ? acceptedReason : reason, actor?.PrincipalId.ToString("D"));
            var identity = AuthorizationEvent(state, accepted ? IdentityEventKind.RecipeTransferAuthorized : IdentityEventKind.ManagementRejected,
                accepted ? acceptedReason : reason, binding, actor?.PrincipalId, lease?.SessionId,
                command.Invocation.StepUpGrantId, command.CorrelationId, actor?.AuthorizationRevision ?? 0, capturedTime: now)
                with { OperationId = accepted ? command.CorrelationId : null };
            var result = new RecipeTransferResult(new(command.CorrelationId, fact.Disposition!.Value,
                fact.ReasonCode, AuditPersistence.Persisted, attempt));
            if (!accepted) return new(result, new[] { identity }, new[] { fact }, null, null);
            var guard = new AuthorizationCommitGuard(lease!, () =>
            {
                lock (_grantSync) if (reserved is not null) reserved.State = GrantState.Consumed;
            }, () =>
            {
                lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
            });
            transferred = true;
            return new(result, duplicate ? Array.Empty<IdentityAuditEvent>() : new[] { identity },
                duplicate ? null : new[] { fact }, guard,
                new RecipeTransferVerifiedActor(actor!.PrincipalId, lease!.SessionId, actor.AuthorizationRevision, now));
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
