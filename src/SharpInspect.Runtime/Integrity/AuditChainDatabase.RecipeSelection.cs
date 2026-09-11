using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using SQLitePCL;

namespace SharpInspect.Runtime.Integrity;

internal static partial class AuditChainDatabase
{
    private sealed record RecipeSelectionVerification(
        RecipeSelectionStoreOptions Options,
        IReadOnlyList<SqliteCommandStore.RecipeSelectionStoredRevision> Revisions,
        IReadOnlyList<SqliteCommandStore.RecipeChangeStoredEvent> Changes)
    {
        internal long MetadataCount => Revisions.Count + Changes.Count + 1L;
    }

    private static RecipeSelectionVerification? BeginRecipeSelectionVerification(sqlite3 database, long schemaVersion,
        RecipeSelectionStoreOptions? options, RecipeReleaseStoreOptions? releaseOptions,
        RecipeActivationStoreOptions? activationOptions, PlcCommunicationStoreOptions? communicationOptions, StoreDeadline deadline)
    {
        var tableCount = Scalar(database, @"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND
            name IN ('recipe_selection_store_config','recipe_selection_revisions','recipe_change_events');", deadline);
        if (schemaVersion < RecipeSelectionStoreOptions.SchemaVersion)
        {
            Require(options is null && tableCount == 0, "RecipeSelectionGovernedMigrationRequired");
            return null;
        }
        Require(options is not null && tableCount == 3, "RecipeSelectionConfigurationRequired");
        Require(releaseOptions is not null && activationOptions is not null && communicationOptions is not null,
            "RecipeSelectionDependenciesRequired");
        SqliteCommandStore.RequireConfiguredRecipeSelections(database, options!, deadline);
        Require(Scalar(database, "SELECT COUNT(*) FROM recipe_selection_store_config;", deadline) == 1,
            "RecipeSelectionConfigurationMismatch");
        var revisions = SqliteCommandStore.ReadRecipeSelectionRows(database, options!, deadline);
        var changes = SqliteCommandStore.ReadRecipeChangeRows(database, options!, deadline);
        Require(Scalar(database, "SELECT COALESCE(MAX(Position),0) FROM recipe_selection_revisions;", deadline) == revisions.Count &&
            Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeSelectionRevision';", deadline) == revisions.Count &&
            Scalar(database, "SELECT COALESCE(MAX(Position),0) FROM recipe_change_events;", deadline) == changes.Count &&
            Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeChangeEvent';", deadline) == changes.Count &&
            Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeSelectionStoreActivated';", deadline) == 1,
            "RecipeSelectionAuditCountMismatch");
        return new(options!, revisions, changes);
    }

    private static void VerifyRecipeSelectionMetadata(sqlite3 database, RecipeSelectionVerification verification,
        string kind, long sequence, string hash, byte[] payload, StoreDeadline deadline)
    {
        if (kind == "RecipeSelectionStoreActivated")
        {
            Require(payload.AsSpan().SequenceEqual(verification.Options.EncodeActivationPayload()),
                "RecipeSelectionActivationBindingMismatch");
            Require(!verification.Revisions.Any(value => value.AuditSequence <= sequence) &&
                !verification.Changes.Any(value => value.AuditSequence <= sequence), "RecipeSelectionActivationOrderInvalid");
            return;
        }
        if (kind == "RecipeSelectionRevision")
        {
            var row = verification.Revisions.SingleOrDefault(value => value.AuditSequence == sequence);
            Require(row is not null && row.AuditHash == hash, "RecipeSelectionAuditRowMissing");
            SqliteCommandStore.ValidateRecipeSelectionAuditRow(database, row!, deadline);
            return;
        }
        var change = verification.Changes.SingleOrDefault(value => value.AuditSequence == sequence);
        Require(kind == "RecipeChangeEvent" && change is not null && change.AuditHash == hash, "RecipeChangeAuditRowMissing");
        SqliteCommandStore.ValidateRecipeChangeAuditRow(database, change!, deadline);
    }

    internal static void RequireFullRecipeSelectionVerification(sqlite3 database, AuditIntegrityReport report,
        StoreDeadline deadline) => Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(database, deadline).Sequence, "RecipeSelectionVerificationBudgetExceeded");

    private static string RecipeSelectionAuditSchema()
    {
        // Selection streams use metadata entries. Their independent positions and
        // previous stream hashes are signed inside their complete canonical payloads.
        // No existing audit position, envelope or pre-31 schema is rewritten.
        var sql = SchemaSqlFor(ProductionRecoveryStoreOptions.SchemaVersion).Replace(
            "PRAGMA user_version=30;", "PRAGMA user_version=31;", StringComparison.Ordinal);
        var start = sql.LastIndexOf(" OR (Kind='PartIdentityEvent'", StringComparison.Ordinal);
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
            "RecipeSelectionStoreActivated", "RecipeSelectionRevision", "RecipeChangeEvent"
        }.Select(kind => $" OR (Kind='{kind}' AND Sequence>1 AND {empty})")));
    }

    internal static (long Sequence, string Hash) AppendRecipeSelectionMetadata(sqlite3 database,
        AuditIntegrityPolicy policy, IAuditSigningKey key, string kind, byte[] payload,
        RecipeSelectionStoreOptions options, StoreDeadline deadline, long? recipeChangeReserveOverride = null)
    {
        Require(Scalar(database, "PRAGMA user_version;", deadline) == RecipeSelectionStoreOptions.SchemaVersion,
            "RecipeSelectionSchemaRequired");
        Require(kind is "RecipeSelectionStoreActivated" or "RecipeSelectionRevision" or "RecipeChangeEvent",
            "RecipeSelectionAuditKindInvalid");
        options.Validate();
        Require(payload.Length is > 0 and <= RecipeSelectionStoreOptions.MaximumAuditPayloadBytes,
            "RecipeSelectionAuditPayloadCapacityExceeded");
        if (kind == "RecipeSelectionStoreActivated")
            Require(Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeSelectionStoreActivated';", deadline) == 0,
                "RecipeSelectionActivationConflict");
        var sequence = AppendEntry(database, policy, kind, null, payload, deadline,
            recipeChangeReserveOverride: recipeChangeReserveOverride);
        var tail = Tail(database, deadline);
        Require(sequence == tail.Sequence, "RecipeSelectionAuditMismatch");
        if (tail.Sequence - Scalar(database, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(database, policy, key, deadline);
        return (sequence, tail.Hash);
    }
}
