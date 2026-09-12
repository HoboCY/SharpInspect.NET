using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using SQLitePCL;

namespace SharpInspect.Runtime.Integrity;

/// <summary>
/// Schema-33 central-audit integration. Lifecycle transitions are signed metadata
/// entries in the existing chain (the schema-32 envelope and every earlier position
/// column stay byte-for-byte identical); no new human identity envelope and no
/// rewrite of an earlier schema is introduced.
/// </summary>
internal static partial class AuditChainDatabase
{
    private sealed record RecipeLifecycleVerification(
        RecipeLifecycleStoreOptions Options,
        IReadOnlyList<SqliteCommandStore.RecipeLifecycleStoredRow> Rows)
    {
        internal long MetadataCount => Rows.Count + 1L;
    }

    private static RecipeLifecycleVerification? BeginRecipeLifecycleVerification(sqlite3 database,
        long schemaVersion, long storedSchemaVersion, RecipeLifecycleStoreOptions? lifecycleOptions,
        RecipeDraftStoreOptions? draftOptions, StoreDeadline deadline)
    {
        var tableCount = Scalar(database, @"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND
            name IN ('recipe_lifecycle_store_config','recipe_lifecycle_events');", deadline);
        if (schemaVersion < RecipeLifecycleStoreOptions.SchemaVersion)
        {
            Require(lifecycleOptions is null && tableCount == 0, "RecipeLifecycleGovernedMigrationRequired");
            return null;
        }
        if (lifecycleOptions is null)
        {
            // Only a schema-36 store that declares the feature absent may omit the ledger;
            // every older generation still requires it exactly as before.
            Require(storedSchemaVersion >= ProductionOutboxStoreOptions.SchemaVersion && tableCount == 0,
                "RecipeLifecycleConfigurationRequired");
            return null;
        }
        // The opt-in is mandatory at schema 33 and the option/tables must match exactly;
        // an absent option, an extra table or a partial ledger fails closed.
        Require(tableCount == 2, "RecipeLifecycleConfigurationRequired");
        // The lifecycle ledger preserves draft and release revisions, so the draft ledger
        // and the identity/audit stack are required; every other gate may be absent.
        Require(draftOptions is not null, "RecipeDraftConfigurationRequired");
        SqliteCommandStore.RequireConfiguredRecipeLifecycle(database, lifecycleOptions!, deadline);
        var rows = SqliteCommandStore.ReadRecipeLifecycleRows(database, lifecycleOptions!, deadline);
        // Bidirectional exact metadata membership: every lifecycle row has exactly one
        // signed metadata entry, and the walk below proves the reverse direction
        // (a metadata entry without a stored row fails closed).
        Require(Scalar(database, "SELECT COALESCE(MAX(Position),0) FROM recipe_lifecycle_events;",
                deadline) == rows.Count &&
            Scalar(database, "SELECT COUNT(*) FROM recipe_lifecycle_events;", deadline) == rows.Count &&
            Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeLifecycleEvent';",
                deadline) == rows.Count &&
            Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeLifecycleStoreActivated';",
                deadline) == 1,
            "RecipeLifecycleAuditCountMismatch");
        return new(lifecycleOptions!, rows);
    }

    private static void VerifyRecipeLifecycleMetadata(sqlite3 database, RecipeLifecycleVerification verification,
        string kind, long sequence, string hash, byte[] payload, StoreDeadline deadline)
    {
        if (kind == SqliteCommandStore.RecipeLifecycleActivationKind)
        {
            Require(payload.AsSpan().SequenceEqual(verification.Options.EncodeActivationPayload()),
                "RecipeLifecycleActivationBindingMismatch");
            Require(!verification.Rows.Any(value => value.AuditSequence <= sequence),
                "RecipeLifecycleActivationOrderInvalid");
            return;
        }
        var row = verification.Rows.SingleOrDefault(value => value.AuditSequence == sequence);
        Require(kind == SqliteCommandStore.RecipeLifecycleEventAuditKind && row is not null &&
            row.AuditHash == hash, "RecipeLifecycleAuditRowMissing");
        Require(payload.AsSpan().SequenceEqual(SqliteCommandStore.EncodeAuditBinding(row!)),
            "RecipeLifecycleAuditBindingMismatch");
    }

    internal static void RequireFullRecipeLifecycleVerification(sqlite3 database, AuditIntegrityReport report,
        StoreDeadline deadline, RecipeLifecycleStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(database, deadline).Sequence,
            "RecipeLifecycleVerificationBudgetExceeded");
    }

    private static string RecipeLifecycleAuditSchema()
    {
        // The schema-32 envelope is reused unchanged: lifecycle transitions are metadata
        // entries with every typed position column NULL. No existing audit position,
        // envelope or pre-33 schema is rewritten.
        var sql = ProductionArmAuditSchema().Replace(
            "PRAGMA user_version=32;", "PRAGMA user_version=33;", StringComparison.Ordinal);
        var start = sql.LastIndexOf(" OR (Kind='ProductionArmEvent'", StringComparison.Ordinal);
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
            SqliteCommandStore.RecipeLifecycleActivationKind, SqliteCommandStore.RecipeLifecycleEventAuditKind
        }.Select(kind => $" OR (Kind='{kind}' AND Sequence>1 AND {empty})")));
    }

    internal static (long Sequence, string Hash) AppendRecipeLifecycleMetadata(sqlite3 database,
        AuditIntegrityPolicy policy, IAuditSigningKey key, string kind, byte[] payload,
        RecipeLifecycleStoreOptions options, StoreDeadline deadline)
    {
        Require(Scalar(database, "PRAGMA user_version;", deadline) is RecipeLifecycleStoreOptions.SchemaVersion
            or ProductionImageEvidenceStoreOptions.SchemaVersion or ProductionImageFinalizationStoreOptions.SchemaVersion
            or ProductionOutboxStoreOptions.SchemaVersion,
            "RecipeLifecycleSchemaRequired");
        Require(kind is SqliteCommandStore.RecipeLifecycleActivationKind or
            SqliteCommandStore.RecipeLifecycleEventAuditKind, "RecipeLifecycleAuditKindInvalid");
        options.Validate();
        Require(payload.Length is > 0 and <= RecipeLifecycleStoreOptions.MaximumAuditPayloadBytes,
            "RecipeLifecycleAuditPayloadCapacityExceeded");
        var activated = Scalar(database,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeLifecycleStoreActivated';", deadline);
        if (kind == SqliteCommandStore.RecipeLifecycleActivationKind)
            Require(activated == 0, "RecipeLifecycleActivationConflict");
        // No transition may be signed before the store activation, so a metadata entry can
        // never claim a lifecycle fact the activation does not already precede.
        else
            Require(activated == 1, "RecipeLifecycleActivationMissing");
        var sequence = AppendEntry(database, policy, kind, null, payload, deadline, recipeLifecycleData: true);
        var tail = Tail(database, deadline);
        Require(sequence == tail.Sequence, "RecipeLifecycleAuditMismatch");
        if (tail.Sequence - Scalar(database, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(database, policy, key, deadline);
        return (sequence, tail.Hash);
    }
}
