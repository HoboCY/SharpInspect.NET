using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeActivationSessionLockTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V132_C09_AuthorizationCallbackDeclinesSessionProjectionContention(bool committing)
    {
        using var sessions = new BlockingSessionQuery();
        await using var runtime = new StationRuntime(new ProbeAuditWriter(),
            TimeSpan.FromSeconds(30), sessions: sessions);
        using var reservation = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.True(reservation.Available, reservation.Failure);
        using var commit = await reservation.EnterCommitAsync(CancellationToken.None);
        sessions.Block = true;
        var reading = Task.Run(async () => await runtime.GetSnapshotAsync());
        try
        {
            // A snapshot owns the station lock while reconciling Current. In the
            // real service Current may wait for the writer's authorization lease.
            await sessions.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var decision = Task.Run(() => committing ? commit.TryBeginCommit() : reservation.GetBlocker());
            Assert.Equal("RecipeActivationRuntimeBusy", await decision.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            sessions.Release.Set();
            await reading.WaitAsync(TimeSpan.FromSeconds(2));
        }
        // Declining lock contention must not claim a commit or keep the command gate.
        commit.Dispose();
        var stop = await runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(),
            new CommandInvocation(CommandSource.PhysicalConsole))).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CommandDisposition.Accepted, stop.Disposition);
        Assert.Equal("RecipeActivationCancelled", reservation.GetBlocker());
        reservation.PublishTerminal("RecipeActivationCancelled", false);
        reservation.Dispose();
        await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }

    private sealed class BlockingSessionQuery : IInteractiveSessionService, IDisposable
    {
        internal bool Block { get; set; }
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        public InteractiveSession Current
        {
            get
            {
                if (Block)
                {
                    Entered.TrySetResult(true);
                    Release.Wait();
                }
                return new(InteractiveSessionState.Unauthenticated, null, null);
            }
        }
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed { add { } remove { } }
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
        public void Dispose() => Release.Dispose();
    }
}
