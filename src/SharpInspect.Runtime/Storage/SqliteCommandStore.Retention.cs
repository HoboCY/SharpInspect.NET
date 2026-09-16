using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed record EvidenceRetentionWriteResult(bool Committed, string ReasonCode,
    EvidenceRetentionStoredRow? Record = null);

internal sealed class EvidenceRetentionWrite
{
    internal EvidenceRetentionWrite(EvidenceRetentionPayload fact, EvidenceRetentionWriteProof proof,
        Action? verifyProtectedFile)
    { Fact = fact; Proof = proof; VerifyProtectedFile = verifyProtectedFile; }
    internal EvidenceRetentionPayload Fact { get; }
    internal EvidenceRetentionWriteProof Proof { get; }
    // Only checks ownership of an already-open file claim, never performs file IO.
    internal Action? VerifyProtectedFile { get; }
    internal TaskCompletionSource<EvidenceRetentionWriteResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed partial class SqliteCommandStore
{
    internal Action? RetentionAfterReadProof { get; set; }
    // Serializes physical retention and T54 reconciliation observations. Never acquired
    // from the writer or human Hold transaction; physical owners acquire it first.
    internal SemaphoreSlim EvidenceFileGate { get; } = new(1, 1);

    internal async ValueTask<EvidenceRetentionWriteResult> AppendRetentionAsync(
        EvidenceRetentionPayload fact, Action? verifyProtectedFile, StoreDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var result = await AppendRetentionOnceAsync(fact, verifyProtectedFile, deadline, cancellationToken)
                .ConfigureAwait(false);
            if (result.Committed || result.ReasonCode is not ("RetentionVerifiedSnapshotChanged" or "RetentionVerifiedTailChanged"))
                return result;
            if (deadline.Remaining <= TimeSpan.Zero) return new(false, "RetentionCommitDeadlineExceeded");
        }
        return new(false, "RetentionVerifiedSnapshotContended");
    }

    private async ValueTask<EvidenceRetentionWriteResult> AppendRetentionOnceAsync(
        EvidenceRetentionPayload fact, Action? verifyProtectedFile, StoreDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        if (_options.StorageRetention is not { } options || _queue is null || _queueSlots is null ||
            Volatile.Read(ref _disposed) != 0 || _worker.IsCompleted)
            return new(false, "RetentionStoreUnavailable");
        try
        {
            // Human governance enters the identity transaction, never this system endpoint.
            EvidenceRetentionCodec.Require(fact.Kind is not (EvidenceRetentionEventKind.Extended or
                EvidenceRetentionEventKind.HoldPlaced or EvidenceRetentionEventKind.HoldReleased),
                "GovernanceAuthorityRequired");
            _ = EvidenceRetentionCodec.Encode(fact, options.MaximumPayloadBytes);
            using var proof = await new SqliteEvidenceRetentionQuery(_options)
                .AcquireWriteProofAsync(cancellationToken).ConfigureAwait(false);
            RetentionAfterReadProof?.Invoke();
            var work = new EvidenceRetentionWrite(fact, proof, verifyProtectedFile);
            SqliteNative.EnsureDeadline(deadline, cancellationToken);
            var remaining = deadline.Remaining;
            if (remaining <= TimeSpan.Zero || !await _queueSlots.WaitAsync(remaining, cancellationToken).ConfigureAwait(false))
                return new(false, "RetentionCommitDeadlineExceeded");
            if (!_queue.Writer.TryWrite(new WriteRequest(null, deadline, Retention: work)))
            {
                _queueSlots.Release();
                return new(false, "RetentionStoreUnavailable");
            }
            // Once queued the proof and physical claim must survive caller cancellation
            // until the writer has either committed or conclusively rejected the event.
            return await work.Completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TimeoutException) { return new(false, "RetentionCommitDeadlineExceeded"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(error, "RetentionWriteUnavailable")); }
    }

    private StoreWriteResult AppendRetentionCore(sqlite3 database, EvidenceRetentionWrite work,
        StoreDeadline deadline)
    {
        if (Integrity?.State == AuditIntegrityState.Faulted) return new(false, Integrity.ReasonCode);
        var started = false;
        var committed = false;
        try
        {
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            work.Proof.RequireUnchanged(deadline);
            RequireConfiguredRetention(database, _options, deadline);
            var state = work.Proof.State;
            var tail = AuditChainDatabase.Tail(database, deadline);
            EvidenceRetentionCodec.Require(tail.Sequence == state.AuditSequence && tail.Hash == state.AuditHash,
                "VerifiedTailChanged");
            var fact = work.Fact;
            // A rollback in the observed wall clock blocks new deletions. An already
            // owned deletion still owes its accurate physical outcome.
            if (fact.Kind == EvidenceRetentionEventKind.DeleteIntent)
                EvidenceRetentionCodec.Require(fact.RecordedAtUtc >= state.Replay.LastRecordedAtUtc,
                    "ClockRollback");
            EvidenceRetentionCodec.Require(fact.Kind == EvidenceRetentionEventKind.ObligationEstablished ||
                work.VerifyProtectedFile is not null, "ProtectedFileClaimRequired");
            work.VerifyProtectedFile?.Invoke();
            var row = AppendRetentionInTransaction(database, state, fact, deadline);
            work.VerifyProtectedFile?.Invoke();
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            PublishProductionOutboxIntegrity(_policy!, row.AuditSequence);
            work.Completion.TrySetResult(new(true, "RetentionEventRecorded", row));
            return new(true, "RetentionEventRecorded");
        }
        catch (TimeoutException) { return new(false, "RetentionCommitDeadlineExceeded"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(error, "RetentionWriteFailed")); }
        finally { if (started && !committed) Rollback(database); }
    }

    private EvidenceRetentionStoredRow AppendRetentionInTransaction(sqlite3 database,
        EvidenceRetentionReadState state, EvidenceRetentionPayload fact, StoreDeadline deadline)
    {
        var configuration = state.Configuration;
        var payload = EvidenceRetentionCodec.Encode(fact, configuration.MaximumPayloadBytes);
        EvidenceRetentionCodec.Require(fact.ExecutionPolicyHash == configuration.ExecutionPolicyHash,
            "ExecutionPolicyMismatch");
        var position = state.Rows.Count + 1L;
        var contentHash = EvidenceRetentionCodec.ContentHash(position, configuration.BindingHash, payload);
        var nextAudit = checked(AuditChainDatabase.Tail(database, deadline).Sequence + 1);
        var prospective = new EvidenceRetentionStoredRow(position, fact, contentHash, nextAudit, new string('0', 64));
        RequireRetentionSource(database, configuration, prospective, deadline);
        var replay = state.Replay;
        replay.Apply(prospective);
        EvidenceRetentionCodec.Require(replay.Subjects[fact.Owner].DeleteAttempts <= configuration.MaximumDeletionAttempts,
            "DeletionAttemptsExceeded");
        var reserve = replay.FutureReserve;
        EvidenceRetentionCodec.Require(position + reserve <= configuration.MaximumEvents &&
            checked(state.PayloadBytes + payload.Length + reserve * configuration.MaximumPayloadBytes) <=
                configuration.MaximumTotalBytes, "LedgerCapacityExceeded");
        var audit = AuditChainDatabase.AppendRetentionEvent(database, _policy!, _signingKey!,
            EvidenceRetentionCodec.AuditBinding(position, configuration.BindingHash, payload), reserve, deadline);
        EvidenceRetentionCodec.Require(audit.Sequence == nextAudit, "AuditSequenceChanged");
        var row = prospective with { AuditHash = audit.Hash };
        AuditChainDatabase.Execute(database, @"INSERT INTO evidence_retention_events
            (Position,EventId,OperationId,OwnerKind,OwnerId,AggregateSequence,Kind,Payload,
             ContentHash,AuditSequence,AuditHash) VALUES(?,?,?,?,?,?,?,?,?,?,?);", deadline,
            RetentionNumber(position), fact.EventId.ToString("D"), fact.OperationId.ToString("D"),
            RetentionNumber((long)fact.Owner.Kind), fact.Owner.OwnerId.ToString("D"),
            RetentionNumber(fact.AggregateSequence), RetentionNumber((long)fact.Kind),
            Encoding.UTF8.GetString(payload), contentHash, RetentionNumber(audit.Sequence), audit.Hash);
        return row;
    }
}
