using SharpInspect.Abstractions;
using SharpInspect.Runtime.Evidence;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private IEvidenceRetentionService? _retentionService;
    private EvidenceRetentionWorker? _retentionWorker;
    private string? _retentionFailure;
    private readonly TaskCompletionSource<Task> _retentionReconciliationStartup =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ConfigureRetentionWorker(ProductionStoreOptions? options)
    {
        if (options?.StorageRetention is null) return;
        if (_audit is not SqliteCommandStore store) throw new ArgumentException("RetentionSqliteStoreRequired");
        _retentionWorker = new(store, options, _snapshot.RuntimeEpoch, _storeInitialization,
            WaitForRetentionReconciliationAsync(), reason =>
            {
                lock (_sync)
                {
                    _retentionFailure ??= reason;
                    if (!_disposed) PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed });
                }
                return Task.CompletedTask;
            }, () => false, startupMaintenance: true);
    }
    private async Task WaitForRetentionReconciliationAsync()
    {
        var task = await _retentionReconciliationStartup.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        await task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
    }
    private bool RetentionReadyLocked() => _retentionWorker is null ||
        _retentionFailure is null && _retentionWorker.Startup.IsCompletedSuccessfully;

    private async Task ShutdownRetentionAsync()
    {
        if (_retentionWorker is { } worker && !await worker.StopAsync().ConfigureAwait(false))
            MarkAuditFault("RetentionPhysicalRetirementPending");
    }
    internal void ConfigureRetentionService(IEvidenceRetentionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        lock (_sync)
        {
            if (_retentionService is not null) throw new InvalidOperationException("RetentionAlreadyConfigured");
            _retentionService = service;
        }
    }
    private async ValueTask<RuntimeCommandOutcome> SubmitRetentionAsync(ChangeEvidenceRetentionCommand command,
        CancellationToken token)
    {
        IEvidenceRetentionService? service;
        lock (_sync)
        {
            if (_disposed || _shutdownRequested)
                return new(command.CorrelationId, CommandDisposition.Rejected, "RuntimeUnavailable",
                    AuditPersistence.Unavailable, Guid.NewGuid());
            service = _retentionService;
        }
        if (service is null) return new(command.CorrelationId, CommandDisposition.Rejected,
            "RetentionConfigurationRequired", AuditPersistence.NotAttempted, Guid.NewGuid());
        return (await service.ChangeAsync(command, token).ConfigureAwait(false)).Outcome;
    }
}
