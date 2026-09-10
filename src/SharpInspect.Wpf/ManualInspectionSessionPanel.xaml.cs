using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace SharpInspect.Wpf;

public partial class ManualInspectionSessionPanel : UserControl
{
    private readonly ManualInspectionSessionViewModel _fallbackViewModel;
    private ManualInspectionSessionViewModel? _observedViewModel;

    public ManualInspectionSessionPanel() : this(null)
    {
    }

    public ManualInspectionSessionPanel(ManualInspectionSessionViewModel? viewModel)
    {
        InitializeComponent();
        _fallbackViewModel = viewModel ?? new ManualInspectionSessionViewModel();
        DataContext = viewModel ?? _fallbackViewModel;
        DataContextChanged += PanelDataContextChanged;
        Unloaded += PanelUnloaded;
        AttachViewModel(DataContext as ManualInspectionSessionViewModel);
        ApplyState();
    }

    public ManualInspectionSessionViewModel ViewModel =>
        DataContext as ManualInspectionSessionViewModel ?? _fallbackViewModel;

    /// <summary>
    /// Clears transient operator input and stops presentation work. It does not
    /// submit Exit; session retirement remains an explicit Runtime command.
    /// </summary>
    public void ClearSensitiveInputs()
    {
        ViewModel.ClearSensitiveInputs();
        StepUpPasswordBox.Clear();
        ManualScrollViewer.ScrollToHome();
        ApplyState();
    }

    public void Deactivate()
    {
        ViewModel.CancelPendingOperations();
        ClearSensitiveInputs();
    }

    private void PanelDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.OldValue is ManualInspectionSessionViewModel oldViewModel)
        {
            oldViewModel.PropertyChanged -= ViewModelChanged;
            oldViewModel.SessionInvalidated -= ViewModelSessionInvalidated;
        }
        AttachViewModel(args.NewValue as ManualInspectionSessionViewModel);
        ClearSensitiveInputs();
        ApplyState();
    }

    private void PanelUnloaded(object sender, RoutedEventArgs args) => Deactivate();

    private void AttachViewModel(ManualInspectionSessionViewModel? viewModel)
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

    private void ViewModelSessionInvalidated(object? sender, EventArgs args) => ClearSensitiveInputs();

    private async void RefreshClick(object sender, RoutedEventArgs args)
    {
        try { await ViewModel.RefreshAsync(); }
        finally { ApplyState(); }
    }

    private async void StartClick(object sender, RoutedEventArgs args)
    {
        var password = TakeStepUpPassword();
        try { await ViewModel.StartManualInspectionSessionAsync(password); }
        finally { ApplyState(); }
    }

    private async void RunClick(object sender, RoutedEventArgs args)
    {
        var password = TakeStepUpPassword();
        try { await ViewModel.RunOneAsync(password); }
        finally { ApplyState(); }
    }

    private async void GracefulExitClick(object sender, RoutedEventArgs args)
    {
        var password = TakeStepUpPassword();
        try { await ViewModel.GracefulExitAsync(password); }
        finally { ApplyState(); }
    }

    private async void AbortClick(object sender, RoutedEventArgs args)
    {
        var password = TakeStepUpPassword();
        try { await ViewModel.AbortAsync(password); }
        finally { ApplyState(); }
    }

    private void CancelClick(object sender, RoutedEventArgs args)
    {
        ViewModel.CancelPendingOperations();
        ApplyState();
    }

    private void UseActiveClick(object sender, RoutedEventArgs args)
    {
        ViewModel.UseCurrentActiveAsExpected();
        ApplyState();
    }

    private void ClearActiveClick(object sender, RoutedEventArgs args)
    {
        ViewModel.ClearExpectedActive();
        ApplyState();
    }

    private void ApplyState()
    {
        var viewModel = ViewModel;
        var configured = viewModel.IsConfigured;
        UnavailablePanel.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        ConfiguredPanel.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        UnavailableText.Text = configured
            ? "当前人工检测查询或 Runtime 状态不可用；请先登录并刷新。"
            : "人工检测不可用：Runtime、人工会话查询或当前用户会话未完整配置。";
        StatusLabel.Text = viewModel.IsBusy
            ? "处理中"
            : viewModel.IsSessionActive
                ? "会话中"
                : viewModel.IsAuthenticated ? "已登录 / 待显式开始" : "未登录";
        StatusText.Text = viewModel.StatusMessage;
        ErrorText.Text = viewModel.ErrorCode ?? string.Empty;

        var editable = configured && !viewModel.IsBusy;
        DraftComboBox.IsEnabled = editable;
        ReleasedComboBox.IsEnabled = editable;
        StartReasonBox.IsEnabled = editable;
        RunReasonBox.IsEnabled = editable;
        PartIdentityBox.IsEnabled = editable;
        ExitReasonBox.IsEnabled = editable;
        StepUpPasswordBox.IsEnabled = editable;
        RefreshButton.IsEnabled = viewModel.CanRefresh;
        StartButton.IsEnabled = viewModel.CanStart;
        RunButton.IsEnabled = viewModel.CanRunOne;
        GracefulExitButton.IsEnabled = viewModel.CanExit;
        AbortButton.IsEnabled = viewModel.CanExit;
        CancelButton.IsEnabled = viewModel.IsBusy;
        UseActiveButton.IsEnabled = editable && viewModel.CurrentActive is not null;
        ClearActiveButton.IsEnabled = editable && viewModel.ExpectedActive is not null;
        RefreshHistoryButton.IsEnabled = viewModel.CanRefreshHistory;
        NextHistoryPageButton.IsEnabled = viewModel.CanNextHistoryPage;
        HistoryGrid.IsEnabled = !viewModel.IsBusy;
    }

    private string TakeStepUpPassword()
    {
        var password = StepUpPasswordBox.Password;
        StepUpPasswordBox.Clear();
        return password;
    }
}
