using SharpInspect.Abstractions;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Evidence;

internal sealed partial class EvidenceReconciliationWorker
{
    private static EvidenceReconciliationSubject ImageSubject(ImageFinalizationReplayItem item) =>
        new(EvidenceReconciliationSubjectKind.Image, item.Position, item.Work.Manifest.InspectionId,
            item.Work.Manifest.ManifestId, item.Work.WorkId, null, null, item.Work.Manifest.ContentHash,
            item.State.LastEventContentHash ?? item.Work.ContentHash, null, null, null, null, null, null, null, null);

    private async Task ReconcileStartupImagesAsync(RunCursor run, CancellationToken token)
    {
        long after = 0; long? through = null;
        while (true)
        {
            var page = await _images!.ReadReplayAsync(after, through, true, token).ConfigureAwait(false);
            through ??= page.ThroughPosition;
            foreach (var item in page.Items)
            {
                var subject = ImageSubject(item);
                try
                {
                    if (item.State.IntegrityConflict)
                        throw new InvalidOperationException("ImageFinalizationIntegrityConflictRecorded");
                    await _files!.VerifyReconciliationMetadataAsync(item, _options.FileTimeout, token).ConfigureAwait(false);
                    if (item.State.Success is not null && item.State.CleanupState == ProductionImageCleanupState.Released)
                        continue; // Terminal history is scanned by the resumable scrubber without startup ledger growth.
                    var hasAttempt = item.Events.Any(x => x.Kind == ProductionImageFinalizationKind.AttemptStarted);
                    using var claim = await _files.ReconcileAsync(item.Work, item.State.Success, hasAttempt,
                        _options.FileTimeout, token).ConfigureAwait(false);
                    var recovered = claim.HasFinal && item.State.Success is null;
                    if (recovered)
                    {
                        var attempt = item.Events.Last(x => x.Kind == ProductionImageFinalizationKind.AttemptStarted).Attempt!;
                        if (item.State.ActiveAttemptId is { } interrupted)
                        {
                            var closed = await _store.AppendImageFinalizationOutcomeAsync(new ImageFinalizationFailureRequest(
                                item.Work.WorkId, interrupted, _epoch, DateTimeOffset.UtcNow,
                                "ImageFinalizationProcessInterrupted", ProductionImageFailureCategory.Temporary,
                                DateTimeOffset.UtcNow.AddSeconds(1), false), new(_storeOptions.CommitTimeout), token).ConfigureAwait(false);
                            RequireImageCommit(closed);
                        }
                        using var png = await _files.FinalizeAsync(item.Work, attempt.AttemptId,
                            attempt.TemporaryFileName, attempt.FinalFileName, true, _options.FileTimeout, token).ConfigureAwait(false);
                        var succeeded = await _store.AppendImageFinalizationOutcomeAsync(new ImageFinalizationSuccessRequest(
                            item.Work.WorkId, _epoch, DateTimeOffset.UtcNow, png),
                            new(_storeOptions.CommitTimeout), token).ConfigureAwait(false);
                        RequireImageCommit(succeeded);
                        subject = subject with { ObservedLifecycleHash = succeeded.Event!.ContentHash };
                    }
                    subject = WithImageObservation(subject, claim);
                    await AppendPageAsync(run, subject, recovered ? EvidenceReconciliationEventKind.ImageFinalRecovered :
                        EvidenceReconciliationEventKind.ImageVerified, recovered ? "BoundFinalRecovered" :
                            claim.HasFinal ? "ImageCleanupQueued" : "ImageFinalizationQueued", 0,
                        claim.VerifyCommitProtection, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (!IsYield(error) && error is not EvidencePersistenceException and not OutOfMemoryException)
                {
                    await RecordFaultAsync(run, subject, Reason(error), 0, token).ConfigureAwait(false);
                    throw;
                }
            }
            if (page.NextAfterPosition is not { } next) break;
            after = next;
        }
    }

    private static EvidenceReconciliationSubject WithImageObservation(EvidenceReconciliationSubject subject,
        ProductionImageFinalizer.ReconciledImageClaim claim) => subject with
    {
        FileArea = claim.HasFinal ? EvidenceReconciliationFileArea.Final : EvidenceReconciliationFileArea.Stage,
        SourceFileName = claim.FileName, SourceRootBindingHash = claim.RootBindingHash,
        ByteLength = claim.ObservedLength, ObservedContentHash = claim.CanonicalPixelHash
    };
    private static void RequireImageCommit(ImageFinalizationWriteResult result)
    { if (!result.Committed) throw new EvidencePersistenceException(result.ReasonCode); }
}
