using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal static void VerifyRecipeSelectionReadGuard(sqlite3 database, ProductionStoreOptions options, StoreDeadline deadline)
    {
        var selections = options.RecipeSelections ?? throw new InvalidOperationException("RecipeSelectionConfigurationRequired");
        selections.Validate();
        var policy = options.AuditIntegrityPolicy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        AuditChainDatabase.Require(AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline) is
            RecipeSelectionStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion
            or ProductionImageEvidenceStoreOptions.SchemaVersion,
            "RecipeSelectionGovernedMigrationRequired");
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
            traceStoragePolicyOptions: options.TraceStoragePolicies, qualificationCycleOptions: options.QualificationCycles,
            plcCommunicationOptions: options.PlcCommunication, productionInspectionOptions: options.ProductionInspections,
            productionRecoveryOptions: options.ProductionRecovery, partIdentityOptions: options.PartIdentities,
            productionArmOptions: options.ProductionArming, recipeSelectionOptions: selections, recipeLifecycleOptions: options.RecipeLifecycle, imageEvidenceOptions: options.ImageEvidence);
        AuditChainDatabase.RequireFullRecipeSelectionVerification(database, report, deadline);
    }
}
