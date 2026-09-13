using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    public async ValueTask<StepUpResult> ReauthenticateAsync(StepUpRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0) return new(false, "AuthorizationUnavailable");
        if (!_authenticationSlots.Wait(0)) return new(false, "StepUpCapacityExceeded");
        Task<AuthenticationResult>? providerTask = null;
        LocalAdministratorState? previous = null;
        try
        {
            if (request.Binding?.CommandKind is >= AuditedCommandKind.ImportCalibrationPackage and <= AuditedCommandKind.PublishImportedCalibration &&
                !_store.CalibrationImportEnabled)
                return await RejectStepUpAsync(request, "CalibrationImportConfigurationRequired", null, cancellationToken).ConfigureAwait(false);
            if (request.Binding?.CommandKind == AuditedCommandKind.ReleaseRecipe && _store.RecipeReleaseOptions is null)
                return await RejectStepUpAsync(request, "RecipeReleaseConfigurationRequired", null, cancellationToken).ConfigureAwait(false);
            if (request.Binding?.CommandKind == AuditedCommandKind.ChangePlcResultContract &&
                _store.PlcResultContractOptions is null)
                return await RejectStepUpAsync(request, "PlcResultContractConfigurationRequired", null, cancellationToken).ConfigureAwait(false);
            if (request.Binding?.CommandKind == AuditedCommandKind.ActivateRecipe &&
                _store.RecipeActivationOptions is null)
                return await RejectStepUpAsync(request, "RecipeActivationConfigurationRequired", null, cancellationToken).ConfigureAwait(false);
            if (request.Binding?.CommandKind == AuditedCommandKind.SelectHistoricalCalibration &&
                !_store.CalibrationImportEnabled)
                return await RejectStepUpAsync(request, "HistoricalCalibrationConfigurationRequired", null, cancellationToken).ConfigureAwait(false);
            if (request.Binding?.CommandKind is (AuditedCommandKind.StartPreview or AuditedCommandKind.Tune or
                AuditedCommandKind.Freeze or AuditedCommandKind.Exit) && _store.PreviewSessionOptions is null)
                return await RejectStepUpAsync(request, "PreviewSessionConfigurationRequired", null, cancellationToken).ConfigureAwait(false);
            if (request.Binding?.CommandKind is (AuditedCommandKind.StartManualInspectionSession or
                AuditedCommandKind.RunManualInspection or AuditedCommandKind.ExitManualInspectionSession) &&
                _store.ManualInspectionOptions is null)
                return await RejectStepUpAsync(request, "ManualInspectionConfigurationRequired", null, cancellationToken).ConfigureAwait(false);
            if (request.Binding?.CommandKind is (AuditedCommandKind.StartStationQualificationSession or
                AuditedCommandKind.ExitStationQualificationSession) && !_store.StationQualificationEnabled)
                return await RejectStepUpAsync(request, "StationQualificationConfigurationRequired", null, cancellationToken).ConfigureAwait(false);
            if (request.Binding?.CommandKind is >= AuditedCommandKind.ReplaceRecipeTrustStore and <= AuditedCommandKind.ImportRecipeTransfer &&
                !_store.RecipeTransferEnabled)
                return await RejectStepUpAsync(request, "RecipeTransferConfigurationRequired", null, cancellationToken).ConfigureAwait(false);
            if (request.Binding?.CommandKind == AuditedCommandKind.PublishTraceStoragePolicy && !_store.TraceStoragePolicyEnabled)
                return await RejectStepUpAsync(request, "TraceStoragePolicyConfigurationRequired", null, cancellationToken).ConfigureAwait(false);
            if (request.CorrelationId == Guid.Empty || request.Binding is null || !ValidBinding(request.Binding))
                return await RejectStepUpAsync(request, "StepUpBindingInvalid", null, cancellationToken).ConfigureAwait(false);
            var before = await _store.ReadIdentityAsync(cancellationToken).ConfigureAwait(false);
            if (!TryLease(request.Invocation, out var initialLease, out var reason))
                return await RejectStepUpAsync(request, reason, null, cancellationToken).ConfigureAwait(false);
            using (initialLease)
            {
                previous = Find(before, initialLease!.Identity.PrincipalId);
                if (previous is not { Enabled: true } || !previous.Permissions.Contains(request.Binding.Permission))
                    reason = "PermissionDenied";
                else reason = "Authorized";
            }
            if (reason != "Authorized")
                return await RejectStepUpAsync(request, reason, previous?.PrincipalId, cancellationToken).ConfigureAwait(false);

            providerTask = _provider.AuthenticateAsync(new(previous!.UserName, request.Password), cancellationToken).AsTask();
            var authentication = await providerTask.WaitAsync(_options.OperationTimeout, cancellationToken).ConfigureAwait(false);
            if (!authentication.Succeeded || authentication.Identity?.PrincipalId != previous.PrincipalId)
                return await RejectStepUpAsync(request, "StepUpAuthenticationRejected", previous.PrincipalId, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await WaitForAuditAsync(cancellationToken).ConfigureAwait(false);
            var committed = await _store.UpdateIdentityAsync(state =>
            {
                if (!TryLease(request.Invocation, out var lease, out var rejection))
                    return StepUpRejection(state, request, rejection, previous.PrincipalId);
                var transferred = false;
                try
                {
                    var actor = Find(state, lease!.Identity.PrincipalId);
                    if (actor is not { Enabled: true } || actor.CredentialId != previous.CredentialId ||
                        actor.AuthorizationRevision != previous.AuthorizationRevision ||
                        !actor.Permissions.Contains(request.Binding.Permission))
                        return StepUpRejection(state, request, "StepUpAuthorityChanged", previous.PrincipalId);
                    if (cancellationToken.IsCancellationRequested)
                        return StepUpRejection(state, request, "StepUpCancelled", actor.PrincipalId, cancelled: true);
                    var now = _utcNow();
                    var grant = new StepUpGrant { Id = Guid.NewGuid(), PrincipalId = actor.PrincipalId,
                        SessionId = lease.SessionId, CredentialId = actor.CredentialId, AuthorizationRevision = actor.AuthorizationRevision,
                        PolicyHash = _options.AuthorizationPolicy.ContentHash, Binding = request.Binding,
                        IssuedTimestamp = NowTicks(), ExpiresAtUtc = now + _options.AuthenticationPolicy.StepUpFreshness,
                        State = GrantState.Pending };
                    lock (_grantSync)
                    {
                        PurgeGrantsLocked();
                        if (_grants.Count >= MaximumGrants)
                            return StepUpRejection(state, request, "StepUpCapacityExceeded", actor.PrincipalId);
                        _grants.Add(grant.Id, grant);
                    }
                    // Grant 在持久化事务提交前仍是 Pending；只有 writer 调用 Commit 后才转为
                    // Active，回滚则删除。
                    var guard = new AuthorizationCommitGuard(lease, () =>
                    {
                        lock (_grantSync) if (_grants.TryGetValue(grant.Id, out var current)) current.State = GrantState.Active;
                    }, () => { lock (_grantSync) _grants.Remove(grant.Id); });
                    transferred = true;
                    var fact = AuthorizationEvent(state, IdentityEventKind.StepUpIssued, "StepUpIssued", request.Binding,
                        actor.PrincipalId, lease.SessionId, grant.Id, request.CorrelationId, actor.AuthorizationRevision);
                    return new IdentityUpdate(new StepUpResult(true, "StepUpIssued", grant.Id, grant.ExpiresAtUtc),
                        new[] { fact }, CommitGuard: guard);
                }
                finally { if (!transferred) lease?.Dispose(); }
            }, cancellationToken).ConfigureAwait(false);
            return committed.Committed && committed.Result is StepUpResult result ? result : new(false, "StepUpAuditUnavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await RejectStepUpAsync(request, "StepUpCancelled", previous?.PrincipalId, CancellationToken.None, cancelled: true).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return await RejectStepUpAsync(request, "StepUpAuthenticationTimeout", previous?.PrincipalId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return await RejectStepUpAsync(request, "StepUpUnavailable", previous?.PrincipalId, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            // Provider 若忽略取消，必须等实际任务结束才释放容量槽，不能让超时的调用伪装成已退出。
            if (providerTask is { IsCompleted: false })
                _ = providerTask.ContinueWith(task => { _ = task.Exception; _authenticationSlots.Release(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else _authenticationSlots.Release();
        }
    }

    private async Task<StepUpResult> RejectStepUpAsync(StepUpRequest request, string reason, Guid? actorId,
        CancellationToken cancellationToken, bool cancelled = false)
    {
        try
        {
            await WaitForAuditAsync(cancellationToken).ConfigureAwait(false);
            var committed = await _store.UpdateIdentityAsync(state => StepUpRejection(state, request, reason, actorId, cancelled),
                cancellationToken).ConfigureAwait(false);
            return committed.Committed && committed.Result is StepUpResult result ? result : new(false, "StepUpAuditUnavailable");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new(false, "StepUpAuditUnavailable"); }
    }

    private IdentityUpdate StepUpRejection(IdentityAuthorityState state, StepUpRequest request,
        string reason, Guid? actorId, bool cancelled = false)
    {
        var fact = AuthorizationEvent(state, cancelled ? IdentityEventKind.StepUpCancelled : IdentityEventKind.StepUpRejected,
            reason, ValidBinding(request.Binding) &&
                (request.Binding.CommandKind is not (>= AuditedCommandKind.ImportCalibrationPackage and <= AuditedCommandKind.PublishImportedCalibration) ||
                    _store.CalibrationImportEnabled) &&
                (request.Binding.CommandKind != AuditedCommandKind.ReleaseRecipe || _store.RecipeReleaseOptions is not null) &&
                (request.Binding.CommandKind != AuditedCommandKind.ChangePlcResultContract ||
                    _store.PlcResultContractOptions is not null) &&
                (request.Binding.CommandKind != AuditedCommandKind.ActivateRecipe ||
                    _store.RecipeActivationOptions is not null) &&
                (request.Binding.CommandKind != AuditedCommandKind.SelectHistoricalCalibration ||
                    _store.CalibrationImportEnabled) &&
                (request.Binding.CommandKind is not (AuditedCommandKind.StartPreview or AuditedCommandKind.Tune or
                    AuditedCommandKind.Freeze or AuditedCommandKind.Exit) ||
                    _store.PreviewSessionOptions is not null) &&
                (request.Binding.CommandKind is not (AuditedCommandKind.StartManualInspectionSession or
                    AuditedCommandKind.RunManualInspection or AuditedCommandKind.ExitManualInspectionSession) ||
                    _store.ManualInspectionOptions is not null) &&
                (request.Binding.CommandKind is not (>= AuditedCommandKind.ReplaceRecipeTrustStore and <= AuditedCommandKind.ImportRecipeTransfer) ||
                    _store.RecipeTransferEnabled) &&
                (request.Binding.CommandKind != AuditedCommandKind.PublishTraceStoragePolicy ||
                    _store.TraceStoragePolicyEnabled) ? request.Binding : null,
            actorId, actorId.HasValue ? request.Invocation?.SessionId : null,
            null, request.CorrelationId == Guid.Empty ? null : request.CorrelationId,
            actorId.HasValue ? Find(state, actorId.Value)?.AuthorizationRevision ?? 0 : 0);
        return new(new StepUpResult(false, reason), new[] { fact });
    }

    private IdentityAuditEvent AuthorizationEvent(IdentityAuthorityState state, IdentityEventKind kind, string reason,
        StepUpBinding? binding, Guid? actorId, Guid? sessionId, Guid? grantId, Guid? correlationId,
        long authorizationRevision, Guid? targetPrincipalId = null, IdentityManagementReason? managementReason = null,
        DateTimeOffset? capturedTime = null)
    {
        var now = capturedTime ?? _utcNow();
        if (now > state.LastObservedUtc) state.LastObservedUtc = now;
        return new IdentityAuditEvent(Guid.NewGuid(), kind, state.LastObservedUtc, state.StationId,
            targetPrincipalId ?? actorId, null, null, null, reason,
            PasswordPolicyVersion: _options.PasswordPolicy.Version, BlocklistId: _options.PasswordPolicy.Blocklist!.Id,
            BlocklistVersion: _options.PasswordPolicy.Blocklist.Version, HashBaselineVersion: _options.HashBaselineVersion,
            HashTargetCost: _options.Baseline.TargetIterations, SessionId: sessionId,
            ActorPrincipalId: actorId, CommandCorrelationId: correlationId, StepUpGrantId: grantId,
            RequiredPermission: binding?.Permission.ToString(), TargetPrincipalId: targetPrincipalId,
            AuthorizationRevision: authorizationRevision, ManagementReason: managementReason?.ToString(), ActionTargetId: binding?.TargetId,
            BoundCommandCorrelationId: binding?.CommandCorrelationId, ActionCommandKind: binding?.CommandKind.ToString());
    }

    private sealed class AuthorizationCommitGuard : IIdentityTransactionGuard
    {
        private SessionAuthorizationLease? _lease;
        private readonly Action _commit;
        private readonly Action _rollback;
        private bool _committed;
        internal AuthorizationCommitGuard(SessionAuthorizationLease lease, Action commit, Action rollback)
        { _lease = lease; _commit = commit; _rollback = rollback; }
        public void Commit() { _commit(); _committed = true; }
        public void Dispose()
        {
            if (_lease is null) return;
            try { if (!_committed) _rollback(); }
            finally { _lease.Dispose(); _lease = null; }
        }
    }
}
