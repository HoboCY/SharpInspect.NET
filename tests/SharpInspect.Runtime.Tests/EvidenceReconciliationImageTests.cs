using System.Runtime.InteropServices;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Evidence;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory, Trait("VerificationId", "V154_R02")]
    [InlineData("pending")]
    [InlineData("bound_final")]
    [InlineData("bound_final_missing_stage")]
    [InlineData("unbound_final")]
    [InlineData("both_missing")]
    [InlineData("corrupt_stage_with_valid_final")]
    [InlineData("corrupt_final")]
    [InlineData("hardlink_stage")]
    [InlineData("succeeded_corrupt_history")]
    public async Task V154_R02_ReconcilesRealCoreStageFinalCombinationsWithoutChangingTheCore(string mode)
    {
        using var stageRoot = new ProductionImageTestRoot();
        using var finalRoot = new ProductionImageFinalizationTestRoot(stageRoot.Options);
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: stageRoot.Options, capturePolicy: new("V154.Combinations", "1", EvidenceCaptureMode.All));
        using var issuer = new ProductionTestIssuer();
        var staged = await StageDurableProductionImageAsync(stageRoot, peer, harness, issuer);
        var source = await MigrateFinalizationStoreAsync(harness, finalRoot.Options);
        var stageBytes = File.ReadAllBytes(staged.StagePath);
        var finalPath = Path.Combine(finalRoot.Path, staged.Work.Manifest.ManifestId.ToString("N") + ".png");
        byte[]? finalBytes = null;
        var bound = mode is "bound_final" or "bound_final_missing_stage" or
            "corrupt_stage_with_valid_final" or "corrupt_final" or "succeeded_corrupt_history";
        await using (var setup = new SqliteCommandStore(source))
        {
            var initialized = await setup.Initialization;
            Assert.True(initialized.Committed, initialized.ReasonCode);
            if (bound || mode == "unbound_final")
            {
                var epoch = Guid.NewGuid(); var attempt = Guid.NewGuid();
                var temporaryName = staged.Work.Manifest.ManifestId.ToString("N") + "." + attempt.ToString("N") + ".tmp";
                if (bound)
                {
                    var begun = await setup.BeginImageFinalizationAttemptAsync(new(staged.Work.WorkId, attempt, 1,
                        epoch, DateTimeOffset.UtcNow, false), ReconciliationDeadline());
                    Assert.True(begun.Committed, begun.ReasonCode);
                    temporaryName = begun.Event!.Attempt!.TemporaryFileName;
                }
                var finalizer = FinalizerFor(finalRoot.Options);
                ProductionImageSuccessDescriptor? success = null;
                using (var claim = await finalizer.FinalizeAsync(staged.Work, attempt, temporaryName,
                    Path.GetFileName(finalPath), false, TimeSpan.FromSeconds(5), default))
                {
                    if (mode == "succeeded_corrupt_history")
                    {
                        var persisted = await setup.AppendImageFinalizationOutcomeAsync(new ImageFinalizationSuccessRequest(
                            staged.Work.WorkId, epoch, DateTimeOffset.UtcNow, claim), ReconciliationDeadline());
                        Assert.True(persisted.Committed, persisted.ReasonCode);
                        success = persisted.Event!.Success!;
                    }
                }
                finalBytes = File.ReadAllBytes(finalPath);
                if (success is not null)
                {
                    await finalizer.ReleaseSucceededStageAsync(staged.Work, success, TimeSpan.FromSeconds(5), default);
                    var released = await setup.RecordImageStageReleasedAsync(new(staged.Work.WorkId, epoch,
                        DateTimeOffset.UtcNow, "ImageFinalizationStageReleased"), ReconciliationDeadline());
                    Assert.True(released.Committed, released.ReasonCode);
                }
            }
        }
        if (mode is "bound_final_missing_stage" or "both_missing") File.Delete(staged.StagePath);
        if (mode == "corrupt_stage_with_valid_final") File.WriteAllBytes(staged.StagePath, new byte[] { 1, 2, 3 });
        if (mode is "corrupt_final" or "succeeded_corrupt_history") File.WriteAllBytes(finalPath, new byte[] { 1, 2, 3 });
        if (mode == "hardlink_stage")
            Assert.True(CreateReconciliationHardLink(staged.StagePath + ".duplicate", staged.StagePath, IntPtr.Zero));

        var target = ProductionOutboxMigrationTests.WithReconciliation(source, Path.GetDirectoryName(source.DatabasePath)!);
        await MigrateReconciliationAsync(target);
        await using var store = new SqliteCommandStore(target);
        var opened = await store.Initialization;
        Assert.True(opened.Committed, opened.ReasonCode);
        var faults = new List<string>();
        var worker = new EvidenceReconciliationWorker(store, target, Guid.NewGuid(), Task.CompletedTask,
            Task.CompletedTask, reason => { faults.Add(reason); return Task.CompletedTask; }, startScrubber: false);
        var integrity = mode is "both_missing" or "corrupt_stage_with_valid_final" or "corrupt_final" or "hardlink_stage";
        try
        {
            if (integrity)
                await Assert.ThrowsAnyAsync<Exception>(() => worker.Startup.WaitAsync(TimeSpan.FromSeconds(20)));
            else
                await worker.Startup.WaitAsync(TimeSpan.FromSeconds(20));
            var snapshot = await new SqliteEvidenceReconciliationQuery(target).ReadAsync(new());
            Assert.True(snapshot.Available, snapshot.ReasonCode);
            if (integrity)
            {
                Assert.True(snapshot.IntegrityFaultRecorded);
                Assert.False(snapshot.LatestStartup!.Completed);
                Assert.NotEmpty(faults);
            }
            else
            {
                Assert.True(snapshot.LatestStartup!.Completed);
                Assert.False(snapshot.IntegrityFaultRecorded);
                Assert.Empty(faults);
            }
            var image = await ReadSingleAsync(new SqliteProductionImageEvidenceQuery(target), staged.Work.WorkId);
            if (mode is "bound_final" or "bound_final_missing_stage")
            {
                Assert.Equal(ProductionImageFinalizationState.Succeeded, image.State.State);
                Assert.Contains(snapshot.Records, x => x.Kind == EvidenceReconciliationEventKind.ImageFinalRecovered);
                Assert.Equal(staged.Work.Manifest.CanonicalPixelHash, CanonicalHashOf(finalPath, staged.Work.Manifest,
                    CodecLimits(stageRoot.Options, finalRoot.Root)));
            }
            if (mode == "unbound_final")
            {
                Assert.Equal(ProductionImageFinalizationState.Pending, image.State.State);
                Assert.False(File.Exists(finalPath));
                var orphan = Assert.Single(snapshot.Records, x => x.Kind == EvidenceReconciliationEventKind.Quarantined);
                Assert.Null(orphan.Subject!.InspectionId);
                Assert.Null(orphan.Subject.ManifestId);
                Assert.Null(orphan.Subject.WorkId);
                Assert.Equal(finalBytes, File.ReadAllBytes(Path.Combine(target.EvidenceReconciliation!.FinalQuarantine!.Root,
                    orphan.Subject.QuarantineFileName!)));
            }
            if (mode == "pending")
            {
                Assert.Equal(ProductionImageFinalizationState.Pending, image.State.State);
                Assert.Empty(image.Events);
                var finalizer = new ProductionImageFinalizationWorker(store, target, Guid.NewGuid(), worker.Startup,
                    _ => { }, reason => { faults.Add(reason); return Task.CompletedTask; });
                try
                {
                    await finalizer.Startup.WaitAsync(TimeSpan.FromSeconds(5));
                    await WaitForFinalizationAsync(new SqliteProductionImageEvidenceQuery(target), staged.Work.WorkId,
                        x => x.State.CleanupState == ProductionImageCleanupState.Released, "V154 pending resumed", finalizer);
                }
                finally { Assert.True(await finalizer.StopAsync()); }
                Assert.Empty(faults);
            }
            if (mode == "succeeded_corrupt_history")
            {
                Assert.Equal(2, snapshot.Records.Count); // Retained history did not consume startup observation rows.
                await Assert.ThrowsAnyAsync<Exception>(() => worker.RunScrubTurnAsync());
                snapshot = await new SqliteEvidenceReconciliationQuery(target).ReadAsync(new());
                Assert.True(snapshot.IntegrityFaultRecorded);
                Assert.NotEmpty(faults);
                File.WriteAllBytes(finalPath, finalBytes!);
            }
            var core = await new SqliteProductionInspectionHistoryQuery(target).ReadAsync(staged.Work.Manifest.InspectionId);
            Assert.True(core.Available, core.ReasonCode);
            Assert.Equal(staged.CoreContentHash, core.Latest!.Core!.ContentHash);
        }
        finally { Assert.True(await worker.StopAsync()); }
        if (mode == "both_missing")
        {
            File.WriteAllBytes(staged.StagePath, stageBytes);
            var restarted = new EvidenceReconciliationWorker(store, target, Guid.NewGuid(), Task.CompletedTask,
                Task.CompletedTask, _ => Task.CompletedTask, startScrubber: false);
            try
            {
                await Assert.ThrowsAnyAsync<Exception>(() => restarted.Startup.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal("EvidenceReconciliationIntegrityFaultRecorded", restarted.FailureReason);
            }
            finally { Assert.True(await restarted.StopAsync()); }
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateReconciliationHardLink(string fileName, string existingFileName, IntPtr security);
}
