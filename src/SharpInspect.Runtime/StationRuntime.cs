using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime;

/// <summary>
/// The initial, deliberately unconfigured station authority. Later tickets supply governed
/// capabilities; no host option can assert that a missing production gate passed.
/// </summary>
public sealed class StationRuntime : IStationRuntime, IAsyncDisposable
{
    private const int MaximumSubscribers = 64;
    private readonly object _sync = new();
    private readonly List<Channel<StationStateSnapshot>> _subscribers = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _heartbeat;
    private StationStateSnapshot _snapshot;
    private bool _disposed;

    public StationRuntime(TimeSpan? heartbeatInterval = null)
    {
        var interval = heartbeatInterval ?? TimeSpan.FromSeconds(1);
        if (interval < TimeSpan.FromMilliseconds(20) || interval > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));

        _snapshot = new StationStateSnapshot(Guid.NewGuid(), 1, DateTimeOffset.UtcNow,
            RuntimeLifecycle.Running, ExclusiveMode.None, ProductionArmState.Disarmed, false, false,
            HandshakePhase.Unknown, RecoveryState.Required, null, null,
            new CameraHealth(HealthState.Unconfigured, HealthState.Unknown, HealthState.Unknown, HealthState.Unknown),
            new PlcHealth(HealthState.Unconfigured, HealthState.Unknown, HealthState.Unknown),
            new SubsystemHealth(HealthState.Unconfigured, "TraceStoreMissing"),
            new EvidenceHealth(HealthState.Unknown, 0, 0),
            new QualificationState(QualificationMatch.Missing, QualificationMatch.Missing,
                QualificationMatch.Missing, QualificationMatch.Missing),
            new PerformanceHealth(HealthState.Unknown, false),
            new AlarmSummary(0, 0, false),
            new InteractiveSession(InteractiveSessionState.Unauthenticated, null, null), null,
            new AdmissionBlockers(new[]
            {
                "DeploymentPoliciesMissing", "CameraBindingMissing", "PlcBindingMissing", "TraceStoreMissing",
                "ActiveRecipeMissing", "AuthorizationUnavailable", "StartupRecoveryNotVerified",
                "FrameworkQualificationMissing", "ProviderQualificationMissing", "PerformanceQualificationMissing",
                "StationAcceptanceMissing", "ProductionCycleUnavailable"
            }));
        _heartbeat = PublishHeartbeatAsync(interval);
    }

    public ValueTask<StationStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) return ValueTask.FromResult(_snapshot);
    }

    public async IAsyncEnumerable<StationStateSnapshot> WatchSnapshotsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<StationStateSnapshot>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        lock (_sync)
        {
            if (_subscribers.Count >= MaximumSubscribers)
                throw new InvalidOperationException("SnapshotSubscriberLimit");
            channel.Writer.TryWrite(_snapshot);
            if (_disposed) channel.Writer.TryComplete();
            else _subscribers.Add(channel);
        }
        try
        {
            await foreach (var snapshot in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return snapshot;
        }
        finally
        {
            lock (_sync) _subscribers.Remove(channel);
        }
    }

    public ValueTask<RuntimeCommandOutcome> SubmitAsync(RuntimeCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            RuntimeCommandOutcome Reject(string code) => new(command.CorrelationId, CommandDisposition.Rejected, code);
            if (_disposed) return ValueTask.FromResult(Reject("RuntimeStopped"));
            if (command.CorrelationId == Guid.Empty || command.Invocation is null ||
                !Enum.IsDefined(typeof(CommandSource), command.Invocation.Source))
                return ValueTask.FromResult(Reject("InvalidCommandContext"));
            if (_snapshot.LastCommand?.CorrelationId == command.CorrelationId)
                return ValueTask.FromResult(Reject("DuplicateCorrelationId"));

            switch (command)
            {
                case ArmProductionCommand:
                    return ValueTask.FromResult(Reject(_snapshot.AdmissionBlockers[0]));
                case GracefulProductionStopCommand when command.Invocation.Source != CommandSource.PhysicalConsole:
                    return ValueTask.FromResult(Reject("LocalConsoleRequired"));
                case GracefulProductionStopCommand when _snapshot.LastCommand?.State == OperationState.Pending:
                    return ValueTask.FromResult(Reject("OperationInProgress"));
                case GracefulProductionStopCommand when _snapshot.LastCommand?.State == OperationState.Completed:
                    return ValueTask.FromResult(Reject("AlreadyLocallyDisarmed"));
                case GracefulProductionStopCommand:
                    // This unconfigured host can admit only its first local Stop. Its ID and
                    // terminal state suffice for bounded in-process deduplication. Rejected Arm
                    // requests consume no ledger capacity. Durable command history is separate.
                    PublishLocked(_snapshot with
                    {
                        Ready = false,
                        ArmState = ProductionArmState.Disarmed,
                        LastCommand = new CommandProgress(command.CorrelationId, OperationState.Pending, "StopAdmitted")
                    });
                    return ValueTask.FromResult(new RuntimeCommandOutcome(command.CorrelationId,
                        CommandDisposition.Accepted, "StopAdmitted"));
                default:
                    return ValueTask.FromResult(Reject("UnsupportedCommand"));
            }
        }
    }

    private async Task PublishHeartbeatAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                lock (_sync)
                {
                    if (_disposed) return;
                    var next = _snapshot;
                    if (next.LastCommand is { State: OperationState.Pending } pending)
                    {
                        // No cycle or device has been admitted by this ticket. Confirm the local
                        // disarmed boundary only; unresolved startup/PLC recovery remains unknown.
                        next = next with { LastCommand = pending with
                            { State = OperationState.Completed, ReasonCode = "LocallyDisarmed" } };
                    }
                    PublishLocked(next);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void PublishLocked(StationStateSnapshot next)
    {
        _snapshot = next with { Revision = checked(_snapshot.Revision + 1), ObservedAtUtc = DateTimeOffset.UtcNow };
        foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(_snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            var lastCommand = _snapshot.LastCommand;
            if (lastCommand is { State: OperationState.Pending })
                lastCommand = lastCommand with { State = OperationState.Failed, ReasonCode = "RuntimeStopped" };
            PublishLocked(_snapshot with { Lifecycle = RuntimeLifecycle.Stopped, Ready = false,
                ArmState = ProductionArmState.Disarmed, LastCommand = lastCommand });
            foreach (var subscriber in _subscribers) subscriber.Writer.TryComplete();
            _subscribers.Clear();
        }
        _lifetime.Cancel();
        await _heartbeat.ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
