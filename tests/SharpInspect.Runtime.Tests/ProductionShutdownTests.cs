using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Cycles;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V145_H04_MissingAckExhaustsOneShutdownBudgetAndRetainsTheUnconfirmedResult()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        peer.AutoAcknowledge = false;
        var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        try
        {
            await PrepareProductionAsync(harness, issuer);
            await ArmProductionAsync(harness);
            await WaitProductionAsync(harness, state => state.Ready, "Ready before missing ACK shutdown");
            peer.RaiseTrigger(61, 1);
            await WaitProductionAsync(harness, state => state.Handshake == HandshakePhase.AwaitingResultAck,
                "Unconfirmed result before shutdown");
            var before = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
            var stopId = Guid.NewGuid();
            Assert.Equal(CommandDisposition.Accepted, (await harness.Runtime.SubmitAsync(
                new GracefulProductionStopCommand(stopId, new(CommandSource.PhysicalConsole)))).Disposition);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                harness.StopRuntimePreservingFixtureAsync().WaitAsync(TimeSpan.FromSeconds(9)));
            Assert.Equal("ProductionInspectionShutdownIncomplete", error.Message);
            Assert.InRange(elapsed.Elapsed.TotalSeconds, 4, 8);
            Assert.True(peer.RuntimeResultValid);
            Assert.Equal(1, peer.ResultValidHighCount);
            var state = await harness.Runtime.GetSnapshotAsync();
            Assert.False(state.Ready);
            Assert.NotEqual(RuntimeLifecycle.Stopped, state.Lifecycle);
            var after = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
            Assert.True(after.Available, after.ReasonCode);
            Assert.True(after.RecoveryRequired);
            Assert.Equal(before.Latest!.Core!.ContentHash, after.Latest!.Core!.ContentHash);
            var audit = await new SqliteCommandTraceQuery(harness.Fixture.Options).QueryAsync(new(CorrelationId: stopId));
            Assert.DoesNotContain(audit.Records, value => value.Phase == CommandAuditPhase.Completed);
        }
        finally
        {
            // The failed shutdown remains a faulted operation by design; cleanup must
            // not mistake a second Dispose call for a successful station shutdown.
            try { await harness.DisposeAsync(); }
            catch (InvalidOperationException exception) when (exception.Message == "ProductionInspectionShutdownIncomplete") { }
            await harness.Fixture.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V145_H02_ControlledShutdownPreservesAnAlreadyPublishedResultUntilRealAck(bool awaitAckLow)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        peer.AutoAcknowledge = awaitAckLow;
        peer.AutoClearAcknowledge = !awaitAckLow;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Ready before shutdown in handshake");
        peer.RaiseTrigger(61, 1);
        await WaitProductionAsync(harness, state => state.Handshake == (awaitAckLow
            ? HandshakePhase.AwaitingAckReset : HandshakePhase.AwaitingResultAck), "Handshake before shutdown");
        var prior = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
        var core = prior.Latest!.Core!;
        var shutdown = harness.StopRuntimePreservingFixtureAsync();
        Assert.False(shutdown.IsCompleted);
        Assert.False((await harness.Runtime.GetSnapshotAsync()).Ready);
        Assert.Equal(!awaitAckLow, peer.RuntimeResultValid);
        Assert.Equal(awaitAckLow, peer.ControllerResultAck);
        if (awaitAckLow)
        {
            peer.AutoClearAcknowledge = true;
            peer.ResetResultAcknowledgement();
        }
        else peer.AcknowledgeResult();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        var after = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
        Assert.False(after.RecoveryRequired);
        Assert.Equal(core.ContentHash, after.Latest!.Core!.ContentHash);
        Assert.Equal(ExecutionStatus.Success, after.Latest.Core.ExecutionStatus);
        Assert.False(peer.RuntimeResultValid);
        Assert.Equal(1, peer.ResultValidHighCount);
        Assert.Equal(RuntimeLifecycle.Stopped, (await harness.Runtime.GetSnapshotAsync()).Lifecycle);
    }

    [Fact]
    public async Task V145_H03_ShutdownCannotCancelTheFixedSuccessWaitingForItsDurableCommit()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Ready before commit shutdown");
        harness.Factory.HoldExecution();
        peer.RaiseTrigger(61, 1);
        await harness.Factory.ExecutionEntered.WaitAsync(TimeSpan.FromSeconds(10));
        var runtime = Assert.IsType<StationRuntime>(harness.Runtime);
        var gate = GetCommandGate(runtime);
        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(5)));
        Task? shutdown = null;
        try
        {
            harness.Factory.ReleaseExecution();
            var owner = typeof(StationRuntime).GetField("_productionInspectionOwner",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(runtime)!;
            var cycle = (InspectionCycleCoordinator<PlcResultPayloadSnapshot>)owner.GetType().GetProperty("Coordinator",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(owner)!;
            await WaitConditionAsync(() => cycle.Phase == InspectionCyclePhase.Committing,
                "The fixed Success must reach the blocked durable commit boundary");
            shutdown = harness.StopRuntimePreservingFixtureAsync();
            Assert.False(shutdown.IsCompleted);
            Assert.Equal(0, peer.ResultValidHighCount);
        }
        finally { gate.Release(); harness.Factory.ReleaseExecution(); }
        await shutdown!.WaitAsync(TimeSpan.FromSeconds(10));
        var history = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).QueryAsync(new(PageSize: 128));
        Assert.Equal(ExecutionStatus.Success, Assert.Single(history.Events, value =>
            value.Kind == ProductionInspectionEventKind.CoreCommitted).Core!.ExecutionStatus);
        Assert.Contains(history.Events, value => value.Kind == ProductionInspectionEventKind.AcknowledgementReset);
        Assert.DoesNotContain(history.Events, value => value.Kind == ProductionInspectionEventKind.FaultTerminated);
        Assert.Equal(1, peer.ResultValidHighCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V145_H01_ControlledShutdownDeliversCancelledWorkAndNeverCompletesAnInterruptedStop(bool stopPending)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Ready before controlled shutdown");
        harness.Factory.HoldExecution(cooperativeCancellation: true);
        Guid? stopId = null;
        try
        {
            peer.RaiseTrigger(61, 1);
            await harness.Factory.ExecutionEntered.WaitAsync(TimeSpan.FromSeconds(10));
            if (stopPending)
            {
                stopId = Guid.NewGuid();
                var stop = await harness.Runtime.SubmitAsync(new GracefulProductionStopCommand(stopId.Value,
                    new CommandInvocation(CommandSource.PhysicalConsole)));
                Assert.Equal(CommandDisposition.Accepted, stop.Disposition);
            }
            await harness.StopRuntimePreservingFixtureAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var history = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options)
                .QueryAsync(new(PageSize: 128));
            Assert.True(history.Available, history.ReasonCode);
            var core = Assert.Single(history.Events, value => value.Kind == ProductionInspectionEventKind.CoreCommitted).Core!;
            Assert.Equal(ExecutionStatus.Cancelled, core.ExecutionStatus);
            Assert.Equal(InspectionDecision.Unknown, core.Decision);
            Assert.Contains(history.Events, value => value.Kind == ProductionInspectionEventKind.AcknowledgementReset);
            Assert.DoesNotContain(history.Events, value => value.Kind == ProductionInspectionEventKind.FaultTerminated);
            Assert.Equal(1, peer.ResultValidHighCount);
            Assert.False(peer.RuntimeResultValid);
            var state = await harness.Runtime.GetSnapshotAsync();
            Assert.Equal(RuntimeLifecycle.Stopped, state.Lifecycle);
            Assert.False(state.Ready);
            if (stopId is { } correlation)
            {
                var audit = await new SqliteCommandTraceQuery(harness.Fixture.Options)
                    .QueryAsync(new(CorrelationId: correlation));
                Assert.DoesNotContain(audit.Records, fact => fact.Phase == CommandAuditPhase.Completed);
                Assert.Contains(audit.Records, fact => fact.Phase == CommandAuditPhase.Failed);
            }
        }
        finally { harness.Factory.ReleaseExecution(); }
    }
}
