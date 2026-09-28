using System.Windows;
using System.Windows.Controls;

namespace SharpInspect.Wpf;

public partial class StationMaintenancePanel : UserControl
{
    public StationMaintenancePanel()
    {
        InitializeComponent();
        Unloaded += (_, _) => Deactivate();
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is StationMaintenanceViewModel old)
            { old.SensitiveInputsInvalidated -= ClearPassword; old.Deactivate(); }
            if (args.NewValue is StationMaintenanceViewModel current)
                current.SensitiveInputsInvalidated += ClearPassword;
            StepUpPasswordBox.Clear();
        };
    }
    public void Deactivate()
    { (DataContext as StationMaintenanceViewModel)?.Deactivate(); StepUpPasswordBox.Clear(); }
    private void ClearPassword(object? sender, EventArgs args) => StepUpPasswordBox.Clear();
    private async void PrepareClick(object sender, RoutedEventArgs args)
    {
        var password = StepUpPasswordBox.Password; StepUpPasswordBox.Clear();
        try { if (DataContext is StationMaintenanceViewModel model) await model.PrepareWithStepUpAsync(password); }
        finally { password = string.Empty; StepUpPasswordBox.Clear(); }
    }
}
