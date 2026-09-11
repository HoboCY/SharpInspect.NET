using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Recipes;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>Read-only lifecycle projection from one fully verified database snapshot.</summary>
public sealed class SqliteRecipeLifecycleQuery : IRecipeLifecycleHistoryQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(4, 4);
    private static int _outstanding;

    public SqliteRecipeLifecycleQuery(ProductionStoreOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<RecipeDraftLifecycleReadResult> ReadDraftAsync(Guid draftId,
        CancellationToken cancellationToken = default)
    {
        if (draftId == Guid.Empty) return new(false, "RecipeDraftIdentityRequired");
        var read = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (read.State is not { } state) return new(false, read.Reason);
        if (!state.DraftHistory.Any(value => value.DraftId == draftId)) return new(false, "RecipeDraftNotFound");
        var transition = RecipeLifecycleProjection.Abandonment(state.Lifecycle, draftId);
        return new(true, transition is null ? "RecipeDraftOpen" : "RecipeDraftAbandoned",
            transition is null ? RecipeDraftLifecycleState.Open : RecipeDraftLifecycleState.Abandoned, transition);
    }

    public async ValueTask<ReleasedRecipeLifecycleReadResult> ReadReleaseAsync(RecipeReference recipe, Guid releaseId,
        string releaseRecordContentHash, CancellationToken cancellationToken = default)
    {
        if (recipe is null || releaseId == Guid.Empty) return new(false, "RecipeReleaseIdentityRequired");
        var read = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (read.State is not { } state) return new(false, read.Reason);
        if (!state.Releases.Any(value => value.Recipe == recipe && value.ReleaseId == releaseId &&
            value.ContentHash == releaseRecordContentHash)) return new(false, "RecipeLifecycleExactReleaseMissing");
        var transition = RecipeLifecycleProjection.Retirement(state.Lifecycle, recipe, releaseId, releaseRecordContentHash);
        return new(true, transition is null ? "RecipeReleaseAvailable" : "RecipeRetired",
            transition is null ? ReleasedRecipeLifecycleState.Available : ReleasedRecipeLifecycleState.Retired, transition);
    }

    public async ValueTask<RecipeLifecyclePage> QueryAsync(RecipeLifecycleFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.AfterPosition < 0 || filter.ThroughPosition is < 0 || filter.ThroughPosition < filter.AfterPosition ||
            filter.PageSize is < 1 or > 200 || filter.Kind is { } kind && !Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(filter));
        var read = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (read.State is not { } state) return new(false, read.Reason, Array.Empty<RecipeLifecycleRecord>(), 0, null);
        var tail = state.Lifecycle.LastOrDefault()?.Position ?? 0;
        if (filter.ThroughPosition > tail) return new(false, "RecipeLifecycleCursorInvalid", Array.Empty<RecipeLifecycleRecord>(), 0, null);
        var through = filter.ThroughPosition ?? tail;
        var selected = state.Lifecycle.Where(value => value.Position > filter.AfterPosition && value.Position <= through &&
                (filter.Kind is null || value.Kind == filter.Kind) &&
                (filter.DraftId is null || value.SourceDraft.DraftId == filter.DraftId) &&
                (filter.ReleaseId is null || value.ReleaseId == filter.ReleaseId))
            .OrderBy(value => value.Position).Take(filter.PageSize + 1).ToArray();
        var page = selected.Take(filter.PageSize).ToArray();
        return new(true, "RecipeLifecycleHistoryVerified", Array.AsReadOnly(page), through,
            selected.Length > filter.PageSize ? page[^1].Position : null);
    }

    private async ValueTask<(RecipeLifecycleCommandState? State, string Reason)> ReadAsync(CancellationToken token)
    {
        if (_options.RecipeLifecycle is null) return (null, "RecipeLifecycleConfigurationRequired");
        if (Interlocked.Increment(ref _outstanding) > 64)
        {
            Interlocked.Decrement(ref _outstanding);
            return (null, "RecipeLifecycleQueryCapacityExceeded");
        }
        var entered = false;
        try
        {
            var deadline = new StoreDeadline(_options.QueryTimeout);
            entered = await Slots.WaitAsync(deadline.Remaining, token).ConfigureAwait(false);
            if (!entered) return (null, "RecipeLifecycleQueryDeadlineExceeded");
            var read = await Task.Run(() => ReadCore(deadline, token), CancellationToken.None).ConfigureAwait(false);
            if (_options.AuditIntegrityPolicy is { RequireExternalAnchor: true } policy)
            {
                var anchor = _options.ExternalAuditAnchor ?? throw new InvalidOperationException("AuditRequiredAnchorUnavailable");
                if (read.Checkpoint is null) throw new InvalidOperationException("AuditCheckpointMissing");
                SqliteNative.EnsureDeadline(deadline, token);
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
                lifetime.CancelAfter(deadline.Remaining);
                var timeout = deadline.Remaining < policy.AnchorTimeout ? deadline.Remaining : policy.AnchorTimeout;
                var latest = await AuditAnchorClient.InvokeAsync(anchor,
                    value => anchor.ReadLatestAsync(policy.StationId, value), timeout, lifetime.Token).ConfigureAwait(false);
                AuditChainDatabase.Require(latest is not null && AuditChainDatabase.ReceiptMatches(policy, read.Checkpoint, latest),
                    "AuditExternalAnchorMismatch");
            }
            token.ThrowIfCancellationRequested();
            return (read.State, "RecipeLifecycleHistoryVerified");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return (null, SqliteAuditIntegrityQuery.FaultReason(exception, "RecipeLifecycleQueryUnavailable")); }
        finally
        {
            if (entered) Slots.Release();
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private (RecipeLifecycleCommandState State, AuditCheckpoint? Checkpoint) ReadCore(StoreDeadline deadline, CancellationToken token)
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
            var state = SqliteCommandStore.ReadRecipeLifecycleCommandState(database, _options, deadline);
            var checkpoint = AuditChainDatabase.LatestCheckpoint(database, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, token);
            committed = true;
            return (state, checkpoint);
        }
        finally { if (!committed) try { raw.sqlite3_exec(database, "ROLLBACK;"); } catch { } }
    }
}
