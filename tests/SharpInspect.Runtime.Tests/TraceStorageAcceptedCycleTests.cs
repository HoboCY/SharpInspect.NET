using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact, Trait("VerificationId", "V155_C05")]
    public async Task V155_C05_WalCrossingAfterAdmissionAllowsCoreImageAndAckSettlementButRejectsNextCycle()
    {
        using var stage = new ProductionImageTestRoot();
        using var final = new ProductionImageFinalizationTestRoot(stage.Options);
        using var stageQuarantine = new ProductionImageFinalizationTestRoot(stage.Options);
        using var finalQuarantine = new ProductionImageFinalizationTestRoot(stage.Options);
        var template = TraceCheckpointTests.Policy();
        var budget = new TraceStorageMaintenanceBudget(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5),
            final.Root.MaximumFinalFileBytes + stage.Options.Stage.MaximumStageBytes, 16);
        var policy = new TraceStoragePolicyDefinition(template.PolicyId, template.Version, template.ApprovalReference,
            template.ApprovalVersion, template.Rationale, template.RetentionRules, template.MinimumReserveBytes,
            template.MinimumReservePercent, template.RequiredRoutes, template.ImageBacklog, template.EvidenceStageTimeout,
            template.TraceCommitTimeout, budget, template.Checkpoint, template.MaximumWalBytes);
        var reconcile = new EvidenceReconciliationStoreOptions(budget)
        {
            StageQuarantine = new(stageQuarantine.Path, stage.Options.Stage.MaximumStageBytes, 256L << 20, 1000),
            FinalQuarantine = new(finalQuarantine.Path, final.Root.MaximumFinalFileBytes, 256L << 20, 1000)
        };
        var retention = new TraceStorageRetentionOptions(new("accepted-cycle", "1", "controlled-test",
            "No deletion needed for the accepted-cycle WAL boundary.", Array.Empty<TraceRetentionClass>(),
            new(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2), final.Root.MaximumFinalFileBytes, 4), 2),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), new(128L << 20, 10000, 1L << 20));
        long wal = 0;
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: stage.Options, capturePolicy: new("V155.Wal", "1", EvidenceCaptureMode.All),
            imageFinalization: final.Options, evidenceReconciliation: reconcile, storageRetention: retention,
            tracePolicy: policy, readWalLength: _ => Volatile.Read(ref wal));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, value => value.Ready, "Ready before controlled WAL crossing");
        harness.Factory.HoldExecution(cooperativeCancellation: true);
        try
        {
            peer.RaiseTrigger(61, 1);
            await harness.Factory.ExecutionEntered.WaitAsync(TimeSpan.FromSeconds(5));
            // One accepted inspection exists, but the algorithm has not returned
            // and no result Core, PNG success or ACK settlement can exist yet.
            Assert.Equal(1, await harness.Fixture.ScalarAsync("SELECT COUNT(*) FROM production_inspection_admissions;"));
            Assert.Equal(0, await harness.Fixture.ScalarAsync("SELECT COUNT(*) FROM production_inspection_cores;"));
            Volatile.Write(ref wal, policy.MaximumWalBytes);
        }
        finally { harness.Factory.ReleaseExecution(); }
        await WaitForProductionResultAsync(harness, peer, 1);
        peer.SetTrigger(false);
        await WaitConditionAsync(() => peer.AckLowCount == 1, "Accepted cycle ACK reset beyond WAL limit");
        var history = new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options);
        var current = await history.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        var core = Assert.IsType<ProductionInspectionCore>(current.Latest!.Core);
        Assert.Equal(ExecutionStatus.Success, core.ExecutionStatus);
        var image = await WaitForFinalizationAsync(harness.Service<IProductionImageEvidenceQuery>(),
            core.ImageEvidence!.Work!.WorkId, state => state.State.CleanupState == ProductionImageCleanupState.Released,
            "Accepted image finalized beyond WAL limit");
        Assert.NotNull(image.State.Success);

        // The injected WAL observation exercises the writer boundary, while the
        // separate physical observer still reads actual disk bytes. It may expose
        // Ready until the next admission reaches the stricter writer recheck.
        await WaitProductionAsync(harness, value => value.Ready, "Ready before writer rechecks next admission");
        peer.RaiseTrigger(61, 2);
        await WaitProductionAsync(harness, value => !value.Ready && value.Recovery == RecoveryState.Required,
            "Next admission rejected at controlled WAL boundary");
        Assert.Equal(1, await harness.Fixture.ScalarAsync("SELECT COUNT(*) FROM production_inspection_admissions;"));
        Assert.Equal(1, await harness.Fixture.ScalarAsync("SELECT COUNT(*) FROM production_inspection_cores;"));
        peer.SetTrigger(false);
        Volatile.Write(ref wal, 0);
    }
}
