using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async Task FinalizeInterruptedProductionArmAttemptsAsync(CancellationToken token)
    {
        if (_productionInspectionStoreOptions?.ProductionArming is null || _audit is not SqliteCommandStore store) return;
        var query = new SqliteProductionArmHistoryQuery(_productionInspectionStoreOptions);
        var pending = new Dictionary<Guid, ProductionArmHistoryEvent>();
        long after = 0;
        long? through = null;
        do
        {
            var page = await query.QueryAsync(new(AfterPosition: after, ThroughPosition: through, PageSize: 128), token)
                .ConfigureAwait(false);
            if (!page.Available) throw new InvalidOperationException(page.ReasonCode);
            through ??= page.ThroughPosition;
            foreach (var value in page.Events)
            {
                if (!value.StatusDelivery)
                    pending[value.AttemptId] = value;
                else pending.Remove(value.AttemptId);
            }
            if (page.NextAfterPosition is not { } next) break;
            if (next <= after) throw new InvalidOperationException("ProductionArmHistoryCursorInvalid");
            after = next;
        } while (true);
        foreach (var previous in pending.Values)
        {
            token.ThrowIfCancellationRequested();
            if (previous.RuntimeEpoch == _snapshot.RuntimeEpoch)
                throw new InvalidOperationException("ProductionArmCurrentEpochPendingOnStartup");
            var terminal = previous;
            if (!previous.Terminal)
            {
                var request = new ProductionArmWriteRequest(previous.AttemptId, previous.RuntimeEpoch, previous.Cause,
                    ProductionArmEventKind.Failed, previous.StationId, previous.StartupPolicy, previous.PostActivationPolicy,
                    previous.DeploymentHash, previous.PlcRequest, previous.Activation, previous.MaintenanceHeadHash,
                    previous.AdmissionGeneration, previous.Report, previous.ExpectedDurableHeads, previous.CurrentDurableHeads,
                    ProductionArmReason.Interrupted, "ProductionArmPreviousRuntimeInterrupted",
                    previous.HumanCommandId, previous.HumanPrincipalId, previous.HumanSessionId,
                    InputStability: previous.InputStability);
                var failed = await store.AppendProductionArmEventAsync(request, new StoreDeadline(_audit.CommitTimeout), token)
                    .ConfigureAwait(false);
                if (!failed.Committed) throw new InvalidOperationException(failed.ReasonCode);
                terminal = failed.Event!;
            }
            // Status is a disposition, not a retry. A new Runtime cannot resend
            // an old epoch's physical status; explicitly release its reserved tail.
            var status = await RecordProductionArmStatusUnavailableAsync(terminal,
                "ProductionArmPreviousRuntimeStatusUnconfirmed", token).ConfigureAwait(false);
            if (!status.Committed) throw new InvalidOperationException(status.ReasonCode);
        }
    }

    private ValueTask<ProductionArmWriteResult> RecordProductionArmStatusUnavailableAsync(
        ProductionArmHistoryEvent terminal, string reasonCode, CancellationToken token = default)
    {
        var request = new ProductionArmWriteRequest(terminal.AttemptId, terminal.RuntimeEpoch, terminal.Cause,
            ProductionArmEventKind.PlcStatusUndelivered, terminal.StationId, terminal.StartupPolicy, terminal.PostActivationPolicy,
            terminal.DeploymentHash, terminal.PlcRequest, terminal.Activation, terminal.MaintenanceHeadHash,
            terminal.AdmissionGeneration, terminal.Report, terminal.ExpectedDurableHeads, terminal.CurrentDurableHeads,
            ProductionArmReason.ConfigurationUnavailable, reasonCode, terminal.HumanCommandId, terminal.HumanPrincipalId,
            terminal.HumanSessionId, terminal.ReadyReceipt, terminal.InputStability);
        return ((SqliteCommandStore)_audit!).AppendProductionArmEventAsync(request,
            new StoreDeadline(_audit!.CommitTimeout), token);
    }
}
