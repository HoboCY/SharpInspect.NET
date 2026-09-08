using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace SharpInspect.Wpf;

public partial class CameraSetupPanel : UserControl
{
    private readonly CameraSetupViewModel _fallbackViewModel;
    private CameraSetupViewModel? _observedViewModel;

    public CameraSetupPanel() : this(null) { }

    public CameraSetupPanel(CameraSetupViewModel? viewModel)
    {
        InitializeComponent();
        _fallbackViewModel = viewModel ?? new CameraSetupViewModel(
            null, null, null, new DispatcherUiDispatcher());
        DataContext = viewModel ?? _fallbackViewModel;
        DataContextChanged += PanelDataContextChanged;
        AttachViewModel(DataContext as CameraSetupViewModel);
        ApplyState();
    }

    public CameraSetupViewModel ViewModel =>
        DataContext as CameraSetupViewModel ?? _fallbackViewModel;

    /// <summary>
    /// Clears the password box and the transient candidate/binding projection
    /// when the maintenance page is hidden or the window is closing.
    /// </summary>
    public void ClearSensitiveInputs()
    {
        StepUpPasswordBox.Clear();
        ViewModel.ClearSensitiveInputs();
        SetupScrollViewer.ScrollToHome();
        ApplyState();
    }

    private void PanelDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.OldValue is CameraSetupViewModel oldViewModel)
            oldViewModel.PropertyChanged -= ViewModelChanged;
        AttachViewModel(args.NewValue as CameraSetupViewModel);
        // A panel can be reused for another session/view model.  Clear the
        // native password control before exposing the new context.
        ClearSensitiveInputs();
        ApplyState();
    }

    private void AttachViewModel(CameraSetupViewModel? viewModel)
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

    private async void DiscoverClick(object sender, RoutedEventArgs args)
    {
        try { await ViewModel.DiscoverAsync(); }
        finally { ApplyState(); }
    }

    private async void RebindClick(object sender, RoutedEventArgs args)
    {
        var password = StepUpPasswordBox.Password;
        StepUpPasswordBox.Clear();
        try { await ViewModel.RebindAsync(password); }
        finally
        {
            StepUpPasswordBox.Clear();
            ApplyState();
        }
    }

    private async void ApplyClick(object sender, RoutedEventArgs args)
    {
        var password = StepUpPasswordBox.Password;
        StepUpPasswordBox.Clear();
        try { await ViewModel.ApplyAsync(password); }
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
            : viewModel.HasSetup
                ? "已读取"
                : viewModel.IsAuthenticated
                    ? "待读取"
                    : "未登录";
        StatusText.Text = viewModel.StatusMessage;
        ErrorText.Text = viewModel.ErrorCode ?? string.Empty;
        CandidateText.Text = viewModel.HasSelectedDevice
            ? $"已选择：{viewModel.SelectedDevice!.DisplayName} · {viewModel.SelectedDevice.StableDeviceIdentity}"
            : $"候选数量：{viewModel.CandidateCount} · 尚未选择设备";
        AuditText.Text = $"审计状态：{viewModel.AuditStatus} · 生产 Ready：不可用 · 配方激活：仍需后续完成";
        UnavailableText.Text = configured
            ? "当前会话或设置边界不可用；请先登录并重试。"
            : "相机设置不可用：未配置受限设置服务。";

        ProviderComboBox.IsEnabled = configured && !viewModel.IsBusy;
        DiscoverButton.IsEnabled = viewModel.CanDiscover;
        DeviceListBox.IsEnabled = configured && !viewModel.IsBusy &&
            viewModel.CandidateCount > 0;
        RefreshButton.IsEnabled = viewModel.CanRefresh;
        RebindButton.IsEnabled = viewModel.CanRebind;
        ApplyButton.IsEnabled = viewModel.CanApply;
        StepUpPasswordBox.IsEnabled = configured && !viewModel.IsBusy;
    }
}
