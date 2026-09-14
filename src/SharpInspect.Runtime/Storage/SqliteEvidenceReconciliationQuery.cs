using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed record EvidenceReconciliationReadState(EvidenceReconciliationConfiguration Configuration,
    IReadOnlyList<EvidenceReconciliationStoredRow> Rows, EvidenceReconciliationReplay Replay, long AuditSequence,
    string AuditHash, long PayloadBytes);

/// <summary>Bounded, read-only reconciliation history from one verified SQLite snapshot.</summary>
public sealed partial class SqliteEvidenceReconciliationQuery : IEvidenceReconciliationQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private static int _outstanding;

    public SqliteEvidenceReconciliationQuery(ProductionStoreOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<EvidenceReconciliationSnapshot> ReadAsync(EvidenceReconciliationFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (_options.EvidenceReconciliation is not { } options)
            return Unavailable("EvidenceReconciliationConfigurationRequired");
        if (filter.AfterPosition < 0 || filter.PageSize is < 1 or > 512 ||
            filter.PageSize > options.MaximumPageSize || filter.RunId == Guid.Empty)
            return Unavailable("EvidenceReconciliationPageInvalid");
        try
        {
            return await ReadVerifiedAsync((_, _, state) =>
            {
                if (filter.AfterPosition > state.Rows.Count)
                    throw new InvalidOperationException("EvidenceReconciliationCursorInvalid");
                var selected = state.Rows.Where(x => x.Position > filter.AfterPosition &&
                    (filter.RunId is null || x.Payload.RunId == filter.RunId)).Take(filter.PageSize + 1).ToArray();
                var records = selected.Take(filter.PageSize).Select(x => x.ToRecord()).ToArray();
                return new EvidenceReconciliationSnapshot(true, "EvidenceReconciliationHistoryAvailable",
                    state.AuditSequence, records, records.LastOrDefault()?.Position ?? filter.AfterPosition,
                    selected.Length > filter.PageSize, state.Replay.Latest(EvidenceReconciliationPhase.Startup)?.Project(),
                    state.Replay.Latest(EvidenceReconciliationPhase.HistoricalScrub)?.Project(),
                    state.Replay.PendingQuarantines.Count, state.Replay.IntegrityFault);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Unavailable(SqliteAuditIntegrityQuery.FaultReason(error, "EvidenceReconciliationQueryUnavailable"));
        }
    }

    internal ValueTask<EvidenceReconciliationReadState> ReadStateAsync(CancellationToken cancellationToken = default) =>
        ReadVerifiedAsync((_, _, state) => state, cancellationToken);

    internal async ValueTask<T> ReadVerifiedAsync<T>(Func<sqlite3, StoreDeadline, EvidenceReconciliationReadState, T> select,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _outstanding) > 32)
        {
            Interlocked.Decrement(ref _outstanding);
            throw new InvalidOperationException("EvidenceReconciliationQueryCapacityExceeded");
        }
        var entered = false;
        try
        {
            var deadline = new StoreDeadline(_options.QueryTimeout);
            entered = await Slots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!entered) throw new TimeoutException("EvidenceReconciliationQueryDeadlineExceeded");
            // Retain the physical slot until the SQLite call really exits, including cancellation.
            return await Task.Run(() => ReadDatabase(select, deadline, cancellationToken), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            if (entered) Slots.Release();
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private T ReadDatabase<T>(Func<sqlite3, StoreDeadline, EvidenceReconciliationReadState, T> select,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        if (_options.EvidenceReconciliation is null)
            throw new InvalidOperationException("EvidenceReconciliationConfigurationRequired");
        if (!StoragePathValidator.TryValidate(_options, out var path, out var reason))
            throw new InvalidOperationException(reason);
        using var connection = SqliteNative.Open(path, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, _options);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        try
        {
            SqliteCommandStore.VerifyEvidenceReconciliationReadGuard(database, _options, deadline);
            var configuration = SqliteCommandStore.ReadEvidenceReconciliationConfiguration(database, deadline);
            var rows = SqliteCommandStore.ReadEvidenceReconciliationRows(database, configuration, deadline);
            var replay = new EvidenceReconciliationReplay();
            foreach (var row in rows) replay.Apply(row);
            var tail = AuditChainDatabase.Tail(database, deadline);
            var result = select(database, deadline, new(configuration, rows, replay, tail.Sequence, tail.Hash,
                rows.Sum(x => (long)EvidenceReconciliationStorageCodec.Encode(x.Payload, configuration.MaximumPayloadBytes).Length)));
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            return result;
        }
        finally { try { raw.sqlite3_exec(database, "ROLLBACK;"); } catch { } }
    }

    private static EvidenceReconciliationSnapshot Unavailable(string reason) =>
        new(false, reason, 0, Array.Empty<EvidenceReconciliationRecord>(), 0, false, null, null, 0, false);
}

internal sealed partial class SqliteCommandStore
{
    internal static void VerifyEvidenceReconciliationReadGuard(sqlite3 database, ProductionStoreOptions options,
        StoreDeadline deadline)
    {
        RequireConfiguredEvidenceReconciliation(database, options, deadline);
        var policy = options.AuditIntegrityPolicy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        using var key = WindowsMachineAuditKey.Open(policy, false, out _);
        var report = AuditChainDatabase.Verify(database, policy, key.KeyId, key.PublicKeyBase64,
            new AuditVerificationRequest(0, policy.MaximumVerificationEntries), startup: false, deadline,
            validateAnchorReceipt: false, archiveOptions: options.AlgorithmResultArchive,
            recipeDraftOptions: options.RecipeDrafts, cameraSetupOptions: options.CameraSetup,
            cameraRecoveryOptions: options.CameraRecovery, cameraNetworkOptions: options.CameraNetwork,
            imagingSetupOptions: options.ImagingSetup, calibrationSessionOptions: options.CalibrationSessions,
            governanceOptions: options.CalibrationGovernance, releaseOptions: options.RecipeReleases,
            contractOptions: options.PlcResultContracts, activationOptions: options.RecipeActivations,
            previewOptions: options.PreviewSessions, importOptions: options.CalibrationImports,
            manualOptions: options.ManualInspections, productionAdmissionOptions: options.ProductionAdmission,
            stationQualificationOptions: options.StationQualifications, recipeTransferOptions: options.RecipeTransfers,
            traceStoragePolicyOptions: options.TraceStoragePolicies, qualificationCycleOptions: options.QualificationCycles,
            plcCommunicationOptions: options.PlcCommunication, productionInspectionOptions: options.ProductionInspections,
            productionRecoveryOptions: options.ProductionRecovery, partIdentityOptions: options.PartIdentities,
            recipeSelectionOptions: options.RecipeSelections, productionArmOptions: options.ProductionArming,
            recipeLifecycleOptions: options.RecipeLifecycle, imageEvidenceOptions: options.ImageEvidence,
            imageFinalizationOptions: options.ImageFinalization, productionOutboxOptions: options.Outbox);
        AuditChainDatabase.Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == AuditChainDatabase.Tail(database, deadline).Sequence,
            "EvidenceReconciliationVerificationBudgetExceeded");
    }
}
