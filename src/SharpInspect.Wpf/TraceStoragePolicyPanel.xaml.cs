using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace SharpInspect.Wpf;

public partial class TraceStoragePolicyPanel : UserControl
{
    private readonly TraceStoragePolicyViewModel _fallbackViewModel;
    private TraceStoragePolicyViewModel? _observedViewModel;

    public TraceStoragePolicyPanel() : this(null)
    {
    }

    public TraceStoragePolicyPanel(TraceStoragePolicyViewModel? viewModel)
    {
        InitializeComponent();
        _fallbackViewModel = viewModel ?? new TraceStoragePolicyViewModel(
            service: null, sessions: null, stepUpAuthentication: null,
            dispatcher: new DispatcherUiDispatcher());
        DataContext = viewModel ?? _fallbackViewModel;
        DataContextChanged += PanelDataContextChanged;
        Unloaded += PanelUnloaded;
        AttachViewModel(DataContext as TraceStoragePolicyViewModel);
        ApplyState();
    }

    public TraceStoragePolicyViewModel ViewModel =>
        DataContext as TraceStoragePolicyViewModel ?? _fallbackViewModel;

    public void Deactivate()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(Deactivate);
            return;
        }

        StepUpPasswordBox.Clear();
        ViewModel.Deactivate();
        ApplyState();
    }

    private void PanelDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.OldValue is TraceStoragePolicyViewModel oldViewModel)
        {
            oldViewModel.PropertyChanged -= ViewModelChanged;
            oldViewModel.SessionInvalidated -= SessionInvalidated;
            oldViewModel.Deactivate();
        }
        AttachViewModel(args.NewValue as TraceStoragePolicyViewModel);
        StepUpPasswordBox.Clear();
        ApplyState();
    }

    private void AttachViewModel(TraceStoragePolicyViewModel? viewModel)
    {
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged -= ViewModelChanged;
            _observedViewModel.SessionInvalidated -= SessionInvalidated;
        }
        _observedViewModel = viewModel;
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged += ViewModelChanged;
            _observedViewModel.SessionInvalidated += SessionInvalidated;
        }
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args) => RunOnUi(ApplyState);
    private void SessionInvalidated(object? sender, EventArgs args) =>
        RunOnUi(() => StepUpPasswordBox.Clear());
    private void PanelUnloaded(object sender, RoutedEventArgs args) => Deactivate();

    private async void RefreshClicked(object sender, RoutedEventArgs args)
    {
        try { await ViewModel.RefreshAsync(); }
        finally { ApplyState(); }
    }

    private void LoadCurrentClicked(object sender, RoutedEventArgs args)
    {
        _ = ViewModel.LoadCurrent();
        ApplyState();
    }

    private async void PublishClicked(object sender, RoutedEventArgs args)
    {
        var reason = ReasonTextBox.Text;
        var password = StepUpPasswordBox.Password;
        StepUpPasswordBox.Clear();
        try { await ViewModel.PublishAsync(reason, password); }
        finally
        {
            password = string.Empty;
            StepUpPasswordBox.Clear();
            ApplyState();
        }
    }

    private void AddRouteClicked(object sender, RoutedEventArgs args)
    {
        ViewModel.Editor.RequiredRoutes.Add(new TraceStorageRouteEditor());
        ApplyState();
    }

    private void RemoveRouteClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: TraceStorageRouteEditor route })
            ViewModel.Editor.RequiredRoutes.Remove(route);
        ApplyState();
    }

    private void ApplyState()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(ApplyState);
            return;
        }

        var model = ViewModel;
        RefreshButton.IsEnabled = model.CanRefresh;
        LoadCurrentButton.IsEnabled = !model.IsBusy && model.Current is not null;
        PublishButton.IsEnabled = model.CanPublish;
        AddRouteButton.IsEnabled = !model.IsBusy;
        StatusLabel.Text = model.IsBusy ? "处理中" :
            model.IsAuthenticated ? "已登录" : "未登录";
    }

    private void RunOnUi(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = Dispatcher.InvokeAsync(action);
    }
}
