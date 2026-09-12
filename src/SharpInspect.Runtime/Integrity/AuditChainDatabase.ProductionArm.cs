using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using SQLitePCL;

namespace SharpInspect.Runtime.Integrity;

/// <summary>
/// Schema-32 central-audit integration. Arm events are signed metadata entries
/// in the existing chain (the schema-31 envelope and every earlier position
/// column stay byte-for-byte identical); no new human identity envelope and no
/// rewrite of an earlier schema is introduced.
/// </summary>
internal static partial class AuditChainDatabase
{
    private sealed record ProductionArmVerification(
        ProductionArmStoreOptions Options,
        ProductionArmSourceOptions Source,
        IReadOnlyList<SqliteCommandStore.ProductionArmStoredRow> Rows)
    {
        internal long MetadataCount => Rows.Count + 1L;
    }

    private static ProductionArmVerification? BeginProductionArmVerification(sqlite3 database, long schemaVersion,
        ProductionArmStoreOptions? armOptions, ProductionAdmissionStoreOptions? admissionOptions,
        RecipeSelectionStoreOptions? selectionOptions, RecipeActivationStoreOptions? activationOptions,
        CalibrationGovernanceStoreOptions? governanceOptions, StoreDeadline deadline)
    {
        var tableCount = Scalar(database, @"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND
            name IN ('production_arm_store_config','production_arm_events');", deadline);
        if (schemaVersion < ProductionArmStoreOptions.SchemaVersion)
        {
            Require(armOptions is null && tableCount == 0, "ProductionArmGovernedMigrationRequired");
            return null;
        }
        if (schemaVersion == ProductionArmStoreOptions.SchemaVersion)
        {
            // The schema-32 store always carries the arm ledger and requires the
            // schema-22 report ledger and the human identity/audit stack.
            Require(armOptions is not null && tableCount == 2, "ProductionArmConfigurationRequired");
            Require(admissionOptions is not null, "ProductionAdmissionConfigurationRequired");
        }
        else
        {
            // A later schema keeps every earlier ledger optional, so the arm ledger
            // is present only when its option is declared and its tables match.
            Require(tableCount == (armOptions is null ? 0 : 2), "ProductionArmConfigurationRequired");
            if (armOptions is null) return null;
        }
        SqliteCommandStore.RequireConfiguredProductionArm(database, armOptions!, deadline);
        Require(Scalar(database, "SELECT COUNT(*) FROM production_arm_store_config;", deadline) == 1,
            "ProductionArmConfigurationMismatch");
        var rows = SqliteCommandStore.ReadProductionArmRows(database, armOptions!, deadline);
        // Bidirectional exact metadata membership: every arm row has exactly one
        // signed metadata entry, and the walk below proves the reverse direction
        // (a metadata entry without a stored row fails closed).
        Require(Scalar(database, "SELECT COALESCE(MAX(Position),0) FROM production_arm_events;", deadline) == rows.Count &&
            Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='ProductionArmEvent';", deadline) == rows.Count &&
            Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='ProductionArmStoreActivated';",
                deadline) == 1,
            "ProductionArmAuditCountMismatch");
        if (rows.Any(row => row.Event.Cause == ProductionArmCause.PlcActivation))
            Require(selectionOptions is not null && activationOptions is not null &&
                TableExists(database, "recipe_change_events", deadline) &&
                TableExists(database, "recipe_activation_events", deadline) &&
                TableExists(database, "recipe_selection_store_config", deadline),
                "ProductionArmPlcHandshakeUnavailable");
        return new(armOptions!, new ProductionArmSourceOptions(selectionOptions, activationOptions, governanceOptions, admissionOptions),
            rows);
    }

    private static void VerifyProductionArmMetadata(sqlite3 database, ProductionArmVerification verification,
        string kind, long sequence, string hash, byte[] payload, StoreDeadline deadline)
    {
        if (kind == "ProductionArmStoreActivated")
        {
            Require(payload.AsSpan().SequenceEqual(verification.Options.EncodeActivationPayload()),
                "ProductionArmActivationBindingMismatch");
            Require(!verification.Rows.Any(value => value.AuditSequence <= sequence),
                "ProductionArmActivationOrderInvalid");
            return;
        }
        var row = verification.Rows.SingleOrDefault(value => value.AuditSequence == sequence);
        Require(kind == "ProductionArmEvent" && row is not null && row.AuditHash == hash,
            "ProductionArmAuditRowMissing");
        SqliteCommandStore.ValidateProductionArmAuditRow(database, row!, deadline);
    }

    internal static void RequireFullProductionArmVerification(sqlite3 database, AuditIntegrityReport report,
        StoreDeadline deadline) => Require(report.VerifiedFromSequence == 1 &&
        report.VerifiedThroughSequence == Tail(database, deadline).Sequence, "ProductionArmVerificationBudgetExceeded");

    private static string ProductionArmAuditSchema()
    {
        // The schema-31 envelope is reused unchanged: arm events are metadata
        // entries with every typed position column NULL. No existing audit
        // position, envelope or pre-32 schema is rewritten.
        var sql = SchemaSqlFor(RecipeSelectionStoreOptions.SchemaVersion).Replace(
            "PRAGMA user_version=31;", "PRAGMA user_version=32;", StringComparison.Ordinal);
        var start = sql.LastIndexOf(" OR (Kind='RecipeChangeEvent'", StringComparison.Ordinal);
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
        return sql.Insert(close + 1, string.Concat(new[] { "ProductionArmStoreActivated", "ProductionArmEvent" }
            .Select(kind => $" OR (Kind='{kind}' AND Sequence>1 AND {empty})")));
    }

    internal static (long Sequence, string Hash) AppendProductionArmMetadata(sqlite3 database,
        AuditIntegrityPolicy policy, IAuditSigningKey key, string kind, byte[] payload,
        ProductionArmStoreOptions options, StoreDeadline deadline, long? productionArmReserveOverride = null)
    {
        Require(Scalar(database, "PRAGMA user_version;", deadline) is
            ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion
            or ProductionImageEvidenceStoreOptions.SchemaVersion,
            "ProductionArmSchemaRequired");
        Require(kind is "ProductionArmStoreActivated" or "ProductionArmEvent", "ProductionArmAuditKindInvalid");
        options.Validate();
        Require(payload.Length is > 0 and <= ProductionArmStoreOptions.MaximumAuditPayloadBytes,
            "ProductionArmAuditPayloadCapacityExceeded");
        if (kind == "ProductionArmStoreActivated")
            Require(Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='ProductionArmStoreActivated';",
                deadline) == 0, "ProductionArmActivationConflict");
        var sequence = AppendEntry(database, policy, kind, null, payload, deadline,
            productionArmData: true, productionArmReserveOverride: productionArmReserveOverride);
        var tail = Tail(database, deadline);
        Require(sequence == tail.Sequence, "ProductionArmAuditMismatch");
        if (tail.Sequence - Scalar(database, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(database, policy, key, deadline);
        return (sequence, tail.Hash);
    }
}
