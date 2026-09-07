using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Tests;

/// <summary>V106 focused coverage for independent human credentials and multi-profile authentication.</summary>
public sealed class MultiAccountAuthenticationTests
{
    [Fact]
    public async Task V106_A01_TwoPersonalCredentialsDisableIndependently()
    {
        RequireWindows();
        var policy = AuthenticationPolicy.Development with
        {
            AccountFailureLimit = 1,
            StationFailureLimit = 50
        };
        await using var fixture = TestFixture.Create(policy);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var authority = new FakeConsoleAuthority();

        await using var store = await fixture.OpenStoreAsync();
        var options = fixture.IdentityOptions();
        var identity = fixture.Identity(store, authority, clock, options);
        var bootstrapPrincipal = await fixture.BootstrapAsync(identity, store);
        var second = await fixture.AdditionalAccountAsync(store, options, "bob-106", "Bob 106",
            "V106 bob correct horse! 2026", cost: 1, saltBytes: 24, derivedBytes: 48);

        var firstFailure = await identity.AuthenticateAsync(
            new PasswordSignInRequest(fixture.UserName, "wrong-password"));
        Assert.False(firstFailure.Succeeded);
        Assert.Equal("AuthenticationRejected", firstFailure.ReasonCode);
        await WaitForVerifiedAsync(store);

        var afterFirst = await store.ReadIdentityAsync(CancellationToken.None);
        Assert.False(afterFirst.Administrator!.Enabled);
        Assert.Equal(bootstrapPrincipal, afterFirst.Administrator.PrincipalId);
        Assert.True(afterFirst.AdditionalAccounts.Single(account => account.PrincipalId == second.PrincipalId).Enabled);
        Assert.Equal(0, afterFirst.AdditionalAccounts.Single(account => account.PrincipalId == second.PrincipalId)
            .Throttle.ConsecutiveFailures);

        clock.UtcNow = LatestNextAllowed(afterFirst);
        var secondFailure = await identity.AuthenticateAsync(
            new PasswordSignInRequest(second.UserName, "wrong-password"));
        Assert.False(secondFailure.Succeeded);
        Assert.Equal("AuthenticationRejected", secondFailure.ReasonCode);
        await WaitForVerifiedAsync(store);

        var afterSecond = await store.ReadIdentityAsync(CancellationToken.None);
        var persistedAdministrator = afterSecond.Administrator!;
        Assert.False(persistedAdministrator.Enabled);
        var persistedSecond = afterSecond.AdditionalAccounts.Single(account => account.PrincipalId == second.PrincipalId);
        Assert.False(persistedSecond.Enabled);
        Assert.Equal(1, persistedAdministrator.Throttle.ConsecutiveFailures);
        Assert.Equal(1, persistedSecond.Throttle.ConsecutiveFailures);
    }

    [Fact]
    public async Task V106_A02_UnknownAndDifferentHashProfilesExecuteEquivalentWork()
    {
        RequireWindows();
        var policy = AuthenticationPolicy.Development with
        {
            AccountFailureLimit = 10,
            StationFailureLimit = 50
        };
        await using var fixture = TestFixture.Create(policy);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var authority = new FakeConsoleAuthority();

        await using var store = await fixture.OpenStoreAsync();
        var options = fixture.IdentityOptions();
        var profiles = new ProfileCollector();
        var identity = fixture.Identity(store, authority, clock, options, profiles.Add);
        await fixture.BootstrapAsync(identity, store);
        var second = await fixture.AdditionalAccountAsync(store, options, "bob-106", "Bob 106",
            "V106 bob correct horse! 2026", cost: 1, saltBytes: 24, derivedBytes: 48);

        var firstProfiles = await AuthenticateWrongAndReadProfilesAsync(identity, store, clock, profiles,
            fixture.UserName);
        var secondProfiles = await AuthenticateWrongAndReadProfilesAsync(identity, store, clock, profiles,
            second.UserName);
        var unknownProfiles = await AuthenticateWrongAndReadProfilesAsync(identity, store, clock, profiles,
            "unknown-106");

        Assert.Equal(firstProfiles, secondProfiles);
        Assert.Equal(secondProfiles, unknownProfiles);
        Assert.Equal(new[]
        {
            new ObservedProfile(1, 24, 48),
            new ObservedProfile(PasswordHashBaseline.SecurityFloorIterations, 16, 32)
        }, firstProfiles);
    }

    [Fact]
    public async Task V106_A03_AdditionalCredentialIdentityAndThrottlePersistAcrossRestart()
    {
        RequireWindows();
        await using var fixture = TestFixture.Create();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var authority = new FakeConsoleAuthority();
        Guid firstPrincipal;
        Guid secondPrincipal;
        Guid secondCredential;
        long secondCredentialRevision;

        await using (var initial = await fixture.OpenStoreAsync())
        {
            var options = fixture.IdentityOptions();
            var identity = fixture.Identity(initial, authority, clock, options);
            firstPrincipal = await fixture.BootstrapAsync(identity, initial);
            var second = await fixture.AdditionalAccountAsync(initial, options, "bob-106", "Bob 106",
                "V106 bob correct horse! 2026", cost: 1, saltBytes: 24, derivedBytes: 48);
            secondPrincipal = second.PrincipalId;

            var failed = await identity.AuthenticateAsync(new PasswordSignInRequest(second.UserName, "wrong-password"));
            Assert.False(failed.Succeeded);
            await WaitForVerifiedAsync(initial);
            var beforeRestart = await initial.ReadIdentityAsync(CancellationToken.None);
            var persisted = beforeRestart.AdditionalAccounts.Single(account => account.PrincipalId == secondPrincipal);
            secondCredential = persisted.CredentialId;
            secondCredentialRevision = persisted.CredentialRevision;
        }

        await using (var restarted = await fixture.OpenStoreAsync())
        {
            var options = fixture.IdentityOptions();
            var identity = fixture.Identity(restarted, authority, clock, options);
            var state = await restarted.ReadIdentityAsync(CancellationToken.None);
            Assert.Equal(firstPrincipal, state.Administrator!.PrincipalId);
            var second = state.AdditionalAccounts.Single(account => account.PrincipalId == secondPrincipal);
            Assert.Equal(secondCredential, second.CredentialId);
            Assert.Equal(secondCredentialRevision, second.CredentialRevision);

            clock.UtcNow = LatestNextAllowed(state);
            var authenticated = await identity.AuthenticateAsync(
                new PasswordSignInRequest(second.UserName, "V106 bob correct horse! 2026"));
            Assert.True(authenticated.Succeeded, authenticated.ReasonCode);
            Assert.Equal(secondPrincipal, authenticated.Identity!.PrincipalId);
            await WaitForVerifiedAsync(restarted);
        }
    }

    [Fact]
    public async Task V106_A04_SessionStartRecognizesAdditionalEnabledCredential()
    {
        RequireWindows();
        await using var fixture = TestFixture.Create();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var authority = new FakeConsoleAuthority();

        await using var store = await fixture.OpenStoreAsync();
        var options = fixture.IdentityOptions();
        var identity = fixture.Identity(store, authority, clock, options);
        await fixture.BootstrapAsync(identity, store);
        var second = await fixture.AdditionalAccountAsync(store, options, "bob-106", "Bob 106",
            "V106 bob correct horse! 2026", cost: 1, saltBytes: 24, derivedBytes: 48);

        var persisted = await identity.PersistSessionEventAsync(
            new SessionAuditEvent(second.PrincipalId, Guid.NewGuid(), "SessionStarted", "Authenticated"),
            CancellationToken.None);
        Assert.True(persisted);
        await WaitForVerifiedAsync(store);
    }

    private static async Task<IReadOnlyList<ObservedProfile>> AuthenticateWrongAndReadProfilesAsync(
        LocalIdentityService identity, SqliteCommandStore store, MutableClock clock,
        ProfileCollector profiles, string userName)
    {
        profiles.Clear();
        var result = await identity.AuthenticateAsync(new PasswordSignInRequest(userName, "wrong-password"));
        Assert.False(result.Succeeded);
        Assert.Equal("AuthenticationRejected", result.ReasonCode);
        var observed = profiles.Snapshot();
        await WaitForVerifiedAsync(store);
        var state = await store.ReadIdentityAsync(CancellationToken.None);
        clock.UtcNow = LatestNextAllowed(state);
        return observed;
    }

    private static DateTimeOffset LatestNextAllowed(IdentityAuthorityState state)
    {
        var latest = state.StationThrottle.NextAllowedAtUtc;
        foreach (var account in state.EnumerateAccounts())
            if (account.Throttle.NextAllowedAtUtc > latest) latest = account.Throttle.NextAllowedAtUtc;
        if (state.UnknownAccountThrottle.NextAllowedAtUtc > latest)
            latest = state.UnknownAccountThrottle.NextAllowedAtUtc;
        return latest;
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Local identity acceptance requires Windows DPAPI machine protection.");
    }

    private static async Task<AuditIntegrityReport> WaitForVerifiedAsync(
        SqliteCommandStore store, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            var report = store.Integrity;
            if (report is { State: AuditIntegrityState.Verified }) return report;
            if (report?.State == AuditIntegrityState.Faulted)
                throw new XunitException($"Audit integrity faulted: {report.ReasonCode}");
            await Task.Delay(20);
        }

        throw new XunitException($"Audit integrity did not become Verified. Last state: {store.Integrity?.State}, " +
            $"reason: {store.Integrity?.ReasonCode}");
    }

    private readonly record struct ObservedProfile(int Cost, int SaltBytes, int OutputBytes);

    private sealed class ProfileCollector
    {
        private readonly object _gate = new();
        private readonly List<ObservedProfile> _profiles = new();

        public void Add(PasswordVerificationWork work)
        {
            lock (_gate) _profiles.Add(new ObservedProfile(work.Cost, work.SaltBytes, work.OutputBytes));
        }

        public void Clear()
        {
            lock (_gate) _profiles.Clear();
        }

        public ObservedProfile[] Snapshot()
        {
            lock (_gate) return _profiles.ToArray();
        }
    }

    private sealed class FakeConsoleAuthority : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-V106-TEST");
    }

    private sealed class MutableClock
    {
        public MutableClock(DateTimeOffset utcNow) => UtcNow = utcNow;
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private TestFixture(string directoryPath, string databasePath, string stationId,
            string password, AuditIntegrityPolicy auditPolicy, AuthenticationPolicy authenticationPolicy)
        {
            DirectoryPath = directoryPath;
            DatabasePath = databasePath;
            StationId = stationId;
            Password = password;
            AuditPolicy = auditPolicy;
            AuthenticationPolicy = authenticationPolicy;
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }
        public string StationId { get; }
        public string Password { get; }
        public AuditIntegrityPolicy AuditPolicy { get; }
        public AuthenticationPolicy AuthenticationPolicy { get; }
        public string UserName => "alice-106";

        public static TestFixture Create(AuthenticationPolicy? authenticationPolicy = null)
        {
            var directoryPath = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V106-MultiAccount",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            var stationId = "V106Station";
            var auditPolicy = new AuditIntegrityPolicy(stationId, "v1",
                "SharpInspect.Test.V106." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directoryPath, "audit-keys"),
                CheckpointEveryEntries = 2,
                VerificationInterval = TimeSpan.FromSeconds(1)
            };
            return new TestFixture(directoryPath, Path.Combine(directoryPath, "identity.sqlite"), stationId,
                "V106 alice correct horse! 2026", auditPolicy,
                authenticationPolicy ?? AuthenticationPolicy.Development);
        }

        public LocalIdentityOptions IdentityOptions(Pbkdf2PasswordHasher? hasher = null) => new(
            StationId,
            new LocalPasswordPolicy
            {
                Blocklist = PasswordBlocklist.Create(
                    "v106-test-blocklist", "v1", new[] { "known-compromised-value" })
            },
            hasher ?? new Pbkdf2PasswordHasher(), AuthenticationPolicy,
            AuthorizationPolicy.Development);

        public ProductionStoreOptions StoreOptions(LocalIdentityOptions? identity = null) => new(DatabasePath)
        {
            AuditIntegrityPolicy = AuditPolicy,
            LocalIdentity = identity ?? IdentityOptions(),
            CommitTimeout = TimeSpan.FromSeconds(2),
            QueryTimeout = TimeSpan.FromSeconds(2),
            QueueCapacity = 8
        };

        public async Task<SqliteCommandStore> OpenStoreAsync(LocalIdentityOptions? identity = null)
        {
            var store = new SqliteCommandStore(StoreOptions(identity));
            var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            if (!initialized.Committed)
            {
                await store.DisposeAsync();
                throw new XunitException($"Store initialization failed: {initialized.ReasonCode}");
            }

            await WaitForVerifiedAsync(store);
            return store;
        }

        public LocalIdentityService Identity(SqliteCommandStore store, FakeConsoleAuthority authority,
            MutableClock clock, LocalIdentityOptions options,
            Action<PasswordVerificationWork>? verificationObserver = null) =>
            new(store, options, authority, () => clock.UtcNow, verificationObserver);

        public async Task<Guid> BootstrapAsync(LocalIdentityService identity, SqliteCommandStore store)
        {
            var issued = await identity.ProvisionBootstrapTokenAsync();
            Assert.True(issued.Succeeded, issued.ReasonCode);
            var token = issued.Token!.TakeForDisplay();
            await WaitForVerifiedAsync(store);
            var created = await identity.CreateFirstAdministratorAsync(new BootstrapAdministratorRequest(
                StationId, token, UserName, "Alice 106", Password));
            Assert.True(created.Succeeded, created.ReasonCode);
            var principal = created.Identity!.PrincipalId;
            created.RecoveryKit?.Dispose();
            await WaitForVerifiedAsync(store);
            return principal;
        }

        public async Task<LocalAdministratorState> AdditionalAccountAsync(SqliteCommandStore store,
            LocalIdentityOptions options, string userName, string displayName, string password,
            int cost, int saltBytes, int derivedBytes)
        {
            var credentialId = Guid.NewGuid();
            var account = new LocalAdministratorState
            {
                PrincipalId = Guid.NewGuid(),
                UserName = userName,
                UserNameKey = userName.ToUpperInvariant(),
                DisplayName = displayName,
                CredentialId = credentialId,
                CredentialRevision = 1,
                AuthorizationRevision = 1,
                RoleBundle = HumanRoleBundle.Operator,
                Permissions = options.AuthorizationPolicy.GetPermissions(HumanRoleBundle.Operator).ToList(),
                Password = PasswordVerifierState.From(Hash(password, cost, saltBytes, derivedBytes))
            };
            var result = await store.UpdateIdentityAsync(state =>
            {
                state.AdditionalAccounts.Add(account);
                return new IdentityUpdate("V106AdditionalAccountSeeded", new[]
                {
                    new IdentityAuditEvent(Guid.NewGuid(), IdentityEventKind.AdministratorCreated,
                        DateTimeOffset.UtcNow, state.StationId, account.PrincipalId, account.CredentialId,
                        null, null, "V106AdditionalAccountSeeded")
                });
            }, CancellationToken.None);
            Assert.True(result.Committed, result.ReasonCode);
            await WaitForVerifiedAsync(store);
            return account;
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                var keyPath = WindowsMachineAuditKey.GetKeyPath(AuditPolicy);
                if (File.Exists(keyPath)) File.Delete(keyPath);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            return ValueTask.CompletedTask;
        }

        private static PasswordHashRecord Hash(string password, int cost, int saltBytes, int derivedBytes)
        {
            var salt = RandomNumberGenerator.GetBytes(saltBytes);
            var passwordBytes = Encoding.UTF8.GetBytes(password);
            byte[]? derived = null;
            try
            {
                derived = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, cost,
                    HashAlgorithmName.SHA256, derivedBytes);
                return new PasswordHashRecord(Pbkdf2PasswordHasher.Algorithm,
                    Pbkdf2PasswordHasher.FormatVersion, Pbkdf2PasswordHasher.ParameterVersion, cost,
                    Convert.ToBase64String(salt), Convert.ToBase64String(derived));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(passwordBytes);
                if (derived is not null) CryptographicOperations.ZeroMemory(derived);
            }
        }
    }
}

#pragma warning restore CA1416
