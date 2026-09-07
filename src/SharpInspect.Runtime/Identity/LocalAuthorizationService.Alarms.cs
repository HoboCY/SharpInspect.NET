using SharpInspect.Abstractions;
using SharpInspect.Runtime.Alarms;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal async ValueTask<RuntimeCommandOutcome> HandleAlarmCommandAsync(RuntimeCommand command,
        Guid runtimeEpoch, Guid attemptId, string? forcedRejection,
        Func<AlarmStateSnapshot, AlarmTransitionDecision> evaluate, StoreDeadline deadline,
        CancellationToken cancellationToken)
    {
        RuntimeCommandOutcome Unavailable() => new(command.CorrelationId, CommandDisposition.Rejected,
            "TraceAuditUnavailable", AuditPersistence.Unavailable, attemptId);
        try
        {
            var write = await _store.UpdateAlarmCommandAsync(command.CorrelationId, runtimeEpoch,
                (identity, alarms, duplicate) => ApplyAlarmCommand(identity, alarms, duplicate,
                    command, runtimeEpoch, attemptId, forcedRejection, evaluate, cancellationToken),
                cancellationToken, deadline).ConfigureAwait(false);
            return write.Committed && write.Result is RuntimeCommandOutcome outcome ? outcome : Unavailable();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return Unavailable(); }
    }

    private IdentityUpdate ApplyAlarmCommand(IdentityAuthorityState state, AlarmStateSnapshot alarms,
        bool duplicate, RuntimeCommand command, Guid epoch, Guid attempt, string? forcedRejection,
        Func<AlarmStateSnapshot, AlarmTransitionDecision> evaluate, CancellationToken cancellationToken)
    {
        // The Runtime command gate is held throughout this transaction. Read its orthogonal
        // interlocks before taking the Session monitor, avoiding a Session -> Runtime lock inversion.
        var transition = evaluate(alarms);
        LocalAdministratorState? actor = null;
        SessionAuthorizationLease? lease = null;
        StepUpGrant? reserved = null;
        var transferred = false;
        try
        {
            var reason = TryLease(command.Invocation, out lease, out var leaseReason) ? "Authorized" : leaseReason;
            if (lease is not null) actor = Find(state, lease.Identity.PrincipalId);
            if (reason == "Authorized" && (actor is not { Enabled: true } || !actor.Permissions.Contains(RequiredPermission(command))))
                reason = "PermissionDenied";
            if (reason == "Authorized" && duplicate) reason = "DuplicateCorrelationId";
            if (reason == "Authorized" && forcedRejection is not null) reason = forcedRejection;
            if (reason == "Authorized" && cancellationToken.IsCancellationRequested) reason = "AlarmCommandCancelled";
            if (reason == "Authorized") reason = CheckGrant(command, actor!, lease!.SessionId, reserve: false, out _);
            if (reason == "Authorized" && !transition.Succeeded) reason = transition.ReasonCode;
            if (reason != "Authorized")
                return CommandDecision(state, command, epoch, attempt, actor, lease?.SessionId, reason, accepted: false);

            reason = CheckGrant(command, actor!, lease!.SessionId, reserve: true, out reserved);
            if (reason != "Authorized")
                return CommandDecision(state, command, epoch, attempt, actor, lease.SessionId, reason, accepted: false);
            var guard = new AuthorizationCommitGuard(lease, () =>
            {
                lock (_grantSync) if (reserved is not null) reserved.State = GrantState.Consumed;
            }, () =>
            {
                lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
            });
            var update = CommandDecision(state, command, epoch, attempt, actor, lease.SessionId,
                transition.ReasonCode, accepted: true, IdentityEventKind.AlarmActionAuthorized, guard);
            var facts = transition.Events.Select(fact => fact with
            {
                ActorPrincipalId = actor!.PrincipalId,
                SessionId = lease.SessionId,
                CommandCorrelationId = command.CorrelationId,
                Instance = fact.Transition == AlarmTransitionKind.Acknowledged && fact.Instance is { } instance
                    ? instance with { AcknowledgedBy = actor.PrincipalId } : fact.Instance
            }).ToArray();
            transferred = true;
            return update with { AlarmEvents = facts };
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
