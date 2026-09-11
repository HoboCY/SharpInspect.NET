using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>Read-only access to the signed schema-27 PLC communication ledger.</summary>
public sealed class SqlitePlcCommunicationHistoryQuery : IPlcCommunicationHistoryQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(4, 4);
    private static int _outstanding;

    public SqlitePlcCommunicationHistoryQuery(ProductionStoreOptions options)
    { _options = options ?? throw new ArgumentNullException(nameof(options)); }

    public async ValueTask<PlcCommunicationHistoryReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReadCoreAsync((database, deadline) =>
            {
                var rows = SqliteCommandStore.ReadPlcCommunicationRows(
                    database, _options.PlcCommunication!, deadline);
                var latest = rows.Count == 0 ? null : rows[^1].Event;
                return new PlcCommunicationHistoryReadResult(true,
                    latest is null ? "PlcCommunicationHistoryEmpty" : "PlcCommunicationHistoryAvailable",
                    latest, RecoveryRequired(rows));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception,
            "PlcCommunicationHistoryUnavailable")); }
    }

    public async ValueTask<PlcCommunicationHistoryPage> QueryAsync(
        PlcCommunicationHistoryFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.AfterPosition < 0 || filter.ThroughPosition is < 0 ||
            filter.ThroughPosition < filter.AfterPosition || filter.PageSize is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(filter));
        if (filter.EndpointBindingHash is not null)
            _ = QualificationContractValidation.Hash(filter.EndpointBindingHash,
                nameof(filter.EndpointBindingHash));
        if (filter.RecoveryCycleId == Guid.Empty || filter.QualificationSessionId == Guid.Empty ||
            filter.RunId == Guid.Empty)
            throw new ArgumentException("PlcCommunicationHistoryFilterInvalid", nameof(filter));
        try
        {
            return await ReadCoreAsync((database, deadline) =>
            {
                var rows = SqliteCommandStore.ReadPlcCommunicationRows(
                    database, _options.PlcCommunication!, deadline);
                var latest = rows.Count == 0 ? 0 : rows[^1].Position;
                var through = filter.ThroughPosition ?? latest;
                AuditChainDatabase.Require(through <= latest,
                    "PlcCommunicationHistoryThroughPositionInvalid");
                // Freeze all derived state at the caller's high-water mark. A
                // later row, even for another endpoint or recovery cycle, must
                // not change the result of this historical query.
                var scoped = rows.Where(row => row.Position <= through &&
                        MatchesFilter(row.Event, filter)).ToArray();
                var selected = scoped.Where(row => row.Position > filter.AfterPosition)
                    .Take(filter.PageSize + 1).Select(row => row.Event).ToArray();
                var page = selected.Take(filter.PageSize).ToArray();
                return new PlcCommunicationHistoryPage(true,
                    page.Length == 0 ? "PlcCommunicationHistoryEmpty" : "PlcCommunicationHistoryAvailable",
                    page, through, selected.Length > filter.PageSize ? page[^1].Position : null,
                    RecoveryRequired(scoped));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception,
                "PlcCommunicationHistoryUnavailable"), Array.Empty<PlcCommunicationEvent>(), 0, null); }
    }

    private async ValueTask<T> ReadCoreAsync<T>(
        Func<sqlite3, StoreDeadline, T> read, CancellationToken cancellationToken)
    {
        if (_options.PlcCommunication is null)
            throw new InvalidOperationException("PlcCommunicationConfigurationRequired");
        if (_options.AuditIntegrityPolicy is null || _options.LocalIdentity is null)
            throw new InvalidOperationException("PlcCommunicationRequiresIdentityAndAudit");
        _options.PlcCommunication.Validate();
        if (_options.QueryTimeout < TimeSpan.FromMilliseconds(1) ||
            _options.QueryTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(_options.QueryTimeout));
        var deadline = new StoreDeadline(_options.QueryTimeout);
        if (Interlocked.Increment(ref _outstanding) > 64)
        {
            Interlocked.Decrement(ref _outstanding);
            throw new InvalidOperationException("PlcCommunicationQueryCapacityExceeded");
        }
        var entered = false;
        try
        {
            entered = await Slots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!entered)
                throw new InvalidOperationException("PlcCommunicationQueryCapacityExceeded");
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
            if (schema is not (PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion))
                throw new InvalidOperationException(schema > ProductionRecoveryStoreOptions.SchemaVersion
                    ? "PlcCommunicationGovernedMigrationRequired"
                    : "PlcCommunicationConfigurationRequired");
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
                productionArmOptions: _options.ProductionArming, recipeSelectionOptions: _options.RecipeSelections, recipeLifecycleOptions: _options.RecipeLifecycle);
            AuditChainDatabase.RequireFullPlcCommunicationVerification(database, verification,
                deadline, _options.PlcCommunication);
            SqliteNative.EnsureDeadline(deadline, cancellationToken);
            var result = read(database, deadline);
            var checkpoint = AuditChainDatabase.LatestCheckpoint(database, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            VerifyExternalAnchor(checkpoint, deadline, cancellationToken);
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

    private void VerifyExternalAnchor(AuditCheckpoint? checkpoint, StoreDeadline deadline,
        CancellationToken cancellationToken)
    {
        var policy = _options.AuditIntegrityPolicy!;
        if (!policy.RequireExternalAnchor)
            return;
        var anchor = _options.ExternalAuditAnchor ?? throw new InvalidOperationException(
            "AuditRequiredAnchorUnavailable");
        if (checkpoint is null)
            throw new InvalidOperationException("AuditCheckpointMissing");
        var remaining = deadline.Remaining;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("PlcCommunicationQueryDeadlineExceeded");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(remaining);
        var timeout = remaining < policy.AnchorTimeout ? remaining : policy.AnchorTimeout;
        var latest = AuditAnchorClient.InvokeAsync(anchor,
            token => anchor.ReadLatestAsync(policy.StationId, token), timeout, lifetime.Token)
            .GetAwaiter().GetResult();
        AuditChainDatabase.Require(latest is not null &&
            AuditChainDatabase.ReceiptMatches(policy, checkpoint, latest),
            "AuditExternalAnchorMismatch");
    }

    private static bool MatchesFilter(PlcCommunicationEvent value,
        PlcCommunicationHistoryFilter filter) =>
        (filter.EndpointBindingHash is null ||
            value.EndpointBindingHash == filter.EndpointBindingHash) &&
        (filter.RecoveryCycleId is null || value.RecoveryCycleId == filter.RecoveryCycleId) &&
        (filter.QualificationSessionId is null ||
            value.QualificationSessionId == filter.QualificationSessionId) &&
        (filter.RunId is null || value.RunId == filter.RunId);

    private readonly record struct RecoveryKey(string EndpointBindingHash,
        Guid RuntimeEpoch, Guid? RecoveryCycleId);

    private static bool RecoveryRequired(IReadOnlyList<PlcCommunicationStoredRow> rows)
    {
        var pending = new HashSet<RecoveryKey>();
        foreach (var row in rows.OrderBy(value => value.Position))
        {
            var value = row.Event;
            var key = new RecoveryKey(value.EndpointBindingHash, value.RuntimeEpoch,
                value.RecoveryCycleId);
            switch (value.Kind)
            {
                case PlcCommunicationEventKind.HeartbeatStale:
                case PlcCommunicationEventKind.ControllerEpochChanged:
                case PlcCommunicationEventKind.CommunicationLost:
                case PlcCommunicationEventKind.ReconnectFailed:
                case PlcCommunicationEventKind.RecoveryExhausted:
                    pending.Add(key);
                    break;
                case PlcCommunicationEventKind.RecoveryCompleted:
                    pending.Remove(key);
                    break;
            }
        }
        return pending.Count != 0;
    }
}
