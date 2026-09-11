using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal ValueTask<RecipeLifecycleAccess> GetRecipeLifecycleAccessAsync(CommandInvocation invocation,
        RecipeLifecycleKind kind, CancellationToken cancellationToken) => QueryAsync(async token =>
        {
            if (!Enum.IsDefined(kind)) return new RecipeLifecycleAccess(false, "RecipeLifecycleKindInvalid");
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            if (!TryLease(invocation, out var lease, out var reason)) return new(false, reason);
            using (lease)
            {
                var actor = Find(state, lease!.Identity.PrincipalId);
                var permission = kind == RecipeLifecycleKind.DraftAbandoned
                    ? Permission.AbandonRecipeDraft : Permission.RetireRecipe;
                return actor is { Enabled: true } && actor.Permissions.Contains(permission)
                    ? new(true, "RecipeLifecycleAccessAvailable") : new(false, "PermissionDenied");
            }
        }, reason => new(false, reason), cancellationToken);

    /// <summary>
    /// The active-retirement service invokes this before acquiring stopping authority.
    /// This read-only check neither consumes a grant nor replaces the final transaction's
    /// fresh identity/permission/Step-Up check and atomic grant consumption.
    /// </summary>
    internal ValueTask<string?> PrecheckRecipeLifecycleAsync(RuntimeCommand command,
        CancellationToken cancellationToken) => QueryAsync<string?>(async token =>
        {
            if (command is not (AbandonRecipeDraftCommand or RetireReleasedRecipeCommand))
                return "RecipeLifecycleCommandInvalid";
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            if (!TryLease(command.Invocation, out var lease, out var reason)) return reason;
            using (lease)
            {
                var actor = Find(state, lease!.Identity.PrincipalId);
                if (actor is not { Enabled: true } || !actor.Permissions.Contains(RequiredPermission(command)))
                    return "PermissionDenied";
                if (command.Invocation.StepUpGrantId is null) return "StepUpRequired";
                var grantReason = CheckGrant(command, actor, lease.SessionId, reserve: false, out _);
                return grantReason == "Authorized" ? null : grantReason;
            }
        }, reason => reason, cancellationToken);
}
