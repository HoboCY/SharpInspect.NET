using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Tests;

/// <summary>Focused V104 tamper and hasher-boundary acceptance tests.</summary>
public sealed class IdentityTamperAcceptanceTests
{
    [Fact]
    public async Task V104_S09_DpapiReprotectedVerifierWithOriginalStateSignatureCannotLogin()
    {
        RequireWindows();
        await using var fixture = Fixture.Create();
        var authority = new FixtureConsoleAuthority();

        await using (var store = await fixture.OpenStoreAsync())
        {
            var identity = new LocalIdentityService(store, fixture.IdentityOptions(), authority);
            var issued = await identity.ProvisionBootstrapTokenAsync();
            Assert.True(issued.Succeeded, issued.ReasonCode);
            await WaitForVerifiedAsync(store);
            var created = await identity.CreateFirstAdministratorAsync(new BootstrapAdministratorRequest(
                fixture.StationId, issued.Token!.TakeForDisplay(), "alice-104", "Alice 104", fixture.Password));
            Assert.True(created.Succeeded, created.ReasonCode);
            await WaitForVerifiedAsync(store);

            var original = await store.ReadIdentityAsync(CancellationToken.None);
            var before = ReadIdentityRow(fixture.DatabasePath);
            original.Administrator!.Password.Derived =
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var reprotected = IdentityStateProtection.Protect(original);
            Execute(fixture.DatabasePath,
                "UPDATE identity_authority SET ProtectedState=$protected WHERE Id=1;",
                ("$protected", reprotected));

            var after = ReadIdentityRow(fixture.DatabasePath);
            Assert.NotEqual(before.ProtectedState, after.ProtectedState);
            Assert.Equal(before.Revision, after.Revision);
            Assert.Equal(before.LastAuditSequence, after.LastAuditSequence);
            Assert.Equal(before.StateSignature, after.StateSignature);

            var login = await identity.AuthenticateAsync(
                new PasswordSignInRequest("alice-104", fixture.Password));
            Assert.False(login.Succeeded);
            Assert.Null(login.Identity);
            await WaitForVerifiedAsync(store);
        }

        await using var reopened = new SqliteCommandStore(fixture.Options());
        var restart = await reopened.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(restart.Committed);
        Assert.NotEqual("TraceStoreReady", restart.ReasonCode);
    }

    [Fact]
    public void V104_S10_WeakPasswordHasherIsRejectedBeforeDatabaseOrKeyCreation()
    {
        RequireWindows();
        using var fixture = Fixture.Create();
        var keyPath = WindowsMachineAuditKey.GetKeyPath(fixture.Policy);
        Assert.False(File.Exists(fixture.DatabasePath));
        Assert.False(File.Exists(keyPath));

        var weak = new WeakPasswordHasher();
        var exception = Record.Exception(() =>
        {
            var identity = new LocalIdentityOptions(
                fixture.StationId,
                fixture.PasswordPolicy(),
                weak, AuthenticationPolicy.Development, AuthorizationPolicy.Development);
            _ = new SqliteCommandStore(new ProductionStoreOptions(fixture.DatabasePath)
            {
                AuditIntegrityPolicy = fixture.Policy,
                LocalIdentity = identity
            });
        });

        Assert.NotNull(exception);
        Assert.Contains("IdentityHasherUnsupported", exception!.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.DatabasePath));
        Assert.False(File.Exists(keyPath));
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

    private static IdentityRow ReadIdentityRow(string databasePath)
    {
        using var connection = Open(databasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Revision,ProtectedState,LastAuditSequence,StateSignature " +
                              "FROM identity_authority WHERE Id=1;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new IdentityRow(
            reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3));
    }

    private static void Execute(string databasePath, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open(databasePath, readOnly: false);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string databasePath, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Identity tamper acceptance requires Windows DPAPI machine protection.");
    }

    private sealed record IdentityRow(long Revision, string ProtectedState, long LastAuditSequence,
        string StateSignature);

    private sealed class FixtureConsoleAuthority : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-V104-TAMPER");
    }

    private sealed class WeakPasswordHasher : IPasswordHasher
    {
        public PasswordHashRecord Hash(string normalizedPassword) =>
            new("weak", 1, 1, 1, "AA==", "AA==");

        public bool Verify(string normalizedPassword, PasswordHashRecord record) => true;

        public bool NeedsRehash(PasswordHashRecord record) => false;
    }

    private sealed class Fixture : IDisposable, IAsyncDisposable
    {
        private Fixture(string directoryPath, string databasePath, string stationId,
            string password, AuditIntegrityPolicy policy)
        {
            DirectoryPath = directoryPath;
            DatabasePath = databasePath;
            StationId = stationId;
            Password = password;
            Policy = policy;
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }
        public string StationId { get; }
        public string Password { get; }
        public AuditIntegrityPolicy Policy { get; }

        public static Fixture Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V104-IdentityTamper",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var stationId = "V104TamperStation";
            var policy = new AuditIntegrityPolicy(stationId, "v1",
                "SharpInspect.Test.IdentityTamper." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "audit-keys"),
                CheckpointEveryEntries = 2,
                VerificationInterval = TimeSpan.FromSeconds(1)
            };
            return new Fixture(directory, Path.Combine(directory, "identity.sqlite"), stationId,
                "V104 tamper password! 2026", policy);
        }

        public PasswordBlocklist Blocklist() => PasswordBlocklist.Create(
            "v104-tamper-blocklist", "v1", new[] { "known-compromised-value" });

        public LocalPasswordPolicy PasswordPolicy() => new() { Blocklist = Blocklist() };

        public LocalIdentityOptions IdentityOptions() => new(StationId, PasswordPolicy(), new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, AuthorizationPolicy.Development);

        public ProductionStoreOptions Options() => new(DatabasePath)
        {
            AuditIntegrityPolicy = Policy,
            LocalIdentity = IdentityOptions(),
            CommitTimeout = TimeSpan.FromSeconds(2),
            QueryTimeout = TimeSpan.FromSeconds(2),
            QueueCapacity = 8
        };

        public async Task<SqliteCommandStore> OpenStoreAsync()
        {
            var store = new SqliteCommandStore(Options());
            var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
            if (!initialized.Committed)
            {
                await store.DisposeAsync();
                throw new XunitException($"Store initialization failed: {initialized.ReasonCode}");
            }

            await WaitForVerifiedAsync(store);
            return store;
        }

        public void Dispose()
        {
            try
            {
                var keyPath = WindowsMachineAuditKey.GetKeyPath(Policy);
                if (File.Exists(keyPath)) File.Delete(keyPath);
            }
            catch (UnauthorizedAccessException)
            {
                // Keep evidence if the host refuses cleanup of this fixture's own key.
            }
            catch (IOException)
            {
                // Keep evidence if a test process still holds the fixture key.
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

#pragma warning restore CA1416
