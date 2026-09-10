namespace SharpInspect.Runtime.Qualification;

/// <summary>Bounded time and run limits for one station qualification session.</summary>
public sealed class StationQualificationSessionOptions
{
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ObservationFreshness { get; init; } = TimeSpan.FromSeconds(2);
    public int MaximumRuns { get; init; } = 64;

    /// <summary>Descriptive alias retained for callers that use the short contract name.</summary>
    public int MaxRuns => MaximumRuns;

    public void Validate()
    {
        if (OperationTimeout < TimeSpan.FromMilliseconds(20) ||
            OperationTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(OperationTimeout));
        if (ShutdownTimeout < TimeSpan.FromMilliseconds(20) ||
            ShutdownTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout));
        if (ObservationFreshness <= TimeSpan.Zero ||
            ObservationFreshness > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(ObservationFreshness));
        if (MaximumRuns is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumRuns));
    }
}
