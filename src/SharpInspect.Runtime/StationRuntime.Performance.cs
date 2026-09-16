using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Alarms;
using SharpInspect.Runtime.Performance;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private const string PerformanceAlarmCode = "PerformanceBudgetViolation";
    private const string PerformanceAlarmSource = "Runtime.Performance";
    private RuntimePerformanceMonitor? _performance;
    internal IPerformanceMonitoringQuery PerformanceQuery => (IPerformanceMonitoringQuery?)_performance ?? UnconfiguredPerformanceQuery.Instance;

    private void ConfigurePerformance(ProductionStoreOptions? options, IPresentationPerformanceQuery? presentation)
    {
        if (options?.PerformanceMonitoring is not { } configured) return;
        if (options.AlarmPolicy is not { } policy || !policy.TryGetRule(PerformanceAlarmCode, out var rule) ||
            rule?.Source != PerformanceAlarmSource || rule.ProductionImpact is not (ProductionImpact.None or ProductionImpact.BlockNewTriggers))
            throw new ArgumentException("PerformanceAlarmPolicyMappingInvalid");
        PerformanceRunHeader? header = null;
        if (configured.BaselineScenarioId is { } scenarioId)
        {
            var scenario = configured.Contract.Scenarios.Single(value => value.Id == scenarioId);
            header = new(Guid.NewGuid(), _snapshot.RuntimeEpoch, configured.Contract.ContentHash, scenarioId,
                scenario.ContentHash, Stopwatch.Frequency, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow,
                ProductionConfigurationBuilder.AssemblyHash(typeof(StationRuntime).Assembly),
                ProductionConfigurationBuilder.AssemblyHash(typeof(IStationRuntime).Assembly),
                typeof(StationRuntime).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Unknown",
                RuntimeInformation.ProcessArchitecture.ToString(), ProductionConfigurationBuilder.ReadPowerPlanHash(),
                Debugger.IsAttached, Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING") == "1" ||
                    Environment.GetEnvironmentVariable("COR_ENABLE_PROFILING") == "1",
                options.LoggingDiagnostics?.BindingHash, PerformanceCollectorDescription.Id,
                PerformanceCollectorDescription.Version, PerformanceCollectorDescription.ContentHash);
        }
        var monitor = _performance = new(configured, _snapshot.RuntimeEpoch, header);
        var sampler = new PerformanceResourceSampler(options.DatabasePath, _frameBufferPool, presentation,
            ReadPerformanceRuntimeResources);
        monitor.StartResources(sampler.Read, header is null ? null :
            (endedAt, events, samples, frames, bindings) => SealPerformanceCaptureAsync(monitor, options, header, endedAt, events, samples, frames, bindings));
    }

    private async Task<PerformanceRawCapture> SealPerformanceCaptureAsync(RuntimePerformanceMonitor monitor,
        ProductionStoreOptions options, PerformanceRunHeader header, long endedAt,
        PerformanceEvent[] events, PerformanceResourceSample[] samples, PerformanceFrameObservation[] frames,
        PerformanceCycleBinding[] bindings)
    {
        var snapshot = monitor.ReadPerformance();
        var firstMissing = monitor.FirstMissingSequence;
        var facts = new List<PerformanceDurableFact>();
        var available = false;
        var reason = "PerformanceLedgerUnavailable";
        try
        {
            using var timeout = new CancellationTokenSource(options.PerformanceMonitoring!.Contract.Capture.ShutdownTimeout);
            var query = new SqliteProductionInspectionHistoryQuery(options);
            long after = 0;
            long? through = null;
            while (true)
            {
                var page = await query.QueryAsync(new(RuntimeEpoch: header.RuntimeEpoch, AfterPosition: after,
                    ThroughPosition: through, PageSize: 128), timeout.Token).ConfigureAwait(false);
                if (!page.Available) { reason = page.ReasonCode; break; }
                through ??= page.ThroughPosition;
                foreach (var item in page.Events)
                {
                    if (item.MonotonicTimestamp < header.StartedAt || item.MonotonicTimestamp > endedAt) continue;
                    if (facts.Count >= options.PerformanceMonitoring.Contract.Capture.MaximumEvents)
                        throw new InvalidOperationException("PerformanceLedgerCapacityExceeded");
                    facts.Add(new(item.Position, item.RuntimeEpoch, item.InspectionId, item.CorrelationId,
                        item.Admission.ControllerCycle.ControllerEpoch, item.Admission.ControllerCycle.CycleSequence,
                        item.Kind, item.MonotonicTimestamp, item.ContentHash, item.Core?.ContentHash,
                        item.Core?.PlcPayload?.ContentHash, item.Core?.ExecutionStatus, item.Core?.Decision,
                        item.Core?.ReasonCode ?? item.ReasonCode));
                }
                if (page.NextAfterPosition is not { } next) { available = true; reason = "PerformanceLedgerVerified"; break; }
                if (next <= after) throw new InvalidOperationException("PerformanceLedgerCursorInvalid");
                after = next;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { reason = exception is OperationCanceledException ? "PerformanceLedgerReadTimeout" : "PerformanceLedgerReadFailed"; }
        var complete = snapshot.ObservationLossCount == 0 && snapshot.PhysicalResourceOperations == 0 && available &&
            !monitor.CaptureRetirementIncomplete;
        return new(options.PerformanceMonitoring!.Contract, header,
            complete ? PerformanceEvidenceState.Sealed : PerformanceEvidenceState.Incomplete, endedAt,
            snapshot.ObservationLossCount, firstMissing,
            complete ? "PerformanceCaptureSealed" : monitor.CaptureRetirementIncomplete ? "PerformanceCaptureProducerRetirementUnverified" :
                "PerformanceCaptureIncomplete", events, samples, facts, available, reason, frames, bindings);
    }

    private IReadOnlyList<PerformanceResourceValue> ReadPerformanceRuntimeResources()
    {
        var values = new List<PerformanceResourceValue>(12);
        void Add(PerformanceResource resource, double value) => values.Add(PerformanceResourceSampler.Measured(resource, value));
        lock (_sync)
        {
            if (_audit is SqliteCommandStore store)
            {
                if (store.PerformanceReservedWrites is { } writes) Add(PerformanceResource.SqliteQueuedWrites, writes);
                Add(PerformanceResource.SqliteCheckpointCount, store.PerformanceCheckpointAttempts);
            }
            if (_productionInspectionStoreOptions?.ImageEvidence is not null)
            {
                Add(PerformanceResource.ImagePendingCount, _productionPendingImages);
                Add(PerformanceResource.ImageActiveOperations, (_productionImageStager?.ActiveOperationCount ?? 0) +
                    (_imageFinalizationWorker?.PerformanceActiveOperations ?? 0));
            }
            if (_outboxStoreOptions?.Outbox is not null)
            {
                Add(PerformanceResource.OutboxPendingCount, CurrentOutboxBacklogLocked().Routes.Sum(value => value.PendingCount));
                if (_outboxWorker is not null) Add(PerformanceResource.OutboxActiveOperations, _outboxWorker.PerformanceActiveOperations);
            }
        }
        if (_diagnostics?.ReadHealth() is { Configured: true } diagnostics)
        {
            var lanes = new[] { diagnostics.Safe, diagnostics.Protected, diagnostics.Forwarded };
            Add(PerformanceResource.DiagnosticQueuedEvents, lanes.Sum(value => value?.QueuedRecords ?? 0));
            Add(PerformanceResource.DiagnosticQueuedBytes, lanes.Sum(value => value?.QueuedBytes ?? 0));
            Add(PerformanceResource.DiagnosticDrops, diagnostics.Rejected + diagnostics.QuotaDropped +
                diagnostics.SaturationDropped + diagnostics.LateDropped + lanes.Sum(value => value?.Dropped ?? 0));
        }
        if (_performance?.LastCycleAllocation is { } allocation) Add(PerformanceResource.AllocatedBytesPerCycle, allocation);
        return values;
    }

    private bool PerformanceConfigurationMatches() => _productionInspectionStoreOptions?.PerformanceMonitoring is { } performance &&
        _performance is not null && _productionInspectionOptions?.Deployment?.Performance.ContentHash == performance.DeploymentDeclarationHash;

    private bool PerformanceBlocksNewTriggersLocked() => _performance is { } performance &&
        (performance.BudgetViolated || performance.ObservationLossCount != 0) &&
        ConfiguredAlarmPolicy?.TryGetRule(PerformanceAlarmCode, out var rule) == true &&
        rule?.Source == PerformanceAlarmSource && rule.ProductionImpact == ProductionImpact.BlockNewTriggers;

    private string PerformanceAdmissionMaterialLocked(StationStateSnapshot state) => _performance is null
        ? state.Performance.ToString() : PerformanceBlocksNewTriggersLocked()
            ? "PerformancePolicyBlocksNewTriggers" : "PerformancePolicyAllowsNewTriggers";

    private AlarmSummary PerformanceAdmissionAlarmSummaryLocked(StationStateSnapshot state)
    {
        var prior = OutboxAdmissionAlarmSummaryLocked(state);
        if (_performance is null || state.AlarmState is not { Available: true, Policy: { } policy } alarms ||
            ConfiguredAlarmPolicy?.ContentHash != policy.ContentHash ||
            !policy.TryGetRule(PerformanceAlarmCode, out var rule) || rule?.Source != PerformanceAlarmSource ||
            rule.ProductionImpact != ProductionImpact.None) return prior;
        var excluded = alarms.Instances.Where(value => PerformanceAlarmMaterial.IsNonBlockingInstance(policy, value)).ToArray();
        return new(prior.ActiveCount - excluded.Count(value => value.Lifecycle == AlarmLifecycle.Active),
            prior.LatchedCount - excluded.Count(value => value.IsLatched), prior.BlocksProduction);
    }

    private StationStateSnapshot ProjectPerformanceLocked(StationStateSnapshot state)
    {
        if (_performance is null) return state;
        var value = _performance.ReadPerformance();
        return state with { PerformanceMonitoring = value,
            Performance = new(value.ObservationLossCount != 0 ? HealthState.Degraded : value.BudgetViolation ?
                HealthState.Degraded : value.ReasonCode == "PerformanceObservationPending" ? HealthState.Unknown : HealthState.Healthy,
                value.BudgetViolation) };
    }

    private AlarmObservation? PerformanceObservationLocked(AlarmPolicy policy)
    {
        if (_performance is null || !policy.TryGetRule(PerformanceAlarmCode, out var rule) || rule?.Source != PerformanceAlarmSource)
            return null;
        var healthy = !_performance.BudgetViolated && _performance.ObservationLossCount == 0;
        if (_alarmObservations.TryGetValue(PerformanceAlarmCode, out var prior) && prior.Healthy == healthy && IsFresh(prior, policy)) return null;
        _registeredAlarmSources.Add(PerformanceAlarmSource);
        var sequence = NextAlarmObservationSequenceLocked(PerformanceAlarmCode);
        if (healthy && _snapshot.AlarmState?.Instances.Any(instance => instance.Code == PerformanceAlarmCode &&
                instance.Lifecycle != AlarmLifecycle.Cleared) != true)
        {
            _alarmObservations[PerformanceAlarmCode] = new(_snapshot.RuntimeEpoch, sequence, Stopwatch.GetTimestamp(), true);
            return null;
        }
        return new(_snapshot.RuntimeEpoch, sequence, PerformanceAlarmCode, PerformanceAlarmSource, healthy, DateTimeOffset.UtcNow);
    }

    private sealed class UnconfiguredPerformanceQuery : IPerformanceMonitoringQuery
    {
        internal static readonly UnconfiguredPerformanceQuery Instance = new();
        public PerformanceMonitorSnapshot ReadPerformance() => new(false, null, false, 0, 0, 0, null, null, 0, 0, 0,
            "PerformanceMonitoringNotConfigured");
        public ValueTask<PerformanceRawCapture?> ReadCaptureAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<PerformanceRawCapture?>(null); }
    }
}
