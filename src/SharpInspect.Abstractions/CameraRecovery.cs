using System.Collections.ObjectModel;

namespace SharpInspect.Abstractions;

/// <summary>
/// Runtime policy state for one bound camera recovery engine.  Camera health is
/// reported separately because a device fact is not itself a Runtime policy.
/// </summary>
public enum CameraRecoveryState
{
    Unknown = 0,
    Healthy = 1,
    RecoveryRequired = 2,
    Recovering = 3,
    Exhausted = 4,
    Disposed = 5
}

/// <summary>One bounded, Runtime-owned recovery event.</summary>
public enum CameraRecoveryEventKind
{
    SourceDisconnected = 0,
    CycleStarted = 1,
    AttemptStarted = 2,
    AttemptFailed = 3,
    CycleCompleted = 4,
    CycleExhausted = 5
}

/// <summary>
/// Safe current recovery projection.  It deliberately contains no serial,
/// network, provider, SDK, or exception details.
/// </summary>
public sealed record CameraRecoverySnapshot
{
    public CameraRecoverySnapshot(string logicalCameraRole, Guid recoveryEpoch, long revision,
        Guid? cycleId, CameraRecoveryState state, int attemptCount, int maximumAttempts,
        TimeSpan retryInterval, FrameTimePoint? nextAttemptAt, bool sourceHealthy,
        CameraHealthSnapshot? health, string reasonCode, long healthObservationRevision = 0)
    {
        LogicalCameraRole = FrameMetadataValidation.Identifier(logicalCameraRole,
            nameof(logicalCameraRole));
        if (recoveryEpoch == Guid.Empty)
            throw new ArgumentException("CameraRecoveryEpochMissing", nameof(recoveryEpoch));
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision));
        if (healthObservationRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(healthObservationRevision));
        if (cycleId == Guid.Empty)
            throw new ArgumentException("CameraRecoveryCycleIdInvalid", nameof(cycleId));
        if (!Enum.IsDefined(typeof(CameraRecoveryState), state))
            throw new ArgumentOutOfRangeException(nameof(state));
        if (attemptCount is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(attemptCount));
        if (maximumAttempts is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        if (attemptCount > maximumAttempts)
            throw new ArgumentException("CameraRecoveryAttemptCountInvalid",
                nameof(attemptCount));
        if (retryInterval <= TimeSpan.Zero || retryInterval > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(retryInterval));

        RecoveryEpoch = recoveryEpoch;
        Revision = revision;
        CycleId = cycleId;
        State = state;
        AttemptCount = attemptCount;
        MaximumAttempts = maximumAttempts;
        RetryInterval = retryInterval;
        NextAttemptAt = nextAttemptAt;
        SourceHealthy = sourceHealthy;
        Health = health;
        ReasonCode = CameraContractValidation.Reason(reasonCode, nameof(reasonCode));
        HealthObservationRevision = healthObservationRevision;
    }

    public string LogicalCameraRole { get; }
    public Guid RecoveryEpoch { get; }
    public long Revision { get; }
    public Guid? CycleId { get; }
    public CameraRecoveryState State { get; }
    public int AttemptCount { get; }
    public int MaximumAttempts { get; }
    public TimeSpan RetryInterval { get; }
    public FrameTimePoint? NextAttemptAt { get; }
    public bool SourceHealthy { get; }
    public CameraHealthSnapshot? Health { get; }
    public string ReasonCode { get; }
    public long HealthObservationRevision { get; }

    /// <summary>The monotonic schedule value, when an attempt is scheduled.</summary>
    public long? NextAttemptTimestamp => NextAttemptAt?.MonotonicTimestamp;
}

/// <summary>One bounded event in the recovery engine's stable event epoch.</summary>
public sealed record CameraRecoveryEvent
{
    public CameraRecoveryEvent(Guid recoveryEpoch, long sequence, Guid cycleId,
        int attemptNumber, CameraRecoveryEventKind kind, FrameTimePoint timestamp,
        string reasonCode)
    {
        if (recoveryEpoch == Guid.Empty)
            throw new ArgumentException("CameraRecoveryEpochMissing", nameof(recoveryEpoch));
        if (sequence < 1)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        if (cycleId == Guid.Empty)
            throw new ArgumentException("CameraRecoveryCycleIdMissing", nameof(cycleId));
        if (attemptNumber < 0 || attemptNumber > 100)
            throw new ArgumentOutOfRangeException(nameof(attemptNumber));
        if (!Enum.IsDefined(typeof(CameraRecoveryEventKind), kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        Timestamp = timestamp ?? throw new ArgumentNullException(nameof(timestamp));

        RecoveryEpoch = recoveryEpoch;
        Sequence = sequence;
        CycleId = cycleId;
        AttemptNumber = attemptNumber;
        Kind = kind;
        ReasonCode = CameraContractValidation.Reason(reasonCode, nameof(reasonCode));
    }

    public Guid RecoveryEpoch { get; }
    public long Sequence { get; }
    public Guid CycleId { get; }
    public int AttemptNumber { get; }
    public CameraRecoveryEventKind Kind { get; }
    public FrameTimePoint Timestamp { get; }
    public string ReasonCode { get; }

}

/// <summary>
/// Bounded page over the recovery event ring.  Overflowed means that the caller's
/// cursor is older than the retained ring and must be treated as a fail-closed gap.
/// </summary>
public sealed class CameraRecoveryEventPage
{
    public CameraRecoveryEventPage(Guid recoveryEpoch, long firstAvailableSequence,
        long throughSequence, bool overflowed, IEnumerable<CameraRecoveryEvent> events)
    {
        if (recoveryEpoch == Guid.Empty)
            throw new ArgumentException("CameraRecoveryEpochMissing", nameof(recoveryEpoch));
        if (throughSequence < 0 || throughSequence == long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(throughSequence));
        if (firstAvailableSequence < 1 || firstAvailableSequence > throughSequence + 1)
            throw new ArgumentOutOfRangeException(nameof(firstAvailableSequence));
        ArgumentNullException.ThrowIfNull(events);

        var copied = new List<CameraRecoveryEvent>(Math.Min(64, 16));
        long previous = firstAvailableSequence - 1;
        foreach (var item in events)
        {
            if (copied.Count == 64)
                throw new ArgumentException("CameraRecoveryEventPageCapacityExceeded",
                    nameof(events));
            if (item is null || item.RecoveryEpoch != recoveryEpoch ||
                item.Sequence <= previous || item.Sequence > throughSequence)
                throw new ArgumentException("CameraRecoveryEventSequenceInvalid",
                    nameof(events));
            copied.Add(item);
            previous = item.Sequence;
        }

        RecoveryEpoch = recoveryEpoch;
        FirstAvailableSequence = firstAvailableSequence;
        ThroughSequence = throughSequence;
        Overflowed = overflowed;
        Events = new ReadOnlyCollection<CameraRecoveryEvent>(copied);
    }

    public Guid RecoveryEpoch { get; }
    public long FirstAvailableSequence { get; }
    public long ThroughSequence { get; }
    public bool Overflowed { get; }
    public IReadOnlyList<CameraRecoveryEvent> Events { get; }
}
