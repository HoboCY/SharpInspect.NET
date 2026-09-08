using System.Diagnostics;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Cameras;

/// <summary>
/// Monotonic acquisition clock for ordinary Runtime hosts. A single bounded
/// scheduler orders due timestamp, phase, and registration sequence before invoking
/// callbacks on its background thread. Schedule never invokes a callback inline.
/// </summary>
public sealed class SystemFrameAcquisitionClock : IFrameAcquisitionClock, IDisposable
{
    private const int MaximumScheduledEvents = 256;
    private readonly object _sync = new();
    private readonly SortedSet<ScheduledEvent> _events = new(ScheduledEventComparer.Instance);
    private readonly Thread _scheduler;
    private long _nextSequence;
    private long _phaseBarrierTimestamp = -1;
    private FrameAcquisitionClockPhase _phaseBarrier;
    private bool _disposed;

    public SystemFrameAcquisitionClock()
    {
        _scheduler = new Thread(RunScheduler)
        {
            IsBackground = true,
            Name = "SharpInspect.FrameAcquisitionClock"
        };
        _scheduler.Start();
    }

    public long Frequency => Stopwatch.Frequency;

    public FrameTimePoint GetTimePoint() =>
        new(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());

    public IDisposable Schedule(long dueTimestamp, FrameAcquisitionClockPhase phase,
        Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!Enum.IsDefined(typeof(FrameAcquisitionClockPhase), phase))
            throw new ArgumentOutOfRangeException(nameof(phase));

        lock (_sync)
        {
            EnsureOpenLocked();
            var now = Stopwatch.GetTimestamp();
            if (dueTimestamp < now)
                throw new ArgumentOutOfRangeException(nameof(dueTimestamp),
                    "FrameAcquisitionClockScheduleInPast");
            if (dueTimestamp == _phaseBarrierTimestamp && phase < _phaseBarrier)
                throw new InvalidOperationException("FrameAcquisitionClockPhaseAlreadyPassed");
            if (_events.Count >= MaximumScheduledEvents)
                throw new InvalidOperationException("FrameAcquisitionClockScheduleCapacityExceeded");
            if (_nextSequence == long.MaxValue)
                throw new InvalidOperationException("FrameAcquisitionClockSequenceExhausted");

            var scheduled = new ScheduledEvent(dueTimestamp, phase, _nextSequence++, callback);
            _events.Add(scheduled);
            Monitor.Pulse(_sync);
            return new ScheduleHandle(this, scheduled);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _events.Clear();
            Monitor.PulseAll(_sync);
        }
        // The thread is a background owner. A callback supplied by a caller is not
        // joined here, so Dispose remains bounded even if that callback is faulty.
    }

    private void RunScheduler()
    {
        while (true)
        {
            ScheduledEvent? scheduled = null;
            lock (_sync)
            {
                while (!_disposed)
                {
                    if (_events.Count == 0)
                    {
                        Monitor.Wait(_sync);
                        continue;
                    }

                    var next = _events.Min!;
                    var now = Stopwatch.GetTimestamp();
                    if (next.DueTimestamp > now)
                    {
                        Monitor.Wait(_sync, ToWait(next.DueTimestamp - now));
                        continue;
                    }

                    _events.Remove(next);
                    _phaseBarrierTimestamp = next.DueTimestamp;
                    _phaseBarrier = next.Phase;
                    scheduled = next;
                    break;
                }

                if (_disposed) return;
            }

            if (scheduled is null || !scheduled.TryBegin()) continue;
            try
            {
                scheduled.Callback();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Runtime callbacks are fail-closed and bounded. Do not let a
                // callback exception terminate the process scheduler thread.
            }
        }
    }

    private void Cancel(ScheduledEvent scheduled)
    {
        lock (_sync)
        {
            if (scheduled.TryCancel())
                _events.Remove(scheduled);
            Monitor.Pulse(_sync);
        }
    }

    private void EnsureOpenLocked()
    {
        if (_disposed)
            throw new InvalidOperationException("FrameAcquisitionClockDisposed");
    }

    private static int ToWait(long timestampDelta)
    {
        if (timestampDelta <= 0) return 0;
        var milliseconds = timestampDelta * 1000d / Stopwatch.Frequency;
        if (!double.IsFinite(milliseconds) || milliseconds >= int.MaxValue)
            return int.MaxValue;
        return (int)Math.Clamp(Math.Ceiling(milliseconds), 1d, int.MaxValue);
    }

    private sealed class ScheduledEvent
    {
        private int _state;

        internal ScheduledEvent(long dueTimestamp, FrameAcquisitionClockPhase phase,
            long sequence, Action callback)
        {
            DueTimestamp = dueTimestamp;
            Phase = phase;
            Sequence = sequence;
            Callback = callback;
        }

        internal long DueTimestamp { get; }
        internal FrameAcquisitionClockPhase Phase { get; }
        internal long Sequence { get; }
        internal Action Callback { get; }

        internal bool TryBegin() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;
        internal bool TryCancel() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;
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
            if (due != 0) return due;
            var phase = left.Phase.CompareTo(right.Phase);
            return phase != 0 ? phase : left.Sequence.CompareTo(right.Sequence);
        }
    }

    private sealed class ScheduleHandle : IDisposable
    {
        private readonly SystemFrameAcquisitionClock _owner;
        private ScheduledEvent? _scheduled;

        internal ScheduleHandle(SystemFrameAcquisitionClock owner, ScheduledEvent scheduled)
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
