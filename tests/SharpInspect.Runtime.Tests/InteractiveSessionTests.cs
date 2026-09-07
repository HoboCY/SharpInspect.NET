using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class InteractiveSessionTests
{
    [Fact]
    public async Task V105_R01_SignInUsesProviderPersistsBeforePublishingAndNotifiesOutsideLock()
    {
        var expected = Identity("operator", "Operator");
        var provider = new ScriptedProvider(_ => new AuthenticationResult(true, "Authenticated", expected));
        var sink = new AuditSink();
        await using var service = CreateService(provider, sink);
        var changed = new TaskCompletionSource<InteractiveSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += (_, args) =>
        {
            // Reading Current here proves callbacks are not raised while the service lock is held.
            Assert.Equal(args.Session, service.Current);
            changed.TrySetResult(args.Session);
        };

        Assert.Equal(InteractiveSessionState.Unauthenticated, service.Current.State);
        var result = await service.SignInAsync(new PasswordSignInRequest("operator", "password"));

        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.Same(expected, result.Identity);
        Assert.Equal(InteractiveSessionState.Authenticated, result.Session.State);
        Assert.NotEqual(Guid.Empty, result.Session.SessionId);
        Assert.Equal(result.Session, service.Current);
        Assert.Single(provider.Requests);
        Assert.Single(sink.Events, value => value.Kind == "SessionStarted");
        Assert.Equal(result.Session.SessionId, sink.Events.Single(value => value.Kind == "SessionStarted").SessionId);
        Assert.Equal(result.Session, await changed.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task V105_R02_FailedProviderAuthenticationDoesNotCreateOrMisattributeSession()
    {
        var provider = new ScriptedProvider(_ =>
            new AuthenticationResult(false, "AuthenticationRejected", Identity("wrong", "Wrong")));
        var sink = new AuditSink();
        await using var service = CreateService(provider, sink);

        var result = await service.SignInAsync(new PasswordSignInRequest("wrong", "bad-password"));

        Assert.False(result.Succeeded);
        Assert.Null(result.Identity);
        Assert.Equal(InteractiveSessionState.Unauthenticated, result.Session.State);
        Assert.Equal(InteractiveSessionState.Unauthenticated, service.Current.State);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task V105_R03_ForgedOldAndLockedSessionIdsCannotRenewAndFreshLoginReplacesSession()
    {
        var attempt = 0;
        var provider = new ScriptedProvider(_ =>
            Interlocked.Increment(ref attempt) == 2
                ? new AuthenticationResult(false, "AuthenticationRejected")
                : new AuthenticationResult(true, "Authenticated", Identity("operator", "Operator")));
        var sink = new AuditSink();
        await using var service = CreateService(provider, sink);
        var first = await service.SignInAsync(new PasswordSignInRequest("operator", "password"));
        Assert.True(first.Succeeded, first.ReasonCode);
        var firstId = first.Session.SessionId!.Value;

        // A new attempt revokes the old session before provider work, even when the
        // replacement credentials are rejected.
        var rejectedReplacement = await service.SignInAsync(
            new PasswordSignInRequest("operator", "bad-password"));
        Assert.False(rejectedReplacement.Succeeded);
        Assert.Equal(InteractiveSessionState.Unauthenticated, service.Current.State);
        Assert.Contains(sink.Events, value => value.Kind == "SessionLoggedOut" &&
            value.SessionId == firstId && value.ReasonCode == "SessionReplaced");

        var forged = await service.ReportActivityAsync(Guid.NewGuid());
        Assert.False(forged.Succeeded);
        Assert.Equal("SessionMismatch", forged.ReasonCode);
        Assert.Equal(InteractiveSessionState.Unauthenticated, service.Current.State);

        var second = await service.SignInAsync(new PasswordSignInRequest("operator", "password"));
        Assert.True(second.Succeeded, second.ReasonCode);
        Assert.NotEqual(firstId, second.Session.SessionId);
        Assert.Equal(InteractiveSessionState.Authenticated, second.Session.State);

        var validReplacement = await service.SignInAsync(
            new PasswordSignInRequest("operator", "password"));
        Assert.True(validReplacement.Succeeded, validReplacement.ReasonCode);
        Assert.NotEqual(second.Session.SessionId, validReplacement.Session.SessionId);
        Assert.Equal(InteractiveSessionState.Authenticated, validReplacement.Session.State);

        var locked = await service.LockAsync(validReplacement.Session.SessionId,
            SessionLockReason.UserRequested);
        Assert.True(locked.Succeeded, locked.ReasonCode);
        Assert.True(locked.AuditPersisted);
        Assert.Equal(InteractiveSessionState.Locked, service.Current.State);
        Assert.False((await service.ReportActivityAsync(firstId)).Succeeded);

        var oldLogout = await service.LogoutAsync(firstId);
        Assert.False(oldLogout.Succeeded);
        Assert.Equal("SessionMismatch", oldLogout.ReasonCode);
        Assert.Equal(InteractiveSessionState.Locked, service.Current.State);
    }

    [Fact]
    public async Task V105_R04_IdleUsesMonotonicTimestampAndPollingDoesNotRenewActivity()
    {
        var clock = new TestClock(0);
        var policy = AuthenticationPolicy.Development with
        {
            SessionIdleTimeout = TimeSpan.FromMinutes(1),
            StepUpFreshness = TimeSpan.FromMinutes(1)
        };
        var provider = new ScriptedProvider(_ =>
            new AuthenticationResult(true, "Authenticated", Identity("operator", "Operator")));
        var sink = new AuditSink();
        await using var service = CreateService(provider, sink, policy, clock);
        var signedIn = await service.SignInAsync(new PasswordSignInRequest("operator", "password"));
        Assert.True(signedIn.Succeeded, signedIn.ReasonCode);
        var sessionId = signedIn.Session.SessionId!.Value;

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(InteractiveSessionState.Authenticated, (await service.GetSessionAsync()).State);
        // A backwards clock movement cannot make the idle deadline move forward.
        clock.Set(clock.Value - Stopwatch.Frequency * 10);
        Assert.Equal(InteractiveSessionState.Authenticated, (await service.GetSessionAsync()).State);
        // Polling still does not count as activity; this is sixty seconds after sign-in.
        clock.Set(Stopwatch.Frequency * 60);
        var expired = await service.GetSessionAsync();

        Assert.Equal(InteractiveSessionState.Locked, expired.State);
        Assert.False((await service.ReportActivityAsync(sessionId)).Succeeded);
        await EventuallyAsync(() => sink.Events.Any(value => value.Kind == "SessionLocked"));
        Assert.Contains(sink.Events, value => value.Kind == "SessionLocked" &&
            value.ReasonCode == SessionLockReason.IdleExpired.ToString());
    }

    [Fact]
    public async Task V105_R05_ProviderIdentityIsTheOnlyAuthenticationSource()
    {
        var expected = Identity("provider-user", "Provider User");
        var provider = new ScriptedProvider(_ => new AuthenticationResult(true, "Authenticated", expected));
        var sink = new AuditSink();
        await using var service = CreateService(provider, sink);

        var result = await service.SignInAsync(new PasswordSignInRequest("provider-user", "password"));

        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.Same(expected, result.Identity);
        Assert.Equal(expected.PrincipalId.ToString("D"), result.Session.PrincipalId);
        Assert.DoesNotContain(sink.Events, value => value.PrincipalId != expected.PrincipalId &&
            value.Kind == "SessionStarted");
    }

    [Fact]
    public async Task V105_R06_LockInvalidatesLateAuthenticationAndRequiresFreshSignIn()
    {
        var provider = new BlockingProvider();
        var sink = new AuditSink();
        await using var service = CreateService(provider, sink);
        var pending = service.SignInAsync(new PasswordSignInRequest("operator", "password")).AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var locked = await service.LockAsync(null, SessionLockReason.UserRequested);
        Assert.True(locked.Succeeded, locked.ReasonCode);
        Assert.Equal(InteractiveSessionState.Locked, service.Current.State);

        provider.Complete(new AuthenticationResult(true, "Authenticated", Identity("operator", "Operator")));
        var late = await pending.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(late.Succeeded);
        Assert.Equal("SessionOperationSuperseded", late.ReasonCode);
        Assert.Equal(InteractiveSessionState.Locked, service.Current.State);
        Assert.DoesNotContain(sink.Events, value => value.Kind == "SessionStarted");

        provider.ResetForNextCall();
        var freshTask = service.SignInAsync(new PasswordSignInRequest("operator", "password")).AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        provider.Complete(new AuthenticationResult(true, "Authenticated", Identity("operator", "Operator")));
        var fresh = await freshTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(fresh.Succeeded, fresh.ReasonCode);
        Assert.Equal(InteractiveSessionState.Authenticated, fresh.Session.State);
    }

    [Fact]
    public async Task V105_R07_LifecycleClearsStateWhenAuditPersistenceFails()
    {
        var provider = new ScriptedProvider(_ =>
            new AuthenticationResult(true, "Authenticated", Identity("operator", "Operator")));
        var sink = new AuditSink { Succeed = false };
        await using var service = CreateService(provider, sink);

        var rejected = await service.SignInAsync(new PasswordSignInRequest("operator", "password"));
        Assert.False(rejected.Succeeded);
        Assert.Equal("SessionAuditUnavailable", rejected.ReasonCode);
        Assert.Equal(InteractiveSessionState.Unauthenticated, service.Current.State);

        sink.Succeed = true;
        var signedIn = await service.SignInAsync(new PasswordSignInRequest("operator", "password"));
        Assert.True(signedIn.Succeeded, signedIn.ReasonCode);
        var sessionId = signedIn.Session.SessionId!.Value;

        sink.Succeed = false;
        var lockResult = await service.LockAsync(sessionId, SessionLockReason.WindowHidden);
        Assert.True(lockResult.Succeeded);
        Assert.False(lockResult.AuditPersisted);
        Assert.Equal(InteractiveSessionState.Locked, service.Current.State);

        sink.Succeed = true;
        var signedInAgain = await service.SignInAsync(new PasswordSignInRequest("operator", "password"));
        Assert.True(signedInAgain.Succeeded, signedInAgain.ReasonCode);
        sink.Succeed = false;
        var logout = await service.LogoutAsync(signedInAgain.Session.SessionId);
        Assert.True(logout.Succeeded);
        Assert.False(logout.AuditPersisted);
        Assert.Equal(InteractiveSessionState.Unauthenticated, service.Current.State);
    }

    [Fact]
    public async Task V105_R08_AuthenticationCapacityIsBoundedAndDisposeDoesNotAwaitIgnoringProvider()
    {
        var provider = new BlockingProvider();
        var sink = new AuditSink();
        var service = CreateService(provider, sink);
        var pending = Enumerable.Range(0, 16)
            .Select(_ => service.SignInAsync(new PasswordSignInRequest("operator", "password")).AsTask())
            .ToArray();
        await EventuallyAsync(() => provider.CallCount == 16);

        var bounded = await service.SignInAsync(new PasswordSignInRequest("operator", "password"));
        Assert.False(bounded.Succeeded);
        Assert.Equal("SessionCapacityExceeded", bounded.ReasonCode);

        var started = Stopwatch.GetTimestamp();
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var elapsed = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency);
        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"Dispose took {elapsed}.");

        provider.CompleteAll(new AuthenticationResult(true, "Authenticated", Identity("operator", "Operator")));
        var results = await Task.WhenAll(pending);
        Assert.All(results, result => Assert.False(result.Succeeded));
        Assert.Equal(InteractiveSessionState.Unauthenticated, service.Current.State);
    }

    private static InteractiveSessionService CreateService(
        IIdentityProvider provider,
        AuditSink sink,
        AuthenticationPolicy? policy = null,
        TestClock? clock = null) =>
        new(provider, policy ?? AuthenticationPolicy.Development, sink.Persist,
            clock is null ? null : clock.Now);

    private static HumanIdentity Identity(string userName, string displayName) =>
        new(Guid.NewGuid(), userName, displayName,
            new[] { new IdentityDisplayClaim("test", "provider") });

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.True(condition(), "Expected condition did not become true.");
    }

    private sealed class TestClock
    {
        public TestClock(long value) => Value = value;
        public long Value { get; private set; }
        public long Now() => Value;
        public void Set(long value) => Value = value;
        public void Advance(TimeSpan value) => Value += (long)(value.TotalSeconds * Stopwatch.Frequency);
    }

    private sealed class AuditSink
    {
        private readonly object _sync = new();
        public bool Succeed { get; set; } = true;
        public List<SessionAuditEvent> Events { get; } = new();

        public ValueTask<bool> Persist(SessionAuditEvent audit, CancellationToken cancellationToken)
        {
            lock (_sync) Events.Add(audit);
            return ValueTask.FromResult(Succeed);
        }
    }

    private sealed class ScriptedProvider : IIdentityProvider
    {
        private readonly Func<PasswordSignInRequest, AuthenticationResult> _handler;
        public ScriptedProvider(Func<PasswordSignInRequest, AuthenticationResult> handler) => _handler = handler;
        public List<PasswordSignInRequest> Requests { get; } = new();

        public ValueTask<AuthenticationResult> AuthenticateAsync(
            PasswordSignInRequest request, CancellationToken cancellationToken = default)
        {
            lock (Requests) Requests.Add(request);
            return ValueTask.FromResult(_handler(request));
        }
    }

    private sealed class BlockingProvider : IIdentityProvider
    {
        private readonly object _sync = new();
        private readonly List<TaskCompletionSource<AuthenticationResult>> _calls = new();
        private TaskCompletionSource<bool> _started = NewStarted();
        private int _callCount;
        public TaskCompletionSource<bool> Started => _started;
        public int CallCount { get { lock (_sync) return _callCount; } }

        public ValueTask<AuthenticationResult> AuthenticateAsync(
            PasswordSignInRequest request, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<AuthenticationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _calls.Add(completion);
                _callCount++;
                _started.TrySetResult(true);
            }

            return new ValueTask<AuthenticationResult>(completion.Task);
        }

        public void Complete(AuthenticationResult result)
        {
            TaskCompletionSource<AuthenticationResult>? completion;
            lock (_sync) completion = _calls.FirstOrDefault(call => !call.Task.IsCompleted);
            completion?.TrySetResult(result);
        }

        public void CompleteAll(AuthenticationResult result)
        {
            TaskCompletionSource<AuthenticationResult>[] calls;
            lock (_sync) calls = _calls.ToArray();
            foreach (var call in calls) call.TrySetResult(result);
        }

        public void ResetForNextCall()
        {
            lock (_sync)
            {
                _calls.Clear();
                _callCount = 0;
                _started = NewStarted();
            }
        }

        private static TaskCompletionSource<bool> NewStarted() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
