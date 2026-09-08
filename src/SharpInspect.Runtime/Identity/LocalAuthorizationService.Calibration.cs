using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal async ValueTask<CalibrationCommandAuthorization> HandleCalibrationCommandAsync(
        RuntimeCommand command, Guid runtimeEpoch, Guid attemptId, CalibrationSessionAdmissionInput? input,
        CalibrationSessionHeader? existing, string? forcedRejection, StoreDeadline deadline,
        CancellationToken cancellationToken)
    {
        CalibrationCommandAuthorization Unavailable(string reason) => new(new RuntimeCommandOutcome(
            command.CorrelationId, CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, attemptId));
        if (command.CorrelationId == Guid.Empty || runtimeEpoch == Guid.Empty || attemptId == Guid.Empty)
            return Unavailable("InvalidCommandContext");
        try
        {
            var result = await _store.UpdateIdentityCommandAsync(command.CorrelationId,
                (state, duplicate) => AuthorizeCalibration(state, command, runtimeEpoch, attemptId,
                    input, existing, forcedRejection, duplicate, cancellationToken), cancellationToken,
                deadline).ConfigureAwait(false);
            return result.Committed && result.Result is CalibrationCommandAuthorization authorization
                ? authorization : Unavailable("CalibrationAuditUnavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Unavailable("CalibrationAuditUnavailable"); }
    }

    private IdentityUpdate AuthorizeCalibration(IdentityAuthorityState state, RuntimeCommand command,
        Guid epoch, Guid attempt, CalibrationSessionAdmissionInput? input, CalibrationSessionHeader? existing,
        string? forced, bool duplicate, CancellationToken cancellationToken)
    {
        LocalAdministratorState? actor = null;
        SessionAuthorizationLease? lease = null;
        StepUpGrant? reserved = null;
        var transferred = false;
        try
        {
            var start = command is StartCalibrationSessionCommand;
            var reason = _store.CalibrationEnabled ? "Authorized" : "CalibrationSessionsUnavailable";
            if (reason == "Authorized" && !ValidCalibrationCommand(command)) reason = "CalibrationCommandInvalid";
            if (reason == "Authorized" && !TryLease(command.Invocation, out lease, out var leaseReason)) reason = leaseReason;
            if (lease is not null) actor = Find(state, lease.Identity.PrincipalId);
            if (reason == "Authorized" && (actor is not { Enabled: true } || !actor.Permissions.Contains(Permission.RunCalibration)))
                reason = "PermissionDenied";
            if (reason == "Authorized" && duplicate) reason = "DuplicateCorrelationId";
            if (reason == "Authorized" && !start && (existing is null ||
                ((CalibrationSessionCommand)command).CalibrationSessionId != existing.SessionId ||
                actor!.PrincipalId != existing.ActorPrincipalId || lease!.SessionId != existing.InteractiveSessionId ||
                actor.AuthorizationRevision != existing.AuthorizationRevision)) reason = "CalibrationSessionAuthorityChanged";
            if (reason == "Authorized" && forced is not null) reason = forced;
            if (reason == "Authorized" && start && input is null) reason = "CalibrationAdmissionUnavailable";
            if (reason == "Authorized" && cancellationToken.IsCancellationRequested) reason = "CalibrationCommandCancelled";
            if (reason == "Authorized" && start) reason = CheckGrant(command, actor!, lease!.SessionId, reserve: true, out reserved);
            var accepted = reason == "Authorized";
            CalibrationSessionHeader? header = existing;
            if (accepted && start)
                header = new CalibrationSessionHeader(Guid.NewGuid(), epoch, attempt, actor!.PrincipalId,
                    lease!.SessionId, actor.AuthorizationRevision, _utcNow(), (StartCalibrationSessionCommand)command,
                    input!.Binding, input.BaselineRequested, input.BaselineEffective, input.DevelopmentFixtureHash);
            var decisionReason = accepted ? start ? "CalibrationSessionStartAuthorized" :
                "CalibrationSessionActionAuthorized" : reason;
            var fact = new CommandAuditFact(Guid.NewGuid(), attempt, command.CorrelationId, epoch, _utcNow(),
                CommandKind(command), command.Invocation is { } invocation && Enum.IsDefined(invocation.Source) ? invocation.Source : null,
                command.Invocation?.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
                command.Invocation?.SessionId, start ? command.Invocation?.StepUpGrantId : null,
                CommandAuditPhase.Outcome, accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected,
                decisionReason, actor?.PrincipalId.ToString("D"));
            var binding = Binding(command);
            var identity = AuthorizationEvent(state, accepted ? start ? IdentityEventKind.CalibrationSessionStartAuthorized :
                IdentityEventKind.CalibrationSessionActionAuthorized : IdentityEventKind.ManagementRejected,
                decisionReason, ValidBinding(binding) ? binding : null, actor?.PrincipalId, lease?.SessionId,
                start ? command.Invocation?.StepUpGrantId : null, command.CorrelationId, actor?.AuthorizationRevision ?? 0)
                with { OperationId = accepted ? header!.SessionId : null };
            var outcome = new RuntimeCommandOutcome(command.CorrelationId, fact.Disposition!.Value,
                decisionReason, AuditPersistence.Persisted, attempt);
            var authorization = new CalibrationCommandAuthorization(outcome, accepted ? fact : null,
                accepted ? header : null, !start && existing is not null && lease is not null &&
                    lease.Identity.PrincipalId == existing.ActorPrincipalId && lease.SessionId == existing.InteractiveSessionId &&
                    (actor is not { Enabled: true } || !actor.Permissions.Contains(Permission.RunCalibration) ||
                     actor.AuthorizationRevision != existing.AuthorizationRevision));
            if (!accepted) return new IdentityUpdate(authorization, new[] { identity }, new[] { fact });
            var guard = new AuthorizationCommitGuard(lease!, () =>
            {
                lock (_grantSync) if (reserved is not null) reserved.State = GrantState.Consumed;
            }, () =>
            {
                lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
            });
            transferred = true;
            return new IdentityUpdate(authorization, new[] { identity }, new[] { fact }, guard,
                CalibrationAdmission: start ? header : null);
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

    internal ValueTask<string?> CheckCalibrationQueryAuthorizationAsync(CommandInvocation invocation,
        CancellationToken cancellationToken) => QueryAsync(async token =>
        {
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            if (!TryLease(invocation, out var lease, out var reason)) return reason;
            using (lease)
            {
                var actor = Find(state, lease!.Identity.PrincipalId);
                return actor is { Enabled: true } && actor.Permissions.Contains(Permission.RunCalibration)
                    ? null : "PermissionDenied";
            }
        }, reason => reason, cancellationToken);

    private static bool ValidCalibrationCommand(RuntimeCommand command)
    {
        if (command.Invocation is null || command.CorrelationId == Guid.Empty ||
            !Enum.IsDefined(command.Invocation.Source) || command.Invocation.PrincipalId?.Length > 256) return false;
        if (command is StartCalibrationSessionCommand) return true;
        if (command is not CalibrationSessionCommand session || session.CalibrationSessionId == Guid.Empty) return false;
        return command switch
        {
            CaptureCalibrationFrameCommand or ComputeCalibrationCandidateCommand => true,
            ExcludeCalibrationFrameCommand exclude => exclude.FrameId != Guid.Empty && ValidCalibrationReason(exclude.Reason),
            ExitCalibrationSessionCommand exit => ValidCalibrationReason(exit.Reason),
            _ => false
        };
    }
    private static bool ValidCalibrationReason(string? reason) =>
        !string.IsNullOrWhiteSpace(reason) && reason.Length <= 512 && !reason.Any(char.IsControl);
}

internal sealed record CalibrationSessionAdmissionInput(CameraBindingRevision Binding,
    RequestedCameraConfiguration BaselineRequested, EffectiveCameraConfiguration BaselineEffective,
    string DevelopmentFixtureHash);
internal sealed record CalibrationCommandAuthorization(RuntimeCommandOutcome Outcome,
    CommandAuditFact? Admission = null, CalibrationSessionHeader? Header = null,
    bool SessionAuthorityRevoked = false);
