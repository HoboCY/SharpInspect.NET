using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class SessionAuthorizationLeaseTests
{
    [Fact]
    public async Task V106_L01_WrongPrincipalAndSessionCannotAcquireLease()
    {
        var provider = new TestProvider();
        await using var service = CreateService(provider);
        var signedIn = await service.SignInAsync(new PasswordSignInRequest("operator", "password"));
        Assert.True(signedIn.Succeeded, signedIn.ReasonCode);

        var principalId = signedIn.Identity!.PrincipalId;
        var sessionId = signedIn.Session.SessionId!.Value;

        Assert.False(service.TryAcquireAuthorizationLease(
            Guid.NewGuid(), sessionId, out var wrongPrincipalLease, out var wrongPrincipalReason));
        Assert.Null(wrongPrincipalLease);
        Assert.Equal("SessionMismatch", wrongPrincipalReason);

        Assert.False(service.TryAcquireAuthorizationLease(
            principalId, Guid.NewGuid(), out var wrongSessionLease, out var wrongSessionReason));
        Assert.Null(wrongSessionLease);
        Assert.Equal("SessionMismatch", wrongSessionReason);

        Assert.True(service.TryAcquireAuthorizationLease(
            principalId, sessionId, out var lease, out var reason), reason);
        Assert.NotNull(lease);
        Assert.Equal(principalId, lease!.Identity.PrincipalId);
        Assert.Equal(sessionId, lease.SessionId);
        lease.Dispose();
    }

    [Fact]
    public async Task V106_L02_ExpiredSessionIsRejectedAndIdleMonitorLocksIt()
    {
        var clock = new TestClock(0);
        var provider = new TestProvider();
        var sink = new AuditSink();
        var policy = AuthenticationPolicy.Development with
        {
            SessionIdleTimeout = TimeSpan.FromMinutes(1),
            StepUpFreshness = TimeSpan.FromMinutes(1)
        };
        await using var service = new InteractiveSessionService(
            provider, policy, sink.Persist, clock.Now);
        var signedIn = await service.SignInAsync(new PasswordSignInRequest("operator", "password"));
        Assert.True(signedIn.Succeeded, signedIn.ReasonCode);

        clock.Set(Stopwatch.Frequency * 60);
        Assert.False(service.TryAcquireAuthorizationLease(
            signedIn.Identity!.PrincipalId, signedIn.Session.SessionId!.Value,
            out var lease, out var reason));
        Assert.Null(lease);
        Assert.Equal("SessionIdleExpired", reason);

        await EventuallyAsync(() => service.Current.State == InteractiveSessionState.Locked);
        Assert.Contains(sink.Events, audit => audit.Kind == "SessionLocked" &&
            audit.ReasonCode == SessionLockReason.IdleExpired.ToString());
    }

    [Fact]
    public async Task V106_L03_LeaseBlocksLockUntilReleasedAndThenCannotBeReacquired()
    {
        var provider = new TestProvider();
        await using var service = CreateService(provider);
        var signedIn = await service.SignInAsync(new PasswordSignInRequest("operator", "password"));
        Assert.True(signedIn.Succeeded, signedIn.ReasonCode);
        var sessionId = signedIn.Session.SessionId!.Value;

        // Monitor ownership is thread-affine. Keep this entire lease interval synchronous;
        // an await here would test an invalid cross-thread use and could leak the test lock.
        var locked = await Task.Run(() =>
        {
            Assert.True(service.TryAcquireAuthorizationLease(
                signedIn.Identity!.PrincipalId, sessionId, out var lease, out var reason), reason);
            Assert.NotNull(lease);
            using var lockStarted = new ManualResetEventSlim();
            Task<SessionActionResult> lockTask;
            try
            {
                lockTask = Task.Run(async () =>
                {
                    lockStarted.Set();
                    return await service.LockAsync(sessionId, SessionLockReason.UserRequested);
                });
                Assert.True(lockStarted.Wait(TimeSpan.FromSeconds(1)));
                Assert.False(lockTask.Wait(TimeSpan.FromMilliseconds(100)));
            }
            finally { lease!.Dispose(); }
            return lockTask;
        }).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(locked.Succeeded, locked.ReasonCode);
        Assert.Equal(InteractiveSessionState.Locked, service.Current.State);

        Assert.False(service.TryAcquireAuthorizationLease(
            signedIn.Identity!.PrincipalId, sessionId, out var afterLockLease, out var afterLockReason));
        Assert.Null(afterLockLease);
        Assert.Equal("SessionMismatch", afterLockReason);
    }

    [Fact]
    public async Task V106_L04_ReentrantAcquireIsRejectedAndReleaseDoesNotLeak()
    {
        var provider = new TestProvider();
        await using var service = CreateService(provider);
        var signedIn = await service.SignInAsync(new PasswordSignInRequest("operator", "password"));
        Assert.True(signedIn.Succeeded, signedIn.ReasonCode);
        var principalId = signedIn.Identity!.PrincipalId;
        var sessionId = signedIn.Session.SessionId!.Value;

        Assert.True(service.TryAcquireAuthorizationLease(
            principalId, sessionId, out var lease, out var reason), reason);
        Assert.NotNull(lease);

        Assert.False(service.TryAcquireAuthorizationLease(
            principalId, sessionId, out var nestedLease, out var nestedReason));
        Assert.Null(nestedLease);
        Assert.Equal("AuthorizationLeaseReentrant", nestedReason);

        lease!.Dispose();
        Assert.True(service.TryAcquireAuthorizationLease(
            principalId, sessionId, out var reacquired, out var reacquireReason), reacquireReason);
        reacquired!.Dispose();
    }

    private static InteractiveSessionService CreateService(TestProvider provider) =>
        new(provider, AuthenticationPolicy.Development, new AuditSink().Persist);

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    private sealed class TestProvider : IIdentityProvider
    {
        private readonly HumanIdentity _identity = new(
            Guid.NewGuid(), "operator", "Operator");

        public ValueTask<AuthenticationResult> AuthenticateAsync(
            PasswordSignInRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AuthenticationResult(true, "Authenticated", _identity));
    }

    private sealed class AuditSink
    {
        private readonly object _sync = new();
        internal List<SessionAuditEvent> Events { get; } = new();

        internal ValueTask<bool> Persist(SessionAuditEvent audit, CancellationToken cancellationToken)
        {
            lock (_sync) Events.Add(audit);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class TestClock
    {
        internal TestClock(long value) => Value = value;
        internal long Value { get; private set; }
        internal long Now() => Value;
        internal void Set(long value) => Value = value;
    }
}
