using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Recipes;

/// <summary>
/// Projections of verified immutable history. These functions confer no storage,
/// authorization, activation or migration authority.
/// </summary>
internal static class RecipeLifecycleProjection
{
    internal static RecipeLifecycleRecord? Retirement(IReadOnlyList<RecipeLifecycleRecord> history,
        RecipeReference recipe, Guid releaseId, string releaseRecordContentHash)
    {
        var transition = history.SingleOrDefault(value => value.Kind == RecipeLifecycleKind.ReleasedRetired &&
            value.ReleaseId == releaseId);
        if (transition is not null && (transition.Recipe != recipe ||
                transition.ReleaseRecordContentHash != releaseRecordContentHash))
            throw new InvalidOperationException("RecipeLifecycleReleaseIdentityMismatch");
        return transition;
    }

    internal static RecipeLifecycleRecord? Abandonment(IReadOnlyList<RecipeLifecycleRecord> history, Guid draftId) =>
        history.SingleOrDefault(value => value.Kind == RecipeLifecycleKind.DraftAbandoned &&
            value.SourceDraft.DraftId == draftId);

    internal static RecipeActivationRecord? EffectiveCurrent(IReadOnlyList<RecipeActivationRecord> activations,
        IReadOnlyList<RecipeLifecycleRecord> history)
    {
        // Choose the latest successful selection FIRST. Its explicit retirement is
        // an empty barrier; filtering retired records before Last would revive an
        // older selection without a new authorized activation.
        var latest = activations.Where(value => value.CanBeActive).OrderBy(value => value.Position).LastOrDefault();
        if (latest is null) return null;
        var retired = Retirement(history, latest.Candidate, latest.ReleaseId, latest.ReleaseRecordContentHash);
        if (retired is null) return latest;
        if (retired.ClearedActive != latest.Reference)
            throw new InvalidOperationException("RecipeLifecycleRetiredActivationConflict");
        return null;
    }

    internal static RecipeActivationRecord? CurrentForEvidence(IReadOnlyList<RecipeActivationRecord> activations,
        IReadOnlyList<RecipeLifecycleRecord> history, RecipeActivationEvidenceKind evidenceKind)
    {
        var latest = activations.Where(value => value.Outcome.Succeeded && value.EvidenceKind == evidenceKind)
            .OrderBy(value => value.Position).LastOrDefault();
        if (latest is null) return null;
        return Retirement(history, latest.Candidate, latest.ReleaseId, latest.ReleaseRecordContentHash) is null
            ? latest : null;
    }

    internal static IReadOnlyList<RecipeRetirementMapImpact> MapImpacts(
        IReadOnlyList<RecipeSelectionRevision> selections, RecipeReference recipe, Guid releaseId,
        string releaseRecordContentHash) => selections.Where(value => value.Map is not null)
        .Select(value => (Revision: value, Codes: value.Map!.Entries.Where(entry => entry.Recipe == recipe &&
                entry.ReleaseId == releaseId && entry.ReleaseRecordContentHash == releaseRecordContentHash)
            .Select(entry => entry.SelectionCode).ToArray()))
        .Where(value => value.Codes.Length != 0)
        .Select(value => new RecipeRetirementMapImpact(value.Revision.Reference,
            value.Revision.Map!.Reference, value.Codes)).ToArray();
}
