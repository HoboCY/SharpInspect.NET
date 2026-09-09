using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace SharpInspect.Wpf;

public partial class PreviewSessionPanel : UserControl
{
    private readonly PreviewSessionViewModel _fallbackViewModel;
    private PreviewSessionViewModel? _observedViewModel;

    public PreviewSessionPanel() : this(null)
    {
    }

    public PreviewSessionPanel(PreviewSessionViewModel? viewModel)
    {
        InitializeComponent();
        _fallbackViewModel = viewModel ?? new PreviewSessionViewModel(
            runtime: null, previewService: null, sessions: null,
            dispatcher: new DispatcherUiDispatcher());
        DataContext = viewModel ?? _fallbackViewModel;
        DataContextChanged += PanelDataContextChanged;
        Unloaded += PanelUnloaded;
        AttachViewModel(DataContext as PreviewSessionViewModel);
        ApplyState();
    }

    public PreviewSessionViewModel ViewModel =>
        DataContext as PreviewSessionViewModel ?? _fallbackViewModel;

    /// <summary>
    /// Stops the read-only watcher and clears the displayed image. It does not
    /// submit Preview Exit; Runtime owns the active session lifecycle.
    /// </summary>
    public void ClearSensitiveInputs()
    {
        ViewModel.ClearSensitiveInputs();
        PreviewScrollViewer.ScrollToHome();
        ApplyState();
    }

    public void Deactivate()
    {
        ViewModel.Deactivate();
        PreviewScrollViewer.ScrollToHome();
        ApplyState();
    }

    private void PanelDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.OldValue is PreviewSessionViewModel oldViewModel)
        {
            oldViewModel.PropertyChanged -= ViewModelChanged;
            oldViewModel.SessionInvalidated -= ViewModelSessionInvalidated;
            oldViewModel.Deactivate();
        }
        AttachViewModel(args.NewValue as PreviewSessionViewModel);
        ClearSensitiveInputs();
    }

    private void PanelUnloaded(object sender, RoutedEventArgs args) => Deactivate();

    private void AttachViewModel(PreviewSessionViewModel? viewModel)
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

    private void ViewModelSessionInvalidated(object? sender, EventArgs args) => ApplyState();

    private async void RefreshClick(object sender, RoutedEventArgs args)
    {
        try { await ViewModel.RefreshAsync(); }
        finally { ApplyState(); }
    }

    private async void StartClick(object sender, RoutedEventArgs args)
    {
        try { await ViewModel.StartPreviewSessionAsync(); }
        finally { ApplyState(); }
    }

    private async void TuneClick(object sender, RoutedEventArgs args)
    {
        try { await ViewModel.ApplyPreviewTuningAsync(); }
        finally { ApplyState(); }
    }

    private async void FreezeClick(object sender, RoutedEventArgs args)
    {
        try { await ViewModel.FreezePreviewSettingsAsync(); }
        finally { ApplyState(); }
    }

    private async void SaveClick(object sender, RoutedEventArgs args)
    {
        try { await ViewModel.SavePreviewToDraftAsync(); }
        finally { ApplyState(); }
    }

    private async void ExitClick(object sender, RoutedEventArgs args)
    {
        try { await ViewModel.ExitPreviewSessionAsync(ViewModel.ExitReason, cancel: false); }
        finally { ApplyState(); }
    }

    private void CancelClick(object sender, RoutedEventArgs args)
    {
        ViewModel.CancelPendingOperations();
        ApplyState();
    }

    private void ApplyState()
    {
        var viewModel = ViewModel;
        var configured = viewModel.IsConfigured;
        UnavailablePanel.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        ConfiguredPanel.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        UnavailableText.Text = configured
            ? "当前会话、工位快照或 Preview 边界不可用；请先登录并刷新。"
            : "Preview 不可用：Runtime、Preview 查询或当前用户会话未完整配置。";
        StatusLabel.Text = viewModel.IsBusy
            ? "处理中"
            : viewModel.IsSessionActive
                ? "Preview 会话中"
                : viewModel.IsAuthenticated ? "已登录 / 待显式进入" : "未登录";
        StatusText.Text = viewModel.StatusMessage;
        ErrorText.Text = viewModel.ErrorCode ?? string.Empty;

        var editable = configured && !viewModel.IsBusy;
        StartReasonBox.IsEnabled = editable;
        TuningReasonBox.IsEnabled = editable;
        FreezeReasonBox.IsEnabled = editable;
        SaveReasonBox.IsEnabled = editable;
        ExitReasonBox.IsEnabled = editable;
        RefreshButton.IsEnabled = viewModel.CanRefresh;
        StartButton.IsEnabled = viewModel.CanStart;
        TuneButton.IsEnabled = viewModel.CanApplyTuning;
        FreezeButton.IsEnabled = viewModel.CanFreeze;
        SaveButton.IsEnabled = viewModel.CanSave;
        ExitButton.IsEnabled = viewModel.CanExit;
        CancelButton.IsEnabled = viewModel.IsBusy;
    }
}
