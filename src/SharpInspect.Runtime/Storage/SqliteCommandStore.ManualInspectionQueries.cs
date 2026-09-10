using SharpInspect.Abstractions;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Read projections for the schema-21 Manual Inspection ledger.
///
/// The command store owns the verified row reader.  These methods only shape
/// that immutable snapshot into the public history contract; they do not
/// create a database, append audit data, or alter any row.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    internal static ManualInspectionHistoryPage QueryManualInspectionHistory(
        sqlite3 database, ManualInspectionStoreOptions options,
        ManualInspectionHistoryFilter filter, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(filter);
        ValidateManualInspectionHistoryFilter(filter);
        options.Validate();

        var rows = ReadManualInspectionRows(database, options, deadline);
        var latestPosition = rows.Count == 0 ? 0L : rows[^1].Position;
        if (filter.ThroughPosition is { } requestedThrough &&
            requestedThrough > latestPosition)
            throw new ArgumentOutOfRangeException(nameof(filter),
                "ManualInspectionHistoryThroughPositionBeyondSnapshot");

        // ThroughPosition is a stable snapshot boundary.  A caller can carry
        // it into later pages while new ledger rows are appended; those rows
        // must remain outside the original page sequence.
        var through = filter.ThroughPosition ?? latestPosition;
        var matching = rows.Where(row =>
            (!filter.SessionId.HasValue || row.Event.SessionId == filter.SessionId.Value) &&
            (!filter.RunId.HasValue || row.Event.ManualRunId == filter.RunId.Value));
        var selected = matching.Where(row => row.Position > filter.AfterPosition &&
                row.Position <= through)
            .Take(filter.PageSize).ToArray();
        var lastSelectedPosition = selected.Length == 0
            ? filter.AfterPosition : selected[^1].Position;
        var hasMore = selected.Length > 0 && matching.Any(row =>
            row.Position > lastSelectedPosition && row.Position <= through);

        // A run is represented by an admission/progress/terminal sequence in
        // the event ledger.  Expose only the latest representation visible at
        // this page's stable through-position, so progress does not duplicate
        // a run in the public history contract.
        var pageRunIds = selected.Select(row => row.Event.ManualRunId)
            .Where(runId => runId.HasValue)
            .Select(runId => runId!.Value)
            .Distinct()
            .Take(filter.PageSize)
            .ToArray();
        var runs = pageRunIds.Select(runId => rows.Where(row =>
                row.Position <= through && row.Event.ManualRunId == runId &&
                row.Run is not null)
            .OrderBy(row => row.Position)
            .LastOrDefault()?.Run)
            .Where(run => run is not null)
            .Select(run => run!)
            .ToArray();

        // Pending status is a session projection, not a row projection.  Use
        // the last event for each session before deciding whether that session
        // is still owned or recovery-blocked.  This prevents an old admitted
        // event from making a closed session appear pending.
        var pending = rows.Where(row => row.Position <= through)
            .Select(row => row.Event)
            .GroupBy(value => value.SessionId)
            .Select(group => group.OrderBy(value => value.Position).Last())
            .Where(value => (!filter.SessionId.HasValue || value.SessionId == filter.SessionId.Value) &&
                IsManualInspectionPending(value))
            .OrderBy(value => value.Position)
            .LastOrDefault();

        return new ManualInspectionHistoryPage(true, "ManualInspectionHistoryVerified",
            selected.Select(row => row.Event), runs, through,
            hasMore ? lastSelectedPosition : null, pending?.Header,
            pending is not null && IsManualInspectionRecovery(value: pending));
    }

    internal static ManualInspectionHistoryReadResult ReadCurrentManualInspectionSession(
        sqlite3 database, ManualInspectionStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var rows = ReadManualInspectionRows(database, options, deadline);
        var current = rows.Select(row => row.Event)
            .GroupBy(value => value.SessionId)
            .Select(group => group.OrderBy(value => value.Position).Last())
            .Where(IsManualInspectionPending)
            .OrderBy(value => value.Position)
            .LastOrDefault();

        if (current is null)
        {
            var recovery = rows.Select(row => row.Event)
                .GroupBy(value => value.SessionId)
                .Select(group => group.OrderBy(value => value.Position).Last())
                .Any(IsManualInspectionRecovery);
            return new ManualInspectionHistoryReadResult(true,
                recovery ? "ManualInspectionRecoveryRequired" : "ManualInspectionIdle",
                null, null, recovery);
        }

        return new ManualInspectionHistoryReadResult(true,
            IsManualInspectionRecovery(current) ? "ManualInspectionRecoveryRequired" :
                "ManualInspectionPending", current.Header,
            LatestManualInspectionRun(rows, current.SessionId),
            IsManualInspectionRecovery(current));
    }

    internal static ManualInspectionHistoryReadResult ReadManualInspectionSession(
        sqlite3 database, ManualInspectionStoreOptions options, Guid sessionId,
        StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        if (sessionId == Guid.Empty)
            throw new ArgumentException("ManualInspectionSessionIdentityRequired", nameof(sessionId));
        options.Validate();

        var rows = ReadManualInspectionRows(database, options, deadline);
        var session = rows.Where(row => row.Event.SessionId == sessionId).ToArray();
        if (session.Length == 0)
            return new ManualInspectionHistoryReadResult(true,
                "ManualInspectionNotFound");

        var latest = session[^1].Event;
        var recovery = session.Any(row => IsManualInspectionRecovery(row.Event));
        return new ManualInspectionHistoryReadResult(true,
            recovery ? "ManualInspectionRecoveryRequired" : "ManualInspectionHistoryVerified",
            latest.Header, LatestManualInspectionRun(rows, sessionId), recovery);
    }

    private static ManualInspectionRunRecord? LatestManualInspectionRun(
        IReadOnlyList<ManualInspectionStoredRow> rows, Guid sessionId)
    {
        return rows.Where(row => row.Event.SessionId == sessionId && row.Run is not null)
            .GroupBy(row => row.Run!.RunId)
            .Select(group => group.OrderBy(row => row.Position).Last())
            .OrderBy(row => row.Position)
            .Select(row => row.Run!)
            .LastOrDefault();
    }

    private static bool IsManualInspectionPending(ManualInspectionSessionEvent value) =>
        value.Header.IsActive || IsManualInspectionRecovery(value);

    private static bool IsManualInspectionRecovery(ManualInspectionSessionEvent value) =>
        value.Header.RecoveryRequired ||
        value.Phase == ManualInspectionSessionPhase.RecoveryBlocked ||
        value.Restoration == ManualInspectionRestorationState.RecoveryBlocked;

    private static void ValidateManualInspectionHistoryFilter(
        ManualInspectionHistoryFilter filter)
    {
        if ((filter.SessionId is Guid sessionId && sessionId == Guid.Empty) ||
            (filter.RunId is Guid runId && runId == Guid.Empty) ||
            filter.AfterPosition < 0 || filter.ThroughPosition is < 0 ||
            filter.ThroughPosition < filter.AfterPosition || filter.PageSize is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(filter));
    }
}
