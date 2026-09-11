using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Recipes;

internal sealed class RecipeLifecycleService : IRecipeLifecycleService
{
    private readonly IRecipeLifecycleHistoryQuery _history;
    private readonly IRecipeActivationQuery? _activations;
    private readonly LocalAuthorizationService _authorization;
    private readonly ProductionStoreOptions _options;
    private readonly RecipeLifecycleRuntimeOptions _runtimeOptions;
    private readonly Func<ValueTask<StationStateSnapshot>> _readStation;
    private readonly Func<RetireReleasedRecipeCommand, CancellationToken, ValueTask<RecipeRetirementRuntimeLease>> _reserve;
    private int _active;

    internal RecipeLifecycleService(IRecipeLifecycleHistoryQuery history, IRecipeActivationQuery? activations,
        LocalAuthorizationService authorization, ProductionStoreOptions options,
        RecipeLifecycleRuntimeOptions runtimeOptions, Func<ValueTask<StationStateSnapshot>> readStation,
        Func<RetireReleasedRecipeCommand, CancellationToken, ValueTask<RecipeRetirementRuntimeLease>> reserve)
    {
        runtimeOptions.Validate();
        _history = history; _activations = activations; _authorization = authorization;
        _options = options; _runtimeOptions = runtimeOptions; _readStation = readStation; _reserve = reserve;
    }

    public ValueTask<RecipeLifecycleAccess> GetAccessAsync(CommandInvocation invocation, RecipeLifecycleKind kind,
        CancellationToken cancellationToken = default) => _options.RecipeLifecycle is null
        ? ValueTask.FromResult(new RecipeLifecycleAccess(false, "RecipeLifecycleConfigurationRequired"))
        : _authorization.GetRecipeLifecycleAccessAsync(invocation, kind, cancellationToken);

    public ValueTask<RecipeDraftLifecycleReadResult> ReadDraftAsync(Guid draftId,
        CancellationToken cancellationToken = default) => _history.ReadDraftAsync(draftId, cancellationToken);

    public ValueTask<ReleasedRecipeLifecycleReadResult> ReadReleaseAsync(RecipeReference recipe, Guid releaseId,
        string releaseRecordContentHash, CancellationToken cancellationToken = default) =>
        _history.ReadReleaseAsync(recipe, releaseId, releaseRecordContentHash, cancellationToken);

    public ValueTask<RecipeLifecyclePage> QueryAsync(RecipeLifecycleFilter filter,
        CancellationToken cancellationToken = default) => _history.QueryAsync(filter, cancellationToken);

    public ValueTask<RecipeLifecycleResult> AbandonAsync(AbandonRecipeDraftCommand command,
        CancellationToken cancellationToken = default) => ApplyAsync(command, cancellationToken);

    public ValueTask<RecipeLifecycleResult> RetireAsync(RetireReleasedRecipeCommand command,
        CancellationToken cancellationToken = default) => ApplyAsync(command, cancellationToken);

    private async ValueTask<RecipeLifecycleResult> ApplyAsync(RuntimeCommand command, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.CorrelationId == Guid.Empty || command.Invocation is null)
            return new(new(command.CorrelationId, CommandDisposition.Rejected, "InvalidCommandContext",
                AuditPersistence.NotAttempted, Guid.NewGuid()));
        var entered = Interlocked.Increment(ref _active) <= 2;
        if (!entered) Interlocked.Decrement(ref _active);
        RecipeRetirementRuntimeLease? runtime = null;
        RecipeLifecycleResult? durable = null;
        Guid epoch = Guid.Empty;
        async ValueTask<RecipeLifecycleResult> RefuseAsync(string reason)
        {
            var refused = await _authorization.ApplyRecipeLifecycleAsync(command, epoch, null, null, reason,
                new StoreDeadline(_options.CommitTimeout), token).ConfigureAwait(false);
            runtime?.PublishTerminal(refused.Outcome.ReasonCode, false);
            return refused;
        }
        try
        {
            var station = await _readStation().ConfigureAwait(false);
            epoch = station.RuntimeEpoch;
            if (station.Lifecycle == RuntimeLifecycle.Stopped) return await RefuseAsync("RuntimeStopped").ConfigureAwait(false);
            if (!entered) return await RefuseAsync("RecipeLifecycleCapacityExceeded").ConfigureAwait(false);
            if (_options.RecipeLifecycle is null)
                return await RefuseAsync("RecipeLifecycleConfigurationRequired").ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // Access presentation alone is insufficient. Check the actual command's
            // fresh, exact-bound Step-Up before it may obtain stopping authority.
            if (await _authorization.PrecheckRecipeLifecycleAsync(command, token).ConfigureAwait(false) is { } denial)
                return await RefuseAsync(denial).ConfigureAwait(false);

            if (command is RetireReleasedRecipeCommand retire)
            {
                var release = await _history.ReadReleaseAsync(retire.Recipe, retire.ReleaseId,
                    retire.ReleaseRecordContentHash, token).ConfigureAwait(false);
                if (!release.Available) return await RefuseAsync(release.ReasonCode).ConfigureAwait(false);
                if (release.State != ReleasedRecipeLifecycleState.Available)
                    return await RefuseAsync("RecipeRetired").ConfigureAwait(false);
                RecipeActivationReadResult? active = null;
                if (_activations is not null)
                {
                    active = await _activations.ReadCurrentAsync(token).ConfigureAwait(false);
                    if (!active.Available) return await RefuseAsync(active.ReasonCode).ConfigureAwait(false);
                }
                if (active?.Record?.Reference != retire.ExpectedActive)
                    return await RefuseAsync("RecipeRetirementActiveChanged").ConfigureAwait(false);
                if (active?.Record?.ReleaseId == retire.ReleaseId)
                {
                    runtime = await _reserve(retire, token).ConfigureAwait(false);
                    if (!runtime.Available) return await RefuseAsync(runtime.Failure!).ConfigureAwait(false);
                    epoch = runtime.RuntimeEpoch;
                    if (await runtime.DrainAsync(_runtimeOptions.QuiescenceTimeout, token).ConfigureAwait(false) is { } failure)
                        return await RefuseAsync(failure).ConfigureAwait(false);
                }
            }

            if (runtime is null)
                return await _authorization.ApplyRecipeLifecycleAsync(command, epoch, null, null, null,
                    new StoreDeadline(_options.CommitTimeout), token).ConfigureAwait(false);

            var deadline = new StoreDeadline(_options.CommitTimeout);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, runtime.Token);
            while (!deadline.Expired)
            {
                string? failure;
                using (var commit = await runtime.EnterCommitAsync(cancellation.Token).ConfigureAwait(false))
                {
                    failure = commit.GetBlocker();
                    if (failure is null)
                    {
                        var result = await _authorization.ApplyRecipeLifecycleAsync(command, epoch, runtime.Active,
                            commit.TryBeginCommit, null, deadline, cancellation.Token).ConfigureAwait(false);
                        if (result.Record is not null && result.Outcome.Disposition == CommandDisposition.Accepted)
                        {
                            durable = result;
                            var cleared = runtime.ClearCommitted(result.Record);
                            commit.Dispose();
                            var cleanup = cleared ? await runtime.CleanupAsync(_runtimeOptions.CleanupTimeout)
                                .ConfigureAwait(false) : "RecipeRetirementActiveClearFailed";
                            runtime.PublishTerminal(cleanup ?? result.Outcome.ReasonCode, cleanup is not null);
                            return result with { RuntimeRecoveryRequired = cleanup is not null, CleanupReasonCode = cleanup };
                        }
                        failure = result.Outcome.ReasonCode;
                        if (failure != "RecipeRetirementRuntimeBusy" || result.Outcome.Audit != AuditPersistence.NotAttempted)
                        {
                            runtime.PublishTerminal(failure, false);
                            return result;
                        }
                    }
                }
                if (failure != "RecipeRetirementRuntimeBusy") return await RefuseAsync(failure!).ConfigureAwait(false);
                // Only contention is retryable, after both SQLite/session and the
                // command gate have been released. Durable facts are never retried.
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation.Token).ConfigureAwait(false);
            }
            return await RefuseAsync("RecipeRetirementCommitDeadlineExceeded").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (durable is not null)
            {
                runtime?.PublishTerminal("RecipeRetirementPostCommitFault", true);
                return durable with { RuntimeRecoveryRequired = true, CleanupReasonCode = "RecipeRetirementPostCommitFault" };
            }
            var reason = token.IsCancellationRequested || runtime?.Token.IsCancellationRequested == true
                ? "RecipeLifecycleCancelled" : "RecipeLifecyclePreparationUnavailable";
            if (epoch != Guid.Empty) return await RefuseAsync(reason).ConfigureAwait(false);
            return new(new(command.CorrelationId, CommandDisposition.Rejected, reason,
                AuditPersistence.Unavailable, Guid.NewGuid()));
        }
        finally
        {
            runtime?.Dispose();
            if (entered) Interlocked.Decrement(ref _active);
        }
    }
}
