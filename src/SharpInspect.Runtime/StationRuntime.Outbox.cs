using SharpInspect.Abstractions;
using SharpInspect.Runtime.Alarms;
using SharpInspect.Runtime.Outbox;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private const string OutboxRequiredAlarmCode = "OutboxRequiredDeliveryBlocked";
    private const string OutboxBestEffortAlarmCode = "OutboxBestEffortDeliveryFailed";
    private const string OutboxAlarmSource = "Runtime.Outbox";
    private ProductionOutboxWorker? _outboxWorker;
    private ProductionStoreOptions? _outboxStoreOptions;
    private ProductionOutboxOptions? _outboxTransports;
    private OutboxBacklogSnapshot _outboxBacklog = new(0, Array.Empty<OutboxRouteBacklog>());
    private readonly Dictionary<Guid, (long Sequence, FrozenOutboxBatch Batch)> _outboxUnobservedCores = new();
    private bool _outboxFaulted;

    internal void ConfigureOutbox(ProductionStoreOptions options, ProductionOutboxOptions? transports)
    {
        if (options.Outbox is null)
        {
            if (transports is { Transports.Count: > 0 }) throw new ArgumentException("OutboxStoreConfigurationRequired");
            return;
        }
        if (_outboxWorker is not null) throw new InvalidOperationException("OutboxAlreadyConfigured");
        if (_audit is not SqliteCommandStore store) throw new ArgumentException("OutboxSqliteStoreRequired");
        options.Outbox.Validate();
        _outboxStoreOptions = options;
        _outboxTransports = transports ?? new(Array.Empty<OutboxTransportBinding>());
        if (_outboxTransports.Transports.Any(binding => !options.Outbox.Routes.Any(route =>
                route.ContentHash == binding.Route.ContentHash)))
            throw new ArgumentException("OutboxTransportNotInDeploymentRoutes", nameof(transports));
        _outboxWorker = new(store, options, _outboxTransports, _snapshot.RuntimeEpoch, _storeInitialization,
            ProjectOutboxBacklog, HandleOutboxFaultAsync);
    }

    private void ProjectOutboxBacklog(OutboxBacklogSnapshot backlog)
    {
        lock (_sync)
        {
            if (_disposed || backlog.ThroughAuditSequence < _outboxBacklog.ThroughAuditSequence) return;
            _outboxBacklog = backlog;
            // 以审计水位消除本地补计，确保刚提交的 Core 在后台扫描追上前也计入积压，追上后又不重复计算。
            foreach (var key in _outboxUnobservedCores.Where(value => value.Value.Sequence <= backlog.ThroughAuditSequence)
                         .Select(value => value.Key).ToArray()) _outboxUnobservedCores.Remove(key);
            PublishLocked(_snapshot with { Outbox = CurrentOutboxBacklogLocked() });
        }
    }

    private void ProjectCommittedOutbox(FrozenOutboxBatch? batch, Guid inspectionId, long auditSequence)
    {
        if (batch is null) return;
        lock (_sync)
        {
            if (auditSequence > _outboxBacklog.ThroughAuditSequence)
                _outboxUnobservedCores[inspectionId] = (auditSequence, batch);
            PublishLocked(_snapshot with { Outbox = CurrentOutboxBacklogLocked() });
        }
        _outboxWorker?.Wake();
    }

    private OutboxBacklogSnapshot CurrentOutboxBacklogLocked()
    {
        var rows = _outboxBacklog.Routes.ToDictionary(value => value.RouteId, StringComparer.OrdinalIgnoreCase);
        foreach (var route in _outboxStoreOptions?.Outbox?.Routes ?? Enumerable.Empty<OutboxRouteDefinition>())
            if (!rows.ContainsKey(route.RouteId)) rows.Add(route.RouteId, new(route.RouteId, route.ContentHash,
                route.Criticality, 0, 0, null, false));
        foreach (var delivery in _outboxUnobservedCores.Values.SelectMany(value => value.Batch.Deliveries))
        {
            var old = rows[delivery.Route.RouteId];
            rows[delivery.Route.RouteId] = old with
            {
                PendingCount = checked(old.PendingCount + 1),
                PendingBytes = checked(old.PendingBytes + (delivery.Payload?.ByteLength ?? 0)),
                OldestCreatedAtUtc = old.OldestCreatedAtUtc is { } oldest && oldest < delivery.CreatedAtUtc
                    ? oldest : delivery.CreatedAtUtc,
                PermanentBlock = old.PermanentBlock || delivery.PreparationFailure is not null,
                FailedCount = checked(old.FailedCount + (delivery.PreparationFailure is null ? 0 : 1))
            };
        }
        return new(_outboxBacklog.ThroughAuditSequence, rows.Values.OrderBy(value => value.RouteId, StringComparer.Ordinal).ToArray());
    }

    private bool ProductionOutboxStartupReadyLocked() => _productionInspectionStoreOptions?.Outbox is null ||
        !_outboxFaulted && _outboxWorker?.Startup.IsCompletedSuccessfully == true;

    private bool ProductionOutboxConfiguredLocked()
    {
        var options = _productionInspectionStoreOptions?.Outbox;
        if (options is null) return true;
        if (_outboxTransports is null || !ProductionOutboxStartupReadyLocked()) return false;
        return options.Routes.All(route => route.Criticality == OutboxRouteCriticality.BestEffort ||
            _outboxTransports.Resolve(route)?.CapabilityHash is not null) &&
            (!options.Routes.Any(route => route.Criticality == OutboxRouteCriticality.Required) ||
                IsOutboxAlarmMappingValid(OutboxRequiredAlarmCode, ProductionImpact.BlockNewTriggers)) &&
            (!options.Routes.Any(route => route.Criticality == OutboxRouteCriticality.BestEffort) ||
                IsOutboxAlarmMappingValid(OutboxBestEffortAlarmCode, ProductionImpact.None));
    }

    private string? ProductionOutboxBacklogFailureLocked()
    {
        if (_productionInspectionStoreOptions?.Outbox is null) return null;
        if (!ProductionOutboxConfiguredLocked()) return "OutboxRuntimeConfigurationUnavailable";
        if (_productionInspectionPolicy is not { } policy) return "OutboxTracePolicyUnavailable";
        return ProductionOutboxBinding.RequiredBacklogFailure(policy, CurrentOutboxBacklogLocked(), DateTimeOffset.UtcNow);
    }

    private bool IsOutboxAlarmMappingValid(string code, ProductionImpact impact) => ConfiguredAlarmPolicy is { } policy &&
        policy.TryGetRule(code, out var rule) && rule is not null && rule.Source == OutboxAlarmSource &&
        rule.ProductionImpact == impact;

    private AlarmSummary OutboxAdmissionAlarmSummaryLocked(StationStateSnapshot state)
    {
        if (_outboxStoreOptions?.Outbox is not { } options ||
            !options.Routes.Any(route => route.Criticality == OutboxRouteCriticality.BestEffort) ||
            state.AlarmState is not { Available: true, Policy: { } policy } alarms ||
            ConfiguredAlarmPolicy?.ContentHash != policy.ContentHash) return state.Alarms;
        var excluded = alarms.Instances.Where(instance => OutboxAlarmMaterial.IsNonBlockingInstance(policy, instance)).ToArray();
        return new(state.Alarms.ActiveCount - excluded.Count(instance => instance.Lifecycle == AlarmLifecycle.Active),
            state.Alarms.LatchedCount - excluded.Count(instance => instance.IsLatched), state.Alarms.BlocksProduction);
    }

    // 在串行报警维护循环中检查，保证没有新账本事件时也能发现积压超龄。
    private IReadOnlyList<AlarmObservation> OutboxObservationsLocked(AlarmPolicy policy)
    {
        if (_outboxWorker is null || _outboxFaulted || _productionInspectionPolicy is null)
            return Array.Empty<AlarmObservation>();
        var rows = CurrentOutboxBacklogLocked();
        var observations = new List<AlarmObservation>(2);
        Observe(OutboxRequiredAlarmCode, ProductionImpact.BlockNewTriggers,
            ProductionOutboxBinding.RequiredBacklogFailure(_productionInspectionPolicy, rows, DateTimeOffset.UtcNow) is null);
        Observe(OutboxBestEffortAlarmCode, ProductionImpact.None,
            !rows.Routes.Any(route => route.Criticality == OutboxRouteCriticality.BestEffort &&
                (route.PermanentBlock || route.FailedCount > 0)));
        return observations;

        void Observe(string code, ProductionImpact impact, bool healthy)
        {
            if (!IsOutboxAlarmMappingValid(code, impact)) return;
            if (_alarmObservations.TryGetValue(code, out var received) && received.Healthy == healthy &&
                IsFresh(received, policy)) return;
            _registeredAlarmSources.Add(OutboxAlarmSource);
            var sequence = NextAlarmObservationSequenceLocked(code);
            if (healthy && _snapshot.AlarmState?.Instances.Any(instance =>
                    instance.Code == code && instance.Lifecycle != AlarmLifecycle.Cleared) != true)
            {
                _alarmObservations[code] = new(_snapshot.RuntimeEpoch, sequence,
                    System.Diagnostics.Stopwatch.GetTimestamp(), true);
                return;
            }
            observations.Add(new(_snapshot.RuntimeEpoch, sequence, code, OutboxAlarmSource, healthy, DateTimeOffset.UtcNow));
        }
    }

    private Task HandleOutboxFaultAsync(string reason)
    {
        lock (_sync)
        {
            _outboxFaulted = true;
            PublishLocked(_snapshot with { Ready = false, Evidence = _snapshot.Evidence with { State = HealthState.Faulted },
                AdmissionBlockers = new AdmissionBlockers(_snapshot.AdmissionBlockers.Append(reason).Distinct(StringComparer.Ordinal)) });
        }
        return Task.CompletedTask;
    }

    private async Task ShutdownOutboxAsync()
    {
        if (_outboxWorker is { } worker && !await worker.StopAsync().ConfigureAwait(false))
            await HandleOutboxFaultAsync("OutboxPhysicalRetirementPending").ConfigureAwait(false);
    }
}
