using System.Windows;

namespace SharpInspect.Wpf;

public partial class ShellWindow
{
    private EvidenceReconciliationViewModel? _evidenceReconciliationViewModel;
    private bool _evidenceReconciliationSelectionLoaded;

    /// <summary>Attaches the verified image query surface while preserving every existing constructor ABI.</summary>
    public void AttachEvidenceReconciliation(EvidenceReconciliationViewModel viewModel)
    {
        Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(viewModel);
        if (_evidenceReconciliationViewModel is not null && !ReferenceEquals(_evidenceReconciliationViewModel, viewModel))
            throw new InvalidOperationException("EvidenceReconciliationAlreadyAttached");
        if (ReferenceEquals(_evidenceReconciliationViewModel, viewModel)) return;
        _evidenceReconciliationViewModel = viewModel;
        EvidenceReconciliationPanel.DataContext = viewModel;
        RenderState();
    }
    private void RenderEvidenceReconciliation(bool traceSelected)
    {
        var visible = traceSelected && !IsPrivacyLocked && _evidenceReconciliationViewModel is not null;
        EvidenceReconciliationPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) DeactivateEvidenceReconciliation();
        else if (!_evidenceReconciliationSelectionLoaded)
        {
            _evidenceReconciliationSelectionLoaded = true;
            _ = _evidenceReconciliationViewModel!.RefreshAsync();
        }
    }
    private void DeactivateEvidenceReconciliation()
    {
        if (!_evidenceReconciliationSelectionLoaded) return;
        _evidenceReconciliationSelectionLoaded = false;
        EvidenceReconciliationPanel.Deactivate();
    }
}
