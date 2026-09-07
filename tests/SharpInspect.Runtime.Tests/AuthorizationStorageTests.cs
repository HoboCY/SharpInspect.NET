using Microsoft.Data.Sqlite;
using System.Globalization;
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
    public async Task V106_S01_Schema7InitializationAndBootstrapAreVerifiable()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Schema6 identity storage uses Windows machine protection.");

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
                Assert.Equal(7L, Convert.ToInt64(command.ExecuteScalar()));
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
                identityOptions.StationId, token, "alice-v106", "Alice V106", "V106 schema6 correct horse!"));
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
    public async Task V106_S02_Schema6DowngradeIsReadOnlyRejectedWithoutSideEffects()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Schema6 identity storage uses Windows machine protection.");

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

    [Fact]
    public async Task V106_S03_Schema5IdentityStoreIsReadOnlyRejectedWithoutSideEffects()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Schema5 identity storage uses Windows machine protection.");

        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V106-S03-" + Guid.NewGuid().ToString("N"));
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
            AuditIntegrityPolicy = policy, LocalIdentity = identityOptions,
            CommitTimeout = TimeSpan.FromSeconds(2), QueryTimeout = TimeSpan.FromSeconds(2), QueueCapacity = 8
        };

        try
        {
            await using (var initial = new SqliteCommandStore(options))
            {
                var initialized = await initial.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(initial);
            }
            using (var connection = Open(databasePath, readOnly: false))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version=5;";
                command.ExecuteNonQuery();
            }

            var databaseBefore = FileSha256(databasePath);
            var keyPath = WindowsMachineAuditKey.GetKeyPath(policy);
            var keyBefore = FileSha256(keyPath);
            await using (var rejected = new SqliteCommandStore(options))
            {
                var result = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(result.Committed);
                Assert.Equal("IdentityRecoveryGovernedMigrationRequired", result.ReasonCode);
            }
            var report = await new SqliteAuditIntegrityQuery(options)
                .VerifyAsync(new AuditVerificationRequest(0, 200));
            Assert.Equal(AuditIntegrityState.Faulted, report.State);
            Assert.Equal("IdentityRecoveryGovernedMigrationRequired", report.ReasonCode);
            Assert.Equal(5L, ReadUserVersion(databasePath));
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

    [Fact]
    public async Task V106_S04_Schema6IdentityStoreIsReadOnlyRejectedForAlarmGovernance()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Schema7 identity storage uses Windows machine protection.");

        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V106-S04-" + Guid.NewGuid().ToString("N"));
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

            using (var connection = Open(databasePath, readOnly: false))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version=6;";
                command.ExecuteNonQuery();
            }

            var databaseBefore = FileSha256(databasePath);
            var keyPath = WindowsMachineAuditKey.GetKeyPath(policy);
            var keyBefore = FileSha256(keyPath);
            await using (var rejected = new SqliteCommandStore(options))
            {
                var result = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(result.Committed);
                Assert.Equal("GovernedAlarmMigrationRequired", result.ReasonCode);
            }

            var report = await new SqliteAuditIntegrityQuery(options)
                .VerifyAsync(new AuditVerificationRequest(0, 200));
            Assert.Equal(AuditIntegrityState.Faulted, report.State);
            Assert.Equal("GovernedAlarmMigrationRequired", report.ReasonCode);
            Assert.Equal(6L, ReadUserVersion(databasePath));
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

    [Fact]
    public async Task V108_S01_DeletingDurableRecoveryOperationIsRejectedBySealedIndexRoot()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Schema6 identity storage uses Windows machine protection.");

        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V108-S01-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var fixture = CreateSchema6Fixture(directory);
        try
        {
            Guid operationId;
            await using (var store = new SqliteCommandStore(fixture.Options))
            {
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(store);
                operationId = await AppendRecoveryOperationAsync(store);
            }

            using (var connection = Open(fixture.DatabasePath, readOnly: false))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DROP TRIGGER recovery_operations_immutable_delete; DELETE FROM recovery_operations WHERE OperationId=$id;";
                command.Parameters.AddWithValue("$id", operationId.ToString("D"));
                command.ExecuteNonQuery();
                command.Parameters.Clear();
                command.CommandText = "CREATE TRIGGER recovery_operations_immutable_delete BEFORE DELETE ON recovery_operations BEGIN SELECT RAISE(ABORT,'ImmutableRecoveryOperation'); END;";
                command.ExecuteNonQuery();
            }

            await using var rejected = new SqliteCommandStore(fixture.Options);
            var result = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.Committed);
            Assert.Equal("RecoveryOperationIndexInvalid", result.ReasonCode);
        }
        finally
        {
            DeleteMachineKey(fixture.Policy);
        }
    }

    [Fact]
    public async Task V108_S02_RehashedDurableRecoveryOperationReplacementIsRejectedBySealedIndexRoot()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Schema6 identity storage uses Windows machine protection.");

        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V108-S02-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var fixture = CreateSchema6Fixture(directory);
        try
        {
            Guid operationId;
            await using (var store = new SqliteCommandStore(fixture.Options))
            {
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(store);
                operationId = await AppendRecoveryOperationAsync(store);
            }

            string reasonCode, kitId, principalId, deliveryCommitted, stateRevision, identitySequence, auditHash;
            using (var connection = Open(fixture.DatabasePath, readOnly: true))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT ReasonCode,COALESCE(KitId,''),COALESCE(PrincipalId,''),
                    DeliveryCommitted,StateRevision,IdentitySequence,AuditHash
                    FROM recovery_operations WHERE OperationId=$id;";
                command.Parameters.AddWithValue("$id", operationId.ToString("D"));
                using var reader = command.ExecuteReader();
                Assert.True(reader.Read());
                reasonCode = reader.GetString(0); kitId = reader.GetString(1); principalId = reader.GetString(2);
                deliveryCommitted = reader.GetInt64(3).ToString(CultureInfo.InvariantCulture);
                stateRevision = reader.GetInt64(4).ToString(CultureInfo.InvariantCulture);
                identitySequence = reader.GetInt64(5).ToString(CultureInfo.InvariantCulture);
                auditHash = reader.GetString(6);
            }
            var replacementKind = "TamperedRecoveryOperation";
            var replacementHash = Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode(
                "RecoveryOperationIndex", operationId.ToString("D"), replacementKind, "1", reasonCode,
                string.IsNullOrEmpty(kitId) ? null : kitId,
                string.IsNullOrEmpty(principalId) ? null : principalId, deliveryCommitted,
                stateRevision, identitySequence, auditHash)));
            using (var connection = Open(fixture.DatabasePath, readOnly: false))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"DROP TRIGGER recovery_operations_immutable_update;
                    UPDATE recovery_operations SET Kind=$kind,RecordHash=$hash WHERE OperationId=$id;";
                command.Parameters.AddWithValue("$kind", replacementKind);
                command.Parameters.AddWithValue("$hash", replacementHash);
                command.Parameters.AddWithValue("$id", operationId.ToString("D"));
                command.ExecuteNonQuery();
                command.Parameters.Clear();
                command.CommandText = "CREATE TRIGGER recovery_operations_immutable_update BEFORE UPDATE ON recovery_operations BEGIN SELECT RAISE(ABORT,'ImmutableRecoveryOperation'); END;";
                command.ExecuteNonQuery();
            }

            await using var rejected = new SqliteCommandStore(fixture.Options);
            var result = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.Committed);
            Assert.Equal("RecoveryOperationIndexInvalid", result.ReasonCode);
        }
        finally
        {
            DeleteMachineKey(fixture.Policy);
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

    private static (ProductionStoreOptions Options, AuditIntegrityPolicy Policy, string DatabasePath)
        CreateSchema6Fixture(string directory)
    {
        var databasePath = Path.Combine(directory, "identity.sqlite");
        var policy = new AuditIntegrityPolicy("V108StorageStation", "v1", "SharpInspect.Test.V108." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            KeyDirectory = Path.Combine(directory, "keys"),
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1)
        };
        var identityOptions = new LocalIdentityOptions("V108StorageStation",
            new LocalPasswordPolicy { Blocklist = PasswordBlocklist.Create("v108-blocklist", "v1", new[] { "known-compromised" }) },
            new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, AuthorizationPolicy.Development);
        return (new ProductionStoreOptions(databasePath)
        {
            AuditIntegrityPolicy = policy,
            LocalIdentity = identityOptions,
            CommitTimeout = TimeSpan.FromSeconds(2),
            QueryTimeout = TimeSpan.FromSeconds(2),
            QueueCapacity = 8
        }, policy, databasePath);
    }

    private static async Task<Guid> AppendRecoveryOperationAsync(SqliteCommandStore store)
    {
        var operationId = Guid.NewGuid();
        var result = await store.UpdateRecoveryIdentityAsync(operationId, (state, previous) =>
        {
            Assert.Null(previous);
            const string reason = "RecoveryOperationTest";
            var fact = new IdentityAuditEvent(Guid.NewGuid(), IdentityEventKind.RecoveryKitRotated,
                DateTimeOffset.UtcNow, state.StationId, null, null, null, null, reason,
                OperationId: operationId);
            return new IdentityUpdate(new object(), new[] { fact },
                CompletedRecoveryOperation: new RecoveryOperationState
                {
                    OperationId = operationId, Kind = "TestRecoveryOperation", Succeeded = true,
                    ReasonCode = reason
                });
        }, CancellationToken.None);
        Assert.True(result.Committed, result.ReasonCode);
        await WaitForVerifiedAsync(store);
        return operationId;
    }

    private static void DeleteMachineKey(AuditIntegrityPolicy policy)
    {
        try
        {
            var keyPath = WindowsMachineAuditKey.GetKeyPath(policy);
            if (File.Exists(keyPath)) File.Delete(keyPath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class FakeConsoleAuthority : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-V106-S01");
    }
}

#pragma warning restore CA1416
