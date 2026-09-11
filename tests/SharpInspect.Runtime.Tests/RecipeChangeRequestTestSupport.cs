using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    private static async Task<uint> RequestRecipeChangeWithFreshBusyAttemptsAsync(ManualHarness harness,
        ModbusQualificationTestServer peer, uint firstSequence, Task? preparationEntered = null)
    {
        var query = new SqliteRecipeSelectionQuery(harness.Fixture.Options);
        var previous = (await harness.Activations.ReadCurrentAsync()).Record!.Reference;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var sequence = checked(firstSequence + (uint)attempt * 1000);
            peer.RequestRecipeChange(sequence, 7);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!peer.RecipeChangeResponse.ResponseValid && preparationEntered?.IsCompleted != true)
            {
                Assert.True(DateTime.UtcNow < deadline, "Recipe request neither responded nor entered preparation");
                await Task.Delay(20);
            }
            if (preparationEntered?.IsCompleted == true) return sequence;
            var response = peer.RecipeChangeResponse;
            var history = await query.QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
            Assert.True(history.Available, history.ReasonCode);
            var decision = Assert.Single(history.Events, value => value.Request.RequestSequence == sequence &&
                value.Kind == RecipeChangeEventKind.DecisionCommitted);
            if (decision.ReasonCode != "RecipeActivationRuntimeBusy") return sequence;

            // Runtime never retries. The controller explicitly acknowledges this fixed failure,
            // completes its all-zero reset, then sends another identity. Retain every failed episode.
            Assert.True(decision.Outcome is RecipeChangeOutcome.RejectedBusy or RecipeChangeOutcome.FailedActivation);
            Assert.Equal((ushort)decision.Outcome!.Value, response.Outcome);
            Assert.Equal((ushort)decision.Reason!.Value, response.Reason);
            Assert.Equal(sequence, response.RequestSequence);
            Assert.Equal(previous, (await harness.Activations.ReadCurrentAsync()).Record!.Reference);
            await CompleteRecipeChangeAsync(harness, peer, query);
            var completed = await query.QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
            Assert.Equal(new[] { RecipeChangeEventKind.RequestObserved, RecipeChangeEventKind.DecisionCommitted,
                RecipeChangeEventKind.ResponsePublished, RecipeChangeEventKind.AcknowledgementObserved,
                RecipeChangeEventKind.ResponseCleared, RecipeChangeEventKind.ResetObserved },
                completed.Events.Where(value => value.Request.RequestSequence == sequence).Select(value => value.Kind));
        }
        Assert.Fail("Eight distinct controller requests were explicitly refused for Runtime contention");
        return 0;
    }
}
