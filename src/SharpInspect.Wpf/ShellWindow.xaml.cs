using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

internal sealed record StateRow(string Label, string Value);

public partial class ShellWindow : Window
{
    internal Task SubmitIdentityLoginSmokeAsync(string userName, string password) => IdentityPanel.SubmitLoginSmokeAsync(userName, password);
    internal Task RefreshIdentityAdministrationSmokeAsync() => IdentityAdministrationPanel.RefreshSmokeAsync();
    internal Task<RuntimeCommandOutcome?> SubmitIdentityAdministrationCreateSmokeAsync(
        string userName, string displayName, string password, string stepUpPassword) =>
        IdentityAdministrationPanel.SubmitCreateSmokeAsync(userName, displayName, password, stepUpPassword);
    internal Task WaitForSessionLockAsync() => _sessionLock;
    internal bool IsAdministratorRecoveryPrivacyVisible =>
        IsPrivacyLocked && PrivacyAdministratorRecoveryPanel.Visibility == Visibility.Visible;
    internal Task OpenAdministratorRecoverySmokeAsync()
    {
        if (!IsPrivacyLocked) ShowPrivacyCover();
        return OpenAdministratorRecoveryAsync();
    }
    internal async Task SubmitLockedLoginSmokeAsync(string userName, string password)
    {
        if (!IsPrivacyLocked) throw new InvalidOperationException("SessionLockPageRequired");
        LockedUserNameBox.Text = userName;
        LockedPasswordBox.Password = password;
        await AuthenticateLockedInputsAsync();
        if (LockedPasswordBox.Password.Length != 0) throw new InvalidOperationException("SessionPasswordInputNotCleared");
    }
    internal Task SubmitLogoutSmokeAsync() => LogoutFromInputsAsync();
    private readonly StationShellViewModel _viewModel;
    private readonly CommandTraceViewModel? _traceViewModel;
    private readonly AuditIntegrityViewModel? _integrityViewModel;
    private readonly IdentityViewModel? _identityViewModel;
    private readonly IdentityAdministrationViewModel? _identityAdministrationViewModel;
    private readonly AdministratorRecoveryViewModel? _administratorRecoveryViewModel;
    private readonly AlarmViewModel? _alarmViewModel;
    private readonly AlgorithmResultHistoryViewModel? _algorithmResultViewModel;
    private bool _algorithmResultSelectionLoaded;
    private bool _allowSmokeShutdown;
    private bool _traceSelectionLoaded;
    private bool _integritySelectionLoaded;
    private bool _identitySelectionLoaded;
    private bool _identityAdministrationSelectionLoaded;
    private bool _administratorRecoverySelectionLoaded;
    private bool _alarmSelectionLoaded;
    private long _lastInputReport;
    private Task _sessionLock = Task.CompletedTask;
    public bool IsPrivacyLocked { get; private set; }

    public ShellWindow(StationShellViewModel viewModel, CommandTraceViewModel? traceViewModel = null,
        AuditIntegrityViewModel? integrityViewModel = null, IdentityViewModel? identityViewModel = null,
        IdentityAdministrationViewModel? identityAdministrationViewModel = null,
        AdministratorRecoveryViewModel? administratorRecoveryViewModel = null,
        AlarmViewModel? alarmViewModel = null, AlgorithmResultHistoryViewModel? algorithmResultViewModel = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _traceViewModel = traceViewModel;
        _integrityViewModel = integrityViewModel;
        _identityViewModel = identityViewModel;
        _identityAdministrationViewModel = identityAdministrationViewModel;
        _administratorRecoveryViewModel = administratorRecoveryViewModel;
        _alarmViewModel = alarmViewModel;
        _algorithmResultViewModel = algorithmResultViewModel;
        DataContext = viewModel;
        TracePanel.DataContext = traceViewModel;
        IntegrityPanel.DataContext = integrityViewModel;
        IdentityPanel.DataContext = identityViewModel;
        IdentityAdministrationPanel.DataContext = identityAdministrationViewModel;
        AdministratorRecoveryPanel.DataContext = administratorRecoveryViewModel;
        PrivacyAdministratorRecoveryPanel.DataContext = administratorRecoveryViewModel;
        AlarmPanel.DataContext = alarmViewModel;
        AlgorithmResultsPanel.DataContext = algorithmResultViewModel;
        viewModel.PropertyChanged += Refresh;
        viewModel.State.PropertyChanged += Refresh;
        if (traceViewModel is not null) traceViewModel.PropertyChanged += TraceChanged;
        if (integrityViewModel is not null) integrityViewModel.PropertyChanged += IntegrityChanged;
        if (identityViewModel is not null) identityViewModel.PropertyChanged += IdentityChanged;
        if (identityAdministrationViewModel is not null)
            identityAdministrationViewModel.PropertyChanged += IdentityAdministrationChanged;
        if (administratorRecoveryViewModel is not null)
            administratorRecoveryViewModel.PropertyChanged += AdministratorRecoveryChanged;
        if (alarmViewModel is not null) alarmViewModel.PropertyChanged += AlarmChanged;
        PreviewMouseDown += ReportInputActivity;
        PreviewKeyDown += ReportInputActivity;
        SystemEvents.SessionSwitch += OperatingSystemSessionSwitch;
        RenderState();
    }

    public void RevealPage()
    {
        if (_identityViewModel is { HasSessionService: true } &&
            _identityViewModel.CurrentSession.State != InteractiveSessionState.Authenticated)
        {
            ShowPrivacyCover();
            return;
        }
        IsPrivacyLocked = false;
        PrivacyCover.Visibility = Visibility.Collapsed;
    }

    internal void AllowSmokeShutdown() => _allowSmokeShutdown = true;
    internal void VerifyAuditLayout()
    {
        UpdateLayout();
        var bottom = IntegrityPanel.TranslatePoint(new Point(0, IntegrityPanel.ActualHeight), TracePanel).Y;
        var top = TraceAvailablePanel.TranslatePoint(new Point(0, 0), TracePanel).Y;
        if (bottom > top + 0.5) throw new InvalidOperationException("Audit panel overlaps command trace controls.");
    }
    internal void VerifyMaintenanceLayout()
    {
        if (IdentityPanel.Visibility != Visibility.Visible ||
            IdentityAdministrationPanel.Visibility != Visibility.Visible ||
            AdministratorRecoveryPanel.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Maintenance identity panels are not reachable.");
    }
    internal void VerifyAlarmLayout()
    {
        if (_viewModel.SelectedSection != "Alarms" || AlarmPanel.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Alarm panel is not reachable.");
    }
    internal void SelectAlarmForSmoke(Guid instanceId) =>
        AlarmPanel.AlarmGrid.SelectedItem = AlarmPanel.ViewModel!.VisibleInstances.Single(item => item.InstanceId == instanceId);
    internal void VerifyAlarmSelectionForSmoke(Guid instanceId)
    {
        if (AlarmPanel.AlarmGrid.SelectedItem is not AlarmInstanceSnapshot selected || selected.InstanceId != instanceId ||
            AlarmPanel.ViewModel?.SelectedAlarm?.InstanceId != instanceId)
            throw new InvalidOperationException("Alarm selection did not survive a fresh snapshot.");
    }
    internal void VerifyAlarmSummaryForSmoke()
    {
        if (AlarmPanel.ViewModel is not { IsSnapshotFresh: true } model ||
            AlarmPanel.AlarmSummaryText.Text != model.SummaryText ||
            !AlarmPanel.AlarmSummaryText.Text.Contains($"原始实例 {model.TotalAlarmCount}", StringComparison.Ordinal))
            throw new InvalidOperationException("The displayed alarm summary does not match the current snapshot.");
    }
    internal void BringIdentityAdministrationIntoViewForSmoke()
    {
        VerifyMaintenanceLayout();
        UpdateLayout();
        IdentityAdministrationPanel.UpdateLayout();
        IdentityAdministrationPanel.BringIntoView();
        MainScrollViewer.UpdateLayout();
        var panelTop = IdentityAdministrationPanel.TranslatePoint(new Point(0, 0), MainScrollViewer).Y;
        var targetOffset = MainScrollViewer.VerticalOffset + panelTop;
        MainScrollViewer.ScrollToVerticalOffset(Math.Clamp(targetOffset, 0, MainScrollViewer.ScrollableHeight));
        MainScrollViewer.UpdateLayout();
    }
    internal void VerifyIdentityAdministrationBottomReachableForSmoke() =>
        IdentityAdministrationPanel.ScrollToBottomForSmoke();
    internal void ShowUnavailable() => FreshnessLabel.Text = "状态不可用，请保持生产禁用";
    private void RevealPageClick(object sender, RoutedEventArgs e) => RevealPage();
    private void LockPage(object sender, RoutedEventArgs e) => LockPage();
    private void LockPage()
    {
        ShowPrivacyCover();
        if (_identityViewModel is not null) _sessionLock = _identityViewModel.LockSessionAsync(SessionLockReason.WindowHidden);
    }

    private void ShowPrivacyCover()
    {
        IsPrivacyLocked = true;
        IdentityPanel.ClearSensitiveInputs();
        IdentityAdministrationPanel.ClearSensitiveInputs();
        AdministratorRecoveryPanel.ClearSensitiveInputs();
        PrivacyAdministratorRecoveryPanel.ClearSensitiveInputs();
        AlarmPanel.ClearSensitiveInputs();
        LockedUserNameBox.Clear();
        LockedPasswordBox.Clear();
        LockedSignInStatus.Text = "";
        PrivacyRecoveryStatus.Text = "";
        PrivacyCover.Visibility = Visibility.Visible;
        LockedLoginPanel.Visibility = _identityViewModel?.HasSessionService == true ? Visibility.Visible : Visibility.Collapsed;
        ReturnToLockedLoginButton.Visibility = Visibility.Collapsed;
        OpenAdministratorRecoveryButton.Visibility = _administratorRecoveryViewModel is null
            ? Visibility.Collapsed : Visibility.Visible;
        PrivacyAdministratorRecoveryPanel.Visibility = Visibility.Collapsed;
        PrivacyInstruction.Text = _identityViewModel?.HasSessionService == true ? "会话已锁定，请重新登录。" : "点击下方“显示页面”恢复查看。";
    }

    private async void OpenAdministratorRecoveryClick(object sender, RoutedEventArgs e)
        => await OpenAdministratorRecoveryAsync();

    private async Task OpenAdministratorRecoveryAsync()
    {
        if (_administratorRecoveryViewModel is null) return;
        OpenAdministratorRecoveryButton.IsEnabled = false;
        PrivacyRecoveryStatus.Text = "正在等待当前会话退出审计…";
        try
        {
            if (_identityViewModel?.HasSessionService == true)
            {
                await _identityViewModel.LogoutSessionAsync();
                if (_identityViewModel.CurrentSession.State != InteractiveSessionState.Unauthenticated)
                {
                    PrivacyRecoveryStatus.Text = "当前会话尚未退出，管理员恢复入口保持关闭。";
                    return;
                }
            }

            PrivacyAdministratorRecoveryPanel.ClearSensitiveInputs();
            PrivacyAdministratorRecoveryPanel.Visibility = Visibility.Visible;
            LockedLoginPanel.Visibility = Visibility.Collapsed;
            ReturnToLockedLoginButton.Visibility = Visibility.Visible;
            OpenAdministratorRecoveryButton.Visibility = Visibility.Collapsed;
            PrivacyInstruction.Text = "管理员恢复已打开；页面仍处于遮蔽状态，不会自动解锁或登录。";
            await _administratorRecoveryViewModel.RefreshAsync();
            PrivacyRecoveryStatus.Text = "恢复完成后请返回登录；恢复流程不会解除页面遮蔽。";
        }
        catch
        {
            PrivacyRecoveryStatus.Text = "管理员恢复入口暂不可用，请返回登录或保持生产禁用。";
        }
        finally
        {
            OpenAdministratorRecoveryButton.IsEnabled = true;
        }
    }

    private void ReturnToLockedLoginClick(object sender, RoutedEventArgs e)
    {
        PrivacyAdministratorRecoveryPanel.ClearSensitiveInputs();
        PrivacyAdministratorRecoveryPanel.Visibility = Visibility.Collapsed;
        ReturnToLockedLoginButton.Visibility = Visibility.Collapsed;
        OpenAdministratorRecoveryButton.Visibility = _administratorRecoveryViewModel is null
            ? Visibility.Collapsed : Visibility.Visible;
        LockedLoginPanel.Visibility = _identityViewModel?.HasSessionService == true
            ? Visibility.Visible : Visibility.Collapsed;
        PrivacyInstruction.Text = _identityViewModel?.HasSessionService == true
            ? "会话已锁定，请重新登录。" : "点击下方“显示页面”恢复查看。";
    }

    private async void LockedSignInClick(object sender, RoutedEventArgs e)
        => await AuthenticateLockedInputsAsync();

    private async Task AuthenticateLockedInputsAsync()
    {
        if (_identityViewModel is null || !LockedSignInButton.IsEnabled) return;
        var userName = LockedUserNameBox.Text;
        var password = LockedPasswordBox.Password;
        LockedUserNameBox.Clear();
        LockedPasswordBox.Clear();
        LockedSignInButton.IsEnabled = false;
        try
        {
            await _sessionLock;
            var result = await _identityViewModel.AuthenticateAsync(userName, password);
            if (result?.Succeeded == true) RevealPage();
            else LockedSignInStatus.Text = "登录未完成，请稍后重试。";
        }
        finally { LockedSignInButton.IsEnabled = true; }
    }

    private async void LogoutClick(object sender, RoutedEventArgs e)
        => await LogoutFromInputsAsync();

    private async Task LogoutFromInputsAsync()
    {
        ShowPrivacyCover();
        if (_identityViewModel is not null) await _identityViewModel.LogoutSessionAsync();
    }

    private async void ReportInputActivity(object sender, InputEventArgs e)
    {
        if (IsPrivacyLocked || _identityViewModel is null) return;
        var now = Stopwatch.GetTimestamp();
        if (now - _lastInputReport < Stopwatch.Frequency) return;
        _lastInputReport = now;
        await _identityViewModel.ReportSessionActivityAsync();
    }

    private void OperatingSystemSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionLock) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            ShowPrivacyCover();
            if (_identityViewModel is not null) _sessionLock = _identityViewModel.LockSessionAsync(SessionLockReason.OperatingSystemLock);
        });
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowSmokeShutdown) { e.Cancel = true; LockPage(); }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= Refresh;
        _viewModel.State.PropertyChanged -= Refresh;
        if (_traceViewModel is not null) _traceViewModel.PropertyChanged -= TraceChanged;
        if (_integrityViewModel is not null) _integrityViewModel.PropertyChanged -= IntegrityChanged;
        if (_identityViewModel is not null) _identityViewModel.PropertyChanged -= IdentityChanged;
        if (_identityAdministrationViewModel is not null)
            _identityAdministrationViewModel.PropertyChanged -= IdentityAdministrationChanged;
        if (_administratorRecoveryViewModel is not null)
            _administratorRecoveryViewModel.PropertyChanged -= AdministratorRecoveryChanged;
        if (_alarmViewModel is not null) _alarmViewModel.PropertyChanged -= AlarmChanged;
        SystemEvents.SessionSwitch -= OperatingSystemSessionSwitch;
        IdentityPanel.ClearSensitiveInputs();
        IdentityAdministrationPanel.ClearSensitiveInputs();
        AdministratorRecoveryPanel.ClearSensitiveInputs();
        PrivacyAdministratorRecoveryPanel.ClearSensitiveInputs();
        AlarmPanel.ClearSensitiveInputs();
        base.OnClosed(e);
    }

    private void Refresh(object? sender, PropertyChangedEventArgs e) => RenderState();
    private void TraceChanged(object? sender, PropertyChangedEventArgs e) => RenderState();
    private void IntegrityChanged(object? sender, PropertyChangedEventArgs e) => RenderState();
    private void IdentityChanged(object? sender, PropertyChangedEventArgs e) => RenderState();
    private void IdentityAdministrationChanged(object? sender, PropertyChangedEventArgs e) => RenderState();
    private void AdministratorRecoveryChanged(object? sender, PropertyChangedEventArgs e) => RenderState();
    private void AlarmChanged(object? sender, PropertyChangedEventArgs e) => RenderState();

    private void RenderState()
    {
        Dispatcher.VerifyAccess();
        if (!IsPrivacyLocked && _identityViewModel?.CurrentSession.State == InteractiveSessionState.Locked) ShowPrivacyCover();
        var fresh = _viewModel.Freshness == SnapshotFreshness.Fresh && _viewModel.State.IsFresh;
        var s = fresh ? _viewModel.CurrentSnapshot : null;
        FreshnessLabel.Text = fresh ? (s!.Ready ? "状态新鲜 · Ready" : "状态新鲜 · 生产禁用") : "状态未知 / 已陈旧";
        SectionLabel.Text = _viewModel.SelectedSection switch
        {
            "Alarms" => "报警 · 当前快照摘要", "Recipes" => "配方 · 当前快照摘要",
            "Trace" => "追溯 · 命令事实查询", "Maintenance" => "维护 · 当前快照摘要",
            "Engineering" => "手动 / 预览 / 标定 · 当前快照摘要", "Qualification" => "资格 · 当前快照摘要",
            _ => "生产 · 当前完整状态"
        };
        var traceSelected = _viewModel.SelectedSection == "Trace";
        var maintenanceSelected = _viewModel.SelectedSection == "Maintenance";
        var alarmSelected = _viewModel.SelectedSection == "Alarms";
        if (maintenanceSelected) SectionLabel.Text = "维护 / 管理 · 身份引导、账号授权与恢复";
        SnapshotPanel.Visibility = traceSelected || maintenanceSelected || alarmSelected ? Visibility.Collapsed : Visibility.Visible;
        BlockersPanel.Visibility = traceSelected || maintenanceSelected || alarmSelected ? Visibility.Collapsed : Visibility.Visible;
        TracePanel.Visibility = traceSelected ? Visibility.Visible : Visibility.Collapsed;
        AlgorithmResultsPanel.Visibility = traceSelected && _algorithmResultViewModel is not null
            ? Visibility.Visible : Visibility.Collapsed;
        if (!traceSelected) _algorithmResultSelectionLoaded = false;
        else if (!_algorithmResultSelectionLoaded && _algorithmResultViewModel is not null)
        {
            _algorithmResultSelectionLoaded = true;
            _ = _algorithmResultViewModel.RefreshAsync();
        }
        AlarmPanel.Visibility = alarmSelected ? Visibility.Visible : Visibility.Collapsed;
        if (_traceViewModel is null)
        {
            TraceUnavailablePanel.Visibility = Visibility.Visible;
            TraceAvailablePanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            TraceUnavailablePanel.Visibility = Visibility.Collapsed;
            TraceAvailablePanel.Visibility = Visibility.Visible;
            if (!traceSelected) _traceSelectionLoaded = false;
            else if (!_traceSelectionLoaded)
            {
                _traceSelectionLoaded = true;
                _ = _traceViewModel.RefreshAsync();
            }
        }
        if (_integrityViewModel is null)
        {
            IntegrityUnavailablePanel.Visibility = Visibility.Visible;
            IntegrityAvailablePanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            IntegrityUnavailablePanel.Visibility = Visibility.Collapsed;
            IntegrityAvailablePanel.Visibility = Visibility.Visible;
            if (!traceSelected) _integritySelectionLoaded = false;
            else if (!_integritySelectionLoaded)
            {
                _integritySelectionLoaded = true;
                _ = _integrityViewModel.RefreshAsync();
            }
        }
        IdentityPanel.Visibility = maintenanceSelected ? Visibility.Visible : Visibility.Collapsed;
        IdentityAdministrationPanel.Visibility = maintenanceSelected ? Visibility.Visible : Visibility.Collapsed;
        AdministratorRecoveryPanel.Visibility = maintenanceSelected ? Visibility.Visible : Visibility.Collapsed;
        if (!alarmSelected)
        {
            if (_alarmSelectionLoaded)
            {
                _alarmSelectionLoaded = false;
                AlarmPanel.ClearSensitiveInputs();
            }
        }
        else if (_alarmViewModel is not null && !_alarmSelectionLoaded)
        {
            _alarmSelectionLoaded = true;
            _ = _alarmViewModel.RefreshAsync();
        }
        if (!maintenanceSelected)
        {
            if (_identitySelectionLoaded)
            {
                _identitySelectionLoaded = false;
                IdentityPanel.ClearSensitiveInputs();
            }
            if (_identityAdministrationSelectionLoaded)
            {
                _identityAdministrationSelectionLoaded = false;
                IdentityAdministrationPanel.ClearSensitiveInputs();
            }
            if (_administratorRecoverySelectionLoaded)
            {
                _administratorRecoverySelectionLoaded = false;
                AdministratorRecoveryPanel.ClearSensitiveInputs();
            }
        }
        else if (_identityViewModel is not null && !_identitySelectionLoaded)
        {
            _identitySelectionLoaded = true;
            _ = _identityViewModel.RefreshAsync();
        }
        if (maintenanceSelected && _identityAdministrationViewModel is not null &&
            !_identityAdministrationSelectionLoaded)
        {
            _identityAdministrationSelectionLoaded = true;
            _ = _identityAdministrationViewModel.RefreshAsync();
        }
        if (maintenanceSelected && _administratorRecoveryViewModel is not null &&
            !_administratorRecoverySelectionLoaded)
        {
            _administratorRecoverySelectionLoaded = true;
            _ = _administratorRecoveryViewModel.RefreshAsync();
        }
        string Value(object? value) => s is null ? "未知" : value?.ToString() ?? "无";
        RuntimeRows.ItemsSource = new[]
        {
            new StateRow("Runtime Epoch", Value(s?.RuntimeEpoch)),
            new StateRow("Snapshot Revision", Value(s?.Revision)),
            new StateRow("观察时间 UTC", Value(s?.ObservedAtUtc.ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'"))),
            new StateRow("生命周期 / 模式", Value(s is null ? null : $"{s.Lifecycle} / {s.Mode}")),
            new StateRow("武装 / Ready / Busy", Value(s is null ? null : $"{s.ArmState} / {s.Ready} / {s.Busy}")),
            new StateRow("握手 / 恢复", Value(s is null ? null : $"{s.Handshake} / {s.Recovery}")),
            new StateRow("Active Recipe", Value(s?.ActiveRecipe)),
            new StateRow("当前执行关联", Value(s?.CurrentExecution)),
            new StateRow("会话 / 主体", Value(s is null ? null : $"{s.Session.State} / {s.Session.PrincipalId ?? "无"}")),
            new StateRow("会话 ID", Value(s?.Session.SessionId)),
            new StateRow("报警 active/latched", Value(s is null ? null : $"{s.Alarms.ActiveCount} / {s.Alarms.LatchedCount} · blocks={s.Alarms.BlocksProduction}"))
        };
        SubsystemRows.ItemsSource = new[]
        {
            new StateRow("相机连接 / 配置", Value(s is null ? null : $"{s.Camera.Connection} / {s.Camera.Configuration}")),
            new StateRow("相机采集 / 缓冲", Value(s is null ? null : $"{s.Camera.Acquisition} / {s.Camera.Buffers}")),
            new StateRow("PLC 连接 / 心跳", Value(s is null ? null : $"{s.Plc.Connection} / {s.Plc.Heartbeat}")),
            new StateRow("PLC 同步", Value(s?.Plc.Synchronization)),
            new StateRow("追溯存储", Value(s is null ? null : $"{s.Store.State} / {s.Store.ReasonCode}")),
            new StateRow("证据健康", Value(s?.Evidence.State)),
            new StateRow("待留图 / 待投递", Value(s is null ? null : $"{s.Evidence.PendingRequiredImages} / {s.Evidence.PendingDeliveries}")),
            new StateRow("框架 / Provider 资格", Value(s is null ? null : $"{s.Qualification.Framework} / {s.Qualification.Provider}")),
            new StateRow("性能 / 工位资格", Value(s is null ? null : $"{s.Qualification.Performance} / {s.Qualification.StationAcceptance}")),
            new StateRow("性能健康 / 预算违例", Value(s is null ? null : $"{s.Performance.State} / {s.Performance.BudgetViolation}"))
        };
        BlockersLabel.Text = s is null ? "当前状态未知，等待新的完整快照。" : string.Join(" · ", s.AdmissionBlockers);
        var outcome = _viewModel.LastCommandOutcome;
        OutcomeLabel.Text = _viewModel.CommandFailureCode is not null
            ? $"本次命令状态不可确认：{_viewModel.CommandFailureCode}，等待 Runtime 确认。"
            : outcome is null ? "命令：尚未提交" :
            $"最近命令结果：{outcome.Disposition} / 审计={outcome.Audit} / {outcome.ReasonCode} · {outcome.CorrelationId}";
        var progress = s?.LastCommand;
        ProgressLabel.Text = progress is null ? "最终状态：等待新的关联快照" :
            $"关联操作状态：{progress.State} / {progress.ReasonCode} · {progress.CorrelationId}";
    }
}
