using System.Windows;

namespace SharpInspect.Wpf;

public partial class ShellWindow
{
    private ProductionOutboxViewModel? _productionOutboxViewModel;
    private bool _productionOutboxSelectionLoaded;

    /// <summary>Attaches delivery operations without changing existing constructor signatures.</summary>
    public void AttachProductionOutbox(ProductionOutboxViewModel viewModel)
    {
        Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(viewModel);
        if (_productionOutboxViewModel is not null && !ReferenceEquals(_productionOutboxViewModel, viewModel))
            throw new InvalidOperationException("ProductionOutboxAlreadyAttached");
        if (ReferenceEquals(_productionOutboxViewModel, viewModel)) return;
        _productionOutboxViewModel = viewModel;
        ProductionOutboxPanel.DataContext = viewModel;
        RenderState();
    }
    private void RenderProductionOutbox(bool traceSelected)
    {
        var visible = traceSelected && !IsPrivacyLocked && _productionOutboxViewModel is not null;
        ProductionOutboxPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) DeactivateProductionOutbox();
        else if (!_productionOutboxSelectionLoaded)
        {
            _productionOutboxSelectionLoaded = true;
            _ = _productionOutboxViewModel!.RefreshAsync();
        }
    }
    private void DeactivateProductionOutbox()
    {
        if (!_productionOutboxSelectionLoaded) return;
        _productionOutboxSelectionLoaded = false;
        ProductionOutboxPanel.Deactivate();
    }
}
