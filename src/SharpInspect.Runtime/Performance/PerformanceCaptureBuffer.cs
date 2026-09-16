using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Performance;

/// <summary>One finite capture; no producer waits, performs IO, replaces an event or starts a task.</summary>
internal sealed class PerformanceCaptureBuffer
{
    // Conservative UTF-8 envelope reservations also bound retained object counts. No raw text payloads are admitted.
    private const int EventBytes = 768;
    private const int ResourceSampleBytes = 32768;
    private readonly object _sync = new();
    private readonly PerformanceContract _contract;
    private readonly PerformanceEvent?[] _events;
    private readonly PerformanceResourceSample?[] _samples;
    private readonly PerformanceFrameObservation?[] _frames;
    private readonly PerformanceCycleBinding?[] _bindings;
    private int _eventCount, _sampleCount, _frameCount, _bindingCount, _sealed;
    private long _bytes, _sequence, _drops, _firstMissing;
    private int _activeObservers, _retirementIncomplete;
    internal PerformanceCaptureBuffer(PerformanceContract contract, PerformanceRunHeader header)
    {
        _contract = contract; Header = header;
        _events = new PerformanceEvent[Math.Min(contract.Capture.MaximumEvents, (int)(contract.Capture.MaximumBytes / EventBytes))];
        _samples = new PerformanceResourceSample[Math.Min(contract.Capture.MaximumResourceSamples,
            (int)(contract.Capture.MaximumBytes / ResourceSampleBytes))];
        _frames = new PerformanceFrameObservation[_events.Length];
        _bindings = new PerformanceCycleBinding[_events.Length];
    }
    internal PerformanceRunHeader Header { get; }
    internal bool RetirementIncomplete => Volatile.Read(ref _retirementIncomplete) != 0;
    internal bool IsSealed => Volatile.Read(ref _sealed) != 0;
    internal int EventCount => Volatile.Read(ref _eventCount);
    internal int SampleCount => Volatile.Read(ref _sampleCount);
    internal long Drops => Interlocked.Read(ref _drops);
    internal long FirstMissingSequence => Interlocked.Read(ref _firstMissing);

    internal void Event(PerformanceEventKind kind, long timestamp, ExecutionCorrelationId? correlation,
        PlcControllerCycle? controller, PerformanceObservationOutcome outcome, string? reason)
    {
        if (!TryBeginObservation()) return;
        try
        {
            var sequence = Interlocked.Increment(ref _sequence);
            if (!Monitor.TryEnter(_sync)) { Drop(sequence); return; }
            try
            {
                if (IsSealed) return;
                if (_eventCount == _events.Length || _bytes > _contract.Capture.MaximumBytes - EventBytes)
                { Drop(sequence); return; }
                _events[_eventCount++] = new(sequence, Header.RuntimeEpoch, timestamp, kind, correlation,
                    controller?.ControllerEpoch, controller?.CycleSequence, outcome, reason);
                _bytes += EventBytes;
            }
            finally { Monitor.Exit(_sync); }
            }
        finally { EndObservation(); }
    }
    internal void Sample(PerformanceResourceSample sample)
    {
        if (!TryBeginObservation()) return;
        try
        {
            if (!Monitor.TryEnter(_sync)) { Drop(Interlocked.Read(ref _sequence) + 1); return; }
            try
            {
                if (IsSealed) return;
                if (_sampleCount == _samples.Length || _bytes > _contract.Capture.MaximumBytes - ResourceSampleBytes)
                { Drop(Interlocked.Read(ref _sequence) + 1); return; }
                _samples[_sampleCount++] = sample; _bytes += ResourceSampleBytes;
            }
            finally { Monitor.Exit(_sync); }
            }
        finally { EndObservation(); }
    }
    internal void ObservationLost()
    {
        if (!TryBeginObservation()) return;
        try { Drop(Interlocked.Read(ref _sequence) + 1); }
        finally { EndObservation(); }
    }

    internal bool TryBeginObservation()
    {
        if (IsSealed) return false;
        Interlocked.Increment(ref _activeObservers);
        if (!IsSealed) return true;
        EndObservation();
        return false;
    }
    internal void EndObservation() => Interlocked.Decrement(ref _activeObservers);
    internal void Frame(PerformanceFrameObservation frame)
    {
        if (!TryBeginObservation()) return;
        try
        {
            if (!Monitor.TryEnter(_sync)) { ObservationLost(); return; }
            try
            {
                if (IsSealed) return;
                if (_frameCount == _frames.Length || _bytes > _contract.Capture.MaximumBytes - EventBytes)
                { ObservationLost(); return; }
                _frames[_frameCount++] = frame; _bytes += EventBytes;
            }
            finally { Monitor.Exit(_sync); }
            }
        finally { EndObservation(); }
    }
    internal void Binding(PerformanceCycleBinding binding)
    {
        if (!TryBeginObservation()) return;
        try
        {
            if (!Monitor.TryEnter(_sync)) { ObservationLost(); return; }
            try
            {
                if (IsSealed) return;
                if (_bindingCount == _bindings.Length || _bytes > _contract.Capture.MaximumBytes - EventBytes)
                { ObservationLost(); return; }
                _bindings[_bindingCount++] = binding; _bytes += EventBytes;
            }
            finally { Monitor.Exit(_sync); }
            }
        finally { EndObservation(); }
    }
    private void Drop(long sequence)
    {
        Interlocked.Increment(ref _drops);
        Interlocked.CompareExchange(ref _firstMissing, sequence, 0);
    }
    internal (PerformanceEvent[] Events, PerformanceResourceSample[] Samples,
        PerformanceFrameObservation[] Frames, PerformanceCycleBinding[] Bindings) Seal()
    {
        lock (_sync)
        {
            Volatile.Write(ref _sealed, 1);
            // A producer paused anywhere before its finally may still account a drop. Do not wait
            // on that producer and do not claim complete evidence. This uncertainty is sticky.
            if (Volatile.Read(ref _activeObservers) != 0) Volatile.Write(ref _retirementIncomplete, 1);
        }
        // Once sealed, producers cannot write either array. Copies never hold a hot-path lock.
        return (_events.Take(_eventCount).Select(value => value!).ToArray(),
            _samples.Take(_sampleCount).Select(value => value!).ToArray(),
            _frames.Take(_frameCount).Select(value => value!).ToArray(),
            _bindings.Take(_bindingCount).Select(value => value!).ToArray());
    }
}
