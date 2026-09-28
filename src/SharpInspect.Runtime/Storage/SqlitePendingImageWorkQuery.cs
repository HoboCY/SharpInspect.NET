using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

/// <summary>A page of immutable initial PNG work, reconstructed from verified production Core facts.</summary>
public sealed record PendingImageWorkPage(bool Available, string ReasonCode,
    IReadOnlyList<PendingImageFinalizationWork> Items, long ThroughPosition, long? NextAfterPosition);

/// <summary>
/// Cold-process access to the image outbox. Cursors refer to the production ledger;
/// an empty page may still have a next cursor. No stage file or in-memory lease can
/// create an item through this query. Finalization status is added by the PNG worker.
/// </summary>
public sealed class SqlitePendingImageWorkQuery
{
    private readonly ProductionStoreOptions _options;
    public SqlitePendingImageWorkQuery(ProductionStoreOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<PendingImageWorkPage> QueryAsync(long afterPosition = 0, long? throughPosition = null,
        int pageSize = 128, CancellationToken cancellationToken = default)
    {
        if (_options.ImageEvidence is null)
            return new(false, "ProductionImageEvidenceConfigurationRequired", Array.Empty<PendingImageFinalizationWork>(), 0, null);
        var page = await new SqliteProductionInspectionHistoryQuery(_options).QueryAsync(
            new ProductionInspectionHistoryFilter(AfterPosition: afterPosition, ThroughPosition: throughPosition,
                PageSize: pageSize), cancellationToken).ConfigureAwait(false);
        return new(page.Available, page.Available ? "ProductionImagePendingWorkAvailable" : page.ReasonCode,
            Array.AsReadOnly(page.Events.Where(value => value.Kind == ProductionInspectionEventKind.CoreCommitted &&
                value.Core?.ImageEvidence?.Work is not null).Select(value => value.Core!.ImageEvidence!.Work!).ToArray()),
            page.ThroughPosition, page.NextAfterPosition);
    }
}
