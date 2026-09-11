using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V147_R16_MaintenanceChangeAfterReadyAckPreservesHistoryAndClearsPhysicalReady(bool manual)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)),
            productionArming: new ProductionArmStoreOptions(), productionArmStatusBinding: V147ArmStatusBinding,
            postActivationArmPolicy: V147AutomaticRearm(),
            productionArmMaintenance: new TestProductionArmMaintenanceProvider(),
            productionArmMaintenanceState: manual ? ProductionArmMaintenanceState.ManualArmRequired :
                ProductionArmMaintenanceState.ManualArmConfirmed);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        if (manual)
        {
            peer.HoldNextProductionReadyWrite();
            await SubmitManualMaintenanceArmAsync(harness);
        }
        else
        {
            await EnableRecipeChangeAsync(harness, (await harness.Activations.ReadCurrentAsync()).Record!);
            await ArmProductionAsync(harness);
            await RequestRecipeChangeWithFreshBusyAttemptsAsync(harness, peer, 241);
            await WaitRecipeResponseAsync(harness, peer);
            Assert.Equal((ushort)RecipeChangeOutcome.Succeeded, peer.RecipeChangeResponse.Outcome);
            peer.HoldNextProductionReadyWrite();
            await CompleteRecipeChangeAsync(harness, peer, new SqliteRecipeSelectionQuery(harness.Fixture.Options));
        }
        await peer.WaitForProductionReadyWriteHeldAsync().WaitAsync(TimeSpan.FromSeconds(20));
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
            peer.SetControllerCycle(61, 402);
            peer.ReleaseProductionReadyWrite();
            // The observer sample gate opens only after the Ready ACK callback.
            await peer.WaitForControllerSampleAsync(61, 402, trigger: false).WaitAsync(TimeSpan.FromSeconds(2));
            var readyObservation = await harness.Runtime.GetSnapshotAsync();
            Assert.True(peer.PhysicalProductionReady, "Ready disappeared before maintenance change: " +
                readyObservation.ArmState + "/" + readyObservation.Recovery + "/" + readyObservation.ProductionArming +
                ";command=" + readyObservation.LastCommand + ";communication=" + readyObservation.PlcCommunication);
            harness.Maintenance!.SetState(ProductionArmMaintenanceState.InProgress);
            Assert.Equal(0, peer.ResultValidHighCount);
        }
        finally
        {
            hold.CommandText = "ROLLBACK;";
            await hold.ExecuteNonQueryAsync();
            peer.ReleaseProductionReadyWrite();
        }
        var history = await WaitForArmAttemptHistoryAsync(harness, page => page.Events.Any(value => value.StatusDelivery),
            "The acknowledged Ready attempt did not finish its historical audit");
        var confirmed = Assert.Single(history.Events, value => value.Kind == ProductionArmEventKind.ReadyConfirmed);
        Assert.NotNull(confirmed.ReadyReceipt);
        Assert.Equal(manual ? ProductionArmCause.ManualMaintenanceArm : ProductionArmCause.PlcActivation, confirmed.Cause);
        Assert.DoesNotContain(history.Events, value => value.Kind is ProductionArmEventKind.Rejected or ProductionArmEventKind.Failed);
        await WaitConditionAsync(() => !peer.PhysicalProductionReady,
            "Maintenance revoked current Ready but its physical PLC bit remained set");
        var state = await harness.Runtime.GetSnapshotAsync();
        Assert.False(state.Ready);
        Assert.Equal(ProductionArmState.Disarmed, state.ArmState);
        Assert.Equal(RecoveryState.None, state.Recovery);
        Assert.Equal(0, peer.ResultValidHighCount);
        Assert.Null(state.CurrentExecution);
        peer.SetTrigger(false);
    }
}
