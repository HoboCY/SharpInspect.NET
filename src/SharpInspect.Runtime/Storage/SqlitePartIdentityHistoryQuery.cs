using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>Read-only access to the schema-29 part-identity rejection/correction ledger.</summary>
public sealed class SqlitePartIdentityHistoryQuery : IPartIdentityHistoryQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(4, 4);
    private static int _outstanding;

    public SqlitePartIdentityHistoryQuery(ProductionStoreOptions options)
    { _options = options ?? throw new ArgumentNullException(nameof(options)); }

    public async ValueTask<PartIdentityHistoryReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReadCoreAsync((database, deadline) =>
            {
                var rows = SqliteCommandStore.ReadPartIdentityRows(
                    database, _options.PartIdentities!, deadline);
                var latest = rows.Count == 0 ? null : rows[^1].Event;
                return new PartIdentityHistoryReadResult(true,
                    latest is null ? "PartIdentityHistoryEmpty" : "PartIdentityHistoryAvailable",
                    latest);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception,
            "PartIdentityHistoryUnavailable")); }
    }

    public async ValueTask<PartIdentityHistoryReadResult> ReadAsync(Guid inspectionId,
        CancellationToken cancellationToken = default)
    {
        if (inspectionId == Guid.Empty)
            throw new ArgumentException("PartIdentityInspectionIdInvalid", nameof(inspectionId));
        try
        {
            return await ReadCoreAsync((database, deadline) =>
            {
                var rows = SqliteCommandStore.ReadPartIdentityRows(
                    database, _options.PartIdentities!, deadline);
                var latest = rows.Where(value => value.Event.InspectionId == inspectionId)
                    .Select(value => value.Event).LastOrDefault();
                return new PartIdentityHistoryReadResult(true,
                    latest is null ? "PartIdentityHistoryEmpty" : "PartIdentityHistoryAvailable",
                    latest);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception,
            "PartIdentityHistoryUnavailable")); }
    }

    public async ValueTask<PartIdentityHistoryPage> QueryAsync(PartIdentityHistoryFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.AfterPosition < 0 || filter.ThroughPosition is < 0 ||
            filter.ThroughPosition < filter.AfterPosition || filter.PageSize is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(filter));
        if (filter.InspectionId == Guid.Empty || filter.CorrelationId == Guid.Empty ||
            filter.RuntimeEpoch == Guid.Empty)
            throw new ArgumentException("PartIdentityHistoryFilterInvalid", nameof(filter));
        try
        {
            return await ReadCoreAsync((database, deadline) =>
            {
                var rows = SqliteCommandStore.ReadPartIdentityRows(
                    database, _options.PartIdentities!, deadline);
                var latestPosition = rows.Count == 0 ? 0 : rows[^1].Position;
                var through = filter.ThroughPosition ?? latestPosition;
                AuditChainDatabase.Require(through <= latestPosition,
                    "PartIdentityHistoryThroughPositionInvalid");
                var scoped = rows.Where(value => value.Position <= through &&
                    (filter.InspectionId is null || value.Event.InspectionId == filter.InspectionId) &&
                    (filter.CorrelationId is null || value.Event.CorrelationId == filter.CorrelationId) &&
                    (filter.RuntimeEpoch is null || value.Event.RuntimeEpoch == filter.RuntimeEpoch))
                    .Select(value => value.Event).ToArray();
                var selected = scoped.Where(value => value.Position > filter.AfterPosition)
                    .Take(filter.PageSize + 1).ToArray();
                var page = selected.Take(filter.PageSize).ToArray();
                return new PartIdentityHistoryPage(true,
                    page.Length == 0 ? "PartIdentityHistoryEmpty" : "PartIdentityHistoryAvailable",
                    page, through, selected.Length > filter.PageSize ? page[^1].Position : null);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception,
            "PartIdentityHistoryUnavailable"), Array.Empty<PartIdentityHistoryEvent>(), 0, null); }
    }

    private async ValueTask<T> ReadCoreAsync<T>(
        Func<sqlite3, StoreDeadline, T> read, CancellationToken cancellationToken)
    {
        if (_options.PartIdentities is null)
            throw new InvalidOperationException("PartIdentityConfigurationRequired");
        if (_options.AuditIntegrityPolicy is null || _options.LocalIdentity is null)
            throw new InvalidOperationException("PartIdentityRequiresIdentityAndAudit");
        _options.PartIdentities.Validate();
        if (_options.QueryTimeout < TimeSpan.FromMilliseconds(1) ||
            _options.QueryTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(_options.QueryTimeout));
        var deadline = new StoreDeadline(_options.QueryTimeout);
        if (Interlocked.Increment(ref _outstanding) > 64)
        {
            Interlocked.Decrement(ref _outstanding);
            throw new InvalidOperationException("PartIdentityQueryCapacityExceeded");
        }
        var entered = false;
        try
        {
            entered = await Slots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!entered)
                throw new InvalidOperationException("PartIdentityQueryCapacityExceeded");
            return await Task.Run(() => ReadDatabase(read, deadline, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (entered) Slots.Release();
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private T ReadDatabase<T>(Func<sqlite3, StoreDeadline, T> read,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        if (!StoragePathValidator.TryValidate(_options, out var path, out var reason))
            throw new InvalidOperationException(reason);
        using var key = WindowsMachineAuditKey.Open(_options.AuditIntegrityPolicy!, false, out _);
        using var connection = SqliteNative.Open(path, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, _options);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        var committed = false;
        try
        {
            var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
            if (schema is not (PartIdentityStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion or ProductionImageFinalizationStoreOptions.SchemaVersion))
                throw new InvalidOperationException(schema > ProductionRecoveryStoreOptions.SchemaVersion
                    ? "PartIdentityGovernedMigrationRequired"
                    : "PartIdentityConfigurationRequired");
            SqliteNative.ConfigureSqliteLimit(database, _options, schema);
            var policy = _options.AuditIntegrityPolicy!;
            var verification = AuditChainDatabase.Verify(database, policy,
                key.KeyId, key.PublicKeyBase64,
                new AuditVerificationRequest(0, policy.MaximumVerificationEntries),
                startup: true, deadline, validateAnchorReceipt: false,
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
                productionInspectionOptions: _options.ProductionInspections,
                productionRecoveryOptions: _options.ProductionRecovery,
                partIdentityOptions: _options.PartIdentities,
                productionArmOptions: _options.ProductionArming, recipeSelectionOptions: _options.RecipeSelections, recipeLifecycleOptions: _options.RecipeLifecycle, imageEvidenceOptions: _options.ImageEvidence, imageFinalizationOptions: _options.ImageFinalization);
            AuditChainDatabase.RequireFullPartIdentityVerification(database, verification,
                deadline, _options.PartIdentities);
            SqliteNative.EnsureDeadline(deadline, cancellationToken);
            var result = read(database, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            return result;
        }
        finally
        {
            if (!committed)
            {
                try { raw.sqlite3_exec(database, "ROLLBACK;"); }
                catch { }
            }
        }
    }
}
