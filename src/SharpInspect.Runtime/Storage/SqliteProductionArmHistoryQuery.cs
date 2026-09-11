using System.Collections.ObjectModel;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Bounded, read-only access to the verified schema-32 production-arm ledger.
/// The read opens the store read-only, verifies the complete signed chain and
/// the arm history, and fails closed with a reason code on corruption, a
/// missing link, an invalid transition, a duplicate, or a source mismatch.
/// </summary>
public sealed class SqliteProductionArmHistoryQuery : IProductionArmHistoryQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(4, 4);
    private static int _outstanding;

    public SqliteProductionArmHistoryQuery(ProductionStoreOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<ProductionArmHistoryPage> QueryAsync(ProductionArmHistoryFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateFilter(filter.AfterPosition, filter.ThroughPosition, filter.PageSize);
        try
        {
            return await ReadCoreAsync((database, deadline) =>
            {
                var rows = SqliteCommandStore.ReadProductionArmRows(database, _options.ProductionArming!, deadline);
                var through = filter.ThroughPosition ?? rows.Count;
                if (through > rows.Count) throw new InvalidOperationException("ProductionArmThroughPositionInvalid");
                var selected = rows.Where(row => row.Event.Position > filter.AfterPosition &&
                        row.Event.Position <= through &&
                        (filter.AttemptId is null || row.Event.AttemptId == filter.AttemptId))
                    .Take(filter.PageSize + 1).Select(row => row.Event).ToArray();
                var page = selected.Take(filter.PageSize).ToArray();
                return new ProductionArmHistoryPage(true, "ProductionArmHistoryAvailable",
                    new ReadOnlyCollection<ProductionArmHistoryEvent>(page), through,
                    selected.Length > page.Length ? page[^1].Position : null);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, SqliteAuditIntegrityQuery.FaultReason(exception, "ProductionArmHistoryUnavailable"),
                new ReadOnlyCollection<ProductionArmHistoryEvent>(Array.Empty<ProductionArmHistoryEvent>()), 0, null);
        }
    }

    public async ValueTask<ProductionArmHistoryReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReadCoreAsync((database, deadline) =>
            {
                var rows = SqliteCommandStore.ReadProductionArmRows(database, _options.ProductionArming!, deadline);
                var latest = rows.LastOrDefault()?.Event;
                // An attempt without a terminal event is an open obligation. It is
                // surfaced for an explicit human/Runtime decision (normally one
                // finalizing Failed); it is never automatically re-authorized.
                var open = rows.Select(row => row.Event).GroupBy(value => value.AttemptId)
                    .Select(group => group.OrderBy(value => value.Position).ToArray())
                    .Where(group => !group[^1].Terminal && !group[^1].StatusDelivery)
                    .OrderByDescending(group => group[^1].Position)
                    .FirstOrDefault();
                return new ProductionArmHistoryReadResult(true, "ProductionArmHistoryAvailable", latest,
                    open?[0], open is not null);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, SqliteAuditIntegrityQuery.FaultReason(exception, "ProductionArmHistoryUnavailable"));
        }
    }

    private static void ValidateFilter(long after, long? through, int size)
    {
        if (after < 0 || through < after || through is < 0 || size is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(after));
    }

    private async ValueTask<T> ReadCoreAsync<T>(Func<sqlite3, StoreDeadline, T> read, CancellationToken token)
    {
        if (_options.ProductionArming is null)
            throw new InvalidOperationException("ProductionArmConfigurationRequired");
        _options.ProductionArming.Validate();
        if (_options.QueryTimeout < TimeSpan.FromMilliseconds(1) || _options.QueryTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(_options.QueryTimeout));
        var deadline = new StoreDeadline(_options.QueryTimeout);
        if (Interlocked.Increment(ref _outstanding) > 64)
        {
            Interlocked.Decrement(ref _outstanding);
            throw new InvalidOperationException("ProductionArmQueryCapacityExceeded");
        }
        var entered = false;
        try
        {
            entered = await Slots.WaitAsync(deadline.Remaining, token).ConfigureAwait(false);
            if (!entered) throw new InvalidOperationException("ProductionArmQueryCapacityExceeded");
            return await Task.Run(() => ReadDatabase(read, deadline, token), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally { if (entered) Slots.Release(); Interlocked.Decrement(ref _outstanding); }
    }

    private T ReadDatabase<T>(Func<sqlite3, StoreDeadline, T> read, StoreDeadline deadline, CancellationToken token)
    {
        SqliteNative.EnsureDeadline(deadline, token);
        if (!StoragePathValidator.TryValidate(_options, out var path, out var reason))
            throw new InvalidOperationException(reason);
        using var connection = SqliteNative.Open(path, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, _options);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, token);
        var committed = false;
        try
        {
            SqliteCommandStore.VerifyProductionArmReadGuard(database, _options, deadline);
            SqliteNative.EnsureDeadline(deadline, token);
            var result = read(database, deadline);
            var checkpoint = AuditChainDatabase.LatestCheckpoint(database, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, token);
            committed = true;
            VerifyExternalAnchor(checkpoint, deadline, token);
            return result;
        }
        finally { if (!committed) raw.sqlite3_exec(database, "ROLLBACK;"); }
    }

    private void VerifyExternalAnchor(AuditCheckpoint? checkpoint, StoreDeadline deadline, CancellationToken token)
    {
        var policy = _options.AuditIntegrityPolicy!;
        if (!policy.RequireExternalAnchor) return;
        var anchor = _options.ExternalAuditAnchor ?? throw new InvalidOperationException("AuditRequiredAnchorUnavailable");
        if (checkpoint is null) throw new InvalidOperationException("AuditCheckpointMissing");
        var remaining = deadline.Remaining;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("ProductionArmQueryDeadlineExceeded");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(remaining);
        var timeout = remaining < policy.AnchorTimeout ? remaining : policy.AnchorTimeout;
        var latest = AuditAnchorClient.InvokeAsync(anchor, cancellation => anchor.ReadLatestAsync(policy.StationId, cancellation),
            timeout, lifetime.Token).GetAwaiter().GetResult();
        AuditChainDatabase.Require(latest is not null && AuditChainDatabase.ReceiptMatches(policy, checkpoint, latest),
            "AuditExternalAnchorMismatch");
    }
}
