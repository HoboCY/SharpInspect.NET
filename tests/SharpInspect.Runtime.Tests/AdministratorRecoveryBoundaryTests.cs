using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class AdministratorRecoveryBoundaryTests
{
    [Fact]
    public async Task V108_R01_LocalDisarmingNeverSubstitutesForSafeLineEvidence()
    {
        await using var runtime = new StationRuntime(new ProbeAuditWriter(), TimeSpan.FromMilliseconds(20));
        var gate = (IAdministratorRecoveryRuntimeGate)runtime;
        Assert.Equal("SafetyStopUnverified", gate.GetBlocker());
        using (var rejected = await gate.EnterAsync(CancellationToken.None))
            Assert.Equal("SafetyStopUnverified", rejected.Check());
        var stop = await runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(),
            new CommandInvocation(CommandSource.PhysicalConsole)));
        Assert.Equal(CommandDisposition.Accepted, stop.Disposition);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (var snapshot in runtime.WatchSnapshotsAsync(timeout.Token))
            if (snapshot.LastCommand?.State == OperationState.Completed) break;
        Assert.Equal("SafetyStopUnverified", gate.GetBlocker());
        var stopped = await runtime.GetSnapshotAsync();
        Assert.False(stopped.Ready);
        Assert.Equal(HandshakePhase.Unknown, stopped.Handshake);
        Assert.Equal(RecoveryState.Required, stopped.Recovery);
        await runtime.DisposeAsync();
        using var disposed = await gate.EnterAsync(CancellationToken.None);
        Assert.Equal("RecoveryRuntimeStopped", disposed.Check());
    }

    [Fact]
    public async Task V108_R02_AnonymousRecoveryLeaseSerializesAConcurrentSignIn()
    {
        var provider = new Provider();
        await using var service = Create(provider);
        // Monitor leases are deliberately synchronous and thread-affine, like a SQLite callback.
        var signedIn = await Task.Run(() =>
        {
            Assert.True(service.TryAcquireRecoveryLease(null, null, out var lease, out var reason), reason);
            using var started = new ManualResetEventSlim();
            Task<SessionSignInResult> signIn;
            try
            {
                Assert.False(service.TryAcquireRecoveryLease(null, null, out var nested, out _));
                Assert.Null(nested);
                signIn = Task.Run(async () =>
                {
                    started.Set();
                    return await service.SignInAsync(new("person", "password"));
                });
                Assert.True(started.Wait(TimeSpan.FromSeconds(1)));
                Assert.False(signIn.Wait(TimeSpan.FromMilliseconds(100)));
                Assert.Equal(0, provider.Calls);
            }
            finally { lease!.Dispose(); }
            return signIn;
        }).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(signedIn.Succeeded);
        Assert.False(service.TryAcquireRecoveryLease(null, null, out _, out _));
        Assert.True(service.TryAcquireRecoveryLease(provider.Identity.PrincipalId, signedIn.Session.SessionId,
            out var authenticated, out var authorizationReason), authorizationReason);
        authenticated!.Dispose();
    }

    [Fact]
    public async Task V108_R03_InFlightAndLateCancelledProviderRemainRecoveryConflicts()
    {
        var release = new TaskCompletionSource<AuthenticationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider { Reply = () => new ValueTask<AuthenticationResult>(release.Task) };
        await using var service = Create(provider);
        using var cancellation = new CancellationTokenSource();
        var signIn = service.SignInAsync(new("person", "password"), cancellation.Token).AsTask();
        Assert.Equal(1, provider.Calls);
        Assert.False(service.TryAcquireRecoveryLease(null, null, out _, out _));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signIn);
        Assert.Equal(InteractiveSessionState.Unauthenticated, service.Current.State);
        Assert.False(service.TryAcquireRecoveryLease(null, null, out _, out _));
        release.SetResult(new(true, "Authenticated", provider.Identity));
        await EventuallyAnonymousLease(service);
        Assert.Equal(InteractiveSessionState.Unauthenticated, service.Current.State);
    }

    [Fact]
    public async Task V108_R04_UnauthenticatedProjectionCannotHidePendingLogoutPersistence()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider();
        await using var service = new InteractiveSessionService(provider, AuthenticationPolicy.Development,
            (fact, _) => fact.Kind == "SessionLoggedOut" ? new ValueTask<bool>(release.Task) : ValueTask.FromResult(true));
        var signedIn = await service.SignInAsync(new("person", "password"));
        var logout = service.LogoutAsync(signedIn.Session.SessionId).AsTask();
        Assert.Equal(InteractiveSessionState.Unauthenticated, service.Current.State);
        Assert.False(service.TryAcquireRecoveryLease(null, null, out _, out _));
        release.SetResult(true);
        Assert.True((await logout).Succeeded);
        await EventuallyAnonymousLease(service);
    }

    [Fact]
    public async Task V108_R05_WrongLockedAndExpiredSessionCannotAuthorizeKitCustody()
    {
        var provider = new Provider();
        long timestamp = 0;
        await using var service = new InteractiveSessionService(provider,
            AuthenticationPolicy.Development, (_, _) => ValueTask.FromResult(true), () => timestamp);
        var signedIn = await service.SignInAsync(new("person", "password"));
        Assert.False(service.TryAcquireRecoveryLease(Guid.NewGuid(), signedIn.Session.SessionId, out _, out _));
        Assert.False(service.TryAcquireRecoveryLease(provider.Identity.PrincipalId, Guid.NewGuid(), out _, out _));
        timestamp = (long)(System.Diagnostics.Stopwatch.Frequency * AuthenticationPolicy.Development.SessionIdleTimeout.TotalSeconds);
        Assert.False(service.TryAcquireRecoveryLease(provider.Identity.PrincipalId, signedIn.Session.SessionId, out _, out _));
        await service.LockAsync(null, SessionLockReason.UserRequested);
        Assert.False(service.TryAcquireRecoveryLease(null, null, out _, out _));
    }

    [Fact]
    public void V108_R06_RecoverySecretsHaveNoPrintableOrSerializableContractProperty()
    {
        const string secret = "sensitive recovery material never in diagnostics";
        var invocation = new CommandInvocation(CommandSource.PhysicalConsole, Guid.NewGuid().ToString("D"), Guid.NewGuid());
        var requests = new object[]
        {
            new RecoverAdministratorRequest(Guid.NewGuid(), "Station", secret, "person", "Person", secret),
            new RotateRecoveryKitRequest(Guid.NewGuid(), "Station", invocation, secret),
            new ConfirmRecoveryKitCustodyRequest(Guid.NewGuid(), "Station", Guid.NewGuid(), invocation, secret),
            new RecoveryKitRotationResult(true, "KitIssued", Guid.NewGuid(), AuditPersistence.Persisted,
                Guid.NewGuid(), new OneTimeSecret(secret))
        };
        foreach (var value in requests)
        {
            Assert.DoesNotContain(secret, value.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(value, value.GetType()), StringComparison.Ordinal);
        }
        ((RecoveryKitRotationResult)requests[3]).RecoveryKit!.Dispose();
    }

    [Fact]
    public async Task V108_R07_WrongThreadDisposePreservesTheOwnersAbilityToRelease()
    {
        var provider = new Provider();
        await using var service = Create(provider);
        await Task.Run(() =>
        {
            Assert.True(service.TryAcquireRecoveryLease(null, null, out var lease, out var reason), reason);
            try
            {
                var rejected = Task.Run(() => Assert.Throws<InvalidOperationException>(() => lease!.Dispose()));
                Assert.True(rejected.Wait(TimeSpan.FromSeconds(1)));
                Assert.Equal("RecoverySessionLeaseThreadMismatch", rejected.Result.Message);
            }
            finally { lease!.Dispose(); }
        }).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True((await service.SignInAsync(new("person", "password"))).Succeeded);
    }

    private static InteractiveSessionService Create(Provider provider) => new(provider,
        AuthenticationPolicy.Development, (_, _) => ValueTask.FromResult(true));

    private static async Task EventuallyAnonymousLease(InteractiveSessionService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (true)
        {
            if (service.TryAcquireRecoveryLease(null, null, out var lease, out _))
            { lease!.Dispose(); return; }
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class Provider : IIdentityProvider
    {
        internal HumanIdentity Identity { get; } = new(Guid.NewGuid(), "person", "Person");
        internal Func<ValueTask<AuthenticationResult>>? Reply { get; init; }
        internal int Calls;
        public ValueTask<AuthenticationResult> AuthenticateAsync(PasswordSignInRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Reply?.Invoke() ?? ValueTask.FromResult(new AuthenticationResult(true, "Authenticated", Identity));
        }
    }
}
