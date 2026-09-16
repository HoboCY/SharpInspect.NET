using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Performance;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Tests;

/// <summary>Explicit development-fixture numbers. They never supply project production thresholds or qualification.</summary>
internal static class PerformanceTestContracts
{
    internal static PerformanceMonitoringOptions CreateDefault(ProductionStoreOptions store, ProductionDeploymentManifest deployment) =>
        Create(store, deployment);

    internal static PerformanceMonitoringOptions Create(ProductionStoreOptions store, ProductionDeploymentManifest deployment,
        double maximumSpanMilliseconds = 60_000, string? baselineScenarioId = null, int cycles = 3, int warmup = 0)
    {
        var image = VirtualCameraImage.CreateSynthetic("manual-frame", 16, 12, VisionPixelFormat.Mono8, null, 135);
        var hashes = Enumerable.Repeat<string?>(image.PixelDataHash, cycles + warmup).ToArray();
        var scenarios = Enum.GetValues<PerformanceScenarioKind>().Select(kind => new PerformanceScenario(
            kind.ToString(), kind, image.ContentHash, PerformanceFrameManifest.ComputeHash(hashes),
            ProductionAdmissionCanonical.Hash("V157.fixture.stimulus-v1", kind.ToString()), cycles, warmup,
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMinutes(3), TimeSpan.FromMilliseconds(1),
            kind == PerformanceScenarioKind.Burst ? 2 : 1, kind == PerformanceScenarioKind.Burst ? TimeSpan.FromMilliseconds(1) : TimeSpan.Zero,
            kind == PerformanceScenarioKind.Recovery ? 1 : 0, kind == PerformanceScenarioKind.Recovery ? 1 : 0,
            Array.Empty<PerformanceExpectedFailure>(), hashes, TimeSpan.FromSeconds(10))).ToArray();
        var spans = Enum.GetValues<PerformanceSpan>().Select(span => new PerformanceSpanBudget(span,
            span != PerformanceSpan.DurableImageStage || store.ImageEvidence is not null,
            span == PerformanceSpan.DurableImageStage && store.ImageEvidence is null ? "Fixture disables image evidence explicitly" : "Runtime observed development span",
            span == PerformanceSpan.DurableImageStage && store.ImageEvidence is null ? 0 : 1, 0,
            span == PerformanceSpan.DurableImageStage && store.ImageEvidence is null ? null : maximumSpanMilliseconds,
            span == PerformanceSpan.DurableImageStage && store.ImageEvidence is null ? null : maximumSpanMilliseconds,
            span == PerformanceSpan.DurableImageStage && store.ImageEvidence is null ? null : maximumSpanMilliseconds,
            span == PerformanceSpan.DurableImageStage && store.ImageEvidence is null ? null : maximumSpanMilliseconds,
            span == PerformanceSpan.DurableImageStage && store.ImageEvidence is null ? null : maximumSpanMilliseconds)).ToArray();
        var required = new HashSet<PerformanceResource>
        { PerformanceResource.WorkingSetBytes, PerformanceResource.PrivateBytes, PerformanceResource.ThreadCount,
            PerformanceResource.HandleCount, PerformanceResource.FramePoolCapacity, PerformanceResource.OutstandingFrameLeases,
            PerformanceResource.PeakFrameLeases, PerformanceResource.FramePoolExhaustions,
            PerformanceResource.SqliteDatabaseBytes, PerformanceResource.SqliteWalBytes, PerformanceResource.SqliteQueuedWrites,
            PerformanceResource.DiagnosticQueuedEvents, PerformanceResource.DiagnosticQueuedBytes, PerformanceResource.DiagnosticDrops };
        var resources = Enum.GetValues<PerformanceResource>().Select(resource =>
        {
            var applicable = required.Contains(resource);
            double maximum = resource switch
            {
                PerformanceResource.FramePoolCapacity or PerformanceResource.OutstandingFrameLeases or PerformanceResource.PeakFrameLeases => 2,
                PerformanceResource.FramePoolExhaustions => 0,
                PerformanceResource.ThreadCount or PerformanceResource.HandleCount => 100_000,
                PerformanceResource.DiagnosticQueuedEvents or PerformanceResource.SqliteQueuedWrites => 100_000,
                _ => 8L * 1024 * 1024 * 1024
            };
            return new PerformanceResourceBudget(resource, applicable,
                applicable ? "Explicit isolated software fixture resource budget" : "Not a required gate of this limited headless development fixture; observations stay visible",
                applicable ? 1 : 0, applicable ? maximum : null, applicable ? maximum : null);
        }).ToArray();
        var contract = new PerformanceContract("V157.Development.Performance", "1",
            PerformancePercentileRule.NearestRankV1, PerformanceJitterRule.ObservedMaximumMinusMinimumV1,
            scenarios, spans, resources, new(20_000, 4096, 32 * 1024 * 1024,
                TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), 4096),
            PerformanceCollectorDescription.Id, PerformanceCollectorDescription.Version, PerformanceCollectorDescription.ContentHash,
            typeof(StationRuntime).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Unknown",
            RuntimeInformation.ProcessArchitecture.ToString(), ProductionConfigurationBuilder.ReadPowerPlanHash() ??
                throw new InvalidOperationException("TestPowerProfileUnavailable"),
            Debugger.IsAttached, Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING") == "1" ||
                Environment.GetEnvironmentVariable("COR_ENABLE_PROFILING") == "1",
            store.LoggingDiagnostics!.BindingHash, "Development evidence only; no production approval authority");
        return new(contract, deployment.Performance.ContentHash, 2, baselineScenarioId);
    }
}
