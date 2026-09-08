using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Storage;
using System.Runtime.CompilerServices;

namespace SharpInspect.Runtime.Identity;

/// <summary>
/// Authorization boundary used by the camera setup coordinator.  The reservation
/// keeps only the exact Step-Up grant metadata; the real session lease is acquired
/// and released synchronously by the durable writer callback.
/// </summary>
internal sealed partial class LocalAuthorizationService : ICameraSetupAuthorizer
{
    private static readonly ConditionalWeakTable<CameraSetupAuthorizationReservation,
        CameraSetupAuthorizationContext> CameraAuthorizationContexts = new();

    public async ValueTask<CameraSetupAuthorization> AuthorizeCameraSetupAsync(
        CommandInvocation invocation, bool readOnly, Guid operationId, string targetId,
        AuditedCommandKind commandKind, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || _sessions is null)
            return CameraSetupAuthorization.Denied("AuthorizationUnavailable");
        if (invocation is null || !Enum.IsDefined(invocation.Source) ||
            !Guid.TryParseExact(invocation.PrincipalId, "D", out var claimedPrincipal) ||
            claimedPrincipal == Guid.Empty || invocation.SessionId is not { } claimedSession ||
            claimedSession == Guid.Empty)
            return CameraSetupAuthorization.Denied("InvalidCommandContext");
        if (!readOnly && (operationId == Guid.Empty ||
            commandKind is not (AuditedCommandKind.RebindCamera or
                AuditedCommandKind.ApplyCameraDebugConfiguration or
                AuditedCommandKind.ChangeCameraNetworkConfiguration or
                AuditedCommandKind.DeclareImagingSetup)))
            return CameraSetupAuthorization.Denied("CameraOperationIdRequired");
        // Discovery is a provider-scoped read and intentionally has no camera
        // role/target.  Setup reads and both mutations still require the exact
        // logical role so a caller cannot broaden an authorization binding.
        if ((!readOnly && !ValidCameraTarget(targetId)) ||
            (readOnly && targetId is { Length: > 0 } && !ValidCameraTarget(targetId)))
            return CameraSetupAuthorization.Denied("CameraLogicalRoleInvalid");

        SessionAuthorizationLease? lease = null;
        StepUpGrant? reserved = null;
        var transferred = false;
        try
        {
            var state = await _store.ReadIdentityAsync(cancellationToken).ConfigureAwait(false);
            if (readOnly)
            {
                // Read-only setup queries retain the existing short synchronous lease
                // boundary.  It is acquired and released on this continuation without
                // any await in between.
                if (!TryLease(invocation, out lease, out var leaseReason))
                    return CameraSetupAuthorization.Denied(leaseReason);
                if (lease!.SessionId != claimedSession || lease.Identity.PrincipalId != claimedPrincipal)
                    return CameraSetupAuthorization.Denied("SessionInvalid");
            }
            else if (!IsCurrentSession(claimedPrincipal, claimedSession, out var sessionReason))
            {
                return CameraSetupAuthorization.Denied(sessionReason);
            }

            var actor = Find(state, claimedPrincipal);
            if (actor is not { Enabled: true } ||
                !actor.Permissions.Contains(Permission.ManageCameraBindings))
                return CameraSetupAuthorization.Denied("PermissionDenied");

            if (readOnly)
                return new CameraSetupAuthorization(true, "Authorized", actor.PrincipalId,
                    claimedSession, actor.AuthorizationRevision, null);

            if (invocation.StepUpGrantId is not { } grantId || grantId == Guid.Empty)
                return CameraSetupAuthorization.Denied("StepUpRequired");
            var binding = new StepUpBinding(Permission.ManageCameraBindings, operationId,
                targetId, commandKind);
            lock (_grantSync)
            {
                PurgeGrantsLocked();
                if (!_grants.TryGetValue(grantId, out var candidate) ||
                    candidate.State != GrantState.Active || candidate.PrincipalId != actor.PrincipalId ||
                    candidate.SessionId != claimedSession || candidate.CredentialId != actor.CredentialId ||
                    candidate.AuthorizationRevision != actor.AuthorizationRevision ||
                    candidate.PolicyHash != _options.AuthorizationPolicy.ContentHash ||
                    candidate.Binding != binding)
                    return CameraSetupAuthorization.Denied("StepUpInvalid");
                candidate.State = GrantState.Reserved;
                reserved = candidate;
            }

            CameraSetupAuthorizationContext? context = null;
            var reservation = CameraSetupAuthorizationReservation.Create(
                () =>
                {
                    lock (_grantSync) if (reserved is not null) reserved.State = GrantState.Consumed;
                    if (context is not null) context.MarkAdmissionCommitted();
                },
                () =>
                {
                    lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
                });
            context = new CameraSetupAuthorizationContext(this,
                claimedPrincipal, claimedSession, operationId, targetId, commandKind,
                grantId, actor.CredentialId, actor.AuthorizationRevision);
            CameraAuthorizationContexts.Add(reservation, context);
            transferred = true;
            return new CameraSetupAuthorization(true, "Authorized", actor.PrincipalId,
                claimedSession, actor.AuthorizationRevision, reservation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return CameraSetupAuthorization.Denied("AuthorizationUnavailable"); }
        finally
        {
            if (!transferred)
            {
                if (reserved is { State: GrantState.Reserved })
                    lock (_grantSync) reserved.State = GrantState.Active;
                lease?.Dispose();
            }
        }
    }

    /// <summary>
    /// Revalidates camera authorization inside the single-writer transaction.  The
    /// session monitor is acquired and released by the writer callback's thread;
    /// this method never stores a session lease in an async reservation.
    /// </summary>
    internal static IIdentityTransactionGuard? AcquireCameraSetupCommitGuard(
        CameraSetupAuthorizationReservation reservation, IdentityAuthorityState state,
        bool requireActiveGrant, out string reason)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(state);
        if (!CameraAuthorizationContexts.TryGetValue(reservation, out var context))
        {
            reason = "AuthorizationReservationUnavailable";
            return null;
        }
        return context.Owner.AcquireCameraSetupCommitGuard(context, state,
            requireActiveGrant, out reason);
    }

    /// <summary>
    /// Checks the immutable command binding captured by the authorization
    /// reservation.  Network maintenance carries a complete target hash rather
    /// than a logical camera role, so the durable writer must compare the
    /// request with the exact Step-Up reservation before it writes admission.
    /// </summary>
    internal static bool MatchesCameraSetupAuthorization(
        CameraSetupAuthorizationReservation reservation, Guid operationId,
        string targetId, AuditedCommandKind commandKind)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (operationId == Guid.Empty || string.IsNullOrEmpty(targetId)) return false;
        return CameraAuthorizationContexts.TryGetValue(reservation, out var context) &&
            context.OperationId == operationId && context.TargetId == targetId &&
            context.CommandKind == commandKind;
    }

    private IIdentityTransactionGuard? AcquireCameraSetupCommitGuard(
        CameraSetupAuthorizationContext context, IdentityAuthorityState state,
        bool requireActiveGrant, out string reason)
    {
        reason = "AuthorizationUnavailable";
        if (Volatile.Read(ref _disposed) != 0 || _sessions is null)
        {
            reason = "AuthorizationUnavailable";
            return null;
        }

        var actor = Find(state, context.PrincipalId);
        if (actor is not { Enabled: true } ||
            !actor.Permissions.Contains(Permission.ManageCameraBindings))
        {
            reason = "PermissionDenied";
            return null;
        }
        if (actor.AuthorizationRevision != context.AuthorizationRevision ||
            actor.CredentialId != context.CredentialId)
        {
            reason = "AuthorizationStale";
            return null;
        }

        SessionAuthorizationLease? lease = null;
        if (!_sessions.TryAcquireAuthorizationLease(context.PrincipalId, context.SessionId,
                out lease, out reason))
            return null;
        if (lease is null)
        {
            reason = "AuthorizationLeaseUnavailable";
            return null;
        }

        try
        {
            if (lease.Identity.PrincipalId != context.PrincipalId || lease.SessionId != context.SessionId)
            {
                reason = "SessionMismatch";
                return null;
            }

            StepUpGrant? grant = null;
            if (requireActiveGrant)
            {
                lock (_grantSync)
                {
                    PurgeGrantsLocked();
                    if (!_grants.TryGetValue(context.GrantId, out grant) ||
                        grant.State != GrantState.Reserved ||
                        grant.PrincipalId != context.PrincipalId ||
                        grant.SessionId != context.SessionId ||
                        grant.CredentialId != context.CredentialId ||
                        grant.AuthorizationRevision != context.AuthorizationRevision ||
                        grant.PolicyHash != _options.AuthorizationPolicy.ContentHash ||
                        grant.Binding != new StepUpBinding(Permission.ManageCameraBindings,
                            context.OperationId, context.TargetId, context.CommandKind))
                    {
                        reason = "StepUpInvalid";
                        return null;
                    }
                }
            }
            else
            {
                // Admission consumption is a durable fact.  The in-memory grant
                // may be purged while hardware work is in flight, so terminal
                // authorization relies on the reservation's one-way commit mark
                // plus the writer's fresh actor/session checks above.
                if (!context.IsAdmissionCommitted)
                {
                    reason = "StepUpInvalid";
                    return null;
                }
            }

            var guard = new AuthorizationCommitGuard(lease, () =>
            {
                if (!requireActiveGrant) return;
                context.MarkAdmissionCommitted();
                lock (_grantSync)
                {
                    if (grant is { State: GrantState.Reserved }) grant.State = GrantState.Consumed;
                }
            }, () =>
            {
                if (!requireActiveGrant) return;
                lock (_grantSync)
                {
                    if (grant is { State: GrantState.Reserved }) grant.State = GrantState.Active;
                }
            });
            lease = null;
            reason = "Authorized";
            return guard;
        }
        finally
        {
            // Every unsuccessful path still disposes the lease on the writer thread.
            lease?.Dispose();
        }
    }

    private bool IsCurrentSession(Guid principalId, Guid sessionId, out string reason)
    {
        reason = "SessionMismatch";
        if (_sessions is null) return false;
        var current = _sessions.Current;
        if (current.State != InteractiveSessionState.Authenticated ||
            current.SessionId != sessionId ||
            !string.Equals(current.PrincipalId, principalId.ToString("D"), StringComparison.Ordinal))
            return false;
        reason = "Authorized";
        return true;
    }

    private static bool ValidCameraTarget(string value) => value is { Length: > 0 and <= 64 } &&
        value.All(character => character is >= 'A' and <= 'Z' || character is >= 'a' and <= 'z' ||
            character is >= '0' and <= '9' || character is '.' or '_' or '-');

    private sealed class CameraSetupAuthorizationContext
    {
        internal CameraSetupAuthorizationContext(LocalAuthorizationService owner, Guid principalId,
            Guid sessionId, Guid operationId, string targetId, AuditedCommandKind commandKind,
            Guid grantId, Guid credentialId, long authorizationRevision)
        {
            Owner = owner;
            PrincipalId = principalId;
            SessionId = sessionId;
            OperationId = operationId;
            TargetId = targetId;
            CommandKind = commandKind;
            GrantId = grantId;
            CredentialId = credentialId;
            AuthorizationRevision = authorizationRevision;
        }

        internal LocalAuthorizationService Owner { get; }
        internal Guid PrincipalId { get; }
        internal Guid SessionId { get; }
        internal Guid OperationId { get; }
        internal string TargetId { get; }
        internal AuditedCommandKind CommandKind { get; }
        internal Guid GrantId { get; }
        internal Guid CredentialId { get; }
        internal long AuthorizationRevision { get; }
        private int _admissionCommitted;

        internal bool IsAdmissionCommitted => Volatile.Read(ref _admissionCommitted) != 0;
        internal void MarkAdmissionCommitted() => Volatile.Write(ref _admissionCommitted, 1);
    }
}
