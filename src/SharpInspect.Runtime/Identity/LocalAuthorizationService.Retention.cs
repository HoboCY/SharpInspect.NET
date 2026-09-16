using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal ValueTask<EvidenceRetentionAccess> GetRetentionAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken) => QueryAsync(async token =>
        {
            if (!_store.StorageRetentionEnabled) return new EvidenceRetentionAccess(false, "RetentionConfigurationRequired", true);
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            if (!TryLease(invocation, out var lease, out var reason)) return new(false, reason, true);
            using (lease)
            {
                var actor = Find(state, lease!.Identity.PrincipalId);
                return actor is { Enabled: true } && actor.Permissions.Contains(Permission.DeleteEvidence)
                    ? new(true, "RetentionAccessAvailable", true) : new(false, "PermissionDenied", true);
            }
        }, reason => new(false, reason, true), cancellationToken);

    internal async ValueTask<EvidenceRetentionResult> AuthorizeRetentionAsync(ChangeEvidenceRetentionCommand command,
        Guid epoch, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        var attempt = Guid.NewGuid();
        EvidenceRetentionResult Unavailable(string reason) => new(new(command.CorrelationId,
            CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, attempt), null);
        if (!_store.StorageRetentionEnabled || epoch == Guid.Empty) return Unavailable("RetentionConfigurationRequired");
        try
        {
            var result = await _store.UpdateRetentionGovernanceAsync(command,
                (identity, retention, duplicate) => AuthorizeRetention(identity, retention, command,
                    epoch, attempt, duplicate, cancellationToken), deadline).ConfigureAwait(false);
            if (!result.Committed) return Unavailable(result.ReasonCode);
            return result.Result is EvidenceRetentionResult retained ? retained :
                result.Result is RuntimeCommandOutcome outcome ? new(outcome, null) : Unavailable("RetentionResultMissing");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Unavailable(Integrity.SqliteAuditIntegrityQuery.FaultReason(error, "RetentionAuthorizationUnavailable")); }
    }

    private IdentityUpdate AuthorizeRetention(IdentityAuthorityState identity, EvidenceRetentionReadState retention,
        ChangeEvidenceRetentionCommand command, Guid epoch, Guid attempt, bool duplicate, CancellationToken cancellation)
    {
        SessionAuthorizationLease? lease = null;
        LocalAdministratorState? actor = null;
        StepUpGrant? reserved = null;
        var transferred = false;
        try
        {
            var reason = TryLease(command.Invocation, out lease, out var leaseReason) ? "Authorized" : leaseReason;
            if (lease is not null) actor = Find(identity, lease.Identity.PrincipalId);
            if (reason == "Authorized" && (actor is not { Enabled: true } ||
                !actor.Permissions.Contains(Permission.DeleteEvidence))) reason = "PermissionDenied";
            if (reason == "Authorized" && duplicate) reason = "DuplicateCorrelationId";
            if (reason == "Authorized" && cancellation.IsCancellationRequested) reason = "RetentionCancelled";
            if (reason == "Authorized" && command.Invocation!.StepUpGrantId is null) reason = "StepUpRequired";
            if (reason == "Authorized") reason = CheckGrant(command, actor!, lease!.SessionId, false, out _);
            retention.Replay.Subjects.TryGetValue(command.Owner, out var subject);
            if (reason == "Authorized" && subject is null) reason = "RetentionObligationMissing";
            if (reason == "Authorized" && subject!.Revision != command.ExpectedRevision) reason = "RetentionRevisionChanged";
            if (reason == "Authorized" && (subject!.DeleteIntent is not null || subject.Tombstone is not null))
                reason = "RetentionDeletionAlreadyStarted";
            if (reason == "Authorized" && command.Change == EvidenceRetentionChange.PlaceHold &&
                subject!.UsedHolds.Contains(command.HoldId!.Value)) reason = "RetentionHoldIdentityReused";
            if (reason == "Authorized" && command.Change == EvidenceRetentionChange.ReleaseHold &&
                !subject!.ActiveHolds.ContainsKey(command.HoldId!.Value)) reason = "RetentionHoldNotActive";
            if (reason == "Authorized" && command.Change == EvidenceRetentionChange.Extend &&
                command.ExtendedUntilUtc <= subject!.EffectiveUntilUtc) reason = "RetentionExtensionMustIncrease";
            var now = _utcNow().ToUniversalTime();
            if (now < identity.LastObservedUtc) now = identity.LastObservedUtc;
            if (reason == "Authorized") reason = CheckGrant(command, actor!, lease!.SessionId, true, out reserved);
            if (reason == "Authorized" && cancellation.IsCancellationRequested) reason = "RetentionCancelled";
            var accepted = reason == "Authorized";
            var effectiveReason = accepted ? "RetentionGovernanceRecorded" : reason;
            var kind = command.Change switch
            {
                EvidenceRetentionChange.PlaceHold => EvidenceRetentionEventKind.HoldPlaced,
                EvidenceRetentionChange.ReleaseHold => EvidenceRetentionEventKind.HoldReleased,
                EvidenceRetentionChange.Extend => EvidenceRetentionEventKind.Extended,
                _ => throw new InvalidOperationException("RetentionChangeInvalid")
            };
            EvidenceRetentionPayload? mutation = accepted ? new(1, Guid.NewGuid(), command.CorrelationId, epoch, kind,
                command.Owner, subject!.Revision + 1, subject.RevisionHash, subject.Obligation.ContentHash, now,
                effectiveReason, command.Reason, SystemPrincipalId.RetentionCleanup, actor!.PrincipalId,
                lease!.SessionId, command.Invocation!.StepUpGrantId!.Value, command.AuthorizationTarget,
                actor.AuthorizationRevision, command.HoldId, command.ExtendedUntilUtc, null, null,
                retention.Configuration.ExecutionPolicyHash) : null;
            var binding = Binding(command);
            var authorization = AuthorizationEvent(identity, accepted ? IdentityEventKind.EvidenceRetentionChanged :
                IdentityEventKind.ManagementRejected, effectiveReason, ValidBinding(binding) ? binding : null,
                actor?.PrincipalId, lease?.SessionId, command.Invocation?.StepUpGrantId, command.CorrelationId,
                actor?.AuthorizationRevision ?? 0, capturedTime: now) with
            {
                OperationId = accepted ? command.CorrelationId : null,
                RecoverySafetyEvidence = mutation is null ? null : SqliteCommandStore.RetentionOperationBinding(mutation)
            };
            var fact = new CommandAuditFact(Guid.NewGuid(), attempt, command.CorrelationId, epoch, now,
                AuditedCommandKind.ChangeEvidenceRetention,
                command.Invocation is { } invocation && Enum.IsDefined(invocation.Source) ? invocation.Source : null,
                command.Invocation?.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
                command.Invocation?.SessionId, command.Invocation?.StepUpGrantId, CommandAuditPhase.Outcome,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected, effectiveReason,
                actor?.PrincipalId.ToString("D"));
            var outcome = new RuntimeCommandOutcome(command.CorrelationId, accepted ? CommandDisposition.Accepted :
                CommandDisposition.Rejected, effectiveReason, AuditPersistence.Persisted, attempt);
            if (!accepted) return new(outcome, new[] { authorization }, new[] { fact });
            var guard = new AuthorizationCommitGuard(lease!, () =>
            { lock (_grantSync) if (reserved is not null) reserved.State = GrantState.Consumed; }, () =>
            { lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active; });
            transferred = true;
            return new(outcome, new[] { authorization }, new[] { fact }, guard, Retention: mutation);
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
