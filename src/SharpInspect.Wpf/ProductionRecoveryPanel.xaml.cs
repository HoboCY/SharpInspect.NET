using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace SharpInspect.Wpf;

public partial class ProductionRecoveryPanel : UserControl
{
    private readonly ProductionRecoveryViewModel _fallbackViewModel;
    private ProductionRecoveryViewModel? _observedViewModel;

    public ProductionRecoveryPanel() : this(null) { }

    public ProductionRecoveryPanel(ProductionRecoveryViewModel? viewModel)
    {
        InitializeComponent();
        _fallbackViewModel = viewModel ?? new ProductionRecoveryViewModel();
        DataContext = viewModel ?? _fallbackViewModel;
        DataContextChanged += PanelDataContextChanged;
        Unloaded += PanelUnloaded;
        AttachViewModel(DataContext as ProductionRecoveryViewModel);
        ApplyState();
    }

    public ProductionRecoveryViewModel ViewModel =>
        DataContext as ProductionRecoveryViewModel ?? _fallbackViewModel;

    public void ClearSensitiveInputs()
    {
        StepUpPasswordBox.Clear();
        ApplyState();
    }

    public void Deactivate()
    {
        ViewModel.CancelPendingOperations();
        ClearSensitiveInputs();
    }

    private void PanelDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        AttachViewModel(args.NewValue as ProductionRecoveryViewModel);
        ClearSensitiveInputs();
        ApplyState();
    }

    private void AttachViewModel(ProductionRecoveryViewModel? viewModel)
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

    private void PanelUnloaded(object sender, RoutedEventArgs args) => Deactivate();
    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args) => ApplyState();
    private void ViewModelSessionInvalidated(object? sender, EventArgs args) => ClearSensitiveInputs();

    private async void RefreshClick(object sender, RoutedEventArgs args)
    {
        try { await ViewModel.RefreshAsync(); }
        finally { ApplyState(); }
    }

    private async void RecoverClick(object sender, RoutedEventArgs args)
    {
        var password = StepUpPasswordBox.Password;
        StepUpPasswordBox.Clear();
        try
        {
            if (ViewModel.SelectedDisposition is not { } disposition) return;
            await ViewModel.RecoverAsync(ReasonTextBox.Text, DispositionNoteTextBox.Text,
                disposition, password);
        }
        finally
        {
            password = string.Empty;
            StepUpPasswordBox.Clear();
            ApplyState();
        }
    }

    private void ApplyState()
    {
        var model = ViewModel;
        var configured = model.IsConfigured;
        UnavailableText.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        ConfiguredPanel.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        var editable = configured && !model.IsBusy && model.IsAuthenticated && !model.IsStale;
        PendingGrid.IsEnabled = configured && !model.IsBusy;
        ReasonTextBox.IsEnabled = editable;
        DispositionComboBox.IsEnabled = editable;
        DispositionNoteTextBox.IsEnabled = editable;
        StepUpPasswordBox.IsEnabled = editable;
        RefreshButton.IsEnabled = model.CanRefresh;
        RecoverButton.IsEnabled = model.CanRecover;
    }
}
