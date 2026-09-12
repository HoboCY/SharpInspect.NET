using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Schema-34 production image evidence storage, entirely separate from every
/// earlier ledger: one bounded immutable configuration row and the two pending
/// initial facts of one inspection whose image bytes stay in the qualified
/// production image stage. The manifest binds the inspection, the stage
/// identity, the evidence policy and the content and payload hashes; the work
/// row binds the same inspection and manifest to the single declared
/// finalization kind. Both tables are append-only initial facts: this file
/// declares no terminal state, no acknowledgement and no capacity decision of
/// its own, so the caller owns every later state fact.
///
/// InitializeImageEvidenceSchema appends the signed activation in the same
/// caller-owned transaction that inserts the immutable configuration row.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    internal const string ImageEvidenceActivationKind = "ImageEvidenceStoreActivated";

    internal const string ImageEvidenceSchemaSql = @"
        CREATE TABLE image_evidence_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1), FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaxImages INTEGER NOT NULL CHECK(MaxImages>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaxTotalBytes INTEGER NOT NULL CHECK(MaxTotalBytes>0),
            StageRootBindingHash TEXT NOT NULL CHECK(length(StageRootBindingHash)=64),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE pending_image_manifests(
            ManifestId TEXT NOT NULL PRIMARY KEY CHECK(length(ManifestId)=36),
            InspectionId TEXT NOT NULL UNIQUE CHECK(length(InspectionId)=36)
                REFERENCES production_inspection_cores(InspectionId),
            StageId TEXT NOT NULL UNIQUE CHECK(length(StageId)=36),
            EvidencePolicyHash TEXT NOT NULL CHECK(length(EvidencePolicyHash)=64),
            ContentHash TEXT NOT NULL UNIQUE CHECK(length(ContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            Payload TEXT NOT NULL CHECK(length(Payload)>0));
        CREATE TABLE pending_image_work(
            WorkId TEXT NOT NULL PRIMARY KEY CHECK(length(WorkId)=36),
            InspectionId TEXT NOT NULL UNIQUE CHECK(length(InspectionId)=36)
                REFERENCES production_inspection_cores(InspectionId),
            ManifestId TEXT NOT NULL UNIQUE CHECK(length(ManifestId)=36)
                REFERENCES pending_image_manifests(ManifestId),
            Kind TEXT NOT NULL CHECK(Kind='FinalizePng'),
            State TEXT NOT NULL CHECK(State='Pending'),
            ContentHash TEXT NOT NULL UNIQUE CHECK(length(ContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            Payload TEXT NOT NULL CHECK(length(Payload)>0));
        CREATE TRIGGER image_evidence_config_immutable_update BEFORE UPDATE
            ON image_evidence_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableImageEvidenceConfiguration');
        END;
        CREATE TRIGGER image_evidence_config_immutable_delete BEFORE DELETE
            ON image_evidence_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableImageEvidenceConfiguration');
        END;
        CREATE TRIGGER pending_image_manifests_immutable_update BEFORE UPDATE
            ON pending_image_manifests BEGIN
            SELECT RAISE(ABORT,'ImmutablePendingImageManifest');
        END;
        CREATE TRIGGER pending_image_manifests_immutable_delete BEFORE DELETE
            ON pending_image_manifests BEGIN
            SELECT RAISE(ABORT,'ImmutablePendingImageManifest');
        END;
        CREATE TRIGGER pending_image_work_immutable_update BEFORE UPDATE
            ON pending_image_work BEGIN
            SELECT RAISE(ABORT,'ImmutablePendingImageWork');
        END;
        CREATE TRIGGER pending_image_work_immutable_delete BEFORE DELETE
            ON pending_image_work BEGIN
            SELECT RAISE(ABORT,'ImmutablePendingImageWork');
        END;";

    /// <summary>
    /// Creates the declared schema-34 tables and inserts the one immutable configuration
    /// row. This variant is used only where the schema already carries its signed
    /// activation entry inside the same transaction.
    /// </summary>
    internal static void InitializeImageEvidenceTables(sqlite3 database,
        ProductionImageEvidenceStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deadline);
        options.Validate();
        SqliteNative.Execute(database, ImageEvidenceSchemaSql, deadline);
        AuditChainDatabase.Execute(database, @"INSERT INTO image_evidence_store_config
            (Id,FormatVersion,MaxImages,MaximumPayloadBytes,MaxTotalBytes,StageRootBindingHash,BindingHash)
            VALUES(1,?,?,?,?,?,?);", deadline,
            ImageEvidenceNumber(ProductionImageEvidenceStoreOptions.FormatVersion),
            ImageEvidenceNumber(options.MaxImages), ImageEvidenceNumber(options.MaximumPayloadBytes),
            ImageEvidenceNumber(options.MaxTotalBytes), options.StageRootBindingHash, options.BindingHash);
    }

    /// <summary>
    /// Creates the declared schema-34 tables, inserts the one immutable configuration
    /// row and appends the signed store-activation entry inside the caller's writer or
    /// migration transaction, so the configuration and its activation evidence commit
    /// together and exactly once.
    /// </summary>
    internal static void InitializeImageEvidenceSchema(sqlite3 database,
        ProductionImageEvidenceStoreOptions options, StoreDeadline deadline, AuditIntegrityPolicy policy,
        IAuditSigningKey key)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(key);
        InitializeImageEvidenceTables(database, options, deadline);
        AuditChainDatabase.AppendImageEvidenceMetadata(database, policy, key, ImageEvidenceActivationKind,
            options.EncodeActivationPayload(), options, deadline);
    }

    /// <summary>
    /// Requires the single immutable configuration row to equal the caller's explicitly
    /// bounded options, including the qualified production image stage binding. A
    /// missing, foreign or edited row fails closed.
    /// </summary>
    internal static void RequireConfiguredImageEvidence(sqlite3 database,
        ProductionImageEvidenceStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deadline);
        options.Validate();
        var rows = AuditChainDatabase.Read(database, @"SELECT FormatVersion,MaxImages,MaximumPayloadBytes,
            MaxTotalBytes,StageRootBindingHash,BindingHash FROM image_evidence_store_config WHERE Id=1 LIMIT 2;",
            deadline, value => new[]
            {
                SqliteNative.ColumnText(value, 0) ?? string.Empty,
                SqliteNative.ColumnText(value, 1) ?? string.Empty,
                SqliteNative.ColumnText(value, 2) ?? string.Empty,
                SqliteNative.ColumnText(value, 3) ?? string.Empty,
                SqliteNative.ColumnText(value, 4) ?? string.Empty,
                SqliteNative.ColumnText(value, 5) ?? string.Empty
            });
        AuditChainDatabase.Require(rows.Count == 1 && rows[0].SequenceEqual(new[]
        {
            ImageEvidenceNumber(ProductionImageEvidenceStoreOptions.FormatVersion),
            ImageEvidenceNumber(options.MaxImages), ImageEvidenceNumber(options.MaximumPayloadBytes),
            ImageEvidenceNumber(options.MaxTotalBytes), options.StageRootBindingHash, options.BindingHash
        }), "ImageEvidenceConfigurationMismatch");
    }

    /// <summary>
    /// Full central-audit proof for the schema-34 image evidence store before any
    /// pending fact is projected: the signed chain is verified from genesis through the
    /// tail with the option supplied, so the reverse metadata membership and the exact
    /// activation binding, the immutable configuration row and the table shape are all
    /// re-proved. A missing option, a foreign generation or a tampered store fails
    /// closed instead of returning evidence.
    /// </summary>
    internal static void VerifyImageEvidenceReadGuard(sqlite3 database, ProductionStoreOptions options,
        StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deadline);
        var imageEvidence = options.ImageEvidence ??
            throw new InvalidOperationException("ImageEvidenceConfigurationRequired");
        imageEvidence.Validate();
        var policy = options.AuditIntegrityPolicy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        AuditChainDatabase.Require(AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline) ==
            ProductionImageEvidenceStoreOptions.SchemaVersion, "ImageEvidenceGovernedMigrationRequired");
        using var key = WindowsMachineAuditKey.Open(policy, false, out _);
        var report = AuditChainDatabase.Verify(database, policy, key.KeyId, key.PublicKeyBase64,
            new AuditVerificationRequest(0, policy.MaximumVerificationEntries), startup: false, deadline,
            validateAnchorReceipt: false, archiveOptions: options.AlgorithmResultArchive,
            recipeDraftOptions: options.RecipeDrafts, cameraSetupOptions: options.CameraSetup,
            cameraRecoveryOptions: options.CameraRecovery, cameraNetworkOptions: options.CameraNetwork,
            imagingSetupOptions: options.ImagingSetup, calibrationSessionOptions: options.CalibrationSessions,
            governanceOptions: options.CalibrationGovernance, releaseOptions: options.RecipeReleases,
            contractOptions: options.PlcResultContracts, activationOptions: options.RecipeActivations,
            previewOptions: options.PreviewSessions, importOptions: options.CalibrationImports,
            manualOptions: options.ManualInspections, productionAdmissionOptions: options.ProductionAdmission,
            stationQualificationOptions: options.StationQualifications, recipeTransferOptions: options.RecipeTransfers,
            traceStoragePolicyOptions: options.TraceStoragePolicies,
            qualificationCycleOptions: options.QualificationCycles,
            plcCommunicationOptions: options.PlcCommunication,
            productionInspectionOptions: options.ProductionInspections,
            productionRecoveryOptions: options.ProductionRecovery, partIdentityOptions: options.PartIdentities,
            recipeSelectionOptions: options.RecipeSelections, productionArmOptions: options.ProductionArming,
            recipeLifecycleOptions: options.RecipeLifecycle, imageEvidenceOptions: imageEvidence);
        AuditChainDatabase.RequireFullImageEvidenceVerification(database, report, deadline);
    }

    private static string ImageEvidenceNumber(long value) => value.ToString(CultureInfo.InvariantCulture);
}
