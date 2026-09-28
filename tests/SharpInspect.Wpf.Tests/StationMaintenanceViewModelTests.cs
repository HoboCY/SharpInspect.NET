using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class StationMaintenanceViewModelTests
{
    private const string Binding = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact, Trait("VerificationId", "V159_W01")]
    public async Task V159_W01_ExactFreshStepUpRequestsHandoffWithoutClaimingTakeover()
    {
        var runtime = new Runtime(); var query = new Query(); var sessions = new Sessions(); var stepUp = new StepUp();
        using var model = Model(runtime, query, sessions, stepUp);
        await model.RefreshAsync();
        model.SelectedReason = model.ReasonOptions[1];
        var result = await model.PrepareWithStepUpAsync("temporary-input");
        Assert.Equal(CommandDisposition.Accepted, result!.Disposition);
        var command = Assert.IsType<PrepareStationMaintenanceCommand>(Assert.Single(runtime.Commands));
        Assert.Equal(StationMaintenanceReason.StationRecovery, command.Reason);
        Assert.Equal(new StepUpBinding(Permission.PrepareStationMaintenance, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.PrepareStationMaintenance), stepUp.Binding);
        Assert.Equal(stepUp.Grant, command.Invocation.StepUpGrantId);
        Assert.Equal(sessions.Current.SessionId, command.Invocation.SessionId);
        Assert.Contains("仍需等待", model.StatusMessage);
        Assert.False(model.CanSubmit);
        Assert.DoesNotContain("temporary-input", model.StatusMessage);
    }

    [Theory, InlineData("hide"), InlineData("dispose"), InlineData("lock"), Trait("VerificationId", "V159_W02")]
    public async Task V159_W02_LeavingPageDuringReauthenticationCannotSubmit(string change)
    {
        var runtime = new Runtime(); var sessions = new Sessions(); var stepUp = new StepUp { Pending = new() };
        using var model = Model(runtime, new Query(), sessions, stepUp);
        await model.RefreshAsync();
        var submission = model.PrepareWithStepUpAsync("temporary-input");
        if (change == "hide") model.Deactivate(); else if (change == "dispose") model.Dispose(); else sessions.Lock();
        stepUp.Pending.SetResult(new(true, "Authenticated", stepUp.Grant));
        Assert.Null(await submission);
        Assert.Empty(runtime.Commands);
    }

    [Fact, Trait("VerificationId", "V159_W03")]
    public async Task V159_W03_PageCloseDoesNotStopRuntimeOrCancelAcceptedHandoff()
    {
        var runtime = new Runtime(); var query = new Query();
        using var model = Model(runtime, query, new Sessions(), new StepUp());
        await model.RefreshAsync(); model.Deactivate();
        Assert.Empty(runtime.Commands);
        await model.RefreshAsync();
        await model.PrepareWithStepUpAsync("temporary-input");
        model.Deactivate(); model.Dispose();
        Assert.IsType<PrepareStationMaintenanceCommand>(Assert.Single(runtime.Commands));
        Assert.False(runtime.LastToken.CanBeCanceled);
    }

    [Fact, Trait("VerificationId", "V159_W04")]
    public async Task V159_W04_RejectedStepUpAndChangedRuntimeNeverReachCommandBoundary()
    {
        var runtime = new Runtime(); var query = new Query(); var stepUp = new StepUp { Success = false };
        using var model = Model(runtime, query, new Sessions(), stepUp);
        await model.RefreshAsync(); Assert.Null(await model.PrepareWithStepUpAsync("temporary-input"));
        stepUp.Success = true; stepUp.BeforeReturn = () => query.State = query.State with { RuntimeEpoch = Guid.NewGuid() };
        await model.RefreshAsync(); Assert.Null(await model.PrepareWithStepUpAsync("temporary-input"));
        Assert.Empty(runtime.Commands);
    }

    [Fact, Trait("VerificationId", "V159_W05")]
    public async Task V159_W05_AnOlderReadCannotReplaceNewerMaintenanceProgress()
    {
        var runtime = new Runtime(); var query = new Query();
        using var model = Model(runtime, query, new Sessions(), new StepUp());
        var first = new TaskCompletionSource<StationMaintenanceSnapshot>();
        query.Next = first.Task;
        var oldRead = model.RefreshAsync();
        query.Next = null; query.State = query.State with { Phase = StationMaintenancePhase.Draining };
        await model.RefreshAsync();
        first.SetResult(query.State with { Phase = StationMaintenancePhase.Idle }); await oldRead;
        Assert.Equal("正在正常停产", model.PhaseText); Assert.False(model.CanSubmit);
    }

    [Fact, Trait("VerificationId", "V159_W06")]
    public async Task V159_W06_PreparedProgressDoesNotDisplayTakeoverCompleted()
    {
        var query = new Query(); query.State = query.State with { Phase = StationMaintenancePhase.PreparedAwaitingHostExit };
        using var model = Model(new Runtime(), query, new Sessions(), new StepUp());
        await model.RefreshAsync();
        Assert.Equal("已准备交接，等待宿主退出", model.PhaseText); Assert.False(model.CanSubmit);
    }

    [Fact, Trait("VerificationId", "V159_W07")]
    public void V159_W07_ReasonAndInstallationAreBoundAndInvalidBoundsAreRejected()
    {
        var correlation = Guid.NewGuid(); var operation = Guid.NewGuid(); var invocation = new CommandInvocation(CommandSource.PhysicalConsole);
        var command = new PrepareStationMaintenanceCommand(correlation, invocation, operation, Binding, StationMaintenanceReason.SoftwareUpgrade);
        Assert.NotEqual(command.AuthorizationTarget, new PrepareStationMaintenanceCommand(correlation, invocation, operation,
            Binding, StationMaintenanceReason.StationRecovery).AuthorizationTarget);
        Assert.NotEqual(command.AuthorizationTarget, new PrepareStationMaintenanceCommand(correlation, invocation, operation,
            new string('B', 64), command.Reason).AuthorizationTarget);
        Assert.Throws<ArgumentException>(() => new PrepareStationMaintenanceCommand(Guid.Empty, invocation, operation, Binding, command.Reason));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrepareStationMaintenanceCommand(correlation, invocation, operation, Binding, (StationMaintenanceReason)255));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StationMaintenancePolicy(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1)));
        Assert.Equal((ushort)44, (ushort)Permission.PrepareStationMaintenance);
    }

    private static StationMaintenanceViewModel Model(Runtime runtime, Query query, Sessions sessions, StepUp stepUp) =>
        new(runtime, query, stepUp, sessions, Binding, new InlineDispatcher());
    private sealed class InlineDispatcher : IUiDispatcher
    { public bool CheckAccess => true; public ValueTask InvokeAsync(Action action) { action(); return ValueTask.CompletedTask; } }
    private sealed class Query : IStationMaintenanceQuery
    {
        internal StationMaintenanceSnapshot State = new(true, Guid.NewGuid(), null, StationMaintenancePhase.Idle, "Idle", null, false);
        internal Task<StationMaintenanceSnapshot>? Next;
        public ValueTask<StationMaintenanceSnapshot> ReadMaintenanceAsync(CancellationToken cancellationToken = default) =>
            Next is { } next ? new(next) : ValueTask.FromResult(State);
    }
    private sealed class Runtime : IStationRuntime
    {
        internal List<RuntimeCommand> Commands { get; } = new();
        internal CancellationToken LastToken;
        public ValueTask<RuntimeCommandOutcome> SubmitAsync(RuntimeCommand command, CancellationToken cancellationToken = default)
        { Commands.Add(command); LastToken = cancellationToken; return ValueTask.FromResult(new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Accepted, "Admitted", AuditPersistence.Persisted)); }
        public ValueTask<StationStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StationStateSnapshot> WatchSnapshotsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class StepUp : IStepUpAuthentication
    {
        internal Guid Grant = Guid.NewGuid(); internal StepUpBinding? Binding; internal bool Success = true;
        internal TaskCompletionSource<StepUpResult>? Pending; internal Action? BeforeReturn;
        public ValueTask<StepUpResult> ReauthenticateAsync(StepUpRequest request, CancellationToken cancellationToken = default)
        { Binding = request.Binding; BeforeReturn?.Invoke(); return Pending is { } pending ? new(pending.Task) : ValueTask.FromResult(new StepUpResult(Success, "Fixture", Success ? Grant : null)); }
    }
    private sealed class Sessions : IInteractiveSessionService
    {
        public InteractiveSession Current { get; private set; } = new(InteractiveSessionState.Authenticated, Guid.NewGuid().ToString("D"), Guid.NewGuid());
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed;
        internal void Lock() { Current = new(InteractiveSessionState.Locked, null, null); Changed?.Invoke(this, new(Current)); }
        public ValueTask<InteractiveSession> GetSessionAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);
        public ValueTask<SessionSignInResult> SignInAsync(PasswordSignInRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> LockAsync(Guid? expectedSessionId, SessionLockReason reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> LogoutAsync(Guid? expectedSessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> ReportActivityAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
