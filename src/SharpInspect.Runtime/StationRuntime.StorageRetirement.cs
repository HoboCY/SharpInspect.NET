using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async Task RetireStorageOwnerAsync()
    {
        if (_audit is SqliteCommandStore { DeploymentOwnership: not null })
        {
            MaintenanceRetirementWitness witness;
            Task admissionRetired;
            lock (_sync)
            {
                _shutdownRequested = true;
                admissionRetired = _maintenanceAdmissionRetired?.Task ?? Task.CompletedTask;
            }
            using var stop = new CancellationTokenSource(_maintenanceOptions?.StationMaintenance?.Policy.RetirementTimeout ??
                TimeSpan.FromMinutes(5));
            await admissionRetired.WaitAsync(stop.Token).ConfigureAwait(false);
            lock (_sync) witness = CaptureMaintenanceRetirementLocked();
            await DisposeAsync().AsTask().WaitAsync(stop.Token).ConfigureAwait(false);
            await witness.WaitAsync(stop.Token).ConfigureAwait(false);
            return;
        }
        try { await DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        // A logical stop deadline is not permission to hand this store's .lock to
        // startup maintenance while an old physical delivery operation still runs.
        if (_storageCapacity is { } capacity) await capacity.StopAsync().ConfigureAwait(false);
        if (_retentionWorker is { } retention) await retention.StopAsync().ConfigureAwait(false);
        if (_evidenceReconciliationWorker is { } reconciliation) await reconciliation.StopAsync().ConfigureAwait(false);
        if (_outboxWorker is { } outbox) await outbox.StopAsync().ConfigureAwait(false);
        if (_imageFinalizationWorker is { } images) await images.StopAsync().ConfigureAwait(false);
        var owners = new[] { _productionInspectionTask, _storageCapacity?.PhysicalCompletion, _retentionWorker?.Completion,
            _evidenceReconciliationWorker?.Completion, _outboxWorker?.Completion, _imageFinalizationWorker?.Completion };
        try { await Task.WhenAll(owners.Where(task => task is not null).Select(task => task!)).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { } // WhenAll has physically retired every owner.
    }
}
