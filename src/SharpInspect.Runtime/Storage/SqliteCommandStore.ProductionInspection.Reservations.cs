using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    private sealed record ProductionInspectionReservation(long Rows, long AuditEntries);

    // The longest legal tail includes a failure after ResultValid is cleared,
    // followed by explicit recovery. Outstanding cycles retain their own budget.
    private static int ProductionInspectionRemainingRows(ProductionInspectionEventKind kind) => kind switch
    {
        ProductionInspectionEventKind.Admitted => 8,
        ProductionInspectionEventKind.CoreCommitted => 7,
        ProductionInspectionEventKind.PublicationPrepared => 6,
        ProductionInspectionEventKind.ResultValidRaised => 5,
        ProductionInspectionEventKind.ResultAcknowledged => 4,
        ProductionInspectionEventKind.ResultValidCleared => 3,
        ProductionInspectionEventKind.FaultTerminated => 2,
        ProductionInspectionEventKind.RecoveryRequired => 1,
        ProductionInspectionEventKind.AcknowledgementReset or ProductionInspectionEventKind.RecoveryCompleted => 0,
        _ => throw new InvalidOperationException("ProductionInspectionTransitionInvalid")
    };

    private static bool ProductionInspectionTransitionAllowed(ProductionInspectionEventKind? previous,
        ProductionInspectionEventKind next) => (previous, next) switch
    {
        (null, ProductionInspectionEventKind.Admitted) => true,
        (ProductionInspectionEventKind.Admitted, ProductionInspectionEventKind.CoreCommitted) => true,
        (ProductionInspectionEventKind.CoreCommitted, ProductionInspectionEventKind.PublicationPrepared) => true,
        (ProductionInspectionEventKind.PublicationPrepared, ProductionInspectionEventKind.ResultValidRaised) => true,
        (ProductionInspectionEventKind.ResultValidRaised, ProductionInspectionEventKind.ResultAcknowledged) => true,
        (ProductionInspectionEventKind.ResultAcknowledged, ProductionInspectionEventKind.ResultValidCleared) => true,
        (ProductionInspectionEventKind.ResultValidCleared, ProductionInspectionEventKind.AcknowledgementReset) => true,
        (ProductionInspectionEventKind.Admitted or ProductionInspectionEventKind.CoreCommitted or
            ProductionInspectionEventKind.PublicationPrepared or ProductionInspectionEventKind.ResultValidRaised or
            ProductionInspectionEventKind.ResultAcknowledged or ProductionInspectionEventKind.ResultValidCleared,
            ProductionInspectionEventKind.FaultTerminated) => true,
        (ProductionInspectionEventKind.FaultTerminated, ProductionInspectionEventKind.RecoveryRequired) => true,
        (ProductionInspectionEventKind.RecoveryRequired, ProductionInspectionEventKind.RecoveryCompleted) => true,
        _ => false
    };

    private static ProductionInspectionReservation ReadProductionInspectionReservation(sqlite3 database,
        StoreDeadline deadline, Guid? nextInspectionId = null, ProductionInspectionEventKind? nextKind = null)
    {
        var states = new Dictionary<Guid, ProductionInspectionEventKind>();
        void Apply(Guid id, ProductionInspectionEventKind kind)
        {
            var previous = states.TryGetValue(id, out var value) ? value : (ProductionInspectionEventKind?)null;
            AuditChainDatabase.Require(id != Guid.Empty && ProductionInspectionTransitionAllowed(previous, kind),
                "ProductionInspectionTransitionInvalid");
            states[id] = kind;
        }
        var entries = AuditChainDatabase.Read(database,
            "SELECT InspectionId,Kind FROM production_inspection_events ORDER BY Position LIMIT ?;", deadline,
            statement => (Id: Guid.Parse(SqliteNative.ColumnText(statement, 0)!),
                Kind: (ProductionInspectionEventKind)SqliteNative.ColumnInt64(statement, 1)),
            (ProductionInspectionStoreOptions.MaximumEntriesHardLimit + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(entries.Count <= ProductionInspectionStoreOptions.MaximumEntriesHardLimit,
            "ProductionInspectionEntryCapacityExceeded");
        foreach (var entry in entries) Apply(entry.Id, entry.Kind);
        if (nextInspectionId is { } nextId && nextKind is { } kind) Apply(nextId, kind);
        long rows = 0, audit = 0;
        foreach (var state in states.Values)
        {
            var remaining = ProductionInspectionRemainingRows(state);
            rows = checked(rows + remaining);
            audit = checked(audit + remaining + (state == ProductionInspectionEventKind.Admitted
                ? ProductionInspectionStoreOptions.AuditEntriesPerCore - 1 : 0));
        }
        return new(rows, audit);
    }

    internal static long ReadProductionInspectionAuditReserve(sqlite3 database, StoreDeadline deadline) =>
        ReadProductionInspectionReservation(database, deadline).AuditEntries;

    private static long ProductionInspectionStoredPayloadBytes(long decodedBytes) => checked(4 * ((decodedBytes + 2) / 3));
}
