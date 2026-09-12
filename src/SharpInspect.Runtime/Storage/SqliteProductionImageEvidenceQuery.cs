using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed record ImageFinalizationReplayItem(long Position, PendingImageFinalizationWork Work,
    ProductionImageFinalizationWorkState State, IReadOnlyList<ProductionImageFinalizationEvent> Events);
internal sealed record ImageFinalizationReplayPage(IReadOnlyList<ImageFinalizationReplayItem> Items,
    long ThroughPosition, long? NextAfterPosition, ImageBacklogSnapshot Backlog);

/// <summary>
/// Read-only, bounded access to the schema-35 production image evidence lifecycle. Every page
/// is reconstructed inside one frozen SQLite snapshot after a full central-audit verification,
/// so the returned audit watermark can never be a stale in-memory projection. The query exposes
/// no mutation surface and never accepts a filesystem path.
/// </summary>
public sealed class SqliteProductionImageEvidenceQuery : IProductionImageEvidenceQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private static int _outstanding;

    public SqliteProductionImageEvidenceQuery(ProductionStoreOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<ProductionImageEvidencePage> QueryAsync(
        ProductionImageEvidenceFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateFilter(filter);
        try
        {
            return await ReadAsync(page => page.Query(filter), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new ProductionImageEvidencePage(false,
                SqliteAuditIntegrityQuery.FaultReason(exception, "ImageEvidenceQueryUnavailable"),
                Array.Empty<ProductionImageEvidenceRecord>(), 0, null, 0);
        }
    }

    public async ValueTask<ProductionImageWorkQueuePage> ReadWorkQueueAsync(int pageSize = 128,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 512)
            return new ProductionImageWorkQueuePage(false, "ImageEvidenceQueryPageSizeInvalid",
                Array.Empty<ProductionImageFinalizationWorkState>(),
                Array.Empty<ProductionImageFinalizationWorkState>(), false, false, 0);
        try
        {
            return await ReadAsync(page => page.Queue(pageSize), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new ProductionImageWorkQueuePage(false,
                SqliteAuditIntegrityQuery.FaultReason(exception, "ImageEvidenceQueryUnavailable"),
                Array.Empty<ProductionImageFinalizationWorkState>(),
                Array.Empty<ProductionImageFinalizationWorkState>(), false, false, 0);
        }
    }

    public async ValueTask<ImageBacklogSnapshot> ReadBacklogAsync(
        CancellationToken cancellationToken = default)
    {
        // A backlog watermark is never fabricated: an unverifiable read throws instead of
        // returning an empty snapshot a caller could mistake for an empty backlog.
        return await ReadAsync(page => page.Backlog(), cancellationToken).ConfigureAwait(false);
    }

    // Cursor is the immutable Core position, so exhausted/retry-delayed work cannot
    // starve later obligations. Work, lifecycle and backlog share one verified snapshot.
    internal ValueTask<ImageFinalizationReplayPage> ReadReplayAsync(long afterPosition,
        long? throughPosition, bool includeReleased, CancellationToken token) =>
        ReadAsync(page => page.Replay(afterPosition, throughPosition, includeReleased,
            _options.ImageFinalization!.MaximumPageSize), token);

    private async ValueTask<T> ReadAsync<T>(Func<PageReader, T> select,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _outstanding) > 32)
        {
            Interlocked.Decrement(ref _outstanding);
            throw new InvalidOperationException("ImageEvidenceQueryCapacityExceeded");
        }
        var entered = false;
        try
        {
            var deadline = new StoreDeadline(_options.QueryTimeout);
            entered = await Slots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!entered) throw new TimeoutException("ImageEvidenceQueryDeadlineExceeded");
            return await Task.Run(() => ReadDatabase(select, deadline, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (entered) Slots.Release();
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private T ReadDatabase<T>(Func<PageReader, T> select, StoreDeadline deadline,
        CancellationToken cancellationToken)
    {
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        var finalization = _options.ImageFinalization ??
            throw new InvalidOperationException("ImageFinalizationConfigurationRequired");
        var production = _options.ProductionInspections ??
            throw new InvalidOperationException("ImageFinalizationProductionConfigurationRequired");
        var policy = _options.AuditIntegrityPolicy ??
            throw new InvalidOperationException("AuditPolicyNotConfigured");
        finalization.Validate();
        if (!StoragePathValidator.TryValidate(_options, out var path, out var reason))
            throw new InvalidOperationException(reason);
        using var key = WindowsMachineAuditKey.Open(policy, false, out _);
        using var connection = SqliteNative.Open(path, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, _options);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        var committed = false;
        try
        {
            var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
            if (schema != ProductionImageFinalizationStoreOptions.SchemaVersion)
                throw new InvalidOperationException(schema > ProductionImageFinalizationStoreOptions.SchemaVersion
                    ? "ImageFinalizationSchemaTooNew"
                    : "ImageFinalizationGovernedMigrationRequired");
            var verification = AuditChainDatabase.Verify(database, policy, key.KeyId,
                key.PublicKeyBase64,
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
                productionAdmissionOptions: _options.ProductionAdmission,
                stationQualificationOptions: _options.StationQualifications,
                recipeTransferOptions: _options.RecipeTransfers,
                traceStoragePolicyOptions: _options.TraceStoragePolicies,
                qualificationCycleOptions: _options.QualificationCycles,
                plcCommunicationOptions: _options.PlcCommunication,
                productionInspectionOptions: production,
                productionRecoveryOptions: _options.ProductionRecovery,
                partIdentityOptions: _options.PartIdentities,
                productionArmOptions: _options.ProductionArming,
                recipeSelectionOptions: _options.RecipeSelections,
                recipeLifecycleOptions: _options.RecipeLifecycle,
                imageEvidenceOptions: _options.ImageEvidence,
                imageFinalizationOptions: finalization);
            AuditChainDatabase.RequireFullImageFinalizationVerification(database, verification,
                deadline, finalization);
            var auditSequence = AuditChainDatabase.Tail(database, deadline).Sequence;
            var reader = new PageReader(database, _options, finalization, production, auditSequence,
                deadline);
            var result = select(reader);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            return result;
        }
        finally
        {
            if (!committed)
                try { raw.sqlite3_exec(database, "ROLLBACK;"); } catch { }
        }
    }

    private static void ValidateFilter(ProductionImageEvidenceFilter filter)
    {
        if (filter.InspectionId == Guid.Empty || filter.WorkId == Guid.Empty ||
            filter.AfterPosition < 0 || filter.ThroughPosition is < 0 ||
            filter.ThroughPosition < filter.AfterPosition || filter.PageSize is < 1 or > 512)
            throw new ArgumentOutOfRangeException(nameof(filter));
    }

    /// <summary>One verified read snapshot; every derived value comes from immutable rows.</summary>
    private sealed class PageReader
    {
        private readonly sqlite3 _database;
        private readonly StoreDeadline _deadline;
        private readonly IReadOnlyList<ImageFinalizationStoredRow> _events;
        private readonly IReadOnlyList<ProductionImageFinalizationWorkState> _states;
        private readonly IReadOnlyList<ProductionInspectionStoredRow> _cores;

        internal PageReader(sqlite3 database, ProductionStoreOptions options,
            ProductionImageFinalizationStoreOptions finalization,
            ProductionInspectionStoreOptions production, long auditSequence, StoreDeadline deadline)
        {
            _database = database;
            _deadline = deadline;
            AuditSequence = auditSequence;
            _events = SqliteCommandStore.ReadImageFinalizationRows(database, finalization, deadline);
            _states = SqliteCommandStore.ReadImageFinalizationObligationStates(database, finalization,
                deadline);
            _cores = SqliteCommandStore.ReadProductionInspectionRows(database, production, deadline);
        }

        internal long AuditSequence { get; }

        internal ProductionImageEvidencePage Query(ProductionImageEvidenceFilter filter)
        {
            var cores = _cores.Where(row => row.Event.Kind ==
                    ProductionInspectionEventKind.CoreCommitted &&
                row.Event.Core?.ImageEvidence?.Work is not null &&
                (filter.InspectionId is null || row.Event.Core!.Admission.InspectionId == filter.InspectionId) &&
                (filter.WorkId is null || row.Event.Core!.ImageEvidence!.Work!.WorkId == filter.WorkId))
                .ToArray();
            var latest = cores.Length == 0 ? 0 : cores[^1].Position;
            var through = filter.ThroughPosition ?? latest;
            if (through > latest) through = latest;
            var selected = cores.Where(row => row.Position <= through &&
                row.Position > filter.AfterPosition).Take(filter.PageSize + 1).ToArray();
            var page = selected.Take(filter.PageSize).ToArray();
            long? next = selected.Length > filter.PageSize && page.Length > 0 ? page[^1].Position : null;
            var items = page.Select(BuildRecord).ToArray();
            return new ProductionImageEvidencePage(true,
                items.Length == 0 ? "ImageEvidenceEmpty" : "ImageEvidenceAvailable", items,
                through, next, AuditSequence);
        }

        internal ProductionImageWorkQueuePage Queue(int pageSize)
        {
            var pending = _states.Where(state => state.State != ProductionImageFinalizationState.Succeeded)
                .OrderBy(state => state.WorkId).ToArray();
            var unreleased = _states.Where(state =>
                    state.State == ProductionImageFinalizationState.Succeeded &&
                    state.CleanupState == ProductionImageCleanupState.Pending)
                .OrderBy(state => state.WorkId).ToArray();
            return new ProductionImageWorkQueuePage(true, "ImageEvidenceWorkQueueAvailable",
                pending.Take(pageSize).ToArray(), unreleased.Take(pageSize).ToArray(),
                pending.Length > pageSize, unreleased.Length > pageSize, AuditSequence);
        }

        internal ImageBacklogSnapshot Backlog() =>
            SqliteCommandStore.ReadImageBacklogSnapshot(_database, AuditSequence, _deadline);

        internal ImageFinalizationReplayPage Replay(long after, long? through, bool includeReleased, int size)
        {
            var cores = _cores.Where(row => row.Event.Kind == ProductionInspectionEventKind.CoreCommitted &&
                row.Event.Core?.ImageEvidence?.Work is not null).ToArray();
            var upper = through ?? (cores.Length == 0 ? 0 : cores[^1].Position);
            var selected = cores.Where(row => row.Position > after && row.Position <= upper)
                .Where(row => includeReleased || !_states.Any(state =>
                    state.WorkId == row.Event.Core!.ImageEvidence!.Work!.WorkId &&
                    state.CleanupState == ProductionImageCleanupState.Released))
                .Take(size + 1).ToArray();
            var items = selected.Take(size).Select(row =>
            {
                var record = BuildRecord(row);
                return new ImageFinalizationReplayItem(row.Position, row.Event.Core!.ImageEvidence!.Work!,
                    record.State, record.Events);
            }).ToArray();
            return new(items, upper, selected.Length > size ? items[^1].Position : null, Backlog());
        }

        private ProductionImageEvidenceRecord BuildRecord(ProductionInspectionStoredRow row)
        {
            var core = row.Event.Core!;
            var work = core.ImageEvidence!.Work!;
            var manifest = work.Manifest;
            var identity = new ProductionImageCoreImageIdentity(manifest.InspectionId,
                manifest.ManifestId, work.WorkId, manifest.StageId, manifest.CanonicalPixelHash,
                manifest.Width, manifest.Height, manifest.PixelFormat, manifest.ValidBits,
                manifest.CanonicalByteLength, manifest.EvidencePolicyContentHash,
                manifest.CreatedAtUtc, manifest.ContentHash, work.ContentHash);
            var state = _states.FirstOrDefault(value => value.WorkId == work.WorkId) ??
                new ProductionImageFinalizationWorkState(work.WorkId, manifest.ManifestId,
                    manifest.InspectionId, work.ContentHash, manifest.ContentHash,
                    ProductionImageFinalizationState.Pending, ProductionImageCleanupState.Pending,
                    0, 1, retryEligible: true, lastFailureReasonCode: null,
                    lastFailureCategory: null, retryAfterUtc: null, success: null,
                    lastEventPosition: 0, lastEventContentHash: null);
            var events = _events.Where(value => value.Event.WorkId == work.WorkId)
                .Select(value => value.Event).ToArray();
            return new ProductionImageEvidenceRecord(identity, state, events, row.Position);
        }
    }
}
