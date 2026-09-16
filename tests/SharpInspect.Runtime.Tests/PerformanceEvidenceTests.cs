using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Performance;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class PerformanceReportCalculatorTests
{
    [Fact]
    public void V157_R11_EachBusinessIdentityIsUniqueIndependently()
    {
        var raw = TwoCycles();
        var first = raw.DurableFacts.First();
        var second = raw.DurableFacts.First(value => value.Kind == ProductionInspectionEventKind.Admitted && value.CorrelationId != first.CorrelationId);
        var altered = CopyRaw(raw, facts: raw.DurableFacts.Select(value => value.CorrelationId == second.CorrelationId
            ? value with { ControllerSequence = first.ControllerSequence } : value),
            events: raw.Events.Select(value => value.Correlation?.Value == second.CorrelationId && value.ControllerSequence is not null
                ? value with { ControllerSequence = first.ControllerSequence } : value));
        var report = PerformanceReportCalculator.Calculate(altered);
        Assert.False(report.Passed);
        Assert.Contains("PerformanceBusinessIdentityReused", Codes(report));
        var reusedInspection = CopyRaw(raw, facts: raw.DurableFacts.Select(value => value.CorrelationId == second.CorrelationId
            ? value with { InspectionId = first.InspectionId } : value));
        Assert.Contains("PerformanceBusinessIdentityReused", Codes(PerformanceReportCalculator.Calculate(reusedInspection)));
    }

    [Fact]
    public void V157_R12_ApplicableSpanCannotSelfExemptWithNotApplicable()
    {
        var raw = TwoCycles();
        var first = raw.Events.First(value => value.Kind == PerformanceEventKind.BusyAsserted);
        var altered = CopyRaw(raw, events: raw.Events.Select(value => value.Sequence == first.Sequence
            ? value with { Outcome = PerformanceObservationOutcome.NotApplicable } : value));
        var report = PerformanceReportCalculator.Calculate(altered);
        Assert.False(report.Passed);
        Assert.Contains("PerformanceSpanApplicabilityMismatch", Codes(report));
    }

    [Fact]
    public void V157_R13_DurableCoreFailureCannotBeHiddenBySuccessfulObservations()
    {
        var raw = TwoCycles();
        var altered = CopyRaw(raw, facts: raw.DurableFacts.Select(value => value.Kind == ProductionInspectionEventKind.CoreCommitted
            ? value with { ExecutionStatus = ExecutionStatus.Timeout, ReasonCode = "AlgorithmExecutionTimeout" } : value));
        var report = PerformanceReportCalculator.Calculate(altered);
        Assert.False(report.Passed);
        Assert.All(report.Cycles, cycle => Assert.True(cycle.ExecutionFailed));
        Assert.Contains("PerformanceUnexpectedExecutionFailure", Codes(report));
        var productNg = CopyRaw(raw, facts: raw.DurableFacts.Select(value => value.Kind == ProductionInspectionEventKind.CoreCommitted
            ? value with { Decision = InspectionDecision.Fail } : value));
        Assert.True(PerformanceReportCalculator.Calculate(productNg).Passed);
    }

    [Fact]
    public void V157_R14_CadenceMustBeFrozenAndActuallySatisfied()
    {
        var raw = TwoCycles();
        var secondTrigger = raw.Events.Last(value => value.Kind == PerformanceEventKind.TriggerObserved);
        var altered = CopyRaw(raw, events: raw.Events.Select(value => value.Sequence == secondTrigger.Sequence
            ? value with { Timestamp = value.Timestamp + 3000 } : value));
        var report = PerformanceReportCalculator.Calculate(altered);
        Assert.False(report.Passed);
        Assert.Contains("PerformanceCadenceDeviationExceeded", Codes(report));
    }

    [Fact]
    public void V157_R15_GaugeDecreaseIsNotACounterReset()
    {
        var scenario = Scenario("Gauge", PerformanceScenarioKind.SteadyState, 1, 0, new[] { FrameHash(1) });
        var contract = ContractWith(scenario, SpanBudgets(), ResourceBudgets((PerformanceResource.PrivateBytes, 2, 100, 100)));
        var builder = new CaptureBuilder(contract, scenario);
        builder.Ready(); builder.CleanCycle(1, 2, FrameHash(1));
        builder.Sample(builder.Clock - 2, Observed(PerformanceResource.PrivateBytes, 90));
        builder.Sample(builder.Clock, Observed(PerformanceResource.PrivateBytes, 30));
        var report = PerformanceReportCalculator.Calculate(builder.Build());
        Assert.True(report.Passed, string.Join(",", Codes(report)));
        var resource = Assert.Single(report.Resources, value => value.Resource == PerformanceResource.PrivateBytes);
        Assert.False(resource.ResetObserved);
        Assert.Equal(-60, resource.Growth);
    }

    [Fact]
    public void V157_R16_CanonicalDocumentRecomputesAndRejectsTampering()
    {
        var raw = TwoCycles();
        var bytes = PerformanceEvidenceDocuments.Encode(raw);
        var read = PerformanceEvidenceDocuments.Decode(bytes);
        Assert.True(read.Report.Passed, string.Join(",", Codes(read.Report)));
        Assert.Equal(raw.Contract.ContentHash, read.Raw.Contract.ContentHash);
        Assert.Equal(bytes, PerformanceEvidenceDocuments.Encode(read.Raw));
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Throws<InvalidDataException>(() => PerformanceEvidenceDocuments.Decode(Encoding.UTF8.GetBytes(
            text.Replace("\"Passed\":true", "\"Passed\":false", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => PerformanceEvidenceDocuments.Decode(Encoding.UTF8.GetBytes(
            text.Replace("\"Raw\":", "\"Extra\":1,\"Raw\":", StringComparison.Ordinal))));
        Assert.False(read.Report.ProductionQualificationAuthority);
    }

    [Fact]
    public async Task V157_R17_ArtifactPublicationNeverOverwritesAndReadbackIsBounded()
    {
        var root = Path.Combine(Path.GetTempPath(), "V157-Document-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "performance.json");
        var raw = TwoCycles();
        await PerformanceEvidenceDocuments.WriteNewAsync(path, raw);
        var before = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<IOException>(() => PerformanceEvidenceDocuments.WriteNewAsync(path, raw));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.True((await PerformanceEvidenceDocuments.ReadAsync(path)).Report.Passed);
        foreach (var file in Directory.GetFiles(root)) File.Delete(file);
        Directory.Delete(root);
    }

    private static PerformanceRawCapture TwoCycles()
    {
        var scenario = Scenario("TwoCycles", PerformanceScenarioKind.SteadyState, 2, 0, new[] { FrameHash(1), FrameHash(2) });
        var contract = ContractWith(scenario, SpanBudgets((PerformanceSpan.TriggerToBusy, 1, 0, 10, 10, 10, 10, 10)));
        var builder = new CaptureBuilder(contract, scenario);
        builder.Ready(); builder.CleanCycle(1, 2, FrameHash(1)); builder.CleanCycle(2, 3, FrameHash(2));
        return builder.Build();
    }

    private static PerformanceRawCapture CopyRaw(PerformanceRawCapture raw, IEnumerable<PerformanceEvent>? events = null,
        IEnumerable<PerformanceDurableFact>? facts = null) => new(raw.Contract, raw.Header, raw.State, raw.EndedAt,
        raw.ObservationDrops, raw.FirstMissingSequence, raw.TerminalReason, events ?? raw.Events,
        raw.ResourceSamples, facts ?? raw.DurableFacts, raw.LedgerAvailable, raw.LedgerReasonCode, raw.Frames, raw.CycleBindings);
}
