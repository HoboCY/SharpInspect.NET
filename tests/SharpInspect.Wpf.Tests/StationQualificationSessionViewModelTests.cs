using System.Runtime.CompilerServices;
using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class StationQualificationSessionViewModelTests
{
    [Fact]
    public async Task V137_W01_RefreshIsReadOnlyAndAcceptedStartIsNotCompletedQualification()
    {
        var fixture = new Fixture();
        await using var model = fixture.Create();
        Assert.False(model.CanStart);
        Assert.Empty(fixture.Commands);
        await model.RefreshAsync();
        Assert.True(model.CanStart);
        Assert.Empty(fixture.Commands);
        var outcome = await model.StartAsync("受控资格验证", "temporary-password");
        var command = Assert.IsType<StartStationQualificationSessionCommand>(Assert.Single(fixture.Commands));
        Assert.Equal(fixture.Grant, command.Invocation.StepUpGrantId);
        Assert.Equal(command.CorrelationId, fixture.StepUpRequest!.Binding.CommandCorrelationId);
        Assert.Equal(command.AuthorizationTarget, fixture.StepUpRequest.Binding.TargetId);
        Assert.Equal(Permission.RunStationQualification, fixture.StepUpRequest.Binding.Permission);
        Assert.Equal(AuditedCommandKind.StartStationQualificationSession, fixture.StepUpRequest.Binding.CommandKind);
        Assert.Equal(CommandDisposition.Accepted, outcome!.Disposition);
        Assert.Equal(StationQualificationSessionPhase.Idle, model.Snapshot!.Phase);
        Assert.Contains("已接纳", model.StatusMessage);
        Assert.False(model.CanStart);
        Assert.False(model.ProductionAuthority);
        Assert.False(model.CanIssueQualification);
    }

    [Fact]
    public async Task V137_W02_SessionLossDuringStepUpCannotSubmitOrRestoreOldPresentation()
    {
        var fixture = new Fixture { StepUpBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var model = fixture.Create();
        await model.RefreshAsync();
        var operation = model.StartAsync("受控资格验证", "temporary-password");
        await fixture.StepUpEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Publish(new(InteractiveSessionState.Unauthenticated, null, null));
        fixture.StepUpBarrier.SetResult(true);
        await operation.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(fixture.Commands);
        Assert.Null(model.Snapshot);
        Assert.Null(model.LastCommandOutcome);
        Assert.False(model.CanStart);
    }

    [Fact]
    public async Task V137_W03_CancelledWaitDoesNotPublishLateAcceptanceOrSendAnExit()
    {
        var fixture = new Fixture { SubmitBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var model = fixture.Create();
        await model.RefreshAsync();
        var operation = model.StartAsync("受控资格验证", "temporary-password");
        await fixture.SubmitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        model.CancelPendingOperations();
        Assert.Null(await operation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("StationQualificationPresentationWaitEnded", model.ErrorCode);
        var lateRender = fixture.Dispatcher.ObserveNextInvocation();
        fixture.SubmitBarrier.SetResult(true);
        await fixture.SubmitFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await lateRender.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(model.LastCommandOutcome);
        Assert.IsType<StartStationQualificationSessionCommand>(Assert.Single(fixture.Commands));
        Assert.False(model.CanStart);
    }

    [Fact]
    public async Task V137_W04_EpochMismatchAndRecoveryBlockPreventStart()
    {
        var fixture = new Fixture { MismatchEpoch = true };
        await using var model = fixture.Create();
        await model.RefreshAsync();
        Assert.False(model.CanStart);
        Assert.Null(model.Snapshot);
        Assert.Equal("StationQualificationSnapshotEpochMismatch", model.ErrorCode);
        fixture.MismatchEpoch = false;
        fixture.RecoveryBlocked = true;
        await model.RefreshAsync();
        Assert.False(model.CanStart);
        Assert.False(model.CanExit);
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public async Task V137_W05_ExplicitAbortUsesSessionIdentityWithoutAnotherStepUp()
    {
        var fixture = new Fixture { Active = true };
        await using var model = fixture.Create();
        await model.RefreshAsync();
        Assert.True(model.CanExit);
        await model.ExitAsync("停止验收并恢复", abort: true);
        var command = Assert.IsType<ExitStationQualificationSessionCommand>(Assert.Single(fixture.Commands));
        Assert.Equal(fixture.QualificationSessionId, command.SessionId);
        Assert.True(command.Abort);
        Assert.Null(fixture.StepUpRequest);
    }

    private sealed class Fixture : IStationRuntime, IStationQualificationSessionService, IInteractiveSessionService, IStepUpAuthentication
    {
        internal Guid Epoch { get; } = Guid.NewGuid();
        internal Guid Principal { get; } = Guid.NewGuid();
        internal Guid QualificationSessionId { get; } = Guid.NewGuid();
        internal Guid Grant { get; } = Guid.NewGuid();
        internal InlineDispatcher Dispatcher { get; } = new();
        internal bool Active { get; set; }
        internal bool RecoveryBlocked { get; set; }
        internal bool MismatchEpoch { get; set; }
        internal List<RuntimeCommand> Commands { get; } = new();
        internal StepUpRequest? StepUpRequest { get; private set; }
        internal TaskCompletionSource<bool>? StepUpBarrier { get; init; }
        internal TaskCompletionSource<bool>? SubmitBarrier { get; init; }
        internal TaskCompletionSource<bool> StepUpEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> SubmitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> SubmitFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public InteractiveSession Current { get; private set; }
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed;
        internal Fixture() { Current = new(InteractiveSessionState.Authenticated, Principal.ToString("D"), Guid.NewGuid()); }
        internal StationQualificationSessionViewModel Create() => new(this, this, this, Plan(), Dispatcher, this);
        internal void Publish(InteractiveSession value) { Current = value; Changed?.Invoke(this, new(value)); }

        private static StationQualificationPlan Plan()
        {
            var hash = new string('A', 64);
            var controller = new QualificationControllerConfiguration(hash, hash, new byte[] { 1 });
            return new(new(1, Guid.NewGuid(), hash), hash, hash, hash, hash,
                new("ui-facility", "1", hash, hash, true), controller, controller,
                Enum.GetValues<QualificationDestinationKind>().Select(kind => new QualificationDestinationBinding(kind, kind.ToString(), hash)),
                new[] { "ui-scenario" });
        }

        public ValueTask<StationQualificationAccess> GetAccessAsync(CommandInvocation invocation, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StationQualificationAccess(true, "Available", true));
        public ValueTask<StationQualificationSessionReadResult> GetSnapshotAsync(CommandInvocation invocation, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StationQualificationSessionReadResult(true, "Available",
                new(Epoch, 1, Active ? QualificationSessionId : null,
                    RecoveryBlocked ? StationQualificationSessionPhase.RecoveryBlocked : Active ?
                        StationQualificationSessionPhase.ReadyForStimulus : StationQualificationSessionPhase.Idle,
                    "Fixture", DateTimeOffset.UtcNow, Principal, Current.SessionId, Plan(), null, null,
                    RecoveryBlocked ? StationQualificationRestorationState.RecoveryBlocked : StationQualificationRestorationState.Pending,
                    false, RecoveryBlocked, null)));
        public ValueTask<StationStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StationStateSnapshot(MismatchEpoch ? Guid.NewGuid() : Epoch, 1, DateTimeOffset.UtcNow,
                RuntimeLifecycle.Running, ExclusiveMode.None, ProductionArmState.Disarmed, false, false,
                HandshakePhase.Unknown, RecoveryState.Required, null, null,
                new(HealthState.Unknown, HealthState.Unknown, HealthState.Unknown, HealthState.Unknown),
                new(HealthState.Unknown, HealthState.Unknown, HealthState.Unknown), new(HealthState.Unknown, "Fixture"),
                new(HealthState.Unknown, 0, 0), new(QualificationMatch.Missing, QualificationMatch.Missing, QualificationMatch.Missing, QualificationMatch.Missing),
                new(HealthState.Unknown, false), new(0, 0, false), Current, null, new(new[] { "StationAcceptanceMissing" })));
        public async ValueTask<RuntimeCommandOutcome> SubmitAsync(RuntimeCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            SubmitEntered.TrySetResult(true);
            if (SubmitBarrier is not null) await SubmitBarrier.Task;
            var result = new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Accepted, "Accepted", AuditPersistence.Persisted);
            SubmitFinished.TrySetResult(true);
            return result;
        }
        public async ValueTask<StepUpResult> ReauthenticateAsync(StepUpRequest request, CancellationToken cancellationToken = default)
        {
            StepUpRequest = request;
            StepUpEntered.TrySetResult(true);
            if (StepUpBarrier is not null) await StepUpBarrier.Task;
            return new(true, "Accepted", Grant);
        }
        public async IAsyncEnumerable<StationStateSnapshot> WatchSnapshotsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
        public ValueTask<SessionSignInResult> SignInAsync(PasswordSignInRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InteractiveSession> GetSessionAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);
        public ValueTask<SessionActionResult> LockAsync(Guid? expectedSessionId, SessionLockReason reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> LogoutAsync(Guid? expectedSessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> ReportActivityAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        private TaskCompletionSource<bool>? _nextInvocation;
        public bool CheckAccess => true;
        internal Task ObserveNextInvocation()
        {
            _nextInvocation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _nextInvocation.Task;
        }
        public ValueTask InvokeAsync(Action action)
        {
            action();
            Interlocked.Exchange(ref _nextInvocation, null)?.TrySetResult(true);
            return ValueTask.CompletedTask;
        }
    }
}
