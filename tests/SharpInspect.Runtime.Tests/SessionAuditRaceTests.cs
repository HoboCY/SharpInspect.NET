using System.Collections.Concurrent;
using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class SessionAuditRaceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task V105_R09_LateStartCommitIsClosedExactlyOnceWithoutPublishingSession(bool committed)
    {
        var sink = new ControlledSink("SessionStarted");
        await using var service = Create(sink);
        var pending = service.SignInAsync(new("alice", "password")).AsTask();
        await sink.Held.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(result.Succeeded);
        Assert.Equal("SessionAuditUnavailable", result.ReasonCode);
        Assert.Equal(InteractiveSessionState.Unauthenticated, service.Current.State);
        sink.Release.TrySetResult(committed);
        await sink.HeldCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (committed)
        {
            await Eventually(() => sink.Committed.Any(e => e.Kind == "SessionSignInCancelled"));
            var cancellation = Assert.Single(sink.Committed, e => e.Kind == "SessionSignInCancelled");
            Assert.Equal(sink.HeldEvent!.SessionId, cancellation.SessionId);
            Assert.Equal(sink.HeldEvent.PrincipalId, cancellation.PrincipalId);
        }
        else
        {
            await Task.Delay(50);
            Assert.DoesNotContain(sink.Committed, e => e.Kind == "SessionSignInCancelled");
        }
        Assert.Equal(InteractiveSessionState.Unauthenticated, service.Current.State);
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("logout")]
    [InlineData("idle")]
    [InlineData("replace")]
    public async Task V105_R10_PendingOrFailedRevocationBlocksNewSessionUntilItsEvidenceCommits(string action)
    {
        long timestamp = 0;
        var sink = new ControlledSink();
        await using var service = Create(sink, () => Interlocked.Read(ref timestamp));
        var first = await service.SignInAsync(new("alice", "password"));
        Assert.True(first.Succeeded);
        sink.HoldKind = action is "logout" or "replace" ? "SessionLoggedOut" : "SessionLocked";
        Task transition;
        if (action == "lock") transition = service.LockAsync(first.Session.SessionId, SessionLockReason.UserRequested).AsTask();
        else if (action == "logout") transition = service.LogoutAsync(first.Session.SessionId).AsTask();
        else if (action == "replace") transition = service.SignInAsync(new("alice", "password")).AsTask();
        else
        {
            Interlocked.Exchange(ref timestamp, Stopwatch.Frequency * 60);
            Assert.Equal(InteractiveSessionState.Locked, service.Current.State);
            transition = Task.CompletedTask;
        }
        await sink.Held.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var concurrent = await service.SignInAsync(new("alice", "password")).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(concurrent.Succeeded);
        Assert.NotEqual(InteractiveSessionState.Authenticated, service.Current.State);
        Assert.Single(sink.Committed, e => e.Kind == "SessionStarted");

        sink.AllowSubsequentCommits = false;
        sink.Release.TrySetResult(false);
        await sink.HeldCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await transition.WaitAsync(TimeSpan.FromSeconds(2));
        var failedRetry = await service.SignInAsync(new("alice", "password"));
        Assert.False(failedRetry.Succeeded);
        Assert.NotEqual(InteractiveSessionState.Authenticated, service.Current.State);

        sink.AllowSubsequentCommits = true;
        var retry = await service.SignInAsync(new("alice", "password"));
        Assert.True(retry.Succeeded, retry.ReasonCode);
        var events = sink.Committed.ToArray();
        var revocation = Array.FindIndex(events, e => e.SessionId == first.Session.SessionId && e.Kind == sink.HoldKind);
        var newStart = Array.FindIndex(events, e => e.SessionId == retry.Session.SessionId && e.Kind == "SessionStarted");
        Assert.True(revocation >= 0 && newStart > revocation);
        Assert.NotEqual(first.Session.SessionId, retry.Session.SessionId);
    }

    private static InteractiveSessionService Create(ControlledSink sink, Func<long>? clock = null) =>
        new(new Provider(), AuthenticationPolicy.Development with
        { SessionIdleTimeout = TimeSpan.FromMinutes(1), StepUpFreshness = TimeSpan.FromMinutes(1) }, sink.Persist, clock);

    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Provider : IIdentityProvider
    {
        private readonly HumanIdentity _identity = new(Guid.NewGuid(), "alice", "Alice");
        public ValueTask<AuthenticationResult> AuthenticateAsync(PasswordSignInRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new AuthenticationResult(true, "Authenticated", _identity));
    }

    private sealed class ControlledSink
    {
        private int _held;
        internal ControlledSink(string? holdKind = null) => HoldKind = holdKind;
        internal string? HoldKind { get; set; }
        internal bool AllowSubsequentCommits { get; set; } = true;
        internal SessionAuditEvent? HeldEvent { get; private set; }
        internal ConcurrentQueue<SessionAuditEvent> Committed { get; } = new();
        internal TaskCompletionSource<bool> Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> HeldCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async ValueTask<bool> Persist(SessionAuditEvent fact, CancellationToken cancellationToken)
        {
            if (fact.Kind == HoldKind && Interlocked.CompareExchange(ref _held, 1, 0) == 0)
            {
                HeldEvent = fact;
                Held.TrySetResult(true);
                var committed = await Release.Task;
                if (committed) Committed.Enqueue(fact);
                HeldCompleted.TrySetResult(true);
                return committed;
            }
            if (!AllowSubsequentCommits) return false;
            Committed.Enqueue(fact);
            return true;
        }
    }
}
