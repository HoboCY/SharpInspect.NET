using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal DateTimeOffset CalibrationGovernanceUtcNow => _utcNow().ToUniversalTime();

    internal async ValueTask<RuntimeCommandOutcome> HandleCalibrationGovernanceCommandAsync(
        CalibrationGovernanceCommand command, Guid epoch, Guid attempt, string? forcedRejection,
        CalibrationGovernancePreparation preparation, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        RuntimeCommandOutcome Unavailable(string reason) => new(command.CorrelationId, CommandDisposition.Rejected,
            reason, AuditPersistence.Unavailable, attempt);
        try
        {
            var committed = await _store.UpdateCalibrationGovernanceCommandAsync(command, (state, governance, duplicate) =>
                AuthorizeCalibrationGovernance(state, governance, command, epoch, attempt, forcedRejection,
                    preparation, duplicate, cancellationToken), cancellationToken, deadline).ConfigureAwait(false);
            return committed.Committed && committed.Result is RuntimeCommandOutcome outcome ? outcome :
                Unavailable(committed.ReasonCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Unavailable("CalibrationGovernanceAuditUnavailable"); }
    }

    private IdentityUpdate AuthorizeCalibrationGovernance(IdentityAuthorityState state,
        CalibrationGovernanceCommandState governance, CalibrationGovernanceCommand command, Guid epoch,
        Guid attempt, string? forced, CalibrationGovernancePreparation preparation, bool duplicate,
        CancellationToken cancellationToken)
    {
        SessionAuthorizationLease? lease = null;
        LocalAdministratorState? actor = null;
        StepUpGrant? reserved = null;
        var transferred = false;
        try
        {
            var governanceTime = CalibrationGovernanceUtcNow;
            if (governanceTime < state.LastObservedUtc) governanceTime = state.LastObservedUtc;
            var required = RequiredPermission(command);
            var reason = governance.Enabled ? "Authorized" : "CalibrationGovernanceUnavailable";
            if (reason == "Authorized" && !TryLease(command.Invocation, out lease, out var leaseReason)) reason = leaseReason;
            if (lease is not null) actor = Find(state, lease.Identity.PrincipalId);
            if (reason == "Authorized" && (actor is not { Enabled: true } || !actor.Permissions.Contains(required)))
                reason = "PermissionDenied";
            if (reason == "Authorized" && duplicate) reason = "DuplicateCorrelationId";
            if (reason == "Authorized" && forced is not null) reason = forced;
            if (reason == "Authorized" && cancellationToken.IsCancellationRequested) reason = "CalibrationGovernanceCancelled";
            if (reason == "Authorized") reason = CheckGrant(command, actor!, lease!.SessionId, reserve: true, out reserved);
            CalibrationGovernanceDecision? decision = null;
            if (reason == "Authorized")
            {
                var expectedCandidate = command switch
                {
                    EvaluateCalibrationCandidateCommand evaluate => evaluate.Candidate,
                    PublishCalibrationProfileCommand publish => publish.Candidate,
                    _ => null
                };
                var imageFailure = preparation.SourceImagesFailure;
                if (expectedCandidate is not null && preparation.VerifiedCandidate != expectedCandidate)
                    imageFailure = "CalibrationSourceImagesUnavailable";
                decision = CalibrationGovernanceProjection.Decide(command, governance.Records, governance.Session,
                    new CalibrationGovernanceActor(actor!.PrincipalId, lease!.SessionId, actor.AuthorizationRevision),
                    governanceTime, imageFailure, preparation.Verification);
                reason = decision.ReasonCode;
            }
            var accepted = decision?.Record is not null;
            var fact = new CommandAuditFact(Guid.NewGuid(), attempt, command.CorrelationId, epoch, governanceTime,
                CommandKind(command), command.Invocation is { } invocation && Enum.IsDefined(invocation.Source) ? invocation.Source : null,
                command.Invocation?.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
                command.Invocation?.SessionId, command.Invocation?.StepUpGrantId, CommandAuditPhase.Outcome,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected, reason, actor?.PrincipalId.ToString("D"));
            var binding = Binding(command);
            var identity = AuthorizationEvent(state, accepted ? IdentityEventKind.CalibrationGovernanceActionAuthorized :
                IdentityEventKind.ManagementRejected, reason, ValidBinding(binding) ? binding : null,
                actor?.PrincipalId, lease?.SessionId, command.Invocation?.StepUpGrantId, command.CorrelationId,
                actor?.AuthorizationRevision ?? 0, capturedTime: governanceTime) with
                { OperationId = accepted ? command.CorrelationId : null };
            var outcome = new RuntimeCommandOutcome(command.CorrelationId, fact.Disposition!.Value, reason,
                AuditPersistence.Persisted, attempt);
            if (!accepted) return new IdentityUpdate(outcome, new[] { identity }, new[] { fact });
            var guard = new AuthorizationCommitGuard(lease!, () =>
            {
                lock (_grantSync) if (reserved is not null) reserved.State = GrantState.Consumed;
            }, () =>
            {
                lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
            });
            transferred = true;
            return new IdentityUpdate(outcome, new[] { identity }, new[] { fact }, guard,
                CalibrationGovernance: new CalibrationGovernanceMutation(decision!.Record!));
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

    internal ValueTask<string?> CheckCalibrationGovernanceQueryAuthorizationAsync(CommandInvocation invocation,
        CancellationToken cancellationToken) => QueryAsync(async token =>
        {
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            if (!TryLease(invocation, out var lease, out var reason)) return reason;
            using (lease)
            {
                var actor = Find(state, lease!.Identity.PrincipalId);
                return actor is { Enabled: true } && actor.Permissions.Any(permission => permission is
                    Permission.PublishCalibration or Permission.ManageCalibrationAcceptancePolicy or
                    Permission.RecordPhysicalCalibrationVerification or Permission.RunCalibration) ? null : "PermissionDenied";
            }
        }, reason => reason, cancellationToken);
}

internal sealed record CalibrationGovernancePreparation(CalibrationCandidateReference? VerifiedCandidate = null,
    string? SourceImagesFailure = null, PhysicalVerificationComputation? Verification = null);
