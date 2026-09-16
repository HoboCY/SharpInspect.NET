using System.Globalization;
using SharpInspect.Runtime.Storage;
using SQLitePCL;

namespace SharpInspect.Runtime.Integrity;

internal static partial class AuditChainDatabase
{
    private sealed record EvidenceReconciliationVerification(EvidenceReconciliationConfiguration Configuration,
        IReadOnlyList<EvidenceReconciliationStoredRow> Rows)
    {
        internal long MetadataCount => Rows.Count + 1L;
        internal IReadOnlyDictionary<long, EvidenceReconciliationStoredRow> ByAuditSequence { get; } =
            Rows.ToDictionary(x => x.AuditSequence);
    }

    private static string EvidenceReconciliationAuditSchema()
    {
        var sql = SchemaSqlFor(ProductionOutboxRecoveryOptions.SchemaVersion)
            .Replace("PRAGMA user_version=37;", "PRAGMA user_version=38;", StringComparison.Ordinal);
        const string original = "Kind='ProductionOutboxRecoveryActivated'";
        Require(sql.Contains(original, StringComparison.Ordinal), "AuditSchemaDefinitionInvalid");
        return sql.Replace(original, "Kind IN ('ProductionOutboxRecoveryActivated'," +
            "'EvidenceReconciliationActivated','EvidenceReconciliationEvent')", StringComparison.Ordinal);
    }

    private static EvidenceReconciliationVerification? BeginEvidenceReconciliationVerification(sqlite3 database,
        long version, StoreDeadline deadline)
    {
        var tables = Scalar(database, @"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND
            name IN ('evidence_reconciliation_config','evidence_reconciliation_events');", deadline);
        if (version is not (EvidenceReconciliationStoreOptions.SchemaVersion or TraceStorageRetentionOptions.SchemaVersion or DiagnosticSupportStoreOptions.SchemaVersion))
        {
            Require(tables == 0, "EvidenceReconciliationGovernedMigrationRequired");
            return null;
        }
        Require(tables == 2, "EvidenceReconciliationConfigurationRequired");
        var configuration = SqliteCommandStore.ReadEvidenceReconciliationConfiguration(database, deadline);
        RequireReconciliationFeature(database, "image_evidence_store_config", "BindingHash",
            configuration.ImageEvidenceHash, deadline);
        RequireReconciliationFeature(database, "image_finalization_store_config", "BindingHash",
            configuration.FinalizationHash, deadline);
        RequireReconciliationFeature(database, "production_outbox_store_config", "BindingHash",
            configuration.OutboxHash, deadline);
        RequireReconciliationFeature(database, "production_outbox_recovery_config", "RecoveryBindingHash",
            configuration.OutboxRecoveryHash, deadline);
        var rows = SqliteCommandStore.ReadEvidenceReconciliationRows(database, configuration, deadline);
        // Without the earlier recovery feature, HandlerBlocked becomes legal only at
        // reconciliation activation. Schema 37 history retains its own activation boundary.
        if (configuration.OutboxHash is not null && configuration.OutboxRecoveryHash is null)
            Require(Scalar(database, @"SELECT COUNT(*) FROM audit_entries WHERE
                Kind='ProductionOutboxHandlerBlocked' AND Sequence <= COALESCE((SELECT MIN(Sequence)
                FROM audit_entries WHERE Kind='EvidenceReconciliationActivated'), 9223372036854775807);",
                deadline) == 0, "EvidenceReconciliationHandlerBlockBeforeActivation");
        Require(Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='EvidenceReconciliationActivated';",
            deadline) == 1, "EvidenceReconciliationActivationMissing");
        Require(Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='EvidenceReconciliationEvent';",
            deadline) == rows.Count, "EvidenceReconciliationAuditCountMismatch");
        return new(configuration, rows);
    }

    private static void RequireReconciliationFeature(sqlite3 database, string table, string column,
        string? expected, StoreDeadline deadline)
    {
        if (expected is null)
        {
            Require(!TableExists(database, table, deadline), "EvidenceReconciliationUndeclaredFeature");
            return;
        }
        Require(TableExists(database, table, deadline), "EvidenceReconciliationFeatureMissing");
        var rows = Read(database, $"SELECT {column} FROM {table} WHERE Id=1 LIMIT 2;", deadline,
            statement => SqliteNative.ColumnText(statement, 0));
        Require(rows.Count == 1 && rows[0] == expected, "EvidenceReconciliationFeatureBindingMismatch");
    }

    private static byte[] EvidenceReconciliationActivationPayload(EvidenceReconciliationConfiguration configuration) =>
        AuditCanonical.Encode("EvidenceReconciliationActivationV1", SqliteCommandStore.SystemPrincipal,
            configuration.BindingHash, Convert.ToBase64String(configuration.Encode()));

    private static void VerifyEvidenceReconciliationMetadata(EvidenceReconciliationVerification verification,
        string kind, long sequence, string hash, byte[] payload)
    {
        if (kind == SqliteCommandStore.EvidenceReconciliationActivationKind)
        {
            Require(payload.AsSpan().SequenceEqual(EvidenceReconciliationActivationPayload(verification.Configuration)) &&
                !verification.Rows.Any(x => x.AuditSequence <= sequence),
                "EvidenceReconciliationActivationBindingMismatch");
            return;
        }
        verification.ByAuditSequence.TryGetValue(sequence, out var row);
        Require(kind == SqliteCommandStore.EvidenceReconciliationEventAuditKind && row is not null &&
            row.AuditHash == hash && payload.AsSpan().SequenceEqual(EvidenceReconciliationStorageCodec.AuditBinding(
                row.Position, verification.Configuration.BindingHash,
                EvidenceReconciliationStorageCodec.Encode(row.Payload, verification.Configuration.MaximumPayloadBytes))),
            "EvidenceReconciliationAuditBindingMismatch");
    }

    internal static void AppendEvidenceReconciliationActivation(sqlite3 database, ProductionStoreOptions store,
        AuditIntegrityPolicy policy, IAuditSigningKey key, StoreDeadline deadline)
    {
        SqliteCommandStore.RequireConfiguredEvidenceReconciliation(database, store, deadline);
        Require(Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='EvidenceReconciliationActivated';",
            deadline) == 0, "EvidenceReconciliationActivationConflict");
        AppendEntry(database, policy, SqliteCommandStore.EvidenceReconciliationActivationKind, null,
            EvidenceReconciliationActivationPayload(SqliteCommandStore.ReconciliationConfiguration(store)), deadline);
        ReconciliationCheckpoint(database, policy, key, deadline);
    }

    internal static (long Sequence, string Hash) AppendEvidenceReconciliationEvent(sqlite3 database,
        AuditIntegrityPolicy policy, IAuditSigningKey key, byte[] payload, long futureReserve, StoreDeadline deadline)
    {
        Require(futureReserve >= 0 && payload.Length <=
            EvidenceReconciliationStoreOptions.MaximumPayloadBytesHardLimit * 2 + 1024,
            "EvidenceReconciliationAuditCapacityExceeded");
        AppendEntry(database, policy, SqliteCommandStore.EvidenceReconciliationEventAuditKind, null, payload,
            deadline, evidenceReconciliationReserveOverride: futureReserve);
        ReconciliationCheckpoint(database, policy, key, deadline);
        var tail = Tail(database, deadline);
        return (tail.Sequence, tail.Hash);
    }

    private static void ReconciliationCheckpoint(sqlite3 database, AuditIntegrityPolicy policy,
        IAuditSigningKey key, StoreDeadline deadline)
    {
        if (Tail(database, deadline).Sequence - Scalar(database,
                "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >= policy.CheckpointEveryEntries)
            CreateCheckpoint(database, policy, key, deadline);
    }
}
