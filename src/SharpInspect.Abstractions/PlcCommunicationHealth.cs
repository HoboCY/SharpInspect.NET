namespace SharpInspect.Abstractions;

/// <summary>Immutable public projection of PLC transport and heartbeat health.</summary>
public sealed record PlcCommunicationHealth
{
    public PlcCommunicationHealth(
        bool transportReachable,
        bool controllerHeartbeatFresh,
        bool runtimeHeartbeatObserved,
        uint? controllerEpoch,
        bool synchronized,
        bool recoveryRequired,
        int recoveryAttempt,
        long connectionGeneration,
        string reasonCode)
    {
        if (recoveryAttempt < 0)
            throw new ArgumentOutOfRangeException(nameof(recoveryAttempt));
        if (connectionGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(connectionGeneration));
        TransportReachable = transportReachable;
        ControllerHeartbeatFresh = controllerHeartbeatFresh;
        RuntimeHeartbeatObserved = runtimeHeartbeatObserved;
        ControllerEpoch = controllerEpoch;
        Synchronized = synchronized;
        RecoveryRequired = recoveryRequired;
        RecoveryAttempt = recoveryAttempt;
        ConnectionGeneration = connectionGeneration;
        ReasonCode = QualificationContractValidation.Reason(reasonCode, nameof(reasonCode));
    }

    public bool TransportReachable { get; }
    public bool ControllerHeartbeatFresh { get; }
    public bool RuntimeHeartbeatObserved { get; }
    public uint? ControllerEpoch { get; }
    public bool Synchronized { get; }
    public bool RecoveryRequired { get; }
    public int RecoveryAttempt { get; }
    public long ConnectionGeneration { get; }
    public string ReasonCode { get; }

    /// <summary>True only when transport, both heartbeat observations and synchronization are healthy.</summary>
    public bool Healthy => TransportReachable && ControllerHeartbeatFresh &&
        RuntimeHeartbeatObserved && ControllerEpoch is > 0 && Synchronized && !RecoveryRequired;

    /// <summary>Policy hash used for this observation, when a policy was bound.</summary>
    public string? PolicyHash { get; init; }

    /// <summary>Runtime epoch whose heartbeat was written, when one was assigned.</summary>
    public Guid RuntimeEpoch { get; init; }
}
