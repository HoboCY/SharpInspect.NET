using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// Immutable, versioned timing and recovery policy for one PLC communication
/// binding.  Every value is supplied by the deployment; this contract has no
/// protocol timing defaults.
/// </summary>
public sealed class PlcCommunicationPolicy
{
    private static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(5);

    public PlcCommunicationPolicy(
        string id,
        string version,
        TimeSpan pollInterval,
        TimeSpan operationTimeout,
        TimeSpan runtimeHeartbeatInterval,
        TimeSpan controllerHeartbeatInterval,
        TimeSpan controllerHeartbeatStaleAfter,
        TimeSpan runtimeHeartbeatStaleAfter,
        TimeSpan reconnectInterval,
        int maximumReconnectAttempts,
        TimeSpan synchronizationStabilityWindow,
        TimeSpan synchronizationTimeout)
    {
        Id = AlgorithmContractValidation.Identifier(id, nameof(id));
        Version = AlgorithmContractValidation.Identifier(version, nameof(version));
        PollInterval = pollInterval;
        OperationTimeout = operationTimeout;
        RuntimeHeartbeatInterval = runtimeHeartbeatInterval;
        ControllerHeartbeatInterval = controllerHeartbeatInterval;
        ControllerHeartbeatStaleAfter = controllerHeartbeatStaleAfter;
        RuntimeHeartbeatStaleAfter = runtimeHeartbeatStaleAfter;
        ReconnectInterval = reconnectInterval;
        MaximumReconnectAttempts = maximumReconnectAttempts;
        SynchronizationStabilityWindow = synchronizationStabilityWindow;
        SynchronizationTimeout = synchronizationTimeout;
        Validate();
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-plc-communication-policy-v1", Id, Version,
            Duration(PollInterval), Duration(OperationTimeout),
            Duration(RuntimeHeartbeatInterval), Duration(ControllerHeartbeatInterval),
            Duration(ControllerHeartbeatStaleAfter), Duration(RuntimeHeartbeatStaleAfter),
            Duration(ReconnectInterval), MaximumReconnectAttempts.ToString(CultureInfo.InvariantCulture),
            Duration(SynchronizationStabilityWindow), Duration(SynchronizationTimeout)
        });
    }

    public string Id { get; }
    public string Version { get; }
    public TimeSpan PollInterval { get; }
    public TimeSpan OperationTimeout { get; }
    public TimeSpan RuntimeHeartbeatInterval { get; }
    public TimeSpan ControllerHeartbeatInterval { get; }
    public TimeSpan ControllerHeartbeatStaleAfter { get; }
    public TimeSpan RuntimeHeartbeatStaleAfter { get; }
    public TimeSpan ReconnectInterval { get; }
    public int MaximumReconnectAttempts { get; }
    public TimeSpan SynchronizationStabilityWindow { get; }
    public TimeSpan SynchronizationTimeout { get; }
    public string ContentHash { get; }

    public void Validate()
    {
        PositiveDuration(PollInterval, nameof(PollInterval));
        PositiveDuration(OperationTimeout, nameof(OperationTimeout));
        PositiveDuration(RuntimeHeartbeatInterval, nameof(RuntimeHeartbeatInterval));
        PositiveDuration(ControllerHeartbeatInterval, nameof(ControllerHeartbeatInterval));
        PositiveDuration(ControllerHeartbeatStaleAfter, nameof(ControllerHeartbeatStaleAfter));
        PositiveDuration(RuntimeHeartbeatStaleAfter, nameof(RuntimeHeartbeatStaleAfter));
        PositiveDuration(ReconnectInterval, nameof(ReconnectInterval));
        PositiveDuration(SynchronizationStabilityWindow, nameof(SynchronizationStabilityWindow));
        PositiveDuration(SynchronizationTimeout, nameof(SynchronizationTimeout));
        if (MaximumReconnectAttempts is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumReconnectAttempts));

        if (ControllerHeartbeatStaleAfter <= ControllerHeartbeatInterval)
            throw new ArgumentException("PlcControllerHeartbeatStaleDeadlineInvalid",
                nameof(ControllerHeartbeatStaleAfter));
        if (RuntimeHeartbeatStaleAfter <= RuntimeHeartbeatInterval)
            throw new ArgumentException("PlcRuntimeHeartbeatStaleDeadlineInvalid",
                nameof(RuntimeHeartbeatStaleAfter));
        if (PollInterval > ControllerHeartbeatStaleAfter)
            throw new ArgumentException("PlcPollIntervalExceedsControllerStaleDeadline",
                nameof(PollInterval));
        if (PollInterval > RuntimeHeartbeatStaleAfter)
            throw new ArgumentException("PlcPollIntervalExceedsRuntimeStaleDeadline",
                nameof(PollInterval));
        if (SynchronizationStabilityWindow < PollInterval)
            throw new ArgumentException("PlcSynchronizationStabilityWindowInvalid",
                nameof(SynchronizationStabilityWindow));
        if (SynchronizationTimeout < SynchronizationStabilityWindow + SynchronizationStabilityWindow ||
            SynchronizationTimeout < OperationTimeout)
            throw new ArgumentException("PlcSynchronizationTimeoutInvalid",
                nameof(SynchronizationTimeout));
        if (OperationTimeout > ControllerHeartbeatStaleAfter ||
            OperationTimeout > RuntimeHeartbeatStaleAfter)
            throw new ArgumentException("PlcOperationTimeoutExceedsStaleDeadline",
                nameof(OperationTimeout));
    }

    private static void PositiveDuration(TimeSpan value, string parameterName)
    {
        if (value < MinimumDuration || value > MaximumDuration)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static string Duration(TimeSpan value) =>
        value.Ticks.ToString(CultureInfo.InvariantCulture);
}
