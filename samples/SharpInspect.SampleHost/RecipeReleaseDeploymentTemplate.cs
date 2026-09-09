using SharpInspect.Abstractions;

namespace SharpInspect.SampleHost;

/// <summary>
/// The sample's explicit release-store deployment choices.  The command-line
/// consumer selects one named, versioned policy; an absent release option does
/// not create a release store at all.
/// </summary>
internal static class RecipeReleaseDeploymentTemplate
{
    internal static RecipeGovernancePolicy SingleApproverRelease { get; } =
        new("Sample.RecipeRelease.SingleApprover", "1", RecipeGovernanceMode.SingleApproverRelease);

    internal static RecipeGovernancePolicy MakerCheckerRelease { get; } =
        new("Sample.RecipeRelease.MakerChecker", "1", RecipeGovernanceMode.MakerCheckerRelease);

    internal static RecipeGovernancePolicy Select(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            throw new ArgumentException("RecipeReleaseGovernanceModeRequired", nameof(mode));
        if (string.Equals(mode, "single", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "single-approver", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "single-approver-release", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, nameof(RecipeGovernanceMode.SingleApproverRelease), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, SingleApproverRelease.Id, StringComparison.OrdinalIgnoreCase))
            return SingleApproverRelease;
        if (string.Equals(mode, "maker-checker", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "makerchecker", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "maker-checker-release", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, nameof(RecipeGovernanceMode.MakerCheckerRelease), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, MakerCheckerRelease.Id, StringComparison.OrdinalIgnoreCase))
            return MakerCheckerRelease;
        throw new ArgumentException("RecipeReleaseGovernanceModeInvalid", nameof(mode));
    }
}
