using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Read-only, bounded access to the schema-21 Manual Inspection ledger.
/// Every projection is obtained from one SQLite snapshot after the central
/// audit chain and all configured dependency ledgers have been verified.
/// This type has no recovery or initialization side effects.
/// </summary>
public sealed class SqliteManualInspectionQuery : IManualInspectionHistoryQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(4, 4);
    private static int _outstanding;

    public SqliteManualInspectionQuery(ProductionStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async ValueTask<ManualInspectionHistoryReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = await RunQueryAsync(new ManualInspectionHistoryFilter(PageSize: 1),
                QueryMode.Current, null, cancellationToken).ConfigureAwait(false);
            if (!query.Page.Available)
                return new(false, query.Page.ReasonCode);
            await VerifyExternalAnchorAsync(query.Checkpoint, query.Deadline,
                cancellationToken).ConfigureAwait(false);
            return query.Read ?? new(false, "ManualInspectionHistoryUnavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, FaultReason(exception,
                "ManualInspectionHistoryUnavailable"));
        }
    }

    public async ValueTask<ManualInspectionHistoryReadResult> ReadAsync(Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            return new(false, "ManualInspectionSessionIdRequired");
        try
        {
            var query = await RunQueryAsync(new ManualInspectionHistoryFilter(
                SessionId: sessionId, PageSize: 1), QueryMode.Session, sessionId,
                cancellationToken).ConfigureAwait(false);
            if (!query.Page.Available)
                return new(false, query.Page.ReasonCode);
            await VerifyExternalAnchorAsync(query.Checkpoint, query.Deadline,
                cancellationToken).ConfigureAwait(false);
            return query.Read ?? new(false, "ManualInspectionHistoryUnavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, FaultReason(exception,
                "ManualInspectionHistoryUnavailable"));
        }
    }

    public async ValueTask<ManualInspectionHistoryPage> QueryAsync(
        ManualInspectionHistoryFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateFilter(filter);
        try
        {
            var query = await RunQueryAsync(filter, QueryMode.Page, null,
                cancellationToken).ConfigureAwait(false);
            if (!query.Page.Available)
                return query.Page;
            await VerifyExternalAnchorAsync(query.Checkpoint, query.Deadline,
                cancellationToken).ConfigureAwait(false);
            return query.Page;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Empty(false, FaultReason(exception,
                "ManualInspectionHistoryUnavailable"));
        }
    }

    private async ValueTask<QueryResult> RunQueryAsync(ManualInspectionHistoryFilter filter,
        QueryMode mode, Guid? sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manualOptions = _options.ManualInspections;
        if (manualOptions is null)
            return EmptyResult("ManualInspectionUnavailable");
        if (_options.AuditIntegrityPolicy is null || _options.LocalIdentity is null ||
            _options.RecipeDrafts is null || _options.CameraSetup is null)
            return EmptyResult("ManualInspectionsRequiresCameraDraftsIdentityAndAudit");
        if (Interlocked.Increment(ref _outstanding) > 64)
        {
            Interlocked.Decrement(ref _outstanding);
            return EmptyResult("ManualInspectionQueryCapacityExceeded");
        }

        var entered = false;
        try
        {
            var deadline = new StoreDeadline(_options.QueryTimeout);
            var remaining = deadline.Remaining;
            if (remaining <= TimeSpan.Zero)
                return EmptyResult("ManualInspectionQueryDeadlineExceeded");
            entered = await Slots.WaitAsync(remaining, cancellationToken)
                .ConfigureAwait(false);
            if (!entered)
                return new(Empty(false, "ManualInspectionQueryDeadlineExceeded"), null,
                    null, deadline);
            var result = await Task.Run(() => QueryCore(filter, mode, sessionId,
                manualOptions, deadline, cancellationToken), CancellationToken.None)
                .ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return EmptyResult(FaultReason(exception,
                "ManualInspectionHistoryUnavailable"));
        }
        finally
        {
            if (entered) Slots.Release();
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private QueryResult QueryCore(ManualInspectionHistoryFilter filter, QueryMode mode,
        Guid? sessionId, ManualInspectionStoreOptions manualOptions,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        manualOptions.Validate();
        if (!StoragePathValidator.TryValidate(_options, out var path, out var pathReason))
            return new(Empty(false, pathReason), null, null, deadline);
        using var key = WindowsMachineAuditKey.Open(_options.AuditIntegrityPolicy!, false, out _);
        using var connection = SqliteNative.Open(path, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, _options);
        // SqliteNative's shared limit is intentionally conservative for stores
        // that omit the Manual option. Raise it only after the option has been
        // explicitly supplied and the path has passed validation.
        ManualInspectionStoreOptions.ConfigureSqliteLimit(database);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; PRAGMA foreign_keys=ON; BEGIN;",
            deadline, cancellationToken);
        var committed = false;
        try
        {
            var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
            TraceStoragePolicyReadGuard.RequireConfiguration(schema, _options);
            AuditChainDatabase.Require(schema is ManualInspectionStoreOptions.SchemaVersion or
                ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion,
                schema < PlcCommunicationStoreOptions.SchemaVersion
                    ? "ManualInspectionGovernedMigrationRequired" : "StoreSchemaTooNew");
            if (_options.ProductionAdmission is not null && schema < ProductionAdmissionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("ProductionAdmissionGovernedMigrationRequired");
            if (_options.ProductionAdmission is null && schema == ProductionAdmissionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("ProductionAdmissionConfigurationRequired");
            SqliteNative.ConfigureSqliteLimit(database, _options, schema);
            StationQualificationReadGuard.RequireConfiguration(schema, _options);
            ManualInspectionStoreOptions.ConfigureSqliteLimit(database);
            VerifyDependencies(database, manualOptions, key, deadline);

            ManualInspectionHistoryPage page;
            ManualInspectionHistoryReadResult? read = null;
            if (mode == QueryMode.Current)
            {
                read = SqliteCommandStore.ReadCurrentManualInspectionSession(database,
                    manualOptions, deadline);
                page = SqliteCommandStore.QueryManualInspectionHistory(database,
                    manualOptions, filter, deadline);
            }
            else if (mode == QueryMode.Session)
            {
                read = SqliteCommandStore.ReadManualInspectionSession(database,
                    manualOptions, sessionId!.Value, deadline);
                page = SqliteCommandStore.QueryManualInspectionHistory(database,
                    manualOptions, filter, deadline);
            }
            else
            {
                page = SqliteCommandStore.QueryManualInspectionHistory(database,
                    manualOptions, filter, deadline);
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

    private void VerifyDependencies(sqlite3 database,
        ManualInspectionStoreOptions manualOptions, IAuditSigningKey key,
        StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(manualOptions);
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
            previewOptions: _options.PreviewSessions,
            importOptions: _options.CalibrationImports,
            manualOptions: manualOptions,
            productionAdmissionOptions: _options.ProductionAdmission,
                stationQualificationOptions: _options.StationQualifications,
                recipeTransferOptions: _options.RecipeTransfers,
                traceStoragePolicyOptions: _options.TraceStoragePolicies,
                qualificationCycleOptions: _options.QualificationCycles,
                plcCommunicationOptions: _options.PlcCommunication);
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
            AuditChainDatabase.RequireFullCameraRecoveryVerification(database, verification,
                deadline, _options.CameraRecovery);
        if (_options.CameraNetwork is not null)
            AuditChainDatabase.RequireFullCameraNetworkVerification(database, verification,
                deadline, _options.CameraNetwork);
        if (_options.ImagingSetup is not null)
            AuditChainDatabase.RequireFullImagingSetupVerification(database, verification,
                deadline, _options.ImagingSetup);
        if (_options.CalibrationGovernance is not null)
            AuditChainDatabase.RequireFullCalibrationGovernanceVerification(database, verification,
                deadline, _options.CalibrationGovernance);
        if (_options.RecipeReleases is not null)
            AuditChainDatabase.RequireFullRecipeReleaseVerification(database, verification,
                deadline, _options.RecipeReleases, _options.RecipeDrafts,
                _options.CalibrationGovernance);
        if (_options.PlcResultContracts is not null)
            AuditChainDatabase.RequireFullPlcResultContractVerification(database, verification,
                deadline, _options.PlcResultContracts);
        if (_options.RecipeActivations is not null)
            AuditChainDatabase.RequireFullRecipeActivationVerification(database, verification,
                deadline, _options.RecipeActivations, _options.RecipeReleases,
                _options.PlcResultContracts, _options.CalibrationGovernance);
        if (_options.PreviewSessions is not null)
            AuditChainDatabase.RequireFullPreviewSessionVerification(database, verification,
                deadline, _options.PreviewSessions);
        if (_options.CalibrationImports is not null)
            AuditChainDatabase.RequireFullCalibrationImportVerification(database, verification,
                deadline, _options.CalibrationImports);
        AuditChainDatabase.RequireFullManualInspectionVerification(database, verification,
            deadline, manualOptions);
        if (_options.ProductionAdmission is not null)
            AuditChainDatabase.RequireFullProductionAdmissionVerification(database, verification,
                deadline, _options.ProductionAdmission);
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
            throw new TimeoutException("ManualInspectionQueryDeadlineExceeded");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(remaining);
        var anchorTimeout = remaining < policy.AnchorTimeout ? remaining : policy.AnchorTimeout;
        var latest = await AuditAnchorClient.InvokeAsync(anchor,
            token => anchor.ReadLatestAsync(policy.StationId, token), anchorTimeout,
            lifetime.Token).ConfigureAwait(false);
        AuditChainDatabase.Require(latest is not null &&
            AuditChainDatabase.ReceiptMatches(policy, checkpoint, latest),
            "AuditExternalAnchorMismatch");
    }

    private static void ValidateFilter(ManualInspectionHistoryFilter filter)
    {
        if ((filter.SessionId is Guid sessionId && sessionId == Guid.Empty) ||
            (filter.RunId is Guid runId && runId == Guid.Empty) ||
            filter.AfterPosition < 0 || filter.ThroughPosition is < 0 ||
            filter.ThroughPosition < filter.AfterPosition || filter.PageSize is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(filter));
    }

    private static string FaultReason(Exception exception, string unavailable)
    {
        if (exception is InvalidOperationException { Message: var message } &&
            (message.StartsWith("ManualInspection", StringComparison.Ordinal) ||
             message is "StoreSchemaTooNew"))
            return message;
        if (exception is TimeoutException { Message: var timeout })
            return timeout.StartsWith("ManualInspection", StringComparison.Ordinal)
                ? timeout : "ManualInspectionQueryDeadlineExceeded";
        if (exception is OperationCanceledException)
            return "ManualInspectionQueryDeadlineExceeded";
        return SqliteAuditIntegrityQuery.FaultReason(exception, unavailable);
    }

    private static ManualInspectionHistoryPage Empty(bool available, string reason) => new(
        available, reason, Array.Empty<ManualInspectionSessionEvent>(),
        Array.Empty<ManualInspectionRunRecord>(), 0, null, null, false);

    private static QueryResult EmptyResult(string reason) =>
        new(Empty(false, reason), null, null, null);

    private enum QueryMode { Current, Session, Page }

    private sealed record QueryResult(ManualInspectionHistoryPage Page,
        ManualInspectionHistoryReadResult? Read, AuditCheckpoint? Checkpoint,
        StoreDeadline? Deadline);
}
