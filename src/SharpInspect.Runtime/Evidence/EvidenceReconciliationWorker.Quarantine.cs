using SharpInspect.Abstractions;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Evidence;

internal sealed partial class EvidenceReconciliationWorker
{
    private EvidenceQuarantine? _stageQuarantine;
    private EvidenceQuarantine? _finalQuarantine;
    private EvidenceQuarantine Quarantine(EvidenceReconciliationFileArea area)
    {
        ref var cached = ref (area == EvidenceReconciliationFileArea.Stage ? ref _stageQuarantine : ref _finalQuarantine);
        if (cached is not null) return cached;
        var images = _storeOptions.ImageFinalization!;
        cached = area == EvidenceReconciliationFileArea.Stage
            ? new(images.ImageEvidence.Stage.StageRoot, images.ImageEvidence.Stage.ContentHash, _options.StageQuarantine!)
            : new(images.FinalRoot.FinalRoot, images.FinalRootBindingHash, _options.FinalQuarantine!);
        _quarantines.Add(cached);
        return cached;
    }
    private static QuarantinedFileDescriptor Descriptor(EvidenceReconciliationSubject subject) =>
        new(subject.OrphanId!.Value, subject.SourceFileName!, subject.QuarantineFileName!,
            subject.SourceRootBindingHash!, subject.QuarantineRootBindingHash!, subject.FileIdentityHash!,
            subject.ByteLength!.Value, subject.ObservedContentHash!);
    private static EvidenceReconciliationSubject OrphanSubject(EvidenceReconciliationFileArea area,
        QuarantinedFileDescriptor file) => new(EvidenceReconciliationSubjectKind.Orphan, null, null, null, null, null,
            file.OrphanId, null, null, area, file.SourceFileName, file.SourceRootBindingHash,
            file.QuarantineFileName, file.QuarantineRootBindingHash, file.FileIdentityHash, file.ByteLength, file.RawContentHash);

    private async Task ReconcileQuarantinesAsync(RunCursor run, EvidenceReconciliationReadState prior, CancellationToken token)
    {
        foreach (var completed in prior.Rows.Where(x => x.Payload.Kind == EvidenceReconciliationEventKind.Quarantined))
        {
            var subject = completed.Payload.Subject!;
            try
            {
                using var retired = await VerifyRetainedAbsenceAsync(new(EvidenceRetentionOwnerKind.QuarantinedFile,
                    subject.OrphanId!.Value), null, token).ConfigureAwait(false);
                if (retired is not null) continue;
                using var protectedFile = await Quarantine(subject.FileArea!.Value)
                    .VerifyCompletedAsync(Descriptor(subject), _options.FileTimeout, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException && !token.IsCancellationRequested)
            {
                await RecordFaultAsync(run, subject, Reason(error), 0, token).ConfigureAwait(false);
                throw;
            }
        }
        foreach (var pending in prior.Replay.PendingQuarantines.Values)
        {
            var subject = pending.Payload.Subject!;
            try
            {
                using var claim = await Quarantine(subject.FileArea!.Value)
                    .RecoverAsync(Descriptor(subject), _options.FileTimeout, token).ConfigureAwait(false);
                await AppendOrphanAsync(run, subject, EvidenceReconciliationEventKind.Quarantined,
                    "OrphanQuarantineRecovered", claim.VerifyCommitProtection, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException && !token.IsCancellationRequested)
            {
                await RecordFaultAsync(run, subject, Reason(error), 0, token).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task ReconcileInventoryAsync(RunCursor run, CancellationToken token)
    {
        var stage = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var final = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long after = 0; long? through = null;
        while (true)
        {
            var page = await _images!.ReadReplayAsync(after, through, true, token).ConfigureAwait(false);
            through ??= page.ThroughPosition;
            foreach (var item in page.Items)
            {
                if (!stage.Add(item.Work.Manifest.StageFileName))
                    throw new InvalidOperationException("EvidenceReconciliationDuplicateStageReference");
                foreach (var attempt in item.Events.Where(x => x.Kind == ProductionImageFinalizationKind.AttemptStarted))
                {
                    final.Add(attempt.Attempt!.FinalFileName);
                    if (!final.Add(attempt.Attempt.TemporaryFileName))
                        throw new InvalidOperationException("EvidenceReconciliationDuplicateTemporaryReference");
                }
            }
            if (page.NextAfterPosition is not { } next) break;
            after = next;
        }
        var images = _storeOptions.ImageFinalization!;
        foreach (var area in new[] { EvidenceReconciliationFileArea.Stage, EvidenceReconciliationFileArea.Final })
        {
            var isStage = area == EvidenceReconciliationFileArea.Stage;
            var names = await _files!.FindUnownedFilesAsync(
                isStage ? images.ImageEvidence.Stage.StageRoot : images.FinalRoot.FinalRoot,
                isStage ? stage : final,
                isStage ? images.ImageEvidence.Stage.MaximumStageFiles : images.FinalRoot.MaximumFinalFiles,
                isStage ? images.ImageEvidence.Stage.MaximumTotalStageBytes : images.FinalRoot.MaximumTotalFinalBytes,
                _options.FileTimeout, token).ConfigureAwait(false);
            foreach (var name in names)
            {
                var orphanId = Guid.NewGuid();
                var subject = new EvidenceReconciliationSubject(EvidenceReconciliationSubjectKind.Orphan,
                    null, null, null, null, null, orphanId, null, null, area, name,
                    isStage ? images.ImageEvidence.Stage.ContentHash : images.FinalRootBindingHash,
                    null, null, null, null, null);
                try
                {
                    var quarantine = Quarantine(area);
                    using var claim = await quarantine.PrepareAsync(orphanId, name, _options.FileTimeout, token).ConfigureAwait(false);
                    subject = OrphanSubject(area, claim.Descriptor);
                    await AppendOrphanAsync(run, subject, EvidenceReconciliationEventKind.QuarantineIntent,
                        "UnreferencedFileQuarantineIntent", claim.VerifyCommitProtection, token).ConfigureAwait(false);
                    await quarantine.MoveAsync(claim, _options.FileTimeout, token).ConfigureAwait(false);
                    await AppendOrphanAsync(run, subject, EvidenceReconciliationEventKind.Quarantined,
                        "UnreferencedFileQuarantined", claim.VerifyCommitProtection, token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OutOfMemoryException && !token.IsCancellationRequested)
                {
                    await RecordFaultAsync(run, subject, Reason(error), 0, token).ConfigureAwait(false);
                    throw;
                }
            }
        }
    }
    private Task<IReadOnlyList<EvidenceReconciliationStoredRow>> AppendOrphanAsync(RunCursor run,
        EvidenceReconciliationSubject subject, EvidenceReconciliationEventKind kind, string reason,
        Action proof, CancellationToken token) => AppendAsync(new[] { run.Template with
        { EventId = Guid.NewGuid(), Kind = kind, RecordedAtUtc = DateTimeOffset.UtcNow,
            Subject = subject, ReasonCode = reason, AfterSourcePosition = run.After } }, proof, token);
}
