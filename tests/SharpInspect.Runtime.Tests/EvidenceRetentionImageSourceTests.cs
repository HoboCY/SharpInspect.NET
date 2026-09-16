using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory, Trait("VerificationId", "V155_S05"), Trait("VerificationId", "V155_G01"), Trait("VerificationId", "V155_G02"),
        Trait("VerificationId", "V155_W01")]
    [InlineData(4096)]
    [InlineData(16384)]
    public async Task V155_S05_RealFinalizedPngDerivesRetentionFromFrozenManifestWithoutChangingCore(int maximumPayloadBytes)
    {
        using var root = new ProductionImageTestRoot();
        using var final = new ProductionImageFinalizationTestRoot(root.Options);
        using var stageQuarantine = new ProductionImageFinalizationTestRoot(root.Options);
        using var finalQuarantine = new ProductionImageFinalizationTestRoot(root.Options);
        var old = TraceCheckpointTests.Policy();
        var scrub = new TraceStorageMaintenanceBudget(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5),
            final.Root.MaximumFinalFileBytes + root.Options.Stage.MaximumStageBytes, 16);
        var policy = new TraceStoragePolicyDefinition(old.PolicyId, old.Version, old.ApprovalReference,
            old.ApprovalVersion, old.Rationale, old.RetentionRules, old.MinimumReserveBytes,
            old.MinimumReservePercent, old.RequiredRoutes, old.ImageBacklog, old.EvidenceStageTimeout,
            old.TraceCommitTimeout, scrub, old.Checkpoint, old.MaximumWalBytes);
        var reconciliation = new EvidenceReconciliationStoreOptions(scrub)
        {
            StageQuarantine = new(stageQuarantine.Path, root.Options.Stage.MaximumStageBytes,
                256L * 1024 * 1024, 1000),
            FinalQuarantine = new(finalQuarantine.Path, final.Root.MaximumFinalFileBytes,
                256L * 1024 * 1024, 1000)
        };
        var retention = new TraceStorageRetentionOptions(new("retention-png-fixture", "1", "fixture-approval",
            "Test exact manifest retention provenance.", new[] { TraceRetentionClass.AuthoritativeImage },
            new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2), final.Root.MaximumFinalFileBytes, 4), 3),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), new(128L << 20, 10000, 1L << 20)) { MaximumPayloadBytes = maximumPayloadBytes };
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V155.Source", "1", EvidenceCaptureMode.All),
            imageFinalization: final.Options, tracePolicy: policy, evidenceReconciliation: reconciliation,
            storageRetention: retention);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        _ = StoragePolicies.TraceStorageCapacityInventory.Observe(harness.Fixture.Options, policy,
            new StoreDeadline(TimeSpan.FromSeconds(5)), default);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, value => value.Ready, "Retention fixture Ready");
        peer.RaiseTrigger(61, 1);
        await WaitForProductionResultAsync(harness, peer, 1);
        peer.SetTrigger(false);
        var history = new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options);
        var core = (await history.ReadCurrentAsync()).Latest!.Core!;
        var work = core.ImageEvidence!.Work!;
        var record = await WaitForFinalizationAsync(harness.Service<IProductionImageEvidenceQuery>(), work.WorkId,
            value => value.State.CleanupState == ProductionImageCleanupState.Released, "Retention source finalization");
        await WaitConditionAsync(() => peer.AckLowCount == 1, "Retention source cycle completed");
        EvidenceRetentionObligation obligation;
        using (var connection = SqliteNative.Open(harness.Fixture.Options.DatabasePath, readOnly: true))
        {
            obligation = SqliteCommandStore.ReadImageRetentionObligation(connection.Handle!, work.Manifest.ManifestId,
                new StoreDeadline(TimeSpan.FromSeconds(5)))!;
        }
        Assert.NotNull(obligation);
        Assert.Equal(work.Manifest.ContentHash, obligation.SourceContentHash);
        Assert.Equal(work.Manifest.TracePolicySnapshotHash, obligation.PolicySnapshotHash);
        Assert.Equal(work.Manifest.RetentionRuleHash, obligation.RuleHash);
        Assert.Equal(work.Manifest.CreatedAtUtc, obligation.StartedAtUtc);
        Assert.Equal(record.State.Success!.EncodedByteLength, obligation.ByteLength);
        var establishmentAttempts = 0;
        harness.Fixture.Store.RetentionAfterReadProof = () =>
        {
            if (Interlocked.Increment(ref establishmentAttempts) == 1)
                throw new TimeoutException("ControlledTransientEstablishmentDelay");
        };
        var establishment = new Evidence.EvidenceRetentionWorker(harness.Fixture.Store, harness.Fixture.Options,
            Guid.NewGuid(), Task.CompletedTask, Task.CompletedTask, _ => Task.CompletedTask, () => false);
        try
        {
            await establishment.Startup.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(establishmentAttempts >= 2);
        }
        finally { harness.Fixture.Store.RetentionAfterReadProof = null; Assert.True(await establishment.StopAsync()); }
        var snapshot = await new SqliteEvidenceRetentionQuery(harness.Fixture.Options).ReadAsync(new(Owner: obligation.Owner));
        Assert.True(snapshot.Available, snapshot.ReasonCode);
        var retained = Assert.Single(snapshot.Subjects);
        Assert.Equal(obligation, retained.Obligation);
        Assert.Equal(EvidenceRetentionDisposition.Retained, retained.Disposition);
        Assert.Equal(core.ContentHash, (await history.ReadCurrentAsync()).Latest!.Core!.ContentHash);
        Assert.True(File.Exists(Path.Combine(final.Path, obligation.FileName)));
        var service = harness.Service<IEvidenceRetentionService>();
        var access = await service.GetAccessAsync(harness.Fixture.Invocation());
        Assert.True(access.Allowed, access.ReasonCode);
        Assert.True(access.RequiresStepUp);
        var change = new ChangeEvidenceRetentionCommand(Guid.NewGuid(), harness.Fixture.Invocation(), obligation.Owner,
            retained.Revision, EvidenceRetentionChange.PlaceHold, Guid.NewGuid(), null, "Protect the retained test PNG.");
        var withoutGrant = await service.ChangeAsync(change);
        Assert.False(withoutGrant.Succeeded);
        Assert.Equal("StepUpRequired", withoutGrant.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, withoutGrant.Outcome.Audit);

        async Task<ChangeEvidenceRetentionCommand> Grant(ChangeEvidenceRetentionCommand value)
        {
            var grant = await harness.Fixture.Authorization.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(),
                harness.Fixture.Invocation(), new StepUpBinding(Permission.DeleteEvidence, value.CorrelationId,
                    value.AuthorizationTarget, AuditedCommandKind.ChangeEvidenceRetention), harness.Fixture.Password));
            Assert.True(grant.Succeeded, grant.ReasonCode);
            return value with { Invocation = harness.Fixture.Invocation(grant.GrantId) };
        }
        change = await Grant(change);
        var hold = await service.ChangeAsync(change);
        Assert.True(hold.Succeeded, hold.Outcome.ReasonCode);
        Assert.Equal(EvidenceRetentionDisposition.Held, hold.Status!.Disposition);
        var coldHeld = await new SqliteEvidenceRetentionQuery(harness.Fixture.Options).ReadAsync(new(Owner: obligation.Owner));
        Assert.True(coldHeld.Available, coldHeld.ReasonCode);
        Assert.Equal(1, coldHeld.ActiveHolds);
        Assert.Equal(2, coldHeld.ThroughPosition);
        Assert.Equal(change.Reason, coldHeld.Records.Single(row => row.Kind == EvidenceRetentionEventKind.HoldPlaced).Reason);
        var reused = await service.ChangeAsync(change);
        Assert.False(reused.Succeeded);
        Assert.Equal("DuplicateCorrelationId", reused.Outcome.ReasonCode);
        var stale = await Grant(new(Guid.NewGuid(), harness.Fixture.Invocation(), obligation.Owner, 1,
            EvidenceRetentionChange.ReleaseHold, change.HoldId, null, "Stale review must be rejected."));
        var staleResult = await service.ChangeAsync(stale);
        Assert.False(staleResult.Succeeded);
        Assert.Equal("RetentionRevisionChanged", staleResult.Outcome.ReasonCode);
        var extension = await Grant(new(Guid.NewGuid(), harness.Fixture.Invocation(), obligation.Owner, 2,
            EvidenceRetentionChange.Extend, null, obligation.RetainUntilUtc.AddDays(1), "Extend the approved retention."));
        var extended = await service.ChangeAsync(extension);
        Assert.True(extended.Succeeded, extended.Outcome.ReasonCode);
        Assert.Equal(EvidenceRetentionDisposition.Held, extended.Status!.Disposition);
        var release = await Grant(new(Guid.NewGuid(), harness.Fixture.Invocation(), obligation.Owner, 3,
            EvidenceRetentionChange.ReleaseHold, change.HoldId, null, "Reviewed test hold may be released."));
        var released = await service.ChangeAsync(release);
        Assert.True(released.Succeeded, released.Outcome.ReasonCode);
        var finalHistory = await new SqliteEvidenceRetentionQuery(harness.Fixture.Options).ReadAsync(new(Owner: obligation.Owner));
        Assert.True(finalHistory.Available, finalHistory.ReasonCode);
        Assert.Equal(4, finalHistory.ThroughPosition);
        if (maximumPayloadBytes == 4096)
        {
            var large = await Grant(new(Guid.NewGuid(), harness.Fixture.Invocation(), obligation.Owner, 4,
                EvidenceRetentionChange.PlaceHold, Guid.NewGuid(), null, new string('保', 512)));
            for (var repeat = 0; repeat < 2; repeat++)
            {
                var rejected = await service.ChangeAsync(large);
                Assert.False(rejected.Succeeded);
                Assert.Equal("RetentionPayloadCapacityExceeded", rejected.Outcome.ReasonCode);
                Assert.Equal(AuditPersistence.Persisted, rejected.Outcome.Audit);
            }
            var unchanged = await service.ReadAsync(new(Owner: obligation.Owner));
            Assert.True(unchanged.Available, unchanged.ReasonCode);
            Assert.Equal(4, unchanged.ThroughPosition);
            Assert.Equal(0, unchanged.ActiveHolds);
        }

        Assert.Equal(0, finalHistory.ActiveHolds);
        Assert.Equal(obligation.RetainUntilUtc.AddDays(1), Assert.Single(finalHistory.Subjects).EffectiveUntilUtc);
        Assert.Equal(core.ContentHash, (await history.ReadCurrentAsync()).Latest!.Core!.ContentHash);
        var finalHold = await Grant(new(Guid.NewGuid(), harness.Fixture.Invocation(), obligation.Owner, 4,
            EvidenceRetentionChange.PlaceHold, Guid.NewGuid(), null, "Hold must survive the controlled expiry clock."));
        Assert.True((await service.ChangeAsync(finalHold)).Succeeded);
        await ((IAsyncDisposable)harness.Service<IStationRuntime>()).DisposeAsync();
        string? retentionFault = null;
        var future = obligation.RetainUntilUtc.AddDays(2);
        var cleanup = new Evidence.EvidenceRetentionWorker(harness.Fixture.Store, harness.Fixture.Options,
            Guid.NewGuid(), Task.CompletedTask, Task.CompletedTask,
            reason => { retentionFault = reason; return Task.CompletedTask; }, () => true, () => future);
        try
        {
            await cleanup.Startup.WaitAsync(TimeSpan.FromSeconds(10));
            await cleanup.RunTurnAsync(default);
            Assert.True(File.Exists(Path.Combine(final.Path, obligation.FileName)));
            Assert.Equal(1, (await service.ReadAsync(new(Owner: obligation.Owner))).ActiveHolds);
            var lastRelease = await Grant(new(Guid.NewGuid(), harness.Fixture.Invocation(), obligation.Owner, 5,
                EvidenceRetentionChange.ReleaseHold, finalHold.HoldId, null, "Controlled retention cleanup may proceed."));
            Assert.True((await service.ChangeAsync(lastRelease)).Succeeded);
            await cleanup.RunTurnAsync(default);
            var deleted = await service.ReadAsync(new(Owner: obligation.Owner));
            Assert.True(deleted.Available, deleted.ReasonCode);
            Assert.Equal(1, deleted.DeletedFiles);
            Assert.Equal(obligation.ByteLength, deleted.DeletedLogicalBytes);
            Assert.Equal(8, deleted.ThroughPosition);
            Assert.False(File.Exists(Path.Combine(final.Path, obligation.FileName)));
            Assert.Equal(core.ContentHash, (await history.ReadCurrentAsync()).Latest!.Core!.ContentHash);
            Assert.Null(retentionFault);
        }
        finally { Assert.True(await cleanup.StopAsync()); }

        string? reconciliationFault = null;
        var reconciler = new Evidence.EvidenceReconciliationWorker(harness.Fixture.Store, harness.Fixture.Options,
            Guid.NewGuid(), Task.CompletedTask, Task.CompletedTask,
            reason => { reconciliationFault = reason; return Task.CompletedTask; }, () => true, false);
        try
        {
            await reconciler.Startup.WaitAsync(TimeSpan.FromSeconds(10));
            await reconciler.RunScrubTurnAsync();
            Assert.Null(reconciliationFault);
            var coldRetention = await service.ReadAsync(new(Owner: obligation.Owner));
            Assert.True(coldRetention.Available, coldRetention.ReasonCode);
            Assert.Equal(1, coldRetention.DeletedFiles);
            var scrubbed = await new SqliteEvidenceReconciliationQuery(harness.Fixture.Options).ReadAsync(new());
            Assert.True(scrubbed.Available, scrubbed.ReasonCode);
            Assert.Contains(scrubbed.Records, row => row.ReasonCode == "RetainedImageTombstoneVerified" &&
                row.Kind == EvidenceReconciliationEventKind.WorkDeferred);
        }
        finally { Assert.True(await reconciler.StopAsync()); }
        await harness.Fixture.Store.DisposeAsync();
        await using var reopened = new SqliteCommandStore(harness.Fixture.Options);
        var initialized = await reopened.Initialization;
        Assert.True(initialized.Committed, initialized.ReasonCode);
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(reopened);
        var coldFinal = await new SqliteEvidenceRetentionQuery(harness.Fixture.Options).ReadAsync(new(Owner: obligation.Owner));
        Assert.True(coldFinal.Available, coldFinal.ReasonCode);
        Assert.Equal(1, coldFinal.DeletedFiles);
    }
}
