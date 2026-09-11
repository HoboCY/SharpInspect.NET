using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>Independent, bounded read-only verification of Recipe transfer governance and provenance.</summary>
public sealed class SqliteRecipeTransferQuery : IRecipeTransferHistoryQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(4, 4);
    private static int _outstanding;

    public SqliteRecipeTransferQuery(ProductionStoreOptions options)
    { _options = options ?? throw new ArgumentNullException(nameof(options)); }

    public async ValueTask<RecipeTrustReadResult> ReadTrustAsync(long? version = null,
        CancellationToken cancellationToken = default)
    {
        if (version is < 1) return new(false, "RecipeTransferTrustVersionInvalid");
        try
        {
            return await ReadAsync((database, deadline) =>
            {
                var trust = version is null
                    ? SqliteCommandStore.ReadRecipeTransferState(database, _options.RecipeTransfers!, deadline).Trust
                    : SqliteCommandStore.ReadRecipeTransferTrustVersion(database, _options.RecipeTransfers!, version, deadline);
                return new RecipeTrustReadResult(version is null || trust is not null,
                    trust is null ? "RecipeTransferTrustNotFound" : "RecipeTransferTrustAvailable", trust);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, Reason(exception)); }
    }

    public async ValueTask<RecipeSigningKeysReadResult> ReadSigningKeysAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReadAsync((database, deadline) => new RecipeSigningKeysReadResult(true,
                "RecipeTransferSigningKeysAvailable", Array.AsReadOnly(SqliteCommandStore.ReadRecipeTransferState(
                    database, _options.RecipeTransfers!, deadline).SigningKeys.ToArray())), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, Reason(exception), Array.Empty<RecipeSigningKeyRecord>()); }
    }

    public async ValueTask<RecipeImportReadResult> ReadImportAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        if (draftId == Guid.Empty) return new(false, "RecipeTransferDraftIdRequired");
        try
        {
            return await ReadAsync((database, deadline) =>
            {
                var provenance = SqliteCommandStore.ReadRecipeTransferState(database,
                    _options.RecipeTransfers!, deadline).Imports.SingleOrDefault(item => item.DraftId == draftId);
                return new RecipeImportReadResult(provenance is not null,
                    provenance is null ? "RecipeTransferImportNotFound" : "RecipeTransferImportAvailable", provenance);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, Reason(exception)); }
    }

    public async ValueTask<RecipeTransferHistoryPage> QueryAsync(RecipeTransferFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.AfterPosition < 0 || filter.ThroughPosition is < 0 ||
            filter.ThroughPosition < filter.AfterPosition || filter.PageSize is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(filter));
        try
        {
            return await ReadAsync((database, deadline) =>
            {
                var latest = AuditChainDatabase.Scalar(database,
                    "SELECT COALESCE(MAX(Position),0) FROM recipe_transfer_events;", deadline);
                var through = filter.ThroughPosition ?? latest;
                AuditChainDatabase.Require(through <= latest, "RecipeTransferHistoryThroughPositionInvalid");
                var rows = AuditChainDatabase.Read(database, @"
                    SELECT Position,Kind,OperationId,PrincipalId,SessionId,SubjectHash,ContentHash,RecordedAtUtc
                    FROM recipe_transfer_events WHERE Position>? AND Position<=? ORDER BY Position LIMIT ?;",
                    deadline, statement => new RecipeTransferHistoryRecord(SqliteNative.ColumnInt64(statement, 0),
                        (RecipeTransferEventKind)SqliteNative.ColumnInt64(statement, 1),
                        Guid.ParseExact(SqliteNative.ColumnText(statement, 2)!, "D"),
                        Guid.ParseExact(SqliteNative.ColumnText(statement, 3)!, "D"),
                        Guid.ParseExact(SqliteNative.ColumnText(statement, 4)!, "D"),
                        SqliteNative.ColumnText(statement, 5)!, SqliteNative.ColumnText(statement, 6)!,
                        DateTimeOffset.ParseExact(SqliteNative.ColumnText(statement, 7)!, "O", CultureInfo.InvariantCulture)),
                    filter.AfterPosition.ToString(CultureInfo.InvariantCulture), through.ToString(CultureInfo.InvariantCulture),
                    (filter.PageSize + 1).ToString(CultureInfo.InvariantCulture));
                var selected = rows.Take(filter.PageSize).ToArray();
                return new RecipeTransferHistoryPage(true, "RecipeTransferHistoryAvailable", Array.AsReadOnly(selected),
                    through, rows.Count > filter.PageSize ? selected[^1].Position : null);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, Reason(exception), Array.Empty<RecipeTransferHistoryRecord>(), 0, null); }
    }

    private async ValueTask<T> ReadAsync<T>(Func<sqlite3, StoreDeadline, T> read, CancellationToken cancellationToken)
    {
        if (_options.RecipeTransfers is null) throw new InvalidOperationException("RecipeTransferConfigurationRequired");
        _options.RecipeTransfers.Validate();
        if (_options.QueryTimeout < TimeSpan.FromMilliseconds(1) || _options.QueryTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(_options.QueryTimeout));
        var deadline = new StoreDeadline(_options.QueryTimeout);
        if (Interlocked.Increment(ref _outstanding) > 64)
        {
            Interlocked.Decrement(ref _outstanding);
            throw new InvalidOperationException("RecipeTransferQueryCapacityExceeded");
        }
        var entered = false;
        try
        {
            entered = await Slots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!entered) throw new InvalidOperationException("RecipeTransferQueryCapacityExceeded");
            var snapshot = await Task.Run(() => ReadCore(read, deadline, cancellationToken), CancellationToken.None).ConfigureAwait(false);
            await VerifyExternalAnchorAsync(snapshot.Checkpoint, deadline, cancellationToken).ConfigureAwait(false);
            return snapshot.Result;
        }
        finally { if (entered) Slots.Release(); Interlocked.Decrement(ref _outstanding); }
    }

    private (T Result, AuditCheckpoint? Checkpoint) ReadCore<T>(Func<sqlite3, StoreDeadline, T> read,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        if (!StoragePathValidator.TryValidate(_options, out var path, out var reason)) throw new InvalidOperationException(reason);
        using var connection = SqliteNative.Open(path, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, _options);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        var committed = false;
        try
        {
            var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
            RecipeTransferReadGuard.RequireConfiguration(schema, _options);
            TraceStoragePolicyReadGuard.RequireConfiguration(schema, _options);
            AuditChainDatabase.Require(schema is RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion, "StoreSchemaTooNew");
            SqliteNative.ConfigureSqliteLimit(database, _options, schema);
            SqliteCommandStore.VerifyRecipeTransferReadGuard(database, _options, deadline);
            SqliteNative.EnsureDeadline(deadline, cancellationToken);
            var result = read(database, deadline);
            var checkpoint = AuditChainDatabase.LatestCheckpoint(database, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            return (result, checkpoint);
        }
        finally { if (!committed) try { raw.sqlite3_exec(database, "ROLLBACK;"); } catch { } }
    }

    private async ValueTask VerifyExternalAnchorAsync(AuditCheckpoint? checkpoint, StoreDeadline deadline,
        CancellationToken cancellationToken)
    {
        var policy = _options.AuditIntegrityPolicy!;
        if (!policy.RequireExternalAnchor) return;
        var anchor = _options.ExternalAuditAnchor ?? throw new InvalidOperationException("AuditRequiredAnchorUnavailable");
        if (checkpoint is null) throw new InvalidOperationException("AuditCheckpointMissing");
        var remaining = deadline.Remaining;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("RecipeTransferQueryDeadlineExceeded");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(remaining);
        var anchorTimeout = remaining < policy.AnchorTimeout ? remaining : policy.AnchorTimeout;
        var latest = await AuditAnchorClient.InvokeAsync(anchor,
            token => anchor.ReadLatestAsync(policy.StationId, token), anchorTimeout, lifetime.Token).ConfigureAwait(false);
        AuditChainDatabase.Require(latest is not null && AuditChainDatabase.ReceiptMatches(policy, checkpoint, latest),
            "AuditExternalAnchorMismatch");
    }

    private static string Reason(Exception exception) =>
        SqliteAuditIntegrityQuery.FaultReason(exception, "RecipeTransferHistoryUnavailable");
}
