using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData(EvidenceCaptureMode.All, 3)]
    [InlineData(EvidenceCaptureMode.FailOrUnknown, 2)]
    [InlineData(EvidenceCaptureMode.None, 0)]
    [Trait("VerificationId", "V150_R01")]
    public async Task V150_R01_RealProductionPoliciesStageActualAlgorithmInputsBeforePublishing(
        EvidenceCaptureMode mode, int expectedImages)
    {
        using var root = new ProductionImageTestRoot();
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        var capture = new EvidenceCapturePolicySnapshot("V150.Capture", "1", mode);
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: capture);
        harness.Factory.RecordInputPixelHashes = true;
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, value => value.Ready, "Image trigger waits for completed Arm admission");
        var stages = 0;
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            await WaitProductionAsync(harness, value => value.Ready, "Ready with bounded unfinished PNG work");
            peer.RaiseTrigger(61, (uint)cycle);
            await WaitForProductionResultAsync(harness, peer, cycle);
            var read = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
            Assert.True(read.Available, read.ReasonCode);
            var core = Assert.IsType<ProductionInspectionCore>(read.Latest!.Core);
            Assert.Equal(capture.ContentHash, core.Admission.EvidenceCapturePolicy!.ContentHash);
            Assert.Equal(ExecutionStatus.Success, core.ExecutionStatus);
            Assert.Equal(cycle == 1 ? InspectionDecision.Pass : cycle == 2 ? InspectionDecision.Fail : InspectionDecision.Unknown,
                core.Decision);
            var evidence = Assert.IsType<ProductionImageEvidenceSnapshot>(core.ImageEvidence);
            var required = mode == EvidenceCaptureMode.All || mode == EvidenceCaptureMode.FailOrUnknown && cycle > 1;
            if (required)
            {
                stages++;
                Assert.Equal(ProductionImageEvidenceState.Pending, evidence.State);
                var manifest = Assert.IsType<PendingImageManifest>(evidence.Manifest);
                Assert.Equal(harness.Factory.InputPixelHashes[core.Admission.CorrelationId], manifest.CanonicalPixelHash);
                var bytes = await File.ReadAllBytesAsync(Path.Combine(root.Path, manifest.StageFileName));
                Assert.Equal(manifest.CanonicalByteLength, bytes.LongLength);
                Assert.Equal(manifest.CanonicalPixelHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
                Assert.Equal("FinalizePng", evidence.Work!.Kind);
                Assert.Equal("Pending", evidence.Work.State);
            }
            else
            {
                Assert.Equal(ProductionImageEvidenceState.NotRequired, evidence.State);
                Assert.Null(evidence.Work);
                Assert.Null(evidence.Manifest);
            }
            // These are cold database reads at the physical ResultValid edge; no
            // final PNG exists, yet the publication barrier has been satisfied.
            Assert.Equal(stages, await harness.Fixture.ScalarAsync("SELECT COUNT(*) FROM pending_image_manifests;"));
            Assert.Equal(stages, await harness.Fixture.ScalarAsync("SELECT COUNT(*) FROM pending_image_work;"));
            Assert.Empty(Directory.GetFiles(root.Path, "*.png"));
            peer.SetTrigger(false);
            await WaitConditionAsync(() => peer.AckLowCount >= cycle, "Image cycle ACK reset");
            await WaitProductionAsync(harness, value => value.CurrentExecution is null && value.Ready,
                "PNG finalization must not block the next cycle below backlog limits");
        }
        Assert.Equal(expectedImages, stages);
        var page = await new SqlitePendingImageWorkQuery(harness.Fixture.Options).QueryAsync();
        Assert.True(page.Available, page.ReasonCode);
        Assert.Equal(expectedImages, page.Items.Count);
        Assert.Equal(expectedImages, (await harness.Runtime.GetSnapshotAsync()).Evidence.PendingRequiredImages);
    }

    [Theory]
    [InlineData(EvidenceCaptureMode.All)]
    [InlineData(EvidenceCaptureMode.FailOrUnknown)]
    [InlineData(EvidenceCaptureMode.None)]
    [Trait("VerificationId", "V150_R02")]
    public async Task V150_R02_NoFrameRecordsActualAcquisitionReasonWithoutImageObligations(EvidenceCaptureMode mode)
    {
        using var root = new ProductionImageTestRoot();
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(timeout: true, activationReadyDraft: true,
            productionPeer: peer, imageEvidence: root.Options, capturePolicy: new("V150.Capture", "1", mode));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, value => value.Ready, "Image trigger waits for completed Arm admission");
        await WaitProductionAsync(harness, value => value.Ready, "Ready for no-frame capture");
        peer.RaiseTrigger(61, 1);
        await WaitForProductionResultAsync(harness, peer, 1);
        var read = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
        Assert.True(read.Available, read.ReasonCode);
        var core = read.Latest!.Core!;
        Assert.Null(core.FrameMetadata);
        Assert.Equal(ProductionImageEvidenceState.NotAvailable, core.ImageEvidence!.State);
        Assert.Equal(core.AcquisitionFailureReasonCode, core.ImageEvidence.ReasonCode);
        Assert.Equal("CameraAcquisitionTimeout", core.ImageEvidence.ReasonCode);
        Assert.Null(core.ImageEvidence.Work);
        Assert.Empty(Directory.GetFiles(root.Path));
        Assert.Equal(0, await harness.Fixture.ScalarAsync("SELECT COUNT(*) FROM pending_image_work;"));
        peer.SetTrigger(false);
        await WaitConditionAsync(() => peer.AckLowCount == 1, "No-frame ACK reset");
    }

    [Fact]
    [Trait("VerificationId", "V150_R03")]
    public async Task V150_R03_AlgorithmTimeoutStillStagesItsExactInput()
    {
        using var root = new ProductionImageTestRoot();
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V150.Capture", "1", EvidenceCaptureMode.FailOrUnknown));
        harness.Factory.RecordInputPixelHashes = true;
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, value => value.Ready, "Image trigger waits for completed Arm admission");
        await WaitProductionAsync(harness, value => value.Ready, "Ready for algorithm timeout");
        harness.Factory.HoldExecution(cooperativeCancellation: true);
        try
        {
            peer.RaiseTrigger(61, 1);
            await harness.Factory.ExecutionEntered.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForProductionResultAsync(harness, peer, 1);
            var read = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
            Assert.True(read.Available, read.ReasonCode);
            var core = read.Latest!.Core!;
            Assert.Equal(ExecutionStatus.Timeout, core.ExecutionStatus);
            Assert.Equal(InspectionDecision.Unknown, core.Decision);
            Assert.Equal(ProductionImageEvidenceState.Pending, core.ImageEvidence!.State);
            Assert.Equal(harness.Factory.InputPixelHashes[core.Admission.CorrelationId], core.ImageEvidence.Manifest!.CanonicalPixelHash);
            peer.SetTrigger(false);
            await WaitConditionAsync(() => peer.AckLowCount == 1, "Timeout image ACK reset");
        }
        finally { harness.Factory.ReleaseExecution(); }
    }

    [Theory]
    [InlineData((int)StageBoundary.BeforeFlush)]
    [InlineData((int)StageBoundary.AfterRename)]
    [Trait("VerificationId", "V150_R04")]
    public async Task V150_R04_FileBarrierFailureTerminatesCycleWithoutCoreOrPublication(int boundaryValue)
    {
        var boundary = (StageBoundary)boundaryValue;
        using var root = new ProductionImageTestRoot();
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V150.Capture", "1", EvidenceCaptureMode.All));
        InstallProductionImageStager(harness, new(root.Options.Stage, value =>
            { if (value == boundary) throw new IOException("V150InjectedFileFailure"); }));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, value => value.Ready, "Image trigger waits for completed Arm admission");
        await WaitProductionAsync(harness, value => value.Ready, "Ready for file fault");
        peer.RaiseTrigger(61, 1);
        await AssertNoImageCorePublishedAsync(harness, peer);
    }

    [Theory]
    [InlineData((int)ProductionImageCommitBoundary.AfterCore)]
    [InlineData((int)ProductionImageCommitBoundary.AfterManifest)]
    [InlineData((int)ProductionImageCommitBoundary.AfterWork)]
    [InlineData((int)ProductionImageCommitBoundary.BeforeCommit)]
    [Trait("VerificationId", "V150_R05")]
    public async Task V150_R05_DatabaseBoundaryFaultRollsBackCoreManifestAndWorkTogether(int boundaryValue)
    {
        var boundary = (ProductionImageCommitBoundary)boundaryValue;
        using var root = new ProductionImageTestRoot();
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V150.Capture", "1", EvidenceCaptureMode.All));
        harness.Fixture.Store.ImageEvidenceCommitObserver = value =>
            { if (value == boundary) throw new IOException("V150InjectedDatabaseBoundaryFailure"); };
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, value => value.Ready, "Image trigger waits for completed Arm admission");
        await WaitProductionAsync(harness, value => value.Ready, "Ready for atomic transaction fault");
        peer.RaiseTrigger(61, 1);
        await AssertNoImageCorePublishedAsync(harness, peer);
        Assert.Single(Directory.GetFiles(root.Path, "*.stage"));
    }

    [Fact]
    [Trait("VerificationId", "V150_R06")]
    public async Task V150_R06_StageTimeoutRetainsPhysicalOwnershipAndCannotCommitLate()
    {
        using var root = new ProductionImageTestRoot();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        var original = TraceStoragePolicyRuntimeTests.Policy();
        var policy = new TraceStoragePolicyDefinition(original.PolicyId, original.Version, original.ApprovalReference,
            original.ApprovalVersion, original.Rationale, original.RetentionRules, original.MinimumReserveBytes,
            original.MinimumReservePercent, original.RequiredRoutes, original.ImageBacklog, TimeSpan.FromMilliseconds(250),
            original.TraceCommitTimeout, original.Scrubber, original.Checkpoint, original.MaximumWalBytes);
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V150.Capture", "1", EvidenceCaptureMode.All), tracePolicy: policy);
        var stager = new ProductionImageStager(root.Options.Stage, value =>
        {
            if (value != StageBoundary.BeforeFlush) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("V150TestReleaseMissing");
        });
        InstallProductionImageStager(harness, stager);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, value => value.Ready, "Image trigger waits for completed Arm admission");
        await WaitProductionAsync(harness, value => value.Ready, "Ready for blocked durable flush");
        try
        {
            peer.RaiseTrigger(61, 1);
            await WaitConditionAsync(() => entered.IsSet, "Stager entered blocking file operation");
            await AssertNoImageCorePublishedAsync(harness, peer);
            Assert.Equal(1, stager.ActiveOperationCount);
            Assert.False(stager.PhysicalCompletion.IsCompleted);
            Assert.True(harness.Service<FrameBufferPool>().GetSnapshot().ActiveReaders > 0);
        }
        finally { release.Set(); }
        await stager.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, stager.ActiveOperationCount);
        // A fresh verified reader after actual task exit still sees no Core or work.
        await AssertNoImageCorePublishedAsync(harness, peer);
    }

    [Fact]
    [Trait("VerificationId", "V150_R07")]
    public async Task V150_R07_TraceCommitDeadlineAfterSuccessfulStageLeavesOnlyUnreferencedFile()
    {
        using var root = new ProductionImageTestRoot();
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V150.Capture", "1", EvidenceCaptureMode.All));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, value => value.Ready, "Image trigger waits for completed Arm admission");
        await WaitProductionAsync(harness, value => value.Ready, "Ready for blocked Core transaction");
        harness.PauseClock();
        peer.RaiseTrigger(61, 1);
        await WaitForProductionHistoryAsync(harness,
            page => page.Events.Any(value => value.Kind == ProductionInspectionEventKind.Admitted), "Image admission before lock");
        using (var writer = SqliteNative.Open(harness.Fixture.Options.DatabasePath, readOnly: false))
        {
            SqliteNative.Execute(writer.Handle!, "BEGIN IMMEDIATE;", new StoreDeadline(TimeSpan.FromSeconds(2)));
            try
            {
                harness.ResumeClock();
                await WaitProductionAsync(harness, value => value.Recovery == RecoveryState.Required && !value.Busy,
                    "Trace deadline must terminate the staged cycle");
                Assert.Equal(0, peer.ResultValidHighCount);
                Assert.Single(Directory.GetFiles(root.Path, "*.stage"));
            }
            finally { SqliteNative.Execute(writer.Handle!, "ROLLBACK;", new StoreDeadline(TimeSpan.FromSeconds(2))); }
        }
        await AssertNoImageCorePublishedAsync(harness, peer);
        Assert.Single(Directory.GetFiles(root.Path, "*.stage"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("VerificationId", "V150_R08")]
    public async Task V150_R08_TamperedPendingProjectionIsRejectedByColdWorkCoreAndAuditReads(bool tamperWork)
    {
        using var root = new ProductionImageTestRoot();
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V150.Capture", "1", EvidenceCaptureMode.All));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, value => value.Ready, "Image trigger waits for completed Arm admission");
        peer.RaiseTrigger(61, 1);
        await WaitConditionAsync(() => peer.ResultValidHighCount == 1, "Original image Core published");
        peer.SetTrigger(false);
        await WaitProductionAsync(harness, value => value.CurrentExecution is null && value.Ready, "Cycle complete before offline corruption");
        await ((StationRuntime)harness.Runtime).DisposeAsync();
        await harness.Fixture.Store.DisposeAsync();
        using (var connection = SqliteNative.Open(harness.Fixture.Options.DatabasePath, readOnly: false))
        {
            var table = tamperWork ? "pending_image_work" : "pending_image_manifests";
            SqliteNative.Execute(connection.Handle!, $"DROP TRIGGER {table}_immutable_update; UPDATE {table} SET Payload='AAAA';",
                new StoreDeadline(TimeSpan.FromSeconds(5)));
        }
        var reason = tamperWork ? "ProductionImageWorkProjectionMismatch" : "ProductionImageManifestProjectionMismatch";
        var pending = await new SqlitePendingImageWorkQuery(harness.Fixture.Options).QueryAsync();
        Assert.False(pending.Available);
        Assert.Contains(reason, pending.ReasonCode);
        Assert.Empty(pending.Items);
        var history = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
        Assert.False(history.Available);
        Assert.Null(history.Latest);
        var audit = await new SharpInspect.Runtime.Integrity.SqliteAuditIntegrityQuery(harness.Fixture.Options)
            .VerifyAsync(new AuditVerificationRequest(0, harness.Fixture.Options.AuditIntegrityPolicy!.MaximumVerificationEntries -
                harness.Fixture.Options.AuditIntegrityPolicy.CheckpointEveryEntries));
        Assert.Equal(SharpInspect.Abstractions.AuditIntegrityState.Faulted, audit.State);
    }

    [Fact]
    [Trait("VerificationId", "V150_R09")]
    public async Task V150_R09_FillingBacklogPublishesTheAdmittedCycleAndBlocksTheNext()
    {
        using var root = new ProductionImageTestRoot();
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        var original = TraceStoragePolicyRuntimeTests.Policy();
        var policy = new TraceStoragePolicyDefinition(original.PolicyId, original.Version, original.ApprovalReference,
            original.ApprovalVersion, original.Rationale, original.RetentionRules, original.MinimumReserveBytes,
            original.MinimumReservePercent, original.RequiredRoutes,
            new TraceBacklogLimits(1, original.ImageBacklog.MaximumBytes, original.ImageBacklog.MaximumOldestAge),
            original.EvidenceStageTimeout, original.TraceCommitTimeout, original.Scrubber, original.Checkpoint, original.MaximumWalBytes);
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V150.Capture", "1", EvidenceCaptureMode.All), tracePolicy: policy);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, value => value.Ready, "Image trigger waits for completed Arm admission");
        peer.RaiseTrigger(61, 1);
        await WaitConditionAsync(() => peer.ResultValidHighCount == 1, "Already admitted cycle publishes at backlog limit");
        peer.SetTrigger(false);
        await WaitConditionAsync(() => peer.AckLowCount == 1, "Full-backlog cycle ACK reset");
        await WaitProductionAsync(harness, value => value.CurrentExecution is null && !value.Busy && !value.Ready,
            "Full pending backlog blocks subsequent admission");
        var snapshot = await harness.Runtime.GetSnapshotAsync();
        Assert.Equal(1, snapshot.Evidence.PendingRequiredImages);
        Assert.Equal(1, await harness.Fixture.ScalarAsync("SELECT COUNT(*) FROM pending_image_work;"));
    }

    [Theory]
    [InlineData(".stage")]
    [InlineData(".partial")]
    [Trait("VerificationId", "V150_R10")]
    public async Task V150_R10_ColdStartupWithUnreferencedImageBytesBlocksBeforePlcReady(string extension)
    {
        using var root = new ProductionImageTestRoot();
        var path = Path.Combine(root.Path, Guid.NewGuid().ToString("N") + extension);
        var retainedBytes = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(path, retainedBytes);
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await ManualHarness.CreateAsync(configureCamera: false,
            activationReadyDraft: true, productionPeer: peer, imageEvidence: root.Options,
            capturePolicy: new("V150.Capture", "1", EvidenceCaptureMode.All));

        await WaitProductionAsync(harness, state => state.AdmissionBlockers.Contains(
            "ProductionImageStartupReconciliationRequired"), "Orphan image prevents cold startup");
        var state = await harness.Runtime.GetSnapshotAsync();
        Assert.False(state.Ready);
        Assert.Equal(ProductionArmState.Disarmed, state.ArmState);
        Assert.Equal(RecoveryState.Required, state.Recovery);
        Assert.False(peer.PhysicalProductionReady);
        Assert.Equal(0, peer.ProductionReadyWriteCount);
        Assert.Equal(0, peer.ResultValidHighCount);
        Assert.Equal(0, await harness.Fixture.ScalarAsync("SELECT COUNT(*) FROM production_inspection_cores;"));
        Assert.Equal(0, await harness.Fixture.ScalarAsync("SELECT COUNT(*) FROM pending_image_work;"));
        Assert.Equal(retainedBytes, await File.ReadAllBytesAsync(path));
    }

    private static void InstallProductionImageStager(ManualHarness harness, ProductionImageStager stager)
    {
        var field = typeof(StationRuntime).GetField("_productionImageStager", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(harness.Runtime, stager);
    }

    private static async Task AssertNoImageCorePublishedAsync(ManualHarness harness, ModbusQualificationTestServer peer)
    {
        await WaitProductionAsync(harness, value => value.Recovery == RecoveryState.Required && !value.Busy && !value.Ready,
            "Required image failure must close the publication gate");
        var page = await WaitForProductionHistoryAsync(harness, value => value.Events.Any(item =>
            item.Kind == ProductionInspectionEventKind.FaultTerminated), "Durable image cycle termination");
        Assert.Single(page.Events, value => value.Kind == ProductionInspectionEventKind.Admitted);
        Assert.DoesNotContain(page.Events, value => value.Core is not null || value.Kind == ProductionInspectionEventKind.ResultValidRaised);
        Assert.Equal(0, peer.ResultValidHighCount);
        Assert.False(peer.RuntimeResultValid);
        Assert.Equal(0, await harness.Fixture.ScalarAsync("SELECT COUNT(*) FROM production_inspection_cores;"));
        Assert.Equal(0, await harness.Fixture.ScalarAsync("SELECT COUNT(*) FROM pending_image_manifests;"));
        Assert.Equal(0, await harness.Fixture.ScalarAsync("SELECT COUNT(*) FROM pending_image_work;"));
        var cold = await new SqlitePendingImageWorkQuery(harness.Fixture.Options).QueryAsync();
        Assert.True(cold.Available, cold.ReasonCode);
        Assert.Empty(cold.Items);
    }

    private sealed class ProductionImageTestRoot : IDisposable
    {
        internal ProductionImageTestRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V150-Images-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Options = new(new ProductionImageStageOptions(Path, 512 * 1024, 32 * 1024 * 1024, 200));
        }
        internal string Path { get; }
        internal ProductionImageEvidenceStoreOptions Options { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
