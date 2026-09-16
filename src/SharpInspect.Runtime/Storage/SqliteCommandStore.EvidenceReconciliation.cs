using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed record EvidenceReconciliationWriteResult(bool Committed, string ReasonCode,
    IReadOnlyList<EvidenceReconciliationStoredRow>? Records = null);

internal sealed class EvidenceReconciliationWrite
{
    internal EvidenceReconciliationWrite(IReadOnlyList<EvidenceReconciliationPayload> facts,
        Action? verifyProtectedFiles, EvidenceReconciliationWriteProof proof)
    {
        Facts = facts.ToArray();
        VerifyProtectedFiles = verifyProtectedFiles;
        Proof = proof;
    }
    internal IReadOnlyList<EvidenceReconciliationPayload> Facts { get; }
    internal EvidenceReconciliationWriteProof Proof { get; }
    internal EvidenceReconciliationReadState Verified => Proof.State;
    // Implementations only inspect ownership of already-open protected handles. No IO,
    // callback to a formatter, external handler or asynchronous work is permitted here.
    internal Action? VerifyProtectedFiles { get; }
    internal TaskCompletionSource<EvidenceReconciliationWriteResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed partial class SqliteCommandStore
{
    internal Action? EvidenceReconciliationAfterReadProof { get; set; }
    internal async ValueTask<EvidenceReconciliationWriteResult> AppendEvidenceReconciliationAsync(
        IReadOnlyList<EvidenceReconciliationPayload> facts, Action? verifyProtectedFiles,
        StoreDeadline deadline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (_options.EvidenceReconciliation is not { } options || _queue is null || _queueSlots is null ||
            Volatile.Read(ref _disposed) != 0 || _worker.IsCompleted)
            return new(false, "EvidenceReconciliationStoreUnavailable");
        if (facts.Count is < 1 || facts.Count > options.MaximumPageSize + 1)
            return new(false, "EvidenceReconciliationBatchInvalid");
        EvidenceReconciliationWriteProof proof;
        try { proof = await new SqliteEvidenceReconciliationQuery(_options).AcquireWriteProofAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(error, "EvidenceReconciliationVerificationUnavailable")); }
        using var proofOwner = proof;
        EvidenceReconciliationAfterReadProof?.Invoke();
        var work = new EvidenceReconciliationWrite(facts, verifyProtectedFiles, proof);
        foreach (var fact in work.Facts) _ = EvidenceReconciliationStorageCodec.Encode(fact, options.MaximumPayloadBytes);
        cancellationToken.ThrowIfCancellationRequested();
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        if (!await _queueSlots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false))
            return new(false, "EvidenceReconciliationCommitDeadlineExceeded");
        if (!_queue.Writer.TryWrite(new WriteRequest(null, deadline, EvidenceReconciliation: work)))
        {
            _queueSlots.Release();
            return new(false, "EvidenceReconciliationStoreUnavailable");
        }
        return await work.Completion.Task.ConfigureAwait(false);
    }

    private StoreWriteResult AppendEvidenceReconciliationCore(sqlite3 database,
        EvidenceReconciliationWrite work, StoreDeadline deadline)
    {
        if (Integrity?.State == AuditIntegrityState.Faulted)
            return new(false, Integrity.ReasonCode);
        if (WalCapacityBlocksNewWork())
            return new(false, "TraceStoreWalLimit");
        var started = false;
        var committed = false;
        try
        {
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            work.Proof.RequireUnchanged(deadline);
            RequireConfiguredEvidenceReconciliation(database, _options, deadline);
            var tail = AuditChainDatabase.Tail(database, deadline);
            AuditChainDatabase.Require(tail.Sequence == work.Verified.AuditSequence && tail.Hash == work.Verified.AuditHash,
                "EvidenceReconciliationVerifiedTailChanged");
            var configuration = ReconciliationConfiguration(_options);
            var existing = work.Verified.Rows;
            var replay = work.Verified.Replay;
            var encoded = work.Facts.Select(x => EvidenceReconciliationStorageCodec.Encode(x,
                configuration.MaximumPayloadBytes)).ToArray();
            AuditChainDatabase.Require(!work.Facts.Any(x => existing.Any(y => y.Payload.EventId == x.EventId)),
                "EvidenceReconciliationEventAlreadyRecorded");
            var needsFileProtection = work.Facts.Any(x => x.Kind is EvidenceReconciliationEventKind.ImageVerified or
                EvidenceReconciliationEventKind.ImageFinalRecovered or EvidenceReconciliationEventKind.QuarantineIntent or
                EvidenceReconciliationEventKind.Quarantined || x.ReasonCode == "RetainedImageTombstoneVerified");
            AuditChainDatabase.Require(!needsFileProtection || work.VerifyProtectedFiles is not null,
                "EvidenceReconciliationProtectedObservationRequired");
            work.VerifyProtectedFiles?.Invoke();
            var bytes = work.Verified.PayloadBytes;
            var written = new List<EvidenceReconciliationStoredRow>(work.Facts.Count);
            for (var index = 0; index < work.Facts.Count; index++)
            {
                var fact = work.Facts[index];
                RequireEvidenceReconciliationSource(database, configuration, fact.Subject, deadline,
                    beforeAuditSequence: null);
                var position = existing.Count + written.Count + 1L;
                var payload = encoded[index];
                var futureReserve = replay.QuarantineReserveAfter(fact);
                AuditChainDatabase.Require(futureReserve >= 0 && position + futureReserve <= configuration.MaximumEvents &&
                    checked(bytes + payload.Length + (long)futureReserve * configuration.MaximumPayloadBytes) <=
                    configuration.MaximumTotalBytes, "EvidenceReconciliationCapacityExceeded");
                var audit = AuditChainDatabase.AppendEvidenceReconciliationEvent(database, _policy!, _signingKey!,
                    EvidenceReconciliationStorageCodec.AuditBinding(position, configuration.BindingHash, payload),
                    futureReserve, deadline);
                var row = new EvidenceReconciliationStoredRow(position, fact,
                    EvidenceReconciliationStorageCodec.ContentHash(position, configuration.BindingHash, payload),
                    audit.Sequence, audit.Hash);
                RequireRetentionReconciliationFact(database, row, deadline);
                replay.Apply(row);
                AuditChainDatabase.Execute(database, @"INSERT INTO evidence_reconciliation_events
                    (Position,EventId,RunId,Kind,OrphanId,Payload,ContentHash,AuditSequence,AuditHash)
                    VALUES(?,?,?,?,?,?,?,?,?);", deadline, ReconciliationNumber(position), fact.EventId.ToString("D"),
                    fact.RunId.ToString("D"), ReconciliationNumber((long)fact.Kind), fact.Subject?.OrphanId?.ToString("D"),
                    Encoding.UTF8.GetString(payload), row.ContentHash, ReconciliationNumber(audit.Sequence), audit.Hash);
                bytes = checked(bytes + payload.Length);
                written.Add(row);
            }
            // Observations and cursor are one historical page transaction, never a durable
            // half page that could count the same subject twice after restart.
            AuditChainDatabase.Require(!replay.Runs.Any(x => x.Phase == EvidenceReconciliationPhase.HistoricalScrub &&
                x.PageScanned != 0), "EvidenceReconciliationPageCheckpointRequired");
            RequireReconciliationCoverage(database, configuration, existing.Concat(written).ToArray(), deadline, existing.Count, writerBatch: true);
            work.VerifyProtectedFiles?.Invoke();
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            PublishProductionOutboxIntegrity(_policy!, written[^1].AuditSequence);
            work.Completion.TrySetResult(new(true, "EvidenceReconciliationRecorded", written.AsReadOnly()));
            return new(true, "EvidenceReconciliationRecorded");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new(false, SqliteAuditIntegrityQuery.FaultReason(error, "EvidenceReconciliationWriteFailed"));
        }
        finally { if (started && !committed) Rollback(database); }
    }
}
