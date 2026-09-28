using System.Windows;

namespace SharpInspect.Wpf;

public partial class ShellWindow
{
    private DiagnosticSupportViewModel? _diagnosticSupportViewModel;
    private bool _diagnosticSupportSelectionLoaded;
    private bool _diagnosticSupportHooksAttached;

    /// <summary>
    /// Attaches the optional bounded diagnostic-support maintenance surface once the
    /// host has composed its Runtime/query/policy services. Existing constructor
    /// signatures and the legacy sample composition stay unchanged.
    /// </summary>
    public void AttachDiagnosticSupport(DiagnosticSupportViewModel viewModel)
    {
        Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(viewModel);
        if (_diagnosticSupportViewModel is not null && !ReferenceEquals(_diagnosticSupportViewModel, viewModel))
            throw new InvalidOperationException("DiagnosticSupportAlreadyAttached");
        if (ReferenceEquals(_diagnosticSupportViewModel, viewModel)) return;
        _diagnosticSupportViewModel = viewModel;
        DiagnosticSupportPanel.DataContext = viewModel;
        if (!_diagnosticSupportHooksAttached)
        {
            _diagnosticSupportHooksAttached = true;
            PrivacyCover.IsVisibleChanged += DiagnosticSupportPrivacyChanged;
            Closed += DiagnosticSupportClosed;
        }
        RenderState();
    }

    private void RenderDiagnosticSupport(bool maintenanceSelected)
    {
        var visible = maintenanceSelected && !IsPrivacyLocked && _diagnosticSupportViewModel is not null;
        DiagnosticSupportPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            DeactivateDiagnosticSupport();
            return;
        }
        if (!_diagnosticSupportSelectionLoaded)
        {
            _diagnosticSupportSelectionLoaded = true;
            _ = _diagnosticSupportViewModel!.RefreshAsync();
        }
    }

    private void DeactivateDiagnosticSupport()
    {
        if (!_diagnosticSupportSelectionLoaded) return;
        _diagnosticSupportSelectionLoaded = false;
        DiagnosticSupportPanel.Deactivate();
    }

    private void DiagnosticSupportPrivacyChanged(object? sender, DependencyPropertyChangedEventArgs args)
    {
        if (PrivacyCover.Visibility == Visibility.Visible) DeactivateDiagnosticSupport();
    }

    private void DiagnosticSupportClosed(object? sender, EventArgs args)
    {
        Closed -= DiagnosticSupportClosed;
        PrivacyCover.IsVisibleChanged -= DiagnosticSupportPrivacyChanged;
        DeactivateDiagnosticSupport();
    }
}
