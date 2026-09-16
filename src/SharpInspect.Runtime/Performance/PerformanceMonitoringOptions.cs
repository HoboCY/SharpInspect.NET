using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;

namespace SharpInspect.Runtime.Performance;

/// <summary>Deployment of the fixed monitor. An optional finite baseline is observability, never a production permit.</summary>
public sealed class PerformanceMonitoringOptions
{
    public PerformanceMonitoringOptions(PerformanceContract contract, string deploymentDeclarationHash,
        int healthyCyclesToClear, string? baselineScenarioId = null)
    {
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
        DeploymentDeclarationHash = PerformanceContractValue.ContentHash(deploymentDeclarationHash);
        if (healthyCyclesToClear is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(healthyCyclesToClear));
        if (baselineScenarioId is not null && !contract.Scenarios.Any(value => value.Id == baselineScenarioId))
            throw new ArgumentException("PerformanceBaselineScenarioUnknown");
        if (contract.CollectorId != PerformanceCollectorDescription.Id ||
            contract.CollectorVersion != PerformanceCollectorDescription.Version ||
            contract.CollectorHash != PerformanceCollectorDescription.ContentHash)
            throw new ArgumentException("PerformanceCollectorContractMismatch");
        HealthyCyclesToClear = healthyCyclesToClear; BaselineScenarioId = baselineScenarioId;
        BindingHash = ProductionAdmissionCanonical.Hash("performance-monitoring-deployment-v1",
            contract.ContentHash, DeploymentDeclarationHash, healthyCyclesToClear.ToString(System.Globalization.CultureInfo.InvariantCulture),
            baselineScenarioId is null ? "BaselineDisabled" : "BaselineEnabled", PerformanceCollectorDescription.ContentHash);
    }
    public PerformanceContract Contract { get; }
    public string DeploymentDeclarationHash { get; }
    public int HealthyCyclesToClear { get; }
    public string? BaselineScenarioId { get; }
    public string BindingHash { get; }
}

/// <summary>The frozen calculation and collector semantics; actual assembly identities are recorded separately.</summary>
public static class PerformanceCollectorDescription
{
    public const string Id = "SharpInspect.Runtime.Performance";
    public const string Version = "1";
    public const string CalculationRules = "nearest-rank: sorted successful complete durations, index ceil(p*n)-1; " +
        "jitter: observed max-min; empty statistics: null; warmup: first configured accepted ordinals including failures; " +
        "expected failures remain in failure counts and maximum-failure gates; duration includes warmup and recovery; " +
        "ordinal is assigned at durable admission, never renumbered by successful outcomes; " +
        "growth is observed last-first, peak budget also applies; allocation per cycle is process-wide interval delta, not exclusive ownership; " +
        "counter deltas are nonnegative, a reset is unknown; captured events are never overwritten; " +
        "baseline cannot satisfy any production qualification; full mix requires every frozen scenario id exactly once; " +
        "only finite baseline capture hashes valid input rows under MaximumFrameHashBytes; hashing overhead is included in full-cycle latency; " +
        "frame hashes use the VirtualCamera valid-pixel hash v1 framing, so dimensions, format and valid bits are covered.";
    public const string ResourceRules = "CPU: process TotalProcessorTime delta / monotonic interval / logical processors * 100; " +
        "WorkingSetBytes/PrivateBytes: Process counters; managed heap/committed: last completed GC GCMemoryInfo, not a forced collection; " +
        "NativeMemoryBytes: Unknown (no exact native allocator collector); NonGcPrivateBytesEstimate: max(0,PrivateBytes-GC.TotalCommittedBytes), proxy only; " +
        "allocation: GC.GetTotalAllocatedBytes(false); collections: GC.CollectionCount; GC pause: sum last completed GC PauseDurations, each GC index once; " +
        "threads/handles: Process counters; frame/image/outbox/diagnostic queues: fixed Runtime owners; SQLite/WAL: file lengths, writer and checkpoint counters; " +
        "WPF: explicitly registered read-only process-wide counters; no registration is Unknown, not zero; success counts and complete bytes exclude failed work, elapsed includes failures with separate failure counters; " +
        "cadence: absolute interval deviation <= frozen MaximumCadenceDeviation; null is unproven and fails; inside each BurstSize group use BurstInterval, between groups use TriggerInterval; " +
        "durations in milliseconds, memory in bytes, counts dimensionless.";
    public static string ContentHash { get; } = ProductionAdmissionCanonical.Hash("performance-collector-contract-v1",
        Id, Version, CalculationRules, ResourceRules);
}
