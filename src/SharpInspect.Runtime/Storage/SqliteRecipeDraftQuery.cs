using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>Bounded read-only history capability for the schema 9 Recipe Draft ledger.</summary>
public sealed class SqliteRecipeDraftQuery : IRecipeDraftHistoryQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(4, 4);
    private static int _outstanding;

    public SqliteRecipeDraftQuery(ProductionStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async ValueTask<RecipeDraftReadResult> ReadAsync(Guid draftId, long? revision = null,
        CancellationToken cancellationToken = default)
    {
        if (draftId == Guid.Empty) return new(false, "RecipeDraftIdRequired", null);
        if (revision is < 1) return new(false, "RecipeDraftRevisionInvalid", null);
        try
        {
            var query = await RunQueryAsync(new RecipeDraftFilter(draftId, PageSize: 1), revision,
                latestOnly: revision is null, cancellationToken).ConfigureAwait(false);
            if (!query.Page.Available) return new(false, query.Page.ReasonCode, null);
            await VerifyExternalAnchorAsync(query.Checkpoint, query.Deadline, cancellationToken).ConfigureAwait(false);
            var result = query.Page.Revisions.SingleOrDefault();
            return result is null ? new(false, "RecipeDraftRevisionNotFound", null) :
                new(true, "RecipeDraftRevisionAvailable", result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(ex, "RecipeDraftHistoryUnavailable"), null); }
    }

    public async ValueTask<RecipeDraftPage> QueryAsync(RecipeDraftFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateFilter(filter);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var query = await RunQueryAsync(filter, requestedRevision: null, latestOnly: false, cancellationToken)
                .ConfigureAwait(false);
            if (!query.Page.Available) return query.Page;
            await VerifyExternalAnchorAsync(query.Checkpoint, query.Deadline, cancellationToken).ConfigureAwait(false);
            return query.Page;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return Empty(false, SqliteAuditIntegrityQuery.FaultReason(ex, "RecipeDraftHistoryUnavailable")); }
    }

    private async ValueTask<QueryResult> RunQueryAsync(RecipeDraftFilter filter, long? requestedRevision,
        bool latestOnly, CancellationToken cancellationToken)
    {
        if (_options.RecipeDrafts is null)
            return new(Empty(false, "RecipeDraftUnavailable"), null, null);
        if (_options.AuditIntegrityPolicy is null || _options.LocalIdentity is null)
            return new(Empty(false, "RecipeDraftRequiresIdentityAndAudit"), null, null);
        if (Interlocked.Increment(ref _outstanding) > 64)
        {
            Interlocked.Decrement(ref _outstanding);
            return new(Empty(false, "RecipeDraftQueryCapacityExceeded"), null, null);
        }
        var entered = false;
        try
        {
            var deadline = new StoreDeadline(_options.QueryTimeout);
            entered = await Slots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!entered) return new(Empty(false, "RecipeDraftQueryDeadlineExceeded"), null, deadline);
            var result = await Task.Run(() => QueryCore(filter, deadline, cancellationToken,
                requestedRevision, latestOnly), CancellationToken.None).ConfigureAwait(false);
            return new(result.Page, result.Checkpoint, deadline);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(Empty(false, SqliteAuditIntegrityQuery.FaultReason(ex, "RecipeDraftHistoryUnavailable")), null, null); }
        finally
        {
            if (entered) Slots.Release();
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private QueryResult QueryCore(RecipeDraftFilter filter, StoreDeadline deadline,
        CancellationToken cancellationToken, long? requestedRevision, bool latestOnly)
    {
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        if (!StoragePathValidator.TryValidate(_options, out var path, out var pathReason))
            return new QueryResult(Empty(false, pathReason), null);
        using var key = WindowsMachineAuditKey.Open(_options.AuditIntegrityPolicy!, false, out _);
        using var connection = SqliteNative.Open(path, readOnly: true);
        SqliteNative.ConfigureSqliteLimit(connection.Handle!, _options);
        var database = connection.Handle!;
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        var committed = false;
        try
        {
            var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
            TraceStoragePolicyReadGuard.RequireConfiguration(schema, _options);
            StationQualificationReadGuard.RequireConfiguration(schema, _options);
            if (_options.RecipeReleases is not null && schema < RecipeReleaseStoreOptions.SchemaVersion)
                throw new InvalidOperationException("RecipeReleaseGovernedMigrationRequired");
            if (_options.RecipeReleases is null && schema == RecipeReleaseStoreOptions.SchemaVersion)
                throw new InvalidOperationException("RecipeReleaseConfigurationRequired");
            if (_options.PlcResultContracts is not null && schema < PlcResultContractStoreOptions.SchemaVersion)
                throw new InvalidOperationException("PlcResultContractGovernedMigrationRequired");
            if (_options.PlcResultContracts is null && schema == PlcResultContractStoreOptions.SchemaVersion)
                throw new InvalidOperationException("PlcResultContractConfigurationRequired");
            if (_options.PreviewSessions is not null && schema < PreviewSessionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("PreviewSessionGovernedMigrationRequired");
            if (_options.PreviewSessions is null &&
                (schema == PreviewSessionStoreOptions.SchemaVersion || schema == CalibrationImportStoreOptions.SchemaVersion))
                throw new InvalidOperationException("PreviewSessionConfigurationRequired");
            if (_options.CalibrationImports is not null && schema < CalibrationImportStoreOptions.SchemaVersion)
                throw new InvalidOperationException("CalibrationImportGovernedMigrationRequired");
            if (_options.CalibrationImports is null && schema == CalibrationImportStoreOptions.SchemaVersion)
                throw new InvalidOperationException("CalibrationImportConfigurationRequired");
            if (_options.ManualInspections is not null && schema < ManualInspectionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("ManualInspectionGovernedMigrationRequired");
            if (_options.ManualInspections is null && schema == ManualInspectionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("ManualInspectionConfigurationRequired");
            if (_options.ProductionAdmission is not null && schema < ProductionAdmissionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("ProductionAdmissionGovernedMigrationRequired");
            if (_options.ProductionAdmission is null && schema == ProductionAdmissionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("ProductionAdmissionConfigurationRequired");
            if (schema == CalibrationImportStoreOptions.SchemaVersion &&
                (_options.PreviewSessions is null || _options.CalibrationGovernance is null ||
                    _options.CalibrationSessions is null || _options.ImagingSetup is null))
                throw new InvalidOperationException("CalibrationImportsRequiresPreviewGovernanceCalibrationAndImagingSetup");
            if (schema == RecipeReleaseStoreOptions.SchemaVersion && _options.RecipeDrafts is null)
                throw new InvalidOperationException("RecipeDraftConfigurationRequired");
            if (_options.CalibrationSessions is not null && schema < CalibrationSessionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("CalibrationGovernedMigrationRequired");
            if (_options.CalibrationSessions is null &&
                (schema is CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion))
                throw new InvalidOperationException("CalibrationConfigurationRequired");
            if (_options.CalibrationGovernance is not null && schema < CalibrationGovernanceStoreOptions.SchemaVersion)
                throw new InvalidOperationException("CalibrationGovernanceMigrationRequired");
            if (_options.CalibrationGovernance is null &&
                (schema == CalibrationGovernanceStoreOptions.SchemaVersion || schema == CalibrationImportStoreOptions.SchemaVersion))
                throw new InvalidOperationException("CalibrationGovernanceConfigurationRequired");
            if (schema is CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
                CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
                CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or
                RecipeActivationStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or
                ManualInspectionStoreOptions.SchemaVersion)
                AuditChainDatabase.Require(_options.CameraSetup is not null, "CameraSetupConfigurationRequired");
            else if (schema < CameraSetupStoreOptions.SchemaVersion)
                AuditChainDatabase.Require(_options.CameraSetup is null, "CameraSetupGovernedMigrationRequired");
            if (_options.CameraRecovery is not null && schema < CameraRecoveryStoreOptions.SchemaVersion)
                throw new InvalidOperationException("CameraRecoveryGovernedMigrationRequired");
            if (_options.CameraRecovery is null &&
                (schema is CameraRecoveryStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion))
                throw new InvalidOperationException("CameraRecoveryConfigurationRequired");
            if (_options.CameraNetwork is not null && schema < CameraNetworkStoreOptions.SchemaVersion)
                throw new InvalidOperationException("CameraNetworkGovernedMigrationRequired");
            if (_options.CameraNetwork is null && schema == CameraNetworkStoreOptions.SchemaVersion)
                throw new InvalidOperationException("CameraNetworkConfigurationRequired");
            if (_options.ImagingSetup is not null && schema < ImagingSetupStoreOptions.SchemaVersion)
                throw new InvalidOperationException("ImagingSetupGovernedMigrationRequired");
            if (_options.ImagingSetup is null &&
                (schema is ImagingSetupStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion))
                throw new InvalidOperationException("ImagingSetupConfigurationRequired");
            AuditChainDatabase.Require(schema is RecipeDraftStoreOptions.SchemaVersion or
                CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
                CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
                 CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or
                 RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or
                 RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or
                  CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or
                  ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion or ProductionImageFinalizationStoreOptions.SchemaVersion,
                schema < RecipeDraftStoreOptions.SchemaVersion
                    ? "RecipeDraftGovernedMigrationRequired"
                    : schema > ProductionRecoveryStoreOptions.SchemaVersion
                        ? "StoreSchemaTooNew" : "RecipeDraftConfigurationRequired");
            var policy = _options.AuditIntegrityPolicy!;
            var verification = AuditChainDatabase.Verify(database, policy, key.KeyId, key.PublicKeyBase64,
                new AuditVerificationRequest(0, policy.MaximumVerificationEntries), false, deadline,
                validateAnchorReceipt: false, archiveOptions: _options.AlgorithmResultArchive,
                recipeDraftOptions: _options.RecipeDrafts, cameraSetupOptions: _options.CameraSetup,
                cameraRecoveryOptions: _options.CameraRecovery, cameraNetworkOptions: _options.CameraNetwork,
                imagingSetupOptions: _options.ImagingSetup,
                calibrationSessionOptions: _options.CalibrationSessions,
                governanceOptions: schema is CalibrationGovernanceStoreOptions.SchemaVersion or
                    RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or
                    RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or
                    CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or
                    ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion or ProductionImageFinalizationStoreOptions.SchemaVersion
                    ? _options.CalibrationGovernance : null,
                releaseOptions: schema is RecipeReleaseStoreOptions.SchemaVersion or
                    PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or
                    PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or
                    ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion or ProductionImageFinalizationStoreOptions.SchemaVersion
                    ? _options.RecipeReleases : null,
                contractOptions: _options.PlcResultContracts,
                activationOptions: _options.RecipeActivations,
                previewOptions: schema is PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or
                    ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion or ProductionImageFinalizationStoreOptions.SchemaVersion
                    ? _options.PreviewSessions : null,
                importOptions: schema is CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or
                    ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion or ProductionImageFinalizationStoreOptions.SchemaVersion
                    ? _options.CalibrationImports : null,
                manualOptions: schema == ManualInspectionStoreOptions.SchemaVersion ||
                    ((schema is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion or ProductionImageFinalizationStoreOptions.SchemaVersion) && _options.ManualInspections is not null)
                    ? _options.ManualInspections : null,
                productionAdmissionOptions: schema is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion or ProductionImageFinalizationStoreOptions.SchemaVersion
                    ? _options.ProductionAdmission : null,
                stationQualificationOptions: _options.StationQualifications,
                recipeTransferOptions: _options.RecipeTransfers,
                traceStoragePolicyOptions: _options.TraceStoragePolicies,
                qualificationCycleOptions: _options.QualificationCycles,
                plcCommunicationOptions: _options.PlcCommunication,
                productionInspectionOptions: _options.ProductionInspections,
                productionRecoveryOptions: _options.ProductionRecovery,
                partIdentityOptions: _options.PartIdentities,
                productionArmOptions: _options.ProductionArming, recipeSelectionOptions: _options.RecipeSelections, recipeLifecycleOptions: _options.RecipeLifecycle, imageEvidenceOptions: _options.ImageEvidence, imageFinalizationOptions: _options.ImageFinalization);
            RecipeTransferReadGuard.RequireVerified(database, verification, deadline, _options);
        if (_options.PlcCommunication is not null)
            AuditChainDatabase.RequireFullPlcCommunicationVerification(database, verification, deadline,
                _options.PlcCommunication);
            TraceStoragePolicyReadGuard.RequireVerified(database, verification, deadline, _options);
            StationQualificationReadGuard.RequireVerified(database, verification, deadline, _options);
            if (_options.AlarmPolicy is not null) AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
            if (_options.AlgorithmResultArchive is not null) AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
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
            if (_options.CalibrationGovernance is not null &&
                (schema is CalibrationGovernanceStoreOptions.SchemaVersion or RecipeReleaseStoreOptions.SchemaVersion or
                    PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or
                    PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or
                    ManualInspectionStoreOptions.SchemaVersion or
                    ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion or ProductionImageFinalizationStoreOptions.SchemaVersion))
                AuditChainDatabase.RequireFullCalibrationGovernanceVerification(database, verification, deadline,
                    _options.CalibrationGovernance);
                if ((schema is RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or
                     RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or
                     CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or
                     ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion or ProductionImageFinalizationStoreOptions.SchemaVersion) &&
                _options.RecipeReleases is not null)
                AuditChainDatabase.RequireFullRecipeReleaseVerification(database, verification, deadline,
                    _options.RecipeReleases, _options.RecipeDrafts, _options.CalibrationGovernance);
            if (_options.PlcResultContracts is not null)
                AuditChainDatabase.RequireFullPlcResultContractVerification(database, verification, deadline,
                    _options.PlcResultContracts);
             if (_options.RecipeActivations is not null)
                 AuditChainDatabase.RequireFullRecipeActivationVerification(database, verification, deadline,
                     _options.RecipeActivations, _options.RecipeReleases, _options.PlcResultContracts,
                     _options.CalibrationGovernance, recipeLifecycleOptions: _options.RecipeLifecycle);
             if (_options.PreviewSessions is not null)
                 AuditChainDatabase.RequireFullPreviewSessionVerification(database, verification, deadline,
                     _options.PreviewSessions);
             if (_options.CalibrationImports is not null)
                 AuditChainDatabase.RequireFullCalibrationImportVerification(database, verification, deadline,
                     _options.CalibrationImports);
             if (_options.ManualInspections is not null)
                 AuditChainDatabase.RequireFullManualInspectionVerification(database, verification, deadline,
                     _options.ManualInspections);
             if (_options.ProductionAdmission is not null)
                 AuditChainDatabase.RequireFullProductionAdmissionVerification(database, verification, deadline,
                     _options.ProductionAdmission);

            var latest = AuditChainDatabase.Scalar(database,
                "SELECT COALESCE(MAX(Position),0) FROM recipe_draft_revisions;", deadline);
            if (filter.ThroughPosition is { } requested && requested > latest)
                throw new InvalidOperationException("RecipeDraftCursorInvalid");
            var through = filter.ThroughPosition ?? latest;
            if (through < filter.AfterPosition) throw new InvalidOperationException("RecipeDraftCursorInvalid");
            var rows = latestOnly || requestedRevision is not null
                ? ReadSingle(database, filter.DraftId!.Value, requestedRevision, _options.RecipeDrafts!, deadline,
                    cancellationToken)
                : ReadPage(database, filter, through, _options.RecipeDrafts!, deadline, cancellationToken);
            var revisions = new List<RecipeDraftRevision>(Math.Min(filter.PageSize, rows.Count));
            var hasNext = rows.Count > 0 && rows[^1].Continuation;
            if (hasNext) rows.RemoveAt(rows.Count - 1);
            foreach (var row in rows)
                revisions.Add(SqliteCommandStore.ReadRecipeDraftRevisionAt(database, row.Position, deadline,
                    _options.RecipeDrafts!));
            long? next = hasNext && revisions.Count > 0 ? revisions[^1].Position : null;
            // Capture the checkpoint while the read transaction is still the
            // snapshot that was verified and projected. Reading it after COMMIT
            // could pair this page with a writer's newer head.
            var checkpoint = AuditChainDatabase.LatestCheckpoint(database, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            var page = new RecipeDraftPage(true, "RecipeDraftHistoryVerified",
                new ReadOnlyCollection<RecipeDraftRevision>(revisions), through, next);
            return new QueryResult(page, checkpoint);
        }
        finally
        {
            if (!committed)
                try { raw.sqlite3_exec(database, "ROLLBACK;"); } catch { }
        }
    }

    private static List<QueryRow> ReadSingle(sqlite3 database, Guid draftId, long? revision,
        RecipeDraftStoreOptions options, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        var sql = revision is { }
            ? "SELECT Position,PayloadJson,length(CAST(PayloadJson AS BLOB)) FROM recipe_draft_revisions WHERE DraftId=? AND Revision=? LIMIT 2;"
            : "SELECT Position,PayloadJson,length(CAST(PayloadJson AS BLOB)) FROM recipe_draft_revisions WHERE DraftId=? ORDER BY Revision DESC LIMIT 1;";
        return SqliteNative.WithStatement(database, sql, deadline, statement =>
        {
            SqliteNative.BindGuid(database, statement, 1, draftId);
            if (revision is { } requested) SqliteNative.BindInt64(database, statement, 2, requested);
            var rows = new List<QueryRow>();
            while (SqliteNative.Step(database, statement, deadline, cancellationToken) == raw.SQLITE_ROW)
            {
                var rowBytes = SqliteNative.ColumnInt64(statement, 2);
                if (rowBytes < 1 || rowBytes > options.MaximumRecordBytes || rowBytes > options.MaximumPageBytes)
                    throw new InvalidOperationException("RecipeDraftQueryRecordOversized");
                var payload = SqliteNative.ColumnText(statement, 1)!;
                if (Encoding.UTF8.GetByteCount(payload) != rowBytes)
                    throw new InvalidOperationException("RecipeDraftPayloadLengthMismatch");
                rows.Add(new QueryRow(SqliteNative.ColumnInt64(statement, 0), false));
            }
            return rows;
        }, cancellationToken);
    }

    private async ValueTask VerifyExternalAnchorAsync(AuditCheckpoint? checkpoint, StoreDeadline? deadline,
        CancellationToken cancellationToken)
    {
        var policy = _options.AuditIntegrityPolicy;
        if (policy is null || !policy.RequireExternalAnchor) return;
        var anchor = _options.ExternalAuditAnchor ?? throw new InvalidOperationException("AuditRequiredAnchorUnavailable");
        if (checkpoint is null) throw new InvalidOperationException("AuditCheckpointMissing");
        var effectiveDeadline = deadline ?? new StoreDeadline(_options.QueryTimeout);
        var remaining = effectiveDeadline.Remaining;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("RecipeDraftQueryDeadlineExceeded");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(remaining);
        var anchorTimeout = remaining < policy.AnchorTimeout ? remaining : policy.AnchorTimeout;
        var latest = await AuditAnchorClient.InvokeAsync(anchor,
            token => anchor.ReadLatestAsync(policy.StationId, token), anchorTimeout, lifetime.Token)
            .ConfigureAwait(false);
        AuditChainDatabase.Require(latest is not null &&
            AuditChainDatabase.ReceiptMatches(policy, checkpoint, latest), "AuditExternalAnchorMismatch");
    }

    private static List<QueryRow> ReadPage(sqlite3 database, RecipeDraftFilter filter, long through,
        RecipeDraftStoreOptions options, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        var draftClause = filter.DraftId is null ? string.Empty : " AND DraftId=?";
        var sql = "SELECT Position,PayloadJson,length(CAST(PayloadJson AS BLOB)) FROM recipe_draft_revisions " +
            "WHERE Position>? AND Position<=?" + draftClause + " ORDER BY Position LIMIT ?;";
        return SqliteNative.WithStatement(database, sql, deadline, statement =>
        {
            var index = 1;
            SqliteNative.BindInt64(database, statement, index++, filter.AfterPosition);
            SqliteNative.BindInt64(database, statement, index++, through);
            if (filter.DraftId is { } draftId) SqliteNative.BindGuid(database, statement, index++, draftId);
            SqliteNative.BindInt(database, statement, index, checked(filter.PageSize + 1));
            var rows = new List<QueryRow>();
            var bytes = 0L;
            while (SqliteNative.Step(database, statement, deadline, cancellationToken) == raw.SQLITE_ROW)
            {
                var rowBytes = SqliteNative.ColumnInt64(statement, 2);
                if (rowBytes < 1 || rowBytes > options.MaximumRecordBytes || rowBytes > options.MaximumPageBytes)
                    throw new InvalidOperationException("RecipeDraftQueryRecordOversized");
                if (rows.Count >= filter.PageSize || bytes > options.MaximumPageBytes - rowBytes)
                {
                    rows.Add(new QueryRow(SqliteNative.ColumnInt64(statement, 0), true));
                    break;
                }
                var payload = SqliteNative.ColumnText(statement, 1)!;
                if (Encoding.UTF8.GetByteCount(payload) != rowBytes)
                    throw new InvalidOperationException("RecipeDraftPayloadLengthMismatch");
                rows.Add(new QueryRow(SqliteNative.ColumnInt64(statement, 0), false));
                bytes += rowBytes;
            }
            return rows;
        }, cancellationToken);
    }

    private static void ValidateFilter(RecipeDraftFilter filter)
    {
        if (filter.DraftId == Guid.Empty || filter.AfterPosition < 0 || filter.ThroughPosition is < 0 ||
            filter.ThroughPosition < filter.AfterPosition || filter.PageSize is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(filter));
    }

    private static RecipeDraftPage Empty(bool available, string reason) => new(available, reason,
        new ReadOnlyCollection<RecipeDraftRevision>(Array.Empty<RecipeDraftRevision>()), 0, null);

    private sealed record QueryRow(long Position, bool Continuation);
    private sealed record QueryResult(RecipeDraftPage Page, AuditCheckpoint? Checkpoint,
        StoreDeadline? Deadline = null);
}
