using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace SharpInspect.Wpf;

public partial class ImagingSetupPanel : UserControl
{
    private readonly ImagingSetupViewModel _fallbackViewModel;
    private ImagingSetupViewModel? _observedViewModel;

    public ImagingSetupPanel() : this(null) { }

    public ImagingSetupPanel(ImagingSetupViewModel? viewModel)
    {
        InitializeComponent();
        _fallbackViewModel = viewModel ?? new ImagingSetupViewModel(
            null, null, null, null, new DispatcherUiDispatcher());
        DataContext = viewModel ?? _fallbackViewModel;
        DataContextChanged += PanelDataContextChanged;
        AttachViewModel(DataContext as ImagingSetupViewModel);
        ApplyState();
    }

    public ImagingSetupViewModel ViewModel =>
        DataContext as ImagingSetupViewModel ?? _fallbackViewModel;

    /// <summary>Clears the native password box and transient read-back state.</summary>
    public void ClearSensitiveInputs()
    {
        StepUpPasswordBox.Clear();
        ViewModel.ClearSensitiveInputs();
        ImagingScrollViewer.ScrollToHome();
        ApplyState();
    }

    private void PanelDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.OldValue is ImagingSetupViewModel oldViewModel)
            oldViewModel.PropertyChanged -= ViewModelChanged;
        AttachViewModel(args.NewValue as ImagingSetupViewModel);
        ClearSensitiveInputs();
        ApplyState();
    }

    private void AttachViewModel(ImagingSetupViewModel? viewModel)
    {
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged -= ViewModelChanged;
            _observedViewModel.SessionInvalidated -= ViewModelSessionInvalidated;
        }

        _observedViewModel = viewModel;
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged += ViewModelChanged;
            _observedViewModel.SessionInvalidated += ViewModelSessionInvalidated;
        }
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args) => ApplyState();

    private void ViewModelSessionInvalidated(object? sender, EventArgs args)
    {
        StepUpPasswordBox.Clear();
        ApplyState();
    }

    private async void RefreshClick(object sender, RoutedEventArgs args)
    {
        try { await ViewModel.RefreshAsync(); }
        finally { ApplyState(); }
    }

    private async void RefreshHistoryClick(object sender, RoutedEventArgs args)
    {
        try { await ViewModel.RefreshHistoryAsync(); }
        finally { ApplyState(); }
    }

    private async void NextHistoryClick(object sender, RoutedEventArgs args)
    {
        try { await ViewModel.NextHistoryAsync(); }
        finally { ApplyState(); }
    }

    private async void DeclareClick(object sender, RoutedEventArgs args)
    {
        var password = StepUpPasswordBox.Password;
        StepUpPasswordBox.Clear();
        try { await ViewModel.DeclareAsync(password); }
        finally
        {
            StepUpPasswordBox.Clear();
            ApplyState();
        }
    }

    private void ApplyState()
    {
        var viewModel = ViewModel;
        var configured = viewModel.IsConfigured;
        UnavailablePanel.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        ConfiguredPanel.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        StatusLabel.Text = viewModel.IsBusy
            ? "处理中"
            : viewModel.HasCurrentRevision
                ? "已读取"
                : viewModel.HasCurrentBinding
                    ? "已读取绑定 / 待读记"
                    : viewModel.IsAuthenticated
                        ? "待读取"
                        : "未登录";
        StatusText.Text = viewModel.StatusMessage;
        ErrorText.Text = viewModel.ErrorCode ?? string.Empty;
        HistoryStatusText.Text = viewModel.HistoryStatusMessage;
        HistoryErrorText.Text = viewModel.HistoryErrorCode ?? string.Empty;
        HistoryPageText.Text = viewModel.HistoryPage.ToString();
        ChangeResultText.Text = viewModel.LastChangeResult is { } result
            ? $"本次登记：{(result.Succeeded ? "成功" : "未完成")} · 原因码={result.ReasonCode} · 审计={result.AuditPersistence}"
            : string.Empty;
        UnavailableText.Text = configured
            ? "当前会话或成像设置边界不可用；请先登录并刷新。"
            : "成像设置不可用：未配置成像修订和相机绑定服务。";

        var editable = configured && !viewModel.IsBusy;
        LogicalRoleBox.IsEnabled = editable;
        LensIdentityBox.IsEnabled = editable;
        FocusStateBox.IsEnabled = editable;
        MountingPoseBox.IsEnabled = editable;
        WorkingDistanceBox.IsEnabled = editable;
        SensorOrientationBox.IsEnabled = editable;
        ChangeReasonBox.IsEnabled = editable;
        RefreshButton.IsEnabled = viewModel.CanRefresh;
        RefreshHistoryButton.IsEnabled = viewModel.CanRefreshHistory;
        NextHistoryButton.IsEnabled = viewModel.CanNextHistory;
        DeclareButton.IsEnabled = viewModel.CanDeclare;
        StepUpPasswordBox.IsEnabled = configured && !viewModel.IsBusy;
    }
}
