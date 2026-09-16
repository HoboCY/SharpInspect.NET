using SharpInspect.Abstractions;
using SharpInspect.Runtime.Diagnostics;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed record DiagnosticSupportAuthorization(RuntimeCommandOutcome Outcome, DiagnosticSupportAuthority? Authority,
    Guid CommandEventId, Guid AuthorizationEventId, DateTimeOffset ObservedAtUtc);

internal sealed partial class LocalAuthorizationService
{
    internal async ValueTask<DiagnosticSupportAuthorization> AuthorizeDiagnosticSupportAsync(DiagnosticSupportCommand command,
        Guid epoch, DiagnosticOperationMutation? mutation, string? forcedRejection, StoreDeadline deadline,
        CancellationToken cancellationToken)
    {
        var attempt = Guid.NewGuid();
        DiagnosticSupportAuthorization Unavailable(string reason) => new(new(command.CorrelationId,
            CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, attempt), null, Guid.Empty, Guid.Empty, _utcNow());
        if (!_store.DiagnosticSupportEnabled) return Unavailable("DiagnosticSupportConfigurationRequired");
        DiagnosticSupportAuthorization? decision = null;
        try
        {
            var written = await _store.UpdateIdentityCommandAsync(command.CorrelationId, (identity, duplicate) =>
            {
                SessionAuthorizationLease? lease = null; StepUpGrant? reserved = null; var transferred = false;
                try
                {
                    var reason = TryLease(command.Invocation, out lease, out var leaseReason) ? "Authorized" : leaseReason;
                    var actor = lease is null ? null : Find(identity, lease.Identity.PrincipalId);
                    if (reason == "Authorized" && (actor is not { Enabled: true } ||
                        !actor.Permissions.Contains(RequiredPermission(command)))) reason = "PermissionDenied";
                    if (reason == "Authorized" && duplicate) reason = "DuplicateCorrelationId";
                    if (reason == "Authorized" && forcedRejection is not null) reason = forcedRejection;
                    if (reason == "Authorized" && cancellationToken.IsCancellationRequested) reason = "DiagnosticSupportCancelled";
                    if (reason == "Authorized") reason = CheckGrant(command, actor!, lease!.SessionId, false, out _);
                    if (reason == "Authorized" && command is not StopDiagnosticCaptureCommand && mutation is null)
                        reason = "DiagnosticSupportScopeMissing";
                    if (reason == "Authorized") reason = CheckGrant(command, actor!, lease!.SessionId, true, out reserved);
                    var accepted = reason == "Authorized";
                    var effective = accepted ? mutation?.ReasonCode ?? "DiagnosticCaptureStopAuthorized" : reason;
                    var now = _utcNow().ToUniversalTime(); if (now < identity.LastObservedUtc) now = identity.LastObservedUtc;
                    var auth = AuthorizationEvent(identity, accepted ? IdentityEventKind.DiagnosticOperationAuthorized :
                        IdentityEventKind.ManagementRejected, effective, Binding(command), actor?.PrincipalId, lease?.SessionId,
                        command.Invocation.StepUpGrantId, command.CorrelationId, actor?.AuthorizationRevision ?? 0, capturedTime: now)
                        with { OperationId = command.OperationId };
                    var fact = new CommandAuditFact(Guid.NewGuid(), attempt, command.CorrelationId, epoch, now, CommandKind(command),
                        Enum.IsDefined(command.Invocation.Source) ? command.Invocation.Source : null,
                        command.Invocation.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
                        command.Invocation.SessionId, command.Invocation.StepUpGrantId, CommandAuditPhase.Outcome,
                        accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected, effective, actor?.PrincipalId.ToString("D"));
                    var stamp = accepted && command is not StopDiagnosticCaptureCommand ?
                        new DiagnosticSupportAuthority(actor!.PrincipalId, lease!.SessionId, actor.CredentialId, actor.AuthorizationRevision) : null;
                    decision = new(new(command.CorrelationId, accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected,
                        effective, AuditPersistence.Persisted, attempt), stamp, fact.EventId, auth.EventId, now);
                    if (!accepted) return new IdentityUpdate(decision, new[] { auth }, new[] { fact });
                    var guard = new AuthorizationCommitGuard(lease!, () =>
                    {
                        lock (_grantSync) if (reserved is not null) reserved.State = GrantState.Consumed;
                        if (stamp is not null) Interlocked.Exchange(ref _diagnosticAuthority, stamp)?.Revoke();
                    }, () =>
                    {
                        stamp?.Revoke();
                        lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
                    });
                    transferred = true;
                    return new IdentityUpdate(decision, new[] { auth }, new[] { fact }, guard, DiagnosticOperation: mutation);
                }
                finally
                {
                    if (!transferred)
                    {
                        lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
                        lease?.Dispose();
                    }
                }
            }, cancellationToken, deadline).ConfigureAwait(false);
            if (written.Committed && decision is not null) return decision;
            decision?.Authority?.Revoke(); return Unavailable(written.ReasonCode);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { decision?.Authority?.Revoke(); return Unavailable("DiagnosticSupportAuditUnavailable"); }
    }

    internal async ValueTask<bool> CheckDiagnosticAuthorityAsync(DiagnosticSupportAuthority authority,
        Permission permission, CancellationToken token)
    {
        if (!authority.Valid || Volatile.Read(ref _disposed) != 0) return false;
        try
        {
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            var actor = Find(state, authority.PrincipalId);
            var invocation = new CommandInvocation(CommandSource.PhysicalConsole, authority.PrincipalId.ToString("D"), authority.SessionId);
            if (!TryLease(invocation, out var lease, out _)) { authority.Revoke(); return false; }
            using (lease)
            {
                if (actor is { Enabled: true } && actor.AuthorizationRevision == authority.Revision &&
                    actor.CredentialId == authority.CredentialId && actor.Permissions.Contains(permission) && authority.Valid) return true;
                authority.Revoke(); return false;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { authority.Revoke(); return false; }
    }
}
