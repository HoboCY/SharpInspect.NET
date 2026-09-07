using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Presentation adapter for the local administrator recovery boundary. Recovery
/// is a physical-console workflow; this type never creates a session, declares
/// the station stopped, or asserts production readiness for the caller.
/// </summary>
public sealed class AdministratorRecoveryViewModel : ObservableObject, IAsyncDisposable
{
    private readonly record struct OperationStart(long Version, CancellationTokenSource Cancellation);

    private const string UnavailableReason = "AdministratorRecoveryUnavailable";
    private const string InputInvalidReason = "AdministratorRecoveryInputInvalid";
    private static readonly IReadOnlySet<string> SafeReasonCodes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            UnavailableReason, InputInvalidReason, "AdministratorRecoveryRejected",
            "AdministratorRecoveryRotationRejected", "AdministratorRecoveryCustodyRejected",
            "AdministratorRecoveryAuthenticationRequired", "AdministratorRecoveryProtocolFailure",
            "AdministratorRecovered", "RecoveryKitRotated", "RecoveryKitCustodyConfirmed",
            "RecoveryAvailable", "RecoveryKitRotationRequired", "RecoveryKitCustodyConfirmationRequired",
            "PhysicalConsoleRequired", "RecoveryRuntimeAuthorityUnavailable", "RecoveryRuntimeBusy",
            "RecoveryRuntimeLeaseExpired", "RecoveryRuntimeStopped", "RecoveryRequiresDisarmedStation",
            "RecoveryInspectionConflict", "RecoveryDeliveryConflict", "RecoveryAuditUnavailable",
            "SafetyStopUnverified", "RecoveryStationMismatch", "OperationIdConflict",
            "UsableAdministratorPresent", "BootstrapRequired", "RecoveryWorkflowConflict",
            "RecoveryCodeInvalid", "CredentialDerivationFailed", "HumanAccountCapacityExceeded",
            "RecoveryCodeUnavailable", "RecoveryCapacityExceeded", "DisplayNameMismatch",
            "RecoveryKitUnavailable", "RecoveryKitIdRequired", "RecoveryStationRequired",
            "RecoveryOperationDeadlineExceeded", "RecoveryOwnerRequired",
            "ReauthenticationRejected", "RecoveryIdentityChanged", "RecoverySessionConflict",
            "RecoverySessionAvailable", "RecoverySessionMismatch", "RecoverySessionRequired",
            "InteractiveSessionAuthorityRequired", "RecoveryKitStateMismatch",
            "RecoveryConfirmationCodeInvalid", "RecoveryCodeMinimumRemaining",
            "InvocationRequired", "OperationIdRequired", "PermissionDenied", "RecoveryUnavailable"
        };

    private readonly ILocalAdministratorRecovery? _recovery;
    private readonly IInteractiveSessionService? _sessions;
    private readonly IUiDispatcher _dispatcher;
    private readonly object _sync = new();
    private CancellationTokenSource? _activeCancellation;
    private AdministratorRecoveryStatus? _status;
    private OneTimeSecret? _pendingRecoveryKit;
    private Guid? _pendingKitId;
    private string? _pendingKitPrincipalId;
    private Guid? _pendingKitSessionId;
    private InteractiveSession _session;
    private string _statusMessage;
    private string? _errorCode;
    private bool _isBusy;
    private long _operationVersion;
    private bool _disposed;

    public AdministratorRecoveryViewModel(ILocalAdministratorRecovery? recovery,
        IInteractiveSessionService? sessions, IUiDispatcher? dispatcher = null)
    {
        _recovery = recovery;
        _sessions = sessions;
        _dispatcher = dispatcher ?? new DispatcherUiDispatcher();
        _session = sessions?.Current ?? UnauthenticatedSession;
        _statusMessage = IsConfigured
            ? "尚未读取管理员恢复状态，请刷新。"
            : "管理员恢复不可用：未配置恢复服务。";

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => CanRefresh);
        if (_sessions is not null) _sessions.Changed += SessionChanged;
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public bool IsConfigured => _recovery is not null;

    public bool IsBusy
    {
        get { lock (_sync) return _isBusy; }
    }

    public bool IsUnavailable
    {
        get
        {
            lock (_sync)
            {
                return !IsConfigured || _status is null ||
                    (!_status.RecoveryAvailable && KitState == RecoveryKitState.Unavailable);
            }
        }
    }

    public bool CanRefresh => IsConfigured && !IsBusy && !_disposed;

    /// <summary>
    /// Recovery is available only when the Runtime's current status projection
    /// says so. The Runtime still rechecks every physical and identity condition.
    /// </summary>
    public bool CanRecover => IsConfigured && !IsBusy && !_disposed &&
        _status?.RecoveryAvailable == true && KitState != RecoveryKitState.CustodyConfirmationRequired &&
        _pendingKitId is null;

    public bool CanRotateRecoveryKit => IsConfigured && !IsBusy && !_disposed &&
        (KitState == RecoveryKitState.RotationRequired || KitState == RecoveryKitState.Available ||
            (KitState == RecoveryKitState.CustodyConfirmationRequired && !HasRecoveryKit)) &&
        IsUsableSession(CurrentSession);

    public bool CanConfirmRecoveryKitCustody => IsConfigured && !IsBusy && !_disposed &&
        KitState == RecoveryKitState.CustodyConfirmationRequired &&
        RecoveryKitId is { } kitId && kitId != Guid.Empty && IsUsableSession(CurrentSession);

    public AdministratorRecoveryStatus? CurrentStatus
    {
        get { lock (_sync) return _status; }
    }

    public string StationId
    {
        get { lock (_sync) return _status?.StationId ?? string.Empty; }
    }

    public string? StatusReasonCode
    {
        get { lock (_sync) return _status is null ? null : SafeReason(_status.ReasonCode, UnavailableReason); }
    }

    public RecoveryKitState KitState
    {
        get
        {
            lock (_sync)
            {
                return _pendingKitId.HasValue
                    ? RecoveryKitState.CustodyConfirmationRequired
                    : _status?.KitState ?? RecoveryKitState.Unavailable;
            }
        }
    }

    public Guid? RecoveryKitId
    {
        get
        {
            lock (_sync) return _pendingKitId ?? _status?.KitId;
        }
    }

    /// <summary>True while the one-time result has not been displayed or cleared.</summary>
    public bool HasRecoveryKit
    {
        get { return HasCurrentPendingRecoveryKit(); }
    }

    /// <summary>Only the page may consume the secret for its one-time display.</summary>
    public bool CanRevealRecoveryKit => HasCurrentPendingRecoveryKit();

    /// <summary>
    /// Checks live session authority for a kit that is already displayed by the
    /// page. This reads the session service directly so a queued UI cleanup
    /// cannot keep an old secret usable.
    /// </summary>
    internal bool IsRecoveryKitSessionCurrent(string? principalId, Guid? sessionId)
    {
        var session = _sessions?.Current;
        return session is not null && MatchesAuthenticatedSession(session, principalId, sessionId);
    }

    public InteractiveSession CurrentSession => _sessions?.Current ?? ReadSession();

    public bool IsAuthenticated => IsUsableSession(CurrentSession);

    public string StatusLabel
    {
        get
        {
            lock (_sync)
            {
                if (!IsConfigured) return "未配置";
                if (_isBusy) return "处理中";
                if (_status is null) return "尚未读取状态";
                return _status.KitState switch
                {
                    RecoveryKitState.RotationRequired => "需要轮换恢复包",
                    RecoveryKitState.CustodyConfirmationRequired => "需要确认恢复包托管",
                    RecoveryKitState.Available => "恢复包可用",
                    _ => _status.RecoveryAvailable ? "可进行恢复" : "不可用"
                };
            }
        }
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

    /// <summary>
    /// Reads the Runtime-owned status projection. No recovery secret is returned
    /// by this read operation.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            await ApplyUnavailableAsync(UnavailableReason).ConfigureAwait(true);
            return;
        }

        var start = Begin(cancellationToken);
        if (!start.HasValue) return;

        try
        {
            AdministratorRecoveryStatus status;
            try
            {
                status = await _recovery!.GetRecoveryStatusAsync(start.Value.Cancellation.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                await ApplyUnavailableAsync(UnavailableReason, start).ConfigureAwait(true);
                return;
            }

            await ApplyStatusAsync(start.Value, status).ConfigureAwait(true);
        }
        finally
        {
            Complete(start.Value);
        }
    }

    /// <summary>
    /// Uses the station-bound recovery code to create a new administrator. The
    /// Runtime owns the stop, Ready, code-consumption, and audit checks; this
    /// method never signs the new administrator in.
    /// </summary>
    public async Task<AdministratorRecoveryResult?> RecoverAdministratorAsync(
        string recoveryCode, string userName, string displayName, string newPassword,
        CancellationToken cancellationToken = default)
    {
        if (!HasInput(recoveryCode, userName, displayName, newPassword))
        {
            await ApplyErrorAsync(InputInvalidReason).ConfigureAwait(true);
            return null;
        }

        var stationId = StationId;
        if (string.IsNullOrEmpty(stationId) || !CanRecover)
        {
            await ApplyUnavailableAsync(UnavailableReason).ConfigureAwait(true);
            return null;
        }

        var start = Begin(cancellationToken);
        if (!start.HasValue) return null;

        var operationId = Guid.NewGuid();
        try
        {
            AdministratorRecoveryResult result;
            try
            {
                result = await _recovery!.RecoverAdministratorAsync(
                    new RecoverAdministratorRequest(operationId, stationId, recoveryCode,
                        userName, displayName, newPassword), start.Value.Cancellation.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
            {
                return null;
            }
            catch
            {
                await ApplyUnavailableAsync(UnavailableReason, start).ConfigureAwait(true);
                return null;
            }

            await ApplyRecoveryResultAsync(start.Value, result).ConfigureAwait(true);
            return result;
        }
        finally
        {
            // These assignments prevent accidental reuse in this presentation
            // object. The request remains owned by the authoritative call only.
            recoveryCode = string.Empty;
            userName = string.Empty;
            displayName = string.Empty;
            newPassword = string.Empty;
            Complete(start.Value);
        }
    }

    /// <summary>
    /// Rotates the kit only for the current authenticated session. The supplied
    /// password is a fresh reauthentication input, not a session claim.
    /// </summary>
    public async Task<RecoveryKitRotationResult?> RotateRecoveryKitAsync(
        string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password))
        {
            await ApplyErrorAsync(InputInvalidReason).ConfigureAwait(true);
            return null;
        }

        if (!CanRotateRecoveryKit)
        {
            await ApplyUnavailableAsync(IsConfigured ? "AdministratorRecoveryAuthenticationRequired" :
                UnavailableReason).ConfigureAwait(true);
            return null;
        }

        var stationId = StationId;
        var start = Begin(cancellationToken);
        if (!start.HasValue) return null;

        var operationId = Guid.NewGuid();
        try
        {
            var session = await ReadCurrentSessionAsync(start.Value.Cancellation.Token)
                .ConfigureAwait(true);
            if (!IsUsableSession(session))
            {
                await ApplyErrorAsync("AdministratorRecoveryAuthenticationRequired", start)
                    .ConfigureAwait(true);
                return null;
            }

            RecoveryKitRotationResult result;
            try
            {
                result = await _recovery!.RotateRecoveryKitAsync(
                    new RotateRecoveryKitRequest(operationId, stationId,
                        CreateInvocation(session), password), start.Value.Cancellation.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
            {
                return null;
            }
            catch
            {
                await ApplyUnavailableAsync(UnavailableReason, start).ConfigureAwait(true);
                return null;
            }

            await ApplyRotationResultAsync(start.Value, result, operationId, session).ConfigureAwait(true);
            return result;
        }
        finally
        {
            password = string.Empty;
            Complete(start.Value);
        }
    }

    /// <summary>Consumes the displayed kit's custody confirmation through Runtime.</summary>
    public async Task<AdministratorRecoveryResult?> ConfirmRecoveryKitCustodyAsync(
        string confirmationCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(confirmationCode))
        {
            await ApplyErrorAsync(InputInvalidReason).ConfigureAwait(true);
            return null;
        }

        Guid kitId;
        string stationId;
        lock (_sync)
        {
            kitId = RecoveryKitId ?? Guid.Empty;
            stationId = _status?.StationId ?? string.Empty;
        }

        if (kitId == Guid.Empty || string.IsNullOrEmpty(stationId) ||
            !CanConfirmRecoveryKitCustody)
        {
            await ApplyUnavailableAsync(UnavailableReason).ConfigureAwait(true);
            return null;
        }

        var start = Begin(cancellationToken);
        if (!start.HasValue) return null;

        var operationId = Guid.NewGuid();
        try
        {
            var session = await ReadCurrentSessionAsync(start.Value.Cancellation.Token)
                .ConfigureAwait(true);
            if (!IsUsableSession(session))
            {
                await ApplyErrorAsync("AdministratorRecoveryAuthenticationRequired", start)
                    .ConfigureAwait(true);
                return null;
            }

            AdministratorRecoveryResult result;
            try
            {
                result = await _recovery!.ConfirmRecoveryKitCustodyAsync(
                    new ConfirmRecoveryKitCustodyRequest(operationId, stationId, kitId,
                        CreateInvocation(session), confirmationCode),
                    start.Value.Cancellation.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
            {
                return null;
            }
            catch
            {
                await ApplyUnavailableAsync(UnavailableReason, start).ConfigureAwait(true);
                return null;
            }

            await ApplyCustodyResultAsync(start.Value, result, operationId).ConfigureAwait(true);
            return result;
        }
        finally
        {
            confirmationCode = string.Empty;
            Complete(start.Value);
        }
    }

    /// <summary>
    /// Takes the runtime-provided kit once for local display and returns the
    /// issuing session alongside it. The owner is captured before the secret is
    /// consumed so a later session cannot be rebound to the old value.
    /// </summary>
    internal bool TryRevealRecoveryKit(out string? value, out string? principalId,
        out Guid? sessionId)
    {
        OneTimeSecret? secret;
        string? issuingPrincipalId;
        Guid? issuingSessionId;
        lock (_sync)
        {
            value = null;
            principalId = null;
            sessionId = null;
            if (_disposed || _pendingRecoveryKit is null) return false;
            if (!IsPendingKitSessionCurrentLocked())
            {
                ClearPendingRecoveryKitLocked();
                return false;
            }

            secret = _pendingRecoveryKit;
            issuingPrincipalId = _pendingKitPrincipalId;
            issuingSessionId = _pendingKitSessionId;
            _pendingRecoveryKit = null;
            _pendingKitPrincipalId = null;
            _pendingKitSessionId = null;
        }

        try { value = secret.TakeForDisplay(); }
        catch (InvalidOperationException) { value = null; }
        finally { secret.Dispose(); }
        NotifyStateChangedOnUi();
        if (value is null || issuingPrincipalId is null || issuingSessionId is null)
            return false;

        principalId = issuingPrincipalId;
        sessionId = issuingSessionId;
        return true;
    }

    /// <summary>
    /// Takes the runtime-provided kit once for local display. The caller owns the
    /// returned string and must clear its visual control when leaving the page.
    /// </summary>
    public string? RevealRecoveryKit()
    {
        return TryRevealRecoveryKit(out var value, out _, out _) ? value : null;
    }

    /// <summary>Stops pending work and invalidates late responses.</summary>
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

    /// <summary>Clears authority-sensitive recovery material without changing Runtime state.</summary>
    public void ClearSensitiveState()
    {
        if (!_dispatcher.CheckAccess)
        {
            _ = _dispatcher.InvokeAsync(ClearSensitiveState);
            return;
        }

        lock (_sync)
        {
            ClearPendingRecoveryKitLocked();
            _pendingKitId = null;
            _errorCode = null;
            _statusMessage = IsConfigured
                ? "恢复页面已清除敏感信息，请重新读取状态。"
                : "管理员恢复不可用：未配置恢复服务。";
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
            ClearPendingRecoveryKitLocked();
            _pendingKitId = null;
            _statusMessage = "恢复页面已释放。";
            _errorCode = null;
        }

        cancellation?.Cancel();
        if (_sessions is not null) _sessions.Changed -= SessionChanged;
        NotifyStateChangedOnUi();
        await Task.CompletedTask;
    }

    private async Task ApplyStatusAsync(OperationStart start, AdministratorRecoveryStatus status)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                if (!IsCurrentLocked(start)) return;
                if (_pendingKitId is { } pendingKitId)
                {
                    if (status.KitState == RecoveryKitState.CustodyConfirmationRequired &&
                        status.KitId == pendingKitId)
                    {
                        _status = status with { KitState = RecoveryKitState.CustodyConfirmationRequired,
                            KitId = pendingKitId };
                    }
                    else
                    {
                        // A fresh authority read supersedes a local delivery that no
                        // longer names the durable pending kit. Dispose the stale
                        // one-time secret before adopting the new projection.
                        ClearPendingRecoveryKitLocked();
                        _pendingKitId = null;
                        _status = status;
                    }
                }
                else _status = status;
                _errorCode = null;
                _statusMessage = status.KitState switch
                {
                    RecoveryKitState.CustodyConfirmationRequired =>
                        "恢复包等待托管确认；请使用已登录管理员继续，生产仍保持禁用。",
                    RecoveryKitState.RotationRequired =>
                        "恢复包需要轮换；请使用已登录管理员继续，生产仍保持禁用。",
                    RecoveryKitState.Available =>
                        "恢复包状态已读取；已登录管理员可按需轮换，生产仍保持禁用。",
                    _ when status.RecoveryAvailable => "管理员恢复状态已读取；生产仍保持禁用。",
                    _ => "管理员恢复当前不可用；生产仍保持禁用。"
                };
            }
            NotifyStateChanged();
        }).ConfigureAwait(true);
    }

    private async Task ApplyRecoveryResultAsync(OperationStart start,
        AdministratorRecoveryResult result)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                if (!IsCurrentLocked(start)) return;
                if (result.Succeeded)
                {
                    _status = _status is null
                        ? _status
                        : _status with
                        {
                            KitState = RecoveryKitState.RotationRequired,
                            RecoveredPrincipalId = result.Identity?.PrincipalId,
                            ProductionIdentityPrerequisitesMet = false
                        };
                    _errorCode = null;
                    _statusMessage = "管理员已恢复。请使用现有会话重新登录后轮换恢复包；本流程不会自动登录。";
                }
                else
                {
                    _errorCode = SafeReason(result.ReasonCode, "AdministratorRecoveryRejected");
                    _statusMessage = "管理员恢复未完成；生产仍保持禁用。";
                }
            }
            NotifyStateChanged();
        }).ConfigureAwait(true);
    }

    private async Task ApplyRotationResultAsync(OperationStart start,
        RecoveryKitRotationResult result, Guid expectedOperationId,
        InteractiveSession issuingSession)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                if (!IsCurrentLocked(start))
                {
                    result.RecoveryKit?.Dispose();
                    return;
                }

                if (!result.Succeeded)
                {
                    result.RecoveryKit?.Dispose();
                    _errorCode = SafeReason(result.ReasonCode, "AdministratorRecoveryRotationRejected");
                    _statusMessage = "恢复包轮换未完成；请重新认证后重试。";
                }
                else if (!IsCurrentAuthenticatedSessionLocked(issuingSession))
                {
                    result.RecoveryKit?.Dispose();
                    ClearPendingRecoveryKitLocked();
                    _pendingKitId = null;
                    _errorCode = "AdministratorRecoveryAuthenticationRequired";
                    _statusMessage = "当前会话已变化；恢复包未交付，请重新认证后重试。";
                }
                else if (result.OperationId != expectedOperationId ||
                    result.KitId is not { } kitId || kitId == Guid.Empty || result.RecoveryKit is null)
                {
                    result.RecoveryKit?.Dispose();
                    _errorCode = "AdministratorRecoveryProtocolFailure";
                    _statusMessage = "恢复包轮换结果不可用；请重新读取状态。";
                }
                else
                {
                    ClearPendingRecoveryKitLocked();
                    _pendingRecoveryKit = result.RecoveryKit;
                    _pendingKitId = kitId;
                    _pendingKitPrincipalId = issuingSession.PrincipalId;
                    _pendingKitSessionId = issuingSession.SessionId;
                    _status = _status is null
                        ? _status
                        : _status with { KitState = RecoveryKitState.CustodyConfirmationRequired,
                            KitId = kitId, ProductionIdentityPrerequisitesMet = false };
                    _errorCode = null;
                    _statusMessage = "新的恢复包已生成。请仅在受控页面显示或复制一次，并输入确认码完成托管确认；生产仍保持禁用。";
                }
            }
            NotifyStateChanged();
        }).ConfigureAwait(true);
    }

    private async Task ApplyCustodyResultAsync(OperationStart start,
        AdministratorRecoveryResult result, Guid expectedOperationId)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                if (!IsCurrentLocked(start)) return;
                if (result.Succeeded && result.OperationId == expectedOperationId)
                {
                    ClearPendingRecoveryKitLocked();
                    _pendingKitId = null;
                    _status = _status is null
                        ? _status
                        : _status with { KitState = RecoveryKitState.Available, KitId = null,
                            ProductionIdentityPrerequisitesMet = false };
                    _errorCode = null;
                    _statusMessage = "恢复包托管已确认。请重新读取状态；生产仍需独立通过全部资格。";
                }
                else if (result.Succeeded)
                {
                    _errorCode = "AdministratorRecoveryProtocolFailure";
                    _statusMessage = "托管确认结果不可用；请重新读取状态。";
                }
                else
                {
                    _errorCode = SafeReason(result.ReasonCode, "AdministratorRecoveryCustodyRejected");
                    _statusMessage = "恢复包托管尚未确认；请输入新的确认码或重新读取状态。";
                }
            }
            NotifyStateChanged();
        }).ConfigureAwait(true);
    }

    private async Task ApplyUnavailableAsync(string reason, OperationStart? start = null)
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
                _errorCode = SafeReason(reason, UnavailableReason);
                _statusMessage = "管理员恢复不可用，请确认本机恢复服务和当前状态。";
            }
            NotifyStateChanged();
        }).ConfigureAwait(true);
    }

    private async Task ApplyErrorAsync(string reason, OperationStart? start = null)
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
                _errorCode = SafeReason(reason, UnavailableReason);
                _statusMessage = "管理员恢复请求未完成；生产仍保持禁用。";
            }
            NotifyStateChanged();
        }).ConfigureAwait(true);
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
            _errorCode = null;
            _statusMessage = "正在处理管理员恢复请求…";
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

    private bool IsCurrentLocked(OperationStart start) => !_disposed &&
        _operationVersion == start.Version && ReferenceEquals(_activeCancellation, start.Cancellation);

    private async Task<InteractiveSession> ReadCurrentSessionAsync(CancellationToken cancellationToken)
    {
        if (_sessions is null) return UnauthenticatedSession;
        var session = await _sessions.GetSessionAsync(cancellationToken).ConfigureAwait(true);
        lock (_sync) _session = session;
        return session;
    }

    private void SessionChanged(object? sender, InteractiveSessionChangedEventArgs args)
    {
        CancelPendingOperations();
        ClearSensitiveState();
        _ = ApplySessionChangedAsync();
    }

    private async Task ApplySessionChangedAsync()
    {
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                var session = _sessions?.Current ?? ReadSession();
                lock (_sync)
                {
                    if (_disposed) return;
                    _session = session;
                    _statusMessage = session.State == InteractiveSessionState.Authenticated
                        ? "当前会话已变化，请重新读取恢复状态。"
                        : "会话已锁定或注销，恢复敏感信息已清除。";
                    _errorCode = null;
                }
                NotifyStateChanged();
            }).ConfigureAwait(true);
        }
        catch (Exception) when (_disposed) { }
    }

    private InteractiveSession ReadSession()
    {
        lock (_sync) return _session;
    }

    private bool HasCurrentPendingRecoveryKit()
    {
        lock (_sync)
        {
            if (_disposed || _pendingRecoveryKit is null) return false;
            if (IsPendingKitSessionCurrentLocked()) return true;

            // This path is intentionally synchronous. The session service is
            // authoritative even while the UI dispatcher still has a queued
            // SessionChanged cleanup callback.
            ClearPendingRecoveryKitLocked();
            return false;
        }
    }

    private bool IsPendingKitSessionCurrentLocked() =>
        _pendingRecoveryKit is not null &&
        IsCurrentAuthenticatedSessionLocked(_pendingKitPrincipalId, _pendingKitSessionId);

    private bool IsCurrentAuthenticatedSessionLocked(InteractiveSession expected) =>
        IsCurrentAuthenticatedSessionLocked(expected.PrincipalId, expected.SessionId);

    private bool IsCurrentAuthenticatedSessionLocked(string? principalId, Guid? sessionId)
    {
        var current = _sessions?.Current;
        return current is not null && MatchesAuthenticatedSession(current, principalId, sessionId);
    }

    private static bool MatchesAuthenticatedSession(InteractiveSession session,
        string? principalId, Guid? sessionId) =>
        IsUsableSession(session) && session.SessionId == sessionId &&
        string.Equals(session.PrincipalId, principalId, StringComparison.Ordinal);

    private void ClearPendingRecoveryKitLocked()
    {
        _pendingRecoveryKit?.Dispose();
        _pendingRecoveryKit = null;
        _pendingKitPrincipalId = null;
        _pendingKitSessionId = null;
    }

    private void NotifyStateChangedOnUi()
    {
        if (_dispatcher.CheckAccess) NotifyStateChanged();
        else _ = _dispatcher.InvokeAsync(NotifyStateChanged);
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsUnavailable));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanRecover));
        OnPropertyChanged(nameof(CanRotateRecoveryKit));
        OnPropertyChanged(nameof(CanConfirmRecoveryKitCustody));
        OnPropertyChanged(nameof(CurrentStatus));
        OnPropertyChanged(nameof(StationId));
        OnPropertyChanged(nameof(StatusReasonCode));
        OnPropertyChanged(nameof(KitState));
        OnPropertyChanged(nameof(RecoveryKitId));
        OnPropertyChanged(nameof(HasRecoveryKit));
        OnPropertyChanged(nameof(CanRevealRecoveryKit));
        OnPropertyChanged(nameof(CurrentSession));
        OnPropertyChanged(nameof(IsAuthenticated));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ErrorCode));
        RefreshCommand.RaiseCanExecuteChanged();
    }

    private static bool HasInput(params string?[] values) =>
        values.All(value => !string.IsNullOrEmpty(value));

    private static bool IsUsableSession(InteractiveSession session) =>
        session.State == InteractiveSessionState.Authenticated &&
        session.SessionId.HasValue && session.SessionId.Value != Guid.Empty &&
        !string.IsNullOrWhiteSpace(session.PrincipalId);

    private static CommandInvocation CreateInvocation(InteractiveSession session) =>
        new(CommandSource.PhysicalConsole, session.PrincipalId, session.SessionId);

    private static string SafeReason(string? reason, string fallback)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 96) return fallback;
        return SafeReasonCodes.Contains(reason) ? reason : fallback;
    }

    private static InteractiveSession UnauthenticatedSession =>
        new(InteractiveSessionState.Unauthenticated, null, null);
}
