using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V135_Q06_HistoryPagesKeepSnapshotAndBoundRunsToSelectedRows()
    {
        await using var harness = await ManualHarness.CreateAsync();

        AssertAccepted(await harness.StartAsync(), "Manual pagination start");
        var ready = await harness.WaitForSnapshotAsync(value =>
            value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Manual pagination session did not become ready");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);

        // The first page freezes the current ledger end.  Its first event is
        // the session admission and therefore has no run projection, even
        // though later session events are already visible at this snapshot.
        var first = await harness.History.QueryAsync(new(
            SessionId: sessionId, PageSize: 1));
        Assert.True(first.Available, first.ReasonCode);
        var firstEvent = Assert.Single(first.Events);
        Assert.Equal(firstEvent.Position, first.NextAfterPosition);
        Assert.True(first.ThroughPosition > firstEvent.Position);
        Assert.Empty(first.Runs);

        var run = await harness.RunAsync(sessionId);
        AssertAccepted(run, "Manual pagination run");
        await harness.WaitForSnapshotAsync(value =>
            value.SessionId == sessionId &&
            value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Manual pagination run did not finish");

        // Reusing both cursor values must continue through the original
        // snapshot and must not expose the run appended after it.
        var frozen = await harness.History.QueryAsync(new(
            SessionId: sessionId,
            AfterPosition: first.NextAfterPosition!.Value,
            ThroughPosition: first.ThroughPosition,
            PageSize: 128));
        Assert.True(frozen.Available, frozen.ReasonCode);
        Assert.Equal(first.ThroughPosition, frozen.ThroughPosition);
        Assert.NotEmpty(frozen.Events);
        Assert.All(frozen.Events, value =>
            Assert.InRange(value.Position, first.NextAfterPosition.Value + 1,
                first.ThroughPosition));
        Assert.DoesNotContain(frozen.Events,
            value => value.CommandCorrelationId == run.CorrelationId);

        // A fresh page sees the appended run and advances its snapshot end.
        var live = await harness.History.QueryAsync(new(
            SessionId: sessionId, PageSize: 128));
        Assert.True(live.Available, live.ReasonCode);
        Assert.True(live.ThroughPosition > first.ThroughPosition);
        Assert.Contains(live.Events,
            value => value.CommandCorrelationId == run.CorrelationId);
        Assert.Single(live.Runs);

        AssertAccepted(await harness.ExitAsync(sessionId,
            ManualInspectionExitMode.Graceful), "Manual pagination exit");
        await harness.WaitForSnapshotAsync(value =>
            value.SessionId == sessionId &&
            value.Phase == ManualInspectionSessionPhase.Closed,
            "Manual pagination session did not close");
    }
}
