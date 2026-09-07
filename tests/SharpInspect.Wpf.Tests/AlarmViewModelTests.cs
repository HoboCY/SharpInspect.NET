using System.Collections.Concurrent;
using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class AlarmViewModelTests
{
    [Fact]
    public async Task V109_U01_FreshSnapshotShowsAllInstancesAndStaleDisablesCommands()
    {
        var epoch = Guid.NewGuid();
        var session = AuthenticatedSession();
        var snapshot = Snapshot(epoch, 1, session);
        var runtime = new FakeRuntime(snapshot);
        var clock = new FakeClock();
        await using var shell = new StationShellViewModel(runtime, new InlineDispatcher(), clock,
            new SnapshotFreshnessPolicy(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(20)));
        await shell.ApplySnapshotAsync(snapshot);
        await using var viewModel = CreateViewModel(shell, runtime, session);

        await viewModel.RefreshAsync();

        Assert.True(viewModel.IsSnapshotFresh);
        Assert.Equal(2, viewModel.TotalAlarmCount);
        Assert.Equal(2, viewModel.VisibleAlarmCount);
        Assert.Equal(1, viewModel.PlcShownCount);
        Assert.Equal(1, viewModel.PlcHiddenCount);
        Assert.Equal(1, viewModel.LatchedAlarmCount);
        Assert.Contains(viewModel.Instances, item => item.Code == "ALM-DOOR");
        Assert.Contains(viewModel.Instances, item => item.Code == "ALM-TEMP");

        clock.Advance(TimeSpan.FromMilliseconds(120));
        shell.RefreshFreshness();
        await Task.Delay(10);

        Assert.False(viewModel.IsSnapshotFresh);
        Assert.False(viewModel.CanAcknowledge);
        Assert.False(viewModel.CanReset);
        var outcome = await viewModel.AcknowledgeAlarmAsync(snapshot.AlarmState!.Instances[0].InstanceId);
        Assert.Null(outcome);
        Assert.Empty(runtime.Commands);
        Assert.Equal("AlarmSnapshotStale", viewModel.ErrorCode);
    }

    [Fact]
    public async Task V109_U02_CommandsUseCurrentSessionExactInstanceAndStepUpBinding()
    {
        var session = AuthenticatedSession();
        var snapshot = Snapshot(Guid.NewGuid(), 4, session);
        var runtime = new FakeRuntime(snapshot)
        {
            CommandOutcome = command => new RuntimeCommandOutcome(
                command.CorrelationId, CommandDisposition.Accepted, "AlarmCommandAccepted",
                AuditPersistence.Persisted)
        };
        var stepUp = new FakeStepUp();
        await using var shell = new StationShellViewModel(runtime, new InlineDispatcher(), new FakeClock());
        await shell.ApplySnapshotAsync(snapshot);
        await using var viewModel = CreateViewModel(shell, runtime, session, stepUp: stepUp);
        await viewModel.RefreshAsync();

        var acknowledged = await viewModel.AcknowledgeAlarmAsync(snapshot.AlarmState!.Instances[0].InstanceId);
        Assert.Equal(CommandDisposition.Accepted, acknowledged?.Disposition);
        var ack = Assert.Single(runtime.Commands.OfType<AcknowledgeAlarmCommand>());
        Assert.Equal(snapshot.AlarmState.Instances[0].InstanceId, ack.AlarmInstanceId);
        Assert.Equal(session.PrincipalId, ack.Invocation.PrincipalId);
        Assert.Equal(session.SessionId, ack.Invocation.SessionId);
        Assert.Equal(2, viewModel.TotalAlarmCount); // Accepted is not completion or optimistic removal.

        var resetId = snapshot.AlarmState.Instances[1].InstanceId;
        var reset = await viewModel.ResetAlarmAsync(resetId, "step-up-password");
        Assert.Equal(CommandDisposition.Accepted, reset?.Disposition);
        var resetCommand = Assert.Single(runtime.Commands.OfType<ResetAlarmCommand>());
        Assert.Equal(resetId, resetCommand.AlarmInstanceId);
        Assert.Equal(stepUp.LastRequest?.Binding.Permission, Permission.ResetAlarm);
        Assert.Equal(resetId.ToString("D"), stepUp.LastRequest?.Binding.TargetId);
        Assert.Equal(AuditedCommandKind.ResetAlarm, stepUp.LastRequest?.Binding.CommandKind);
        Assert.Equal("step-up-password", stepUp.LastRequest?.Password);
        Assert.Equal(stepUp.GrantId, resetCommand.Invocation.StepUpGrantId);
    }

    [Fact]
    public async Task V109_U03_FilterChangesVisibleRowsButNeverSummaryOrPlcProjection()
    {
        var session = AuthenticatedSession();
        var snapshot = Snapshot(Guid.NewGuid(), 1, session);
        var runtime = new FakeRuntime(snapshot);
        await using var shell = new StationShellViewModel(runtime, new InlineDispatcher(), new FakeClock());
        await shell.ApplySnapshotAsync(snapshot);
        await using var viewModel = CreateViewModel(shell, runtime, session);
        await viewModel.RefreshAsync();

        viewModel.CodeFilterText = "does-not-exist";

        Assert.Equal(2, viewModel.TotalAlarmCount);
        Assert.Equal(0, viewModel.VisibleAlarmCount);
        Assert.Equal(1, viewModel.PlcShownCount);
        Assert.Equal(1, viewModel.PlcHiddenCount);
        Assert.Equal(1, viewModel.LatchedAlarmCount);
    }

    [Fact]
    public async Task V109_U04_LateHistoryFailureUsesSafeReasonAndLeavesAlarmProjection()
    {
        var session = AuthenticatedSession();
        var snapshot = Snapshot(Guid.NewGuid(), 2, session);
        var runtime = new FakeRuntime(snapshot);
        await using var shell = new StationShellViewModel(runtime, new InlineDispatcher(), new FakeClock());
        await shell.ApplySnapshotAsync(snapshot);
        var history = new FailingHistory();
        await using var viewModel = CreateViewModel(shell, runtime, session, history: history);

        await viewModel.RefreshHistoryAsync();

        Assert.Equal("AlarmHistoryQueryFailed", viewModel.HistoryErrorCode);
        Assert.DoesNotContain("private", viewModel.HistoryStatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, viewModel.TotalAlarmCount);
        Assert.Empty(viewModel.HistoryRows);
    }

    [Fact]
    public async Task V109_U05_BoundaryFaultRetainsKnownInstancesAndDisablesActions()
    {
        var session = AuthenticatedSession();
        var snapshot = Snapshot(Guid.NewGuid(), 2, session);
        var runtime = new FakeRuntime(snapshot);
        await using var shell = new StationShellViewModel(runtime, new InlineDispatcher(), new FakeClock());
        await shell.ApplySnapshotAsync(snapshot);
        await using var viewModel = CreateViewModel(shell, runtime, session);
        await viewModel.RefreshAsync();
        var alarms = snapshot.AlarmState!;
        await shell.ApplySnapshotAsync(snapshot with { Revision = 3,
            AlarmState = new AlarmStateSnapshot(false, "AlarmCodeUnmapped", snapshot.RuntimeEpoch, 3,
                alarms.Policy, alarms.Instances, alarms.Plc) });

        Assert.Equal(2, viewModel.TotalAlarmCount);
        Assert.Equal(2, viewModel.VisibleAlarmCount);
        Assert.Equal(1, viewModel.ActiveAlarmCount);
        Assert.Equal(1, viewModel.LatchedAlarmCount);
        Assert.Equal(1, viewModel.PlcHiddenCount);
        Assert.False(viewModel.CanAcknowledge);
        Assert.False(viewModel.CanReset);
        Assert.False(viewModel.IsSnapshotFresh);
    }

    [Fact]
    public async Task V109_U07_SummaryRaisesPropertyChangedAndRecomputesAcrossAvailabilityTransitions()
    {
        var session = AuthenticatedSession();
        var snapshot = Snapshot(Guid.NewGuid(), 1, session);
        var runtime = new FakeRuntime(snapshot);
        await using var shell = new StationShellViewModel(runtime, new InlineDispatcher(), new FakeClock());
        await shell.ApplySnapshotAsync(snapshot);
        await using var viewModel = CreateViewModel(shell, runtime, session);
        await viewModel.RefreshAsync();

        var initialSummary = viewModel.SummaryText;
        Assert.Contains("新鲜", initialSummary);
        Assert.Contains("原始实例 2", initialSummary);
        Assert.Contains("PLC 显示 1 / 隐藏 1", initialSummary);

        var summaryNotifications = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AlarmViewModel.SummaryText)) summaryNotifications++;
        };

        var alarms = snapshot.AlarmState!;
        var unavailable = snapshot with
        {
            Revision = 2,
            AlarmState = new AlarmStateSnapshot(false, "AlarmCodeUnmapped", snapshot.RuntimeEpoch, 2,
                alarms.Policy, alarms.Instances, alarms.Plc)
        };
        await shell.ApplySnapshotAsync(unavailable);

        var unavailableSummary = viewModel.SummaryText;
        Assert.True(summaryNotifications > 0);
        Assert.Contains("未知 / 已陈旧", unavailableSummary);
        Assert.Contains("原始实例 2", unavailableSummary);
        Assert.Contains("活跃 2", unavailableSummary);
        Assert.Contains("PLC 显示 1 / 隐藏 1", unavailableSummary);
        Assert.Contains("AlarmCodeUnmapped", unavailableSummary);

        var availableAgain = snapshot with
        {
            Revision = 3,
            AlarmState = new AlarmStateSnapshot(true, "AlarmStateAvailable", snapshot.RuntimeEpoch, 3,
                alarms.Policy, new[] { alarms.Instances[1] },
                new AlarmPlcProjection(Array.Empty<AlarmPlcEntry>(), 1, 1, true))
        };
        await shell.ApplySnapshotAsync(availableAgain);

        Assert.True(summaryNotifications > 1);
        var finalSummary = viewModel.SummaryText;
        Assert.Contains("新鲜", finalSummary);
        Assert.Contains("原始实例 1", finalSummary);
        Assert.Contains("活跃 1", finalSummary);
        Assert.Contains("锁存 1", finalSummary);
        Assert.Contains("PLC 显示 0 / 隐藏 1", finalSummary);
        Assert.Contains("AlarmStateAvailable", finalSummary);
    }

    [Fact]
    public async Task V109_U06_TightenedAckPolicyUsesExactStepUpGrant()
    {
        var session = AuthenticatedSession();
        var snapshot = Snapshot(Guid.NewGuid(), 4, session);
        var runtime = new FakeRuntime(snapshot);
        var stepUp = new FakeStepUp();
        await using var shell = new StationShellViewModel(runtime, new InlineDispatcher(), new FakeClock());
        await shell.ApplySnapshotAsync(snapshot);
        await using var viewModel = CreateViewModel(shell, runtime, session, stepUp: stepUp,
            acknowledgeRequiresStepUp: true);
        await viewModel.RefreshAsync();
        var id = snapshot.AlarmState!.Instances[0].InstanceId;
        Assert.Null(await viewModel.AcknowledgeAlarmAsync(id));
        Assert.Empty(runtime.Commands);

        await viewModel.AcknowledgeAlarmAsync(id, "acknowledgement-password");
        var command = Assert.Single(runtime.Commands.OfType<AcknowledgeAlarmCommand>());
        Assert.Equal(Permission.AcknowledgeAlarm, stepUp.LastRequest?.Binding.Permission);
        Assert.Equal(AuditedCommandKind.AcknowledgeAlarm, stepUp.LastRequest?.Binding.CommandKind);
        Assert.Equal(id.ToString("D"), stepUp.LastRequest?.Binding.TargetId);
        Assert.Equal(command.CorrelationId, stepUp.LastRequest?.Binding.CommandCorrelationId);
        Assert.Equal(stepUp.GrantId, command.Invocation.StepUpGrantId);
    }

    private static AlarmViewModel CreateViewModel(StationShellViewModel shell, IStationRuntime runtime,
        InteractiveSession session, FakeStepUp? stepUp = null, IAlarmHistoryQuery? history = null,
        bool acknowledgeRequiresStepUp = false)
    {
        var authorization = new FakeAuthorization(session,
            new[] { Permission.AcknowledgeAlarm, Permission.ResetAlarm });
        return new AlarmViewModel(shell, runtime, new FakeSessions(session), authorization,
            stepUp ?? new FakeStepUp(), history, new InlineDispatcher(),
            acknowledgeRequiresStepUp: acknowledgeRequiresStepUp);
    }

    private static InteractiveSession AuthenticatedSession() =>
        new(InteractiveSessionState.Authenticated, "operator-principal", Guid.NewGuid());

    private static StationStateSnapshot Snapshot(Guid epoch, long revision, InteractiveSession session)
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var policy = new AlarmPolicy("alarm-policy", "1", new[]
        {
            new AlarmPolicyRule("ALM-DOOR", "door-sensor", AlarmSeverity.Error, ProductionImpact.BlockNewTriggers,
                false, AlarmNotification.UntilAcknowledged, 10, 1),
            new AlarmPolicyRule("ALM-TEMP", "temperature", AlarmSeverity.Critical, ProductionImpact.FaultAbort,
                true, AlarmNotification.UntilCleared, null, 2, AlarmResetPrerequisites.RecoveryComplete)
        }, TimeSpan.FromSeconds(5));
        var instances = new[]
        {
            new AlarmInstanceSnapshot(firstId, "ALM-DOOR", "door-sensor", policy.Id, policy.Version,
                policy.ContentHash, AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, false,
                AlarmNotification.UntilAcknowledged, 10, 1, AlarmResetPrerequisites.None,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, epoch, revision,
                AlarmLifecycle.Active, false, null, null, revision),
            new AlarmInstanceSnapshot(secondId, "ALM-TEMP", "temperature", policy.Id, policy.Version,
                policy.ContentHash, AlarmSeverity.Critical, ProductionImpact.FaultAbort, true,
                AlarmNotification.UntilCleared, null, 2, AlarmResetPrerequisites.RecoveryComplete,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, epoch, revision,
                AlarmLifecycle.RecoveredLatched, true, Guid.NewGuid(), DateTimeOffset.UtcNow, revision)
        };
        var plc = new AlarmPlcProjection(new[]
        {
            new AlarmPlcEntry(firstId, "ALM-DOOR", 10, AlarmSeverity.Error,
                ProductionImpact.BlockNewTriggers)
        }, 2, 2, true);
        var alarmState = new AlarmStateSnapshot(true, "AlarmStateAvailable", epoch, revision,
            policy, instances, plc);
        return new StationStateSnapshot(epoch, revision, DateTimeOffset.UtcNow, RuntimeLifecycle.Running,
            ExclusiveMode.None, ProductionArmState.Disarmed, false, false, HandshakePhase.Idle,
            RecoveryState.None, null, null,
            new CameraHealth(HealthState.Healthy, HealthState.Healthy, HealthState.Healthy, HealthState.Healthy),
            new PlcHealth(HealthState.Healthy, HealthState.Healthy, HealthState.Healthy),
            new SubsystemHealth(HealthState.Healthy, "Ready"), new EvidenceHealth(HealthState.Healthy, 0, 0),
            new QualificationState(QualificationMatch.Matches, QualificationMatch.Matches,
                QualificationMatch.Matches, QualificationMatch.Matches),
            new PerformanceHealth(HealthState.Healthy, false), new AlarmSummary(2, 1, true), session,
            null, new AdmissionBlockers(Array.Empty<string>()), null, alarmState);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess => true;
        public ValueTask InvokeAsync(Action action)
        {
            action();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeClock : IMonotonicClock
    {
        private long _ticks;
        public long GetTimestamp() => _ticks;
        public TimeSpan ElapsedSince(long timestamp) => TimeSpan.FromTicks(_ticks - timestamp);
        public void Advance(TimeSpan amount) => _ticks += amount.Ticks;
    }

    private sealed class FakeRuntime : IStationRuntime
    {
        private readonly StationStateSnapshot _snapshot;
        public FakeRuntime(StationStateSnapshot snapshot) => _snapshot = snapshot;
        public List<RuntimeCommand> Commands { get; } = new();
        public Func<RuntimeCommand, RuntimeCommandOutcome>? CommandOutcome { get; set; }
        public ValueTask<StationStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_snapshot);
        public async IAsyncEnumerable<StationStateSnapshot> WatchSnapshotsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }
        public ValueTask<RuntimeCommandOutcome> SubmitAsync(RuntimeCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return ValueTask.FromResult(CommandOutcome?.Invoke(command) ??
                new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Accepted,
                    "AlarmCommandAccepted"));
        }
    }

    private sealed class FakeSessions : IInteractiveSessionService
    {
        public FakeSessions(InteractiveSession current) => Current = current;
        public InteractiveSession Current { get; private set; }
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed;
        public ValueTask<SessionSignInResult> SignInAsync(PasswordSignInRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InteractiveSession> GetSessionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current);
        public ValueTask<SessionActionResult> LockAsync(Guid? expectedSessionId, SessionLockReason reason,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> LogoutAsync(Guid? expectedSessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> ReportActivityAsync(Guid sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Publish(InteractiveSession session)
        {
            Current = session;
            Changed?.Invoke(this, new InteractiveSessionChangedEventArgs(session));
        }
    }

    private sealed class FakeAuthorization : IIdentityAdministrationQuery
    {
        private readonly InteractiveSession _session;
        private readonly IReadOnlyList<Permission> _permissions;
        public FakeAuthorization(InteractiveSession session, IReadOnlyList<Permission> permissions)
        { _session = session; _permissions = permissions; }
        public ValueTask<HumanAuthorizationSnapshot> GetCurrentAuthorizationAsync(Guid? sessionId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
                new HumanAuthorizationSnapshot(true, "AuthorizationAvailable", _session.SessionId,
                    new HumanAccountSummary(Guid.NewGuid(), "operator", "Operator", true, 1,
                        _permissions, HumanRoleBundle.Operator)));
        public ValueTask<HumanDirectorySnapshot> GetAccountsAsync(CommandInvocation invocation,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
                new HumanDirectorySnapshot(false, "NotRequested"));
    }

    private sealed class FakeStepUp : IStepUpAuthentication
    {
        public StepUpRequest? LastRequest { get; private set; }
        public Guid GrantId { get; } = Guid.NewGuid();
        public ValueTask<StepUpResult> ReauthenticateAsync(StepUpRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return ValueTask.FromResult(new StepUpResult(true, "StepUpAccepted", GrantId));
        }
    }

    private sealed class FailingHistory : IAlarmHistoryQuery
    {
        public ValueTask<AlarmHistoryPage> QueryAsync(AlarmHistoryFilter filter,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("private history failure");
    }
}
