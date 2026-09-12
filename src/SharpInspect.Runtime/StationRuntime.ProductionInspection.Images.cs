using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cycles;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private bool _productionImageStartupVerified;
    private int _productionPendingImages;
    private long _productionPendingImageBytes;
    private DateTimeOffset? _productionOldestPendingImage;

    private async Task VerifyProductionImageStartupAsync(CancellationToken token)
    {
        if (_productionInspectionStoreOptions?.ImageEvidence is not { } options) return;
        if (_imageFinalizationWorker is { } worker)
        {
            await worker.Startup.WaitAsync(token).ConfigureAwait(false);
            lock (_sync)
            {
                if (_imageFinalizationFaulted) throw new InvalidOperationException("ProductionImageIntegrityFault");
                if (!IsProductionImageAlarmMappingValid() || !IsProductionImageBacklogAlarmMappingValid())
                    throw new InvalidOperationException("ProductionImageIntegrityAlarmMappingUnavailable");
                _productionImageStartupVerified = true;
            }
            return;
        }
        var query = new SqlitePendingImageWorkQuery(_productionInspectionStoreOptions);
        long after = 0;
        long? through = null;
        var count = 0;
        long bytes = 0;
        DateTimeOffset? oldest = null;
        do
        {
            var page = await query.QueryAsync(after, through, 128, token).ConfigureAwait(false);
            if (!page.Available) throw new InvalidOperationException(page.ReasonCode);
            through ??= page.ThroughPosition;
            foreach (var item in page.Items)
            {
                count = checked(count + 1);
                bytes = checked(bytes + item.Manifest.CanonicalByteLength);
                if (oldest is null || item.Manifest.CreatedAtUtc < oldest) oldest = item.Manifest.CreatedAtUtc;
            }
            if (page.NextAfterPosition is not { } next) break;
            if (next <= after) throw new InvalidOperationException("ProductionImageWorkCursorInvalid");
            after = next;
        } while (true);
        lock (_sync)
        {
            _productionPendingImages = count;
            _productionPendingImageBytes = bytes;
            _productionOldestPendingImage = oldest;
            PublishLocked(_snapshot with { Evidence = _snapshot.Evidence with { PendingRequiredImages = count } });
        }
        // T50 never turns a pre-existing stage or pending obligation into recovery
        // evidence. The later reconciliation capability must resolve these first.
        var hasFiles = await Task.Run(() => Directory.EnumerateFileSystemEntries(options.Stage.StageRoot).Any(), token)
            .WaitAsync(_productionInspectionOptions!.OperationTimeout, token).ConfigureAwait(false);
        if (count != 0 || hasFiles) throw new InvalidOperationException("ProductionImageStartupReconciliationRequired");
        lock (_sync) _productionImageStartupVerified = true;
    }

    private bool ProductionImageBacklogReadyLocked(StationStateSnapshot state)
    {
        if (_productionInspectionOptions?.ImageStage is null) return state.Evidence.PendingRequiredImages == 0;
        if (_imageFinalizationWorker is not null &&
            (_imageFinalizationFaulted || !IsProductionImageAlarmMappingValid() ||
             !IsProductionImageBacklogAlarmMappingValid())) return false;
        if (!_productionImageStartupVerified || state.Evidence.PendingRequiredImages != _productionPendingImages ||
            _productionInspectionPolicy is not { } policy) return false;
        return !ProductionImageBacklogExceededLocked();
    }

    private void ProjectCommittedProductionImage(ProductionInspectionCore core, long auditSequence)
    {
        if (core.ImageEvidence?.Manifest is not { } manifest) return;
        if (_imageFinalizationWorker is { } worker)
        {
            lock (_sync)
            {
                if (auditSequence <= 0) throw new InvalidOperationException("ProductionImageCoreWatermarkRequired");
                if (auditSequence > _imageBacklogSnapshot.ThroughAuditSequence)
                    _imageBacklogUnobservedCores[core.ImageEvidence.Work!.WorkId] = (auditSequence, manifest);
                ProjectImageBacklogLocked();
            }
            worker.Wake();
            return;
        }
        lock (_sync)
        {
            _productionPendingImages = checked(_productionPendingImages + 1);
            _productionPendingImageBytes = checked(_productionPendingImageBytes + manifest.CanonicalByteLength);
            if (_productionOldestPendingImage is null || manifest.CreatedAtUtc < _productionOldestPendingImage)
                _productionOldestPendingImage = manifest.CreatedAtUtc;
            PublishLocked(_snapshot with { Evidence = _snapshot.Evidence with
                { PendingRequiredImages = _productionPendingImages } });
        }
    }

    private EvidenceCapturePolicySnapshot? ResolveProductionCapturePolicy(RecipeDraftContent recipe) =>
        ProductionImageEvidenceBinding.Resolve(_productionInspectionOptions!, _productionInspectionStoreOptions!, recipe);

    private async Task<(ProductionImageEvidenceSnapshot? Evidence, ProductionImageStager.StageCommitClaim? Claim)>
        PrepareProductionImageEvidenceAsync(ProductionInspectionOwner owner,
            InspectionCycleExecutionResult<PlcResultPayloadSnapshot> result)
    {
        var admission = owner.Current ?? throw new InvalidOperationException("ProductionInspectionAdmissionMissing");
        var policy = admission.EvidenceCapturePolicy;
        if (policy is null) return (null, null);
        if (result.Metadata is null)
        {
            if (owner.ImageInput is not null || result.AcquisitionFailure is null)
                throw new InvalidOperationException("ProductionImageAcquisitionEvidenceMissing");
            return (new(ProductionImageEvidenceState.NotAvailable, result.AcquisitionFailure.ReasonCode), null);
        }
        if (!policy.RequiresImage(true, result.Status, result.Payload!.Decision))
        {
            owner.ImageInput?.Dispose();
            owner.ImageInput = null;
            return (new(ProductionImageEvidenceState.NotRequired, policy.Mode == EvidenceCaptureMode.None ?
                "ProductionEvidenceCaptureNone" : "ProductionEvidenceCapturePassingFrameNotRequired"), null);
        }
        var stager = _productionImageStager ?? throw new InvalidOperationException("ProductionImageStageConfigurationRequired");
        var input = owner.ImageInput ?? throw new InvalidOperationException("ProductionImageInputNotRetained");
        owner.ImageInput = null;
        // The stager takes ownership on call. In particular, a semantic timeout
        // never releases the input while its physical I/O task is still using it.
        var claim = await stager.StageAsync(admission.InspectionId, admission.ContentHash, policy.ContentHash,
            input, admission.TracePolicySnapshot.Policy.EvidenceStageTimeout, owner.Cancellation.Token).ConfigureAwait(false);
        try
        {
            var rule = admission.TracePolicySnapshot.RetentionRules.Single(value =>
                value.EvidenceClass == TraceRetentionClass.AuthoritativeImage);
            var manifest = new PendingImageManifest(Guid.NewGuid(), admission.InspectionId, admission.ContentHash,
                policy.ContentHash, claim.StageId, claim.InputLeaseId, claim.StageRootBindingHash,
                claim.StageFileName, claim.Metadata.Width, claim.Metadata.Height, claim.Metadata.PixelFormat,
                claim.Metadata.ValidBits, claim.CanonicalPixelHash, claim.CanonicalByteLength,
                ProductionInspectionCore.FrameHash(claim.Metadata)!, ProductionInspectionCore.ProvenanceHash(claim.Provenance)!,
                admission.TracePolicySnapshot.ContentHash, rule.ContentHash, DateTimeOffset.UtcNow);
            return (new(ProductionImageEvidenceState.Pending, "ProductionImageStagedPendingFinalization",
                new PendingImageFinalizationWork(Guid.NewGuid(), manifest)), claim);
        }
        catch { claim.Dispose(); throw; }
    }
}
