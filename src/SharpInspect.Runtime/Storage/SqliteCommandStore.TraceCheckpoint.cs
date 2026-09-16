using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed record TraceCheckpointTestHooks(Func<DateTimeOffset>? UtcNow = null, Action<string>? Phase = null);

internal sealed partial class SqliteCommandStore
{
    private readonly TraceCheckpointTestHooks? _checkpointHooks;
    private TraceCheckpointObservation _checkpoint = new(TraceCheckpointStatus.Awaiting,
        "TraceStorageStoppedCheckpointRequired", null, null, null, null, null, null);
    internal TraceCheckpointObservation Checkpoint => Volatile.Read(ref _checkpoint);
    // Test hooks run inside the same physical owner, never an alternative writer.
    private DateTimeOffset CheckpointUtcNow() => (_checkpointHooks?.UtcNow?.Invoke() ?? DateTimeOffset.UtcNow).ToUniversalTime();

    private void RunStartupCheckpoint(sqlite3 db)
    {
        if (_options.StorageRetention is null) return;
        var preparation = new StoreDeadline(_options.StorageRetention.StartupTimeout);
        VerifyTraceStoragePolicyReadGuard(db, _options, preparation);
        var config = ReadRetentionConfiguration(db, preparation);
        var rows = AuditChainDatabase.ReadTraceCheckpoints(db, config, preparation);
        var publication = ReadTraceStoragePolicyState(db, _options.TraceStoragePolicies!, preparation).Publication;
        Volatile.Write(ref _retentionWalLimit, publication?.Policy.MaximumWalBytes ?? 0);
        if (rows.LastOrDefault()?.Record is { Status: TraceCheckpointStatus.Awaiting } pending)
        {
            PersistCheckpoint(db, pending with { Status = TraceCheckpointStatus.Unknown,
                ReasonCode = "TraceCheckpointInterruptedOutcomeUnknown", RecordedAtUtc = CheckpointUtcNow() }, preparation);
            return; // A restart never turns an unobserved checkpoint into success or retries it immediately.
        }
        if (rows.LastOrDefault() is { } last) Volatile.Write(ref _checkpoint, last.Record.Observation());
        if (publication is null) return; // Keep the first policy-publication path available; production remains unqualified.
        var budget = publication.Policy.Checkpoint;
        var now = CheckpointUtcNow();
        if (rows.Count != 0 && now - rows.Max(x => x.Record.RecordedAtUtc) < budget.Interval) return;
        var start = new TraceCheckpointRecord(1, Guid.NewGuid(), config.BindingHash, publication.Version,
            publication.ContentHash, new TraceStoragePolicySnapshot(publication).ContentHash, budget.ContentHash,
            now, now, TraceCheckpointStatus.Awaiting, "TraceCheckpointStarted", ReadCheckpointWalLength(), null, null, null, null);
        // No worker or integrity monitor has been released. The store retains its .lock and sole connection.
        var deadline = new StoreDeadline(budget.MaximumRunTime);
        var timer = Stopwatch.StartNew();
        PersistCheckpoint(db, start, deadline);
        Interlocked.Increment(ref _performanceCheckpointAttempts);
        _checkpointHooks?.Phase?.Invoke("Started");
        var status = TraceCheckpointStatus.Failed;
        var reason = "TraceCheckpointFailed";
        long? pages = null, written = null, after = null;
        try
        {
            var bytes = ReadCheckpointWalLength();
            var pageBytes = AuditChainDatabase.Scalar(db, "PRAGMA page_size;", deadline);
            var frameBytes = checked(pageBytes + 24);
            var frames = bytes <= 32 ? 0 : checked((bytes - 32 + frameBytes - 1) / frameBytes);
            if (bytes > budget.MaximumBytes || frames > budget.MaximumItems)
            { status = TraceCheckpointStatus.BudgetExceeded; reason = "TraceCheckpointBudgetExceeded"; }
            else
            {
                _checkpointHooks?.Phase?.Invoke("BeforeNative");
                var result = SqliteNative.WithStatement(db, "PRAGMA wal_checkpoint(TRUNCATE);", deadline, statement =>
                {
                    if (SqliteNative.Step(db, statement, deadline) != raw.SQLITE_ROW)
                        throw new InvalidOperationException("TraceCheckpointUnobserved");
                    var counters = (Busy: SqliteNative.ColumnInt64(statement, 0), Pages: SqliteNative.ColumnInt64(statement, 1),
                        Written: SqliteNative.ColumnInt64(statement, 2));
                    if (SqliteNative.Step(db, statement, deadline) != raw.SQLITE_DONE)
                        throw new InvalidOperationException("TraceCheckpointUnobserved");
                    return counters;
                });
                pages = result.Pages; written = result.Written;
                after = ReadCheckpointWalLength();
                var complete = result.Busy == 0 && pages == 0 && written == 0 && after == 0;
                status = complete ? TraceCheckpointStatus.Completed : TraceCheckpointStatus.Busy;
                reason = complete ? "TraceCheckpointCompleted" : "TraceCheckpointBusy";
                _checkpointHooks?.Phase?.Invoke("AfterNative");
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            status = TraceCheckpointStatus.Unknown;
            reason = error is TimeoutException ? "TraceCheckpointDeadlineOutcomeUnknown" : "TraceCheckpointOutcomeUnknown";
        }
        // An owed result gets its own commit deadline. Native work has already retired on this writer thread.
        PersistCheckpoint(db, start with { Status = status, ReasonCode = reason, RecordedAtUtc = CheckpointUtcNow(),
            AfterWalBytes = after, LogFrames = pages, CheckpointedFrames = written, ElapsedTicks = timer.Elapsed.Ticks },
            new StoreDeadline(CommitTimeout));
    }

    private long ReadCheckpointWalLength()
    {
        var bytes = _readWalLength(_databasePath! + "-wal");
        if (bytes < 0) throw new IOException("TraceCheckpointWalObservationInvalid");
        return bytes;
    }

    private void PersistCheckpoint(sqlite3 db, TraceCheckpointRecord record, StoreDeadline deadline)
    {
        var committed = false;
        SqliteNative.Execute(db, "BEGIN IMMEDIATE;", deadline);
        try
        {
            AuditChainDatabase.AppendTraceCheckpoint(db, _policy!, _signingKey!, record, deadline);
            SqliteNative.Execute(db, "COMMIT;", deadline);
            committed = true;
            Volatile.Write(ref _checkpoint, record.Observation());
            Interlocked.Exchange(ref _lastCommittedAuditSequence, AuditChainDatabase.Tail(db, deadline).Sequence);
        }
        finally
        {
            if (!committed)
                try { SqliteNative.Execute(db, "ROLLBACK;", new StoreDeadline(CommitTimeout)); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
        }
    }
}
