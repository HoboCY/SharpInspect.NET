using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    [Trait("VerificationId", "V151_R05")]
    public async Task V151_R05_HealthyPendingImagesDoNotRevokeArmOrSerializeThePlcBehindEncoding()
    {
        using var root = new ProductionImageTestRoot();
        using var final = new ProductionImageFinalizationTestRoot(root.Options);
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V151.Pending", "1", EvidenceCaptureMode.All),
            imageFinalization: final.Options);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Initial image production readiness");
        var files = ImageFilesOf(ImageWorkerOf(harness.Runtime));
        var hookField = typeof(ProductionImageFinalizer).GetField("_faultHook", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredCount = 0;
        hookField.SetValue(files, (Action<ImageFinalizationBoundary>)(boundary =>
        {
            if (boundary != ImageFinalizationBoundary.BeforeEncode || Interlocked.Increment(ref enteredCount) != 1) return;
            entered.TrySetResult(true);
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("V151.TestEncodingHoldExpired");
        }));
        try
        {
            for (var cycle = 1; cycle <= 2; cycle++)
            {
                peer.RaiseTrigger(61, (uint)cycle);
                await WaitForProductionResultAsync(harness, peer, cycle);
                peer.SetTrigger(false);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitConditionAsync(() => peer.AckLowCount >= cycle, "ACK completion while encoder is held");
                await WaitProductionAsync(harness, state => state.Ready && state.ArmState == ProductionArmState.Armed &&
                    state.CurrentExecution is null && state.Evidence.PendingRequiredImages == cycle,
                    "Healthy bounded image backlog must preserve production admission");
            }
            Assert.Equal(1, files.ActiveOperationCount);
            Assert.Empty(Directory.GetFiles(final.Path, "*.png"));
            Assert.Equal(2, Directory.GetFiles(root.Path, "*.stage").Length);
            var query = harness.Service<IProductionImageEvidenceQuery>();
            Assert.Equal(2, (await query.ReadBacklogAsync()).Count);
            var pending = await query.QueryAsync(new());
            Assert.True(pending.Available, pending.ReasonCode);
            Assert.Equal(2, pending.Items.Count);
            release.Set();
            foreach (var item in pending.Items)
                await WaitForFinalizationAsync(query, item.Identity.WorkId,
                    record => record.State.CleanupState == ProductionImageCleanupState.Released,
                    "Both pending obligations must drain after releasing the encoder", ImageWorkerOf(harness.Runtime));
            await WaitProductionAsync(harness, state => state.Ready && state.ArmState == ProductionArmState.Armed &&
                state.Evidence.PendingRequiredImages == 0, "Drained backlog must retain Arm");
        }
        finally
        {
            release.Set();
            await files.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            hookField.SetValue(files, null);
        }
    }

    [Fact]
    [Trait("VerificationId", "V151_R06")]
    public async Task V151_R06_CorruptSucceededEvidenceLatchesARealAlarmAndPreservesPublishedCore()
    {
        using var root = new ProductionImageTestRoot();
        using var final = new ProductionImageFinalizationTestRoot(root.Options);
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V151.Integrity", "1", EvidenceCaptureMode.All),
            imageFinalization: final.Options);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Ready for integrity fixture");
        peer.RaiseTrigger(61, 1);
        await WaitForProductionResultAsync(harness, peer, 1);
        peer.SetTrigger(false);
        await WaitConditionAsync(() => peer.AckLowCount == 1, "Integrity fixture ACK reset");
        var history = new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options);
        var original = (await history.ReadCurrentAsync()).Latest!.Core!;
        var work = original.ImageEvidence!.Work!;
        var completed = await WaitForFinalizationAsync(harness.Service<IProductionImageEvidenceQuery>(), work.WorkId,
            record => record.State.CleanupState == ProductionImageCleanupState.Released,
            "Integrity fixture PNG release", ImageWorkerOf(harness.Runtime));
        var path = Path.Combine(final.Path, completed.State.Success!.FinalFileName);
        var originalPng = File.ReadAllBytes(path);
        await harness.StopRuntimePreservingFixtureAsync();
        await harness.Fixture.Store.DisposeAsync();
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        await using var store = new SqliteCommandStore(harness.Fixture.Options);
        Assert.True((await store.Initialization).Committed);
        await using var cold = new StationRuntime(store, productionStoreOptions: harness.Fixture.Options);
        await Assert.ThrowsAnyAsync<Exception>(() => ImageWorkerOf(cold).Startup);
        await WaitConditionAsync(() => cold.GetSnapshotAsync().AsTask().GetAwaiter().GetResult()
            .AlarmState?.Instances.Any(alarm => alarm.Code == "EvidenceIntegrityFault" && alarm.IsLatched) == true,
            "Referenced corrupt PNG must create the durable latched image integrity alarm");
        var faulted = await cold.GetSnapshotAsync();
        Assert.False(faulted.Ready);
        Assert.Equal(ProductionArmState.Disarmed, faulted.ArmState);
        Assert.Equal(HealthState.Faulted, faulted.Evidence.State);
        var alarm = Assert.Single(faulted.AlarmState!.Instances, value => value.Code == "EvidenceIntegrityFault");
        Assert.Equal(ProductionImpact.BlockNewTriggers, alarm.ProductionImpact);
        var current = await history.ReadAsync(work.Manifest.InspectionId);
        Assert.True(current.Available, current.ReasonCode);
        Assert.Equal(original.ContentHash, current.Latest!.Core!.ContentHash);
        var image = await ReadSingleAsync(new SqliteProductionImageEvidenceQuery(harness.Fixture.Options), work.WorkId);
        Assert.Equal(ProductionImageFinalizationState.Succeeded, image.State.State);
        Assert.Equal(completed.Events.Count, image.Events.Count);
        // Replacing bytes alone has no authority to clear the latched fault.
        File.WriteAllBytes(path, originalPng);
        Assert.Equal(HealthState.Faulted, (await cold.GetSnapshotAsync()).Evidence.State);
    }

    [Fact]
    [Trait("VerificationId", "V151_R08")]
    public async Task V151_R08_CorruptStageDuringAnActiveAttemptAppendsIntegrityFailureBeforeStopping()
    {
        using var root = new ProductionImageTestRoot();
        using var final = new ProductionImageFinalizationTestRoot(root.Options);
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V151.ActiveIntegrity", "1", EvidenceCaptureMode.All));
        using var issuer = new ProductionTestIssuer();
        var staged = await StageDurableProductionImageAsync(root, peer, harness, issuer);
        var target = await MigrateFinalizationStoreAsync(harness, final.Options);
        await using var store = new SqliteCommandStore(target);
        Assert.True((await store.Initialization).Committed);
        var faults = new List<string>();
        var worker = new ProductionImageFinalizationWorker(store, target, Guid.NewGuid(), store.Initialization,
            _ => { }, reason => { faults.Add(reason); return Task.CompletedTask; }, boundary =>
            {
                // After initial input verification and durable AttemptStarted, before the
                // encoder acquires its protected stage handle: a real decoder failure.
                if (boundary == ImageFinalizationBoundary.BeforeStageOpen)
                    File.WriteAllBytes(staged.StagePath, new byte[] { 1, 2, 3 });
            });
        try
        {
            await worker.Startup.WaitAsync(TimeSpan.FromSeconds(10));
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEmpty(faults);
            var image = await ReadSingleAsync(new SqliteProductionImageEvidenceQuery(target), staged.Work.WorkId);
            var failure = Assert.Single(image.Events, e => e.Kind == ProductionImageFinalizationKind.AttemptFailed);
            Assert.Equal(ProductionImageFailureCategory.Integrity, failure.Failure!.Category);
            Assert.True(image.State.IntegrityConflict);
            Assert.Null(image.State.Success);
            Assert.True(File.Exists(staged.StagePath));
            Assert.Empty(Directory.GetFiles(final.Path, "*.png"));
            Assert.Equal(staged.CoreContentHash,
                (await new SqliteProductionInspectionHistoryQuery(target).ReadCurrentAsync()).Latest!.Core!.ContentHash);
        }
        finally { Assert.True(await worker.StopAsync()); }
    }

    private static ProductionImageFinalizationWorker ImageWorkerOf(IStationRuntime runtime) =>
        Assert.IsType<ProductionImageFinalizationWorker>(typeof(StationRuntime).GetField("_imageFinalizationWorker",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime));

    private static ProductionImageFinalizer ImageFilesOf(ProductionImageFinalizationWorker worker) =>
        Assert.IsType<ProductionImageFinalizer>(typeof(ProductionImageFinalizationWorker).GetField("_files",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(worker));
}
