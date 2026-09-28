using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed record StationMaintenanceAuthorization(RuntimeCommandOutcome Outcome,
    StationMaintenanceAuthority? Authority, Guid CommandEventId, Guid AuthorizationEventId,
    DateTimeOffset ObservedAtUtc);

internal sealed partial class LocalAuthorizationService
{
    // Runtime admits at most one maintenance operation, including its authorizing/draining work.
    // This callback-free stamp can be revoked while the identity writer holds its leases.
    private StationMaintenanceAuthority? _stationMaintenanceAuthority;

    internal void RevokeStationMaintenanceAuthority(Guid? principalId = null)
    {
        var stamp = Volatile.Read(ref _stationMaintenanceAuthority);
        if (principalId is null || stamp?.PrincipalId == principalId) stamp?.Revoke();
    }

    /// <summary>
    /// Authorizes one bounded maintenance operation and admits its immutable ledger fact in the
    /// same identity transaction as the accepted command fact and the signed authorization event.
    /// A fresh Step-Up grant must name exactly this operation, installation and reason.
    /// </summary>
    internal async ValueTask<StationMaintenanceAuthorization> AuthorizeStationMaintenanceAsync(
        PrepareStationMaintenanceCommand command, Guid epoch, int processId, DateTimeOffset processStartedUtc,
        string? forcedRejection, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var attempt = Guid.NewGuid();
        StationMaintenanceAuthorization Unavailable(string reason) => new(new RuntimeCommandOutcome(
            command.CorrelationId, CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, attempt),
            null, Guid.Empty, Guid.Empty, _utcNow());
        if (!_store.StationMaintenanceEnabled) return Unavailable("StationMaintenanceConfigurationRequired");
        if (epoch == Guid.Empty || processId <= 0 || processStartedUtc.Offset != TimeSpan.Zero ||
            processStartedUtc == default)
            return Unavailable("StationMaintenanceProcessIdentityInvalid");
        StationMaintenanceAuthorization? decision = null;
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
                        !actor.Permissions.Contains(Permission.PrepareStationMaintenance))) reason = "PermissionDenied";
                    if (reason == "Authorized" && duplicate) reason = "DuplicateCorrelationId";
                    if (reason == "Authorized" && forcedRejection is not null) reason = forcedRejection;
                    if (reason == "Authorized" && cancellationToken.IsCancellationRequested)
                        reason = "StationMaintenanceCancelled";
                    if (reason == "Authorized") reason = CheckGrant(command, actor!, lease!.SessionId, false, out _);
                    if (reason == "Authorized") reason = CheckGrant(command, actor!, lease!.SessionId, true, out reserved);
                    var accepted = reason == "Authorized";
                    var effective = accepted ? "StationMaintenanceAdmitted" : reason;
                    var now = _utcNow().ToUniversalTime();
                    if (now < identity.LastObservedUtc) now = identity.LastObservedUtc;
                    var binding = Binding(command);
                    var authorization = AuthorizationEvent(identity, accepted
                            ? IdentityEventKind.StationMaintenanceAuthorized : IdentityEventKind.ManagementRejected,
                        effective, binding, actor?.PrincipalId, lease?.SessionId, command.Invocation.StepUpGrantId,
                        command.CorrelationId, actor?.AuthorizationRevision ?? 0, capturedTime: now)
                        with { OperationId = command.OperationId };
                    var fact = new CommandAuditFact(Guid.NewGuid(), attempt, command.CorrelationId, epoch, now,
                        AuditedCommandKind.PrepareStationMaintenance,
                        Enum.IsDefined(command.Invocation.Source) ? command.Invocation.Source : null,
                        command.Invocation.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
                        command.Invocation.SessionId, command.Invocation.StepUpGrantId, CommandAuditPhase.Outcome,
                        accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected, effective,
                        actor?.PrincipalId.ToString("D"));
                    StationMaintenanceAuthority? stamp = null;
                    if (accepted)
                        stamp = new StationMaintenanceAuthority(actor!.PrincipalId, lease!.SessionId, actor.CredentialId,
                            actor.AuthorizationRevision, command.OperationId, epoch, processId, processStartedUtc)
                        {
                            LiveCheck = token => CheckStationMaintenanceAuthorityAsync(stamp!, token)
                        };
                    decision = new(new RuntimeCommandOutcome(command.CorrelationId,
                        accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected, effective,
                        AuditPersistence.Persisted, attempt), stamp, fact.EventId, authorization.EventId, now);
                    if (!accepted) return new IdentityUpdate(decision, new[] { authorization }, new[] { fact });
                    var admission = new StationMaintenanceAdmission(command.OperationId, epoch, processId,
                        processStartedUtc, command.Reason, effective, command.AuthorizationTarget);
                    var guard = new AuthorizationCommitGuard(lease!, () =>
                    {
                        lock (_grantSync) if (reserved is not null) reserved.State = GrantState.Consumed;
                        if (stamp is not null)
                            Interlocked.Exchange(ref _stationMaintenanceAuthority, stamp)?.Revoke();
                    }, () =>
                    {
                        stamp?.Revoke();
                        lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
                    });
                    transferred = true;
                    return new IdentityUpdate(decision, new[] { authorization }, new[] { fact }, guard,
                        StationMaintenance: admission);
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
            decision?.Authority?.Revoke();
            return Unavailable(written.ReasonCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            decision?.Authority?.Revoke();
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            decision?.Authority?.Revoke();
            return Unavailable("StationMaintenanceAuditUnavailable");
        }
    }

    /// <summary>
    /// Rechecks one authority against the live sealed identity state and the current interactive
    /// session lease. The seal calls this inside its serialized final path; root may call it
    /// before requesting the host shutdown. A failed check revokes the authority.
    /// </summary>
    internal async ValueTask<bool> CheckStationMaintenanceAuthorityAsync(StationMaintenanceAuthority authority,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!authority.Valid || Volatile.Read(ref _disposed) != 0) return false;
        try
        {
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            var actor = Find(state, authority.PrincipalId);
            var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
                authority.PrincipalId.ToString("D"), authority.SessionId);
            if (!TryLease(invocation, out var lease, out _))
            {
                authority.Revoke();
                return false;
            }
            using (lease)
            {
                if (actor is { Enabled: true } && actor.AuthorizationRevision == authority.Revision &&
                    actor.CredentialId == authority.CredentialId &&
                    actor.Permissions.Contains(Permission.PrepareStationMaintenance) && authority.Valid)
                    return true;
                authority.Revoke();
                return false;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            authority.Revoke();
            return false;
        }
    }
}
