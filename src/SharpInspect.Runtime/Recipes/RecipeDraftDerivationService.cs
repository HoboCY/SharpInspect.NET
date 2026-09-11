using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Recipes;

internal sealed class RecipeDraftDerivationService : IRecipeDraftDerivationService
{
    private readonly RecipeDraftService _drafts;
    private readonly IRecipeLifecycleHistoryQuery _history;

    internal RecipeDraftDerivationService(RecipeDraftService drafts, IRecipeLifecycleHistoryQuery history)
    { _drafts = drafts; _history = history; }

    public async ValueTask<RecipeDraftSaveResult> DeriveAsync(RecipeDraftDerivationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RecipeDraftSaveResult Refuse(string reason) => new(false, reason, null, Array.Empty<AlgorithmValidationIssue>());
        try
        {
            var access = await _drafts.GetAccessAsync(request.Invocation, cancellationToken).ConfigureAwait(false);
            if (!access.CanSave) return Refuse(access.ReasonCode);
            var page = await _history.QueryAsync(new(AfterPosition: request.Source.Position - 1,
                ThroughPosition: request.Source.Position, PageSize: 1), cancellationToken).ConfigureAwait(false);
            if (!page.Available) return Refuse(page.ReasonCode);
            var transition = page.Records.SingleOrDefault(value => value.Reference == request.Source);
            if (transition is null) return Refuse("RecipeDraftLifecycleSourceMissing");
            if (request.NewDraftId == transition.SourceDraft.DraftId) return Refuse("RecipeDraftDerivationRequiresNewIdentity");
            var read = await _drafts.ReadAsync(transition.SourceDraft.DraftId, transition.SourceDraft.Revision,
                cancellationToken).ConfigureAwait(false);
            if (!read.Available || read.Revision is not { } source) return Refuse(read.ReasonCode);
            if (source.RevisionContentHash != transition.SourceDraft.RevisionContentHash ||
                source.Content.ContentHash != transition.SourceContentHash) return Refuse("RecipeDraftLifecycleSourceMismatch");
            var old = source.Content;
            var content = new RecipeDraftContent(new RecipeDraftLifecycleLineage(transition), null,
                old.RecipeKey, old.DisplayName, old.Algorithm, old.Configuration, old.CameraRole, old.Camera,
                old.AlgorithmExecutionTimeout, old.AssetRequirements, old.PolicyRequirements, old.ValueOrigins,
                old.CameraProviderExtension, old.CalibrationRequirements, old.PartIdentityRequirement);
            // Ordinary Save rechecks current schema/semantic policy and authenticated
            // authoring. The writer also proves this exact preserved origin, forbids
            // identity reuse and preserves the lineage across all subsequent edits.
            return await _drafts.SaveAsync(new(request.OperationId, request.NewDraftId, 0, null, content,
                request.Reason, request.Invocation, request.Invocation.StepUpGrantId), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return Refuse("RecipeDraftDerivationCancelled"); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Refuse("RecipeDraftDerivationUnavailable"); }
    }
}

internal static class RecipeLifecycleSources
{
    internal static async ValueTask<RecipeDraftLifecycleLineage?> ResolveForMigrationAsync(
        IRecipeLifecycleHistoryQuery? query, RecipeDraftRevision source, bool required, CancellationToken token)
    {
        if (query is null)
        {
            if (required) throw new InvalidOperationException("RecipeLifecycleHistoryUnavailable");
            return source.Content.LifecycleLineage;
        }
        RecipeLifecycleRecord? origin = null;
        long after = 0;
        long? through = null;
        do
        {
            var page = await query.QueryAsync(new(DraftId: source.DraftId, AfterPosition: after,
                ThroughPosition: through, PageSize: 100), token).ConfigureAwait(false);
            if (!page.Available) throw new InvalidOperationException(page.ReasonCode);
            through ??= page.ThroughPosition;
            foreach (var candidate in page.Records.Where(value => value.SourceDraft.Revision == source.Revision &&
                value.SourceDraft.RevisionContentHash == source.RevisionContentHash &&
                value.SourceContentHash == source.Content.ContentHash))
            {
                if (origin is not null) throw new InvalidOperationException("RecipeDraftLifecycleSourceAmbiguous");
                origin = candidate;
            }
            if (page.NextAfterPosition is not { } next) break;
            if (next <= after) throw new InvalidOperationException("RecipeLifecycleHistoryCursorInvalid");
            after = next;
        } while (true);
        return origin is null ? source.Content.LifecycleLineage : new RecipeDraftLifecycleLineage(origin);
    }
}
