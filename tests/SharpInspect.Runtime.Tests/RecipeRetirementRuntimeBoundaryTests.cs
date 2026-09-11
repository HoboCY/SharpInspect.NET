using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData("Execution")]
    [InlineData("AckHigh")]
    [InlineData("AckLow")]
    public async Task V148_R01_RetirementDrainPreservesAcceptedCycleUntilActualQuiescence(string stage)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        peer.AutoAcknowledge = stage == "AckLow";
        peer.AutoClearAcknowledge = stage != "AckLow";
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Ready before lifecycle drain");
        var active = (await harness.Activations.ReadCurrentAsync()).Record!;
        var runtime = Assert.IsType<StationRuntime>(harness.Runtime);
        if (stage == "Execution") harness.Factory.HoldExecution();
        try
        {
            peer.RaiseTrigger(61, 1);
            if (stage == "Execution")
                await harness.Factory.ExecutionEntered.WaitAsync(TimeSpan.FromSeconds(10));
            else
                await WaitProductionAsync(harness, state => state.Handshake ==
                    (stage == "AckHigh" ? HandshakePhase.AwaitingResultAck : HandshakePhase.AwaitingAckReset),
                    "Retained result acknowledgement before lifecycle drain");
            // Exercise Runtime's internal mechanical boundary. Identity and durable
            // lifecycle publication are deliberately not claimed by this fixture.
            var command = new RetireReleasedRecipeCommand(Guid.NewGuid(), new(CommandSource.PhysicalConsole),
                active.Candidate, active.ReleaseId, active.ReleaseRecordContentHash, active.Reference,
                "V148 mechanical quiescence boundary");
            using var lease = await runtime.ReserveRecipeRetirementAsync(command, CancellationToken.None);
            Assert.True(lease.Available, lease.Failure);
            var drain = lease.DrainAsync(TimeSpan.FromSeconds(20), CancellationToken.None).AsTask();
            await Task.Delay(100);
            Assert.False(drain.IsCompleted, "The held execution or acknowledgement must retain the cycle.");
            var waiting = await runtime.GetSnapshotAsync();
            Assert.False(waiting.Ready);
            Assert.Equal(ProductionArmState.Disarmed, waiting.ArmState);
            Assert.Equal(active.Candidate, waiting.ActiveRecipe);
            if (stage is "AckHigh" or "AckLow")
            {
                Assert.Equal(stage == "AckHigh", peer.RuntimeResultValid);
                Assert.Equal(stage == "AckLow", peer.ControllerResultAck);
            }
            using (var competing = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None))
            {
                Assert.False(competing.Available);
                Assert.Equal("RecipeActivationInProgress", competing.Failure);
            }
            harness.Factory.ReleaseExecution();
            if (stage != "AckLow")
            {
                await peer.WaitForResultValidAsync().WaitAsync(TimeSpan.FromSeconds(10));
                peer.AcknowledgeResult();
            }
            else
            {
                peer.AutoClearAcknowledge = true;
                peer.ResetResultAcknowledgement();
            }
            peer.SetTrigger(false);
            Assert.Null(await drain.WaitAsync(TimeSpan.FromSeconds(20)));
            // The writer fence deliberately refuses synchronous Runtime lock
            // contention. Retry only that transient refusal outside the command gate;
            // the same quiescence predicate must eventually pass without a bypass.
            var fenceDeadline = new StoreDeadline(TimeSpan.FromSeconds(5));
            string? fenceReason;
            do
            {
                using (var commit = await lease.EnterCommitAsync(CancellationToken.None))
                    fenceReason = commit.GetBlocker();
                if (fenceReason != "RecipeRetirementRuntimeBusy" || fenceDeadline.Expired) break;
                await Task.Delay(10);
            } while (true);
            Assert.Null(fenceReason);
            lease.PublishTerminal("RecipeRetirementFixtureNoCommit", false);
            lease.Dispose();
            var history = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options)
                .QueryAsync(new(PageSize: 128));
            Assert.True(history.Available, history.ReasonCode);
            Assert.Single(history.Events, value => value.Kind == ProductionInspectionEventKind.CoreCommitted);
            Assert.Contains(history.Events, value => value.Kind == ProductionInspectionEventKind.AcknowledgementReset);
            Assert.DoesNotContain(history.Events, value => value.Kind == ProductionInspectionEventKind.FaultTerminated);
            Assert.Equal(active.Reference, (await harness.Activations.ReadCurrentAsync()).Record?.Reference);
            Assert.Equal(active.Candidate, (await runtime.GetSnapshotAsync()).ActiveRecipe);
            Assert.False((await runtime.GetSnapshotAsync()).Ready);
        }
        finally
        {
            harness.Factory.ReleaseExecution();
            peer.AutoAcknowledge = true;
            peer.AutoClearAcknowledge = true;
            if (peer.RuntimeResultValid) peer.AcknowledgeResult();
            if (peer.ControllerResultAck) peer.ResetResultAcknowledgement();
            peer.SetTrigger(false);
        }
    }

    [Fact]
    public async Task V148_R02_StaleActiveRetirementCannotDisarmCurrentProduction()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Ready before stale lifecycle request");
        var active = (await harness.Activations.ReadCurrentAsync()).Record!;
        var command = new RetireReleasedRecipeCommand(Guid.NewGuid(), new(CommandSource.PhysicalConsole),
            active.Candidate, active.ReleaseId, active.ReleaseRecordContentHash,
            new(active.Position, Guid.NewGuid(), active.ContentHash), "Stale lifecycle request");
        using var lease = await Assert.IsType<StationRuntime>(harness.Runtime)
            .ReserveRecipeRetirementAsync(command, CancellationToken.None);
        Assert.False(lease.Available);
        Assert.Equal("RecipeRetirementActiveChanged", lease.Failure);
        var after = await harness.Runtime.GetSnapshotAsync();
        Assert.True(after.Ready);
        Assert.Equal(ProductionArmState.Armed, after.ArmState);
        Assert.Equal(active.Candidate, after.ActiveRecipe);
    }
}
