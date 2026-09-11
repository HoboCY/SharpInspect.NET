using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Qualification;

namespace SharpInspect.Runtime.Plc;

internal sealed record PlcCommunicationTransition(string Kind, string ReasonCode,
    long Generation, Guid? RecoveryCycleId, int Attempt, uint? ControllerEpoch, long ObservedAt);

internal sealed class PlcRequestRevokedException : OperationCanceledException
{
    internal PlcRequestRevokedException() : base("PlcRequestAuthorityRevoked") { }
}

/// <summary>
/// Owns communication evidence, never inspection execution or delivery. A failed
/// generation can only be replaced for diagnosis; it can never feed the old cycle.
/// </summary>
internal sealed class PlcCommunicationOwner : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly IModbusInspectionProfile _profile;
    private readonly PlcCommunicationPolicy _policy;
    private readonly Guid _runtimeEpoch;
    private readonly Action<PlcCommunicationHealth> _publish;
    private readonly Action<string> _revoke;
    private readonly Func<PlcCommunicationTransition, Task> _record;
    private readonly Func<long>? _nextGeneration;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<uint, long> _sent = new();
    private readonly Queue<uint> _sentOrder = new();
    private Task? _watchdog;
    private long _generation = 1;
    private Guid? _recoveryCycle;
    private int _attempt;
    private uint _runtimeHeartbeat;
    private uint? _controllerHeartbeat;
    private uint? _echo;
    private uint? _epoch;
    private long _controllerChangedAt;
    private long _runtimeObservedAt;
    private long _lastHeartbeatWrite;
    private long? _stableSince;
    private bool _synchronized;
    private bool _reachable;
    private bool _recovering;
    private bool _recoveryStarted;
    private bool _stopped;
    private string? _failure;
    private PlcCommunicationHealth? _lastPublished;

    internal PlcCommunicationOwner(IModbusInspectionProfile profile, Guid runtimeEpoch,
        Action<PlcCommunicationHealth> publish, Action<string> revoke,
        Func<PlcCommunicationTransition, Task> record, Func<long>? nextGeneration = null)
    {
        _profile = profile;
        _policy = profile.CommunicationBinding?.Policy ??
            throw new ArgumentException("PlcCommunicationBindingRequired", nameof(profile));
        if (runtimeEpoch == Guid.Empty) throw new ArgumentException("RuntimeEpochRequired", nameof(runtimeEpoch));
        _runtimeEpoch = runtimeEpoch; _publish = publish; _revoke = revoke; _record = record;
        _nextGeneration = nextGeneration;
        _generation = nextGeneration?.Invoke() ?? 1;
    }

    internal string? Failure { get { lock (_sync) return _failure; } }

    // Called inside the channel's transport gate. Starting the physical send
    // and revoking the generation share this one linearization lock.
    internal Task StartCycleWrite(Func<Task> start)
    {
        lock (_sync)
        {
            RequireHealthy();
            if (_recovering) throw new InvalidOperationException("PlcCommunicationGenerationRetired");
            return start();
        }
    }

    internal async Task SynchronizeAsync(ModbusQualificationChannel channel, CancellationToken token)
    {
        await RecordAsync("ConnectionEstablished", "PlcTransportConnected").ConfigureAwait(false);
        await SynchronizeCoreAsync(channel, pending: false, token).ConfigureAwait(false);
        _watchdog = Task.Run(WatchAsync);
    }

    internal async Task<ModbusControllerSignals> ReadAsync(ModbusQualificationChannel channel, CancellationToken token)
    {
        try
        {
            RequireHealthy();
            var signals = await SampleAsync(channel, token).ConfigureAwait(false);
            RequireHealthy();
            return signals;
        }
        catch (PlcRequestRevokedException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!token.IsCancellationRequested && Failure is null)
                Fail(exception.Message is "QualificationControllerEpochChanged" or "PlcControllerHeartbeatStale" or
                    "PlcRuntimeHeartbeatUnobserved" ? exception.Message : "PlcCommunicationTransportLost");
            throw;
        }
    }

    internal void RequireHealthy()
    {
        string? reason;
        lock (_sync)
            reason = _failure ?? (_stopped ? "PlcCommunicationStopped" :
                !_synchronized ? "PlcControllerNotSynchronized" : FreshnessFailure(Stopwatch.GetTimestamp()));
        if (reason is null) return;
        throw new InvalidOperationException(reason);
    }

    // Synchronous invalidation precedes audit or any best-effort network write.
    internal void Fail(string reason)
    {
        lock (_sync)
        {
            if (_failure is not null || _stopped) return;
            _failure = reason; _synchronized = false;
            if (reason == "PlcCommunicationTransportLost") _reachable = false;
        }
        Publish(reason);
        _revoke(reason);
    }

    internal async Task RecoverAsync(ModbusQualificationChannel retiredChannel, bool pending, CancellationToken token)
    {
        lock (_sync)
        {
            if (_recoveryStarted || _stopped) return;
            _recoveryStarted = true; _recovering = true; _synchronized = false;
            _recoveryCycle = Guid.NewGuid();
        }
        await retiredChannel.DisposeAsync().ConfigureAwait(false);
        var reason = Failure ?? "PlcCommunicationInterrupted";
        await RecordAsync(reason == "QualificationControllerEpochChanged" ? "ControllerEpochChanged" :
            reason.Contains("Heartbeat", StringComparison.Ordinal) ? "HeartbeatStale" : "CommunicationLost", reason).ConfigureAwait(false);
        await RecordAsync("RecoveryCycleStarted", "PlcRecoveryCycleStarted").ConfigureAwait(false);
        for (var attempt = 1; attempt <= _policy.MaximumReconnectAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(_policy.ReconnectInterval, token).ConfigureAwait(false);
            lock (_sync)
            {
                _attempt = attempt; _generation = _nextGeneration?.Invoke() ?? checked(_generation + 1); ResetEvidence();
            }
            Publish("PlcReconnectAttempt");
            await RecordAsync("ReconnectAttempt", "PlcReconnectAttempt").ConfigureAwait(false);
            await using var candidate = new ModbusQualificationChannel(_profile);
            var unresolved = pending;
            try
            {
                await candidate.ConnectAsync(token).ConfigureAwait(false);
                await RecordAsync("ConnectionEstablished", "PlcTransportReconnected").ConfigureAwait(false);
                // Fresh heartbeats must not revive a remotely retained Ready.
                // Only revoke permits; never clear/reassert Busy or ResultValid.
                var retained = await candidate.ReadRuntimeStateAsync(token).ConfigureAwait(false);
                unresolved |= retained.Busy || retained.ResultValid || retained.CycleFault || retained.ProtocolViolation;
                if (retained.QualificationReady)
                    await candidate.WriteSingleRegisterAsync(_profile.RuntimeStartAddress, 0, token).ConfigureAwait(false);
                if (retained.ProductionReady)
                    await candidate.WriteSingleRegisterAsync(checked((ushort)(_profile.RuntimeStartAddress + 5)), 0, token).ConfigureAwait(false);
                await SynchronizeCoreAsync(candidate, unresolved, token).ConfigureAwait(false);
                // No Ready, payload, or delivery write belongs to this generation.
                await RecordAsync("RecoveryCompleted", "PlcCommunicationRecoveredArmRequired").ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException && !token.IsCancellationRequested)
            {
                lock (_sync) { _reachable = false; _synchronized = false; }
                await RecordAsync("ReconnectFailed", exception is TimeoutException ?
                    unresolved ? "PlcPendingDeliveryBlocksSynchronization" : "PlcSynchronizationTimedOut" :
                    "PlcReconnectFailed").ConfigureAwait(false);
            }
        }
        Publish("PlcRecoveryAttemptsExhausted");
        await RecordAsync("RecoveryExhausted", "PlcRecoveryAttemptsExhausted").ConfigureAwait(false);
    }

    private async Task SynchronizeCoreAsync(ModbusQualificationChannel channel, bool pending, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_policy.SynchronizationTimeout);
        uint? auditedEpoch = null;
        try
        {
            while (true)
            {
                var signals = await SampleAsync(channel, deadline.Token).ConfigureAwait(false);
                var recipeChangeClear = true;
                if (_profile is ModbusProductionProfile { RecipeChange: not null })
                {
                    var request = await channel.ReadRecipeChangeControllerAsync(deadline.Token).ConfigureAwait(false);
                    var response = await channel.ReadRecipeChangeRuntimeAsync(deadline.Token).ConfigureAwait(false);
                    recipeChangeClear = !request.Request && !request.Acknowledgement && request.RequestSequence == 0 &&
                        request.SelectionCode == 0 && !response.ResponseValid && response.Outcome == 0 && response.Reason == 0 &&
                        response.RequestSequence == 0 && response.SelectionCode == 0;
                }
                var complete = false;
                lock (_sync)
                {
                    var now = Stopwatch.GetTimestamp();
                    var clean = !pending && recipeChangeClear && signals.ControllerEpoch != 0 && !signals.Trigger && !signals.ResultAck &&
                        FreshnessFailure(now) is null;
                    if (!clean || auditedEpoch is { } audited && audited != signals.ControllerEpoch)
                    {
                        _stableSince = null;
                        auditedEpoch = null;
                    }
                    else if (_stableSince is null) _stableSince = now;
                    else if (Elapsed(now, _stableSince.Value) >= _policy.SynchronizationStabilityWindow)
                        complete = true;
                }
                if (complete)
                {
                    if (auditedEpoch is null)
                    {
                        // Audit may stall observation. Its pre-write stability
                        // evidence cannot cover that gap: sample a complete new
                        // stable window after persistence before allowing work.
                        await RecordAsync("SynchronizationWindowObserved", "PlcSynchronizationWindowObserved").ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        auditedEpoch = signals.ControllerEpoch;
                        lock (_sync) _stableSince = null;
                        continue;
                    }
                    lock (_sync) _synchronized = true;
                    Publish(_recovering ? "PlcCommunicationRecoveredArmRequired" : "PlcCommunicationHealthy");
                    return;
                }
                Publish(pending ? "PlcPendingDeliveryBlocksSynchronization" : "PlcControllerSynchronizing");
                await Task.Delay(_policy.PollInterval, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("PlcSynchronizationTimedOut"); }
    }

    private async Task<ModbusControllerSignals> SampleAsync(ModbusQualificationChannel channel, CancellationToken token)
    {
        var now = Stopwatch.GetTimestamp();
        bool write;
        uint heartbeat;
        long generation;
        lock (_sync)
        {
            if (_stopped || !_recovering && _failure is not null)
                throw new InvalidOperationException(_failure ?? "PlcCommunicationStopped");
            write = _lastHeartbeatWrite == 0 || Elapsed(now, _lastHeartbeatWrite) >= _policy.RuntimeHeartbeatInterval;
            heartbeat = write ? unchecked(++_runtimeHeartbeat) : _runtimeHeartbeat;
            generation = _generation;
        }
        if (write)
        {
            await channel.WriteHeartbeatAsync(_runtimeEpoch, heartbeat, token).ConfigureAwait(false);
            lock (_sync)
            {
                if (generation != _generation || _stopped || !_recovering && _failure is not null)
                    throw new InvalidOperationException(_failure ?? "PlcCommunicationGenerationRetired");
                _lastHeartbeatWrite = Stopwatch.GetTimestamp();
                _sent[heartbeat] = _lastHeartbeatWrite; _sentOrder.Enqueue(heartbeat);
                while (_sentOrder.Count > 10000) _sent.Remove(_sentOrder.Dequeue());
            }
        }
        // The heartbeat block and handshake block are independent Modbus
        // reads. Bracket heartbeat evidence with the controller-owned epoch.
        var epochBefore = await channel.ReadControllerEpochAsync(token).ConfigureAwait(false);
        var communication = await channel.ReadCommunicationAsync(token).ConfigureAwait(false);
        var signals = await channel.ReadAsync(token).ConfigureAwait(false);
        bool epochChanged;
        lock (_sync)
        {
            if (generation != _generation || _stopped || !_recovering && _failure is not null)
                throw new InvalidOperationException(_failure ?? "PlcCommunicationGenerationRetired");
            now = Stopwatch.GetTimestamp();
            _reachable = true;
            epochChanged = epochBefore != signals.ControllerEpoch ||
                _epoch is { } previous && signals.ControllerEpoch != previous;
            if (epochChanged)
            {
                _stableSince = null; _controllerHeartbeat = null; _echo = null;
                _controllerChangedAt = 0; _runtimeObservedAt = 0;
            }
            if (epochChanged && _synchronized && !_recovering)
            {
                _epoch = signals.ControllerEpoch;
                throw new InvalidOperationException("QualificationControllerEpochChanged");
            }
            _epoch = signals.ControllerEpoch;
            if (epochBefore == signals.ControllerEpoch)
            {
                if (communication.RuntimeEpochEcho != _runtimeEpoch)
                    _runtimeObservedAt = 0;
                if (_controllerHeartbeat is { } old && communication.ControllerHeartbeat != old)
                    _controllerChangedAt = now;
                _controllerHeartbeat = communication.ControllerHeartbeat;
                if (communication.RuntimeEpochEcho == _runtimeEpoch &&
                    _sent.TryGetValue(communication.RuntimeHeartbeatEcho, out var sentAt) &&
                    (_echo is null || IsAfter(communication.RuntimeHeartbeatEcho, _echo.Value)))
                {
                    // A first echo is only a baseline. Later echoes must name a
                    // heartbeat actually written by this particular generation.
                    if (_echo is not null) _runtimeObservedAt = sentAt;
                    _echo = communication.RuntimeHeartbeatEcho;
                }
            }
        }
        if (epochChanged)
            await RecordAsync("ControllerEpochObserved", "PlcControllerEpochObserved").ConfigureAwait(false);
        return signals;
    }

    private async Task WatchAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(_policy.PollInterval, _stop.Token).ConfigureAwait(false);
                string? reason;
                lock (_sync) reason = !_recovering && _synchronized && _failure is null ?
                    FreshnessFailure(Stopwatch.GetTimestamp()) : null;
                if (reason is not null) Fail(reason);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private string? FreshnessFailure(long now) =>
        _controllerChangedAt == 0 || Elapsed(now, _controllerChangedAt) >= _policy.ControllerHeartbeatStaleAfter
            ? "PlcControllerHeartbeatStale" :
        _runtimeObservedAt == 0 || Elapsed(now, _runtimeObservedAt) >= _policy.RuntimeHeartbeatStaleAfter
            ? "PlcRuntimeHeartbeatUnobserved" : null;

    private void ResetEvidence()
    {
        _reachable = false; _synchronized = false; _epoch = null; _controllerHeartbeat = null; _echo = null;
        _controllerChangedAt = 0; _runtimeObservedAt = 0; _lastHeartbeatWrite = 0; _stableSince = null;
        _sent.Clear(); _sentOrder.Clear();
    }

    private void Publish(string reason)
    {
        PlcCommunicationHealth value;
        lock (_sync)
        {
            var now = Stopwatch.GetTimestamp();
            value = new(_reachable, _controllerChangedAt != 0 && Elapsed(now, _controllerChangedAt) < _policy.ControllerHeartbeatStaleAfter,
                _runtimeObservedAt != 0 && Elapsed(now, _runtimeObservedAt) < _policy.RuntimeHeartbeatStaleAfter,
                _epoch, _synchronized, _failure is not null || _recovering, _attempt, _generation, reason)
                { PolicyHash = _policy.ContentHash, RuntimeEpoch = _runtimeEpoch };
            if (value == _lastPublished) return;
            _lastPublished = value;
        }
        _publish(value);
    }

    private Task RecordAsync(string kind, string reason)
    {
        PlcCommunicationTransition value;
        lock (_sync) value = new(kind, reason, _generation, _recoveryCycle, _attempt, _epoch, Stopwatch.GetTimestamp());
        return _record(value);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync) { if (_stopped) return; _stopped = true; _reachable = false; _synchronized = false; }
        _stop.Cancel();
        if (_watchdog is not null) await _watchdog.ConfigureAwait(false);
        Publish(_lastPublished?.ReasonCode == "PlcRecoveryAttemptsExhausted" ?
            "PlcRecoveryAttemptsExhausted" : _recoveryStarted ? "PlcRecoveryOwnerRetired" : "PlcCommunicationStopped");
        await RecordAsync("CommunicationStopped", "PlcCommunicationStopped").ConfigureAwait(false);
        _stop.Dispose();
    }

    private static bool IsAfter(uint candidate, uint previous) => unchecked(candidate - previous) is > 0 and < 0x80000000;
    private static TimeSpan Elapsed(long now, long then) => TimeSpan.FromSeconds((now - then) / (double)Stopwatch.Frequency);
}
