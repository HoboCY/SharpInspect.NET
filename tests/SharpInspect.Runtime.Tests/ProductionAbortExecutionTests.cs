using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Frames;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class AlgorithmExecutionTests
{
    [Fact]
    public async Task V145_C01_AbortBeforeInvocationConsumesOnlyTheCancelledProductionFrame()
    {
        var contract = CreateContract("V145C01");
        var algorithm = new TestAlgorithm((_, _) => ValueTask.FromResult(Result(contract, InspectionDecision.Pass)));
        await using var fixture = await PrepareAsync(contract, algorithm);
        await using var engine = new AlgorithmExecutionService(ExecutionOptions());
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));
        var cancelled = CopyFrame(pool, ExecutionKind.Production);
        var correlation = cancelled.Frame.Metadata.Correlation;
        engine.RequestProductionCancellation(correlation);

        var rejected = await engine.ExecuteProductionOwnedAsync(fixture.Prepared, cancelled,
            ExecutionRequest(TimeSpan.FromSeconds(1)), correlation, CancellationToken.None);

        Assert.False(rejected.Executed);
        Assert.Equal("AlgorithmExecutionCancelledBeforeStart", rejected.ReasonCode);
        Assert.Equal(0, algorithm.ExecuteCalls);
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
        var next = CopyFrame(pool, ExecutionKind.Production);
        var nextCorrelation = next.Frame.Metadata.Correlation;
        var completed = await engine.ExecuteProductionOwnedAsync(fixture.Prepared, next,
            ExecutionRequest(TimeSpan.FromSeconds(1)), nextCorrelation, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Success, completed.Outcome?.ExecutionStatus);
        Assert.Equal(1, algorithm.ExecuteCalls);
    }

    [Fact]
    public async Task V145_C02_ProductionAbortFixesOutcomeBeforeAsynchronousConsumerNotification()
    {
        var contract = CreateContract("V145C02");
        var entered = NewSignal();
        var released = NewSignal();
        var algorithm = new TestAlgorithm(async (_, _) =>
        {
            entered.TrySetResult(true);
            await released.Task;
            return Result(contract, InspectionDecision.Pass);
        });
        await using var fixture = await PrepareAsync(contract, algorithm);
        await using var engine = new AlgorithmExecutionService(ExecutionOptions());
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));
        var lease = CopyFrame(pool, ExecutionKind.Production);
        var borrowed = lease.Frame;
        var correlation = borrowed.Metadata.Correlation;
        var pending = engine.ExecuteProductionOwnedAsync(fixture.Prepared, lease,
            ExecutionRequest(TimeSpan.FromSeconds(1)), correlation, CancellationToken.None).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            engine.RequestProductionCancellation(correlation);
            var attempt = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ExecutionStatus.Cancelled, attempt.Outcome?.ExecutionStatus);
            Assert.Equal(InspectionDecision.Unknown, attempt.Outcome?.Decision);
            Assert.True(borrowed.IsLoanActive);
            Assert.Equal(1, pool.GetSnapshot().OutstandingLeases);
            released.TrySetResult(true);
            await fixture.Prepared.DisposeAsync();
            Assert.False(borrowed.IsLoanActive);
            Assert.Null(attempt.Outcome!.ValidatedResult);
        }
        finally { released.TrySetResult(true); }
    }
}
