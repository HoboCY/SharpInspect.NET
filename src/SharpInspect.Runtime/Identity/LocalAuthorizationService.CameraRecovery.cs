using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

/// <summary>
/// Dedicated authorization boundary for starting an exhausted camera recovery
/// cycle. It records one durable outcome fact; hardware completion is a later
/// terminal continuation owned by the Runtime caller.
/// </summary>
internal sealed partial class LocalAuthorizationService
{
    internal async ValueTask<CameraRecoveryStartAuthorization> HandleCameraRecoveryCycleStartAsync(
        StartCameraRecoveryCycleCommand command, Guid runtimeEpoch, Guid attemptId,
        string? forcedRejection, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.CorrelationId == Guid.Empty || runtimeEpoch == Guid.Empty || attemptId == Guid.Empty)
            return new(new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Rejected,
                "InvalidCommandContext", AuditPersistence.NotAttempted, attemptId), null);
        try
        {
            var write = await _store.UpdateIdentityCommandAsync(command.CorrelationId,
                (state, duplicate) => ApplyCameraRecoveryCycleStart(state, command, runtimeEpoch,
                    attemptId, duplicate, forcedRejection, cancellationToken),
                cancellationToken, deadline).ConfigureAwait(false);
            if (write.Committed && write.Result is CameraRecoveryStartAuthorization authorization)
                return authorization;
            return new(new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Rejected,
                "TraceAuditUnavailable", AuditPersistence.Unavailable, attemptId), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new(new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Rejected,
                "TraceAuditUnavailable", AuditPersistence.Unavailable, attemptId), null);
        }
    }

    internal ValueTask<StoreWriteResult> CompleteCameraRecoveryCycleStartAsync(
        CommandAuditFact admission, Guid? newCycleId, bool started, string reasonCode,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(deadline);
        if (admission.Phase != CommandAuditPhase.Outcome ||
            admission.Disposition != CommandDisposition.Accepted ||
            admission.CommandKind != AuditedCommandKind.StartCameraRecoveryCycle ||
            admission.AttemptId == Guid.Empty || admission.CorrelationId == Guid.Empty ||
            admission.RuntimeEpoch == Guid.Empty ||
            ((started && (newCycleId is null || newCycleId == Guid.Empty)) ||
                (!started && newCycleId is not null)) ||
            !StableAsciiIdentifier(reasonCode))
            return ValueTask.FromResult(new StoreWriteResult(false, "CameraRecoveryTerminalInputInvalid"));
        return _store.AppendCameraRecoveryTerminalAsync(admission, newCycleId, started,
            reasonCode, deadline, cancellationToken);
    }

    private IdentityUpdate ApplyCameraRecoveryCycleStart(IdentityAuthorityState state,
        StartCameraRecoveryCycleCommand command, Guid runtimeEpoch, Guid attemptId,
        bool duplicate, string? forcedRejection, CancellationToken cancellationToken)
    {
        LocalAdministratorState? actor = null;
        SessionAuthorizationLease? lease = null;
        StepUpGrant? reserved = null;
        var transferred = false;
        try
        {
            var reason = _store.CameraRecoveryEnabled ? "Authorized" : "CameraRecoveryUnavailable";
            if (reason == "Authorized" && !ValidRecoveryCommand(command, runtimeEpoch, attemptId))
                reason = "CameraRecoveryCommandInvalid";
            if (reason == "Authorized" &&
                !TryLease(command.Invocation, out lease, out var leaseReason)) reason = leaseReason;
            if (lease is not null) actor = Find(state, lease.Identity.PrincipalId);
            if (reason == "Authorized" && (actor is not { Enabled: true } ||
                !actor.Permissions.Contains(Permission.ManageCameraBindings))) reason = "PermissionDenied";
            if (reason == "Authorized" && duplicate) reason = "DuplicateCorrelationId";
            if (reason == "Authorized" && forcedRejection is not null) reason = forcedRejection;
            if (reason == "Authorized" && cancellationToken.IsCancellationRequested)
                reason = "CameraRecoveryCommandCancelled";
            if (reason == "Authorized")
                reason = CheckGrant(command, actor!, lease!.SessionId, reserve: false, out _);
            if (reason != "Authorized")
                return RecoveryCommandDecision(state, command, runtimeEpoch, attemptId, actor,
                    lease?.SessionId, reason, accepted: false);

            reason = CheckGrant(command, actor!, lease!.SessionId, reserve: true, out reserved);
            if (reason != "Authorized")
                return RecoveryCommandDecision(state, command, runtimeEpoch, attemptId, actor,
                    lease.SessionId, reason, accepted: false);
            var guard = new AuthorizationCommitGuard(lease, () =>
            {
                lock (_grantSync) if (reserved is not null) reserved.State = GrantState.Consumed;
            }, () =>
            {
                lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
            });
            var fact = new CommandAuditFact(Guid.NewGuid(), attemptId, command.CorrelationId,
                runtimeEpoch, _utcNow(), AuditedCommandKind.StartCameraRecoveryCycle,
                command.Invocation is { } invocation && Enum.IsDefined(invocation.Source)
                    ? invocation.Source : null,
                command.Invocation?.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
                command.Invocation?.SessionId, command.Invocation?.StepUpGrantId,
                CommandAuditPhase.Outcome, CommandDisposition.Accepted,
                "CameraRecoveryCycleStartAuthorized", actor!.PrincipalId.ToString("D"));
            var binding = new StepUpBinding(Permission.ManageCameraBindings,
                command.CorrelationId, command.LogicalRole,
                AuditedCommandKind.StartCameraRecoveryCycle);
            var identity = AuthorizationEvent(state,
                IdentityEventKind.CameraRecoveryCycleStartAuthorized,
                "CameraRecoveryCycleStartAuthorized", binding, actor.PrincipalId,
                lease!.SessionId, command.Invocation?.StepUpGrantId, command.CorrelationId,
                actor.AuthorizationRevision) with
            {
                CameraRecoveryExpectedCycleId = command.ExpectedCycleId,
                CameraRecoveryLogicalRole = command.LogicalRole,
                CameraRecoveryReasonCode = command.ReasonCode
            };
            transferred = true;
            var outcome = new RuntimeCommandOutcome(command.CorrelationId,
                CommandDisposition.Accepted, "CameraRecoveryCycleStartAuthorized",
                AuditPersistence.Persisted, attemptId);
            return new IdentityUpdate(new CameraRecoveryStartAuthorization(outcome, fact),
                new[] { identity }, new[] { fact }, guard);
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

    private IdentityUpdate RecoveryCommandDecision(IdentityAuthorityState state,
        StartCameraRecoveryCycleCommand command, Guid epoch, Guid attempt,
        LocalAdministratorState? actor, Guid? sessionId, string reason, bool accepted)
    {
        var binding = Binding(command);
        if (!ValidBinding(binding)) binding = null;
        var fact = new CommandAuditFact(Guid.NewGuid(), attempt, command.CorrelationId, epoch,
            _utcNow(), AuditedCommandKind.StartCameraRecoveryCycle,
            command.Invocation is { } invocation && Enum.IsDefined(invocation.Source)
                ? invocation.Source : null,
            command.Invocation?.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
            command.Invocation?.SessionId, command.Invocation?.StepUpGrantId,
            CommandAuditPhase.Outcome, accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected,
            reason, actor?.PrincipalId.ToString("D"));
        var identity = AuthorizationEvent(state, IdentityEventKind.ManagementRejected, reason,
            binding, actor?.PrincipalId, sessionId, command.Invocation?.StepUpGrantId,
            command.CorrelationId, actor?.AuthorizationRevision ?? 0);
        var outcome = new RuntimeCommandOutcome(command.CorrelationId,
            accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected, reason,
            AuditPersistence.Persisted, attempt);
        return new IdentityUpdate(new CameraRecoveryStartAuthorization(outcome,
                accepted ? fact : null), new[] { identity }, new[] { fact });
    }

    private static bool ValidRecoveryCommand(StartCameraRecoveryCycleCommand command,
        Guid runtimeEpoch, Guid attemptId) =>
        runtimeEpoch != Guid.Empty && attemptId != Guid.Empty && command.ExpectedCycleId != Guid.Empty &&
        StableAsciiIdentifier(command.LogicalRole, 64) && StableAsciiIdentifier(command.ReasonCode);

    private static bool StableAsciiIdentifier(string? value, int maximum = 128) =>
        value is { Length: > 0 } && value.Length <= maximum &&
        value.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or
            (>= '0' and <= '9') or '.' or '_' or '-');
}

internal sealed record CameraRecoveryStartAuthorization(RuntimeCommandOutcome Outcome,
    CommandAuditFact? Admission);
