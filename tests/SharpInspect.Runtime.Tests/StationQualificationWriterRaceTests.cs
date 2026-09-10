using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V137_R17_RevocationAfterSuccessQueuedBeforeWriterCommitClearsPayload(bool logout)
    {
        var factory = new QualificationWriterRaceFactory();
        await using var harness = await QualificationHarness.CreateAsync(
            qualificationExecutionFactory: factory);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "writer race start");
        await factory.ExecutionEntered.WaitAsync(TimeSpan.FromSeconds(15));
        var running = await harness.WaitForSnapshotAsync(value =>
            value.CurrentRunId is not null, "writer race run was not admitted");
        var sessionId = Assert.IsType<Guid>(running.SessionId);
        using var writerEntered = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        var barrier = harness.Fixture.Store.UpdateIdentityAsync(_ =>
        {
            writerEntered.Set();
            if (!releaseWriter.Wait(TimeSpan.FromSeconds(3)))
                throw new TimeoutException("V137WriterBarrierTimedOut");
            return new IdentityUpdate("V137WriterBarrier", Array.Empty<IdentityAuditEvent>(), NoMutation: true);
        }, CancellationToken.None).AsTask();
        try
        {
            Assert.True(await Task.Run(() => writerEntered.Wait(TimeSpan.FromSeconds(2))));
            factory.ReleaseExecution();
            // Peek the real serialized writer queue without consuming work. This
            // proves Success was proposed after actual algorithm execution but
            // before Core COMMIT. Physical publication must not have started.
            await WaitForQueuedQualificationSuccessAsync(harness.Fixture.Store);
            Assert.Equal(0, harness.Facility.WriteCount);
            Task revocation;
            if (logout)
                revocation = harness.LogoutAsync();
            else
                revocation = harness.Runtime.SubmitAsync(new GracefulProductionStopCommand(
                    Guid.NewGuid(), harness.Invocation())).AsTask();
            releaseWriter.Set();
            var barrierResult = await barrier.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(barrierResult.Committed, barrierResult.ReasonCode);
            await revocation.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            factory.ReleaseExecution();
            releaseWriter.Set();
            await barrier.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var page = await harness.WaitForHistoryAsync(sessionId, value =>
            value.Events.LastOrDefault() is { Terminal: true }, "queued terminal was not restored");
        var run = Assert.Single(page.Runs);
        Assert.Equal(ExecutionStatus.Cancelled, run.ExecutionStatus);
        Assert.Equal(InspectionDecision.Unknown, run.Decision);
        Assert.Null(run.ResultPayloadJson);
        Assert.Null(run.ResultPayloadHash);
        Assert.Null(run.QualificationPayload);
        Assert.Equal(0, harness.Facility.WriteCount);
    }

    private sealed class QualificationWriterRaceFactory : IVisionAlgorithmFactory
    {
        private readonly ManualFactory _inner = new();
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AlgorithmDescriptor Descriptor => _inner.Descriptor;
        internal Task ExecutionEntered => _entered.Task;
        internal void ReleaseExecution() => _released.TrySetResult(true);

        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default) =>
            _inner.ValidateConfigurationAsync(configuration, cancellationToken);

        public async ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) =>
            new GatedAlgorithm(this, await _inner.CreateAsync(configuration, cancellationToken));

        private sealed class GatedAlgorithm : IVisionAlgorithm
        {
            private readonly QualificationWriterRaceFactory _owner;
            private readonly IVisionAlgorithm _inner;
            internal GatedAlgorithm(QualificationWriterRaceFactory owner, IVisionAlgorithm inner)
            { _owner = owner; _inner = inner; }
            public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) =>
                _inner.WarmUpAsync(cancellationToken);
            public async ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
                CancellationToken cancellationToken = default)
            {
                _owner._entered.TrySetResult(true);
                await _owner._released.Task.WaitAsync(cancellationToken);
                return await _inner.ExecuteAsync(context, cancellationToken);
            }
            public ValueTask DisposeAsync() => _inner.DisposeAsync();
        }
    }

    private static async Task WaitForQueuedQualificationSuccessAsync(SqliteCommandStore store)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var queue = typeof(SqliteCommandStore).GetField("_queue", instance)!.GetValue(store)!;
        var reader = queue.GetType().GetProperty("Reader", instance)!.GetValue(queue)!;
        var peek = reader.GetType().GetMethod("TryPeek", instance)!;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var arguments = new object?[] { null };
            if ((bool)peek.Invoke(reader, arguments)!)
            {
                var item = arguments[0]!;
                var work = item.GetType().GetProperty("StationQualificationProgress", instance)!.GetValue(item);
                var request = work?.GetType().GetProperty("Request", instance)?.GetValue(work)
                    as StationQualificationProgressRequest;
                if (request?.Run is { Terminal: true, ExecutionStatus: ExecutionStatus.Success }) return;
            }
            await Task.Delay(5);
        }
        Assert.Fail("No successful qualification terminal reached the blocked serialized writer.");
    }
}
