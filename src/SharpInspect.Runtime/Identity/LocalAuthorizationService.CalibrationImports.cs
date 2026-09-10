using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal ValueTask<string?> CheckCalibrationImportAuthorizationAsync(CalibrationImportCommand command,
        CancellationToken cancellationToken) => QueryAsync(async token =>
        {
            if (command.Invocation.Source != CommandSource.PhysicalConsole) return "LocalConsoleRequired";
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            if (!TryLease(command.Invocation, out var lease, out var reason)) return reason;
            using (lease)
            {
                var actor = Find(state, lease!.Identity.PrincipalId);
                var permission = RequiredPermission(command);
                if (permission == Permission.None || actor is not { Enabled: true } ||
                    !actor.Permissions.Contains(permission)) return "PermissionDenied";
                if (command.Invocation.StepUpGrantId is null) return "StepUpRequired";
                reason = CheckGrant(command, actor, lease.SessionId, false, out var grant);
                return reason != "Authorized" ? reason : grant is null ? "StepUpRequired" : null;
            }
        }, reason => reason, cancellationToken);

    internal async ValueTask<RuntimeCommandOutcome> HandleCalibrationImportCommandAsync(CalibrationImportCommand command,
        Guid epoch, Guid attempt, string? forced, CalibrationImportPreparation preparation,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _store.UpdateCalibrationImportCommandAsync(command, (state, imports, duplicate) =>
                AuthorizeCalibrationImport(state, imports, command, epoch, attempt, forced, preparation, duplicate,
                    cancellationToken), cancellationToken, deadline).ConfigureAwait(false);
            return result.Committed && result.Result is RuntimeCommandOutcome outcome ? outcome :
                new(command.CorrelationId, CommandDisposition.Rejected, result.ReasonCode, AuditPersistence.Unavailable, attempt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(command.CorrelationId, CommandDisposition.Rejected, "CalibrationImportAuditUnavailable",
                AuditPersistence.Unavailable, attempt);
        }
    }

    private IdentityUpdate AuthorizeCalibrationImport(IdentityAuthorityState state, CalibrationImportCommandState imports,
        CalibrationImportCommand command, Guid epoch, Guid attempt, string? forced, CalibrationImportPreparation preparation,
        bool duplicate, CancellationToken cancellationToken)
    {
        SessionAuthorizationLease? lease = null;
        LocalAdministratorState? actor = null;
        StepUpGrant? reserved = null;
        var transferred = false;
        try
        {
            var now = CalibrationGovernanceUtcNow;
            if (now < state.LastObservedUtc) now = state.LastObservedUtc;
            var permission = RequiredPermission(command);
            var reason = imports.Enabled ? "Authorized" : "CalibrationImportUnavailable";
            if (reason == "Authorized" && command.Invocation.Source != CommandSource.PhysicalConsole)
                reason = "LocalConsoleRequired";
            if (reason == "Authorized" && !TryLease(command.Invocation, out lease, out var leaseReason)) reason = leaseReason;
            if (lease is not null) actor = Find(state, lease.Identity.PrincipalId);
            if (reason == "Authorized" && (permission == Permission.None || actor is not { Enabled: true } ||
                !actor.Permissions.Contains(permission))) reason = "PermissionDenied";
            if (reason == "Authorized" && duplicate) reason = "DuplicateCorrelationId";
            if (reason == "Authorized" && forced is not null) reason = forced;
            if (reason == "Authorized" && imports.CameraPending) reason = "CalibrationImportCameraOperationPending";
            if (reason == "Authorized" && cancellationToken.IsCancellationRequested) reason = "CalibrationImportCancelled";
            if (reason == "Authorized" && command.Invocation.StepUpGrantId is null) reason = "StepUpRequired";
            if (reason == "Authorized") reason = CheckGrant(command, actor!, lease!.SessionId, true, out reserved);
            if (reason == "Authorized" && reserved is null) reason = "StepUpRequired";
            CalibrationImportDecision? decision = null;
            if (reason == "Authorized")
            {
                decision = CalibrationImportProjection.Decide(command, imports.Records, imports.Governance,
                    imports.Binding, imports.Imaging, preparation,
                    new CalibrationGovernanceActor(actor!.PrincipalId, lease!.SessionId, actor.AuthorizationRevision), now);
                reason = decision.ReasonCode;
            }
            var accepted = decision?.Record is not null;
            var fact = new CommandAuditFact(Guid.NewGuid(), attempt, command.CorrelationId, epoch, now,
                CommandKind(command), Enum.IsDefined(command.Invocation.Source) ? command.Invocation.Source : null,
                command.Invocation.PrincipalId is { Length: <= 256 } principal ? principal : null,
                command.Invocation.SessionId, command.Invocation.StepUpGrantId, CommandAuditPhase.Outcome,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected, reason, actor?.PrincipalId.ToString("D"));
            var binding = Binding(command);
            var authorization = AuthorizationEvent(state, accepted ? IdentityEventKind.CalibrationGovernanceActionAuthorized :
                IdentityEventKind.ManagementRejected, reason, ValidBinding(binding) ? binding : null,
                actor?.PrincipalId, lease?.SessionId, command.Invocation.StepUpGrantId, command.CorrelationId,
                actor?.AuthorizationRevision ?? 0, capturedTime: now) with
                { OperationId = accepted ? command.CorrelationId : null };
            var outcome = new RuntimeCommandOutcome(command.CorrelationId, fact.Disposition!.Value, reason,
                AuditPersistence.Persisted, attempt);
            if (!accepted) return new IdentityUpdate(outcome, new[] { authorization }, new[] { fact });
            var guard = new AuthorizationCommitGuard(lease!, () =>
            {
                lock (_grantSync) if (reserved is not null) reserved.State = GrantState.Consumed;
            }, () =>
            {
                lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
            });
            transferred = true;
            return new IdentityUpdate(outcome, new[] { authorization }, new[] { fact }, guard,
                CalibrationImport: new CalibrationImportMutation(decision!.Record!));
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
