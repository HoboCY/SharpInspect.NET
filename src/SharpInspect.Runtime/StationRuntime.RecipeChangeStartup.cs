using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async Task FinalizeInterruptedRecipeChangesAsync(CancellationToken token)
    {
        if (_productionInspectionStoreOptions?.RecipeSelections is null) return;
        Guid epoch;
        lock (_sync) epoch = _snapshot.RuntimeEpoch;
        var query = new SqliteRecipeSelectionQuery(_productionInspectionStoreOptions);
        var interrupted = await query.ReadInterruptedAsync(epoch, token).ConfigureAwait(false);
        foreach (var item in interrupted)
        {
            token.ThrowIfCancellationRequested();
            if (item.Activation is { IsTerminal: false })
                throw new InvalidOperationException("RecipeActivationStartupRecoveryRequired");
            RecipeChangeDecision decision;
            if (item.Decision is { } committed)
                decision = new(committed.Outcome!.Value, committed.Reason!.Value, committed.ReasonCode, committed.Activation);
            else
            {
                decision = item.Activation is { Outcome.Succeeded: true } succeeded
                    ? new(RecipeChangeOutcome.Succeeded, RecipeChangeReason.None, "RecipeActivated", succeeded.Reference)
                    : new(RecipeChangeOutcome.FailedActivation, RecipeChangeReason.CommunicationLost,
                        "RecipeChangeInterruptedByRestart", item.Activation?.Reference);
                var terminal = await AppendRecipeChangeAsync(item.Request, RecipeChangeEventKind.DecisionCommitted,
                    decision, decision.ReasonCode).ConfigureAwait(false);
                if (!terminal.Committed) throw new InvalidOperationException(terminal.ReasonCode);
            }
            if (!item.FaultRecorded)
            {
                var fault = await AppendRecipeChangeAsync(item.Request, RecipeChangeEventKind.ProtocolFault,
                    decision, "RecipeChangeInterruptedByRestart").ConfigureAwait(false);
                if (!fault.Committed) throw new InvalidOperationException(fault.ReasonCode);
            }
        }
        // No activation capability, response write, ACK or reset is synthesized here.
        // The new connection still requires actual zeroed fields and manual arming.
    }
}
