using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>Read-only, bounded access to the schema-26 qualification-cycle ledger.</summary>
public sealed class SqliteQualificationCycleHistoryQuery : IQualificationCycleHistoryQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(4, 4);

    public SqliteQualificationCycleHistoryQuery(ProductionStoreOptions options)
    { _options = options ?? throw new ArgumentNullException(nameof(options)); }

    public async ValueTask<QualificationCycleHistoryReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!snapshot.Available) return new(false, snapshot.ReasonCode);
            var latest = snapshot.Rows.LastOrDefault()?.Event;
            var pending = LatestPending(snapshot.Rows.Select(value => value.Event));
            return pending is null
                ? new(true, "QualificationCycleNoPendingEvent", latest)
                : new(true, "QualificationCyclePending", pending, true, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception, "QualificationCycleHistoryUnavailable")); }
    }

    public async ValueTask<QualificationCycleHistoryReadResult> ReadAsync(Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) return new(false, "QualificationCycleSessionIdRequired");
        try
        {
            var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!snapshot.Available) return new(false, snapshot.ReasonCode);
            var sessionEvents = snapshot.Rows.Where(value => value.Event.SessionId == sessionId)
                .Select(value => value.Event).ToArray();
            var last = sessionEvents.LastOrDefault();
            if (last is null) return new(true, "QualificationCycleSessionNotFound");
            var pending = LatestPending(sessionEvents);
            return new(true, pending is not null ? "QualificationCyclePending" :
                "QualificationCycleAvailable", pending ?? last, pending is not null, pending is not null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception, "QualificationCycleHistoryUnavailable")); }
    }

    public async ValueTask<QualificationCycleHistoryPage> QueryAsync(
        QualificationCycleHistoryFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateFilter(filter);
        try
        {
            var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!snapshot.Available) return Empty(false, snapshot.ReasonCode);
            var latest = snapshot.Rows.LastOrDefault()?.Position ?? 0;
            var through = filter.ThroughPosition ?? latest;
            AuditChainDatabase.Require(through <= latest,
                "QualificationCycleHistoryThroughPositionInvalid");
            var selected = snapshot.Rows.Where(value => value.Position > filter.AfterPosition &&
                    value.Position <= through &&
                    (filter.SessionId is null || value.Event.SessionId == filter.SessionId.Value) &&
                    (filter.RunId is null || value.Event.RunId?.Value == filter.RunId.Value))
                .Take(filter.PageSize + 1).Select(value => value.Event).ToArray();
            var page = selected.Take(filter.PageSize).ToArray();
            var hasMore = selected.Length > page.Length;
            var pending = LatestPending(snapshot.Rows.Where(value => value.Position <= through &&
                    (filter.RunId is null || value.Event.RunId?.Value == filter.RunId.Value))
                .Select(value => value.Event), filter.SessionId);
            return new QualificationCycleHistoryPage(true,
                page.Length == 0 ? "QualificationCycleHistoryEmpty" : "QualificationCycleHistoryAvailable",
                page, through, hasMore ? page[^1].Position : null, pending,
                pending is not null, pending is not null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Empty(false, SqliteAuditIntegrityQuery.FaultReason(exception, "QualificationCycleHistoryUnavailable")); }
    }

    private async ValueTask<Snapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var options = _options.QualificationCycles;
        if (options is null) return new(false, "QualificationCycleConfigurationRequired",
            Array.Empty<QualificationCycleStoredRow>());
        if (_options.AuditIntegrityPolicy is null || _options.LocalIdentity is null)
            return new(false, "QualificationCycleRequiresIdentityAndAudit",
                Array.Empty<QualificationCycleStoredRow>());
        options.Validate();
        var deadline = new StoreDeadline(_options.QueryTimeout);
        var entered = await Slots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
        if (!entered) return new(false, "QualificationCycleQueryCapacityExceeded",
            Array.Empty<QualificationCycleStoredRow>());
        try
        {
            return await Task.Run(() => ReadSnapshotCore(options, deadline, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally { Slots.Release(); }
    }

    private Snapshot ReadSnapshotCore(QualificationCycleStoreOptions options,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        if (!StoragePathValidator.TryValidate(_options, out var path, out var pathReason))
            return new(false, pathReason, Array.Empty<QualificationCycleStoredRow>());
        using var key = WindowsMachineAuditKey.Open(_options.AuditIntegrityPolicy!, false, out _);
        using var connection = SqliteNative.Open(path, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, _options);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        var committed = false;
        try
        {
            var schema = checked((int)AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline));
            AuditChainDatabase.Require(schema is QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion or ProductionImageFinalizationStoreOptions.SchemaVersion,
                schema < PlcCommunicationStoreOptions.SchemaVersion
                    ? "QualificationCycleConfigurationRequired" :
                    "QualificationCycleGovernedMigrationRequired");
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
                manualOptions: _options.ManualInspections, productionAdmissionOptions: _options.ProductionAdmission,
                stationQualificationOptions: _options.StationQualifications,
                recipeTransferOptions: _options.RecipeTransfers,
                traceStoragePolicyOptions: _options.TraceStoragePolicies,
                qualificationCycleOptions: options,
                plcCommunicationOptions: _options.PlcCommunication,
                productionInspectionOptions: _options.ProductionInspections,
                productionRecoveryOptions: _options.ProductionRecovery,
                partIdentityOptions: _options.PartIdentities,
                productionArmOptions: _options.ProductionArming, recipeSelectionOptions: _options.RecipeSelections, recipeLifecycleOptions: _options.RecipeLifecycle, imageEvidenceOptions: _options.ImageEvidence, imageFinalizationOptions: _options.ImageFinalization);
            RecipeTransferReadGuard.RequireVerified(database, verification, deadline, _options);
            TraceStoragePolicyReadGuard.RequireVerified(database, verification, deadline, _options);
            AuditChainDatabase.RequireFullQualificationCycleVerification(database, verification,
                deadline, options);
            if (_options.PlcCommunication is not null)
                AuditChainDatabase.RequireFullPlcCommunicationVerification(database, verification,
                    deadline, _options.PlcCommunication);
            var rows = SqliteCommandStore.ReadQualificationCycleRows(database, options, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            return new(true, "QualificationCycleHistoryAvailable", rows);
        }
        finally
        {
            if (!committed) try { raw.sqlite3_exec(database, "ROLLBACK;"); } catch { }
        }
    }

    private static QualificationCycleEvent? LatestPending(
        IEnumerable<QualificationCycleEvent> source, Guid? sessionId = null)
    {
        // ProtocolRequestRejected is an observation about a rejected request;
        // it does not advance or resolve an existing run.  Project lifecycle
        // state per RunId so a rejection (or a later AckReset for another run)
        // cannot hide an older unresolved run in the same session.
        return source.Where(value => value.RunId is not null &&
                value.Kind != QualificationCycleEventKind.ProtocolRequestRejected &&
                (sessionId is null || value.SessionId == sessionId.Value))
            .GroupBy(value => value.RunId!.Value)
            .Select(group => group.OrderBy(value => value.Position).Last())
            .Where(value => value.Kind != QualificationCycleEventKind.AckReset)
            .OrderBy(value => value.Position)
            .LastOrDefault();
    }

    private static void ValidateFilter(QualificationCycleHistoryFilter filter)
    {
        if (filter.SessionId == Guid.Empty || filter.RunId == Guid.Empty || filter.AfterPosition < 0 ||
            filter.ThroughPosition is < 0 || filter.ThroughPosition < filter.AfterPosition ||
            filter.PageSize is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(filter));
    }

    private static QualificationCycleHistoryPage Empty(bool available, string reason) => new(
        available, reason, Array.Empty<QualificationCycleEvent>(), 0, null);

    private sealed record Snapshot(bool Available, string ReasonCode,
        IReadOnlyList<QualificationCycleStoredRow> Rows);
}
