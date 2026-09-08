using SharpInspect.Abstractions;

namespace SharpInspect.Cameras.Hikrobot;

/// <summary>
/// The adapter-owned, bounded record of frames which could not be attributed to
/// the currently pending controlled acquisition.  It deliberately stores facts,
/// never a frame or a vendor object.
/// </summary>
internal sealed class HikrobotProtocolRing
{
    private const int MaximumCapacity = 64;
    private readonly object _sync = new();
    private readonly IFrameAcquisitionClock _clock;
    private readonly Guid _epoch = NewEpoch();
    private readonly Queue<CameraProtocolObservation> _observations = new();
    private long _nextSequence = 1;

    internal HikrobotProtocolRing(IFrameAcquisitionClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    internal Guid Epoch => _epoch;

    internal void Append(CameraProtocolViolationKind kind, string reasonCode,
        ExecutionCorrelationId? correlation = null, int droppedFrames = 0,
        FrameTimePoint? observedAt = null)
    {
        if (droppedFrames < 0) droppedFrames = 0;
        if (droppedFrames > 64) droppedFrames = 64;

        CameraProtocolObservation observation;
        lock (_sync)
        {
            if (_nextSequence == long.MaxValue)
                return;

            // The clock is shared with Runtime.  It is read while holding only
            // this small adapter lock and never while an SDK call is in flight.
            var point = observedAt ?? _clock.GetTimePoint();
            observation = new CameraProtocolObservation(_nextSequence++, kind,
                reasonCode, point, correlation, droppedFrames);
            _observations.Enqueue(observation);
            while (_observations.Count > MaximumCapacity)
                _observations.Dequeue();
        }
    }

    internal CameraProtocolSnapshot Read(long afterSequence, int maximumCount = MaximumCapacity)
    {
        if (afterSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (maximumCount is < 1 or > MaximumCapacity)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));

        lock (_sync)
        {
            var through = _nextSequence - 1;
            var first = _observations.Count == 0 ? 1 : _observations.Peek().Sequence;
            var overflowed = afterSequence < first - 1;
            var observations = _observations
                .Where(item => item.Sequence > afterSequence)
                .Take(maximumCount)
                .ToArray();
            return new CameraProtocolSnapshot(_epoch, first, through, overflowed,
                observations);
        }
    }

    private static Guid NewEpoch()
    {
        Guid value;
        do { value = Guid.NewGuid(); } while (value == Guid.Empty);
        return value;
    }
}
