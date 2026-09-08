namespace SharpInspect.Runtime.Cameras;

/// <summary>
/// Explicit bounds for one camera recovery cycle.  The defaults implement the
/// V1 five-second/twenty-attempt policy; every physical operation and shutdown
/// has its own caller-facing bound while the underlying task remains owned.
/// </summary>
public sealed class CameraRecoveryOptions
{
    public CameraRecoveryOptions()
        : this(TimeSpan.FromSeconds(5), 20, TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10))
    {
    }

    public CameraRecoveryOptions(TimeSpan retryInterval, int maximumAttempts,
        TimeSpan? operationTimeout = null, TimeSpan? shutdownWaitTimeout = null)
    {
        ValidateRetryInterval(retryInterval, nameof(retryInterval));
        if (maximumAttempts is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));

        var operation = operationTimeout ?? TimeSpan.FromSeconds(5);
        var shutdown = shutdownWaitTimeout ?? TimeSpan.FromSeconds(10);
        ValidateBound(operation, nameof(operationTimeout));
        ValidateBound(shutdown, nameof(shutdownWaitTimeout));

        RetryInterval = retryInterval;
        MaximumAttempts = maximumAttempts;
        OperationTimeout = operation;
        ShutdownWaitTimeout = shutdown;
    }

    public TimeSpan RetryInterval { get; }
    public int MaximumAttempts { get; }
    public TimeSpan OperationTimeout { get; }
    public TimeSpan ShutdownWaitTimeout { get; }

    internal const int EventCapacity = 256;
    internal const int MaximumEventReadCount = 64;

    private static void ValidateRetryInterval(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void ValidateBound(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(parameterName);
    }
}
