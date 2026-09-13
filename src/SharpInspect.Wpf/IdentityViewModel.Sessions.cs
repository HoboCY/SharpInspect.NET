using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

public sealed partial class IdentityViewModel
{
    private readonly IInteractiveSessionService? _sessions;
    private readonly IUiDispatcher _dispatcher;
    private InteractiveSession _session;
    private Guid? _recoveryOwner;
    public bool HasSessionService => _sessions is not null;
    // Dispatcher 通知只是可能滞后的展示提示；敏感操作始终读取 Runtime 持有的当前会话。
    public InteractiveSession CurrentSession { get { if (_sessions is not null) return _sessions.Current;
        lock (_sync) return _session; } }
    public string SessionStatus => CurrentSession.State switch
    { InteractiveSessionState.Authenticated => "已登录", InteractiveSessionState.Locked => "已锁定，请重新登录", _ => "尚未登录" };

    public async Task LockSessionAsync(SessionLockReason reason)
    {
        // 先撤销页面仍在等待的身份请求；锁屏转换本身仍由会话服务执行。
        CancelPendingOperation();
        if (_sessions is not null) await _sessions.LockAsync(_sessions.Current.SessionId, reason).ConfigureAwait(true);
    }

    public async Task LogoutSessionAsync()
    {
        CancelPendingOperation();
        if (_sessions is not null) await _sessions.LogoutAsync(_sessions.Current.SessionId).ConfigureAwait(true);
    }

    public async Task ReportSessionActivityAsync()
    {
        if (_sessions?.Current is { State: InteractiveSessionState.Authenticated, SessionId: { } id })
            await _sessions.ReportActivityAsync(id).ConfigureAwait(true);
    }

    private async void SessionChanged(object? sender, InteractiveSessionChangedEventArgs args)
    {
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                lock (_sync)
                {
                    if (_disposed || _sessions?.Current != args.Session) return;
                    _session = args.Session;
                    if (_session.State != InteractiveSessionState.Authenticated) _identity = null;
                    OnPropertyChanged(nameof(CurrentSession));
                    OnPropertyChanged(nameof(SessionStatus));
                    NotifyStateChanged();
                }
            });
        }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) { }
    }
}
