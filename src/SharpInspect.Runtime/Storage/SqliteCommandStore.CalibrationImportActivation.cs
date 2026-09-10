using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    /// <summary>
    /// Reads the governance, import and imaging tails used by calibration activation from
    /// one read-only SQLite snapshot, including exact selected profile resolution.
    /// </summary>
    internal async ValueTask<CalibrationImportActivationCatalogSnapshot>
        ReadCalibrationImportActivationCatalogAsync(string logicalCameraRole,
            IReadOnlyList<CalibrationProfileReference> selectedProfiles,
            CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(logicalCameraRole))
            throw new ArgumentException("ImagingSetupLogicalRoleInvalid", nameof(logicalCameraRole));
        ArgumentNullException.ThrowIfNull(selectedProfiles);
        var requestedProfiles = selectedProfiles.ToArray();

        var initialized = await Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!initialized.Committed || _databasePath is null || _options.CalibrationImports is null)
            return CalibrationImportActivationCatalogSnapshot.Unavailable("CalibrationImportUnavailable");

        try
        {
            return await Task.Run(() => ReadCalibrationImportActivationCatalogCore(
                logicalCameraRole, requestedProfiles, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CalibrationImportActivationCatalogSnapshot.Unavailable(
                SqliteAuditIntegrityQuery.FaultReason(exception,
                    "CalibrationImportActivationCatalogUnavailable"));
        }
    }

    private CalibrationImportActivationCatalogSnapshot ReadCalibrationImportActivationCatalogCore(
        string logicalCameraRole, IReadOnlyList<CalibrationProfileReference> selectedProfiles,
        CancellationToken cancellationToken)
    {
        if (!StoragePathValidator.TryValidate(_options, out var path, out var pathReason))
            return CalibrationImportActivationCatalogSnapshot.Unavailable(pathReason);
        if (_policy is null || _signingKey is null)
            return CalibrationImportActivationCatalogSnapshot.Unavailable("CalibrationImportAuditUnavailable");

        var importOptions = _options.CalibrationImports ??
            throw new InvalidOperationException("CalibrationImportConfigurationRequired");
        using var connection = SqliteNative.Open(path, readOnly: true);
        var database = connection.Handle!;
        var deadline = new StoreDeadline(_options.QueryTimeout);
        SqliteNative.ConfigureSqliteLimit(database, _options);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        var committed = false;
        try
        {
            var schema = checked((int)AuditChainDatabase.Scalar(database,
                "PRAGMA user_version;", deadline));
            AuditChainDatabase.Require(schema is CalibrationImportStoreOptions.SchemaVersion or
                ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion,
                schema < CalibrationImportStoreOptions.SchemaVersion
                    ? "CalibrationImportGovernedMigrationRequired" : "StoreSchemaTooNew");
            AuditChainDatabase.Require(_options.PreviewSessions is not null &&
                _options.CalibrationGovernance is not null && _options.CalibrationSessions is not null &&
                _options.ImagingSetup is not null,
                "CalibrationImportsRequiresPreviewGovernanceCalibrationAndImagingSetup");

            SqliteNative.ConfigureSqliteLimit(database, _options, schema);
            var verification = AuditChainDatabase.Verify(database, _policy, _signingKey.KeyId,
                _signingKey.PublicKeyBase64,
                new AuditVerificationRequest(0, _policy.MaximumVerificationEntries), startup: false,
                deadline, validateAnchorReceipt: false,
                archiveOptions: _options.AlgorithmResultArchive,
                recipeDraftOptions: _options.RecipeDrafts,
                cameraSetupOptions: _options.CameraSetup,
                cameraRecoveryOptions: _options.CameraRecovery,
                cameraNetworkOptions: _options.CameraNetwork,
                imagingSetupOptions: _options.ImagingSetup,
                calibrationSessionOptions: _options.CalibrationSessions,
                governanceOptions: _options.CalibrationGovernance,
                releaseOptions: _options.RecipeReleases,
                contractOptions: _options.PlcResultContracts,
                activationOptions: _options.RecipeActivations,
                previewOptions: _options.PreviewSessions,
                importOptions: importOptions, manualOptions: _options.ManualInspections,
                productionAdmissionOptions: _options.ProductionAdmission);

            RequireFullCalibrationImportActivationVerification(database, verification, deadline);

            var governance = ReadCalibrationGovernanceRecords(database,
                _options.CalibrationGovernance, deadline);
            var importRows = ReadCalibrationImportRows(database, importOptions, deadline);
            var imports = importRows
                .Select(value => CalibrationGovernanceCodec.DecodeImport(
                    DecodeImportRowPayload(value, importOptions)))
                .ToArray();
            var imaging = ReadImagingSetupState(database, logicalCameraRole, deadline).Current;
            var catalog = CalibrationImportActivationCatalog.Create(governance.Records, imports, imaging,
                selectedProfiles, governance.Position, governance.ContentHash,
                importRows.LastOrDefault()?.Position ?? 0,
                importRows.LastOrDefault()?.RecordContentHash);

            var governanceTail = AuditChainDatabase.Read(database,
                "SELECT Position,RecordContentHash FROM calibration_governance_events " +
                "ORDER BY Position DESC LIMIT 1;", deadline,
                statement => (Position: SqliteNative.ColumnInt64(statement, 0),
                    Hash: SqliteNative.ColumnText(statement, 1))).SingleOrDefault();
            var importTail = AuditChainDatabase.Read(database,
                "SELECT Position,RecordContentHash FROM calibration_import_events " +
                "ORDER BY Position DESC LIMIT 1;", deadline,
                statement => (Position: SqliteNative.ColumnInt64(statement, 0),
                    Hash: SqliteNative.ColumnText(statement, 1))).SingleOrDefault();
            AuditChainDatabase.Require(catalog.TailsMatch(governanceTail.Position, governanceTail.Hash,
                importTail.Position, importTail.Hash), "CalibrationImportActivationTailChanged");

            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            return catalog;
        }
        finally
        {
            if (!committed)
            {
                try { SqliteNative.Execute(database, "ROLLBACK;", deadline); }
                catch { }
            }
        }
    }

    private void RequireFullCalibrationImportActivationVerification(sqlite3 database,
        AuditIntegrityReport verification, StoreDeadline deadline)
    {
        if (_options.AlarmPolicy is not null)
            AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
        if (_options.AlgorithmResultArchive is not null)
            AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
        AuditChainDatabase.RequireFullRecipeDraftVerification(database, verification, deadline,
            _options.RecipeDrafts);
        if (_options.CameraSetup is not null)
            AuditChainDatabase.RequireFullCameraSetupVerification(database, verification, deadline,
                _options.CameraSetup);
        if (_options.CameraRecovery is not null)
            AuditChainDatabase.RequireFullCameraRecoveryVerification(database, verification, deadline,
                _options.CameraRecovery);
        if (_options.CameraNetwork is not null)
            AuditChainDatabase.RequireFullCameraNetworkVerification(database, verification, deadline,
                _options.CameraNetwork);
        if (_options.ImagingSetup is not null)
            AuditChainDatabase.RequireFullImagingSetupVerification(database, verification, deadline,
                _options.ImagingSetup);
        if (_options.CalibrationGovernance is not null)
            AuditChainDatabase.RequireFullCalibrationGovernanceVerification(database, verification, deadline,
                _options.CalibrationGovernance);
        if (_options.RecipeReleases is not null)
            AuditChainDatabase.RequireFullRecipeReleaseVerification(database, verification, deadline,
                _options.RecipeReleases, _options.RecipeDrafts, _options.CalibrationGovernance);
        if (_options.PlcResultContracts is not null)
            AuditChainDatabase.RequireFullPlcResultContractVerification(database, verification, deadline,
                _options.PlcResultContracts);
        if (_options.RecipeActivations is not null)
            AuditChainDatabase.RequireFullRecipeActivationVerification(database, verification, deadline,
                _options.RecipeActivations, _options.RecipeReleases, _options.PlcResultContracts,
                _options.CalibrationGovernance);
        if (_options.PreviewSessions is not null)
            AuditChainDatabase.RequireFullPreviewSessionVerification(database, verification, deadline,
                _options.PreviewSessions);
            if (_options.CalibrationImports is not null)
                AuditChainDatabase.RequireFullCalibrationImportVerification(database, verification, deadline,
                    _options.CalibrationImports);
            if (_options.ManualInspections is not null)
                AuditChainDatabase.RequireFullManualInspectionVerification(database, verification, deadline, _options.ManualInspections);
            if (_options.ProductionAdmission is not null)
                AuditChainDatabase.RequireFullProductionAdmissionVerification(database, verification, deadline,
                    _options.ProductionAdmission);
    }
}
