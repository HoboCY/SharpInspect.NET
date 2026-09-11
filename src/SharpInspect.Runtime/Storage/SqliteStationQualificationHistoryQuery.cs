using System.Collections.ObjectModel;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>Read-only, bounded projection of the schema-23 qualification ledger.</summary>
public sealed class SqliteStationQualificationHistoryQuery : IStationQualificationHistoryQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(4, 4);

    public SqliteStationQualificationHistoryQuery(ProductionStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async ValueTask<StationQualificationHistoryReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!snapshot.Available) return new(false, snapshot.ReasonCode);
            var current = snapshot.Rows.GroupBy(row => row.Event.SessionId)
                .Select(group => group.OrderBy(row => row.Position).Last().Event)
                .Where(value => !value.Terminal || value.Restoration == StationQualificationRestorationState.RecoveryBlocked)
                .OrderBy(value => value.Position).LastOrDefault();
            var globalLast = snapshot.Rows.OrderBy(row => row.Position).LastOrDefault()?.Event;
            var all = current is null ? null : snapshot.Rows.Where(row => row.Event.SessionId == current.SessionId)
                .Select(row => row.Event).ToArray();
            return current is null
                ? new(true, "StationQualificationNoPendingSession", LastEvent: globalLast)
                : ReadResult(current.Header, current, all!, snapshot.Rows);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception, "StationQualificationHistoryUnavailable")); }
    }

    public async ValueTask<StationQualificationHistoryReadResult> ReadAsync(Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) return new(false, "StationQualificationSessionIdRequired");
        try
        {
            var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!snapshot.Available) return new(false, snapshot.ReasonCode);
            var events = snapshot.Rows.Where(row => row.Event.SessionId == sessionId)
                .OrderBy(row => row.Position).Select(row => row.Event).ToArray();
            if (events.Length == 0) return new(true, "StationQualificationSessionNotFound");
            return ReadResult(events[0].Header, events[^1], events, snapshot.Rows);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception, "StationQualificationHistoryUnavailable")); }
    }

    public async ValueTask<StationQualificationHistoryPage> QueryAsync(
        StationQualificationHistoryFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateFilter(filter);
        try
        {
            var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!snapshot.Available) return Empty(false, snapshot.ReasonCode);
            var through = filter.ThroughPosition ?? snapshot.Rows.LastOrDefault()?.Position ?? 0;
            AuditChainDatabase.Require(through <= (snapshot.Rows.LastOrDefault()?.Position ?? 0),
                "StationQualificationHistoryThroughPositionInvalid");
            var selected = snapshot.Rows.Where(row => row.Position > filter.AfterPosition &&
                    row.Position <= through &&
                    (filter.SessionId is null || row.Event.SessionId == filter.SessionId.Value) &&
                    (filter.RunId is null || row.Event.Run?.RunId.Value == filter.RunId.Value))
                .Take(filter.PageSize).Select(row => row.Event).ToArray();
            var hasMore = snapshot.Rows.Any(row => row.Position > (selected.LastOrDefault()?.Position ?? filter.AfterPosition) &&
                row.Position <= through &&
                (filter.SessionId is null || row.Event.SessionId == filter.SessionId.Value) &&
                (filter.RunId is null || row.Event.Run?.RunId.Value == filter.RunId.Value));
            var runs = selected.Where(value => value.Run is not null).Select(value => value.Run!)
                .GroupBy(value => value.RunId.Value).Select(group => group.OrderBy(value => value.Position).Last())
                .ToArray();
            var pending = snapshot.Rows.Where(row => row.Position <= through)
                .GroupBy(row => row.Event.SessionId).Select(group => group.OrderBy(row => row.Position).Last().Event)
                .Where(value => !value.Terminal || value.Restoration == StationQualificationRestorationState.RecoveryBlocked)
                .OrderBy(value => value.Position).LastOrDefault();
            return new(true, "StationQualificationHistoryAvailable", selected, runs, through,
                hasMore ? selected.LastOrDefault()?.Position : null, pending?.Header,
                pending?.Phase == StationQualificationSessionPhase.RecoveryBlocked ||
                pending?.Restoration == StationQualificationRestorationState.RecoveryBlocked);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Empty(false, SqliteAuditIntegrityQuery.FaultReason(exception, "StationQualificationHistoryUnavailable")); }
    }

    private async ValueTask<Snapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var options = _options.StationQualifications;
        if (options is null) return new(false, "StationQualificationConfigurationRequired", Array.Empty<SqliteCommandStore.StationQualificationStoredRow>());
        if (_options.AuditIntegrityPolicy is null || _options.LocalIdentity is null)
            return new(false, "StationQualificationRequiresIdentityAndAudit", Array.Empty<SqliteCommandStore.StationQualificationStoredRow>());
        var deadline = new StoreDeadline(_options.QueryTimeout);
        var entered = await Slots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
        if (!entered) return new(false, "StationQualificationQueryCapacityExceeded", Array.Empty<SqliteCommandStore.StationQualificationStoredRow>());
        try
        {
            return await Task.Run(() => ReadSnapshotCore(options, deadline, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally { Slots.Release(); }
    }

    private Snapshot ReadSnapshotCore(StationQualificationStoreOptions options,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        if (!StoragePathValidator.TryValidate(_options, out var path, out var pathReason))
            return new(false, pathReason, Array.Empty<SqliteCommandStore.StationQualificationStoredRow>());
        using var key = WindowsMachineAuditKey.Open(_options.AuditIntegrityPolicy!, false, out _);
        using var connection = SqliteNative.Open(path, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, _options);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        var committed = false;
        try
        {
            var schema = checked((int)AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline));
            TraceStoragePolicyReadGuard.RequireConfiguration(schema, _options);
            AuditChainDatabase.Require(schema is StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion,
                schema < PlcCommunicationStoreOptions.SchemaVersion
                    ? "StationQualificationGovernedMigrationRequired" : "StoreSchemaTooNew");
            SqliteNative.ConfigureSqliteLimit(database, _options, schema);
            var policy = _options.AuditIntegrityPolicy!;
            var verification = AuditChainDatabase.Verify(database, policy, key.KeyId,
                key.PublicKeyBase64, new AuditVerificationRequest(0, policy.MaximumVerificationEntries),
                startup: false, deadline, validateAnchorReceipt: false,
                archiveOptions: _options.AlgorithmResultArchive,
                recipeDraftOptions: _options.RecipeDrafts, cameraSetupOptions: _options.CameraSetup,
                cameraRecoveryOptions: _options.CameraRecovery, cameraNetworkOptions: _options.CameraNetwork,
                imagingSetupOptions: _options.ImagingSetup, calibrationSessionOptions: _options.CalibrationSessions,
                governanceOptions: _options.CalibrationGovernance, releaseOptions: _options.RecipeReleases,
                contractOptions: _options.PlcResultContracts, activationOptions: _options.RecipeActivations,
                previewOptions: _options.PreviewSessions, importOptions: _options.CalibrationImports,
                manualOptions: _options.ManualInspections,
                productionAdmissionOptions: _options.ProductionAdmission,
                stationQualificationOptions: options,
                recipeTransferOptions: _options.RecipeTransfers,
                traceStoragePolicyOptions: _options.TraceStoragePolicies,
                qualificationCycleOptions: _options.QualificationCycles,
                plcCommunicationOptions: _options.PlcCommunication,
                productionInspectionOptions: _options.ProductionInspections,
                productionRecoveryOptions: _options.ProductionRecovery,
                partIdentityOptions: _options.PartIdentities);
            RecipeTransferReadGuard.RequireVerified(database, verification, deadline, _options);
        if (_options.PlcCommunication is not null)
            AuditChainDatabase.RequireFullPlcCommunicationVerification(database, verification, deadline,
                _options.PlcCommunication);
            TraceStoragePolicyReadGuard.RequireVerified(database, verification, deadline, _options);
            AuditChainDatabase.RequireFullStationQualificationVerification(database, verification,
                deadline, options);
            var rows = SqliteCommandStore.ReadStationQualificationRows(database, options, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            return new(true, "StationQualificationHistoryAvailable", rows);
        }
        finally
        {
            if (!committed) try { raw.sqlite3_exec(database, "ROLLBACK;"); } catch { }
        }
    }

    private StationQualificationHistoryReadResult ReadResult(
        StationQualificationSessionHeader header, StationQualificationSessionEvent last,
        IReadOnlyList<StationQualificationSessionEvent> events,
        IReadOnlyList<SqliteCommandStore.StationQualificationStoredRow> allRows)
    {
        var latestRun = events.Where(value => value.Run is not null).Select(value => value.Run!)
            .GroupBy(value => value.RunId.Value).Select(group => group.OrderBy(value => value.Position).Last())
            .OrderBy(value => value.Position).LastOrDefault();
        var recovery = last.Phase == StationQualificationSessionPhase.RecoveryBlocked ||
            last.Restoration == StationQualificationRestorationState.RecoveryBlocked;
        return new(true, "StationQualificationHistoryAvailable", header, last, latestRun, recovery);
    }

    private static void ValidateFilter(StationQualificationHistoryFilter filter)
    {
        if (filter.SessionId == Guid.Empty || filter.RunId == Guid.Empty || filter.AfterPosition < 0 ||
            filter.ThroughPosition is < 0 || filter.ThroughPosition < filter.AfterPosition ||
            filter.PageSize is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(filter));
    }

    private static StationQualificationHistoryPage Empty(bool available, string reason) => new(
        available, reason, Array.Empty<StationQualificationSessionEvent>(),
        Array.Empty<StationQualificationRunRecord>(), 0, null);

    private sealed record Snapshot(bool Available, string ReasonCode,
        IReadOnlyList<SqliteCommandStore.StationQualificationStoredRow> Rows);
}
