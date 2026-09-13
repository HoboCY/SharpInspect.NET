using System.Diagnostics;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Algorithms;

/// <summary>
/// Process-wide permanent execution safety latch. A latched guard cannot be reset by
/// rebuilding a dependency graph; recovery therefore requires a controlled process
/// restart. The guard does not run consumers or own the algorithm/frame resources.
/// </summary>
public sealed class AlgorithmExecutionGuard
{
    private static readonly AlgorithmExecutionGuard s_currentProcess = new();
    private readonly object _sync = new();
    private AlgorithmHungSnapshot? _snapshot;
    private readonly List<GraceRegistration> _pending = new();

    private AlgorithmExecutionGuard()
    {
    }

    /// <summary>Returns the one guard for this process.</summary>
    public static AlgorithmExecutionGuard CurrentProcess => s_currentProcess;

    /// <summary>Whether this process has observed an unbounded algorithm invocation.</summary>
    public bool IsHung
    {
        get { lock (_sync) { ObserveExpiredLocked(); return _snapshot is not null; } }
    }

    /// <summary>Returns the immutable first Hung observation, if one has been latched.</summary>
    public AlgorithmHungSnapshot? Snapshot
    {
        get { lock (_sync) { ObserveExpiredLocked(); return _snapshot; } }
    }

    /// <summary>
    /// Runs one framework-only publication while the healthy check and publication are
    /// atomic with respect to the permanent latch. The callback must be non-blocking and
    /// must not invoke consumer code.
    /// </summary>
    internal bool TryRunIfHealthy(Action frameworkPublication)
    {
        ArgumentNullException.ThrowIfNull(frameworkPublication);
        lock (_sync)
        {
            ObserveExpiredLocked();
            if (_snapshot is not null) return false;
            frameworkPublication();
            return true;
        }
    }

    internal GraceRegistration RegisterGrace(ExecutionCorrelationId correlation, Guid preparedInstanceId,
        Guid frameLeaseId, ExecutionStatus status, AlgorithmExecutionTimingSnapshot timing, long fixedAt)
    {
        var registration = new GraceRegistration(correlation, preparedInstanceId, frameLeaseId, status, timing, fixedAt);
        lock (_sync)
        {
            _pending.Add(registration);
            ObserveExpiredLocked();
        }
        return registration;
    }

    // 物理调用退出与宽限期到期在同一把锁下竞争；持锁期间不调用 Attempt 或消费者回调。
    internal void CompleteGrace(GraceRegistration registration)
    {
        lock (_sync)
        {
            ObserveExpiredLocked();
            _pending.Remove(registration);
        }
    }

    private void ObserveExpiredLocked()
    {
        if (_snapshot is not null) return;
        var now = Stopwatch.GetTimestamp();
        foreach (var pending in _pending)
        {
            if (TimeSpan.FromSeconds((now - pending.FixedAt) / (double)Stopwatch.Frequency) <
                pending.Timing.CancellationGracePeriod) continue;
            _snapshot = new(pending.Correlation, pending.PreparedInstanceId, pending.FrameLeaseId,
                pending.Status, pending.Timing, now, DateTimeOffset.UtcNow);
            return;
        }
    }

    internal sealed record GraceRegistration(ExecutionCorrelationId Correlation, Guid PreparedInstanceId,
        Guid FrameLeaseId, ExecutionStatus Status, AlgorithmExecutionTimingSnapshot Timing, long FixedAt);

    /// <summary>Publishes the first valid Hung observation; later observations are ignored.</summary>
    internal bool TryLatch(AlgorithmHungSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            if (_snapshot is not null) return false;
            _snapshot = snapshot;
            return true;
        }
    }
}

/// <summary>Immutable evidence for the first algorithm invocation that exceeded its grace boundary.</summary>
public sealed class AlgorithmHungSnapshot
{
    internal AlgorithmHungSnapshot(ExecutionCorrelationId correlation, Guid preparedInstanceId,
        Guid frameLeaseId, ExecutionStatus fixedExecutionStatus,
        AlgorithmExecutionTimingSnapshot timing, long monotonicObservedAt,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(timing);
        AlgorithmContractValidation.Correlation(correlation, nameof(correlation));
        if (preparedInstanceId == Guid.Empty) throw new ArgumentException("PreparedInstanceIdInvalid", nameof(preparedInstanceId));
        if (frameLeaseId == Guid.Empty) throw new ArgumentException("FrameLeaseIdInvalid", nameof(frameLeaseId));
        if (fixedExecutionStatus is not (ExecutionStatus.Timeout or ExecutionStatus.Cancelled))
            throw new ArgumentException("AlgorithmHungStatusInvalid", nameof(fixedExecutionStatus));
        if (monotonicObservedAt < 0)
            throw new ArgumentOutOfRangeException(nameof(monotonicObservedAt));
        if (observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("ObservedAtUtcInvalid", nameof(observedAtUtc));

        Correlation = correlation;
        PreparedInstanceId = preparedInstanceId;
        FrameLeaseId = frameLeaseId;
        FixedExecutionStatus = fixedExecutionStatus;
        Timing = timing;
        MonotonicObservedAt = monotonicObservedAt;
        ObservedAtUtc = observedAtUtc;
    }

    public ExecutionCorrelationId Correlation { get; }
    public Guid PreparedInstanceId { get; }
    public Guid FrameLeaseId { get; }
    public ExecutionStatus FixedExecutionStatus { get; }
    public AlgorithmExecutionTimingSnapshot Timing { get; }
    public long MonotonicObservedAt { get; }
    public DateTimeOffset ObservedAtUtc { get; }
}
