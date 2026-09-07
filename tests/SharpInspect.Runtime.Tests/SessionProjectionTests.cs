using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class SessionProjectionTests
{
    [Fact]
    public async Task V105_I01_ConstructorSubscribesBeforeReconcilingInitialSession()
    {
        var initial = Session(InteractiveSessionState.Authenticated, "before");
        var authority = new ControlledSessionService(initial)
        {
            TransitionAfterFirstCurrentRead = true,
            TransitionAfterFirstCurrentReadTo = Session(InteractiveSessionState.Locked, null)
        };

        await using var runtime = new StationRuntime(null, TimeSpan.FromSeconds(30), authority);

        var snapshot = await runtime.GetSnapshotAsync();

        Assert.Equal(InteractiveSessionState.Locked, snapshot.Session.State);
        Assert.Null(snapshot.Session.PrincipalId);
        Assert.Null(snapshot.Session.SessionId);
    }

    [Fact]
    public async Task V105_I02_GetSnapshotReconcilesSilentAuthorityChangeOnSessionAxisOnly()
    {
        var authority = new ControlledSessionService(Session(InteractiveSessionState.Unauthenticated, null));
        await using var runtime = new StationRuntime(null, TimeSpan.FromSeconds(30), authority);
        var before = await runtime.GetSnapshotAsync();

        var authenticated = Session(InteractiveSessionState.Authenticated, "operator");
        authority.SetSilently(authenticated);

        var after = await runtime.GetSnapshotAsync();

        Assert.Equal(authenticated, after.Session);
        AssertSessionAxesUnchanged(before, after);
    }

    [Fact]
    public async Task V105_I03_WatchInitialSnapshotReconcilesSilentAuthorityChange()
    {
        var authority = new ControlledSessionService(Session(InteractiveSessionState.Unauthenticated, null));
        await using var runtime = new StationRuntime(null, TimeSpan.FromSeconds(30), authority);
        var before = await runtime.GetSnapshotAsync();
        var authenticated = Session(InteractiveSessionState.Authenticated, "operator");
        authority.SetSilently(authenticated);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var feed = runtime.WatchSnapshotsAsync(timeout.Token).GetAsyncEnumerator();

        Assert.True(await feed.MoveNextAsync());
        var first = feed.Current;
        Assert.Equal(authenticated, first.Session);
        AssertSessionAxesUnchanged(before, first);
    }

    private static InteractiveSession Session(InteractiveSessionState state, string? principalId)
        => new(state, principalId, state == InteractiveSessionState.Authenticated ? Guid.NewGuid() : null);

    private static void AssertSessionAxesUnchanged(StationStateSnapshot before, StationStateSnapshot after)
    {
        Assert.Equal(before.RuntimeEpoch, after.RuntimeEpoch);
        Assert.Equal(before.Lifecycle, after.Lifecycle);
        Assert.Equal(before.Mode, after.Mode);
        Assert.Equal(before.ArmState, after.ArmState);
        Assert.Equal(before.Ready, after.Ready);
        Assert.Equal(before.Busy, after.Busy);
        Assert.Equal(before.Handshake, after.Handshake);
        Assert.Equal(before.Recovery, after.Recovery);
        Assert.Equal(before.ActiveRecipe, after.ActiveRecipe);
        Assert.Equal(before.CurrentExecution, after.CurrentExecution);
        Assert.Equal(before.Camera, after.Camera);
        Assert.Equal(before.Plc, after.Plc);
        Assert.Equal(before.Store, after.Store);
        Assert.Equal(before.Evidence, after.Evidence);
        Assert.Equal(before.Qualification, after.Qualification);
        Assert.Equal(before.Performance, after.Performance);
        Assert.Equal(before.Alarms, after.Alarms);
        Assert.Equal(before.LastCommand, after.LastCommand);
        Assert.Equal(before.AdmissionBlockers.ToArray(), after.AdmissionBlockers.ToArray());
        Assert.Equal(before.AuditIntegrity, after.AuditIntegrity);
    }

    private sealed class ControlledSessionService : IInteractiveSessionService
    {
        private EventHandler<InteractiveSessionChangedEventArgs>? _changed;
        private InteractiveSession _current;
        private int _transitionAfterFirstCurrentRead;

        public ControlledSessionService(InteractiveSession current) => _current = current;

        public bool TransitionAfterFirstCurrentRead
        {
            get => Volatile.Read(ref _transitionAfterFirstCurrentRead) != 0;
            set => Volatile.Write(ref _transitionAfterFirstCurrentRead, value ? 1 : 0);
        }

        public InteractiveSession? TransitionAfterFirstCurrentReadTo { get; set; }

        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public InteractiveSession Current
        {
            get
            {
                var observed = _current;
                if (Interlocked.Exchange(ref _transitionAfterFirstCurrentRead, 0) != 0)
                {
                    var next = TransitionAfterFirstCurrentReadTo ??
                        new InteractiveSession(InteractiveSessionState.Locked, null, null);
                    SetAndPublish(next);
                }

                return observed;
            }
        }

        public void SetSilently(InteractiveSession session) => _current = session;

        private void SetAndPublish(InteractiveSession session)
        {
            _current = session;
            _changed?.Invoke(this, new InteractiveSessionChangedEventArgs(session));
        }

        public ValueTask<SessionSignInResult> SignInAsync(
            PasswordSignInRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<InteractiveSession> GetSessionAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);

        public ValueTask<SessionActionResult> LockAsync(
            Guid? expectedSessionId, SessionLockReason reason,
            CancellationToken cancellationToken = default)
        {
            SetAndPublish(new InteractiveSession(InteractiveSessionState.Locked, null, null));
            return ValueTask.FromResult(new SessionActionResult(true, "SessionLocked", true));
        }

        public ValueTask<SessionActionResult> LogoutAsync(
            Guid? expectedSessionId, CancellationToken cancellationToken = default)
        {
            SetAndPublish(new InteractiveSession(InteractiveSessionState.Unauthenticated, null, null));
            return ValueTask.FromResult(new SessionActionResult(true, "SessionLoggedOut", true));
        }

        public ValueTask<SessionActionResult> ReportActivityAsync(
            Guid sessionId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SessionActionResult(true, "ActivityRecorded", true));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
