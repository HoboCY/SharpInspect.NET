namespace SharpInspect.Abstractions;

/// <summary>At one timestamp, frame observations precede deadline adjudication.</summary>
public enum FrameAcquisitionClockPhase { FrameObservation = 0, Deadline = 1 }

/// <summary>
/// Shared Runtime/adapter monotonic domain. UTC is evidence only. Scheduled
/// callbacks must be bounded; implementations must not run a callback from Schedule.
/// Equal timestamps are ordered by phase, then registration sequence. Once deadline
/// adjudication at a timestamp starts, registering an observation at that timestamp
/// must fail rather than moving backwards through the phases.
/// </summary>
public interface IFrameAcquisitionClock
{
    long Frequency { get; }
    FrameTimePoint GetTimePoint();
    IDisposable Schedule(long dueTimestamp, FrameAcquisitionClockPhase phase, Action callback);
}

/// <summary>The Runtime's single Busy-to-normalized-frame interval.</summary>
public sealed record FrameAcquisitionStart
{
    public FrameAcquisitionStart(FrameTimePoint busyAt, long deadlineTimestamp, long monotonicFrequency)
    {
        BusyAt = busyAt ?? throw new ArgumentNullException(nameof(busyAt));
        if (deadlineTimestamp <= busyAt.MonotonicTimestamp)
            throw new ArgumentOutOfRangeException(nameof(deadlineTimestamp));
        if (monotonicFrequency is < 1 or > 10_000_000_000)
            throw new ArgumentOutOfRangeException(nameof(monotonicFrequency));
        DeadlineTimestamp = deadlineTimestamp;
        MonotonicFrequency = monotonicFrequency;
    }
    public FrameTimePoint BusyAt { get; }
    public long DeadlineTimestamp { get; }
    public long MonotonicFrequency { get; }
}

/// <summary>
/// A one-attempt Runtime gate. The adapter acknowledges only after installing its
/// exact pending request, then waits for Busy before issuing a software trigger or
/// accepting the permitted hardware pulse. Completion never invokes continuations inline.
/// After the wait completes the adapter must recheck IsBusy before dispatch: a
/// completed Busy task does not authorize a trigger after this control is closed.
/// </summary>
public sealed class FrameAcquisitionControl
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource<bool> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<FrameAcquisitionStart> _busy = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FrameTimePoint? _pendingInstalledAt;
    private FrameAcquisitionStart? _start;
    private bool _closed;

    internal FrameAcquisitionControl(FrameAcquisitionRequest request, IFrameAcquisitionClock clock)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public FrameAcquisitionRequest Request { get; }
    public IFrameAcquisitionClock Clock { get; }
    public bool IsBusy { get { lock (_sync) return !_closed && _start is not null; } }
    public bool IsClosed { get { lock (_sync) return _closed; } }
    public FrameAcquisitionStart? Start { get { lock (_sync) return _start; } }

    /// <summary>
    /// The immutable clock point at which the adapter installed and acknowledged
    /// this pending request.  Runtime uses this point for both Busy and deadline
    /// construction so an asynchronous acknowledgement cannot move the interval.
    /// </summary>
    public FrameTimePoint? PendingInstalledAt
    {
        get { lock (_sync) return _pendingInstalledAt; }
    }

    public bool AcknowledgePending(FrameAcquisitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_sync)
        {
            if (!CanAcknowledgePendingLocked(request)) return false;
            FrameTimePoint installedAt;
            try
            {
                installedAt = Clock.GetTimePoint();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return false;
            }

            return CommitPendingLocked(installedAt);
        }
    }

    /// <summary>
    /// Acknowledges an installed request with the adapter's already captured clock
    /// point.  The point is published before completing the pending task, making it
    /// impossible for a Runtime continuation to observe an acknowledged request
    /// without its installation time.
    /// </summary>
    public bool AcknowledgePending(FrameAcquisitionRequest request,
        FrameTimePoint installedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(installedAt);
        lock (_sync)
        {
            if (!CanAcknowledgePendingLocked(request)) return false;

            FrameTimePoint current;
            try
            {
                current = Clock.GetTimePoint();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return false;
            }

            // Monotonic time is the only ordering authority here.  Adapter and
            // host UTC observations can legitimately have different offsets.
            if (installedAt.MonotonicTimestamp > current.MonotonicTimestamp)
                return false;

            return CommitPendingLocked(installedAt);
        }
    }

    private bool CanAcknowledgePendingLocked(FrameAcquisitionRequest request) =>
        !_closed && request == Request && !_pending.Task.IsCompleted;

    private bool CommitPendingLocked(FrameTimePoint installedAt)
    {
        _pendingInstalledAt = installedAt;
        return _pending.TrySetResult(true);
    }

    public Task<FrameAcquisitionStart> WaitForBusyAsync(CancellationToken cancellationToken = default) =>
        _busy.Task.WaitAsync(cancellationToken);

    internal Task PendingInstalled => _pending.Task;

    internal bool TryOpen(FrameAcquisitionStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        lock (_sync)
        {
            if (_closed || _start is not null || !_pending.Task.IsCompletedSuccessfully ||
                start.MonotonicFrequency != Clock.Frequency) return false;
            _start = start;
            return _busy.TrySetResult(start);
        }
    }

    internal void Close()
    {
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            _pending.TrySetCanceled();
            _busy.TrySetCanceled();
        }
    }
}

public enum CameraProtocolViolationKind
{
    EarlyFrame,
    ExtraFrame,
    LateFrame,
    CorrelationMismatch,
    EarlyHardwarePulse,
    DuplicateHardwarePulse,
    TriggerWhileBusy,
    InvalidFrame,
    ObservationGap
}

/// <summary>A bounded adapter fact; it cannot call Runtime, assign identity, or decide a product result.</summary>
public sealed record CameraProtocolObservation
{
    public CameraProtocolObservation(long sequence, CameraProtocolViolationKind kind,
        string reasonCode, FrameTimePoint observedAt, ExecutionCorrelationId? correlation = null,
        int droppedFrames = 0)
    {
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (droppedFrames is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(droppedFrames));
        if (correlation is not null) AlgorithmContractValidation.Correlation(correlation, nameof(correlation));
        Sequence = sequence;
        Kind = CameraContractValidation.Enum(kind, nameof(kind));
        ReasonCode = CameraContractValidation.Reason(reasonCode, nameof(reasonCode));
        ObservedAt = observedAt ?? throw new ArgumentNullException(nameof(observedAt));
        Correlation = correlation;
        DroppedFrames = droppedFrames;
    }
    public long Sequence { get; }
    public CameraProtocolViolationKind Kind { get; }
    public string ReasonCode { get; }
    public FrameTimePoint ObservedAt { get; }
    public ExecutionCorrelationId? Correlation { get; }
    public int DroppedFrames { get; }
}

/// <summary>
/// Provider-owned bounded protocol history. Epoch/cursor gaps or overflow prevent
/// a consumer from treating an incomplete observation history as healthy.
/// Overflowed describes a gap relative to the requested cursor, not a permanent
/// historical latch. A paged consumer advances only to the last returned sequence;
/// ThroughSequence includes observations beyond the current page.
/// </summary>
public sealed class CameraProtocolSnapshot
{
    public CameraProtocolSnapshot(Guid epoch, long firstAvailableSequence, long throughSequence,
        bool overflowed, IEnumerable<CameraProtocolObservation> observations)
    {
        if (epoch == Guid.Empty) throw new ArgumentException("CameraProtocolEpochMissing", nameof(epoch));
        if (throughSequence is < 0 or long.MaxValue || firstAvailableSequence < 1 || firstAvailableSequence > throughSequence + 1)
            throw new ArgumentOutOfRangeException(nameof(firstAvailableSequence));
        ArgumentNullException.ThrowIfNull(observations);
        var copied = CameraContractValidation.Copy(observations, nameof(observations), 64);
        long previous = firstAvailableSequence - 1;
        foreach (var observation in copied)
        {
            if (observation.Sequence <= previous || observation.Sequence > throughSequence)
                throw new ArgumentException("CameraProtocolSequenceInvalid", nameof(observations));
            previous = observation.Sequence;
        }
        Epoch = epoch;
        FirstAvailableSequence = firstAvailableSequence;
        ThroughSequence = throughSequence;
        Overflowed = overflowed;
        Observations = copied;
    }
    public Guid Epoch { get; }
    public long FirstAvailableSequence { get; }
    public long ThroughSequence { get; }
    public bool Overflowed { get; }
    public IReadOnlyList<CameraProtocolObservation> Observations { get; }
}

/// <summary>
/// Additive controlled-acquisition capability. Acquire's returned task is the sole
/// frame ownership channel and must complete when the normalized frame is ready.
/// Protocol reads inspect an adapter-owned ring only, are bounded and thread-safe,
/// and never access a vendor handle or invoke callbacks. Production qualification
/// must establish these guarantees; the interface itself grants no qualification.
/// </summary>
public interface IControlledCameraDevice : ICameraDevice
{
    ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
        FrameAcquisitionControl control, CancellationToken cancellationToken = default);
    /// <summary>Returns at most maximumCount facts, all strictly after afterSequence.</summary>
    CameraProtocolSnapshot ReadProtocolObservations(long afterSequence, int maximumCount = 64);
}
