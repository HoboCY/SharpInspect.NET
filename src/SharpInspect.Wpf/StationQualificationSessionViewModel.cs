using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>Explicit presentation over Runtime's non-production qualification session.</summary>
public sealed class StationQualificationSessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IStationRuntime? _runtime;
    private readonly IStationQualificationSessionService? _qualification;
    private readonly IInteractiveSessionService? _sessions;
    private readonly IStepUpAuthentication? _stepUp;
    private readonly IStationQualificationHistoryQuery? _history;
    private readonly IUiDispatcher _dispatcher;
    private readonly object _sync = new();
    private CancellationTokenSource? _pending;
    private long _generation;
    private bool _disposed;
    private bool _fresh;
    private StationQualificationAccess? _access;

    public StationQualificationSessionViewModel(IStationRuntime? runtime = null,
        IStationQualificationSessionService? qualificationService = null,
        IInteractiveSessionService? sessions = null, StationQualificationPlan? plan = null,
        IUiDispatcher? dispatcher = null, IStepUpAuthentication? stepUpAuthentication = null,
        IStationQualificationHistoryQuery? historyQuery = null)
    {
        _runtime = runtime; _qualification = qualificationService; _sessions = sessions;
        Plan = plan; _dispatcher = dispatcher ?? new DispatcherUiDispatcher();
        _stepUp = stepUpAuthentication; _history = historyQuery;
        if (sessions is not null) sessions.Changed += SessionChanged;
    }

    public StationQualificationPlan? Plan { get; }
    public StationQualificationSessionSnapshot? Snapshot { get; private set; }
    public RuntimeCommandOutcome? LastCommandOutcome { get; private set; }
    public IReadOnlyList<StationQualificationSessionEvent> History { get; private set; } = Array.Empty<StationQualificationSessionEvent>();
    public string StatusMessage { get; private set; } = "请刷新状态。进入资格会话前需重新认证。";
    public string? ErrorCode { get; private set; }
    public bool IsBusy { get { lock (_sync) return _pending is not null; } }
    public bool IsConfigured => _runtime is not null && _qualification is not null && _sessions is not null;
    public bool CanRefresh => IsConfigured && !IsBusy && !_disposed;
    public bool CanStart => CanRefresh && _fresh && _access?.CanRun == true && Plan is not null && _stepUp is not null &&
        Snapshot is { IsSessionActive: false, RecoveryRequired: false };
    public bool CanExit => CanRefresh && _fresh && _access?.CanRun == true &&
        Snapshot is { IsSessionActive: true, RecoveryRequired: false, ExitRequested: false };
    public bool ProductionAuthority => false;
    public bool CanIssueQualification => false;
    internal event EventHandler? SessionInvalidated;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async (invocation, version, token) =>
        {
            var access = await _qualification!.GetAccessAsync(invocation, token).ConfigureAwait(false);
            var state = access.CanRun ? await _qualification.GetSnapshotAsync(invocation, token).ConfigureAwait(false) :
                new StationQualificationSessionReadResult(false, access.ReasonCode, null);
            var station = await _runtime!.GetSnapshotAsync(token).ConfigureAwait(false);
            var page = access.CanRun && _history is not null ? await _history.QueryAsync(
                new StationQualificationHistoryFilter(SessionId: state.Snapshot?.SessionId, PageSize: 20), token).ConfigureAwait(false) : null;
            token.ThrowIfCancellationRequested();
            await PublishAsync(version, invocation.SessionId, () =>
            {
                _access = access;
                _fresh = state.Available && state.Snapshot is not null && state.Snapshot.RuntimeEpoch == station.RuntimeEpoch;
                Snapshot = _fresh ? state.Snapshot : null;
                History = page?.Available == true ? page.Events : Array.Empty<StationQualificationSessionEvent>();
                ErrorCode = !_fresh ? state.Available ? "StationQualificationSnapshotEpochMismatch" : state.ReasonCode :
                    page is { Available: false } ? page.ReasonCode : null;
                StatusMessage = _fresh ? $"{Snapshot!.Phase} · {Snapshot.ReasonCode} · 恢复：{Snapshot.Restoration}" :
                    "资格状态不可用，请检查服务和当前权限。";
            }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<RuntimeCommandOutcome?> StartAsync(string reason, string stepUpPassword,
        CancellationToken cancellationToken = default)
    {
        if (!CanStart) return Task.FromResult<RuntimeCommandOutcome?>(null);
        return SubmitAsync(async (invocation, token) =>
        {
            var correlation = Guid.NewGuid();
            var command = new StartStationQualificationSessionCommand(correlation, invocation, Plan!, reason);
            var binding = new StepUpBinding(Permission.RunStationQualification, correlation,
                command.AuthorizationTarget, AuditedCommandKind.StartStationQualificationSession);
            StepUpResult authentication;
            try { authentication = await _stepUp!.ReauthenticateAsync(new(correlation, invocation, binding, stepUpPassword), token).ConfigureAwait(false); }
            finally { stepUpPassword = string.Empty; }
            token.ThrowIfCancellationRequested();
            if (!authentication.Succeeded || authentication.GrantId is not { } grant || grant == Guid.Empty)
                throw new InvalidOperationException(authentication.ReasonCode);
            return new StartStationQualificationSessionCommand(correlation,
                new(invocation.Source, invocation.PrincipalId, invocation.SessionId, grant), Plan!, reason);
        }, cancellationToken);
    }

    public Task<RuntimeCommandOutcome?> ExitAsync(string reason, bool abort = false,
        CancellationToken cancellationToken = default)
    {
        if (!CanExit || Snapshot?.SessionId is not { } sessionId) return Task.FromResult<RuntimeCommandOutcome?>(null);
        return SubmitAsync((invocation, _) => Task.FromResult<StationQualificationCommand>(
            new ExitStationQualificationSessionCommand(Guid.NewGuid(), invocation, sessionId, abort, reason)), cancellationToken);
    }

    private async Task<RuntimeCommandOutcome?> SubmitAsync(
        Func<CommandInvocation, CancellationToken, Task<StationQualificationCommand>> create, CancellationToken token)
    {
        RuntimeCommandOutcome? outcome = null;
        await RunAsync(async (invocation, version, cancellation) =>
        {
            var command = await create(invocation, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            outcome = await _runtime!.SubmitAsync(command, cancellation).ConfigureAwait(false);
            await PublishAsync(version, invocation.SessionId, () =>
            {
                LastCommandOutcome = outcome;
                _fresh = false;
                StatusMessage = outcome.Disposition == CommandDisposition.Accepted
                    ? "请求已接纳；请刷新查看实际执行和恢复状态。" : "请求被拒绝：" + outcome.ReasonCode;
                ErrorCode = outcome.Disposition == CommandDisposition.Accepted ? null : outcome.ReasonCode;
            }).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        return outcome;
    }

    private async Task RunAsync(Func<CommandInvocation, long, CancellationToken, Task> action, CancellationToken callerToken)
    {
        CancellationTokenSource pending;
        long version;
        InteractiveSession? session;
        lock (_sync)
        {
            if (_disposed || _pending is not null || !IsConfigured) return;
            session = _sessions!.Current;
            if (session.State != InteractiveSessionState.Authenticated || session.SessionId is null) return;
            _pending = pending = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            pending.CancelAfter(TimeSpan.FromSeconds(10));
            version = ++_generation;
        }
        var invocation = new CommandInvocation(CommandSource.PhysicalConsole, session.PrincipalId, session.SessionId);
        await PublishAsync(version, session.SessionId, () => ErrorCode = null).ConfigureAwait(false);
        try { await action(invocation, version, pending.Token).WaitAsync(pending.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            await PublishAsync(version, session.SessionId, () =>
            {
                _fresh = false;
                StatusMessage = "已结束界面等待；已接纳的操作由工位继续处理，请刷新状态。";
                ErrorCode = "StationQualificationPresentationWaitEnded";
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await PublishAsync(version, session.SessionId, () =>
            {
                _fresh = false; ErrorCode = "StationQualificationPresentationUnavailable";
                StatusMessage = "操作未完成，请刷新状态并查看命令记录。";
            }).ConfigureAwait(false);
        }
        finally
        {
            var finalVersion = version;
            lock (_sync)
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                    if (_generation == version) finalVersion = ++_generation;
                }
            pending.Dispose();
            await PublishAsync(finalVersion, session.SessionId, () => { }).ConfigureAwait(false);
        }
    }

    private ValueTask PublishAsync(long version, Guid? sessionId, Action update) => _dispatcher.InvokeAsync(() =>
    {
        lock (_sync)
        {
            if (_disposed || version != _generation || _sessions?.Current is not { State: InteractiveSessionState.Authenticated } session ||
                session.SessionId != sessionId) return;
            update();
        }
        RaiseStateChanged();
    });

    public void CancelPendingOperations()
    {
        CancellationTokenSource? pending;
        lock (_sync) { pending = _pending; _fresh = false; }
        if (pending is not null) _ = Task.Run(() => { try { pending.Cancel(); } catch (ObjectDisposedException) { } });
    }

    private void SessionChanged(object? sender, InteractiveSessionChangedEventArgs args)
    {
        CancelPendingOperations();
        lock (_sync) ++_generation;
        _ = _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                _fresh = false; _access = null; Snapshot = null; LastCommandOutcome = null;
                History = Array.Empty<StationQualificationSessionEvent>();
                StatusMessage = "用户会话已变化，请重新刷新。"; ErrorCode = null;
            }
            SessionInvalidated?.Invoke(this, EventArgs.Empty);
            RaiseStateChanged();
        });
    }

    private void RaiseStateChanged()
    {
        foreach (var name in new[] { nameof(Snapshot), nameof(LastCommandOutcome), nameof(History), nameof(StatusMessage),
            nameof(ErrorCode), nameof(IsBusy), nameof(CanStart), nameof(CanExit), nameof(CanRefresh) }) OnPropertyChanged(name);
    }

    public ValueTask DisposeAsync()
    {
        CancelPendingOperations();
        lock (_sync) { _disposed = true; ++_generation; }
        if (_sessions is not null) _sessions.Changed -= SessionChanged;
        return ValueTask.CompletedTask;
    }
}
