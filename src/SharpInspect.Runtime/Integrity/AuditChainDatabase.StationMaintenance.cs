using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using SQLitePCL;

namespace SharpInspect.Runtime.Integrity;

internal static partial class AuditChainDatabase
{
    private sealed record StationMaintenanceVerification(StationMaintenanceConfiguration Configuration,
        IReadOnlyList<StationMaintenanceStoredRow> Rows)
    {
        internal long MetadataCount => Rows.Count + 1L;
        internal IReadOnlyDictionary<long, StationMaintenanceStoredRow> ByAudit { get; } =
            Rows.ToDictionary(row => row.AuditSequence);
    }

    /// <summary>
    /// The schema-41 audit envelope keeps every schema-40 byte: only the closed metadata
    /// kind list grows by the activation entry and the immutable maintenance fact entries.
    /// </summary>
    private static string StationMaintenanceAuditSchema()
    {
        var sql = DiagnosticSupportAuditSchema()
            .Replace("PRAGMA user_version=40;", "PRAGMA user_version=41;", StringComparison.Ordinal);
        const string prior = "'DiagnosticSupportActivated','DiagnosticOperationEvent')";
        Require(sql.Contains(prior, StringComparison.Ordinal), "AuditSchemaDefinitionInvalid");
        return sql.Replace(prior,
            "'DiagnosticSupportActivated','DiagnosticOperationEvent','StationMaintenanceActivated','StationMaintenanceEvent')",
            StringComparison.Ordinal);
    }

    private static StationMaintenanceVerification? BeginStationMaintenanceVerification(sqlite3 database,
        long storedVersion, ProductionStoreOptions? options, StoreDeadline deadline)
    {
        var tables = Scalar(database, @"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND
            name IN('station_maintenance_config','station_maintenance_facts');", deadline);
        if (storedVersion != StationMaintenanceStoreOptions.SchemaVersion)
        {
            Require(tables == 0, "StationMaintenanceGovernedMigrationRequired");
            return null;
        }
        Require(tables == 2, "StationMaintenanceConfigurationRequired");
        var configuration = SqliteCommandStore.ReadStationMaintenanceConfiguration(database, deadline);
        if (options?.StationMaintenance is not null)
            Require(SqliteCommandStore.StationMaintenanceConfigurationFor(options).BindingHash ==
                configuration.BindingHash, "StationMaintenanceConfigurationMismatch");
        var rows = SqliteCommandStore.ReadStationMaintenanceRows(database, configuration, deadline);
        SqliteCommandStore.RequireStationMaintenanceAuthorityCoverage(database, rows, deadline);
        foreach (var row in rows)
            SqliteCommandStore.RequireStationMaintenanceAuthority(database, row, deadline);
        Require(Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='StationMaintenanceActivated';",
            deadline) == 1, "StationMaintenanceActivationMissing");
        Require(Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='StationMaintenanceEvent';", deadline) ==
            rows.Count, "StationMaintenanceAuditCountMismatch");
        return new(configuration, rows);
    }

    private static byte[] StationMaintenanceActivationPayload(StationMaintenanceConfiguration configuration) =>
        AuditCanonical.Encode("StationMaintenanceActivationV1", SystemPrincipalId.Runtime,
            configuration.BindingHash, Convert.ToBase64String(configuration.Encode()));

    private static void VerifyStationMaintenanceMetadata(StationMaintenanceVerification verification, string kind,
        long sequence, string hash, byte[] payload)
    {
        if (kind == SqliteCommandStore.StationMaintenanceActivationKind)
        {
            Require(payload.AsSpan().SequenceEqual(StationMaintenanceActivationPayload(verification.Configuration)) &&
                !verification.Rows.Any(row => row.AuditSequence <= sequence),
                "StationMaintenanceActivationBindingMismatch");
            return;
        }
        verification.ByAudit.TryGetValue(sequence, out var row);
        Require(kind == SqliteCommandStore.StationMaintenanceOperationAuditKind && row is not null &&
            row.AuditHash == hash && payload.AsSpan().SequenceEqual(StationMaintenanceStorageCodec.AuditBinding(
                row.Position, verification.Configuration.BindingHash, row.PayloadBytes)),
            "StationMaintenanceAuditBindingMismatch");
    }

    internal static void EnsureStationMaintenanceTransactionCapacity(sqlite3 database, AuditIntegrityPolicy policy,
        int writes, long futureReserve, StoreDeadline deadline)
    {
        Require(writes > 0 && futureReserve >= 0, "StationMaintenanceAuditReservationInvalid");
        _ = NextSequence(database, policy, deadline, archiveData: false,
            stationMaintenanceData: true,
            stationMaintenanceReserveOverride: checked(futureReserve + writes - 1));
    }

    internal static void AppendStationMaintenanceActivation(sqlite3 database, ProductionStoreOptions options,
        AuditIntegrityPolicy policy, IAuditSigningKey key, StoreDeadline deadline)
    {
        SqliteCommandStore.RequireConfiguredStationMaintenance(database, options, deadline);
        Require(Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='StationMaintenanceActivated';",
            deadline) == 0, "StationMaintenanceActivationConflict");
        AppendEntry(database, policy, SqliteCommandStore.StationMaintenanceActivationKind, null,
            StationMaintenanceActivationPayload(SqliteCommandStore.StationMaintenanceConfigurationFor(options)),
            deadline, stationMaintenanceReserveOverride: 0);
        ReconciliationCheckpoint(database, policy, key, deadline);
    }

    internal static (long Sequence, string Hash) AppendStationMaintenanceEvent(sqlite3 database,
        AuditIntegrityPolicy policy, IAuditSigningKey key, byte[] payload, long reserve, StoreDeadline deadline)
    {
        Require(reserve >= 0 && payload.Length <=
            StationMaintenanceStoreOptions.MaximumPayloadBytesHardLimit * 2 + 1024,
            "StationMaintenanceAuditCapacityExceeded");
        AppendEntry(database, policy, SqliteCommandStore.StationMaintenanceOperationAuditKind, null, payload, deadline,
            stationMaintenanceData: true, stationMaintenanceReserveOverride: reserve);
        ReconciliationCheckpoint(database, policy, key, deadline);
        var tail = Tail(database, deadline);
        return (tail.Sequence, tail.Hash);
    }

    internal static void RequireFullStationMaintenanceVerification(sqlite3 database, AuditIntegrityReport report,
        StoreDeadline deadline, ProductionStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(database, deadline).Sequence,
            "StationMaintenanceVerificationBudgetExceeded");
        if (options?.StationMaintenance is not null)
            _ = SqliteCommandStore.StationMaintenanceConfigurationFor(options);
    }
}
