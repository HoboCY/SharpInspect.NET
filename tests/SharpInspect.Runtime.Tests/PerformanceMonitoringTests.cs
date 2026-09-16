using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Performance;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class PerformanceReportCalculatorTests
{
    [Fact]
    public async Task V157_M01_TimedOutPhysicalSamplerKeepsItsOnlySlotUntilActualReturn()
    {
        var raw = TwoCycles();
        await using var monitor = new RuntimePerformanceMonitor(new(raw.Contract, HashA, 2), Guid.NewGuid(), null);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        try
        {
            monitor.StartResources(sequence =>
            {
                Interlocked.Increment(ref calls); entered.TrySetResult(true); release.Wait();
                return new(sequence, Stopwatch.GetTimestamp(), new[] { Observed(PerformanceResource.PrivateBytes, 1) });
            }, null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitMonitorAsync(() => monitor.ObservationLossCount > 0);
            Assert.Equal(1, monitor.ReadPerformance().PhysicalResourceOperations);
            await monitor.DisposeAsync();
            Assert.Equal(1, calls);
            Assert.Equal(1, monitor.ReadPerformance().PhysicalResourceOperations);
        }
        finally { release.Set(); }
        await WaitMonitorAsync(() => monitor.ReadPerformance().PhysicalResourceOperations == 0);
        Assert.Equal(1, calls);
        Assert.True(monitor.ObservationLossCount > 0);
    }

    [Fact]
    public void V157_M02_FiniteCaptureNeverOverwritesAndOverflowRemainsVisible()
    {
        var raw = TwoCycles();
        var buffer = new PerformanceCaptureBuffer(raw.Contract, raw.Header);
        var total = raw.Contract.Capture.MaximumEvents + 1;
        for (var index = 0; index < total; index++)
            buffer.Event(PerformanceEventKind.StopRequested, raw.Header.StartedAt + index, null, null,
                PerformanceObservationOutcome.Observed, "TestStop");
        var first = buffer.Seal();
        Assert.True(buffer.Drops > 0);
        Assert.Equal(total, first.Events.Length + buffer.Drops);
        Assert.Equal(1, first.Events[0].Sequence);
        Assert.Equal(first.Events.Length, first.Events[^1].Sequence);
        Assert.Equal(first.Events.Length + 1, buffer.FirstMissingSequence);
        buffer.Event(PerformanceEventKind.StopCompleted, raw.Header.StartedAt, null, null,
            PerformanceObservationOutcome.Observed, "LateStop");
        Assert.Equal(first.Events, buffer.Seal().Events);
    }

    [Fact]
    public void V157_M03_AdvisoryBudgetClearsOnlyAfterFrozenHealthyCycleCount()
    {
        var raw = TwoCycles();
        var monitor = new RuntimePerformanceMonitor(new(raw.Contract, HashA, 2), Guid.NewGuid(), null);
        var at = Stopwatch.GetTimestamp();
        EmitCycle(20); // Frozen TriggerToBusy maximum is 10 ms.
        Assert.True(monitor.BudgetViolated);
        EmitCycle(2);
        Assert.True(monitor.BudgetViolated);
        EmitCycle(2);
        Assert.False(monitor.BudgetViolated);
        Assert.True(monitor.ReadPerformance().BudgetViolationCount > 0);

        void EmitCycle(int duration)
        {
            var correlation = new ExecutionCorrelationId(ExecutionKind.Production, Guid.NewGuid());
            monitor.Observe(PerformanceEventKind.ReadyAsserted, observedAt: at++);
            monitor.Observe(PerformanceEventKind.TriggerAccepted, correlation, observedAt: at++);
            monitor.Observe(PerformanceEventKind.TriggerObserved, correlation, observedAt: at);
            at += Stopwatch.Frequency * duration / 1000;
            monitor.Observe(PerformanceEventKind.BusyAsserted, correlation, observedAt: at++);
            monitor.Observe(PerformanceEventKind.CycleCompleted, correlation, observedAt: at++);
            monitor.Observe(PerformanceEventKind.ReadyAsserted, correlation, observedAt: at++);
        }
    }

    private static async Task WaitMonitorAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task V157_M04_ShutdownTimeoutSealsIncompleteBeforePhysicalSamplerReturns()
    {
        var source = TwoCycles();
        var prior = source.Contract;
        var contract = new PerformanceContract(prior.Id, prior.Version, prior.PercentileRule, prior.JitterRule,
            prior.Scenarios, prior.Spans, prior.Resources, new(4096, 256, 1 << 20, TimeSpan.FromMilliseconds(10),
                TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100), 1 << 20), prior.CollectorId, prior.CollectorVersion,
            prior.CollectorHash, prior.BuildConfiguration, prior.ProcessArchitecture, prior.PowerProfileHash,
            prior.DebuggerPresent, prior.ProfilerPresent, prior.DiagnosticProfileHash, prior.ApprovalRule);
        var header = source.Header with { ContractHash = contract.ContentHash, StartedAt = Stopwatch.GetTimestamp(),
            MonotonicFrequency = Stopwatch.Frequency };
        var monitor = new RuntimePerformanceMonitor(new(contract, HashA, 2, header.ScenarioId), header.RuntimeEpoch, header);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.StartResources(sequence =>
        {
            entered.TrySetResult(true); release.Wait();
            return new(sequence, Stopwatch.GetTimestamp(), Array.Empty<PerformanceResourceValue>());
        }, (endedAt, events, samples, frames, bindings) => Task.FromResult(new PerformanceRawCapture(contract, header,
            PerformanceEvidenceState.Sealed, endedAt, 0, 0, "TestLateSeal", events, samples,
            Array.Empty<PerformanceDurableFact>(), true, "TestLedger", frames, bindings)));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await monitor.DisposeAsync();
            var raw = Assert.IsType<PerformanceRawCapture>(await monitor.ReadCaptureAsync());
            Assert.Equal(PerformanceEvidenceState.Incomplete, raw.State);
            Assert.Equal("PerformanceShutdownTimeout", raw.TerminalReason);
            Assert.Equal(1, monitor.ReadPerformance().PhysicalResourceOperations);
        }
        finally { release.Set(); }
        await WaitMonitorAsync(() => monitor.ReadPerformance().PhysicalResourceOperations == 0);
        Assert.Equal(PerformanceEvidenceState.Incomplete, (await monitor.ReadCaptureAsync())!.State);
    }

    [Fact]
    public void V157_M05_SealWithUnretiredProducerCannotClaimCompleteCapture()
    {
        var raw = TwoCycles();
        var buffer = new PerformanceCaptureBuffer(raw.Contract, raw.Header);
        Assert.True(buffer.TryBeginObservation());
        try
        {
            buffer.Seal();
            Assert.True(buffer.RetirementIncomplete);
            Assert.False(buffer.TryBeginObservation());
        }
        finally { buffer.EndObservation(); }
        buffer.Seal();
        Assert.True(buffer.RetirementIncomplete);
    }

    [Fact]
    public async Task V157_M06_LateLedgerSealCannotReplaceShutdownFailure()
    {
        var source = TwoCycles();
        var prior = source.Contract;
        var contract = new PerformanceContract(prior.Id, prior.Version, prior.PercentileRule, prior.JitterRule,
            prior.Scenarios, prior.Spans, prior.Resources, new(4096, 256, 1 << 20, TimeSpan.FromMilliseconds(10),
                TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), 1 << 20), prior.CollectorId, prior.CollectorVersion,
            prior.CollectorHash, prior.BuildConfiguration, prior.ProcessArchitecture, prior.PowerProfileHash,
            prior.DebuggerPresent, prior.ProfilerPresent, prior.DiagnosticProfileHash, prior.ApprovalRule);
        var header = source.Header with { ContractHash = contract.ContentHash, StartedAt = Stopwatch.GetTimestamp(),
            MonotonicFrequency = Stopwatch.Frequency };
        var monitor = new RuntimePerformanceMonitor(new(contract, HashA, 2, header.ScenarioId), header.RuntimeEpoch, header);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.StartResources(sequence => new(sequence, Stopwatch.GetTimestamp(), Array.Empty<PerformanceResourceValue>()),
            async (endedAt, events, samples, frames, bindings) =>
            {
                entered.TrySetResult(true);
                await release.Task;
                return new PerformanceRawCapture(contract, header, PerformanceEvidenceState.Sealed, endedAt, 0, 0,
                    "TestLateSeal", events, samples, Array.Empty<PerformanceDurableFact>(), true, "TestLedger", frames, bindings);
            });
        try
        {
            var stop = monitor.DisposeAsync().AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await stop;
            Assert.Equal("PerformanceShutdownTimeout", (await monitor.ReadCaptureAsync())!.TerminalReason);
        }
        finally { release.TrySetResult(true); }
        await monitor.DisposeAsync();
        Assert.Equal("PerformanceShutdownTimeout", (await monitor.ReadCaptureAsync())!.TerminalReason);
    }
}
