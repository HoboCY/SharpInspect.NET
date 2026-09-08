using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal async ValueTask<RecipeDraftAccess> GetRecipeDraftAccessAsync(
        CommandInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (_store.RecipeDraftOptions is null || _sessions is null)
            return new(false, "RecipeDraftUnavailable", false);
        try
        {
            var state = await _store.ReadIdentityAsync(cancellationToken).ConfigureAwait(false);
            if (!TryLease(invocation, out var lease, out var reason))
                return new(false, reason, RequiresRecipeDraftStepUp());
            using (lease)
            {
                var actor = Find(state, lease!.Identity.PrincipalId);
                if (actor is not { Enabled: true } || !actor.Permissions.Contains(Permission.EditRecipeDraft))
                    return new(false, "PermissionDenied", RequiresRecipeDraftStepUp());
                return new(true, "RecipeDraftAccessAvailable", RequiresRecipeDraftStepUp());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(false, "RecipeDraftAuthorizationUnavailable", RequiresRecipeDraftStepUp()); }
    }

    internal ValueTask<RecipeDraftSaveResult> SaveRecipeDraftAsync(RecipeDraftSaveRequest request,
        RecipeDraftDocument document, StoreDeadline deadline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(document);
        if (_store.RecipeDraftOptions is null)
            return ValueTask.FromResult(new RecipeDraftSaveResult(false, "RecipeDraftUnavailable", null,
                Array.Empty<AlgorithmValidationIssue>()));

        return _store.SaveRecipeDraftAsync(request, document, (state, head, replay) =>
            EvaluateRecipeDraftSave(state, head, request, replay), deadline, cancellationToken);
    }

    private RecipeDraftEvaluation EvaluateRecipeDraftSave(IdentityAuthorityState state,
        RecipeDraftHead? head, RecipeDraftSaveRequest request, bool replay)
    {
        SessionAuthorizationLease? lease = null;
        StepUpGrant? reserved = null;
        var transferred = false;
        try
        {
            var reason = TryLease(request.Invocation, out lease, out var leaseReason) ? "Authorized" : leaseReason;
            LocalAdministratorState? actor = lease is null ? null : Find(state, lease.Identity.PrincipalId);
            if (reason == "Authorized" && (actor is not { Enabled: true } ||
                    !actor.Permissions.Contains(Permission.EditRecipeDraft)))
                reason = "PermissionDenied";
            if (reason == "Authorized" && request.OperationId == Guid.Empty)
                reason = "RecipeDraftOperationIdRequired";
            if (reason == "Authorized" && request.DraftId == Guid.Empty)
                reason = "RecipeDraftIdRequired";
            if (reason == "Authorized" && request.ExpectedRevision < 0)
                reason = "RecipeDraftRevisionInvalid";
            if (reason == "Authorized" && !replay && request.ExpectedRevision != (head?.Revision ?? 0))
                reason = "RecipeDraftRevisionConflict";
            if (reason == "Authorized" && !replay && request.ExpectedRevisionContentHash != (head?.RevisionContentHash))
                reason = "RecipeDraftRevisionContentConflict";
            if (reason == "Authorized" && replay &&
                (request.ExpectedRevision != (head?.Revision ?? 0) - 1 ||
                 request.ExpectedRevisionContentHash != (head?.PreviousRevisionContentHash)))
                reason = "RecipeDraftOperationConflict";
            if (reason == "Authorized" && string.IsNullOrWhiteSpace(request.ChangeReason))
                reason = "RecipeDraftChangeReasonRequired";
            if (reason == "Authorized" && request.ChangeReason.Length > 256)
                reason = "RecipeDraftChangeReasonTooLong";
            if (reason == "Authorized" && replay &&
                request.StepUpGrantId != request.Invocation?.StepUpGrantId)
                reason = "StepUpInvalid";
            if (reason == "Authorized" && !replay)
                reason = CheckRecipeDraftGrant(request, actor!, lease!.SessionId, reserve: false, out _,
                    allowConsumed: false);

            if (reason != "Authorized")
                return replay ? ReplayDecision(request, reason) :
                    Decision(state, request, actor, lease?.SessionId, reason, accepted: false);

            if (replay)
            {
                // The original grant/session is validated above, but no grant is
                // reserved or consumed and no new audit event is appended.
                return new RecipeDraftEvaluation(
                    new RecipeDraftSaveResult(true, "RecipeDraftAuthorized", null,
                        Array.Empty<AlgorithmValidationIssue>()),
                    new RecipeDraftMutation(actor!.PrincipalId, lease!.SessionId,
                        actor.AuthorizationRevision, request.ChangeReason), Array.Empty<IdentityAuditEvent>());
            }

            reason = CheckRecipeDraftGrant(request, actor!, lease!.SessionId, reserve: true, out reserved);
            if (reason != "Authorized")
                return Decision(state, request, actor, lease.SessionId, reason, accepted: false);

            var binding = RecipeDraftBinding(request);
            var guard = new AuthorizationCommitGuard(lease, () =>
            {
                lock (_grantSync) if (reserved is not null) reserved.State = GrantState.Consumed;
            }, () =>
            {
                lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
            });
            transferred = true;
            var eventFact = AuthorizationEvent(state, IdentityEventKind.RecipeDraftSaved,
                "RecipeDraftAuthorized", binding, actor!.PrincipalId, lease!.SessionId,
                request.Invocation!.StepUpGrantId, request.OperationId, actor.AuthorizationRevision,
                targetPrincipalId: null) with
            { ActionTargetId = request.DraftId.ToString("D"),
                BoundCommandCorrelationId = request.OperationId,
                ActionCommandKind = AuditedCommandKind.SaveRecipeDraft.ToString() };
            return new RecipeDraftEvaluation(
                new RecipeDraftSaveResult(true, "RecipeDraftAuthorized", null,
                    Array.Empty<AlgorithmValidationIssue>()),
                new RecipeDraftMutation(actor.PrincipalId, lease.SessionId, actor.AuthorizationRevision,
                    request.ChangeReason), new[] { eventFact }, guard);
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

    private RecipeDraftEvaluation Decision(IdentityAuthorityState state, RecipeDraftSaveRequest request,
        LocalAdministratorState? actor, Guid? sessionId, string reason, bool accepted)
    {
        var binding = RecipeDraftBinding(request);
        var eventFact = AuthorizationEvent(state, IdentityEventKind.ManagementRejected, reason, binding,
            actor?.PrincipalId, sessionId, request.Invocation.StepUpGrantId, request.OperationId,
            actor?.AuthorizationRevision ?? 0) with
        { ActionTargetId = request.DraftId.ToString("D"),
            BoundCommandCorrelationId = request.OperationId,
            ActionCommandKind = AuditedCommandKind.SaveRecipeDraft.ToString() };
        return new RecipeDraftEvaluation(new RecipeDraftSaveResult(accepted, reason, null,
            Array.Empty<AlgorithmValidationIssue>()), null, new[] { eventFact });
    }

    private static RecipeDraftEvaluation ReplayDecision(RecipeDraftSaveRequest request, string reason) =>
        new(new RecipeDraftSaveResult(false, reason, null, Array.Empty<AlgorithmValidationIssue>()),
            null, Array.Empty<IdentityAuditEvent>());

    private string CheckRecipeDraftGrant(RecipeDraftSaveRequest request, LocalAdministratorState actor,
        Guid sessionId, bool reserve, out StepUpGrant? grant, bool allowConsumed = false)
    {
        grant = null;
        if (!RequiresRecipeDraftStepUp()) return "Authorized";
        if (request.StepUpGrantId is not { } requestGrant || request.Invocation?.StepUpGrantId != requestGrant)
            return request.Invocation?.StepUpGrantId is null ? "StepUpRequired" : "StepUpInvalid";
        var binding = RecipeDraftBinding(request);
        lock (_grantSync)
        {
            PurgeGrantsLocked();
            if (!_grants.TryGetValue(requestGrant, out var candidate) ||
                (candidate.State != GrantState.Active && !(allowConsumed && candidate.State == GrantState.Consumed)) ||
                candidate.PrincipalId != actor.PrincipalId || candidate.SessionId != sessionId ||
                candidate.CredentialId != actor.CredentialId || candidate.AuthorizationRevision != actor.AuthorizationRevision ||
                candidate.PolicyHash != _options.AuthorizationPolicy.ContentHash || candidate.Binding != binding)
                return "StepUpInvalid";
            grant = candidate;
            if (reserve) candidate.State = GrantState.Reserved;
            return "Authorized";
        }
    }

    private bool RequiresRecipeDraftStepUp() => _store.RecipeDraftOptions?.RequireStepUp == true ||
        _options.AuthorizationPolicy.RequiresStepUp(Permission.EditRecipeDraft);

    private static StepUpBinding RecipeDraftBinding(RecipeDraftSaveRequest request) =>
        new(Permission.EditRecipeDraft, request.OperationId, request.DraftId.ToString("D"),
            AuditedCommandKind.SaveRecipeDraft);
}
