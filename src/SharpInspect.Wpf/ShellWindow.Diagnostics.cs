using System.Windows;

namespace SharpInspect.Wpf;

public partial class ShellWindow
{
    private DiagnosticsViewModel? _diagnosticsViewModel;
    public void AttachDiagnostics(DiagnosticsViewModel viewModel)
    {
        Dispatcher.VerifyAccess(); ArgumentNullException.ThrowIfNull(viewModel);
        if (_diagnosticsViewModel is not null && !ReferenceEquals(_diagnosticsViewModel, viewModel))
            throw new InvalidOperationException("DiagnosticsAlreadyAttached");
        _diagnosticsViewModel = viewModel; DiagnosticsPanel.DataContext = viewModel; RenderState();
    }
    private void RenderDiagnostics(bool maintenanceSelected)
    {
        var visible = maintenanceSelected && !IsPrivacyLocked && _diagnosticsViewModel is not null;
        DiagnosticsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) DeactivateDiagnostics();
        RenderDiagnosticSupport(maintenanceSelected);
    }
    private void DeactivateDiagnostics() => _diagnosticsViewModel?.Deactivate();
}
