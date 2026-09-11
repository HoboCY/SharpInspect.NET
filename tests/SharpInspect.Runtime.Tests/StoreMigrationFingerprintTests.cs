using Microsoft.Data.Sqlite;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// V149 logical SQLite fingerprint checks.  Every case builds an isolated real
/// Microsoft.Data.Sqlite database under the test temporary directory and calls
/// the production fingerprint reader directly.  Nothing here fabricates
/// migration authority, production qualification or a canonical store schema;
/// the small schemas below exist only to pin the fingerprint contract.
/// </summary>
public sealed class StoreMigrationFingerprintTests
{
    private const long GenerousBudget = 64L * 1024 * 1024;
    private static readonly TimeSpan FingerprintTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("VerificationId", "V149_F01")]
    public void V149_F01_RealBackupReplicaKeepsTheSameLogicalFingerprint()
    {
        using var workspace = new FingerprintWorkspace();
        var source = workspace.PathFor("source.sqlite");
        CreateStore(source);

        var original = workspace.Read(source);
        Assert.Equal(33, original.SchemaVersion);
        Assert.Equal(new[] { "empty_ledger", "recipe_lifecycle_events", "recipe_releases" },
            original.Tables.Select(table => table.Name));
        var empty = Table(original, "empty_ledger");
        Assert.Equal(0L, empty.RowCount);
        Assert.Null(empty.MaximumRowId);
        var releases = Table(original, "recipe_releases");
        Assert.Equal(2L, releases.RowCount);
        Assert.NotNull(releases.MaximumRowId);
        Assert.Equal(2L, releases.MaximumRowId.Value);
        var events = Table(original, "recipe_lifecycle_events");
        Assert.Equal(2L, events.RowCount);
        Assert.NotNull(events.MaximumRowId);
        Assert.Equal(6L, events.MaximumRowId.Value);

        // The same database always yields the same logical fingerprint.
        AssertSameFingerprint(original, workspace.Read(source));

        // A real SQLite backup copy is logically identical even when SQLite
        // rewrites page metadata, so every fingerprint field must match.
        var replica = workspace.PathFor("replica.sqlite");
        workspace.Backup(source, replica);
        AssertSameFingerprint(original, workspace.Read(replica));

        // Growing and then freeing a large row changes the physical file (more
        // pages, different bytes) without touching any old row value or rowid.
        // A logical fingerprint must not follow that physical change, which a
        // database file hash would.
        workspace.ExecuteFile(replica,
            "INSERT INTO recipe_releases(ReleaseId, Payload, Weight, Evidence) " +
            "VALUES (999, '{}', NULL, zeroblob(262144)); " +
            "DELETE FROM recipe_releases WHERE ReleaseId = 999;");
        Assert.True(new FileInfo(replica).Length > new FileInfo(source).Length);
        AssertSameFingerprint(original, workspace.Read(replica));
    }

    [Theory]
    [InlineData("integer-versus-real")]
    [InlineData("real-precision")]
    [InlineData("null-versus-empty-text")]
    [InlineData("text-versus-blob")]
    [InlineData("embedded-nul-text-versus-shorter-text")]
    [InlineData("embedded-nul-text-versus-same-bytes-blob")]
    [InlineData("rowid")]
    [Trait("VerificationId", "V149_F02")]
    public void V149_F02_ValueOrTypeChangeChangesTheLogicalHash(string change)
    {
        using var workspace = new FingerprintWorkspace();
        var (baselineSql, changedSql) = change switch
        {
            "integer-versus-real" => ("INSERT INTO payloads(Position, Value) VALUES (1, 1);",
                "INSERT INTO payloads(Position, Value) VALUES (1, 1.0);"),
            "real-precision" => ("INSERT INTO payloads(Position, Value) VALUES (1, 1.0000000000000002);",
                "INSERT INTO payloads(Position, Value) VALUES (1, 1.0000000000000004);"),
            "null-versus-empty-text" => ("INSERT INTO payloads(Position, Value) VALUES (1, NULL);",
                "INSERT INTO payloads(Position, Value) VALUES (1, '');"),
            "text-versus-blob" => ("INSERT INTO payloads(Position, Value) VALUES (1, 'A1');",
                "INSERT INTO payloads(Position, Value) VALUES (1, X'4131');"),
            "embedded-nul-text-versus-shorter-text" =>
                ("INSERT INTO payloads(Position, Value) VALUES (1, 'a' || char(0) || 'b');",
                    "INSERT INTO payloads(Position, Value) VALUES (1, 'a');"),
            "embedded-nul-text-versus-same-bytes-blob" =>
                ("INSERT INTO payloads(Position, Value) VALUES (1, 'a' || char(0) || 'b');",
                    "INSERT INTO payloads(Position, Value) VALUES (1, X'610062');"),
            "rowid" => ("INSERT INTO payloads(Position, Value) VALUES (1, 'same');",
                "INSERT INTO payloads(Position, Value) VALUES (2, 'same');"),
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, null)
        };
        var baseline = workspace.PathFor("baseline.sqlite");
        var changed = workspace.PathFor("changed.sqlite");
        CreatePayloadStore(baseline, baselineSql);
        CreatePayloadStore(changed, changedSql);

        var original = workspace.Read(baseline);
        var mutation = workspace.Read(changed);

        // The structure and the row count stay identical, so only the data
        // hash may prove that values, types or rowids really changed.
        Assert.Equal(original.SchemaHash, mutation.SchemaHash);
        Assert.Equal(Table(original, "payloads").RowCount, Table(mutation, "payloads").RowCount);
        Assert.NotEqual(Table(original, "payloads").ContentHash,
            Table(mutation, "payloads").ContentHash);
        Assert.NotEqual(original.ContentHash, mutation.ContentHash);
        AssertSameFingerprint(original, workspace.Read(baseline));
    }

    [Theory]
    [InlineData("index")]
    [InlineData("trigger")]
    [InlineData("column")]
    [InlineData("user-version")]
    [Trait("VerificationId", "V149_F03")]
    public void V149_F03_StructuralChangeChangesTheSchemaHash(string change)
    {
        using var workspace = new FingerprintWorkspace();
        var (baselineSql, changedSql) = change switch
        {
            "index" => ("CREATE TABLE payloads(Position INTEGER PRIMARY KEY, Value TEXT);",
                "CREATE TABLE payloads(Position INTEGER PRIMARY KEY, Value TEXT); " +
                "CREATE INDEX payloads_value ON payloads(Value);"),
            "trigger" => ("CREATE TABLE payloads(Position INTEGER PRIMARY KEY, Value TEXT);",
                "CREATE TABLE payloads(Position INTEGER PRIMARY KEY, Value TEXT); " +
                "CREATE TRIGGER payloads_immutable_delete BEFORE DELETE ON payloads " +
                "BEGIN SELECT RAISE(ABORT, 'ImmutablePayload'); END;"),
            "column" => ("CREATE TABLE payloads(Position INTEGER PRIMARY KEY, Value TEXT);",
                "CREATE TABLE payloads(Position INTEGER PRIMARY KEY, Payload TEXT);"),
            "user-version" => ("PRAGMA user_version = 32; " +
                "CREATE TABLE payloads(Position INTEGER PRIMARY KEY, Value TEXT);",
                "PRAGMA user_version = 33; " +
                "CREATE TABLE payloads(Position INTEGER PRIMARY KEY, Value TEXT);"),
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, null)
        };
        var baseline = workspace.PathFor("baseline.sqlite");
        var changed = workspace.PathFor("changed.sqlite");
        CreateSchemaStore(baseline, baselineSql);
        CreateSchemaStore(changed, changedSql);

        var original = workspace.Read(baseline);
        var mutation = workspace.Read(changed);

        Assert.NotEqual(original.SchemaHash, mutation.SchemaHash);
        Assert.NotEqual(original.ContentHash, mutation.ContentHash);
        if (change == "column")
        {
            // Column names belong to the table data hash as well.
            Assert.NotEqual(Table(original, "payloads").ContentHash,
                Table(mutation, "payloads").ContentHash);
        }
        else if (change == "index" || change == "trigger")
        {
            // Indexes and triggers change the structure but not the row content.
            Assert.Equal(Table(original, "payloads").ContentHash,
                Table(mutation, "payloads").ContentHash);
        }
        else
        {
            Assert.Equal(32, original.SchemaVersion);
            Assert.Equal(33, mutation.SchemaVersion);
        }
    }

    [Fact]
    [Trait("VerificationId", "V149_F04")]
    public void V149_F04_RebuiltConstraintTablesAndAppendedRowsKeepOldRowsUnchanged()
    {
        using var workspace = new FingerprintWorkspace();
        var source = workspace.PathFor("source.sqlite");
        CreateStore(source);
        var original = workspace.Read(source);

        // T49-style maintenance: the constraint tables are recreated with new
        // constraints while every old row keeps its rowid and values, the new
        // lifecycle configuration and signed activation rows are appended, and
        // a brand new table appears.
        var rebuilt = workspace.PathFor("rebuilt.sqlite");
        workspace.Backup(source, rebuilt);
        workspace.ExecuteFile(rebuilt, @"
PRAGMA foreign_keys = OFF;
ALTER TABLE recipe_lifecycle_events RENAME TO recipe_lifecycle_events_rebuild;
CREATE TABLE recipe_lifecycle_events(Position INTEGER PRIMARY KEY, Kind TEXT NOT NULL CHECK (Kind <> ''), Detail TEXT);
INSERT INTO recipe_lifecycle_events(Position, Kind, Detail)
    SELECT Position, Kind, Detail FROM recipe_lifecycle_events_rebuild;
DROP TABLE recipe_lifecycle_events_rebuild;
INSERT INTO recipe_lifecycle_events(Position, Kind, Detail) VALUES (7, 'Activated', 'signedStoreActivated');
INSERT INTO recipe_releases(ReleaseId, Payload, Weight, Evidence) VALUES (3, '{}', NULL, NULL);
CREATE TABLE recipe_lifecycle_configuration(Id INTEGER PRIMARY KEY, BindingHash TEXT NOT NULL);
INSERT INTO recipe_lifecycle_configuration(Id, BindingHash) VALUES (1, 'BINDING');");

        Assert.True(workspace.ExistingRowsUnchanged(rebuilt, original.Tables));
        var afterRebuild = workspace.Read(rebuilt);
        Assert.Equal(3L, Table(afterRebuild, "recipe_releases").RowCount);
        Assert.Equal(3L, Table(afterRebuild, "recipe_lifecycle_events").RowCount);
        Assert.Contains(afterRebuild.Tables,
            table => table.Name == "recipe_lifecycle_configuration");

        // Appending rows alone never fails the old-row check.
        var appended = workspace.PathFor("appended.sqlite");
        workspace.Backup(source, appended);
        workspace.ExecuteFile(appended,
            "INSERT INTO empty_ledger(Id, Value) VALUES (7, 'checkpoint');");
        Assert.True(workspace.ExistingRowsUnchanged(appended, original.Tables));

        // An old value that changed, an old row that was deleted, an old rowid
        // that moved and an old table that disappeared must all fail.
        var changedValue = workspace.PathFor("changed-value.sqlite");
        workspace.Backup(source, changedValue);
        workspace.ExecuteFile(changedValue,
            @"UPDATE recipe_releases SET Payload = '{""Release"":""B""}' WHERE ReleaseId = 1;");
        Assert.False(workspace.ExistingRowsUnchanged(changedValue, original.Tables));

        var deletedRow = workspace.PathFor("deleted-row.sqlite");
        workspace.Backup(source, deletedRow);
        workspace.ExecuteFile(deletedRow, "DELETE FROM recipe_lifecycle_events WHERE Position = 5;");
        Assert.False(workspace.ExistingRowsUnchanged(deletedRow, original.Tables));

        var movedRowId = workspace.PathFor("moved-rowid.sqlite");
        workspace.Backup(source, movedRowId);
        workspace.ExecuteFile(movedRowId,
            "UPDATE recipe_releases SET ReleaseId = 101 WHERE ReleaseId = 1;");
        Assert.False(workspace.ExistingRowsUnchanged(movedRowId, original.Tables));

        var droppedTable = workspace.PathFor("dropped-table.sqlite");
        workspace.Backup(source, droppedTable);
        workspace.ExecuteFile(droppedTable, "DROP TABLE empty_ledger;");
        Assert.False(workspace.ExistingRowsUnchanged(droppedTable, original.Tables));
    }

    [Fact]
    [Trait("VerificationId", "V149_F05")]
    public void V149_F05_EmptySourceTablesAndRowIdZeroAreBoundExplicitly()
    {
        using var workspace = new FingerprintWorkspace();
        var source = workspace.PathFor("source.sqlite");
        CreateRowIdZeroStore(source);
        var original = workspace.Read(source);

        // Rowid 0 is a real row and never means "no rows"; an empty table is
        // recorded as zero rows without a maximum rowid.
        var zero = Table(original, "zero_ledger");
        Assert.Equal(1L, zero.RowCount);
        Assert.NotNull(zero.MaximumRowId);
        Assert.Equal(0L, zero.MaximumRowId.Value);
        var empty = Table(original, "empty_ledger");
        Assert.Equal(0L, empty.RowCount);
        Assert.Null(empty.MaximumRowId);

        var appended = workspace.PathFor("appended.sqlite");
        workspace.Backup(source, appended);
        workspace.ExecuteFile(appended,
            "INSERT INTO empty_ledger(Id, Value) VALUES (3, 'later'); " +
            "INSERT INTO zero_ledger(Id, Value) VALUES (4, 'later');");
        Assert.True(workspace.ExistingRowsUnchanged(appended, original.Tables));

        var changedZero = workspace.PathFor("changed-zero.sqlite");
        workspace.Backup(source, changedZero);
        workspace.ExecuteFile(changedZero,
            "UPDATE zero_ledger SET Value = 'changed' WHERE Id = 0;");
        Assert.False(workspace.ExistingRowsUnchanged(changedZero, original.Tables));

        var deletedZero = workspace.PathFor("deleted-zero.sqlite");
        workspace.Backup(source, deletedZero);
        workspace.ExecuteFile(deletedZero, "DELETE FROM zero_ledger WHERE Id = 0;");
        Assert.False(workspace.ExistingRowsUnchanged(deletedZero, original.Tables));

        // An empty source table has no old row but still binds its old columns:
        // rebuilding it with a different column name is a change.
        var changedColumn = workspace.PathFor("changed-column.sqlite");
        workspace.Backup(source, changedColumn);
        workspace.ExecuteFile(changedColumn, @"
ALTER TABLE empty_ledger RENAME TO empty_ledger_rebuild;
CREATE TABLE empty_ledger(Id INTEGER PRIMARY KEY, Payload TEXT);
DROP TABLE empty_ledger_rebuild;");
        Assert.False(workspace.ExistingRowsUnchanged(changedColumn, original.Tables));

        // A missing old table can never be reported as unchanged.
        var dropped = workspace.PathFor("dropped.sqlite");
        workspace.Backup(source, dropped);
        workspace.ExecuteFile(dropped, "DROP TABLE empty_ledger;");
        Assert.False(workspace.ExistingRowsUnchanged(dropped, original.Tables));
    }

    [Fact]
    [Trait("VerificationId", "V149_F06")]
    public void V149_F06_UnknownRowIdShapesAreRejectedExplicitly()
    {
        using var workspace = new FingerprintWorkspace();

        var withoutRowId = workspace.PathFor("without-rowid.sqlite");
        workspace.ExecuteFile(withoutRowId,
            "PRAGMA user_version = 33; " +
            "CREATE TABLE keyed(Key TEXT PRIMARY KEY, Value TEXT) WITHOUT ROWID; " +
            "INSERT INTO keyed(Key, Value) VALUES ('a', 'b');");
        var readFailure = Assert.Throws<StoreMigrationFingerprint.Failure>(
            () => workspace.Read(withoutRowId));
        Assert.Equal("StoreMigrationFingerprintWithoutRowIdUnsupported", readFailure.ReasonCode);

        var shadowed = workspace.PathFor("rowid-alias.sqlite");
        workspace.ExecuteFile(shadowed,
            "PRAGMA user_version = 33; " +
            "CREATE TABLE keyed(rowid INTEGER PRIMARY KEY, Value TEXT); " +
            "INSERT INTO keyed(rowid, Value) VALUES (1, 'b');");
        var aliasFailure = Assert.Throws<StoreMigrationFingerprint.Failure>(
            () => workspace.Read(shadowed));
        Assert.Equal("StoreMigrationFingerprintRowIdColumnUnsupported", aliasFailure.ReasonCode);

        // The target check rejects an unsupported shape instead of pretending
        // that old rows are unchanged.
        var source = workspace.PathFor("source.sqlite");
        CreateRowIdZeroStore(source);
        var original = workspace.Read(source);
        var rebuilt = workspace.PathFor("rebuilt.sqlite");
        workspace.ExecuteFile(rebuilt, @"
CREATE TABLE empty_ledger(Id INTEGER PRIMARY KEY, Value TEXT);
CREATE TABLE zero_ledger(Key TEXT PRIMARY KEY, Value TEXT) WITHOUT ROWID;
INSERT INTO zero_ledger(Key, Value) VALUES ('0', 'zero');");
        var checkFailure = Assert.Throws<StoreMigrationFingerprint.Failure>(
            () => workspace.ExistingRowsUnchanged(rebuilt, original.Tables));
        Assert.Equal("StoreMigrationFingerprintWithoutRowIdUnsupported", checkFailure.ReasonCode);
    }

    [Fact]
    [Trait("VerificationId", "V149_F07")]
    public void V149_F07_WorkBudgetIsEnforcedWhileScanning()
    {
        using var workspace = new FingerprintWorkspace();
        var large = workspace.PathFor("large.sqlite");
        CreateLargeStore(large);

        // A sufficient budget streams the whole table.
        var original = workspace.Read(large, GenerousBudget);
        Assert.Equal(2000L, Table(original, "payloads").RowCount);
        Assert.NotNull(Table(original, "payloads").MaximumRowId);
        Assert.Equal(2000L, Table(original, "payloads").MaximumRowId.GetValueOrDefault());

        // A budget that only covers a prefix of the stream stops the scan in
        // the middle instead of buffering the rest of the table.
        var readFailure = Assert.Throws<StoreMigrationFingerprint.Failure>(
            () => workspace.Read(large, 1024 * 1024));
        Assert.Equal("StoreMigrationFingerprintBudgetExceeded", readFailure.ReasonCode);
        var checkFailure = Assert.Throws<StoreMigrationFingerprint.Failure>(
            () => workspace.ExistingRowsUnchanged(large, original.Tables, 1024 * 1024));
        Assert.Equal("StoreMigrationFingerprintBudgetExceeded", checkFailure.ReasonCode);

        // Only a positive budget up to the 16 GiB hard cap is accepted.
        foreach (var invalid in new[] { 0L, -1L, StoreMigrationFingerprint.MaximumBytesHardLimit + 1 })
        {
            var invalidFailure = Assert.Throws<StoreMigrationFingerprint.Failure>(
                () => workspace.Read(large, invalid));
            Assert.Equal("StoreMigrationFingerprintBudgetInvalid", invalidFailure.ReasonCode);
        }
    }

    [Fact]
    [Trait("VerificationId", "V149_F08")]
    public void V149_F08_CancellationAndDeadlineAreNeverConvertedToAResult()
    {
        using var workspace = new FingerprintWorkspace();
        var source = workspace.PathFor("source.sqlite");
        CreateStore(source);
        var original = workspace.Read(source);
        using var connection = FingerprintWorkspace.Open(source, readOnly: true);
        var database = connection.Handle!;

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => StoreMigrationFingerprint.Read(database,
            GenerousBudget, new StoreDeadline(FingerprintTimeout), cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            StoreMigrationFingerprint.ExistingRowsUnchanged(database, original.Tables, GenerousBudget,
                new StoreDeadline(FingerprintTimeout), cancelled.Token));

        var expired = new StoreDeadline(TimeSpan.Zero);
        var readFailure = Assert.Throws<StoreMigrationFingerprint.Failure>(() =>
            StoreMigrationFingerprint.Read(database, GenerousBudget, expired));
        Assert.Equal("StoreMigrationFingerprintDeadlineExceeded", readFailure.ReasonCode);
        var checkFailure = Assert.Throws<StoreMigrationFingerprint.Failure>(() =>
            StoreMigrationFingerprint.ExistingRowsUnchanged(database, original.Tables, GenerousBudget,
                expired));
        Assert.Equal("StoreMigrationFingerprintDeadlineExceeded", checkFailure.ReasonCode);
    }

    private static MigrationTableFingerprint Table(MigrationDatabaseFingerprint fingerprint, string name) =>
        Assert.Single(fingerprint.Tables, table => table.Name == name);

    private static void AssertSameFingerprint(MigrationDatabaseFingerprint expected,
        MigrationDatabaseFingerprint actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.SchemaHash, actual.SchemaHash);
        Assert.Equal(expected.ContentHash, actual.ContentHash);
        Assert.Equal(expected.Tables.Select(table => table.Name),
            actual.Tables.Select(table => table.Name));
        Assert.Equal(expected.Tables.Select(table => table.RowCount),
            actual.Tables.Select(table => table.RowCount));
        Assert.Equal(expected.Tables.Select(table => table.MaximumRowId),
            actual.Tables.Select(table => table.MaximumRowId));
        Assert.Equal(expected.Tables.Select(table => table.ContentHash),
            actual.Tables.Select(table => table.ContentHash));
    }

    private static void CreateStore(string path)
    {
        using var connection = FingerprintWorkspace.Open(path);
        FingerprintWorkspace.Execute(connection, @"
PRAGMA user_version = 33;
CREATE TABLE recipe_releases(ReleaseId INTEGER PRIMARY KEY, Payload TEXT NOT NULL, Weight REAL, Evidence BLOB);
CREATE INDEX recipe_releases_payload ON recipe_releases(Payload);
CREATE TABLE recipe_lifecycle_events(Position INTEGER PRIMARY KEY, Kind TEXT, Detail TEXT);
CREATE TRIGGER recipe_lifecycle_events_immutable_update BEFORE UPDATE ON recipe_lifecycle_events
BEGIN
    SELECT RAISE(ABORT, 'ImmutableRecipeLifecycleEvent');
END;
CREATE TABLE empty_ledger(Id INTEGER PRIMARY KEY, Value TEXT);
INSERT INTO recipe_releases(ReleaseId, Payload, Weight, Evidence)
    VALUES (1, '{""Release"":""A""}', 1.5, X'00FF10');
INSERT INTO recipe_releases(ReleaseId, Payload, Weight, Evidence)
    VALUES (2, '{""Release"":""B""}', NULL, NULL);
INSERT INTO recipe_lifecycle_events(Position, Kind, Detail) VALUES (5, 'Retired', NULL);
INSERT INTO recipe_lifecycle_events(Position, Kind, Detail) VALUES (6, 'RolledBack', 'replacement');");
    }

    private static void CreatePayloadStore(string path, string insertSql)
    {
        using var connection = FingerprintWorkspace.Open(path);
        FingerprintWorkspace.Execute(connection,
            "PRAGMA user_version = 33; " +
            "CREATE TABLE payloads(Position INTEGER PRIMARY KEY, Value); " + insertSql);
    }

    private static void CreateSchemaStore(string path, string script)
    {
        using var connection = FingerprintWorkspace.Open(path);
        FingerprintWorkspace.Execute(connection,
            script + " INSERT INTO payloads VALUES (1, 'x');");
    }

    private static void CreateRowIdZeroStore(string path)
    {
        using var connection = FingerprintWorkspace.Open(path);
        FingerprintWorkspace.Execute(connection, @"
PRAGMA user_version = 33;
CREATE TABLE zero_ledger(Id INTEGER PRIMARY KEY, Value TEXT);
CREATE TABLE empty_ledger(Id INTEGER PRIMARY KEY, Value TEXT);
INSERT INTO zero_ledger(Id, Value) VALUES (0, 'zero');");
    }

    private static void CreateLargeStore(string path)
    {
        using var connection = FingerprintWorkspace.Open(path);
        FingerprintWorkspace.Execute(connection, @"
PRAGMA user_version = 33;
CREATE TABLE payloads(Position INTEGER PRIMARY KEY, Evidence BLOB NOT NULL);
INSERT INTO payloads(Position, Evidence)
    WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 2000)
    SELECT n, zeroblob(2048) FROM seq;");
    }

    private sealed class FingerprintWorkspace : IDisposable
    {
        public FingerprintWorkspace()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "SharpInspect.Fingerprint.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public string PathFor(string name) => Path.Combine(DirectoryPath, name);

        public static SqliteConnection Open(string path, bool readOnly = false)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            };
            var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            return connection;
        }

        public static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void ExecuteFile(string path, string sql)
        {
            using var connection = Open(path);
            Execute(connection, sql);
        }

        public void Backup(string sourcePath, string replicaPath)
        {
            using var source = Open(sourcePath);
            using var replica = Open(replicaPath);
            source.BackupDatabase(replica);
        }

        public MigrationDatabaseFingerprint Read(string path) => Read(path, GenerousBudget);

        public MigrationDatabaseFingerprint Read(string path, long maximumBytes)
        {
            using var connection = Open(path, readOnly: true);
            return StoreMigrationFingerprint.Read(connection.Handle!, maximumBytes,
                new StoreDeadline(FingerprintTimeout));
        }

        public bool ExistingRowsUnchanged(string path, IReadOnlyList<MigrationTableFingerprint> source) =>
            ExistingRowsUnchanged(path, source, GenerousBudget);

        public bool ExistingRowsUnchanged(string path,
            IReadOnlyList<MigrationTableFingerprint> source, long maximumBytes)
        {
            using var connection = Open(path, readOnly: true);
            return StoreMigrationFingerprint.ExistingRowsUnchanged(connection.Handle!, source,
                maximumBytes, new StoreDeadline(FingerprintTimeout));
        }

        public void Dispose()
        {
            try { Directory.Delete(DirectoryPath, recursive: true); }
            catch { }
        }
    }
}
