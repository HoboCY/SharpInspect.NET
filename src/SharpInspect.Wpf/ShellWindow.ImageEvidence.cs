using System.Windows;

namespace SharpInspect.Wpf;

public partial class ShellWindow
{
    private ProductionImageEvidenceViewModel? _productionImageEvidenceViewModel;
    private bool _productionImageEvidenceSelectionLoaded;

    /// <summary>Attaches the verified image query surface while preserving every existing constructor ABI.</summary>
    public void AttachProductionImageEvidence(ProductionImageEvidenceViewModel viewModel)
    {
        Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(viewModel);
        if (_productionImageEvidenceViewModel is not null && !ReferenceEquals(_productionImageEvidenceViewModel, viewModel))
            throw new InvalidOperationException("ProductionImageEvidenceAlreadyAttached");
        if (ReferenceEquals(_productionImageEvidenceViewModel, viewModel)) return;
        _productionImageEvidenceViewModel = viewModel;
        ProductionImageEvidencePanel.DataContext = viewModel;
        RenderState();
    }
    private void RenderProductionImageEvidence(bool traceSelected)
    {
        var visible = traceSelected && !IsPrivacyLocked && _productionImageEvidenceViewModel is not null;
        ProductionImageEvidencePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) DeactivateProductionImageEvidence();
        else if (!_productionImageEvidenceSelectionLoaded)
        {
            _productionImageEvidenceSelectionLoaded = true;
            _ = _productionImageEvidenceViewModel!.RefreshAsync();
        }
    }
    private void DeactivateProductionImageEvidence()
    {
        if (!_productionImageEvidenceSelectionLoaded) return;
        _productionImageEvidenceSelectionLoaded = false;
        ProductionImageEvidencePanel.Deactivate();
    }
}
