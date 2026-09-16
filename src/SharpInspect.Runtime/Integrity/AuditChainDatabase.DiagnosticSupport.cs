using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using SQLitePCL;

namespace SharpInspect.Runtime.Integrity;

internal static partial class AuditChainDatabase
{
    private sealed record DiagnosticSupportVerification(DiagnosticSupportConfiguration Configuration,
        IReadOnlyList<DiagnosticOperationStoredRow> Rows)
    {
        internal long MetadataCount => Rows.Count + 1L;
        internal IReadOnlyDictionary<long, DiagnosticOperationStoredRow> ByAudit { get; } =
            Rows.ToDictionary(row => row.AuditSequence);
    }

    /// <summary>
    /// The schema-40 audit envelope keeps every schema-33/39 byte: only the closed metadata
    /// kind list grows by the activation entry and the immutable operation fact entries.
    /// </summary>
    private static string DiagnosticSupportAuditSchema()
    {
        var sql = RetentionAuditSchema()
            .Replace("PRAGMA user_version=39;", "PRAGMA user_version=40;", StringComparison.Ordinal);
        const string prior = "'EvidenceRetentionActivated','EvidenceRetentionEvent','TraceCheckpointStarted','TraceCheckpointOutcome')";
        Require(sql.Contains(prior, StringComparison.Ordinal), "AuditSchemaDefinitionInvalid");
        return sql.Replace(prior,
            "'EvidenceRetentionActivated','EvidenceRetentionEvent','TraceCheckpointStarted','TraceCheckpointOutcome','DiagnosticSupportActivated','DiagnosticOperationEvent')",
            StringComparison.Ordinal);
    }

    private static DiagnosticSupportVerification? BeginDiagnosticSupportVerification(sqlite3 database,
        long storedVersion, ProductionStoreOptions? options, StoreDeadline deadline)
    {
        var tables = Scalar(database, @"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND
            name IN('diagnostic_support_config','diagnostic_operation_facts');", deadline);
        if (storedVersion != DiagnosticSupportStoreOptions.SchemaVersion)
        {
            Require(tables == 0, "DiagnosticSupportGovernedMigrationRequired");
            return null;
        }
        Require(tables == 2, "DiagnosticSupportConfigurationRequired");
        var configuration = SqliteCommandStore.ReadDiagnosticSupportConfiguration(database, deadline);
        if (options?.DiagnosticSupport is not null)
            Require(SqliteCommandStore.DiagnosticSupportConfigurationFor(options).BindingHash ==
                configuration.BindingHash, "DiagnosticSupportConfigurationMismatch");
        var rows = SqliteCommandStore.ReadDiagnosticOperationRows(database, configuration, deadline);
        SqliteCommandStore.RequireDiagnosticSupportAuthorityCoverage(database, rows, deadline);
        foreach (var row in rows)
            SqliteCommandStore.RequireDiagnosticSupportAuthority(database, row, deadline);
        Require(Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='DiagnosticSupportActivated';", deadline) == 1,
            "DiagnosticSupportActivationMissing");
        Require(Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='DiagnosticOperationEvent';", deadline) == rows.Count,
            "DiagnosticSupportAuditCountMismatch");
        return new(configuration, rows);
    }

    private static byte[] DiagnosticSupportActivationPayload(DiagnosticSupportConfiguration configuration) =>
        AuditCanonical.Encode("DiagnosticSupportActivationV1", Abstractions.SystemPrincipalId.Runtime,
            configuration.BindingHash, Convert.ToBase64String(configuration.Encode()));

    private static void VerifyDiagnosticSupportMetadata(DiagnosticSupportVerification verification, string kind,
        long sequence, string hash, byte[] payload)
    {
        if (kind == SqliteCommandStore.DiagnosticSupportActivationKind)
        {
            Require(payload.AsSpan().SequenceEqual(DiagnosticSupportActivationPayload(verification.Configuration)) &&
                !verification.Rows.Any(row => row.AuditSequence <= sequence),
                "DiagnosticSupportActivationBindingMismatch");
            return;
        }
        verification.ByAudit.TryGetValue(sequence, out var row);
        Require(kind == SqliteCommandStore.DiagnosticOperationAuditKind && row is not null &&
            row.AuditHash == hash && payload.AsSpan().SequenceEqual(DiagnosticOperationStorageCodec.AuditBinding(
                row.Position, verification.Configuration.BindingHash, row.PayloadBytes)),
            "DiagnosticSupportAuditBindingMismatch");
    }

    internal static void EnsureDiagnosticSupportTransactionCapacity(sqlite3 database, AuditIntegrityPolicy policy,
        int writes, long futureReserve, StoreDeadline deadline)
    {
        Require(writes > 0 && futureReserve >= 0, "DiagnosticSupportAuditReservationInvalid");
        _ = NextSequence(database, policy, deadline, archiveData: false,
            diagnosticSupportData: true,
            diagnosticSupportReserveOverride: checked(futureReserve + writes - 1));
    }

    internal static void AppendDiagnosticSupportActivation(sqlite3 database, ProductionStoreOptions options,
        AuditIntegrityPolicy policy, IAuditSigningKey key, StoreDeadline deadline)
    {
        SqliteCommandStore.RequireConfiguredDiagnosticSupport(database, options, deadline);
        Require(Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='DiagnosticSupportActivated';", deadline) == 0,
            "DiagnosticSupportActivationConflict");
        AppendEntry(database, policy, SqliteCommandStore.DiagnosticSupportActivationKind, null,
            DiagnosticSupportActivationPayload(SqliteCommandStore.DiagnosticSupportConfigurationFor(options)), deadline,
            diagnosticSupportReserveOverride: 0);
        ReconciliationCheckpoint(database, policy, key, deadline);
    }

    internal static (long Sequence, string Hash) AppendDiagnosticSupportEvent(sqlite3 database,
        AuditIntegrityPolicy policy, IAuditSigningKey key, byte[] payload, long reserve, StoreDeadline deadline)
    {
        Require(reserve >= 0 && payload.Length <=
            DiagnosticSupportStoreOptions.MaximumPayloadBytesHardLimit * 2 + 1024, "DiagnosticSupportAuditCapacityExceeded");
        AppendEntry(database, policy, SqliteCommandStore.DiagnosticOperationAuditKind, null, payload, deadline,
            diagnosticSupportData: true, diagnosticSupportReserveOverride: reserve);
        ReconciliationCheckpoint(database, policy, key, deadline);
        var tail = Tail(database, deadline);
        return (tail.Sequence, tail.Hash);
    }

    internal static void RequireFullDiagnosticSupportVerification(sqlite3 database, AuditIntegrityReport report,
        StoreDeadline deadline, ProductionStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(database, deadline).Sequence,
            "DiagnosticSupportVerificationBudgetExceeded");
        if (options?.DiagnosticSupport is not null)
        {
            _ = SqliteCommandStore.DiagnosticSupportConfigurationFor(options);
        }
    }
}
