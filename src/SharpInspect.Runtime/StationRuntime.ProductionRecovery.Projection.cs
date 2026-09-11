using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async Task<ProductionRecoveryPendingPage> RefreshProductionRecoveryAsync(CancellationToken token)
    {
        var page = _productionInspectionStoreOptions is { } options
            ? await new SqliteProductionRecoveryHistoryQuery(options).QueryPendingAsync(
                cancellationToken: token).ConfigureAwait(false)
            : new ProductionRecoveryPendingPage(false, "ProductionRecoveryHistoryUnavailable", null, 0, null);
        lock (_sync)
        {
            if (!_disposed)
                PublishLocked(_snapshot with
                {
                    ProductionRecovery = page,
                    Evidence = page.Available
                        ? _snapshot.Evidence with { PendingDeliveries = page.PendingCount }
                        : _snapshot.Evidence,
                    Recovery = page.Available && page.PendingCount > 0
                        ? RecoveryState.Required : _snapshot.Recovery
                });
        }
        return page;
    }
}
