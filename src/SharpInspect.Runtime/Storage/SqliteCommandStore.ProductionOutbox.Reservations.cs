using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Outbox;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// T52 pre-Core outbox capacity reservation. A durable production cycle exists in the immutable
/// production ledger before its Core, and therefore its frozen outbox batch, may be committed,
/// so every durable Admitted cycle without a Core row holds its complete future outbox liability
/// before a Trigger is accepted. The reservation is a pure bounded function of that ledger and
/// the immutable schema-36 configuration row: no reservation table is added and no delivery,
/// event, audit entry or other backend state is invented. A cycle that leaves Admitted without a
/// Core (FaultTerminated/RecoveryRequired) releases its uncreated reserve naturally, and the Core
/// transaction transfers the reservation into the actual delivery rows and reserves without ever
/// counting the current cycle twice.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    /// <summary>
    /// The complete future outbox footprint of every durable Admitted cycle without a Core. Per
    /// configured route a cycle owes one delivery row, one Created fact and both facts of every
    /// configured attempt (start plus terminal). The central-audit footprint is exactly that
    /// future fact count, because a delivery row owns no metadata entry of its own.
    /// </summary>
    internal sealed record ProductionOutboxPreCoreReserve(long Cycles, long Rows, long PayloadBytes,
        long AuditEntries)
    {
        internal static ProductionOutboxPreCoreReserve Empty { get; } = new(0, 0, 0, 0);
    }

    /// <summary>
    /// The bounded liability of the given number of uncreated cycles over the frozen routes.
    /// Every owed attempt fact reserves the bounded stored receipt size. The delivery itself
    /// reserves its route payload maximum; Created carries no separate receipt bytes.
    /// </summary>
    internal static ProductionOutboxPreCoreReserve ProductionOutboxPreCoreReserveForCycles(
        ProductionOutboxStoreOptions options, long cycles)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        // The extra cycle is the read-only candidate observation the prospective check adds
        // before the admission transaction that will make it durable.
        AuditChainDatabase.Require(cycles is >= 0 and
            <= (long)ProductionInspectionStoreOptions.MaximumEntriesHardLimit + 1,
            "ProductionOutboxReserveCapacityExceeded");
        if (cycles == 0) return ProductionOutboxPreCoreReserve.Empty;
        var eventsPerCycle = checked((long)options.Routes.Count * (1 + 2L * options.MaximumAttempts));
        var rowsPerCycle = checked(eventsPerCycle + options.Routes.Count);
        var attemptFactsPerCycle = checked(2L * options.Routes.Count * options.MaximumAttempts);
        var payloadsPerRoute = options.Routes.Aggregate(0L,
            (sum, route) => checked(sum + ProductionInspectionStoredPayloadBytes(route.MaximumPayloadBytes)));
        var payloadPerCycle = checked(payloadsPerRoute + attemptFactsPerCycle *
            ProductionOutboxStoreOptions.MaximumStoredReceiptBytes);
        return new(cycles, checked(cycles * rowsPerCycle), checked(cycles * payloadPerCycle),
            checked(cycles * eventsPerCycle));
    }

    /// <summary>
    /// The bounded count of durable cycles whose latest immutable production fact is Admitted and
    /// which own no Core row. A faulted, recovery-bound or Core-committed cycle owes no uncreated
    /// batch, so its reserve is released by the ledger itself.
    /// </summary>
    internal static long ReadProductionOutboxPendingAdmittedCycles(sqlite3 database, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(deadline);
        if (!AuditChainDatabase.TableExists(database, "production_inspection_events", deadline) ||
            !AuditChainDatabase.TableExists(database, "production_inspection_cores", deadline)) return 0;
        var cycles = AuditChainDatabase.Scalar(database, @"
            SELECT COUNT(*) FROM production_inspection_events admitted
            WHERE admitted.Kind=? AND NOT EXISTS(SELECT 1 FROM production_inspection_events later
                WHERE later.InspectionId=admitted.InspectionId AND later.Position>admitted.Position)
                AND NOT EXISTS(SELECT 1 FROM production_inspection_cores core
                    WHERE core.InspectionId=admitted.InspectionId);", deadline,
            ((int)ProductionInspectionEventKind.Admitted).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(cycles is >= 0 and <= ProductionInspectionStoreOptions.MaximumEntriesHardLimit,
            "ProductionOutboxReserveCapacityExceeded");
        return cycles;
    }

    /// <summary>The complete pre-Core liability of every currently pending admitted cycle.</summary>
    internal static ProductionOutboxPreCoreReserve ReadProductionOutboxPreCoreReserve(sqlite3 database,
        ProductionOutboxStoreOptions options, StoreDeadline deadline) =>
        ProductionOutboxPreCoreReserveForCycles(options,
            ReadProductionOutboxPendingAdmittedCycles(database, deadline));

    /// <summary>
    /// The same pre-Core central-audit reserve derived from the store alone, for every shared
    /// audit writer: the frozen route count and attempt budget come from the immutable
    /// configuration row, so no other ledger can ever consume the budget an uncreated batch owes.
    /// </summary>
    internal static long ReadProductionOutboxPreCoreAuditReserve(sqlite3 database, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(deadline);
        if (!AuditChainDatabase.TableExists(database, "production_outbox_store_config", deadline)) return 0;
        var config = AuditChainDatabase.Read(database, @"SELECT RouteCount,MaximumAttempts
            FROM production_outbox_store_config WHERE Id=1 LIMIT 2;", deadline,
            statement => (RouteCount: SqliteNative.ColumnInt64(statement, 0),
                MaximumAttempts: SqliteNative.ColumnInt64(statement, 1)));
        AuditChainDatabase.Require(config.Count == 1 && config[0].RouteCount is > 0 and
            <= ProductionOutboxStoreOptions.MaximumRoutesHardLimit && config[0].MaximumAttempts is > 0 and
            <= ProductionOutboxStoreOptions.MaximumAttemptsHardLimit, "ProductionOutboxConfigurationMismatch");
        var cycles = ReadProductionOutboxPendingAdmittedCycles(database, deadline);
        return checked(cycles * config[0].RouteCount * (1 + 2 * config[0].MaximumAttempts));
    }

    /// <summary>
    /// The local entry/byte reserve of one complete liability set, exactly as the cold
    /// verification proves it: every persisted row, every still-owed fact of every persisted
    /// obligation and the complete uncreated liability must fit the declared budgets.
    /// </summary>
    private static string? ProductionOutboxLocalReserveFailure(sqlite3 database,
        ProductionOutboxStoreOptions options, ProductionOutboxPreCoreReserve preCore, StoreDeadline deadline)
    {
        var persisted = ReadProductionOutboxReserveRows(database, deadline);
        var events = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM production_outbox_events;", deadline);
        var deliveries = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM production_outbox_deliveries;", deadline);
        if (checked(events + deliveries + persisted.Events + preCore.Rows) > options.MaximumEvents)
            return "ProductionOutboxReserveCapacityExceeded";
        var usedBytes = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(ReceiptBase64)),0) FROM production_outbox_events;", deadline);
        var storedPayloadBytes = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(PayloadBase64)),0) FROM production_outbox_deliveries;", deadline);
        if (checked(usedBytes + storedPayloadBytes + persisted.Events *
            ProductionOutboxStoreOptions.MaximumStoredReceiptBytes + preCore.PayloadBytes) >
            options.MaximumTotalBytes)
            return "ProductionOutboxReserveTotalCapacityExceeded";
        return null;
    }

    /// <summary>
    /// The pre-Core acceptance gate of one admission transaction: the candidate Admitted fact is
    /// already durable inside the open transaction, so its complete liability plus that of every
    /// other cycle still without a Core must fit the local budgets and the central-audit budget.
    /// Any failure rolls the whole admission back, and an accepted cycle keeps its own
    /// reservation until its Core transaction transfers it into the frozen batch.
    /// </summary>
    private string? ProductionOutboxAdmissionCapacityFailure(sqlite3 database,
        ProductionInspectionAdmission admission, StoreDeadline deadline)
    {
        if (_options.Outbox is not { } options) return null;
        if (!ProductionOutboxBinding.RoutesMatch(_options, admission.TracePolicySnapshot)) return null;
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        options.Validate();
        var reason = ProductionOutboxLocalReserveFailure(database, options,
            ReadProductionOutboxPreCoreReserve(database, options, deadline), deadline);
        return reason ?? AuditChainDatabase.ProductionOutboxAuditCapacityFailure(database, policy, deadline);
    }

    /// <summary>
    /// Read-only prospective capacity for the next Trigger: the cycles already durable plus one
    /// candidate that is not part of the ledger yet. The returned reason is exactly the rejection
    /// the admission transaction would produce, or null when the next cycle fits.
    /// </summary>
    internal static string? ProductionOutboxProspectiveCapacityFailure(sqlite3 database,
        AuditIntegrityPolicy policy, ProductionOutboxStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var pending = ReadProductionOutboxPreCoreReserve(database, options, deadline);
        var reason = ProductionOutboxLocalReserveFailure(database, options,
            ProductionOutboxPreCoreReserveForCycles(options, checked(pending.Cycles + 1)), deadline);
        if (reason is not null) return reason;
        var persisted = ReadProductionOutboxReserveRows(database, deadline).Events;
        var candidate = ProductionOutboxPreCoreReserveForCycles(options, 1).AuditEntries;
        return AuditChainDatabase.CanReserveProductionOutboxAudit(database, policy, deadline,
            checked(persisted + candidate)) ? null : "ProductionOutboxAuditCapacityExceeded";
    }
}
