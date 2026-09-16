using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Performance;

namespace SharpInspect.Performance.Consumer;

/// <summary>
/// Development-only consumer of the public performance evidence surface. It declares its own
/// explicitly bounded development contract, recomputes a report from one empty Incomplete raw
/// capture, round-trips the canonical bounded document and inspects canonical documents. It is
/// not a project production threshold source and grants no production qualification.
/// </summary>
internal static class Program
{
    private const int ExpectedScenarioKinds = 6;
    private const int ExpectedSpans = 12;
    private const int ExpectedResources = 42;
    private const string DevelopmentApplicability = "Explicit development fixture; not a project production threshold";
    private const string DevelopmentTerminalReason = "DevelopmentEmptyCaptureNotSealed";
    private const string DevelopmentLedgerReason = "DevelopmentLedgerUnavailable";
    private const string DevelopmentApprovalRule = "Development evidence only; no production approval authority";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || (args.Length == 1 && args[0] == "--smoke")) return RunSmoke();
        if (args.Length == 2 && args[0] == "--emit") return await EmitAsync(args[1]).ConfigureAwait(false);
        if (args.Length == 2 && args[0] == "--inspect") return await InspectAsync(args[1]).ConfigureAwait(false);
        Emit(new { mode = "usage", result = "Invalid", usage = new[] { "--smoke", "--emit <path>", "--inspect <path>" } });
        return 2;
    }

    private static int RunSmoke()
    {
        try { return RunSmokeCore(); }
        catch (Exception exception)
        {
            Emit(new { mode = "smoke", result = "Failed", reason = exception.Message,
                checks = new Dictionary<string, bool>(), failedChecks = new[] { "unhandledException" } });
            return 4;
        }
    }

    private static int RunSmokeCore()
    {
        var scenarioInput = new List<PerformanceScenario>();
        var spanInput = new List<PerformanceSpanBudget>();
        var resourceInput = new List<PerformanceResourceBudget>();
        var contract = BuildContract(scenarioInput, spanInput, resourceInput);
        // The inputs stay mutable and are extended afterwards; the contract must remain their copy.
        scenarioInput.Add(scenarioInput[0]);
        spanInput.Add(spanInput[0]);
        resourceInput.Add(resourceInput[0]);
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["developmentScope"] = contract.ApprovalRule == DevelopmentApprovalRule,
            ["contractDefensiveCopy"] = contract.Scenarios.Count == ExpectedScenarioKinds &&
                contract.Spans.Count == ExpectedSpans && contract.Resources.Count == ExpectedResources &&
                scenarioInput.Count == ExpectedScenarioKinds + 1 && spanInput.Count == ExpectedSpans + 1 &&
                resourceInput.Count == ExpectedResources + 1,
            ["collectorHashBinding"] = contract.CollectorId == PerformanceCollectorDescription.Id &&
                contract.CollectorVersion == PerformanceCollectorDescription.Version &&
                contract.CollectorHash == PerformanceCollectorDescription.ContentHash && contract.ContentHash.Length == 64
        };
        var (scenario, capture) = BuildEmptyCapture(contract);
        checks["emptyCapture"] = capture.State == PerformanceEvidenceState.Incomplete && !capture.LedgerAvailable &&
            capture.LedgerReasonCode == DevelopmentLedgerReason && capture.TerminalReason == DevelopmentTerminalReason &&
            capture.Events.Count == 0 && capture.ResourceSamples.Count == 0 && capture.DurableFacts.Count == 0 &&
            capture.Frames.Count == 0 && capture.CycleBindings.Count == 0 && capture.ObservationDrops == 0;
        checks["headerHashBinding"] = capture.Header.ContractHash == contract.ContentHash &&
            capture.Header.ScenarioId == scenario.Id && capture.Header.ScenarioHash == scenario.ContentHash &&
            capture.Header.CollectorHash == contract.CollectorHash;
        var bytes = PerformanceEvidenceDocuments.Encode(capture);
        var document = PerformanceEvidenceDocuments.Decode(bytes);
        var report = document.Report;
        checks["documentRoundTrip"] = bytes.Length > 0 && document.RawHash.Length == 64 &&
            document.ReportHash.Length == 64 && document.Raw.Contract.ContentHash == contract.ContentHash &&
            document.Raw.State == PerformanceEvidenceState.Incomplete &&
            PerformanceEvidenceDocuments.Encode(document.Raw).AsSpan().SequenceEqual(bytes);
        var tampered = (byte[])bytes.Clone();
        tampered[tampered.Length / 2] ^= 0x01;
        checks["tamperRejected"] = false;
        try { _ = PerformanceEvidenceDocuments.Decode(tampered); }
        catch (InvalidDataException) { checks["tamperRejected"] = true; }
        checks["nullStatistics"] = report.Spans.Count == ExpectedSpans && report.Spans.All(value =>
            value.P50Milliseconds is null && value.P95Milliseconds is null && value.P99Milliseconds is null &&
            value.ObservedMaxMilliseconds is null && value.JitterMilliseconds is null && value.ObservedCount == 0);
        checks["unknownAndUnrunState"] = !report.Passed && report.FailureCount > 0 &&
            report.CaptureState == PerformanceEvidenceState.Incomplete && !report.CaptureSealed &&
            !report.Evidence.LedgerAvailable && report.Evidence.LedgerReasonCode == DevelopmentLedgerReason &&
            report.Cycles.Count == 0 && report.AcceptedCycles == 0 && report.Purpose == PerformanceReportPurpose.FrameworkBaseline &&
            report.Resources.Count == ExpectedResources && report.Resources.All(value =>
                value.ObservedCount == 0 && value.First is null && value.Last is null &&
                value.ObservedMaximum is null && value.Growth is null);
        var partial = PerformanceReportCalculator.Aggregate(contract, new[] { report });
        var duplicate = PerformanceReportCalculator.Aggregate(contract, new[] { report, report });
        checks["aggregateNotRun"] = !partial.Passed && partial.Scenarios.Count == ExpectedScenarioKinds &&
            partial.Scenarios.Count(value => value.Status == PerformanceReportStatus.NotRun) == ExpectedScenarioKinds - 1 &&
            partial.Failures.Any(value => value.Code == "PerformanceAggregateScenarioMissing");
        checks["aggregateDuplicate"] = !duplicate.Passed && duplicate.Scenarios.Any(value =>
                value.ScenarioId == scenario.Id && value.Status == PerformanceReportStatus.Failed && value.ReportCount == 2) &&
            duplicate.Failures.Any(value => value.Code == "PerformanceAggregateScenarioDuplicate");
        checks["noProductionQualification"] = !report.ProductionQualificationAuthority &&
            !partial.ProductionQualificationAuthority && !duplicate.ProductionQualificationAuthority && !report.Passed;
        var failed = checks.Where(pair => !pair.Value).Select(pair => pair.Key).ToArray();
        Emit(new
        {
            mode = "smoke",
            result = failed.Length == 0 ? "Pass" : "Failed",
            declaration = DevelopmentApplicability,
            productionQualificationAuthority = report.ProductionQualificationAuthority,
            scenarioKinds = contract.Scenarios.Count,
            spanBudgets = contract.Spans.Count,
            resourceBudgets = contract.Resources.Count,
            documentBytes = bytes.Length,
            rawHash = document.RawHash,
            reportHash = document.ReportHash,
            captureState = report.CaptureState.ToString(),
            reportPassed = report.Passed,
            notRunScenarios = partial.Scenarios.Count(value => value.Status == PerformanceReportStatus.NotRun),
            checks,
            failedChecks = failed
        });
        return failed.Length == 0 ? 0 : 3;
    }

    private static PerformanceContract BuildContract(List<PerformanceScenario> scenarios,
        List<PerformanceSpanBudget> spans, List<PerformanceResourceBudget> resources)
    {
        var kinds = Enum.GetValues<PerformanceScenarioKind>();
        var spanIdentities = Enum.GetValues<PerformanceSpan>();
        var resourceIdentities = Enum.GetValues<PerformanceResource>();
        if (kinds.Length != ExpectedScenarioKinds || spanIdentities.Length != ExpectedSpans ||
            resourceIdentities.Length != ExpectedResources)
            throw new InvalidOperationException("DevelopmentEnumerationCoverageChanged");
        foreach (var kind in kinds)
        {
            var frames = new[] { DevelopmentHash("frame/" + kind) };
            var recovery = kind == PerformanceScenarioKind.Recovery ? 1 : 0;
            scenarios.Add(new PerformanceScenario(kind.ToString(), kind, DevelopmentHash("dataset/" + kind),
                PerformanceFrameManifest.ComputeHash(frames), DevelopmentHash("stimulus/" + kind), 1, 0,
                TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), 1,
                TimeSpan.Zero, recovery, recovery, Array.Empty<PerformanceExpectedFailure>(), frames,
                TimeSpan.FromMilliseconds(5)));
        }

        foreach (var span in spanIdentities)
            spans.Add(new PerformanceSpanBudget(span, true, DevelopmentApplicability, 1, 0,
                30_000, 45_000, 55_000, 60_000, 60_000));
        foreach (var resource in resourceIdentities)
            resources.Add(new PerformanceResourceBudget(resource, false,
                "Not a required gate of this empty development capture; a future observation stays visible", 0, null, null));
        return new PerformanceContract("SharpInspect.Performance.Consumer.Development", "1",
            PerformancePercentileRule.NearestRankV1, PerformanceJitterRule.ObservedMaximumMinusMinimumV1,
            scenarios, spans, resources, new PerformanceCaptureBounds(20_000, 4_096, 32L * 1024 * 1024,
                TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), 4_096),
            PerformanceCollectorDescription.Id, PerformanceCollectorDescription.Version,
            PerformanceCollectorDescription.ContentHash, "Development", RuntimeInformation.ProcessArchitecture.ToString(),
            DevelopmentHash("power-profile"), Debugger.IsAttached, IsProfilerEnabled(),
            DevelopmentHash("diagnostic-profile"), DevelopmentApprovalRule);
    }

    private static (PerformanceScenario Scenario, PerformanceRawCapture Capture) BuildEmptyCapture(
        PerformanceContract contract)
    {
        var scenario = contract.Scenarios.First(value => value.Kind == PerformanceScenarioKind.SteadyState);
        var header = new PerformanceRunHeader(Guid.NewGuid(), Guid.NewGuid(), contract.ContentHash, scenario.Id,
            scenario.ContentHash, Stopwatch.Frequency, 0, DateTimeOffset.UtcNow,
            AssemblyHash(typeof(PerformanceEvidenceDocuments)), AssemblyHash(typeof(PerformanceContract)),
            contract.BuildConfiguration, contract.ProcessArchitecture, contract.PowerProfileHash,
            contract.DebuggerPresent, contract.ProfilerPresent, contract.DiagnosticProfileHash,
            contract.CollectorId, contract.CollectorVersion, contract.CollectorHash);
        var capture = new PerformanceRawCapture(contract, header, PerformanceEvidenceState.Incomplete, 0, 0, 0,
            DevelopmentTerminalReason, Array.Empty<PerformanceEvent>(), Array.Empty<PerformanceResourceSample>(),
            Array.Empty<PerformanceDurableFact>(), false, DevelopmentLedgerReason,
            Array.Empty<PerformanceFrameObservation>(), Array.Empty<PerformanceCycleBinding>());
        return (scenario, capture);
    }

    private static async Task<int> EmitAsync(string path)
    {
        try
        {
            var contract = BuildContract(new(), new(), new());
            var (_, capture) = BuildEmptyCapture(contract);
            await PerformanceEvidenceDocuments.WriteNewAsync(path, capture).ConfigureAwait(false);
            var document = await PerformanceEvidenceDocuments.ReadAsync(path).ConfigureAwait(false);
            Emit(new
            {
                mode = "emit",
                result = "Pass",
                format = PerformanceEvidenceDocuments.Format,
                productionQualificationAuthority = document.Report.ProductionQualificationAuthority,
                documentBytes = new FileInfo(path).Length,
                rawHash = document.RawHash,
                reportHash = document.ReportHash,
                captureState = document.Report.CaptureState.ToString()
            });
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Emit(new { mode = "emit", result = "Invalid", reason = "PerformanceDocumentUnwritable" });
            return 2;
        }
    }

    private static async Task<int> InspectAsync(string path)
    {
        PerformanceEvidenceDocument document;
        try { document = await PerformanceEvidenceDocuments.ReadAsync(path).ConfigureAwait(false); }
        catch (InvalidDataException exception)
        {
            Emit(new { mode = "inspect", result = "Invalid", reason = exception.Message });
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Emit(new { mode = "inspect", result = "Invalid", reason = "PerformanceDocumentUnreadable" });
            return 2;
        }

        var report = document.Report;
        var unknownResources = report.Resources.Where(value => value.UnknownCount > 0)
            .Select(value => value.Resource.ToString()).ToArray();
        Emit(new
        {
            mode = "inspect",
            result = report.Passed ? "Pass" : "Failed",
            scenarioId = report.ScenarioId,
            purpose = report.Purpose.ToString(),
            passed = report.Passed,
            productionQualificationAuthority = report.ProductionQualificationAuthority,
            captureState = report.CaptureState.ToString(),
            observationDrops = report.ObservationDrops,
            counts = new
            {
                events = document.Raw.Events.Count,
                resourceSamples = document.Raw.ResourceSamples.Count,
                durableFacts = document.Raw.DurableFacts.Count,
                frames = document.Raw.Frames.Count,
                cycleBindings = document.Raw.CycleBindings.Count,
                measuredCycles = report.MeasuredCycles,
                warmupCycles = report.WarmupCycles,
                acceptedCycles = report.AcceptedCycles
            },
            hashes = new { raw = document.RawHash, report = document.ReportHash },
            spanStatisticsNull = report.Spans.All(value => value.P50Milliseconds is null &&
                value.P95Milliseconds is null && value.P99Milliseconds is null &&
                value.ObservedMaxMilliseconds is null && value.JitterMilliseconds is null),
            spans = report.Spans.Select(value => new
            {
                span = value.Span.ToString(),
                p50 = value.P50Milliseconds,
                p95 = value.P95Milliseconds,
                p99 = value.P99Milliseconds,
                observedMax = value.ObservedMaxMilliseconds,
                jitter = value.JitterMilliseconds,
                observed = value.ObservedCount,
                failures = value.FailureCount,
                unknown = value.UnknownCount,
                passed = value.Passed
            }).ToArray(),
            failureCount = report.FailureCount,
            failureCodes = report.Failures.Select(value => value.Code).Distinct(StringComparer.Ordinal).ToArray(),
            resourcesUnknown = new { count = unknownResources.Length, names = unknownResources }
        });
        return report.Passed ? 0 : 3;
    }

    private static bool IsProfilerEnabled() =>
        Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING") == "1" ||
        Environment.GetEnvironmentVariable("COR_ENABLE_PROFILING") == "1";

    private static string DevelopmentHash(string label) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes("SharpInspect.Performance.Consumer/development/" + label)));

    private static string AssemblyHash(Type type)
    {
        var location = type.Assembly.Location;
        if (string.IsNullOrEmpty(location)) throw new InvalidOperationException("DevelopmentAssemblyLocationUnavailable");
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(location)));
    }

    private static void Emit(object value) => Console.WriteLine(JsonSerializer.Serialize(value, value.GetType()));
}
