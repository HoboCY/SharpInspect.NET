namespace SharpInspect.Runtime.Recipes;

/// <summary>Bounded waiting budgets. Expiry refuses retirement; it never asserts resource exit.</summary>
public sealed class RecipeLifecycleRuntimeOptions
{
    public TimeSpan QuiescenceTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(2);

    internal void Validate()
    {
        if (QuiescenceTimeout <= TimeSpan.Zero || QuiescenceTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(QuiescenceTimeout));
        if (CleanupTimeout <= TimeSpan.Zero || CleanupTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(CleanupTimeout));
    }
}
