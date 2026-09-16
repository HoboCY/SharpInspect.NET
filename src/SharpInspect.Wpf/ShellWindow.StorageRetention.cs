using System.Windows;

namespace SharpInspect.Wpf;

public partial class ShellWindow
{
    private StorageRetentionViewModel? _storageRetentionViewModel;
    private bool _storageRetentionSelectionLoaded;

    public void AttachStorageRetention(StorageRetentionViewModel viewModel)
    {
        Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(viewModel);
        if (_storageRetentionViewModel is not null && !ReferenceEquals(_storageRetentionViewModel, viewModel))
            throw new InvalidOperationException("StorageRetentionAlreadyAttached");
        if (ReferenceEquals(_storageRetentionViewModel, viewModel)) return;
        _storageRetentionViewModel = viewModel;
        StorageRetentionPanel.SetViewModel(viewModel);
        RenderState();
    }

    private void RenderStorageRetention(bool maintenanceSelected)
    {
        var visible = maintenanceSelected && !IsPrivacyLocked && _storageRetentionViewModel is not null;
        StorageRetentionPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) DeactivateStorageRetention();
        else if (!_storageRetentionSelectionLoaded)
        {
            _storageRetentionSelectionLoaded = true;
            _ = _storageRetentionViewModel!.RefreshAsync();
        }
    }

    private void DeactivateStorageRetention()
    {
        if (!_storageRetentionSelectionLoaded) return;
        _storageRetentionSelectionLoaded = false;
        StorageRetentionPanel.Deactivate();
    }
}
