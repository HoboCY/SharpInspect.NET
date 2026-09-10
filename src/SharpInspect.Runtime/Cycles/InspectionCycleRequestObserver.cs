using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;

namespace SharpInspect.Runtime.Cycles;

/// <summary>
/// Continuously observes controller-owned signals while a cycle is computing,
/// committing or awaiting acknowledgement. Only an observed fresh edge in the
/// accepting state can occupy the single admission slot. Rejections are facts,
/// never deferred work. The bounded fact queue faults instead of dropping data.
/// </summary>
internal sealed class InspectionCycleRequestObserver : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Func<CancellationToken, Task<ModbusControllerSignals>> _read;
    private readonly Action _onAccepted;
    private readonly Action<string> _onFailure;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop;
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private readonly HashSet<PlcControllerCycle> _seen;
    private readonly Queue<RejectedCycleRequest> _rejections = new();
    private Task? _operation;
    private Task? _retirement;
    private ModbusControllerSignals? _latest;
    private ModbusControllerSignals? _accepted;
    private bool _accepting;
    private bool _wasHigh = true; // A held request at connection is not a fresh trigger.
    private PlcControllerCycle? _previousKey;
    private uint? _activeEpoch;
    private string? _failure;
    private bool _closing;
    private bool _admissionRevoked;
    private long _observationSequence;
    private long _ackObservationSequence;
    private long _ackObservedAt;

    internal InspectionCycleRequestObserver(Func<CancellationToken, Task<ModbusControllerSignals>> read,
        TimeSpan interval, IEnumerable<PlcControllerCycle> knownKeys, Action onAccepted,
        Action<string> onFailure, CancellationToken cancellationToken)
    {
        _read = read; _interval = interval; _onAccepted = onAccepted; _onFailure = onFailure;
        _seen = new(knownKeys); _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    internal void Start()
    {
        lock (_sync)
        {
            if (_closing || _operation is not null) throw new InvalidOperationException("InspectionCycleObserverAlreadyStarted");
            _operation = Task.Run(PollAsync);
        }
    }
    internal void StopAccepting() { lock (_sync) _accepting = false; }
    internal void RevokeAdmission()
    {
        lock (_sync)
        {
            _admissionRevoked = true; _accepting = false;
            if (_accepted is { } pending)
            {
                // This was only an observed candidate, not a durably accepted
                // Run. Retain a rejection fact if Stop wins the admission race.
                if (_rejections.Count < 256)
                    _rejections.Enqueue(new(new(pending.ControllerEpoch, pending.CycleSequence),
                        "QualificationRequestRevokedBeforeAdmission"));
                else _failure = "InspectionCycleProtocolFactCapacityExceeded";
                _accepted = null;
            }
        }
    }
    internal void CompleteCycle() { lock (_sync) _activeEpoch = null; }
    internal async Task EnableAcceptingAsync(Func<Task> advertiseReady, CancellationToken token)
    {
        // Serialize the physical Ready write with observation, so a controller
        // that reacts immediately cannot lose its request between the write
        // acknowledgement and enabling the local admission slot.
        await _sampleGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ThrowIfFailed();
                if (_closing || _admissionRevoked) throw new OperationCanceledException("InspectionCycleObserverStopped");
                if (_latest is not { ResultAck: false })
                    throw new InvalidOperationException("QualificationInitialResultAckNotClear");
            }
            await advertiseReady().ConfigureAwait(false);
            lock (_sync)
            {
                ThrowIfFailed();
                if (_closing || _admissionRevoked || _stop.IsCancellationRequested)
                    throw new OperationCanceledException("InspectionCycleObserverStopped");
                _accepting = true;
            }
        }
        finally { _sampleGate.Release(); }
    }
    internal (ModbusControllerSignals? Signals, long Sequence, long AckSequence, long AckObservedAt) Latest
    { get { lock (_sync) return (_latest, _observationSequence, _ackObservationSequence, _ackObservedAt); } }

    internal async Task<(long Sequence, long StartedAt)> WriteAcknowledgementStateAsync(
        bool previousAck, Func<Task> write, CancellationToken token)
    {
        await _sampleGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            long sequence;
            lock (_sync)
            {
                ThrowIfFailed();
                if (_closing || _stop.IsCancellationRequested)
                    throw new OperationCanceledException("InspectionCycleObserverStopped");
                if (_latest is null || _latest.ResultAck != previousAck)
                    throw new InvalidOperationException("QualificationPrematureResultAck");
                sequence = _observationSequence;
            }
            await write().ConfigureAwait(false);
            // Sampling cannot race this successful physical flag write. An Ack
            // must subsequently be observed in this particular bounded window.
            return (sequence, Stopwatch.GetTimestamp());
        }
        finally { _sampleGate.Release(); }
    }
    internal bool TryTakeAccepted(out ModbusControllerSignals? signals)
    {
        lock (_sync) { ThrowIfFailed(); signals = _accepted; _accepted = null; return signals is not null; }
    }
    internal RejectedCycleRequest? TakeRejected()
    { lock (_sync) { ThrowIfFailed(); return _rejections.Count > 0 ? _rejections.Dequeue() : null; } }
    internal void RequireHealthy() { lock (_sync) ThrowIfFailed(); }
    private void ThrowIfFailed()
    { if (_failure is not null) throw new InvalidOperationException(_failure); }

    private async Task PollAsync()
    {
        try
        {
            while (true)
            {
                await _sampleGate.WaitAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    var signals = await _read(_stop.Token).ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (_closing || _stop.IsCancellationRequested)
                            throw new OperationCanceledException("InspectionCycleObserverStopped");
                        _observationSequence = checked(_observationSequence + 1);
                        if (_latest is null || _latest.ResultAck != signals.ResultAck ||
                            _latest.ControllerEpoch != signals.ControllerEpoch || _latest.CycleSequence != signals.CycleSequence)
                        {
                            _ackObservationSequence = _observationSequence;
                            _ackObservedAt = Stopwatch.GetTimestamp();
                        }
                        _latest = signals;
                        if (_activeEpoch is { } activeEpoch && signals.ControllerEpoch != activeEpoch)
                            throw new InvalidOperationException("QualificationControllerEpochChanged");
                        var key = new PlcControllerCycle(signals.ControllerEpoch, signals.CycleSequence);
                        if (signals.Trigger && (!_wasHigh || _previousKey != key))
                        {
                            var repeated = !_seen.Add(key);
                            var invalidIdentity = signals.ControllerEpoch == 0 ? "QualificationStimulusControllerEpochInvalid" :
                                signals.CycleSequence == 0 ? "QualificationStimulusCycleSequenceInvalid" : null;
                            if (_seen.Count > 10000) throw new InvalidOperationException("InspectionCycleObservedKeyCapacityExceeded");
                            if (_accepting && !signals.ResultAck && !repeated && !_wasHigh && invalidIdentity is null)
                            {
                                _accepting = false;
                                _accepted = signals;
                                _activeEpoch = signals.ControllerEpoch;
                                _onAccepted();
                            }
                            else
                            {
                                if (_rejections.Count >= 256)
                                    throw new InvalidOperationException("InspectionCycleProtocolFactCapacityExceeded");
                                _rejections.Enqueue(new(key, invalidIdentity ?? (repeated ? "QualificationDuplicateCycleRejected" :
                                    "QualificationTriggerRejectedWhileNotReady")));
                            }
                        }
                        _wasHigh = signals.Trigger;
                        _previousKey = key;
                    }
                }
                finally { _sampleGate.Release(); }
                await Task.Delay(_interval, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_closing || _stop.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var reason = exception.Message == "QualificationControllerEpochChanged" ?
                "QualificationControllerEpochChanged" : "QualificationControllerObservationFailed";
            lock (_sync) _failure = reason;
            _onFailure(reason);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _closing = true; _accepting = false; _accepted = null;
            return new(_retirement ??= Task.Run(RetireAsync));
        }
    }

    private async Task RetireAsync()
    {
        _stop.Cancel();
        if (_operation is not null) await _operation.ConfigureAwait(false);
        // An in-flight Ready advertisement owns this same gate. Await its actual
        // completion before retiring; it checks _closing before enabling any
        // request. Keep the private managed gate alive for already queued callers.
        await _sampleGate.WaitAsync().ConfigureAwait(false);
        _sampleGate.Release();
        _stop.Dispose();
    }
}

internal sealed record RejectedCycleRequest(PlcControllerCycle Key, string ReasonCode);
