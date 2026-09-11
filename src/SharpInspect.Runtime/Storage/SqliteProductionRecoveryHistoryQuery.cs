using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

/// <summary>Recovery-focused view over the immutable production inspection ledger.</summary>
public sealed class SqliteProductionRecoveryHistoryQuery : IProductionRecoveryHistoryQuery
{
    private readonly SqliteProductionInspectionHistoryQuery _inner;
    private readonly bool _enabled;

    public SqliteProductionRecoveryHistoryQuery(ProductionStoreOptions options,
        Func<CalibrationProfileReference, PublishedCalibrationProfileVersion?>? calibrationResolver = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _enabled = options.ProductionRecovery is not null;
        _inner = new SqliteProductionInspectionHistoryQuery(options, calibrationResolver);
    }

    public async ValueTask<ProductionRecoveryHistoryReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_enabled)
            return new(false, "ProductionRecoveryConfigurationRequired");
        var result = await _inner.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        return ToReadResult(result);
    }

    public async ValueTask<ProductionRecoveryHistoryReadResult> ReadAsync(Guid inspectionId,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled)
            return new(false, "ProductionRecoveryConfigurationRequired");
        var result = await _inner.ReadAsync(inspectionId, cancellationToken).ConfigureAwait(false);
        return ToReadResult(result);
    }

    public async ValueTask<ProductionRecoveryHistoryPage> QueryAsync(
        ProductionInspectionHistoryFilter filter, CancellationToken cancellationToken = default)
    {
        if (!_enabled)
            return new(false, "ProductionRecoveryConfigurationRequired",
                Array.Empty<ProductionInspectionHistoryEvent>(), 0, null);
        var result = await _inner.QueryAsync(filter, cancellationToken).ConfigureAwait(false);
        return new ProductionRecoveryHistoryPage(result.Available, result.ReasonCode,
            result.Events.Where(value => value.Recovery is not null), result.ThroughPosition,
            result.NextAfterPosition, result.RecoveryRequired);
    }

    public ValueTask<ProductionRecoveryPendingPage> QueryPendingAsync(int pageSize = 128,
        long afterPosition = 0, CancellationToken cancellationToken = default) =>
        _enabled ? _inner.QueryPendingAsync(pageSize, afterPosition, cancellationToken) :
        ValueTask.FromResult(new ProductionRecoveryPendingPage(false,
            "ProductionRecoveryConfigurationRequired",
            Array.Empty<ProductionRecoveryPendingItem>(), 0, null));

    private static ProductionRecoveryHistoryReadResult ToReadResult(
        ProductionInspectionHistoryReadResult value) => new(value.Available, value.ReasonCode,
        value.Latest, value.RecoveryRequired);
}
