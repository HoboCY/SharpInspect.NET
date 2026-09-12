using SharpInspect.Abstractions;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [Trait("VerificationId", "V151_S10")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V151_S10_RealObligationHonorsCommitFencesIdempotencyAndIntegrityLatch(bool integrityFailure)
    {
        using var root = new ProductionImageTestRoot();
        using var final = new ProductionImageFinalizationTestRoot(root.Options,
            maximumRetryDelay: TimeSpan.FromSeconds(20));
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V151.Transaction", "1", EvidenceCaptureMode.All));
        using var issuer = new ProductionTestIssuer();
        var staged = await StageDurableProductionImageAsync(root, peer, harness, issuer);
        var target = await MigrateFinalizationStoreAsync(harness, final.Options);
        await using var store = new SqliteCommandStore(target);
        var initialized = await store.Initialization;
        Assert.True(initialized.Committed, initialized.ReasonCode);
        var query = new SqliteProductionImageEvidenceQuery(target);
        var work = staged.Work;
        var epoch = Guid.NewGuid();
        StoreDeadline Deadline() => new(TimeSpan.FromSeconds(5));
        var start = new ImageFinalizationAttemptStartRequest(work.WorkId, Guid.NewGuid(), 1, epoch,
            DateTimeOffset.UtcNow, false);
        var begun = await store.BeginImageFinalizationAttemptAsync(start, Deadline());
        Assert.True(begun.Committed, begun.ReasonCode);
        var duplicateStart = await store.BeginImageFinalizationAttemptAsync(start, Deadline());
        Assert.True(duplicateStart.Committed, duplicateStart.ReasonCode);
        Assert.Equal(begun.Event!.ContentHash, duplicateStart.Event!.ContentHash);
        var prematureRelease = await store.RecordImageStageReleasedAsync(new(work.WorkId, epoch,
            DateTimeOffset.UtcNow, "ImageFinalizationStageReleased"), Deadline());
        Assert.False(prematureRelease.Committed);
        Assert.True(File.Exists(staged.StagePath));

        // A failed attempt can leave a fully verified final file after rename, while
        // Succeeded still has not committed. Both halves are exercised against real SQL.
        var files = FinalizerFor(final.Options);
        using var claim = await files.FinalizeAsync(work, start.AttemptId, begun.Event.Attempt!.TemporaryFileName,
            begun.Event.Attempt.FinalFileName, false, TimeSpan.FromSeconds(5));
        var occurred = DateTimeOffset.UtcNow;
        var failed = await store.AppendImageFinalizationOutcomeAsync(new ImageFinalizationFailureRequest(work.WorkId,
            start.AttemptId, epoch, occurred, "V151.InjectedFailure", integrityFailure
                ? ProductionImageFailureCategory.Integrity : ProductionImageFailureCategory.Temporary,
            integrityFailure ? null : occurred.AddSeconds(20), false), Deadline());
        Assert.True(failed.Committed, failed.ReasonCode);
        Assert.Equal(1, (await query.ReadBacklogAsync()).Count);
        var forgedClock = await store.BeginImageFinalizationAttemptAsync(new(work.WorkId, Guid.NewGuid(), 2,
            epoch, DateTimeOffset.UtcNow.AddHours(1), false), Deadline());
        Assert.False(forgedClock.Committed);
        Assert.Equal(integrityFailure ? "ImageFinalizationIntegrityConflictRecorded" :
            "ImageFinalizationRetryWindowOpen", forgedClock.ReasonCode);
        var bypass = await store.BeginImageFinalizationAttemptAsync(new(work.WorkId, Guid.NewGuid(), 2,
            epoch, DateTimeOffset.UtcNow, true), Deadline());
        Assert.False(bypass.Committed);
        Assert.Equal("ImageFinalizationGovernedRecoveryUnavailable", bypass.ReasonCode);
        var before = await ReadSingleAsync(query, work.WorkId);
        Assert.Equal(2, before.Events.Count);
        var successRequest = new ImageFinalizationSuccessRequest(work.WorkId, epoch, DateTimeOffset.UtcNow, claim);
        if (integrityFailure)
        {
            var refused = await store.AppendImageFinalizationOutcomeAsync(successRequest, Deadline());
            Assert.False(refused.Committed);
            Assert.Equal("ImageFinalizationIntegrityConflictRecorded", refused.ReasonCode);
            Assert.False(claim.IsConsumed);
            Assert.True(File.Exists(staged.StagePath));
            Assert.Equal(2, (await ReadSingleAsync(query, work.WorkId)).Events.Count);
            return;
        }

        var guarded = await store.AppendImageFinalizationOutcomeAsync(successRequest with
            { FinalGuard = () => "V151.CommitFenceRejected" }, Deadline());
        Assert.False(guarded.Committed);
        Assert.Equal("V151.CommitFenceRejected", guarded.ReasonCode);
        Assert.False(claim.IsConsumed);
        Assert.True(File.Exists(staged.StagePath));
        Assert.Equal(2, (await ReadSingleAsync(query, work.WorkId)).Events.Count);
        var succeeded = await store.AppendImageFinalizationOutcomeAsync(successRequest, Deadline());
        Assert.True(succeeded.Committed, succeeded.ReasonCode);
        Assert.True(claim.IsConsumed);
        Assert.Equal(0, succeeded.Backlog!.Count);
        Assert.True(File.Exists(staged.StagePath)); // SQL success precedes physical stage cleanup.
        var repeatedSuccess = await store.AppendImageFinalizationOutcomeAsync(successRequest, Deadline());
        Assert.True(repeatedSuccess.Committed, repeatedSuccess.ReasonCode);
        Assert.Equal(succeeded.Event!.ContentHash, repeatedSuccess.Event!.ContentHash);
        Assert.True(await files.ReleaseSucceededStageAsync(work, succeeded.Event.Success!, TimeSpan.FromSeconds(5), default));
        var release = new ImageFinalizationReleaseRequest(work.WorkId, epoch, DateTimeOffset.UtcNow,
            "ImageFinalizationStageReleased");
        var released = await store.RecordImageStageReleasedAsync(release, Deadline());
        Assert.True(released.Committed, released.ReasonCode);
        var repeatedRelease = await store.RecordImageStageReleasedAsync(release, Deadline());
        Assert.True(repeatedRelease.Committed, repeatedRelease.ReasonCode);
        var record = await ReadSingleAsync(query, work.WorkId);
        Assert.Equal(4, record.Events.Count);
        Assert.Equal(ProductionImageFinalizationState.Succeeded, record.State.State);
        Assert.Equal(ProductionImageCleanupState.Released, record.State.CleanupState);
        Assert.False(File.Exists(staged.StagePath));
        var core = await new SqliteProductionInspectionHistoryQuery(target).ReadAsync(work.Manifest.InspectionId);
        Assert.True(core.Available, core.ReasonCode);
        Assert.Equal(staged.CoreContentHash, core.Latest!.Core!.ContentHash);
    }
}
