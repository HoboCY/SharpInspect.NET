using System.Windows;

namespace SharpInspect.Wpf;

public partial class ShellWindow
{
    private StationMaintenanceViewModel? _stationMaintenanceViewModel;
    private bool _stationMaintenanceLoaded;

    /// <summary>Attaches the governed host-shutdown page without changing existing constructor contracts.</summary>
    public void AttachStationMaintenance(StationMaintenanceViewModel viewModel)
    {
        Dispatcher.VerifyAccess(); ArgumentNullException.ThrowIfNull(viewModel);
        if (_stationMaintenanceViewModel is not null && !ReferenceEquals(_stationMaintenanceViewModel, viewModel))
            throw new InvalidOperationException("StationMaintenanceAlreadyAttached");
        if (ReferenceEquals(_stationMaintenanceViewModel, viewModel)) return;
        _stationMaintenanceViewModel = viewModel; StationMaintenancePanel.DataContext = viewModel;
        Closed += StationMaintenanceClosed;
        PrivacyCover.IsVisibleChanged += StationMaintenancePrivacyChanged;
        RenderState();
    }
    private void RenderStationMaintenance(bool maintenanceSelected)
    {
        var visible = maintenanceSelected && !IsPrivacyLocked && _stationMaintenanceViewModel is not null;
        StationMaintenancePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) { DeactivateStationMaintenance(); return; }
        if (!_stationMaintenanceLoaded)
        { _stationMaintenanceLoaded = true; _ = _stationMaintenanceViewModel!.RefreshAsync(); }
    }
    private void DeactivateStationMaintenance()
    {
        if (!_stationMaintenanceLoaded) return;
        _stationMaintenanceLoaded = false; StationMaintenancePanel.Deactivate();
    }
    private void StationMaintenancePrivacyChanged(object? sender, DependencyPropertyChangedEventArgs args)
    { if (PrivacyCover.Visibility == Visibility.Visible) DeactivateStationMaintenance(); }
    private void StationMaintenanceClosed(object? sender, EventArgs args)
    {
        Closed -= StationMaintenanceClosed; PrivacyCover.IsVisibleChanged -= StationMaintenancePrivacyChanged;
        DeactivateStationMaintenance();
    }
}
