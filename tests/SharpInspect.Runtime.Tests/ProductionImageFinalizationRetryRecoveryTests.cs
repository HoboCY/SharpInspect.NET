using SharpInspect.Abstractions;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Bounded retry and recovery regression coverage for the two storage-transition gaps the T51
/// runtime integration review recorded. Every step is owned by the real SQLite writer and the
/// real headless finalization worker; the storage fault is the writer's own WAL-limit gate,
/// driven through the existing internal WAL-length seam of SqliteCommandStore (the technique
/// already used by StoreFailureAcceptanceTests), and the encoder faults use the file-boundary
/// callback the product already exposes. Nothing is mocked, no new product hook is added and no
/// global environment or other file is changed.
/// R09: one temporarily uncommitted finalization write - the attempt start, and separately the
/// success that would claim an already published canonical PNG - must stay a retryable,
/// diagnosed transient condition: no integrity callback, no stopped worker, no durable failure
/// fact, and a later replay from the verified durable queue that ends in exactly one Succeeded
/// plus StageReleased with the Core and the canonical stage preserved.
/// R11: an exhausted attempt budget must persist a terminal Failed projection with
/// RetryEligible=false and the original reason, must never auto-retry after a restart, and must
/// still adopt an already verified final PNG through the valid final-claim path.
/// </summary>
public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    [Trait("VerificationId", "V151_R09")]
    public async Task V151_R09_TransientStoreRejectionOfTheAttemptStartIsRetriedWithoutIntegrityLatch()
    {
        using var root = new ProductionImageTestRoot();
        using var final = new ProductionImageFinalizationTestRoot(root.Options);
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V151.Transient", "1", EvidenceCaptureMode.All));
        using var issuer = new ProductionTestIssuer();
        try
        {
            var staged = await StageDurableProductionImageAsync(root, peer, harness, issuer);
            var target = await MigrateFinalizationStoreAsync(harness, final.Options);
            var query = new SqliteProductionImageEvidenceQuery(target);
            var history = new SqliteProductionInspectionHistoryQuery(target);
            var finalPath = System.IO.Path.Combine(final.Path,
                staged.Work.Manifest.ManifestId.ToString("N") + ".png");
            var faults = new List<string>();
            var wal = new FinalizationWalLimitFault();
            await using (var store = new SqliteCommandStore(target, wal.Read))
            {
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                // One benign request that cannot write a row keeps the writer's passive-checkpoint
                // cadence warm, so the short armed window below never latches the writer's own
                // WAL flag: the fault stays exactly one rejected write, never a stuck writer.
                var warmup = await store.BeginImageFinalizationAttemptAsync(
                    new ImageFinalizationAttemptStartRequest(Guid.NewGuid(), Guid.NewGuid(), 1,
                        Guid.NewGuid(), DateTimeOffset.UtcNow, false),
                    new StoreDeadline(TimeSpan.FromSeconds(10)));
                Assert.False(warmup.Committed, warmup.ReasonCode);
                Assert.Equal("ImageFinalizationWorkRequired", warmup.ReasonCode);
                wal.Arm();
                var worker = new ProductionImageFinalizationWorker(store, target, Guid.NewGuid(),
                    store.Initialization, _ => { }, reason => { lock (faults) faults.Add(reason); return Task.CompletedTask; });
                try
                {
                    await worker.Startup.WaitAsync(TimeSpan.FromSeconds(30));
                    await WaitForTransientFinalizationRejectionAsync(worker, "TraceStoreWalLimit",
                        "The armed WAL-limit rejection never became a transient worker diagnostic");
                    // The uncommitted write left no durable trace: no event, no attempt, no file.
                    var waiting = await ReadSingleAsync(query, staged.Work.WorkId);
                    Assert.Equal(ProductionImageFinalizationState.Pending, waiting.State.State);
                    Assert.Equal(0, waiting.State.AttemptCount);
                    Assert.Null(waiting.State.ActiveAttemptId);
                    Assert.Null(waiting.State.LastFailureReasonCode);
                    Assert.Empty(waiting.Events);
                    Assert.True(File.Exists(staged.StagePath));
                    Assert.False(File.Exists(finalPath));
                    Assert.Empty(Directory.GetFiles(final.Path, "*.tmp"));
                    Assert.Equal(1, (await query.ReadBacklogAsync()).Count);
                    // The worker itself is neither stopped nor faulted while storage is failing.
                    Assert.Null(worker.FailureReason);
                    Assert.False(worker.Completion.IsCompleted);
                    Assert.Empty(faults);
                    var blocked = await history.ReadCurrentAsync();
                    Assert.True(blocked.Available, blocked.ReasonCode);
                    Assert.Equal(staged.CoreContentHash, blocked.Latest!.Core!.ContentHash);

                    // Releasing the storage fault lets the same worker replay the durable queue.
                    wal.Release();
                    var recovered = await WaitForFinalizationAsync(query, staged.Work.WorkId,
                        value => value.State.State == ProductionImageFinalizationState.Succeeded &&
                            value.State.CleanupState == ProductionImageCleanupState.Released,
                        "The released storage fault was not retried from the durable queue", worker);
                    Assert.Single(recovered.Events, value => value.Kind == ProductionImageFinalizationKind.AttemptStarted);
                    Assert.Single(recovered.Events, value => value.Kind == ProductionImageFinalizationKind.Succeeded);
                    Assert.Single(recovered.Events, value => value.Kind == ProductionImageFinalizationKind.StageReleased);
                    Assert.Equal(1, recovered.State.AttemptCount);
                    Assert.Equal(staged.Work.Manifest.CanonicalPixelHash, recovered.State.Success!.CanonicalPixelHash);
                    Assert.Single(Directory.GetFiles(final.Path, "*.png"));
                    Assert.Empty(Directory.GetFiles(final.Path, "*.tmp"));
                    Assert.Equal(staged.Work.Manifest.CanonicalPixelHash, CanonicalHashOf(finalPath,
                        staged.Work.Manifest, CodecLimits(root.Options, final.Root)));
                    Assert.Equal(recovered.State.Success.EncodedByteLength, new FileInfo(finalPath).Length);
                    Assert.False(File.Exists(staged.StagePath));
                    Assert.Empty(Directory.GetFiles(root.Path, "*.stage"));
                    Assert.Equal(0, (await query.ReadBacklogAsync()).Count);
                    Assert.Empty(faults);
                    Assert.Null(worker.FailureReason);
                    Assert.Equal(staged.CoreContentHash, (await history.ReadCurrentAsync()).Latest!.Core!.ContentHash);
                    Assert.Equal(AuditIntegrityState.Verified, (await new SqliteAuditIntegrityQuery(target)
                        .VerifyAsync(new AuditVerificationRequest(0, 9_900))).State);
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

    [Fact]
    [Trait("VerificationId", "V151_R09")]
    public async Task V151_R09_TransientStoreRejectionOfTheSuccessWriteIsReplayedToExactlyOneSucceededFinal()
    {
        using var root = new ProductionImageTestRoot();
        using var final = new ProductionImageFinalizationTestRoot(root.Options);
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V151.Transient", "1", EvidenceCaptureMode.All));
        using var issuer = new ProductionTestIssuer();
        try
        {
            var staged = await StageDurableProductionImageAsync(root, peer, harness, issuer);
            var target = await MigrateFinalizationStoreAsync(harness, final.Options);
            var query = new SqliteProductionImageEvidenceQuery(target);
            var history = new SqliteProductionInspectionHistoryQuery(target);
            var finalPath = System.IO.Path.Combine(final.Path,
                staged.Work.Manifest.ManifestId.ToString("N") + ".png");
            var faults = new List<string>();
            var wal = new FinalizationWalLimitFault();
            await using (var store = new SqliteCommandStore(target, wal.Read))
            {
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                // The real fault gate is armed inside the real file boundary that runs after the
                // canonical PNG was renamed into place but before the worker asks SQL to commit
                // it: an attempt exists and one verified final exists, but no fact claims it yet.
                var armed = 0;
                var worker = new ProductionImageFinalizationWorker(store, target, Guid.NewGuid(),
                    store.Initialization, _ => { }, reason => { lock (faults) faults.Add(reason); return Task.CompletedTask; },
                    boundary =>
                    {
                        if (boundary == ImageFinalizationBoundary.BeforeClaimPublication &&
                            Interlocked.Increment(ref armed) == 1)
                            wal.Arm();
                    });
                try
                {
                    await worker.Startup.WaitAsync(TimeSpan.FromSeconds(30));
                    await WaitForTransientFinalizationRejectionAsync(worker, "TraceStoreWalLimit",
                        "The armed WAL-limit rejection of the success write never became a transient diagnostic");
                    var pending = await ReadSingleAsync(query, staged.Work.WorkId);
                    var started = Assert.Single(pending.Events, value =>
                        value.Kind == ProductionImageFinalizationKind.AttemptStarted);
                    Assert.Equal(ProductionImageFinalizationState.Pending, pending.State.State);
                    Assert.Equal(1, pending.State.AttemptCount);
                    Assert.Equal(started.AttemptId, pending.State.ActiveAttemptId);
                    Assert.DoesNotContain(pending.Events, value =>
                        value.Kind == ProductionImageFinalizationKind.Succeeded);
                    Assert.DoesNotContain(pending.Events, value =>
                        value.Kind == ProductionImageFinalizationKind.AttemptFailed);
                    // The published PNG is not authority and the recoverable stage is untouched.
                    Assert.True(File.Exists(finalPath));
                    Assert.True(File.Exists(staged.StagePath));
                    Assert.Equal(1, (await query.ReadBacklogAsync()).Count);
                    Assert.Null(worker.FailureReason);
                    Assert.False(worker.Completion.IsCompleted);
                    Assert.Empty(faults);
                    var blocked = await history.ReadCurrentAsync();
                    Assert.True(blocked.Available, blocked.ReasonCode);
                    Assert.Equal(staged.CoreContentHash, blocked.Latest!.Core!.ContentHash);

                    // Releasing the fault replays the durable queue: the interrupted attempt gets
                    // its ProcessInterrupted fact and the very same verified final is claimed,
                    // never a second physical PNG.
                    wal.Release();
                    var recovered = await WaitForFinalizationAsync(query, staged.Work.WorkId,
                        value => value.State.State == ProductionImageFinalizationState.Succeeded &&
                            value.State.CleanupState == ProductionImageCleanupState.Released,
                        "The rejected success write was not replayed to exactly one Succeeded final", worker);
                    Assert.Single(recovered.Events, value => value.Kind == ProductionImageFinalizationKind.AttemptStarted);
                    var succeeded = Assert.Single(recovered.Events, value =>
                        value.Kind == ProductionImageFinalizationKind.Succeeded);
                    Assert.Equal(started.AttemptId, succeeded.AttemptId);
                    Assert.Single(recovered.Events, value =>
                        value.Kind == ProductionImageFinalizationKind.AttemptFailed &&
                        value.ReasonCode == "ImageFinalizationProcessInterrupted" &&
                        value.Failure!.Category == ProductionImageFailureCategory.Temporary);
                    Assert.Single(recovered.Events, value => value.Kind == ProductionImageFinalizationKind.StageReleased);
                    Assert.Equal(1, recovered.State.AttemptCount);
                    Assert.Equal(staged.Work.Manifest.CanonicalPixelHash, recovered.State.Success!.CanonicalPixelHash);
                    Assert.Single(Directory.GetFiles(final.Path, "*.png"));
                    Assert.Empty(Directory.GetFiles(final.Path, "*.tmp"));
                    Assert.Equal(staged.Work.Manifest.CanonicalPixelHash, CanonicalHashOf(finalPath,
                        staged.Work.Manifest, CodecLimits(root.Options, final.Root)));
                    Assert.Equal(recovered.State.Success.EncodedByteLength, new FileInfo(finalPath).Length);
                    Assert.False(File.Exists(staged.StagePath));
                    Assert.Empty(Directory.GetFiles(root.Path, "*.stage"));
                    Assert.Equal(0, (await query.ReadBacklogAsync()).Count);
                    Assert.Empty(faults);
                    Assert.Null(worker.FailureReason);
                    Assert.Equal(staged.CoreContentHash, (await history.ReadCurrentAsync()).Latest!.Core!.ContentHash);
                    Assert.Equal(AuditIntegrityState.Verified, (await new SqliteAuditIntegrityQuery(target)
                        .VerifyAsync(new AuditVerificationRequest(0, 9_900))).State);
                    Assert.True(await worker.StopAsync(), "The replaying worker did not retire");
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

    [Fact]
    [Trait("VerificationId", "V151_R11")]
    public async Task V151_R11_ExhaustedAttemptBudgetPersistsTerminalFailureWithoutAutomaticRetry()
    {
        using var root = new ProductionImageTestRoot();
        using var final = new ProductionImageFinalizationTestRoot(root.Options, maximumAttempts: 2,
            maximumRetryDelay: TimeSpan.FromMilliseconds(50));
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V151.Exhausted", "1", EvidenceCaptureMode.All));
        using var issuer = new ProductionTestIssuer();
        try
        {
            var staged = await StageDurableProductionImageAsync(root, peer, harness, issuer);
            var target = await MigrateFinalizationStoreAsync(harness, final.Options);
            var query = new SqliteProductionImageEvidenceQuery(target);
            var history = new SqliteProductionInspectionHistoryQuery(target);
            var finalPath = System.IO.Path.Combine(final.Path,
                staged.Work.Manifest.ManifestId.ToString("N") + ".png");
            var faults = new List<string>();
            var eventCount = 0;
            await using (var store = new SqliteCommandStore(target))
            {
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                // A real encoder failure repeats until the persisted per-work budget is spent.
                var worker = new ProductionImageFinalizationWorker(store, target, Guid.NewGuid(),
                    store.Initialization, _ => { }, reason => { lock (faults) faults.Add(reason); return Task.CompletedTask; },
                    boundary =>
                    {
                        if (boundary == ImageFinalizationBoundary.BeforeEncode)
                            throw new IOException("V151ExhaustedEncoderFailure");
                    });
                try
                {
                    await worker.Startup.WaitAsync(TimeSpan.FromSeconds(30));
                    var exhausted = await WaitForFinalizationAsync(query, staged.Work.WorkId,
                        value => value.State.State == ProductionImageFinalizationState.Failed &&
                            value.State.AttemptCount == 2 && !value.State.RetryEligible,
                        "The injected encoder fault was not retried to the persisted attempt budget", worker);
                    Assert.Equal(ProductionImageFailureCategory.Temporary, exhausted.State.LastFailureCategory);
                    Assert.Equal("V151ExhaustedEncoderFailure", exhausted.State.LastFailureReasonCode);
                    Assert.Null(exhausted.State.Success);
                    Assert.Null(exhausted.State.ActiveAttemptId);
                    Assert.Equal(ProductionImageCleanupState.Pending, exhausted.State.CleanupState);
                    Assert.Equal(2, exhausted.Events.Count(value =>
                        value.Kind == ProductionImageFinalizationKind.AttemptStarted));
                    Assert.Equal(2, exhausted.Events.Count(value =>
                        value.Kind == ProductionImageFinalizationKind.AttemptFailed));
                    Assert.DoesNotContain(exhausted.Events, value =>
                        value.Kind == ProductionImageFinalizationKind.Succeeded);
                    // The recoverable stage and the last interrupted temporary file survive, no
                    // final authority exists and the exhaustion never escalates to integrity.
                    Assert.True(File.Exists(staged.StagePath));
                    Assert.False(File.Exists(finalPath));
                    Assert.Single(Directory.GetFiles(final.Path, "*.tmp"));
                    Assert.Empty(faults);
                    Assert.Null(worker.FailureReason);
                    // A closed budget spends no further physical attempt once its retry window,
                    // deliberately short here, has long expired.
                    await Task.Delay(TimeSpan.FromMilliseconds(2_500));
                    var stable = await ReadSingleAsync(query, staged.Work.WorkId);
                    Assert.Equal(exhausted.Events.Count, stable.Events.Count);
                    Assert.Equal(2, stable.State.AttemptCount);
                    Assert.False(stable.State.RetryEligible);
                    eventCount = stable.Events.Count;
                    Assert.True(await worker.StopAsync(), "The exhausted worker did not retire");
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

            // A restart replays the same durable budget: still no automatic retry, no new facts.
            await using (var restarted = new SqliteCommandStore(target))
            {
                var initialized = await restarted.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                var worker = new ProductionImageFinalizationWorker(restarted, target, Guid.NewGuid(),
                    restarted.Initialization, _ => { }, reason => { lock (faults) faults.Add(reason); return Task.CompletedTask; });
                try
                {
                    await worker.Startup.WaitAsync(TimeSpan.FromSeconds(30));
                    await Task.Delay(TimeSpan.FromMilliseconds(2_500));
                    var stable = await ReadSingleAsync(query, staged.Work.WorkId);
                    Assert.Equal(eventCount, stable.Events.Count);
                    Assert.Equal(ProductionImageFinalizationState.Failed, stable.State.State);
                    Assert.Equal(2, stable.State.AttemptCount);
                    Assert.False(stable.State.RetryEligible);
                    Assert.Equal("V151ExhaustedEncoderFailure", stable.State.LastFailureReasonCode);
                    Assert.Equal(ProductionImageFailureCategory.Temporary, stable.State.LastFailureCategory);
                    Assert.False(File.Exists(finalPath));
                    Assert.True(File.Exists(staged.StagePath));
                    Assert.Equal(1, (await query.ReadBacklogAsync()).Count);
                    var queue = await query.ReadWorkQueueAsync();
                    Assert.True(queue.Available, queue.ReasonCode);
                    var pending = Assert.Single(queue.Pending);
                    Assert.Equal(staged.Work.WorkId, pending.WorkId);
                    Assert.False(pending.RetryEligible);
                    Assert.Empty(faults);
                    Assert.Null(worker.FailureReason);
                    Assert.Equal(staged.CoreContentHash, (await history.ReadCurrentAsync()).Latest!.Core!.ContentHash);
                    Assert.True(await worker.StopAsync(), "The restarted worker did not retire");
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

    [Fact]
    [Trait("VerificationId", "V151_R11")]
    public async Task V151_R11_ExhaustedAttemptBudgetStillAdoptsAnExistingVerifiedFinal()
    {
        using var root = new ProductionImageTestRoot();
        using var final = new ProductionImageFinalizationTestRoot(root.Options, maximumAttempts: 1,
            maximumRetryDelay: TimeSpan.FromMilliseconds(50));
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V151.Exhausted", "1", EvidenceCaptureMode.All));
        using var issuer = new ProductionTestIssuer();
        try
        {
            var staged = await StageDurableProductionImageAsync(root, peer, harness, issuer);
            var target = await MigrateFinalizationStoreAsync(harness, final.Options);
            var query = new SqliteProductionImageEvidenceQuery(target);
            var history = new SqliteProductionInspectionHistoryQuery(target);
            var finalPath = System.IO.Path.Combine(final.Path,
                staged.Work.Manifest.ManifestId.ToString("N") + ".png");
            var faults = new List<string>();
            await using (var store = new SqliteCommandStore(target))
            {
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                // The real encoder publishes the canonical final and then dies before the SQL
                // success: the only attempt of this budget is spent on a Temporary failure.
                var fired = 0;
                var worker = new ProductionImageFinalizationWorker(store, target, Guid.NewGuid(),
                    store.Initialization, _ => { }, reason => { lock (faults) faults.Add(reason); return Task.CompletedTask; },
                    boundary =>
                    {
                        if (boundary == ImageFinalizationBoundary.AfterRename &&
                            Interlocked.Increment(ref fired) == 1)
                            throw new IOException("V151ExhaustedPostRenameFailure");
                    });
                try
                {
                    await worker.Startup.WaitAsync(TimeSpan.FromSeconds(30));
                    var exhausted = await WaitForFinalizationAsync(query, staged.Work.WorkId,
                        value => value.State.State == ProductionImageFinalizationState.Failed &&
                            value.State.AttemptCount == 1 && !value.State.RetryEligible,
                        "The post-rename fault did not exhaust the persisted attempt budget", worker);
                    Assert.Equal(ProductionImageFailureCategory.Temporary, exhausted.State.LastFailureCategory);
                    Assert.Equal("V151ExhaustedPostRenameFailure", exhausted.State.LastFailureReasonCode);
                    Assert.Null(exhausted.State.Success);
                    Assert.Single(exhausted.Events, value => value.Kind == ProductionImageFinalizationKind.AttemptStarted);
                    Assert.Single(exhausted.Events, value => value.Kind == ProductionImageFinalizationKind.AttemptFailed);
                    Assert.DoesNotContain(exhausted.Events, value =>
                        value.Kind == ProductionImageFinalizationKind.Succeeded);
                    Assert.True(File.Exists(finalPath));
                    Assert.True(File.Exists(staged.StagePath));
                    Assert.Empty(Directory.GetFiles(final.Path, "*.tmp"));
                    Assert.Empty(faults);
                    Assert.Null(worker.FailureReason);

                    // The exhausted budget blocks new attempts, never the recovery of the exact
                    // verified final this durable obligation already owns.
                    var recovered = await WaitForFinalizationAsync(query, staged.Work.WorkId,
                        value => value.State.State == ProductionImageFinalizationState.Succeeded &&
                            value.State.CleanupState == ProductionImageCleanupState.Released,
                        "The existing verified final was not adopted after the budget was exhausted", worker);
                    var started = Assert.Single(recovered.Events, value =>
                        value.Kind == ProductionImageFinalizationKind.AttemptStarted);
                    var succeeded = Assert.Single(recovered.Events, value =>
                        value.Kind == ProductionImageFinalizationKind.Succeeded);
                    Assert.Equal(started.AttemptId, succeeded.AttemptId);
                    Assert.Single(recovered.Events, value =>
                        value.Kind == ProductionImageFinalizationKind.AttemptFailed &&
                        value.ReasonCode == "V151ExhaustedPostRenameFailure");
                    Assert.Single(recovered.Events, value => value.Kind == ProductionImageFinalizationKind.StageReleased);
                    Assert.Equal(1, recovered.State.AttemptCount);
                    Assert.Equal(staged.Work.Manifest.CanonicalPixelHash, recovered.State.Success!.CanonicalPixelHash);
                    Assert.Single(Directory.GetFiles(final.Path, "*.png"));
                    Assert.Empty(Directory.GetFiles(final.Path, "*.tmp"));
                    Assert.Equal(staged.Work.Manifest.CanonicalPixelHash, CanonicalHashOf(finalPath,
                        staged.Work.Manifest, CodecLimits(root.Options, final.Root)));
                    Assert.Equal(recovered.State.Success.EncodedByteLength, new FileInfo(finalPath).Length);
                    Assert.False(File.Exists(staged.StagePath));
                    Assert.Empty(Directory.GetFiles(root.Path, "*.stage"));
                    Assert.Equal(0, (await query.ReadBacklogAsync()).Count);
                    Assert.Empty(faults);
                    Assert.Null(worker.FailureReason);
                    Assert.Equal(staged.CoreContentHash, (await history.ReadCurrentAsync()).Latest!.Core!.ContentHash);
                    Assert.Equal(AuditIntegrityState.Verified, (await new SqliteAuditIntegrityQuery(target)
                        .VerifyAsync(new AuditVerificationRequest(0, 9_900))).State);
                    Assert.True(await worker.StopAsync(), "The budget-exhausted worker did not retire");
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

    /// <summary>
    /// Drives the writer's existing WAL-limit gate through the store's real WAL-length seam
    /// (the technique of StoreFailureAcceptanceTests): while armed, every write observes a WAL
    /// above the fixed 64 MiB ceiling and the writer rejects it with TraceStoreWalLimit, exactly
    /// like a temporarily uncommitted production write. Nothing outside the test process is
    /// touched, the fault is entered and released explicitly, and the unarmed path is the
    /// writer's normal file-length read.
    /// </summary>
    private sealed class FinalizationWalLimitFault
    {
        private volatile bool _armed;
        internal void Arm() => _armed = true;
        internal void Release() => _armed = false;
        internal long Read(string path)
        {
            if (_armed) return 64L * 1024 * 1024 + 1;
            var file = new FileInfo(path);
            return file.Exists ? file.Length : 0;
        }
    }

    /// <summary>
    /// Observes the bounded transient diagnostic of a temporarily uncommitted finalization
    /// write while never accepting a stopped worker, so a regression to the latched
    /// integrity fault fails here instead of silently passing.
    /// </summary>
    private static async Task WaitForTransientFinalizationRejectionAsync(
        ProductionImageFinalizationWorker worker, string expectedReason, string reason)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (worker.FailureReason is { } failure)
                throw new XunitException(reason + ": the worker stopped instead of retrying: " + failure);
            if (string.Equals(worker.LastTransientFailureReason, expectedReason, StringComparison.Ordinal)) return;
            await Task.Delay(25);
        }
        throw new XunitException(reason + ": last transient reason " +
            (worker.LastTransientFailureReason ?? "none"));
    }
}
