using System.Windows;
using System.Windows.Controls;

namespace SharpInspect.Wpf;

public partial class AlarmPanel : UserControl
{
    public AlarmPanel()
    {
        InitializeComponent();
        DataContextChanged += DataContextChangedHandler;
        AttachViewModel(DataContext as AlarmViewModel);
        ApplyState();
    }

    public AlarmViewModel? ViewModel => DataContext as AlarmViewModel;

    /// <summary>Clears Step-Up input and cancels only work not yet admitted by Runtime.</summary>
    public void ClearSensitiveInputs()
    {
        ResetStepUpPasswordBox.Clear();
        ViewModel?.CancelPendingOperations();
        ApplyState();
    }

    private void DataContextChangedHandler(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is AlarmViewModel oldViewModel)
            oldViewModel.PropertyChanged -= ViewModelChanged;
        AttachViewModel(e.NewValue as AlarmViewModel);
        ClearSensitiveInputs();
        ApplyState();
    }

    private void AttachViewModel(AlarmViewModel? viewModel)
    {
        if (viewModel is not null) viewModel.PropertyChanged += ViewModelChanged;
    }

    private void ViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplyState();

    private async void ResetClick(object sender, RoutedEventArgs e) => await SubmitSelectedAsync(reset: true);

    private async void AcknowledgeClick(object sender, RoutedEventArgs e) => await SubmitSelectedAsync(reset: false);

    private async Task SubmitSelectedAsync(bool reset)
    {
        var viewModel = ViewModel;
        var selected = viewModel?.SelectedAlarm;
        var password = ResetStepUpPasswordBox.Password;
        ResetStepUpPasswordBox.Clear();
        try
        {
            if (viewModel is not null && selected is not null)
            {
                if (reset) await viewModel.ResetAlarmAsync(selected.InstanceId, password);
                else await viewModel.AcknowledgeAlarmAsync(selected.InstanceId, password);
            }
        }
        catch
        {
            // The VM converts expected Runtime failures to stable safe state;
            // an unexpected UI event failure must not escape the WPF dispatcher.
        }
        finally
        {
            password = string.Empty;
            ResetStepUpPasswordBox.Clear();
            ApplyState();
        }
    }

    private void ApplyState()
    {
        var viewModel = ViewModel;
        UnavailablePanel.Visibility = viewModel is not null && !viewModel.IsSnapshotUnavailable
            ? Visibility.Collapsed : Visibility.Visible;
        ConfiguredPanel.Visibility = viewModel is null ? Visibility.Collapsed : Visibility.Visible;
        if (viewModel is null)
        {
            AcknowledgeButton.IsEnabled = false;
            ResetButton.IsEnabled = false;
            return;
        }

        AcknowledgeButton.IsEnabled = viewModel.CanAcknowledge;
        ResetButton.IsEnabled = viewModel.CanReset;
    }
}
