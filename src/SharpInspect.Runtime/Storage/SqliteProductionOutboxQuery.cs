using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Outbox;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Read-only, bounded access to the schema-36 production outbox. Every page is reconstructed
/// inside one frozen SQLite snapshot after the full central-audit verification and the full
/// local-ledger replay, so a returned backlog watermark can never be a stale in-memory
/// projection. The query exposes no mutation surface, never accepts a filesystem path and
/// re-verifies every stored acceptance receipt signature on a cold read.
/// </summary>
public sealed class SqliteProductionOutboxQuery : IProductionOutboxQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private static int _outstanding;

    public SqliteProductionOutboxQuery(ProductionStoreOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<OutboxPendingPage> ReadPendingAsync(long afterPosition = 0,
        int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var outbox = _options.Outbox;
        if (outbox is null)
            return PendingUnavailable("ProductionOutboxConfigurationRequired", afterPosition);
        if (afterPosition < 0 || pageSize is < 1 or > ProductionOutboxStoreOptions.MaximumPageSizeHardLimit ||
            pageSize is { } requested && requested > outbox.MaximumPageSize)
            return PendingUnavailable("ProductionOutboxPageInvalid", afterPosition);
        try
        {
            return await ReadAsync(page => page.Pending(afterPosition,
                pageSize ?? outbox.MaximumPageSize), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return PendingUnavailable(SqliteAuditIntegrityQuery.FaultReason(exception,
                "ProductionOutboxQueryUnavailable"), afterPosition);
        }
    }

    public async ValueTask<OutboxBacklogSnapshot> ReadBacklogAsync(
        CancellationToken cancellationToken = default)
    {
        // A backlog watermark is never fabricated: an unverifiable read throws instead of
        // returning an empty snapshot a caller could mistake for an empty backlog.
        return await ReadAsync(page => page.Backlog(), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<OutboxHistoryPage> ReadHistoryAsync(Guid? inspectionId = null,
        Guid? deliveryId = null, long afterPosition = 0, int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var outbox = _options.Outbox;
        if (outbox is null)
            return HistoryUnavailable("ProductionOutboxConfigurationRequired", afterPosition);
        if (inspectionId == Guid.Empty || deliveryId == Guid.Empty || afterPosition < 0 ||
            pageSize is < 1 or > ProductionOutboxStoreOptions.MaximumPageSizeHardLimit ||
            pageSize is { } requested && requested > outbox.MaximumPageSize)
            return HistoryUnavailable("ProductionOutboxPageInvalid", afterPosition);
        try
        {
            return await ReadAsync(page => page.History(inspectionId, deliveryId, afterPosition,
                pageSize ?? outbox.MaximumPageSize), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return HistoryUnavailable(SqliteAuditIntegrityQuery.FaultReason(exception,
                "ProductionOutboxQueryUnavailable"), afterPosition);
        }
    }

    private static OutboxPendingPage PendingUnavailable(string reason, long afterPosition) =>
        new(false, reason, Array.Empty<OutboxPendingItem>(), false, Math.Max(0, afterPosition), null);

    private static OutboxHistoryPage HistoryUnavailable(string reason, long afterPosition) =>
        new(false, reason, Array.Empty<ProductionOutboxEvent>(), false, Math.Max(0, afterPosition));

    private async ValueTask<T> ReadAsync<T>(Func<PageReader, T> select,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _outstanding) > 32)
        {
            Interlocked.Decrement(ref _outstanding);
            throw new InvalidOperationException("ProductionOutboxQueryCapacityExceeded");
        }
        var entered = false;
        try
        {
            var deadline = new StoreDeadline(_options.QueryTimeout);
            entered = await Slots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!entered) throw new TimeoutException("ProductionOutboxQueryDeadlineExceeded");
            return await Task.Run(() => ReadDatabase(select, deadline, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (entered) Slots.Release();
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private T ReadDatabase<T>(Func<PageReader, T> select, StoreDeadline deadline,
        CancellationToken cancellationToken)
    {
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        var outbox = _options.Outbox ?? throw new InvalidOperationException(
            "ProductionOutboxConfigurationRequired");
        var production = _options.ProductionInspections ?? throw new InvalidOperationException(
            "ProductionOutboxProductionConfigurationRequired");
        var policy = _options.AuditIntegrityPolicy ?? throw new InvalidOperationException(
            "AuditPolicyNotConfigured");
        outbox.Validate();
        if (!StoragePathValidator.TryValidate(_options, out var path, out var reason))
            throw new InvalidOperationException(reason);
        using var key = WindowsMachineAuditKey.Open(policy, false, out _);
        using var connection = SqliteNative.Open(path, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, _options);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        var committed = false;
        try
        {
            var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
            if (schema != ProductionOutboxStoreOptions.SchemaVersion)
                throw new InvalidOperationException(schema > ProductionOutboxStoreOptions.SchemaVersion
                    ? "ProductionOutboxSchemaTooNew" : "ProductionOutboxGovernedMigrationRequired");
            SqliteCommandStore.VerifyProductionOutboxReadGuard(database, _options, deadline);
            SqliteCommandStore.ValidateProductionOutboxHistory(database, outbox, production, deadline);
            var auditSequence = AuditChainDatabase.Tail(database, deadline).Sequence;
            var result = select(new PageReader(database, outbox, auditSequence, deadline));
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            return result;
        }
        finally
        {
            if (!committed)
                try { raw.sqlite3_exec(database, "ROLLBACK;"); } catch { }
        }
    }

    /// <summary>One verified read snapshot; every derived value comes from immutable rows.</summary>
    private sealed class PageReader
    {
        private readonly sqlite3 _database;
        private readonly StoreDeadline _deadline;
        private readonly IReadOnlyList<ProductionOutboxStoredDelivery> _deliveries;
        private readonly IReadOnlyList<ProductionOutboxStoredEvent> _events;
        private readonly IReadOnlyList<ProductionOutboxWorkState> _states;

        internal PageReader(sqlite3 database, ProductionOutboxStoreOptions options, long auditSequence,
            StoreDeadline deadline)
        {
            _database = database;
            _deadline = deadline;
            AuditSequence = auditSequence;
            _deliveries = SqliteCommandStore.ReadProductionOutboxDeliveries(database, options, deadline);
            _events = SqliteCommandStore.ReadProductionOutboxRows(database, options, deadline);
            _states = SqliteCommandStore.ReadProductionOutboxObligationStates(database, options, deadline);
        }

        internal long AuditSequence { get; }

        internal OutboxPendingPage Pending(long afterPosition, int pageSize)
        {
            var ordered = _states.Where(state => state.State != OutboxDeliveryState.Succeeded)
                .OrderBy(state => PositionOf(state.Delivery.DeliveryId)).ToArray();
            var selected = ordered.Where(state => PositionOf(state.Delivery.DeliveryId) > afterPosition)
                .Take(pageSize + 1).ToArray();
            var page = selected.Take(pageSize).ToArray();
            var items = page.Select(BuildPendingItem).ToArray();
            return new OutboxPendingPage(true,
                items.Length == 0 ? "ProductionOutboxEmpty" : "ProductionOutboxAvailable",
                items, selected.Length > pageSize,
                items.Length == 0 ? afterPosition : PositionOf(page[^1].Delivery.DeliveryId),
                Backlog());
        }

        internal OutboxBacklogSnapshot Backlog() =>
            SqliteCommandStore.ReadProductionOutboxBacklogSnapshot(_database, AuditSequence, _deadline);

        internal OutboxHistoryPage History(Guid? inspectionId, Guid? deliveryId, long afterPosition,
            int pageSize)
        {
            // The exact stored receipt is re-verified against the pinned receiver identity and
            // the frozen route before it is projected to a cold caller.
            foreach (var row in _events)
            {
                if (row.Event.Kind != OutboxEventKind.Succeeded) continue;
                VerifyStoredReceipt(row.Event);
            }
            var selected = _events
                .Where(row => row.Event.Position > afterPosition &&
                    (inspectionId is null || row.Event.InspectionId == inspectionId) &&
                    (deliveryId is null || row.Event.DeliveryId == deliveryId))
                .OrderBy(row => row.Position).Take(pageSize + 1).ToArray();
            var page = selected.Take(pageSize).ToArray();
            var items = page.Select(row => row.Event).ToArray();
            return new OutboxHistoryPage(true,
                items.Length == 0 ? "ProductionOutboxHistoryEmpty" : "ProductionOutboxHistoryAvailable",
                items, selected.Length > pageSize,
                items.Length == 0 ? afterPosition : items[^1].Position);
        }

        private void VerifyStoredReceipt(ProductionOutboxEvent value)
        {
            var delivery = _deliveries.SingleOrDefault(row =>
                row.Delivery.DeliveryId == value.DeliveryId) ??
                throw new InvalidOperationException("ProductionOutboxDeliveryMissing");
            var receipt = value.CopyReceipt() ?? throw new InvalidOperationException(
                "ProductionOutboxReceiptInvalid");
            var verified = OutboxAcceptanceVerifier.VerifyReceipt(delivery.Delivery, receipt);
            AuditChainDatabase.Require(verified.ReceiptId == value.ReceiptId &&
                verified.ReceiptHash == value.ReceiptHash && verified.AcceptedAtUtc == value.AcceptedAtUtc,
                "ProductionOutboxReceiptBindingMismatch");
        }

        private OutboxPendingItem BuildPendingItem(ProductionOutboxWorkState state)
        {
            var position = PositionOf(state.Delivery.DeliveryId);
            var contentHash = _deliveries.Single(row =>
                row.Delivery.DeliveryId == state.Delivery.DeliveryId).ContentHash;
            return new OutboxPendingItem(state.Delivery, state.State, state.AttemptCount,
                state.ActiveAttemptId, state.ActiveRuntimeEpoch, state.RetryEligible,
                state.RetryAfterUtc, state.PermanentBlock, state.LastFailureReasonCode, position,
                contentHash);
        }

        private long PositionOf(Guid deliveryId) => _deliveries.Single(row =>
            row.Delivery.DeliveryId == deliveryId).Position;
    }
}
