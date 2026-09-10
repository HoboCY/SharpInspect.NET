namespace SharpInspect.Runtime.Cycles;

/// <summary>
/// Serializes partial state changes against the actual output image. A delayed
/// protocol-violation observation can set its own bit without restoring stale
/// Busy/ResultValid values over a newer publication or acknowledgement.
/// </summary>
internal sealed class InspectionCycleOutputLatch
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<bool, bool, bool, bool, bool, CancellationToken, Task> _write;
    private bool _ready, _busy, _valid, _fault, _violation;
    private bool _unavailable;
    internal InspectionCycleOutputLatch(Func<bool, bool, bool, bool, bool, CancellationToken, Task> write) => _write = write;
    internal bool? ConfirmedResultValid => Volatile.Read(ref _unavailable) ? null : Volatile.Read(ref _valid);

    internal async Task ChangeAsync(CancellationToken token, bool? ready = null, bool? busy = null,
        bool? valid = null, bool? fault = null, bool? violation = null)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_unavailable) throw new InvalidOperationException("InspectionCycleOutputStateUncertain");
            var nextReady = ready ?? _ready; var nextBusy = busy ?? _busy; var nextValid = valid ?? _valid;
            var nextFault = fault ?? _fault; var nextViolation = violation ?? _violation;
            try
            {
                await _write(nextReady, nextBusy, nextValid, nextFault, nextViolation, token).ConfigureAwait(false);
            }
            catch
            {
                // A failed response cannot establish whether the remote write
                // happened. Seal this owner; never clear or replay an uncertain
                // ResultValid through a later partial state update.
                _unavailable = true;
                throw;
            }
            _ready = nextReady; _busy = nextBusy; _valid = nextValid;
            _fault = nextFault; _violation = nextViolation;
        }
        finally { _gate.Release(); }
    }
}
