using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using SQLitePCL;

namespace SharpInspect.Runtime.Integrity;

/// <summary>
/// Schema-35 central-audit integration. Finalization facts are signed metadata entries in the
/// existing chain: the schema-34 (schema-33) envelope and every earlier position column stay
/// byte-for-byte identical, no new human identity envelope is introduced and no earlier schema
/// is rewritten. Every fact has exactly one metadata entry and every entry must prove its
/// stored row, so membership is verified in both directions.
/// </summary>
internal static partial class AuditChainDatabase
{
    private sealed record ImageFinalizationVerification(
        ProductionImageFinalizationStoreOptions Options,
        IReadOnlyList<ImageFinalizationStoredRow> Rows)
    {
        internal long MetadataCount => Rows.Count + 1L;
    }

    /// <summary>
    /// Presence and exact-membership proof for the schema-35 finalization store. A store below
    /// the generation must not carry the option or any declared table; a store at the generation
    /// must carry exactly the three declared tables, exactly one signed activation entry and
    /// exactly one signed entry per stored fact, and a caller that supplies the option
    /// additionally re-proves the immutable configuration row against it.
    /// </summary>
    private static ImageFinalizationVerification? BeginImageFinalizationVerification(sqlite3 database,
        long storedSchemaVersion, ProductionImageFinalizationStoreOptions? finalizationOptions,
        StoreDeadline deadline)
    {
        var tableCount = Scalar(database, @"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND
            name IN ('image_finalization_store_config','image_finalization_events','image_finalization_work');",
            deadline);
        if (storedSchemaVersion < ProductionImageFinalizationStoreOptions.SchemaVersion)
        {
            Require(finalizationOptions is null && tableCount == 0,
                "ImageFinalizationGovernedMigrationRequired");
            return null;
        }
        if (finalizationOptions is null)
        {
            // Only a schema-36 store that declares the feature absent may omit the ledger;
            // every older generation still requires it exactly as before.
            Require(storedSchemaVersion >= ProductionOutboxStoreOptions.SchemaVersion && tableCount == 0,
                "ImageFinalizationConfigurationRequired");
            return null;
        }
        Require(tableCount == 3, "ImageFinalizationConfigurationRequired");
        SqliteCommandStore.RequireConfiguredImageFinalization(database, finalizationOptions!, deadline);
        var rows = SqliteCommandStore.ReadImageFinalizationRows(database, finalizationOptions!, deadline);
        Require(Scalar(database,
                "SELECT COUNT(*) FROM audit_entries WHERE Kind='ImageFinalizationStoreActivated';",
                deadline) == 1, "ImageFinalizationActivationMissing");
        Require(Scalar(database, @"SELECT COUNT(*) FROM audit_entries WHERE Kind IN
                ('ImageFinalizationAttemptStarted','ImageFinalizationAttemptFailed',
                 'ImageFinalizationSucceeded','ImageFinalizationStageReleased');", deadline) == rows.Count,
            "ImageFinalizationAuditCountMismatch");
        return new(finalizationOptions!, rows);
    }

    private static void VerifyImageFinalizationMetadata(sqlite3 database,
        ImageFinalizationVerification verification, string kind, long sequence, string hash,
        byte[] payload, StoreDeadline deadline)
    {
        Require(payload.Length is > 0 and <= ProductionImageFinalizationStoreOptions.MaximumAuditPayloadBytes,
            "ImageFinalizationAuditPayloadCapacityExceeded");
        if (kind == SqliteCommandStore.ImageFinalizationActivationKind)
        {
            Require(payload.AsSpan().SequenceEqual(verification.Options.EncodeActivationPayload()),
                "ImageFinalizationActivationBindingMismatch");
            // The activation precedes every stored fact, so a metadata entry can never claim a
            // finalization fact that the activation does not already precede.
            Require(!verification.Rows.Any(value => value.Event.AuditSequence <= sequence),
                "ImageFinalizationActivationOrderInvalid");
            return;
        }
        var row = verification.Rows.SingleOrDefault(value => value.Event.AuditSequence == sequence);
        Require(row is not null && row.Event.AuditHash == hash &&
            kind == SqliteCommandStore.ImageFinalizationAuditKind(row.Event.Kind),
            "ImageFinalizationAuditRowMissing");
        Require(payload.AsSpan().SequenceEqual(
                ProductionImageFinalizationStorageCodec.EncodeAuditPayload(row!.Event)),
            "ImageFinalizationAuditBindingMismatch");
    }

    internal static void RequireFullImageFinalizationVerification(sqlite3 database,
        AuditIntegrityReport report, StoreDeadline deadline, ProductionImageFinalizationStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(database, deadline).Sequence,
            "ImageFinalizationVerificationBudgetExceeded");
    }

    /// <summary>
    /// Reserves the complete future central-audit footprint of the image finalization ledger
    /// while the writer still holds its transaction lock: the future Succeeded and StageReleased
    /// entries of every open obligation, the release entry of every succeeded-but-unreleased work
    /// and the terminal entry of every active attempt stay reserved before a new Core or attempt
    /// may be admitted. Failed attempts never consume the completion reserve.
    /// </summary>
    internal static void EnsureImageFinalizationTransactionCapacity(sqlite3 database,
        AuditIntegrityPolicy policy, long futureReserve, StoreDeadline deadline)
    {
        Require(futureReserve >= 0, "ImageFinalizationAuditReservationInvalid");
        _ = NextSequence(database, policy, deadline, archiveData: false,
            imageFinalizationData: true,
            imageFinalizationReserveOverride: futureReserve);
    }

    private static string ImageFinalizationAuditSchema()
    {
        // The schema-34 envelope is reused unchanged: finalization facts are metadata entries
        // with every typed position column NULL. No existing audit position, envelope or pre-35
        // schema is rewritten.
        var sql = ImageEvidenceAuditSchema().Replace(
            "PRAGMA user_version=34;", "PRAGMA user_version=35;", StringComparison.Ordinal);
        var start = sql.LastIndexOf(" OR (Kind='ImageEvidenceStoreActivated'", StringComparison.Ordinal);
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
            SqliteCommandStore.ImageFinalizationActivationKind,
            "ImageFinalizationAttemptStarted", "ImageFinalizationAttemptFailed",
            "ImageFinalizationSucceeded", "ImageFinalizationStageReleased"
        }.Select(kind => $" OR (Kind='{kind}' AND Sequence>1 AND {empty})")));
    }

    /// <summary>
    /// Appends the one signed store-activation entry. The caller must already be inside the
    /// transaction that inserts the immutable configuration row, so configuration and activation
    /// evidence become durable together and the activation is signed exactly once.
    /// </summary>
    internal static (long Sequence, string Hash) AppendImageFinalizationActivation(sqlite3 database,
        AuditIntegrityPolicy policy, IAuditSigningKey key, ProductionImageFinalizationStoreOptions options,
        StoreDeadline deadline)
    {
        Require(Scalar(database, "PRAGMA user_version;", deadline) is
            ProductionImageFinalizationStoreOptions.SchemaVersion or ProductionOutboxStoreOptions.SchemaVersion,
            "ImageFinalizationSchemaRequired");
        options.Validate();
        var payload = options.EncodeActivationPayload();
        Require(payload.Length is > 0 and <= ProductionImageFinalizationStoreOptions.MaximumAuditPayloadBytes,
            "ImageFinalizationAuditPayloadCapacityExceeded");
        Require(Scalar(database,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='ImageFinalizationStoreActivated';",
            deadline) == 0, "ImageFinalizationActivationConflict");
        var sequence = AppendEntry(database, policy, SqliteCommandStore.ImageFinalizationActivationKind,
            null, payload, deadline, imageFinalizationData: true);
        var tail = Tail(database, deadline);
        Require(sequence == tail.Sequence, "ImageFinalizationAuditMismatch");
        if (tail.Sequence - Scalar(database, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;",
                deadline) >= policy.CheckpointEveryEntries)
            CreateCheckpoint(database, policy, key, deadline);
        return (sequence, tail.Hash);
    }

    /// <summary>
    /// Appends the one signed metadata entry of one finalization fact. The future reserve is the
    /// exact number of events this ledger must still be able to append after this entry, so no
    /// other writer can consume the completion budget of an open obligation.
    /// </summary>
    internal static (long Sequence, string Hash) AppendImageFinalizationEvent(sqlite3 database,
        AuditIntegrityPolicy policy, IAuditSigningKey key, ProductionImageFinalizationKind kind,
        byte[] payload, ProductionImageFinalizationStoreOptions options, long futureReserve,
        StoreDeadline deadline)
    {
        Require(Scalar(database, "PRAGMA user_version;", deadline) is
            ProductionImageFinalizationStoreOptions.SchemaVersion or ProductionOutboxStoreOptions.SchemaVersion,
            "ImageFinalizationSchemaRequired");
        options.Validate();
        Require(futureReserve >= 0, "ImageFinalizationAuditReservationInvalid");
        Require(payload is { Length: > 0 } &&
            payload.Length <= ProductionImageFinalizationStoreOptions.MaximumAuditPayloadBytes,
            "ImageFinalizationAuditPayloadCapacityExceeded");
        Require(Scalar(database,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='ImageFinalizationStoreActivated';",
            deadline) == 1, "ImageFinalizationActivationMissing");
        var sequence = AppendEntry(database, policy, SqliteCommandStore.ImageFinalizationAuditKind(kind),
            null, payload, deadline, imageFinalizationData: true,
            imageFinalizationReserveOverride: futureReserve);
        var tail = Tail(database, deadline);
        Require(sequence == tail.Sequence, "ImageFinalizationAuditMismatch");
        if (tail.Sequence - Scalar(database, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;",
                deadline) >= policy.CheckpointEveryEntries)
            CreateCheckpoint(database, policy, key, deadline);
        return (sequence, tail.Hash);
    }
}
