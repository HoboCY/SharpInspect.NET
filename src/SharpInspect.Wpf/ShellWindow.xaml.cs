using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

internal sealed record StateRow(string Label, string Value);
internal sealed record AdmissionGateRow(string Gate, string Status, string ReasonCode,
    string ExpectedFingerprint, string ObservedFingerprint, string EvidenceRecordHash);

public partial class ShellWindow : Window
{
    internal Task SubmitIdentityLoginSmokeAsync(string userName, string password) => IdentityPanel.SubmitLoginSmokeAsync(userName, password);
    internal Task RefreshIdentityAdministrationSmokeAsync() => IdentityAdministrationPanel.RefreshSmokeAsync();
    internal Task<RuntimeCommandOutcome?> SubmitIdentityAdministrationCreateSmokeAsync(
        string userName, string displayName, string password, string stepUpPassword) =>
        IdentityAdministrationPanel.SubmitCreateSmokeAsync(userName, displayName, password, stepUpPassword);
    internal Task RefreshRecipeDraftSmokeAsync() => RecipeDraftEditorPanel.ViewModel.RefreshAsync();
    internal void BringRecipeDraftIntoViewForSmoke()
    {
        if (_viewModel.SelectedSection != "Recipes")
            _viewModel.NavigateCommand.Execute("Recipes");
        RecipeDraftEditorPanel.ScrollToEditorForSmoke();
        MainScrollViewer.UpdateLayout();
        RecipeDraftEditorPanel.BringIntoView();
        MainScrollViewer.UpdateLayout();
    }
    internal void VerifyRecipeDraftLayoutForSmoke()
    {
        if (_viewModel.SelectedSection != "Recipes" || RecipeDraftEditorPanel.Visibility != Visibility.Visible)
            throw new InvalidOperationException("RecipeDraftPanelNotReachable");
        RecipeDraftEditorPanel.UpdateLayout();
        if (RecipeDraftEditorPanel.ActualHeight <= 0)
            throw new InvalidOperationException("RecipeDraftPanelHasNoLayout");
    }
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
    private readonly RecipeDraftEditorViewModel? _recipeDraftViewModel;
    private readonly PreviewSessionViewModel? _previewSessionViewModel;
    private readonly ManualInspectionSessionViewModel? _manualInspectionViewModel;
    private readonly StationQualificationSessionViewModel? _qualificationViewModel;
    private readonly TraceStoragePolicyViewModel? _traceStoragePolicyViewModel;
    private ProductionRecoveryViewModel? _productionRecoveryViewModel;
    private bool _algorithmResultSelectionLoaded;
    private bool _allowSmokeShutdown;
    private bool _traceSelectionLoaded;
    private bool _integritySelectionLoaded;
    private bool _identitySelectionLoaded;
    private bool _identityAdministrationSelectionLoaded;
    private bool _administratorRecoverySelectionLoaded;
    private bool _alarmSelectionLoaded;
    private bool _recipeDraftSelectionLoaded;
    private bool _previewSelectionLoaded;
    private bool _manualSelectionLoaded;
    private bool _qualificationSelectionLoaded;
    private bool _traceStoragePolicySelectionLoaded;
    private bool _productionRecoverySelectionLoaded;
    private long _lastInputReport;
    private Task _sessionLock = Task.CompletedTask;
    public bool IsPrivacyLocked { get; private set; }

    /// <summary>
    /// Adds the production recovery maintenance surface once the host has built
    /// its real Runtime/query composition. This keeps the legacy constructor and
    /// existing sample composition ABI unchanged.
    /// </summary>
    public void AttachProductionRecovery(ProductionRecoveryViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (_productionRecoveryViewModel is not null &&
            !ReferenceEquals(_productionRecoveryViewModel, viewModel))
            throw new InvalidOperationException("ProductionRecoveryAlreadyAttached");
        if (ReferenceEquals(_productionRecoveryViewModel, viewModel)) return;
        _productionRecoveryViewModel = viewModel;
        ProductionRecoveryPanel.DataContext = viewModel;
        viewModel.PropertyChanged += ProductionRecoveryChanged;
        RenderState();
    }

    /// <summary>
    /// Keeps the original public constructor signature for source and binary
    /// compatibility. Preview is attached through the explicit factory below.
    /// </summary>
    public ShellWindow(StationShellViewModel viewModel, CommandTraceViewModel? traceViewModel = null,
        AuditIntegrityViewModel? integrityViewModel = null, IdentityViewModel? identityViewModel = null,
        IdentityAdministrationViewModel? identityAdministrationViewModel = null,
        AdministratorRecoveryViewModel? administratorRecoveryViewModel = null,
        AlarmViewModel? alarmViewModel = null, AlgorithmResultHistoryViewModel? algorithmResultViewModel = null,
        RecipeDraftEditorViewModel? recipeDraftViewModel = null)
        : this(viewModel, traceViewModel, integrityViewModel, identityViewModel,
            identityAdministrationViewModel, administratorRecoveryViewModel, alarmViewModel,
            algorithmResultViewModel, recipeDraftViewModel, previewSessionViewModel: null)
    {
    }

    /// <summary>
    /// Explicit Preview-enabled construction. Keeping this as a named factory
    /// avoids changing or ambiguating the legacy optional-argument constructor.
    /// </summary>
    public static ShellWindow CreateWithPreviewSession(
        StationShellViewModel viewModel,
        PreviewSessionViewModel previewSessionViewModel,
        CommandTraceViewModel? traceViewModel = null,
        AuditIntegrityViewModel? integrityViewModel = null,
        IdentityViewModel? identityViewModel = null,
        IdentityAdministrationViewModel? identityAdministrationViewModel = null,
        AdministratorRecoveryViewModel? administratorRecoveryViewModel = null,
        AlarmViewModel? alarmViewModel = null,
        AlgorithmResultHistoryViewModel? algorithmResultViewModel = null,
        RecipeDraftEditorViewModel? recipeDraftViewModel = null) =>
        new ShellWindow(viewModel, traceViewModel, integrityViewModel, identityViewModel,
            identityAdministrationViewModel, administratorRecoveryViewModel, alarmViewModel,
            algorithmResultViewModel, recipeDraftViewModel, previewSessionViewModel);

    /// <summary>Attaches the explicit Manual inspection command surface.</summary>
    public static ShellWindow CreateWithManualInspection(
        StationShellViewModel viewModel,
        ManualInspectionSessionViewModel manualInspectionViewModel,
        PreviewSessionViewModel? previewSessionViewModel = null,
        CommandTraceViewModel? traceViewModel = null,
        AuditIntegrityViewModel? integrityViewModel = null,
        IdentityViewModel? identityViewModel = null,
        IdentityAdministrationViewModel? identityAdministrationViewModel = null,
        AdministratorRecoveryViewModel? administratorRecoveryViewModel = null,
        AlarmViewModel? alarmViewModel = null,
        AlgorithmResultHistoryViewModel? algorithmResultViewModel = null,
        RecipeDraftEditorViewModel? recipeDraftViewModel = null) =>
        new ShellWindow(viewModel, traceViewModel, integrityViewModel, identityViewModel,
            identityAdministrationViewModel, administratorRecoveryViewModel, alarmViewModel,
            algorithmResultViewModel, recipeDraftViewModel, previewSessionViewModel,
            manualInspectionViewModel);

    /// <summary>Attaches the explicit isolated qualification command surface.</summary>
    public static ShellWindow CreateWithStationQualification(StationShellViewModel viewModel,
        StationQualificationSessionViewModel qualificationViewModel,
        ManualInspectionSessionViewModel? manualInspectionViewModel = null,
        PreviewSessionViewModel? previewSessionViewModel = null,
        CommandTraceViewModel? traceViewModel = null, AuditIntegrityViewModel? integrityViewModel = null,
        IdentityViewModel? identityViewModel = null, IdentityAdministrationViewModel? identityAdministrationViewModel = null,
        AdministratorRecoveryViewModel? administratorRecoveryViewModel = null, AlarmViewModel? alarmViewModel = null,
        AlgorithmResultHistoryViewModel? algorithmResultViewModel = null, RecipeDraftEditorViewModel? recipeDraftViewModel = null) =>
        new(viewModel, traceViewModel, integrityViewModel, identityViewModel, identityAdministrationViewModel,
            administratorRecoveryViewModel, alarmViewModel, algorithmResultViewModel, recipeDraftViewModel,
            previewSessionViewModel, manualInspectionViewModel, qualificationViewModel);

    /// <summary>Attaches the governed deployment storage-policy editor without changing existing constructor ABI.</summary>
    public static ShellWindow CreateWithTraceStoragePolicy(StationShellViewModel viewModel,
        TraceStoragePolicyViewModel traceStoragePolicyViewModel,
        CommandTraceViewModel? traceViewModel = null, AuditIntegrityViewModel? integrityViewModel = null,
        IdentityViewModel? identityViewModel = null, IdentityAdministrationViewModel? identityAdministrationViewModel = null,
        AdministratorRecoveryViewModel? administratorRecoveryViewModel = null, AlarmViewModel? alarmViewModel = null,
        AlgorithmResultHistoryViewModel? algorithmResultViewModel = null, RecipeDraftEditorViewModel? recipeDraftViewModel = null,
        PreviewSessionViewModel? previewSessionViewModel = null, ManualInspectionSessionViewModel? manualInspectionViewModel = null,
        StationQualificationSessionViewModel? qualificationViewModel = null) =>
        new(viewModel, traceViewModel, integrityViewModel, identityViewModel, identityAdministrationViewModel,
            administratorRecoveryViewModel, alarmViewModel, algorithmResultViewModel, recipeDraftViewModel,
            previewSessionViewModel, manualInspectionViewModel, qualificationViewModel, traceStoragePolicyViewModel);

    private ShellWindow(StationShellViewModel viewModel, CommandTraceViewModel? traceViewModel,
        AuditIntegrityViewModel? integrityViewModel, IdentityViewModel? identityViewModel,
        IdentityAdministrationViewModel? identityAdministrationViewModel,
        AdministratorRecoveryViewModel? administratorRecoveryViewModel,
        AlarmViewModel? alarmViewModel, AlgorithmResultHistoryViewModel? algorithmResultViewModel,
        RecipeDraftEditorViewModel? recipeDraftViewModel,
        PreviewSessionViewModel? previewSessionViewModel,
        ManualInspectionSessionViewModel? manualInspectionViewModel = null,
        StationQualificationSessionViewModel? qualificationViewModel = null,
        TraceStoragePolicyViewModel? traceStoragePolicyViewModel = null)
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
        _recipeDraftViewModel = recipeDraftViewModel;
        _previewSessionViewModel = previewSessionViewModel;
        _manualInspectionViewModel = manualInspectionViewModel;
        _qualificationViewModel = qualificationViewModel;
        _traceStoragePolicyViewModel = traceStoragePolicyViewModel;
        DataContext = viewModel;
        TracePanel.DataContext = traceViewModel;
        IntegrityPanel.DataContext = integrityViewModel;
        IdentityPanel.DataContext = identityViewModel;
        IdentityAdministrationPanel.DataContext = identityAdministrationViewModel;
        AdministratorRecoveryPanel.DataContext = administratorRecoveryViewModel;
        PrivacyAdministratorRecoveryPanel.DataContext = administratorRecoveryViewModel;
        AlarmPanel.DataContext = alarmViewModel;
        AlgorithmResultsPanel.DataContext = algorithmResultViewModel;
        RecipeDraftEditorPanel.DataContext = recipeDraftViewModel;
        PreviewPanel.DataContext = previewSessionViewModel;
        ManualInspectionPanel.DataContext = manualInspectionViewModel;
        QualificationPanel.DataContext = qualificationViewModel;
        TraceStoragePolicyPanel.DataContext = traceStoragePolicyViewModel;
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
        if (recipeDraftViewModel is not null) recipeDraftViewModel.PropertyChanged += RecipeDraftChanged;
        if (previewSessionViewModel is not null) previewSessionViewModel.PropertyChanged += PreviewChanged;
        if (manualInspectionViewModel is not null) manualInspectionViewModel.PropertyChanged += ManualInspectionChanged;
        if (qualificationViewModel is not null) qualificationViewModel.PropertyChanged += QualificationChanged;
        if (traceStoragePolicyViewModel is not null) traceStoragePolicyViewModel.PropertyChanged += TraceStoragePolicyChanged;
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
        RenderState();
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
        RecipeDraftEditorPanel.ClearSensitiveInputs();
        PreviewPanel.ClearSensitiveInputs();
        ManualInspectionPanel.Deactivate();
        QualificationPanel.Deactivate();
        TraceStoragePolicyPanel.Deactivate();
        ProductionRecoveryPanel.Deactivate();
        _productionRecoverySelectionLoaded = false;
        _previewSelectionLoaded = false;
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
        if (_recipeDraftViewModel is not null) _recipeDraftViewModel.PropertyChanged -= RecipeDraftChanged;
        if (_previewSessionViewModel is not null) _previewSessionViewModel.PropertyChanged -= PreviewChanged;
        if (_manualInspectionViewModel is not null) _manualInspectionViewModel.PropertyChanged -= ManualInspectionChanged;
        if (_qualificationViewModel is not null) _qualificationViewModel.PropertyChanged -= QualificationChanged;
        if (_traceStoragePolicyViewModel is not null) _traceStoragePolicyViewModel.PropertyChanged -= TraceStoragePolicyChanged;
        if (_productionRecoveryViewModel is not null)
        {
            _productionRecoveryViewModel.PropertyChanged -= ProductionRecoveryChanged;
            _productionRecoveryViewModel.Deactivate();
        }
        SystemEvents.SessionSwitch -= OperatingSystemSessionSwitch;
        IdentityPanel.ClearSensitiveInputs();
        IdentityAdministrationPanel.ClearSensitiveInputs();
        AdministratorRecoveryPanel.ClearSensitiveInputs();
        PrivacyAdministratorRecoveryPanel.ClearSensitiveInputs();
        AlarmPanel.ClearSensitiveInputs();
        RecipeDraftEditorPanel.ClearSensitiveInputs();
        PreviewPanel.ClearSensitiveInputs();
        ManualInspectionPanel.Deactivate();
        QualificationPanel.Deactivate();
        TraceStoragePolicyPanel.Deactivate();
        ProductionRecoveryPanel.Deactivate();
        base.OnClosed(e);
    }

    private void Refresh(object? sender, PropertyChangedEventArgs e) => RenderState();
    private void TraceChanged(object? sender, PropertyChangedEventArgs e) => RenderState();
    private void IntegrityChanged(object? sender, PropertyChangedEventArgs e) => RenderState();
    private void IdentityChanged(object? sender, PropertyChangedEventArgs e) => RenderState();
    private void IdentityAdministrationChanged(object? sender, PropertyChangedEventArgs e) => RenderState();
    private void AdministratorRecoveryChanged(object? sender, PropertyChangedEventArgs e) => RenderState();
    private void AlarmChanged(object? sender, PropertyChangedEventArgs e) => RenderState();
    private void RecipeDraftChanged(object? sender, PropertyChangedEventArgs e) => RenderState();
    private void PreviewChanged(object? sender, PropertyChangedEventArgs e) => RenderState();
    private void ManualInspectionChanged(object? sender, PropertyChangedEventArgs e) => RenderState();
    private void QualificationChanged(object? sender, PropertyChangedEventArgs e) => RenderState();
    private void TraceStoragePolicyChanged(object? sender, PropertyChangedEventArgs e) => RenderState();
    private void ProductionRecoveryChanged(object? sender, PropertyChangedEventArgs e) => RenderState();

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
        var recipeSelected = _viewModel.SelectedSection == "Recipes";
        var engineeringSelected = _viewModel.SelectedSection == "Engineering";
        if (maintenanceSelected) SectionLabel.Text = "维护 / 管理 · 身份引导、账号授权与恢复";
        if (recipeSelected) SectionLabel.Text = "配方 · 受限草稿编辑";
        var snapshotVisible = traceSelected || maintenanceSelected || alarmSelected || recipeSelected
            ? Visibility.Collapsed : Visibility.Visible;
        SnapshotPanel.Visibility = snapshotVisible;
        BlockersPanel.Visibility = snapshotVisible;
        AdmissionPanel.Visibility = snapshotVisible;
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
        RecipeDraftEditorPanel.Visibility = recipeSelected ? Visibility.Visible : Visibility.Collapsed;
        PreviewPanel.Visibility = engineeringSelected && _previewSessionViewModel is not null
            ? Visibility.Visible : Visibility.Collapsed;
        ManualInspectionPanel.Visibility = engineeringSelected && _manualInspectionViewModel is not null
            ? Visibility.Visible : Visibility.Collapsed;
        QualificationPanel.Visibility = engineeringSelected && _qualificationViewModel is not null
            ? Visibility.Visible : Visibility.Collapsed;
        if (!engineeringSelected || IsPrivacyLocked)
        {
            if (_qualificationSelectionLoaded)
            {
                _qualificationSelectionLoaded = false;
                QualificationPanel.Deactivate();
            }
        }
        else if (_qualificationViewModel is not null && !_qualificationSelectionLoaded)
        {
            _qualificationSelectionLoaded = true;
            _ = _qualificationViewModel.RefreshAsync();
        }
        if (!engineeringSelected || IsPrivacyLocked)
        {
            if (_manualSelectionLoaded)
            {
                _manualSelectionLoaded = false;
                ManualInspectionPanel.Deactivate();
            }
        }
        else if (_manualInspectionViewModel is not null && !_manualSelectionLoaded)
        {
            _manualSelectionLoaded = true;
            _ = _manualInspectionViewModel.RefreshAsync();
        }
        if (!recipeSelected)
        {
            if (_recipeDraftSelectionLoaded)
            {
                _recipeDraftSelectionLoaded = false;
                RecipeDraftEditorPanel.ClearSensitiveInputs();
            }
        }
        else if (_recipeDraftViewModel is not null && !_recipeDraftSelectionLoaded)
        {
            _recipeDraftSelectionLoaded = true;
            _ = _recipeDraftViewModel.RefreshAsync();
        }
        if (!engineeringSelected)
        {
            if (_previewSelectionLoaded)
            {
                _previewSelectionLoaded = false;
                PreviewPanel.ClearSensitiveInputs();
            }
        }
        else if (!IsPrivacyLocked && _previewSessionViewModel is not null && !_previewSelectionLoaded)
        {
            _previewSelectionLoaded = true;
            _ = _previewSessionViewModel.StartWatchingAsync();
        }
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
        TraceStoragePolicyPanel.Visibility = maintenanceSelected && _traceStoragePolicyViewModel is not null
            ? Visibility.Visible : Visibility.Collapsed;
        if (!maintenanceSelected || IsPrivacyLocked)
        {
            if (_traceStoragePolicySelectionLoaded)
            {
                _traceStoragePolicySelectionLoaded = false;
                TraceStoragePolicyPanel.Deactivate();
            }
        }
        else if (_traceStoragePolicyViewModel is not null && !_traceStoragePolicySelectionLoaded)
        {
            _traceStoragePolicySelectionLoaded = true;
            _ = _traceStoragePolicyViewModel.RefreshAsync();
        }
        ProductionRecoveryPanel.Visibility = maintenanceSelected && _productionRecoveryViewModel is not null
            ? Visibility.Visible : Visibility.Collapsed;
        if (!maintenanceSelected || IsPrivacyLocked)
        {
            if (_productionRecoverySelectionLoaded)
            {
                _productionRecoverySelectionLoaded = false;
                ProductionRecoveryPanel.Deactivate();
            }
        }
        else if (_productionRecoveryViewModel is not null && !_productionRecoverySelectionLoaded)
        {
            _productionRecoverySelectionLoaded = true;
            _ = _productionRecoveryViewModel.RefreshAsync();
        }
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
        }.Concat(s?.CameraRecovery is { } recovery ? new[]
        {
            new StateRow("相机恢复 / 尝试次数", $"{recovery.State} / {recovery.AttemptCount} / {recovery.MaximumAttempts}"),
            new StateRow("相机恢复原因", recovery.ReasonCode)
        } : Array.Empty<StateRow>());
        BlockersLabel.Text = s is null ? "当前状态未知，等待新的完整快照。" : string.Join(" · ", s.AdmissionBlockers);
        var admission = _viewModel.State.DisplayedProductionAdmission;
        AdmissionSummaryLabel.Text = admission is null
            ? "等待当前完整准入报告。"
            : (admission.CanArm ? "当前准入门已满足，武装仍需当前权限确认。" :
                $"当前有 {admission.Gates.Count(gate => gate.Status is not (ProductionAdmissionGateStatus.Passed or ProductionAdmissionGateStatus.NotApplicable))} 项准入阻塞，生产保持禁用。") +
                $"\n准入代次 {admission.AdmissionGeneration} · 报告哈希 {admission.ContentHash}";
        AdmissionRows.ItemsSource = admission is null
            ? Array.Empty<AdmissionGateRow>()
            : admission.Gates.Select(gate => new AdmissionGateRow(
                AdmissionGateLabel(gate.Gate), AdmissionStatusLabel(gate.Status), gate.ReasonCode,
                gate.ExpectedFingerprint ?? "无", gate.ObservedFingerprint ?? "无",
                gate.EvidenceRecordHashes.Count == 0 ? "无" : string.Join(", ", gate.EvidenceRecordHashes))).ToArray();
        var outcome = _viewModel.LastCommandOutcome;
        OutcomeLabel.Text = _viewModel.CommandFailureCode is not null
            ? $"本次命令状态不可确认：{_viewModel.CommandFailureCode}，等待 Runtime 确认。"
            : outcome is null ? "命令：尚未提交" :
            $"最近命令结果：{outcome.Disposition} / 审计={outcome.Audit} / {outcome.ReasonCode} · {outcome.CorrelationId}";
        var progress = s?.LastCommand;
        ProgressLabel.Text = progress is null ? "最终状态：等待新的关联快照" :
            $"关联操作状态：{progress.State} / {progress.ReasonCode} · {progress.CorrelationId}";
    }

    private static string AdmissionGateLabel(ProductionAdmissionGate gate) => gate switch
    {
        ProductionAdmissionGate.DeploymentPolicies => "部署政策",
        ProductionAdmissionGate.VersionPolicy => "版本政策",
        ProductionAdmissionGate.ActiveRecipe => "当前活动配方",
        ProductionAdmissionGate.PreparedAlgorithm => "算法准备",
        ProductionAdmissionGate.RecipeAssets => "配方资产",
        ProductionAdmissionGate.CameraBinding => "相机绑定",
        ProductionAdmissionGate.CameraConfiguration => "相机配置",
        ProductionAdmissionGate.CameraHealth => "相机健康",
        ProductionAdmissionGate.PlcCommunication => "PLC 通信",
        ProductionAdmissionGate.ControllerSynchronization => "控制器同步",
        ProductionAdmissionGate.Recovery => "恢复状态",
        ProductionAdmissionGate.ExclusiveWork => "独占工作",
        ProductionAdmissionGate.Alarms => "报警",
        ProductionAdmissionGate.StoreIntegrity => "存储完整性",
        ProductionAdmissionGate.StoreCapacity => "存储容量",
        ProductionAdmissionGate.EvidenceReconciliation => "证据对账",
        ProductionAdmissionGate.Backlog => "积压",
        ProductionAdmissionGate.IdentityRecovery => "身份恢复方式",
        ProductionAdmissionGate.FrameworkQualification => "框架资格",
        ProductionAdmissionGate.ProviderQualification => "相机适配器资格",
        ProductionAdmissionGate.PerformanceQualification => "性能资格",
        ProductionAdmissionGate.StationAcceptance => "工位验收",
        ProductionAdmissionGate.PowerLossQualification => "断电验收",
        ProductionAdmissionGate.ProductionCycle => "生产周期",
        _ => "未知准入门"
    };

    private static string AdmissionStatusLabel(ProductionAdmissionGateStatus status) => status switch
    {
        ProductionAdmissionGateStatus.Passed => "通过",
        ProductionAdmissionGateStatus.Missing => "缺少证据",
        ProductionAdmissionGateStatus.NotConfigured => "尚未配置",
        ProductionAdmissionGateStatus.Mismatch => "不匹配",
        ProductionAdmissionGateStatus.Expired => "已过期或未生效",
        ProductionAdmissionGateStatus.Failed => "失败",
        ProductionAdmissionGateStatus.Blocked => "阻塞",
        ProductionAdmissionGateStatus.NotApplicable => "已证明不适用",
        _ => "未知"
    };
}
