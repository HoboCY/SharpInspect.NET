using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    /// <summary>
    /// A private, non-running schema model. It shares the production definitions
    /// and configuration checks, but owns no writer queue, Runtime, or monitor.
    /// Only the startup-maintenance coordinator supplies its exclusive connection.
    /// </summary>
    internal sealed class StartupMaintenanceSchema : IDisposable
    {
        private static readonly string[] RebuiltTables = { "command_attempts", "command_facts", "audit_entries" };
        private readonly SqliteCommandStore _model;
        private readonly int _migrationTargetVersion;

        /// <summary>
        /// One private, non-running schema model for one maintenance operation. The
        /// operation's target generation is explicit, so the source and target profiles
        /// of the same operation stage and rebuild the identical tables even though
        /// their own schema versions differ by one.
        /// </summary>
        internal StartupMaintenanceSchema(ProductionStoreOptions options, int migrationTargetVersion)
        {
            _migrationTargetVersion = migrationTargetVersion;
            _model = new SqliteCommandStore(options, ReadFileLength, startWriter: false);
            if (_model._initializationReason.Length != 0)
            {
                _model.DisposeAsync().GetAwaiter().GetResult();
                throw new InvalidOperationException(_model._initializationReason);
            }
            if (_model.SchemaVersion is not (32 or 33 or 34 or 35) ||
                migrationTargetVersion is not (33 or 34 or 35) ||
                _model.SchemaVersion > migrationTargetVersion || options.RecipeDrafts is null ||
                options.ProductionArming is null || options.LocalIdentity is null || _model._policy is null)
            {
                _model.DisposeAsync().GetAwaiter().GetResult();
                throw new InvalidOperationException("StoreMigrationUnsupportedSourceProfile");
            }
        }

        internal int Version => _model.SchemaVersion;
        internal ProductionStoreOptions Options => _model._options;

        internal void RequireQuiescent(sqlite3 database, StoreDeadline deadline) =>
            _model.RequireMigrationQuiescent(database, deadline);

        internal void ConfigureConnection(sqlite3 database, StoreDeadline deadline)
        {
            SqliteNative.ConfigureSqliteLimit(database, Options);
            if (!_model.ConfigureProductionProfile(database, deadline))
                throw new InvalidOperationException("StoreMigrationProfileUnsupported");
        }

        internal (long Sequence, string Hash) VerifyExisting(SqliteConnection connection, StoreDeadline deadline)
        {
            var database = connection.Handle!;
            if (ReadUserVersion(database, deadline) != Version)
                throw new InvalidOperationException("StoreMigrationSchemaVersionMismatch");
            _model._signingKey?.Dispose();
            _model._signingKey = null;
            var result = _model.InitializeDatabase(connection, deadline, configureProfile: false);
            if (!result.Committed) throw new InvalidOperationException(result.ReasonCode);
            var options = Options;
            var policy = _model._policy!;
            var key = _model._signingKey!;
            var report = AuditChainDatabase.Verify(database, policy, key.KeyId, key.PublicKeyBase64,
                new AuditVerificationRequest(0, policy.MaximumVerificationEntries), startup: false, deadline,
                archiveOptions: options.AlgorithmResultArchive, recipeDraftOptions: options.RecipeDrafts,
                cameraSetupOptions: options.CameraSetup, cameraRecoveryOptions: options.CameraRecovery,
                cameraNetworkOptions: options.CameraNetwork, imagingSetupOptions: options.ImagingSetup,
                calibrationSessionOptions: options.CalibrationSessions, governanceOptions: options.CalibrationGovernance,
                releaseOptions: options.RecipeReleases, contractOptions: options.PlcResultContracts,
                activationOptions: options.RecipeActivations, previewOptions: options.PreviewSessions,
                importOptions: options.CalibrationImports, manualOptions: options.ManualInspections,
                productionAdmissionOptions: options.ProductionAdmission, stationQualificationOptions: options.StationQualifications,
                recipeTransferOptions: options.RecipeTransfers, traceStoragePolicyOptions: options.TraceStoragePolicies,
                qualificationCycleOptions: options.QualificationCycles, plcCommunicationOptions: options.PlcCommunication,
                productionInspectionOptions: options.ProductionInspections, productionRecoveryOptions: options.ProductionRecovery,
                partIdentityOptions: options.PartIdentities, recipeSelectionOptions: options.RecipeSelections,
                productionArmOptions: options.ProductionArming, recipeLifecycleOptions: options.RecipeLifecycle,
                imageEvidenceOptions: options.ImageEvidence, imageFinalizationOptions: options.ImageFinalization);
            var tail = AuditChainDatabase.Tail(database, deadline);
            if (report.State != AuditIntegrityState.Verified || report.VerifiedFromSequence != 1 ||
                report.VerifiedThroughSequence != tail.Sequence)
                throw new InvalidOperationException("StoreMigrationFullAuditVerificationRequired");
            RequireForeignKeys(database, deadline);
            return tail;
        }

        internal void CaptureConstraintTables(sqlite3 database, StoreDeadline deadline)
        {
            foreach (var name in RebuiltTables)
            {
                // Transactional staging stays on the already-qualified database
                // volume instead of spilling evidence into an ambient TEMP path.
                SqliteNative.Execute(database,
                    $"CREATE TABLE {Quote(StageName(name))} AS SELECT rowid AS __source_rowid,* FROM {Quote(name)};", deadline);
            }
        }

        internal void RebuildConstraintTables(sqlite3 database, StoreDeadline deadline)
        {
            if (Version != _migrationTargetVersion || _migrationTargetVersion is not (33 or 34 or 35))
                throw new InvalidOperationException("StoreMigrationTargetSchemaRequired");
            using var canonical = SqliteNative.Open(":memory:", readOnly: false);
            _model.InitializeCanonicalSchema(canonical.Handle!, deadline);
            var definitions = ReadSchemaDefinitions(canonical.Handle!, deadline);
            // Drop the only foreign-key child before its parent. Foreign keys
            // remain ON throughout the transaction, including row restoration.
            foreach (var name in new[] { "command_facts", "command_attempts", "audit_entries" })
                SqliteNative.Execute(database, $"DROP TABLE {Quote(name)};", deadline);
            foreach (var name in RebuiltTables)
            {
                SqliteNative.Execute(database, definitions["table:" + name], deadline);
                var columns = AuditChainDatabase.Read(database, $"PRAGMA table_info({Quote(name)});", deadline,
                    statement => SqliteNative.ColumnText(statement, 1) ??
                        throw new InvalidOperationException("StoreMigrationColumnNameMissing"));
                if (columns.Count is < 1 or > 256) throw new InvalidOperationException("StoreMigrationColumnCountInvalid");
                var quotedColumns = string.Join(",", columns.Select(Quote));
                SqliteNative.Execute(database,
                    $"INSERT INTO {Quote(name)}(rowid,{quotedColumns}) SELECT __source_rowid,{quotedColumns} FROM {Quote(StageName(name))} ORDER BY __source_rowid;",
                    deadline);
            }
            foreach (var name in RebuiltTables)
            {
                var objects = AuditChainDatabase.Read(canonical.Handle!,
                    "SELECT sql FROM sqlite_master WHERE tbl_name=? AND type IN ('index','trigger') AND sql IS NOT NULL ORDER BY type,name;",
                    deadline, statement => SqliteNative.ColumnText(statement, 0)!, name);
                foreach (var sql in objects) SqliteNative.Execute(database, sql, deadline);
                SqliteNative.Execute(database, $"DROP TABLE {Quote(StageName(name))};", deadline);
            }
            RequireForeignKeys(database, deadline);
        }

        /// <summary>
        /// Adds the one feature of the operation's target generation inside the caller's
        /// transaction: the lifecycle ledger for schema 33, the production image evidence
        /// store for schema 34. Both write their single immutable configuration row and
        /// their signed activation entry, and both raise PRAGMA user_version to their own
        /// generation, so the target proof always observes the exact migration target.
        /// </summary>
        internal void AddTargetFeature(sqlite3 database, StoreDeadline deadline)
        {
            using var key = WindowsMachineAuditKey.Open(_model._policy!, allowCreation: false, out _);
            if (Version != _migrationTargetVersion)
                throw new InvalidOperationException("StoreMigrationTargetSchemaRequired");
            if (_migrationTargetVersion == RecipeLifecycleStoreOptions.SchemaVersion &&
                Options.RecipeLifecycle is not null)
            {
                SqliteNative.Execute(database, "PRAGMA user_version=33;", deadline);
                InitializeRecipeLifecycleSchema(database, Options.RecipeLifecycle, deadline, _model._policy!, key);
                return;
            }
            if (_migrationTargetVersion == ProductionImageEvidenceStoreOptions.SchemaVersion &&
                Options.ImageEvidence is not null)
            {
                SqliteNative.Execute(database, "PRAGMA user_version=34;", deadline);
                InitializeImageEvidenceSchema(database, Options.ImageEvidence, deadline, _model._policy!, key);
                return;
            }
            if (_migrationTargetVersion == ProductionImageFinalizationStoreOptions.SchemaVersion &&
                Options.ImageFinalization is not null && Options.ImageEvidence is not null)
            {
                SqliteNative.Execute(database, "PRAGMA user_version=35;", deadline);
                InitializeImageFinalizationSchema(database, Options.ImageFinalization, deadline,
                    _model._policy!, key);
                return;
            }
            throw new InvalidOperationException("StoreMigrationTargetSchemaRequired");
        }

        internal static void RequireForeignKeys(sqlite3 database, StoreDeadline deadline)
        {
            if (AuditChainDatabase.Scalar(database, "PRAGMA foreign_keys;", deadline) != 1 ||
                SqliteNative.WithStatement(database, "PRAGMA foreign_key_check;", deadline,
                    statement => SqliteNative.Step(database, statement, deadline)) != raw.SQLITE_DONE)
                throw new InvalidOperationException("StoreMigrationForeignKeyVerificationFailed");
        }

        public void Dispose() => _model.DisposeAsync().GetAwaiter().GetResult();
        private string StageName(string name) => _migrationTargetVersion switch
        {
            ProductionImageFinalizationStoreOptions.SchemaVersion =>
                "__sharpinspect_migration34_35_" + name,
            ProductionImageEvidenceStoreOptions.SchemaVersion => "__sharpinspect_migration33_34_" + name,
            _ => "__sharpinspect_migration32_33_" + name
        };
        private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    internal static ProductionStoreOptions MigrationSourceOptions(ProductionStoreOptions target) => new()
    {
        DatabasePath = target.DatabasePath, CommitTimeout = target.CommitTimeout, QueryTimeout = target.QueryTimeout,
        QueueCapacity = target.QueueCapacity, AuditIntegrityPolicy = target.AuditIntegrityPolicy,
        LocalIdentity = target.LocalIdentity, AlarmPolicy = target.AlarmPolicy, ExternalAuditAnchor = target.ExternalAuditAnchor,
        AlgorithmResultArchive = target.AlgorithmResultArchive, RecipeDrafts = target.RecipeDrafts,
        CameraSetup = target.CameraSetup, CameraRecovery = target.CameraRecovery, CameraNetwork = target.CameraNetwork,
        ImagingSetup = target.ImagingSetup, CalibrationSessions = target.CalibrationSessions,
        CalibrationGovernance = target.CalibrationGovernance, RecipeReleases = target.RecipeReleases,
        PlcResultContracts = target.PlcResultContracts, RecipeActivations = target.RecipeActivations,
        PreviewSessions = target.PreviewSessions, CalibrationImports = target.CalibrationImports,
        ManualInspections = target.ManualInspections, ProductionAdmission = target.ProductionAdmission,
        StationQualifications = target.StationQualifications, RecipeTransfers = target.RecipeTransfers,
        TraceStoragePolicies = target.TraceStoragePolicies, QualificationCycles = target.QualificationCycles,
        PlcCommunication = target.PlcCommunication, ProductionInspections = target.ProductionInspections,
        PartIdentities = target.PartIdentities, ProductionRecovery = target.ProductionRecovery,
        RecipeSelections = target.RecipeSelections, ProductionArming = target.ProductionArming, RecipeLifecycle = null
    };

    /// <summary>
    /// The schema-33 source profile of the governed schema-33 to schema-34 migration:
    /// the exact target configuration with only the image evidence option removed. The
    /// recipe lifecycle ledger is retained, because a schema-33 source already owns it
    /// and the source proof must re-verify every store the source generation carries.
    /// </summary>
    internal static ProductionStoreOptions MigrationImageEvidenceSourceOptions(ProductionStoreOptions target) => new()
    {
        DatabasePath = target.DatabasePath, CommitTimeout = target.CommitTimeout, QueryTimeout = target.QueryTimeout,
        QueueCapacity = target.QueueCapacity, AuditIntegrityPolicy = target.AuditIntegrityPolicy,
        LocalIdentity = target.LocalIdentity, AlarmPolicy = target.AlarmPolicy, ExternalAuditAnchor = target.ExternalAuditAnchor,
        AlgorithmResultArchive = target.AlgorithmResultArchive, RecipeDrafts = target.RecipeDrafts,
        CameraSetup = target.CameraSetup, CameraRecovery = target.CameraRecovery, CameraNetwork = target.CameraNetwork,
        ImagingSetup = target.ImagingSetup, CalibrationSessions = target.CalibrationSessions,
        CalibrationGovernance = target.CalibrationGovernance, RecipeReleases = target.RecipeReleases,
        PlcResultContracts = target.PlcResultContracts, RecipeActivations = target.RecipeActivations,
        PreviewSessions = target.PreviewSessions, CalibrationImports = target.CalibrationImports,
        ManualInspections = target.ManualInspections, ProductionAdmission = target.ProductionAdmission,
        StationQualifications = target.StationQualifications, RecipeTransfers = target.RecipeTransfers,
        TraceStoragePolicies = target.TraceStoragePolicies, QualificationCycles = target.QualificationCycles,
        PlcCommunication = target.PlcCommunication, ProductionInspections = target.ProductionInspections,
        PartIdentities = target.PartIdentities, ProductionRecovery = target.ProductionRecovery,
        RecipeSelections = target.RecipeSelections, ProductionArming = target.ProductionArming,
        RecipeLifecycle = target.RecipeLifecycle, ImageEvidence = null
    };

    /// <summary>
    /// The schema-34 source profile of the governed schema-34 to schema-35 migration: the exact
    /// target configuration with only the image finalization option removed. The image evidence
    /// store is retained, because a schema-34 source already owns it and the source proof must
    /// re-verify every store the source generation carries.
    /// </summary>
    internal static ProductionStoreOptions MigrationImageFinalizationSourceOptions(
        ProductionStoreOptions target) => new()
    {
        DatabasePath = target.DatabasePath, CommitTimeout = target.CommitTimeout,
        QueryTimeout = target.QueryTimeout, QueueCapacity = target.QueueCapacity,
        AuditIntegrityPolicy = target.AuditIntegrityPolicy, LocalIdentity = target.LocalIdentity,
        AlarmPolicy = target.AlarmPolicy, ExternalAuditAnchor = target.ExternalAuditAnchor,
        AlgorithmResultArchive = target.AlgorithmResultArchive, RecipeDrafts = target.RecipeDrafts,
        CameraSetup = target.CameraSetup, CameraRecovery = target.CameraRecovery,
        CameraNetwork = target.CameraNetwork, ImagingSetup = target.ImagingSetup,
        CalibrationSessions = target.CalibrationSessions,
        CalibrationGovernance = target.CalibrationGovernance, RecipeReleases = target.RecipeReleases,
        PlcResultContracts = target.PlcResultContracts, RecipeActivations = target.RecipeActivations,
        PreviewSessions = target.PreviewSessions, CalibrationImports = target.CalibrationImports,
        ManualInspections = target.ManualInspections,
        ProductionAdmission = target.ProductionAdmission,
        StationQualifications = target.StationQualifications,
        RecipeTransfers = target.RecipeTransfers, TraceStoragePolicies = target.TraceStoragePolicies,
        QualificationCycles = target.QualificationCycles, PlcCommunication = target.PlcCommunication,
        ProductionInspections = target.ProductionInspections, PartIdentities = target.PartIdentities,
        ProductionRecovery = target.ProductionRecovery, RecipeSelections = target.RecipeSelections,
        ProductionArming = target.ProductionArming, RecipeLifecycle = target.RecipeLifecycle,
        ImageEvidence = target.ImageEvidence
    };
}
