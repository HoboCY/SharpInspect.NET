using Microsoft.Data.Sqlite;
using SharpInspect.Runtime;

namespace SharpInspect.Runtime.Storage;

internal sealed class SqliteNativeException : Exception
{
    public SqliteNativeException(string reasonCode, int sqliteErrorCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
        SqliteErrorCode = sqliteErrorCode;
    }

    public string ReasonCode { get; }

    public int SqliteErrorCode { get; }
}

internal static class SqliteNative
{
    public static SqliteConnection Open(string path, bool readOnly)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            // Bound individual SQLite values before any path reads schema, facts, or checkpoint JSON.
            SQLitePCL.raw.sqlite3_limit(connection.Handle!, SQLitePCL.raw.SQLITE_LIMIT_LENGTH, 65536);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static void Execute(
        SQLitePCL.sqlite3 database,
        string sql,
        StoreDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        EnsureDeadline(deadline, cancellationToken);
        using var scope = new SqliteDeadlineScope(database, deadline, cancellationToken);
        var result = SQLitePCL.raw.sqlite3_exec(database, sql);
        ThrowIfFailed(database, result, deadline, cancellationToken, "TraceStoreSqlFailed");
    }

    public static T WithStatement<T>(
        SQLitePCL.sqlite3 database,
        string sql,
        StoreDeadline deadline,
        Func<SQLitePCL.sqlite3_stmt, T> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        EnsureDeadline(deadline, cancellationToken);
        using var scope = new SqliteDeadlineScope(database, deadline, cancellationToken);
        SQLitePCL.sqlite3_stmt? statement = null;
        var result = SQLitePCL.raw.sqlite3_prepare_v2(database, sql, out statement);
        ThrowIfFailed(database, result, deadline, cancellationToken, "TraceStorePrepareFailed");
        using (statement)
        {
            return callback(statement);
        }
    }

    public static void BindText(SQLitePCL.sqlite3 database, SQLitePCL.sqlite3_stmt statement, int index, string? value)
    {
        var result = value is null
            ? SQLitePCL.raw.sqlite3_bind_null(statement, index)
            : SQLitePCL.raw.sqlite3_bind_text(statement, index, value);
        if (result != SQLitePCL.raw.SQLITE_OK)
            ThrowIfFailed(database, result, null, default, "TraceStoreBindFailed");
    }

    public static void BindGuid(SQLitePCL.sqlite3 database, SQLitePCL.sqlite3_stmt statement, int index, Guid value) =>
        BindText(database, statement, index, value.ToString("D"));

    public static void BindInt64(SQLitePCL.sqlite3 database, SQLitePCL.sqlite3_stmt statement, int index, long value)
    {
        var result = SQLitePCL.raw.sqlite3_bind_int64(statement, index, value);
        if (result != SQLitePCL.raw.SQLITE_OK)
            ThrowIfFailed(database, result, null, default, "TraceStoreBindFailed");
    }

    public static void BindInt(SQLitePCL.sqlite3 database, SQLitePCL.sqlite3_stmt statement, int index, int value)
    {
        var result = SQLitePCL.raw.sqlite3_bind_int(statement, index, value);
        if (result != SQLitePCL.raw.SQLITE_OK)
            ThrowIfFailed(database, result, null, default, "TraceStoreBindFailed");
    }

    /// <summary>
    /// Set one connection-wide value limit that can accommodate every enabled
    /// bounded ledger.  SQLite has a single SQLITE_LIMIT_LENGTH per connection;
    /// configuring the optional network ledger first must not shrink a larger
    /// recipe or algorithm payload limit in a combined store.
    /// </summary>
    internal static void ConfigureSqliteLimit(SQLitePCL.sqlite3 database,
        ProductionStoreOptions options, long schemaVersion = 0)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        // Plain trace/audit stores retain their original 64 KiB boundary.
        // Identity enables the governed alarm ledger in schema 7 even when no
        // station alarm policy has been registered.
        var limit = options.LocalIdentity is not null
            ? AlarmStorageCodec.SqliteValueLimitBytes : 65536;
        if (options.RecipeDrafts is not null)
            limit = Math.Max(limit, RecipeDraftStoreOptions.SqliteValueLimitBytes);
        if (options.AlgorithmResultArchive is not null)
            limit = Math.Max(limit, AlgorithmResultArchiveOptions.SqliteValueLimitBytes);
        if (options.CameraNetwork is not null)
            limit = Math.Max(limit, CameraNetworkStoreOptions.SqliteValueLimitBytes);
        if (options.CameraRecovery is not null)
            limit = Math.Max(limit, CameraRecoveryStoreOptions.SqliteValueLimitBytes);
        if (options.CameraSetup is not null)
            limit = Math.Max(limit, CameraSetupStoreOptions.SqliteValueLimitBytes);
        if (options.ImagingSetup is not null)
            limit = Math.Max(limit, ImagingSetupStoreOptions.SqliteValueLimitBytes);
        if (options.CalibrationSessions is not null)
            limit = Math.Max(limit, CalibrationSessionStoreOptions.SqliteValueLimitBytes);
        if (options.CalibrationGovernance is not null)
            limit = Math.Max(limit, CalibrationGovernanceStoreOptions.SqliteValueLimitBytes);
        if (options.RecipeReleases is not null)
            limit = Math.Max(limit, RecipeReleaseStoreOptions.SqliteValueLimitBytes);
        if (options.PlcResultContracts is not null)
            limit = Math.Max(limit, PlcResultContractStoreOptions.SqliteValueLimitBytes);
        if (options.RecipeActivations is not null)
            limit = Math.Max(limit, RecipeActivationStoreOptions.SqliteValueLimitBytes);
        // Read-only audit consumers may omit identity configuration. Preserve
        // the historical schema's payload budget after reading its version,
        // without reducing a larger limit from another configured ledger.
        var schemaLimit = schemaVersion switch
        {
            7 => AlarmStorageCodec.SqliteValueLimitBytes,
            8 => AlgorithmResultArchiveOptions.SqliteValueLimitBytes,
            9 => RecipeDraftStoreOptions.SqliteValueLimitBytes,
            10 or 11 => CameraSetupStoreOptions.SqliteValueLimitBytes,
            12 => CameraNetworkStoreOptions.SqliteValueLimitBytes,
            13 => ImagingSetupStoreOptions.SqliteValueLimitBytes,
            14 => CalibrationSessionStoreOptions.SqliteValueLimitBytes,
            15 => Math.Max(CalibrationSessionStoreOptions.SqliteValueLimitBytes,
                CalibrationGovernanceStoreOptions.SqliteValueLimitBytes),
            16 => Math.Max(Math.Max(CalibrationSessionStoreOptions.SqliteValueLimitBytes,
                    CalibrationGovernanceStoreOptions.SqliteValueLimitBytes),
                RecipeReleaseStoreOptions.SqliteValueLimitBytes),
            17 => Math.Max(Math.Max(Math.Max(CalibrationSessionStoreOptions.SqliteValueLimitBytes,
                    CalibrationGovernanceStoreOptions.SqliteValueLimitBytes),
                RecipeReleaseStoreOptions.SqliteValueLimitBytes),
                PlcResultContractStoreOptions.SqliteValueLimitBytes),
            18 => Math.Max(Math.Max(Math.Max(Math.Max(CalibrationSessionStoreOptions.SqliteValueLimitBytes,
                    CalibrationGovernanceStoreOptions.SqliteValueLimitBytes),
                RecipeReleaseStoreOptions.SqliteValueLimitBytes),
                PlcResultContractStoreOptions.SqliteValueLimitBytes),
                RecipeActivationStoreOptions.SqliteValueLimitBytes),
            _ => 65536
        };
        limit = Math.Max(limit, schemaLimit);
        _ = SQLitePCL.raw.sqlite3_limit(database, SQLitePCL.raw.SQLITE_LIMIT_LENGTH, limit);
    }

    public static int Step(
        SQLitePCL.sqlite3 database,
        SQLitePCL.sqlite3_stmt statement,
        StoreDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        EnsureDeadline(deadline, cancellationToken);
        var result = SQLitePCL.raw.sqlite3_step(statement);
        if (result != SQLitePCL.raw.SQLITE_ROW && result != SQLitePCL.raw.SQLITE_DONE)
            ThrowIfFailed(database, result, deadline, cancellationToken, "TraceStoreStepFailed");
        return result;
    }

    public static string? ColumnText(SQLitePCL.sqlite3_stmt statement, int index)
    {
        if (SQLitePCL.raw.sqlite3_column_type(statement, index) == SQLitePCL.raw.SQLITE_NULL) return null;
        return SQLitePCL.raw.sqlite3_column_text(statement, index).utf8_to_string();
    }

    public static long ColumnInt64(SQLitePCL.sqlite3_stmt statement, int index) =>
        SQLitePCL.raw.sqlite3_column_int64(statement, index);

    public static long? ColumnInt64Nullable(SQLitePCL.sqlite3_stmt statement, int index) =>
        SQLitePCL.raw.sqlite3_column_type(statement, index) == SQLitePCL.raw.SQLITE_NULL
            ? null : SQLitePCL.raw.sqlite3_column_int64(statement, index);

    public static void EnsureDeadline(StoreDeadline deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline.Expired) throw new TimeoutException("TraceCommitDeadlineExceeded");
    }

    public static void ThrowIfFailed(
        SQLitePCL.sqlite3 database,
        int result,
        StoreDeadline? deadline,
        CancellationToken cancellationToken,
        string defaultReason)
    {
        if (result == SQLitePCL.raw.SQLITE_OK || result == SQLitePCL.raw.SQLITE_ROW ||
            result == SQLitePCL.raw.SQLITE_DONE) return;

        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        if (deadline is not null && deadline.Expired &&
            (result == SQLitePCL.raw.SQLITE_BUSY || result == SQLitePCL.raw.SQLITE_LOCKED ||
             result == SQLitePCL.raw.SQLITE_INTERRUPT))
            throw new TimeoutException("TraceCommitDeadlineExceeded");

        var primaryCode = result & 0xFF;
        var reason = primaryCode switch
        {
            SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED => "TraceStoreBusy",
            SQLitePCL.raw.SQLITE_CONSTRAINT => "TraceStoreConstraint",
            SQLitePCL.raw.SQLITE_READONLY => "TraceStoreReadOnly",
            SQLitePCL.raw.SQLITE_CORRUPT or SQLitePCL.raw.SQLITE_NOTADB => "TraceStoreCorrupt",
            _ => defaultReason
        };
        throw new SqliteNativeException(reason, result, GetErrorMessage(database));
    }

    private static string GetErrorMessage(SQLitePCL.sqlite3 database)
    {
        try { return SQLitePCL.raw.sqlite3_errmsg(database).utf8_to_string(); }
        catch { return "SQLite operation failed."; }
    }

    private sealed class SqliteDeadlineScope : IDisposable
    {
        private readonly SQLitePCL.sqlite3 _database;
        private readonly SQLitePCL.delegate_progress _progress;

        public SqliteDeadlineScope(SQLitePCL.sqlite3 database, StoreDeadline deadline,
            CancellationToken cancellationToken)
        {
            _database = database;
            var remaining = deadline.Remaining;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("TraceCommitDeadlineExceeded");
            var milliseconds = (int)Math.Clamp(Math.Floor(remaining.TotalMilliseconds), 1, int.MaxValue);
            var busyResult = SQLitePCL.raw.sqlite3_busy_timeout(database, milliseconds);
            if (busyResult != SQLitePCL.raw.SQLITE_OK)
                ThrowIfFailed(database, busyResult, deadline, cancellationToken, "TraceStoreBusyTimeoutFailed");

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
