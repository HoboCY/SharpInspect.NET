#pragma warning disable CA1416

using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Acceptance tests for the narrow camera setup authorization seam. These tests
/// deliberately use the real local identity, session, Step-Up and SQLite writer
/// rather than inspecting the authorization service's grant table.
/// </summary>
public sealed class CameraSetupAuthorizationTests
{
    private const string LogicalRole = "TopCamera";

    [Fact]
    public async Task V117_G01_ReadOnlyAuthorizationReleasesLeaseAndWriterGuardBoundsOtherSessionWork()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.SignInAsync();

        var readOnly = await fixture.Authorization.AuthorizeCameraSetupAsync(
            first.Invocation, readOnly: true, Guid.Empty, LogicalRole,
            AuditedCommandKind.RebindCamera);
        Assert.True(readOnly.Authorized, readOnly.ReasonCode);
        Assert.Null(readOnly.Reservation);

        // A lease acquired after an asynchronous identity read must be gone when
        // authorization returns. Otherwise this call would block on the session
        // monitor indefinitely.
        var unlocked = await fixture.Sessions.LockAsync(first.Invocation.SessionId,
            SessionLockReason.UserRequested).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(unlocked.Succeeded, unlocked.ReasonCode);
        await fixture.WaitForVerifiedAsync();

        var second = await fixture.SignInAsync();
        var operationId = Guid.NewGuid();
        var grantId = await fixture.IssueCameraGrantAsync(second.Invocation, operationId);
        var authorization = await fixture.AuthorizeMutationAsync(second.Invocation, operationId, grantId);
        var reservation = authorization.Reservation!;

        using var writerEntered = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        var writer = fixture.AppendAdmissionHoldingGuardAsync(operationId, authorization, grantId,
            writerEntered, releaseWriter);
        try
        {
            Assert.True(await Task.Run(() => writerEntered.Wait(TimeSpan.FromSeconds(3))));

            // The writer callback owns the session lease synchronously. Both
            // operations must remain bounded while it is held, then complete
            // after the callback returns and the guard is disposed.
            var lockStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queryStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var lockTask = Task.Run(() =>
            {
                lockStarted.TrySetResult(true);
                return fixture.Sessions.LockAsync(second.Invocation.SessionId,
                    SessionLockReason.OperatingSystemLock).AsTask();
            });
            var queryTask = Task.Run(() =>
            {
                queryStarted.TrySetResult(true);
                return fixture.Authorization.GetCurrentAuthorizationAsync(
                    second.Invocation.SessionId).AsTask();
            });
            await Task.WhenAll(lockStarted.Task, queryStarted.Task).WaitAsync(TimeSpan.FromSeconds(2));
            var early = await Task.WhenAny(lockTask, queryTask, Task.Delay(150));
            Assert.NotSame(lockTask, early);
            Assert.NotSame(queryTask, early);

            releaseWriter.Set();
            var written = await writer.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(written.Committed, written.ReasonCode);
            reservation.Commit();

            var locked = await lockTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(locked.Succeeded, locked.ReasonCode);
            _ = await queryTask.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            releaseWriter.Set();
            await writer.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await fixture.WaitForVerifiedAsync();
    }

    [Fact]
    public async Task V117_G02_CommittedAdmissionSurvivesRealStepUpGrantPurgeForTerminalGuard()
    {
        await using var fixture = await Fixture.CreateAsync();
        var signedIn = await fixture.SignInAsync();
        var operationId = Guid.NewGuid();
        var grantId = await fixture.IssueCameraGrantAsync(signedIn.Invocation, operationId);
        var authorization = await fixture.AuthorizeMutationAsync(signedIn.Invocation, operationId, grantId);

        var admitted = await fixture.AppendAdmissionAsync(operationId, authorization, grantId);
        Assert.True(admitted.Committed, admitted.ReasonCode);
        authorization.Reservation!.Commit();
        await fixture.WaitForVerifiedAsync();

        // Reauthenticate through the real provider. ReauthenticateAsync calls
        // PurgeGrantsLocked, so the consumed admission grant is no longer an
        // in-memory active entry when the terminal proof is checked.
        var secondOperation = Guid.NewGuid();
        var secondGrant = await fixture.IssueCameraGrantAsync(signedIn.Invocation, secondOperation);
        Assert.NotEqual(Guid.Empty, secondGrant);

        var state = await fixture.Store.ReadIdentityAsync(CancellationToken.None);
        var terminal = LocalAuthorizationService.AcquireCameraSetupCommitGuard(
            authorization.Reservation!, state, requireActiveGrant: false, out var reason);
        Assert.NotNull(terminal);
        Assert.Equal("Authorized", reason);
        terminal!.Commit();
        terminal.Dispose();
        await fixture.WaitForVerifiedAsync();
    }

    [Fact]
    public async Task V117_G03_UncommittedAdmissionReservationCannotAuthorizeTerminal()
    {
        await using var fixture = await Fixture.CreateAsync();
        var signedIn = await fixture.SignInAsync();
        var operationId = Guid.NewGuid();
        var grantId = await fixture.IssueCameraGrantAsync(signedIn.Invocation, operationId);
        var authorization = await fixture.AuthorizeMutationAsync(signedIn.Invocation, operationId, grantId);

        var state = await fixture.Store.ReadIdentityAsync(CancellationToken.None);
        var terminal = LocalAuthorizationService.AcquireCameraSetupCommitGuard(
            authorization.Reservation!, state, requireActiveGrant: false, out var reason);
        Assert.Null(terminal);
        Assert.Equal("StepUpInvalid", reason);

        // Releasing an uncommitted reservation returns the grant to its active
        // state, but it never creates the durable admission proof required by a
        // terminal continuation.
        authorization.Reservation!.Dispose();
        var retry = LocalAuthorizationService.AcquireCameraSetupCommitGuard(
            authorization.Reservation!, state, requireActiveGrant: false, out reason);
        Assert.Null(retry);
        Assert.Equal("StepUpInvalid", reason);
    }

    [Fact]
    public async Task V117_G04_CommittedAdmissionDisposeCannotReactivateOneTimeGrant()
    {
        await using var fixture = await Fixture.CreateAsync();
        var signedIn = await fixture.SignInAsync();
        var operationId = Guid.NewGuid();
        var grantId = await fixture.IssueCameraGrantAsync(signedIn.Invocation, operationId);
        var authorization = await fixture.AuthorizeMutationAsync(signedIn.Invocation, operationId, grantId);

        var admitted = await fixture.AppendAdmissionAsync(operationId, authorization, grantId);
        Assert.True(admitted.Committed, admitted.ReasonCode);
        // The writer's commit guard has already consumed the grant and marked
        // the admission proof. Exercise the caller cleanup path before its
        // reservation.Commit call: rollback must not reactivate that grant.
        authorization.Reservation!.Dispose();
        await fixture.WaitForVerifiedAsync();

        // The exact Step-Up grant was consumed at durable admission. A failed
        // cleanup/dispose attempt cannot make the same one-time proof reusable.
        var reused = await fixture.Authorization.AuthorizeCameraSetupAsync(
            signedIn.Invocation with { StepUpGrantId = grantId }, readOnly: false,
            operationId, LogicalRole, AuditedCommandKind.RebindCamera);
        Assert.False(reused.Authorized);
        Assert.Equal("StepUpInvalid", reused.ReasonCode);
        Assert.Null(reused.Reservation);
    }

    private static IdentityUpdate CreateAdmissionUpdate(IdentityAuthorityState state,
        CameraSetupAuthorization authorization, Guid grantId, Guid operationId,
        IIdentityTransactionGuard? guard)
    {
        var now = DateTimeOffset.UtcNow;
        var fact = new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), operationId, Guid.NewGuid(), now,
            AuditedCommandKind.RebindCamera, CommandSource.PhysicalConsole,
            authorization.PrincipalId.ToString("D"), authorization.SessionId, grantId,
            CommandAuditPhase.Outcome, CommandDisposition.Accepted, "CameraRebindAdmitted",
            authorization.PrincipalId.ToString("D"));
        var identity = new IdentityAuditEvent(Guid.NewGuid(), IdentityEventKind.CameraSetupActionAuthorized,
            now, state.StationId, authorization.PrincipalId, null, null, null,
            fact.ReasonCode, ActorPrincipalId: authorization.PrincipalId,
            CommandCorrelationId: operationId, StepUpGrantId: grantId,
            RequiredPermission: Permission.ManageCameraBindings.ToString(),
            AuthorizationRevision: authorization.AuthorizationRevision,
            ActionTargetId: LogicalRole, BoundCommandCorrelationId: operationId,
            ActionCommandKind: AuditedCommandKind.RebindCamera.ToString(),
            OperationId: operationId, SessionId: authorization.SessionId);
        return new IdentityUpdate("CameraAuthorizationAdmissionPersisted", new[] { identity },
            new[] { fact }, CommitGuard: guard);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly AuditIntegrityPolicy _auditPolicy;
        private readonly ServiceProvider _provider;

        private Fixture(AuditIntegrityPolicy auditPolicy, ProductionStoreOptions options,
            ServiceProvider provider, SqliteCommandStore store,
            InteractiveSessionService sessions, LocalAuthorizationService authorization)
        {
            _auditPolicy = auditPolicy;
            _provider = provider;
            Options = options;
            Store = store;
            Sessions = sessions;
            Authorization = authorization;
        }

        internal ProductionStoreOptions Options { get; }
        internal SqliteCommandStore Store { get; }
        internal InteractiveSessionService Sessions { get; }
        internal LocalAuthorizationService Authorization { get; }
        internal string StationId => _auditPolicy.StationId;
        internal string UserName => "camera-authorization-admin";
        internal string Password => "V117 camera authorization 26! test secret";

        internal static async Task<Fixture> CreateAsync()
        {
            if (!OperatingSystem.IsWindows())
                throw SkipException.ForSkip("Camera authorization requires Windows DPAPI machine protection.");

            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
                "V117-CameraAuthorization-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var station = "V117CameraAuthorizationStation";
            var audit = new AuditIntegrityPolicy(station, "v1",
                "SharpInspect.Test.V117.CameraAuthorization." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "audit-keys"),
                CheckpointEveryEntries = 2,
                VerificationInterval = TimeSpan.FromSeconds(1)
            };
            var identityOptions = new LocalIdentityOptions(station,
                new LocalPasswordPolicy
                {
                    Blocklist = PasswordBlocklist.Create("v117-camera-authorization-blocklist", "v1",
                        new[] { "known-compromised-camera-password" })
                }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
                AuthorizationPolicy.Development);
            var options = new ProductionStoreOptions(Path.Combine(directory, "camera-authorization.sqlite"))
            {
                AuditIntegrityPolicy = audit,
                LocalIdentity = identityOptions,
                CameraSetup = new CameraSetupStoreOptions(),
                CommitTimeout = TimeSpan.FromSeconds(5),
                QueryTimeout = TimeSpan.FromSeconds(5),
                QueueCapacity = 8
            };

            var services = new ServiceCollection();
            services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(20));
            var provider = services.BuildServiceProvider();
            try
            {
                var store = provider.GetRequiredService<SqliteCommandStore>();
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                var fixtureIdentity = new LocalIdentityService(store, identityOptions,
                    new FixtureConsoleAuthority());
                var fixture = new Fixture(audit, options, provider, store,
                    (InteractiveSessionService)provider.GetRequiredService<IInteractiveSessionService>(),
                    provider.GetRequiredService<LocalAuthorizationService>());
                await fixture.WaitForVerifiedAsync();

                var tokenResult = await fixtureIdentity.ProvisionBootstrapTokenAsync();
                Assert.True(tokenResult.Succeeded, tokenResult.ReasonCode);
                var token = tokenResult.Token!.TakeForDisplay();
                await fixture.WaitForVerifiedAsync();
                var created = await fixtureIdentity.CreateFirstAdministratorAsync(
                    new BootstrapAdministratorRequest(station, token, fixture.UserName,
                        "V117 Camera Authorization Administrator", fixture.Password));
                Assert.True(created.Succeeded, created.ReasonCode);
                created.RecoveryKit?.Dispose();
                await fixture.WaitForVerifiedAsync();
                return fixture;
            }
            catch
            {
                await provider.DisposeAsync();
                TryDeleteKey(audit);
                throw;
            }
        }

        internal async Task<(CommandInvocation Invocation, Guid PrincipalId)> SignInAsync()
        {
            var result = await Sessions.SignInAsync(new PasswordSignInRequest(UserName, Password));
            Assert.True(result.Succeeded, result.ReasonCode);
            Assert.NotNull(result.Identity);
            await WaitForVerifiedAsync();
            var principal = result.Identity!.PrincipalId;
            return (new CommandInvocation(CommandSource.PhysicalConsole,
                principal.ToString("D"), result.Session.SessionId), principal);
        }

        internal async Task<Guid> IssueCameraGrantAsync(CommandInvocation invocation, Guid operationId)
        {
            var stepUp = await Authorization.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(), invocation,
                new StepUpBinding(Permission.ManageCameraBindings, operationId, LogicalRole,
                    AuditedCommandKind.RebindCamera), Password));
            Assert.True(stepUp.Succeeded, stepUp.ReasonCode);
            Assert.True(stepUp.GrantId.HasValue);
            await WaitForVerifiedAsync();
            return stepUp.GrantId!.Value;
        }

        internal async Task<CameraSetupAuthorization> AuthorizeMutationAsync(
            CommandInvocation invocation, Guid operationId, Guid grantId)
        {
            var result = await Authorization.AuthorizeCameraSetupAsync(
                invocation with { StepUpGrantId = grantId }, readOnly: false,
                operationId, LogicalRole, AuditedCommandKind.RebindCamera);
            Assert.True(result.Authorized, result.ReasonCode);
            Assert.NotNull(result.Reservation);
            return result;
        }

        internal async Task<IdentityWriteResult> AppendAdmissionAsync(Guid operationId,
            CameraSetupAuthorization authorization, Guid grantId)
        {
            var result = await Store.UpdateCameraSetupAsync(operationId, LogicalRole,
                (state, _, duplicate) =>
                {
                    if (duplicate) throw new InvalidOperationException("CameraSetupOperationConflict");
                    var guard = LocalAuthorizationService.AcquireCameraSetupCommitGuard(
                        authorization.Reservation!, state, requireActiveGrant: true, out var reason);
                    if (guard is null) throw new InvalidOperationException(reason);
                    return CreateAdmissionUpdate(state, authorization, grantId, operationId, guard);
                }, CancellationToken.None, new StoreDeadline(Options.CommitTimeout));
            return result;
        }

        internal async Task<IdentityWriteResult> AppendAdmissionHoldingGuardAsync(Guid operationId,
            CameraSetupAuthorization authorization, Guid grantId, ManualResetEventSlim entered,
            ManualResetEventSlim release)
        {
            return await Store.UpdateCameraSetupAsync(operationId, LogicalRole,
                (state, _, duplicate) =>
                {
                    if (duplicate) throw new InvalidOperationException("CameraSetupOperationConflict");
                    var guard = LocalAuthorizationService.AcquireCameraSetupCommitGuard(
                        authorization.Reservation!, state, requireActiveGrant: true, out var reason);
                    if (guard is null) throw new InvalidOperationException(reason);
                    entered.Set();
                    release.Wait();
                    return CreateAdmissionUpdate(state, authorization, grantId, operationId, guard);
                }, CancellationToken.None, new StoreDeadline(Options.CommitTimeout));
        }

        internal async Task WaitForVerifiedAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var report = Store.Integrity;
                if (report is { State: AuditIntegrityState.Verified }) return;
                if (report is { State: AuditIntegrityState.Faulted } fault)
                    throw new XunitException(fault.ReasonCode);
                await Task.Delay(25);
            }
            throw new XunitException("Camera authorization audit did not become Verified: " +
                Store.Integrity?.ReasonCode);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            TryDeleteKey(_auditPolicy);
        }

        private static void TryDeleteKey(AuditIntegrityPolicy policy)
        {
            try
            {
                var path = WindowsMachineAuditKey.GetKeyPath(policy);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FixtureConsoleAuthority : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-V117-CameraAuthorization");
    }
}

#pragma warning restore CA1416
