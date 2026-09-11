using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed record RecipeSelectionAuthoritySnapshot(RecipeSelectionRevision? Current,
    IReadOnlyList<RecipeReleaseRecord> Releases, long ReleaseHighWatermark, PlcResultContractRevision? PlcContract);
internal sealed record RecipeChangeInterruptedRequest(RecipeChangeRequestEvidence Request,
    RecipeChangeHistoryEvent? Decision, bool FaultRecorded, RecipeActivationRecord? Activation);

/// <summary>Bounded, read-only access to verified selection and dedicated handshake history.</summary>
public sealed class SqliteRecipeSelectionQuery : IRecipeSelectionQuery, IRecipeChangeHistoryQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(4, 4);
    private static int _outstanding;
    public SqliteRecipeSelectionQuery(ProductionStoreOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<RecipeSelectionReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default) =>
        await ReadSelectionAsync(null, cancellationToken).ConfigureAwait(false);
    public ValueTask<RecipeSelectionReadResult> ReadAsync(RecipeSelectionReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return ReadSelectionAsync(reference, cancellationToken);
    }
    private async ValueTask<RecipeSelectionReadResult> ReadSelectionAsync(RecipeSelectionReference? reference, CancellationToken token)
    {
        try
        {
            return await ReadCoreAsync((database, deadline) =>
            {
                var rows = SqliteCommandStore.ReadRecipeSelectionRows(database, _options.RecipeSelections!, deadline);
                var revision = reference is null ? rows.LastOrDefault()?.Revision :
                    rows.SingleOrDefault(value => value.Revision.Reference == reference)?.Revision;
                return reference is not null && revision is null ? new RecipeSelectionReadResult(false, "RecipeSelectionReferenceMissing") :
                    new(true, revision is null ? "RecipeSelectionDefaultLocalOperatorOnly" : "RecipeSelectionAvailable", revision);
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception, "RecipeSelectionHistoryUnavailable")); }
    }

    public async ValueTask<RecipeSelectionPage> QueryAsync(RecipeSelectionFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateFilter(filter.AfterPosition, filter.ThroughPosition, filter.PageSize);
        try
        {
            return await ReadCoreAsync((database, deadline) =>
            {
                var rows = SqliteCommandStore.ReadRecipeSelectionRows(database, _options.RecipeSelections!, deadline);
                var through = filter.ThroughPosition ?? rows.Count;
                if (through > rows.Count) throw new InvalidOperationException("RecipeSelectionThroughPositionInvalid");
                var selected = rows.Where(value => value.Revision.Position > filter.AfterPosition && value.Revision.Position <= through)
                    .Take(filter.PageSize + 1).Select(value => value.Revision).ToArray();
                var page = selected.Take(filter.PageSize).ToArray();
                return new RecipeSelectionPage(true, "RecipeSelectionHistoryAvailable", Array.AsReadOnly(page), through,
                    selected.Length > page.Length ? page[^1].Position : null);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception, "RecipeSelectionHistoryUnavailable"),
            Array.Empty<RecipeSelectionRevision>(), 0, null); }
    }

    public async ValueTask<RecipeChangeHistoryPage> QueryAsync(RecipeChangeHistoryFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateFilter(filter.AfterPosition, filter.ThroughPosition, filter.PageSize);
        if (filter.RequestIdentityHash is { } hash) RecipeActivationValidation.Hash(hash, nameof(filter));
        try
        {
            return await ReadCoreAsync((database, deadline) =>
            {
                var rows = SqliteCommandStore.ReadRecipeChangeRows(database, _options.RecipeSelections!, deadline);
                var through = filter.ThroughPosition ?? rows.Count;
                if (through > rows.Count) throw new InvalidOperationException("RecipeChangeThroughPositionInvalid");
                var selected = rows.Where(value => value.Event.Position > filter.AfterPosition && value.Event.Position <= through &&
                        (filter.RequestIdentityHash is null || value.Event.Request.RequestIdentityHash == filter.RequestIdentityHash))
                    .Take(filter.PageSize + 1).Select(value => value.Event).ToArray();
                var page = selected.Take(filter.PageSize).ToArray();
                return new RecipeChangeHistoryPage(true, "RecipeChangeHistoryAvailable", Array.AsReadOnly(page), through,
                    selected.Length > page.Length ? page[^1].Position : null);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception, "RecipeChangeHistoryUnavailable"),
            Array.Empty<RecipeChangeHistoryEvent>(), 0, null); }
    }

    internal ValueTask<RecipeSelectionAuthoritySnapshot> ReadAuthorityAsync(CancellationToken token) =>
        ReadCoreAsync((database, deadline) =>
        {
            var releases = SqliteCommandStore.ReadRecipeReleaseRows(database, _options.RecipeReleases!, deadline);
            return new RecipeSelectionAuthoritySnapshot(
                SqliteCommandStore.ReadRecipeSelectionRows(database, _options.RecipeSelections!, deadline).LastOrDefault()?.Revision,
                Array.AsReadOnly(releases.Select(value => value.Record).ToArray()), releases.Count,
                SqliteCommandStore.ReadPlcResultContractRows(database, _options.PlcResultContracts!, deadline).LastOrDefault()?.Revision);
        }, token);

    internal ValueTask<IReadOnlyList<RecipeChangeInterruptedRequest>> ReadInterruptedAsync(Guid currentEpoch, CancellationToken token) =>
        ReadCoreAsync<IReadOnlyList<RecipeChangeInterruptedRequest>>((database, deadline) =>
        {
            var changes = SqliteCommandStore.ReadRecipeChangeRows(database, _options.RecipeSelections!, deadline);
            var activations = SqliteCommandStore.ReadRecipeActivationRows(database, _options.RecipeActivations!, deadline,
                SqliteCommandStore.CreateCalibrationProfileResolver(database, _options.CalibrationGovernance, deadline));
            return changes.Select(value => value.Event).GroupBy(value => value.Request.ContentHash)
                .Where(group => group.First().Request.RuntimeEpoch != currentEpoch &&
                    group.Any(value => value.Kind == RecipeChangeEventKind.RequestObserved) &&
                    !group.Any(value => value.Kind == RecipeChangeEventKind.ResetObserved) &&
                    !(group.Any(value => value.Kind == RecipeChangeEventKind.DecisionCommitted) &&
                      group.Any(value => value.Kind == RecipeChangeEventKind.ProtocolFault)))
                .Select(group => new RecipeChangeInterruptedRequest(group.First().Request,
                    group.SingleOrDefault(value => value.Kind == RecipeChangeEventKind.DecisionCommitted),
                    group.Any(value => value.Kind == RecipeChangeEventKind.ProtocolFault),
                    activations.LastOrDefault(value => value.Record.OperationId == group.First().Request.OperationId)?.Record))
                .ToArray();
        }, token);

    private static void ValidateFilter(long after, long? through, int size)
    {
        if (after < 0 || through < after || through is < 0 || size is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(after));
    }

    private async ValueTask<T> ReadCoreAsync<T>(Func<sqlite3, StoreDeadline, T> read, CancellationToken token)
    {
        if (_options.RecipeSelections is null) throw new InvalidOperationException("RecipeSelectionConfigurationRequired");
        _options.RecipeSelections.Validate();
        if (_options.QueryTimeout < TimeSpan.FromMilliseconds(1) || _options.QueryTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(_options.QueryTimeout));
        var deadline = new StoreDeadline(_options.QueryTimeout);
        if (Interlocked.Increment(ref _outstanding) > 64)
        {
            Interlocked.Decrement(ref _outstanding);
            throw new InvalidOperationException("RecipeSelectionQueryCapacityExceeded");
        }
        var entered = false;
        try
        {
            entered = await Slots.WaitAsync(deadline.Remaining, token).ConfigureAwait(false);
            if (!entered) throw new InvalidOperationException("RecipeSelectionQueryCapacityExceeded");
            return await Task.Run(() => ReadDatabase(read, deadline, token), CancellationToken.None).ConfigureAwait(false);
        }
        finally { if (entered) Slots.Release(); Interlocked.Decrement(ref _outstanding); }
    }

    private T ReadDatabase<T>(Func<sqlite3, StoreDeadline, T> read, StoreDeadline deadline, CancellationToken token)
    {
        SqliteNative.EnsureDeadline(deadline, token);
        if (!StoragePathValidator.TryValidate(_options, out var path, out var reason)) throw new InvalidOperationException(reason);
        using var connection = SqliteNative.Open(path, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, _options);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, token);
        var committed = false;
        try
        {
            SqliteCommandStore.VerifyRecipeSelectionReadGuard(database, _options, deadline);
            SqliteNative.EnsureDeadline(deadline, token);
            var result = read(database, deadline);
            var checkpoint = AuditChainDatabase.LatestCheckpoint(database, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, token);
            committed = true;
            VerifyExternalAnchor(checkpoint, deadline, token);
            return result;
        }
        finally { if (!committed) raw.sqlite3_exec(database, "ROLLBACK;"); }
    }

    private void VerifyExternalAnchor(AuditCheckpoint? checkpoint, StoreDeadline deadline, CancellationToken token)
    {
        var policy = _options.AuditIntegrityPolicy!;
        if (!policy.RequireExternalAnchor) return;
        var anchor = _options.ExternalAuditAnchor ?? throw new InvalidOperationException("AuditRequiredAnchorUnavailable");
        if (checkpoint is null) throw new InvalidOperationException("AuditCheckpointMissing");
        var remaining = deadline.Remaining;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("RecipeSelectionQueryDeadlineExceeded");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(remaining);
        var timeout = remaining < policy.AnchorTimeout ? remaining : policy.AnchorTimeout;
        var latest = AuditAnchorClient.InvokeAsync(anchor, cancellation => anchor.ReadLatestAsync(policy.StationId, cancellation),
            timeout, lifetime.Token).GetAwaiter().GetResult();
        AuditChainDatabase.Require(latest is not null && AuditChainDatabase.ReceiptMatches(policy, checkpoint, latest),
            "AuditExternalAnchorMismatch");
    }
}
