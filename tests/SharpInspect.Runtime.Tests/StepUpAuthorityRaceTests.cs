using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class StepUpAuthorityRaceTests
{
    [Fact]
    public async Task V106_R01_GrantExpiresByMonotonicTimeWithoutChangingTheTarget()
    {
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var invocation = await SignIn(fixture);
        long timestamp = 0;
        using var authorization = new LocalAuthorizationService(fixture.Store, fixture.IdentityOptions,
            fixture.Identity, fixture.Sessions, () => timestamp);
        await using var runtime = new StationRuntime(fixture.Store, TimeSpan.FromMilliseconds(20), fixture.Sessions, authorization);
        var command = Create(invocation);
        var proof = await Grant(authorization, fixture, command, Permission.ManageAccounts, AuditedCommandKind.CreateHumanAccount);
        timestamp = (long)(fixture.IdentityOptions.AuthenticationPolicy.StepUpFreshness.TotalSeconds * Stopwatch.Frequency);
        var rejected = await runtime.SubmitAsync(command with { Invocation = invocation with { StepUpGrantId = proof } });
        Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
        Assert.Equal("StepUpInvalid", rejected.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, rejected.Audit);
        await fixture.WaitVerifiedAsync();
        Assert.Single((await fixture.Store.ReadIdentityAsync(CancellationToken.None)).EnumerateAccounts());
    }

    [Fact]
    public async Task V106_R02_LockWhileProviderProofIsPendingCannotPublishAGrant()
    {
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var invocation = await SignIn(fixture);
        var provider = new HeldProofProvider(fixture.Identity);
        using var authorization = new LocalAuthorizationService(fixture.Store, fixture.IdentityOptions, provider, fixture.Sessions);
        var command = Create(invocation);
        var pending = authorization.ReauthenticateAsync(Request(command, fixture.BootstrapPassword,
            Permission.ManageAccounts, AuditedCommandKind.CreateHumanAccount)).AsTask();
        await provider.ProofReady.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await fixture.WaitVerifiedAsync();
        var locked = await fixture.Sessions.LockAsync(invocation.SessionId, SessionLockReason.OperatingSystemLock);
        Assert.True(locked.Succeeded, locked.ReasonCode);
        provider.Release.TrySetResult(true);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.False(result.Succeeded);
        Assert.Null(result.GrantId);
        Assert.Equal("SessionMismatch", result.ReasonCode);
        Assert.Equal(InteractiveSessionState.Locked, fixture.Sessions.Current.State);
        await fixture.WaitVerifiedAsync();
        Assert.Single((await fixture.Store.ReadIdentityAsync(CancellationToken.None)).EnumerateAccounts());
    }

    [Fact]
    public async Task V106_R03_PermissionChangeDuringProviderProofInvalidatesBothPendingAndOlderGrants()
    {
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var invocation = await SignIn(fixture);
        var oldCommand = new GovernedAuditChangeCommand(Guid.NewGuid(), invocation, GovernedAuditChangeKind.RotateSigningKey);
        var olderGrant = await Grant(fixture.StepUp, fixture, oldCommand, Permission.ManageAuditSigningKeys,
            AuditedCommandKind.RotateSigningKey);
        var provider = new HeldProofProvider(fixture.Identity);
        using var authorization = new LocalAuthorizationService(fixture.Store, fixture.IdentityOptions, provider, fixture.Sessions);
        var pendingCommand = new GovernedAuditChangeCommand(Guid.NewGuid(), invocation, GovernedAuditChangeKind.RotateSigningKey);
        var pending = authorization.ReauthenticateAsync(Request(pendingCommand, fixture.BootstrapPassword,
            Permission.ManageAuditSigningKeys, AuditedCommandKind.RotateSigningKey, fixture.StationId)).AsTask();
        await provider.ProofReady.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await fixture.WaitVerifiedAsync();

        var permissions = fixture.IdentityOptions.AuthorizationPolicy.GetPermissions(HumanRoleBundle.Administrator)
            .Where(permission => permission != Permission.ManageAuditSigningKeys).ToArray();
        var revoke = new SetHumanPermissionsCommand(Guid.NewGuid(), invocation, fixture.BootstrapAdminPrincipalId, permissions);
        var grant = await Grant(fixture.StepUp, fixture, revoke, Permission.ManagePermissions, AuditedCommandKind.SetHumanPermissions);
        var changed = await fixture.Runtime.SubmitAsync(revoke with { Invocation = invocation with { StepUpGrantId = grant } });
        Assert.Equal(CommandDisposition.Accepted, changed.Disposition);
        await fixture.WaitVerifiedAsync();
        provider.Release.TrySetResult(true);
        var late = await pending.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.False(late.Succeeded);
        Assert.Equal("StepUpAuthorityChanged", late.ReasonCode);
        Assert.Null(late.GrantId);
        await fixture.WaitVerifiedAsync();
        var stale = await fixture.Runtime.SubmitAsync(oldCommand with { Invocation = invocation with { StepUpGrantId = olderGrant } });
        Assert.Equal(CommandDisposition.Rejected, stale.Disposition);
        Assert.Equal("PermissionDenied", stale.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, stale.Audit);
        await fixture.WaitVerifiedAsync();
        Assert.DoesNotContain(Permission.ManageAuditSigningKeys,
            (await fixture.Authorization.GetCurrentAuthorizationAsync(invocation.SessionId)).Account!.Permissions);
    }

    [Fact]
    public async Task V106_R04_CancelledProviderProofCannotCreateLateAuthority()
    {
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var invocation = await SignIn(fixture);
        var provider = new HeldProofProvider(fixture.Identity);
        using var authorization = new LocalAuthorizationService(fixture.Store, fixture.IdentityOptions, provider, fixture.Sessions);
        using var cancellation = new CancellationTokenSource();
        var command = Create(invocation);
        var pending = authorization.ReauthenticateAsync(Request(command, fixture.BootstrapPassword,
            Permission.ManageAccounts, AuditedCommandKind.CreateHumanAccount), cancellation.Token).AsTask();
        await provider.ProofReady.Task.WaitAsync(TimeSpan.FromSeconds(8));
        cancellation.Cancel();
        var cancelled = await pending.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.False(cancelled.Succeeded);
        Assert.Equal("StepUpCancelled", cancelled.ReasonCode);
        Assert.Null(cancelled.GrantId);
        provider.Release.TrySetResult(true);
        await provider.ActualCompletion.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await fixture.WaitVerifiedAsync();
        var rejected = await fixture.Runtime.SubmitAsync(command);
        Assert.Equal("StepUpRequired", rejected.ReasonCode);
        Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
    }

    [Fact]
    public async Task V106_I01_SystemClaimsCannotAuthorizeHumansAndLocalStopKeepsItsSeparateAttribution()
    {
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var system = new CommandInvocation(CommandSource.Integration, SystemPrincipalId.Runtime, Guid.NewGuid(), Guid.NewGuid());
        var denied = await fixture.Runtime.SubmitAsync(Create(system));
        Assert.Equal(CommandDisposition.Rejected, denied.Disposition);
        Assert.Equal("AuthenticationRequired", denied.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, denied.Audit);
        await fixture.WaitVerifiedAsync();
        var integrationStop = await fixture.Runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(), system));
        Assert.Equal("LocalConsoleRequired", integrationStop.ReasonCode);
        await fixture.WaitVerifiedAsync();
        var local = new GracefulProductionStopCommand(Guid.NewGuid(), new(CommandSource.PhysicalConsole));
        var accepted = await fixture.Runtime.SubmitAsync(local);
        Assert.Equal(CommandDisposition.Accepted, accepted.Disposition);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while ((await fixture.Runtime.GetSnapshotAsync(timeout.Token)).LastCommand?.State != OperationState.Completed)
            await Task.Delay(20, timeout.Token);
        await fixture.WaitVerifiedAsync();
        var facts = (await fixture.TraceQuery.QueryAsync(new(CorrelationId: local.CorrelationId))).Records;
        Assert.Equal(2, facts.Count);
        Assert.All(facts, fact =>
        {
            Assert.Null(fact.AuthenticatedHumanPrincipalId);
            Assert.Equal(SystemPrincipalId.Runtime, fact.SystemPrincipalId);
        });
        Assert.False((await fixture.Runtime.GetSnapshotAsync()).Ready);
    }

    [Fact]
    public async Task V106_R05_WrongActorCannotUseARealGrantAndConcurrentReplayAcceptsOnlyOnce()
    {
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var invocation = await SignIn(fixture);
        var command = Create(invocation);
        var proof = await Grant(fixture.StepUp, fixture, command, Permission.ManageAccounts, AuditedCommandKind.CreateHumanAccount);
        var invalid = await fixture.Runtime.SubmitAsync(command with
            { Invocation = invocation with { PrincipalId = Guid.NewGuid().ToString("D"), StepUpGrantId = proof } });
        Assert.Equal("SessionMismatch", invalid.ReasonCode);
        Assert.Equal(CommandDisposition.Rejected, invalid.Disposition);
        await fixture.WaitVerifiedAsync();
        var authorized = command with { Invocation = invocation with { StepUpGrantId = proof } };
        var outcomes = await Task.WhenAll(fixture.Runtime.SubmitAsync(authorized).AsTask(),
            fixture.Runtime.SubmitAsync(authorized).AsTask());
        Assert.Single(outcomes, result => result.Disposition == CommandDisposition.Accepted);
        Assert.Single(outcomes, result => result.Disposition == CommandDisposition.Rejected && result.ReasonCode == "DuplicateCorrelationId");
        Assert.All(outcomes, outcome => Assert.Equal(AuditPersistence.Persisted, outcome.Audit));
        await fixture.WaitVerifiedAsync();
        Assert.Equal(2, (await fixture.Store.ReadIdentityAsync(CancellationToken.None)).EnumerateAccounts().Count());
    }

    [Theory]
    [InlineData(GovernedAuditChangeKind.RotateSigningKey, Permission.ManageAuditSigningKeys, AuditedCommandKind.RotateSigningKey)]
    [InlineData(GovernedAuditChangeKind.RetireSigningKey, Permission.ManageAuditSigningKeys, AuditedCommandKind.RetireSigningKey)]
    [InlineData(GovernedAuditChangeKind.CorrectHistoricalFact, Permission.CorrectHistoricalFact, AuditedCommandKind.CorrectHistoricalFact)]
    [InlineData(GovernedAuditChangeKind.DeleteEvidence, Permission.DeleteEvidence, AuditedCommandKind.DeleteEvidence)]
    public async Task V106_R06_GovernedAuditActionsUseDedicatedPermissionAndRemainClosedUntilCapabilityExists(
        GovernedAuditChangeKind change, Permission permission, AuditedCommandKind kind)
    {
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var invocation = await SignIn(fixture);
        var command = new GovernedAuditChangeCommand(Guid.NewGuid(), invocation, change);
        var noProof = await fixture.Runtime.SubmitAsync(command);
        Assert.Equal("StepUpRequired", noProof.ReasonCode);
        await fixture.WaitVerifiedAsync();
        var proof = await Grant(fixture.StepUp, fixture, command, permission, kind);
        var rejected = await fixture.Runtime.SubmitAsync(command with { Invocation = invocation with { StepUpGrantId = proof } });
        Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
        Assert.Equal("GovernedCapabilityUnavailable", rejected.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, rejected.Audit);
        await fixture.WaitVerifiedAsync();
        var facts = (await fixture.TraceQuery.QueryAsync(new(CorrelationId: command.CorrelationId))).Records;
        Assert.Equal(2, facts.Count);
        Assert.All(facts, fact =>
        {
            Assert.Equal(kind, fact.CommandKind);
            Assert.Equal(fixture.BootstrapAdminPrincipalId.ToString("D"), fact.AuthenticatedHumanPrincipalId);
            Assert.Equal(CommandDisposition.Rejected, fact.Disposition);
        });
        Assert.False((await fixture.Runtime.GetSnapshotAsync()).Ready);
    }

    [Fact]
    public async Task V106_R07_CredentialPreparationCannotBlockLocalStopAndHandlingRechecksAfterward()
    {
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var invocation = await SignIn(fixture);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var authorization = new LocalAuthorizationService(fixture.Store, fixture.IdentityOptions, fixture.Identity,
            fixture.Sessions, beforeCredentialDerivation: () =>
            {
                started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("TestPreparationNotReleased");
            });
        await using var runtime = new StationRuntime(fixture.Store, TimeSpan.FromMilliseconds(20), fixture.Sessions, authorization);
        var command = Create(invocation);
        var proof = await Grant(authorization, fixture, command, Permission.ManageAccounts, AuditedCommandKind.CreateHumanAccount);
        var pending = runtime.SubmitAsync(command with { Invocation = invocation with { StepUpGrantId = proof } }).AsTask();
        try
        {
            Assert.True(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(2))));
            var stop = new GracefulProductionStopCommand(Guid.NewGuid(), new(CommandSource.PhysicalConsole));
            var result = await runtime.SubmitAsync(stop).AsTask().WaitAsync(fixture.Options.CommitTimeout);
            Assert.Equal(CommandDisposition.Accepted, result.Disposition);
            Assert.Equal(AuditPersistence.Persisted, result.Audit);
            Assert.False(pending.IsCompleted);
            // Locking after the preparation check must still revoke the pending management command.
            await fixture.WaitVerifiedAsync();
            var locked = await fixture.Sessions.LockAsync(invocation.SessionId, SessionLockReason.UserRequested);
            Assert.True(locked.Succeeded, locked.ReasonCode);
        }
        finally { release.Set(); }
        var management = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CommandDisposition.Rejected, management.Disposition);
        Assert.Equal("SessionMismatch", management.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, management.Audit);
        await fixture.WaitVerifiedAsync();
        Assert.Single((await fixture.Store.ReadIdentityAsync(CancellationToken.None)).EnumerateAccounts());
    }

    private static async Task<CommandInvocation> SignIn(IdentityManagementAcceptanceFixture fixture)
    {
        var result = await fixture.Sessions.SignInAsync(new(fixture.BootstrapUserName, fixture.BootstrapPassword));
        Assert.True(result.Succeeded, result.ReasonCode);
        await fixture.WaitVerifiedAsync();
        return new(CommandSource.PhysicalConsole, result.Identity!.PrincipalId.ToString("D"), result.Session.SessionId);
    }

    [Fact]
    public async Task V106_R08_InternalPreparationTimeoutReturnsTypedOutcomeAndRetainsUnconsumedGrant()
    {
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var invocation = await SignIn(fixture);
        var options = new LocalIdentityOptions(fixture.StationId, fixture.IdentityOptions.PasswordPolicy,
            fixture.IdentityOptions.PasswordHasher, fixture.IdentityOptions.AuthenticationPolicy,
            fixture.IdentityOptions.AuthorizationPolicy) { OperationTimeout = TimeSpan.FromSeconds(1) };
        using var started = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var observations = 0;
        using var authorization = new LocalAuthorizationService(fixture.Store, options, fixture.Identity, fixture.Sessions,
            beforeCredentialDerivation: () =>
            {
                if (Interlocked.Increment(ref observations) <= 2) started.Signal();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("TestPreparationNotReleased");
            });
        await using var runtime = new StationRuntime(fixture.Store, TimeSpan.FromMilliseconds(20), fixture.Sessions, authorization);
        var commands = Enumerable.Range(0, 3).Select(_ => Create(invocation)).ToArray();
        for (var index = 0; index < commands.Length; index++)
        {
            var proof = await Grant(authorization, fixture, commands[index], Permission.ManageAccounts, AuditedCommandKind.CreateHumanAccount);
            commands[index] = commands[index] with { Invocation = invocation with { StepUpGrantId = proof } };
        }
        var first = runtime.SubmitAsync(commands[0]).AsTask();
        var second = runtime.SubmitAsync(commands[1]).AsTask();
        try
        {
            Assert.True(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(2))));
            var result = await runtime.SubmitAsync(commands[2]);
            Assert.Equal(CommandDisposition.Rejected, result.Disposition);
            Assert.Equal(AuditPersistence.Unavailable, result.Audit);
            Assert.Equal("ManagementPreparationDeadlineExceeded", result.ReasonCode);
        }
        finally { release.Set(); }
        Assert.All(await Task.WhenAll(first, second), result => Assert.Equal(CommandDisposition.Rejected, result.Disposition));
        Assert.Single((await fixture.Store.ReadIdentityAsync(CancellationToken.None)).EnumerateAccounts());
        var retry = await runtime.SubmitAsync(commands[0]);
        Assert.Equal(CommandDisposition.Accepted, retry.Disposition);
        Assert.Equal(AuditPersistence.Persisted, retry.Audit);
        await fixture.WaitVerifiedAsync();
    }

    [Fact]
    public async Task V106_R09_PreparationAndBlockedWriterShareTheOriginalCommandDeadline()
    {
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var invocation = await SignIn(fixture);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var authorization = new LocalAuthorizationService(fixture.Store, fixture.IdentityOptions, fixture.Identity,
            fixture.Sessions, beforeCredentialDerivation: () =>
            {
                started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("TestPreparationNotReleased");
            });
        await using var runtime = new StationRuntime(fixture.Store, TimeSpan.FromMilliseconds(20), fixture.Sessions, authorization);
        var command = Create(invocation);
        var grant = await Grant(authorization, fixture, command, Permission.ManageAccounts, AuditedCommandKind.CreateHumanAccount);
        var stopwatch = Stopwatch.StartNew();
        var pending = runtime.SubmitAsync(command with { Invocation = invocation with { StepUpGrantId = grant } }).AsTask();
        using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        blocker.Open();
        using var sql = blocker.CreateCommand();
        try
        {
            Assert.True(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(2))));
            sql.CommandText = "BEGIN IMMEDIATE;";
            sql.ExecuteNonQuery();
            await Task.Delay(1150);
            release.Set();
            var rejected = await pending.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
            Assert.Equal(AuditPersistence.Unavailable, rejected.Audit);
            Assert.True(stopwatch.Elapsed < fixture.Options.CommitTimeout + TimeSpan.FromMilliseconds(600),
                $"The single command deadline was renewed: {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
        }
        finally
        {
            release.Set();
            sql.CommandText = "ROLLBACK;";
            sql.ExecuteNonQuery();
        }
        Assert.Single((await fixture.Store.ReadIdentityAsync(CancellationToken.None)).EnumerateAccounts());
        Assert.Empty((await fixture.TraceQuery.QueryAsync(new(CorrelationId: command.CorrelationId))).Records);
    }

    [Fact]
    public async Task V106_R10_CommittedSelfDisableDoesNotHoldCommandGateForLogoutPersistence()
    {
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var logoutStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var localIdentity = (LocalIdentityService)fixture.Identity;
        async ValueTask<bool> Persist(SessionAuditEvent fact, CancellationToken token)
        {
            if (fact.Kind == "SessionLoggedOut")
            {
                logoutStarted.TrySetResult(true);
                await release.Task;
            }
            return await localIdentity.PersistSessionEventAsync(fact, token);
        }
        await using var sessions = new InteractiveSessionService(fixture.Identity, fixture.IdentityOptions.AuthenticationPolicy, Persist);
        using var authorization = new LocalAuthorizationService(fixture.Store, fixture.IdentityOptions, fixture.Identity, sessions);
        await using var runtime = new StationRuntime(fixture.Store, TimeSpan.FromMilliseconds(20), sessions, authorization);
        var login = await sessions.SignInAsync(new(fixture.BootstrapUserName, fixture.BootstrapPassword));
        Assert.True(login.Succeeded, login.ReasonCode);
        await fixture.WaitVerifiedAsync();
        var invocation = new CommandInvocation(CommandSource.PhysicalConsole, login.Identity!.PrincipalId.ToString("D"), login.Session.SessionId);
        var create = new CreateHumanAccountCommand(Guid.NewGuid(), invocation, Guid.NewGuid(), "jane.qi", "Jane Qi",
            "Pacific blossom 26! safe admin", HumanRoleBundle.Administrator);
        var createProof = await Grant(authorization, fixture, create, Permission.ManageAccounts, AuditedCommandKind.CreateHumanAccount);
        Assert.Equal(CommandDisposition.Accepted,
            (await runtime.SubmitAsync(create with { Invocation = invocation with { StepUpGrantId = createProof } })).Disposition);
        await fixture.WaitVerifiedAsync();
        var disable = new DisableHumanCredentialCommand(Guid.NewGuid(), invocation, login.Identity.PrincipalId,
            IdentityManagementReason.PersonnelDeparture);
        var proof = await Grant(authorization, fixture, disable, Permission.ManageAccounts, AuditedCommandKind.DisableHumanCredential);
        try
        {
            var pending = runtime.SubmitAsync(disable with { Invocation = invocation with { StepUpGrantId = proof } }).AsTask();
            await logoutStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var disabled = await pending.WaitAsync(TimeSpan.FromMilliseconds(700));
            Assert.Equal(CommandDisposition.Accepted, disabled.Disposition);
            Assert.Equal(InteractiveSessionState.Unauthenticated, sessions.Current.State);
            Assert.False(release.Task.IsCompleted);
            await fixture.WaitVerifiedAsync();
            var stop = await runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(), new(CommandSource.PhysicalConsole)))
                .AsTask().WaitAsync(fixture.Options.CommitTimeout);
            Assert.Equal(CommandDisposition.Accepted, stop.Disposition);
            Assert.Equal(AuditPersistence.Persisted, stop.Audit);
        }
        finally { release.TrySetResult(true); }
    }

    private static CreateHumanAccountCommand Create(CommandInvocation invocation) => new(Guid.NewGuid(), invocation,
        Guid.NewGuid(), "rui.li.race", "Rui Li", "Ocean birch 26! cloud garden", HumanRoleBundle.Operator);

    private static StepUpRequest Request(RuntimeCommand command, string password, Permission permission,
        AuditedCommandKind kind, string? station = null) => new(Guid.NewGuid(), command.Invocation,
        new(permission, command.CorrelationId, command is IdentityManagementCommand management
            ? management.TargetPrincipalId.ToString("D") : station!, kind), password);

    private static async Task<Guid> Grant(IStepUpAuthentication stepUp, IdentityManagementAcceptanceFixture fixture,
        RuntimeCommand command, Permission permission, AuditedCommandKind kind)
    {
        var issued = await stepUp.ReauthenticateAsync(Request(command, fixture.BootstrapPassword, permission, kind, fixture.StationId));
        Assert.True(issued.Succeeded, issued.ReasonCode);
        await fixture.WaitVerifiedAsync();
        return issued.GrantId!.Value;
    }

    private sealed class HeldProofProvider : IIdentityProvider
    {
        private readonly IIdentityProvider _inner;
        internal HeldProofProvider(IIdentityProvider inner) => _inner = inner;
        internal TaskCompletionSource<bool> ProofReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ActualCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<AuthenticationResult> AuthenticateAsync(PasswordSignInRequest request, CancellationToken cancellationToken = default)
        {
            var result = await _inner.AuthenticateAsync(request, cancellationToken);
            Assert.True(result.Succeeded, result.ReasonCode);
            ProofReady.TrySetResult(true);
            await Release.Task; // Deliberately ignores cancellation after obtaining a real provider proof.
            ActualCompletion.TrySetResult(true);
            return result;
        }
    }
}
