using SharpInspect.Abstractions;

namespace SharpInspect.Cameras.Virtual;

/// <summary>
/// Deterministic monotonic time for virtual camera adapters. It has no dependency
/// on a wall clock, timer, or task scheduler; callers advance it explicitly.
/// </summary>
public sealed class VirtualCameraClock : IDisposable
{
    public const long Frequency = TimeSpan.TicksPerSecond;

    private const int MaximumPendingEvents = 512;
    private const int MaximumCallbacksPerAdvance = 4096;

    private readonly object _stateGate = new();
    private readonly object _operationGate = new();
    private readonly SortedSet<ScheduledEvent> _events = new(ScheduledEventComparer.Instance);
    private long _timestamp;
    private DateTimeOffset _utcNow;
    private long _nextRegistrationSequence;
    private bool _disposed;
    private bool _operationActive;
    private int _operationThreadId;

    public VirtualCameraClock(DateTimeOffset initialUtc)
    {
        _utcNow = NormalizeUtc(initialUtc, nameof(initialUtc));
    }

    public long Timestamp
    {
        get
        {
            lock (_stateGate) return _timestamp;
        }
    }

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_stateGate) return _utcNow;
        }
    }

    public int PendingEventCount
    {
        get
        {
            lock (_stateGate) return _events.Count;
        }
    }

    public FrameTimePoint GetTimePoint()
    {
        lock (_stateGate) return new FrameTimePoint(_utcNow, _timestamp);
    }

    public void AdvanceBy(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delta), "VirtualCameraClockDeltaNegative");

        EnterOperation();
        try
        {
            long target;
            DateTimeOffset targetUtc;
            lock (_stateGate)
            {
                EnsureNotDisposedLocked();
                try
                {
                    target = checked(_timestamp + delta.Ticks);
                }
                catch (OverflowException)
                {
                    throw new ArgumentOutOfRangeException(nameof(delta),
                        "VirtualCameraClockTimestampOverflow");
                }

                targetUtc = AddUtc(_utcNow, delta.Ticks, nameof(delta));
            }

            AdvanceCore(target, targetUtc);
        }
        finally
        {
            ExitOperation();
        }
    }

    public void AdvanceTo(long timestamp)
    {
        if (timestamp < 0)
            throw new ArgumentOutOfRangeException(nameof(timestamp), "VirtualCameraClockTimestampInvalid");

        EnterOperation();
        try
        {
            DateTimeOffset targetUtc;
            lock (_stateGate)
            {
                EnsureNotDisposedLocked();
                if (timestamp < _timestamp)
                    throw new ArgumentOutOfRangeException(nameof(timestamp),
                        "VirtualCameraClockTimestampCannotGoBackwards");

                targetUtc = AddUtc(_utcNow, timestamp - _timestamp, nameof(timestamp));
            }

            AdvanceCore(timestamp, targetUtc);
        }
        finally
        {
            ExitOperation();
        }
    }

    public void SetUtc(DateTimeOffset utc)
    {
        var normalized = NormalizeUtc(utc, nameof(utc));
        EnterOperation();
        try
        {
            lock (_stateGate)
            {
                EnsureNotDisposedLocked();
                _utcNow = normalized;
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>Schedules adapter-owned work at a monotonic timestamp.</summary>
    internal IDisposable Schedule(long dueTimestamp, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_stateGate)
        {
            EnsureNotDisposedLocked();
            if (dueTimestamp < _timestamp)
                throw new ArgumentOutOfRangeException(nameof(dueTimestamp),
                    "VirtualCameraClockScheduleInPast");
            if (_events.Count >= MaximumPendingEvents)
                throw new InvalidOperationException("VirtualCameraClockScheduleCapacityExceeded");
            if (_nextRegistrationSequence == long.MaxValue)
                throw new InvalidOperationException("VirtualCameraClockScheduleSequenceExhausted");

            var scheduled = new ScheduledEvent(dueTimestamp, _nextRegistrationSequence++, callback);
            _events.Add(scheduled);
            return new ScheduleHandle(this, scheduled);
        }
    }

    public void Dispose()
    {
        var currentThread = Environment.CurrentManagedThreadId;
        lock (_operationGate)
        {
            while (_operationActive && _operationThreadId != currentThread)
                Monitor.Wait(_operationGate);

            lock (_stateGate)
            {
                if (_disposed) return;
                _disposed = true;
                _events.Clear();
            }
        }
    }

    private void AdvanceCore(long target, DateTimeOffset targetUtc)
    {
        var executedCallbacks = 0;
        while (true)
        {
            ScheduledEvent scheduled;
            lock (_stateGate)
            {
                if (_disposed) return;
                if (_events.Count == 0 || _events.Min!.DueTimestamp > target)
                {
                    _timestamp = target;
                    _utcNow = targetUtc;
                    return;
                }

                scheduled = _events.Min!;
                _events.Remove(scheduled);
                if (executedCallbacks >= MaximumCallbacksPerAdvance)
                    throw new InvalidOperationException("VirtualCameraClockCallbackLimitExceeded");

                var elapsed = scheduled.DueTimestamp - _timestamp;
                _utcNow = AddUtc(_utcNow, elapsed, nameof(target));
                _timestamp = scheduled.DueTimestamp;
            }

            executedCallbacks++;
            try
            {
                scheduled.Callback();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw new InvalidOperationException("VirtualCameraClockCallbackFailed");
            }
        }
    }

    private void Cancel(ScheduledEvent scheduled)
    {
        lock (_stateGate) _events.Remove(scheduled);
    }

    private void EnterOperation()
    {
        lock (_operationGate)
        {
            if (_operationActive)
            {
                if (_operationThreadId == Environment.CurrentManagedThreadId)
                    throw new InvalidOperationException("VirtualCameraClockReentrantOperation");
                throw new InvalidOperationException("VirtualCameraClockBusy");
            }

            _operationActive = true;
            _operationThreadId = Environment.CurrentManagedThreadId;
        }
    }

    private void ExitOperation()
    {
        lock (_operationGate)
        {
            _operationActive = false;
            _operationThreadId = 0;
            Monitor.PulseAll(_operationGate);
        }
    }

    private void EnsureNotDisposedLocked()
    {
        if (_disposed) throw new InvalidOperationException("VirtualCameraClockDisposed");
    }

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value, string parameterName)
    {
        try
        {
            var utc = value.ToUniversalTime();
            // Every observable virtual instant must fit the shared FrameTimePoint contract.
            if (utc == default) throw new ArgumentException("VirtualCameraClockUtcInvalid");
            return utc;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new ArgumentOutOfRangeException(parameterName, "VirtualCameraClockUtcInvalid");
        }
    }

    private static DateTimeOffset AddUtc(DateTimeOffset value, long ticks, string parameterName)
    {
        try
        {
            return value.AddTicks(ticks);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentOutOfRangeException(parameterName, "VirtualCameraClockUtcOverflow");
        }
    }

    private sealed class ScheduledEvent
    {
        internal ScheduledEvent(long dueTimestamp, long registrationSequence, Action callback)
        {
            DueTimestamp = dueTimestamp;
            RegistrationSequence = registrationSequence;
            Callback = callback;
        }

        internal long DueTimestamp { get; }
        internal long RegistrationSequence { get; }
        internal Action Callback { get; }
    }

    private sealed class ScheduledEventComparer : IComparer<ScheduledEvent>
    {
        internal static ScheduledEventComparer Instance { get; } = new();

        public int Compare(ScheduledEvent? left, ScheduledEvent? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var due = left.DueTimestamp.CompareTo(right.DueTimestamp);
            return due != 0 ? due : left.RegistrationSequence.CompareTo(right.RegistrationSequence);
        }
    }

    private sealed class ScheduleHandle : IDisposable
    {
        private readonly VirtualCameraClock _owner;
        private ScheduledEvent? _scheduled;

        internal ScheduleHandle(VirtualCameraClock owner, ScheduledEvent scheduled)
        {
            _owner = owner;
            _scheduled = scheduled;
        }

        public void Dispose()
        {
            var scheduled = Interlocked.Exchange(ref _scheduled, null);
            if (scheduled is not null)
                _owner.Cancel(scheduled);
        }
    }
}
