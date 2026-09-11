using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed record RecipeChangeWriteResult(bool Committed, string ReasonCode, RecipeChangeHistoryEvent? Event = null)
{
    internal bool Duplicate => !Committed && ReasonCode == "RecipeChangeRequestIdentityReused";
}
internal sealed class RecipeChangeWork
{
    internal RecipeChangeWork(RecipeChangeRequestEvidence request, RecipeChangeEventKind kind, RecipeChangeOutcome? outcome,
        RecipeChangeReason? reason, string reasonCode, RecipeActivationReference? activation)
    { Request = request; Kind = kind; Outcome = outcome; Reason = reason; ReasonCode = reasonCode; Activation = activation; }
    internal RecipeChangeRequestEvidence Request { get; }
    internal RecipeChangeEventKind Kind { get; }
    internal RecipeChangeOutcome? Outcome { get; }
    internal RecipeChangeReason? Reason { get; }
    internal string ReasonCode { get; }
    internal RecipeActivationReference? Activation { get; }
    internal RecipeChangeHistoryEvent? Persisted { get; set; }
}

internal sealed partial class SqliteCommandStore
{
    internal async ValueTask<RecipeChangeWriteResult> AppendRecipeChangeEventAsync(RecipeChangeRequestEvidence request,
        RecipeChangeEventKind kind, RecipeChangeOutcome? outcome, RecipeChangeReason? reason, string reasonCode,
        RecipeActivationReference? activation, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!RecipeSelectionEnabled) return new(false, "RecipeSelectionConfigurationRequired");
        var work = new RecipeChangeWork(request, kind, outcome, reason, reasonCode, activation);
        var result = await EnqueueVerifiedWorkAsync(() => new WriteRequest(null, deadline, RecipeChange: work), deadline,
            cancellationToken, "RecipeChangeWriterUnavailable", "RecipeChangeCommitDeadlineExceeded").ConfigureAwait(false);
        return new(result.Committed, result.ReasonCode, work.Persisted);
    }

    private StoreWriteResult AppendRecipeChangeCore(sqlite3 database, RecipeChangeWork work, StoreDeadline deadline)
    {
        var options = _options.RecipeSelections ?? throw new InvalidOperationException("RecipeSelectionConfigurationRequired");
        if (Integrity?.State == AuditIntegrityState.Faulted) return new(false, Integrity.ReasonCode);
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes) return new(false, "TraceStoreWalLimit");
        var started = false;
        var committed = false;
        try
        {
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            VerifyRecipeSelectionReadGuard(database, _options, deadline);
            var rows = ReadRecipeChangeRows(database, options, deadline);
            if (work.Kind == RecipeChangeEventKind.RequestObserved && rows.Any(value =>
                    value.Event.Kind == RecipeChangeEventKind.RequestObserved &&
                    value.Event.Request.RequestIdentityHash == work.Request.RequestIdentityHash))
                return new(false, "RecipeChangeRequestIdentityReused");
            var revisions = ReadRecipeSelectionRows(database, options, deadline);
            var priorTail = AuditChainDatabase.Tail(database, deadline);
            ValidateRecipeChangeSelection(work.Request, priorTail.Sequence + 1,
                revisions.ToDictionary(value => value.Revision.Reference));
            // Observation may race a governance change that already fixed RejectedBusy.
            // Only the activation writer can authorize against the then-current map;
            // this append records the frozen observation and grants no execution authority.
            var episode = rows.Select(value => value.Event).Where(value => value.Request.ContentHash == work.Request.ContentHash).ToArray();
            var time = DateTimeOffset.UtcNow;
            if (time < work.Request.ObservedAtUtc) time = work.Request.ObservedAtUtc;
            if (rows.LastOrDefault() is { } previous && time < previous.Event.RecordedAtUtc) time = previous.Event.RecordedAtUtc;
            var value = new RecipeChangeHistoryEvent(rows.Count + 1L, Guid.NewGuid(), work.Request, work.Kind,
                work.Outcome, work.Reason, work.ReasonCode, work.Activation, time);
            ValidateRecipeChangeTransition(episode, value);
            var remaining = checked(ReadRecipeChangeAuditReserve(database, deadline) - RecipeChangeRemaining(episode) +
                RecipeChangeRemaining(episode.Append(value).ToArray()));
            if (checked(rows.Count + 1L + remaining) > options.MaximumHandshakeEvents)
                throw new InvalidOperationException("RecipeChangeEventCapacityExceeded");
            var payload = RecipeSelectionStorageCodec.EncodeHandshake(value);
            RequireRecipeSelectionTotalCapacity(database, options, payload.Length, deadline, remaining);
            var provisional = new RecipeChangeStoredEvent(value, rows.LastOrDefault()?.AuditHash,
                Convert.ToHexString(SHA256.HashData(payload)), payload, 0, "");
            var audit = AuditChainDatabase.AppendRecipeSelectionMetadata(database, _policy!, _signingKey!,
                "RecipeChangeEvent", EncodeRecipeChangeAudit(provisional), options, deadline, remaining);
            var row = provisional with { AuditSequence = audit.Sequence, AuditHash = audit.Hash };
            AuditChainDatabase.Execute(database, @"INSERT INTO recipe_change_events
                (Position,PreviousHash,EventId,RequestIdentityHash,RequestContextHash,Kind,ContentHash,PayloadHash,Payload,AuditSequence,AuditHash)
                VALUES(?,?,?,?,?,?,?,?,?,?,?);", deadline, N(value.Position), row.PreviousHash, value.EventId.ToString("D"),
                value.Request.RequestIdentityHash, value.Request.ContentHash, ((int)value.Kind).ToString(CultureInfo.InvariantCulture),
                value.ContentHash, row.PayloadHash, Convert.ToBase64String(payload), N(audit.Sequence), audit.Hash);
            // Validate both directions of all map/activation/handshake references before signing off the transaction.
            ValidateRecipeSelectionHistory(database, options, _options.RecipeReleases!, _options.RecipeActivations!,
                _options.CalibrationGovernance, deadline, revisions, rows.Append(row).ToArray());
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            work.Persisted = value;
            Interlocked.Exchange(ref _lastCommittedAuditSequence, audit.Sequence);
            PublishIntegrity(SqliteAuditIntegrityQuery.Report(_policy!, AuditIntegrityState.Verifying,
                _policy!.RequireExternalAnchor ? "AuditAnchorRecheckPending" : "AuditRecheckPending"));
            WakeIntegrityMonitor();
            return new(true, "RecipeChangeEventPersisted");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception, "RecipeChangeCommitFailed")); }
        finally { if (started && !committed) Rollback(database); }
    }

    // A request reserves decision, response, ACK, clear, reset and one fault fact.
    // A fault before activation retirement retains the remaining decision slot.
    internal static long RecipeChangeRemaining(IReadOnlyList<RecipeChangeHistoryEvent> history)
    {
        if (!history.Any(value => value.Kind == RecipeChangeEventKind.RequestObserved)) return 0;
        if (history.Any(value => value.Kind == RecipeChangeEventKind.ResetObserved)) return 0;
        if (history.Any(value => value.Kind == RecipeChangeEventKind.ProtocolFault))
            return history.Any(value => value.Kind == RecipeChangeEventKind.DecisionCommitted) ? 0 : 1;
        return 7 - history.Count;
    }

    internal static long ReadRecipeChangeAuditReserve(sqlite3 database, StoreDeadline deadline) =>
        AuditChainDatabase.Scalar(database, @"SELECT COALESCE(SUM(CASE
            WHEN Observed=0 OR ResetSeen=1 THEN 0 WHEN FaultSeen=1 THEN 1-DecisionSeen ELSE 7-EventCount END),0)
            FROM (SELECT MAX(Kind=1) AS Observed,MAX(Kind=2) AS DecisionSeen,MAX(Kind=6) AS ResetSeen,
                MAX(Kind=7) AS FaultSeen,COUNT(*) AS EventCount FROM recipe_change_events GROUP BY RequestContextHash);", deadline);
}
