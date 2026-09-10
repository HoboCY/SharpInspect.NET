using System.Windows;
using System.Windows.Controls;

namespace SharpInspect.Wpf;

public partial class StationQualificationSessionPanel : UserControl
{
    private StationQualificationSessionViewModel? _observed;

    public StationQualificationSessionPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, args) =>
        {
            if (_observed is not null) _observed.SessionInvalidated -= ClearSessionInputs;
            _observed = args.NewValue as StationQualificationSessionViewModel;
            if (_observed is not null) _observed.SessionInvalidated += ClearSessionInputs;
            ClearSensitiveInputs();
        };
        Unloaded += (_, _) => Deactivate();
    }

    public StationQualificationSessionViewModel? ViewModel => DataContext as StationQualificationSessionViewModel;
    public void ClearSensitiveInputs() { ReasonBox.Clear(); StepUpPasswordBox.Clear(); }
    public void Deactivate() { ViewModel?.CancelPendingOperations(); ClearSensitiveInputs(); }
    private void ClearSessionInputs(object? sender, EventArgs args) => ClearSensitiveInputs();

    private async void RefreshClicked(object sender, RoutedEventArgs args)
    { if (ViewModel is { } model) await model.RefreshAsync(); }

    private async void StartClicked(object sender, RoutedEventArgs args)
    {
        var reason = ReasonBox.Text;
        var password = StepUpPasswordBox.Password;
        StepUpPasswordBox.Clear();
        if (ViewModel is { } model) await model.StartAsync(reason, password);
    }

    private async void ExitClicked(object sender, RoutedEventArgs args)
    { if (ViewModel is { } model) await model.ExitAsync(ReasonBox.Text); }

    private async void AbortClicked(object sender, RoutedEventArgs args)
    { if (ViewModel is { } model) await model.ExitAsync(ReasonBox.Text, abort: true); }
}
