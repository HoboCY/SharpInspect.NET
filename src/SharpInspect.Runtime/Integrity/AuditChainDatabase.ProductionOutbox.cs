using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using SQLitePCL;

namespace SharpInspect.Runtime.Integrity;

/// <summary>
/// Schema-36 central-audit integration. Outbox facts are signed metadata entries in the
/// existing chain: the schema-33 envelope and every earlier position column stay byte-for-byte
/// identical, no new human identity envelope is introduced and no earlier schema is rewritten.
/// Every fact has exactly one metadata entry and every entry must prove its stored row, so
/// membership is verified in both directions.
/// </summary>
internal static partial class AuditChainDatabase
{
    private sealed record ProductionOutboxVerification(ProductionOutboxStoreOptions Options,
        IReadOnlyList<ProductionOutboxStoredEvent> Rows)
    {
        internal long MetadataCount => Rows.Count + 1L;
    }

    /// <summary>
    /// Presence and exact-membership proof for the schema-36 outbox. A store below the
    /// generation must not carry the option or any declared table; a store at the generation
    /// must carry exactly the four declared tables, exactly one signed activation entry and
    /// exactly one signed entry per stored fact, and the option additionally re-proves the
    /// immutable configuration row against it.
    /// </summary>
    private static ProductionOutboxVerification? BeginProductionOutboxVerification(sqlite3 database,
        long storedSchemaVersion, ProductionOutboxStoreOptions? outboxOptions, StoreDeadline deadline)
    {
        var tableCount = Scalar(database, @"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND
            name IN ('production_outbox_store_config','production_outbox_deliveries',
                'production_outbox_events','production_outbox_work');", deadline);
        if (storedSchemaVersion < ProductionOutboxStoreOptions.SchemaVersion)
        {
            Require(outboxOptions is null && tableCount == 0, "ProductionOutboxGovernedMigrationRequired");
            return null;
        }
        Require(tableCount == 4 && outboxOptions is not null, "ProductionOutboxConfigurationRequired");
        SqliteCommandStore.RequireConfiguredProductionOutbox(database, outboxOptions!, deadline);
        var rows = SqliteCommandStore.ReadProductionOutboxRows(database, outboxOptions!, deadline);
        Require(Scalar(database,
                "SELECT COUNT(*) FROM audit_entries WHERE Kind='ProductionOutboxStoreActivated';",
                deadline) == 1, "ProductionOutboxActivationMissing");
        Require(Scalar(database, @"SELECT COUNT(*) FROM audit_entries WHERE Kind IN
                ('ProductionOutboxCreated','ProductionOutboxAttemptStarted',
                 'ProductionOutboxAttemptFailed','ProductionOutboxSucceeded');", deadline) == rows.Count,
            "ProductionOutboxAuditCountMismatch");
        return new(outboxOptions!, rows);
    }

    private static void VerifyProductionOutboxMetadata(sqlite3 database,
        ProductionOutboxVerification verification, string kind, long sequence, string hash,
        byte[] payload, StoreDeadline deadline)
    {
        Require(payload.Length is > 0 and <= ProductionOutboxStoreOptions.MaximumAuditPayloadBytes,
            "ProductionOutboxAuditPayloadCapacityExceeded");
        if (kind == SqliteCommandStore.ProductionOutboxActivationKind)
        {
            Require(payload.AsSpan().SequenceEqual(verification.Options.EncodeActivationPayload()),
                "ProductionOutboxActivationBindingMismatch");
            // The activation precedes every stored fact, so a metadata entry can never claim an
            // outbox fact that the activation does not already precede.
            Require(!verification.Rows.Any(value => value.Event.AuditSequence <= sequence),
                "ProductionOutboxActivationOrderInvalid");
            return;
        }
        var row = verification.Rows.SingleOrDefault(value => value.Event.AuditSequence == sequence);
        Require(row is not null && row.Event.AuditHash == hash &&
            kind == SqliteCommandStore.ProductionOutboxAuditKind(row.Event.Kind),
            "ProductionOutboxAuditRowMissing");
        Require(payload.AsSpan().SequenceEqual(ProductionOutboxStorageCodec.EncodeAuditBinding(
                ProductionOutboxStorageCodec.Bindings(row!.Event))),
            "ProductionOutboxAuditBindingMismatch");
    }

    internal static void RequireFullProductionOutboxVerification(sqlite3 database,
        AuditIntegrityReport report, StoreDeadline deadline, ProductionOutboxStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(database, deadline).Sequence,
            "ProductionOutboxVerificationBudgetExceeded");
    }

    /// <summary>
    /// Reserves the complete future central-audit footprint of the outbox ledger while the
    /// writer still holds its transaction lock: the remaining attempt and terminal entries of
    /// every open delivery stay reserved before a new Core, attempt or outcome may be admitted.
    /// The supplied reserve is the persisted part only; the ledger-derived uncreated-batch
    /// reserve of every cycle still Admitted without a Core is always added on top.
    /// </summary>
    internal static void EnsureProductionOutboxTransactionCapacity(sqlite3 database,
        AuditIntegrityPolicy policy, int writes, long futureReserve, StoreDeadline deadline)
    {
        Require(writes > 0 && futureReserve >= 0, "ProductionOutboxAuditReservationInvalid");
        _ = NextSequence(database, policy, deadline, archiveData: false, productionOutboxData: true,
            productionOutboxReserveOverride: checked(futureReserve + writes - 1));
    }

    /// <summary>
    /// The same reservation check as a read-only probe: the supplied reserve is the persisted
    /// part, and the uncreated-batch reserve of every durable Admitted cycle without a Core is
    /// added by the shared sequence calculation. Returns the exact rejection reason, or null.
    /// </summary>
    internal static string? ProductionOutboxAuditCapacityFailure(sqlite3 database,
        AuditIntegrityPolicy policy, StoreDeadline deadline, long? persistedReserve = null)
    {
        try
        {
            _ = NextSequence(database, policy, deadline, archiveData: false, productionOutboxData: true,
                productionOutboxReserveOverride: persistedReserve);
            return null;
        }
        catch (InvalidOperationException exception) when (IsCapacityReason(exception.Message))
        { return exception.Message; }
    }

    internal static bool CanReserveProductionOutboxAudit(sqlite3 database, AuditIntegrityPolicy policy,
        StoreDeadline deadline, long persistedReserve) =>
        ProductionOutboxAuditCapacityFailure(database, policy, deadline, persistedReserve) is null;

    private static string ProductionOutboxAuditSchema()
    {
        // The schema-35 (schema-33) envelope is reused unchanged: outbox facts are metadata
        // entries with every typed position column NULL. No existing audit position, envelope
        // or pre-36 schema is rewritten.
        var sql = ImageFinalizationAuditSchema().Replace(
            "PRAGMA user_version=35;", "PRAGMA user_version=36;", StringComparison.Ordinal);
        var start = sql.LastIndexOf(" OR (Kind='ImageFinalizationStoreActivated'", StringComparison.Ordinal);
        var close = start < 0 ? -1 : sql.IndexOf(")));", start, StringComparison.Ordinal);
        if (close < 0) throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
        var empty = string.Join(" AND ", new[]
        {
            "FactPosition", "IdentityPosition", "AlarmPosition", "ResultPosition", "DraftPosition", "CameraPosition",
            "NetworkPosition", "ImagingPosition", "CalibrationSessionPosition", "CalibrationEventPosition",
            "CalibrationManifestPosition", "GovernancePosition", "ReleasePosition", "PlcResultContractPosition",
            "ActivationPosition", "PreviewPosition", "CalibrationImportPosition", "ManualInspectionPosition",
            "ProductionAdmissionPosition", "StationQualificationPosition", "RecipeTransferPosition",
            "TraceStoragePolicyPosition", "QualificationCyclePosition", "PlcCommunicationPosition",
            "ProductionInspectionPosition", "PartIdentityPosition"
        }.Select(value => value + " IS NULL"));
        return sql.Insert(close + 1, string.Concat(new[]
        {
            SqliteCommandStore.ProductionOutboxActivationKind, "ProductionOutboxCreated",
            "ProductionOutboxAttemptStarted", "ProductionOutboxAttemptFailed", "ProductionOutboxSucceeded"
        }.Select(kind => $" OR (Kind='{kind}' AND Sequence>1 AND {empty})")));
    }

    /// <summary>
    /// Appends the one signed store-activation entry. The caller must already be inside the
    /// transaction that inserts the immutable configuration row, so configuration and activation
    /// evidence become durable together and the activation is signed exactly once.
    /// </summary>
    internal static (long Sequence, string Hash) AppendProductionOutboxActivation(sqlite3 database,
        AuditIntegrityPolicy policy, IAuditSigningKey key, ProductionOutboxStoreOptions options,
        StoreDeadline deadline)
    {
        Require(Scalar(database, "PRAGMA user_version;", deadline) ==
            ProductionOutboxStoreOptions.SchemaVersion, "ProductionOutboxSchemaRequired");
        options.Validate();
        var payload = options.EncodeActivationPayload();
        Require(payload.Length is > 0 and <= ProductionOutboxStoreOptions.MaximumAuditPayloadBytes,
            "ProductionOutboxAuditPayloadCapacityExceeded");
        Require(Scalar(database,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='ProductionOutboxStoreActivated';",
            deadline) == 0, "ProductionOutboxActivationConflict");
        var sequence = AppendEntry(database, policy, SqliteCommandStore.ProductionOutboxActivationKind,
            null, payload, deadline, productionOutboxData: true);
        var tail = Tail(database, deadline);
        Require(sequence == tail.Sequence, "ProductionOutboxAuditMismatch");
        if (tail.Sequence - Scalar(database, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;",
                deadline) >= policy.CheckpointEveryEntries)
            CreateCheckpoint(database, policy, key, deadline);
        return (sequence, tail.Hash);
    }

    /// <summary>
    /// Appends the one signed metadata entry of one outbox fact. The future reserve is the exact
    /// number of facts this ledger must still be able to append after this entry, so no other
    /// writer can consume the remaining budget of an open obligation.
    /// </summary>
    internal static (long Sequence, string Hash) AppendProductionOutboxEvent(sqlite3 database,
        AuditIntegrityPolicy policy, IAuditSigningKey key, OutboxEventKind kind, byte[] payload,
        ProductionOutboxStoreOptions options, long futureReserve, StoreDeadline deadline)
    {
        Require(Scalar(database, "PRAGMA user_version;", deadline) ==
            ProductionOutboxStoreOptions.SchemaVersion, "ProductionOutboxSchemaRequired");
        options.Validate();
        Require(futureReserve >= 0, "ProductionOutboxAuditReservationInvalid");
        Require(payload is { Length: > 0 } &&
            payload.Length <= ProductionOutboxStoreOptions.MaximumAuditPayloadBytes,
            "ProductionOutboxAuditPayloadCapacityExceeded");
        Require(Scalar(database,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='ProductionOutboxStoreActivated';",
            deadline) == 1, "ProductionOutboxActivationMissing");
        var sequence = AppendEntry(database, policy, SqliteCommandStore.ProductionOutboxAuditKind(kind),
            null, payload, deadline, productionOutboxData: true,
            productionOutboxReserveOverride: futureReserve);
        var tail = Tail(database, deadline);
        Require(sequence == tail.Sequence, "ProductionOutboxAuditMismatch");
        if (tail.Sequence - Scalar(database, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;",
                deadline) >= policy.CheckpointEveryEntries)
            CreateCheckpoint(database, policy, key, deadline);
        return (sequence, tail.Hash);
    }
}
