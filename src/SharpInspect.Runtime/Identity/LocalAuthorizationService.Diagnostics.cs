using SharpInspect.Abstractions;
using SharpInspect.Runtime.Diagnostics;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal ValueTask<DiagnosticHistoryPage> ReadDiagnosticHistoryAsync(DiagnosticHistoryRequest request,
        Func<CancellationToken, ValueTask<DiagnosticHistoryPage>> read, CancellationToken cancellationToken) =>
        QueryAsync(async token =>
        {
            try
            {
                var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
                if (!TryLease(request.Invocation, out var lease, out var reason)) return RuntimeDiagnosticService.Unavailable(reason);
                Guid principalId; long revision;
                bool Allowed(LocalAdministratorState? account) => account is { Enabled: true } &&
                    account.Permissions.Contains(Permission.RunDiagnostics) &&
                    (!request.Protected || account.Permissions.Contains(Permission.ExportProtectedDiagnostics));
                using (lease)
                {
                    var actor = Find(state, lease!.Identity.PrincipalId);
                    if (!Allowed(actor)) return RuntimeDiagnosticService.Unavailable("PermissionDenied");
                    principalId = actor!.PrincipalId; revision = actor.AuthorizationRevision;
                }
                // Session leases own a thread-affine monitor. They never span an await or IO.
                var page = await read(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
                if (!TryLease(request.Invocation, out var finalLease, out reason))
                    return RuntimeDiagnosticService.Unavailable("DiagnosticQueryAuthorizationChanged");
                using (finalLease)
                {
                    var current = Find(state, principalId);
                    if (!Allowed(current) || current!.AuthorizationRevision != revision ||
                        finalLease!.Identity.PrincipalId != principalId)
                        return RuntimeDiagnosticService.Unavailable("DiagnosticQueryAuthorizationChanged");
                    return page;
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { return RuntimeDiagnosticService.Unavailable("AuthorizationUnavailable"); }
        }, RuntimeDiagnosticService.Unavailable, cancellationToken);
}
