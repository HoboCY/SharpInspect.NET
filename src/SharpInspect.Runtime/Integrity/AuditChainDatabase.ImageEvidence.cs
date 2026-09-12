using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using SQLitePCL;

namespace SharpInspect.Runtime.Integrity;

/// <summary>
/// Schema-34 central-audit integration. The store activation is one signed metadata
/// entry in the existing chain: the schema-33 envelope and every earlier position
/// column stay byte-for-byte identical, no new human identity envelope is introduced
/// and no earlier schema is rewritten. The entry is the only schema-34 metadata kind;
/// the pending manifest and work facts are stored facts without their own signed
/// metadata entry, exactly like the lifecycle configuration row.
/// </summary>
internal static partial class AuditChainDatabase
{
    private sealed record ImageEvidenceVerification(ProductionImageEvidenceStoreOptions Options)
    {
        internal long MetadataCount => 1L;
    }

    /// <summary>
    /// Presence proof for the schema-34 image evidence store. A store below the
    /// generation must not carry the option or any of the declared tables; a store at
    /// the generation must carry exactly the three declared tables and exactly one
    /// signed activation entry, and a caller that supplies the option additionally
    /// re-proves the immutable configuration row against it.
    /// </summary>
    private static ImageEvidenceVerification? BeginImageEvidenceVerification(sqlite3 database,
        long storedSchemaVersion, ProductionImageEvidenceStoreOptions? imageEvidenceOptions, StoreDeadline deadline)
    {
        var tableCount = Scalar(database, @"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND
            name IN ('image_evidence_store_config','pending_image_manifests','pending_image_work');", deadline);
        if (storedSchemaVersion < ProductionImageEvidenceStoreOptions.SchemaVersion)
        {
            Require(imageEvidenceOptions is null && tableCount == 0, "ImageEvidenceGovernedMigrationRequired");
            return null;
        }
        if (imageEvidenceOptions is null)
        {
            // Only a schema-36 store that declares the feature absent may omit the ledger;
            // every older generation still requires it exactly as before.
            Require(storedSchemaVersion >= ProductionOutboxStoreOptions.SchemaVersion && tableCount == 0,
                "ImageEvidenceConfigurationRequired");
            return null;
        }
        // Every reader must supply the exact image binding. Presence alone cannot
        // prove which deployment configuration the signed activation describes.
        Require(tableCount == 3, "ImageEvidenceConfigurationRequired");
        SqliteCommandStore.RequireConfiguredImageEvidence(database, imageEvidenceOptions!, deadline);
        Require(Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='ImageEvidenceStoreActivated';",
            deadline) == 1, "ImageEvidenceActivationMissing");
        return new(imageEvidenceOptions!);
    }

    private static void VerifyImageEvidenceMetadata(sqlite3 database, ImageEvidenceVerification verification,
        string kind, long sequence, string hash, byte[] payload, StoreDeadline deadline)
    {
        Require(kind == SqliteCommandStore.ImageEvidenceActivationKind, "ImageEvidenceAuditKindUnsupported");
        Require(payload.Length is > 0 and <= ProductionImageEvidenceStoreOptions.MaximumAuditPayloadBytes,
            "ImageEvidenceAuditPayloadCapacityExceeded");
        // The signed payload is the exact option binding, so the activation entry can
        // never describe a different stage, capacity set or format version than the
        // immutable configuration row proved above.
        Require(payload.AsSpan().SequenceEqual(verification.Options.EncodeActivationPayload()),
            "ImageEvidenceActivationBindingMismatch");
    }

    internal static void RequireFullImageEvidenceVerification(sqlite3 database, AuditIntegrityReport report,
        StoreDeadline deadline) => Require(report.VerifiedFromSequence == 1 &&
        report.VerifiedThroughSequence == Tail(database, deadline).Sequence,
        "ImageEvidenceVerificationBudgetExceeded");

    private static string ImageEvidenceAuditSchema()
    {
        // The schema-33 envelope is reused unchanged: the store activation is a metadata
        // entry with every typed position column NULL. No existing audit position,
        // envelope or pre-34 schema is rewritten.
        var sql = RecipeLifecycleAuditSchema().Replace(
            "PRAGMA user_version=33;", "PRAGMA user_version=34;", StringComparison.Ordinal);
        var start = sql.LastIndexOf(" OR (Kind='RecipeLifecycleEvent'", StringComparison.Ordinal);
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
        return sql.Insert(close + 1,
            $" OR (Kind='{SqliteCommandStore.ImageEvidenceActivationKind}' AND Sequence>1 AND {empty})");
    }

    /// <summary>
    /// Appends the one signed store-activation entry. The caller must already be inside
    /// the transaction that inserts the immutable configuration row, so configuration
    /// and activation evidence become durable together and the activation is signed
    /// exactly once.
    /// </summary>
    internal static (long Sequence, string Hash) AppendImageEvidenceMetadata(sqlite3 database,
        AuditIntegrityPolicy policy, IAuditSigningKey key, string kind, byte[] payload,
        ProductionImageEvidenceStoreOptions options, StoreDeadline deadline)
    {
        Require(Scalar(database, "PRAGMA user_version;", deadline) is
            ProductionImageEvidenceStoreOptions.SchemaVersion or ProductionImageFinalizationStoreOptions.SchemaVersion or ProductionOutboxStoreOptions.SchemaVersion
            or ProductionOutboxStoreOptions.SchemaVersion,
            "ImageEvidenceSchemaRequired");
        Require(kind == SqliteCommandStore.ImageEvidenceActivationKind, "ImageEvidenceAuditKindInvalid");
        options.Validate();
        Require(payload.Length is > 0 and <= ProductionImageEvidenceStoreOptions.MaximumAuditPayloadBytes,
            "ImageEvidenceAuditPayloadCapacityExceeded");
        Require(Scalar(database, "SELECT COUNT(*) FROM audit_entries WHERE Kind='ImageEvidenceStoreActivated';",
            deadline) == 0, "ImageEvidenceActivationConflict");
        var sequence = AppendEntry(database, policy, kind, null, payload, deadline, imageEvidenceData: true);
        var tail = Tail(database, deadline);
        Require(sequence == tail.Sequence, "ImageEvidenceAuditMismatch");
        if (tail.Sequence - Scalar(database, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(database, policy, key, deadline);
        return (sequence, tail.Hash);
    }
}
