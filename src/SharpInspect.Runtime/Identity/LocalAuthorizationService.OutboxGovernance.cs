using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

/// <summary>
/// Identity-side admission for the governed schema-37 outbox operations. The full actor,
/// session, permission, policy, target, reason and fresh Step-Up binding is re-checked inside
/// the same writer transaction that appends the command fact and the operation record, so a
/// caller-supplied value can never be reinterpreted after the fact. An accepted result is the
/// durable authorization fact; the store mutation persists the exact operation in that same
/// transaction.
/// </summary>
internal sealed partial class LocalAuthorizationService
{
    internal async ValueTask<OutboxGovernanceAuthorizationResult> AuthorizeOutboxGovernanceAsync(
        RuntimeCommand command, Guid runtimeEpoch, StoreDeadline deadline, CancellationToken cancellationToken,
        Func<OutboxDelivery, string?>? dependencyGuard = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        var attempt = Guid.NewGuid();
        OutboxGovernanceAuthorizationResult Unavailable(string reason) => new(new RuntimeCommandOutcome(
            command.CorrelationId, CommandDisposition.Rejected, reason, AuditPersistence.Unavailable,
            attempt), null, null);
        if (command.CorrelationId == Guid.Empty || runtimeEpoch == Guid.Empty)
            return Unavailable("InvalidCommandContext");
        if (!_store.ProductionOutboxRecoveryEnabled)
            return Unavailable("OutboxGovernanceConfigurationRequired");
        if (command is not (RecoverOutboxDeliveryCommand or CreateCorrectiveOutboxDeliveryCommand))
            return Unavailable("OutboxGovernanceCommandInvalid");
        try
        {
            var written = await _store.UpdateOutboxGovernanceCommandAsync(command,
                (identity, state, duplicate) => AuthorizeOutboxGovernance(identity, state, command,
                    runtimeEpoch, attempt, duplicate, cancellationToken, dependencyGuard),
                CancellationToken.None, deadline).ConfigureAwait(false);
            if (!written.Committed) return Unavailable(written.ReasonCode);
            return written.Result switch
            {
                RuntimeCommandOutcome outcome => new(outcome, null, null),
                ProductionOutboxRecoveryResult recovery => new(recovery.Outcome, recovery, null),
                ProductionOutboxCorrectionResult correction => new(correction.Outcome, null, correction),
                _ => Unavailable("OutboxGovernanceResultMissing")
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var reason = exception.Message.StartsWith("Outbox", StringComparison.Ordinal)
                ? exception.Message : "OutboxGovernanceAuditUnavailable";
            return Unavailable(reason);
        }
    }

    private IdentityUpdate AuthorizeOutboxGovernance(IdentityAuthorityState identity,
        OutboxGovernanceCommandState state, RuntimeCommand command, Guid runtimeEpoch, Guid attempt,
        bool duplicate, CancellationToken cancellation, Func<OutboxDelivery, string?>? dependencyGuard)
    {
        SessionAuthorizationLease? lease = null;
        LocalAdministratorState? actor = null;
        StepUpGrant? reserved = null;
        var transferred = false;
        try
        {
            var recoveryCommand = command as RecoverOutboxDeliveryCommand;
            var correctionCommand = command as CreateCorrectiveOutboxDeliveryCommand;
            var reason = "Authorized";
            if (!TryLease(command.Invocation, out lease, out var leaseReason)) reason = leaseReason;
            if (lease is not null) actor = Find(identity, lease.Identity.PrincipalId);
            if (reason == "Authorized" && state.Options is null)
                reason = "OutboxGovernanceConfigurationRequired";
            if (reason == "Authorized" && (actor is not { Enabled: true } ||
                    !actor.Permissions.Contains(RequiredPermission(command)))) reason = "PermissionDenied";
            if (reason == "Authorized" && duplicate) reason = "DuplicateCorrelationId";
            if (reason == "Authorized" && runtimeEpoch == Guid.Empty)
                reason = "OutboxGovernanceRuntimeUnavailable";
            if (reason == "Authorized" && cancellation.IsCancellationRequested)
                reason = "OutboxGovernanceCancelled";
            if (reason == "Authorized" && command.Invocation.StepUpGrantId is null) reason = "StepUpRequired";
            if (reason == "Authorized")
                reason = CheckGrant(command, actor!, lease!.SessionId, reserve: false, out _);

            OutboxRecoveryMutation? recovery = null;
            OutboxCorrectionMutation? correction = null;
            if (reason == "Authorized" && recoveryCommand is not null)
                reason = PrepareOutboxRecovery(state, recoveryCommand, actor!, lease!, runtimeEpoch, out recovery);
            if (reason == "Authorized" && recoveryCommand is not null)
                reason = dependencyGuard?.Invoke(state.Deliveries.Single(x =>
                    x.Delivery.DeliveryId == recoveryCommand.DeliveryId).Delivery) ??
                    (dependencyGuard is null ? "OutboxHistoricalHandlerUnavailable" : "Authorized");
            if (reason == "Authorized" && correctionCommand is not null)
                reason = PrepareOutboxCorrection(state, correctionCommand, actor!, lease!, runtimeEpoch, out correction);

            var now = _utcNow().ToUniversalTime();
            if (now < identity.LastObservedUtc) now = identity.LastObservedUtc;
            if (reason == "Authorized") reason = CheckGrant(command, actor!, lease!.SessionId, reserve: true, out reserved);
            if (reason == "Authorized" && cancellation.IsCancellationRequested) reason = "OutboxGovernanceCancelled";
            if (reason != "Authorized") { recovery = null; correction = null; }
            else
            {
                if (recovery is not null) recovery = recovery with { RecordedAtUtc = now };
                if (correction is not null) correction = correction with { RecordedAtUtc = now };
            }
            var accepted = reason == "Authorized";
            var effectiveReason = accepted
                ? (recoveryCommand is not null ? "OutboxRecoveryAuthorized" : "OutboxCorrectionCreated")
                : reason;
            var binding = Binding(command);
            var operationBinding = recovery is not null
                ? SqliteCommandStore.ProductionOutboxRecoveryOperationBinding(recovery)
                : correction is not null
                    ? SqliteCommandStore.ProductionOutboxCorrectionOperationBinding(correction)
                    : null;
            var fact = new CommandAuditFact(Guid.NewGuid(), attempt, command.CorrelationId, runtimeEpoch, now,
                CommandKind(command), command.Invocation is { } invocation && Enum.IsDefined(invocation.Source)
                    ? invocation.Source : null,
                command.Invocation?.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
                command.Invocation?.SessionId, command.Invocation?.StepUpGrantId, CommandAuditPhase.Outcome,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected, effectiveReason,
                actor?.PrincipalId.ToString("D"));
            var authorization = AuthorizationEvent(identity,
                !accepted ? IdentityEventKind.ManagementRejected : recoveryCommand is not null ? IdentityEventKind.OutboxDeliveryRecovered :
                    IdentityEventKind.OutboxCorrectiveDeliveryCreated,
                effectiveReason, ValidBinding(binding) ? binding : null, actor?.PrincipalId,
                lease?.SessionId, command.Invocation?.StepUpGrantId, command.CorrelationId,
                actor?.AuthorizationRevision ?? 0, capturedTime: now) with
            {
                OperationId = accepted ? command.CorrelationId : null,
                RecoverySafetyEvidence = operationBinding
            };
            var placeholder = new RuntimeCommandOutcome(command.CorrelationId,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected, effectiveReason,
                AuditPersistence.Persisted, attempt);
            if (!accepted) return new IdentityUpdate(placeholder, new[] { authorization }, new[] { fact });

            var guard = new AuthorizationCommitGuard(lease!, () =>
            {
                lock (_grantSync) if (reserved is not null) reserved.State = GrantState.Consumed;
            }, () =>
            {
                lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
            });
            transferred = true;
            return new IdentityUpdate(placeholder, new[] { authorization }, new[] { fact }, guard,
                OutboxRecovery: recovery, OutboxCorrection: correction);
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

    private string PrepareOutboxRecovery(OutboxGovernanceCommandState state,
        RecoverOutboxDeliveryCommand command, LocalAdministratorState actor, SessionAuthorizationLease lease,
        Guid runtimeEpoch, out OutboxRecoveryMutation? mutation)
    {
        mutation = null;
        if (state.Recovery is not { } limits || state.Options is null)
            return "OutboxGovernanceConfigurationRequired";
        if (command.DeliveryId == Guid.Empty || !IsHash(command.ExpectedDeliveryContentHash) ||
            command.RequestedAttempts is < 1 or > 16 || !IsSafeReason(command.Reason) ||
            !IsSafeTarget(command.AuthorizationTarget))
            return "OutboxGovernanceInputInvalid";
        var delivery = state.Deliveries.SingleOrDefault(value =>
            value.Delivery.DeliveryId == command.DeliveryId);
        if (delivery is null) return "OutboxRecoveryDeliveryMissing";
        if (delivery.Delivery.ContentHash != command.ExpectedDeliveryContentHash)
            return "OutboxRecoveryDeliveryChanged";
        if (delivery.Delivery.Payload is null) return "OutboxRecoveryUnsendableObligation";
        if (!state.States.TryGetValue(command.DeliveryId, out var workState))
            return "OutboxRecoveryStateMissing";
        if (command.ExpectedStateRevisionHash != SqliteCommandStore.ProductionOutboxStateRevision(
                delivery.Delivery, workState.LastEventContentHash, state.Recoveries, state.Corrections))
            return "OutboxRecoveryStateChanged";
        if (workState.State == OutboxDeliveryState.Succeeded) return "OutboxRecoveryAlreadySucceeded";
        if (workState.ActiveAttemptId is not null) return "OutboxRecoveryAttemptActive";
        if (!workState.PermanentBlock && workState.RetryEligible) return "OutboxRecoveryNotBlocked";
        if (command.RequestedAttempts > limits.MaximumGrantedAttempts)
            return "OutboxRecoveryGrantExceeded";
        var granted = state.Recoveries.Where(value => value.DeliveryId == command.DeliveryId)
            .Sum(value => value.GrantedAttempts);
        if (granted + command.RequestedAttempts > limits.MaximumCumulativeGrantedAttempts ||
            delivery.Delivery.MaximumAttempts + granted + command.RequestedAttempts >
                ProductionOutboxStoreOptions.MaximumAttemptsHardLimit)
            return "OutboxRecoveryCumulativeGrantExceeded";
        if (state.Recoveries.Count >= limits.MaximumRecoveryOperations)
            return "OutboxRecoveryOperationCapacityExceeded";
        mutation = new OutboxRecoveryMutation(Guid.NewGuid(), command.DeliveryId, command.RequestedAttempts,
            command.Reason, actor.PrincipalId, lease.SessionId, command.Invocation!.StepUpGrantId!.Value,
            _options.AuthorizationPolicy.Id, _options.AuthorizationPolicy.Version,
            _options.AuthorizationPolicy.ContentHash, command.CorrelationId, DateTimeOffset.UtcNow,
            command.AuthorizationTarget);
        return "Authorized";
    }

    private string PrepareOutboxCorrection(OutboxGovernanceCommandState state,
        CreateCorrectiveOutboxDeliveryCommand command, LocalAdministratorState actor,
        SessionAuthorizationLease lease, Guid runtimeEpoch, out OutboxCorrectionMutation? mutation)
    {
        mutation = null;
        if (state.Recovery is not { } limits || state.Options is null)
            return "OutboxGovernanceConfigurationRequired";
        if (command.SourceDeliveryId == Guid.Empty || !IsHash(command.ExpectedSourceDeliveryContentHash) ||
            !IsSafeReason(command.Reason) || !IsSafeTarget(command.AuthorizationTarget))
            return "OutboxGovernanceInputInvalid";
        if (command.PayloadBytes.Length is < 1 ||
            command.PayloadBytes.Length > limits.MaximumCorrectionPayloadBytes ||
            command.PayloadBytes.Length > state.Options.MaximumPayloadBytes)
            return "OutboxCorrectionPayloadCapacityExceeded";
        var source = state.Deliveries.SingleOrDefault(value =>
            value.Delivery.DeliveryId == command.SourceDeliveryId);
        if (source is null) return "OutboxCorrectionSourceMissing";
        if (source.Delivery.ContentHash != command.ExpectedSourceDeliveryContentHash)
            return "OutboxCorrectionSourceChanged";
        if (!state.States.TryGetValue(command.SourceDeliveryId, out var sourceState)) return "OutboxCorrectionStateMissing";
        if (command.ExpectedStateRevisionHash != SqliteCommandStore.ProductionOutboxStateRevision(
                source.Delivery, sourceState.LastEventContentHash, state.Recoveries, state.Corrections))
            return "OutboxCorrectionStateChanged";
        if (sourceState.ActiveAttemptId is not null) return "OutboxCorrectionAttemptActive";
        if (!sourceState.PermanentBlock && sourceState.RetryEligible) return "OutboxCorrectionNotBlocked";
        if (command.ContentType != source.Delivery.Route.ContentType)
            return "OutboxCorrectionContentTypeMismatch";
        if (command.PayloadBytes.Length > source.Delivery.Route.MaximumPayloadBytes)
            return "OutboxCorrectionPayloadCapacityExceeded";
        if (state.Corrections.Count >= limits.MaximumCorrections)
            return "OutboxCorrectionCapacityExceeded";
        if (sourceState.State == OutboxDeliveryState.Succeeded)
            return "OutboxCorrectionSourceSucceeded";
        var byDelivery = state.Corrections.ToDictionary(value => value.DeliveryId);
        var depth = 0;
        var cursor = command.SourceDeliveryId;
        while (byDelivery.TryGetValue(cursor, out var link))
        {
            if (++depth > 8) return "OutboxCorrectionChainExceeded";
            cursor = link.SourceDeliveryId;
        }
        var payload = command.PayloadBytes.ToArray();
        mutation = new OutboxCorrectionMutation(Guid.NewGuid(), command.SourceDeliveryId, command.Reason,
            payload, actor.PrincipalId, lease.SessionId, command.Invocation!.StepUpGrantId!.Value,
            _options.AuthorizationPolicy.Id, _options.AuthorizationPolicy.Version,
            _options.AuthorizationPolicy.ContentHash, command.CorrelationId, DateTimeOffset.UtcNow,
            command.AuthorizationTarget);
        return "Authorized";
    }

    private static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(character => Uri.IsHexDigit(character));

    private static bool IsSafeReason(string? value) => value is { Length: > 0 and <= 256 } &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private static bool IsSafeTarget(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-');

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));
}
