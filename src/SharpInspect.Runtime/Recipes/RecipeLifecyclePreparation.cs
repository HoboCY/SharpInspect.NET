using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Recipes;

/// <summary>All heads are captured and verified in the same authoritative writer transaction.</summary>
internal sealed record RecipeLifecycleCommandState(bool Enabled,
    IReadOnlyList<RecipeDraftRevision> DraftHistory, IReadOnlyList<RecipeReleaseRecord> Releases,
    IReadOnlyList<RecipeActivationRecord> Activations, IReadOnlyList<RecipeLifecycleRecord> Lifecycle,
    IReadOnlyList<RecipeSelectionRevision> Selections)
{
    internal RecipeActivationRecord? Current => RecipeLifecycleProjection.EffectiveCurrent(Activations, Lifecycle);
}

internal sealed record RecipeLifecycleMutation(RecipeLifecycleRecord Record);
