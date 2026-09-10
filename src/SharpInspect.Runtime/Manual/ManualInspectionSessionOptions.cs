namespace SharpInspect.Runtime.Manual;

/// <summary>Host resource budgets for the explicitly admitted manual workflow.</summary>
public sealed class ManualInspectionSessionOptions
{
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PreparationTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public void Validate()
    {
        if (ShutdownTimeout < TimeSpan.FromSeconds(1) || ShutdownTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout));
        if (PreparationTimeout <= TimeSpan.Zero || PreparationTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(PreparationTimeout));
    }
}
