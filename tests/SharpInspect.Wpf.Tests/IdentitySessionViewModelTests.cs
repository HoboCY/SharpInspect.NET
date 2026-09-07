using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class IdentitySessionViewModelTests
{
    [Fact]
    public async Task V105_U01_SessionBoundaryReceivesCompletePasswordAndLockClearsDisplay()
    {
        var sessions = new FixtureSessions();
        await using var vm = new IdentityViewModel(null, null, "station", sessions, new InlineDispatcher());
        const string password = "  空格和密码管理器𝄞 unchanged  ";
        Assert.True(vm.IsConfigured);
        Assert.True(vm.CanAuthenticate);
        var result = await vm.AuthenticateAsync("alice", password);
        Assert.True(result?.Succeeded);
        Assert.Equal(password, sessions.Request?.Password);
        Assert.True(vm.HasIdentity);
        var old = vm.CurrentSession.SessionId;
        await vm.LockSessionAsync(SessionLockReason.IdleExpired);
        Assert.False(vm.HasIdentity);
        Assert.Equal(InteractiveSessionState.Locked, vm.CurrentSession.State);
        Assert.Null(vm.CurrentSession.SessionId);
        Assert.Equal(old, sessions.LastExpectedId);
    }

    [Fact]
    public async Task V105_U02_UnclaimedRecoveryDeliveryRequiresItsOwnerSessionAfterLock()
    {
        var owner = new HumanIdentity(Guid.NewGuid(), "alice", "Alice");
        var sessions = new FixtureSessions { Identity = owner };
        var bootstrap = new FixtureBootstrap(owner);
        await using var vm = new IdentityViewModel(bootstrap, null, "station", sessions, new InlineDispatcher());
        await vm.CreateFirstAdministratorAsync("token", "alice", "Alice", "password");
        Assert.False(vm.CanRevealRecoveryKit);
        Assert.Null(vm.RevealRecoveryKit());
        await vm.AuthenticateAsync("alice", "password");
        Assert.True(vm.CanRevealRecoveryKit);
        await vm.LockSessionAsync(SessionLockReason.WindowHidden);
        Assert.False(vm.CanRevealRecoveryKit);
        sessions.Identity = new HumanIdentity(Guid.NewGuid(), "bob", "Bob");
        await vm.AuthenticateAsync("bob", "password");
        Assert.Null(vm.RevealRecoveryKit());
        sessions.Identity = owner;
        await vm.AuthenticateAsync("alice", "password");
        Assert.Equal("test-recovery-kit", vm.RevealRecoveryKit());
        Assert.Null(vm.RevealRecoveryKit());
    }

    [Fact]
    public async Task V105_U03_LockDuringSubscriptionCannotLeaveAnAuthenticatedProjection()
    {
        var sessions = new FixtureSessions();
        await sessions.SignInAsync(new("alice", "password"));
        sessions.LockWhenSubscribed = true;
        await using var vm = new IdentityViewModel(null, null, "station", sessions, new InlineDispatcher());
        Assert.Equal(InteractiveSessionState.Locked, vm.CurrentSession.State);
        Assert.False(vm.HasIdentity);
        Assert.False(vm.CanRevealRecoveryKit);
    }

    [Fact]
    public async Task V105_U04_DelayedUiNotificationCannotKeepIdentityOrRecoveryDeliveryAfterLock()
    {
        var owner = new HumanIdentity(Guid.NewGuid(), "alice", "Alice");
        var sessions = new FixtureSessions { Identity = owner };
        await using var vm = new IdentityViewModel(new FixtureBootstrap(owner), null, "station", sessions,
            new DelayedDispatcher());
        await vm.CreateFirstAdministratorAsync("token", "alice", "Alice", "password");
        await vm.AuthenticateAsync("alice", "password");
        Assert.True(vm.HasIdentity);
        Assert.True(vm.CanRevealRecoveryKit);
        await sessions.LockAsync(sessions.Current.SessionId, SessionLockReason.IdleExpired);
        Assert.Equal(InteractiveSessionState.Locked, vm.CurrentSession.State);
        Assert.False(vm.HasIdentity);
        Assert.Null(vm.CurrentIdentity);
        Assert.False(vm.CanRevealRecoveryKit);
        Assert.Null(vm.RevealRecoveryKit());
    }

    private sealed class DelayedDispatcher : IUiDispatcher
    {
        public bool CheckAccess => false;
        public ValueTask InvokeAsync(Action action) => ValueTask.CompletedTask;
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess => true;
        public ValueTask InvokeAsync(Action action) { action(); return ValueTask.CompletedTask; }
    }

    private sealed class FixtureBootstrap : ILocalAdministratorBootstrap
    {
        private readonly HumanIdentity _identity;
        internal FixtureBootstrap(HumanIdentity identity) => _identity = identity;
        public ValueTask<BootstrapTokenResult> ProvisionBootstrapTokenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new BootstrapTokenResult(false, "NotUsed"));
        public ValueTask<BootstrapAdministratorResult> CreateFirstAdministratorAsync(BootstrapAdministratorRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new BootstrapAdministratorResult(true,
                "AdministratorCreated", _identity, RecoveryKit: new OneTimeSecret("test-recovery-kit")));
        public ValueTask<StationIdentityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StationIdentityStatus("station", false, 1, 8, "Present"));
    }

    private sealed class FixtureSessions : IInteractiveSessionService
    {
        public HumanIdentity Identity { get; set; } = new(Guid.NewGuid(), "alice", "Alice");
        public PasswordSignInRequest? Request { get; private set; }
        public Guid? LastExpectedId { get; private set; }
        public InteractiveSession Current { get; private set; } = new(InteractiveSessionState.Unauthenticated, null, null);
        public bool LockWhenSubscribed { get; set; }
        private EventHandler<InteractiveSessionChangedEventArgs>? _changed;
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed
        {
            add
            {
                if (LockWhenSubscribed) Current = new(InteractiveSessionState.Locked, null, null);
                _changed += value;
            }
            remove => _changed -= value;
        }
        public ValueTask<SessionSignInResult> SignInAsync(PasswordSignInRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            Current = new(InteractiveSessionState.Authenticated, Identity.PrincipalId.ToString("D"), Guid.NewGuid());
            _changed?.Invoke(this, new(Current));
            return ValueTask.FromResult(new SessionSignInResult(true, "Authenticated", Identity, Current));
        }
        public ValueTask<InteractiveSession> GetSessionAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);
        public ValueTask<SessionActionResult> LockAsync(Guid? expectedSessionId, SessionLockReason reason, CancellationToken cancellationToken = default)
        {
            LastExpectedId = expectedSessionId;
            Current = new(InteractiveSessionState.Locked, null, null);
            _changed?.Invoke(this, new(Current));
            return ValueTask.FromResult(new SessionActionResult(true, "SessionLocked", true));
        }
        public ValueTask<SessionActionResult> LogoutAsync(Guid? expectedSessionId, CancellationToken cancellationToken = default) =>
            LockAsync(expectedSessionId, SessionLockReason.UserRequested, cancellationToken);
        public ValueTask<SessionActionResult> ReportActivityAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SessionActionResult(true, "ActivityRecorded", true));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
