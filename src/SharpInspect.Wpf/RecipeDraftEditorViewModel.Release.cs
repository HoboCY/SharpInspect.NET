using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

public sealed partial class RecipeDraftEditorViewModel
{
    private const int MaximumReleaseReasonCharacters = 256;
    private const int MaximumReleaseReasonBytes = 4096;
    private const string ReleaseDefaultReason = "发布配方草稿";

    private readonly IRecipeReleaseService? _releaseService;
    private RecipeReleaseAccess? _releaseAccess;
    private ReleasedRecipe? _releasedRecipe;
    private string _releaseReason = ReleaseDefaultReason;
    private string _releaseStatusText = string.Empty;

    private readonly record struct ReleaseSnapshot(Guid DraftId, long Revision,
        string RevisionContentHash, string ContentHash, string ReleaseReason);

    public bool HasReleaseService => _releaseService is not null;
    public RecipeReleaseAccess? ReleaseAccess => _releaseAccess;
    public RecipeGovernancePolicy? ReleasePolicy => _releaseAccess?.Policy;
    public RecipeGovernanceMode? ReleasePolicyMode => ReleasePolicy?.Mode;
    public string ReleasePolicyText => ReleasePolicy is not { } policy
        ? string.Empty
        : $"{policy.Id} v{policy.Version} · {policy.ContentHash}";
    public string ReleasePolicyModeText => ReleasePolicy?.Mode switch
    {
        RecipeGovernanceMode.SingleApproverRelease => "Single Approver Release",
        RecipeGovernanceMode.MakerCheckerRelease => "Maker-Checker Release",
        _ => string.Empty
    };
    public string ReleaseReason
    {
        get => _releaseReason;
        set
        {
            SetBoundedProperty(ref _releaseReason, value,
                MaximumReleaseReasonCharacters, MaximumReleaseReasonBytes, nameof(ReleaseReason));
            ReleaseProjectionChanged();
            NotifyReleaseProperties();
            NotifyReleaseCommands();
        }
    }
    public bool ReleaseRequiresStepUp => _releaseAccess?.RequiresStepUp == true;
    public bool CanRelease => CanPrepareRelease;
    public bool CanReleaseWithStepUp => CanPrepareRelease && ReleaseRequiresStepUp && _stepUp is not null;
    public ReleasedRecipe? ReleasedRecipe => _releasedRecipe;
    public string ReleasedRecipeReferenceText => _releasedRecipe is not { } recipe
        ? string.Empty
        : $"{recipe.Reference.Id} v{recipe.Reference.Version} · {recipe.Reference.ContentHash}";
    public string ReleaseStatusText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_releaseStatusText)) return _releaseStatusText;
            if (_releaseService is null) return "发布服务未配置。";
            if (_releaseAccess is null) return "发布权限尚未读取。";
            if (!_releaseAccess.CanRelease || _releaseAccess.Policy is null)
                return $"发布不可用：{SafeReason(_releaseAccess.ReasonCode, "RecipeReleaseAccessDenied")}";
            return ReleaseRequiresStepUp
                ? "发布政策已加载；提交前需要当前人员新鲜 Step-Up。"
                : "发布政策已加载。";
        }
    }

    private bool CanPrepareRelease => _releaseService is not null && IsConfigured &&
        IsAuthenticated && IsUsableSession(CurrentSession) && !IsBusy && !_disposed &&
        _releaseAccess?.CanRelease == true && _releaseAccess.Policy is not null &&
        IsCurrentSavedReleaseContent() && IsValidReleaseReason();

    private bool IsValidReleaseReason() => !string.IsNullOrWhiteSpace(_releaseReason) &&
        IsValidReleaseReason(_releaseReason);

    private static bool IsValidReleaseReason(string reason) => !string.IsNullOrWhiteSpace(reason) &&
        !reason.Any(char.IsControl) && reason.Length <= MaximumReleaseReasonCharacters;

    private bool IsCurrentSavedReleaseContent()
    {
        var revision = _revision;
        var content = _localContent;
        return revision is not null && revision.DraftId != Guid.Empty && revision.Revision >= 1 &&
            IsSha256(revision.RevisionContentHash) && content is not null && IsValid &&
            !_customInputRejected &&
            string.Equals(content.ContentHash, revision.Content.ContentHash, StringComparison.Ordinal);
    }

    private bool HasExactSavedReleaseSnapshot() => _releaseService is not null &&
        IsCurrentSavedReleaseContent();

    private bool TryCaptureReleaseSnapshot(out ReleaseSnapshot snapshot, out string reason)
    {
        snapshot = default;
        if (_releaseService is null)
        {
            reason = "RecipeReleaseUnavailable";
            return false;
        }
        if (_revision is not { } revision)
        {
            reason = "RecipeReleaseDraftRequired";
            return false;
        }
        if (!IsCurrentSavedReleaseContent())
        {
            reason = _localContent is null || !IsValid || _customInputRejected
                ? "RecipeReleaseDraftUnsaved" : "RecipeReleaseContentChanged";
            return false;
        }
        snapshot = new ReleaseSnapshot(revision.DraftId, revision.Revision,
            revision.RevisionContentHash, revision.Content.ContentHash, _releaseReason);
        reason = string.Empty;
        return true;
    }

    private bool IsReleaseSnapshotCurrent(OperationStart start, ReleaseSnapshot snapshot,
        InteractiveSession session)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return false;
        }
        var currentSession = _sessions?.Current ?? session;
        if (!SameSession(session, currentSession)) return false;
        return IsCurrentSavedReleaseContent() && _revision is { } revision &&
            revision.DraftId == snapshot.DraftId && revision.Revision == snapshot.Revision &&
            string.Equals(revision.RevisionContentHash, snapshot.RevisionContentHash,
                StringComparison.Ordinal) && _localContent is { } content &&
            string.Equals(content.ContentHash, snapshot.ContentHash, StringComparison.Ordinal) &&
            string.Equals(_releaseReason, snapshot.ReleaseReason, StringComparison.Ordinal);
    }

    private async ValueTask<RecipeReleaseAccess?> ReadReleaseAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (_releaseService is null) return null;
        try
        {
            return await _releaseService.GetAccessAsync(invocation, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new RecipeReleaseAccess(false, "RecipeReleaseAccessUnavailable", null);
        }
    }

    private async Task<bool> ApplyReleaseAccessAsync(RecipeReleaseAccess access,
        OperationStart start)
    {
        var applied = false;
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                if (!IsCurrentLocked(start)) return;
                _releaseAccess = access;
                applied = true;
            }
            NotifyReleaseProperties();
            NotifyReleaseCommands();
        }).ConfigureAwait(true);
        return applied;
    }

    public Task<RecipeReleaseResult?> ReleaseAsync(CancellationToken cancellationToken = default) =>
        ReleaseCoreAsync(null, cancellationToken);

    public Task<RecipeReleaseResult?> ReleaseWithStepUpAsync(string password,
        CancellationToken cancellationToken = default) =>
        ReleaseCoreAsync(password ?? string.Empty, cancellationToken);

    private async Task<RecipeReleaseResult?> ReleaseCoreAsync(string? stepUpPassword,
        CancellationToken cancellationToken)
    {
        if (_releaseService is null)
        {
            await ApplyReleaseFailureAsync("RecipeReleaseUnavailable").ConfigureAwait(true);
            return null;
        }
        var start = Begin(cancellationToken);
        if (!start.HasValue) return null;
        ReleaseSnapshot? capturedSnapshot = null;
        InteractiveSession? capturedSession = null;
        try
        {
            await _dispatcher.InvokeAsync(() => ResetReleaseProjection(clearAccess: false))
                .ConfigureAwait(true);
            var session = _sessions?.Current ?? UnauthenticatedSession;
            if (!IsUsableSession(session))
            {
                await ApplyReleaseFailureAsync("RecipeReleaseSessionChanged", start).ConfigureAwait(true);
                return null;
            }

            if (!TryCaptureReleaseSnapshot(out var snapshot, out var snapshotReason))
            {
                await ApplyReleaseFailureAsync(snapshotReason, start).ConfigureAwait(true);
                return null;
            }
            capturedSnapshot = snapshot;
            capturedSession = session;

            var access = await ReadReleaseAccessAsync(CreateInvocation(session),
                start.Value.Cancellation.Token).ConfigureAwait(true);
            if (access is null)
            {
                await ApplyReleaseFailureAsync("RecipeReleaseAccessUnavailable", start, snapshot, session).ConfigureAwait(true);
                return null;
            }
            if (!await ApplyReleaseAccessAsync(access, start.Value).ConfigureAwait(true)) return null;
            var policy = access.Policy;
            if (!access.CanRelease || policy is null)
            {
                await ApplyReleaseFailureAsync(SafeReason(access.ReasonCode,
                    "RecipeReleaseAccessDenied"), start, snapshot, session).ConfigureAwait(true);
                return null;
            }
            if (!IsReleaseSnapshotCurrent(start.Value, snapshot, session))
            {
                await ApplyReleaseFailureAsync("RecipeReleaseContentChanged", start).ConfigureAwait(true);
                return null;
            }

            var operationId = Guid.NewGuid();
            var reason = snapshot.ReleaseReason;
            if (!IsValidReleaseReason(reason))
            {
                await ApplyReleaseFailureAsync("RecipeReleaseReasonRequired", start, snapshot, session).ConfigureAwait(true);
                return null;
            }
            var command = new ReleaseRecipeCommand(operationId, CreateInvocation(session),
                snapshot.DraftId, snapshot.Revision, snapshot.RevisionContentHash,
                policy.Reference, reason);

            Guid? grantId = null;
            if (access.RequiresStepUp)
            {
                if (string.IsNullOrEmpty(stepUpPassword))
                {
                    await ApplyReleaseFailureAsync("RecipeReleaseStepUpRequired", start, snapshot, session).ConfigureAwait(true);
                    return null;
                }
                if (_stepUp is null)
                {
                    await ApplyReleaseFailureAsync("RecipeReleaseStepUpUnavailable", start, snapshot, session).ConfigureAwait(true);
                    return null;
                }

                var binding = new StepUpBinding(Permission.ReleaseRecipe, operationId,
                    command.AuthorizationTarget, AuditedCommandKind.ReleaseRecipe);
                lock (_sync) _lastStepUpBinding = binding;
                StepUpResult stepUpResult;
                try
                {
                    stepUpResult = await _stepUp.ReauthenticateAsync(
                        new StepUpRequest(operationId, CreateInvocation(session), binding,
                            stepUpPassword), start.Value.Cancellation.Token).ConfigureAwait(true);
                }
                finally
                {
                    stepUpPassword = string.Empty;
                }

                start.Value.Cancellation.Token.ThrowIfCancellationRequested();
                if (!stepUpResult.Succeeded || stepUpResult.GrantId is not { } grant)
                {
                    await ApplyReleaseFailureAsync("StepUpAuthenticationRejected", start, snapshot, session).ConfigureAwait(true);
                    return null;
                }
                grantId = grant;
                var currentSession = _sessions?.Current ?? session;
                if (!IsReleaseSnapshotCurrent(start.Value, snapshot, session) ||
                    !SameSession(session, currentSession))
                {
                    await ApplyReleaseFailureAsync("RecipeReleaseContentChanged", start).ConfigureAwait(true);
                    return null;
                }
                session = currentSession;
                command = new ReleaseRecipeCommand(operationId, CreateInvocation(session, grantId),
                    snapshot.DraftId, snapshot.Revision, snapshot.RevisionContentHash,
                    policy.Reference, reason);
            }

            if (!IsReleaseSnapshotCurrent(start.Value, snapshot, session))
            {
                await ApplyReleaseFailureAsync("RecipeReleaseContentChanged", start).ConfigureAwait(true);
                return null;
            }
            var result = await _releaseService.ReleaseAsync(command,
                start.Value.Cancellation.Token).ConfigureAwait(true);
            // Once the authority returns a committed outcome, late cancellation cannot
            // turn it into a cancelled release. A superseded editor still returns that
            // outcome to its caller while leaving the new projection untouched.
            if (!IsReleaseSnapshotCurrent(start.Value, snapshot, session)) return result;
            if (result.Outcome.CorrelationId != command.CorrelationId)
            {
                await ApplyReleaseFailureAsync("RecipeReleaseResultInvalid", start, snapshot, session).ConfigureAwait(true);
                return result;
            }
            if (result.Outcome.Disposition != CommandDisposition.Accepted)
            {
                await ApplyReleaseFailureAsync(SafeReason(result.Outcome.ReasonCode,
                    "RecipeReleaseRejected"), start, snapshot, session).ConfigureAwait(true);
                return result;
            }
            if (result.Outcome.Audit != AuditPersistence.Persisted)
            {
                await ApplyReleaseFailureAsync(result.Outcome.Audit == AuditPersistence.Unavailable
                    ? "RecipeReleaseAuditUnavailable" : "RecipeReleaseRejected", start, snapshot, session)
                    .ConfigureAwait(true);
                return result;
            }
            var releasedRecipe = result.Recipe;
            if (releasedRecipe is null || !releasedRecipe.Available ||
                !ReleaseResultMatchesSnapshot(releasedRecipe, snapshot, policy, command, session))
            {
                await ApplyReleaseFailureAsync("RecipeReleaseResultInvalid", start, snapshot, session).ConfigureAwait(true);
                return result;
            }

            await _dispatcher.InvokeAsync(() =>
            {
                if (!IsReleaseSnapshotCurrent(start.Value, snapshot, session)) return;
                lock (_sync)
                {
                    if (!IsCurrentLocked(start.Value)) return;
                    _releasedRecipe = releasedRecipe;
                    _releaseStatusText = $"发布成功：Available · {ReleasedRecipeReferenceText}";
                    _errorCode = null;
                    _statusMessage = "配方已生成不可变 Available 版本；尚未激活或武装。";
                }
                NotifyReleaseProperties();
                NotifyReleaseCommands();
                OnPropertyChanged(nameof(StatusMessage));
                OnPropertyChanged(nameof(ErrorCode));
            }).ConfigureAwait(true);
            return result;
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            await ApplyReleaseFailureAsync("RecipeReleaseCancelled", start, capturedSnapshot, capturedSession).ConfigureAwait(true);
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await ApplyReleaseFailureAsync("RecipeReleaseFailed", start, capturedSnapshot, capturedSession).ConfigureAwait(true);
            return null;
        }
        finally
        {
            stepUpPassword = string.Empty;
            Complete(start.Value);
        }
    }

    private bool ReleaseResultMatchesSnapshot(ReleasedRecipe recipe, ReleaseSnapshot snapshot,
        RecipeGovernancePolicy policy, ReleaseRecipeCommand command, InteractiveSession session)
    {
        var record = recipe.Record;
        return record.Source.DraftId == snapshot.DraftId &&
            record.Source.Revision == snapshot.Revision &&
            string.Equals(record.Source.RevisionContentHash, snapshot.RevisionContentHash,
                StringComparison.Ordinal) &&
            string.Equals(record.Source.Content.ContentHash, snapshot.ContentHash,
                StringComparison.Ordinal) && record.GovernancePolicy.Reference == policy.Reference &&
            string.Equals(record.ReleaseReason, snapshot.ReleaseReason, StringComparison.Ordinal) &&
            record.OperationId == command.CorrelationId &&
            string.Equals(record.AuthorizationTarget, command.AuthorizationTarget,
                StringComparison.Ordinal) &&
            command.Invocation.StepUpGrantId is { } grantId && record.StepUpGrantId == grantId &&
            session.SessionId is { } sessionId && record.ApproverSessionId == sessionId &&
            string.Equals(record.ApproverPrincipalId.ToString("D"), session.PrincipalId,
                StringComparison.OrdinalIgnoreCase);
    }

    private async Task ApplyReleaseFailureAsync(string reason, OperationStart? start = null,
        ReleaseSnapshot? snapshot = null, InteractiveSession? session = null)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            if (start.HasValue)
            {
                if (snapshot.HasValue && (session is null ||
                    !IsReleaseSnapshotCurrent(start.Value, snapshot.Value, session))) return;
                lock (_sync)
                {
                    if (!IsCurrentLocked(start.Value)) return;
                }
            }
            _releasedRecipe = null;
            var safe = SafeReason(reason, "RecipeReleaseRejected");
            _releaseStatusText = $"发布未完成：{safe}";
            _errorCode = safe;
            _statusMessage = "发布未完成；当前草稿内容未被 Runtime 替换。";
            NotifyReleaseProperties();
            NotifyReleaseCommands();
            OnPropertyChanged(nameof(StatusMessage));
            OnPropertyChanged(nameof(ErrorCode));
        }).ConfigureAwait(true);
    }

    private void ResetReleaseProjection(bool clearAccess)
    {
        _releasedRecipe = null;
        _releaseStatusText = string.Empty;
        if (clearAccess) _releaseAccess = null;
        NotifyReleaseProperties();
        NotifyReleaseCommands();
    }

    private void ReleaseProjectionChanged()
    {
        if (_releasedRecipe is null ||
            (IsCurrentSavedReleaseContent() &&
             string.Equals(_releasedRecipe.Record.ReleaseReason, _releaseReason,
                 StringComparison.Ordinal))) return;
        _releasedRecipe = null;
        _releaseStatusText = "草稿内容或发布原因已变化，需重新发布。";
        if (_errorCode?.StartsWith("RecipeRelease", StringComparison.Ordinal) == true)
            _errorCode = null;
        NotifyReleaseProperties();
        NotifyReleaseCommands();
    }

    private void NotifyReleaseProperties()
    {
        OnPropertyChanged(nameof(ReleaseAccess));
        OnPropertyChanged(nameof(HasReleaseService));
        OnPropertyChanged(nameof(ReleasePolicy));
        OnPropertyChanged(nameof(ReleasePolicyMode));
        OnPropertyChanged(nameof(ReleasePolicyText));
        OnPropertyChanged(nameof(ReleasePolicyModeText));
        OnPropertyChanged(nameof(ReleaseReason));
        OnPropertyChanged(nameof(ReleaseRequiresStepUp));
        OnPropertyChanged(nameof(CanRelease));
        OnPropertyChanged(nameof(CanReleaseWithStepUp));
        OnPropertyChanged(nameof(ReleasedRecipe));
        OnPropertyChanged(nameof(ReleasedRecipeReferenceText));
        OnPropertyChanged(nameof(ReleaseStatusText));
    }

    private void NotifyReleaseCommands()
    {
        ReleaseCommand?.RaiseCanExecuteChanged();
        ReleaseWithStepUpCommand?.RaiseCanExecuteChanged();
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(ch =>
        ch is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');
}
