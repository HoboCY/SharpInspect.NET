using SharpInspect.Abstractions;
using SharpInspect.Runtime.Performance;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V157_P01_AdvisoryBudgetViolationPreservesCorePayloadAndNextTrigger()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            performanceMonitoring: (store, deployment) => PerformanceTestContracts.Create(store, deployment,
                maximumSpanMilliseconds: 0, baselineScenarioId: "SteadyState", cycles: 2));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        var query = harness.Service<IPerformanceMonitoringQuery>();
        var cores = new List<ProductionInspectionCore>();
        var beforeHeads = await harness.Fixture.Store.ReadProductionAdmissionDurableHeadsAsync(CancellationToken.None);
        for (var index = 1; index <= 2; index++)
        {
            await WaitProductionAsync(harness, state => state.Ready, "V157 advisory permits next trigger");
            peer.RaiseTrigger(61, (uint)index);
            await WaitConditionAsync(() => peer.ResultValidHighCount == index, "V157 result is published");
            var read = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
            Assert.True(read.Available, read.ReasonCode);
            var core = Assert.IsType<ProductionInspectionCore>(read.Latest!.Core);
            cores.Add(core);
            Assert.Equal(ExecutionStatus.Success, core.ExecutionStatus);
            Assert.Equal(core.Result!.Decision, core.Decision);
            Assert.Equal(core.Decision, core.PlcPayload!.Decision);
            Assert.Equal(core.Result.ReasonCode, core.PlcPayload.ReasonCode);
            peer.SetTrigger(false);
            await WaitConditionAsync(() => peer.AckLowCount == index, "V157 ack reset completes");
            if (Environment.GetEnvironmentVariable("SHARPINSPECT_V157_EVIDENCE_ROOT") is { } debugRoot)
            {
                Directory.CreateDirectory(debugRoot);
                var state = await harness.Runtime.GetSnapshotAsync();
                var afterHeads = await harness.Fixture.Store.ReadProductionAdmissionDurableHeadsAsync(CancellationToken.None);
                File.WriteAllText(Path.Combine(debugRoot, "advisory-state-" + index + ".json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    BeforeHeads = beforeHeads, AfterHeads = afterHeads, state.ArmState, state.AdmissionBlockers,
                    state.Alarms, state.AlarmState, state.Performance, state.ProductionAdmission
                }));
            }
            await WaitProductionAsync(harness, state => state.Ready && state.CurrentExecution is null,
                "V157 advisory retains armed continuation");
        }
        Assert.True(query.ReadPerformance().BudgetViolation);
        Assert.True(query.ReadPerformance().BudgetViolationCount > 0);
        Assert.Equal(2, cores.Select(core => core.Admission.InspectionId).Distinct().Count());
        var stop = await harness.Runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(),
            new CommandInvocation(CommandSource.PhysicalConsole)));
        Assert.Equal(CommandDisposition.Accepted, stop.Disposition);
        await WaitProductionAsync(harness, state => state.LastCommand?.CorrelationId == stop.CorrelationId &&
            state.LastCommand.State == OperationState.Completed, "V157 governed stop completes");
        await harness.StopRuntimePreservingFixtureAsync();
        var raw = Assert.IsType<PerformanceRawCapture>(await query.ReadCaptureAsync());
        Assert.True(raw.LedgerAvailable, raw.LedgerReasonCode);
        Assert.Equal(2, raw.Events.Count(value => value.Kind == PerformanceEventKind.TriggerAccepted));
        Assert.Equal(2, raw.Events.Count(value => value.Kind == PerformanceEventKind.CycleCompleted));
        Assert.Equal(2, raw.Frames.Count);
        Assert.All(raw.Frames, frame => Assert.Equal(PerformanceObservationOutcome.Observed, frame.Outcome));
        Assert.Contains(raw.Events, value => value.Kind == PerformanceEventKind.BudgetViolation && value.Correlation is not null);
        Assert.Contains(raw.Events, value => value.Kind == PerformanceEventKind.StopCompleted);
        Assert.All(cores, core => Assert.Contains(raw.DurableFacts,
            fact => fact.CoreHash == core.ContentHash && fact.PayloadHash == core.PlcPayload!.ContentHash));
        Assert.Contains(raw.ResourceSamples.SelectMany(value => value.Values), value =>
            value.Resource == PerformanceResource.NativeMemoryBytes && value.Value is null &&
            value.Outcome == PerformanceObservationOutcome.Unknown);
        WriteV157RawEvidence("advisory-budget", raw);
    }

    [Fact]
    public async Task V157_P02_BlockNewTriggersFinishesAcceptedCycleWithoutRewritingResult()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            performanceImpact: ProductionImpact.BlockNewTriggers,
            performanceMonitoring: (store, deployment) => PerformanceTestContracts.Create(store, deployment, maximumSpanMilliseconds: 0));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "V157 block case initial ready");
        peer.RaiseTrigger(61, 1);
        await WaitConditionAsync(() => peer.ResultValidHighCount == 1, "V157 budget does not cancel accepted result");
        peer.SetTrigger(false);
        await WaitConditionAsync(() => peer.AckLowCount == 1, "V157 budget permits accepted acknowledgement");
        await WaitProductionAsync(harness, state => state.CurrentExecution is null && !state.Busy && !state.Ready,
            "V157 subsequent work is blocked");
        Assert.True(harness.Service<IPerformanceMonitoringQuery>().ReadPerformance().BudgetViolation);
        peer.RaiseTrigger(61, 2);
        await Task.Delay(200);
        var page = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).QueryAsync(new(PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        var core = Assert.Single(page.Events, value => value.Kind == ProductionInspectionEventKind.CoreCommitted).Core!;
        Assert.Equal(ExecutionStatus.Success, core.ExecutionStatus);
        Assert.Equal(core.Result!.Decision, core.Decision);
        Assert.Equal(core.Decision, core.PlcPayload!.Decision);
        Assert.Single(page.Events, value => value.Kind == ProductionInspectionEventKind.Admitted);
        Assert.Equal(1, peer.ResultValidHighCount);
    }

    private static void WriteV157RawEvidence(string name, PerformanceRawCapture raw)
    {
        var root = Environment.GetEnvironmentVariable("SHARPINSPECT_V157_EVIDENCE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        // Development evidence only. The final exporter will bind and verify the canonical report package.
        File.WriteAllText(Path.Combine(root, name + ".json"), System.Text.Json.JsonSerializer.Serialize(raw));
        File.WriteAllBytes(Path.Combine(root, name + ".performance.json"), PerformanceEvidenceDocuments.Encode(raw));
    }

    [Fact]
    public async Task V157_P03_FixedVirtualRuntimeCaptureRoundTripsEverySpanAndResource()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            performanceMonitoring: (store, deployment) => PerformanceTestContracts.Create(store, deployment,
                baselineScenarioId: "SteadyState", cycles: 2));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        var query = harness.Service<IPerformanceMonitoringQuery>();
        for (var index = 1; index <= 2; index++)
        {
            await WaitProductionAsync(harness, state => state.Ready, "V157 baseline ready");
            peer.RaiseTrigger(61, (uint)index);
            await WaitConditionAsync(() => peer.ResultValidHighCount == index, "V157 baseline publication");
            peer.SetTrigger(false);
            await WaitConditionAsync(() => peer.AckLowCount == index, "V157 baseline acknowledgement");
            await WaitProductionAsync(harness, state => state.Ready && state.CurrentExecution is null, "V157 baseline retired");
        }
        var stop = await harness.Runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(),
            new CommandInvocation(CommandSource.PhysicalConsole)));
        await WaitProductionAsync(harness, state => state.LastCommand?.CorrelationId == stop.CorrelationId &&
            state.LastCommand.State == OperationState.Completed, "V157 baseline stop");
        await harness.StopRuntimePreservingFixtureAsync();
        var raw = Assert.IsType<PerformanceRawCapture>(await query.ReadCaptureAsync());
        WriteV157RawEvidence("fixed-virtual-runtime", raw);
        var read = PerformanceEvidenceDocuments.Decode(PerformanceEvidenceDocuments.Encode(raw));
        Assert.True(read.Report.Passed, string.Join(",", read.Report.Failures.Select(value => value.Code + ":" + value.Detail)));
        Assert.Equal(Enum.GetValues<PerformanceSpan>().Length, read.Report.Spans.Count);
        Assert.Equal(Enum.GetValues<PerformanceResource>().Length, read.Report.Resources.Count);
        Assert.Equal(2, read.Report.Cycles.Count);
        Assert.False(read.Report.ProductionQualificationAuthority);
        var aggregate = PerformanceReportCalculator.Aggregate(raw.Contract, new[] { read.Report });
        Assert.False(aggregate.Passed);
        Assert.Equal(5, aggregate.Scenarios.Count(value => value.Status == PerformanceReportStatus.NotRun));
    }

    [Fact]
    public async Task V157_P04_AuthorizedColdRecoveryIsObservedWithoutGrantingBaselineQualification()
    {
        await using var scenario = await ProductionRecoveryScenario.CreateAsync(capturePerformance: true);
        var query = scenario.Runtime.PerformanceQuery;
        var command = await scenario.CreateCommandAsync("V157ColdRecovery");
        var authorized = await scenario.AuthorizeAsync(command);
        var result = await scenario.Runtime.SubmitAsync(authorized);
        Assert.Equal(CommandDisposition.Accepted, result.Disposition);
        await WaitRecoveryStateAsync(scenario.Runtime, state => state.Recovery == RecoveryState.None &&
            state.ArmState == ProductionArmState.Disarmed && !state.Ready, "V157 cold recovery completed");
        var history = await new SqliteProductionInspectionHistoryQuery(scenario.Harness.Fixture.Options).ReadAsync(scenario.InspectionId);
        Assert.Equal(ProductionInspectionEventKind.RecoveryCompleted, history.Latest!.Kind);
        await scenario.Runtime.DisposeAsync();
        var raw = Assert.IsType<PerformanceRawCapture>(await query.ReadCaptureAsync());
        Assert.Contains(raw.Events, value => value.Kind == PerformanceEventKind.RecoveryCompleted);
        WriteV157RawEvidence("cold-recovery", raw);
        var report = PerformanceReportCalculator.Calculate(raw);
        // This restarted epoch contains recovery of an earlier epoch, not the full frozen mixed run.
        Assert.False(report.Passed);
        Assert.False(report.ProductionQualificationAuthority);
    }

    [Fact]
    public async Task V157_P05_RealAcquisitionFailureKeepsCoreReasonAndMissingFrameVisible()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(timeout: true, activationReadyDraft: true, productionPeer: peer,
            performanceMonitoring: (store, deployment) => PerformanceTestContracts.Create(store, deployment,
                baselineScenarioId: "SteadyState", cycles: 1));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer); await ArmProductionAsync(harness);
        var query = harness.Service<IPerformanceMonitoringQuery>();
        await WaitProductionAsync(harness, state => state.Ready, "V157 acquisition failure ready");
        peer.RaiseTrigger(61, 1);
        await WaitConditionAsync(() => peer.ResultValidHighCount == 1, "V157 acquisition failure published");
        peer.SetTrigger(false);
        await WaitConditionAsync(() => peer.AckLowCount == 1, "V157 acquisition failure reset");
        await WaitProductionAsync(harness, state => state.CurrentExecution is null, "V157 failed cycle retired");
        await harness.StopRuntimePreservingFixtureAsync();
        var raw = Assert.IsType<PerformanceRawCapture>(await query.ReadCaptureAsync());
        WriteV157RawEvidence("acquisition-timeout", raw);
        var core = Assert.Single(raw.DurableFacts, value => value.Kind == ProductionInspectionEventKind.CoreCommitted);
        Assert.Equal(ExecutionStatus.Timeout, core.ExecutionStatus);
        Assert.Equal("CameraAcquisitionTimeout", core.ReasonCode);
        Assert.Empty(raw.Frames);
        var report = PerformanceReportCalculator.Calculate(raw);
        Assert.False(report.Passed);
        Assert.True(Assert.Single(report.Cycles).ExecutionFailed);
        Assert.Null(Assert.Single(report.Spans, value => value.Span == PerformanceSpan.Acquisition).ObservedMaxMilliseconds);
        Assert.Contains(report.Failures, value => value.Code == "PerformanceUnexpectedExecutionFailure");
    }
}
