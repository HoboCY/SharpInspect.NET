using System.Diagnostics;

namespace SharpInspect.Runtime.Outbox;

/// <summary>A monotonic lifetime and one linearization boundary for claim consumption and retirement.</summary>
internal sealed class OutboxAttemptAuthority : IDisposable
{
    private readonly object _sync = new();
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private readonly TimeSpan _timeout;
    private readonly CancellationToken _stop;
    private readonly CancellationTokenRegistration _registration;
    private bool _retired;

    internal OutboxAttemptAuthority(TimeSpan timeout, CancellationToken stop)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeout = timeout; _stop = stop;
        _registration = stop.Register(Retire);
    }
    internal TimeSpan Remaining
    {
        get
        {
            var remaining = _timeout - Elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
    private TimeSpan Elapsed => TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - _startedAt) / (double)Stopwatch.Frequency);
    private bool CurrentLocked => !_retired && !_stop.IsCancellationRequested && Elapsed < _timeout;
    internal bool IsCurrent { get { lock (_sync) return CurrentLocked; } }
    internal void Retire() { lock (_sync) _retired = true; }
    internal bool TryConsume(ref int consumed)
    {
        lock (_sync)
        {
            if (!CurrentLocked || consumed != 0) return false;
            consumed = 1;
            return true;
        }
    }
    public void Dispose() { Retire(); _registration.Dispose(); }
}
