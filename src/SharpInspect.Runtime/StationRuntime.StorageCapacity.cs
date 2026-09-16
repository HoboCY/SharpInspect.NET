using SharpInspect.Abstractions;
using SharpInspect.Runtime.Alarms;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.StoragePolicies;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private const string StorageCapacityAlarmCode = "TraceStorageCapacityBlocked";
    private const string StorageCapacityAlarmSource = "Runtime.TraceStorage";
    private TraceStorageCapacityMonitor? _storageCapacity;

    internal ITraceStorageCapacityQuery StorageCapacityQuery => _storageCapacity ??
        throw new InvalidOperationException("TraceStorageRetentionNotConfigured");

    private void ConfigureStorageCapacity(ProductionStoreOptions? options)
    {
        if (options?.StorageRetention is null) return;
        _storageCapacity = new(options, _snapshot.RuntimeEpoch, _storeInitialization, () =>
        {
            lock (_sync)
            {
                if (_disposed || _shutdownRequested) return;
                // Reprojection only blocks the next trigger. Accepted-cycle cancellation
                // remains owned by its existing lifecycle and alarm impact contract.
                PublishLocked(_snapshot);
            }
        }, checkpoint: () => ((SqliteCommandStore)_audit!).Checkpoint);
    }

    private string? StorageCapacityFailureLocked()
    {
        if (_storageCapacity is null) return null;
        if (_retentionFailure is not null) return "RetentionRecoveryRequired";
        if (!RetentionReadyLocked()) return "RetentionStartupPending";
        var capacity = _storageCapacity.Current();
        if (!capacity.Available || capacity.Health != TraceStorageHealth.Healthy)
            return capacity.ReasonCode;
        if (_productionInspectionPolicy?.ContentHash != capacity.PolicySnapshotHash)
            return "TraceStorageCapacityPolicyChanged";
        if (!StorageCapacityAlarmMappingValid()) return "TraceStorageCapacityAlarmMappingUnavailable";
        return null;
    }

    private bool StorageCapacityAlarmMappingValid() => ConfiguredAlarmPolicy is { } policy &&
        policy.TryGetRule(StorageCapacityAlarmCode, out var rule) && rule is not null &&
        rule.Source == StorageCapacityAlarmSource && rule.ProductionImpact == ProductionImpact.BlockNewTriggers && rule.IsLatched;

    private AlarmObservation? StorageCapacityObservationLocked(AlarmPolicy policy)
    {
        if (_storageCapacity is null || _productionInspectionPolicy is null || !StorageCapacityAlarmMappingValid())
            return null;
        // Policy publication can precede the first observer turn; admission remains closed meanwhile.
        if (_storageCapacity.Current().ReasonCode is "TraceStorageObservationPending" or "TraceStoragePolicyMissing") return null;
        var healthy = StorageCapacityFailureLocked() is null;
        if (_alarmObservations.TryGetValue(StorageCapacityAlarmCode, out var prior) &&
            prior.Healthy == healthy && IsFresh(prior, policy)) return null;
        _registeredAlarmSources.Add(StorageCapacityAlarmSource);
        var sequence = NextAlarmObservationSequenceLocked(StorageCapacityAlarmCode);
        if (healthy && _snapshot.AlarmState?.Instances.Any(instance =>
                instance.Code == StorageCapacityAlarmCode && instance.Lifecycle != AlarmLifecycle.Cleared) != true)
        {
            _alarmObservations[StorageCapacityAlarmCode] = new(_snapshot.RuntimeEpoch, sequence,
                System.Diagnostics.Stopwatch.GetTimestamp(), true);
            return null;
        }
        return new(_snapshot.RuntimeEpoch, sequence, StorageCapacityAlarmCode,
            StorageCapacityAlarmSource, healthy, DateTimeOffset.UtcNow);
    }

    private async Task ShutdownStorageCapacityAsync()
    {
        if (_storageCapacity is { } monitor && !await monitor.StopAsync().ConfigureAwait(false))
            MarkAuditFault("TraceStorageObservationPhysicalRetirementPending");
    }
}
