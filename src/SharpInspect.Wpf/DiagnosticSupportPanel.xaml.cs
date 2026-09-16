using System.Windows;
using System.Windows.Controls;

namespace SharpInspect.Wpf;

/// <summary>
/// Presentation host for the optional bounded diagnostic-support surface. Native
/// credential input is read exactly once per submission and cleared immediately; the
/// view model never stores a password. The panel exposes no destination path, raw or
/// protected toggle, or save/copy/read-material action.
/// </summary>
public partial class DiagnosticSupportPanel : UserControl
{
    public DiagnosticSupportPanel()
    {
        InitializeComponent();
        Unloaded += (_, _) => Deactivate();
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is DiagnosticSupportViewModel old)
            {
                old.SensitiveInputsInvalidated -= ClearSensitiveInputs;
                old.Deactivate();
            }
            if (args.NewValue is DiagnosticSupportViewModel current)
                current.SensitiveInputsInvalidated += ClearSensitiveInputs;
            ClearSensitiveInputs(this, EventArgs.Empty);
        };
    }

    public void Deactivate()
    {
        Dispatcher.VerifyAccess();
        (DataContext as DiagnosticSupportViewModel)?.Deactivate();
        ClearSensitiveInputs(this, EventArgs.Empty);
    }

    private void ClearSensitiveInputs(object? sender, EventArgs args) => StepUpPasswordBox.Clear();

    private async void RefreshClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is DiagnosticSupportViewModel model) await model.RefreshAsync();
    }

    private async void StartCaptureClick(object sender, RoutedEventArgs args)
    {
        var password = StepUpPasswordBox.Password;
        StepUpPasswordBox.Clear();
        try
        {
            if (DataContext is DiagnosticSupportViewModel model)
                await model.StartCaptureWithStepUpAsync(password);
        }
        finally
        {
            password = string.Empty;
            StepUpPasswordBox.Clear();
        }
    }

    private async void StopCaptureClick(object sender, RoutedEventArgs args)
    {
        var password = StepUpPasswordBox.Password;
        StepUpPasswordBox.Clear();
        try
        {
            if (DataContext is DiagnosticSupportViewModel model)
                await model.StopCaptureWithStepUpAsync(password);
        }
        finally
        {
            password = string.Empty;
            StepUpPasswordBox.Clear();
        }
    }

    private async void CreateBundleClick(object sender, RoutedEventArgs args)
    {
        var password = StepUpPasswordBox.Password;
        StepUpPasswordBox.Clear();
        try
        {
            if (DataContext is DiagnosticSupportViewModel model)
                await model.CreateBundleWithStepUpAsync(password);
        }
        finally
        {
            password = string.Empty;
            StepUpPasswordBox.Clear();
        }
    }
}
