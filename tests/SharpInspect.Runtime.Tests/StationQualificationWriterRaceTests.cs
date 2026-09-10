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
        await using var harness = await QualificationHarness.CreateAsync(blockWrite: true);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "writer race start");
        await harness.Facility.WriteEntered.WaitAsync(TimeSpan.FromSeconds(15));
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
            harness.Facility.ReleaseWrite();
            // Peek the real serialized writer queue without consuming work. This
            // proves Success was already proposed after the physical write;
            // merely blocking the facility would exercise an earlier Abort check.
            await WaitForQueuedQualificationSuccessAsync(harness.Fixture.Store);
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
