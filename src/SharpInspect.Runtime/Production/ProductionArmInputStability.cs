using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Production;

/// <summary>
/// A fresh window for one attempt. Historical synchronization and repeated reads
/// of the same cached sample cannot satisfy it. Clock values are monotonic ticks.
/// The tracker also counts the clear windows an observed high input or an
/// over-gap interruption ended, so captured evidence can carry them.
/// </summary>
internal sealed class ProductionArmInputStability
{
    private readonly PlcCommunicationPolicy _policy;
    private readonly long _timestampFrequency;
    private readonly long _windowTicks;
    private readonly long _maximumSampleGapTicks;
    private readonly long _startedAt;
    private readonly long _initialSequence;
    private long _lastSequence;
    private long _lastObservedAt;
    private long? _clearSince;
    private long _clearFirstSequence;
    private long _clearSampleCount;
    private long _clearMaximumGapTicks;
    private int _highInputResetCount;
    private int _sampleGapResetCount;

    internal ProductionArmInputStability(PlcCommunicationPolicy policy, long startedAt,
        long initialSequence, long timestampFrequency)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (timestampFrequency < 1 || startedAt < 0 || initialSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        _policy = policy;
        _timestampFrequency = timestampFrequency;
        _windowTicks = checked((long)Math.Ceiling(policy.SynchronizationStabilityWindow.TotalSeconds * timestampFrequency));
        _maximumSampleGapTicks = checked((long)Math.Floor(
            (policy.OperationTimeout + policy.PollInterval).TotalSeconds * timestampFrequency));
        _startedAt = startedAt;
        _initialSequence = initialSequence;
        _lastSequence = initialSequence;
    }

    internal void Observe(long sequence, long observedAt, bool inputsClear)
    {
        if (sequence <= _lastSequence || observedAt <= _lastObservedAt || observedAt <= _startedAt)
            return;
        if (_lastObservedAt > 0 && observedAt - _lastObservedAt > _maximumSampleGapTicks)
        {
            // An over-gap interruption ends the window; this sample may open a new one.
            if (_clearSince is not null) _sampleGapResetCount++;
            _clearSince = null;
        }
        if (!inputsClear)
        {
            if (_clearSince is not null) _highInputResetCount++;
            _clearSince = null;
        }
        else if (_clearSince is null)
        {
            _clearSince = observedAt;
            _clearFirstSequence = sequence;
            _clearSampleCount = 1;
            _clearMaximumGapTicks = 0;
        }
        else
        {
            var gap = observedAt - _lastObservedAt;
            if (gap > _clearMaximumGapTicks) _clearMaximumGapTicks = gap;
            _clearSampleCount++;
        }
        _lastSequence = sequence;
        _lastObservedAt = observedAt;
    }

    internal bool IsStable(long now) => _clearSince is { } start && now >= _lastObservedAt &&
        now - _lastObservedAt <= _maximumSampleGapTicks && _lastObservedAt - start >= _windowTicks;

    /// <summary>
    /// The immutable evidence of the current fresh window, or null while this attempt has not
    /// completed one. Each call snapshots the monotonic freshness age it was given.
    /// </summary>
    internal ProductionArmInputStabilityEvidence? TryCapture(long now)
    {
        if (!IsStable(now) || _clearSince is not { } start) return null;
        return new ProductionArmInputStabilityEvidence(_policy, _timestampFrequency, _initialSequence,
            _clearFirstSequence, _lastSequence, checked((int)_clearSampleCount), _lastObservedAt - start,
            _clearMaximumGapTicks, now - _lastObservedAt, _highInputResetCount, _sampleGapResetCount);
    }
}
