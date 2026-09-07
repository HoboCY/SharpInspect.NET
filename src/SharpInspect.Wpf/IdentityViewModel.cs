using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Identity bootstrap and sign-in presentation. Input secrets are operation arguments;
/// an unclaimed, committed recovery kit stays private until its single display or explicit disposal.
/// </summary>
public sealed partial class IdentityViewModel : ObservableObject, IAsyncDisposable
{
    private enum OperationKind
    {
        None,
        Status,
        Bootstrap,
        Authenticate
    }

    private readonly record struct OperationStart(long Version, OperationKind Kind,
        CancellationTokenSource Cancellation);

    private readonly ILocalAdministratorBootstrap? _bootstrap;
    private readonly IIdentityProvider? _identityProvider;
    private readonly string _stationId;
    private readonly object _sync = new();
    private CancellationTokenSource? _activeCancellation;
    private StationIdentityStatus? _status;
    private HumanIdentity? _identity;
    private OneTimeSecret? _recoveryKit;
    private string _statusMessage;
    private string? _errorMessage;
    private bool? _bootstrapRequiredOverride;
    private OperationKind _operation;
    private long _requestVersion;
    private bool _isBusy;
    private bool _disposed;

    public IdentityViewModel(ILocalAdministratorBootstrap? bootstrap,
        IIdentityProvider? identityProvider, string stationId, IInteractiveSessionService? sessions = null,
        IUiDispatcher? dispatcher = null)
    {
        _bootstrap = bootstrap;
        _identityProvider = identityProvider;
        _sessions = sessions;
        _dispatcher = dispatcher ?? new DispatcherUiDispatcher();
        _session = new(InteractiveSessionState.Unauthenticated, null, null);
        _stationId = stationId?.Trim() ?? string.Empty;
        _statusMessage = !IsConfigured
            ? "身份服务不可用：未配置身份引导或登录服务。"
            : "尚未读取身份状态，请点击“刷新状态”。";

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => CanRefresh);
        lock (_sync)
        {
            if (_sessions is not null)
            {
                _sessions.Changed += SessionChanged;
                _session = _sessions.Current;
            }
        }
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public string StationId => _stationId;

    public bool IsConfigured => _bootstrap is not null || _identityProvider is not null || _sessions is not null;

    public bool IsBootstrapConfigured => _bootstrap is not null;

    public bool IsIdentityProviderConfigured => _identityProvider is not null || _sessions is not null;

    public bool IsBusy
    {
        get { lock (_sync) return _isBusy; }
    }

    public bool HasError
    {
        get { lock (_sync) return _errorMessage is not null; }
    }

    public bool CanRefresh => IsBootstrapConfigured && !_disposed && !IsBusy;

    public bool CanBootstrap
    {
        get
        {
            lock (_sync)
            {
                return !_disposed && !_isBusy && _bootstrap is not null && BootstrapRequired;
            }
        }
    }

    public bool CanAuthenticate => IsIdentityProviderConfigured && !_disposed && !IsBusy;

    public StationIdentityStatus? CurrentStatus
    {
        get { lock (_sync) return _status; }
    }

    public bool BootstrapRequired
    {
        get
        {
            lock (_sync)
            {
                return _bootstrapRequiredOverride ?? _status?.BootstrapRequired ?? false;
            }
        }
    }

    public int UsableAdministratorCount
    {
        get { lock (_sync) return _status?.UsableAdministratorCount ?? 0; }
    }

    public int ValidRecoveryCodeCount
    {
        get { lock (_sync) return _status?.ValidRecoveryCodeCount ?? 0; }
    }

    public string? StatusReasonCode
    {
        get { lock (_sync) return _status?.ReasonCode; }
    }

    public string StatusLabel
    {
        get
        {
            lock (_sync)
            {
                if (!IsConfigured) return "未配置";
                if (_isBusy) return "处理中";
                if (_status is null) return "尚未读取状态";
                if (BootstrapRequired) return "需要首次管理员";
                return "身份服务已配置";
            }
        }
    }

    public string StatusMessage
    {
        get { lock (_sync) return _statusMessage; }
    }

    public string? ErrorMessage
    {
        get { lock (_sync) return _errorMessage; }
    }

    public HumanIdentity? CurrentIdentity
    {
        get { lock (_sync) return _sessions is null || CurrentSession.State == InteractiveSessionState.Authenticated
            && CurrentSession.PrincipalId == _identity?.PrincipalId.ToString("D") ? _identity : null; }
    }

    public bool HasIdentity
    {
        get => CurrentIdentity is not null;
    }

    public string IdentitySummary
    {
        get
        {
            lock (_sync)
            {
                var identity = CurrentIdentity;
                return identity is null
                    ? "当前没有可显示的已认证身份。"
                    : $"{identity.DisplayName}（{identity.UserName}） · PrincipalId={identity.PrincipalId:D}";
            }
        }
    }

    public string IdentityNotice { get; } =
        "已验证的身份可在此查看；操作权限和生产准入功能尚未交付。";

    public bool HasRecoveryKit
    {
        get { lock (_sync) return _recoveryKit is not null; }
    }

    public bool CanRevealRecoveryKit => HasRecoveryKit && !_disposed && (_sessions is null ||
        CurrentSession.State == InteractiveSessionState.Authenticated && CurrentSession.PrincipalId == _recoveryOwner?.ToString("D"));

    public Task StartAsync(CancellationToken cancellationToken = default) => RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var start = Begin(OperationKind.Status, cancellationToken);
        if (!start.HasValue) return;

        try
        {
            StationIdentityStatus status;
            try
            {
                status = await _bootstrap!.GetStatusAsync(start.Value.Cancellation.Token)
                    .ConfigureAwait(true);
                start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
            {
                CompleteCancellation(start.Value);
                return;
            }
            catch
            {
                CompleteFailure(start.Value, "身份状态读取失败，请重试。", "IdentityStatusQueryFailed");
                return;
            }

            ApplyStatus(start.Value, status);
        }
        finally
        {
            CompleteOperation(start.Value);
        }
    }

    public async Task<BootstrapAdministratorResult?> CreateFirstAdministratorAsync(string bootstrapToken,
        string userName, string displayName, string password,
        CancellationToken cancellationToken = default)
    {
        if (!HasRequiredInput(bootstrapToken, userName, displayName, password))
        {
            SetInputError("首位管理员信息未填写完整。");
            return null;
        }

        var start = Begin(OperationKind.Bootstrap, cancellationToken);
        if (!start.HasValue) return null;

        try
        {
            BootstrapAdministratorResult? result = null;
            try
            {
                result = await _bootstrap!.CreateFirstAdministratorAsync(
                    new BootstrapAdministratorRequest(_stationId, bootstrapToken, userName.Trim(),
                        displayName.Trim(), password), start.Value.Cancellation.Token).ConfigureAwait(true);
                if (!result.Succeeded) start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
            {
                result?.RecoveryKit?.Dispose();
                CompleteCancellation(start.Value);
                return null;
            }
            catch
            {
                CompleteFailure(start.Value, "首位管理员创建失败，请重试。", "IdentityBootstrapFailed");
                return null;
            }

            ApplyBootstrapResult(start.Value, result);
            return result;
        }
        finally
        {
            CompleteOperation(start.Value);
        }
    }

    public async Task<AuthenticationResult?> AuthenticateAsync(string userName, string password,
        CancellationToken cancellationToken = default)
    {
        if (!HasRequiredInput(userName, password))
        {
            SetInputError("登录信息未填写完整。");
            return null;
        }

        var start = Begin(OperationKind.Authenticate, cancellationToken);
        if (!start.HasValue) return null;

        try
        {
            AuthenticationResult result;
            try
            {
                var request = new PasswordSignInRequest(userName.Trim(), password);
                if (_sessions is not null)
                {
                    var signIn = await _sessions.SignInAsync(request, start.Value.Cancellation.Token).ConfigureAwait(true);
                    result = new AuthenticationResult(signIn.Succeeded, signIn.ReasonCode, signIn.Identity);
                }
                else result = await _identityProvider!.AuthenticateAsync(request, start.Value.Cancellation.Token).ConfigureAwait(true);
                start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
            {
                CompleteCancellation(start.Value);
                return null;
            }
            catch
            {
                CompleteFailure(start.Value, "登录服务调用失败，请重试。", "IdentityAuthenticationFailed");
                return null;
            }

            ApplyAuthenticationResult(start.Value, result);
            return result;
        }
        finally
        {
            CompleteOperation(start.Value);
        }
    }

    /// <summary>Consumes the recovery kit exactly once. The returned string belongs to the caller.</summary>
    public string? RevealRecoveryKit()
    {
        string? value;
        lock (_sync)
        {
            if (!CanRevealRecoveryKit || _recoveryKit is null) return null;
            var secret = _recoveryKit;
            _recoveryKit = null;
            try { value = secret.TakeForDisplay(); }
            catch (InvalidOperationException) { value = null; }
            finally { secret.Dispose(); }
        }

        NotifyRecoveryStateChanged();
        return value;
    }

    /// <summary>Disposes a not-yet-revealed recovery kit without exposing it.</summary>
    public void ClearTransientSecrets()
    {
        lock (_sync)
        {
            _recoveryKit?.Dispose();
            _recoveryKit = null;
        }

        NotifyRecoveryStateChanged();
    }

    /// <summary>Cancels the active request and invalidates any response that arrives later.</summary>
    public void CancelPendingOperation()
    {
        CancellationTokenSource? cancellation;
        bool changed;
        lock (_sync)
        {
            if (!_isBusy)
            {
                cancellation = null;
                changed = false;
            }
            else if (_operation == OperationKind.Bootstrap)
            {
                // Cancellation can only withdraw work that has not committed. Keep this
                // operation current until the authoritative result delivers its one-time kit.
                cancellation = _activeCancellation;
                changed = false;
            }
            else
            {
                _requestVersion++;
                cancellation = _activeCancellation;
                _activeCancellation = null;
                _isBusy = false;
                _operation = OperationKind.None;
                _errorMessage = null;
                _statusMessage = "身份请求已取消，可重新操作。";
                changed = true;
            }
        }

        cancellation?.Cancel();
        if (changed) NotifyStateChanged();
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _requestVersion++;
            cancellation = _activeCancellation;
            _activeCancellation = null;
            _isBusy = false;
            _operation = OperationKind.None;
            _recoveryKit?.Dispose();
            _recoveryKit = null;
            _statusMessage = "身份面板已释放。";
            _errorMessage = null;
        }

        cancellation?.Cancel();
        if (_sessions is not null) _sessions.Changed -= SessionChanged;
        NotifyStateChanged();
        await Task.CompletedTask;
    }

    private OperationStart? Begin(OperationKind operation, CancellationToken cancellationToken)
    {
        OperationStart start;
        lock (_sync)
        {
            if (_disposed || _isBusy) return null;
            if (operation == OperationKind.Status && _bootstrap is null) return null;
            if (operation == OperationKind.Bootstrap && _bootstrap is null) return null;
            if (operation == OperationKind.Authenticate && !IsIdentityProviderConfigured) return null;

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCancellation = cancellation;
            _requestVersion++;
            _isBusy = true;
            _operation = operation;
            _errorMessage = null;
            if (operation is OperationKind.Bootstrap or OperationKind.Authenticate)
            {
                _identity = null;
            }

            _statusMessage = operation switch
            {
                OperationKind.Status => "正在读取身份状态…",
                OperationKind.Bootstrap => "正在创建首位管理员…",
                OperationKind.Authenticate => "正在验证登录信息…",
                _ => _statusMessage
            };
            start = new OperationStart(_requestVersion, operation, cancellation);
        }

        NotifyStateChanged();
        return start;
    }

    private void ApplyStatus(OperationStart start, StationIdentityStatus status)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            _status = status;
            _bootstrapRequiredOverride = null;
            _errorMessage = null;
            _statusMessage = status.BootstrapRequired
                ? "需要安装流程提供的 BootstrapToken 才能创建首位管理员；本页面不会发行 Token。"
                : "身份状态已读取。";
            NotifyStateChanged();
        }
    }

    private void ApplyBootstrapResult(OperationStart start, BootstrapAdministratorResult result)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start))
            {
                result.RecoveryKit?.Dispose();
                return;
            }

            if (result.Succeeded)
            {
                _identity = result.Identity;
                _recoveryKit = result.RecoveryKit;
                _recoveryOwner = result.Identity?.PrincipalId;
                _bootstrapRequiredOverride = false;
                _errorMessage = null;
                _statusMessage = _recoveryKit is null
                    ? "首位管理员已创建；请刷新状态确认。"
                    : "首位管理员已创建。恢复包只可领取一次，请立即显示、保管或复制后清除。";
            }
            else
            {
                result.RecoveryKit?.Dispose();
                _identity = null;
                _errorMessage = SafeReason(result.ReasonCode, "IdentityBootstrapRejected");
                _statusMessage = "首位管理员创建未完成，可修正信息后重试。";
            }

            NotifyStateChanged();
        }
    }

    private void ApplyAuthenticationResult(OperationStart start, AuthenticationResult result)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            if (result.Succeeded)
            {
                _identity = result.Identity;
                _errorMessage = null;
                _statusMessage = "登录成功。";
            }
            else
            {
                _identity = null;
                _errorMessage = SafeReason(result.ReasonCode, "IdentityAuthenticationRejected");
                _statusMessage = "登录未完成，可重试。";
            }

            NotifyStateChanged();
        }
    }

    private void CompleteFailure(OperationStart start, string status, string errorCode)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            _identity = null;
            _errorMessage = errorCode;
            _statusMessage = status;
            NotifyStateChanged();
        }
    }

    private void CompleteCancellation(OperationStart start)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            _identity = null;
            _errorMessage = null;
            _statusMessage = "身份请求已取消，可重新操作。";
            NotifyStateChanged();
        }
    }

    private void CompleteOperation(OperationStart start)
    {
        bool changed = false;
        lock (_sync)
        {
            if (IsCurrentLocked(start))
            {
                _isBusy = false;
                _operation = OperationKind.None;
                _activeCancellation = null;
                changed = true;
            }
        }

        start.Cancellation.Dispose();
        if (changed) NotifyStateChanged();
    }

    private void SetInputError(string status)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _errorMessage = "IdentityInputInvalid";
            _statusMessage = status;
            _identity = null;
        }

        NotifyStateChanged();
    }

    private bool IsCurrentLocked(OperationStart start) =>
        !_disposed && _requestVersion == start.Version && _operation == start.Kind &&
        ReferenceEquals(_activeCancellation, start.Cancellation);

    private void NotifyRecoveryStateChanged()
    {
        OnPropertyChanged(nameof(HasRecoveryKit));
        OnPropertyChanged(nameof(CanRevealRecoveryKit));
        OnPropertyChanged(nameof(CurrentSession));
        OnPropertyChanged(nameof(SessionStatus));
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanBootstrap));
        OnPropertyChanged(nameof(CanAuthenticate));
        OnPropertyChanged(nameof(CurrentStatus));
        OnPropertyChanged(nameof(BootstrapRequired));
        OnPropertyChanged(nameof(UsableAdministratorCount));
        OnPropertyChanged(nameof(ValidRecoveryCodeCount));
        OnPropertyChanged(nameof(StatusReasonCode));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(CurrentIdentity));
        OnPropertyChanged(nameof(HasIdentity));
        OnPropertyChanged(nameof(IdentitySummary));
        OnPropertyChanged(nameof(HasRecoveryKit));
        OnPropertyChanged(nameof(CanRevealRecoveryKit));
        RefreshCommand.RaiseCanExecuteChanged();
    }

    private static bool HasRequiredInput(params string?[] values) =>
        values.All(value => !string.IsNullOrEmpty(value));

    private static string SafeReason(string? reason, string fallback)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 96) return fallback;
        return reason.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.') ? reason : fallback;
    }
}
