using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal enum ProductionImageCommitBoundary { AfterCore, AfterManifest, AfterWork, BeforeCommit }

internal sealed partial class SqliteCommandStore
{
    internal Action<ProductionImageCommitBoundary>? ImageEvidenceCommitObserver { get; set; }

    private void EnsureImageEvidenceAdmissionCapacity(sqlite3 database, ProductionInspectionAdmission admission,
        StoreDeadline deadline)
    {
        if (admission.EvidenceCapturePolicy is null) return;
        var options = _options.ImageEvidence ?? throw new InvalidOperationException("ProductionImageEvidenceConfigurationRequired");
        RequireConfiguredImageEvidence(database, options, deadline);
        if (admission.EvidenceCapturePolicy.Mode == EvidenceCaptureMode.None) return;
        var rows = ReadProductionInspectionRows(database, _options.ProductionInspections!, deadline);
        var reserved = CountImageReservations(rows);
        var used = ReadImageUsage(database, deadline);
        var backlog = admission.TracePolicySnapshot.Policy.ImageBacklog;
        var imageStates = _options.ImageFinalization is { } finalization
            ? ReadImageFinalizationObligationStates(database, finalization, deadline)
            : Array.Empty<ProductionImageFinalizationWorkState>();
        AuditChainDatabase.Require(!imageStates.Any(state => state.IntegrityConflict),
            "ProductionImageIntegrityConflictRecorded");
        var succeeded = imageStates.Where(state => state.State == ProductionImageFinalizationState.Succeeded)
            .Select(state => state.WorkId).ToHashSet();
        var pendingImages = rows.Where(value => value.Event.Kind == ProductionInspectionEventKind.CoreCommitted &&
                value.Event.Core?.ImageEvidence?.Work is { } work && !succeeded.Contains(work.WorkId))
            .Select(value => value.Event.Core!.ImageEvidence!.Manifest!).ToArray();
        var pendingAdmissions = rows.GroupBy(value => value.Event.InspectionId)
            .Where(group => group.Last().Event.Kind == ProductionInspectionEventKind.Admitted &&
                group.First().Event.Admission.EvidenceCapturePolicy is { Mode: not EvidenceCaptureMode.None })
            .Select(group => group.First().Event.Admission).ToArray();
        var expectedBytes = checked(pendingImages.Sum(value => value!.CanonicalByteLength) +
            pendingAdmissions.Sum(ExpectedCanonicalImageBytes) + ExpectedCanonicalImageBytes(admission));
        AuditChainDatabase.Require(checked(pendingImages.LongLength + reserved + 1) <= backlog.MaximumItems &&
            expectedBytes <= backlog.MaximumBytes && pendingImages.All(value =>
                DateTimeOffset.UtcNow - value!.CreatedAtUtc <= backlog.MaximumOldestAge),
            "ProductionImageBacklogLimitExceeded");
        AuditChainDatabase.Require(checked(used.Count + reserved + 1) <= options.MaxImages,
            "ProductionImageEvidenceEntryCapacityExceeded");
        AuditChainDatabase.Require(checked(used.Bytes + (reserved + 1) * 2 *
            ProductionInspectionStoredPayloadBytes(options.MaximumPayloadBytes)) <= options.MaxTotalBytes,
            "ProductionImageEvidenceTotalCapacityExceeded");
        // Every obligation this admission will create must still be able to write its future
        // Succeeded and StageReleased facts, in this ledger and in the shared central audit.
        EnsureImageFinalizationReserveCapacity(database, checked(reserved + 1), deadline);
    }

    private static long CountImageReservations(IReadOnlyList<ProductionInspectionStoredRow> rows) =>
        rows.GroupBy(value => value.Event.InspectionId).LongCount(group =>
            group.Last().Event.Kind == ProductionInspectionEventKind.Admitted &&
            group.First().Event.Admission.EvidenceCapturePolicy is { Mode: not EvidenceCaptureMode.None });

    private static long ExpectedCanonicalImageBytes(ProductionInspectionAdmission admission)
    {
        var camera = admission.ActivationSnapshot.Release.Source.Content.Camera;
        return checked(32L + (long)camera.RegionOfInterest.Width * camera.RegionOfInterest.Height *
            (camera.PixelFormat == VisionPixelFormat.Mono8 ? 1 : camera.PixelFormat == VisionPixelFormat.Mono16 ? 2 : 3));
    }

    private static (long Count, long Bytes) ReadImageUsage(sqlite3 database, StoreDeadline deadline) =>
        (AuditChainDatabase.Scalar(database, "SELECT COUNT(*) FROM pending_image_manifests;", deadline),
         checked(AuditChainDatabase.Scalar(database, "SELECT COALESCE(SUM(length(Payload)),0) FROM pending_image_manifests;", deadline) +
             AuditChainDatabase.Scalar(database, "SELECT COALESCE(SUM(length(Payload)),0) FROM pending_image_work;", deadline)));

    private void InsertProductionImageEvidence(sqlite3 database, ProductionInspectionCoreWriteRequest request,
        StoreDeadline deadline)
    {
        var evidence = request.Core.ImageEvidence;
        if (evidence is null)
        {
            AuditChainDatabase.Require(request.ImageClaim is null, "ProductionImageStageClaimUnexpected");
            return;
        }
        var options = _options.ImageEvidence ?? throw new InvalidOperationException("ProductionImageEvidenceConfigurationRequired");
        RequireConfiguredImageEvidence(database, options, deadline);
        if (evidence.Work is not { } work)
        {
            AuditChainDatabase.Require(request.ImageClaim is null, "ProductionImageStageClaimUnexpected");
            return;
        }
        RequireProductionImageClaim(request, options.Stage.ContentHash);
        ImageEvidenceCommitObserver?.Invoke(ProductionImageCommitBoundary.AfterCore);
        var manifest = work.Manifest;
        var manifestPayload = ProductionInspectionStorageCodec.EncodeImageManifest(manifest);
        var workPayload = ProductionInspectionStorageCodec.EncodeImageWork(work);
        AuditChainDatabase.Require(manifestPayload.Length <= options.MaximumPayloadBytes && workPayload.Length <= options.MaximumPayloadBytes,
            "ProductionImageEvidencePayloadCapacityExceeded");
        var rows = ReadProductionInspectionRows(database, _options.ProductionInspections!, deadline);
        // This Core's admission owns one reservation until its Core event is appended.
        var reserved = Math.Max(0, CountImageReservations(rows) - 1);
        var used = ReadImageUsage(database, deadline);
        AuditChainDatabase.Require(checked(used.Count + reserved + 1) <= options.MaxImages &&
            checked(used.Bytes + ProductionInspectionStoredPayloadBytes(manifestPayload.Length) +
                ProductionInspectionStoredPayloadBytes(workPayload.Length) + reserved * 2 *
                ProductionInspectionStoredPayloadBytes(options.MaximumPayloadBytes)) <= options.MaxTotalBytes,
            "ProductionImageEvidenceCapacityExceeded");
        EnsureImageFinalizationReserveCapacity(database, checked(reserved + 1), deadline);
        AuditChainDatabase.Execute(database, @"INSERT INTO pending_image_manifests
            (ManifestId,InspectionId,StageId,EvidencePolicyHash,ContentHash,PayloadHash,Payload) VALUES(?,?,?,?,?,?,?);", deadline,
            manifest.ManifestId.ToString("D"), manifest.InspectionId.ToString("D"), manifest.StageId.ToString("D"),
            manifest.EvidencePolicyContentHash, manifest.ContentHash, ProductionInspectionStorageCodec.PayloadHash(manifestPayload),
            Convert.ToBase64String(manifestPayload));
        ImageEvidenceCommitObserver?.Invoke(ProductionImageCommitBoundary.AfterManifest);
        AuditChainDatabase.Execute(database, @"INSERT INTO pending_image_work
            (WorkId,InspectionId,ManifestId,Kind,State,ContentHash,PayloadHash,Payload) VALUES(?,?,?,?,?,?,?,?);", deadline,
            work.WorkId.ToString("D"), manifest.InspectionId.ToString("D"), manifest.ManifestId.ToString("D"),
            work.Kind, work.State, work.ContentHash, ProductionInspectionStorageCodec.PayloadHash(workPayload),
            Convert.ToBase64String(workPayload));
        ImageEvidenceCommitObserver?.Invoke(ProductionImageCommitBoundary.AfterWork);
        VerifyProductionImageProjection(database, request.Core, options.Stage.ContentHash, deadline);
    }

    private void ConsumeProductionImageClaim(ProductionInspectionCoreWriteRequest request)
    {
        if (request.Core.ImageEvidence?.Manifest is null) return;
        ImageEvidenceCommitObserver?.Invoke(ProductionImageCommitBoundary.BeforeCommit);
        var options = _options.ImageEvidence ?? throw new InvalidOperationException("ProductionImageEvidenceConfigurationRequired");
        RequireProductionImageClaim(request, options.Stage.ContentHash);
        request.ImageClaim!.ConsumeForCommit(request.Core.Admission.InspectionId, request.Core.Admission.ContentHash,
            request.Core.Admission.EvidenceCapturePolicy!.ContentHash, options.Stage.ContentHash);
        request.ImageClaim.VerifyCommitProtection();
    }

    private static void RequireProductionImageClaim(ProductionInspectionCoreWriteRequest request, string rootHash)
    {
        var manifest = request.Core.ImageEvidence?.Manifest;
        var claim = request.ImageClaim;
        AuditChainDatabase.Require(manifest is not null && claim is not null && !claim.IsConsumed &&
            claim.InspectionId == request.Core.Admission.InspectionId && claim.AdmissionContentHash == request.Core.Admission.ContentHash &&
            claim.EvidencePolicyContentHash == request.Core.Admission.EvidenceCapturePolicy?.ContentHash &&
            claim.StageRootBindingHash == rootHash && manifest!.StageRootBindingHash == rootHash &&
            claim.StageId == manifest.StageId && claim.InputLeaseId == manifest.InputLeaseId &&
            claim.StageFileName == manifest.StageFileName && claim.CanonicalPixelHash == manifest.CanonicalPixelHash &&
            claim.CanonicalByteLength == manifest.CanonicalByteLength &&
            ProductionInspectionCore.FrameHash(claim.Metadata) == manifest.InputMetadataHash &&
            ProductionInspectionCore.ProvenanceHash(claim.Provenance) == manifest.InputProvenanceHash,
            "ProductionImageStageClaimMismatch");
        claim!.VerifyCommitProtection();
    }

    internal static void ValidateImageEvidenceHistory(sqlite3 database, ProductionImageEvidenceStoreOptions options,
        ProductionInspectionStoreOptions productionOptions, StoreDeadline deadline)
    {
        RequireConfiguredImageEvidence(database, options, deadline);
        var rows = ReadProductionInspectionRows(database, productionOptions, deadline);
        ValidateProductionImageProjections(database, rows.Where(value => value.Event.Kind ==
            ProductionInspectionEventKind.CoreCommitted).Select(value => value.Event.Core!).ToArray(), deadline);
        var usage = ReadImageUsage(database, deadline);
        var reserved = CountImageReservations(rows);
        AuditChainDatabase.Require(usage.Count + reserved <= options.MaxImages &&
            checked(usage.Bytes + reserved * 2 * ProductionInspectionStoredPayloadBytes(options.MaximumPayloadBytes)) <= options.MaxTotalBytes,
            "ProductionImageEvidenceCapacityExceeded");
        var largest = AuditChainDatabase.Scalar(database, @"SELECT MAX(n) FROM (
            SELECT COALESCE(MAX(length(Payload)),0) AS n FROM pending_image_manifests UNION ALL
            SELECT COALESCE(MAX(length(Payload)),0) AS n FROM pending_image_work);", deadline);
        AuditChainDatabase.Require(largest <= ProductionInspectionStoredPayloadBytes(options.MaximumPayloadBytes),
            "ProductionImageEvidencePayloadCapacityExceeded");
    }

    private static void ValidateProductionImageProjections(sqlite3 database, IReadOnlyList<ProductionInspectionCore> cores,
        StoreDeadline deadline)
    {
        var configured = AuditChainDatabase.Scalar(database, @"SELECT COUNT(*) FROM sqlite_master WHERE type='table'
            AND name IN ('image_evidence_store_config','pending_image_manifests','pending_image_work');", deadline);
        if (configured == 0)
        {
            AuditChainDatabase.Require(cores.All(value => value.ImageEvidence is null), "ProductionImageEvidenceSchemaRequired");
            return;
        }
        AuditChainDatabase.Require(configured == 3, "ProductionImageEvidenceSchemaIncomplete");
        var rootHash = AuditChainDatabase.Text(database,
            "SELECT StageRootBindingHash FROM image_evidence_store_config WHERE Id=1;", deadline) ??
            throw new InvalidOperationException("ProductionImageEvidenceConfigurationMissing");
        var expected = cores.Where(value => value.ImageEvidence?.Manifest is not null).ToArray();
        foreach (var core in expected) VerifyProductionImageProjection(database, core, rootHash, deadline);
        AuditChainDatabase.Require(AuditChainDatabase.Scalar(database, "SELECT COUNT(*) FROM pending_image_manifests;", deadline) == expected.Length &&
            AuditChainDatabase.Scalar(database, "SELECT COUNT(*) FROM pending_image_work;", deadline) == expected.Length,
            "ProductionImageEvidenceProjectionCountMismatch");
    }

    private static void VerifyProductionImageProjection(sqlite3 database, ProductionInspectionCore core,
        string rootHash, StoreDeadline deadline)
    {
        var work = core.ImageEvidence!.Work!;
        var manifest = work.Manifest;
        AuditChainDatabase.Require(manifest.StageRootBindingHash == rootHash, "ProductionImageEvidenceRootBindingMismatch");
        var payload = ProductionInspectionStorageCodec.EncodeImageManifest(manifest);
        RequireProductionProjection(database, "pending_image_manifests", new[]
        { "ManifestId", "InspectionId", "StageId", "EvidencePolicyHash", "ContentHash", "PayloadHash", "Payload" }, new[]
        { manifest.ManifestId.ToString("D"), manifest.InspectionId.ToString("D"), manifest.StageId.ToString("D"),
            manifest.EvidencePolicyContentHash, manifest.ContentHash, ProductionInspectionStorageCodec.PayloadHash(payload), Convert.ToBase64String(payload) },
            manifest.InspectionId, deadline, "ProductionImageManifestProjectionMismatch");
        payload = ProductionInspectionStorageCodec.EncodeImageWork(work);
        RequireProductionProjection(database, "pending_image_work", new[]
        { "WorkId", "InspectionId", "ManifestId", "Kind", "State", "ContentHash", "PayloadHash", "Payload" }, new[]
        { work.WorkId.ToString("D"), manifest.InspectionId.ToString("D"), manifest.ManifestId.ToString("D"), work.Kind, work.State,
            work.ContentHash, ProductionInspectionStorageCodec.PayloadHash(payload), Convert.ToBase64String(payload) },
            manifest.InspectionId, deadline, "ProductionImageWorkProjectionMismatch");
    }
}
