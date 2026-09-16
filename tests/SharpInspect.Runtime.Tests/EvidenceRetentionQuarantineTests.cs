using SharpInspect.Abstractions;
using SharpInspect.Runtime.Evidence;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory, Trait("VerificationId", "V155_W02"), Trait("VerificationId", "V155_W03")]
    [InlineData("success")]
    [InlineData("unsupported-start")]
    [InlineData("failure-cap")]
    [InlineData("unknown")]
    public async Task V155_W02_QuarantineOriginsAutomaticObligationsAndBoundedDeletionRecovery(string mode)
    {
        using var stage = new ProductionImageTestRoot();
        using var final = new ProductionImageFinalizationTestRoot(stage.Options);
        using var stageQuarantine = new ProductionImageFinalizationTestRoot(stage.Options);
        using var finalQuarantine = new ProductionImageFinalizationTestRoot(stage.Options);
        var p = TraceCheckpointTests.Policy();
        var scrub = new TraceStorageMaintenanceBudget(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5),
            final.Root.MaximumFinalFileBytes + stage.Options.Stage.MaximumStageBytes, 16);
        var rules = p.RetentionRules.Select(rule => rule.EvidenceClass == TraceRetentionClass.OrphanImageStage &&
            mode != "unsupported-start" ? new TraceRetentionRule(rule.EvidenceClass, RetentionStartEvent.Quarantined,
                rule.MinimumRetention) : rule);
        var policy = new TraceStoragePolicyDefinition(p.PolicyId, p.Version, p.ApprovalReference, p.ApprovalVersion,
            p.Rationale, rules, p.MinimumReserveBytes, p.MinimumReservePercent, p.RequiredRoutes, p.ImageBacklog,
            p.EvidenceStageTimeout, p.TraceCommitTimeout, scrub, p.Checkpoint, p.MaximumWalBytes);
        var reconciliation = new EvidenceReconciliationStoreOptions(scrub)
        {
            StageQuarantine = new(stageQuarantine.Path, stage.Options.Stage.MaximumStageBytes, 256L * 1024 * 1024, 1000),
            FinalQuarantine = new(finalQuarantine.Path, final.Root.MaximumFinalFileBytes, 256L * 1024 * 1024, 1000)
        };
        var retention = new TraceStorageRetentionOptions(new("V155.Quarantine", "1", "controlled fixture",
            "Only proven quarantine origins may expire.", new[] { TraceRetentionClass.QuarantineEvidence, TraceRetentionClass.OrphanImageStage },
            new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), scrub.MaximumBytes, 4), 2),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), new(128L << 20, 10000, 1L << 20));
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: stage.Options, capturePolicy: new("V155.Quarantine", "1", EvidenceCaptureMode.All),
            imageFinalization: final.Options, tracePolicy: policy,
            evidenceReconciliation: reconciliation, storageRetention: retention);
        await ((IAsyncDisposable)harness.Service<IStationRuntime>()).DisposeAsync();
        File.WriteAllBytes(Path.Combine(final.Path, "unowned.png"), Array.Empty<byte>());
        var hasStage = mode is "success" or "unsupported-start";
        if (hasStage) File.WriteAllBytes(Path.Combine(stage.Options.Stage.StageRoot, "unowned.stage"), new byte[] { 1, 2, 3 });
        var faults = new List<string>();
        async Task Reconcile()
        {
            var worker = new EvidenceReconciliationWorker(harness.Fixture.Store, harness.Fixture.Options,
                Guid.NewGuid(), Task.CompletedTask, Task.CompletedTask,
                reason => { faults.Add(reason); return Task.CompletedTask; }, startScrubber: false);
            try { await worker.Startup.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { Assert.True(await worker.StopAsync()); }
        }
        await Reconcile();
        var quarantined = await new SqliteEvidenceReconciliationQuery(harness.Fixture.Options).ReadAsync(new());
        Assert.True(quarantined.Available, quarantined.ReasonCode);
        Assert.Equal(hasStage ? 2 : 1, quarantined.Records.Count(row => row.Kind == EvidenceReconciliationEventKind.Quarantined));
        var now = DateTimeOffset.UtcNow;
        var files = new EvidenceRetentionFiles(harness.Fixture.Options, boundary =>
        {
            if (mode == "failure-cap" && boundary == EvidenceRetentionFileBoundary.BeforeDelete)
                throw new IOException("ControlledDeleteDenied");
        });
        var cleanup = new EvidenceRetentionWorker(harness.Fixture.Store, harness.Fixture.Options, Guid.NewGuid(),
            Task.CompletedTask, Task.CompletedTask, reason => { faults.Add(reason); return Task.CompletedTask; },
            () => true, () => now, files);
        var query = new SqliteEvidenceRetentionQuery(harness.Fixture.Options);
        try
        {
            await cleanup.Startup.WaitAsync(TimeSpan.FromSeconds(15));
            var established = await query.ReadAsync(new());
            Assert.True(established.Available, established.ReasonCode);
            Assert.Equal(mode == "success" ? 2 : 1, established.Subjects.Count);
            Assert.All(established.Subjects, subject => Assert.Equal(RetentionStartEvent.Quarantined, subject.Obligation.StartsAt));
            Assert.Equal(2, established.ExecutionPolicy!.MaximumDeletionAttempts);
            now = established.Subjects.Max(subject => subject.EffectiveUntilUtc).AddDays(1);
            if (mode == "unknown")
            {
                var writes = 0;
                harness.Fixture.Store.RetentionAfterReadProof = () =>
                {
                    if (Interlocked.Increment(ref writes) == 2)
                        throw new IOException("ControlledOutcomePersistenceInterruption");
                };
                await Assert.ThrowsAsync<IOException>(() => cleanup.RunTurnAsync(default));
                harness.Fixture.Store.RetentionAfterReadProof = null;
            }
            else await cleanup.RunTurnAsync(default);
            if (mode == "failure-cap")
            {
                now += retention.ExecutionPolicy.Cleanup.Interval;
                await cleanup.RunTurnAsync(default);
                var failed = await query.ReadAsync(new());
                Assert.Equal(2, Assert.Single(failed.Subjects).DeleteAttempts);
                Assert.Equal(0, failed.DeletedFiles);
                var position = failed.ThroughPosition;
                now += retention.ExecutionPolicy.Cleanup.Interval;
                await cleanup.RunTurnAsync(default);
                Assert.Equal(position, (await query.ReadAsync(new())).ThroughPosition);
                Assert.Single(Directory.GetFiles(finalQuarantine.Path));
            }
            else if (mode != "unknown")
            {
                Assert.Equal(mode == "success" ? 2 : 1, (await query.ReadAsync(new())).DeletedFiles);
                Assert.Empty(Directory.GetFiles(finalQuarantine.Path));
                Assert.Equal(mode == "unsupported-start" ? 1 : 0, Directory.GetFiles(stageQuarantine.Path).Length);
            }
        }
        finally { harness.Fixture.Store.RetentionAfterReadProof = null; Assert.True(await cleanup.StopAsync()); }
        if (mode == "unknown")
        {
            Assert.Empty(Directory.GetFiles(finalQuarantine.Path));
            var recovered = new EvidenceRetentionWorker(harness.Fixture.Store, harness.Fixture.Options, Guid.NewGuid(),
                Task.CompletedTask, Task.CompletedTask, reason => { faults.Add(reason); return Task.CompletedTask; },
                () => false, () => now);
            try { await Assert.ThrowsAsync<InvalidOperationException>(() => recovered.Startup.WaitAsync(TimeSpan.FromSeconds(15))); }
            finally { Assert.True(await recovered.StopAsync()); }
            var unknown = await query.ReadAsync(new());
            Assert.True(unknown.Available, unknown.ReasonCode);
            Assert.Equal(1, unknown.UnknownDeletes);
            Assert.Equal(1, unknown.PendingDeletes);
            Assert.Equal(0, unknown.DeletedFiles);
        }
        else
        {
            await Reconcile(); // Actual cold T54 quarantine checking accepts only the committed tombstones.
            Assert.Empty(faults);
        }
    }
}
