using SharpInspect.Runtime.Storage;
using SQLitePCL;

namespace SharpInspect.Runtime.Integrity;

internal static partial class AuditChainDatabase
{
    private sealed record RetentionVerification(RetentionConfiguration Configuration,
        IReadOnlyList<EvidenceRetentionStoredRow> Rows, IReadOnlyList<TraceCheckpointRow> Checkpoints)
    {
        internal long MetadataCount => Rows.Count + Checkpoints.Count + 1L;
        internal IReadOnlyDictionary<long, EvidenceRetentionStoredRow> ByAudit { get; } =
            Rows.ToDictionary(x => x.AuditSequence);
        internal IReadOnlyDictionary<long, EvidenceRetentionStoredRow> ByAuthorization { get; } =
            Rows.Where(x => x.Payload.Authority is not null).ToDictionary(x => x.Payload.Authority!.AuthorizationAuditSequence);
    }

    private static string RetentionAuditSchema()
    {
        var sql = EvidenceReconciliationAuditSchema()
            .Replace("PRAGMA user_version=38;", "PRAGMA user_version=39;", StringComparison.Ordinal);
        const string prior = "'EvidenceReconciliationActivated','EvidenceReconciliationEvent')";
        Require(sql.Contains(prior, StringComparison.Ordinal), "AuditSchemaDefinitionInvalid");
        return sql.Replace(prior,
            "'EvidenceReconciliationActivated','EvidenceReconciliationEvent','EvidenceRetentionActivated','EvidenceRetentionEvent','TraceCheckpointStarted','TraceCheckpointOutcome')",
            StringComparison.Ordinal);
    }

    private static RetentionVerification? BeginRetentionVerification(sqlite3 database, long version, StoreDeadline deadline)
    {
        var tables = Scalar(database, @"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND
            name IN('evidence_retention_config','evidence_retention_events');", deadline);
        if (version is not (TraceStorageRetentionOptions.SchemaVersion or
            DiagnosticSupportStoreOptions.SchemaVersion))
        {
            Require(tables == 0, "RetentionGovernedMigrationRequired");
            return null;
        }
        Require(tables == 2, "RetentionConfigurationRequired");
        var configuration = SqliteCommandStore.ReadRetentionConfiguration(database, deadline);
        Require(SqliteCommandStore.ReadEvidenceReconciliationConfiguration(database, deadline).OptionsHash ==
            configuration.ReconciliationHash, "RetentionReconciliationBindingMismatch");
        Require(Text(database, "SELECT BindingHash FROM trace_storage_policy_store_config WHERE Id=1;", deadline) ==
            configuration.TracePolicyStoreHash, "RetentionTracePolicyBindingMismatch");
        var rows = SqliteCommandStore.ReadRetentionRows(database, configuration, deadline);
        SqliteCommandStore.RequireRetentionAuthorityCoverage(database, rows, deadline);
        Require(Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='EvidenceRetentionActivated';", deadline) == 1,
            "RetentionActivationMissing");
        Require(Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='EvidenceRetentionEvent';", deadline) == rows.Count,
            "RetentionAuditCountMismatch");
        var authorities = rows.Where(x => x.Payload.Authority is not null).Select(x => x.Payload.Authority!.AuthorizationAuditSequence).ToArray();
        Require(authorities.Distinct().Count() == authorities.Length, "RetentionAuthorizationReused");
        return new(configuration, rows, ReadTraceCheckpoints(database, configuration, deadline));
    }

    private static byte[] RetentionActivationPayload(RetentionConfiguration configuration) =>
        AuditCanonical.Encode("EvidenceRetentionActivationV1", Abstractions.SystemPrincipalId.RetentionCleanup,
            configuration.BindingHash, Convert.ToBase64String(configuration.Encode()));

    private static void VerifyRetentionMetadata(RetentionVerification verification, string kind,
        long sequence, string hash, byte[] payload)
    {
        if (kind is TraceCheckpointStarted or TraceCheckpointOutcome)
        {
            var checkpoint = verification.Checkpoints.SingleOrDefault(x => x.AuditSequence == sequence);
            Require(checkpoint is not null && checkpoint.AuditHash == hash &&
                payload.AsSpan().SequenceEqual(checkpoint.Record.Encode()), "TraceCheckpointAuditBindingMismatch");
            return;
        }
        if (kind == SqliteCommandStore.RetentionActivationKind)
        {
            Require(payload.AsSpan().SequenceEqual(RetentionActivationPayload(verification.Configuration)) &&
                !verification.Rows.Any(x => x.AuditSequence <= sequence) &&
                !verification.Checkpoints.Any(x => x.AuditSequence <= sequence), "RetentionActivationBindingMismatch");
            return;
        }
        verification.ByAudit.TryGetValue(sequence, out var row);
        Require(kind == SqliteCommandStore.RetentionEventAuditKind && row is not null &&
            row.AuditHash == hash && payload.AsSpan().SequenceEqual(EvidenceRetentionCodec.AuditBinding(
                row.Position, verification.Configuration.BindingHash,
                EvidenceRetentionCodec.Encode(row.Payload, verification.Configuration.MaximumPayloadBytes))),
            "RetentionAuditBindingMismatch");
    }

    private static void VerifyRetentionAuthorization(RetentionVerification verification,
        long sequence, string hash, byte[] payload)
    {
        if (SqliteCommandStore.RetentionAuthorizationEventId(payload) is not { } id) return;
        Require(verification.ByAuthorization.TryGetValue(sequence, out var row) &&
            row.Payload.Authority!.AuthorizationEventId == id &&
            row.Payload.Authority.AuthorizationAuditHash == hash, "RetentionAuthorizationRowMissing");
    }

    internal static void EnsureRetentionTransactionCapacity(sqlite3 database, AuditIntegrityPolicy policy,
        int writes, long futureReserve, StoreDeadline deadline)
    {
        Require(writes > 0 && futureReserve >= 0, "RetentionAuditReservationInvalid");
        _ = NextSequence(database, policy, deadline, archiveData: false,
            retentionReserveOverride: checked(futureReserve + writes - 1));
    }

    internal static void AppendRetentionActivation(sqlite3 database, ProductionStoreOptions options,
        AuditIntegrityPolicy policy, IAuditSigningKey key, StoreDeadline deadline)
    {
        SqliteCommandStore.RequireConfiguredRetention(database, options, deadline);
        Require(Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='EvidenceRetentionActivated';", deadline) == 0,
            "RetentionActivationConflict");
        AppendEntry(database, policy, SqliteCommandStore.RetentionActivationKind, null,
            RetentionActivationPayload(SqliteCommandStore.RetentionConfigurationFor(options)), deadline);
        ReconciliationCheckpoint(database, policy, key, deadline);
    }

    internal static (long Sequence, string Hash) AppendRetentionEvent(sqlite3 database,
        AuditIntegrityPolicy policy, IAuditSigningKey key, byte[] payload, long reserve, StoreDeadline deadline)
    {
        Require(reserve >= 0 && payload.Length <= TraceStorageRetentionOptions.MaximumPayloadBytesHardLimit * 2 + 1024,
            "RetentionAuditCapacityExceeded");
        AppendEntry(database, policy, SqliteCommandStore.RetentionEventAuditKind, null, payload, deadline,
            retentionReserveOverride: reserve);
        ReconciliationCheckpoint(database, policy, key, deadline);
        var tail = Tail(database, deadline);
        return (tail.Sequence, tail.Hash);
    }
}
