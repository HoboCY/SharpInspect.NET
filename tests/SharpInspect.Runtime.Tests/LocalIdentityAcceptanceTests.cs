using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Windows acceptance coverage for the offline local identity bootstrap. Each test owns a
/// unique database and machine-protected audit key. The database directory is retained as
/// evidence; cleanup removes only the key named by this fixture.
/// </summary>
public sealed class LocalIdentityAcceptanceTests
{
    [Fact]
    public async Task V104_S01_BootstrapHasNoDefaultAdminAndPersistsPrincipalAcrossRestart()
    {
        RequireWindows();
        await using var context = TestContext.Create();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var authority = new FakeConsoleAuthority(physicalConsole: true, windowsAdministrator: true);

        Guid principalId;
        await using (var store = await context.OpenStoreAsync())
        {
            var identity = context.Identity(store, authority, clock);
            var initial = await identity.GetStatusAsync();
            Assert.Equal(context.StationId, initial.StationId);
            Assert.True(initial.BootstrapRequired);
            Assert.Equal(0, initial.UsableAdministratorCount);

            var issued = await identity.ProvisionBootstrapTokenAsync();
            Assert.True(issued.Succeeded, issued.ReasonCode);
            Assert.NotNull(issued.Token);
            var bootstrapToken = issued.Token!.TakeForDisplay();
            Assert.Equal(76, bootstrapToken.Length);
            await WaitForVerifiedAsync(store);

            var request = new BootstrapAdministratorRequest(
                context.StationId, bootstrapToken, "alice-104", "Alice 104", context.Password);
            var created = await identity.CreateFirstAdministratorAsync(request);
            Assert.True(created.Succeeded, created.ReasonCode);
            Assert.NotNull(created.Identity);
            Assert.NotNull(created.RecoveryKit);
            principalId = created.Identity!.PrincipalId;
            var recoveryKit = created.RecoveryKit!.TakeForDisplay();
            Assert.Contains("Recovery Kit:", recoveryKit, StringComparison.Ordinal);
            Assert.Equal(1, (await identity.GetStatusAsync()).UsableAdministratorCount);
            Assert.Equal(context.RecoveryCodeCount, (await identity.GetStatusAsync()).ValidRecoveryCodeCount);
            await WaitForVerifiedAsync(store);

            var beforeFailure = await store.ReadIdentityAsync(CancellationToken.None);
            var failed = await identity.AuthenticateAsync(
                new PasswordSignInRequest("alice-104", "short"));
            Assert.False(failed.Succeeded);
            Assert.Equal("AuthenticationRejected", failed.ReasonCode);
            await WaitForVerifiedAsync(store);
            var afterFailure = await store.ReadIdentityAsync(CancellationToken.None);
            Assert.NotNull(beforeFailure.Administrator);
            Assert.NotNull(afterFailure.Administrator);
            Assert.Equal(beforeFailure.Administrator!.CredentialId, afterFailure.Administrator!.CredentialId);
            Assert.Equal(beforeFailure.Administrator.CredentialRevision, afterFailure.Administrator.CredentialRevision);
            Assert.Equal(beforeFailure.Administrator.Password.Algorithm, afterFailure.Administrator.Password.Algorithm);
            Assert.Equal(beforeFailure.Administrator.Password.Cost, afterFailure.Administrator.Password.Cost);
            Assert.Equal(beforeFailure.Administrator.Password.Salt, afterFailure.Administrator.Password.Salt);
            Assert.Equal(beforeFailure.Administrator.Password.Derived, afterFailure.Administrator.Password.Derived);

            // T5 now persists a delay after the intentionally incorrect login above.
            clock.UtcNow = afterFailure.StationThrottle.NextAllowedAtUtc;
            var authenticated = await identity.AuthenticateAsync(
                new PasswordSignInRequest("alice-104", context.Password));
            Assert.True(authenticated.Succeeded, authenticated.ReasonCode);
            Assert.Equal(principalId, authenticated.Identity!.PrincipalId);
            await WaitForVerifiedAsync(store);
        }

        await using (var restarted = await context.OpenStoreAsync())
        {
            var identity = context.Identity(restarted, authority, clock);
            var authenticated = await identity.AuthenticateAsync(
                new PasswordSignInRequest("alice-104", context.Password));
            Assert.True(authenticated.Succeeded, authenticated.ReasonCode);
            Assert.Equal(principalId, authenticated.Identity!.PrincipalId);
            await WaitForVerifiedAsync(restarted);
        }
    }

    [Fact]
    public async Task V104_S02_BootstrapRequiresPhysicalAdminStationAndSingleUseToken()
    {
        RequireWindows();
        await using var context = TestContext.Create();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var authority = new FakeConsoleAuthority(physicalConsole: false, windowsAdministrator: false);

        await using (var store = await context.OpenStoreAsync())
        {
            var identity = context.Identity(store, authority, clock);
            var noConsole = await identity.ProvisionBootstrapTokenAsync();
            Assert.False(noConsole.Succeeded);
            Assert.Equal("PhysicalConsoleRequired", noConsole.ReasonCode);
            await WaitForVerifiedAsync(store);

            authority.PhysicalConsole = true;
            var noAdministrator = await identity.ProvisionBootstrapTokenAsync();
            Assert.False(noAdministrator.Succeeded);
            Assert.Equal("WindowsAdministratorRequired", noAdministrator.ReasonCode);
            await WaitForVerifiedAsync(store);

            authority.WindowsAdministrator = true;
            var issued = await identity.ProvisionBootstrapTokenAsync();
            Assert.True(issued.Succeeded, issued.ReasonCode);
            var token = issued.Token!.TakeForDisplay();
            await WaitForVerifiedAsync(store);

            var wrongStation = await identity.CreateFirstAdministratorAsync(
                new BootstrapAdministratorRequest("OtherStation", token, "alice-104", "Alice 104", "short"));
            Assert.False(wrongStation.Succeeded);
            Assert.Equal("BootstrapStationMismatch", wrongStation.ReasonCode);
            await WaitForVerifiedAsync(store);

            clock.UtcNow = clock.UtcNow.Add(context.TokenLifetime + TimeSpan.FromSeconds(1));
            var expired = await identity.CreateFirstAdministratorAsync(
                new BootstrapAdministratorRequest(context.StationId, token, "alice-104", "Alice 104", "short"));
            Assert.False(expired.Succeeded);
            Assert.Equal("BootstrapTokenExpired", expired.ReasonCode);
            await WaitForVerifiedAsync(store);
        }

        await using (var reuseContext = TestContext.Create())
        {
            var reuseClock = new MutableClock(DateTimeOffset.UtcNow);
            var reuseAuthority = new FakeConsoleAuthority(physicalConsole: true, windowsAdministrator: true);
            await using var store = await reuseContext.OpenStoreAsync();
            var identity = reuseContext.Identity(store, reuseAuthority, reuseClock);
            var issued = await identity.ProvisionBootstrapTokenAsync();
            Assert.True(issued.Succeeded, issued.ReasonCode);
            var token = issued.Token!.TakeForDisplay();
            await WaitForVerifiedAsync(store);
            var created = await identity.CreateFirstAdministratorAsync(
                new BootstrapAdministratorRequest(reuseContext.StationId, token, "alice-104", "Alice 104",
                    reuseContext.Password));
            Assert.True(created.Succeeded, created.ReasonCode);
            await WaitForVerifiedAsync(store);

            var reused = await identity.CreateFirstAdministratorAsync(
                new BootstrapAdministratorRequest(reuseContext.StationId, token, "alice-104", "Alice 104", "short"));
            Assert.False(reused.Succeeded);
            Assert.Equal("AdministratorAlreadyEstablished", reused.ReasonCode);
            await WaitForVerifiedAsync(store);
        }
    }

    [Fact]
    public async Task V104_S03_SuccessfulLoginUpgradesHistoricalVerifier()
    {
        RequireWindows();
        await using var context = TestContext.Create();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var authority = new FakeConsoleAuthority(physicalConsole: true, windowsAdministrator: true);

        await using var store = await context.OpenStoreAsync();
        var identity = context.Identity(store, authority, clock);
        var issued = await identity.ProvisionBootstrapTokenAsync();
        Assert.True(issued.Succeeded, issued.ReasonCode);
        var token = issued.Token!.TakeForDisplay();
        await WaitForVerifiedAsync(store);
        var created = await identity.CreateFirstAdministratorAsync(
            new BootstrapAdministratorRequest(context.StationId, token, "alice-104", "Alice 104", context.Password));
        Assert.True(created.Succeeded, created.ReasonCode);
        await WaitForVerifiedAsync(store);

        await InstallHistoricalVerifierAsync(store, context.Password, context.IdentityOptions());
        await WaitForVerifiedAsync(store);
        var before = await store.ReadIdentityAsync(CancellationToken.None);
        Assert.Equal(1, before.Administrator!.Password.Cost);

        var authenticated = await identity.AuthenticateAsync(
            new PasswordSignInRequest("alice-104", context.Password));
        Assert.True(authenticated.Succeeded, authenticated.ReasonCode);
        Assert.Equal(created.Identity!.PrincipalId, authenticated.Identity!.PrincipalId);
        await WaitForVerifiedAsync(store);

        var after = await store.ReadIdentityAsync(CancellationToken.None);
        Assert.Equal(PasswordHashBaseline.SecurityFloorIterations, after.Administrator!.Password.Cost);
        Assert.Equal(before.Administrator.CredentialRevision + 1, after.Administrator.CredentialRevision);
        Assert.NotEqual(before.Administrator.Password.Derived, after.Administrator.Password.Derived);
    }

    [Fact]
    public async Task V104_S04_IdentityTriggersRollbackBootstrapConsumptionAndPreserveToken()
    {
        RequireWindows();
        await using var context = TestContext.Create();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var authority = new FakeConsoleAuthority(physicalConsole: true, windowsAdministrator: true);

        await using var store = await context.OpenStoreAsync();
        var identity = context.Identity(store, authority, clock);
        var issued = await identity.ProvisionBootstrapTokenAsync();
        Assert.True(issued.Succeeded, issued.ReasonCode);
        var token = issued.Token!.TakeForDisplay();
        await WaitForVerifiedAsync(store);

        var before = await store.ReadIdentityAsync(CancellationToken.None);
        var beforeEntries = CountRows(context.DatabasePath, "audit_entries");
        var authorityTrigger = "v104_identity_authority_abort_" + Guid.NewGuid().ToString("N");
        Execute(context.DatabasePath, $@"
            CREATE TRIGGER {authorityTrigger}
            BEFORE UPDATE ON identity_authority
            BEGIN
                SELECT RAISE(ABORT, 'V104IdentityAuthorityAbort');
            END;");
        var authorityFailure = await identity.CreateFirstAdministratorAsync(
            CreateAdministratorRequest(context, token));
        Assert.False(authorityFailure.Succeeded);
        Assert.NotEqual("AdministratorCreated", authorityFailure.ReasonCode);
        await WaitForVerifiedAsync(store);
        var afterAuthorityFailure = await store.ReadIdentityAsync(CancellationToken.None);
        Assert.Null(afterAuthorityFailure.Administrator);
        Assert.Equal(before.Revision, afterAuthorityFailure.Revision);
        Assert.Equal("Pending", afterAuthorityFailure.Bootstrap!.State);
        Assert.Equal(beforeEntries, CountRows(context.DatabasePath, "audit_entries"));
        Execute(context.DatabasePath, $"DROP TRIGGER {authorityTrigger};");

        var auditTrigger = "v104_audit_entries_abort_" + Guid.NewGuid().ToString("N");
        Execute(context.DatabasePath, $@"
            CREATE TRIGGER {auditTrigger}
            BEFORE INSERT ON audit_entries
            BEGIN
                SELECT RAISE(ABORT, 'V104AuditEntryAbort');
            END;");
        var auditFailure = await identity.CreateFirstAdministratorAsync(
            CreateAdministratorRequest(context, token));
        Assert.False(auditFailure.Succeeded);
        Assert.NotEqual("AdministratorCreated", auditFailure.ReasonCode);
        await WaitForVerifiedAsync(store);
        var afterAuditFailure = await store.ReadIdentityAsync(CancellationToken.None);
        Assert.Null(afterAuditFailure.Administrator);
        Assert.Equal(before.Revision, afterAuditFailure.Revision);
        Assert.Equal("Pending", afterAuditFailure.Bootstrap!.State);
        Assert.Equal(beforeEntries, CountRows(context.DatabasePath, "audit_entries"));
        Execute(context.DatabasePath, $"DROP TRIGGER {auditTrigger};");

        var recovered = await identity.CreateFirstAdministratorAsync(
            CreateAdministratorRequest(context, token));
        Assert.True(recovered.Succeeded, recovered.ReasonCode);
        await WaitForVerifiedAsync(store);
        Assert.NotNull((await store.ReadIdentityAsync(CancellationToken.None)).Administrator);
    }

    [Fact]
    public async Task V104_S05_AuditInsertAbortRollsBackVerifierUpgrade()
    {
        RequireWindows();
        await using var context = TestContext.Create();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var authority = new FakeConsoleAuthority(physicalConsole: true, windowsAdministrator: true);

        await using var store = await context.OpenStoreAsync();
        var identity = context.Identity(store, authority, clock);
        var issued = await identity.ProvisionBootstrapTokenAsync();
        Assert.True(issued.Succeeded, issued.ReasonCode);
        var token = issued.Token!.TakeForDisplay();
        await WaitForVerifiedAsync(store);
        var created = await identity.CreateFirstAdministratorAsync(
            new BootstrapAdministratorRequest(context.StationId, token, "alice-104", "Alice 104", context.Password));
        Assert.True(created.Succeeded, created.ReasonCode);
        await WaitForVerifiedAsync(store);

        await InstallHistoricalVerifierAsync(store, context.Password, context.IdentityOptions());
        await WaitForVerifiedAsync(store);
        var before = await store.ReadIdentityAsync(CancellationToken.None);
        var beforeEntries = CountRows(context.DatabasePath, "audit_entries");
        var trigger = "v104_upgrade_audit_abort_" + Guid.NewGuid().ToString("N");
        Execute(context.DatabasePath, $@"
            CREATE TRIGGER {trigger}
            BEFORE INSERT ON audit_entries
            BEGIN
                SELECT RAISE(ABORT, 'V104UpgradeAuditAbort');
            END;");

        var failed = await identity.AuthenticateAsync(
            new PasswordSignInRequest("alice-104", context.Password));
        Assert.False(failed.Succeeded);
        await WaitForVerifiedAsync(store);
        var afterFailure = await store.ReadIdentityAsync(CancellationToken.None);
        Assert.Equal(before.Revision, afterFailure.Revision);
        Assert.Equal(before.Administrator!.CredentialRevision, afterFailure.Administrator!.CredentialRevision);
        Assert.Equal(1, afterFailure.Administrator.Password.Cost);
        Assert.Equal(before.Administrator.Password.Derived, afterFailure.Administrator.Password.Derived);
        Assert.Equal(beforeEntries, CountRows(context.DatabasePath, "audit_entries"));

        Execute(context.DatabasePath, $"DROP TRIGGER {trigger};");
        var recovered = await identity.AuthenticateAsync(
            new PasswordSignInRequest("alice-104", context.Password));
        Assert.True(recovered.Succeeded, recovered.ReasonCode);
        await WaitForVerifiedAsync(store);
        var afterRecovery = await store.ReadIdentityAsync(CancellationToken.None);
        Assert.Equal(PasswordHashBaseline.SecurityFloorIterations, afterRecovery.Administrator!.Password.Cost);
        Assert.Equal(before.Administrator.CredentialRevision + 1, afterRecovery.Administrator.CredentialRevision);
    }

    [Fact]
    public async Task V104_S06_SecretsStayOutOfAuditPayloadRequestsAndTextRepresentations()
    {
        RequireWindows();
        await using var context = TestContext.Create();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var authority = new FakeConsoleAuthority(physicalConsole: true, windowsAdministrator: true);

        await using var store = await context.OpenStoreAsync();
        var identity = context.Identity(store, authority, clock);
        var issued = await identity.ProvisionBootstrapTokenAsync();
        Assert.True(issued.Succeeded, issued.ReasonCode);
        var token = issued.Token!.TakeForDisplay();
        await WaitForVerifiedAsync(store);
        var request = new BootstrapAdministratorRequest(
            context.StationId, token, "alice-104", "Alice 104", context.Password);
        var requestJson = JsonSerializer.Serialize(request);
        Assert.DoesNotContain(token, requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain(context.Password, requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain(token, request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(context.Password, request.ToString(), StringComparison.Ordinal);

        var created = await identity.CreateFirstAdministratorAsync(request);
        Assert.True(created.Succeeded, created.ReasonCode);
        var recoveryKit = created.RecoveryKit!.TakeForDisplay();
        await WaitForVerifiedAsync(store);
        var state = await store.ReadIdentityAsync(CancellationToken.None);
        var verifierValues = new List<string>
        {
            context.Password,
            token,
            recoveryKit,
            state.Administrator!.Password.Salt,
            state.Administrator.Password.Derived
        };
        if (state.Bootstrap is not null) verifierValues.Add(state.Bootstrap.Verifier);
        verifierValues.AddRange(state.RecoveryCodes.Select(code => code.Verifier));

        var requestAgainJson = JsonSerializer.Serialize(new PasswordSignInRequest("alice-104", context.Password));
        Assert.DoesNotContain(context.Password, requestAgainJson, StringComparison.Ordinal);
        using var transient = new OneTimeSecret(context.Password);
        Assert.DoesNotContain(context.Password, transient.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(state.Administrator.Password.Salt, state.Administrator.Password.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(state.Administrator.Password.Derived, state.Administrator.Password.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(context.Password, state.ToString(), StringComparison.Ordinal);

        var payloadText = ReadAuditPayloadText(context.DatabasePath);
        await store.DisposeAsync();
        var databaseText = Encoding.UTF8.GetString(File.ReadAllBytes(context.DatabasePath));
        foreach (var value in verifierValues.Where(value => !string.IsNullOrEmpty(value)))
        {
            Assert.False(payloadText.Contains(value, StringComparison.Ordinal), "A prohibited secret appeared in audit payloads.");
            Assert.False(databaseText.Contains(value, StringComparison.Ordinal), "A prohibited secret appeared in database clear text.");
        }
    }

    [Fact]
    public async Task V104_S07_LegacySchemasRejectIdentityReadOnlyWithoutChangingDbOrKey()
    {
        RequireWindows();
        await using (var schemaOne = TestContext.Create())
        {
            await using (var legacy = new SqliteCommandStore(new ProductionStoreOptions(schemaOne.DatabasePath)))
            {
                var initialized = await legacy.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(initialized.Committed, initialized.ReasonCode);
            }

            var before = DatabaseHash(schemaOne.DatabasePath);
            var keyPath = WindowsMachineAuditKey.GetKeyPath(schemaOne.Policy);
            Assert.False(File.Exists(keyPath));
            await using var rejected = new SqliteCommandStore(schemaOne.Options());
            var result = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.Committed);
            Assert.Equal("AuditGovernedMigrationRequired", result.ReasonCode);
            Assert.Equal(before, DatabaseHash(schemaOne.DatabasePath));
            Assert.False(File.Exists(keyPath));
        }

        await using (var schemaTwo = TestContext.Create())
        {
            await using (var legacy = await schemaTwo.OpenStoreAsync(identityEnabled: false))
            {
                // The store is deliberately held open until this scope ends so its writer and
                // machine key are released before the identity3 read-only probe.
            }

            var before = DatabaseHash(schemaTwo.DatabasePath);
            var keyPath = WindowsMachineAuditKey.GetKeyPath(schemaTwo.Policy);
            Assert.True(File.Exists(keyPath));
            var keyBefore = Convert.ToHexString(File.ReadAllBytes(keyPath));
            await using var rejected = new SqliteCommandStore(schemaTwo.Options());
            var result = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.Committed);
            Assert.Equal("AuditGovernedMigrationRequired", result.ReasonCode);
            Assert.Equal(before, DatabaseHash(schemaTwo.DatabasePath));
            Assert.Equal(keyBefore, Convert.ToHexString(File.ReadAllBytes(keyPath)));
        }
    }

    [Fact]
    public async Task V104_S08_CommandAndIdentityEventsInterleaveAndRemainReadOnlyVerifiable()
    {
        RequireWindows();
        await using var context = TestContext.Create();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var authority = new FakeConsoleAuthority(physicalConsole: true, windowsAdministrator: true);

        await using var store = await context.OpenStoreAsync();
        var commandBeforeIdentity = OutcomeFact();
        var firstWrite = await store.AppendAsync(commandBeforeIdentity, new StoreDeadline(TimeSpan.FromSeconds(2)));
        Assert.True(firstWrite.Committed, firstWrite.ReasonCode);
        await WaitForVerifiedAsync(store);

        var identity = context.Identity(store, authority, clock);
        var issued = await identity.ProvisionBootstrapTokenAsync();
        Assert.True(issued.Succeeded, issued.ReasonCode);
        var token = issued.Token!.TakeForDisplay();
        await WaitForVerifiedAsync(store);
        await WaitForVerifiedAsync(store);
        var created = await identity.CreateFirstAdministratorAsync(
            new BootstrapAdministratorRequest(context.StationId, token, "alice-104", "Alice 104", context.Password));
        Assert.True(created.Succeeded, created.ReasonCode);
        await WaitForVerifiedAsync(store);

        var commandAfterIdentity = OutcomeFact();
        var secondWrite = await store.AppendAsync(commandAfterIdentity, new StoreDeadline(TimeSpan.FromSeconds(2)));
        Assert.True(secondWrite.Committed, secondWrite.ReasonCode);
        await WaitForVerifiedAsync(store);

        var report = await new SqliteAuditIntegrityQuery(context.Options())
            .VerifyAsync(new AuditVerificationRequest(0, 200));
        Assert.Equal(AuditIntegrityState.Verified, report.State);
        Assert.Equal(report.ThroughSequence, report.VerifiedThroughSequence);
        Assert.True(report.ThroughSequence >= 6);
    }

    [Fact]
    public async Task V104_S11_CoveredHistoricalMetadataTamperingBreaksEnvelopeHash()
    {
        await using var context = TestContext.Create();
        await using (var store = await context.OpenStoreAsync())
        {
            var identity = context.Identity(store, new(true, true), new(DateTimeOffset.UtcNow));
            var issued = await identity.ProvisionBootstrapTokenAsync();
            Assert.True(issued.Succeeded, issued.ReasonCode);
            await WaitForVerifiedAsync(store);
            var created = await identity.CreateFirstAdministratorAsync(CreateAdministratorRequest(context, issued.Token!.TakeForDisplay()));
            Assert.True(created.Succeeded, created.ReasonCode);
            created.RecoveryKit!.Dispose();
            await WaitForVerifiedAsync(store);
        }
        // The altered first IdentityEvent precedes a later signed checkpoint.
        Execute(context.DatabasePath, "DROP TRIGGER audit_entries_immutable_update; PRAGMA ignore_check_constraints=ON; UPDATE audit_entries SET IdentityPosition=0 WHERE Sequence=2;");
        var before = DatabaseHash(context.DatabasePath);
        var report = await new SqliteAuditIntegrityQuery(context.Options()).VerifyAsync(new(0, 200));
        Assert.Equal(AuditIntegrityState.Faulted, report.State);
        Assert.Equal("AuditPayloadHashMismatch", report.ReasonCode);
        Assert.Equal(before, DatabaseHash(context.DatabasePath));
    }

    [Fact]
    public async Task V104_S12_RewrittenPolicyBindingCannotRetargetSignedAuthority()
    {
        await using var context = TestContext.Create();
        await using (var store = await context.OpenStoreAsync()) { }
        var original = context.IdentityOptions();
        var changed = new LocalIdentityOptions(context.StationId, original.PasswordPolicy with
        { Blocklist = PasswordBlocklist.Create(original.PasswordPolicy.Blocklist!.Id,
            original.PasswordPolicy.Blocklist.Version, new[] { "different-compromised-value" }) }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development);
        using (var connection = Open(context.DatabasePath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_master WHERE name='identity_policy_immutable_update';";
            var trigger = (string)command.ExecuteScalar()!;
            command.CommandText = "DROP TRIGGER identity_policy_immutable_update; UPDATE identity_policy_binding SET PolicyContentHash=$hash; " + trigger;
            command.Parameters.AddWithValue("$hash", changed.PolicyContentHash);
            command.ExecuteNonQuery();
        }
        var before = DatabaseHash(context.DatabasePath);
        var keyBefore = DatabaseHash(WindowsMachineAuditKey.GetKeyPath(context.Policy));
        await using (var rejected = new SqliteCommandStore(new(context.DatabasePath)
        { AuditIntegrityPolicy = context.Policy, LocalIdentity = changed }))
        {
            Assert.False((await rejected.Initialization).Committed);
            Assert.Equal(AuditIntegrityState.Faulted, rejected.Integrity?.State);
        }
        Assert.Equal(before, DatabaseHash(context.DatabasePath));
        Assert.Equal(keyBefore, DatabaseHash(WindowsMachineAuditKey.GetKeyPath(context.Policy)));
    }

    [Fact]
    public async Task V104_S13_SignedStateFromSameRevisionOnDifferentBranchIsRejected()
    {
        await using var context = TestContext.Create();
        await using (var initial = await context.OpenStoreAsync()) { }
        var otherPath = Path.Combine(context.DirectoryPath, "other-branch.sqlite");
        File.Copy(context.DatabasePath, otherPath);
        await using (var issuedBranch = await context.OpenStoreAsync())
        {
            var result = await context.Identity(issuedBranch, new(true, true), new(DateTimeOffset.UtcNow)).ProvisionBootstrapTokenAsync();
            Assert.True(result.Succeeded, result.ReasonCode);
            result.Token!.Dispose();
            await WaitForVerifiedAsync(issuedBranch);
        }
        var otherOptions = new ProductionStoreOptions(otherPath)
        { AuditIntegrityPolicy = context.Policy, LocalIdentity = context.IdentityOptions() };
        await using (var rejectedBranch = new SqliteCommandStore(otherOptions))
        {
            Assert.True((await rejectedBranch.Initialization).Committed);
            await WaitForVerifiedAsync(rejectedBranch);
            var result = await context.Identity(rejectedBranch, new(false, false), new(DateTimeOffset.UtcNow)).ProvisionBootstrapTokenAsync();
            Assert.False(result.Succeeded);
            Assert.Equal("PhysicalConsoleRequired", result.ReasonCode);
            await WaitForVerifiedAsync(rejectedBranch);
        }
        using (var source = Open(context.DatabasePath, true))
        using (var target = Open(otherPath))
        using (var read = source.CreateCommand())
        using (var write = target.CreateCommand())
        {
            read.CommandText = "SELECT Revision,ProtectedState,LastAuditSequence,StateSignature FROM identity_authority;";
            using var row = read.ExecuteReader();
            Assert.True(row.Read());
            write.CommandText = "SELECT Revision,LastAuditSequence FROM identity_authority;";
            using (var targetRow = write.ExecuteReader())
            {
                Assert.True(targetRow.Read());
                Assert.Equal(row.GetInt64(0), targetRow.GetInt64(0));
                Assert.Equal(row.GetInt64(2), targetRow.GetInt64(1));
            }
            write.CommandText = "UPDATE identity_authority SET ProtectedState=$state,StateSignature=$signature;";
            write.Parameters.AddWithValue("$state", row.GetString(1));
            write.Parameters.AddWithValue("$signature", row.GetString(3));
            write.ExecuteNonQuery();
        }
        var before = DatabaseHash(otherPath);
        await using (var rejected = new SqliteCommandStore(otherOptions))
        {
            Assert.False((await rejected.Initialization).Committed);
            Assert.Equal(AuditIntegrityState.Faulted, rejected.Integrity?.State);
        }
        Assert.Equal(before, DatabaseHash(otherPath));
    }

    private static BootstrapAdministratorRequest CreateAdministratorRequest(TestContext context, string token) =>
        new(context.StationId, token, "alice-104", "Alice 104", context.Password);

    private static async Task InstallHistoricalVerifierAsync(SqliteCommandStore store, string password, LocalIdentityOptions options)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var derived = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 1, HashAlgorithmName.SHA256, 32);
        var result = await store.UpdateIdentityAsync(state =>
        {
            state.Administrator!.Password = PasswordVerifierState.From(new(Pbkdf2PasswordHasher.Algorithm, 1, 1, 1,
                Convert.ToBase64String(salt), Convert.ToBase64String(derived)));
            state.Administrator.CredentialRevision++;
            return new IdentityUpdate("HistoricalTestFixture", new[] { new IdentityAuditEvent(Guid.NewGuid(),
                IdentityEventKind.PasswordVerifierUpgraded, DateTimeOffset.UtcNow, options.StationId,
                state.Administrator.PrincipalId, state.Administrator.CredentialId, null, null, "HistoricalTestFixture") });
        }, CancellationToken.None);
        CryptographicOperations.ZeroMemory(derived);
        Assert.True(result.Committed, result.ReasonCode);
    }

    private static CommandAuditFact OutcomeFact() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            AuditedCommandKind.GracefulProductionStop, CommandSource.PhysicalConsole,
            "claimed/operator", Guid.NewGuid(), Guid.NewGuid(), CommandAuditPhase.Outcome,
            CommandDisposition.Accepted, "StopAdmitted");

    private static SqliteConnection Open(string path, bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long CountRows(string path, string table)
    {
        Assert.Equal("audit_entries", table);
        using var connection = Open(path, true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_entries;";
        return (long)command.ExecuteScalar()!;
    }

    private static string DatabaseHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string ReadAuditPayloadText(string path)
    {
        using var connection = Open(path, true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM audit_entries ORDER BY Sequence;";
        using var reader = command.ExecuteReader();
        var result = new StringBuilder();
        while (reader.Read()) result.Append(Encoding.UTF8.GetString(Convert.FromBase64String(reader.GetString(0))));
        return result.ToString();
    }

    private static async Task<AuditIntegrityReport> WaitForVerifiedAsync(
        SqliteCommandStore store, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var report = store.Integrity;
            if (report is { State: AuditIntegrityState.Verified }) return report;
            if (report?.State == AuditIntegrityState.Faulted)
                throw new XunitException($"Audit integrity faulted: {report.ReasonCode}");
            await Task.Delay(25);
        }

        throw new XunitException($"Audit integrity did not become Verified. Last state: {store.Integrity?.State}, " +
            $"reason: {store.Integrity?.ReasonCode}");
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Local identity acceptance requires Windows DPAPI machine protection.");
    }

    private sealed class FakeConsoleAuthority : IPhysicalConsoleAuthority
    {
        public FakeConsoleAuthority(bool physicalConsole, bool windowsAdministrator)
        {
            PhysicalConsole = physicalConsole;
            WindowsAdministrator = windowsAdministrator;
        }

        public bool PhysicalConsole { get; set; }
        public bool WindowsAdministrator { get; set; }
        public string? WindowsSid { get; set; } = "S-1-5-21-V104-TEST";

        public ConsoleAuthority Observe() => new(PhysicalConsole, WindowsAdministrator, WindowsSid);
    }

    private sealed class MutableClock
    {
        public MutableClock(DateTimeOffset value) => UtcNow = value;
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class TestContext : IAsyncDisposable
    {
        private TestContext(string directoryPath, string databasePath, string stationId,
            string password, int recoveryCodeCount, AuditIntegrityPolicy policy)
        {
            DirectoryPath = directoryPath;
            DatabasePath = databasePath;
            StationId = stationId;
            Password = password;
            RecoveryCodeCount = recoveryCodeCount;
            Policy = policy;
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }
        public string StationId { get; }
        public string Password { get; }
        public int RecoveryCodeCount { get; }
        public TimeSpan TokenLifetime => TimeSpan.FromMinutes(15);
        public AuditIntegrityPolicy Policy { get; }

        public static TestContext Create()
        {
            var directoryPath = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V104-LocalIdentity",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            var stationId = "V104Station";
            var databasePath = Path.Combine(directoryPath, "identity.sqlite");
            var keyName = "SharpInspect.Test.LocalIdentity." + Guid.NewGuid().ToString("N");
            var policy = new AuditIntegrityPolicy(stationId, "v1", keyName)
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directoryPath, "audit-keys"),
                CheckpointEveryEntries = 2,
                VerificationInterval = TimeSpan.FromSeconds(1)
            };
            return new TestContext(directoryPath, databasePath, stationId,
                "V104 correct horse! 2026", recoveryCodeCount: 8, policy);
        }

        public LocalIdentityOptions IdentityOptions() => new(
            StationId,
            new LocalPasswordPolicy
            {
                Blocklist = PasswordBlocklist.Create(
                    "v104-test-blocklist", "v1", new[] { "known-compromised-value" })
            },
            new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development);

        public ProductionStoreOptions Options(bool identityEnabled = true) => new(DatabasePath)
        {
            AuditIntegrityPolicy = Policy,
            LocalIdentity = identityEnabled ? IdentityOptions() : null,
            CommitTimeout = TimeSpan.FromSeconds(2),
            QueryTimeout = TimeSpan.FromSeconds(2),
            QueueCapacity = 8
        };

        public async Task<SqliteCommandStore> OpenStoreAsync(bool identityEnabled = true)
        {
            var store = new SqliteCommandStore(Options(identityEnabled));
            var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
            if (!initialized.Committed)
            {
                await store.DisposeAsync();
                throw new XunitException($"Store initialization failed: {initialized.ReasonCode}");
            }

            await WaitForVerifiedAsync(store);
            return store;
        }

        public LocalIdentityService Identity(SqliteCommandStore store,
            FakeConsoleAuthority authority, MutableClock clock) =>
            new(store, IdentityOptions(), authority, () => clock.UtcNow);

        public ValueTask DisposeAsync()
        {
            try
            {
                var keyPath = WindowsMachineAuditKey.GetKeyPath(Policy);
                if (File.Exists(keyPath)) File.Delete(keyPath);
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve evidence if this host refuses cleanup of the fixture's own key.
            }
            catch (IOException)
            {
                // Preserve evidence if a test process still has the fixture key open.
            }

            return ValueTask.CompletedTask;
        }
    }
}

#pragma warning restore CA1416
