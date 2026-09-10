using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;

namespace SharpInspect.Runtime.Manual;

internal sealed record ManualRecipeExecutionPlan(ManualRecipeSelection Selection,
    RecipeDraftRevision Draft, RecipeReleaseRecord? Release)
{
    internal RecipeDraftContent Content => Draft.Content;
}

/// <summary>Uses the authoritative immutable source and the ordinary recipe validator.</summary>
internal sealed class ManualRecipeResolver
{
    private readonly RecipeDraftService drafts;
    private readonly IReleasedRecipeQuery? releases;

    internal ManualRecipeResolver(RecipeDraftService drafts, IReleasedRecipeQuery? releases)
    { this.drafts = drafts; this.releases = releases; }

    internal async ValueTask<(ManualRecipeExecutionPlan? Plan, string ReasonCode)> ResolveAsync(
        ManualRecipeSelection selection, CancellationToken token)
    {
        RecipeDraftRevision draft;
        RecipeReleaseRecord? release = null;
        if (selection.Kind == ManualRecipeSourceKind.Draft)
        {
            var read = await drafts.ReadAsync(selection.DraftId!.Value, selection.DraftRevision, token)
                .ConfigureAwait(false);
            if (!read.Available || read.Revision is null) return (null, read.ReasonCode);
            draft = read.Revision;
            if (ManualRecipeSelection.FromDraft(draft) != selection) return (null, "ManualDraftConflict");
        }
        else
        {
            if (releases is null || selection.Recipe is null) return (null, "ManualReleasedRecipeUnavailable");
            var read = await releases.ReadAsync(selection.Recipe, token).ConfigureAwait(false);
            if (!read.Available || read.Recipe is not { Available: true } recipe)
                return (null, read.ReasonCode);
            if (ManualRecipeSelection.FromReleased(recipe) != selection) return (null, "ManualReleasedRecipeConflict");
            release = recipe.Record;
            draft = release.Source;
        }
        var validation = await drafts.ValidateAsync(draft.Content, token).ConfigureAwait(false);
        return validation.Valid
            ? (new(selection, draft, release), "ManualRecipeValidated")
            : (null, validation.ReasonCode);
    }
}
