using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Read-only, bounded access to the schema-22 production-admission ledger.
/// Every projection is obtained from one SQLite snapshot after the central
/// audit chain and all configured dependency ledgers have been verified.
/// This type has no recovery or initialization side effects.
/// </summary>
public sealed class SqliteProductionAdmissionHistoryQuery : IProductionAdmissionHistoryQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(4, 4);
    private static int _outstanding;

    public SqliteProductionAdmissionHistoryQuery(ProductionStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async ValueTask<ProductionAdmissionHistoryReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = await RunQueryAsync(new ProductionAdmissionHistoryFilter(PageSize: 1),
                QueryMode.Current, null, cancellationToken).ConfigureAwait(false);
            if (!query.Page.Available)
                return new(false, query.Page.ReasonCode);
            await VerifyExternalAnchorAsync(query.Checkpoint, query.Deadline,
                cancellationToken).ConfigureAwait(false);
            return query.Read ?? new(false, "ProductionAdmissionHistoryUnavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, FaultReason(exception,
                "ProductionAdmissionHistoryUnavailable"));
        }
    }

    public async ValueTask<ProductionAdmissionHistoryReadResult> ReadAsync(Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        if (correlationId == Guid.Empty)
            return new(false, "ProductionAdmissionCorrelationIdRequired");
        try
        {
            var query = await RunQueryAsync(new ProductionAdmissionHistoryFilter(
                CorrelationId: correlationId, PageSize: 1), QueryMode.Correlation, correlationId,
                cancellationToken).ConfigureAwait(false);
            if (!query.Page.Available)
                return new(false, query.Page.ReasonCode);
            await VerifyExternalAnchorAsync(query.Checkpoint, query.Deadline,
                cancellationToken).ConfigureAwait(false);
            return query.Read ?? new(false, "ProductionAdmissionHistoryUnavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, FaultReason(exception,
                "ProductionAdmissionHistoryUnavailable"));
        }
    }

    public async ValueTask<ProductionAdmissionHistoryPage> QueryAsync(
        ProductionAdmissionHistoryFilter filter,
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
                "ProductionAdmissionHistoryUnavailable"));
        }
    }

    private async ValueTask<QueryResult> RunQueryAsync(ProductionAdmissionHistoryFilter filter,
        QueryMode mode, Guid? correlationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var admissionOptions = _options.ProductionAdmission;
        if (admissionOptions is null)
            return EmptyResult("ProductionAdmissionUnavailable");
        if (_options.AuditIntegrityPolicy is null || _options.LocalIdentity is null)
            return EmptyResult("ProductionAdmissionRequiresIdentityAndAudit");
        if (Interlocked.Increment(ref _outstanding) > 64)
        {
            Interlocked.Decrement(ref _outstanding);
            return EmptyResult("ProductionAdmissionQueryCapacityExceeded");
        }

        var entered = false;
        try
        {
            var deadline = new StoreDeadline(_options.QueryTimeout);
            var remaining = deadline.Remaining;
            if (remaining <= TimeSpan.Zero)
                return EmptyResult("ProductionAdmissionQueryDeadlineExceeded");
            entered = await Slots.WaitAsync(remaining, cancellationToken)
                .ConfigureAwait(false);
            if (!entered)
                return new(Empty(false, "ProductionAdmissionQueryDeadlineExceeded"), null,
                    null, deadline);
            var result = await Task.Run(() => QueryCore(filter, mode, correlationId,
                admissionOptions, deadline, cancellationToken), CancellationToken.None)
                .ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return EmptyResult(FaultReason(exception,
                "ProductionAdmissionHistoryUnavailable"));
        }
        finally
        {
            if (entered) Slots.Release();
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private QueryResult QueryCore(ProductionAdmissionHistoryFilter filter, QueryMode mode,
        Guid? correlationId, ProductionAdmissionStoreOptions admissionOptions,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        admissionOptions.Validate();
        if (!StoragePathValidator.TryValidate(_options, out var path, out var pathReason))
            return new(Empty(false, pathReason), null, null, deadline);
        using var key = WindowsMachineAuditKey.Open(_options.AuditIntegrityPolicy!, false, out _);
        using var connection = SqliteNative.Open(path, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, _options);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; PRAGMA foreign_keys=ON; BEGIN;",
            deadline, cancellationToken);
        var committed = false;
        try
        {
            var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
            TraceStoragePolicyReadGuard.RequireConfiguration(schema, _options);
            AuditChainDatabase.Require(schema is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion or ProductionImageFinalizationStoreOptions.SchemaVersion,
                schema < PlcCommunicationStoreOptions.SchemaVersion
                    ? "ProductionAdmissionGovernedMigrationRequired" : "StoreSchemaTooNew");
            SqliteNative.ConfigureSqliteLimit(database, _options, schema);
            StationQualificationReadGuard.RequireConfiguration(schema, _options);
            VerifyDependencies(database, admissionOptions, key, deadline);

            var rows = SqliteCommandStore.ReadProductionAdmissionRows(database,
                admissionOptions, deadline).Select(row => row.Event).ToArray();
            var tail = rows.LastOrDefault()?.Position ?? 0;
            if (filter.ThroughPosition is { } requested && requested > tail)
                throw new InvalidOperationException("ProductionAdmissionCursorInvalid");
            var through = filter.ThroughPosition ?? tail;
            if (filter.AfterPosition > through)
                throw new InvalidOperationException("ProductionAdmissionCursorInvalid");
            var selected = rows.Where(row => row.Position <= through &&
                (!filter.CorrelationId.HasValue || row.CorrelationId == filter.CorrelationId.Value) &&
                (!filter.AttemptId.HasValue || row.AttemptId == filter.AttemptId.Value)).ToArray();
            var paged = selected.Where(row => row.Position > filter.AfterPosition)
                .Take(filter.PageSize + 1).ToArray();
            var events = paged.Take(filter.PageSize).ToArray();
            var pending = selected.GroupBy(row => row.AttemptId)
                .Select(group => group.Last())
                .LastOrDefault(row => row.Kind == ProductionAdmissionEventKind.Admitted);
            var page = new ProductionAdmissionHistoryPage(true, "ProductionAdmissionHistoryRead",
                Array.AsReadOnly(events), through,
                paged.Length > filter.PageSize ? events[^1].Position : null,
                pending, pending is not null);
            ProductionAdmissionHistoryReadResult? read = null;
            if (mode != QueryMode.Page)
            {
                var latest = selected.LastOrDefault();
                read = new(true, latest is null ? "ProductionAdmissionHistoryEmpty" :
                    "ProductionAdmissionHistoryRead", latest, pending is not null);
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
        ProductionAdmissionStoreOptions admissionOptions, IAuditSigningKey key,
        StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(admissionOptions);
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
            manualOptions: _options.ManualInspections,
            productionAdmissionOptions: admissionOptions,
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
        if (_options.AlarmPolicy is not null)
            AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
        if (_options.AlgorithmResultArchive is not null)
            AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
        if (_options.RecipeDrafts is not null)
            AuditChainDatabase.RequireFullRecipeDraftVerification(database, verification, deadline,
                _options.RecipeDrafts);
        if (_options.CameraSetup is not null)
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
                _options.PlcResultContracts, _options.CalibrationGovernance, recipeLifecycleOptions: _options.RecipeLifecycle);
        if (_options.PreviewSessions is not null)
            AuditChainDatabase.RequireFullPreviewSessionVerification(database, verification,
                deadline, _options.PreviewSessions);
        if (_options.CalibrationImports is not null)
            AuditChainDatabase.RequireFullCalibrationImportVerification(database, verification,
                deadline, _options.CalibrationImports);
        if (_options.ManualInspections is not null)
            AuditChainDatabase.RequireFullManualInspectionVerification(database, verification,
                deadline, _options.ManualInspections);
        AuditChainDatabase.RequireFullProductionAdmissionVerification(database, verification,
            deadline, admissionOptions);
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
            throw new TimeoutException("ProductionAdmissionQueryDeadlineExceeded");
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

    private static void ValidateFilter(ProductionAdmissionHistoryFilter filter)
    {
        if ((filter.CorrelationId is Guid correlationId && correlationId == Guid.Empty) ||
            (filter.AttemptId is Guid attemptId && attemptId == Guid.Empty) ||
            filter.AfterPosition < 0 || filter.ThroughPosition is < 0 ||
            filter.ThroughPosition < filter.AfterPosition || filter.PageSize is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(filter));
    }

    private static string FaultReason(Exception exception, string unavailable)
    {
        if (exception is InvalidOperationException { Message: var message } &&
            (message.StartsWith("ProductionAdmission", StringComparison.Ordinal) ||
             message is "StoreSchemaTooNew"))
            return message;
        if (exception is TimeoutException { Message: var timeout })
            return timeout.StartsWith("ProductionAdmission", StringComparison.Ordinal)
                ? timeout : "ProductionAdmissionQueryDeadlineExceeded";
        if (exception is OperationCanceledException)
            return "ProductionAdmissionQueryDeadlineExceeded";
        return SqliteAuditIntegrityQuery.FaultReason(exception, unavailable);
    }

    private static ProductionAdmissionHistoryPage Empty(bool available, string reason) => new(
        available, reason, Array.AsReadOnly(Array.Empty<ProductionAdmissionHistoryEvent>()),
        0, null, null, false);

    private static QueryResult EmptyResult(string reason) =>
        new(Empty(false, reason), null, null, null);

    private enum QueryMode { Current, Correlation, Page }

    private sealed record QueryResult(ProductionAdmissionHistoryPage Page,
        ProductionAdmissionHistoryReadResult? Read, AuditCheckpoint? Checkpoint,
        StoreDeadline? Deadline);
}
