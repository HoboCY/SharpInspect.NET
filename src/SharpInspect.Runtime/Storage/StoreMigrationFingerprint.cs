using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// One logical table fingerprint: the scanned row count, the highest scanned
/// rowid and a deterministic SHA-256 hash over the column names, the row count
/// boundary and every row's rowid and column values in ascending rowid order.
/// </summary>
internal sealed record MigrationTableFingerprint(string Name, long RowCount, long? MaximumRowId,
    string ContentHash);

/// <summary>
/// One logical database fingerprint: PRAGMA user_version, a hash over the
/// non-internal main.sqlite_master structure and a hash over that structure
/// plus every table fingerprint.
/// </summary>
internal sealed record MigrationDatabaseFingerprint(int SchemaVersion, string SchemaHash, string ContentHash,
    IReadOnlyList<MigrationTableFingerprint> Tables);

/// <summary>
/// Deterministic, streaming, strictly bounded logical fingerprint of one
/// SQLite database.  The fingerprint binds PRAGMA user_version, the
/// non-internal main.sqlite_master structure (name, type, tbl_name, sql in an
/// explicit binary order) and every table's rows in ascending rowid order
/// (rowid plus every column value, with NULL, 64-bit integer, REAL bit
/// pattern, UTF-8 text and blob values tagged and length-prefixed).  No
/// database file bytes are hashed, no whole database is buffered and only one
/// row is inspected at a time.  The component grants no migration or
/// production authority: callers decide when and where a fingerprint is taken.
/// </summary>
internal static class StoreMigrationFingerprint
{
    /// <summary>Explicit version of every hash produced by this component.</summary>
    internal const int HashSchemeVersion = 1;

    /// <summary>Positive hard cap of one fingerprint run: 16 GiB of charged work.</summary>
    internal const long MaximumBytesHardLimit = 16L * 1024 * 1024 * 1024;

    /// <summary>Upper bound of the fingerprinted table count.</summary>
    internal const int MaximumTables = 256;

    /// <summary>Upper bound of the data columns of one fingerprinted table.</summary>
    internal const int MaximumColumnsPerTable = 256;

    /// <summary>Upper bound of the non-internal sqlite_master objects in the schema hash.</summary>
    internal const int MaximumSchemaObjects = 4096;

    /// <summary>Upper bound of one sqlite_master identifier that is quoted into generated SQL.</summary>
    internal const int MaximumIdentifierLength = 256;

    private const string HashDomain = "SharpInspect.Runtime.Storage.StoreMigrationFingerprint";
    private const string SchemaObjectsSql = @"SELECT name, type, tbl_name, sql FROM main.sqlite_master
        WHERE name NOT LIKE 'sqlite\_%' ESCAPE '\'
        ORDER BY type COLLATE BINARY, name COLLATE BINARY;";

    private const byte TagSchema = 1;
    private const byte TagSchemaObject = 2;
    private const byte TagTable = 3;
    private const byte TagRowId = 4;
    private const byte TagNull = 5;
    private const byte TagInteger = 6;
    private const byte TagReal = 7;
    private const byte TagText = 8;
    private const byte TagBlob = 9;
    private const byte TagEndOfEntries = 10;
    private const byte TagPresent = 11;
    private const byte TagAbsent = 12;
    private const byte TagDatabase = 13;
    private const byte TagTableFingerprint = 14;

    private const int ValueWorkOverhead = 16;
    private const int RowWorkOverhead = 16;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    /// <summary>
    /// Reads the logical fingerprint of one open SQLite database.  Every table
    /// is streamed in ascending rowid order and every encoded byte is charged
    /// to the caller's work budget, so a hostile or damaged database is stopped
    /// by the budget, the shape limits, the deadline or cancellation instead of
    /// by exhausting memory.
    /// </summary>
    /// <param name="database">An open SQLite handle; the caller keeps ownership.</param>
    /// <param name="maximumBytes">
    /// Positive hard cap of all encoding and reading work of this run, at most
    /// <see cref="MaximumBytesHardLimit"/> bytes.
    /// </param>
    /// <param name="deadline">Monotonic deadline that also interrupts SQLite steps.</param>
    /// <param name="cancellationToken">Signalled to stop the fingerprint immediately.</param>
    /// <exception cref="Failure">
    /// Reason-coded failure: invalid budget, exceeded budget or shape limit,
    /// unsupported table shape, missing table, corruption or an expired
    /// deadline.  Reason codes are prefixed with StoreMigrationFingerprint.
    /// </exception>
    /// <exception cref="OperationCanceledException">The token was signalled.</exception>
    internal static MigrationDatabaseFingerprint Read(SQLitePCL.sqlite3 database, long maximumBytes,
        StoreDeadline deadline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(deadline);
        var budget = new WorkBudget(maximumBytes);
        using var scope = new SqliteDeadlineScope(database, deadline, cancellationToken);
        var schemaVersion = ReadSchemaVersion(database, deadline, cancellationToken);
        var schema = HashSchema(database, schemaVersion, deadline, cancellationToken, budget);
        var tables = new List<MigrationTableFingerprint>(schema.Tables.Count);
        foreach (var table in schema.Tables)
        {
            EnsureDeadline(deadline, cancellationToken);
            var scan = TryScanTable(database, table, TableScanMode.AllRows, null, deadline, cancellationToken,
                    budget)
                ?? throw Fail("StoreMigrationFingerprintTableMissing",
                    "The table '" + table + "' disappeared while it was fingerprinted.");
            tables.Add(new MigrationTableFingerprint(table, scan.RowCount, scan.MaximumRowId, scan.ContentHash));
        }

        var contentHash = HashDatabase(schemaVersion, schema.Hash, tables, budget);
        return new MigrationDatabaseFingerprint(schemaVersion, schema.Hash, contentHash, tables);
    }

    /// <summary>
    /// Verifies that every old row and column of the supplied source
    /// fingerprints still exists unchanged in the target database.  Only the
    /// old rowid prefix of every source table (rowid &lt;= the source maximum
    /// rowid) is scanned and compared against the source row count, maximum
    /// rowid and content hash, so rows appended afterwards by an audit,
    /// checkpoint or activation writer are deliberately accepted.  An empty
    /// source table has no old row and is still bound to its old columns; rowid
    /// 0 is a real row and never means "no rows".  Structure is not compared
    /// here; DDL is the job of the target schema validation.
    /// </summary>
    /// <returns>
    /// False only when an old table, row or column is missing or changed in the
    /// target.  Everything else (invalid arguments, unsupported shape, corrupt
    /// database, exceeded budget, expired deadline) is thrown.
    /// </returns>
    internal static bool ExistingRowsUnchanged(SQLitePCL.sqlite3 database,
        IReadOnlyList<MigrationTableFingerprint> source, long maximumBytes, StoreDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(deadline);
        var budget = new WorkBudget(maximumBytes);
        ValidateSourceFingerprints(source);
        using var scope = new SqliteDeadlineScope(database, deadline, cancellationToken);
        foreach (var expected in source)
        {
            EnsureDeadline(deadline, cancellationToken);
            var mode = expected.MaximumRowId.HasValue
                ? TableScanMode.PrefixThroughRowId : TableScanMode.EmptyPrefix;
            var scan = TryScanTable(database, expected.Name, mode, expected.MaximumRowId, deadline,
                cancellationToken, budget);
            if (scan is null || scan.RowCount != expected.RowCount ||
                scan.MaximumRowId != expected.MaximumRowId ||
                !string.Equals(scan.ContentHash, expected.ContentHash, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static int ReadSchemaVersion(SQLitePCL.sqlite3 database, StoreDeadline deadline,
        CancellationToken cancellationToken)
    {
        using var statement = Prepare(database, "PRAGMA main.user_version;", deadline, cancellationToken);
        if (Step(database, statement, deadline, cancellationToken) != SQLitePCL.raw.SQLITE_ROW)
            throw Fail("StoreMigrationFingerprintSchemaVersionUnavailable",
                "SQLite did not return PRAGMA main.user_version.");
        var version = SqliteNative.ColumnInt64(statement, 0);
        if (version < 0 || version > int.MaxValue)
            throw Fail("StoreMigrationFingerprintSchemaVersionInvalid",
                "The user_version of " + Number(version) + " is outside the supported range.");
        return (int)version;
    }

    private static SchemaFingerprint HashSchema(SQLitePCL.sqlite3 database, int schemaVersion,
        StoreDeadline deadline, CancellationToken cancellationToken, WorkBudget budget)
    {
        var tables = new List<string>();
        using var hasher = new HashStream(budget);
        hasher.WriteTag(TagSchema);
        hasher.WriteInt64(schemaVersion);
        using (var statement = Prepare(database, SchemaObjectsSql, deadline, cancellationToken))
        {
            long objects = 0;
            while (Step(database, statement, deadline, cancellationToken) == SQLitePCL.raw.SQLITE_ROW)
            {
                if (objects == MaximumSchemaObjects)
                    throw Fail("StoreMigrationFingerprintSchemaObjectLimitExceeded",
                        "The schema holds more than " + Number(MaximumSchemaObjects) +
                        " non-internal objects.");
                objects++;
                budget.Charge(RowWorkOverhead);
                hasher.WriteTag(TagSchemaObject);
                var name = RequireText(statement, 0, "sqlite_master.name");
                var type = RequireText(statement, 1, "sqlite_master.type");
                var tableName = RequireText(statement, 2, "sqlite_master.tbl_name");
                var sql = SQLitePCL.raw.sqlite3_column_type(statement, 3) == SQLitePCL.raw.SQLITE_NULL
                    ? null : RequireText(statement, 3, "sqlite_master.sql");
                ValidateIdentifier(name, "sqlite_master name");
                hasher.WriteLengthPrefixedText(name);
                hasher.WriteLengthPrefixedText(type);
                hasher.WriteLengthPrefixedText(tableName);
                if (sql is null)
                {
                    hasher.WriteTag(TagAbsent);
                }
                else
                {
                    hasher.WriteTag(TagPresent);
                    hasher.WriteLengthPrefixedText(sql);
                }

                if (string.Equals(type, "table", StringComparison.Ordinal))
                {
                    if (tables.Count == MaximumTables)
                        throw Fail("StoreMigrationFingerprintTableLimitExceeded",
                            "The schema holds more than " + Number(MaximumTables) + " tables.");
                    tables.Add(name);
                }
            }

            hasher.WriteTag(TagEndOfEntries);
            hasher.WriteInt64(objects);
        }

        return new SchemaFingerprint(hasher.Finish(), tables);
    }

    private static TableScan? TryScanTable(SQLitePCL.sqlite3 database, string tableName, TableScanMode mode,
        long? prefixMaximumRowId, StoreDeadline deadline, CancellationToken cancellationToken, WorkBudget budget)
    {
        ValidateIdentifier(tableName, "table name");
        if (!TryReadTableShape(database, tableName, out var withoutRowId, deadline, cancellationToken))
            return null;
        if (withoutRowId)
            throw Fail("StoreMigrationFingerprintWithoutRowIdUnsupported",
                "The table '" + tableName + "' is stored WITHOUT ROWID and has no rowid order to fingerprint.");
        var predicate = mode switch
        {
            TableScanMode.AllRows => string.Empty,
            TableScanMode.PrefixThroughRowId => " WHERE rowid <= ?1",
            TableScanMode.EmptyPrefix => " WHERE 0",
            _ => throw Fail("StoreMigrationFingerprintScanModeUnsupported",
                "The requested table scan mode is not supported.")
        };
        using var statement = Prepare(database,
            "SELECT rowid, * FROM " + QuoteIdentifier(tableName) + predicate + " ORDER BY rowid;",
            deadline, cancellationToken);
        if (mode == TableScanMode.PrefixThroughRowId)
        {
            var bound = SQLitePCL.raw.sqlite3_bind_int64(statement, 1, prefixMaximumRowId!.Value);
            if (bound != SQLitePCL.raw.SQLITE_OK)
                ThrowIfFailed(database, bound, deadline, cancellationToken, "StoreMigrationFingerprintBindFailed");
        }

        var columnCount = SQLitePCL.raw.sqlite3_column_count(statement);
        if (columnCount < 2)
            throw Fail("StoreMigrationFingerprintTableShapeUnsupported",
                "The table '" + tableName + "' exposes no data columns.");
        if (columnCount - 1 > MaximumColumnsPerTable)
            throw Fail("StoreMigrationFingerprintColumnLimitExceeded",
                "The table '" + tableName + "' exposes more than " + Number(MaximumColumnsPerTable) +
                " data columns.");
        var columns = new string[columnCount - 1];
        for (var index = 1; index < columnCount; index++)
        {
            var column = SQLitePCL.raw.sqlite3_column_name(statement, index).utf8_to_string() ??
                throw Fail("StoreMigrationFingerprintTableShapeUnsupported", "A column name is missing.");
            ValidateIdentifier(column, "column name");
            if (IsRowIdAlias(column))
                throw Fail("StoreMigrationFingerprintRowIdColumnUnsupported",
                    "The table '" + tableName + "' has a column named '" + column +
                    "' that shadows the rowid.");
            columns[index - 1] = column;
        }

        using var hasher = new HashStream(budget);
        hasher.WriteTag(TagTable);
        hasher.WriteLengthPrefixedText(tableName);
        hasher.WriteInt64(columns.Length);
        foreach (var column in columns)
            hasher.WriteLengthPrefixedText(column);
        long rowCount = 0;
        long? maximumRowId = null;
        while (Step(database, statement, deadline, cancellationToken) == SQLitePCL.raw.SQLITE_ROW)
        {
            budget.Charge(RowWorkOverhead + ((long)columnCount * ValueWorkOverhead));
            var rowId = SqliteNative.ColumnInt64(statement, 0);
            hasher.WriteTag(TagRowId);
            hasher.WriteInt64(rowId);
            for (var index = 1; index < columnCount; index++)
                WriteColumnValue(hasher, statement, index);
            rowCount++;
            if (!maximumRowId.HasValue || maximumRowId.Value < rowId) maximumRowId = rowId;
        }

        hasher.WriteTag(TagEndOfEntries);
        hasher.WriteInt64(rowCount);
        if (maximumRowId.HasValue)
        {
            hasher.WriteTag(TagPresent);
            hasher.WriteInt64(maximumRowId.Value);
        }
        else
        {
            hasher.WriteTag(TagAbsent);
        }

        return new TableScan(rowCount, maximumRowId, hasher.Finish());
    }

    private static bool TryReadTableShape(SQLitePCL.sqlite3 database, string tableName, out bool withoutRowId,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        withoutRowId = false;
        using var statement = Prepare(database, "PRAGMA main.table_list(" + QuoteIdentifier(tableName) + ");",
            deadline, cancellationToken);
        if (Step(database, statement, deadline, cancellationToken) != SQLitePCL.raw.SQLITE_ROW)
            return false;
        if (SQLitePCL.raw.sqlite3_column_count(statement) < 6 ||
            !string.Equals(SQLitePCL.raw.sqlite3_column_name(statement, 1).utf8_to_string(), "name", StringComparison.Ordinal) ||
            !string.Equals(SQLitePCL.raw.sqlite3_column_name(statement, 2).utf8_to_string(), "type", StringComparison.Ordinal) ||
            !string.Equals(SQLitePCL.raw.sqlite3_column_name(statement, 4).utf8_to_string(), "wr", StringComparison.Ordinal))
            throw Fail("StoreMigrationFingerprintTableShapeUnsupported",
                "PRAGMA table_list did not expose the expected column layout.");
        if (!string.Equals(ColumnText(statement, 1), tableName, StringComparison.OrdinalIgnoreCase))
            throw Fail("StoreMigrationFingerprintTableShapeUnsupported",
                "PRAGMA table_list did not describe the requested table '" + tableName + "'.");
        if (!string.Equals(ColumnText(statement, 2), "table", StringComparison.Ordinal))
            return false;
        withoutRowId = SqliteNative.ColumnInt64(statement, 4) != 0;
        return true;
    }

    private static void WriteColumnValue(HashStream hasher, SQLitePCL.sqlite3_stmt statement, int index)
    {
        switch (SQLitePCL.raw.sqlite3_column_type(statement, index))
        {
            case SQLitePCL.raw.SQLITE_NULL:
                hasher.WriteTag(TagNull);
                return;
            case SQLitePCL.raw.SQLITE_INTEGER:
                hasher.WriteTag(TagInteger);
                hasher.WriteInt64(SqliteNative.ColumnInt64(statement, index));
                return;
            case SQLitePCL.raw.SQLITE_FLOAT:
                hasher.WriteTag(TagReal);
                hasher.WriteReal(SQLitePCL.raw.sqlite3_column_double(statement, index));
                return;
            case SQLitePCL.raw.SQLITE_TEXT:
                hasher.WriteTag(TagText);
                hasher.WriteLengthPrefixedBytes(ColumnBytes(statement, index));
                return;
            case SQLitePCL.raw.SQLITE_BLOB:
                hasher.WriteTag(TagBlob);
                hasher.WriteLengthPrefixedBytes(SQLitePCL.raw.sqlite3_column_blob(statement, index));
                return;
            default:
                throw Fail("StoreMigrationFingerprintColumnTypeUnsupported",
                    "The column at index " + Number(index) + " holds an unknown SQLite value type.");
        }
    }

    private static ReadOnlySpan<byte> ColumnBytes(SQLitePCL.sqlite3_stmt statement, int index)
    {
        var text = SQLitePCL.raw.sqlite3_column_text(statement, index);
        var length = SQLitePCL.raw.sqlite3_column_bytes(statement, index);
        if (length < 0)
            throw Fail("StoreMigrationFingerprintColumnValueInvalid",
                "SQLite reported a negative byte length for the column at index " + Number(index) + ".");
        return length == 0
            ? ReadOnlySpan<byte>.Empty
            : MemoryMarshal.CreateReadOnlySpan(ref System.Runtime.CompilerServices.Unsafe.AsRef(in text.GetPinnableReference()), length);
    }

    private static string HashDatabase(int schemaVersion, string schemaHash,
        IReadOnlyList<MigrationTableFingerprint> tables, WorkBudget budget)
    {
        using var hasher = new HashStream(budget);
        hasher.WriteTag(TagDatabase);
        hasher.WriteInt64(schemaVersion);
        hasher.WriteLengthPrefixedText(schemaHash);
        foreach (var table in tables)
        {
            hasher.WriteTag(TagTableFingerprint);
            hasher.WriteLengthPrefixedText(table.Name);
            hasher.WriteInt64(table.RowCount);
            if (table.MaximumRowId.HasValue)
            {
                hasher.WriteTag(TagPresent);
                hasher.WriteInt64(table.MaximumRowId.Value);
            }
            else
            {
                hasher.WriteTag(TagAbsent);
            }

            hasher.WriteLengthPrefixedText(table.ContentHash);
        }

        hasher.WriteTag(TagEndOfEntries);
        hasher.WriteInt64(tables.Count);
        return hasher.Finish();
    }

    private static void ValidateSourceFingerprints(IReadOnlyList<MigrationTableFingerprint> source)
    {
        if (source.Count > MaximumTables)
            throw Fail("StoreMigrationFingerprintTableLimitExceeded",
                "The source fingerprint holds more than " + Number(MaximumTables) + " tables.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in source)
        {
            if (table is null)
                throw Fail("StoreMigrationFingerprintSourceInvalid", "A source table fingerprint is null.");
            ValidateIdentifier(table.Name, "source table name");
            if (!names.Add(table.Name))
                throw Fail("StoreMigrationFingerprintSourceInvalid",
                    "The source fingerprint repeats the table '" + table.Name + "'.");
            if (table.RowCount < 0 || (table.RowCount == 0) != (table.MaximumRowId is null))
                throw Fail("StoreMigrationFingerprintSourceInvalid",
                    "The source table '" + table.Name +
                    "' does not bind its row count to its maximum rowid.");
            if (!IsHash(table.ContentHash))
                throw Fail("StoreMigrationFingerprintSourceInvalid",
                    "The source table '" + table.Name + "' has no SHA-256 content hash.");
        }
    }

    private static void ValidateIdentifier(string? name, string role)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaximumIdentifierLength ||
            name.Any(static character => char.IsControl(character)))
            throw Fail("StoreMigrationFingerprintIdentifierInvalid",
                "The " + role + " is not a supported SQLite identifier.");
    }

    private static string QuoteIdentifier(string name)
    {
        ValidateIdentifier(name, "identifier");
        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static bool IsRowIdAlias(string name) =>
        name.Equals("rowid", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("oid", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("_rowid_", StringComparison.OrdinalIgnoreCase);

    private static bool IsHash(string? value) => value is not null && value.Length == 64 &&
        value.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F' or
            >= 'a' and <= 'f');

    private static void EnsureDeadline(StoreDeadline deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline.Expired) throw DeadlineExceeded();
    }

    private static Failure DeadlineExceeded() => Fail("StoreMigrationFingerprintDeadlineExceeded",
        "The fingerprint deadline expired before the SQLite step completed.");

    private static Failure Fail(string reasonCode, string message) => new(reasonCode, message);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static SQLitePCL.sqlite3_stmt Prepare(SQLitePCL.sqlite3 database, string sql, StoreDeadline deadline,
        CancellationToken cancellationToken)
    {
        EnsureDeadline(deadline, cancellationToken);
        SQLitePCL.sqlite3_stmt? statement = null;
        var result = SQLitePCL.raw.sqlite3_prepare_v2(database, sql, out statement);
        ThrowIfFailed(database, result, deadline, cancellationToken, "StoreMigrationFingerprintPrepareFailed");
        if (statement is null)
            throw Fail("StoreMigrationFingerprintPrepareFailed",
                "SQLite prepared no statement for: " + sql);
        return statement;
    }

    private static int Step(SQLitePCL.sqlite3 database, SQLitePCL.sqlite3_stmt statement, StoreDeadline deadline,
        CancellationToken cancellationToken)
    {
        EnsureDeadline(deadline, cancellationToken);
        var result = SQLitePCL.raw.sqlite3_step(statement);
        if (result == SQLitePCL.raw.SQLITE_ROW || result == SQLitePCL.raw.SQLITE_DONE) return result;
        ThrowIfFailed(database, result, deadline, cancellationToken, "StoreMigrationFingerprintStepFailed");
        return result;
    }

    private static void ThrowIfFailed(SQLitePCL.sqlite3 database, int result, StoreDeadline deadline,
        CancellationToken cancellationToken, string defaultReason)
    {
        if (result == SQLitePCL.raw.SQLITE_OK || result == SQLitePCL.raw.SQLITE_ROW ||
            result == SQLitePCL.raw.SQLITE_DONE) return;
        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);
        var primaryCode = result & 0xFF;
        if (deadline.Expired &&
            (primaryCode == SQLitePCL.raw.SQLITE_BUSY || primaryCode == SQLitePCL.raw.SQLITE_LOCKED ||
             primaryCode == SQLitePCL.raw.SQLITE_INTERRUPT))
            throw DeadlineExceeded();
        var reason = primaryCode switch
        {
            SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED => "StoreMigrationFingerprintBusy",
            SQLitePCL.raw.SQLITE_CORRUPT or SQLitePCL.raw.SQLITE_NOTADB => "StoreMigrationFingerprintCorrupt",
            SQLitePCL.raw.SQLITE_INTERRUPT => "StoreMigrationFingerprintInterrupted",
            SQLitePCL.raw.SQLITE_TOOBIG => "StoreMigrationFingerprintValueLimitExceeded",
            SQLitePCL.raw.SQLITE_READONLY => "StoreMigrationFingerprintReadOnly",
            _ => defaultReason
        };
        throw Fail(reason, "SQLite reported result " + Number(result) + ": " + ErrorMessage(database));
    }

    private static string ErrorMessage(SQLitePCL.sqlite3 database)
    {
        try { return SQLitePCL.raw.sqlite3_errmsg(database).utf8_to_string(); }
        catch { return "SQLite operation failed."; }
    }

    private static string? ColumnText(SQLitePCL.sqlite3_stmt statement, int index) =>
        SqliteNative.ColumnText(statement, index);

    private static string RequireText(SQLitePCL.sqlite3_stmt statement, int index, string role)
    {
        if (SQLitePCL.raw.sqlite3_column_type(statement, index) == SQLitePCL.raw.SQLITE_NULL)
            throw Fail("StoreMigrationFingerprintTableShapeUnsupported", role + " is NULL.");
        return ColumnText(statement, index)!;
    }

    private enum TableScanMode
    {
        AllRows,
        PrefixThroughRowId,
        EmptyPrefix
    }

    private sealed record TableScan(long RowCount, long? MaximumRowId, string ContentHash);

    private sealed record SchemaFingerprint(string Hash, List<string> Tables);

    /// <summary>
    /// Reason-coded failure of one logical fingerprint step.  Cancellation is
    /// reported as <see cref="OperationCanceledException"/>; an invalid or
    /// exceeded budget, an unsupported shape, corruption and an expired
    /// deadline are reported here with a StoreMigrationFingerprint reason code.
    /// </summary>
    internal sealed class Failure : Exception
    {
        internal Failure(string reasonCode, string message) : base(message) => ReasonCode = reasonCode;

        internal string ReasonCode { get; }
    }

    private sealed class HashStream : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly WorkBudget _budget;

        internal HashStream(WorkBudget budget)
        {
            _budget = budget;
            WriteLengthPrefixedText(HashDomain);
            WriteInt64(HashSchemeVersion);
        }

        internal void WriteTag(byte tag)
        {
            Span<byte> buffer = stackalloc byte[1];
            buffer[0] = tag;
            Append(buffer);
        }

        internal void WriteInt64(long value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(buffer, value);
            Append(buffer);
        }

        internal void WriteReal(double value) => WriteInt64(BitConverter.DoubleToInt64Bits(value));

        internal void WriteLengthPrefixedText(string value) => WriteLengthPrefixedBytes(Utf8.GetBytes(value));

        internal void WriteLengthPrefixedBytes(ReadOnlySpan<byte> value)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
            Append(length);
            Append(value);
        }

        internal string Finish() => Convert.ToHexString(_hash.GetHashAndReset());

        public void Dispose() => _hash.Dispose();

        private void Append(ReadOnlySpan<byte> value)
        {
            _budget.Charge(value.Length);
            _hash.AppendData(value);
        }
    }

    private sealed class WorkBudget
    {
        private readonly long _maximum;
        private long _used;

        internal WorkBudget(long maximumBytes)
        {
            if (maximumBytes <= 0 || maximumBytes > MaximumBytesHardLimit)
                throw Fail("StoreMigrationFingerprintBudgetInvalid",
                    "The fingerprint work budget of " + Number(maximumBytes) + " bytes is outside 1.." +
                    Number(MaximumBytesHardLimit) + " bytes.");
            _maximum = maximumBytes;
        }

        internal void Charge(long bytes)
        {
            if (bytes < 0 || bytes > _maximum - _used)
                throw Fail("StoreMigrationFingerprintBudgetExceeded",
                    "The fingerprint work budget of " + Number(_maximum) + " bytes was exhausted.");
            _used += bytes;
        }
    }

    private sealed class SqliteDeadlineScope : IDisposable
    {
        private readonly SQLitePCL.sqlite3 _database;
        private readonly SQLitePCL.delegate_progress _progress;

        internal SqliteDeadlineScope(SQLitePCL.sqlite3 database, StoreDeadline deadline,
            CancellationToken cancellationToken)
        {
            _database = database;
            var remaining = deadline.Remaining;
            if (remaining <= TimeSpan.Zero) throw DeadlineExceeded();
            var milliseconds = (int)Math.Clamp(Math.Floor(remaining.TotalMilliseconds), 1, int.MaxValue);
            var busyResult = SQLitePCL.raw.sqlite3_busy_timeout(database, milliseconds);
            if (busyResult != SQLitePCL.raw.SQLITE_OK)
                ThrowIfFailed(database, busyResult, deadline, cancellationToken,
                    "StoreMigrationFingerprintBusyTimeoutFailed");
            _progress = _ => deadline.Expired || cancellationToken.IsCancellationRequested ? 1 : 0;
            SQLitePCL.raw.sqlite3_progress_handler(database, 1000, _progress, null);
        }

        public void Dispose()
        {
            SQLitePCL.raw.sqlite3_progress_handler(_database, 0, null!, null);
            SQLitePCL.raw.sqlite3_busy_timeout(_database, 0);
        }
    }
}
