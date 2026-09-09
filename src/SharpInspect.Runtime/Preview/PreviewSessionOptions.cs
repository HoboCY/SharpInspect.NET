namespace SharpInspect.Runtime.Preview;

/// <summary>Resource budgets for explicitly admitted non-production Preview sessions.</summary>
public sealed class PreviewSessionOptions
{
    public TimeSpan FrameInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int MaximumFrameBytes { get; init; } = 16 * 1024 * 1024;

    public void Validate()
    {
        if (FrameInterval < TimeSpan.FromMilliseconds(100) || FrameInterval > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(FrameInterval));
        if (ShutdownTimeout < TimeSpan.FromSeconds(1) || ShutdownTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout));
        if (MaximumFrameBytes < 1 || MaximumFrameBytes > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumFrameBytes));
    }
}
