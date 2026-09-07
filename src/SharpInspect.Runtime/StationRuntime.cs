using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;

namespace SharpInspect.Runtime;

/// <summary>
/// The initial, deliberately unconfigured station authority. Later tickets supply governed
/// capabilities; no host option can assert that a missing production gate passed.
/// </summary>
public sealed partial class StationRuntime : IStationRuntime, IAsyncDisposable, IAdministratorRecoveryRuntimeGate
{
    private const int MaximumSubscribers = 64;
    private readonly object _sync = new();
    private readonly List<Channel<StationStateSnapshot>> _subscribers = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _heartbeat;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly ICommandAuditWriter? _audit;
    private readonly IInteractiveSessionService? _sessions;
    private readonly LocalAuthorizationService? _authorization;
    private readonly Task _storeInitialization;
    private Task? _completion;
    private Task? _shutdown;
    private CommandAuditFact? _pendingAudit;
    private bool _shutdownRequested;
    private bool _auditFault;
    private bool _storeReady;
    private int _queuedCommands;
    private StationStateSnapshot _snapshot;
    private long _sessionProjectionVersion;
    private bool _disposed;

    public StationRuntime(TimeSpan? heartbeatInterval = null) : this(null, heartbeatInterval) { }

    internal StationRuntime(ICommandAuditWriter? audit, TimeSpan? heartbeatInterval = null, IInteractiveSessionService? sessions = null,
        LocalAuthorizationService? authorization = null)
    {
        _audit = audit;
        _sessions = sessions;
        _authorization = authorization;
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
                "ActiveRecipeMissing", authorization is null ? "AuthorizationUnavailable" : "AuthorizationQualificationMissing", "StartupRecoveryNotVerified",
                "FrameworkQualificationMissing", "ProviderQualificationMissing", "PerformanceQualificationMissing",
                "StationAcceptanceMissing", "ProductionCycleUnavailable"
            }));
        if (_sessions is not null)
        {
            lock (_sync)
            {
                _sessions.Changed += OnSessionChanged;
                var observedVersion = _sessionProjectionVersion;
                var initialSession = _sessions.Current;
                // A provider can publish while its Current getter is reconciling. Preserve
                // the handler's newer projection in that re-entrant case.
                if (_sessionProjectionVersion == observedVersion)
                    _snapshot = _snapshot with { Session = initialSession };
            }
        }
        _storeInitialization = InitializeStoreAsync();
        _heartbeat = PublishHeartbeatAsync(interval);
    }

    public ValueTask<StationStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ReconcileSessionLocked();
            return ValueTask.FromResult(_snapshot);
        }
    }

    private void OnSessionChanged(object? sender, InteractiveSessionChangedEventArgs args)
    {
        lock (_sync)
        {
            if (_disposed || _shutdownRequested) return;
            _sessionProjectionVersion = checked(_sessionProjectionVersion + 1);
            var observedVersion = _sessionProjectionVersion;
            var current = _sessions!.Current;
            if (_sessionProjectionVersion != observedVersion) return;
            // Interactive identity is a separate axis. No arm/Ready/PLC/background work is changed.
            PublishLocked(_snapshot with { Session = current });
        }
    }

    private void ReconcileSessionLocked()
    {
        if (_sessions is null || _disposed || _shutdownRequested) return;
        var observedVersion = _sessionProjectionVersion;
        var current = _sessions.Current;
        if (_sessionProjectionVersion != observedVersion) return;
        if (_snapshot.Session == current) return;
        _sessionProjectionVersion = checked(_sessionProjectionVersion + 1);
        PublishLocked(_snapshot with { Session = current });
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
            ReconcileSessionLocked();
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
            LocalAuthorizationService.PreparedManagement? prepared = null;
            var governedCommand = _authorization is not null && command is (IdentityManagementCommand or ArmProductionCommand or GovernedAuditChangeCommand);
            if (governedCommand)
            {
                await _storeInitialization.WaitAsync(PositiveRemaining(deadline), cancellationToken).ConfigureAwait(false);
                var preparation = _authorization!.PrepareCommandAsync(command, cancellationToken);
                // Preparation may finish after the caller deadline, but cannot mutate authority.
                // It retains its own actual capacity until completion and never blocks local Stop's gate.
                _ = preparation.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                prepared = await preparation.WaitAsync(PositiveRemaining(deadline), cancellationToken).ConfigureAwait(false);
                if (prepared.Reason is "ManagementPreparationCapacityExceeded" or "ManagementPreparationDeadlineExceeded" or "ManagementPreparationUnavailable")
                    return Unavailable(prepared.Reason);
            }
            while (true)
            {
                entered = await _commandGate.WaitAsync(PositiveRemaining(deadline), cancellationToken).ConfigureAwait(false);
                if (!entered || !governedCommand || _audit?.Integrity?.State != AuditIntegrityState.Verifying) break;
                // A previous command may have committed since preparation observed Verified.
                // Yield the gate while its verification settles; local Stop never waits behind this poll.
                _commandGate.Release();
                entered = false;
                using var recheck = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                recheck.CancelAfter(PositiveRemaining(deadline));
                await _authorization!.WaitForAuditAsync(recheck.Token).ConfigureAwait(false);
            }
            if (!entered)
            {
                MarkAuditFault("CommandDeadlineExceeded");
                return Unavailable("CommandDeadlineExceeded");
            }
            await _storeInitialization.WaitAsync(PositiveRemaining(deadline), cancellationToken).ConfigureAwait(false);
            if (governedCommand)
            {
                string? forced;
                Guid epoch;
                lock (_sync)
                {
                    if (_shutdownRequested || _disposed) return Unavailable("RuntimeStopped");
                    forced = _snapshot.LastCommand?.State == OperationState.Pending ? "OperationInProgress" : null;
                    epoch = _snapshot.RuntimeEpoch;
                }
                var governed = await _authorization!.HandleCommandAsync(command, epoch, attempt, prepared!, forced, deadline, cancellationToken).ConfigureAwait(false);
                if (governed.Audit == AuditPersistence.Unavailable) MarkAuditFault("TraceAuditUnavailable");
                if (governed.Disposition == CommandDisposition.Accepted)
                {
                    lock (_sync)
                        PublishLocked(_snapshot with { LastCommand = new CommandProgress(command.CorrelationId,
                            OperationState.Completed, governed.ReasonCode) });
                }
                return governed;
            }
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
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
            GovernedAuditChangeCommand => Reject("AuthorizationUnavailable"),
            ArmProductionCommand => Reject(_snapshot.AdmissionBlockers[0]),
            GracefulProductionStopCommand when command.Invocation.Source != CommandSource.PhysicalConsole => Reject("LocalConsoleRequired"),
            GracefulProductionStopCommand when _snapshot.LastCommand?.State == OperationState.Pending => Reject("OperationInProgress"),
            GracefulProductionStopCommand when _snapshot.LastCommand is { State: OperationState.Completed, ReasonCode: "LocallyDisarmed" } => Reject("AlreadyLocallyDisarmed"),
            GracefulProductionStopCommand => new(command.CorrelationId, CommandDisposition.Accepted, "StopAdmitted"),
            _ => Reject("UnsupportedCommand")
        };
    }

    private CommandAuditFact CreateFact(RuntimeCommand command, Guid attempt, RuntimeCommandOutcome outcome) =>
        new(Guid.NewGuid(), attempt, command.CorrelationId, _snapshot.RuntimeEpoch, DateTimeOffset.UtcNow,
            command switch { ArmProductionCommand => AuditedCommandKind.ArmProduction,
                GracefulProductionStopCommand => AuditedCommandKind.GracefulProductionStop,
                GovernedAuditChangeCommand change when _audit?.Integrity is { State: not AuditIntegrityState.NotConfigured } => change.Change switch
                {
                    GovernedAuditChangeKind.RotateSigningKey => AuditedCommandKind.RotateSigningKey,
                    GovernedAuditChangeKind.RetireSigningKey => AuditedCommandKind.RetireSigningKey,
                    GovernedAuditChangeKind.CorrectHistoricalFact => AuditedCommandKind.CorrectHistoricalFact,
                    GovernedAuditChangeKind.DeleteEvidence => AuditedCommandKind.DeleteEvidence,
                    _ => AuditedCommandKind.Unsupported
                }, _ => AuditedCommandKind.Unsupported },
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
            PublishLocked(_snapshot with { AuditIntegrity = _audit.Integrity, Store = _auditFault ? _snapshot.Store :
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
                    var integrity = _audit?.Integrity;
                    var integrityBlocked = integrity is { State: AuditIntegrityState.Faulted or AuditIntegrityState.Verifying };
                    var blockers = _snapshot.AdmissionBlockers.Where(x => x != "AuditIntegrityUnavailable");
                    if (integrityBlocked) blockers = blockers.Append("AuditIntegrityUnavailable");
                    var next = _snapshot with { AuditIntegrity = integrity,
                        AdmissionBlockers = new AdmissionBlockers(blockers) };
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
        if (_sessions is not null) _sessions.Changed -= OnSessionChanged;
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
