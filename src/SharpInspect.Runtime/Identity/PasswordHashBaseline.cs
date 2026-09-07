namespace SharpInspect.Runtime.Identity;

/// <summary>
/// Versioned release parameters for password verification records.
/// </summary>
public sealed record PasswordHashBaseline
{
    public const string DevelopmentVersion = "development-2026-09";
    public const int SecurityFloorIterations = 600_000;
    public const int MaximumSupportedIterations = 10_000_000;
    public const int MinimumSaltBytes = 16;
    public const int MinimumDerivedBytes = 32;
    public const int CurrentParameterVersion = 1;

    public PasswordHashBaseline()
    {
    }

    public PasswordHashBaseline(string version, int targetIterations)
    {
        Version = version;
        TargetIterations = targetIterations;
    }

    public string Version { get; init; } = DevelopmentVersion;
    public int ParameterVersion { get; init; } = CurrentParameterVersion;
    public int MinimumIterations { get; init; } = SecurityFloorIterations;
    public int TargetIterations { get; init; } = SecurityFloorIterations;
    public int MaximumIterations { get; init; } = MaximumSupportedIterations;
    public int SaltBytes { get; init; } = MinimumSaltBytes;
    public int DerivedBytes { get; init; } = MinimumDerivedBytes;

    public static PasswordHashBaseline Development { get; } = new();

    public static PasswordHashBaseline Current => Development;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Version) || Version.Length > 128)
            throw new ArgumentException("PasswordHashBaselineVersionInvalid", nameof(Version));
        if (ParameterVersion != CurrentParameterVersion)
            throw new ArgumentOutOfRangeException(nameof(ParameterVersion), ParameterVersion,
                "PasswordHashBaselineParameterVersionUnsupported");
        if (MinimumIterations < SecurityFloorIterations || MinimumIterations > MaximumSupportedIterations)
            throw new ArgumentOutOfRangeException(nameof(MinimumIterations), MinimumIterations,
                "PasswordHashBaselineMinimumIterationsInvalid");
        if (MaximumIterations < MinimumIterations || MaximumIterations > MaximumSupportedIterations)
            throw new ArgumentOutOfRangeException(nameof(MaximumIterations), MaximumIterations,
                "PasswordHashBaselineMaximumIterationsInvalid");
        if (TargetIterations < MinimumIterations || TargetIterations > MaximumIterations)
            throw new ArgumentOutOfRangeException(nameof(TargetIterations), TargetIterations,
                "PasswordHashBaselineTargetIterationsInvalid");
        if (SaltBytes < MinimumSaltBytes || SaltBytes > 1024)
            throw new ArgumentOutOfRangeException(nameof(SaltBytes), SaltBytes,
                "PasswordHashBaselineSaltLengthInvalid");
        if (DerivedBytes < MinimumDerivedBytes || DerivedBytes > 1024)
            throw new ArgumentOutOfRangeException(nameof(DerivedBytes), DerivedBytes,
                "PasswordHashBaselineDerivedLengthInvalid");
    }
}
