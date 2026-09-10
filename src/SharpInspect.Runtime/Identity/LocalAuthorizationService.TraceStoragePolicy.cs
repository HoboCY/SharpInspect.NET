using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal DateTimeOffset TraceStoragePolicyUtcNow => _utcNow().ToUniversalTime();

    internal async ValueTask<TraceStoragePolicyAccess> GetTraceStoragePolicyAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!_store.TraceStoragePolicyEnabled)
            return new(false, "TraceStoragePolicyUnavailable", false);
        const bool stepUp = true;
        try
        {
            var state = await _store.ReadIdentityAsync(cancellationToken).ConfigureAwait(false);
            if (!TryLease(invocation, out var lease, out var reason)) return new(false, reason, stepUp);
            using (lease)
            {
                var actor = Find(state, lease!.Identity.PrincipalId);
                return actor is { Enabled: true } && actor.Permissions.Contains(Permission.ManageProductionPolicy)
                    ? new(true, "TraceStoragePolicyAccessAvailable", stepUp) : new(false, "PermissionDenied", stepUp);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, "TraceStoragePolicyAuthorizationUnavailable", stepUp); }
    }

    internal async ValueTask<TraceStoragePolicyResult> ExecuteTraceStoragePolicyAsync(PublishTraceStoragePolicyCommand command,
        string? forcedRejection, Guid epoch,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        var attempt = Guid.NewGuid();
        try
        {
            return await _store.ExecuteTraceStoragePolicyAsync(command, (state, duplicate) =>
                AuthorizeTraceStoragePolicy(state, command, duplicate, forcedRejection, epoch, attempt, cancellationToken),
                deadline, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(new(command.CorrelationId, CommandDisposition.Rejected, "TraceStoragePolicyAuditUnavailable", AuditPersistence.Unavailable, attempt)); }
    }

    private TraceStoragePolicyEvaluation AuthorizeTraceStoragePolicy(IdentityAuthorityState state,
        PublishTraceStoragePolicyCommand command, bool duplicate, string? forced, Guid epoch, Guid attempt,
        CancellationToken cancellationToken)
    {
        SessionAuthorizationLease? lease = null;
        StepUpGrant? reserved = null;
        var transferred = false;
        try
        {
            var now = TraceStoragePolicyUtcNow;
            if (now < state.LastObservedUtc) now = state.LastObservedUtc;
            var reason = TryLease(command.Invocation, out lease, out var leaseReason) ? "Authorized" : leaseReason;
            var actor = lease is null ? null : Find(state, lease.Identity.PrincipalId);
            if (reason == "Authorized" && (actor is not { Enabled: true } || !actor.Permissions.Contains(RequiredPermission(command))))
                reason = "PermissionDenied";
            if (reason == "Authorized" && forced is not null) reason = forced;
            if (reason == "Authorized" && cancellationToken.IsCancellationRequested) reason = "TraceStoragePolicyCancelled";
            // The writer additionally checks the exact persisted command, actor and policy before replay.
            if (reason == "Authorized" && !duplicate) reason = CheckGrant(command, actor!, lease!.SessionId, true, out reserved);
            var accepted = reason == "Authorized";
            var binding = Binding(command);
            var acceptedReason = duplicate ? "TraceStoragePolicyReplayAuthorized" : "TraceStoragePolicyAuthorized";
            var fact = new CommandAuditFact(Guid.NewGuid(), attempt, command.CorrelationId, epoch, now,
                CommandKind(command), Enum.IsDefined(command.Invocation.Source) ? command.Invocation.Source : null,
                command.Invocation.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
                command.Invocation.SessionId, command.Invocation.StepUpGrantId, CommandAuditPhase.Outcome,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected,
                accepted ? acceptedReason : reason, actor?.PrincipalId.ToString("D"));
            var identity = AuthorizationEvent(state, accepted ? IdentityEventKind.TraceStoragePolicyAuthorized : IdentityEventKind.ManagementRejected,
                accepted ? acceptedReason : reason, binding, actor?.PrincipalId, lease?.SessionId,
                command.Invocation.StepUpGrantId, command.CorrelationId, actor?.AuthorizationRevision ?? 0, capturedTime: now)
                with { OperationId = accepted ? command.CorrelationId : null };
            var result = new TraceStoragePolicyResult(new(command.CorrelationId, fact.Disposition!.Value,
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
                new TraceStoragePolicyVerifiedActor(actor!.PrincipalId, lease!.SessionId, actor.AuthorizationRevision, now,
                    new(_options.AuthorizationPolicy.Id, _options.AuthorizationPolicy.Version, _options.AuthorizationPolicy.ContentHash)));
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
