using SharpInspect.Runtime.Diagnostics;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    // Runtime admits at most one capture or export, including authorizing/draining work.
    // This callback-free stamp can be revoked while the identity writer holds its leases.
    private DiagnosticSupportAuthority? _diagnosticAuthority;
    internal void RevokeDiagnosticAuthority(Guid? principalId = null)
    {
        var stamp = Volatile.Read(ref _diagnosticAuthority);
        if (principalId is null || stamp?.PrincipalId == principalId) stamp?.Revoke();
    }
}
