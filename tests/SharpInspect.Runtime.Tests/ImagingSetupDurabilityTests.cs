#pragma warning disable CA1416

using Microsoft.Data.Sqlite;
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
/// Durable schema-13 imaging declaration tests.  The fixture uses the real
/// SQLite writer, local identity/session/authorization services, and imaging
/// persistence adapter; no fake persistence or physical camera is involved.
/// </summary>
public sealed class ImagingSetupDurabilityTests
{
    private const string LogicalRole = "TopCamera";

    [Fact]
    public async Task V123_D01_MaximumRevisionCountOneRejectsSecondAndPreservesFirstAfterRestart()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(maximumRevisionCount: 1);
        var binding = await fixture.AppendCompletedBindingAsync();
        var signedIn = await fixture.SignInAsync();

        var firstWrite = await fixture.AppendAuthorizedAsync(signedIn, binding, "Lens-A",
            "Declare first physical imaging setup");
        Assert.True(firstWrite.Committed, firstWrite.ReasonCode);

        var firstState = await fixture.Persistence.ReadAsync(LogicalRole);
        var first = Assert.Single(firstState.Revisions);
        Assert.Equal(1L, first.Revision);

        var secondWrite = await fixture.AppendAuthorizedAsync(signedIn, binding, "Lens-B",
            "Declare second physical imaging setup", expectedRevision: first.Revision,
            expectedRevisionHash: first.RevisionHash);
        Assert.False(secondWrite.Committed);
        Assert.Equal("ImagingSetupRevisionCapacityExceeded", secondWrite.ReasonCode);

        await fixture.WaitForVerifiedAsync();
        var afterRejected = await fixture.Persistence.ReadAsync(LogicalRole);
        var retained = Assert.Single(afterRejected.Revisions);
        Assert.Equal(first.OperationId, retained.OperationId);
        Assert.Equal(first.RevisionHash, retained.RevisionHash);
        Assert.Equal(first.Definition.ContentHash, retained.Definition.ContentHash);

        await fixture.RestartStoreAsync();
        var history = await fixture.Persistence.QueryHistoryAsync(LogicalRole, 0, null, 20);
        Assert.True(history.Available, history.ReasonCode);
        var restarted = Assert.Single(history.Revisions);
        Assert.Equal(first.OperationId, restarted.OperationId);
        Assert.Equal(first.RevisionHash, restarted.RevisionHash);
        Assert.Equal(first.Binding.Target.ContentHash, restarted.Binding.Target.ContentHash);
        Assert.Equal(first.Definition.ContentHash, restarted.Definition.ContentHash);
        Assert.Null(history.NextAfterPosition);

        var audit = await new SqliteAuditIntegrityQuery(fixture.Options).VerifyAsync(
            new AuditVerificationRequest());
        Assert.Equal(AuditIntegrityState.Verified, audit.State);
        Assert.True(audit.ThroughSequence > 0);
    }

    [Fact]
    public async Task V123_D02_RevokedSessionReservationIsRejectedAtWriterWithoutRevision()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(maximumRevisionCount: 1);
        var binding = await fixture.AppendCompletedBindingAsync();
        var signedIn = await fixture.SignInAsync();
        var operationId = Guid.NewGuid();
        var provisionalChange = fixture.CreateChange(signedIn, operationId, Guid.NewGuid(), binding,
            "Declare revoked-session imaging setup");
        var grantId = await fixture.IssueImagingGrantAsync(signedIn, operationId,
            provisionalChange.AuthorizationTarget);
        var authorization = await fixture.AuthorizeImagingAsync(signedIn, operationId, grantId,
            provisionalChange.AuthorizationTarget);
        Assert.True(authorization.Authorized, authorization.ReasonCode);
        Assert.NotNull(authorization.Reservation);

        try
        {
            var logout = await fixture.Sessions.LogoutAsync(signedIn.SessionId)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(logout.Succeeded, logout.ReasonCode);
            Assert.True(logout.AuditPersisted);

            var change = fixture.CreateChange(signedIn, operationId, grantId, binding,
                "Declare revoked-session imaging setup");
            var write = await fixture.Persistence.AppendAsync(
                new ImagingSetupPersistenceRequest(fixture.CreateFact(signedIn, operationId, grantId),
                    change, authorization), new StoreDeadline(fixture.Options.CommitTimeout));

            Assert.False(write.Committed);
            Assert.Equal("SessionMismatch", write.ReasonCode);
            await fixture.WaitForVerifiedAsync();

            var state = await fixture.Persistence.ReadAsync(LogicalRole);
            Assert.Empty(state.Revisions);
            Assert.Equal(0L, await fixture.ScalarAsync(
                "SELECT COUNT(*) FROM imaging_setup_revisions;"));
        }
        finally
        {
            authorization.Reservation?.Dispose();
        }
    }

    [Fact]
    public async Task V123_D03_SignedImagingRowWithMismatchedAuthorizationBindingIsRejected()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(maximumRevisionCount: 1);
        var binding = await fixture.AppendCompletedBindingAsync();
        var signedIn = await fixture.SignInAsync();

        var write = await fixture.AppendMalformedAuthorizationAsync(signedIn, binding);
        Assert.True(write.Committed, write.ReasonCode);
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM imaging_setup_revisions;"));

        var readFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.ReadImagingSetupAsync(LogicalRole).AsTask());
        Assert.Equal("ImagingSetupAuthorizationBindingMismatch", readFailure.Message);

        var verification = await new SqliteAuditIntegrityQuery(fixture.Options).VerifyAsync(
            new AuditVerificationRequest());
        Assert.Equal(AuditIntegrityState.Faulted, verification.State);
        Assert.Equal("ImagingSetupAuthorizationBindingMismatch", verification.ReasonCode);
    }

    private sealed class DurabilityFixture : IAsyncDisposable
    {
        private const string UserName = "imaging-durability-admin";
        private const string Password = "V123 imaging durability 26! test secret";
        private readonly string _directory;
        private readonly AuditIntegrityPolicy _audit;
        private bool _disposed;

        private DurabilityFixture(string directory, AuditIntegrityPolicy audit,
            ProductionStoreOptions options, SqliteCommandStore store,
            LocalIdentityService identity, InteractiveSessionService sessions,
            LocalAuthorizationService authorization)
        {
            _directory = directory;
            _audit = audit;
            Options = options;
            Store = store;
            Identity = identity;
            Sessions = sessions;
            Authorization = authorization;
            Persistence = new SqliteImagingSetupRevisionPersistence(store);
        }

        internal ProductionStoreOptions Options { get; }
        internal SqliteCommandStore Store { get; private set; }
        internal LocalIdentityService Identity { get; }
        internal InteractiveSessionService Sessions { get; }
        internal LocalAuthorizationService Authorization { get; }
        internal SqliteImagingSetupRevisionPersistence Persistence { get; private set; }

        internal static async Task<DurabilityFixture> CreateAsync(int maximumRevisionCount)
        {
            if (!OperatingSystem.IsWindows())
                throw SkipException.ForSkip("Imaging setup durability requires Windows machine protection.");

            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
                "V123-ImagingDurability-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var audit = new AuditIntegrityPolicy("V123ImagingDurabilityStation", "v1",
                "SharpInspect.Test.V123.ImagingDurability." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "audit-keys"),
                CheckpointEveryEntries = 2,
                VerificationInterval = TimeSpan.FromSeconds(1),
                MaximumVerificationEntries = 10_000
            };
            var identityOptions = new LocalIdentityOptions(audit.StationId,
                new LocalPasswordPolicy
                {
                    Blocklist = PasswordBlocklist.Create("v123-imaging-durability-blocklist", "1",
                        new[] { "known-compromised-imaging-password" })
                }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
                AuthorizationPolicy.Development);
            var options = new ProductionStoreOptions(Path.Combine(directory, "imaging-durability.sqlite"))
            {
                AuditIntegrityPolicy = audit,
                LocalIdentity = identityOptions,
                CameraSetup = new CameraSetupStoreOptions(),
                ImagingSetup = new ImagingSetupStoreOptions
                {
                    MaximumRevisionCount = maximumRevisionCount
                },
                CommitTimeout = TimeSpan.FromSeconds(5),
                QueryTimeout = TimeSpan.FromSeconds(5),
                QueueCapacity = 8
            };

            SqliteCommandStore? store = null;
            InteractiveSessionService? sessions = null;
            LocalAuthorizationService? authorization = null;
            try
            {
                store = new SqliteCommandStore(options);
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(store);

                var identity = new LocalIdentityService(store, identityOptions,
                    new FixtureConsoleAuthority());
                sessions = new InteractiveSessionService(identity,
                    identityOptions.AuthenticationPolicy, identity.PersistSessionEventAsync);
                authorization = new LocalAuthorizationService(store, identityOptions, identity, sessions);
                var fixture = new DurabilityFixture(directory, audit, options, store,
                    identity, sessions, authorization);

                var tokenResult = await identity.ProvisionBootstrapTokenAsync();
                Assert.True(tokenResult.Succeeded, tokenResult.ReasonCode);
                await fixture.WaitForVerifiedAsync();
                var created = await identity.CreateFirstAdministratorAsync(
                    new BootstrapAdministratorRequest(audit.StationId,
                        tokenResult.Token!.TakeForDisplay(), UserName,
                        "V123 Imaging Durability Administrator", Password));
                Assert.True(created.Succeeded, created.ReasonCode);
                created.RecoveryKit?.Dispose();
                await fixture.WaitForVerifiedAsync();
                return fixture;
            }
            catch
            {
                authorization?.Dispose();
                if (sessions is not null) await sessions.DisposeAsync();
                if (store is not null) await store.DisposeAsync();
                Cleanup(directory, audit);
                throw;
            }
        }

        internal async Task<CameraBindingRevision> AppendCompletedBindingAsync()
        {
            var operationId = Guid.NewGuid();
            var actor = Guid.NewGuid();
            var session = Guid.NewGuid();
            var grant = Guid.NewGuid();
            var target = new CameraBindingTarget(
                new CameraProviderIdentity("V123.Durability.Provider", "1",
                    "V123.Durability.Adapter", "1"), "device-001");
            var recordedAt = DateTimeOffset.UtcNow;
            var cameraEvent = new CameraSetupEvent(0, Guid.NewGuid(), operationId, LogicalRole,
                AuditedCommandKind.RebindCamera, CameraSetupEventPhase.Completed, 1,
                target: target, succeeded: true, reasonCode: "CameraRebindCompleted",
                changeReason: "V123DurabilityBinding", actorPrincipalId: actor,
                sessionId: session, authorAuthorizationRevision: 1,
                recordedAtUtc: recordedAt);
            cameraEvent = cameraEvent with
            {
                RevisionHash = CameraSetupStorageCodec.ComputeRevisionHash(cameraEvent, 1, null, target)
            };
            var admission = new IdentityAuditEvent(Guid.NewGuid(),
                IdentityEventKind.CameraSetupActionAuthorized, recordedAt, _audit.StationId, actor,
                null, null, null, "CameraRebindAdmitted", ActorPrincipalId: actor,
                CommandCorrelationId: operationId, StepUpGrantId: grant,
                RequiredPermission: Permission.ManageCameraBindings.ToString(),
                AuthorizationRevision: 1, ManagementReason: IdentityManagementReason.AccessChange.ToString(),
                ActionTargetId: LogicalRole, BoundCommandCorrelationId: operationId,
                ActionCommandKind: AuditedCommandKind.RebindCamera.ToString(),
                OperationId: operationId, SessionId: session);
            var terminalIdentity = admission with
            {
                EventId = Guid.NewGuid(), Kind = IdentityEventKind.CameraSetupOperationCompleted,
                ReasonCode = "CameraRebindCompleted"
            };
            var attemptId = Guid.NewGuid();
            var runtimeEpoch = Guid.NewGuid();
            var outcome = new CommandAuditFact(Guid.NewGuid(), attemptId, operationId, runtimeEpoch,
                recordedAt, AuditedCommandKind.RebindCamera, CommandSource.PhysicalConsole,
                actor.ToString("D"), session, grant, CommandAuditPhase.Outcome,
                CommandDisposition.Accepted, "CameraRebindAdmitted", actor.ToString("D"));
            var terminal = outcome with
            {
                EventId = Guid.NewGuid(), Phase = CommandAuditPhase.Completed,
                Disposition = null, ReasonCode = "CameraRebindCompleted"
            };
            var write = await Store.UpdateIdentityAsync(state => new IdentityUpdate(
                new object(), new[] { admission, terminalIdentity },
                new[] { outcome, terminal }, CameraEvents: new[] { cameraEvent }),
                CancellationToken.None);
            Assert.True(write.Committed, write.ReasonCode);
            await WaitForVerifiedAsync();

            var persisted = await Store.ReadCameraSetupAsync(LogicalRole);
            Assert.NotNull(persisted.State.Binding);
            return persisted.State.Binding!;
        }

        internal async Task<SignedIn> SignInAsync()
        {
            var result = await Sessions.SignInAsync(new PasswordSignInRequest(UserName, Password));
            Assert.True(result.Succeeded, result.ReasonCode);
            Assert.NotNull(result.Identity);
            Assert.Equal(InteractiveSessionState.Authenticated, result.Session.State);
            await WaitForVerifiedAsync();
            var state = await Store.ReadIdentityAsync(CancellationToken.None);
            var account = state.EnumerateAccounts().Single(item =>
                item.PrincipalId == result.Identity!.PrincipalId);
            return new SignedIn(result.Identity.PrincipalId, result.Session.SessionId!.Value,
                account.AuthorizationRevision);
        }

        internal async Task<Guid> IssueImagingGrantAsync(SignedIn signedIn, Guid operationId,
            string authorizationTarget)
        {
            var result = await Authorization.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(),
                signedIn.Invocation, new StepUpBinding(Permission.ManageCameraBindings, operationId,
                    authorizationTarget, AuditedCommandKind.DeclareImagingSetup), Password));
            Assert.True(result.Succeeded, result.ReasonCode);
            Assert.True(result.GrantId.HasValue);
            await WaitForVerifiedAsync();
            return result.GrantId!.Value;
        }

        internal async Task<CameraSetupAuthorization> AuthorizeImagingAsync(SignedIn signedIn,
            Guid operationId, Guid grantId, string authorizationTarget) => await Authorization.AuthorizeCameraSetupAsync(
                signedIn.Invocation with { StepUpGrantId = grantId }, readOnly: false,
                operationId, authorizationTarget, AuditedCommandKind.DeclareImagingSetup);

        internal async Task<StoreWriteResult> AppendAuthorizedAsync(SignedIn signedIn,
            CameraBindingRevision binding, string lensIdentity, string reason,
            long expectedRevision = 0, string? expectedRevisionHash = null)
        {
            var operationId = Guid.NewGuid();
            var provisionalChange = CreateChange(signedIn, operationId, Guid.NewGuid(), binding, reason,
                expectedRevision, expectedRevisionHash, lensIdentity);
            var grantId = await IssueImagingGrantAsync(signedIn, operationId,
                provisionalChange.AuthorizationTarget);
            var authorization = await AuthorizeImagingAsync(signedIn, operationId, grantId,
                provisionalChange.AuthorizationTarget);
            Assert.True(authorization.Authorized, authorization.ReasonCode);
            Assert.NotNull(authorization.Reservation);
            try
            {
                var change = CreateChange(signedIn, operationId, grantId, binding, reason,
                    expectedRevision, expectedRevisionHash, lensIdentity);
                return await Persistence.AppendAsync(new ImagingSetupPersistenceRequest(
                    CreateFact(signedIn, operationId, grantId), change, authorization),
                    new StoreDeadline(Options.CommitTimeout));
            }
            finally
            {
                authorization.Reservation?.Dispose();
            }
        }

        internal ImagingSetupChangeRequest CreateChange(SignedIn signedIn, Guid operationId,
            Guid grantId, CameraBindingRevision binding, string reason,
            long expectedRevision = 0, string? expectedRevisionHash = null,
            string lensIdentity = "Lens-A") => new(operationId,
                signedIn.Invocation with { StepUpGrantId = grantId }, LogicalRole,
                binding.Revision, binding.RevisionHash, expectedRevision, expectedRevisionHash,
                new ImagingSetupDefinition(lensIdentity, "Focus-100", "Mount-Top", 250, "SensorUp"),
                reason);

        internal CommandAuditFact CreateFact(SignedIn signedIn, Guid operationId, Guid grantId) =>
            new(Guid.NewGuid(), Guid.NewGuid(), operationId, Guid.NewGuid(), DateTimeOffset.UtcNow,
                AuditedCommandKind.DeclareImagingSetup, CommandSource.PhysicalConsole,
                signedIn.PrincipalId.ToString("D"), signedIn.SessionId, grantId,
                CommandAuditPhase.Outcome, CommandDisposition.Accepted,
                "ImagingSetupRevisionPersisted", signedIn.PrincipalId.ToString("D"));

        internal async Task<IdentityWriteResult> AppendMalformedAuthorizationAsync(
            SignedIn signedIn, CameraBindingRevision binding)
        {
            var operationId = Guid.NewGuid();
            var grantId = Guid.NewGuid();
            var change = CreateChange(signedIn, operationId, grantId, binding,
                "Persist malformed authorization evidence", lensIdentity: "Lens-Malformed");
            var commandFact = CreateFact(signedIn, operationId, grantId);
            var wrongBoundCorrelation = Guid.NewGuid();
            var result = await Store.UpdateImagingSetupAsync(operationId, LogicalRole,
                (state, camera, _, _) =>
                {
                    var actualBinding = camera.Binding ??
                        throw new InvalidOperationException("CameraBindingMissingForImagingDurabilityTest");
                    var action = new IdentityAuditEvent(Guid.NewGuid(),
                        IdentityEventKind.CameraSetupActionAuthorized, commandFact.OccurredAtUtc,
                        state.StationId, signedIn.PrincipalId, null, null, null,
                        "ImagingSetupRevisionPersisted", ActorPrincipalId: signedIn.PrincipalId,
                        CommandCorrelationId: operationId, StepUpGrantId: grantId,
                        RequiredPermission: Permission.ManageCameraBindings.ToString(),
                        AuthorizationRevision: signedIn.AuthorizationRevision,
                        ActionTargetId: change.AuthorizationTarget,
                        BoundCommandCorrelationId: wrongBoundCorrelation,
                        ActionCommandKind: AuditedCommandKind.DeclareImagingSetup.ToString(),
                        OperationId: operationId, SessionId: signedIn.SessionId);
                    var completed = action with
                    {
                        EventId = Guid.NewGuid(), Kind = IdentityEventKind.CameraSetupOperationCompleted
                    };
                    var terminal = commandFact with
                    {
                        EventId = Guid.NewGuid(), Phase = CommandAuditPhase.Completed,
                        Disposition = null
                    };
                    var mutation = new ImagingSetupRevisionMutation(change, actualBinding,
                        signedIn.PrincipalId, signedIn.SessionId, signedIn.AuthorizationRevision);
                    return new IdentityUpdate(new object(), new[] { action, completed },
                        new[] { commandFact, terminal }, ImagingRevision: mutation);
                }, CancellationToken.None, new StoreDeadline(Options.CommitTimeout));
            return result;
        }

        internal async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Options.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        internal async Task RestartStoreAsync()
        {
            await Store.DisposeAsync();
            Store = new SqliteCommandStore(Options);
            var initialized = await Store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await WaitForVerifiedAsync();
            Persistence = new SqliteImagingSetupRevisionPersistence(Store);
        }

        internal async Task WaitForVerifiedAsync() => await WaitForVerifiedAsync(Store);

        private static async Task WaitForVerifiedAsync(SqliteCommandStore store)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (store.Integrity is { State: AuditIntegrityState.Verified }) return;
                if (store.Integrity is { State: AuditIntegrityState.Faulted } fault)
                    throw new XunitException(fault.ReasonCode);
                await Task.Delay(20);
            }
            throw new XunitException("Imaging durability audit did not become Verified: " +
                store.Integrity?.ReasonCode);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            Authorization.Dispose();
            await Sessions.DisposeAsync();
            await Store.DisposeAsync();
            Cleanup(_directory, _audit);
        }

        private static void Cleanup(string directory, AuditIntegrityPolicy audit)
        {
            try
            {
                var key = WindowsMachineAuditKey.GetKeyPath(audit);
                if (File.Exists(key)) File.Delete(key);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            try
            {
                var fullDirectory = Path.GetFullPath(directory);
                var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                    "SharpInspect.Runtime.Tests"));
                if (fullDirectory.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(fullDirectory))
                    Directory.Delete(fullDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record SignedIn(Guid PrincipalId, Guid SessionId,
        long AuthorizationRevision)
    {
        internal CommandInvocation Invocation => new(CommandSource.PhysicalConsole,
            PrincipalId.ToString("D"), SessionId);
    }

    private sealed class FixtureConsoleAuthority : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true,
            "S-1-5-21-V123-ImagingDurability");
    }
}

#pragma warning restore CA1416
