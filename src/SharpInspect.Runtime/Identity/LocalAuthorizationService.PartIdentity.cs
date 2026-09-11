using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    /// <summary>
    /// Authorizes a historical part-identity correction through the normal
    /// identity writer.  The production evidence is deliberately not an
    /// argument: the storage transaction resolves it from the immutable
    /// admission/Core ledger after this authorization has been accepted.
    /// </summary>
    internal async ValueTask<PartIdentityCorrectionResult> CorrectProductionPartIdentityAsync(
        CorrectProductionPartIdentityCommand command,
        Guid runtimeEpoch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (runtimeEpoch == Guid.Empty)
            return new(new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Rejected,
                "PartIdentityRuntimeEpochRequired", AuditPersistence.NotAttempted));
        var attempt = Guid.NewGuid();
        var deadline = new StoreDeadline(_store.CommitTimeout);
        try
        {
            var written = await _store.UpdateIdentityCommandAsync(command.CorrelationId,
                (state, duplicate) => AuthorizePartIdentityCorrection(state, command,
                    runtimeEpoch, attempt, duplicate, cancellationToken), cancellationToken, deadline)
                .ConfigureAwait(false);
            if (!written.Committed || written.Result is not PartIdentityCorrectionResult result)
                return new(new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Rejected,
                    written.ReasonCode, AuditPersistence.Unavailable, attempt));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Rejected,
                "PartIdentityCorrectionAuditUnavailable", AuditPersistence.Unavailable, attempt));
        }
    }

    private IdentityUpdate AuthorizePartIdentityCorrection(IdentityAuthorityState state,
        CorrectProductionPartIdentityCommand command, Guid runtimeEpoch, Guid attempt,
        bool duplicate, CancellationToken cancellationToken)
    {
        SessionAuthorizationLease? lease = null;
        StepUpGrant? reserved = null;
        var transferred = false;
        var now = _utcNow().ToUniversalTime();
        if (now < state.LastObservedUtc) now = state.LastObservedUtc;
        var binding = new StepUpBinding(Permission.CorrectHistoricalFact, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.CorrectHistoricalFact);
        try
        {
            var reason = TryLease(command.Invocation, out lease, out var leaseReason)
                ? "Authorized" : leaseReason;
            var actor = lease is null ? null : Find(state, lease.Identity.PrincipalId);
            if (reason == "Authorized" && command.Invocation.Source != CommandSource.PhysicalConsole)
                reason = "PhysicalConsoleRequired";
            if (reason == "Authorized" && (actor is not { Enabled: true } ||
                    !actor.Permissions.Contains(Permission.CorrectHistoricalFact)))
                reason = "PermissionDenied";
            if (reason == "Authorized" && duplicate)
                reason = "DuplicateCorrelationId";
            if (reason == "Authorized" && cancellationToken.IsCancellationRequested)
                reason = "PartIdentityCorrectionCancelled";
            if (reason == "Authorized")
                reason = CheckPartIdentityGrant(command, binding, actor!, lease!.SessionId,
                    reserve: true, out reserved);

            var accepted = reason == "Authorized";
            var effectiveReason = accepted ? "PartIdentityCorrectionAuthorized" : reason;
            var fact = new CommandAuditFact(Guid.NewGuid(), attempt, command.CorrelationId,
                runtimeEpoch, now, AuditedCommandKind.CorrectHistoricalFact,
                Enum.IsDefined(command.Invocation.Source) ? command.Invocation.Source : null,
                command.Invocation.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
                command.Invocation.SessionId, command.Invocation.StepUpGrantId,
                CommandAuditPhase.Outcome,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected,
                effectiveReason, actor?.PrincipalId.ToString("D"));
            var identity = AuthorizationEvent(state,
                accepted ? IdentityEventKind.PartIdentityCorrectionAuthorized : IdentityEventKind.ManagementRejected,
                effectiveReason, binding, actor?.PrincipalId, lease?.SessionId,
                command.Invocation.StepUpGrantId, command.CorrelationId,
                actor?.AuthorizationRevision ?? 0, capturedTime: now);
            var outcome = new RuntimeCommandOutcome(command.CorrelationId,
                fact.Disposition!.Value, effectiveReason, AuditPersistence.Persisted, attempt);
            if (!accepted)
            {
                var rejected = new PartIdentityCorrectionResult(outcome);
                return new IdentityUpdate(rejected, new[] { identity }, new[] { fact });
            }

            var write = new PartIdentityWriteRequest(null,
                new PartIdentityCorrectionWrite(command, runtimeEpoch, attempt, actor!.PrincipalId,
                    lease!.SessionId, actor.AuthorizationRevision,
                    command.Invocation.StepUpGrantId!.Value, now));
            var guard = new AuthorizationCommitGuard(lease!, () =>
            {
                lock (_grantSync)
                    if (reserved is not null) reserved.State = GrantState.Consumed;
            }, () =>
            {
                lock (_grantSync)
                    if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
            });
            transferred = true;
            return new IdentityUpdate(new PartIdentityCorrectionResult(outcome),
                new[] { identity }, new[] { fact }, guard, PartIdentity: write);
        }
        finally
        {
            if (!transferred)
            {
                lock (_grantSync)
                {
                    if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
                }
                lease?.Dispose();
            }
        }
    }

    private string CheckPartIdentityGrant(CorrectProductionPartIdentityCommand command,
        StepUpBinding binding, LocalAdministratorState actor, Guid sessionId, bool reserve,
        out StepUpGrant? grant)
    {
        grant = null;
        if (command.Invocation.StepUpGrantId is not { } id)
            return "StepUpRequired";
        lock (_grantSync)
        {
            PurgeGrantsLocked();
            if (!_grants.TryGetValue(id, out var candidate) || candidate.State != GrantState.Active ||
                candidate.PrincipalId != actor.PrincipalId || candidate.SessionId != sessionId ||
                candidate.CredentialId != actor.CredentialId ||
                candidate.AuthorizationRevision != actor.AuthorizationRevision ||
                candidate.PolicyHash != _options.AuthorizationPolicy.ContentHash ||
                candidate.Binding != binding)
                return "StepUpInvalid";
            grant = candidate;
            if (reserve) candidate.State = GrantState.Reserved;
            return "Authorized";
        }
    }
}
