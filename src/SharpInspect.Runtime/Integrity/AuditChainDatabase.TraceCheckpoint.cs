using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using SQLitePCL;

namespace SharpInspect.Runtime.Integrity;

internal static partial class AuditChainDatabase
{
    internal const string TraceCheckpointStarted = "TraceCheckpointStarted";
    internal const string TraceCheckpointOutcome = "TraceCheckpointOutcome";

    internal static long TraceCheckpointReserve(sqlite3 db, StoreDeadline deadline) =>
        Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='TraceCheckpointStarted';", deadline) -
        Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='TraceCheckpointOutcome';", deadline);

    internal static IReadOnlyList<TraceCheckpointRow> ReadTraceCheckpoints(sqlite3 db,
        RetentionConfiguration config, StoreDeadline deadline)
    {
        var publications = new Dictionary<long, TraceStoragePolicyPublication>();
        // These are central signed metadata, not a second mutable status table.
        var rows = Read(db, @"SELECT Sequence,Hash,Kind,Payload FROM audit_entries
            WHERE Kind IN('TraceCheckpointStarted','TraceCheckpointOutcome') ORDER BY Sequence;", deadline, s =>
        {
            var bytes = Convert.FromBase64String(SqliteNative.ColumnText(s, 3)!);
            Require(bytes.Length is > 0 and <= 4096, "TraceCheckpointPayloadSizeInvalid");
            var value = JsonSerializer.Deserialize<TraceCheckpointRecord>(bytes) ??
                throw new InvalidOperationException("TraceCheckpointPayloadMissing");
            Require(bytes.AsSpan().SequenceEqual(value.Encode()) && value.FormatVersion == 1 &&
                value.OperationId != Guid.Empty && value.ConfigurationHash == config.BindingHash &&
                value.Actor == SystemPrincipalId.RetentionCleanup &&
                value.StartedAtUtc.Offset == TimeSpan.Zero && value.RecordedAtUtc.Offset == TimeSpan.Zero &&
                value.BeforeWalBytes >= 0 && (value.AfterWalBytes is null or >= 0) &&
                (value.ElapsedTicks is null or >= 0), "TraceCheckpointPayloadInvalid");
            var started = SqliteNative.ColumnText(s, 2) == TraceCheckpointStarted;
            Require(started == (value.Status == TraceCheckpointStatus.Awaiting) &&
                (started ? value.ReasonCode == "TraceCheckpointStarted" && value.RecordedAtUtc == value.StartedAtUtc &&
                    value.AfterWalBytes is null && value.LogFrames is null && value.CheckpointedFrames is null && value.ElapsedTicks is null :
                    value.Status is TraceCheckpointStatus.Completed or TraceCheckpointStatus.Busy or
                        TraceCheckpointStatus.BudgetExceeded or TraceCheckpointStatus.Failed or TraceCheckpointStatus.Unknown),
                "TraceCheckpointStatusInvalid");
            if (value.Status == TraceCheckpointStatus.Completed)
                Require(value.LogFrames == 0 && value.CheckpointedFrames == 0 && value.AfterWalBytes == 0 &&
                    value.ReasonCode == "TraceCheckpointCompleted", "TraceCheckpointCompletionInvalid");
            if (value.Status is TraceCheckpointStatus.Busy or TraceCheckpointStatus.Completed)
                Require(value.LogFrames is >= 0 && value.CheckpointedFrames is >= 0 &&
                    value.CheckpointedFrames <= value.LogFrames && value.ElapsedTicks is not null,
                    "TraceCheckpointCountersInvalid");
            var sequence = SqliteNative.ColumnInt64(s, 0);
            // Bind every attempt to the most recent publication preceding its start.
            var policyRows = Read(db, @"SELECT Version,PublicationPayload,CentralSequence FROM trace_storage_policy_events
                WHERE Version=? LIMIT 2;", deadline, p => (Version: SqliteNative.ColumnInt64(p, 0),
                    Publication: TraceStoragePolicyStorageCodec.DecodePublication(Convert.FromBase64String(SqliteNative.ColumnText(p, 1)!)),
                    Sequence: SqliteNative.ColumnInt64(p, 2)), value.PolicyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Require(policyRows.Count == 1, "TraceCheckpointPolicyMissing");
            var policy = policyRows[0];
            publications[value.PolicyVersion] = policy.Publication;
            Require(policy.Sequence < sequence && policy.Publication.ContentHash == value.PublicationHash &&
                new TraceStoragePolicySnapshot(policy.Publication).ContentHash == value.PolicySnapshotHash &&
                policy.Publication.Policy.Checkpoint.ContentHash == value.BudgetHash,
                "TraceCheckpointPolicyBindingMismatch");
            if (started)
                Require(Scalar(db, "SELECT COUNT(*) FROM trace_storage_policy_events WHERE CentralSequence<? AND Version>?;",
                    deadline, sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    value.PolicyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)) == 0,
                    "TraceCheckpointPolicyStale");
            return new TraceCheckpointRow(sequence, SqliteNative.ColumnText(s, 1)!, value);
        });
        TraceCheckpointRecord? pending = null;
        var seen = new HashSet<Guid>();
        DateTimeOffset? lastObserved = null;
        foreach (var row in rows)
        {
            var fact = row.Record;
            if (fact.Status == TraceCheckpointStatus.Awaiting)
            {
                Require(pending is null && seen.Add(fact.OperationId) &&
                    (lastObserved is null || fact.StartedAtUtc - lastObserved >= publications[fact.PolicyVersion].Policy.Checkpoint.Interval),
                    "TraceCheckpointAttemptOrderInvalid");
                pending = fact;
            }
            else
            {
                Require(pending is not null && fact with { Status = pending.Status, ReasonCode = pending.ReasonCode,
                    RecordedAtUtc = pending.RecordedAtUtc, AfterWalBytes = null, LogFrames = null,
                    CheckpointedFrames = null, ElapsedTicks = null } == pending, "TraceCheckpointOutcomeBindingMismatch");
                pending = null;
            }
            if (lastObserved is null || fact.RecordedAtUtc > lastObserved) lastObserved = fact.RecordedAtUtc;
        }
        Require(TraceCheckpointReserve(db, deadline) == (pending is null ? 0 : 1), "TraceCheckpointReserveInvalid");
        return rows;
    }

    internal static void AppendTraceCheckpoint(sqlite3 db, AuditIntegrityPolicy policy, IAuditSigningKey key,
        TraceCheckpointRecord record, StoreDeadline deadline)
    {
        var started = record.Status == TraceCheckpointStatus.Awaiting;
        Require(TraceCheckpointReserve(db, deadline) == (started ? 0 : 1), "TraceCheckpointPendingMismatch");
        AppendEntry(db, policy, started ? TraceCheckpointStarted : TraceCheckpointOutcome, null, record.Encode(), deadline,
            checkpointReserveOverride: started ? 1 : 0);
        ReconciliationCheckpoint(db, policy, key, deadline);
    }
}
