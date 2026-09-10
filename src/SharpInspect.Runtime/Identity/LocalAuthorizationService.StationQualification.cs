using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal ValueTask<StationQualificationAccess> GetStationQualificationAccessAsync(
        CommandInvocation invocation, CancellationToken cancellationToken = default) => QueryStationQualificationAccessAsync(async token =>
        {
            var stepUp = true;
            if (!_store.StationQualificationEnabled)
                return new StationQualificationAccess(false, "StationQualificationConfigurationRequired", stepUp);
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            if (!TryLease(invocation, out var lease, out var reason))
                return new StationQualificationAccess(false, reason, stepUp);
            using (lease)
            {
                var actor = Find(state, lease!.Identity.PrincipalId);
                var allowed = actor is { Enabled: true } && actor.Permissions.Contains(Permission.RunStationQualification);
                return new StationQualificationAccess(allowed,
                    allowed ? "StationQualificationAccessAvailable" : "PermissionDenied", stepUp);
            }
        }, reason => new StationQualificationAccess(false, reason,
            true), cancellationToken);

    internal ValueTask<StationQualificationAccess> GetStationQualificationOwnerAccessAsync(Guid principalId,
        Guid sessionId, long authorizationRevision, RecipeContractReference authorizationPolicy,
        CancellationToken cancellationToken = default) => QueryStationQualificationAccessAsync(async token =>
        {
            var stepUp = true;
            if (!_store.StationQualificationEnabled)
                return new StationQualificationAccess(false, "StationQualificationConfigurationRequired", stepUp);
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            var reason = "AuthenticationRequired";
            if (_sessions is null || !_sessions.TryAcquireAuthorizationLease(principalId,
                    sessionId, out var lease, out reason))
                return new StationQualificationAccess(false, reason, stepUp);
            using (lease)
            {
                var actor = Find(state, principalId);
                var policy = new RecipeContractReference(_options.AuthorizationPolicy.Id,
                    _options.AuthorizationPolicy.Version, _options.AuthorizationPolicy.ContentHash);
                var allowed = actor is { Enabled: true } && actor.Permissions.Contains(Permission.RunStationQualification) &&
                    actor.AuthorizationRevision == authorizationRevision && policy == authorizationPolicy;
                return new StationQualificationAccess(allowed,
                    allowed ? "StationQualificationOwnerAvailable" : "StationQualificationAuthorityChanged", stepUp);
            }
        }, reason => new StationQualificationAccess(false, reason,
            true), cancellationToken);

    private async ValueTask<StationQualificationAccess> QueryStationQualificationAccessAsync(
        Func<CancellationToken, ValueTask<StationQualificationAccess>> query,
        Func<string, StationQualificationAccess> unavailable, CancellationToken cancellationToken)
    {
        try { return await QueryAsync(query, unavailable, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return unavailable("StationQualificationAuthorizationUnavailable"); }
    }
}
