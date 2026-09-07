using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Real local SQLite failure-boundary tests. Each case owns an isolated temporary directory;
/// the directory is intentionally retained so a failed acceptance run can be inspected.
/// </summary>
public sealed class StoreFailureAcceptanceTests
{
    [Fact]
    public async Task V102_F01_ImmediateWriterLockHonorsDeadlineAndLeavesNoPartialRows()
    {
        var options = CreateOptions();
        await using var store = new SqliteCommandStore(options);
        var initialization = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(initialization.Committed, initialization.ReasonCode);

        using var blocker = Open(options, readOnly: false);
        Execute(blocker, "BEGIN IMMEDIATE;");
        try
        {
            var started = Stopwatch.GetTimestamp();
            var blocked = await store.AppendAsync(OutcomeFact(), new StoreDeadline(TimeSpan.FromMilliseconds(100)));
            var elapsed = TimeSpan.FromSeconds(
                (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency);

            Assert.False(blocked.Committed);
            Assert.True(blocked.ReasonCode is "TraceCommitDeadlineExceeded" or "TraceStoreBusy",
                blocked.ReasonCode);
            Assert.True(elapsed < TimeSpan.FromMilliseconds(700),
                $"The 100 ms store deadline took {elapsed.TotalMilliseconds:F0} ms.");
        }
        finally
        {
            Execute(blocker, "ROLLBACK;");
        }

        Assert.Equal(0, CountRows(options, "command_attempts"));
        Assert.Equal(0, CountRows(options, "command_facts"));

        var afterUnlock = await store.AppendAsync(OutcomeFact(), new StoreDeadline(TimeSpan.FromSeconds(1)));
        Assert.True(afterUnlock.Committed, afterUnlock.ReasonCode);
        Assert.Equal(1, CountRows(options, "command_attempts"));
        Assert.Equal(1, CountRows(options, "command_facts"));
    }

    [Fact]
    public async Task V102_F02_FactInsertAbortRollsBackTheEarlierAttemptInsert()
    {
        var options = CreateOptions();
        await using var store = new SqliteCommandStore(options);
        var initialization = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(initialization.Committed, initialization.ReasonCode);

        using var injector = Open(options, readOnly: false);
        // The writer's native sqlite3 handle is intentionally private and has no commit-hook
        // seam. Use the permitted post-initialization trigger fallback without reflection.
        var triggerName = "test_fail_command_facts_" + Guid.NewGuid().ToString("N");
        Execute(injector, $@"
            CREATE TRIGGER {triggerName}
            BEFORE INSERT ON command_facts
            BEGIN
                SELECT RAISE(ABORT, 'TestCommitFailure');
            END;");

        var failedFact = OutcomeFact();
        var failed = await store.AppendAsync(failedFact, new StoreDeadline(TimeSpan.FromSeconds(1)));

        Assert.False(failed.Committed);
        Assert.Equal("TraceStoreConstraint", failed.ReasonCode);
        Assert.Equal(0, CountRows(options, "command_attempts", failedFact.AttemptId));
        Assert.Equal(0, CountRows(options, "command_facts", failedFact.AttemptId));

        Execute(injector, $"DROP TRIGGER {triggerName};");
        var recovered = await store.AppendAsync(OutcomeFact(), new StoreDeadline(TimeSpan.FromSeconds(1)));
        Assert.True(recovered.Committed, recovered.ReasonCode);
    }

    [Fact]
    public async Task V102_F03_SecondWriterCannotBecomeAuthorityForTheSameDatabase()
    {
        var options = CreateOptions();
        await using var first = new SqliteCommandStore(options);
        var firstInitialization = await first.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(firstInitialization.Committed, firstInitialization.ReasonCode);

        await using var second = new SqliteCommandStore(options);
        var secondInitialization = await second.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(secondInitialization.Committed);
        Assert.Equal("StoreWriterAlreadyOpen", secondInitialization.ReasonCode);

        var secondWrite = await second.AppendAsync(OutcomeFact(), new StoreDeadline(TimeSpan.FromMilliseconds(200)));
        Assert.False(secondWrite.Committed);
        Assert.Equal("StoreWriterAlreadyOpen", secondWrite.ReasonCode);

        var firstWrite = await first.AppendAsync(OutcomeFact(), new StoreDeadline(TimeSpan.FromSeconds(1)));
        Assert.True(firstWrite.Committed, firstWrite.ReasonCode);
        Assert.Equal(1, CountRows(options, "command_attempts"));
        Assert.Equal(1, CountRows(options, "command_facts"));
    }

    [Fact]
    public async Task V102_F04_ReadOnlyWalTransactionDoesNotBlockWriterCommit()
    {
        var options = CreateOptions();
        await using var store = new SqliteCommandStore(options);
        var initialization = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(initialization.Committed, initialization.ReasonCode);

        using var reader = Open(options, readOnly: true);
        Assert.Equal("wal", ScalarText(reader, "PRAGMA journal_mode;")?.ToLowerInvariant());
        Execute(reader, "BEGIN;");
        try
        {
            Assert.Equal(0, ScalarInt64(reader, "SELECT COUNT(*) FROM command_facts;"));

            var fact = OutcomeFact();
            var committed = await store.AppendAsync(fact, new StoreDeadline(TimeSpan.FromSeconds(1)));

            Assert.True(committed.Committed, committed.ReasonCode);
            // The reader keeps its original WAL snapshot until the read transaction ends.
            Assert.Equal(0, ScalarInt64(reader, "SELECT COUNT(*) FROM command_facts;"));
        }
        finally
        {
            Execute(reader, "ROLLBACK;");
        }

        Assert.Equal(1, CountRows(options, "command_facts"));
    }

    private static ProductionStoreOptions CreateOptions()
    {
        var parentDirectory = Path.Combine(Path.GetTempPath(), "SharpInspect.Tests", "V102-StoreFailure",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parentDirectory);
        return new ProductionStoreOptions(Path.Combine(parentDirectory, "trace.sqlite"))
        {
            CommitTimeout = TimeSpan.FromSeconds(1),
            QueryTimeout = TimeSpan.FromSeconds(1),
            QueueCapacity = 4
        };
    }

    [Fact]
    public async Task V102_F07_PostCommitWalMetadataFailureCannotLoseTheCommittedResponseOrHangLaterCommands()
    {
        var options = CreateOptions();
        var metadataReads = 0;
        await using var store = new SqliteCommandStore(options, path =>
        {
            if (Interlocked.Increment(ref metadataReads) == 2)
                throw new UnauthorizedAccessException("Injected post-commit WAL metadata failure");
            var file = new FileInfo(path);
            return file.Exists ? file.Length : 0;
        });
        Assert.True((await store.Initialization).Committed);
        var committed = await store.AppendAsync(OutcomeFact(), new StoreDeadline(TimeSpan.FromSeconds(1)))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(committed.Committed, committed.ReasonCode);
        Assert.Equal(1, CountRows(options, "command_attempts"));
        Assert.Equal(1, CountRows(options, "command_facts"));
        var later = await store.AppendAsync(OutcomeFact(), new StoreDeadline(TimeSpan.FromSeconds(1)))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(later.Committed || later.ReasonCode == "TraceStoreWalLimit", later.ReasonCode);
    }

    [Fact]
    public async Task V102_F05_DeferredConstraintFailureAtCommitRollsBackBothFacts()
    {
        var options = CreateOptions();
        await using var store = new SqliteCommandStore(options);
        var initialized = await store.Initialization;
        Assert.True(initialized.Committed, initialized.ReasonCode);
        using var injector = Open(options, readOnly: false);
        Execute(injector, @"
            CREATE TABLE test_parent(Id INTEGER PRIMARY KEY);
            CREATE TABLE test_deferred(ParentId INTEGER,
                FOREIGN KEY(ParentId) REFERENCES test_parent(Id) DEFERRABLE INITIALLY DEFERRED);
            CREATE TRIGGER test_deferred_commit AFTER INSERT ON command_facts BEGIN
                INSERT INTO test_deferred(ParentId) VALUES(99);
            END;");
        var failed = await store.AppendAsync(OutcomeFact(), new StoreDeadline(TimeSpan.FromSeconds(1)));
        Assert.False(failed.Committed);
        Assert.Equal("TraceStoreConstraint", failed.ReasonCode);
        Assert.Equal(0, CountRows(options, "command_attempts"));
        Assert.Equal(0, CountRows(options, "command_facts"));
        Assert.Equal(0, CountRows(options, "test_deferred"));
        Execute(injector, "DROP TRIGGER test_deferred_commit;");
        Assert.True((await store.AppendAsync(OutcomeFact(), new StoreDeadline(TimeSpan.FromSeconds(1)))).Committed);
    }

    [Theory]
    [InlineData(0, "StoreForeignSchema")]
    [InlineData(2, "StoreSchemaTooNew")]
    [InlineData(-1, "StoreSchemaUnsupported")]
    public async Task V102_F06_UnsupportedDatabaseIsRejectedWithoutChangingItsBytes(int version, string reason)
    {
        var options = CreateOptions();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = options.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            Execute(connection, $"CREATE TABLE preserved(Value TEXT); INSERT INTO preserved VALUES('original'); PRAGMA user_version={version};");
        }
        var before = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(options.DatabasePath));
        await using (var store = new SqliteCommandStore(options))
        {
            var result = await store.Initialization;
            Assert.False(result.Committed);
            Assert.Equal(reason, result.ReasonCode);
        }
        Assert.Equal(before, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(options.DatabasePath)));
    }

    private static CommandAuditFact OutcomeFact() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            AuditedCommandKind.GracefulProductionStop, CommandSource.PhysicalConsole, "claimed/operator",
            Guid.NewGuid(), Guid.NewGuid(), CommandAuditPhase.Outcome, CommandDisposition.Accepted, "StopAdmitted");

    private static SqliteConnection Open(ProductionStoreOptions options, bool readOnly)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long CountRows(ProductionStoreOptions options, string table, Guid? attemptId = null)
    {
        using var connection = Open(options, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = attemptId is null
            ? $"SELECT COUNT(*) FROM {table};"
            : $"SELECT COUNT(*) FROM {table} WHERE AttemptId = $attemptId;";
        if (attemptId is not null) command.Parameters.AddWithValue("$attemptId", attemptId.Value.ToString("D"));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long ScalarInt64(SqliteConnection connection, string sql) =>
        Convert.ToInt64(Scalar(connection, sql));

    private static string? ScalarText(SqliteConnection connection, string sql) =>
        Scalar(connection, sql)?.ToString();

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
