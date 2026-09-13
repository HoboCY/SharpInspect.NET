using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

/// <summary>
/// Identity-side admission for manual production recovery.  The PLC cleanup is
/// intentionally outside this class; an accepted result is only the durable
/// authorization fact that permits the caller to perform that cleanup.
/// </summary>
internal sealed partial class LocalAuthorizationService
{
    /// <summary>
    /// Authorizes one manual recovery attempt and hands the recovery marker to
    /// the same SQLite writer transaction as the identity and command facts.
    /// The caller token is observed by the callback, while the writer itself is
    /// allowed to finish the durable rejection/authorization after cancellation.
    /// </summary>
    internal async ValueTask<ProductionRecoveryAuthorizationResult> AuthorizeProductionRecoveryAsync(
        ManualProductionRecoveryCommand command,
        Guid runtimeEpoch,
        ProductionRecoverySafetyCapture? safetyCapture,
        ProductionRecoveryObservation? observation,
        string? forcedRejection,
        Func<string?>? finalGuard,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var attemptId = Guid.NewGuid();
        RuntimeCommandOutcome Unavailable(string reason) => new(command.CorrelationId,
            CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, attemptId);

        if (command.CorrelationId == Guid.Empty || runtimeEpoch == Guid.Empty)
            return new(Unavailable("InvalidCommandContext"), RecoveryAttemptId: attemptId);
        if (!_store.ProductionRecoveryEnabled)
            return new(Unavailable("ProductionRecoveryConfigurationRequired"), RecoveryAttemptId: attemptId);

        try
        {
            var deadline = new StoreDeadline(_store.CommitTimeout);
            while (true)
            {
                var written = await _store.UpdateIdentityCommandAsync(command.CorrelationId,
                    (state, duplicate) => AuthorizeProductionRecovery(state, command, runtimeEpoch,
                        attemptId, safetyCapture, observation, forcedRejection, finalGuard,
                        duplicate, cancellationToken), CancellationToken.None, deadline).ConfigureAwait(false);
                // Writer 已回滚并释放未消费的 Step-Up 租约；只在原 attempt/deadline 内重试
                // 这个瞬时栅栏，已提交授权绝不在此重复或执行物理清理。
                if (!written.Committed && written.ReasonCode == "ProductionRecoveryCommitFenceBusy" && !deadline.Expired)
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    continue;
                }
                if (written.Committed && written.Result is ProductionRecoveryAuthorizationResult result)
                    return result;
                return new(Unavailable(written.Committed ? "ProductionRecoveryAuthorizationResultMissing" :
                    written.ReasonCode), RecoveryAttemptId: attemptId);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var reason = exception.Message.StartsWith("ProductionRecovery", StringComparison.Ordinal)
                ? exception.Message : "ProductionRecoveryAuditUnavailable";
            return new(Unavailable(reason), RecoveryAttemptId: attemptId);
        }
    }

    private IdentityUpdate AuthorizeProductionRecovery(
        IdentityAuthorityState state,
        ManualProductionRecoveryCommand command,
        Guid runtimeEpoch,
        Guid attemptId,
        ProductionRecoverySafetyCapture? safetyCapture,
        ProductionRecoveryObservation? observation,
        string? forcedRejection,
        Func<string?>? finalGuard,
        bool duplicate,
        CancellationToken callerCancellation)
    {
        SessionAuthorizationLease? lease = null;
        LocalAdministratorState? actor = null;
        StepUpGrant? reserved = null;
        var transferred = false;

        try
        {
            var reason = "Authorized";
            if (!TryLease(command.Invocation, out lease, out var leaseReason))
                reason = leaseReason;
            if (lease is not null)
                actor = Find(state, lease.Identity.PrincipalId);
            if (reason == "Authorized" && command.Invocation.Source != CommandSource.PhysicalConsole)
                reason = "PhysicalConsoleRequired";
            if (reason == "Authorized" && (actor is not { Enabled: true } ||
                    !actor.Permissions.Contains(Permission.ManualRecovery)))
                reason = "PermissionDenied";
            if (reason == "Authorized" && duplicate)
                reason = "DuplicateCorrelationId";
            // Runtime 预检失败在身份边界之后具有权威性；保持原因稳定，不能让缺少可选
            // capture 或 observation 覆盖它。
            if (reason == "Authorized" && forcedRejection is not null)
                reason = forcedRejection;
            if (reason == "Authorized" && runtimeEpoch == Guid.Empty)
                reason = "ProductionRecoveryRuntimeUnavailable";
            if (reason == "Authorized" && observation is null)
                reason = "ProductionRecoveryObservationMissing";
            // observation 绑定原始已接纳 inspection 的 epoch。重启后的恢复会有新的
            // runtimeEpoch；Storage writer 将旧 observation epoch 与不可变 Admission 行比较。
            if (reason == "Authorized" && safetyCapture is null)
                reason = "ProductionRecoverySafetyCaptureUnavailable";
            if (reason == "Authorized" && safetyCapture is not null)
                reason = ValidateSafetyCapture(safetyCapture, runtimeEpoch);
            if (reason == "Authorized" && callerCancellation.IsCancellationRequested)
                reason = "ProductionRecoveryCancelled";

            var binding = Binding(command);
            if (reason == "Authorized")
                reason = CheckManualRecoveryGrant(command, binding, actor!, lease!.SessionId,
                    reserve: false, out _);
            if (reason == "Authorized")
                reason = CheckManualRecoveryGrant(command, binding, actor!, lease!.SessionId,
                    reserve: true, out reserved);

            var now = _utcNow().ToUniversalTime();
            if (now < state.LastObservedUtc) now = state.LastObservedUtc;
            var safetyEvidence = TryCreateSafetyEvidence(command, attemptId, safetyCapture,
                out var evidenceReason);
            if (safetyEvidence is null && reason == "Authorized")
                reason = evidenceReason ?? "ProductionRecoverySafetyEvidenceInvalid";

            var accepted = reason == "Authorized";
            var effectiveReason = accepted ? "ProductionRecoveryAuthorized" : reason;
            var fact = new CommandAuditFact(Guid.NewGuid(), attemptId, command.CorrelationId,
                runtimeEpoch, now, AuditedCommandKind.ManualProductionRecovery,
                command.Invocation is { } invocation && Enum.IsDefined(invocation.Source)
                    ? invocation.Source : null,
                command.Invocation?.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
                command.Invocation?.SessionId, command.Invocation?.StepUpGrantId,
                CommandAuditPhase.Outcome,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected,
                effectiveReason, actor?.PrincipalId.ToString("D"));

            // Production recovery 失败与成功授权使用同样有界且带版本的安全封套；这样重试
            // 和拒绝尝试都可审计，无需新增身份列。
            var identityKind = accepted ? IdentityEventKind.ProductionRecoveryAuthorized :
                safetyEvidence is not null ? IdentityEventKind.ProductionRecoveryFailed :
                IdentityEventKind.ManagementRejected;
            var identity = AuthorizationEvent(state, identityKind,
                effectiveReason, ValidBinding(binding) ? binding : null,
                actor?.PrincipalId, lease?.SessionId, command.Invocation?.StepUpGrantId,
                command.CorrelationId, actor?.AuthorizationRevision ?? 0,
                capturedTime: now) with
            {
                OperationId = command.InspectionId,
                RecoverySafetyEvidence = safetyEvidence
            };

            var outcome = new RuntimeCommandOutcome(command.CorrelationId,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected,
                effectiveReason, AuditPersistence.Persisted, attemptId);
            var result = new ProductionRecoveryAuthorizationResult(outcome, fact,
                RecoveryAttemptId: attemptId);
            if (!accepted)
                return new IdentityUpdate(result, new[] { identity }, new[] { fact });

            var write = new ProductionRecoveryWriteRequest(command, runtimeEpoch, attemptId,
                safetyCapture!, observation!, actor!.PrincipalId, lease!.SessionId,
                actor.AuthorizationRevision, command.Invocation!.StepUpGrantId!.Value,
                new RecipeContractReference(_options.AuthorizationPolicy.Id,
                    _options.AuthorizationPolicy.Version, _options.AuthorizationPolicy.ContentHash),
                now, finalGuard);
            var guard = new AuthorizationCommitGuard(lease!, () =>
            {
                lock (_grantSync)
                {
                    if (reserved is not null) reserved.State = GrantState.Consumed;
                }
            }, () =>
            {
                lock (_grantSync)
                {
                    if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
                }
            });
            transferred = true;
            return new IdentityUpdate(result, new[] { identity }, new[] { fact }, guard,
                ProductionRecovery: write);
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

    private static string ValidateSafetyCapture(ProductionRecoverySafetyCapture capture,
        Guid runtimeEpoch)
    {
        if (capture.RuntimeEpoch != runtimeEpoch)
            return "ProductionRecoverySafetyCaptureStale";
        if (!capture.Available || capture.Observation is null)
            return capture.ReasonCode is { Length: > 0 }
                ? capture.ReasonCode : "ProductionRecoverySafetyCaptureUnavailable";
        var observation = capture.Observation;
        if (!observation.IsSafeLineStopped)
            return "ProductionRecoverySafetyStopRequired";
        if (observation.SourceEpoch != capture.Source.SourceEpoch ||
            observation.SourceGeneration != capture.Source.SourceGeneration)
            return "ProductionRecoverySafetyCaptureStale";
        if (!capture.Source.Available)
            return "ProductionRecoverySafetySourceUnavailable";
        return "Authorized";
    }

    private static string? TryCreateSafetyEvidence(ManualProductionRecoveryCommand command,
        Guid attemptId, ProductionRecoverySafetyCapture? capture, out string? reason)
    {
        reason = null;
        if (capture is null)
        {
            reason = "ProductionRecoverySafetyCaptureUnavailable";
            return null;
        }
        try
        {
            return IdentityAuditEvent.CreateProductionRecoverySafetyEvidence(attemptId,
                command.CorrelationId, command.InspectionId, command.ExpectedEventHash,
                command.ReasonCode, command.Disposition.ToString(), command.DispositionNote,
                capture);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reason = exception.Message.StartsWith("ProductionRecovery", StringComparison.Ordinal)
                ? exception.Message : "ProductionRecoverySafetyEvidenceInvalid";
            return null;
        }
    }

    /// <summary>
    /// Manual recovery always consumes an exact fresh Step-Up grant.  The
    /// authorization policy's optional-step-up setting is deliberately not
    /// consulted here, and the binding includes the complete recovery target.
    /// </summary>
    private string CheckManualRecoveryGrant(ManualProductionRecoveryCommand command,
        StepUpBinding binding, LocalAdministratorState actor, Guid sessionId, bool reserve,
        out StepUpGrant? grant)
    {
        grant = null;
        if (!ValidBinding(binding) || binding.Permission != Permission.ManualRecovery ||
            binding.CommandKind != AuditedCommandKind.ManualProductionRecovery)
            return "StepUpBindingInvalid";
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
