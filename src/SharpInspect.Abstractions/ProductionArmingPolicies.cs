namespace SharpInspect.Abstractions;

/// <summary>
/// The immutable versioned deployment choice for the first production arm after startup.
/// Manual Arm is the framework default, and Automatic Arm must be explicitly selected for the
/// deployment. Selecting a mode declares intent only: the policy never arms production, never
/// asserts Ready and never bypasses a current admission gate.
/// </summary>
public enum StartupProductionMode { ManualArm = 1, AutomaticArm = 2 }

/// <summary>
/// An immutable deployment declaration for startup production arming. It carries no authority of
/// its own, and a committed deployment upgrade, rollback or Station Restore still requires a
/// present Human Principal to perform the first Manual Arm.
/// </summary>
public sealed class StartupProductionPolicy
{
    /// <summary>The framework default: Manual Arm.</summary>
    public static StartupProductionPolicy Default { get; } = new("SharpInspect.DefaultStartupProduction",
        "1", StartupProductionMode.ManualArm);

    public StartupProductionPolicy(string id, string version, StartupProductionMode mode)
    {
        Id = AlgorithmConfigurationValidation.Identifier(id, nameof(id));
        Version = AlgorithmConfigurationValidation.Identifier(version, nameof(version));
        Mode = AlgorithmConfigurationValidation.Enum(mode, nameof(mode));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-startup-production-policy-v1", Id, Version, Mode.ToString()
        });
        Reference = new(Id, Version, ContentHash);
    }

    public string Id { get; }
    public string Version { get; }
    public StartupProductionMode Mode { get; }
    public string ContentHash { get; }
    public RecipeContractReference Reference { get; }
}

/// <summary>
/// The immutable versioned deployment choice for rearming after a successful Recipe Activation.
/// Manual Rearm is the framework default; Automatic Rearm After PLC Activation applies only to
/// activation requested through the governed PLC path. Selecting a mode declares intent only:
/// Recipe Activation never asserts Ready and the policy grants no rearming authority.
/// </summary>
public enum PostActivationArmMode { ManualRearm = 1, AutomaticRearmAfterPlcActivation = 2 }

/// <summary>
/// An immutable deployment declaration, versioned independently from the Startup Production
/// Policy, for the arm transition that may follow Recipe Activation. It is a declaration only and
/// is never evidence that production became Ready.
/// </summary>
public sealed class PostActivationArmPolicy
{
    /// <summary>The framework default: Manual Rearm.</summary>
    public static PostActivationArmPolicy Default { get; } = new("SharpInspect.DefaultPostActivationArm",
        "1", PostActivationArmMode.ManualRearm);

    public PostActivationArmPolicy(string id, string version, PostActivationArmMode mode)
    {
        Id = AlgorithmConfigurationValidation.Identifier(id, nameof(id));
        Version = AlgorithmConfigurationValidation.Identifier(version, nameof(version));
        Mode = AlgorithmConfigurationValidation.Enum(mode, nameof(mode));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-post-activation-arm-policy-v1", Id, Version, Mode.ToString()
        });
        Reference = new(Id, Version, ContentHash);
    }

    public string Id { get; }
    public string Version { get; }
    public PostActivationArmMode Mode { get; }
    public string ContentHash { get; }
    public RecipeContractReference Reference { get; }
}
