using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V146_E07_ColdRestartFinalizesInterruptedRequestWithoutActivationOrInventedAcknowledgement(bool decisionPresent)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var oldState = await harness.Runtime.GetSnapshotAsync();
        var previous = (await harness.Activations.ReadCurrentAsync()).Record!.Reference;
        var production = harness.Service<ProductionInspectionOptions>();
        await harness.StopRuntimePreservingFixtureAsync();
        var request = new RecipeChangeRequestEvidence(oldState.RuntimeEpoch, production.Profile.EndpointBindingHash,
            new(production.Profile.Id, production.Profile.Version, production.Profile.ContentHash),
            61, 107, 999, null, RecipeSelectionPolicy.Default.Reference, null, null, DateTimeOffset.UtcNow);
        var observed = await harness.Fixture.Store.AppendRecipeChangeEventAsync(request,
            RecipeChangeEventKind.RequestObserved, null, null, "RecipeChangeRequestObserved", null,
            new StoreDeadline(TimeSpan.FromSeconds(5)), CancellationToken.None);
        Assert.True(observed.Committed, observed.ReasonCode);
        if (decisionPresent)
        {
            var decision = await harness.Fixture.Store.AppendRecipeChangeEventAsync(request,
                RecipeChangeEventKind.DecisionCommitted, RecipeChangeOutcome.RejectedUnknownCode,
                RecipeChangeReason.LocalOperatorOnly, "RecipeChangeLocalOperatorOnly", null,
                new StoreDeadline(TimeSpan.FromSeconds(5)), CancellationToken.None);
            Assert.True(decision.Committed, decision.ReasonCode);
        }
        await harness.Fixture.RestartStoreAsync();
        var clock = new VirtualCameraClock(DateTimeOffset.UtcNow);
        await using var pump = ClockPump.Start(clock);
        var provider = ManualHarness.CreateProvider(clock, timeout: false);
        await using var restarted = CreateRestartedManualRuntime(harness, new[] { provider }, clock);
        restarted.ConfigureProductionInspections(production, harness.Fixture.Options,
            harness.Service<AlgorithmExecutionOptions>(), clock);
        var query = new SqliteRecipeSelectionQuery(harness.Fixture.Options);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        RecipeChangeHistoryPage history;
        do
        {
            history = await query.QueryAsync(new RecipeChangeHistoryFilter());
            Assert.True(history.Available, history.ReasonCode);
            if (history.Events.Any(value => value.Kind == RecipeChangeEventKind.ProtocolFault)) break;
            Assert.True(DateTime.UtcNow < deadline, "Interrupted recipe change was not reconciled: " +
                string.Join(",", (await restarted.GetSnapshotAsync()).AdmissionBlockers));
            await Task.Delay(20);
        } while (true);
        Assert.Equal(new[] { RecipeChangeEventKind.RequestObserved, RecipeChangeEventKind.DecisionCommitted,
            RecipeChangeEventKind.ProtocolFault }, history.Events.Select(value => value.Kind));
        var decisionRow = Assert.Single(history.Events, value => value.Kind == RecipeChangeEventKind.DecisionCommitted);
        Assert.Equal(decisionPresent ? RecipeChangeOutcome.RejectedUnknownCode : RecipeChangeOutcome.FailedActivation,
            decisionRow.Outcome);
        Assert.Equal(previous, (await new SqliteRecipeActivationQuery(harness.Fixture.Options).ReadCurrentAsync()).Record!.Reference);
        Assert.False(peer.RecipeChangeResponse.ResponseValid);
        Assert.DoesNotContain(peer.Writes, value => value.StartAddress == 600);
        var state = await restarted.GetSnapshotAsync();
        Assert.NotEqual(oldState.RuntimeEpoch, state.RuntimeEpoch);
        Assert.False(state.Ready);
        Assert.Equal(ProductionArmState.Disarmed, state.ArmState);
    }
}
