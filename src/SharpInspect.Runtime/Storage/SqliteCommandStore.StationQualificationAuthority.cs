using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    private void RequireStationQualificationWriteSnapshot(sqlite3 database,
        AuditIntegrityReport verification, StoreDeadline deadline)
    {
        if (_options.StationQualifications is not null)
            AuditChainDatabase.RequireFullStationQualificationVerification(database, verification, deadline,
                _options.StationQualifications);
    }

    private void VerifyStationQualificationWriterAuthority(sqlite3 database, StoreDeadline deadline)
    {
        // The background integrity state is advisory. Every raw continuation
        // verifies the actual signed chain inside its BEGIN IMMEDIATE snapshot.
        var report = AuditChainDatabase.Verify(database, _policy!, _signingKey!.KeyId,
            _signingKey.PublicKeyBase64, new AuditVerificationRequest(0, _policy!.MaximumVerificationEntries),
            false, deadline, validateAnchorReceipt: false,
            archiveOptions: _options.AlgorithmResultArchive,
            recipeDraftOptions: _options.RecipeDrafts, cameraSetupOptions: _options.CameraSetup,
            cameraRecoveryOptions: _options.CameraRecovery, cameraNetworkOptions: _options.CameraNetwork,
            imagingSetupOptions: _options.ImagingSetup, calibrationSessionOptions: _options.CalibrationSessions,
            governanceOptions: _options.CalibrationGovernance, releaseOptions: _options.RecipeReleases,
            contractOptions: _options.PlcResultContracts, activationOptions: _options.RecipeActivations,
            previewOptions: _options.PreviewSessions, importOptions: _options.CalibrationImports,
            manualOptions: _options.ManualInspections, productionAdmissionOptions: _options.ProductionAdmission,
            stationQualificationOptions: _options.StationQualifications,
                recipeTransferOptions: _options.RecipeTransfers,
                traceStoragePolicyOptions: _options.TraceStoragePolicies,
                qualificationCycleOptions: _options.QualificationCycles,
                plcCommunicationOptions: _options.PlcCommunication,
                productionInspectionOptions: _options.ProductionInspections,
                partIdentityOptions: _options.PartIdentities);
            RecipeTransferReadGuard.RequireVerified(database, report, deadline, _options);
            if (_options.PlcCommunication is not null)
                AuditChainDatabase.RequireFullPlcCommunicationVerification(database, report, deadline,
                    _options.PlcCommunication);
            TraceStoragePolicyReadGuard.RequireVerified(database, report, deadline, _options);
        AuditChainDatabase.RequireFullStationQualificationVerification(database, report, deadline,
            _options.StationQualifications);
    }
}
