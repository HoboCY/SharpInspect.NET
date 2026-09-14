using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Evidence;

internal sealed partial class EvidenceReconciliationWorker
{
    private int _scrubBusy;
    private EvidenceReconciliationStream? _nextScrubStream;
    internal async Task RunScrubTurnAsync(CancellationToken token = default)
    {
        if (!_startup.Task.IsCompletedSuccessfully || !_canScrub() || _files?.PhysicalCompletion.IsCompleted == false ||
            Interlocked.CompareExchange(ref _scrubBusy, 1, 0) != 0) return;
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        turn.CancelAfter(_options.Scrubber.MaximumRunTime);
        var budget = _options.Scrubber;
        try
        {
            var state = await _query.ReadStateAsync(turn.Token).ConfigureAwait(false);
            if (state.Replay.IntegrityFault) throw new InvalidOperationException("EvidenceReconciliationIntegrityFaultRecorded");
            var previous = state.Replay.Latest(EvidenceReconciliationPhase.HistoricalScrub);
            var stream = _images is null ? EvidenceReconciliationStream.Outbox :
                _storeOptions.Outbox is null ? EvidenceReconciliationStream.Images :
                _nextScrubStream ?? (previous?.Stream == EvidenceReconciliationStream.Images ?
                    EvidenceReconciliationStream.Outbox : EvidenceReconciliationStream.Images);
            // Rotate even when this turn yields without a checkpoint. A persistently
            // slow image must not starve independently verifiable Outbox history.
            _nextScrubStream = stream == EvidenceReconciliationStream.Images ?
                EvidenceReconciliationStream.Outbox : EvidenceReconciliationStream.Images;
            var kind = stream == EvidenceReconciliationStream.Images
                ? EvidenceReconciliationSubjectKind.Image : EvidenceReconciliationSubjectKind.Outbox;
            var pending = state.Replay.Runs.SingleOrDefault(x => x.Stream == stream && !x.Completed &&
                x.Phase == EvidenceReconciliationPhase.HistoricalScrub);
            RunCursor run;
            if (pending is not null)
            {
                var start = state.Rows.First(x => x.Payload.RunId == pending.RunId).Payload;
                run = new(start, pending.CheckpointHash, pending.AfterSourcePosition);
            }
            else
            {
                var through = await _query.ReadSourceTailAsync(kind, turn.Token).ConfigureAwait(false);
                run = await StartRunAsync(stream, through, turn.Token).ConfigureAwait(false);
            }
            long readBytes = 0;
            for (var count = 0; count < budget.MaximumItems && _canScrub(); count++)
            {
                turn.Token.ThrowIfCancellationRequested();
                if (run.After == run.Template.ThroughSourcePosition)
                { await CompleteRunAsync(run, turn.Token).ConfigureAwait(false); return; }
                var sources = await _query.ReadSourcesAsync(kind, run.After, run.Template.ThroughSourcePosition, 1, turn.Token)
                    .ConfigureAwait(false);
                if (sources.Count == 0)
                { await CompleteRunAsync(run, turn.Token).ConfigureAwait(false); return; }
                var source = sources[0];
                var after = source.SourcePosition!.Value;
                if (kind == EvidenceReconciliationSubjectKind.Outbox)
                {
                    await AppendPageAsync(run, source, EvidenceReconciliationEventKind.OutboxVerified,
                        "HistoricalOutboxLocalEvidenceVerified", after, null, turn.Token).ConfigureAwait(false);
                    continue;
                }
                var images = _storeOptions.ImageFinalization!;
                var maximumRead = checked(images.FinalRoot.MaximumFinalFileBytes + images.ImageEvidence.Stage.MaximumStageBytes);
                if (maximumRead > budget.MaximumBytes - readBytes) return;
                var page = await _images!.ReadReplayAsync(after - 1, after, true, turn.Token).ConfigureAwait(false);
                var item = page.Items.Single(x => x.Work.WorkId == source.WorkId);
                if (ImageSubject(item).ObservedLifecycleHash != source.ObservedLifecycleHash)
                    throw new EvidencePersistenceException("EvidenceReconciliationSourceChanged");
                if (item.State.Success is null || item.State.CleanupState != ProductionImageCleanupState.Released)
                {
                    await AppendPageAsync(run, source, EvidenceReconciliationEventKind.WorkDeferred,
                        "ActiveImageWorkDeferred", after, null, turn.Token).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    using var claim = await _files!.ReconcileAsync(item.Work, item.State.Success, true,
                        _options.FileTimeout, turn.Token).ConfigureAwait(false);
                    readBytes = checked(readBytes + claim.ReadBytes);
                    if (readBytes > budget.MaximumBytes)
                        throw new InvalidOperationException("EvidenceReconciliationReadBudgetExceeded");
                    await AppendPageAsync(run, WithImageObservation(source, claim),
                        EvidenceReconciliationEventKind.ImageVerified, "RetainedImageDecodedAndHashVerified",
                        after, claim.VerifyCommitProtection, turn.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (turn.IsCancellationRequested) { throw; }
                catch (Exception error) when (!IsYield(error) && error is not EvidencePersistenceException and not OutOfMemoryException)
                {
                    await RecordFaultAsync(run, source, Reason(error), after, turn.Token).ConfigureAwait(false);
                    throw;
                }
            }
            if (run.After == run.Template.ThroughSourcePosition)
                await CompleteRunAsync(run, turn.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (turn.IsCancellationRequested && !token.IsCancellationRequested) { }
        catch (Exception error) when (IsYield(error)) { }
        finally { Volatile.Write(ref _scrubBusy, 0); }
    }
}
