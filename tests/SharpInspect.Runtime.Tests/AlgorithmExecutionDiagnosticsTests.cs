using System.Collections.Concurrent;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Diagnostics;
using SharpInspect.Runtime.Frames;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class AlgorithmExecutionTests
{
    [Fact, Trait("VerificationId", "V156_E03")]
    public async Task V156_E03_AnOwnerCreatedBeforePolicyActivationUsesThePipelineForLaterExecutions()
    {
        await using var storage = await TraceStoragePolicyRuntimeTests.CreateFixtureAsync();
        var publication = await TraceStoragePolicyRuntimeTests.Service(storage).PublishAsync(
            await TraceStoragePolicyRuntimeTests.AuthorizedCommand(storage, 0, TraceStoragePolicyRuntimeTests.Policy(days: 1)));
        Assert.True(publication.Succeeded); storage.ConfigureDiagnostics(publication.Snapshot!);
        var initialization = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var diagnostics = new RuntimeDiagnosticService(storage.Options, Guid.NewGuid(), initialization.Task, storage.Authorization);
        await using var execution = new AlgorithmExecutionService(ExecutionOptions(), null, diagnosticSource: diagnostics);
        Assert.Null(diagnostics.Pipeline);
        initialization.SetResult(true);
        await diagnostics.Initialization.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(diagnostics.Pipeline);
        var contract = CreateContract("T56LateActivation");
        var algorithm = new TestAlgorithm((_, _) => ValueTask.FromResult(Result(contract, InspectionDecision.Pass)));
        await using var prepared = await PrepareAsync(contract, algorithm);
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));
        var attempt = await execution.ExecuteAsync(prepared.Prepared, CopyFrame(pool), ExecutionRequest(TimeSpan.FromSeconds(1)));
        Assert.Equal(InspectionDecision.Pass, attempt.Outcome!.Decision);
        Assert.True(await diagnostics.Pipeline!.FlushAsync());
        var history = await diagnostics.ReadAsync(new(false, 10, 4096, storage.Invocation()));
        Assert.True(history.Available, history.ReasonCode);
        Assert.Equal(attempt.Outcome.Correlation, Assert.Single(history.Records).Execution);
        Assert.Equal("Runtime.AlgorithmOutcome", Assert.Single(history.Records).Code);
    }

    [Fact, Trait("VerificationId", "V156_E01")]
    public async Task V156_E01_HungDiagnosticOutputDoesNotRewriteLegalResultAndRetainedSinkCannotKeepFrameAlive()
    {
        using var release = new ManualResetEventSlim(); using var entered = new ManualResetEventSlim();
        await using var pipeline = new DiagnosticPipeline(DiagnosticPipelineTests.Policy(timeout: TimeSpan.FromMilliseconds(50)),
            Guid.NewGuid(), _ => { entered.Set(); release.Wait(); return ValueTask.CompletedTask; }, _ => ValueTask.CompletedTask);
        try
        {
            IAlgorithmDiagnosticSink? retained = null;
            var contract = CreateContract("T56Legal");
            var algorithm = new TestAlgorithm((context, _) =>
            {
                retained = context.Diagnostics;
                context.Diagnostics.TryEmit(new("Algorithm.Measurement", new[]
                    { new AlgorithmDiagnosticField("Result", AlgorithmScalarValue.FromEnum("Pass")) }));
                return ValueTask.FromResult(Result(contract, InspectionDecision.Pass));
            });
            await using var fixture = await PrepareAsync(contract, algorithm);
            await using var execution = new AlgorithmExecutionService(ExecutionOptions(), null, diagnostics: pipeline);
            using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));
            var attempt = await execution.ExecuteAsync(fixture.Prepared, CopyFrame(pool), ExecutionRequest(TimeSpan.FromSeconds(1)));
            Assert.True(attempt.Executed); Assert.Equal(ExecutionStatus.Success, attempt.Outcome!.ExecutionStatus);
            Assert.Equal(InspectionDecision.Pass, attempt.Outcome.Decision); Assert.NotNull(attempt.Outcome.ValidatedResult);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
            Assert.IsType<ExecutionDiagnosticScope>(retained);
            Assert.Equal(AlgorithmDiagnosticEmission.Dropped, retained!.TryEmit(new("Algorithm.Measurement")));
            Assert.Equal(0, pipeline.ReadHealth().ActiveExecutionScopes);
            await Task.Delay(75); Assert.True(pipeline.ReadHealth().Unhealthy);
            Assert.Equal(InspectionDecision.Pass, attempt.Outcome.Decision);
        }
        finally { release.Set(); }
    }

    [Fact, Trait("VerificationId", "V156_E02")]
    public async Task V156_E02_AlgorithmExceptionHasOneOwnerDiagnosticAndTheOriginalSanitizedOutcome()
    {
        var safe = new ConcurrentQueue<DiagnosticEnvelope>(); var protectedRecords = new ConcurrentQueue<DiagnosticEnvelope>();
        await using var pipeline = new DiagnosticPipeline(DiagnosticPipelineTests.Policy(), Guid.NewGuid(),
            item => { safe.Enqueue(item); return ValueTask.CompletedTask; },
            item => { protectedRecords.Enqueue(item); return ValueTask.CompletedTask; });
        var contract = CreateContract("T56Error");
        var algorithm = new TestAlgorithm((_, _) => ValueTask.FromException<AlgorithmResult>(new Exception("TOKEN-BAIT")));
        await using var fixture = await PrepareAsync(contract, algorithm);
        await using var execution = new AlgorithmExecutionService(ExecutionOptions(), null, diagnostics: pipeline);
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));
        var attempt = await execution.ExecuteAsync(fixture.Prepared, CopyFrame(pool), ExecutionRequest(TimeSpan.FromSeconds(1)));
        Assert.Equal(ExecutionStatus.Error, attempt.Outcome!.ExecutionStatus);
        Assert.Equal("AlgorithmExecutionError", attempt.Outcome.ReasonCode);
        Assert.True(await pipeline.FlushAsync());
        Assert.Single(safe, item => item.Record.Code == "Runtime.OwnerFault");
        Assert.Single(safe, item => item.Record.Code == "Runtime.AlgorithmOutcome");
        Assert.Equal("HResult", Assert.Single(Assert.Single(protectedRecords).Record.Properties).Name);
        Assert.Equal(0, pipeline.ReadHealth().ActiveExecutionScopes);
    }
}
