using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

public sealed class PerformanceSpanBudget
{
    public PerformanceSpanBudget(PerformanceSpan span, bool applicable, string applicability,
        int minimumSamples, int maximumFailures, double? p50Milliseconds, double? p95Milliseconds,
        double? p99Milliseconds, double? observedMaxMilliseconds, double? jitterMilliseconds)
    {
        if (!Enum.IsDefined(span) || minimumSamples < 0 || maximumFailures < 0)
            throw new ArgumentException("PerformanceSpanBudgetInvalid");
        Span = span; Applicable = applicable;
        Applicability = PerformanceContractValue.Text(applicability);
        var limits = new[] { p50Milliseconds, p95Milliseconds, p99Milliseconds, observedMaxMilliseconds, jitterMilliseconds };
        if (applicable && (minimumSamples < 1 || limits.Any(value => value is null || !double.IsFinite(value.Value) || value < 0)) ||
            !applicable && (minimumSamples != 0 || maximumFailures != 0 || limits.Any(value => value is not null)))
            throw new ArgumentException("PerformanceExplicitSpanThresholdsRequired");
        if (applicable && (p50Milliseconds > p95Milliseconds || p95Milliseconds > p99Milliseconds ||
                p99Milliseconds > observedMaxMilliseconds || jitterMilliseconds > observedMaxMilliseconds))
            throw new ArgumentException("PerformanceSpanThresholdOrderInvalid");
        MinimumSamples = minimumSamples; MaximumFailures = maximumFailures;
        P50Milliseconds = p50Milliseconds; P95Milliseconds = p95Milliseconds;
        P99Milliseconds = p99Milliseconds; ObservedMaxMilliseconds = observedMaxMilliseconds; JitterMilliseconds = jitterMilliseconds;
        ContentHash = PerformanceContractValue.Hash("performance-span-budget-v1", span, applicable, Applicability,
            minimumSamples, maximumFailures, p50Milliseconds, p95Milliseconds, p99Milliseconds, observedMaxMilliseconds, jitterMilliseconds);
    }
    public PerformanceSpan Span { get; }
    public bool Applicable { get; }
    public string Applicability { get; }
    public int MinimumSamples { get; }
    public int MaximumFailures { get; }
    public double? P50Milliseconds { get; }
    public double? P95Milliseconds { get; }
    public double? P99Milliseconds { get; }
    public double? ObservedMaxMilliseconds { get; }
    public double? JitterMilliseconds { get; }
    public string ContentHash { get; }
}

public sealed class PerformanceResourceBudget
{
    public PerformanceResourceBudget(PerformanceResource resource, bool required, string applicability,
        int minimumSamples, double? maximum, double? maximumGrowth)
    {
        if (!Enum.IsDefined(resource) || minimumSamples < 0 || required && minimumSamples < 1 ||
            required && (maximum is null || maximumGrowth is null) ||
            new[] { maximum, maximumGrowth }.Any(value => value is { } number && (!double.IsFinite(number) || number < 0)) ||
            !required && (minimumSamples != 0 || maximum is not null || maximumGrowth is not null))
            throw new ArgumentException("PerformanceResourceBudgetInvalid");
        Resource = resource; Required = required; Applicability = PerformanceContractValue.Text(applicability);
        MinimumSamples = minimumSamples; Maximum = maximum; MaximumGrowth = maximumGrowth;
        ContentHash = PerformanceContractValue.Hash("performance-resource-budget-v1", resource, required,
            Applicability, minimumSamples, maximum, maximumGrowth);
    }
    public PerformanceResource Resource { get; }
    public bool Required { get; }
    public string Applicability { get; }
    public int MinimumSamples { get; }
    public double? Maximum { get; }
    /// <summary>Observed last minus first; cumulative counters require an explicit permitted delta.</summary>
    public double? MaximumGrowth { get; }
    public string ContentHash { get; }
}

public sealed class PerformanceExpectedFailure
{
    public PerformanceExpectedFailure(int cycleOrdinal, string reasonCode)
    {
        if (cycleOrdinal < 1) throw new ArgumentOutOfRangeException(nameof(cycleOrdinal));
        CycleOrdinal = cycleOrdinal; ReasonCode = AlgorithmContractValidation.Identifier(reasonCode, nameof(reasonCode));
    }
    public int CycleOrdinal { get; }
    public string ReasonCode { get; }
}

/// <summary>One frozen scenario. Warm-up observations are retained; only their statistics are excluded.</summary>
public sealed class PerformanceScenario
{
    public PerformanceScenario(string id, PerformanceScenarioKind kind, string datasetHash, string frameManifestHash,
        string stimulusHash, int measuredCycles, int warmupCycles, TimeSpan minimumDuration,
        TimeSpan maximumDuration, TimeSpan triggerInterval, int burstSize, TimeSpan burstInterval,
        int requiredStops, int requiredRecoveries, IEnumerable<PerformanceExpectedFailure> expectedFailures,
        IEnumerable<string?> expectedFrameHashes, TimeSpan? maximumCadenceDeviation = null)
    {
        Id = AlgorithmContractValidation.Identifier(id, nameof(id));
        if (!Enum.IsDefined(kind) || measuredCycles is < 1 or > 100_000 || warmupCycles is < 0 or > 100_000 ||
            measuredCycles + warmupCycles > 100_000 || minimumDuration <= TimeSpan.Zero ||
            maximumDuration < minimumDuration || maximumDuration > TimeSpan.FromDays(30) ||
            triggerInterval <= TimeSpan.Zero || triggerInterval > maximumDuration || burstSize is < 1 or > 100_000 ||
            burstInterval < TimeSpan.Zero || burstInterval > maximumDuration || burstSize > 1 && burstInterval <= TimeSpan.Zero ||
            requiredStops is < 0 or > 100_000 || requiredRecoveries is < 0 or > 100_000 ||
            kind == PerformanceScenarioKind.Recovery && (requiredStops < 1 || requiredRecoveries < 1))
            throw new ArgumentException("PerformanceScenarioInvalid");
        if (maximumCadenceDeviation is { } deviation && (deviation < TimeSpan.Zero || deviation > maximumDuration))
            throw new ArgumentException("PerformanceCadenceDeviationInvalid");
        MaximumCadenceDeviation = maximumCadenceDeviation;
        Kind = kind; DatasetHash = PerformanceContractValue.ContentHash(datasetHash);
        FrameManifestHash = PerformanceContractValue.ContentHash(frameManifestHash);
        StimulusHash = PerformanceContractValue.ContentHash(stimulusHash);
        MeasuredCycles = measuredCycles; WarmupCycles = warmupCycles; MinimumDuration = minimumDuration;
        MaximumDuration = maximumDuration; TriggerInterval = triggerInterval; BurstSize = burstSize;
        BurstInterval = burstInterval; RequiredStops = requiredStops; RequiredRecoveries = requiredRecoveries;
        var failures = AlgorithmContractValidation.Copy(expectedFailures, nameof(expectedFailures), 100_000);
        if (failures.Any(value => value.CycleOrdinal > measuredCycles + warmupCycles) ||
            failures.Select(value => value.CycleOrdinal).Distinct().Count() != failures.Count)
            throw new ArgumentException("PerformanceExpectedFailureInvalid");
        ExpectedFailures = Array.AsReadOnly(failures.OrderBy(value => value.CycleOrdinal).ToArray());
        ArgumentNullException.ThrowIfNull(expectedFrameHashes);
        var frameHashes = expectedFrameHashes.Take(100_001).Select(value => value is null ? null : PerformanceContractValue.ContentHash(value)).ToArray();
        if (frameHashes.Length != measuredCycles + warmupCycles || frameHashes.Where((value, index) => value is null &&
                !ExpectedFailures.Any(failure => failure.CycleOrdinal == index + 1)).Any() ||
            PerformanceFrameManifest.ComputeHash(frameHashes) != FrameManifestHash)
            throw new ArgumentException("PerformanceFrozenFrameManifestInvalid");
        ExpectedFrameHashes = Array.AsReadOnly(frameHashes);
        ContentHash = PerformanceContractValue.Hash("performance-scenario-v1", Id, kind, DatasetHash,
            FrameManifestHash, StimulusHash, measuredCycles, warmupCycles, minimumDuration.Ticks, maximumDuration.Ticks,
            triggerInterval.Ticks, burstSize, burstInterval.Ticks, requiredStops, requiredRecoveries, maximumCadenceDeviation?.Ticks,
            PerformanceContractValue.Hash("performance-expected-failures-v1", ExpectedFailures.Select(value =>
                PerformanceContractValue.Hash("expected-failure-v1", value.CycleOrdinal, value.ReasonCode)).Cast<object?>().ToArray()));
    }
    public string Id { get; }
    public PerformanceScenarioKind Kind { get; }
    public string DatasetHash { get; }
    public string FrameManifestHash { get; }
    public string StimulusHash { get; }
    public int MeasuredCycles { get; }
    public int WarmupCycles { get; }
    public TimeSpan MinimumDuration { get; }
    public TimeSpan MaximumDuration { get; }
    public TimeSpan TriggerInterval { get; }
    public int BurstSize { get; }
    public TimeSpan BurstInterval { get; }
    /// <summary>Frozen absolute deviation allowed per trigger interval. Null leaves cadence unproven and fails the report.</summary>
    public TimeSpan? MaximumCadenceDeviation { get; }
    public int RequiredStops { get; }
    public int RequiredRecoveries { get; }
    public ReadOnlyCollection<PerformanceExpectedFailure> ExpectedFailures { get; }
    public ReadOnlyCollection<string?> ExpectedFrameHashes { get; }
    public string ContentHash { get; }
}

public sealed class PerformanceCaptureBounds
{
    public PerformanceCaptureBounds(int maximumEvents, int maximumResourceSamples, long maximumBytes,
        TimeSpan resourceSampleInterval, TimeSpan resourceSampleTimeout, TimeSpan shutdownTimeout, int maximumFrameHashBytes)
    {
        if (maximumEvents is < 32 or > 1_000_000 || maximumResourceSamples is < 2 or > 100_000 ||
            maximumBytes is < 4096 or > 256 * 1024 * 1024 || resourceSampleInterval < TimeSpan.FromMilliseconds(10) ||
            resourceSampleInterval > TimeSpan.FromHours(1) || resourceSampleTimeout <= TimeSpan.Zero ||
            resourceSampleTimeout > TimeSpan.FromMinutes(1) || shutdownTimeout <= TimeSpan.Zero || shutdownTimeout > TimeSpan.FromSeconds(30) ||
            maximumFrameHashBytes is < 1 or > 64 * 1024 * 1024)
            throw new ArgumentException("PerformanceCaptureBoundsInvalid");
        MaximumEvents = maximumEvents; MaximumResourceSamples = maximumResourceSamples; MaximumBytes = maximumBytes;
        ResourceSampleInterval = resourceSampleInterval; ResourceSampleTimeout = resourceSampleTimeout; ShutdownTimeout = shutdownTimeout;
        MaximumFrameHashBytes = maximumFrameHashBytes;
        ContentHash = PerformanceContractValue.Hash("performance-capture-bounds-v1", maximumEvents, maximumResourceSamples,
            maximumBytes, resourceSampleInterval.Ticks, resourceSampleTimeout.Ticks, shutdownTimeout.Ticks, maximumFrameHashBytes);
    }
    public int MaximumEvents { get; }
    public int MaximumResourceSamples { get; }
    public long MaximumBytes { get; }
    public TimeSpan ResourceSampleInterval { get; }
    public TimeSpan ResourceSampleTimeout { get; }
    public TimeSpan ShutdownTimeout { get; }
    public int MaximumFrameHashBytes { get; }
    public string ContentHash { get; }
}

/// <summary>Immutable engineering targets and frozen measurement rules, without production qualification authority.</summary>
public sealed class PerformanceContract
{
    public PerformanceContract(string id, string version, PerformancePercentileRule percentileRule,
        PerformanceJitterRule jitterRule, IEnumerable<PerformanceScenario> scenarios,
        IEnumerable<PerformanceSpanBudget> spans, IEnumerable<PerformanceResourceBudget> resources,
        PerformanceCaptureBounds capture, string collectorId, string collectorVersion, string collectorHash,
        string buildConfiguration, string processArchitecture, string powerProfileHash, bool debuggerPresent, bool profilerPresent,
        string diagnosticProfileHash, string approvalRule)
    {
        Id = AlgorithmContractValidation.Identifier(id, nameof(id)); Version = AlgorithmContractValidation.Identifier(version, nameof(version));
        if (!Enum.IsDefined(percentileRule) || !Enum.IsDefined(jitterRule)) throw new ArgumentException("PerformanceCalculationRuleInvalid");
        PercentileRule = percentileRule; JitterRule = jitterRule;
        var copiedScenarios = AlgorithmContractValidation.Copy(scenarios, nameof(scenarios), 64);
        if (copiedScenarios.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != copiedScenarios.Count ||
            Enum.GetValues<PerformanceScenarioKind>().Any(kind => !copiedScenarios.Any(value => value.Kind == kind)))
            throw new ArgumentException("PerformanceCompleteScenarioMixRequired");
        Scenarios = Array.AsReadOnly(copiedScenarios.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray());
        var copiedSpans = AlgorithmContractValidation.Copy(spans, nameof(spans), 32);
        if (copiedSpans.Count != Enum.GetValues<PerformanceSpan>().Length ||
            copiedSpans.Select(value => value.Span).Distinct().Count() != copiedSpans.Count)
            throw new ArgumentException("PerformanceEverySpanApplicabilityRequired");
        Spans = Array.AsReadOnly(copiedSpans.OrderBy(value => value.Span).ToArray());
        var copiedResources = AlgorithmContractValidation.Copy(resources, nameof(resources), 64);
        if (copiedResources.Count != Enum.GetValues<PerformanceResource>().Length ||
            copiedResources.Select(value => value.Resource).Distinct().Count() != copiedResources.Count)
            throw new ArgumentException("PerformanceEveryResourceApplicabilityRequired");
        Resources = Array.AsReadOnly(copiedResources.OrderBy(value => value.Resource).ToArray());
        Capture = capture ?? throw new ArgumentNullException(nameof(capture));
        CollectorId = AlgorithmContractValidation.Identifier(collectorId, nameof(collectorId));
        CollectorVersion = AlgorithmContractValidation.Identifier(collectorVersion, nameof(collectorVersion));
        CollectorHash = PerformanceContractValue.ContentHash(collectorHash);
        BuildConfiguration = AlgorithmContractValidation.Identifier(buildConfiguration, nameof(buildConfiguration));
        ProcessArchitecture = AlgorithmContractValidation.Identifier(processArchitecture, nameof(processArchitecture));
        PowerProfileHash = PerformanceContractValue.ContentHash(powerProfileHash);
        DebuggerPresent = debuggerPresent; ProfilerPresent = profilerPresent;
        DiagnosticProfileHash = PerformanceContractValue.ContentHash(diagnosticProfileHash);
        ApprovalRule = PerformanceContractValue.Text(approvalRule);
        ContentHash = PerformanceContractValue.Hash("sharpinspect-performance-contract-v1", Id, Version, percentileRule, jitterRule,
            PerformanceContractValue.Hash("scenarios-v1", Scenarios.Select(value => (object?)value.ContentHash).ToArray()),
            PerformanceContractValue.Hash("spans-v1", Spans.Select(value => (object?)value.ContentHash).ToArray()),
            PerformanceContractValue.Hash("resources-v1", Resources.Select(value => (object?)value.ContentHash).ToArray()),
            capture.ContentHash, CollectorId, CollectorVersion, CollectorHash, BuildConfiguration, ProcessArchitecture, PowerProfileHash,
            debuggerPresent, profilerPresent, DiagnosticProfileHash, ApprovalRule);
    }
    public string Id { get; }
    public string Version { get; }
    public PerformancePercentileRule PercentileRule { get; }
    public PerformanceJitterRule JitterRule { get; }
    public ReadOnlyCollection<PerformanceScenario> Scenarios { get; }
    public ReadOnlyCollection<PerformanceSpanBudget> Spans { get; }
    public ReadOnlyCollection<PerformanceResourceBudget> Resources { get; }
    public PerformanceCaptureBounds Capture { get; }
    public string CollectorId { get; }
    public string CollectorVersion { get; }
    public string CollectorHash { get; }
    public string BuildConfiguration { get; }
    public string ProcessArchitecture { get; }
    public string PowerProfileHash { get; }
    public bool DebuggerPresent { get; }
    public bool ProfilerPresent { get; }
    public string DiagnosticProfileHash { get; }
    public string ApprovalRule { get; }
    public string ContentHash { get; }
}

internal static class PerformanceContractValue
{
    internal static string Text(string value) => AlgorithmContractValidation.BoundedText(value, nameof(value), 512);
    internal static string ContentHash(string value) => AlgorithmConfigurationValidation.Hash(value, nameof(value)).ToUpperInvariant();
    internal static string Hash(string kind, params object?[] values) => AlgorithmContractValidation.HashParts(
        new[] { kind }.Concat(values.Select(value => value switch
        { null => null, IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() })));
}

public static class PerformanceFrameManifest
{
    public static string ComputeHash(IEnumerable<string?> frameHashes)
    {
        ArgumentNullException.ThrowIfNull(frameHashes);
        var copied = frameHashes.Take(100_001).ToArray();
        if (copied.Length is < 1 or > 100_000) throw new ArgumentException("PerformanceFrameManifestCapacity");
        return PerformanceContractValue.Hash("performance-frame-manifest-v1", copied.Select(value =>
            (object?)(value is null ? null : PerformanceContractValue.ContentHash(value))).ToArray());
    }
}
