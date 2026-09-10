using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>Bounded, read-only access to the schema-25 trace policy ledger.</summary>
public sealed class SqliteTraceStoragePolicyQuery : ITraceStoragePolicyHistoryQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(4, 4);
    private static int _outstanding;

    public SqliteTraceStoragePolicyQuery(ProductionStoreOptions options)
    { _options = options ?? throw new ArgumentNullException(nameof(options)); }

    public async ValueTask<TraceStoragePolicyReadResult> ReadAsync(long? version = null,
        CancellationToken cancellationToken = default)
    {
        if (version is < 1)
            return new(false, "TraceStoragePolicyVersionInvalid");
        try
        {
            return await ReadCoreAsync((database, deadline) =>
                SqliteCommandStore.ReadTraceStoragePolicyState(database,
                    _options.TraceStoragePolicies!, deadline, version), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception,
            "TraceStoragePolicyHistoryUnavailable")); }
    }

    public async ValueTask<TraceStoragePolicyHistoryPage> QueryAsync(
        TraceStoragePolicyFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.AfterVersion < 0 || filter.ThroughVersion is < 0 ||
            filter.ThroughVersion < filter.AfterVersion || filter.PageSize is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(filter));
        try
        {
            return await ReadCoreAsync((database, deadline) =>
            {
                var latest = AuditChainDatabase.Scalar(database,
                    "SELECT COALESCE(MAX(Version),0) FROM trace_storage_policy_events;", deadline);
                var through = filter.ThroughVersion ?? latest;
                AuditChainDatabase.Require(through <= latest,
                    "TraceStoragePolicyHistoryThroughVersionInvalid");
                var publications = SqliteCommandStore.ReadTraceStoragePolicyPublications(
                    database, _options.TraceStoragePolicies!, deadline);
                var selected = publications.Where(value => value.Version > filter.AfterVersion &&
                        value.Version <= through)
                    .Take(filter.PageSize + 1).ToArray();
                var page = selected.Take(filter.PageSize).ToArray();
                return new TraceStoragePolicyHistoryPage(true,
                    page.Length == 0 ? "TraceStoragePolicyHistoryEmpty" : "TraceStoragePolicyHistoryAvailable",
                    Array.AsReadOnly(page), through,
                    selected.Length > filter.PageSize ? page[^1].Version : null);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception,
                "TraceStoragePolicyHistoryUnavailable"), Array.Empty<TraceStoragePolicyPublication>(), 0, null); }
    }

    private async ValueTask<T> ReadCoreAsync<T>(
        Func<sqlite3, StoreDeadline, T> read, CancellationToken cancellationToken)
    {
        if (_options.TraceStoragePolicies is null)
            throw new InvalidOperationException("TraceStoragePolicyConfigurationRequired");
        _options.TraceStoragePolicies.Validate();
        if (_options.QueryTimeout < TimeSpan.FromMilliseconds(1) ||
            _options.QueryTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(_options.QueryTimeout));
        var deadline = new StoreDeadline(_options.QueryTimeout);
        if (Interlocked.Increment(ref _outstanding) > 64)
        {
            Interlocked.Decrement(ref _outstanding);
            throw new InvalidOperationException("TraceStoragePolicyQueryCapacityExceeded");
        }
        var entered = false;
        try
        {
            entered = await Slots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!entered)
                throw new InvalidOperationException("TraceStoragePolicyQueryCapacityExceeded");
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
        using var connection = SqliteNative.Open(path, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, _options);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        var committed = false;
        try
        {
            var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
            if (schema is not (TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion))
                throw new InvalidOperationException(schema > TraceStoragePolicyStoreOptions.SchemaVersion
                    ? "TraceStoragePolicyGovernedMigrationRequired"
                    : "TraceStoragePolicyConfigurationRequired");
            SqliteNative.ConfigureSqliteLimit(database, _options, schema);
            SqliteCommandStore.VerifyTraceStoragePolicyReadGuard(database, _options, deadline);
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
        var policy = _options.AuditIntegrityPolicy;
        if (policy is null || !policy.RequireExternalAnchor)
            return;
        var anchor = _options.ExternalAuditAnchor ?? throw new InvalidOperationException(
            "AuditRequiredAnchorUnavailable");
        if (checkpoint is null)
            throw new InvalidOperationException("AuditCheckpointMissing");
        var remaining = deadline.Remaining;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("TraceStoragePolicyQueryDeadlineExceeded");
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
}
