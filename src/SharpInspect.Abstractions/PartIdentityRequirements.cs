namespace SharpInspect.Abstractions;

public enum PartIdentityRequirementMode : byte { None = 1, Optional = 2, Required = 3 }

/// <summary>
/// An explicit portable Recipe declaration. A declaration never supplies a station provider,
/// a part value, freshness evidence, or permission to accept a production trigger.
/// </summary>
public sealed record PartIdentityRequirement
{
    public PartIdentityRequirement(PartIdentityRequirementMode mode, string? logicalRole = null,
        RecipeContractReference? format = null)
    {
        if (!Enum.IsDefined(typeof(PartIdentityRequirementMode), mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == PartIdentityRequirementMode.None)
        {
            if (logicalRole is not null || format is not null)
                throw new ArgumentException("PartIdentityNoneCannotBindSourceOrFormat");
        }
        else
        {
            logicalRole = AlgorithmContractValidation.Identifier(logicalRole!, nameof(logicalRole));
            ArgumentNullException.ThrowIfNull(format);
        }
        Mode = mode;
        LogicalRole = logicalRole;
        Format = format;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-part-identity-requirement-v1", mode.ToString(), logicalRole,
            format?.Id, format?.Version, format?.ContentHash
        });
    }

    public static PartIdentityRequirement None { get; } = new(PartIdentityRequirementMode.None);
    public PartIdentityRequirementMode Mode { get; }
    public string? LogicalRole { get; }
    public RecipeContractReference? Format { get; }
    public string ContentHash { get; }
}
