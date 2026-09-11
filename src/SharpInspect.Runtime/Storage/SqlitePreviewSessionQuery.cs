using System.Collections.ObjectModel;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Read-only, bounded access to the schema-19 Preview session ledger. Every
/// call verifies the central chain and the dependency ledgers in one SQLite
/// snapshot before exposing events.
/// </summary>
public sealed class SqlitePreviewSessionQuery : IPreviewSessionHistoryQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(4, 4);
    private static int _outstanding;

    public SqlitePreviewSessionQuery(ProductionStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async ValueTask<PreviewSessionHistoryReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = await RunQueryAsync(new PreviewSessionHistoryFilter(PageSize: 1),
                QueryMode.Current, null, cancellationToken).ConfigureAwait(false);
            if (!query.Page.Available)
                return new(false, query.Page.ReasonCode);
            await VerifyExternalAnchorAsync(query.Checkpoint, query.Deadline, cancellationToken)
                .ConfigureAwait(false);
            return query.Read ?? new(false, "PreviewSessionHistoryUnavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, SqliteAuditIntegrityQuery.FaultReason(exception,
                "PreviewSessionHistoryUnavailable"));
        }
    }

    public async ValueTask<PreviewSessionHistoryReadResult> ReadAsync(Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            return new(false, "PreviewSessionIdRequired");
        try
        {
            var query = await RunQueryAsync(new PreviewSessionHistoryFilter(sessionId, PageSize: 1),
                QueryMode.Session, sessionId, cancellationToken).ConfigureAwait(false);
            if (!query.Page.Available)
                return new(false, query.Page.ReasonCode);
            await VerifyExternalAnchorAsync(query.Checkpoint, query.Deadline, cancellationToken)
                .ConfigureAwait(false);
            return query.Read ?? new(false, "PreviewSessionHistoryUnavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, SqliteAuditIntegrityQuery.FaultReason(exception,
                "PreviewSessionHistoryUnavailable"));
        }
    }

    public async ValueTask<PreviewSessionHistoryPage> QueryAsync(
        PreviewSessionHistoryFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateFilter(filter);
        try
        {
            var query = await RunQueryAsync(filter, QueryMode.Page, null,
                cancellationToken).ConfigureAwait(false);
            if (!query.Page.Available)
                return query.Page;
            await VerifyExternalAnchorAsync(query.Checkpoint, query.Deadline, cancellationToken)
                .ConfigureAwait(false);
            return query.Page;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Empty(false, SqliteAuditIntegrityQuery.FaultReason(exception,
                "PreviewSessionHistoryUnavailable"));
        }
    }

    private async ValueTask<QueryResult> RunQueryAsync(PreviewSessionHistoryFilter filter,
        QueryMode mode, Guid? sessionId, CancellationToken cancellationToken)
    {
        var previewOptions = _options.PreviewSessions;
        if (previewOptions is null)
            return EmptyResult("PreviewSessionUnavailable");
        if (_options.AuditIntegrityPolicy is null || _options.LocalIdentity is null ||
            _options.RecipeDrafts is null || _options.CameraSetup is null ||
            _options.RecipeReleases is null || _options.PlcResultContracts is null ||
            _options.RecipeActivations is null)
            return EmptyResult("PreviewSessionsRequiresCameraDraftReleaseActivationIdentityAndAudit");
        if (Interlocked.Increment(ref _outstanding) > 64)
        {
            Interlocked.Decrement(ref _outstanding);
            return EmptyResult("PreviewSessionQueryCapacityExceeded");
        }

        var entered = false;
        try
        {
            var deadline = new StoreDeadline(_options.QueryTimeout);
            entered = await Slots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!entered)
                return new(Empty(false, "PreviewSessionQueryDeadlineExceeded"), null, null, deadline);
            var result = await Task.Run(() => QueryCore(filter, mode, sessionId, previewOptions,
                deadline, cancellationToken), CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return EmptyResult(SqliteAuditIntegrityQuery.FaultReason(exception,
                "PreviewSessionHistoryUnavailable"));
        }
        finally
        {
            if (entered) Slots.Release();
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private QueryResult QueryCore(PreviewSessionHistoryFilter filter, QueryMode mode,
        Guid? sessionId, PreviewSessionStoreOptions previewOptions, StoreDeadline deadline,
        CancellationToken cancellationToken)
    {
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        if (!StoragePathValidator.TryValidate(_options, out var path, out var pathReason))
            return new(Empty(false, pathReason), null, null, deadline);
        using var key = WindowsMachineAuditKey.Open(_options.AuditIntegrityPolicy!, false, out _);
        using var connection = SqliteNative.Open(path, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, _options);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        var committed = false;
        try
        {
            var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
            TraceStoragePolicyReadGuard.RequireConfiguration(schema, _options);
            AuditChainDatabase.Require(schema is PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or
                ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion,
                schema < PlcCommunicationStoreOptions.SchemaVersion
                    ? "PreviewSessionGovernedMigrationRequired" : "StoreSchemaTooNew");
            if (_options.ManualInspections is not null && schema < ManualInspectionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("ManualInspectionGovernedMigrationRequired");
            if (_options.ManualInspections is null && schema == ManualInspectionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("ManualInspectionConfigurationRequired");
            if (schema == ManualInspectionStoreOptions.SchemaVersion && _options.CameraSetup is null)
                throw new InvalidOperationException("CameraSetupConfigurationRequired");
            if (_options.CalibrationImports is not null && schema < CalibrationImportStoreOptions.SchemaVersion)
                throw new InvalidOperationException("CalibrationImportGovernedMigrationRequired");
            if (_options.CalibrationImports is null && schema == CalibrationImportStoreOptions.SchemaVersion)
                throw new InvalidOperationException("CalibrationImportConfigurationRequired");
            if (_options.ProductionAdmission is not null && schema < ProductionAdmissionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("ProductionAdmissionGovernedMigrationRequired");
            if (_options.ProductionAdmission is null && schema == ProductionAdmissionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("ProductionAdmissionConfigurationRequired");
            SqliteNative.ConfigureSqliteLimit(database, _options, schema);
            StationQualificationReadGuard.RequireConfiguration(schema, _options);
            VerifyDependencies(database, previewOptions, key, deadline);

            PreviewSessionHistoryPage page;
            PreviewSessionHistoryReadResult? read = null;
            if (mode == QueryMode.Current)
            {
                read = SqliteCommandStore.ReadCurrentPreviewSession(database, previewOptions, deadline);
                page = SqliteCommandStore.QueryPreviewSessionHistory(database, previewOptions,
                    filter, deadline);
            }
            else if (mode == QueryMode.Session)
            {
                read = SqliteCommandStore.ReadPreviewSession(database, previewOptions,
                    sessionId!.Value, deadline);
                page = SqliteCommandStore.QueryPreviewSessionHistory(database, previewOptions,
                    filter, deadline);
            }
            else
            {
                page = SqliteCommandStore.QueryPreviewSessionHistory(database, previewOptions,
                    filter, deadline);
            }

            var checkpoint = AuditChainDatabase.LatestCheckpoint(database, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            return new(page, read, checkpoint, deadline);
        }
        finally
        {
            if (!committed)
                try { raw.sqlite3_exec(database, "ROLLBACK;"); } catch { }
        }
    }

    private void VerifyDependencies(sqlite3 database, PreviewSessionStoreOptions previewOptions,
        IAuditSigningKey key, StoreDeadline deadline)
    {
        // The machine key is opened once per read snapshot and is also the
        // trusted key used for the complete verification below.
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(previewOptions);
        var policy = _options.AuditIntegrityPolicy ??
            throw new InvalidOperationException("AuditPolicyNotConfigured");
        var verification = AuditChainDatabase.Verify(database, policy,
            key.KeyId, key.PublicKeyBase64,
            new AuditVerificationRequest(0, policy.MaximumVerificationEntries), startup: false,
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
            previewOptions: previewOptions, importOptions: _options.CalibrationImports,
            manualOptions: _options.ManualInspections,
            productionAdmissionOptions: _options.ProductionAdmission,
                stationQualificationOptions: _options.StationQualifications,
            recipeTransferOptions: _options.RecipeTransfers,
            traceStoragePolicyOptions: _options.TraceStoragePolicies,
                qualificationCycleOptions: _options.QualificationCycles,
                plcCommunicationOptions: _options.PlcCommunication,
                productionInspectionOptions: _options.ProductionInspections);
            RecipeTransferReadGuard.RequireVerified(database, verification, deadline, _options);
        if (_options.PlcCommunication is not null)
            AuditChainDatabase.RequireFullPlcCommunicationVerification(database, verification, deadline,
                _options.PlcCommunication);
            TraceStoragePolicyReadGuard.RequireVerified(database, verification, deadline, _options);
            StationQualificationReadGuard.RequireVerified(database, verification, deadline, _options);
        if (_options.AlarmPolicy is not null)
            AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
        if (_options.AlgorithmResultArchive is not null)
            AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
        AuditChainDatabase.RequireFullRecipeDraftVerification(database, verification, deadline,
            _options.RecipeDrafts);
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
        AuditChainDatabase.RequireFullRecipeReleaseVerification(database, verification, deadline,
            _options.RecipeReleases, _options.RecipeDrafts, _options.CalibrationGovernance);
        AuditChainDatabase.RequireFullPlcResultContractVerification(database, verification, deadline,
            _options.PlcResultContracts);
        AuditChainDatabase.RequireFullRecipeActivationVerification(database, verification, deadline,
            _options.RecipeActivations, _options.RecipeReleases, _options.PlcResultContracts,
            _options.CalibrationGovernance);
        AuditChainDatabase.RequireFullPreviewSessionVerification(database, verification, deadline,
            previewOptions);
         if (_options.CalibrationImports is not null)
             AuditChainDatabase.RequireFullCalibrationImportVerification(database, verification, deadline, _options.CalibrationImports);
         AuditChainDatabase.RequireFullManualInspectionVerification(database, verification, deadline,
             _options.ManualInspections);
         if (_options.ProductionAdmission is not null)
             AuditChainDatabase.RequireFullProductionAdmissionVerification(database, verification, deadline,
                 _options.ProductionAdmission);
    }

    private async ValueTask VerifyExternalAnchorAsync(AuditCheckpoint? checkpoint,
        StoreDeadline? deadline, CancellationToken cancellationToken)
    {
        var policy = _options.AuditIntegrityPolicy;
        if (policy is null || !policy.RequireExternalAnchor) return;
        var anchor = _options.ExternalAuditAnchor ??
            throw new InvalidOperationException("AuditRequiredAnchorUnavailable");
        if (checkpoint is null)
            throw new InvalidOperationException("AuditCheckpointMissing");
        var effectiveDeadline = deadline ?? new StoreDeadline(_options.QueryTimeout);
        var remaining = effectiveDeadline.Remaining;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("PreviewSessionQueryDeadlineExceeded");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(remaining);
        var anchorTimeout = remaining < policy.AnchorTimeout ? remaining : policy.AnchorTimeout;
        var latest = await AuditAnchorClient.InvokeAsync(anchor,
            token => anchor.ReadLatestAsync(policy.StationId, token), anchorTimeout, lifetime.Token)
            .ConfigureAwait(false);
        AuditChainDatabase.Require(latest is not null &&
            AuditChainDatabase.ReceiptMatches(policy, checkpoint, latest),
            "AuditExternalAnchorMismatch");
    }

    private static void ValidateFilter(PreviewSessionHistoryFilter filter)
    {
        if (filter.SessionId == Guid.Empty || filter.AfterPosition < 0 ||
            filter.ThroughPosition is < 0 ||
            filter.ThroughPosition < filter.AfterPosition || filter.PageSize is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(filter));
    }

    private static PreviewSessionHistoryPage Empty(bool available, string reason) => new(
        available, reason,
        new ReadOnlyCollection<PreviewSessionEvent>(Array.Empty<PreviewSessionEvent>()), 0, null);

    private static QueryResult EmptyResult(string reason) => new(Empty(false, reason), null, null, null);

    private enum QueryMode { Current, Session, Page }

    private sealed record QueryResult(PreviewSessionHistoryPage Page,
        PreviewSessionHistoryReadResult? Read, AuditCheckpoint? Checkpoint,
        StoreDeadline? Deadline);
}
