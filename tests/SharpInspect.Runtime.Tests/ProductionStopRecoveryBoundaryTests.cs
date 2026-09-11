using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task V145_R01_RecoveryAuthorizationContentionRetriesOnlyRolledBackFactsWithTheSameGrant(int contendedCheck)
    {
        await using var scenario = await ProductionRecoveryScenario.CreateAsync(
            coldBoundary: ProductionInspectionEventKind.CoreCommitted);
        var command = await scenario.AuthorizeAsync(await scenario.CreateCommandAsync("StopRecoveryFence"));
        var snapshot = await scenario.Runtime.GetSnapshotAsync();
        var registry = Assert.IsType<ProductionRecoverySafetyRegistry>(typeof(StationRuntime).GetField(
            "_productionRecoverySafety", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(scenario.Runtime));
        var capture = await registry.CaptureAsync(snapshot.RuntimeEpoch);
        Assert.True(capture.Available, capture.ReasonCode);
        var history = await new SqliteProductionInspectionHistoryQuery(scenario.Harness.Fixture.Options)
            .QueryAsync(new(InspectionId: scenario.InspectionId));
        Assert.True(history.Available, history.ReasonCode);
        var core = Assert.IsType<ProductionInspectionCore>(Assert.Single(history.Events,
            value => value.Kind == ProductionInspectionEventKind.CoreCommitted).Core);
        var admission = core.Admission;
        var observation = new ProductionRecoveryObservation(ProductionRecoveryDeliveryPhase.CoreCommitted,
            ProductionRecoveryUncertaintyKind.ProcessRestart, ProductionRecoveryAcknowledgementObservation.Unknown,
            admission.RuntimeEpoch, admission.ControllerCycle.ControllerEpoch, admission.ControllerCycle.CycleSequence,
            admission.ConnectionGeneration, admission.EndpointBindingHash, admission.PlcProfileHash,
            admission.PlcPolicyHash, core.ContentHash, core.PlcPayload?.ContentHash, core.PlcPayload?.WireContentHash);
        var calls = 0;
        var requests = scenario.Peer.RequestCount;
        var result = await scenario.RuntimeContext.Authorization.AuthorizeProductionRecoveryAsync(command,
            snapshot.RuntimeEpoch, capture, observation, null, () => Interlocked.Increment(ref calls) == contendedCheck
                ? "ProductionRecoveryCommitFenceBusy" : null);
        Assert.True(result.Outcome.Disposition == CommandDisposition.Accepted, result.Outcome.ReasonCode);
        Assert.True(calls > contendedCheck);
        Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);
        Assert.Equal(requests, scenario.Peer.RequestCount);
        var audit = await new SqliteCommandTraceQuery(scenario.Harness.Fixture.Options)
            .QueryAsync(new(CorrelationId: command.CorrelationId));
        Assert.Equal(CommandDisposition.Accepted, Assert.Single(audit.Records).Disposition);
        var persisted = await new SqliteProductionInspectionHistoryQuery(scenario.Harness.Fixture.Options)
            .QueryAsync(new(InspectionId: scenario.InspectionId));
        Assert.Single(persisted.Events, value => value.Kind == ProductionInspectionEventKind.RecoveryRequired);
        Assert.Equal(core.ContentHash, persisted.Events[^1].Core!.ContentHash);
        var duplicate = await scenario.RuntimeContext.Authorization.AuthorizeProductionRecoveryAsync(command,
            snapshot.RuntimeEpoch, capture, observation, null, () => null);
        Assert.Equal(CommandDisposition.Rejected, duplicate.Outcome.Disposition);
        Assert.Equal("DuplicateCorrelationId", duplicate.Outcome.ReasonCode);
    }
}
