using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Tests;

public sealed class AuthorizationStorageTests
{
    [Fact]
    public async Task V106_S01_Schema5InitializationAndBootstrapAreVerifiable()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Schema5 identity storage uses Windows machine protection.");

        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V106-S01-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "identity.sqlite");
        var policy = new AuditIntegrityPolicy("V106StorageStation", "v1", "SharpInspect.Test.V106." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            KeyDirectory = Path.Combine(directory, "keys"),
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1)
        };
        var identityOptions = new LocalIdentityOptions("V106StorageStation",
            new LocalPasswordPolicy { Blocklist = PasswordBlocklist.Create("v106-blocklist", "v1", new[] { "known-compromised" }) },
            new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, AuthorizationPolicy.Development);
        var options = new ProductionStoreOptions(databasePath)
        {
            AuditIntegrityPolicy = policy,
            LocalIdentity = identityOptions,
            CommitTimeout = TimeSpan.FromSeconds(2),
            QueryTimeout = TimeSpan.FromSeconds(2),
            QueueCapacity = 8
        };

        var store = new SqliteCommandStore(options);
        try
        {
            var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await WaitForVerifiedAsync(store);

            using (var connection = Open(databasePath, readOnly: true))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version;";
                Assert.Equal(5L, Convert.ToInt64(command.ExecuteScalar()));
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('identity_authority','identity_policy_binding');";
                Assert.Equal(2L, Convert.ToInt64(command.ExecuteScalar()));
            }

            var authority = new FakeConsoleAuthority();
            var identity = new LocalIdentityService(store, identityOptions, authority,
                () => DateTimeOffset.UtcNow);
            var status = await identity.GetStatusAsync();
            Assert.True(status.BootstrapRequired);

            var issued = await identity.ProvisionBootstrapTokenAsync();
            Assert.True(issued.Succeeded, issued.ReasonCode);
            var token = issued.Token!.TakeForDisplay();
            await WaitForVerifiedAsync(store);
            var created = await identity.CreateFirstAdministratorAsync(new BootstrapAdministratorRequest(
                identityOptions.StationId, token, "alice-v106", "Alice V106", "V106 schema5 correct horse!"));
            Assert.True(created.Succeeded, created.ReasonCode);
            created.RecoveryKit?.Dispose();
            await WaitForVerifiedAsync(store);

            var report = await new SqliteAuditIntegrityQuery(options).VerifyAsync(new AuditVerificationRequest(0, 200));
            Assert.Equal(AuditIntegrityState.Verified, report.State);
            Assert.Equal(report.ThroughSequence, report.VerifiedThroughSequence);
        }
        finally
        {
            await store.DisposeAsync();
            try
            {
                var keyPath = WindowsMachineAuditKey.GetKeyPath(policy);
                if (File.Exists(keyPath)) File.Delete(keyPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task V106_S02_Schema5DowngradeIsReadOnlyRejectedWithoutSideEffects()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Schema5 identity storage uses Windows machine protection.");

        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V106-S02-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "identity.sqlite");
        var policy = new AuditIntegrityPolicy("V106StorageStation", "v1", "SharpInspect.Test.V106." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            KeyDirectory = Path.Combine(directory, "keys"),
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1)
        };
        var identityOptions = new LocalIdentityOptions("V106StorageStation",
            new LocalPasswordPolicy { Blocklist = PasswordBlocklist.Create("v106-blocklist", "v1", new[] { "known-compromised" }) },
            new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, AuthorizationPolicy.Development);
        var options = new ProductionStoreOptions(databasePath)
        {
            AuditIntegrityPolicy = policy,
            LocalIdentity = identityOptions,
            CommitTimeout = TimeSpan.FromSeconds(2),
            QueryTimeout = TimeSpan.FromSeconds(2),
            QueueCapacity = 8
        };

        try
        {
            await using (var initial = new SqliteCommandStore(options))
            {
                var initialized = await initial.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(initial);
            }

            var keyPath = WindowsMachineAuditKey.GetKeyPath(policy);
            Assert.True(File.Exists(keyPath));
            using (var connection = Open(databasePath, readOnly: false))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version=4;";
                command.ExecuteNonQuery();
            }

            var databaseBefore = FileSha256(databasePath);
            var keyBefore = FileSha256(keyPath);
            await using (var rejected = new SqliteCommandStore(options))
            {
                var result = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(result.Committed);
                Assert.Equal("IdentityAuthorizationGovernedMigrationRequired", result.ReasonCode);
            }

            var report = await new SqliteAuditIntegrityQuery(options)
                .VerifyAsync(new AuditVerificationRequest(0, 200));
            Assert.Equal(AuditIntegrityState.Faulted, report.State);
            Assert.Equal("IdentityAuthorizationGovernedMigrationRequired", report.ReasonCode);
            Assert.Equal(4L, ReadUserVersion(databasePath));
            Assert.Equal(databaseBefore, FileSha256(databasePath));
            Assert.Equal(keyBefore, FileSha256(keyPath));
        }
        finally
        {
            try
            {
                var keyPath = WindowsMachineAuditKey.GetKeyPath(policy);
                if (File.Exists(keyPath)) File.Delete(keyPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task WaitForVerifiedAsync(SqliteCommandStore store)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (store.Integrity is { State: AuditIntegrityState.Verified }) return;
            if (store.Integrity is { State: AuditIntegrityState.Faulted } fault)
                throw new XunitException(fault.ReasonCode);
            await Task.Delay(25);
        }

        throw new XunitException($"Audit integrity did not become Verified: {store.Integrity?.ReasonCode}");
    }

    private static SqliteConnection Open(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long ReadUserVersion(string path)
    {
        using var connection = Open(path, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string FileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class FakeConsoleAuthority : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-V106-S01");
    }
}

#pragma warning restore CA1416
