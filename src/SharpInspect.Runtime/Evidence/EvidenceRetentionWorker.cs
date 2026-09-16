using SharpInspect.Abstractions;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Evidence;

/// <summary>One bounded retention scheduler. A committed intent owns its exact outcome across logical timeouts.</summary>
internal sealed class EvidenceRetentionWorker
{
    private readonly SqliteCommandStore _store;
    private readonly ProductionStoreOptions _options;
    private readonly TraceStorageRetentionOptions _retention;
    private readonly SqliteEvidenceRetentionQuery _query;
    private readonly EvidenceRetentionFiles _files;
    private readonly Guid _epoch;
    private readonly Func<string, Task> _fault;
    private readonly Func<bool> _canCleanup;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly bool _startupMaintenance;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<bool> _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _run;
    private long _sourceAfter;

    internal EvidenceRetentionWorker(SqliteCommandStore store, ProductionStoreOptions options, Guid epoch,
        Task initialization, Task reconciliationStartup, Func<string, Task> fault, Func<bool> canCleanup,
        Func<DateTimeOffset>? utcNow = null, EvidenceRetentionFiles? files = null, bool startupMaintenance = false)
    {
        _store = store; _options = options; _retention = options.StorageRetention!; _epoch = epoch;
        _query = new(options); _files = files ?? new(options); _fault = fault; _canCleanup = canCleanup;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _startupMaintenance = startupMaintenance;
        _run = Task.Run(() => RunAsync(initialization, reconciliationStartup));
    }
    internal Task Startup => _startup.Task;
    internal Task Completion => _run;
    internal string? FailureReason { get; private set; }

    internal async Task<bool> StopAsync()
    {
        _stop.Cancel();
        try { await _run.WaitAsync(_retention.FileTimeout).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }

    private async Task RunAsync(Task initialization, Task reconciliationStartup)
    {
        try
        {
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                startup.CancelAfter(_retention.StartupTimeout);
                await initialization.WaitAsync(startup.Token).ConfigureAwait(false);
                await _store.EvidenceFileGate.WaitAsync(startup.Token).ConfigureAwait(false);
                try
                {
                    var state = await _query.ReadStateAsync(startup.Token).ConfigureAwait(false);
                    foreach (var subject in state.Replay.Subjects.Values.Where(value => value.DeleteIntent is not null))
                    {
                        startup.Token.ThrowIfCancellationRequested();
                        await CompleteDeletionAsync(subject, startup.Token).ConfigureAwait(false);
                    }
                    // Establish old completed sources before Ready. This freezes their
                    // original rules without deleting them or deriving any creation time.
                    while (true)
                    {
                        try { if (!await EstablishPageAsync(_retention.MaximumPageSize, startup.Token).ConfigureAwait(false)) break; }
                        catch (TimeoutException) when (!startup.IsCancellationRequested)
                        { await Task.Delay(_retention.ObservationInterval, startup.Token).ConfigureAwait(false); }
                    }
                }
                finally { _store.EvidenceFileGate.Release(); }
                if (_startupMaintenance) await RunTurnCoreAsync(startup.Token, startupMaintenance: true).ConfigureAwait(false);
            }
            _startup.TrySetResult(true);
            await reconciliationStartup.WaitAsync(_stop.Token).ConfigureAwait(false);
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(_retention.ExecutionPolicy.Cleanup.Interval, _stop.Token).ConfigureAwait(false);
                await RunTurnAsync(_stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { _startup.TrySetCanceled(_stop.Token); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FailureReason = ProductionImageFinalizationWorker.Reason(error);
            _startup.TrySetException(error);
            await _fault(FailureReason).ConfigureAwait(false);
        }
        finally
        {
            // A timeout never releases the store's physical ownership prematurely.
            try { await _files.PhysicalCompletion.ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
    }

    internal Task RunTurnAsync(CancellationToken token) => RunTurnCoreAsync(token, startupMaintenance: false);

    private async Task RunTurnCoreAsync(CancellationToken token, bool startupMaintenance)
    {
        var budget = _retention.ExecutionPolicy.Cleanup;
        var deadline = new StoreDeadline(budget.MaximumRunTime);
        var callerToken = token;
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        turn.CancelAfter(budget.MaximumRunTime);
        token = turn.Token;
        var entered = false;
        try
        {
            entered = await _store.EvidenceFileGate.WaitAsync(deadline.Remaining, token).ConfigureAwait(false);
            if (!entered) return;
            await EstablishPageAsync(Math.Min(_retention.MaximumPageSize, budget.MaximumItems), token).ConfigureAwait(false);
            if (!startupMaintenance && !_canCleanup()) return;
            var state = await _query.ReadStateAsync(token).ConfigureAwait(false);
            var completedImages = await _query.ReadCompletedImagesAsync(token).ConfigureAwait(false);
            var now = _utcNow();
            if (now < state.Replay.LastRecordedAtUtc) return;
            var lastIntent = state.Rows.Where(row => row.Payload.Kind == EvidenceRetentionEventKind.DeleteIntent)
                .Select(row => (DateTimeOffset?)row.Payload.RecordedAtUtc).Max();
            if (lastIntent is { } previous && now - previous < budget.Interval) return;
            long bytes = 0;
            var candidates = state.Replay.Subjects.Values.Where(subject => subject.ActiveHolds.Count == 0 &&
                    subject.Tombstone is null && subject.EffectiveUntilUtc <= now &&
                    (subject.DeleteIntent is not null || subject.DeleteAttempts < _retention.ExecutionPolicy.MaximumDeletionAttempts) &&
                    _retention.ExecutionPolicy.DeletableClasses.Contains(subject.Obligation.EvidenceClass) &&
                    (subject.Obligation.Owner.Kind == EvidenceRetentionOwnerKind.QuarantinedFile ||
                        completedImages.Contains(subject.Obligation.Owner.OwnerId)))
                .OrderBy(subject => subject.EffectiveUntilUtc).ThenBy(subject => subject.Obligation.SourceAuditSequence)
                .Take(budget.MaximumItems).ToArray();
            foreach (var subject in candidates)
            {
                if (deadline.Remaining <= TimeSpan.Zero || (!startupMaintenance && !_canCleanup())) break;
                if (subject.Obligation.ByteLength > budget.MaximumBytes - bytes) continue;
                bytes += subject.Obligation.ByteLength;
                if (subject.DeleteIntent is not null) await CompleteDeletionAsync(subject, token).ConfigureAwait(false);
                else await BeginDeletionAsync(subject, deadline, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (turn.IsCancellationRequested && !callerToken.IsCancellationRequested && !_stop.IsCancellationRequested) { }
        catch (TimeoutException) { }
        finally
        {
            if (entered)
            {
                if (!_files.PhysicalCompletion.IsCompleted)
                    await _fault("RetentionPhysicalRetirementPending").ConfigureAwait(false);
                try { await _files.PhysicalCompletion.ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
                _store.EvidenceFileGate.Release();
            }
        }
    }

    private async Task<bool> EstablishPageAsync(int size, CancellationToken token)
    {
        var sources = await _query.ReadSourcesAsync(_sourceAfter, size, token).ConfigureAwait(false);
        if (sources.Count == 0) return false;
        var state = await _query.ReadStateAsync(token).ConfigureAwait(false);
        foreach (var source in sources)
        {
            if (source.Obligation is { } obligation && !state.Replay.Subjects.ContainsKey(obligation.Owner))
            {
                var fact = Fact(obligation, null, EvidenceRetentionEventKind.ObligationEstablished,
                    Guid.NewGuid(), "RetentionObligationEstablished", null) with { EstablishedObligation = obligation };
                var result = await _store.AppendRetentionAsync(fact, null,
                    new StoreDeadline(_options.CommitTimeout), token).ConfigureAwait(false);
                if (!result.Committed)
                {
                    if (result.ReasonCode is "RetentionVerifiedSnapshotContended" or "RetentionCommitDeadlineExceeded" or
                        "TraceCommitDeadlineExceeded" or "RetentionQueryDeadlineExceeded" or "RetentionWriteDeadlineExceeded")
                        throw new TimeoutException(result.ReasonCode);
                    throw new IOException(result.ReasonCode);
                }
            }
            _sourceAfter = source.AuditSequence;
        }
        return sources.Count == size;
    }

    private async Task BeginDeletionAsync(EvidenceRetentionReplay.SubjectState subject, StoreDeadline turn, CancellationToken token)
    {
        var manifest = await _query.ReadManifestAsync(subject.Obligation, token).ConfigureAwait(false);
        var remaining = turn.Remaining;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("RetentionTurnDeadlineExceeded");
        using var claim = await _files.PrepareAsync(subject.Obligation, manifest, null,
            Minimum(_retention.FileTimeout, remaining), token).ConfigureAwait(false);
        var intent = Fact(subject.Obligation, subject, EvidenceRetentionEventKind.DeleteIntent,
            Guid.NewGuid(), "RetentionDeletionIntent", claim.Descriptor);
        var written = await AppendClaimAsync(intent, claim, token).ConfigureAwait(false);
        if (!written.Committed)
        {
            // Hold/Extend or unfinished work may win the writer's transaction. No bytes
            // have been touched, and the next turn will read the new authoritative state.
            if (written.ReasonCode is "RetentionDeletionNotEligible" or "RetentionSubjectRevisionInvalid" or
                "RetentionInspectionLifecycleUnfinished" or "RetentionRequiredDeliveryUnfinished" or
                "RetentionImageWorkUnfinished" or "RetentionClockRollback") return;
            throw new IOException(written.ReasonCode);
        }
        var state = new EvidenceRetentionReplay.SubjectState(subject.Obligation)
        { Revision = written.Record!.Payload.AggregateSequence, RevisionHash = written.Record.ContentHash,
            DeleteIntent = written.Record };
        await DeleteOwnedAsync(state, claim).ConfigureAwait(false);
    }

    private async Task CompleteDeletionAsync(EvidenceRetentionReplay.SubjectState subject, CancellationToken token)
    {
        var manifest = await _query.ReadManifestAsync(subject.Obligation, token).ConfigureAwait(false);
        using var claim = await _files.PrepareAsync(subject.Obligation, manifest, subject.DeleteIntent!.Payload.File,
            _retention.FileTimeout, token).ConfigureAwait(false);
        if (claim.Missing)
        {
            if (!subject.DeleteUnknown)
                RequireCommitted(await AppendClaimAsync(Fact(subject.Obligation, subject,
                    EvidenceRetentionEventKind.DeleteOutcomeUnknown, subject.DeleteIntent.Payload.OperationId,
                    "RetentionMissingAfterIntentOutcomeUnknown", claim.Descriptor), claim, CancellationToken.None).ConfigureAwait(false));
            throw new InvalidOperationException("RetentionDeletionOutcomeUnknown");
        }
        await DeleteOwnedAsync(subject, claim).ConfigureAwait(false);
    }

    private async Task DeleteOwnedAsync(EvidenceRetentionReplay.SubjectState subject, EvidenceDeletionClaim claim)
    {
        try { await _files.DeleteAsync(claim, _retention.FileTimeout, _stop.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!_files.PhysicalCompletion.IsCompleted)
                await _fault("RetentionDeletionPhysicalRetirementPending").ConfigureAwait(false);
            try { await _files.PhysicalCompletion.ConfigureAwait(false); }
            catch (Exception physical) when (physical is not OutOfMemoryException) { }
        }
        var kind = claim.Deleted ? EvidenceRetentionEventKind.Tombstone : claim.DeleteStarted
            ? EvidenceRetentionEventKind.DeleteOutcomeUnknown : EvidenceRetentionEventKind.DeleteFailed;
        RequireCommitted(await AppendClaimAsync(Fact(subject.Obligation, subject, kind,
            subject.DeleteIntent!.Payload.OperationId, kind == EvidenceRetentionEventKind.Tombstone
                ? "RetentionExactFileDeleted" : kind == EvidenceRetentionEventKind.DeleteFailed
                    ? "RetentionDeleteFailedFilePreserved" : "RetentionDeletionOutcomeUnknown", claim.Descriptor),
            claim, CancellationToken.None).ConfigureAwait(false));
        if (kind == EvidenceRetentionEventKind.DeleteOutcomeUnknown) throw new InvalidOperationException("RetentionDeletionOutcomeUnknown");
    }

    private ValueTask<EvidenceRetentionWriteResult> AppendClaimAsync(EvidenceRetentionPayload fact, EvidenceDeletionClaim claim,
        CancellationToken token) => _store.AppendRetentionAsync(fact, () =>
        {
            claim.RequireOwner(_files);
            EvidenceRetentionCodec.Require(fact.ObligationHash == claim.Obligation.ContentHash && fact.File == claim.Descriptor,
                "FileClaimMismatch");
            EvidenceRetentionCodec.Require(fact.Kind switch
            {
                EvidenceRetentionEventKind.DeleteIntent => !claim.Missing && !claim.DeleteStarted && !claim.Deleted,
                EvidenceRetentionEventKind.Tombstone => claim.Deleted,
                EvidenceRetentionEventKind.DeleteFailed => !claim.Missing && !claim.DeleteStarted && !claim.Deleted,
                EvidenceRetentionEventKind.DeleteOutcomeUnknown => !claim.Deleted,
                _ => false
            }, "FileClaimOutcomeMismatch");
        }, new StoreDeadline(_options.CommitTimeout), token);

    private EvidenceRetentionPayload Fact(EvidenceRetentionObligation obligation, EvidenceRetentionReplay.SubjectState? state,
        EvidenceRetentionEventKind kind, Guid operation, string reason, EvidenceDeletionFile? file) =>
        new(1, Guid.NewGuid(), operation, _epoch, kind, obligation.Owner, (state?.Revision ?? 0) + 1,
            state?.RevisionHash, obligation.ContentHash, _utcNow(), reason, null, SystemPrincipalId.RetentionCleanup, null,
            null, null, null, null, null, null, file, null, _retention.ExecutionPolicy.ContentHash);
    private static TimeSpan Minimum(TimeSpan first, TimeSpan second) => first <= second ? first : second;
    private static void RequireCommitted(EvidenceRetentionWriteResult result)
    { if (!result.Committed) throw new IOException(result.ReasonCode); }
}
