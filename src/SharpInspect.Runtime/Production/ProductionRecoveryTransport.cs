using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;

namespace SharpInspect.Runtime.Production;

/// <summary>
/// A single manually authorized cleanup attempt on the frozen endpoint. It has
/// no result encoder, payload publisher, inspection observer, or automatic retry.
/// The caller must keep its durable recovery authorization and safety fence live
/// until the completion transaction has settled and this owner has retired.
/// </summary>
internal sealed class ProductionRecoveryTransport : IAsyncDisposable
{
    private readonly ModbusQualificationChannel _channel;
    private readonly PlcCommunicationOwner _communication;
    private readonly Func<string?> _authorityFailure;
    private readonly Func<string?> _completionAuthorityFailure;
    private PlcCommunicationHealth? _health;
    private int _attemptClaimed;
    private int _disposed;

    internal ProductionRecoveryTransport(ModbusProductionProfile profile, Guid runtimeEpoch,
        Func<Func<Task>, Task> startOwnedRequest, Func<string?> authorityFailure,
        Action<PlcCommunicationHealth> publish, Func<PlcCommunicationTransition, Task> record,
        Func<long> nextGeneration, Func<string?>? completionAuthorityFailure = null)
    {
        ArgumentNullException.ThrowIfNull(startOwnedRequest);
        _authorityFailure = authorityFailure ?? throw new ArgumentNullException(nameof(authorityFailure));
        _completionAuthorityFailure = completionAuthorityFailure ?? _authorityFailure;
        _channel = new(profile, startOwnedRequest: startOwnedRequest);
        _communication = new(profile, runtimeEpoch, health =>
        {
            Volatile.Write(ref _health, health);
            publish(health);
        }, _ => { }, record, nextGeneration);
    }

    internal async Task<ProductionRecoveryTransportObservation> ClearAndSynchronizeAsync(
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new InvalidOperationException("ProductionRecoveryTransportRetired");
        if (Interlocked.CompareExchange(ref _attemptClaimed, 1, 0) != 0)
            throw new InvalidOperationException("ProductionRecoveryAttemptAlreadyStarted");
        RequireAuthority();
        await _channel.ConnectAsync(cancellationToken).ConfigureAwait(false);
        RequireAuthority();
        // This explicit primitive fences even a low-state write, which normal
        // best-effort fault exits deliberately allow after cycle revocation.
        await _channel.ClearProductionRecoveryStateAsync(cancellationToken).ConfigureAwait(false);
        var cleared = await _channel.ReadRuntimeStateAsync(cancellationToken).ConfigureAwait(false);
        RequireClear(cleared);
        // The normal automatic communication-recovery path never reaches here.
        // The outer manual owner has already persisted the physical disposition.
        await _communication.SynchronizeAsync(_channel, cancellationToken).ConfigureAwait(false);
        RequireAuthority();
        _communication.RequireHealthy();
        var final = await _channel.ReadRuntimeStateAsync(cancellationToken).ConfigureAwait(false);
        RequireClear(final);
        var controller = await _communication.ReadAsync(_channel, cancellationToken).ConfigureAwait(false);
        if (controller.Trigger || controller.ResultAck)
            throw new InvalidOperationException("ProductionRecoveryControllerInputsNotClear");
        _communication.RequireHealthy();
        return new(Volatile.Read(ref _health) ?? throw new InvalidOperationException("ProductionRecoverySynchronizationMissing"),
            controller.ControllerEpoch, controller.CycleSequence, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
    }

    internal string? CompletionFailure()
    {
        if (_completionAuthorityFailure() is { } reason) return reason;
        try { _communication.RequireHealthy(); return null; }
        catch (InvalidOperationException exception) { return exception.Message; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await _communication.DisposeAsync().ConfigureAwait(false); }
        finally { await _channel.DisposeAsync().ConfigureAwait(false); }
    }

    private void RequireAuthority()
    {
        if (_authorityFailure() is { } reason) throw new InvalidOperationException(reason);
    }

    private static void RequireClear(ModbusRuntimeSignals value)
    {
        if (value.QualificationReady || value.ProductionReady || value.Busy || value.ResultValid ||
            value.CycleFault || value.ProtocolViolation)
            throw new InvalidOperationException("ProductionRecoveryOutputsNotClear");
    }
}

internal sealed record ProductionRecoveryTransportObservation(PlcCommunicationHealth Communication,
    uint ControllerEpoch, uint CycleSequence, DateTimeOffset ObservedAtUtc, long ObservedMonotonicTimestamp);
