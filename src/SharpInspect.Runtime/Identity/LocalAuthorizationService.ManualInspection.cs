using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal ValueTask<ManualInspectionAccess> GetManualInspectionAccessAsync(
        CommandInvocation invocation, CancellationToken cancellationToken = default) => QueryManualAccessAsync(async token =>
        {
            var stepUp = _options.AuthorizationPolicy.RequiresStepUp(Permission.RunManualInspection);
            if (_store.ManualInspectionOptions is null)
                return new ManualInspectionAccess(false, "ManualInspectionConfigurationRequired", stepUp);
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            if (!TryLease(invocation, out var lease, out var reason))
                return new ManualInspectionAccess(false, reason, stepUp);
            using (lease)
            {
                var actor = Find(state, lease!.Identity.PrincipalId);
                var allowed = actor is { Enabled: true } && actor.Permissions.Contains(Permission.RunManualInspection);
                return new ManualInspectionAccess(allowed,
                    allowed ? "ManualInspectionAccessAvailable" : "PermissionDenied", stepUp);
            }
        }, reason => new ManualInspectionAccess(false, reason,
            _options.AuthorizationPolicy.RequiresStepUp(Permission.RunManualInspection)), cancellationToken);

    internal ValueTask<ManualInspectionAccess> GetManualInspectionOwnerAccessAsync(Guid principalId,
        Guid sessionId, long authorizationRevision, RecipeContractReference authorizationPolicy,
        CancellationToken cancellationToken = default) => QueryManualAccessAsync(async token =>
        {
            var stepUp = _options.AuthorizationPolicy.RequiresStepUp(Permission.RunManualInspection);
            if (_store.ManualInspectionOptions is null)
                return new ManualInspectionAccess(false, "ManualInspectionConfigurationRequired", stepUp);
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            var reason = "AuthenticationRequired";
            if (_sessions is null || !_sessions.TryAcquireAuthorizationLease(principalId,
                    sessionId, out var lease, out reason))
                return new ManualInspectionAccess(false, reason, stepUp);
            using (lease)
            {
                var actor = Find(state, principalId);
                var policy = new RecipeContractReference(_options.AuthorizationPolicy.Id,
                    _options.AuthorizationPolicy.Version, _options.AuthorizationPolicy.ContentHash);
                var allowed = actor is { Enabled: true } && actor.Permissions.Contains(Permission.RunManualInspection) &&
                    actor.AuthorizationRevision == authorizationRevision && policy == authorizationPolicy;
                return new ManualInspectionAccess(allowed,
                    allowed ? "ManualInspectionOwnerAvailable" : "ManualInspectionAuthorityChanged", stepUp);
            }
        }, reason => new ManualInspectionAccess(false, reason,
            _options.AuthorizationPolicy.RequiresStepUp(Permission.RunManualInspection)), cancellationToken);

    private async ValueTask<ManualInspectionAccess> QueryManualAccessAsync(
        Func<CancellationToken, ValueTask<ManualInspectionAccess>> query,
        Func<string, ManualInspectionAccess> unavailable, CancellationToken cancellationToken)
    {
        try { return await QueryAsync(query, unavailable, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return unavailable("ManualInspectionAuthorizationUnavailable"); }
    }
}
