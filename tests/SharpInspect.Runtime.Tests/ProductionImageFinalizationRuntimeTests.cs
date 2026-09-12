using SharpInspect.Abstractions;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Bounded T51 runtime coverage for the opt-in schema-35 production image finalization
/// lifecycle. Every case uses the real Runtime, the real SQLite writer, the real canonical
/// PNG codec and the virtual camera: one case proves the fully opted-in station finalizes the
/// exact canonical PNG of a real PLC cycle and admits the next cycle, the other cases prove a
/// durable schema-34 T50 stage is finalized by the headless worker after the governed 34 to 35
/// migration, that an interrupted verified final is adopted exactly once, and that a persisted
/// encoder fault is retried to exactly one Succeeded fact. No case fabricates a commit claim,
/// edits a ledger row or deletes anything outside its own fixture.
/// </summary>
public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    [Trait("VerificationId", "V151_R01")]
    public async Task V151_R01_FullyOptedInProductionFinalizesTheExactCanonicalPngAndAdmitsTheNextCycle()
    {
        using var root = new ProductionImageTestRoot();
        using var final = new ProductionImageFinalizationTestRoot(root.Options);
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V151.Capture", "1", EvidenceCaptureMode.All),
            imageFinalization: final.Options);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, value => value.Ready, "Image trigger waits for completed Arm admission");
        // The public verified query capability itself is the reader for the whole lifecycle.
        var query = harness.Service<IProductionImageEvidenceQuery>();
        var history = new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options);
        for (var cycle = 1; cycle <= 2; cycle++)
        {
            peer.RaiseTrigger(61, (uint)cycle);
            await WaitForProductionResultAsync(harness, peer, cycle);
            // The controller completes its ordinary handshake independently of image
            // finalization. Holding Trigger while waiting for PNG would test ACK timeout.
            peer.SetTrigger(false);
            var read = await history.ReadCurrentAsync();
            Assert.True(read.Available, read.ReasonCode);
            var core = Assert.IsType<ProductionInspectionCore>(read.Latest!.Core);
            Assert.Equal((uint)cycle, core.Admission.ControllerCycle.CycleSequence);
            Assert.Equal(ExecutionStatus.Success, core.ExecutionStatus);
            var evidence = Assert.IsType<ProductionImageEvidenceSnapshot>(core.ImageEvidence);
            Assert.Equal(ProductionImageEvidenceState.Pending, evidence.State);
            var work = Assert.IsType<PendingImageFinalizationWork>(evidence.Work);
            var manifest = work.Manifest;
            var stagePath = System.IO.Path.Combine(root.Path, manifest.StageFileName);
            // The committed Core already proves the pending obligation durably; the live worker
            // may already have released the stage by the time this reader observes it, so the
            // stage presence itself is never asserted here.
            var finalPath = System.IO.Path.Combine(final.Path, manifest.ManifestId.ToString("N") + ".png");

            var record = await WaitForFinalizationAsync(query, work.WorkId,
                value => value.State.State == ProductionImageFinalizationState.Succeeded &&
                    value.State.CleanupState == ProductionImageCleanupState.Released,
                "The armed PLC cycle did not reach Succeeded and StageReleased");
            Assert.Single(record.Events, value => value.Kind == ProductionImageFinalizationKind.AttemptStarted);
            Assert.Single(record.Events, value => value.Kind == ProductionImageFinalizationKind.Succeeded);
            Assert.Single(record.Events, value => value.Kind == ProductionImageFinalizationKind.StageReleased);
            Assert.Equal(manifest.CanonicalPixelHash, record.State.Success!.CanonicalPixelHash);
            // The final PNG is independently re-verified against the exact Core manifest with
            // the real codec: the persisted answer is not trusted on its own.
            Assert.Equal(manifest.CanonicalPixelHash, CanonicalHashOf(finalPath, manifest,
                CodecLimits(root.Options, final.Root)));
            Assert.Equal(record.State.Success.EncodedByteLength, new FileInfo(finalPath).Length);
            // The canonical stage is gone once Succeeded was durable, and the verified backlog
            // no longer owes anything for this cycle.
            Assert.False(File.Exists(stagePath));
            Assert.Empty(Directory.GetFiles(root.Path, "*.stage"));
            Assert.Empty(Directory.GetFiles(final.Path, "*.tmp"));
            var backlog = await query.ReadBacklogAsync();
            Assert.Equal(0, backlog.Count);
            Assert.Equal(0, backlog.Bytes);
            var queue = await query.ReadWorkQueueAsync();
            Assert.True(queue.Available, queue.ReasonCode);
            Assert.Empty(queue.Pending);
            Assert.Empty(queue.SucceededNotReleased);

            await WaitConditionAsync(() => peer.AckLowCount >= cycle, "Image cycle ACK reset");
            await WaitProductionAsync(harness, value => value.CurrentExecution is null && value.Ready &&
                value.Evidence.PendingRequiredImages == 0,
                "The released image must not block the next admission");
        }
        Assert.Equal(2, Directory.GetFiles(final.Path, "*.png").Length);
    }

    [Fact]
    [Trait("VerificationId", "V151_R02")]
    public async Task V151_R02_DurableT50StageIsFinalizedByAHeadlessWorkerAfterTheGovernedMigration()
    {
        using var root = new ProductionImageTestRoot();
        using var final = new ProductionImageFinalizationTestRoot(root.Options);
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V151.Capture", "1", EvidenceCaptureMode.All));
        using var issuer = new ProductionTestIssuer();
        try
        {
            var staged = await StageDurableProductionImageAsync(root, peer, harness, issuer);

            // The live Runtime and its store are retired before any migration byte is written:
            // the only remaining owner of the obligation is the durable schema-34 ledger.
            var target = await MigrateFinalizationStoreAsync(harness, final.Options);
            var query = new SqliteProductionImageEvidenceQuery(target);
            var faults = new List<string>();
            var snapshots = new List<ImageBacklogSnapshot>();
            await using (var store = new SqliteCommandStore(target))
            {
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                var worker = new ProductionImageFinalizationWorker(store, target, Guid.NewGuid(),
                    store.Initialization, value => { lock (snapshots) snapshots.Add(value); },
                    reason => { lock (faults) faults.Add(reason); return Task.CompletedTask; });
                try
                {
                    await worker.Startup.WaitAsync(TimeSpan.FromSeconds(30));
                    var record = await WaitForFinalizationAsync(query, staged.Work.WorkId,
                        value => value.State.State == ProductionImageFinalizationState.Succeeded &&
                            value.State.CleanupState == ProductionImageCleanupState.Released,
                        "The headless worker did not finalize the durable schema-34 stage", worker);
                    Assert.Single(record.Events, value => value.Kind == ProductionImageFinalizationKind.AttemptStarted);
                    Assert.Single(record.Events, value => value.Kind == ProductionImageFinalizationKind.Succeeded);
                    Assert.Single(record.Events, value => value.Kind == ProductionImageFinalizationKind.StageReleased);
                    Assert.Equal(staged.Work.Manifest.CanonicalPixelHash,
                        record.State.Success!.CanonicalPixelHash);
                    Assert.True(await worker.StopAsync(), "The headless finalization worker did not retire");
                }
                finally
                {
                    if (!worker.Completion.IsCompleted)
                    {
                        try { await worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                        catch (Exception exception) when (exception is not OutOfMemoryException) { }
                    }
                }
            }

            // Without any camera, PLC or Runtime the durable stage became exactly one verified
            // canonical final PNG and the canonical stage was released.
            var finalPath = System.IO.Path.Combine(final.Path,
                staged.Work.Manifest.ManifestId.ToString("N") + ".png");
            Assert.True(File.Exists(finalPath));
            Assert.Equal(staged.Work.Manifest.CanonicalPixelHash, CanonicalHashOf(finalPath,
                staged.Work.Manifest, CodecLimits(root.Options, final.Root)));
            Assert.False(File.Exists(staged.StagePath));
            Assert.Empty(Directory.GetFiles(root.Path, "*.stage"));
            Assert.Empty(Directory.GetFiles(final.Path, "*.tmp"));
            Assert.Empty(faults);
            Assert.Contains(snapshots, value => value.Count == 0);
            Assert.Equal(0, (await query.ReadBacklogAsync()).Count);

            // The original Core fact and the signed central audit chain are unchanged.
            var after = await new SqliteProductionInspectionHistoryQuery(target).ReadCurrentAsync();
            Assert.True(after.Available, after.ReasonCode);
            var core = Assert.IsType<ProductionInspectionCore>(after.Latest!.Core);
            Assert.Equal(staged.CoreContentHash, core.ContentHash);
            Assert.Equal(staged.Work.Manifest.CanonicalPixelHash,
                core.ImageEvidence!.Manifest!.CanonicalPixelHash);
            Assert.Equal(AuditIntegrityState.Verified, (await new SqliteAuditIntegrityQuery(target)
                .VerifyAsync(new AuditVerificationRequest(0, 9_900))).State);
        }
        finally { peer.SetTrigger(false); }
    }

    [Fact]
    [Trait("VerificationId", "V151_R03")]
    public async Task V151_R03_HeadlessReplayAdoptsOneInterruptedVerifiedFinalWithoutRewritingFacts()
    {
        using var root = new ProductionImageTestRoot();
        using var final = new ProductionImageFinalizationTestRoot(root.Options);
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V151.Capture", "1", EvidenceCaptureMode.All));
        using var issuer = new ProductionTestIssuer();
        try
        {
            var staged = await StageDurableProductionImageAsync(root, peer, harness, issuer);
            var target = await MigrateFinalizationStoreAsync(harness, final.Options);
            var query = new SqliteProductionImageEvidenceQuery(target);
            var finalPath = System.IO.Path.Combine(final.Path,
                staged.Work.Manifest.ManifestId.ToString("N") + ".png");
            var attemptId = Guid.NewGuid();
            int eventCount;
            await using (var store = new SqliteCommandStore(target))
            {
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                // One durable attempt starts, one verified final is written, and the claim is
                // disposed without any SQL success: an interrupted writer exit.
                var begun = await store.BeginImageFinalizationAttemptAsync(
                    new ImageFinalizationAttemptStartRequest(staged.Work.WorkId, attemptId, 1,
                        Guid.NewGuid(), DateTimeOffset.UtcNow, false),
                    new StoreDeadline(TimeSpan.FromSeconds(30)));
                Assert.True(begun.Committed, begun.ReasonCode);
                var attempt = Assert.IsType<ProductionImageAttemptDescriptor>(begun.Event!.Attempt);
                Assert.Equal(1, begun.Event.AttemptNumber);
                using (var claim = await FinalizerFor(final.Options).FinalizeAsync(staged.Work,
                    attempt.AttemptId, attempt.TemporaryFileName, attempt.FinalFileName, false,
                    FinalizationFileTimeout))
                {
                    Assert.Equal(staged.Work.Manifest.CanonicalPixelHash, claim.CanonicalPixelHash);
                    Assert.Equal(staged.Work.Manifest.ManifestId, claim.ManifestId);
                }
                Assert.True(File.Exists(finalPath));
                Assert.Empty(Directory.GetFiles(final.Path, "*.tmp"));

                var interrupted = await ReadSingleAsync(query, staged.Work.WorkId);
                Assert.Equal(ProductionImageFinalizationState.Pending, interrupted.State.State);
                Assert.Equal(attemptId, interrupted.State.ActiveAttemptId);
                Assert.Equal(1, interrupted.State.AttemptCount);
                Assert.Equal(2, interrupted.State.NextAttemptNumber);
                Assert.DoesNotContain(interrupted.Events, value =>
                    value.Kind == ProductionImageFinalizationKind.Succeeded);
                Assert.Equal(1, (await query.ReadBacklogAsync()).Count);
                Assert.True(File.Exists(staged.StagePath));

                // The headless replay adopts that exact verified final: one Succeeded, no new
                // attempt, and the canonical stage is released.
                var worker = new ProductionImageFinalizationWorker(store, target, Guid.NewGuid(),
                    store.Initialization, _ => { }, _ => Task.CompletedTask);
                try
                {
                    await worker.Startup.WaitAsync(TimeSpan.FromSeconds(30));
                    var replayed = await WaitForFinalizationAsync(query, staged.Work.WorkId,
                        value => value.State.State == ProductionImageFinalizationState.Succeeded &&
                            value.State.CleanupState == ProductionImageCleanupState.Released,
                        "The headless replay did not adopt the interrupted verified final", worker);
                    var succeeded = Assert.Single(replayed.Events, value =>
                        value.Kind == ProductionImageFinalizationKind.Succeeded);
                    Assert.Equal(attemptId, succeeded.AttemptId);
                    Assert.Single(replayed.Events, value => value.Kind == ProductionImageFinalizationKind.AttemptStarted);
                    Assert.Single(replayed.Events, value => value.Kind == ProductionImageFinalizationKind.AttemptFailed &&
                        value.ReasonCode == "ImageFinalizationProcessInterrupted");
                    Assert.Single(replayed.Events, value => value.Kind == ProductionImageFinalizationKind.StageReleased);
                    Assert.Equal(1, replayed.State.AttemptCount);
                    Assert.Equal(staged.Work.Manifest.CanonicalPixelHash,
                        replayed.State.Success!.CanonicalPixelHash);
                    Assert.Equal(replayed.State.Success.EncodedByteLength, new FileInfo(finalPath).Length);
                    Assert.False(File.Exists(staged.StagePath));
                    eventCount = replayed.Events.Count;
                    Assert.True(await worker.StopAsync(), "The replay worker did not retire");
                }
                finally
                {
                    if (!worker.Completion.IsCompleted)
                    {
                        try { await worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                        catch (Exception exception) when (exception is not OutOfMemoryException) { }
                    }
                }
            }

            // A fresh worker restart replays the very same durable facts and appends nothing.
            await using (var restarted = new SqliteCommandStore(target))
            {
                var initialized = await restarted.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                var second = new ProductionImageFinalizationWorker(restarted, target, Guid.NewGuid(),
                    restarted.Initialization, _ => { }, _ => Task.CompletedTask);
                try
                {
                    await second.Startup.WaitAsync(TimeSpan.FromSeconds(30));
                    await Task.Delay(TimeSpan.FromMilliseconds(1_500));
                    var stable = await ReadSingleAsync(query, staged.Work.WorkId);
                    Assert.Equal(eventCount, stable.Events.Count);
                    Assert.Single(stable.Events, value =>
                        value.Kind == ProductionImageFinalizationKind.Succeeded);
                    Assert.True(await second.StopAsync(), "The restarted worker did not retire");
                }
                finally
                {
                    if (!second.Completion.IsCompleted)
                    {
                        try { await second.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                        catch (Exception exception) when (exception is not OutOfMemoryException) { }
                    }
                }
            }
            Assert.Equal(AuditIntegrityState.Verified, (await new SqliteAuditIntegrityQuery(target)
                .VerifyAsync(new AuditVerificationRequest(0, 9_900))).State);
            Assert.Equal(0, (await query.ReadBacklogAsync()).Count);
        }
        finally { peer.SetTrigger(false); }
    }

    [Fact]
    [Trait("VerificationId", "V151_R04")]
    public async Task V151_R04_PersistedEncoderFaultRetriesToExactlyOneSucceededFinal()
    {
        using var root = new ProductionImageTestRoot();
        using var final = new ProductionImageFinalizationTestRoot(root.Options, maximumAttempts: 3,
            maximumRetryDelay: TimeSpan.FromMilliseconds(50));
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V151.Capture", "1", EvidenceCaptureMode.All));
        using var issuer = new ProductionTestIssuer();
        try
        {
            var staged = await StageDurableProductionImageAsync(root, peer, harness, issuer);
            var target = await MigrateFinalizationStoreAsync(harness, final.Options);
            var query = new SqliteProductionImageEvidenceQuery(target);
            var finalPath = System.IO.Path.Combine(final.Path,
                staged.Work.Manifest.ManifestId.ToString("N") + ".png");

            // The first physical encoder enters and fails once, before any PNG byte is flushed.
            await using (var store = new SqliteCommandStore(target))
            {
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                var fired = 0;
                var worker = new ProductionImageFinalizationWorker(store, target, Guid.NewGuid(),
                    store.Initialization, _ => { }, _ => Task.CompletedTask, boundary =>
                    {
                        if (boundary == ImageFinalizationBoundary.BeforeEncode &&
                            Interlocked.Increment(ref fired) == 1)
                            throw new IOException("V151InjectedEncoderFailure");
                    });
                try
                {
                    await worker.Startup.WaitAsync(TimeSpan.FromSeconds(30));
                    var failed = await WaitForFinalizationAsync(query, staged.Work.WorkId,
                        value => value.State.State == ProductionImageFinalizationState.Failed,
                        "The injected encoder fault was not persisted as a visible failure");
                    Assert.Equal(ProductionImageFailureCategory.Temporary,
                        failed.State.LastFailureCategory);
                    Assert.Equal("V151InjectedEncoderFailure", failed.State.LastFailureReasonCode);
                    Assert.True(failed.State.RetryEligible);
                    Assert.Null(failed.State.Success);
                    Assert.Equal(ProductionImageCleanupState.Pending, failed.State.CleanupState);
                    Assert.Single(failed.Events, value =>
                        value.Kind == ProductionImageFinalizationKind.AttemptFailed &&
                        value.Failure!.Category == ProductionImageFailureCategory.Temporary);
                    // The canonical stage is retained for the retry; the interrupted encoder left
                    // exactly one temporary file and never published a final PNG.
                    Assert.True(File.Exists(staged.StagePath));
                    Assert.False(File.Exists(finalPath));
                    Assert.Single(Directory.GetFiles(final.Path, "*.tmp"));
                    Assert.True(await worker.StopAsync(), "The faulting worker did not retire");
                }
                finally
                {
                    if (!worker.Completion.IsCompleted)
                    {
                        try { await worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                        catch (Exception exception) when (exception is not OutOfMemoryException) { }
                    }
                }
            }

            // A restarted worker resumes the persisted obligation within the short retry window,
            // discards the known temporary file and reaches exactly one Succeeded fact.
            await using (var restarted = new SqliteCommandStore(target))
            {
                var initialized = await restarted.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                var worker = new ProductionImageFinalizationWorker(restarted, target, Guid.NewGuid(),
                    restarted.Initialization, _ => { }, _ => Task.CompletedTask);
                try
                {
                    await worker.Startup.WaitAsync(TimeSpan.FromSeconds(30));
                    var recovered = await WaitForFinalizationAsync(query, staged.Work.WorkId,
                        value => value.State.State == ProductionImageFinalizationState.Succeeded &&
                            value.State.CleanupState == ProductionImageCleanupState.Released,
                        "The persisted encoder fault was not retried to Succeeded", worker);
                    Assert.Single(recovered.Events, value =>
                        value.Kind == ProductionImageFinalizationKind.Succeeded);
                    Assert.Equal(2, recovered.Events.Count(value =>
                        value.Kind == ProductionImageFinalizationKind.AttemptStarted));
                    Assert.Single(recovered.Events, value =>
                        value.Kind == ProductionImageFinalizationKind.AttemptFailed);
                    Assert.Equal(2, recovered.State.AttemptCount);
                    Assert.Equal(staged.Work.Manifest.CanonicalPixelHash,
                        recovered.State.Success!.CanonicalPixelHash);
                    Assert.Equal(staged.Work.Manifest.CanonicalPixelHash, CanonicalHashOf(finalPath,
                        staged.Work.Manifest, CodecLimits(root.Options, final.Root)));
                    Assert.Equal(recovered.State.Success.EncodedByteLength, new FileInfo(finalPath).Length);
                    Assert.False(File.Exists(staged.StagePath));
                    Assert.Empty(Directory.GetFiles(root.Path, "*.stage"));
                    Assert.Empty(Directory.GetFiles(final.Path, "*.tmp"));
                    Assert.Equal(0, (await query.ReadBacklogAsync()).Count);
                    Assert.True(await worker.StopAsync(), "The retrying worker did not retire");
                }
                finally
                {
                    if (!worker.Completion.IsCompleted)
                    {
                        try { await worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                        catch (Exception exception) when (exception is not OutOfMemoryException) { }
                    }
                }
            }
        }
        finally { peer.SetTrigger(false); }
    }

    private sealed record StagedProductionImage(string CoreContentHash, PendingImageFinalizationWork Work,
        string StagePath);

    /// <summary>The bounded physical file budget of one manual verified-final operation.</summary>
    private static readonly TimeSpan FinalizationFileTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs one real armed virtual-production cycle against the T50 stage ledger (no finalization
    /// option), so the resulting schema-34 store owns one durable stage plus one pending obligation.
    /// </summary>
    private static async Task<StagedProductionImage> StageDurableProductionImageAsync(ProductionImageTestRoot root,
        ModbusQualificationTestServer peer, ManualHarness harness, ProductionTestIssuer issuer)
    {
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, value => value.Ready, "Image trigger waits for completed Arm admission");
        peer.RaiseTrigger(61, 1);
        await WaitForProductionResultAsync(harness, peer, 1);
        var read = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
        Assert.True(read.Available, read.ReasonCode);
        var core = Assert.IsType<ProductionInspectionCore>(read.Latest!.Core);
        Assert.Equal(ExecutionStatus.Success, core.ExecutionStatus);
        Assert.Equal(ProductionImageEvidenceState.Pending, core.ImageEvidence!.State);
        var work = Assert.IsType<PendingImageFinalizationWork>(core.ImageEvidence.Work);
        var stagePath = System.IO.Path.Combine(root.Path, work.Manifest.StageFileName);
        Assert.True(File.Exists(stagePath));
        Assert.Equal(1, await harness.Fixture.ScalarAsync("SELECT COUNT(*) FROM pending_image_work;"));
        peer.SetTrigger(false);
        await WaitConditionAsync(() => peer.AckLowCount == 1, "Durable image stage ACK reset");
        return new StagedProductionImage(core.ContentHash, work, stagePath);
    }

    /// <summary>
    /// Retires the live Runtime and its writer, then performs the real governed schema-34 to
    /// schema-35 migration with the complete declared option set plus the finalization opt-in.
    /// </summary>
    private static async Task<ProductionStoreOptions> MigrateFinalizationStoreAsync(ManualHarness harness,
        ProductionImageFinalizationStoreOptions finalization)
    {
        await harness.StopRuntimePreservingFixtureAsync();
        await harness.Fixture.Store.DisposeAsync();
        var target = WithFinalization(harness.Fixture.Options, finalization);
        var opened = await ImageFinalizationFixture.OpenAsync(target);
        Assert.True(opened.Available, opened.Status.ReasonCode);
        Assert.Equal(34, opened.Status.SourceSchemaVersion);
        Assert.Equal(35, opened.Status.TargetSchemaVersion);
        await using (var session = opened.Session!)
        {
            var completed = await StoreStartupMaintenanceTests.Finish(session);
            Assert.True(completed.Completed, completed.ReasonCode);
            Assert.Equal(35, completed.TargetSchemaVersion);
        }
        return target;
    }

    /// <summary>
    /// The exact declared store configuration plus the finalization opt-in. The migration
    /// source profile re-verifies every ledger the schema-34 store actually carries, so the
    /// target must repeat every option of the live store instead of a fixed subset.
    /// </summary>
    private static ProductionStoreOptions WithFinalization(ProductionStoreOptions source,
        ProductionImageFinalizationStoreOptions finalization) => new(source.DatabasePath)
    {
        CommitTimeout = source.CommitTimeout,
        QueryTimeout = source.QueryTimeout,
        QueueCapacity = source.QueueCapacity,
        AuditIntegrityPolicy = source.AuditIntegrityPolicy,
        LocalIdentity = source.LocalIdentity,
        AlarmPolicy = source.AlarmPolicy,
        ExternalAuditAnchor = source.ExternalAuditAnchor,
        AlgorithmResultArchive = source.AlgorithmResultArchive,
        RecipeDrafts = source.RecipeDrafts,
        CameraSetup = source.CameraSetup,
        CameraRecovery = source.CameraRecovery,
        CameraNetwork = source.CameraNetwork,
        ImagingSetup = source.ImagingSetup,
        CalibrationSessions = source.CalibrationSessions,
        CalibrationGovernance = source.CalibrationGovernance,
        RecipeReleases = source.RecipeReleases,
        PlcResultContracts = source.PlcResultContracts,
        RecipeActivations = source.RecipeActivations,
        PreviewSessions = source.PreviewSessions,
        CalibrationImports = source.CalibrationImports,
        ManualInspections = source.ManualInspections,
        ProductionAdmission = source.ProductionAdmission,
        StationQualifications = source.StationQualifications,
        RecipeTransfers = source.RecipeTransfers,
        TraceStoragePolicies = source.TraceStoragePolicies,
        QualificationCycles = source.QualificationCycles,
        PlcCommunication = source.PlcCommunication,
        ProductionInspections = source.ProductionInspections,
        PartIdentities = source.PartIdentities,
        ProductionRecovery = source.ProductionRecovery,
        RecipeSelections = source.RecipeSelections,
        ProductionArming = source.ProductionArming,
        RecipeLifecycle = source.RecipeLifecycle,
        ImageEvidence = source.ImageEvidence,
        ImageFinalization = finalization
    };

    private static ProductionImageFinalizer FinalizerFor(ProductionImageFinalizationStoreOptions finalization) =>
        new(finalization.ImageEvidence.Stage, finalization.FinalRoot.FinalRoot,
            finalization.FinalRoot.ContentHash, new ProductionPngFileLimits(
                CodecLimits(finalization.ImageEvidence, finalization.FinalRoot),
                finalization.FinalRoot.MaximumTotalFinalBytes, finalization.FinalRoot.MaximumFinalFiles));

    private static PngCodecLimits CodecLimits(ProductionImageEvidenceStoreOptions evidence,
        ProductionImageFinalizationRootOptions finalRoot) => new(evidence.Stage.MaximumStageBytes,
        finalRoot.MaximumFinalFileBytes, 8 * 1024 * 1024);

    /// <summary>Independent canonical proof of one final PNG against the exact Core manifest.</summary>
    private static string CanonicalHashOf(string finalPath, PendingImageManifest manifest,
        PngCodecLimits limits)
    {
        using var file = ProductionImageFinalizer.OpenProtected(finalPath);
        return CanonicalPngCodec.Verify(file, ProductionImageFinalizer.Descriptor(manifest), limits,
            new StoreDeadline(TimeSpan.FromSeconds(30))).CanonicalPixelHash;
    }

    private static async Task<ProductionImageEvidenceRecord> WaitForFinalizationAsync(
        IProductionImageEvidenceQuery query, Guid workId,
        Func<ProductionImageEvidenceRecord, bool> predicate, string reason,
        ProductionImageFinalizationWorker? worker = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        ProductionImageEvidenceRecord? last = null;
        string? reasonCode = null;
        while (DateTime.UtcNow < deadline)
        {
            if (worker?.FailureReason is { } failure) throw new XunitException(reason + ": worker stopped: " + failure);
            var page = await query.QueryAsync(new ProductionImageEvidenceFilter(WorkId: workId));
            if (page.Available) { last = page.Items.SingleOrDefault(); }
            else reasonCode = page.ReasonCode;
            if (last is not null && predicate(last)) return last;
            await Task.Delay(50);
        }
        throw new XunitException(reason + ": " + (last is null ? reasonCode ?? "ImageEvidenceEmpty"
            : last.State.State + "/" + last.State.LastFailureReasonCode + "/" + last.State.CleanupState));
    }

    private static async Task<ProductionImageEvidenceRecord> ReadSingleAsync(
        IProductionImageEvidenceQuery query, Guid workId)
    {
        var page = await query.QueryAsync(new ProductionImageEvidenceFilter(WorkId: workId));
        Assert.True(page.Available, page.ReasonCode);
        return Assert.Single(page.Items);
    }

    /// <summary>
    /// Owns one explicit, qualified local final root outside the stage root. The diagnostic
    /// cleanup only ever removes this fixture's own directory.
    /// </summary>
    private sealed class ProductionImageFinalizationTestRoot : IDisposable
    {
        internal ProductionImageFinalizationTestRoot(ProductionImageEvidenceStoreOptions evidence,
            int maximumAttempts = 5, TimeSpan? maximumRetryDelay = null)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SharpInspect.Runtime.Tests",
                "V151-Final-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Root = new ProductionImageFinalizationRootOptions(Path, 64L * 1024 * 1024,
                256L * 1024 * 1024, 1_000);
            Options = new ProductionImageFinalizationStoreOptions(Root, evidence)
            {
                MaximumAttempts = maximumAttempts,
                MaximumRetryDelay = maximumRetryDelay ?? TimeSpan.FromSeconds(5)
            };
        }

        internal string Path { get; }
        internal ProductionImageFinalizationRootOptions Root { get; }
        internal ProductionImageFinalizationStoreOptions Options { get; }

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
