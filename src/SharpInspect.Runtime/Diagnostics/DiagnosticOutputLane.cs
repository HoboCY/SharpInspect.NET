using System.Diagnostics;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Diagnostics;

/// <summary>One fixed physical worker, with in-flight work charged until actual retirement.</summary>
internal sealed class DiagnosticOutputLane
{
    private readonly object _sync = new();
    private readonly DiagnosticQueueBudget _budget;
    private readonly Func<DiagnosticEnvelope, ValueTask> _write;
    private readonly Func<ValueTask>? _close;
    private readonly Action? _fault;
    private readonly Queue<(DiagnosticEnvelope Item, bool Reserved)> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly TaskCompletionSource<bool> _retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _normalCount, _reservedCount, _physical;
    private long _normalBytes, _reservedBytes, _started, _written, _dropped, _failures;
    private bool _stopping;
    private bool _signalDisposed;
    private DiagnosticSinkState _state = DiagnosticSinkState.Healthy;
    private string _reason = "DiagnosticSinkHealthy";

    internal DiagnosticOutputLane(DiagnosticQueueBudget budget, Func<DiagnosticEnvelope, ValueTask> write,
        Func<ValueTask>? close = null, Action? fault = null)
    {
        _budget = budget; _write = write; _close = close; _fault = fault;
        // A callback that blocks before returning a ValueTask still occupies this same fixed
        // thread. No per-event Task.Run, replacement thread or cancellation-based slot release.
        new Thread(Run) { IsBackground = true, Name = "SharpInspect.Diagnostics" }.Start();
    }

    internal bool TryWrite(DiagnosticEnvelope item)
    {
        if (!Monitor.TryEnter(_sync)) { Interlocked.Increment(ref _dropped); return false; }
        try
        {
            CheckTimeoutLocked();
            if (_stopping || _state != DiagnosticSinkState.Healthy) { Interlocked.Increment(ref _dropped); return false; }
            var reserved = false;
            if (_normalCount >= _budget.NormalRecords || item.Line.Length > _budget.NormalBytes - _normalBytes)
            {
                if (item.Record.Level < _budget.ReservationLevel || _reservedCount >= _budget.ReservedRecords ||
                    item.Line.Length > _budget.ReservedBytes - _reservedBytes)
                { Interlocked.Increment(ref _dropped); return false; }
                reserved = true;
            }
            Charge(item, reserved, 1); _queue.Enqueue((item, reserved)); _signal.Set(); return true;
        }
        finally { Monitor.Exit(_sync); }
    }

    private void Charge(DiagnosticEnvelope item, bool reserved, int direction)
    {
        if (direction > 0) item.Capture?.AddReference(); else item.Capture?.ReleaseReference();
        if (reserved) { _reservedCount += direction; _reservedBytes += direction * (long)item.Line.Length; }
        else { _normalCount += direction; _normalBytes += direction * (long)item.Line.Length; }
    }

    private void CheckTimeoutLocked()
    {
        if (_physical != 0 && _state == DiagnosticSinkState.Healthy &&
            (Stopwatch.GetTimestamp() - _started) / (double)Stopwatch.Frequency >= _budget.WriteTimeout.TotalSeconds)
            FailLocked("DiagnosticSinkTimeout");
    }

    private void FailLocked(string reason)
    {
        _state = DiagnosticSinkState.Unavailable; _reason = reason; _failures++;
        _fault?.Invoke(); // Runtime supplies only a callback-free atomic capture revocation.
        while (_queue.TryDequeue(out var pending))
        { Charge(pending.Item, pending.Reserved, -1); Interlocked.Increment(ref _dropped); }
        _stopping = true; _signal.Set();
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                (DiagnosticEnvelope Item, bool Reserved) work;
                lock (_sync)
                {
                    if (!_queue.TryDequeue(out work))
                    { if (_stopping) break; work = default; }
                    else { _physical = 1; _started = Stopwatch.GetTimestamp(); }
                }
                if (work.Item is null) { _signal.WaitOne(); continue; }
                var failed = false;
                try { _write(work.Item).AsTask().GetAwaiter().GetResult(); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { failed = true; }
                lock (_sync)
                {
                    CheckTimeoutLocked();
                    _physical = 0; Charge(work.Item, work.Reserved, -1);
                    if (failed && _state != DiagnosticSinkState.Unavailable) FailLocked("DiagnosticSinkWriteFailed");
                    if (!failed) _written++;
                }
            }
        }
        finally
        {
            try { _close?.Invoke().AsTask().GetAwaiter().GetResult(); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { lock (_sync) { _state = DiagnosticSinkState.Unavailable; _reason = "DiagnosticSinkCloseFailed"; _failures++; _fault?.Invoke(); } }
            lock (_sync)
            {
                if (_state != DiagnosticSinkState.Unavailable) { _state = DiagnosticSinkState.Stopped; _reason = "DiagnosticSinkStopped"; }
                _signalDisposed = true; _signal.Dispose();
            }
            _retired.TrySetResult(true);
        }
    }

    internal DiagnosticSinkHealth ReadHealth()
    {
        lock (_sync)
        {
            CheckTimeoutLocked();
            return new(_state, _reason, _normalCount + _reservedCount, _normalBytes + _reservedBytes,
                _physical, _written, Interlocked.Read(ref _dropped), _failures);
        }
    }
    internal Task Stop()
    { lock (_sync) { _stopping = true; if (!_signalDisposed) _signal.Set(); return _retired.Task; } }
}
