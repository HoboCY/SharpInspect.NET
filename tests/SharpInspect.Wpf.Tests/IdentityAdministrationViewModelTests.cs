using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class IdentityAdministrationViewModelTests
{
    [Fact]
    public async Task V106_U01_RefreshReadsCurrentSessionAndAuthorizedDirectory()
    {
        var session = AuthenticatedSession();
        var sessions = new FakeSessions(session);
        var query = new FakeAdministrationQuery
        {
            Authorization = new HumanAuthorizationSnapshot(true, "Available", session.SessionId,
                Account(Permission.ManageAccounts, Permission.ManagePermissions))
        };
        var expected = Account(Permission.ManageAccounts);
        query.Directory = new HumanDirectorySnapshot(true, "Available", new[] { expected });
        await using var vm = CreateViewModel(sessions, query, new FakeRuntime(), new FakeStepUp());

        await vm.RefreshAsync();

        Assert.False(vm.IsUnavailable);
        Assert.Single(vm.Accounts);
        Assert.Equal(expected.PrincipalId, vm.Accounts[0].PrincipalId);
        Assert.Equal(session.SessionId, query.LastAuthorizationSessionId);
        Assert.Equal(session.SessionId, query.LastDirectoryInvocation?.SessionId);
        Assert.Equal(session.PrincipalId, query.LastDirectoryInvocation?.PrincipalId);
    }

    [Fact]
    public async Task V106_U02_CreateBindsStepUpAndSubmitToOneCorrelationSessionAndGrant()
    {
        var session = AuthenticatedSession();
        var sessions = new FakeSessions(session);
        var query = AuthorizedQuery(sessions, Permission.ManageAccounts, Permission.ManagePermissions);
        var runtime = new FakeRuntime();
        var stepUp = new FakeStepUp
        {
            Result = new StepUpResult(true, "StepUpAccepted", Guid.NewGuid())
        };
        await using var vm = CreateViewModel(sessions, query, runtime, stepUp);
        const string newPassword = "  full Unicode 密码𝄞  ";
        const string stepUpPassword = "current step-up password";

        var outcome = await vm.CreateAccountAsync("alice", "Alice", newPassword,
            HumanRoleBundle.Operator, stepUpPassword);

        var command = Assert.IsType<CreateHumanAccountCommand>(Assert.Single(runtime.Commands));
        Assert.NotNull(outcome);
        Assert.Equal(CommandDisposition.Accepted, outcome!.Disposition);
        Assert.Equal(command.CorrelationId, stepUp.LastRequest?.CorrelationId);
        Assert.Equal(command.CorrelationId, stepUp.LastRequest?.Binding.CommandCorrelationId);
        Assert.Equal(AuditedCommandKind.CreateHumanAccount, stepUp.LastRequest?.Binding.CommandKind);
        Assert.Equal(command.TargetPrincipalId.ToString("D"), stepUp.LastRequest?.Binding.TargetId);
        Assert.Equal(session.SessionId, stepUp.LastRequest?.Invocation.SessionId);
        Assert.Equal(session.PrincipalId, stepUp.LastRequest?.Invocation.PrincipalId);
        Assert.Null(stepUp.LastRequest?.Invocation.StepUpGrantId);
        Assert.Equal(stepUp.Result.GrantId, command.Invocation.StepUpGrantId);
        Assert.Equal(newPassword, command.Password);
        Assert.DoesNotContain(newPassword, vm.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(stepUpPassword, vm.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(vm.Accounts);
    }

    [Fact]
    public async Task V106_U03_StalePermissionButtonCannotOptimisticallyChangeDirectoryAfterRuntimeRejection()
    {
        var session = AuthenticatedSession();
        var sessions = new FakeSessions(session);
        var existing = Account(Permission.ManageAccounts);
        var query = AuthorizedQuery(sessions, Permission.ManageAccounts);
        query.Directory = new HumanDirectorySnapshot(true, "Available", new[] { existing });
        var runtime = new FakeRuntime
        {
            Result = new RuntimeCommandOutcome(Guid.Empty, CommandDisposition.Rejected, "PermissionRevisionChanged")
        };
        var stepUp = new FakeStepUp { Result = new StepUpResult(true, "StepUpAccepted", Guid.NewGuid()) };
        await using var vm = CreateViewModel(sessions, query, runtime, stepUp);
        await vm.RefreshAsync();

        var outcome = await vm.DisableAccountAsync(existing.PrincipalId, "current step-up password");

        Assert.NotNull(outcome);
        Assert.Equal(CommandDisposition.Rejected, outcome!.Disposition);
        Assert.Equal(AuditedCommandKind.DisableHumanCredential, stepUp.LastRequest?.Binding.CommandKind);
        Assert.Single(vm.Accounts);
        Assert.Equal(existing.PrincipalId, vm.Accounts[0].PrincipalId);
        Assert.Equal("PermissionRevisionChanged", vm.LastCommandOutcome?.ReasonCode);
    }

    [Fact]
    public async Task V106_U04_LockDuringDelayedStepUpCancelsSubmitAndClearsDirectoryProjection()
    {
        var session = AuthenticatedSession();
        var sessions = new FakeSessions(session);
        var existing = Account(Permission.ManageAccounts);
        var query = AuthorizedQuery(sessions, Permission.ManageAccounts);
        query.Directory = new HumanDirectorySnapshot(true, "Available", new[] { existing });
        var runtime = new FakeRuntime();
        var stepUp = new FakeStepUp { WaitForRelease = true, Result = new StepUpResult(true, "StepUpAccepted", Guid.NewGuid()) };
        await using var vm = CreateViewModel(sessions, query, runtime, stepUp);
        await vm.RefreshAsync();
        var pending = vm.DisableAccountAsync(existing.PrincipalId, "current step-up password");
        await stepUp.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        sessions.Publish(new(InteractiveSessionState.Locked, null, null));
        stepUp.Release();
        var outcome = await pending;

        Assert.Null(outcome);
        Assert.Empty(runtime.Commands);
        Assert.Empty(vm.Accounts);
        Assert.True(vm.IsUnavailable);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task V106_U05_DuplicateMutationIsRejectedBeforeSecondStepUp()
    {
        var session = AuthenticatedSession();
        var sessions = new FakeSessions(session);
        var query = AuthorizedQuery(sessions, Permission.ManageAccounts);
        var runtime = new FakeRuntime();
        var stepUp = new FakeStepUp { WaitForRelease = true, Result = new StepUpResult(true, "StepUpAccepted", Guid.NewGuid()) };
        await using var vm = CreateViewModel(sessions, query, runtime, stepUp);
        var first = vm.DisableAccountAsync(Guid.NewGuid(), "current step-up password");
        await stepUp.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = await vm.DisableAccountAsync(Guid.NewGuid(), "current step-up password");

        Assert.Null(second);
        Assert.True(vm.IsBusy);
        stepUp.Release();
        await first;
        Assert.Single(stepUp.Requests);
    }

    [Fact]
    public async Task V106_U06_UnconfiguredServicesRenderUnavailableWithoutSubmitting()
    {
        await using var vm = new IdentityAdministrationViewModel(null, null, null, null,
            new InlineDispatcher());

        await vm.RefreshAsync();
        var outcome = await vm.CreateAccountAsync("alice", "Alice", "password",
            HumanRoleBundle.Operator, "step-up");

        Assert.Null(outcome);
        Assert.True(vm.IsUnavailable);
        Assert.Empty(vm.Accounts);
        Assert.Equal("IdentityAdministrationUnavailable", vm.ErrorCode);
    }

    [Fact]
    public async Task V106_U07_ClearingAfterAcceptedCannotRewriteOutcomeAsCancellation()
    {
        var session = AuthenticatedSession();
        var sessions = new FakeSessions(session);
        var query = AuthorizedQuery(sessions, Permission.ManageAccounts);
        var runtime = new FakeRuntime();
        var stepUp = new FakeStepUp();
        await using var vm = CreateViewModel(sessions, query, runtime, stepUp);

        var outcome = await vm.DisableAccountAsync(Guid.NewGuid(), "current step-up password");
        Assert.Equal(CommandDisposition.Accepted, outcome?.Disposition);

        vm.CancelPendingOperations();
        vm.ClearSensitiveState();

        Assert.Equal(CommandDisposition.Accepted, vm.LastCommandOutcome?.Disposition);
        Assert.NotEqual("IdentityAdministrationCancelled", vm.ErrorCode);
    }

    [Fact]
    public async Task V106_U08_LateSessionEventCannotRestoreOldAuthorityProjection()
    {
        var oldSession = AuthenticatedSession();
        var currentSession = AuthenticatedSession();
        var sessions = new FakeSessions(oldSession);
        var query = AuthorizedQuery(sessions, Permission.ManageAccounts);
        await using var vm = CreateViewModel(sessions, query, new FakeRuntime(), new FakeStepUp());

        await vm.RefreshAsync();
        sessions.PublishDelayed(oldSession, currentSession);
        await Task.Yield();

        Assert.Equal(currentSession.SessionId, vm.CurrentSession.SessionId);
        Assert.Null(vm.CurrentAuthorization);
        Assert.True(vm.IsUnavailable);
        Assert.False(vm.CanManageAccounts);
        Assert.Empty(vm.Accounts);
    }

    private static IdentityAdministrationViewModel CreateViewModel(
        FakeSessions sessions, FakeAdministrationQuery query, FakeRuntime runtime, FakeStepUp stepUp) =>
        new(runtime, sessions, stepUp, query, new InlineDispatcher());

    private static FakeAdministrationQuery AuthorizedQuery(FakeSessions sessions, params Permission[] permissions) =>
        new()
        {
            Authorization = new HumanAuthorizationSnapshot(true, "Available", sessions.Current.SessionId,
                Account(permissions))
        };

    private static HumanAccountSummary Account(params Permission[] permissions) =>
        new(Guid.NewGuid(), "operator", "Operator", true, 7, permissions, HumanRoleBundle.Operator);

    private static InteractiveSession AuthenticatedSession() =>
        new(InteractiveSessionState.Authenticated, Guid.NewGuid().ToString("D"), Guid.NewGuid());

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess => true;
        public ValueTask InvokeAsync(Action action)
        {
            action();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeRuntime : IStationRuntime
    {
        public ConcurrentQueue<RuntimeCommand> Commands { get; } = new();
        public RuntimeCommandOutcome Result { get; set; } =
            new(Guid.Empty, CommandDisposition.Accepted, "Accepted");

        public ValueTask<StationStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<StationStateSnapshot> WatchSnapshotsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        public ValueTask<RuntimeCommandOutcome> SubmitAsync(RuntimeCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Enqueue(command);
            return ValueTask.FromResult(Result with { CorrelationId = command.CorrelationId });
        }
    }

    private sealed class FakeStepUp : IStepUpAuthentication
    {
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StepUpResult Result { get; set; } = new(true, "StepUpAccepted", Guid.NewGuid());
        public bool WaitForRelease { get; set; }
        public TaskCompletionSource<bool> Started => _started;
        public List<StepUpRequest> Requests { get; } = new();
        public StepUpRequest? LastRequest => Requests.LastOrDefault();

        public async ValueTask<StepUpResult> ReauthenticateAsync(StepUpRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            _started.TrySetResult(true);
            if (WaitForRelease) await _release.Task.ConfigureAwait(false);
            return Result;
        }

        public void Release() => _release.TrySetResult(true);
    }

    private sealed class FakeAdministrationQuery : IIdentityAdministrationQuery
    {
        public HumanAuthorizationSnapshot Authorization { get; set; } =
            new(false, "Unavailable", null, null);
        public HumanDirectorySnapshot Directory { get; set; } =
            new(false, "Unavailable");
        public Guid? LastAuthorizationSessionId { get; private set; }
        public CommandInvocation? LastDirectoryInvocation { get; private set; }

        public ValueTask<HumanAuthorizationSnapshot> GetCurrentAuthorizationAsync(Guid? sessionId,
            CancellationToken cancellationToken = default)
        {
            LastAuthorizationSessionId = sessionId;
            return ValueTask.FromResult(Authorization with { SessionId = sessionId });
        }

        public ValueTask<HumanDirectorySnapshot> GetAccountsAsync(CommandInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            LastDirectoryInvocation = invocation;
            return ValueTask.FromResult(Directory);
        }
    }

    private sealed class FakeSessions : IInteractiveSessionService
    {
        private EventHandler<InteractiveSessionChangedEventArgs>? _changed;

        public FakeSessions(InteractiveSession current) => Current = current;
        public InteractiveSession Current { get; private set; }
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed
        {
            add => _changed += value;
            remove => _changed -= value;
        }

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
            _changed?.Invoke(this, new InteractiveSessionChangedEventArgs(session));
        }

        public void PublishDelayed(InteractiveSession eventSession, InteractiveSession currentSession)
        {
            Current = currentSession;
            _changed?.Invoke(this, new InteractiveSessionChangedEventArgs(eventSession));
        }
    }
}
