using SharpInspect.Abstractions;
using SharpInspect.Runtime.Performance;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Deterministic V157 checks for <see cref="PerformanceReportCalculator"/>. Every capture is
/// built by hand so the declared V1 rules are asserted exactly once and never depend on wall time.
/// </summary>
public sealed partial class PerformanceReportCalculatorTests
{
    private const long Frequency = 1_000; // One stopwatch tick equals one millisecond in every fixture.
    private static readonly string HashA = new('A', 64);
    private static readonly string HashB = new('B', 64);
    private static readonly string HashC = new('C', 64);

    [Fact, Trait("VerificationId", "V157_R01")]
    public void V157_R01_KnownNearestRankPercentilesExcludeWarmup()
    {
        var scenario = Scenario("Performance.V157.Steady", PerformanceScenarioKind.SteadyState,
            measuredCycles: 4, warmupCycles: 1,
            frameHashes: new[] { FrameHash(1), FrameHash(2), FrameHash(3), FrameHash(4), FrameHash(5) });
        var contract = ContractWith(scenario, SpanBudgets(
            (PerformanceSpan.TriggerToBusy, 4, 0, 4, 8, 8, 8, 6)));
        var builder = new CaptureBuilder(contract, scenario);
        builder.Ready();
        builder.CleanCycle(1, triggerToBusyMilliseconds: 500, FrameHash(1)); // Warm-up: excluded from statistics.
        builder.CleanCycle(2, 2, FrameHash(2));
        builder.CleanCycle(3, 4, FrameHash(3));
        builder.CleanCycle(4, 6, FrameHash(4));
        builder.CleanCycle(5, 8, FrameHash(5));

        var report = PerformanceReportCalculator.Calculate(builder.Build());

        Assert.True(report.Passed);
        Assert.Equal(PerformanceReportPurpose.FrameworkBaseline, report.Purpose);
        Assert.False(report.ProductionQualificationAuthority);
        Assert.NotEqual(Guid.Empty, report.RunId);
        Assert.Equal(contract.ContentHash, report.ContractHash);
        Assert.Equal(scenario.Id, report.ScenarioId);
        Assert.Equal(scenario.ContentHash, report.ScenarioHash);
        Assert.True(report.CaptureSealed);
        Assert.Equal(5, report.Cycles.Count);
        Assert.True(report.Cycles.Single(item => item.Ordinal == 1).Warmup);
        Assert.False(report.Cycles.Single(item => item.Ordinal == 2).Warmup);

        var span = report.Spans.Single(item => item.Span == PerformanceSpan.TriggerToBusy);
        Assert.True(span.Applicable);
        Assert.Equal(4, span.ObservedCount);
        Assert.Equal(0, span.FailureCount);
        Assert.Equal(4d, span.P50Milliseconds);
        Assert.Equal(8d, span.P95Milliseconds);
        Assert.Equal(8d, span.P99Milliseconds);
        Assert.Equal(8d, span.ObservedMaxMilliseconds);
        Assert.Equal(6d, span.JitterMilliseconds);
        Assert.True(span.Passed);
    }

    [Fact, Trait("VerificationId", "V157_R02")]
    public void V157_R02_ExpectedFailureStaysInsideFailureCountAndMaximumFailureGate()
    {
        var scenario = Scenario("Performance.V157.ExpectedFailure", PerformanceScenarioKind.SteadyState,
            measuredCycles: 3, warmupCycles: 1,
            frameHashes: new[] { FrameHash(1), FrameHash(2), FrameHash(3), null },
            expectedFailures: new[] { new PerformanceExpectedFailure(4, "AlgorithmExecutionTimeout") });
        var tolerant = ContractWith(scenario, SpanBudgets(
            (PerformanceSpan.ResultValidation, 2, 1, 1, 1, 1, 1, 0)));
        var strict = ContractWith(scenario, SpanBudgets(
            (PerformanceSpan.ResultValidation, 2, 0, 1, 1, 1, 1, 0)));

        var report = PerformanceReportCalculator.Calculate(BuildExpectedFailureCapture(tolerant, scenario));
        Assert.True(report.Passed);
        var span = report.Spans.Single(item => item.Span == PerformanceSpan.ResultValidation);
        Assert.Equal(2, span.ObservedCount); // Warm-up ordinal excluded; measured ordinals 2 and 3 remain.
        Assert.Equal(1, span.FailureCount);  // The expected failure is still counted, never excused.
        Assert.True(span.Passed);
        var failed = report.Cycles.Single(item => item.Ordinal == 4);
        Assert.True(failed.ExecutionFailed);
        Assert.Equal("AlgorithmExecutionTimeout", failed.ExpectedFailureReason);
        Assert.Contains("AlgorithmExecutionTimeout", failed.ObservedFailureReasons);
        Assert.DoesNotContain("PerformanceUnexpectedExecutionFailure", Codes(report));

        var rejected = PerformanceReportCalculator.Calculate(BuildExpectedFailureCapture(strict, scenario));
        Assert.False(rejected.Passed);
        Assert.False(rejected.Spans.Single(item => item.Span == PerformanceSpan.ResultValidation).Passed);
        Assert.Contains("PerformanceSpanMaximumFailures", Codes(rejected));
    }

    [Fact, Trait("VerificationId", "V157_R03")]
    public void V157_R03_EmptyStatisticsAreNullAndExpectedFailureNotObservedFails()
    {
        var scenario = Scenario("Performance.V157.ExpectedFailureSilent", PerformanceScenarioKind.SteadyState,
            measuredCycles: 1, warmupCycles: 0, frameHashes: new string?[] { null },
            expectedFailures: new[] { new PerformanceExpectedFailure(1, "AlgorithmExecutionTimeout") });
        var contract = ContractWith(scenario, SpanBudgets());
        var builder = new CaptureBuilder(contract, scenario);
        var correlation = Guid.NewGuid();
        builder.Emit(PerformanceEventKind.TriggerObserved, Correlation(correlation), 7, 1, at: 1_000);
        builder.Emit(PerformanceEventKind.TriggerAccepted, Correlation(correlation), 7, 1, at: 1_001);
        builder.Admit(correlation, 1, 1_001, Guid.NewGuid());
        builder.Binding(correlation, HashA);
        builder.Emit(PerformanceEventKind.AlgorithmOutcomeFixed, Correlation(correlation), at: 1_010);
        builder.Emit(PerformanceEventKind.ImageStageCompleted, Correlation(correlation), at: 1_010,
            outcome: PerformanceObservationOutcome.NotApplicable, reason: "PerformanceImageStageNotApplicable");
        builder.Emit(PerformanceEventKind.CycleCompleted, Correlation(correlation), 7, 1, at: 1_011);
        builder.Advance(1_011);

        var report = PerformanceReportCalculator.Calculate(builder.Build());

        Assert.False(report.Passed);
        Assert.Contains("PerformanceExpectedFailureNotObserved", Codes(report));
        // An explicit NotApplicable image event is the declared exemption; it is never filled with zero.
        var image = report.Spans.Single(item => item.Span == PerformanceSpan.DurableImageStage);
        Assert.Equal(1, image.NotApplicableCount);
        Assert.Equal(0, image.ObservedCount);
        Assert.Equal(0, image.FailureCount);
        var span = report.Spans.Single(item => item.Span == PerformanceSpan.TriggerToBusy);
        Assert.False(span.Applicable);
        Assert.Equal(0, span.ObservedCount);
        Assert.Null(span.P50Milliseconds);
        Assert.Null(span.P95Milliseconds);
        Assert.Null(span.P99Milliseconds);
        Assert.Null(span.ObservedMaxMilliseconds);
        Assert.Null(span.JitterMilliseconds);
        Assert.True(span.Passed); // Not applicable: missing endpoints are not an integrity defect here.
    }

    [Fact, Trait("VerificationId", "V157_R04")]
    public void V157_R04_BudgetOverflowAndRequiredResourceGatesFail()
    {
        var scenario = Scenario("Performance.V157.Overflow", PerformanceScenarioKind.SteadyState,
            measuredCycles: 2, warmupCycles: 0, frameHashes: new[] { FrameHash(1), FrameHash(2) });
        var contract = ContractWith(scenario,
            SpanBudgets((PerformanceSpan.TriggerToBusy, 2, 0, 10, 10, 10, 10, 0)),
            ResourceBudgets((PerformanceResource.WorkingSetBytes, 2, 150, 50)));
        var builder = new CaptureBuilder(contract, scenario);
        builder.Ready();
        builder.CleanCycle(1, 50, FrameHash(1));
        builder.CleanCycle(2, 60, FrameHash(2));
        builder.Sample(builder.Clock, Observed(PerformanceResource.WorkingSetBytes, 100));
        builder.Advance(1);
        builder.Sample(builder.Clock, Observed(PerformanceResource.WorkingSetBytes, 200));

        var report = PerformanceReportCalculator.Calculate(builder.Build());

        Assert.False(report.Passed);
        var span = report.Spans.Single(item => item.Span == PerformanceSpan.TriggerToBusy);
        Assert.Equal(2, span.ObservedCount);
        Assert.False(span.Passed);
        Assert.Contains("PerformanceSpanBudgetExceeded", Codes(report));
        var resource = report.Resources.Single(item => item.Resource == PerformanceResource.WorkingSetBytes);
        Assert.True(resource.Required);
        Assert.Equal(2, resource.ObservedCount);
        Assert.Equal(200d, resource.ObservedMaximum);
        Assert.Equal(100d, resource.Growth);
        Assert.False(resource.Passed);
        Assert.Contains("PerformanceResourceMaximumExceeded", Codes(report));
        Assert.Contains("PerformanceResourceGrowthExceeded", Codes(report));
        // Every contract resource and span is always output, including untouched ones.
        Assert.Equal(Enum.GetValues<PerformanceResource>().Length, report.Resources.Count);
        Assert.Equal(Enum.GetValues<PerformanceSpan>().Length, report.Spans.Count);
    }

    [Fact, Trait("VerificationId", "V157_R05")]
    public void V157_R05_RawDuplicatesAreObservationDefectsOnly()
    {
        var scenario = Scenario("Performance.V157.ObservationDuplicate", PerformanceScenarioKind.SteadyState,
            measuredCycles: 1, warmupCycles: 0, frameHashes: new[] { FrameHash(1) });
        var contract = ContractWith(scenario, SpanBudgets());
        var builder = new CaptureBuilder(contract, scenario);
        builder.Ready();
        var cycle = builder.CleanCycle(1, 2, FrameHash(1));
        builder.Emit(PerformanceEventKind.TriggerAccepted, Correlation(cycle.Correlation), 7, 1, at: 1_001);

        var report = PerformanceReportCalculator.Calculate(builder.Build());

        Assert.False(report.Passed);
        Assert.Contains("PerformanceObservationDuplicate", Codes(report));
        Assert.True(report.Evidence.LedgerAvailable);
        Assert.Equal("PerformanceLedgerVerified", report.Evidence.LedgerReasonCode);
        var accepted = report.Evidence.Identities.Single(item => item.Kind == PerformanceIdentityKind.Accepted);
        Assert.Equal(1, accepted.ObservationDuplicateCount);
        Assert.Equal(0, accepted.BusinessDuplicateCount);
        Assert.Equal(0, accepted.BusinessLossCount);
        Assert.Equal(0, accepted.ObservationMissingCount);
    }

    [Fact, Trait("VerificationId", "V157_R06")]
    public void V157_R06_BusinessLossAndLedgerDuplicateAreCorrectnessFailures()
    {
        var scenario = Scenario("Performance.V157.Business", PerformanceScenarioKind.SteadyState,
            measuredCycles: 1, warmupCycles: 0, frameHashes: new[] { FrameHash(1) });
        var contract = ContractWith(scenario, SpanBudgets());
        var builder = new CaptureBuilder(contract, scenario);
        builder.Ready();
        var cycle = builder.CleanCycle(1, 2, FrameHash(1));
        builder.Admit(cycle.Correlation, 1, 1_001, cycle.InspectionId); // Same correlation/inspection/controller.
        var ghost = Guid.NewGuid();
        builder.Emit(PerformanceEventKind.TriggerAccepted, Correlation(ghost), 7, 99, at: builder.Clock);

        var report = PerformanceReportCalculator.Calculate(builder.Build());

        Assert.False(report.Passed);
        Assert.Contains("PerformanceBusinessDuplicate", Codes(report));
        Assert.Contains("PerformanceBusinessLoss", Codes(report));
        var accepted = report.Evidence.Identities.Single(item => item.Kind == PerformanceIdentityKind.Accepted);
        Assert.Equal(1, accepted.BusinessDuplicateCount);
        Assert.Equal(1, accepted.BusinessLossCount);
        Assert.Equal(0, accepted.ObservationMissingCount);
        Assert.Equal(0, accepted.ObservationDuplicateCount);
    }

    [Fact, Trait("VerificationId", "V157_R07")]
    public void V157_R07_TargetAndFrameMismatchesAreEvidenceFailures()
    {
        var scenario = Scenario("Performance.V157.Frames", PerformanceScenarioKind.SteadyState,
            measuredCycles: 1, warmupCycles: 0, frameHashes: new[] { FrameHash(1) });
        var contract = ContractWith(scenario, SpanBudgets());
        var builder = new CaptureBuilder(contract, scenario);
        builder.Ready();
        var cycle = builder.CleanCycle(1, 2, FrameHash(9)); // Actual hash never substitutes the declared hash.
        builder.Binding(cycle.Correlation, HashB);

        var report = PerformanceReportCalculator.Calculate(builder.Build());

        Assert.False(report.Passed);
        Assert.Contains("PerformanceCycleBindingDuplicate", Codes(report));
        Assert.Contains("PerformanceTargetFingerprintMismatch", Codes(report));
        Assert.Contains("PerformanceFrameHashMismatch", Codes(report));
        var cycleReport = report.Cycles.Single();
        Assert.Equal(FrameHash(1), cycleReport.ExpectedFrameHash);
        Assert.Equal(FrameHash(9), cycleReport.ObservedFrameHash);

        var second = new CaptureBuilder(contract, scenario);
        second.Ready();
        second.CleanCycle(1, 2, FrameHash(1), observeFrame: false);
        Assert.Contains("PerformanceFrameObservationMissing",
            Codes(PerformanceReportCalculator.Calculate(second.Build())));

        var third = new CaptureBuilder(contract, scenario);
        third.Ready();
        third.CleanCycle(1, 2, FrameHash(1), emitFrameReady: false, observeFrame: false);
        Assert.Contains("PerformanceFrameMissing",
            Codes(PerformanceReportCalculator.Calculate(third.Build())));
    }

    [Fact, Trait("VerificationId", "V157_R08")]
    public void V157_R08_UnknownNativeMemoryCannotBeSubstitutedByProxy()
    {
        var scenario = Scenario("Performance.V157.Native", PerformanceScenarioKind.SteadyState,
            measuredCycles: 2, warmupCycles: 0, frameHashes: new[] { FrameHash(1), FrameHash(2) });
        var contract = ContractWith(scenario, SpanBudgets(), ResourceBudgets(
            (PerformanceResource.NativeMemoryBytes, 2, 1_000_000_000, 0),
            (PerformanceResource.NonGcPrivateBytesEstimate, 2, 1_000_000_000, 1_000_000_000)));
        var builder = new CaptureBuilder(contract, scenario);
        builder.Ready();
        builder.CleanCycle(1, 2, FrameHash(1));
        builder.CleanCycle(2, 2, FrameHash(2));
        builder.Sample(builder.Clock,
            Unknown(PerformanceResource.NativeMemoryBytes, "ExactNativeAllocatorCollectorUnavailable"),
            Observed(PerformanceResource.NonGcPrivateBytesEstimate, 1_000));
        builder.Advance(1);
        builder.Sample(builder.Clock,
            Unknown(PerformanceResource.NativeMemoryBytes, "ExactNativeAllocatorCollectorUnavailable"),
            Observed(PerformanceResource.NonGcPrivateBytesEstimate, 2_000));

        var report = PerformanceReportCalculator.Calculate(builder.Build());

        Assert.False(report.Passed);
        var native = report.Resources.Single(item => item.Resource == PerformanceResource.NativeMemoryBytes);
        Assert.Equal(0, native.ObservedCount);
        Assert.Equal(2, native.UnknownCount);
        Assert.False(native.Passed);
        Assert.Contains("PerformanceResourceUnknown", Codes(report));
        var proxy = report.Resources.Single(item => item.Resource == PerformanceResource.NonGcPrivateBytesEstimate);
        Assert.Equal(2, proxy.ObservedCount);
        Assert.Equal(2_000d, proxy.ObservedMaximum);
        Assert.Equal(1_000d, proxy.Growth);
        Assert.True(proxy.Passed);
    }

    [Fact, Trait("VerificationId", "V157_R09")]
    public void V157_R09_MixedMissingAndDuplicateEvidenceIsRetained()
    {
        var scenario = Scenario("Performance.V157.Mixed", PerformanceScenarioKind.SteadyState,
            measuredCycles: 2, warmupCycles: 0, frameHashes: new[] { FrameHash(1), FrameHash(2) });
        var contract = ContractWith(scenario,
            SpanBudgets((PerformanceSpan.Acquisition, 2, 0, 1, 1, 1, 1, 0)),
            ResourceBudgets((PerformanceResource.WorkingSetBytes, 3, 1_000, 1_000)));
        var builder = new CaptureBuilder(contract, scenario);
        builder.Ready();
        var first = builder.CleanCycle(1, 2, FrameHash(1));
        builder.Emit(PerformanceEventKind.BusyAsserted, Correlation(first.Correlation), at: builder.Clock - 17);
        builder.CleanCycle(2, 2, FrameHash(2));
        builder.EmitWithSequenceGap(PerformanceEventKind.BudgetViolation);
        builder.Sample(builder.Clock, Observed(PerformanceResource.WorkingSetBytes, 100));
        builder.Sample(builder.Clock);
        builder.SampleWithSequenceGap(builder.Clock,
            Unknown(PerformanceResource.WorkingSetBytes, "PerformanceResourceReadFailed"));

        var report = PerformanceReportCalculator.Calculate(builder.Build());
        var codes = Codes(report);

        Assert.False(report.Passed);
        Assert.Contains("PerformanceEventSequenceInvalid", codes);
        Assert.Contains("PerformanceResourceSequenceInvalid", codes);
        Assert.Contains("PerformanceSpanDuplicatePhase", codes);
        Assert.Contains("PerformanceSpanMinimumSamples", codes);
        Assert.Contains("PerformanceResourceMinimumSamples", codes);
        Assert.Contains("PerformanceResourceMissing", codes);
        Assert.Contains("PerformanceResourceUnknown", codes);
        var resource = report.Resources.Single(item => item.Resource == PerformanceResource.WorkingSetBytes);
        Assert.Equal(1, resource.ObservedCount);
        Assert.Equal(1, resource.MissingCount);
        Assert.Equal(1, resource.UnknownCount);
        Assert.Equal(1, report.Spans.Single(item => item.Span == PerformanceSpan.Acquisition).DefectCount);
    }

    [Fact, Trait("VerificationId", "V157_R10")]
    public void V157_R10_AggregateRequiresEveryFrozenScenarioExactlyOnce()
    {
        var scenario = Scenario("Performance.V157.Steady", PerformanceScenarioKind.SteadyState,
            measuredCycles: 1, warmupCycles: 0, frameHashes: new[] { FrameHash(1) });
        var contract = ContractWith(scenario,
            SpanBudgets((PerformanceSpan.TriggerToBusy, 1, 0, 5, 5, 5, 5, 0)));

        var passingBuilder = new CaptureBuilder(contract, scenario);
        passingBuilder.Ready();
        passingBuilder.CleanCycle(1, 2, FrameHash(1));
        var passing = PerformanceReportCalculator.Calculate(passingBuilder.Build());

        var failingBuilder = new CaptureBuilder(contract, scenario);
        failingBuilder.Ready();
        failingBuilder.CleanCycle(1, 50, FrameHash(1));
        var failing = PerformanceReportCalculator.Calculate(failingBuilder.Build());

        Assert.True(passing.Passed);
        Assert.False(failing.Passed);

        var partial = PerformanceReportCalculator.Aggregate(contract, new[] { passing });
        Assert.False(partial.Passed);
        Assert.False(partial.ProductionQualificationAuthority);
        Assert.Equal(PerformanceReportStatus.Passed,
            partial.Scenarios.Single(item => item.ScenarioId == scenario.Id).Status);
        Assert.Equal(5, partial.Scenarios.Count(item => item.Status == PerformanceReportStatus.NotRun));
        Assert.Contains("PerformanceAggregateScenarioMissing", AggregateCodes(partial));

        var duplicates = PerformanceReportCalculator.Aggregate(contract, new[] { passing, failing });
        Assert.False(duplicates.Passed);
        Assert.Equal(PerformanceReportStatus.Failed,
            duplicates.Scenarios.Single(item => item.ScenarioId == scenario.Id).Status);
        Assert.Contains("PerformanceAggregateScenarioDuplicate", AggregateCodes(duplicates));

        // Two green reports cannot be cherry-picked either; a duplicated scenario fails on its own.
        var greenDuplicates = PerformanceReportCalculator.Aggregate(contract, new[] { passing, passing });
        Assert.False(greenDuplicates.Passed);
        Assert.Equal(PerformanceReportStatus.Failed,
            greenDuplicates.Scenarios.Single(item => item.ScenarioId == scenario.Id).Status);

        var singleFailure = PerformanceReportCalculator.Aggregate(contract, new[] { failing });
        Assert.False(singleFailure.Passed);
        Assert.Equal(PerformanceReportStatus.Failed,
            singleFailure.Scenarios.Single(item => item.ScenarioId == scenario.Id).Status);
        Assert.Contains("PerformanceAggregateScenarioFailed", AggregateCodes(singleFailure));

        var unknownBuilder = new CaptureBuilder(contract, scenario);
        unknownBuilder.Ready();
        unknownBuilder.CleanCycle(1, 2, FrameHash(1));
        var unknown = PerformanceReportCalculator.Calculate(unknownBuilder.Build(
            scenarioId: "Performance.V157.Unknown", scenarioHash: HashA));
        var unknownAggregate = PerformanceReportCalculator.Aggregate(contract, new[] { unknown });
        Assert.False(unknownAggregate.Passed);
        Assert.Contains("PerformanceAggregateScenarioUnknown", AggregateCodes(unknownAggregate));
        Assert.Equal(6, unknownAggregate.Scenarios.Count(item => item.Status == PerformanceReportStatus.NotRun));
    }

    private static PerformanceResourceValue Observed(PerformanceResource resource, double value) =>
        new(resource, value, PerformanceObservationOutcome.Observed, "PerformanceResourceObserved");

    private static PerformanceResourceValue Unknown(PerformanceResource resource, string reason) =>
        new(resource, null, PerformanceObservationOutcome.Unknown, reason);

    private static PerformanceRawCapture BuildExpectedFailureCapture(PerformanceContract contract,
        PerformanceScenario scenario)
    {
        var builder = new CaptureBuilder(contract, scenario);
        builder.Ready();
        builder.CleanCycle(1, triggerToBusyMilliseconds: 100, FrameHash(1)); // Warm-up.
        builder.CleanCycle(2, 2, FrameHash(2));
        builder.CleanCycle(3, 2, FrameHash(3));
        var correlation = Guid.NewGuid();
        var trigger = builder.Clock;
        builder.Emit(PerformanceEventKind.TriggerObserved, Correlation(correlation), 7, 4, at: trigger);
        builder.Emit(PerformanceEventKind.TriggerAccepted, Correlation(correlation), 7, 4, at: trigger + 1);
        var inspectionId = Guid.NewGuid();
        builder.Admit(correlation, 4, trigger + 1, inspectionId);
        builder.Binding(correlation, HashA);
        builder.Emit(PerformanceEventKind.BusyAsserted, Correlation(correlation), at: trigger + 2);
        builder.Emit(PerformanceEventKind.AlgorithmQueued, Correlation(correlation), at: trigger + 3);
        builder.Emit(PerformanceEventKind.AlgorithmStarted, Correlation(correlation), at: trigger + 4);
        builder.Emit(PerformanceEventKind.AlgorithmReturned, Correlation(correlation), at: trigger + 5,
            outcome: PerformanceObservationOutcome.Failed, reason: "AlgorithmExecutionTimeout");
        builder.Emit(PerformanceEventKind.AlgorithmOutcomeFixed, Correlation(correlation), at: trigger + 5,
            outcome: PerformanceObservationOutcome.TimedOut, reason: "AlgorithmExecutionTimeout");
        builder.Emit(PerformanceEventKind.CoreCommitCompleted, Correlation(correlation), at: trigger + 6);
        builder.CoreCommitted(correlation, 4, trigger + 6, inspectionId, ExecutionStatus.Timeout, "AlgorithmExecutionTimeout");
        builder.Emit(PerformanceEventKind.CycleCompleted, Correlation(correlation), 7, 4, at: trigger + 7);
        builder.Advance(trigger + 7);
        return builder.Build();
    }

    private static ExecutionCorrelationId Correlation(Guid value) => new(ExecutionKind.Production, value);

    private static string FrameHash(int ordinal) => new((char)('0' + ordinal), 64);

    private static string[] Codes(PerformanceScenarioReport report) =>
        report.Failures.Select(item => item.Code).ToArray();

    private static string[] AggregateCodes(PerformanceAggregateReport report) =>
        report.Failures.Select(item => item.Code).ToArray();

    private static PerformanceScenario Scenario(string id, PerformanceScenarioKind kind, int measuredCycles,
        int warmupCycles, string?[] frameHashes, PerformanceExpectedFailure[]? expectedFailures = null,
        int requiredStops = 0, int requiredRecoveries = 0, int burstSize = 1)
    {
        return new PerformanceScenario(id, kind, HashA, PerformanceFrameManifest.ComputeHash(frameHashes), HashC,
            measuredCycles, warmupCycles, TimeSpan.FromMilliseconds(1), TimeSpan.FromMinutes(10),
            TimeSpan.FromMilliseconds(10), burstSize,
            burstSize > 1 ? TimeSpan.FromMilliseconds(2) : TimeSpan.Zero,
            requiredStops, requiredRecoveries, expectedFailures ?? Array.Empty<PerformanceExpectedFailure>(),
            frameHashes, TimeSpan.FromSeconds(2));
    }

    private static PerformanceScenario FillerScenario(PerformanceScenarioKind kind)
    {
        var hashes = new string?[] { HashA };
        return Scenario("Performance.V157.Filler." + kind, kind, 1, 0, hashes,
            requiredStops: kind == PerformanceScenarioKind.Recovery ? 1 : 0,
            requiredRecoveries: kind == PerformanceScenarioKind.Recovery ? 1 : 0);
    }

    private static PerformanceContract ContractWith(PerformanceScenario target, PerformanceSpanBudget[] spans,
        PerformanceResourceBudget[]? resources = null)
    {
        var scenarios = new List<PerformanceScenario> { target };
        foreach (var kind in Enum.GetValues<PerformanceScenarioKind>())
            if (kind != target.Kind)
                scenarios.Add(FillerScenario(kind));
        return new PerformanceContract("Performance.V157", "1", PerformancePercentileRule.NearestRankV1,
            PerformanceJitterRule.ObservedMaximumMinusMinimumV1, scenarios, spans, resources ?? ResourceBudgets(),
            CaptureBounds(), PerformanceCollectorDescription.Id, PerformanceCollectorDescription.Version,
            PerformanceCollectorDescription.ContentHash, "Release", "X64", HashA, debuggerPresent: false,
            profilerPresent: false, HashB, "FrameworkBaselineOnly");
    }

    private static PerformanceSpanBudget[] SpanBudgets(
        params (PerformanceSpan Span, int MinimumSamples, int MaximumFailures, double P50, double P95, double P99,
            double Max, double Jitter)[] applicable)
    {
        var bySpan = applicable.ToDictionary(item => item.Span);
        return Enum.GetValues<PerformanceSpan>().Select(span => bySpan.TryGetValue(span, out var budget)
            ? new PerformanceSpanBudget(span, true, "PerformanceSpanApplicable", budget.MinimumSamples,
                budget.MaximumFailures, budget.P50, budget.P95, budget.P99, budget.Max, budget.Jitter)
            : new PerformanceSpanBudget(span, false, "PerformanceSpanNotApplicable", 0, 0,
                null, null, null, null, null)).ToArray();
    }

    private static PerformanceResourceBudget[] ResourceBudgets(
        params (PerformanceResource Resource, int MinimumSamples, double Maximum, double MaximumGrowth)[] required)
    {
        var byResource = required.ToDictionary(item => item.Resource);
        return Enum.GetValues<PerformanceResource>().Select(resource => byResource.TryGetValue(resource, out var budget)
            ? new PerformanceResourceBudget(resource, true, "PerformanceResourceRequired", budget.MinimumSamples,
                budget.Maximum, budget.MaximumGrowth)
            : new PerformanceResourceBudget(resource, false, "PerformanceResourceNotRequired", 0, null, null)).ToArray();
    }

    private static PerformanceCaptureBounds CaptureBounds() => new(maximumEvents: 4096, maximumResourceSamples: 256,
        maximumBytes: 1 << 20, resourceSampleInterval: TimeSpan.FromMilliseconds(10),
        resourceSampleTimeout: TimeSpan.FromSeconds(1), shutdownTimeout: TimeSpan.FromSeconds(1),
        maximumFrameHashBytes: 1 << 20);

    private sealed class CaptureBuilder
    {
        private readonly PerformanceContract _contract;
        private readonly PerformanceScenario _scenario;
        private readonly Guid _epoch = Guid.NewGuid();
        private readonly List<PerformanceEvent> _events = new();
        private readonly List<PerformanceResourceSample> _samples = new();
        private readonly List<PerformanceDurableFact> _facts = new();
        private readonly List<PerformanceFrameObservation> _frames = new();
        private readonly List<PerformanceCycleBinding> _bindings = new();
        private readonly long _startedAt = 1_000;
        private readonly uint _controllerEpoch = 7;
        private long _clock = 1_000;
        private long _eventSequence;
        private long _sampleSequence;
        private long _factPosition;

        internal CaptureBuilder(PerformanceContract contract, PerformanceScenario scenario)
        {
            _contract = contract;
            _scenario = scenario;
        }

        internal long Clock => _clock;

        internal CaptureBuilder Advance(long ticks)
        {
            _clock += ticks;
            return this;
        }

        internal CaptureBuilder Ready()
        {
            Emit(PerformanceEventKind.ReadyAsserted);
            return this;
        }

        internal CaptureBuilder Emit(PerformanceEventKind kind, ExecutionCorrelationId? correlation = null,
            uint? controllerEpoch = null, uint? controllerSequence = null,
            PerformanceObservationOutcome outcome = PerformanceObservationOutcome.Observed,
            string? reason = null, long? at = null)
        {
            _events.Add(new(++_eventSequence, _epoch, at ?? _clock, kind, correlation, controllerEpoch,
                controllerSequence, outcome, reason));
            return this;
        }

        internal CaptureBuilder EmitWithSequenceGap(PerformanceEventKind kind,
            ExecutionCorrelationId? correlation = null)
        {
            _eventSequence++;
            return Emit(kind, correlation, at: _clock);
        }

        internal CaptureBuilder Admit(Guid correlation, uint sequence, long at, Guid inspectionId)
        {
            _facts.Add(new(++_factPosition, _epoch, inspectionId, correlation, _controllerEpoch, sequence,
                ProductionInspectionEventKind.Admitted, at, HashA, null, null, null, null,
                "ProductionInspectionAdmitted"));
            return this;
        }

        internal CaptureBuilder Binding(Guid correlation, string? fingerprint)
        {
            _bindings.Add(new(Correlation(correlation), fingerprint));
            return this;
        }

        internal CaptureBuilder Frame(Guid correlation, string hash, long at)
        {
            _frames.Add(new(Correlation(correlation), at, 4, 2, VisionPixelFormat.Mono8, 8, hash,
                PerformanceObservationOutcome.Observed, "PerformanceFrameHashObserved"));
            return this;
        }

        internal CaptureBuilder Sample(long timestamp, params PerformanceResourceValue[] values)
        {
            _samples.Add(new(++_sampleSequence, timestamp, values));
            return this;
        }

        internal CaptureBuilder SampleWithSequenceGap(long timestamp, params PerformanceResourceValue[] values)
        {
            _sampleSequence++;
            return Sample(timestamp, values);
        }

        internal (Guid Correlation, Guid InspectionId) CleanCycle(uint sequence, int triggerToBusyMilliseconds,
            string frameHash, bool emitFrameReady = true, bool observeFrame = true)
        {
            var correlation = Guid.NewGuid();
            var inspectionId = Guid.NewGuid();
            var trigger = _clock;
            // The durable accept and its raw event are emitted first, then TriggerObserved is appended
            // with an earlier timestamp: a span must use timestamps, never array or emission order.
            Emit(PerformanceEventKind.TriggerAccepted, Correlation(correlation), _controllerEpoch, sequence,
                at: trigger + 1);
            Admit(correlation, sequence, trigger + 1, inspectionId);
            Emit(PerformanceEventKind.TriggerObserved, Correlation(correlation), _controllerEpoch, sequence, at: trigger);
            Binding(correlation, HashA);
            var busy = trigger + triggerToBusyMilliseconds;
            Emit(PerformanceEventKind.BusyAsserted, Correlation(correlation), at: busy);
            if (emitFrameReady) Emit(PerformanceEventKind.FrameReady, Correlation(correlation), at: busy + 1);
            if (observeFrame) Frame(correlation, frameHash, busy + 1);
            Emit(PerformanceEventKind.AlgorithmQueued, Correlation(correlation), at: busy + 2);
            Emit(PerformanceEventKind.AlgorithmStarted, Correlation(correlation), at: busy + 3);
            Emit(PerformanceEventKind.AlgorithmReturned, Correlation(correlation), at: busy + 4);
            Emit(PerformanceEventKind.ResultValidationStarted, Correlation(correlation), at: busy + 5);
            Emit(PerformanceEventKind.ResultValidationCompleted, Correlation(correlation), at: busy + 6);
            Emit(PerformanceEventKind.PlcEncodingStarted, Correlation(correlation), at: busy + 7);
            Emit(PerformanceEventKind.PlcEncodingCompleted, Correlation(correlation), at: busy + 8);
            Emit(PerformanceEventKind.ImageStageStarted, Correlation(correlation), at: busy + 9);
            Emit(PerformanceEventKind.ImageStageCompleted, Correlation(correlation), at: busy + 10);
            Emit(PerformanceEventKind.CoreCommitStarted, Correlation(correlation), at: busy + 11);
            Emit(PerformanceEventKind.CoreCommitCompleted, Correlation(correlation), at: busy + 12);
            CoreCommitted(correlation, sequence, busy + 12, inspectionId);
            Emit(PerformanceEventKind.ResultValidAsserted, Correlation(correlation), at: busy + 13);
            Emit(PerformanceEventKind.ResultAckObserved, Correlation(correlation), _controllerEpoch, sequence,
                at: busy + 14);
            Emit(PerformanceEventKind.AcknowledgementReset, Correlation(correlation), _controllerEpoch, sequence,
                at: busy + 15);
            Delivery(correlation, sequence, busy + 15, inspectionId,
                ProductionInspectionEventKind.AcknowledgementReset, "ProductionInspectionAckReset");
            Emit(PerformanceEventKind.CycleCompleted, Correlation(correlation), _controllerEpoch, sequence,
                at: busy + 16);
            Emit(PerformanceEventKind.ReadyAsserted, Correlation(correlation), at: busy + 17);
            _clock = busy + 17;
            return (correlation, inspectionId);
        }

        internal void CoreCommitted(Guid correlation, uint sequence, long at, Guid inspectionId,
            ExecutionStatus status = ExecutionStatus.Success, string reason = "ProductionInspectionCoreCommitted")
        {
            _facts.Add(new(++_factPosition, _epoch, inspectionId, correlation, _controllerEpoch, sequence,
                ProductionInspectionEventKind.CoreCommitted, at, HashA, HashB, HashC, status,
                InspectionDecision.Pass, reason));
        }

        private void Delivery(Guid correlation, uint sequence, long at, Guid inspectionId,
            ProductionInspectionEventKind kind, string reasonCode)
        {
            _facts.Add(new(++_factPosition, _epoch, inspectionId, correlation, _controllerEpoch, sequence, kind, at,
                HashA, null, null, null, null, reasonCode));
        }

        internal PerformanceRawCapture Build(PerformanceEvidenceState state = PerformanceEvidenceState.Sealed,
            long observationDrops = 0, long firstMissingSequence = 0, bool ledgerAvailable = true,
            string ledgerReasonCode = "PerformanceLedgerVerified", string? scenarioId = null,
            string? scenarioHash = null)
        {
            var header = new PerformanceRunHeader(Guid.NewGuid(), _epoch, _contract.ContentHash,
                scenarioId ?? _scenario.Id, scenarioHash ?? _scenario.ContentHash, Frequency, _startedAt,
                DateTimeOffset.UnixEpoch, HashA, HashB, _contract.BuildConfiguration, _contract.ProcessArchitecture,
                _contract.PowerProfileHash, _contract.DebuggerPresent, _contract.ProfilerPresent,
                _contract.DiagnosticProfileHash, _contract.CollectorId, _contract.CollectorVersion,
                _contract.CollectorHash);
            return new PerformanceRawCapture(_contract, header, state, _clock + 1, observationDrops,
                firstMissingSequence,
                state == PerformanceEvidenceState.Sealed ? "PerformanceCaptureSealed" : "PerformanceCaptureIncomplete",
                _events, _samples, _facts, ledgerAvailable, ledgerReasonCode, _frames, _bindings);
        }
    }
}
