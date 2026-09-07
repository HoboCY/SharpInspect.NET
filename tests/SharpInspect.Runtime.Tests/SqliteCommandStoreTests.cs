using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class SqliteCommandStoreTests
{
    [Fact]
    public async Task V102_S01_InitializationSetsProductionProfileAndFactsSurviveRestart()
    {
        await using var directory = new TemporaryStoreDirectory();
        var options = directory.Options();
        var outcome = Fact(CommandAuditPhase.Outcome, CommandDisposition.Accepted, "StopAdmitted");
        var terminal = outcome with
        {
            EventId = Guid.NewGuid(),
            Phase = CommandAuditPhase.Completed,
            Disposition = null,
            ReasonCode = "LocallyDisarmed",
            OccurredAtUtc = outcome.OccurredAtUtc.AddMilliseconds(5)
        };

        await using (var store = new SqliteCommandStore(options))
        {
            Assert.True((await store.Initialization).Committed);
            Assert.True((await store.AppendAsync(outcome, new StoreDeadline(TimeSpan.FromSeconds(2)))).Committed);
            Assert.True((await store.AppendAsync(terminal, new StoreDeadline(TimeSpan.FromSeconds(2)))).Committed);
        }

        await using var restarted = new SqliteCommandStore(options);
        Assert.True((await restarted.Initialization).Committed);
        Assert.Equal("wal", restarted.VerifiedProfile!.JournalMode);
        Assert.Equal(2L, restarted.VerifiedProfile.Synchronous);
        Assert.Equal(1L, restarted.VerifiedProfile.ForeignKeys);
        Assert.Equal(0L, restarted.VerifiedProfile.WalAutoCheckpoint);
        var query = new SqliteCommandTraceQuery(options);
        var page = await query.QueryAsync(new CommandTraceFilter(CorrelationId: outcome.CorrelationId, PageSize: 1));
        Assert.Equal(2, page.ThroughPosition);
        Assert.Single(page.Records);
        Assert.Equal(CommandAuditPhase.Outcome, page.Records[0].Phase);
        Assert.Equal(1, page.NextAfterPosition);

        var second = await query.QueryAsync(new CommandTraceFilter(CorrelationId: outcome.CorrelationId,
            AfterPosition: page.NextAfterPosition!.Value, ThroughPosition: page.ThroughPosition, PageSize: 1));
        Assert.Single(second.Records);
        Assert.Equal(CommandAuditPhase.Completed, second.Records[0].Phase);
        Assert.Null(second.NextAfterPosition);

        using var connection = Open(directory.DatabasePath);
        Assert.Equal("wal", ScalarString(connection, "PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task V102_S02_DuplicateCorrelationIsPersistedAsRejectedAttempt()
    {
        await using var directory = new TemporaryStoreDirectory();
        await using var store = new SqliteCommandStore(directory.Options());
        Assert.True((await store.Initialization).Committed);
        var first = Fact(CommandAuditPhase.Outcome, CommandDisposition.Accepted, "StopAdmitted");
        var duplicate = first with { EventId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), Disposition = CommandDisposition.Accepted };

        Assert.True((await store.AppendAsync(first, new StoreDeadline(TimeSpan.FromSeconds(2)))).Committed);
        var result = await store.AppendAsync(duplicate, new StoreDeadline(TimeSpan.FromSeconds(2)));
        Assert.True(result.Committed);
        Assert.NotNull(result.Fact);
        Assert.Equal(CommandDisposition.Rejected, result.Fact!.Disposition);
        Assert.Equal("DuplicateCorrelationId", result.Fact.ReasonCode);

        var query = new SqliteCommandTraceQuery(directory.Options());
        var page = await query.QueryAsync(new CommandTraceFilter(CorrelationId: first.CorrelationId, PageSize: 10));
        Assert.Equal(2, page.Records.Count);
        Assert.Equal(CommandDisposition.Accepted, page.Records[0].Disposition);
        Assert.Equal(CommandDisposition.Rejected, page.Records[1].Disposition);
    }

    [Fact]
    public async Task V102_S03_TerminalLifecycleRequiresAcceptedOutcomeAndRejectsConflictingDuplicate()
    {
        await using var directory = new TemporaryStoreDirectory();
        await using var store = new SqliteCommandStore(directory.Options());
        Assert.True((await store.Initialization).Committed);
        var rejected = Fact(CommandAuditPhase.Outcome, CommandDisposition.Rejected, "DeploymentPoliciesMissing");
        var terminal = rejected with { EventId = Guid.NewGuid(), Phase = CommandAuditPhase.Failed, Disposition = null,
            ReasonCode = "RuntimeStopped" };
        var missing = await store.AppendAsync(terminal, new StoreDeadline(TimeSpan.FromSeconds(2)));
        Assert.False(missing.Committed);
        Assert.Equal("LifecycleOutcomeMissing", missing.ReasonCode);

        var accepted = Fact(CommandAuditPhase.Outcome, CommandDisposition.Accepted, "StopAdmitted");
        Assert.True((await store.AppendAsync(accepted, new StoreDeadline(TimeSpan.FromSeconds(2)))).Committed);
        var completed = accepted with { EventId = Guid.NewGuid(), Phase = CommandAuditPhase.Completed,
            Disposition = null, ReasonCode = "LocallyDisarmed", OccurredAtUtc = accepted.OccurredAtUtc.AddSeconds(1) };
        Assert.True((await store.AppendAsync(completed, new StoreDeadline(TimeSpan.FromSeconds(2)))).Committed);
        var conflict = completed with { EventId = Guid.NewGuid(), ReasonCode = "OtherTerminal" };
        var conflictResult = await store.AppendAsync(conflict, new StoreDeadline(TimeSpan.FromSeconds(2)));
        Assert.False(conflictResult.Committed);
        Assert.Equal("DuplicateTerminalConflict", conflictResult.ReasonCode);
    }

    [Fact]
    public async Task V102_S04_HeldWriterLockHonorsShorterDeadlineAndLeavesNoPartialFact()
    {
        await using var directory = new TemporaryStoreDirectory();
        var options = new ProductionStoreOptions(directory.DatabasePath) { CommitTimeout = TimeSpan.FromSeconds(2) };
        await using var store = new SqliteCommandStore(options);
        Assert.True((await store.Initialization).Committed);
        using var holder = Open(directory.DatabasePath);
        Execute(holder, "BEGIN IMMEDIATE;");
        try
        {
            var fact = Fact(CommandAuditPhase.Outcome, CommandDisposition.Accepted, "StopAdmitted");
            var started = Stopwatch.GetTimestamp();
            var result = await store.AppendAsync(fact, new StoreDeadline(TimeSpan.FromMilliseconds(75)));
            var elapsed = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency);
            Assert.False(result.Committed);
            Assert.Equal("TraceCommitDeadlineExceeded", result.ReasonCode);
            Assert.True(elapsed < TimeSpan.FromSeconds(1), $"deadline elapsed {elapsed}");
        }
        finally
        {
            Execute(holder, "ROLLBACK;");
        }

        var query = new SqliteCommandTraceQuery(options);
        var page = await query.QueryAsync(new CommandTraceFilter(PageSize: 10));
        Assert.Empty(page.Records);
    }

    [Fact]
    public async Task V102_S05_NewerSchemaIsRejectedWithoutChangingForeignDatabase()
    {
        await using var directory = new TemporaryStoreDirectory();
        using (var connection = Open(directory.DatabasePath))
        {
            Execute(connection, "CREATE TABLE foreign_table(Value TEXT); PRAGMA user_version=99;");
        }

        await using var store = new SqliteCommandStore(directory.Options());
        var result = await store.Initialization;
        Assert.False(result.Committed);
        Assert.Equal("StoreSchemaTooNew", result.ReasonCode);
        using var check = Open(directory.DatabasePath);
        Assert.Equal(99L, ScalarInt64(check, "PRAGMA user_version;"));
        Assert.Equal(1L, ScalarInt64(check, "SELECT COUNT(*) FROM sqlite_master WHERE name='foreign_table';"));
        Assert.NotEqual("wal", ScalarString(check, "PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task V102_S06_InvalidLocalPathIsRejectedBeforeOpening()
    {
        await using var relative = new SqliteCommandStore(new ProductionStoreOptions("relative.db"));
        var relativeResult = await relative.Initialization;
        Assert.False(relativeResult.Committed);
        Assert.Equal("StorePathMustBeExplicitLocalPath", relativeResult.ReasonCode);

        await using var unc = new SqliteCommandStore(new ProductionStoreOptions(@"\\server\share\trace.db"));
        var uncResult = await unc.Initialization;
        Assert.False(uncResult.Committed);
        Assert.Equal("StorePathNetworkShare", uncResult.ReasonCode);
    }

    private static CommandAuditFact Fact(CommandAuditPhase phase, CommandDisposition? disposition, string reason) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            AuditedCommandKind.GracefulProductionStop, CommandSource.PhysicalConsole, "claimed", Guid.NewGuid(), Guid.NewGuid(),
            phase, disposition, reason);

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static long ScalarInt64(SqliteConnection connection, string sql) => Convert.ToInt64(Scalar(connection, sql));

    private static string ScalarString(SqliteConnection connection, string sql) => Convert.ToString(Scalar(connection, sql))!;

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class TemporaryStoreDirectory : IAsyncDisposable
    {
        public TemporaryStoreDirectory()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            DatabasePath = Path.Combine(DirectoryPath, "trace.db");
        }

        public string DirectoryPath { get; }

        public string DatabasePath { get; }

        public ProductionStoreOptions Options() => new(DatabasePath);

        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(DirectoryPath, recursive: true); }
            catch { }
            return ValueTask.CompletedTask;
        }
    }
}
