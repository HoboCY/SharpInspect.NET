using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Recipes;

internal sealed class RecipeReleaseService : IRecipeReleaseService
{
    private readonly RecipeDraftService _drafts;
    private readonly LocalAuthorizationService _authorization;
    private readonly IReleasedRecipeQuery _history;
    private readonly ProductionStoreOptions _options;
    private readonly Func<ValueTask<StationStateSnapshot>> _readStation;
    private int _active;

    internal RecipeReleaseService(RecipeDraftService drafts, LocalAuthorizationService authorization,
        IReleasedRecipeQuery history, ProductionStoreOptions options,
        Func<ValueTask<StationStateSnapshot>> readStation)
    {
        _drafts = drafts; _authorization = authorization; _history = history; _options = options;
        _readStation = readStation;
    }

    public ValueTask<RecipeReleaseAccess> GetAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken = default) =>
        _authorization.GetRecipeReleaseAccessAsync(invocation, cancellationToken);

    public ValueTask<ReleasedRecipeReadResult> ReadAsync(RecipeReference reference, CancellationToken cancellationToken = default) =>
        _history.ReadAsync(reference, cancellationToken);

    public ValueTask<ReleasedRecipePage> QueryAsync(ReleasedRecipeFilter filter, CancellationToken cancellationToken = default) =>
        _history.QueryAsync(filter, cancellationToken);

    public async ValueTask<RecipeReleaseResult> ReleaseAsync(ReleaseRecipeCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.CorrelationId == Guid.Empty || command.Invocation is null)
            return new(new(command.CorrelationId, CommandDisposition.Rejected, "InvalidCommandContext",
                AuditPersistence.NotAttempted, Guid.NewGuid()));
        var entered = Interlocked.Increment(ref _active) <= 2;
        if (!entered) Interlocked.Decrement(ref _active);
        try
        {
            var preparation = new RecipeReleasePreparation(null, entered ? null : "RecipeReleaseCapacityExceeded");
            try
            {
                if (preparation.Failure is null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var access = await GetAccessAsync(command.Invocation, cancellationToken).ConfigureAwait(false);
                    if (!access.CanRelease) preparation = new(null, access.ReasonCode);
                    else
                    {
                        var read = await _drafts.ReadAsync(command.DraftId, command.ExpectedRevision, cancellationToken).ConfigureAwait(false);
                        if (!read.Available || read.Revision is not { } source)
                            preparation = new(null, read.ReasonCode);
                        else if (source.RevisionContentHash != command.ExpectedRevisionContentHash)
                            preparation = new(null, "RecipeReleaseDraftRevisionConflict");
                        else
                        {
                            var validation = await _drafts.ValidateAsync(source.Content, cancellationToken).ConfigureAwait(false);
                            preparation = validation.Valid
                                ? new(new(source.DraftId, source.Revision, source.RevisionContentHash), null)
                                : new(null, validation.ReasonCode);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { preparation = new(null, "RecipeReleaseCancelled"); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { preparation = new(null, "RecipeReleaseValidationUnavailable"); }

            // Reading an immutable station snapshot neither changes its state nor calls a device.
            // The command audit uses the real Runtime epoch, not an invented authoring epoch.
            var station = await _readStation().ConfigureAwait(false);
            if (station.Lifecycle == RuntimeLifecycle.Stopped)
                preparation = new(null, "RuntimeStopped");
            return await _authorization.ReleaseRecipeAsync(command, station.RuntimeEpoch, preparation,
                new StoreDeadline(_options.CommitTimeout), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(new(command.CorrelationId, CommandDisposition.Rejected, "RecipeReleaseUnavailable",
                AuditPersistence.Unavailable, Guid.NewGuid()));
        }
        finally { if (entered) Interlocked.Decrement(ref _active); }
    }
}
