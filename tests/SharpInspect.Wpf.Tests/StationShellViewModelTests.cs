using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class StationShellViewModelTests
{
    [Fact]
    public async Task DuplicateOrOlderRevisionDoesNotRefreshArrivalAgeOrReadyDisplay()
    {
        var runtime = new ControlledRuntime();
        var clock = new FakeClock();
        await using var viewModel = new StationShellViewModel(runtime, new InlineUiDispatcher(), clock,
            new SnapshotFreshnessPolicy(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(20)));
        var first = Snapshot(Guid.NewGuid(), 4, ready: true);

        await viewModel.ApplySnapshotAsync(first);
        Assert.Equal(SnapshotFreshness.Fresh, viewModel.Freshness);
        Assert.True(viewModel.State.Ready);
        clock.Advance(TimeSpan.FromMilliseconds(90));
        await viewModel.ApplySnapshotAsync(first with { Ready = false });
        clock.Advance(TimeSpan.FromMilliseconds(20));
        viewModel.RefreshFreshness();

        Assert.Equal(SnapshotFreshness.Stale, viewModel.Freshness);
        Assert.False(viewModel.State.Ready);
        Assert.False(viewModel.CanArmProduction);
        Assert.Equal(4, viewModel.State.Revision);
    }

    [Fact]
    public async Task NewEpochRequestsFullSnapshotAndDelayedOldEpochIsIgnored()
    {
        var firstEpoch = Guid.NewGuid();
        var secondEpoch = Guid.NewGuid();
        var runtime = new ControlledRuntime
        {
            FullSnapshot = Snapshot(secondEpoch, 2, ready: false)
        };
        await using var viewModel = new StationShellViewModel(runtime, new InlineUiDispatcher(), new FakeClock(),
            new SnapshotFreshnessPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20)));

        await viewModel.ApplySnapshotAsync(Snapshot(firstEpoch, 9, ready: true));
        await viewModel.ObserveFeedSnapshotAsync(Snapshot(secondEpoch, 99, ready: true));

        Assert.Equal(secondEpoch, viewModel.State.RuntimeEpoch);
        Assert.Equal(2, viewModel.State.Revision);
        Assert.False(viewModel.State.Ready);
        Assert.Equal(1, runtime.FullSnapshotReads);

        await viewModel.ObserveFeedSnapshotAsync(Snapshot(firstEpoch, 100, ready: true));
        Assert.Equal(secondEpoch, viewModel.State.RuntimeEpoch);
        Assert.Equal(2, viewModel.State.Revision);
    }

    [Fact]
    public async Task StopRemainsAvailableWhenStaleAndAcceptedDoesNotBecomeCompletion()
    {
        var runtime = new ControlledRuntime
        {
            CommandResult = command => command is GracefulProductionStopCommand
                ? new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Accepted, "StopAdmitted")
                : new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Rejected, "DeploymentPoliciesMissing")
        };
        var clock = new FakeClock();
        await using var viewModel = new StationShellViewModel(runtime, new InlineUiDispatcher(), clock,
            new SnapshotFreshnessPolicy(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(20)));
        var epoch = Guid.NewGuid();
        await viewModel.ApplySnapshotAsync(Snapshot(epoch, 1, ready: true));
        runtime.SetCurrent(Snapshot(epoch, 1, ready: true));
        clock.Advance(TimeSpan.FromMilliseconds(120));
        viewModel.RefreshFreshness();

        Assert.True(viewModel.CanStopProduction);
        var outcome = await viewModel.GracefulStopAsync();
        runtime.PublishCurrent();
        await viewModel.ApplySnapshotAsync(runtime.CurrentSnapshot);

        Assert.Equal(CommandDisposition.Accepted, outcome.Disposition);
        Assert.Equal("StopAdmitted", outcome.ReasonCode);
        Assert.Equal(OperationState.Pending, viewModel.State.LastCommand?.State);
        Assert.Equal(CommandDisposition.Accepted, viewModel.LastCommandOutcome?.Disposition);
        Assert.NotEqual(OperationState.Completed, viewModel.State.LastCommand?.State);
        Assert.Contains(runtime.Commands, command => command is GracefulProductionStopCommand);
    }

    [Fact]
    public async Task ArmDoesNotSubmitWhenPresentationIsUnavailable()
    {
        var runtime = new ControlledRuntime();
        await using var viewModel = new StationShellViewModel(runtime, new InlineUiDispatcher(), new FakeClock());

        var exception = await Assert.ThrowsAsync<PresentationStateUnavailableException>(
            async () => await viewModel.ArmProductionAsync());

        Assert.Equal(PresentationStateUnavailableException.SafeReasonCode, exception.ReasonCode);
        Assert.Equal(PresentationStateUnavailableException.SafeReasonCode, viewModel.CommandFailureCode);
        Assert.Empty(runtime.Commands);
    }

    [Fact]
    public async Task SubmitTransportFailureClearsPriorOutcomeAndUsesSafeFailureCode()
    {
        var runtime = new ControlledRuntime();
        await using var viewModel = new StationShellViewModel(runtime, new InlineUiDispatcher(), new FakeClock());
        await viewModel.ApplySnapshotAsync(Snapshot(Guid.NewGuid(), 1, ready: true));
        runtime.ThrowOnSubmit = true;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await viewModel.GracefulStopAsync());

        Assert.Equal("CommandOutcomeUnknown", viewModel.CommandFailureCode);
        Assert.Null(viewModel.LastCommandOutcome);
        Assert.Equal(SnapshotFreshness.Unavailable, viewModel.Freshness);
        Assert.True(viewModel.CanStopProduction);
    }

    [Fact]
    public async Task ArmUsesTypedRuntimeBoundaryAndNavigationIsObservational()
    {
        var runtime = new ControlledRuntime
        {
            CommandResult = command => new RuntimeCommandOutcome(command.CorrelationId,
                CommandDisposition.Rejected, "DeploymentPoliciesMissing")
        };
        await using var viewModel = new StationShellViewModel(runtime, new InlineUiDispatcher(), new FakeClock());
        await viewModel.ApplySnapshotAsync(Snapshot(Guid.NewGuid(), 1, ready: false));

        var outcome = await viewModel.ArmProductionAsync();
        viewModel.NavigateTo("Alarms");

        Assert.Equal(CommandDisposition.Rejected, outcome.Disposition);
        Assert.Equal("DeploymentPoliciesMissing", outcome.ReasonCode);
        Assert.Equal("Alarms", viewModel.SelectedSection);
        Assert.Single(runtime.Commands);
        Assert.IsType<ArmProductionCommand>(runtime.Commands.Single());
    }

    [Fact]
    public async Task StartQueryFailureLeavesUnknownPresentationButKeepsStopAvailable()
    {
        var runtime = new ControlledRuntime { ThrowOnGetSnapshot = true };
        await using var viewModel = new StationShellViewModel(runtime, new InlineUiDispatcher(), new FakeClock());

        await viewModel.StartAsync();

        Assert.Equal(SnapshotFreshness.Unavailable, viewModel.Freshness);
        Assert.False(viewModel.CanArmProduction);
        Assert.True(viewModel.CanStopProduction);
        Assert.Null(viewModel.LastCommandOutcome);
    }

    [Fact]
    public async Task ReconnectFullQueryForDuplicateRevisionDoesNotResetArrivalAge()
    {
        var epoch = Guid.NewGuid();
        var clock = new FakeClock();
        var runtime = new ControlledRuntime { FullSnapshot = Snapshot(epoch, 1, ready: true) };
        await using var viewModel = new StationShellViewModel(runtime, new InlineUiDispatcher(), clock,
            new SnapshotFreshnessPolicy(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(20)));

        await viewModel.StartAsync();
        clock.Advance(TimeSpan.FromMilliseconds(90));
        runtime.CompleteFeed();
        await EventuallyAsync(() => runtime.FullSnapshotReads >= 2);
        clock.Advance(TimeSpan.FromMilliseconds(20));
        viewModel.RefreshFreshness();

        Assert.Equal(SnapshotFreshness.Stale, viewModel.Freshness);
        Assert.False(viewModel.State.Ready);
    }

    private static StationStateSnapshot Snapshot(Guid epoch, long revision, bool ready) =>
        new(epoch, revision, DateTimeOffset.UtcNow, RuntimeLifecycle.Running, ExclusiveMode.None,
            ready ? ProductionArmState.Armed : ProductionArmState.Disarmed, ready, false,
            HandshakePhase.Idle, RecoveryState.None, new RecipeReference("r1", "1", "hash"), null,
            new CameraHealth(HealthState.Healthy, HealthState.Healthy, HealthState.Healthy, HealthState.Healthy),
            new PlcHealth(HealthState.Healthy, HealthState.Healthy, HealthState.Healthy),
            new SubsystemHealth(HealthState.Healthy, "Ready"), new EvidenceHealth(HealthState.Healthy, 0, 0),
            new QualificationState(QualificationMatch.Matches, QualificationMatch.Matches,
                QualificationMatch.Matches, QualificationMatch.Matches),
            new PerformanceHealth(HealthState.Healthy, false), new AlarmSummary(0, 0, false),
            new InteractiveSession(InteractiveSessionState.Authenticated, "operator", Guid.NewGuid()),
            null, new AdmissionBlockers(Array.Empty<string>()));

    [Fact]
    public async Task V101_U06_QueuedNewRevisionCannotBrieflyPublishExpiredReady()
    {
        var epoch = Guid.NewGuid();
        var clock = new FakeClock();
        var runtime = new ControlledRuntime { FullSnapshot = Snapshot(epoch, 1, ready: false) };
        var dispatcher = new PausableDispatcher();
        await using var vm = new StationShellViewModel(runtime, dispatcher, clock,
            new SnapshotFreshnessPolicy(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(20)), feedCapacity: 1);
        await vm.StartAsync();
        dispatcher.Pause = true;
        runtime.SetCurrent(Snapshot(epoch, 1, ready: true));
        runtime.PublishCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await dispatcher.Queued.Task.WaitAsync(timeout.Token);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        var expiredReadyWasPublished = false;
        vm.State.PropertyChanged += (_, _) =>
        {
            if (vm.State.Revision == 2 && vm.State.Ready) expiredReadyWasPublished = true;
        };
        dispatcher.Resume();
        await EventuallyAsync(() => vm.State.Revision == 2);
        Assert.False(expiredReadyWasPublished);
        Assert.False(vm.State.Ready);
        Assert.Equal(SnapshotFreshness.Stale, vm.Freshness);
    }

    private sealed class PausableDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<(Action Action, TaskCompletionSource<bool> Completion)> _actions = new();
        public TaskCompletionSource<bool> Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Pause { get; set; }
        public bool CheckAccess => true;
        public ValueTask InvokeAsync(Action action)
        {
            if (!Pause) { action(); return ValueTask.CompletedTask; }
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _actions.Enqueue((action, completion));
            Queued.TrySetResult(true);
            return new ValueTask(completion.Task);
        }
        public void Resume()
        {
            Pause = false;
            while (_actions.TryDequeue(out var item))
            {
                try { item.Action(); item.Completion.TrySetResult(true); }
                catch (Exception error) { item.Completion.TrySetException(error); }
            }
        }
    }

    [Fact]
    public async Task V101_U07_ReconnectQueryFailureCanRecoverOnSameEpochFeed()
    {
        var epoch = Guid.NewGuid();
        var runtime = new ControlledRuntime { FullSnapshot = Snapshot(epoch, 1, ready: false) };
        await using var vm = new StationShellViewModel(runtime, new InlineUiDispatcher(), new FakeClock());
        await vm.StartAsync();
        runtime.ThrowOnGetSnapshot = true;
        runtime.Reconnect();
        await EventuallyAsync(() => vm.Freshness == SnapshotFreshness.Unavailable);
        runtime.FullSnapshot = Snapshot(epoch, 2, ready: false);
        runtime.ThrowOnGetSnapshot = false;
        runtime.SetCurrent(Snapshot(epoch, 1, ready: true));
        runtime.PublishCurrent();
        await EventuallyAsync(() => vm.State.Revision == 2);
        Assert.Equal(SnapshotFreshness.Fresh, vm.Freshness);
        Assert.False(vm.State.Ready); // The full query, rather than the feed's Ready=true, is authoritative.
    }

    [Fact]
    public async Task V101_U09_CoalescingCannotDiscardAnEpochChange()
    {
        var oldEpoch = Guid.NewGuid();
        var newEpoch = Guid.NewGuid();
        var runtime = new ControlledRuntime { FullSnapshot = Snapshot(oldEpoch, 1, ready: false) };
        var dispatcher = new PausableDispatcher();
        await using var vm = new StationShellViewModel(runtime, dispatcher, new FakeClock(), feedCapacity: 1);
        await vm.StartAsync();
        dispatcher.Pause = true;
        runtime.SetCurrent(Snapshot(oldEpoch, 1, ready: true));
        runtime.PublishCurrent(); // Old revision 2 is now waiting on the dispatcher.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await dispatcher.Queued.Task.WaitAsync(timeout.Token);
        runtime.FullSnapshot = Snapshot(newEpoch, 1, ready: false);
        runtime.SetCurrent(Snapshot(newEpoch, 9, ready: true));
        runtime.PublishCurrent();
        runtime.SetCurrent(Snapshot(oldEpoch, 99, ready: true));
        runtime.PublishCurrent(); // Drops the new epoch notification from the capacity-one feed.
        await EventuallyAsync(() => runtime.DeliveredSnapshots >= 3);
        var oldReadyWasPublished = false;
        vm.State.PropertyChanged += (_, _) =>
        {
            if (vm.State.RuntimeEpoch == oldEpoch && vm.State.Ready) oldReadyWasPublished = true;
        };
        dispatcher.Resume();
        await EventuallyAsync(() => vm.State.RuntimeEpoch == newEpoch);
        Assert.False(oldReadyWasPublished);
        Assert.False(vm.State.Ready);
        Assert.Equal(1, vm.State.Revision);
        Assert.True(runtime.FullSnapshotReads >= 2);
    }

    [Fact]
    public async Task V101_U10_CancelledInitialStartCanBeRetried()
    {
        var runtime = new ControlledRuntime();
        await using var vm = new StationShellViewModel(runtime, new InlineUiDispatcher(), new FakeClock());
        using var cancelled = new CancellationTokenSource();
        runtime.OnGetSnapshot = cancelled.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.StartAsync(cancelled.Token));
        runtime.OnGetSnapshot = null;
        await vm.StartAsync();
        Assert.Equal(SnapshotFreshness.Fresh, vm.Freshness);
        Assert.NotNull(vm.CurrentSnapshot);
    }

    private sealed class FakeClock : IMonotonicClock
    {
        private long _timestamp;
        public long GetTimestamp() => _timestamp;
        public TimeSpan ElapsedSince(long timestamp) => TimeSpan.FromTicks(_timestamp - timestamp);
        public void Advance(TimeSpan amount) => _timestamp += amount.Ticks;
    }

    private sealed class ControlledRuntime : IStationRuntime
    {
        private Channel<StationStateSnapshot> _snapshots = Channel.CreateUnbounded<StationStateSnapshot>();
        private StationStateSnapshot _current = Snapshot(Guid.NewGuid(), 1, ready: false);

        public StationStateSnapshot FullSnapshot { get; set; }
        public StationStateSnapshot CurrentSnapshot => _current;
        public int FullSnapshotReads { get; private set; }
        private int _deliveredSnapshots;
        public int DeliveredSnapshots => Volatile.Read(ref _deliveredSnapshots);
        public bool ThrowOnGetSnapshot { get; set; }
        public Action? OnGetSnapshot { get; set; }
        public bool ThrowOnSubmit { get; set; }
        public ConcurrentQueue<RuntimeCommand> Commands { get; } = new();
        public Func<RuntimeCommand, RuntimeCommandOutcome> CommandResult { get; set; } = command =>
            new(command.CorrelationId, CommandDisposition.Rejected, "Unconfigured");

        public ControlledRuntime() => FullSnapshot = _current;

        public ValueTask<StationStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            OnGetSnapshot?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnGetSnapshot) throw new InvalidOperationException("ProbeGetFailure");
            FullSnapshotReads++;
            _current = FullSnapshot;
            return ValueTask.FromResult(FullSnapshot);
        }

        public async IAsyncEnumerable<StationStateSnapshot> WatchSnapshotsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var snapshot in _snapshots.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return snapshot;
                Interlocked.Increment(ref _deliveredSnapshots);
            }
        }

        public ValueTask<RuntimeCommandOutcome> SubmitAsync(RuntimeCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnSubmit) throw new InvalidOperationException("ProbeSubmitFailure");
            Commands.Enqueue(command);
            var outcome = CommandResult(command);
            if (outcome.Disposition == CommandDisposition.Accepted)
                _current = _current with
                {
                    LastCommand = new CommandProgress(command.CorrelationId, OperationState.Pending, outcome.ReasonCode),
                    Ready = false,
                    ArmState = ProductionArmState.Disarmed
                };
            return ValueTask.FromResult(outcome);
        }

        public void PublishCurrent()
        {
            _current = _current with { Revision = _current.Revision + 1 };
            _snapshots.Writer.TryWrite(_current);
        }

        public void SetCurrent(StationStateSnapshot snapshot) => _current = snapshot;

        public void CompleteFeed() => _snapshots.Writer.TryComplete();
        public void Reconnect() => Interlocked.Exchange(ref _snapshots,
            Channel.CreateUnbounded<StationStateSnapshot>()).Writer.TryComplete();
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
