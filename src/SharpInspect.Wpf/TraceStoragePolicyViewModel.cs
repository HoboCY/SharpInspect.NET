using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Presentation-only adapter for the governed trace storage policy service.
/// It never supplies policy defaults and never treats a UI projection as proof
/// that a policy was durably published.
/// </summary>
public sealed class TraceStoragePolicyViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ITraceStoragePolicyService? _service;
    private readonly IInteractiveSessionService? _sessions;
    private readonly IStepUpAuthentication? _stepUpAuthentication;
    private readonly IUiDispatcher _dispatcher;
    private readonly object _sync = new();
    private CancellationTokenSource? _pending;
    private long _generation;
    private bool _disposed;
    private bool _fresh;
    private TraceStoragePolicyAccess? _access;
    private TraceStoragePolicyPublication? _current;
    private TraceStoragePolicySnapshot? _snapshot;
    private TraceStoragePreflightReport? _preflight;
    private TraceStoragePolicyHistoryPage? _history;
    private TraceStoragePolicyResult? _lastResult;
    private string? _errorCode;
    private string _statusMessage;
    private IReadOnlyList<string> _validationErrors = Array.Empty<string>();

    private readonly record struct OperationStart(long Version, CancellationTokenSource Cancellation,
        InteractiveSession Session);

    public TraceStoragePolicyViewModel(ITraceStoragePolicyService? service = null,
        IInteractiveSessionService? sessions = null,
        IStepUpAuthentication? stepUpAuthentication = null,
        IUiDispatcher? dispatcher = null)
    {
        _service = service;
        _sessions = sessions;
        _stepUpAuthentication = stepUpAuthentication;
        _dispatcher = dispatcher ?? new DispatcherUiDispatcher();
        Editor = new TraceStoragePolicyEditor();
        _statusMessage = IsConfigured
            ? "尚未读取追溯存储政策，请刷新。"
            : "追溯存储政策不可用：服务或当前交互会话未完整配置。";
        if (_sessions is not null) _sessions.Changed += SessionChanged;
    }

    public TraceStoragePolicyEditor Editor { get; }
    public TraceStoragePolicyAccess? Access { get { lock (_sync) return _access; } }
    public TraceStoragePolicyPublication? Current { get { lock (_sync) return _current; } }
    public TraceStoragePolicySnapshot? Snapshot { get { lock (_sync) return _snapshot; } }
    public TraceStoragePreflightReport? Preflight { get { lock (_sync) return _preflight; } }
    public TraceStoragePolicyHistoryPage? History { get { lock (_sync) return _history; } }
    public TraceStoragePolicyResult? LastResult { get { lock (_sync) return _lastResult; } }
    public IReadOnlyList<string> ValidationErrors { get { lock (_sync) return _validationErrors; } }
    public string? ErrorCode { get { lock (_sync) return _errorCode; } }
    public string StatusMessage { get { lock (_sync) return _statusMessage; } }
    public bool IsConfigured => _service is not null && _sessions is not null;
    public bool IsAuthenticated => IsUsableSession(_sessions?.Current ?? UnauthenticatedSession);
    public bool IsBusy { get { lock (_sync) return _pending is not null; } }
    public bool CanRefresh => IsConfigured && IsAuthenticated && !IsBusy && !_disposed;
    public bool CanPublish
    {
        get
        {
            lock (_sync)
            {
                return IsConfigured && IsAuthenticated && _fresh && !_disposed &&
                    _pending is null && _access is { Allowed: true } access &&
                    (!access.RequiresStepUp || _stepUpAuthentication is not null);
            }
        }
    }

    /// <summary>The panel clears native credential controls after a session change.</summary>
    internal event EventHandler? SessionInvalidated;

    /// <summary>Explicitly copies the current immutable policy into the editor.</summary>
    public bool LoadCurrent()
    {
        if (!_dispatcher.CheckAccess)
        {
            var hasCurrent = Current is not null;
            _ = InvokeOnUiAsync(() => LoadCurrentCore());
            return hasCurrent;
        }

        return LoadCurrentCore();
    }

    private bool LoadCurrentCore()
    {
        var current = Current;
        if (current is null) return false;
        Editor.Load(current.Policy);
        SetValidationErrors(Array.Empty<string>());
        return true;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!TryBegin(cancellationToken, out var start)) return;
        try
        {
            var token = start.Cancellation.Token;
            var invocation = CreateInvocation(start.Session);
            var access = await _service!.GetAccessAsync(invocation, token).ConfigureAwait(false);
            var current = await _service!.ReadAsync(null, token).ConfigureAwait(false);
            var preflight = await _service!.GetPreflightAsync(token).ConfigureAwait(false);
            var history = await _service!.QueryAsync(new TraceStoragePolicyFilter(PageSize: 20), token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var refreshError = !access.Allowed ? access.ReasonCode :
                !current.Available ? current.ReasonCode :
                !history.Available ? history.ReasonCode : null;
            await PublishAsync(start, () =>
            {
                _access = access;
                _current = current.Publication;
                _snapshot = current.Snapshot;
                _preflight = preflight;
                _history = history;
                _fresh = access.Allowed && current.Available && history.Available && preflight is not null;
                _errorCode = refreshError;
                _statusMessage = refreshError is null
                    ? _current is null ? "当前没有已发布政策；可编辑后提交首个版本。" :
                        $"已读取政策 v{_current.Version}，请显式加载到编辑器后发布。"
                    : $"政策状态不可用：{refreshError}";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (start.Cancellation.IsCancellationRequested)
        {
            await PublishAsync(start, () =>
            {
                _fresh = false;
                _errorCode = "TraceStoragePolicyPresentationWaitEnded";
                _statusMessage = "已结束界面等待；已接纳的发布由 Runtime 继续处理，请刷新状态。";
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await PublishAsync(start, () =>
            {
                _fresh = false;
                _errorCode = SafeReason(exception.Message, "TraceStoragePolicyPresentationUnavailable");
                _statusMessage = "政策状态不可用，请刷新并查看 Runtime 审计记录。";
            }).ConfigureAwait(false);
        }
        finally
        {
            await EndAsync(start).ConfigureAwait(false);
        }
    }

    public async Task<TraceStoragePolicyResult?> PublishAsync(string reason,
        string stepUpPassword, CancellationToken cancellationToken = default)
    {
        if (!TryBegin(cancellationToken, out var start)) return null;
        var password = stepUpPassword ?? string.Empty;
        try
        {
            if (!Editor.TryBuild(out var policy, out var errors) || policy is null)
            {
                await PublishAsync(start, () =>
                {
                    _validationErrors = errors;
                    _errorCode = "TraceStoragePolicyInputInvalid";
                    _statusMessage = "政策输入无效，请修正所有标记字段后重试。";
                }).ConfigureAwait(false);
                return null;
            }
            await PublishAsync(start, () => _validationErrors = Array.Empty<string>()).ConfigureAwait(false);

            var token = start.Cancellation.Token;
            var invocation = CreateInvocation(start.Session);
            var access = await _service!.GetAccessAsync(invocation, token).ConfigureAwait(false);
            if (!access.Allowed)
            {
                await PublishAsync(start, () =>
                {
                    _access = access;
                    _errorCode = access.ReasonCode;
                    _statusMessage = $"发布被拒绝：{access.ReasonCode}";
                }).ConfigureAwait(false);
                return null;
            }
            var expectedVersion = Current?.Version ?? 0;
            var correlation = Guid.NewGuid();
            var command = new PublishTraceStoragePolicyCommand(correlation, invocation,
                expectedVersion, policy, reason ?? string.Empty);

            if (access.RequiresStepUp)
            {
                var stepUpAuthentication = _stepUpAuthentication;
                if (stepUpAuthentication is null)
                {
                    await PublishAsync(start, () =>
                    {
                        _errorCode = "TraceStoragePolicyStepUpUnavailable";
                        _statusMessage = "当前政策要求新鲜 Step-Up，但认证服务未配置。";
                    }).ConfigureAwait(false);
                    return null;
                }
                var binding = new StepUpBinding(Permission.ManageProductionPolicy, correlation,
                    command.AuthorizationTarget, AuditedCommandKind.PublishTraceStoragePolicy);
                StepUpResult authentication;
                try
                {
                    authentication = await stepUpAuthentication.ReauthenticateAsync(
                        new StepUpRequest(correlation, invocation, binding, password), token)
                        .ConfigureAwait(false);
                }
                finally { password = string.Empty; }
                token.ThrowIfCancellationRequested();
                if (!authentication.Succeeded || authentication.GrantId is not { } grant || grant == Guid.Empty)
                {
                    var rejectedReason = SafeReason(authentication.ReasonCode,
                        "StepUpAuthenticationRejected");
                    await PublishAsync(start, () =>
                    {
                        _errorCode = rejectedReason;
                        _statusMessage = $"发布未开始：{rejectedReason}";
                    }).ConfigureAwait(false);
                    return null;
                }
                if (!IsSameSession(start.Session, _sessions!.Current))
                {
                    await PublishAsync(start, () =>
                    {
                        _fresh = false;
                        _errorCode = "TraceStoragePolicySessionChanged";
                        _statusMessage = "当前会话已变化，已丢弃待提交政策。";
                    }).ConfigureAwait(false);
                    return null;
                }
                invocation = CreateInvocation(_sessions!.Current, grant);
                command = new PublishTraceStoragePolicyCommand(correlation, invocation,
                    expectedVersion, policy, reason ?? string.Empty);
            }

            var result = await _service!.PublishAsync(command, token).ConfigureAwait(false);
            await PublishAsync(start, () =>
            {
                _lastResult = result;
                if (result.Succeeded && result.Publication is not null)
                {
                    _current = result.Publication;
                    _snapshot = result.Snapshot ?? new TraceStoragePolicySnapshot(result.Publication);
                    _preflight = null;
                    _history = null;
                    _fresh = false;
                    _errorCode = null;
                    _statusMessage = $"政策 v{result.Publication.Version} 已由 Runtime 接纳并持久化；请刷新读取新的预检与历史。";
                }
                else
                {
                    _errorCode = SafeReason(result.Outcome.ReasonCode, "TraceStoragePolicyRejected");
                    _statusMessage = $"发布结果：{_errorCode}";
                }
            }).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (start.Cancellation.IsCancellationRequested)
        {
            await PublishAsync(start, () =>
            {
                _fresh = false;
                _errorCode = "TraceStoragePolicyPresentationWaitEnded";
                _statusMessage = "已结束界面等待；Runtime 结果仍需刷新确认。";
            }).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await PublishAsync(start, () =>
            {
                _fresh = false;
                _errorCode = SafeReason(exception.Message, "TraceStoragePolicyPresentationUnavailable");
                _statusMessage = "发布边界不可用，请刷新并查看 Runtime 审计记录。";
            }).ConfigureAwait(false);
            return null;
        }
        finally
        {
            password = string.Empty;
            await EndAsync(start).ConfigureAwait(false);
        }
    }

    public void CancelPendingOperations()
    {
        CancellationTokenSource? pending;
        lock (_sync)
        {
            pending = _pending;
            _fresh = false;
        }
        if (pending is not null)
            _ = Task.Run(() => { try { pending.Cancel(); } catch (ObjectDisposedException) { } });
        RaiseStateChanged();
    }

    public void Deactivate() => CancelPendingOperations();

    public async ValueTask DisposeAsync()
    {
        CancelPendingOperations();
        lock (_sync) { _disposed = true; ++_generation; }
        if (_sessions is not null) _sessions.Changed -= SessionChanged;
        await Task.CompletedTask;
    }

    private bool TryBegin(CancellationToken caller, out OperationStart start)
    {
        start = default;
        if (!IsConfigured) return false;
        lock (_sync)
        {
            if (_disposed || _pending is not null) return false;
            var session = _sessions!.Current;
            if (!IsUsableSession(session)) return false;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller);
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            var version = ++_generation;
            _pending = cancellation;
            start = new OperationStart(version, cancellation, session);
        }
        RaiseStateChanged();
        return true;
    }

    private async ValueTask PublishAsync(OperationStart start, Action update)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                if (_disposed || start.Version != _generation ||
                    !IsSameSession(start.Session, _sessions?.Current ?? UnauthenticatedSession)) return;
                update();
            }
            RaiseStateChanged();
        }).ConfigureAwait(false);
    }

    private async ValueTask EndAsync(OperationStart start)
    {
        CancellationTokenSource? dispose = null;
        lock (_sync)
        {
            if (ReferenceEquals(_pending, start.Cancellation))
            {
                _pending = null;
                if (_generation == start.Version) ++_generation;
                dispose = start.Cancellation;
            }
        }
        dispose?.Dispose();
        await RaiseStateChangedAsync().ConfigureAwait(false);
    }

    private void SessionChanged(object? sender, InteractiveSessionChangedEventArgs args)
    {
        CancelPendingOperations();
        // Invalidate the old projection synchronously with the session event.
        // A queued UI callback must never erase a result obtained by a newer
        // session while the dispatcher was busy.
        lock (_sync)
        {
            ++_generation;
            _fresh = false;
            _access = null;
            _current = null;
            _snapshot = null;
            _preflight = null;
            _history = null;
            _lastResult = null;
            _validationErrors = Array.Empty<string>();
            _errorCode = null;
            _statusMessage = "用户会话已变化，请重新刷新。";
        }
        _ = InvokeOnUiAsync(() =>
        {
            SessionInvalidated?.Invoke(this, EventArgs.Empty);
            RaiseStateChanged();
        });
    }

    private void SetValidationErrors(IReadOnlyList<string> errors)
    {
        lock (_sync) _validationErrors = errors.ToArray();
        RaisePropertyChanged(nameof(ValidationErrors));
    }

    private void RaiseStateChanged()
    {
        if (!_dispatcher.CheckAccess)
        {
            _ = InvokeOnUiAsync(RaiseStateChangedCore);
            return;
        }

        RaiseStateChangedCore();
    }

    private ValueTask RaiseStateChangedAsync()
    {
        if (_dispatcher.CheckAccess)
        {
            RaiseStateChangedCore();
            return ValueTask.CompletedTask;
        }

        return _dispatcher.InvokeAsync(RaiseStateChangedCore);
    }

    private void RaiseStateChangedCore()
    {
        foreach (var name in new[] { nameof(Access), nameof(Current), nameof(Snapshot), nameof(Preflight),
            nameof(History), nameof(LastResult), nameof(ValidationErrors), nameof(ErrorCode),
            nameof(StatusMessage), nameof(IsBusy), nameof(CanRefresh), nameof(CanPublish), nameof(IsAuthenticated) })
            RaisePropertyChanged(name);
    }

    private void RaisePropertyChanged(string propertyName)
    {
        if (!_dispatcher.CheckAccess)
        {
            _ = InvokeOnUiAsync(() => RaisePropertyChangedCore(propertyName));
            return;
        }

        RaisePropertyChangedCore(propertyName);
    }

    private void RaisePropertyChangedCore(string propertyName) =>
        OnPropertyChanged(propertyName);

    private async Task InvokeOnUiAsync(Action action)
    {
        try
        {
            await _dispatcher.InvokeAsync(action).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (_disposed)
        {
            // The dispatcher may be shutting down while a presentation-only
            // notification is queued.  Runtime state remains authoritative.
        }
    }

    private static bool IsUsableSession(InteractiveSession session) =>
        session.State == InteractiveSessionState.Authenticated &&
        session.SessionId is { } id && id != Guid.Empty &&
        !string.IsNullOrWhiteSpace(session.PrincipalId);

    private static bool IsSameSession(InteractiveSession left, InteractiveSession right) =>
        IsUsableSession(left) && IsUsableSession(right) && left.SessionId == right.SessionId &&
        string.Equals(left.PrincipalId, right.PrincipalId, StringComparison.Ordinal);

    private static CommandInvocation CreateInvocation(InteractiveSession session, Guid? grant = null) =>
        new(CommandSource.PhysicalConsole, session.PrincipalId, session.SessionId, grant);

    private static string SafeReason(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
        value.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.')) ? fallback : value;

    private static readonly InteractiveSession UnauthenticatedSession =
        new(InteractiveSessionState.Unauthenticated, null, null);
}
