using System.Collections.ObjectModel;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Presentation adapter for the identity-administration boundary. It only
/// presents the latest read-only authority projection and sends typed Runtime
/// commands after a fresh, target-bound Step-Up exchange. It never changes the
/// directory optimistically after command admission.
/// </summary>
public sealed class IdentityAdministrationViewModel : ObservableObject, IAsyncDisposable
{
    private readonly record struct OperationStart(long Version, CancellationTokenSource Cancellation);

    private static readonly ReadOnlyCollection<Permission> PermissionOptionsValue =
        new(Enum.GetValues<Permission>().Where(permission => permission != Permission.None).ToArray());

    private static readonly ReadOnlyCollection<HumanRoleBundle> RoleOptionsValue =
        new(Enum.GetValues<HumanRoleBundle>().ToArray());

    private readonly IStationRuntime? _runtime;
    private readonly IInteractiveSessionService? _sessions;
    private readonly IStepUpAuthentication? _stepUp;
    private readonly IIdentityAdministrationQuery? _query;
    private readonly IUiDispatcher _dispatcher;
    private readonly object _sync = new();
    private readonly ObservableCollection<HumanAccountSummary> _accounts = new();
    private readonly ReadOnlyObservableCollection<HumanAccountSummary> _readOnlyAccounts;
    private CancellationTokenSource? _activeCancellation;
    private long _operationVersion;
    private InteractiveSession _session;
    private HumanAuthorizationSnapshot? _authorization;
    private HumanAccountSummary? _selectedAccount;
    private HumanRoleBundle _selectedRoleBundle = HumanRoleBundle.Operator;
    private StepUpBinding? _lastStepUpBinding;
    private RuntimeCommandOutcome? _lastCommandOutcome;
    private string _statusMessage;
    private string? _errorCode;
    private bool _isBusy;
    private bool _disposed;

    public IdentityAdministrationViewModel(
        IStationRuntime? runtime,
        IInteractiveSessionService? sessions,
        IStepUpAuthentication? stepUpAuthentication,
        IIdentityAdministrationQuery? administrationQuery,
        IUiDispatcher? dispatcher = null)
    {
        _runtime = runtime;
        _sessions = sessions;
        _stepUp = stepUpAuthentication;
        _query = administrationQuery;
        _dispatcher = dispatcher ?? new DispatcherUiDispatcher();
        _readOnlyAccounts = new ReadOnlyObservableCollection<HumanAccountSummary>(_accounts);
        _session = sessions?.Current ?? UnauthenticatedSession;
        _statusMessage = IsConfigured
            ? "尚未读取当前人员权限，请刷新。"
            : "身份管理不可用：未配置完整的会话、权限查询或 Runtime 服务。";

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => CanRefresh);
        if (_sessions is not null)
        {
            _sessions.Changed += SessionChanged;
        }
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public IReadOnlyList<Permission> PermissionOptions => PermissionOptionsValue;

    public IReadOnlyList<HumanRoleBundle> RoleOptions => RoleOptionsValue;

    public ReadOnlyObservableCollection<HumanAccountSummary> Accounts => _readOnlyAccounts;

    public HumanAuthorizationSnapshot? CurrentAuthorization
    {
        get
        {
            var session = _sessions?.Current;
            lock (_sync)
            {
                var projected = session ?? _session;
                return _authorization is not null &&
                    projected.State == InteractiveSessionState.Authenticated &&
                    _authorization.SessionId == projected.SessionId
                    ? _authorization
                    : null;
            }
        }
    }

    public InteractiveSession CurrentSession
    {
        get
        {
            // The event payload is only a notification.  The session service owns
            // the authoritative current projection and may have advanced again
            // before a queued dispatcher callback is applied.
            return _sessions?.Current ?? ReadProjectedSession();
        }
    }

    private InteractiveSession ReadProjectedSession()
    {
        lock (_sync) return _session;
    }

    public HumanAccountSummary? SelectedAccount
    {
        get { lock (_sync) return _selectedAccount; }
        set
        {
            lock (_sync)
            {
                if (ReferenceEquals(_selectedAccount, value) ||
                    (_selectedAccount?.PrincipalId == value?.PrincipalId))
                    return;
                _selectedAccount = value;
            }
            OnPropertyChanged();
            NotifyAvailabilityChanged();
        }
    }

    public HumanRoleBundle SelectedRoleBundle
    {
        get { lock (_sync) return _selectedRoleBundle; }
        set
        {
            if (!RoleOptionsValue.Contains(value)) return;
            if (!SetProperty(ref _selectedRoleBundle, value)) return;
            NotifyAvailabilityChanged();
        }
    }

    public bool IsConfigured => _sessions is not null && _query is not null;

    public bool IsUnavailable
    {
        get
        {
            var session = _sessions?.Current;
            lock (_sync)
            {
                var projected = session ?? _session;
                return !IsConfigured || projected.State != InteractiveSessionState.Authenticated ||
                    _authorization is null || !_authorization.Available || _authorization.Account is null ||
                    _authorization.SessionId != projected.SessionId;
            }
        }
    }

    public bool IsAuthenticated
    {
        get => (_sessions?.Current ?? ReadProjectedSession()).State == InteractiveSessionState.Authenticated;
    }

    public bool IsBusy
    {
        get { lock (_sync) return _isBusy; }
    }

    public bool CanRefresh => IsConfigured && !IsBusy && !_disposed;

    public bool CanManageAccounts => HasPermission(Permission.ManageAccounts) && !IsBusy;

    public bool CanManagePermissions => HasPermission(Permission.ManagePermissions) && !IsBusy;

    public bool CanCreateAccount => CanManageAccounts && CanManagePermissions &&
        _runtime is not null && _stepUp is not null;

    public bool CanDisableAccount => CanManageAccounts && HasSelectedAccount &&
        _runtime is not null && _stepUp is not null;

    public bool CanUnlockAccount => HasPermission(Permission.UnlockCredential) && HasSelectedAccount &&
        _runtime is not null && _stepUp is not null;

    public bool CanRebindAccount => HasPermission(Permission.RebindCredential) && HasSelectedAccount &&
        _runtime is not null && _stepUp is not null;

    public bool CanSetPermissions => CanManagePermissions && HasSelectedAccount &&
        _runtime is not null && _stepUp is not null;

    public RuntimeCommandOutcome? LastCommandOutcome
    {
        get { lock (_sync) return _lastCommandOutcome; }
        private set
        {
            lock (_sync) _lastCommandOutcome = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Development-only observation for the isolated process smoke. The value
    /// contains no password and is never used as an authorization input.
    /// </summary>
    internal StepUpBinding? LastStepUpBinding
    {
        get { lock (_sync) return _lastStepUpBinding; }
    }

    public string StatusMessage
    {
        get { lock (_sync) return _statusMessage; }
        private set
        {
            lock (_sync) _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public string? ErrorCode
    {
        get { lock (_sync) return _errorCode; }
        private set
        {
            lock (_sync) _errorCode = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Reads the live session and then the read-only authority projection.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            await ApplyUnavailableAsync("IdentityAdministrationUnavailable").ConfigureAwait(true);
            return;
        }

        var start = Begin(cancellationToken);
        if (!start.HasValue) return;

        try
        {
            var session = await _sessions!.GetSessionAsync(start.Value.Cancellation.Token)
                .ConfigureAwait(true);
            if (!IsUsableSession(session))
            {
                await ApplyUnavailableAsync("IdentityAdministrationUnauthenticated", start)
                    .ConfigureAwait(true);
                return;
            }

            var authorization = await _query!.GetCurrentAuthorizationAsync(
                    session.SessionId, start.Value.Cancellation.Token)
                .ConfigureAwait(true);
            start.Value.Cancellation.Token.ThrowIfCancellationRequested();

            HumanDirectorySnapshot? directory = null;
            if (IsUsableAuthorization(authorization, session) &&
                HasPermission(authorization, Permission.ManageAccounts))
            {
                directory = await _query.GetAccountsAsync(
                        CreateInvocation(session, grantId: null), start.Value.Cancellation.Token)
                    .ConfigureAwait(true);
                start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            }

            await ApplyAuthorizationAsync(start.Value, session, authorization, directory)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            // A page change, lock, or explicit clear owns cancellation presentation.
        }
        catch
        {
            await ApplyUnavailableAsync("IdentityAdministrationUnavailable", start)
                .ConfigureAwait(true);
        }
        finally
        {
            Complete(start.Value);
        }
    }

    public Task<RuntimeCommandOutcome?> CreateAccountAsync(
        string userName,
        string displayName,
        string newPassword,
        HumanRoleBundle roleBundle,
        string stepUpPassword,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(displayName) ||
            string.IsNullOrEmpty(newPassword))
            return LocalInputFailureAsync("IdentityAdministrationInputInvalid");
        if (!RoleOptionsValue.Contains(roleBundle))
            return LocalInputFailureAsync("IdentityAdministrationRoleInvalid");

        var targetPrincipalId = Guid.NewGuid();
        return ExecuteManagementAsync(Permission.ManageAccounts, AuditedCommandKind.CreateHumanAccount,
            targetPrincipalId, stepUpPassword,
            (correlationId, invocation) => new CreateHumanAccountCommand(correlationId, invocation,
                targetPrincipalId, userName, displayName, newPassword, roleBundle), cancellationToken);
    }

    public Task<RuntimeCommandOutcome?> DisableAccountAsync(
        Guid targetPrincipalId,
        string stepUpPassword,
        CancellationToken cancellationToken = default) =>
        ExecuteTargetedAsync(Permission.ManageAccounts, AuditedCommandKind.DisableHumanCredential,
            targetPrincipalId, stepUpPassword,
            (correlationId, invocation) => new DisableHumanCredentialCommand(correlationId, invocation,
                targetPrincipalId, IdentityManagementReason.PersonnelDeparture), cancellationToken);

    public Task<RuntimeCommandOutcome?> UnlockAccountAsync(
        Guid targetPrincipalId,
        string stepUpPassword,
        CancellationToken cancellationToken = default) =>
        ExecuteTargetedAsync(Permission.UnlockCredential, AuditedCommandKind.UnlockHumanCredential,
            targetPrincipalId, stepUpPassword,
            (correlationId, invocation) => new UnlockHumanCredentialCommand(correlationId, invocation,
                targetPrincipalId, IdentityManagementReason.CredentialLockout), cancellationToken);

    public Task<RuntimeCommandOutcome?> RebindAccountAsync(
        Guid targetPrincipalId,
        string newPassword,
        string stepUpPassword,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(newPassword))
            return LocalInputFailureAsync("IdentityAdministrationInputInvalid");

        return ExecuteTargetedAsync(Permission.RebindCredential, AuditedCommandKind.RebindHumanCredential,
            targetPrincipalId, stepUpPassword,
            (correlationId, invocation) => new RebindHumanCredentialCommand(correlationId, invocation,
                targetPrincipalId, newPassword, IdentityManagementReason.CredentialCompromise), cancellationToken);
    }

    public Task<RuntimeCommandOutcome?> SetPermissionsAsync(
        Guid targetPrincipalId,
        IEnumerable<Permission> permissions,
        string stepUpPassword,
        CancellationToken cancellationToken = default)
    {
        if (permissions is null) return LocalInputFailureAsync("IdentityAdministrationInputInvalid");
        var copiedPermissions = permissions.Take(65).ToArray();
        if (copiedPermissions.Length > 64)
            return LocalInputFailureAsync("PermissionSetCapacityExceeded");

        return ExecuteTargetedAsync(Permission.ManagePermissions, AuditedCommandKind.SetHumanPermissions,
            targetPrincipalId, stepUpPassword,
            (correlationId, invocation) => new SetHumanPermissionsCommand(correlationId, invocation,
                targetPrincipalId, copiedPermissions), cancellationToken);
    }

    /// <summary>
    /// Cancels work that has not reached Runtime submission. A command outcome
    /// already returned by Runtime remains observable and is never rewritten.
    /// </summary>
    public void CancelPendingOperations()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            _operationVersion++;
            cancellation = _activeCancellation;
            _activeCancellation = null;
            _isBusy = false;
        }

        cancellation?.Cancel();
        NotifyStateChangedOnUi();
    }

    /// <summary>Clears the visible authority projection after lock or page hiding.</summary>
    public void ClearSensitiveState()
    {
        if (!_dispatcher.CheckAccess)
        {
            _ = _dispatcher.InvokeAsync(ClearSensitiveState);
            return;
        }

        lock (_sync)
        {
            _authorization = null;
            _selectedAccount = null;
            _accounts.Clear();
            _errorCode = null;
            _statusMessage = IsConfigured
                ? "管理视图已清除，请重新读取当前会话权限。"
                : "身份管理不可用：未配置完整服务。";
        }
        NotifyStateChanged();
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _operationVersion++;
            cancellation = _activeCancellation;
            _activeCancellation = null;
            _isBusy = false;
        }

        cancellation?.Cancel();
        if (_sessions is not null) _sessions.Changed -= SessionChanged;
        ClearSensitiveState();
        await Task.CompletedTask;
    }

    private Task<RuntimeCommandOutcome?> ExecuteTargetedAsync(
        Permission permission,
        AuditedCommandKind commandKind,
        Guid targetPrincipalId,
        string stepUpPassword,
        Func<Guid, CommandInvocation, RuntimeCommand> commandFactory,
        CancellationToken cancellationToken) =>
        targetPrincipalId == Guid.Empty
            ? LocalInputFailureAsync("IdentityAdministrationTargetInvalid")
            : ExecuteManagementAsync(permission, commandKind, targetPrincipalId, stepUpPassword,
                commandFactory, cancellationToken);

    private async Task<RuntimeCommandOutcome?> ExecuteManagementAsync(
        Permission permission,
        AuditedCommandKind commandKind,
        Guid targetPrincipalId,
        string stepUpPassword,
        Func<Guid, CommandInvocation, RuntimeCommand> commandFactory,
        CancellationToken cancellationToken)
    {
        if (_runtime is null || _sessions is null || _stepUp is null)
        {
            await ApplyUnavailableAsync("IdentityAdministrationUnavailable").ConfigureAwait(true);
            return null;
        }
        if (string.IsNullOrEmpty(stepUpPassword))
        {
            await ApplyErrorAsync("IdentityAdministrationInputInvalid").ConfigureAwait(true);
            return null;
        }

        var start = Begin(cancellationToken);
        if (!start.HasValue) return null;

        try
        {
            var session = await _sessions.GetSessionAsync(start.Value.Cancellation.Token)
                .ConfigureAwait(true);
            if (!IsUsableSession(session))
            {
                await ApplyUnavailableAsync("IdentityAdministrationUnauthenticated", start)
                    .ConfigureAwait(true);
                return null;
            }

            var correlationId = Guid.NewGuid();
            var binding = new StepUpBinding(permission, correlationId,
                targetPrincipalId.ToString("D"), commandKind);
            lock (_sync) _lastStepUpBinding = binding;
            StepUpResult stepUpResult;
            try
            {
                stepUpResult = await _stepUp.ReauthenticateAsync(
                    new StepUpRequest(correlationId, CreateInvocation(session, grantId: null), binding,
                        stepUpPassword), start.Value.Cancellation.Token).ConfigureAwait(true);
            }
            finally
            {
                // The VM never retains the supplied secret after this call.
                stepUpPassword = string.Empty;
            }

            start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            if (!stepUpResult.Succeeded || stepUpResult.GrantId is null)
            {
                await ApplyErrorAsync("StepUpAuthenticationRejected", start).ConfigureAwait(true);
                return null;
            }

            var currentSession = await _sessions.GetSessionAsync(start.Value.Cancellation.Token)
                .ConfigureAwait(true);
            if (!SameSession(session, currentSession))
            {
                await ApplyUnavailableAsync("IdentityAdministrationSessionChanged", start)
                    .ConfigureAwait(true);
                return null;
            }

            var command = commandFactory(correlationId,
                CreateInvocation(currentSession, stepUpResult.GrantId));
            RuntimeCommandOutcome outcome;
            try
            {
                outcome = await _runtime.SubmitAsync(command, start.Value.Cancellation.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
            {
                await ApplyErrorAsync("IdentityAdministrationCancelled", start).ConfigureAwait(true);
                return null;
            }
            catch
            {
                await ApplyErrorAsync("IdentityAdministrationSubmitFailed", start).ConfigureAwait(true);
                return null;
            }

            // This is a real Runtime result. It is safe to expose after a local
            // cancellation because UI cancellation cannot rewrite admission.
            await ApplyCommandOutcomeAsync(outcome).ConfigureAwait(true);
            return outcome;
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            await ApplyErrorAsync("IdentityAdministrationUnavailable", start).ConfigureAwait(true);
            return null;
        }
        finally
        {
            Complete(start.Value);
        }
    }

    private async Task ApplyAuthorizationAsync(
        OperationStart start,
        InteractiveSession session,
        HumanAuthorizationSnapshot authorization,
        HumanDirectorySnapshot? directory)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                if (!IsCurrentLocked(start) || !SameSession(_session, session)) return;
                _session = session;
                _authorization = IsUsableAuthorization(authorization, session) ? authorization : null;
                _accounts.Clear();
                if (_authorization is not null && directory?.Available == true &&
                    HasPermission(_authorization, Permission.ManageAccounts))
                {
                    foreach (var account in directory.Accounts) _accounts.Add(account);
                }
                KeepSelectionIfPresentLocked();
                _errorCode = _authorization is null
                    ? SafeReason(authorization.ReasonCode, "IdentityAdministrationUnavailable")
                    : directory?.Available == false
                        ? SafeReason(directory.ReasonCode, "IdentityDirectoryUnavailable")
                        : null;
                _statusMessage = _authorization is null
                    ? "当前人员权限不可用。"
                    : directory?.Available == false
                        ? "当前人员已认证，但个人目录不可用。"
                        : "当前人员权限和个人目录已读取；每次管理动作仍由当前权限再次确认。";
            }
            NotifyStateChanged();
        }).ConfigureAwait(true);
    }

    private async Task ApplyCommandOutcomeAsync(RuntimeCommandOutcome outcome)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            LastCommandOutcome = outcome;
            ErrorCode = outcome.Disposition == CommandDisposition.Rejected
                ? SafeReason(outcome.ReasonCode, "RuntimeCommandRejected")
                : null;
            StatusMessage = outcome.Disposition == CommandDisposition.Accepted
                ? "操作已提交；完成状态请刷新个人目录确认。"
                : "操作未获批准；个人目录未作预先修改。";
            NotifyAvailabilityChanged();
        }).ConfigureAwait(true);
    }

    private async Task ApplyErrorAsync(string errorCode, OperationStart? start = null)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            if (start.HasValue)
            {
                lock (_sync)
                {
                    if (!IsCurrentLocked(start.Value)) return;
                }
            }
            LastCommandOutcome = null;
            ErrorCode = SafeReason(errorCode, "IdentityAdministrationUnavailable");
            StatusMessage = ErrorCode == "IdentityAdministrationCancelled"
                ? "管理请求已取消。"
                : "管理请求未完成。";
            NotifyAvailabilityChanged();
        }).ConfigureAwait(true);
    }

    private async Task ApplyUnavailableAsync(string errorCode, OperationStart? start = null)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            if (start.HasValue)
            {
                lock (_sync)
                {
                    if (!IsCurrentLocked(start.Value)) return;
                }
            }
            lock (_sync)
            {
                _authorization = null;
                _selectedAccount = null;
                _accounts.Clear();
                _errorCode = SafeReason(errorCode, "IdentityAdministrationUnavailable");
                _statusMessage = "身份管理不可用，请确认当前会话和服务配置。";
            }
            NotifyStateChanged();
        }).ConfigureAwait(true);
    }

    private Task<RuntimeCommandOutcome?> LocalInputFailureAsync(string errorCode)
    {
        var task = ApplyErrorAsync(errorCode);
        return CompleteLocalFailureAsync(task);
    }

    private static async Task<RuntimeCommandOutcome?> CompleteLocalFailureAsync(Task task)
    {
        await task.ConfigureAwait(true);
        return null;
    }

    private OperationStart? Begin(CancellationToken cancellationToken)
    {
        OperationStart start;
        lock (_sync)
        {
            if (_disposed || _isBusy) return null;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCancellation = linked;
            _operationVersion++;
            _isBusy = true;
            start = new OperationStart(_operationVersion, linked);
        }
        NotifyStateChangedOnUi();
        return start;
    }

    private void Complete(OperationStart start)
    {
        var changed = false;
        lock (_sync)
        {
            if (IsCurrentLocked(start))
            {
                _activeCancellation = null;
                _isBusy = false;
                changed = true;
            }
        }
        start.Cancellation.Dispose();
        if (changed) NotifyStateChangedOnUi();
    }

    private bool IsCurrentLocked(OperationStart start) =>
        !_disposed && _operationVersion == start.Version &&
        ReferenceEquals(_activeCancellation, start.Cancellation);

    private void SessionChanged(object? sender, InteractiveSessionChangedEventArgs args)
    {
        CancelPendingOperations();
        _ = ApplySessionChangedAsync();
    }

    private async Task ApplySessionChangedAsync()
    {
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                var session = _sessions?.Current ?? ReadProjectedSession();
                lock (_sync)
                {
                    if (_disposed) return;
                    _session = session;
                    _authorization = null;
                    _selectedAccount = null;
                    _accounts.Clear();
                    _errorCode = session.State == InteractiveSessionState.Authenticated
                        ? null
                        : "IdentityAdministrationUnauthenticated";
                    _statusMessage = session.State == InteractiveSessionState.Authenticated
                        ? "会话已更换，请重新读取当前权限。"
                        : "会话已锁定或注销，管理目录已清除。";
                }
                NotifyStateChanged();
            }).ConfigureAwait(true);
        }
        catch (Exception) when (_disposed) { }
    }

    private void KeepSelectionIfPresentLocked()
    {
        if (_selectedAccount is null) return;
        _selectedAccount = _accounts.FirstOrDefault(
            account => account.PrincipalId == _selectedAccount.PrincipalId);
    }

    private bool HasSelectedAccount
    {
        get { lock (_sync) return _selectedAccount is not null; }
    }

    private bool HasPermission(Permission permission)
    {
        var authoritativeSession = _sessions?.Current;
        lock (_sync)
        {
            var current = authoritativeSession ?? _session;
            return HasPermission(_authorization, permission) &&
                current.State == InteractiveSessionState.Authenticated &&
                _authorization?.SessionId == current.SessionId;
        }
    }

    private static bool HasPermission(HumanAuthorizationSnapshot? authorization, Permission permission) =>
        authorization?.Available == true && authorization.Account?.Permissions.Contains(permission) == true;

    private void NotifyAvailabilityChanged()
    {
        OnPropertyChanged(nameof(SelectedAccount));
        OnPropertyChanged(nameof(IsUnavailable));
        OnPropertyChanged(nameof(CanManageAccounts));
        OnPropertyChanged(nameof(CanManagePermissions));
        OnPropertyChanged(nameof(CanCreateAccount));
        OnPropertyChanged(nameof(CanDisableAccount));
        OnPropertyChanged(nameof(CanUnlockAccount));
        OnPropertyChanged(nameof(CanRebindAccount));
        OnPropertyChanged(nameof(CanSetPermissions));
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CurrentAuthorization));
        OnPropertyChanged(nameof(CurrentSession));
        OnPropertyChanged(nameof(IsAuthenticated));
        OnPropertyChanged(nameof(IsUnavailable));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ErrorCode));
        OnPropertyChanged(nameof(LastCommandOutcome));
        OnPropertyChanged(nameof(SelectedAccount));
        OnPropertyChanged(nameof(CanRefresh));
        NotifyAvailabilityChanged();
        RefreshCommand.RaiseCanExecuteChanged();
    }

    private void NotifyStateChangedOnUi()
    {
        if (_dispatcher.CheckAccess) NotifyStateChanged();
        else _ = _dispatcher.InvokeAsync(NotifyStateChanged);
    }

    private static CommandInvocation CreateInvocation(InteractiveSession session, Guid? grantId) =>
        new(CommandSource.PhysicalConsole, session.PrincipalId, session.SessionId, grantId);

    private static bool IsUsableSession(InteractiveSession session) =>
        session.State == InteractiveSessionState.Authenticated &&
        session.SessionId.HasValue && session.SessionId.Value != Guid.Empty &&
        !string.IsNullOrWhiteSpace(session.PrincipalId);

    private static bool SameSession(InteractiveSession left, InteractiveSession right) =>
        left.State == InteractiveSessionState.Authenticated && IsUsableSession(right) &&
        left.SessionId == right.SessionId &&
        string.Equals(left.PrincipalId, right.PrincipalId, StringComparison.Ordinal);

    private static bool IsUsableAuthorization(HumanAuthorizationSnapshot authorization,
        InteractiveSession session) =>
        authorization.Available && authorization.Account is not null &&
        authorization.SessionId == session.SessionId && IsUsableSession(session);

    private static string SafeReason(string? reason, string fallback)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 96) return fallback;
        return reason.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.') ? reason : fallback;
    }

    private static InteractiveSession UnauthenticatedSession =>
        new(InteractiveSessionState.Unauthenticated, null, null);
}
