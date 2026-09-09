namespace SharpInspect.Abstractions;

public enum RecipeGovernanceMode { SingleApproverRelease, MakerCheckerRelease }

/// <summary>An explicit immutable deployment choice; absence never selects a less restrictive mode.</summary>
public sealed class RecipeGovernancePolicy
{
    public RecipeGovernancePolicy(string id, string version, RecipeGovernanceMode mode)
    {
        Id = AlgorithmConfigurationValidation.Identifier(id, nameof(id));
        Version = AlgorithmConfigurationValidation.Identifier(version, nameof(version));
        Mode = AlgorithmConfigurationValidation.Enum(mode, nameof(mode));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-governance-policy-v1", Id, Version, Mode.ToString()
        });
        Reference = new(Id, Version, ContentHash);
    }

    public string Id { get; }
    public string Version { get; }
    public RecipeGovernanceMode Mode { get; }
    public string ContentHash { get; }
    public RecipeContractReference Reference { get; }
}
