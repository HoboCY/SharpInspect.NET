using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class AdministratorRecoveryViewModelTests
{
    [Fact]
    public async Task V108_U01_RecoveryUsesPhysicalRecoveryFlowWithoutAutoLogin()
    {
        var recovery = new FakeRecovery
        {
            Status = new AdministratorRecoveryStatus("Station-01", true, "RecoveryAvailable",
                RecoveryKitState.Unavailable, null, null, 0, 1, false),
            RecoverResult = new AdministratorRecoveryResult(true, "AdministratorRecovered",
                Guid.NewGuid(), AuditPersistence.Persisted,
                new HumanIdentity(Guid.NewGuid(), "personal-admin", "Personal Admin"))
        };
        var sessions = new FakeSessions();
        await using var viewModel = new AdministratorRecoveryViewModel(recovery, sessions,
            new InlineDispatcher());

        await viewModel.RefreshAsync();
        var result = await viewModel.RecoverAdministratorAsync(
            "recovery-code", "personal-admin", "Personal Admin", "new secret password");

        Assert.True(result?.Succeeded);
        Assert.Equal("recovery-code", recovery.RecoverRequest?.RecoveryCode);
        Assert.Equal("new secret password", recovery.RecoverRequest?.NewPassword);
        Assert.Equal(0, sessions.SignInCalls);
        Assert.Equal(RecoveryKitState.RotationRequired, viewModel.KitState);
        Assert.DoesNotContain("recovery-code", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("new secret password", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V108_U02_RotationUsesCurrentSessionAndCustodyConsumesOperation()
    {
        var session = AuthenticatedSession();
        var recovery = new FakeRecovery
        {
            Status = new AdministratorRecoveryStatus("Station-01", true, "RotationRequired",
                RecoveryKitState.RotationRequired, null, Guid.NewGuid(), 1, 0, false),
            RotateHandler = request => ValueTask.FromResult(new RecoveryKitRotationResult(true,
                "RecoveryKitRotated", request.OperationId, AuditPersistence.Persisted, Guid.NewGuid(),
                new OneTimeSecret("kit-secret"))),
            ConfirmHandler = request => ValueTask.FromResult(new AdministratorRecoveryResult(true,
                "CustodyConfirmed", request.OperationId, AuditPersistence.Persisted))
        };
        var sessions = new FakeSessions(session);
        await using var viewModel = new AdministratorRecoveryViewModel(recovery, sessions,
            new InlineDispatcher());

        await viewModel.RefreshAsync();
        var rotated = await viewModel.RotateRecoveryKitAsync("current password");

        Assert.True(rotated?.Succeeded);
        Assert.Equal(session.SessionId, recovery.RotateRequest?.Invocation.SessionId);
        Assert.Equal(session.PrincipalId, recovery.RotateRequest?.Invocation.PrincipalId);
        Assert.Equal("current password", recovery.RotateRequest?.Password);
        Assert.Equal(RecoveryKitState.CustodyConfirmationRequired, viewModel.KitState);
        Assert.True(viewModel.CanConfirmRecoveryKitCustody);
        Assert.True(viewModel.HasRecoveryKit);
        Assert.Equal("kit-secret", viewModel.RevealRecoveryKit());
        Assert.Null(viewModel.RevealRecoveryKit());
        Assert.True(viewModel.CanConfirmRecoveryKitCustody);

        var confirmed = await viewModel.ConfirmRecoveryKitCustodyAsync("kit-secret");

        Assert.True(confirmed?.Succeeded);
        Assert.NotEqual(recovery.RotateRequest!.OperationId, recovery.ConfirmRequest?.OperationId);
        Assert.NotEqual(Guid.Empty, recovery.ConfirmRequest?.OperationId);
        Assert.Equal("kit-secret", recovery.ConfirmRequest?.ConfirmationCode);
        Assert.False(viewModel.CanConfirmRecoveryKitCustody);
        Assert.Equal(RecoveryKitState.Available, viewModel.KitState);
        Assert.Null(viewModel.RecoveryKitId);
    }

    [Fact]
    public async Task V108_U03_LockClearsKitAndLateRotationCannotRestoreIt()
    {
        var session = AuthenticatedSession();
        var resultSource = new TaskCompletionSource<RecoveryKitRotationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = new FakeRecovery
        {
            Status = new AdministratorRecoveryStatus("Station-01", true, "RotationRequired",
                RecoveryKitState.RotationRequired, null, Guid.NewGuid(), 1, 0, false),
            RotateHandler = _ => new ValueTask<RecoveryKitRotationResult>(resultSource.Task)
        };
        var sessions = new FakeSessions(session);
        await using var viewModel = new AdministratorRecoveryViewModel(recovery, sessions,
            new InlineDispatcher());
        await viewModel.RefreshAsync();
        var rotation = viewModel.RotateRecoveryKitAsync("current password");
        await recovery.WaitForRotateAsync();

        sessions.Publish(new InteractiveSession(InteractiveSessionState.Locked, null, null));
        resultSource.SetResult(new RecoveryKitRotationResult(true, "RecoveryKitRotated",
            recovery.RotateRequest!.OperationId, AuditPersistence.Persisted, Guid.NewGuid(),
            new OneTimeSecret("late-secret")));
        await rotation;

        Assert.False(viewModel.HasRecoveryKit);
        Assert.Null(viewModel.RecoveryKitId);
        Assert.False(viewModel.CanConfirmRecoveryKitCustody);
        Assert.DoesNotContain("late-secret", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V108_U04_UnknownServiceFailureUsesStableReasonWithoutExceptionText()
    {
        var session = AuthenticatedSession();
        var recovery = new FakeRecovery
        {
            Status = new AdministratorRecoveryStatus("Station-01", true, "RotationRequired",
                RecoveryKitState.RotationRequired, null, Guid.NewGuid(), 1, 0, false),
            ThrowOnRotate = true
        };
        var sessions = new FakeSessions(session);
        await using var viewModel = new AdministratorRecoveryViewModel(recovery, sessions,
            new InlineDispatcher());
        await viewModel.RefreshAsync();

        await viewModel.RotateRecoveryKitAsync("current password");

        Assert.Equal("AdministratorRecoveryUnavailable", viewModel.ErrorCode);
        Assert.DoesNotContain("private failure", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private failure", viewModel.ErrorCode ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.HasRecoveryKit);

        recovery.ThrowOnRotate = false;
        recovery.RotateHandler = request => ValueTask.FromResult(new RecoveryKitRotationResult(false,
            "SuperSecretPassword123", request.OperationId, AuditPersistence.NotAttempted));
        await viewModel.RotateRecoveryKitAsync("current password");

        Assert.Equal("AdministratorRecoveryRotationRejected", viewModel.ErrorCode);
        Assert.DoesNotContain("SuperSecretPassword123", viewModel.StatusMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SuperSecretPassword123", viewModel.ErrorCode ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task V108_U05_DisposeClearsUnrevealedKitAndStopsFurtherCommands()
    {
        var session = AuthenticatedSession();
        var recovery = new FakeRecovery
        {
            Status = new AdministratorRecoveryStatus("Station-01", true, "RotationRequired",
                RecoveryKitState.RotationRequired, null, Guid.NewGuid(), 1, 0, false),
            RotateHandler = request => ValueTask.FromResult(new RecoveryKitRotationResult(true,
                "RecoveryKitRotated", request.OperationId, AuditPersistence.Persisted, Guid.NewGuid(),
                new OneTimeSecret("dispose-secret")))
        };
        await using var viewModel = new AdministratorRecoveryViewModel(recovery,
            new FakeSessions(session), new InlineDispatcher());
        await viewModel.RefreshAsync();
        await viewModel.RotateRecoveryKitAsync("current password");

        await viewModel.DisposeAsync();

        Assert.False(viewModel.HasRecoveryKit);
        Assert.False(viewModel.CanRefresh);
        Assert.False(viewModel.CanRotateRecoveryKit);
        Assert.Null(viewModel.RevealRecoveryKit());
    }

    [Fact]
    public async Task V108_U06_PersistedCustodyStateCanResumeWithoutOldOperationMemory()
    {
        var kitId = Guid.NewGuid();
        var session = AuthenticatedSession();
        var recovery = new FakeRecovery
        {
            Status = new AdministratorRecoveryStatus("Station-01", false,
                "RecoveryKitCustodyConfirmationRequired", RecoveryKitState.CustodyConfirmationRequired,
                kitId, Guid.NewGuid(), 1, 2, false),
            ConfirmHandler = request => ValueTask.FromResult(new AdministratorRecoveryResult(true,
                "RecoveryKitCustodyConfirmed", request.OperationId, AuditPersistence.Persisted))
        };
        var sessions = new FakeSessions(session);
        await using var viewModel = new AdministratorRecoveryViewModel(recovery, sessions,
            new InlineDispatcher());

        await viewModel.RefreshAsync();

        Assert.False(viewModel.IsUnavailable);
        Assert.Equal(RecoveryKitState.CustodyConfirmationRequired, viewModel.KitState);
        Assert.Equal(kitId, viewModel.RecoveryKitId);
        Assert.True(viewModel.CanConfirmRecoveryKitCustody);
        var result = await viewModel.ConfirmRecoveryKitCustodyAsync("persisted-kit-code");

        Assert.True(result?.Succeeded);
        Assert.Equal(kitId, recovery.ConfirmRequest?.KitId);
        Assert.Equal("persisted-kit-code", recovery.ConfirmRequest?.ConfirmationCode);
        Assert.Equal(RecoveryKitState.Available, viewModel.KitState);
    }

    [Fact]
    public async Task V108_U07_LostKitCanBeReplacedAfterFreshReauthentication()
    {
        var session = AuthenticatedSession();
        var recovery = new FakeRecovery
        {
            Status = new AdministratorRecoveryStatus("Station-01", true, "RecoveryAvailable",
                RecoveryKitState.Available, Guid.NewGuid(), Guid.NewGuid(), 1, 2, false),
            RotateHandler = request => ValueTask.FromResult(new RecoveryKitRotationResult(true,
                "RecoveryKitRotated", request.OperationId, AuditPersistence.Persisted, Guid.NewGuid(),
                new OneTimeSecret("replacement-kit")))
        };
        var sessions = new FakeSessions(session);
        await using var viewModel = new AdministratorRecoveryViewModel(recovery, sessions,
            new InlineDispatcher());
        await viewModel.RefreshAsync();

        Assert.True(viewModel.CanRotateRecoveryKit);
        await viewModel.RotateRecoveryKitAsync("current password");
        viewModel.ClearSensitiveState();

        Assert.True(viewModel.CanRotateRecoveryKit);
        await viewModel.RotateRecoveryKitAsync("current password");

        Assert.Equal(2, recovery.RotateCallCount);
        Assert.True(viewModel.HasRecoveryKit);
        Assert.Equal("replacement-kit", viewModel.RevealRecoveryKit());
    }

    [Fact]
    public async Task V108_U08_DeferredSessionChangeCannotRevealPendingKit()
    {
        foreach (var changedSession in new[]
        {
            new InteractiveSession(InteractiveSessionState.Locked, null, null),
            new InteractiveSession(InteractiveSessionState.Authenticated,
                "different-principal", Guid.NewGuid())
        })
        {
            var originalSession = AuthenticatedSession();
            var recovery = new FakeRecovery
            {
                Status = new AdministratorRecoveryStatus("Station-01", true, "RotationRequired",
                    RecoveryKitState.RotationRequired, null, Guid.NewGuid(), 1, 0, false),
                RotateHandler = request => ValueTask.FromResult(new RecoveryKitRotationResult(true,
                    "RecoveryKitRotated", request.OperationId, AuditPersistence.Persisted, Guid.NewGuid(),
                    new OneTimeSecret("deferred-secret")))
            };
            var sessions = new FakeSessions(originalSession);
            var dispatcher = new DeferredDispatcher();
            await using var viewModel = new AdministratorRecoveryViewModel(recovery, sessions, dispatcher);

            await viewModel.RefreshAsync();
            await viewModel.RotateRecoveryKitAsync("current password");
            Assert.True(viewModel.CanRevealRecoveryKit);

            dispatcher.Defer = true;
            sessions.Publish(changedSession);

            // The session event has queued its UI cleanup, but the source of
            // truth already changed. Secret access must fail synchronously.
            if (changedSession.State == InteractiveSessionState.Locked)
                Assert.Null(viewModel.RevealRecoveryKit());
            else
                Assert.False(viewModel.CanRevealRecoveryKit);

            Assert.False(viewModel.HasRecoveryKit);
            Assert.Null(viewModel.RevealRecoveryKit());

            dispatcher.Defer = false;
            dispatcher.Drain();
            Assert.Null(viewModel.RevealRecoveryKit());
        }
    }

    [Fact]
    public async Task V108_U09_AtomicRevealKeepsOriginalOwnerAcrossPresentationGap()
    {
        var issuingSession = AuthenticatedSession();
        var recovery = new FakeRecovery
        {
            Status = new AdministratorRecoveryStatus("Station-01", true, "RotationRequired",
                RecoveryKitState.RotationRequired, null, Guid.NewGuid(), 1, 0, false),
            RotateHandler = request => ValueTask.FromResult(new RecoveryKitRotationResult(true,
                "RecoveryKitRotated", request.OperationId, AuditPersistence.Persisted, Guid.NewGuid(),
                new OneTimeSecret("atomic-secret")))
        };
        var sessions = new FakeSessions(issuingSession);
        await using var viewModel = new AdministratorRecoveryViewModel(recovery, sessions,
            new InlineDispatcher());

        await viewModel.RefreshAsync();
        await viewModel.RotateRecoveryKitAsync("current password");

        Assert.True(viewModel.TryRevealRecoveryKit(out var kit, out var principalId,
            out var sessionId));
        Assert.Equal("atomic-secret", kit);
        Assert.Equal(issuingSession.PrincipalId, principalId);
        Assert.Equal(issuingSession.SessionId, sessionId);

        sessions.Publish(AuthenticatedSession());

        // The page must validate this captured owner before writing the value
        // to a control or clipboard; it must not rebind to the new session.
        Assert.False(viewModel.IsRecoveryKitSessionCurrent(principalId, sessionId));
    }

    private static InteractiveSession AuthenticatedSession() =>
        new(InteractiveSessionState.Authenticated, "admin-principal", Guid.NewGuid());

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess => true;
        public ValueTask InvokeAsync(Action action)
        {
            action();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DeferredDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _queued = new();

        public bool Defer { get; set; }
        public bool CheckAccess => !Defer;

        public ValueTask InvokeAsync(Action action)
        {
            if (Defer) _queued.Enqueue(action);
            else action();
            return ValueTask.CompletedTask;
        }

        public void Drain()
        {
            while (_queued.Count != 0)
                _queued.Dequeue()();
        }
    }

    private sealed class FakeRecovery : ILocalAdministratorRecovery
    {
        public AdministratorRecoveryStatus Status { get; set; } =
            new("Station-01", false, "Unavailable", RecoveryKitState.Unavailable,
                null, null, 0, 0, false);
        public AdministratorRecoveryResult RecoverResult { get; set; } =
            new(false, "Unavailable", Guid.NewGuid(), AuditPersistence.NotAttempted);
        public RecoverAdministratorRequest? RecoverRequest { get; private set; }
        public RotateRecoveryKitRequest? RotateRequest { get; private set; }
        public ConfirmRecoveryKitCustodyRequest? ConfirmRequest { get; private set; }
        public Func<RotateRecoveryKitRequest, ValueTask<RecoveryKitRotationResult>>? RotateHandler { get; set; }
        public Func<ConfirmRecoveryKitCustodyRequest, ValueTask<AdministratorRecoveryResult>>? ConfirmHandler { get; set; }
        public bool ThrowOnRotate { get; set; }
        public int RotateCallCount { get; private set; }
        private readonly TaskCompletionSource<bool> _rotateStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<AdministratorRecoveryStatus> GetRecoveryStatusAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Status);

        public ValueTask<AdministratorRecoveryResult> RecoverAdministratorAsync(
            RecoverAdministratorRequest request, CancellationToken cancellationToken = default)
        {
            RecoverRequest = request;
            return ValueTask.FromResult(RecoverResult);
        }

        public ValueTask<RecoveryKitRotationResult> RotateRecoveryKitAsync(
            RotateRecoveryKitRequest request, CancellationToken cancellationToken = default)
        {
            RotateRequest = request;
            RotateCallCount++;
            _rotateStarted.TrySetResult(true);
            if (ThrowOnRotate) throw new InvalidOperationException("private failure details");
            return RotateHandler is null
                ? ValueTask.FromResult(new RecoveryKitRotationResult(false, "Unavailable",
                    request.OperationId, AuditPersistence.NotAttempted))
                : RotateHandler(request);
        }

        public ValueTask<AdministratorRecoveryResult> ConfirmRecoveryKitCustodyAsync(
            ConfirmRecoveryKitCustodyRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmRequest = request;
            return ConfirmHandler is null
                ? ValueTask.FromResult(new AdministratorRecoveryResult(false, "Unavailable",
                    request.OperationId, AuditPersistence.NotAttempted))
                : ConfirmHandler(request);
        }

        public Task WaitForRotateAsync() => _rotateStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private sealed class FakeSessions : IInteractiveSessionService
    {
        public InteractiveSession Current { get; private set; }
        public int SignInCalls { get; private set; }
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed;

        public FakeSessions() : this(new InteractiveSession(InteractiveSessionState.Unauthenticated, null, null)) { }

        public FakeSessions(InteractiveSession current) => Current = current;

        public ValueTask<SessionSignInResult> SignInAsync(PasswordSignInRequest request,
            CancellationToken cancellationToken = default)
        {
            SignInCalls++;
            throw new NotSupportedException();
        }

        public ValueTask<InteractiveSession> GetSessionAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);

        public ValueTask<SessionActionResult> LockAsync(Guid? expectedSessionId,
            SessionLockReason reason, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
}
