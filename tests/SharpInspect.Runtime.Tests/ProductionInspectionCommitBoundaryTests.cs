using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V142_R10_CoreWriterTimeoutRetainsAdmittedIdentityAndNeverPublishesResult()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Production ready before blocked Core writer");
        harness.PauseClock();
        peer.RaiseTrigger(61, 1);
        var admitted = await WaitForProductionHistoryAsync(harness,
            page => page.Events.Any(value => value.Kind == ProductionInspectionEventKind.Admitted),
            "Production admission before Core writer contention");
        var inspectionId = Assert.Single(admitted.Events).InspectionId;
        using (var competingWriter = SqliteNative.Open(harness.Fixture.Options.DatabasePath, readOnly: false))
        {
            SqliteNative.Execute(competingWriter.Handle!, "BEGIN IMMEDIATE;", new StoreDeadline(TimeSpan.FromSeconds(2)));
            try
            {
                harness.ResumeClock();
                await WaitProductionAsync(harness, state => !state.Ready && !state.Busy &&
                    state.Recovery == RecoveryState.Required, "Core writer deadline must close the production publication gate");
                Assert.Equal(0, peer.ResultValidHighCount);
                Assert.False(peer.RuntimeResultValid);
            }
            finally
            {
                SqliteNative.Execute(competingWriter.Handle!, "ROLLBACK;", new StoreDeadline(TimeSpan.FromSeconds(2)));
            }
        }
        var history = await WaitForProductionHistoryAsync(harness,
            page => page.Events.Any(value => value.Kind == ProductionInspectionEventKind.FaultTerminated),
            "Failed Core cycle terminal remains attributable to its admission");
        Assert.All(history.Events, value => Assert.Equal(inspectionId, value.InspectionId));
        Assert.DoesNotContain(history.Events, value => value.Kind is ProductionInspectionEventKind.CoreCommitted or
            ProductionInspectionEventKind.ResultValidRaised or ProductionInspectionEventKind.ResultAcknowledged);
        Assert.Equal(0, peer.ResultValidHighCount);
        var cold = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadAsync(inspectionId);
        Assert.True(cold.Available, cold.ReasonCode);
        Assert.True(cold.RecoveryRequired);
        Assert.Null(cold.Latest!.Core);
    }
}
