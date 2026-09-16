using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Read-only, bounded access to the schema-37 governed recovery and correction facts. Every
/// snapshot is reconstructed inside one frozen verified read transaction after the full central
/// audit verification and the complete schema-36/schema-37 ledger replay, so no caller can
/// obtain an unverified operation row or a mutation surface.
/// </summary>
public sealed class SqliteProductionOutboxGovernanceQuery : IProductionOutboxGovernanceQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private static int _outstanding;

    public SqliteProductionOutboxGovernanceQuery(ProductionStoreOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<ProductionOutboxGovernanceSnapshot> ReadAsync(Guid? deliveryId = null,
        CancellationToken cancellationToken = default)
    {
        var outbox = _options.Outbox;
        if (outbox?.ManualRecovery is not { } recovery)
            return Unavailable("ProductionOutboxRecoveryConfigurationRequired");
        if (deliveryId == Guid.Empty)
            return Unavailable("ProductionOutboxGovernanceDeliveryInvalid");
        try
        {
            return await ReadAsync(page => page.Project(deliveryId, recovery), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Unavailable(SqliteAuditIntegrityQuery.FaultReason(exception,
                "ProductionOutboxGovernanceQueryUnavailable"));
        }
    }

    private static ProductionOutboxGovernanceSnapshot Unavailable(string reason) =>
        new(false, reason, 0, Array.Empty<ProductionOutboxRecoveryRecord>(),
            Array.Empty<ProductionOutboxCorrectionRecord>());

    private async ValueTask<T> ReadAsync<T>(Func<GovernanceReader, T> select,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _outstanding) > 16)
        {
            Interlocked.Decrement(ref _outstanding);
            throw new InvalidOperationException("ProductionOutboxGovernanceQueryCapacityExceeded");
        }
        var entered = false;
        try
        {
            var deadline = new StoreDeadline(_options.QueryTimeout);
            entered = await Slots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!entered) throw new TimeoutException("ProductionOutboxGovernanceQueryDeadlineExceeded");
            return await Task.Run(() => ReadDatabase(select, deadline, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (entered) Slots.Release();
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private T ReadDatabase<T>(Func<GovernanceReader, T> select, StoreDeadline deadline,
        CancellationToken cancellationToken)
    {
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        var outbox = _options.Outbox ?? throw new InvalidOperationException(
            "ProductionOutboxRecoveryConfigurationRequired");
        var recovery = outbox.ManualRecovery ?? throw new InvalidOperationException(
            "ProductionOutboxRecoveryConfigurationRequired");
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
            var expected = _options.StorageRetention is not null ? TraceStorageRetentionOptions.SchemaVersion
                : _options.EvidenceReconciliation is not null ? EvidenceReconciliationStoreOptions.SchemaVersion
                : ProductionOutboxRecoveryOptions.SchemaVersion;
            if (schema != expected)
                throw new InvalidOperationException(schema > expected
                    ? "ProductionOutboxSchemaTooNew" : "ProductionOutboxRecoveryGovernedMigrationRequired");
            SqliteCommandStore.VerifyProductionOutboxReadGuard(database, _options, deadline);
            SqliteCommandStore.RequireConfiguredRetention(database, _options, deadline);
            SqliteCommandStore.ValidateProductionOutboxHistory(database, outbox, production, deadline);
            var auditSequence = AuditChainDatabase.Tail(database, deadline).Sequence;
            var result = select(new GovernanceReader(database, outbox, recovery, auditSequence, deadline));
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            return result;
        }
        finally
        {
            if (!committed)
                try { SQLitePCL.raw.sqlite3_exec(database, "ROLLBACK;"); } catch { }
        }
    }

    private sealed class GovernanceReader
    {
        private readonly IReadOnlyList<SqliteCommandStore.ProductionOutboxRecoveryStoredRow> _recoveries;
        private readonly IReadOnlyList<SqliteCommandStore.ProductionOutboxCorrectionStoredRow> _corrections;

        internal GovernanceReader(SQLitePCL.sqlite3 database, ProductionOutboxStoreOptions outbox,
            ProductionOutboxRecoveryOptions recovery, long auditSequence, StoreDeadline deadline)
        {
            AuditSequence = auditSequence;
            (_recoveries, _corrections) = SqliteCommandStore.ReadProductionOutboxGovernanceRows(
                database, outbox, recovery, deadline);
        }

        internal long AuditSequence { get; }

        internal ProductionOutboxGovernanceSnapshot Project(Guid? deliveryId,
            ProductionOutboxRecoveryOptions recovery)
        {
            var recoveries = _recoveries.Where(value => deliveryId is null ||
                    value.DeliveryId == deliveryId.Value)
                .Select(value => value.Project()).ToArray();
            var corrections = _corrections.Where(value => deliveryId is null ||
                    value.DeliveryId == deliveryId.Value || value.SourceDeliveryId == deliveryId.Value)
                .Select(value => value.Project()).ToArray();
            var reason = recoveries.Length == 0 && corrections.Length == 0
                ? "ProductionOutboxGovernanceEmpty" : "ProductionOutboxGovernanceAvailable";
            return new ProductionOutboxGovernanceSnapshot(true, reason, AuditSequence, recoveries, corrections);
        }
    }
}
