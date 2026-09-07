using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SharpInspect.Runtime.Conformance;
using Xunit;
using Xunit.Sdk;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Tests;

public sealed class ConformanceLedgerTests
{
    [Fact]
    public void V107_S01_AppendReadAndReopenPreserveOpaqueEntriesAndHashes()
    {
        RequireWindows();
        using var fixture = TestFixture.Create();
        var items = new[]
        {
            new ConformanceLedgerItem("profile", "profile-107", "{\"ProfileId\":\"profile-107\"}"),
            new ConformanceLedgerItem("candidate", "candidate-107", "candidate bytes"),
            new ConformanceLedgerItem("context", "context-107", "context bytes"),
            new ConformanceLedgerItem("reservation", "reservation-107", "reserved"),
            new ConformanceLedgerItem("result", "result-107", "Pass")
        };
        using (var ledger = new ConformanceLedger(fixture.Options()))
        {
            ledger.Append(items);
            var rows = ledger.ReadAll();
            Assert.Equal(items.Length, rows.Count);
            Assert.Equal(items.Select(item => item.Kind), rows.Select(row => row.Kind));
            Assert.Equal(items.Select(item => item.Id), rows.Select(row => row.Id));
            Assert.Equal(items.Select(item => item.Payload), rows.Select(row => row.Payload));
            Assert.Equal(new string('0', 64), rows[0].PreviousHash);
            Assert.All(rows, row => Assert.Matches("^[0-9A-F]{64}$", row.Sha256));
        }

        using var reopened = new ConformanceLedger(fixture.Options());
        var persisted = reopened.ReadAll();
        Assert.Equal(items.Length, persisted.Count);
        Assert.Equal(items.Select(item => item.Payload), persisted.Select(row => row.Payload));
        Assert.Equal(persisted[0].Sha256, SHA256.HashData(Encoding.UTF8.GetBytes(persisted[0].Payload)).ToHex());
    }

    [Fact]
    public void V107_S02_EntriesAreImmutableAndKindIdIsUnique()
    {
        RequireWindows();
        using var fixture = TestFixture.Create();
        using var ledger = new ConformanceLedger(fixture.Options());
        ledger.Append(new[] { new ConformanceLedgerItem("result", "r-107", "first") });

        Assert.Throws<InvalidOperationException>(() => ledger.Append(
            new[] { new ConformanceLedgerItem("result", "r-107", "replacement") }));
        using (var connection = fixture.OpenConnection())
        {
            Assert.Throws<SqliteException>(() => fixture.Execute(connection,
                "UPDATE conformance_ledger_entries SET Payload='changed' WHERE Sequence=1;"));
            Assert.Throws<SqliteException>(() => fixture.Execute(connection,
                "DELETE FROM conformance_ledger_entries WHERE Sequence=1;"));
        }

        Assert.Single(ledger.ReadAll());
        Assert.Throws<ArgumentException>(() => ledger.Append(
            new[] { new ConformanceLedgerItem("unsafe/kind", "id", "x") }));
        Assert.Throws<ArgumentException>(() => ledger.Append(
            new[] { new ConformanceLedgerItem("result", "id with spaces", "x") }));
    }

    [Fact]
    public void V107_S03_ReadOnlyLedgerReadsWithoutCreatingOrModifyingFiles()
    {
        RequireWindows();
        using var fixture = TestFixture.Create();
        using (var writer = new ConformanceLedger(fixture.Options()))
            writer.Append(new[] { new ConformanceLedgerItem("result", "readonly-107", "stable") });

        var databaseHash = HashFile(fixture.DatabasePath);
        var anchorHash = HashFile(fixture.AnchorPath);
        using (var reader = new ConformanceLedger(fixture.Options(), readOnly: true))
        {
            Assert.Single(reader.ReadAll());
            Assert.Throws<InvalidOperationException>(() => reader.Append(
                new[] { new ConformanceLedgerItem("result", "rejected", "write") }));
        }

        Assert.Equal(databaseHash, HashFile(fixture.DatabasePath));
        Assert.Equal(anchorHash, HashFile(fixture.AnchorPath));

        using var missing = TestFixture.Create();
        var missingDatabase = missing.DatabasePath;
        Assert.ThrowsAny<Exception>(() => new ConformanceLedger(missing.Options(), readOnly: true));
        Assert.False(File.Exists(missingDatabase));
    }

    [Fact]
    public void V107_S04_TamperedPayloadIsRejectedOnReopen()
    {
        RequireWindows();
        using var fixture = TestFixture.Create();
        using (var ledger = new ConformanceLedger(fixture.Options()))
            ledger.Append(new[] { new ConformanceLedgerItem("result", "tamper-107", "original") });

        using (var connection = fixture.OpenConnection())
        {
            fixture.Execute(connection, "DROP TRIGGER conformance_ledger_entries_immutable_update;");
            fixture.Execute(connection, "UPDATE conformance_ledger_entries SET Payload='tampered' WHERE Sequence=1;");
            fixture.CreateEntryUpdateTrigger(connection);
        }

        var error = Assert.Throws<InvalidOperationException>(() => new ConformanceLedger(fixture.Options()));
        Assert.Equal("ConformanceLedgerPayloadHashMismatch", error.Message);
    }

    [Fact]
    public void V107_S05_DeletedTailAndForgedAnchorCannotBeAccepted()
    {
        RequireWindows();
        using var fixture = TestFixture.Create();
        using (var ledger = new ConformanceLedger(fixture.Options()))
            ledger.Append(new[]
            {
                new ConformanceLedgerItem("result", "tail-1", "one"),
                new ConformanceLedgerItem("result", "tail-2", "two")
            });

        using (var connection = fixture.OpenConnection())
        {
            fixture.Execute(connection, "DROP TRIGGER conformance_ledger_entries_immutable_delete;");
            fixture.Execute(connection, "DELETE FROM conformance_ledger_entries WHERE Sequence=2;");
            fixture.CreateEntryDeleteTrigger(connection);
        }

        var error = Assert.Throws<InvalidOperationException>(() => new ConformanceLedger(fixture.Options()));
        Assert.Contains(error.Message, new[] { "ConformanceLedgerAnchorMismatch", "ConformanceLedgerChainInvalid" });
    }

    [Fact]
    public void V107_S06_WrongApplicationSchemaAndMissingKeyRemainRejected()
    {
        RequireWindows();
        using (var fixture = TestFixture.Create())
        {
            using (var ledger = new ConformanceLedger(fixture.Options()))
                ledger.Append(new[] { new ConformanceLedgerItem("result", "schema-107", "value") });
            using (var connection = fixture.OpenConnection()) fixture.Execute(connection, "PRAGMA application_id=17;");
            var error = Assert.Throws<InvalidOperationException>(() => new ConformanceLedger(fixture.Options()));
            Assert.Equal("ConformanceLedgerApplicationIdMismatch", error.Message);
        }

        using (var fixture = TestFixture.Create())
        {
            using (var ledger = new ConformanceLedger(fixture.Options()))
                ledger.Append(new[] { new ConformanceLedgerItem("result", "version-107", "value") });
            using (var connection = fixture.OpenConnection()) fixture.Execute(connection, "PRAGMA user_version=99;");
            var error = Assert.Throws<InvalidOperationException>(() => new ConformanceLedger(fixture.Options()));
            Assert.Equal("ConformanceLedgerSchemaVersionUnsupported", error.Message);
        }

        using var missingKey = TestFixture.Create();
        using (var ledger = new ConformanceLedger(missingKey.Options()))
            ledger.Append(new[] { new ConformanceLedgerItem("result", "key-107", "value") });
        var key = Directory.GetFiles(missingKey.KeyDirectory, "*.key").Single();
        File.Delete(key);
        var missingError = Assert.Throws<InvalidOperationException>(() => new ConformanceLedger(missingKey.Options()));
        Assert.Contains("AuditSigningKey", missingError.Message);
    }

    [Fact]
    public void V107_S07_CapacityPayloadAndArtifactLimitsAreEnforcedBeforeCommit()
    {
        RequireWindows();
        using var fixture = TestFixture.Create(maxEntries: 2, maxTotalBytes: 10);
        using var ledger = new ConformanceLedger(fixture.Options());
        ledger.Append(new[] { new ConformanceLedgerItem("result", "capacity-1", "12345") });
        Assert.Throws<InvalidOperationException>(() => ledger.Append(
            new[] { new ConformanceLedgerItem("result", "capacity-2", "123456") }));
        Assert.Single(ledger.ReadAll());
        Assert.Throws<InvalidOperationException>(() => ledger.Append(new[]
        {
            new ConformanceLedgerItem("result", "capacity-2", "12345"),
            new ConformanceLedgerItem("result", "capacity-3", "1")
        }));

        Assert.Throws<ArgumentException>(() => ledger.Append(new[]
        {
            new ConformanceLedgerItem("result", "large", new string('x', 48 * 1024 + 1))
        }));
        Assert.Throws<ArgumentException>(() => ledger.Append(new[]
        {
            new ConformanceLedgerItem("artifact", new string('A', 64), "not-json")
        }));
        var oversizedBytes = new byte[32 * 1024 + 1];
        var oversizedArtifact = JsonSerializer.Serialize(new
        {
            Length = oversizedBytes.Length,
            BytesBase64 = Convert.ToBase64String(oversizedBytes),
            Classification = "PublicTestData",
            ContentType = "application/octet-stream"
        });
        var oversizedId = Convert.ToHexString(SHA256.HashData(oversizedBytes));
        Assert.Throws<ArgumentException>(() => ledger.Append(new[]
        {
            new ConformanceLedgerItem("artifact", oversizedId, oversizedArtifact)
        }));
    }

    [Fact]
    public void V107_S08_OneBatchRollsBackAfterEarlierInsertWhenLaterIdConflicts()
    {
        RequireWindows();
        using var fixture = TestFixture.Create();
        using var ledger = new ConformanceLedger(fixture.Options());
        ledger.Append(new[] { new ConformanceLedgerItem("result", "atomic-existing", "original") });
        var anchorHash = HashFile(fixture.AnchorPath);

        var error = Assert.Throws<InvalidOperationException>(() => ledger.Append(new[]
        {
            new ConformanceLedgerItem("result", "atomic-new", "must rollback"),
            new ConformanceLedgerItem("result", "atomic-existing", "duplicate")
        }));
        Assert.Equal("ConformanceLedgerDuplicateId", error.Message);

        Assert.Single(ledger.ReadAll());
        Assert.Equal("atomic-existing", ledger.ReadAll()[0].Id);
        Assert.Equal(anchorHash, HashFile(fixture.AnchorPath));
    }

    [Fact]
    public void V107_S11_MissingHeadIsRejectedWithoutProvisioning()
    {
        RequireWindows();
        using var fixture = TestFixture.Create();
        using (var ledger = new ConformanceLedger(fixture.Options()))
            ledger.Append(new[] { new ConformanceLedgerItem("result", "missing-head-107", "value") });
        File.Delete(fixture.AnchorPath);

        var error = Assert.Throws<InvalidOperationException>(() => new ConformanceLedger(fixture.Options()));
        Assert.Equal("ConformanceLedgerAnchorMissing", error.Message);
        Assert.True(File.Exists(fixture.DatabasePath));
    }

    [Fact]
    public async Task V107_S12_ReadSnapshotRetriesWhenWriterAdvancesHead()
    {
        RequireWindows();
        using var fixture = TestFixture.Create();
        using (var initial = new ConformanceLedger(fixture.Options()))
            initial.Append(new[] { new ConformanceLedgerItem("result", "race-1", "one") });

        using var reader = new ConformanceLedger(fixture.Options(), readOnly: true);
        using var writer = new ConformanceLedger(fixture.Options());
        using var snapshotReached = new ManualResetEventSlim(false);
        using var releaseSnapshot = new ManualResetEventSlim(false);
        var snapshotCount = 0;
        reader.AfterReadSnapshot = () =>
        {
            if (Interlocked.Increment(ref snapshotCount) == 1)
            {
                snapshotReached.Set();
                releaseSnapshot.Wait(TimeSpan.FromSeconds(10));
            }
        };

        Task<IReadOnlyList<ConformanceLedgerEntry>>? pending = null;
        try
        {
            pending = Task.Run(() => reader.ReadAll());
            Assert.True(snapshotReached.Wait(TimeSpan.FromSeconds(10)));
            writer.Append(new[] { new ConformanceLedgerItem("result", "race-2", "two") });
            releaseSnapshot.Set();

            var rows = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, rows.Count);
            Assert.Equal(new[] { "race-1", "race-2" }, rows.Select(row => row.Id));
            Assert.True(Volatile.Read(ref snapshotCount) >= 2);
        }
        finally
        {
            releaseSnapshot.Set();
            if (pending is not null)
            {
                try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { }
            }
        }
    }

    [Fact]
    public void V107_S09_OnlyOneWriterMayHoldTheLedgerAndReadersRemainIndependent()
    {
        RequireWindows();
        using var fixture = TestFixture.Create();
        using var first = new ConformanceLedger(fixture.Options());
        Assert.Throws<InvalidOperationException>(() => new ConformanceLedger(fixture.Options()));
        using var reader = new ConformanceLedger(fixture.Options(), readOnly: true);
        Assert.Empty(reader.ReadAll());
    }

    [Fact]
    public void V107_S10_CommittedDatabaseAndFailedHeadWriteLeaveLedgerBlocked()
    {
        RequireWindows();
        using var fixture = TestFixture.Create();
        using (var initial = new ConformanceLedger(fixture.Options()))
            initial.Append(new[] { new ConformanceLedgerItem("result", "anchor-1", "one") });

        using var ledger = new ConformanceLedger(fixture.Options());
        var originalAttributes = File.GetAttributes(fixture.AnchorPath);
        File.SetAttributes(fixture.AnchorPath, originalAttributes | FileAttributes.ReadOnly);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => ledger.Append(
                new[] { new ConformanceLedgerItem("result", "anchor-2", "two") }));
            Assert.Equal("ConformanceLedgerAnchorWriteFailed", error.Message);
            using (var connection = fixture.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM conformance_ledger_entries;";
                Assert.Equal(2L, (long)command.ExecuteScalar()!);
            }
            var blocked = Assert.Throws<InvalidOperationException>(() => ledger.ReadAll());
            Assert.Equal("ConformanceLedgerBlocked", blocked.Message);
        }
        finally
        {
            File.SetAttributes(fixture.AnchorPath, originalAttributes);
        }
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Conformance ledger acceptance requires Windows local NTFS and DPAPI.");
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class TestFixture : IDisposable
    {
        private TestFixture(string directoryPath, string databasePath, string keyDirectory, string signingKeyName,
            int maxEntries, long maxTotalBytes)
        {
            DirectoryPath = directoryPath;
            DatabasePath = databasePath;
            KeyDirectory = keyDirectory;
            SigningKeyName = signingKeyName;
            MaxEntries = maxEntries;
            MaxTotalBytes = maxTotalBytes;
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }
        public string KeyDirectory { get; }
        public string SigningKeyName { get; }
        public string AnchorPath => DatabasePath + ".anchor";
        public int MaxEntries { get; }
        public long MaxTotalBytes { get; }

        public static TestFixture Create(int maxEntries = 10_000, long maxTotalBytes = 128L * 1024 * 1024)
        {
            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V107-ConformanceLedger",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return new TestFixture(directory, Path.Combine(directory, "conformance.sqlite"),
                Path.Combine(directory, "keys"), "SharpInspect.Test.V107." + Guid.NewGuid().ToString("N"),
                maxEntries, maxTotalBytes);
        }

        public ConformanceLedgerOptions Options() => new(DatabasePath, KeyDirectory, SigningKeyName)
        {
            MaxEntries = MaxEntries,
            MaxTotalBytes = MaxTotalBytes
        };

        public SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }

        public void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void CreateEntryUpdateTrigger(SqliteConnection connection) => Execute(connection,
            "CREATE TRIGGER conformance_ledger_entries_immutable_update BEFORE UPDATE ON conformance_ledger_entries " +
            "BEGIN SELECT RAISE(ABORT,'ConformanceLedgerImmutable'); END;");

        public void CreateEntryDeleteTrigger(SqliteConnection connection) => Execute(connection,
            "CREATE TRIGGER conformance_ledger_entries_immutable_delete BEFORE DELETE ON conformance_ledger_entries " +
            "BEGIN SELECT RAISE(ABORT,'ConformanceLedgerImmutable'); END;");

        public void Dispose()
        {
            // Evidence directories are intentionally retained. The test-created key and
            // database are isolated by a random directory and can be inspected after a failure.
        }
    }
}

internal static class ConformanceLedgerTestHashExtensions
{
    internal static string ToHex(this byte[] value) => Convert.ToHexString(value);
}

#pragma warning restore CA1416
