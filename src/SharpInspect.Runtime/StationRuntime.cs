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
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly ICommandAuditWriter? _audit;
    private readonly Task _storeInitialization;
    private Task? _completion;
    private Task? _shutdown;
    private CommandAuditFact? _pendingAudit;
    private bool _shutdownRequested;
    private bool _auditFault;
    private bool _storeReady;
    private int _queuedCommands;
    private StationStateSnapshot _snapshot;
    private bool _disposed;

    public StationRuntime(TimeSpan? heartbeatInterval = null) : this(null, heartbeatInterval) { }

    internal StationRuntime(ICommandAuditWriter? audit, TimeSpan? heartbeatInterval = null)
    {
        _audit = audit;
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
        _storeInitialization = InitializeStoreAsync();
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

    public async ValueTask<RuntimeCommandOutcome> SubmitAsync(RuntimeCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        var attempt = Guid.NewGuid();
        RuntimeCommandOutcome Unavailable(string reason) => new(command.CorrelationId,
            CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, attempt);
        lock (_sync)
        {
            if (_shutdownRequested || _disposed) return Unavailable("RuntimeStopped");
        }
        if (Interlocked.Increment(ref _queuedCommands) > 64)
        {
            Interlocked.Decrement(ref _queuedCommands);
            MarkAuditFault("CommandQueueFull");
            return Unavailable("CommandQueueFull");
        }
        var deadline = new StoreDeadline(_audit?.CommitTimeout ?? TimeSpan.FromSeconds(2));
        var entered = false;
        try
        {
            entered = await _commandGate.WaitAsync(PositiveRemaining(deadline), cancellationToken).ConfigureAwait(false);
            if (!entered)
            {
                MarkAuditFault("CommandDeadlineExceeded");
                return Unavailable("CommandDeadlineExceeded");
            }
            await _storeInitialization.WaitAsync(PositiveRemaining(deadline), cancellationToken).ConfigureAwait(false);
            RuntimeCommandOutcome decision;
            lock (_sync)
            {
                if (_shutdownRequested || _disposed) return Unavailable("RuntimeStopped");
                decision = DecideLocked(command);
            }
            var fact = CreateFact(command, attempt, decision);
            var result = _audit is null || !_storeReady
                ? new StoreWriteResult(false, "TraceStoreUnavailable")
                : await _audit.AppendAsync(fact, deadline, cancellationToken).ConfigureAwait(false);
            if (!result.Committed || result.Fact is null)
            {
                MarkAuditFault(result.ReasonCode);
                return Unavailable("TraceAuditUnavailable");
            }
            fact = result.Fact;
            var outcome = new RuntimeCommandOutcome(fact.CorrelationId, fact.Disposition!.Value,
                fact.ReasonCode, AuditPersistence.Persisted, fact.AttemptId);
            lock (_sync)
            {
                if (outcome.Disposition == CommandDisposition.Accepted)
                {
                    _pendingAudit = fact;
                    _completion = null;
                    PublishLocked(_snapshot with
                    {
                        Ready = false,
                        ArmState = ProductionArmState.Disarmed,
                        LastCommand = new CommandProgress(command.CorrelationId, OperationState.Pending, outcome.ReasonCode)
                    });
                }
            }
            return outcome;
        }
        catch (TimeoutException)
        {
            MarkAuditFault("TraceCommitDeadlineExceeded");
            return Unavailable("CommandDeadlineExceeded");
        }
        finally
        {
            if (entered) _commandGate.Release();
            Interlocked.Decrement(ref _queuedCommands);
        }
    }

    private RuntimeCommandOutcome DecideLocked(RuntimeCommand command)
    {
        RuntimeCommandOutcome Reject(string code) => new(command.CorrelationId, CommandDisposition.Rejected, code);
        if (command.CorrelationId == Guid.Empty || command.Invocation is null ||
            !Enum.IsDefined(typeof(CommandSource), command.Invocation.Source) || command.Invocation.PrincipalId?.Length > 256)
            return Reject("InvalidCommandContext");
        if (_snapshot.LastCommand?.CorrelationId == command.CorrelationId) return Reject("DuplicateCorrelationId");
        return command switch
        {
            ArmProductionCommand => Reject(_snapshot.AdmissionBlockers[0]),
            GracefulProductionStopCommand when command.Invocation.Source != CommandSource.PhysicalConsole => Reject("LocalConsoleRequired"),
            GracefulProductionStopCommand when _snapshot.LastCommand?.State == OperationState.Pending => Reject("OperationInProgress"),
            GracefulProductionStopCommand when _snapshot.LastCommand?.State == OperationState.Completed => Reject("AlreadyLocallyDisarmed"),
            GracefulProductionStopCommand => new(command.CorrelationId, CommandDisposition.Accepted, "StopAdmitted"),
            _ => Reject("UnsupportedCommand")
        };
    }

    private CommandAuditFact CreateFact(RuntimeCommand command, Guid attempt, RuntimeCommandOutcome outcome) =>
        new(Guid.NewGuid(), attempt, command.CorrelationId, _snapshot.RuntimeEpoch, DateTimeOffset.UtcNow,
            command switch { ArmProductionCommand => AuditedCommandKind.ArmProduction,
                GracefulProductionStopCommand => AuditedCommandKind.GracefulProductionStop, _ => AuditedCommandKind.Unsupported },
            command.Invocation is { } invocation && Enum.IsDefined(typeof(CommandSource), invocation.Source) ? invocation.Source : null,
            command.Invocation?.PrincipalId is { Length: <= 256 } principal ? principal : null,
            command.Invocation?.SessionId, command.Invocation?.StepUpGrantId,
            CommandAuditPhase.Outcome, outcome.Disposition, outcome.ReasonCode);

    private static TimeSpan PositiveRemaining(StoreDeadline deadline)
    {
        var remaining = deadline.Remaining;
        return remaining > TimeSpan.Zero ? remaining : throw new TimeoutException("TraceCommitDeadlineExceeded");
    }

    private async Task InitializeStoreAsync()
    {
        if (_audit is null) return;
        var result = await _audit.Initialization.ConfigureAwait(false);
        lock (_sync)
        {
            _storeReady = result.Committed;
            if (_disposed) return;
            var blockers = _snapshot.AdmissionBlockers.Where(x => x != "TraceStoreMissing").ToList();
            if (!result.Committed) blockers.Add(result.ReasonCode);
            PublishLocked(_snapshot with { Store = _auditFault ? _snapshot.Store :
                new SubsystemHealth(result.Committed ? HealthState.Healthy : HealthState.Faulted, result.ReasonCode),
                AdmissionBlockers = new AdmissionBlockers(blockers) });
        }
    }

    private void MarkAuditFault(string reason)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _auditFault = true;
            PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                Store = new SubsystemHealth(HealthState.Faulted, reason),
                AdmissionBlockers = new AdmissionBlockers(_snapshot.AdmissionBlockers.Concat(new[] { "TraceAuditUnavailable" }).Distinct()) });
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
                    if (_shutdownRequested || _disposed) return;
                    var next = _snapshot;
                    if (next.LastCommand is { State: OperationState.Pending })
                    {
                        if (_completion is null) _completion = Task.Run(CompletePendingStopAsync);
                    }
                    PublishLocked(next);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task CompletePendingStopAsync()
    {
        await _commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            CommandAuditFact? admitted;
            lock (_sync)
            {
                if (_shutdownRequested || _disposed) return;
                admitted = _pendingAudit;
            }
            if (admitted is null) return;
            var terminal = admitted with { EventId = Guid.NewGuid(), OccurredAtUtc = DateTimeOffset.UtcNow,
                Phase = CommandAuditPhase.Completed, Disposition = null, ReasonCode = "LocallyDisarmed" };
            var result = await _audit!.AppendAsync(terminal, new StoreDeadline(_audit.CommitTimeout)).ConfigureAwait(false);
            if (!result.Committed) MarkAuditFault(result.ReasonCode);
            lock (_sync)
            {
                _pendingAudit = null;
                PublishLocked(_snapshot with { LastCommand = new CommandProgress(admitted.CorrelationId,
                    result.Committed ? OperationState.Completed : OperationState.Failed,
                    result.Committed ? "LocallyDisarmed" : "TraceAuditUnavailable") });
            }
        }
        finally { _commandGate.Release(); }
    }

    private void PublishLocked(StationStateSnapshot next)
    {
        _snapshot = next with { Revision = checked(_snapshot.Revision + 1), ObservedAtUtc = DateTimeOffset.UtcNow };
        foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(_snapshot);
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync) return new ValueTask(_shutdown ??= ShutdownAsync());
    }

    private async Task ShutdownAsync()
    {
        // Stop admitting new work immediately. A terminal transaction that already owns
        // the command gate settles first; shutdown never rewrites an immutable terminal fact.
        // Pending work without an in-flight terminal is resolved as RuntimeStopped below.
        _shutdownRequested = true;
        _lifetime.Cancel();
        await _heartbeat.ConfigureAwait(false);
        await _commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_pendingAudit is { } pending)
            {
                var result = await _audit!.AppendAsync(pending with { EventId = Guid.NewGuid(), OccurredAtUtc = DateTimeOffset.UtcNow,
                    Phase = CommandAuditPhase.Failed, Disposition = null, ReasonCode = "RuntimeStopped" },
                    new StoreDeadline(_audit.CommitTimeout)).ConfigureAwait(false);
                if (!result.Committed) MarkAuditFault(result.ReasonCode);
                _pendingAudit = null;
            }
        lock (_sync)
        {
            _disposed = true;
            var lastCommand = _snapshot.LastCommand;
            if (lastCommand is { State: OperationState.Pending })
                lastCommand = lastCommand with { State = OperationState.Failed, ReasonCode = _auditFault ? "TraceAuditUnavailable" : "RuntimeStopped" };
            PublishLocked(_snapshot with { Lifecycle = RuntimeLifecycle.Stopped, Ready = false,
                ArmState = ProductionArmState.Disarmed, LastCommand = lastCommand });
            foreach (var subscriber in _subscribers) subscriber.Writer.TryComplete();
            _subscribers.Clear();
        }
        }
        finally { _commandGate.Release(); }
        if (_completion is not null) await _completion.ConfigureAwait(false);
        await _storeInitialization.ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
