using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace SharpInspect.Wpf;

public partial class CalibrationSessionPanel : UserControl
{
    private readonly CalibrationSessionViewModel _fallbackViewModel;
    private CalibrationSessionViewModel? _observedViewModel;

    public CalibrationSessionPanel() : this(null)
    {
    }

    public CalibrationSessionPanel(CalibrationSessionViewModel? viewModel)
    {
        InitializeComponent();
        _fallbackViewModel = viewModel ?? new CalibrationSessionViewModel(
            null, null, null, null, null, null, new DispatcherUiDispatcher());
        DataContext = viewModel ?? _fallbackViewModel;
        DataContextChanged += PanelDataContextChanged;
        AttachViewModel(DataContext as CalibrationSessionViewModel);
        ApplyState();
    }

    public CalibrationSessionViewModel ViewModel =>
        DataContext as CalibrationSessionViewModel ?? _fallbackViewModel;

    /// <summary>
    /// Clears the native PasswordBox and the page's sensitive read-back. It
    /// does not submit Exit; Runtime owns the active session lifecycle.
    /// </summary>
    public void ClearSensitiveInputs()
    {
        StartPasswordBox.Clear();
        ViewModel.ClearSensitiveInputs();
        CalibrationScrollViewer.ScrollToHome();
        ApplyState();
    }

    public void Deactivate()
    {
        StartPasswordBox.Clear();
        ViewModel.Deactivate();
        CalibrationScrollViewer.ScrollToHome();
        ApplyState();
    }

    private void PanelDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.OldValue is CalibrationSessionViewModel oldViewModel)
            oldViewModel.PropertyChanged -= ViewModelChanged;
        AttachViewModel(args.NewValue as CalibrationSessionViewModel);
        ClearSensitiveInputs();
        ApplyState();
    }

    private void AttachViewModel(CalibrationSessionViewModel? viewModel)
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
        StartPasswordBox.Clear();
        ApplyState();
    }

    private async void RefreshClick(object sender, RoutedEventArgs args)
    {
        try { await ViewModel.RefreshAsync(); }
        finally { ApplyState(); }
    }

    private async void StartClick(object sender, RoutedEventArgs args)
    {
        var password = StartPasswordBox.Password;
        StartPasswordBox.Clear();
        try { await ViewModel.StartCalibrationSessionAsync(password); }
        finally
        {
            StartPasswordBox.Clear();
            ApplyState();
        }
    }

    private async void CaptureClick(object sender, RoutedEventArgs args)
    {
        try { await ViewModel.CaptureCalibrationFrameAsync(); }
        finally { ApplyState(); }
    }

    private async void ExcludeClick(object sender, RoutedEventArgs args)
    {
        var selected = ViewModel.SelectedFrame;
        try
        {
            if (selected is not null)
                await ViewModel.ExcludeCalibrationFrameAsync(selected.FrameId, ViewModel.ExcludeReason);
        }
        finally { ApplyState(); }
    }

    private async void ComputeClick(object sender, RoutedEventArgs args)
    {
        try { await ViewModel.ComputeCalibrationCandidateAsync(); }
        finally { ApplyState(); }
    }

    private async void ExitClick(object sender, RoutedEventArgs args)
    {
        try { await ViewModel.ExitCalibrationSessionAsync(ViewModel.ExitReason); }
        finally { ApplyState(); }
    }

    private async void ReadFrameClick(object sender, RoutedEventArgs args)
    {
        try { await ViewModel.ReadSelectedFrameAsync(); }
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
            ? "当前方案、会话或 Runtime 边界不可用；请先配置方案、登录并刷新。"
            : "标定会话不可用：Runtime、会话、绑定、成像或证据查询服务未完整配置。";
        StatusLabel.Text = viewModel.IsBusy
            ? "处理中"
            : viewModel.HasActiveCalibrationSession
                ? "会话中"
                : viewModel.IsAuthenticated ? "待刷新 / 待开始" : "未登录";
        StatusText.Text = viewModel.StatusMessage;
        ErrorText.Text = viewModel.ErrorCode ?? string.Empty;
        BindingText.Text = viewModel.CurrentBinding is { } binding
            ? $"Revision={binding.Revision} · Hash={binding.RevisionHash} · TargetHash={binding.Target.ContentHash}"
            : "尚未授权读回实际绑定。";
        ImagingText.Text = viewModel.CurrentImagingRevision is { } revision
            ? $"Revision={revision.Revision} · Hash={revision.RevisionHash} · 原因={revision.ChangeReason}"
            : "尚未授权读回当前成像修订。";

        var editable = configured && !viewModel.IsBusy;
        StartReasonBox.IsEnabled = editable;
        StartPasswordBox.IsEnabled = editable;
        ExcludeReasonBox.IsEnabled = editable;
        ExitReasonBox.IsEnabled = editable;
        RefreshButton.IsEnabled = viewModel.CanRefresh;
        CancelButton.IsEnabled = viewModel.IsBusy;
        StartButton.IsEnabled = viewModel.CanStart;
        CaptureButton.IsEnabled = viewModel.CanCapture;
        ExcludeButton.IsEnabled = viewModel.CanExclude;
        ComputeButton.IsEnabled = viewModel.CanCompute;
        ExitButton.IsEnabled = viewModel.CanExit;
        ReadFrameButton.IsEnabled = viewModel.CanReadSelectedFrame;
        FramesGrid.IsEnabled = configured && !viewModel.IsBusy;
        ObservationsGrid.IsEnabled = configured;
        ExclusionsGrid.IsEnabled = configured;
    }
}
