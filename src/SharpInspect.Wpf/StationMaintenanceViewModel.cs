using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

public sealed record StationMaintenanceReasonOption(StationMaintenanceReason Reason, string Label);

/// <summary>The page only submits a governed command. Hiding or disposing it never stops the station or host.</summary>
public sealed class StationMaintenanceViewModel : ObservableObject, IDisposable
{
    private readonly IStationRuntime _runtime;
    private readonly IStationMaintenanceQuery _query;
    private readonly IStepUpAuthentication _stepUp;
    private readonly IInteractiveSessionService _sessions;
    private readonly IUiDispatcher _dispatcher;
    private readonly string _installationBindingHash;
    private CancellationTokenSource _pending = new();
    private long _generation;
    private long _readVersion;
    private bool _busy, _disposed, _fresh;
    private StationMaintenanceSnapshot? _state;
    private string _status = "尚未读取维护状态。";
    private StationMaintenanceReasonOption _reason;
    internal event EventHandler? SensitiveInputsInvalidated;

    public StationMaintenanceViewModel(IStationRuntime runtime, IStationMaintenanceQuery query,
        IStepUpAuthentication stepUp, IInteractiveSessionService sessions, string installationBindingHash,
        IUiDispatcher? dispatcher = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _stepUp = stepUp ?? throw new ArgumentNullException(nameof(stepUp));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _installationBindingHash = new PrepareStationMaintenanceCommand(Guid.NewGuid(), new(CommandSource.PhysicalConsole),
            Guid.NewGuid(), installationBindingHash, StationMaintenanceReason.MaintenanceInspection).InstallationBindingHash;
        _dispatcher = dispatcher ?? new DispatcherUiDispatcher();
        ReasonOptions = Array.AsReadOnly(new[]
        {
            new StationMaintenanceReasonOption(StationMaintenanceReason.SoftwareUpgrade, "软件升级"),
            new StationMaintenanceReasonOption(StationMaintenanceReason.StationRecovery, "工位恢复"),
            new StationMaintenanceReasonOption(StationMaintenanceReason.MaintenanceInspection, "维护检查")
        });
        _reason = ReasonOptions[0];
        RefreshCommand = new(RefreshAsync, () => !_disposed && !_busy);
        _sessions.Changed += SessionChanged;
    }

    public IReadOnlyList<StationMaintenanceReasonOption> ReasonOptions { get; }
    public StationMaintenanceReasonOption SelectedReason
    {
        get => _reason;
        set { if (!_busy && ReasonOptions.Contains(value)) SetProperty(ref _reason, value); }
    }
    public AsyncRelayCommand RefreshCommand { get; }
    public string StatusMessage => _status;
    public string PhaseText => !_fresh ? "未读取" : _state?.Phase switch
    {
        StationMaintenancePhase.Idle => "可申请维护",
        StationMaintenancePhase.Authorizing => "正在核对授权",
        StationMaintenancePhase.Draining => "正在正常停产",
        StationMaintenancePhase.Retiring => "正在等待后台工作退出",
        StationMaintenancePhase.PreparedAwaitingHostExit => "已准备交接，等待宿主退出",
        StationMaintenancePhase.SafeMaintenance => "安全维护状态，需要处理",
        _ => "维护交接未配置"
    };
    public bool CanSubmit => !_disposed && !_busy && _fresh && _state is { Available: true, Phase: StationMaintenancePhase.Idle } &&
        Usable(_sessions.Current);

    public async Task RefreshAsync()
    {
        var generation = _generation;
        var readVersion = Interlocked.Increment(ref _readVersion);
        try
        {
            var state = await _query.ReadMaintenanceAsync(_pending.Token).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || generation != _generation || readVersion != _readVersion) return;
                _state = state; _fresh = true; _status = state.Available ? "已读取工位维护状态。" : "此工位尚未配置维护交接。";
                Notify();
            });
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { await _dispatcher.InvokeAsync(() => { if (generation == _generation && readVersion == _readVersion && !_disposed) { _fresh = false; _status = "维护状态不可用。"; Notify(); } }); }
    }

    public async Task<RuntimeCommandOutcome?> PrepareWithStepUpAsync(string password)
    {
        InteractiveSession? session = null;
        PrepareStationMaintenanceCommand? command = null;
        long generation = 0;
        Guid epoch = Guid.Empty;
        CancellationToken token = default;
        await _dispatcher.InvokeAsync(() =>
        {
            if (!CanSubmit) return;
            _busy = true; generation = _generation; token = _pending.Token; session = _sessions.Current; epoch = _state!.RuntimeEpoch;
            command = new(Guid.NewGuid(), new(CommandSource.PhysicalConsole, session.PrincipalId, session.SessionId),
                Guid.NewGuid(), _installationBindingHash, _reason.Reason);
            _status = "正在核对维护权限与身份…"; Notify();
        });
        if (command is null) return null;
        try
        {
            var grant = await _stepUp.ReauthenticateAsync(new(Guid.NewGuid(), command.Invocation,
                new(Permission.PrepareStationMaintenance, command.CorrelationId, command.AuthorizationTarget,
                    AuditedCommandKind.PrepareStationMaintenance), password), token).ConfigureAwait(false);
            password = string.Empty;
            if (!grant.Succeeded || grant.GrantId is not { } grantId || grantId == Guid.Empty)
            {
                await SetStatusAsync(generation, "身份验证未通过，未提交维护请求。");
                return null;
            }
            var current = await _query.ReadMaintenanceAsync(token).ConfigureAwait(false);
            if (!current.Available || current.RuntimeEpoch != epoch || current.Phase != StationMaintenancePhase.Idle)
            { await SetStatusAsync(generation, "维护状态已变化，未提交本次请求。"); return null; }
            Task<RuntimeCommandOutcome>? submitted = null;
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || generation != _generation || !SameSession(session, _sessions.Current)) return;
                var request = new PrepareStationMaintenanceCommand(command.CorrelationId,
                    command.Invocation with { StepUpGrantId = grantId }, command.OperationId, _installationBindingHash, command.Reason);
                Interlocked.Increment(ref _readVersion);
                _fresh = false; _status = "维护请求已发出，正在等待受理结果…"; Notify();
                submitted = _runtime.SubmitAsync(request, CancellationToken.None).AsTask();
            });
            if (submitted is null) return null;
            var outcome = await submitted.ConfigureAwait(false);
            await SetStatusAsync(generation, outcome.Disposition == CommandDisposition.Accepted
                ? "已受理停产交接；仍需等待后台退出和独立协调器接管。" : "维护请求被拒绝，请刷新状态后处理。");
            return outcome;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { await SetStatusAsync(generation, "请求结果未知，请刷新维护状态。"); return null; }
        finally
        {
            password = string.Empty;
            await _dispatcher.InvokeAsync(() => { _busy = false; SensitiveInputsInvalidated?.Invoke(this, EventArgs.Empty); Notify(); });
        }
    }

    /// <summary>Invalidates page work only. No Runtime command, session logout or host shutdown is issued.</summary>
    public void Deactivate()
    {
        _generation++; _fresh = false; _pending.Cancel(); _pending.Dispose(); _pending = new();
        SensitiveInputsInvalidated?.Invoke(this, EventArgs.Empty); Notify();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _sessions.Changed -= SessionChanged; Deactivate(); _pending.Dispose();
    }
    private void SessionChanged(object? sender, InteractiveSessionChangedEventArgs args) =>
        _ = _dispatcher.InvokeAsync(() => { if (!_disposed) Deactivate(); });
    private async Task SetStatusAsync(long generation, string message) =>
        await _dispatcher.InvokeAsync(() => { if (!_disposed && generation == _generation) { _status = message; Notify(); } });
    private void Notify()
    { OnPropertyChanged(nameof(StatusMessage)); OnPropertyChanged(nameof(PhaseText)); OnPropertyChanged(nameof(CanSubmit)); RefreshCommand.RaiseCanExecuteChanged(); }
    private static bool Usable(InteractiveSession? value) => value is { State: InteractiveSessionState.Authenticated, SessionId: { } id } &&
        id != Guid.Empty && !string.IsNullOrEmpty(value.PrincipalId);
    private static bool SameSession(InteractiveSession? left, InteractiveSession? right) => Usable(left) && Usable(right) &&
        left!.SessionId == right!.SessionId && left.PrincipalId == right.PrincipalId;
}
