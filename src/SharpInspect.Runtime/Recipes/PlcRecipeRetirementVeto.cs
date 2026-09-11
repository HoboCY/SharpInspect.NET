using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Recipes;

/// <summary>
/// Closed, deny-only lifecycle fixture for isolated protocol checks. This is not a
/// lifecycle authority: an absent match never admits a release. T48 must bind real
/// retirement facts inside the trusted selection/activation storage transaction.
/// </summary>
internal sealed class PlcRecipeRetirementVeto
{
    private readonly HashSet<(RecipeReference Recipe, Guid ReleaseId, string Hash)> _retired;
    internal PlcRecipeRetirementVeto(IEnumerable<RecipeSelectionMapEntry> retired)
    {
        ArgumentNullException.ThrowIfNull(retired);
        _retired = retired.Select(entry => (entry.Recipe, entry.ReleaseId, entry.ReleaseRecordContentHash)).ToHashSet();
    }
    internal bool IsRetired(RecipeSelectionMapEntry target) =>
        _retired.Contains((target.Recipe, target.ReleaseId, target.ReleaseRecordContentHash));
}
