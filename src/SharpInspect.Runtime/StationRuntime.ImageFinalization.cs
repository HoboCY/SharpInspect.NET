using SharpInspect.Abstractions;
using SharpInspect.Runtime.Alarms;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private const string ImageIntegrityAlarmCode = "EvidenceIntegrityFault";
    private const string ImageBacklogAlarmCode = "ImageEvidenceBacklog";
    private const string ImageIntegrityAlarmSource = "Runtime.ImageEvidence";
    private ProductionImageFinalizationWorker? _imageFinalizationWorker;
    private ImageBacklogSnapshot _imageBacklogSnapshot = ImageBacklogSnapshot.Empty(0);
    private readonly Dictionary<Guid, (long Sequence, PendingImageManifest Manifest)> _imageBacklogUnobservedCores = new();
    private bool _imageFinalizationFaulted;

    private void ConfigureImageFinalization(ProductionStoreOptions? options)
    {
        if (options?.ImageFinalization is null) return;
        if (_audit is not SqliteCommandStore store)
            throw new ArgumentException("ImageFinalizationSqliteStoreRequired");
        _imageFinalizationWorker = new(store, options, _snapshot.RuntimeEpoch, _storeInitialization,
            ProjectImageBacklog, reason => HandleImageFinalizationFaultAsync(reason, true),
            classifiedFault: HandleImageFinalizationFaultAsync);
    }

    private void ProjectImageBacklog(ImageBacklogSnapshot backlog)
    {
        lock (_sync)
        {
            if (_disposed || backlog.ThroughAuditSequence <= _imageBacklogSnapshot.ThroughAuditSequence) return;
            _imageBacklogSnapshot = backlog;
            foreach (var id in _imageBacklogUnobservedCores.Where(pair =>
                pair.Value.Sequence <= backlog.ThroughAuditSequence).Select(pair => pair.Key).ToArray())
                _imageBacklogUnobservedCores.Remove(id);
            ProjectImageBacklogLocked();
        }
    }

    private void ProjectImageBacklogLocked()
    {
        _productionPendingImages = checked((int)(_imageBacklogSnapshot.Count + _imageBacklogUnobservedCores.Count));
        _productionPendingImageBytes = checked(_imageBacklogSnapshot.Bytes +
            _imageBacklogUnobservedCores.Values.Sum(value => value.Manifest.CanonicalByteLength));
        _productionOldestPendingImage = _imageBacklogSnapshot.OldestCreatedAtUtc;
        foreach (var value in _imageBacklogUnobservedCores.Values)
            if (_productionOldestPendingImage is null || value.Manifest.CreatedAtUtc < _productionOldestPendingImage)
                _productionOldestPendingImage = value.Manifest.CreatedAtUtc;
        PublishLocked(_snapshot with { Evidence = _snapshot.Evidence with
            { PendingRequiredImages = _productionPendingImages } });
    }

    private bool IsProductionImageAlarmMappingValid() => ConfiguredAlarmPolicy is { } policy &&
        policy.TryGetRule(ImageIntegrityAlarmCode, out var rule) && rule is not null &&
        rule.Source == ImageIntegrityAlarmSource && rule.IsLatched &&
        rule.ProductionImpact == ProductionImpact.BlockNewTriggers &&
        (rule.ResetPrerequisites & (AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution)) ==
            (AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution);

    private bool IsProductionImageBacklogAlarmMappingValid() => ConfiguredAlarmPolicy is { } policy &&
        policy.TryGetRule(ImageBacklogAlarmCode, out var rule) && rule is not null &&
        rule.Source == ImageIntegrityAlarmSource && rule.ProductionImpact == ProductionImpact.BlockNewTriggers;

    private bool ProductionImageBacklogExceededLocked() => _productionInspectionPolicy is { } policy &&
        (_productionPendingImages >= policy.Policy.ImageBacklog.MaximumItems ||
         _productionPendingImageBytes >= policy.Policy.ImageBacklog.MaximumBytes ||
         _productionOldestPendingImage is { } oldest &&
            DateTimeOffset.UtcNow - oldest >= policy.Policy.ImageBacklog.MaximumOldestAge);

    // Runs inside the existing single alarm-maintenance task and command gate.
    // Heartbeats also detect age limits when no lifecycle audit sequence changes.
    private AlarmObservation? ImageBacklogObservationLocked(AlarmPolicy policy)
    {
        if (_imageFinalizationWorker is null || _imageFinalizationFaulted ||
            _productionInspectionPolicy is null || !IsProductionImageBacklogAlarmMappingValid()) return null;
        var healthy = !ProductionImageBacklogExceededLocked();
        if (_alarmObservations.TryGetValue(ImageBacklogAlarmCode, out var received) &&
            received.Healthy == healthy && IsFresh(received, policy)) return null;
        _registeredAlarmSources.Add(ImageIntegrityAlarmSource);
        var sequence = NextAlarmObservationSequenceLocked(ImageBacklogAlarmCode);
        if (healthy && _snapshot.AlarmState?.Instances.Any(instance =>
            instance.Code == ImageBacklogAlarmCode && instance.Lifecycle != AlarmLifecycle.Cleared) != true)
        {
            _alarmObservations[ImageBacklogAlarmCode] = new(_snapshot.RuntimeEpoch, sequence,
                System.Diagnostics.Stopwatch.GetTimestamp(), true);
            return null;
        }
        return new(_snapshot.RuntimeEpoch, sequence,
            ImageBacklogAlarmCode, ImageIntegrityAlarmSource, healthy, DateTimeOffset.UtcNow);
    }

    private async Task HandleImageFinalizationFaultAsync(string reason, bool integrity)
    {
        AlarmObservation observation;
        lock (_sync)
        {
            if (_imageFinalizationFaulted) return;
            _imageFinalizationFaulted = true;
            _productionImageStartupVerified = false;
            PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                Evidence = _snapshot.Evidence with { State = HealthState.Faulted },
                AdmissionBlockers = new AdmissionBlockers(_snapshot.AdmissionBlockers.Append(reason).Distinct(StringComparer.Ordinal)) });
            // Unknown files require reconciliation; unavailable storage and a pending
            // physical retirement are different from proven loss of referenced pixels.
            if (!integrity) return;
            if (!IsProductionImageAlarmMappingValid())
            {
                MarkAuditFault("ProductionImageIntegrityAlarmMappingUnavailable", alarmAuthorityUnavailable: true);
                return;
            }
            _registeredAlarmSources.Add(ImageIntegrityAlarmSource);
            observation = new(_snapshot.RuntimeEpoch, NextAlarmObservationSequenceLocked(ImageIntegrityAlarmCode),
                ImageIntegrityAlarmCode, ImageIntegrityAlarmSource, false, DateTimeOffset.UtcNow);
        }
        var result = await ObserveAlarmAsync(observation).ConfigureAwait(false);
        if (!result.Accepted) MarkAuditFault("ProductionImageIntegrityAlarmUnavailable", alarmAuthorityUnavailable: true);
    }

    private async Task ShutdownImageFinalizationAsync()
    {
        if (_imageFinalizationWorker is { } worker && !await worker.StopAsync().ConfigureAwait(false))
            MarkAuditFault("ImageFinalizationPhysicalRetirementPending");
    }
}
