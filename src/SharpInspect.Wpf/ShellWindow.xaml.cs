using System.ComponentModel;
using System.Windows;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

internal sealed record StateRow(string Label, string Value);

public partial class ShellWindow : Window
{
    private readonly StationShellViewModel _viewModel;
    private bool _allowSmokeShutdown;
    public bool IsPrivacyLocked { get; private set; }

    public ShellWindow(StationShellViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += Refresh;
        viewModel.State.PropertyChanged += Refresh;
        RenderState();
    }

    public void RevealPage()
    {
        IsPrivacyLocked = false;
        PrivacyCover.Visibility = Visibility.Collapsed;
    }

    internal void AllowSmokeShutdown() => _allowSmokeShutdown = true;
    internal void ShowUnavailable() => FreshnessLabel.Text = "状态不可用，请保持生产禁用";
    private void RevealPageClick(object sender, RoutedEventArgs e) => RevealPage();
    private void LockPage(object sender, RoutedEventArgs e) => LockPage();
    private void LockPage()
    {
        IsPrivacyLocked = true;
        PrivacyCover.Visibility = Visibility.Visible;
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
        base.OnClosed(e);
    }

    private void Refresh(object? sender, PropertyChangedEventArgs e) => RenderState();

    private void RenderState()
    {
        Dispatcher.VerifyAccess();
        var fresh = _viewModel.Freshness == SnapshotFreshness.Fresh && _viewModel.State.IsFresh;
        var s = fresh ? _viewModel.CurrentSnapshot : null;
        FreshnessLabel.Text = fresh ? (s!.Ready ? "状态新鲜 · Ready" : "状态新鲜 · 生产禁用") : "状态未知 / 已陈旧";
        SectionLabel.Text = _viewModel.SelectedSection switch
        {
            "Alarms" => "报警 · 当前快照摘要", "Recipes" => "配方 · 当前快照摘要",
            "Trace" => "追溯 · 当前快照摘要", "Maintenance" => "维护 · 当前快照摘要",
            "Engineering" => "手动 / 预览 / 标定 · 当前快照摘要", "Qualification" => "资格 · 当前快照摘要",
            _ => "生产 · 当前完整状态"
        };
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
            $"最近命令受理记录：{outcome.Disposition} / {outcome.ReasonCode} · {outcome.CorrelationId}";
        var progress = s?.LastCommand;
        ProgressLabel.Text = progress is null ? "最终状态：等待新的关联快照" :
            $"关联操作状态：{progress.State} / {progress.ReasonCode} · {progress.CorrelationId}";
    }
}
