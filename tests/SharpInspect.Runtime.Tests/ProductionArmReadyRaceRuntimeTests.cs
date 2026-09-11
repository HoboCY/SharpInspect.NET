using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V147_R13_ImmediateTriggerWaitsForReadyReceiptAndSurvivesItsAuditRecheck()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)),
            productionArming: new ProductionArmStoreOptions(), productionArmStatusBinding: V147ArmStatusBinding,
            postActivationArmPolicy: V147AutomaticRearm(),
            productionArmMaintenance: new TestProductionArmMaintenanceProvider());
        using var issuer = new ProductionTestIssuer();
        await using var observation = new ProductionStateObservation(harness.Runtime);
        await PrepareProductionAsync(harness, issuer);
        var previous = (await harness.Activations.ReadCurrentAsync()).Record!;
        await EnableRecipeChangeAsync(harness, previous);
        await ArmProductionAsync(harness);
        await RequestRecipeChangeWithFreshBusyAttemptsAsync(harness, peer, 191);
        await WaitRecipeResponseAsync(harness, peer);
        Assert.Equal((ushort)RecipeChangeOutcome.Succeeded, peer.RecipeChangeResponse.Outcome);
        peer.HoldNextProductionReadyWrite();
        await CompleteRecipeChangeAsync(harness, peer, new SqliteRecipeSelectionQuery(harness.Fixture.Options));
        await peer.WaitForProductionReadyWriteHeldAsync().WaitAsync(TimeSpan.FromSeconds(20));

        // Delay the actual Ready receipt transaction while leaving WAL readers
        // and the controller observer free. The controller raises Trigger at the
        // physical Ready edge; Run must wait for the receipt and its audit.
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = harness.Fixture.Options.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var hold = connection.CreateCommand();
        hold.CommandText = "BEGIN IMMEDIATE;";
        await hold.ExecuteNonQueryAsync();
        try
        {
            peer.RaiseTrigger(61, 401);
            peer.ReleaseProductionReadyWrite();
            await peer.WaitForControllerSampleAsync(61, 401).WaitAsync(TimeSpan.FromSeconds(2));
            var waiting = await harness.Runtime.GetSnapshotAsync();
            Assert.Null(waiting.CurrentExecution);
            Assert.False(waiting.Busy);
            Assert.Equal(0, peer.ResultValidHighCount);
            Assert.DoesNotContain(peer.ProductionArmStatusWrites,
                value => value.Outcome == (ushort)ProductionArmAttemptOutcome.ReadyConfirmed);
        }
        finally
        {
            hold.CommandText = "ROLLBACK;";
            await hold.ExecuteNonQueryAsync();
            peer.ReleaseProductionReadyWrite();
        }

        try { await WaitForProductionResultAsync(harness, peer, 1); }
        catch (XunitException)
        {
            var failed = await harness.Runtime.GetSnapshotAsync();
            var arms = await new SqliteProductionArmHistoryQuery(harness.Fixture.Options).QueryAsync(new());
            var runs = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).QueryAsync(new(PageSize: 128));
            throw new XunitException($"Immediate trigger: {failed.ArmState}/{failed.Ready}/{failed.Recovery}; " +
                $"arm={failed.ProductionArming?.Outcome}/{failed.ProductionArming?.ReasonCode}; " +
                "arms=" + arms.ReasonCode + ":" + string.Join(";", arms.Events.Select(value => value.Kind + ":" + value.ReasonCode)) +
                ";runs=" + runs.ReasonCode + ":" + string.Join(";", runs.Events.Select(value => value.Kind + ":" + value.ReasonCode)) +
                ";observations=" + observation.Read());
        }
        var history = await WaitForArmAttemptHistoryAsync(harness, value => value.Events.Any(entry =>
            entry.Kind == ProductionArmEventKind.PlcStatusDelivered), "Immediate trigger lost the Ready receipt");
        var confirmed = Assert.Single(history.Events, value => value.Kind == ProductionArmEventKind.ReadyConfirmed);
        Assert.NotNull(confirmed.ReadyReceipt);
        Assert.NotNull(confirmed.InputStability);
        Assert.DoesNotContain(history.Events, value => value.Kind is ProductionArmEventKind.Rejected or ProductionArmEventKind.Failed);
        var cycle = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
        Assert.True(cycle.Available, cycle.ReasonCode);
        Assert.Equal(401u, cycle.Latest!.Core!.Admission.ControllerCycle.CycleSequence);
        peer.SetTrigger(false);
        await WaitConditionAsync(() => peer.AckLowCount == 1, "Immediate trigger result reset");
    }
}
