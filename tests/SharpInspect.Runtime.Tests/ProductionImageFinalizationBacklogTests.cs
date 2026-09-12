using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("VerificationId", "V151_R10")]
    public async Task V151_R10_BacklogCountOrAgeRaisesABlockingAlarmWithoutChangingPublishedCore(bool age)
    {
        using var root = new ProductionImageTestRoot();
        using var final = new ProductionImageFinalizationTestRoot(root.Options);
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        var original = TraceStoragePolicyRuntimeTests.Policy();
        var limits = new TraceBacklogLimits(age ? original.ImageBacklog.MaximumItems : 1,
            original.ImageBacklog.MaximumBytes, age ? TimeSpan.FromSeconds(2) : original.ImageBacklog.MaximumOldestAge);
        var policy = new TraceStoragePolicyDefinition(original.PolicyId, original.Version, original.ApprovalReference,
            original.ApprovalVersion, original.Rationale, original.RetentionRules, original.MinimumReserveBytes,
            original.MinimumReservePercent, original.RequiredRoutes, limits, original.EvidenceStageTimeout,
            original.TraceCommitTimeout, original.Scrubber, original.Checkpoint, original.MaximumWalBytes);
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V151.Backlog", "1", EvidenceCaptureMode.All),
            imageFinalization: final.Options, tracePolicy: policy);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Backlog fixture Ready");
        var files = ImageFilesOf(ImageWorkerOf(harness.Runtime));
        var hook = typeof(ProductionImageFinalizer).GetField("_faultHook", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        hook.SetValue(files, (Action<ImageFinalizationBoundary>)(boundary =>
        {
            if (boundary != ImageFinalizationBoundary.BeforeEncode) return;
            entered.TrySetResult(true);
            if (!release.Wait(TimeSpan.FromSeconds(25))) throw new TimeoutException("V151.BacklogHoldExpired");
        }));
        try
        {
            peer.RaiseTrigger(61, 1);
            await WaitForProductionResultAsync(harness, peer, 1);
            peer.SetTrigger(false);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitConditionAsync(() => peer.AckLowCount == 1, "Accepted cycle completes despite backlog limit");
            await WaitProductionAsync(harness, state => !state.Ready && state.AlarmState?.Instances.Any(alarm =>
                alarm.Code == "ImageEvidenceBacklog" && alarm.ProductionImpact == ProductionImpact.BlockNewTriggers &&
                !alarm.SourceHealthy) == true, "Count/age threshold raises durable backlog alarm");
            var snapshot = await harness.Runtime.GetSnapshotAsync();
            Assert.DoesNotContain(snapshot.AlarmState!.Instances, alarm => alarm.Code == "EvidenceIntegrityFault");
            var history = new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options);
            var core = (await history.ReadCurrentAsync()).Latest!.Core!;
            Assert.NotNull(core.ImageEvidence!.Work);
            Assert.True(File.Exists(Path.Combine(root.Path, core.ImageEvidence.Manifest!.StageFileName)));
            release.Set();
            await WaitForFinalizationAsync(harness.Service<IProductionImageEvidenceQuery>(), core.ImageEvidence.Work!.WorkId,
                record => record.State.CleanupState == ProductionImageCleanupState.Released,
                "Backlog drains after file owner release", ImageWorkerOf(harness.Runtime));
            await WaitProductionAsync(harness, state => state.AlarmState is { Available: true } alarms &&
                !alarms.Instances.Any(alarm => alarm.Code == "ImageEvidenceBacklog" &&
                    alarm.Lifecycle != AlarmLifecycle.Cleared),
                "Backlog recovery records the cleared source condition");
            var alarms = await new SqliteAlarmHistoryQuery(harness.Fixture.Options).QueryAsync(
                new AlarmHistoryFilter(code: "ImageEvidenceBacklog", pageSize: 100));
            Assert.True(alarms.Available, alarms.ReasonCode);
            Assert.Contains(alarms.Records, entry => entry.Transition == AlarmTransitionKind.SourceRecovered);
            Assert.Contains(alarms.Records, entry => entry.Transition == AlarmTransitionKind.Cleared);
            Assert.Equal(core.ContentHash, (await history.ReadCurrentAsync()).Latest!.Core!.ContentHash);
            Assert.Equal(1, peer.ResultValidHighCount);
        }
        finally
        {
            release.Set();
            await files.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            hook.SetValue(files, null);
        }
    }

    [Fact]
    [Trait("VerificationId", "V151_R12")]
    public async Task V151_R12_OrphanNeedsReconciliationWithoutClaimingReferencedEvidenceWasCorrupt()
    {
        using var root = new ProductionImageTestRoot();
        using var final = new ProductionImageFinalizationTestRoot(root.Options);
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V151.Orphan", "1", EvidenceCaptureMode.All));
        using var issuer = new ProductionTestIssuer();
        var staged = await StageDurableProductionImageAsync(root, peer, harness, issuer);
        var target = await MigrateFinalizationStoreAsync(harness, final.Options);
        var orphan = Path.Combine(root.Path, Guid.NewGuid().ToString("N") + ".stage");
        var original = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(orphan, original);
        await using var store = new SqliteCommandStore(target);
        Assert.True((await store.Initialization).Committed);
        await using var runtime = new StationRuntime(store, productionStoreOptions: target);
        var worker = ImageWorkerOf(runtime);
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.Startup);
        await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("ProductionImageStartupReconciliationRequired", worker.FailureReason);
        var snapshot = await runtime.GetSnapshotAsync();
        Assert.False(snapshot.Ready);
        Assert.Equal(HealthState.Faulted, snapshot.Evidence.State);
        Assert.DoesNotContain(snapshot.AlarmState!.Instances, alarm => alarm.Code == "EvidenceIntegrityFault");
        Assert.Equal(original, File.ReadAllBytes(orphan));
        Assert.True(File.Exists(staged.StagePath));
        Assert.Empty((await ReadSingleAsync(new SqliteProductionImageEvidenceQuery(target), staged.Work.WorkId)).Events);
    }
}
