using SharpInspect.Abstractions;
using SharpInspect.Runtime.Evidence;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private EvidenceReconciliationWorker? _evidenceReconciliationWorker;
    private bool _evidenceReconciliationRequired;
    private bool _evidenceReconciliationFaulted;
    private readonly TaskCompletionSource<Task> _evidenceOutboxStartup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void ConfigureEvidenceReconciliation(ProductionStoreOptions? options)
    {
        if (options?.EvidenceReconciliation is null) return;
        if (_audit is not SqliteCommandStore store) throw new ArgumentException("EvidenceReconciliationSqliteStoreRequired");
        _evidenceReconciliationRequired = true;
        var initialization = _retentionWorker is null ? _storeInitialization :
            Task.WhenAll(_storeInitialization, _retentionWorker.Startup);
        _evidenceReconciliationWorker = new(store, options, _snapshot.RuntimeEpoch, initialization,
            options.Outbox is null ? Task.CompletedTask : WaitForEvidenceOutboxStartupAsync(),
            HandleEvidenceReconciliationFaultAsync, () =>
            {
                lock (_sync) return !_disposed && !_shutdownRequested && !_snapshot.Busy &&
                    _productionInspectionOwner?.Current is null;
            });
        _retentionReconciliationStartup.TrySetResult(_evidenceReconciliationWorker.Startup);
    }
    private async Task WaitForEvidenceOutboxStartupAsync()
    {
        var startup = await _evidenceOutboxStartup.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        await startup.WaitAsync(_lifetime.Token).ConfigureAwait(false);
    }
    private bool EvidenceReconciliationReadyLocked() => RetentionReadyLocked() && (!_evidenceReconciliationRequired ||
        !_evidenceReconciliationFaulted && _evidenceReconciliationWorker?.Startup.IsCompletedSuccessfully == true);
    private async Task HandleEvidenceReconciliationFaultAsync(string reason)
    {
        lock (_sync)
        {
            if (_disposed || _evidenceReconciliationFaulted) return;
            _evidenceReconciliationFaulted = true;
            PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                Evidence = _snapshot.Evidence with { State = HealthState.Faulted },
                AdmissionBlockers = new AdmissionBlockers(_snapshot.AdmissionBlockers.Append(reason).Distinct(StringComparer.Ordinal)) });
        }
        var integrity = !reason.Contains("Capacity", StringComparison.Ordinal) &&
            !reason.Contains("Deadline", StringComparison.Ordinal) && !reason.Contains("Unavailable", StringComparison.Ordinal);
        await HandleImageFinalizationFaultAsync(reason, integrity).ConfigureAwait(false);
    }
    private async Task ShutdownEvidenceReconciliationAsync()
    {
        if (_evidenceReconciliationWorker is { } worker && !await worker.StopAsync().ConfigureAwait(false))
            MarkAuditFault("EvidenceReconciliationPhysicalRetirementPending");
    }
}
