using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal ValueTask<PlcResultContractAccess> GetPlcResultContractAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken) => QueryAsync(async token =>
        {
            if (_store.PlcResultContractOptions is null)
                return new PlcResultContractAccess(false, "PlcResultContractConfigurationRequired");
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            if (!TryLease(invocation, out var lease, out var reason)) return new(false, reason);
            using (lease)
            {
                var actor = Find(state, lease!.Identity.PrincipalId);
                return actor is { Enabled: true } && actor.Permissions.Contains(Permission.ManagePlcResultContract)
                    ? new(true, "PlcResultContractAccessAvailable")
                    : new(false, "PermissionDenied");
            }
        }, reason => new(false, reason), cancellationToken);

    internal async ValueTask<PlcResultContractChangeResult> ChangePlcResultContractAsync(
        ChangePlcResultContractCommand command, Guid epoch, PlcResultContractPreparation preparation,
        Func<string?> runtimeBlocker, StoreDeadline deadline, CancellationToken callerCancellation)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(runtimeBlocker);
        var attempt = Guid.NewGuid();
        PlcResultContractChangeResult Unavailable(string reason) => new(
            new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Rejected, reason,
                AuditPersistence.Unavailable, attempt));
        try
        {
            var committed = await _store.UpdatePlcResultContractCommandAsync(command,
                (state, contracts, duplicate) => AuthorizePlcResultContractChange(state, contracts, command,
                    epoch, attempt, preparation, runtimeBlocker, duplicate, callerCancellation),
                CancellationToken.None, deadline).ConfigureAwait(false);
            return committed.Committed && committed.Result is PlcResultContractChangeResult result
                ? result : Unavailable(committed.ReasonCode);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Unavailable("PlcResultContractAuditUnavailable"); }
    }

    private IdentityUpdate AuthorizePlcResultContractChange(IdentityAuthorityState state,
        PlcResultContractCommandState contracts, ChangePlcResultContractCommand command, Guid epoch,
        Guid attempt, PlcResultContractPreparation preparation, Func<string?> runtimeBlocker,
        bool duplicate, CancellationToken callerCancellation)
    {
        SessionAuthorizationLease? lease = null;
        LocalAdministratorState? actor = null;
        StepUpGrant? reserved = null;
        var transferred = false;
        try
        {
            var reason = contracts.Enabled && _store.PlcResultContractOptions is not null
                ? "Authorized" : "PlcResultContractConfigurationRequired";
            if (reason == "Authorized" && !TryLease(command.Invocation, out lease, out var leaseReason)) reason = leaseReason;
            if (lease is not null) actor = Find(state, lease.Identity.PrincipalId);
            if (reason == "Authorized" && (actor is not { Enabled: true } ||
                !actor.Permissions.Contains(Permission.ManagePlcResultContract))) reason = "PermissionDenied";
            if (reason == "Authorized" && duplicate) reason = "DuplicateCorrelationId";
            if (reason == "Authorized" && epoch == Guid.Empty) reason = "PlcResultContractRuntimeUnavailable";
            if (reason == "Authorized" && callerCancellation.IsCancellationRequested) reason = "PlcResultContractChangeCancelled";
            if (reason == "Authorized" && command.ExpectedCurrent != contracts.Revisions.LastOrDefault()?.Reference)
                reason = "PlcResultContractCurrentConflict";
            if (reason == "Authorized" && preparation.Failure is not null) reason = preparation.Failure;
            if (reason == "Authorized" && (preparation.ReleaseHighWatermark is null ||
                preparation.ReleaseHighWatermark.Value != contracts.ReleaseHighWatermark))
                reason = "PlcResultContractReleaseSnapshotChanged";
            if (reason == "Authorized" && (preparation.SchemaValidations.Count == 0 ||
                preparation.SchemaValidations.Any(value => value.Contract.ContentHash != command.Proposal.ContentHash) ||
                preparation.Bindings.Any(value => value.Binding.Contract.ContentHash != command.Proposal.ContentHash)))
                reason = "PlcResultContractPreparationInvalid";
            if (reason == "Authorized" && contracts.Revisions.Any(value =>
                value.Contract.Id == command.Proposal.Id && value.Contract.Version == command.Proposal.Version))
                reason = "PlcResultContractVersionConflict";
            if (reason == "Authorized") reason = runtimeBlocker() ?? "Authorized";
            if (reason == "Authorized" && command.Invocation.StepUpGrantId is null) reason = "StepUpRequired";
            if (reason == "Authorized") reason = CheckGrant(command, actor!, lease!.SessionId, reserve: true, out reserved);

            var time = _utcNow().ToUniversalTime();
            if (time < state.LastObservedUtc) time = state.LastObservedUtc;
            if (contracts.Revisions.LastOrDefault() is { } previous && time < previous.RecordedAtUtc)
                time = previous.RecordedAtUtc;
            PlcResultContractRevision? revision = null;
            if (reason == "Authorized")
            {
                revision = new PlcResultContractRevision(contracts.Revisions.Count + 1L, Guid.NewGuid(),
                    command.CorrelationId, command.Proposal, command.ExpectedCurrent,
                    preparation.ReleaseHighWatermark!.Value, preparation.SchemaValidations, preparation.Bindings,
                    actor!.PrincipalId, lease!.SessionId, actor.AuthorizationRevision,
                    command.Invocation.StepUpGrantId!.Value,
                    new(_options.AuthorizationPolicy.Id, _options.AuthorizationPolicy.Version,
                        _options.AuthorizationPolicy.ContentHash), command.ChangeReason, command.AuthorizationTarget, time);
                reason = "PlcResultContractChanged";
            }

            var accepted = revision is not null;
            var fact = new CommandAuditFact(Guid.NewGuid(), attempt, command.CorrelationId, epoch, time,
                AuditedCommandKind.ChangePlcResultContract,
                Enum.IsDefined(command.Invocation.Source) ? command.Invocation.Source : null,
                command.Invocation.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
                command.Invocation.SessionId, command.Invocation.StepUpGrantId, CommandAuditPhase.Outcome,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected, reason, actor?.PrincipalId.ToString("D"));
            var binding = Binding(command);
            var identity = AuthorizationEvent(state,
                accepted ? IdentityEventKind.PlcResultContractChanged : IdentityEventKind.ManagementRejected,
                reason, binding, actor?.PrincipalId, lease?.SessionId, command.Invocation.StepUpGrantId,
                command.CorrelationId, actor?.AuthorizationRevision ?? 0, capturedTime: time) with
            { OperationId = accepted ? command.CorrelationId : null };
            var result = new PlcResultContractChangeResult(
                new RuntimeCommandOutcome(command.CorrelationId, fact.Disposition!.Value, reason,
                    AuditPersistence.Persisted, attempt), revision);
            if (!accepted) return new IdentityUpdate(result, new[] { identity }, new[] { fact });
            var guard = new AuthorizationCommitGuard(lease!, () =>
            {
                lock (_grantSync) if (reserved is not null) reserved.State = GrantState.Consumed;
            }, () =>
            {
                lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
            });
            transferred = true;
            return new IdentityUpdate(result, new[] { identity }, new[] { fact }, guard,
                PlcResultContract: new PlcResultContractMutation(revision!));
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
