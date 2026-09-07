using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;

namespace SharpInspect.Runtime.Storage;

/// <summary>Bounded read-only command-trace capability. Every call owns and closes its read connection.</summary>
public sealed class SqliteCommandTraceQuery : ICommandTraceQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim QuerySlots = new(4, 4);
    private static int _outstandingQueries;

    public SqliteCommandTraceQuery(ProductionStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async ValueTask<CommandTracePage> QueryAsync(CommandTraceFilter filter,
        CancellationToken cancellationToken = default)
    {
        ValidateFilter(filter);
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.QueryTimeout < TimeSpan.FromMilliseconds(1) || _options.QueryTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(_options.QueryTimeout));
        var deadline = new StoreDeadline(_options.QueryTimeout);
        if (Interlocked.Increment(ref _outstandingQueries) > 64)
        {
            Interlocked.Decrement(ref _outstandingQueries);
            throw new InvalidOperationException("TraceQueryCapacityExceeded");
        }
        var entered = false;
        try
        {
            var remaining = deadline.Remaining;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("TraceQueryDeadlineExceeded");
            entered = await QuerySlots.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            if (!entered) throw new TimeoutException("TraceQueryDeadlineExceeded");
            return await Task.Run(() =>
                {
                    SqliteNative.EnsureDeadline(deadline, cancellationToken);
                    // Filesystem and registry inspection must not block the WPF Dispatcher.
                    if (!StoragePathValidator.TryValidate(_options, out var currentPath, out var pathReason))
                        throw new InvalidOperationException(pathReason);
                    return QueryCore(currentPath, filter, deadline, cancellationToken);
                }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (SqliteNativeException ex)
        {
            throw new InvalidOperationException(ex.ReasonCode, ex);
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException("TraceStoreUnavailable", ex);
        }
        finally
        {
            if (entered) QuerySlots.Release();
            Interlocked.Decrement(ref _outstandingQueries);
        }
    }

    private static CommandTracePage QueryCore(string databasePath, CommandTraceFilter filter, StoreDeadline deadline,
        CancellationToken cancellationToken)
    {
        using var connection = SqliteNative.Open(databasePath, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.Execute(database, "PRAGMA query_only=ON; PRAGMA foreign_keys=ON;", deadline, cancellationToken);

        var schemaVersion = SqliteNative.WithStatement(database, "PRAGMA user_version;", deadline,
            statement =>
            {
                SqliteNative.Step(database, statement, deadline, cancellationToken);
                return checked((int)SqliteNative.ColumnInt64(statement, 0));
            }, cancellationToken);
        if (schemaVersion is not (1 or 2)) throw new InvalidOperationException("StoreSchemaUnavailable");

        var latestPosition = SqliteNative.WithStatement(database, "SELECT COALESCE(MAX(Position),0) FROM command_facts;",
            deadline, statement =>
        {
            SqliteNative.Step(database, statement, deadline, cancellationToken);
            return SqliteNative.ColumnInt64(statement, 0);
        }, cancellationToken);
        if (filter.ThroughPosition is { } requested && requested > latestPosition)
            throw new InvalidOperationException("TraceCursorInvalid");
        var through = filter.ThroughPosition is { } requestedPosition
            ? requestedPosition : latestPosition;
        if (through < filter.AfterPosition || through == 0)
            return new CommandTracePage(new ReadOnlyCollection<CommandTraceRecord>(Array.Empty<CommandTraceRecord>()),
                through, null);

        var predicates = new List<string> { "Position > ?", "Position <= ?" };
        if (filter.CorrelationId is not null) predicates.Add("CorrelationId = ?");
        if (filter.ClaimedPrincipalId is not null) predicates.Add("ClaimedPrincipalId = ?");
        var where = string.Join(" AND ", predicates);
        var sql = $"SELECT Position, EventId, AttemptId, CorrelationId, RuntimeEpoch, EventVersion, " +
            "AggregateSequence, OccurredAtUtc, SystemPrincipalId, AuthenticatedHumanPrincipalId, " +
            "CommandKind, Source, ClaimedPrincipalId, ClaimedSessionId, ClaimedStepUpGrantId, " +
            $"Phase, Disposition, ReasonCode FROM command_facts WHERE {where} " +
            "ORDER BY Position LIMIT ?;";

        var rows = SqliteNative.WithStatement(database, sql, deadline, statement =>
        {
            var parameter = 1;
            SqliteNative.BindInt64(database, statement, parameter++, filter.AfterPosition);
            SqliteNative.BindInt64(database, statement, parameter++, through);
            if (filter.CorrelationId is { } correlation)
                SqliteNative.BindGuid(database, statement, parameter++, correlation);
            if (filter.ClaimedPrincipalId is { } principal)
                SqliteNative.BindText(database, statement, parameter++, principal);
            SqliteNative.BindInt(database, statement, parameter, checked(filter.PageSize + 1));

            var result = new List<CommandTraceRecord>(filter.PageSize + 1);
            while (SqliteNative.Step(database, statement, deadline, cancellationToken) == SQLitePCL.raw.SQLITE_ROW)
                result.Add(ReadRecord(statement));
            return result;
        }, cancellationToken);

        long? next = null;
        if (rows.Count > filter.PageSize)
        {
            rows.RemoveAt(rows.Count - 1);
            next = rows[^1].Position;
        }

        return new CommandTracePage(new ReadOnlyCollection<CommandTraceRecord>(rows), through, next);
    }

    private static CommandTraceRecord ReadRecord(SQLitePCL.sqlite3_stmt statement)
    {
        var sourceText = SqliteNative.ColumnText(statement, 11);
        var dispositionText = SqliteNative.ColumnText(statement, 16);
        return new CommandTraceRecord(
            SqliteNative.ColumnInt64(statement, 0),
            ParseGuid(SqliteNative.ColumnText(statement, 1)),
            ParseGuid(SqliteNative.ColumnText(statement, 2)),
            ParseGuid(SqliteNative.ColumnText(statement, 3)),
            ParseGuid(SqliteNative.ColumnText(statement, 4)),
            checked((int)SqliteNative.ColumnInt64(statement, 5)),
            checked((int)SqliteNative.ColumnInt64(statement, 6)),
            ParseTime(SqliteNative.ColumnText(statement, 7)),
            SqliteNative.ColumnText(statement, 8) ?? throw new InvalidOperationException("TraceStoreCorrupt"),
            SqliteNative.ColumnText(statement, 9),
            (AuditedCommandKind)SqliteNative.ColumnInt64(statement, 10),
            ParseNullableEnum<CommandSource>(sourceText),
            SqliteNative.ColumnText(statement, 12),
            ParseNullableGuid(SqliteNative.ColumnText(statement, 13)),
            ParseNullableGuid(SqliteNative.ColumnText(statement, 14)),
            (CommandAuditPhase)SqliteNative.ColumnInt64(statement, 15),
            ParseNullableEnum<CommandDisposition>(dispositionText),
            SqliteNative.ColumnText(statement, 17) ?? throw new InvalidOperationException("TraceStoreCorrupt"));
    }

    private static void ValidateFilter(CommandTraceFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.AfterPosition < 0) throw new ArgumentOutOfRangeException(nameof(filter.AfterPosition));
        if (filter.ThroughPosition is < 0) throw new ArgumentOutOfRangeException(nameof(filter.ThroughPosition));
        if (filter.PageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(filter.PageSize));
        if (filter.ClaimedPrincipalId?.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(filter.ClaimedPrincipalId));
    }

    private static Guid ParseGuid(string? value) => Guid.TryParse(value, out var parsed)
        ? parsed : throw new InvalidOperationException("TraceStoreCorrupt");

    private static Guid? ParseNullableGuid(string? value) => string.IsNullOrEmpty(value) ? null : ParseGuid(value);

    private static T? ParseNullableEnum<T>(string? value) where T : struct, Enum =>
        string.IsNullOrEmpty(value) ? null :
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
        Enum.IsDefined(typeof(T), number) ? (T)Enum.ToObject(typeof(T), number) : throw new InvalidOperationException("TraceStoreCorrupt");

    private static DateTimeOffset ParseTime(string? value) => DateTimeOffset.TryParse(value,
        CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
        ? parsed : throw new InvalidOperationException("TraceStoreCorrupt");
}
