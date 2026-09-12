using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Read-only access to the schema-28 production inspection ledger.  Every page
/// is reconstructed from the immutable binary event envelope after one frozen
/// SQLite snapshot and a full central-audit verification.
/// </summary>
public sealed class SqliteProductionInspectionHistoryQuery : IProductionInspectionHistoryQuery
{
    private readonly ProductionStoreOptions _options;
    private readonly Func<CalibrationProfileReference, PublishedCalibrationProfileVersion?>? _calibrationResolver;
    private static readonly SemaphoreSlim Slots = new(4, 4);
    private static int _outstanding;

    public SqliteProductionInspectionHistoryQuery(ProductionStoreOptions options,
        Func<CalibrationProfileReference, PublishedCalibrationProfileVersion?>? calibrationResolver = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _calibrationResolver = calibrationResolver;
    }

    public async ValueTask<ProductionInspectionHistoryReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await QueryCoreAsync(new ProductionInspectionHistoryFilter(PageSize: 1),
                ReadMode.Current, cancellationToken).ConfigureAwait(false);
            if (!result.Available)
                return new(false, result.ReasonCode);
            var latest = result.Events.LastOrDefault();
            return new(true, latest is null ? "ProductionInspectionHistoryEmpty" :
                "ProductionInspectionHistoryAvailable", latest, result.RecoveryRequired);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception,
            "ProductionInspectionHistoryUnavailable")); }
    }

    public async ValueTask<ProductionInspectionHistoryReadResult> ReadAsync(Guid inspectionId,
        CancellationToken cancellationToken = default)
    {
        if (inspectionId == Guid.Empty)
            return new(false, "ProductionInspectionInspectionIdRequired");
        try
        {
            var result = await QueryCoreAsync(new ProductionInspectionHistoryFilter(
                InspectionId: inspectionId, PageSize: 1), ReadMode.Inspection, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Available)
                return new(false, result.ReasonCode);
            var latest = result.Events.LastOrDefault();
            return new(true, latest is null ? "ProductionInspectionHistoryEmpty" :
                "ProductionInspectionHistoryAvailable", latest, result.RecoveryRequired);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception,
            "ProductionInspectionHistoryUnavailable")); }
    }

    public async ValueTask<ProductionInspectionHistoryPage> QueryAsync(
        ProductionInspectionHistoryFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateFilter(filter);
        try
        {
            var result = await QueryCoreAsync(filter, ReadMode.Page, cancellationToken)
                .ConfigureAwait(false);
            return new ProductionInspectionHistoryPage(result.Available, result.ReasonCode,
                result.Events, result.ThroughPosition, result.NextAfterPosition,
                result.RecoveryRequired);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new ProductionInspectionHistoryPage(false,
                SqliteAuditIntegrityQuery.FaultReason(exception,
                    "ProductionInspectionHistoryUnavailable"), Array.Empty<ProductionInspectionHistoryEvent>(),
                0, null);
        }
    }

    public async ValueTask<ProductionRecoveryPendingPage> QueryPendingAsync(int pageSize = 128,
        long afterPosition = 0, CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 128 || afterPosition < 0)
            return new(false, "ProductionRecoveryPendingQueryInvalid",
                Array.Empty<ProductionRecoveryPendingItem>(), 0, null);
        try
        {
            // ReadMode.Pending reconstructs the complete verified ledger in
            // this one SQLite snapshot. Paging the ordinary history first can
            // hide a later inspection's pending recovery behind the default
            // 128-row page and falsely clear the station barrier.
            var result = await QueryCoreAsync(new ProductionInspectionHistoryFilter(
                AfterPosition: 0, PageSize: pageSize), ReadMode.Pending, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Available)
                return new(false, result.ReasonCode, Array.Empty<ProductionRecoveryPendingItem>(), 0, null);
            var pending = result.Events
                .GroupBy(value => value.InspectionId)
                .Select(group => group.OrderBy(value => value.Position).Last())
                .Where(value => value.Kind is not (ProductionInspectionEventKind.AcknowledgementReset or
                    ProductionInspectionEventKind.RecoveryCompleted) &&
                    (value.Recovery is null ||
                     value.Recovery.Outcome != ProductionRecoveryOutcome.Completed))
                .Select(value => new ProductionRecoveryPendingItem(value.InspectionId, value.Position,
                    value.ContentHash, value.Recovery?.Observation.DeliveryPhase ??
                        DeliveryPhaseFor(value.Kind), value.Recovery?.Observation.Uncertainty ??
                        ProductionRecoveryUncertaintyKind.Unknown, value))
                .OrderBy(value => value.Position).ToArray();
            var visible = pending.Where(value => value.Position > afterPosition)
                .Take(pageSize).ToArray();
            long? next = visible.Length == pageSize && pending.Any(value => value.Position > visible[^1].Position)
                ? visible[^1].Position : null;
            return new(true, result.ReasonCode, visible, pending.Length, next);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, SqliteAuditIntegrityQuery.FaultReason(exception,
                "ProductionRecoveryPendingUnavailable"),
                Array.Empty<ProductionRecoveryPendingItem>(), 0, null);
        }
    }

    private async ValueTask<QueryResult> QueryCoreAsync(ProductionInspectionHistoryFilter filter,
        ReadMode mode, CancellationToken cancellationToken)
    {
        if (_options.ProductionInspections is null)
            return QueryResult.Unavailable("ProductionInspectionConfigurationRequired");
        if (_options.AuditIntegrityPolicy is null || _options.LocalIdentity is null)
            return QueryResult.Unavailable("ProductionInspectionRequiresIdentityAndAudit");
        if (Interlocked.Increment(ref _outstanding) > 64)
        {
            Interlocked.Decrement(ref _outstanding);
            return QueryResult.Unavailable("ProductionInspectionQueryCapacityExceeded");
        }
        var entered = false;
        try
        {
            var deadline = new StoreDeadline(_options.QueryTimeout);
            entered = await Slots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!entered)
                return QueryResult.Unavailable("ProductionInspectionQueryDeadlineExceeded");
            return await Task.Run(() => QueryDatabase(filter, mode, deadline, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (entered) Slots.Release();
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private QueryResult QueryDatabase(ProductionInspectionHistoryFilter filter, ReadMode mode,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        var production = _options.ProductionInspections!;
        production.Validate();
        if (!StoragePathValidator.TryValidate(_options, out var path, out var reason))
            return QueryResult.Unavailable(reason);
        using var key = WindowsMachineAuditKey.Open(_options.AuditIntegrityPolicy!, false, out _);
        using var connection = SqliteNative.Open(path, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, _options);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        var committed = false;
        try
        {
            var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
            if (schema == RecipeSelectionStoreOptions.SchemaVersion &&
                _options.RecipeSelections is null)
                throw new InvalidOperationException("RecipeSelectionConfigurationRequired");
            if (schema == ProductionRecoveryStoreOptions.SchemaVersion &&
                _options.ProductionRecovery is null)
                throw new InvalidOperationException("ProductionRecoveryConfigurationRequired");
            if (schema is not (ProductionInspectionStoreOptions.SchemaVersion or
                PartIdentityStoreOptions.SchemaVersion or
                ProductionRecoveryStoreOptions.SchemaVersion or
                RecipeSelectionStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion))
                throw new InvalidOperationException(schema > ProductionInspectionStoreOptions.SchemaVersion
                    ? "ProductionInspectionGovernedMigrationRequired"
                    : "ProductionInspectionConfigurationRequired");
            SqliteNative.ConfigureSqliteLimit(database, _options, schema);
            var policy = _options.AuditIntegrityPolicy!;
            var verification = AuditChainDatabase.Verify(database, policy, key.KeyId,
                key.PublicKeyBase64, new AuditVerificationRequest(0, policy.MaximumVerificationEntries),
                startup: false, deadline, validateAnchorReceipt: false,
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
                productionAdmissionOptions: _options.ProductionAdmission,
                stationQualificationOptions: _options.StationQualifications,
                recipeTransferOptions: _options.RecipeTransfers,
                 traceStoragePolicyOptions: _options.TraceStoragePolicies,
                 qualificationCycleOptions: _options.QualificationCycles,
                 plcCommunicationOptions: _options.PlcCommunication,
                 productionRecoveryOptions: _options.ProductionRecovery,
                 productionInspectionOptions: production,
                 partIdentityOptions: _options.PartIdentities,
                 productionArmOptions: _options.ProductionArming, recipeSelectionOptions: _options.RecipeSelections, recipeLifecycleOptions: _options.RecipeLifecycle, imageEvidenceOptions: _options.ImageEvidence);
            AuditChainDatabase.RequireFullProductionInspectionVerification(database, verification,
                deadline, production);
            var rows = SqliteCommandStore.ReadProductionInspectionRows(database, production,
                deadline, _calibrationResolver);
            var latestPosition = rows.Count == 0 ? 0 : rows[^1].Position;
            var through = filter.ThroughPosition ?? latestPosition;
            if (through < 0 || through > latestPosition || filter.AfterPosition > through)
                throw new InvalidOperationException("ProductionInspectionCursorInvalid");
            var scoped = rows.Where(row => row.Position <= through && MatchesFilter(row.Event, filter))
                .Select(row => row.Event).ToArray();
            var selected = mode == ReadMode.Pending
                ? scoped.ToArray()
                : mode == ReadMode.Page
                ? scoped.Where(value => value.Position > filter.AfterPosition).Take(filter.PageSize + 1).ToArray()
                : scoped.TakeLast(1).ToArray();
            var page = mode == ReadMode.Pending ? selected : selected.Take(filter.PageSize).ToArray();
            long? next = mode == ReadMode.Page && selected.Length > filter.PageSize ? page[^1].Position : null;
            var checkpoint = AuditChainDatabase.LatestCheckpoint(database, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            return new QueryResult(true, page.Length == 0 ? "ProductionInspectionHistoryEmpty" :
                "ProductionInspectionHistoryAvailable", page, through, next, RecoveryRequired(scoped));
        }
        finally
        {
            if (!committed)
                try { raw.sqlite3_exec(database, "ROLLBACK;"); } catch { }
        }
    }

    private static bool MatchesFilter(ProductionInspectionHistoryEvent value,
        ProductionInspectionHistoryFilter filter) =>
        (!filter.InspectionId.HasValue || value.InspectionId == filter.InspectionId.Value) &&
        (!filter.CorrelationId.HasValue || value.CorrelationId == filter.CorrelationId.Value) &&
        (!filter.RuntimeEpoch.HasValue || value.RuntimeEpoch == filter.RuntimeEpoch.Value);

    private static bool RecoveryRequired(IEnumerable<ProductionInspectionHistoryEvent> events)
    {
        var pending = new HashSet<Guid>();
        foreach (var value in events.OrderBy(item => item.Position))
        {
            switch (value.Kind)
            {
                case ProductionInspectionEventKind.Admitted:
                case ProductionInspectionEventKind.CoreCommitted:
                case ProductionInspectionEventKind.FaultTerminated:
                case ProductionInspectionEventKind.PublicationPrepared:
                case ProductionInspectionEventKind.ResultValidRaised:
                case ProductionInspectionEventKind.ResultAcknowledged:
                case ProductionInspectionEventKind.ResultValidCleared:
                case ProductionInspectionEventKind.RecoveryRequired:
                    pending.Add(value.InspectionId);
                    break;
                case ProductionInspectionEventKind.AcknowledgementReset:
                case ProductionInspectionEventKind.RecoveryCompleted:
                    pending.Remove(value.InspectionId);
                    break;
            }
        }
        return pending.Count != 0;
    }

    private static ProductionRecoveryDeliveryPhase DeliveryPhaseFor(
        ProductionInspectionEventKind kind) => kind switch
        {
            ProductionInspectionEventKind.Admitted => ProductionRecoveryDeliveryPhase.Admitted,
            ProductionInspectionEventKind.CoreCommitted => ProductionRecoveryDeliveryPhase.CoreCommitted,
            ProductionInspectionEventKind.PublicationPrepared => ProductionRecoveryDeliveryPhase.PublicationPrepared,
            ProductionInspectionEventKind.ResultValidRaised => ProductionRecoveryDeliveryPhase.ResultValidRaised,
            ProductionInspectionEventKind.ResultAcknowledged => ProductionRecoveryDeliveryPhase.ResultAcknowledged,
            ProductionInspectionEventKind.ResultValidCleared => ProductionRecoveryDeliveryPhase.ResultValidCleared,
            ProductionInspectionEventKind.FaultTerminated => ProductionRecoveryDeliveryPhase.FaultTerminated,
            ProductionInspectionEventKind.RecoveryRequired => ProductionRecoveryDeliveryPhase.RecoveryRequired,
            _ => ProductionRecoveryDeliveryPhase.Unknown
        };

    private static void ValidateFilter(ProductionInspectionHistoryFilter filter)
    {
        if (filter.InspectionId == Guid.Empty || filter.CorrelationId == Guid.Empty ||
            filter.RuntimeEpoch == Guid.Empty || filter.AfterPosition < 0 ||
            filter.ThroughPosition is < 0 || filter.ThroughPosition < filter.AfterPosition ||
            filter.PageSize is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(filter));
    }

    private enum ReadMode { Current, Inspection, Page, Pending }

    private sealed record QueryResult(bool Available, string ReasonCode,
        IReadOnlyList<ProductionInspectionHistoryEvent> Events, long ThroughPosition,
        long? NextAfterPosition, bool RecoveryRequired)
    {
        internal static QueryResult Unavailable(string reason) =>
            new(false, reason, Array.Empty<ProductionInspectionHistoryEvent>(), 0, null, false);
    }
}
