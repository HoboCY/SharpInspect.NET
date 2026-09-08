using System.Collections.ObjectModel;
using System.Text;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>Bounded, read-only query over the signed schema 8 development archive.</summary>
public sealed class SqliteAlgorithmResultQuery : IAlgorithmResultQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(4, 4);
    private static int _outstanding;

    public SqliteAlgorithmResultQuery(ProductionStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async ValueTask<AlgorithmResultPage> QueryAsync(AlgorithmResultFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();
        if (filter.AfterPosition < 0 || filter.ThroughPosition is < 0 ||
            filter.ThroughPosition < filter.AfterPosition || filter.PageSize is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(filter));
        if (_options.AlgorithmResultArchive is null)
            return Empty(false, "AlgorithmResultArchiveConfigurationRequired");
        if (_options.AuditIntegrityPolicy is null || _options.LocalIdentity is null)
            return Empty(false, "AlgorithmResultArchiveRequiresIdentityAndAudit");
        if (filter.Correlation is { Kind: ExecutionKind.Production })
            return Empty(false, "AlgorithmResultProductionCorrelationRejected");
        if (Interlocked.Increment(ref _outstanding) > 64)
        {
            Interlocked.Decrement(ref _outstanding);
            return Empty(false, "AlgorithmResultQueryCapacityExceeded");
        }

        var entered = false;
        try
        {
            var deadline = new StoreDeadline(_options.QueryTimeout);
            entered = await Slots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!entered) return Empty(false, "AlgorithmResultQueryDeadlineExceeded");
            var query = await Task.Run(() => QueryCore(filter, deadline, cancellationToken), CancellationToken.None)
                .ConfigureAwait(false);
            if (!query.Page.Available)
                return query.Page;
            if (_options.AuditIntegrityPolicy.RequireExternalAnchor)
            {
                var anchor = _options.ExternalAuditAnchor ??
                    throw new InvalidOperationException("AuditRequiredAnchorUnavailable");
                var checkpoint = query.Checkpoint ??
                    throw new InvalidOperationException("AuditCheckpointMissing");
                var remaining = deadline.Remaining;
                if (remaining <= TimeSpan.Zero)
                    return Empty(false, "AlgorithmResultQueryDeadlineExceeded");
                using var anchorLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                anchorLifetime.CancelAfter(remaining);
                var anchorTimeout = remaining < _options.AuditIntegrityPolicy.AnchorTimeout
                    ? remaining : _options.AuditIntegrityPolicy.AnchorTimeout;
                var latest = await AuditAnchorClient.InvokeAsync(anchor,
                    token => anchor.ReadLatestAsync(_options.AuditIntegrityPolicy.StationId, token),
                    anchorTimeout, anchorLifetime.Token).ConfigureAwait(false);
                AuditChainDatabase.Require(latest is not null &&
                    AuditChainDatabase.ReceiptMatches(_options.AuditIntegrityPolicy, checkpoint, latest),
                    "AuditExternalAnchorMismatch");
            }
            return query.Page;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Empty(false, SqliteAuditIntegrityQuery.FaultReason(ex, "AlgorithmResultHistoryUnavailable"));
        }
        finally
        {
            if (entered) Slots.Release();
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private QueryResult QueryCore(AlgorithmResultFilter filter, StoreDeadline deadline,
        CancellationToken cancellationToken)
    {
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        if (!StoragePathValidator.TryValidate(_options, out var path, out var pathReason))
            return new QueryResult(Empty(false, pathReason), null);
        using var key = WindowsMachineAuditKey.Open(_options.AuditIntegrityPolicy!, false, out _);
        using var connection = SqliteNative.Open(path, readOnly: true);
        if (_options.RecipeDrafts is not null)
            RecipeDraftStoreOptions.ConfigureSqliteLimit(connection.Handle!);
        else if (_options.AlgorithmResultArchive is not null)
            AlgorithmResultArchiveOptions.ConfigureSqliteLimit(connection.Handle!);
        else if (_options.CameraRecovery is not null)
            CameraRecoveryStoreOptions.ConfigureSqliteLimit(connection.Handle!);
        else if (_options.CameraSetup is not null)
            CameraSetupStoreOptions.ConfigureSqliteLimit(connection.Handle!);
        var database = connection.Handle!;
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        var committed = false;
        try
        {
            var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
            if (schema is CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion)
                AuditChainDatabase.Require(_options.CameraSetup is not null, "CameraSetupConfigurationRequired");
            else if (schema < CameraSetupStoreOptions.SchemaVersion)
                AuditChainDatabase.Require(_options.CameraSetup is null, "CameraSetupGovernedMigrationRequired");
            if (_options.CameraRecovery is not null && schema < CameraRecoveryStoreOptions.SchemaVersion)
                throw new InvalidOperationException("CameraRecoveryGovernedMigrationRequired");
            if (_options.CameraRecovery is null && schema == CameraRecoveryStoreOptions.SchemaVersion)
                throw new InvalidOperationException("CameraRecoveryConfigurationRequired");
            if (_options.AlgorithmResultArchive is null)
                throw new InvalidOperationException(schema == AlgorithmResultArchiveOptions.SchemaVersion
                    ? "AlgorithmResultArchiveConfigurationRequired"
                    : "AlgorithmResultArchiveGovernedMigrationRequired");
            AuditChainDatabase.Require(schema is AlgorithmResultArchiveOptions.SchemaVersion or
                RecipeDraftStoreOptions.SchemaVersion or CameraSetupStoreOptions.SchemaVersion or
                CameraRecoveryStoreOptions.SchemaVersion,
                schema < AlgorithmResultArchiveOptions.SchemaVersion
                    ? "AlgorithmResultArchiveGovernedMigrationRequired" : "StoreSchemaTooNew");
            if (schema == RecipeDraftStoreOptions.SchemaVersion)
                AuditChainDatabase.Require(_options.RecipeDrafts is not null,
                    "RecipeDraftConfigurationRequired");
            if (schema == AlgorithmResultArchiveOptions.SchemaVersion)
                AuditChainDatabase.Require(_options.RecipeDrafts is null,
                    "RecipeDraftGovernedMigrationRequired");
            var policy = _options.AuditIntegrityPolicy!;
            var verification = AuditChainDatabase.Verify(database, policy, key.KeyId, key.PublicKeyBase64,
                new AuditVerificationRequest(0, policy.MaximumVerificationEntries), false, deadline,
                validateAnchorReceipt: false, archiveOptions: _options.AlgorithmResultArchive,
                recipeDraftOptions: _options.RecipeDrafts, cameraSetupOptions: _options.CameraSetup,
                cameraRecoveryOptions: _options.CameraRecovery);
            AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
            if (_options.RecipeDrafts is not null)
                AuditChainDatabase.RequireFullRecipeDraftVerification(database, verification, deadline,
                    _options.RecipeDrafts);
            if (_options.CameraSetup is not null)
                AuditChainDatabase.RequireFullCameraSetupVerification(database, verification, deadline,
                    _options.CameraSetup);
            if (_options.CameraRecovery is not null)
                AuditChainDatabase.RequireFullCameraRecoveryVerification(database, verification, deadline,
                    _options.CameraRecovery);

            var latest = AuditChainDatabase.Scalar(database,
                "SELECT COALESCE(MAX(Position),0) FROM development_algorithm_results;", deadline);
            if (filter.ThroughPosition is { } requested && requested > latest)
                throw new InvalidOperationException("AlgorithmResultCursorInvalid");
            var through = filter.ThroughPosition ?? latest;
            if (through < filter.AfterPosition)
                throw new InvalidOperationException("AlgorithmResultCursorInvalid");

            var rows = ReadPage(database, filter, through, _options.AlgorithmResultArchive!, deadline,
                cancellationToken);
            var records = new List<AlgorithmResultRecord>(Math.Min(rows.Count, filter.PageSize));
            var hasNext = rows.Count > 0 && rows[^1].IsContinuation;
            if (hasNext)
                rows.RemoveAt(rows.Count - 1);
            foreach (var row in rows)
            {
                var binding = SqliteCommandStore.ReadAuditBindingPayload(database, row.Position, deadline);
                _ = SqliteCommandStore.ReadAndValidate(database, row.Position, binding, deadline,
                    _options.AlgorithmResultArchive);
                records.Add(AlgorithmResultStorageCodec.Decode(row.Position, ParseTime(row.RecordedAtUtc),
                    row.PayloadJson!, row.PayloadHash));
            }

            long? next = hasNext ? records[^1].Position : null;
            var checkpoint = AuditChainDatabase.LatestCheckpoint(database, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            return new QueryResult(new AlgorithmResultPage(true, "AlgorithmResultHistoryVerified",
                new ReadOnlyCollection<AlgorithmResultRecord>(records), through, next), checkpoint);
        }
        finally
        {
            if (!committed)
                try { raw.sqlite3_exec(database, "ROLLBACK;"); } catch { }
        }
    }

    private static List<ArchiveQueryRow> ReadPage(SQLitePCL.sqlite3 database,
        AlgorithmResultFilter filter, long through, AlgorithmResultArchiveOptions options,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        var correlationClause = filter.Correlation is null ? string.Empty :
            " AND CorrelationKind=? AND CorrelationId=?";
        var sql = @"SELECT Position,RecordedAtUtc,PayloadHash,PayloadJson,
            length(CAST(PayloadJson AS BLOB)) FROM development_algorithm_results
            WHERE Position>? AND Position<=?" + correlationClause +
            " ORDER BY Position LIMIT ?;";
        return SqliteNative.WithStatement(database, sql, deadline, statement =>
        {
            var index = 1;
            SqliteNative.BindInt64(database, statement, index++, filter.AfterPosition);
            SqliteNative.BindInt64(database, statement, index++, through);
            if (filter.Correlation is { } correlation)
            {
                SqliteNative.BindInt(database, statement, index++, (int)correlation.Kind);
                SqliteNative.BindGuid(database, statement, index++, correlation.Value);
            }
            SqliteNative.BindInt(database, statement, index, checked(filter.PageSize + 1));
            var rows = new List<ArchiveQueryRow>();
            var bytes = 0L;
            while (SqliteNative.Step(database, statement, deadline, cancellationToken) == raw.SQLITE_ROW)
            {
                var rowBytes = SqliteNative.ColumnInt64(statement, 4);
                if (rowBytes < 1 || rowBytes > options.MaximumRecordBytes ||
                    rowBytes > options.MaximumPageBytes)
                    throw new InvalidOperationException("AlgorithmResultQueryRecordOversized");
                if (rows.Count >= filter.PageSize || bytes > options.MaximumPageBytes - rowBytes)
                {
                    rows.Add(new ArchiveQueryRow(SqliteNative.ColumnInt64(statement, 0),
                        SqliteNative.ColumnText(statement, 1)!, SqliteNative.ColumnText(statement, 2)!,
                        null, rowBytes, IsContinuation: true));
                    break;
                }

                var payload = SqliteNative.ColumnText(statement, 3)!;
                if (Encoding.UTF8.GetByteCount(payload) != rowBytes)
                    throw new InvalidOperationException("AlgorithmResultPayloadLengthMismatch");
                rows.Add(new ArchiveQueryRow(SqliteNative.ColumnInt64(statement, 0),
                    SqliteNative.ColumnText(statement, 1)!, SqliteNative.ColumnText(statement, 2)!,
                    payload, rowBytes, IsContinuation: false));
                bytes += rowBytes;
            }
            return rows;
        }, cancellationToken);
    }

    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.TryParse(value,
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed :
        throw new InvalidOperationException("AlgorithmResultRecordedAtInvalid");

    private static AlgorithmResultPage Empty(bool available, string reason) =>
        new(available, reason, new ReadOnlyCollection<AlgorithmResultRecord>(Array.Empty<AlgorithmResultRecord>()), 0, null);

    private sealed record QueryResult(AlgorithmResultPage Page, AuditCheckpoint? Checkpoint);

    private sealed record ArchiveQueryRow(long Position, string RecordedAtUtc, string PayloadHash,
        string? PayloadJson, long PayloadBytes, bool IsContinuation);
}
