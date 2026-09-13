namespace SharpInspect.Abstractions;

/// <summary>V1 geometric calibration contracts. A global scalar pixel scale is not a profile kind.</summary>
public enum CalibrationKind { Intrinsic = 0, PlanarHomography = 1 }

/// <summary>
/// Portable Recipe dependency. It names the geometric kind, logical purpose and exact
/// acceptance policy; it contains no station profile, physical identity or coefficients.
/// </summary>
// 这里冻结的是配方依赖的契约引用；设备配置、物理身份和系数由 Runtime 侧另行解析。
public sealed record CalibrationRequirement
{
    public CalibrationRequirement(string logicalCameraRole, CalibrationKind kind,
        string logicalPurpose, RecipeContractReference coefficientContract,
        RecipeContractReference acceptancePolicy)
    {
        LogicalCameraRole = AlgorithmConfigurationValidation.Identifier(logicalCameraRole,
            nameof(logicalCameraRole));
        Kind = AlgorithmConfigurationValidation.Enum(kind, nameof(kind));
        LogicalPurpose = AlgorithmConfigurationValidation.Identifier(logicalPurpose,
            nameof(logicalPurpose));
        CoefficientContract = coefficientContract ?? throw new ArgumentNullException(nameof(coefficientContract));
        AcceptancePolicy = acceptancePolicy ?? throw new ArgumentNullException(nameof(acceptancePolicy));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-requirement-v1", LogicalCameraRole, Kind.ToString(),
            LogicalPurpose, CoefficientContract.Id, CoefficientContract.Version, CoefficientContract.ContentHash,
            AcceptancePolicy.Id, AcceptancePolicy.Version, AcceptancePolicy.ContentHash
        });
    }

    public string LogicalCameraRole { get; }
    public CalibrationKind Kind { get; }
    public string LogicalPurpose { get; }
    public RecipeContractReference CoefficientContract { get; }
    public RecipeContractReference AcceptancePolicy { get; }
    public string ContentHash { get; }
}
