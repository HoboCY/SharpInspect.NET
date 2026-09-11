using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Recipes;

internal sealed record RecipeSelectionPreparation(long? ReleaseHighWatermark,
    RecipeContractReference? PlcContract, IReadOnlyList<RecipeSelectionValidatedRelease> Validations, string? Failure)
{
    internal static RecipeSelectionPreparation Rejected(string reason) =>
        new(null, null, Array.Empty<RecipeSelectionValidatedRelease>(), reason);
}
