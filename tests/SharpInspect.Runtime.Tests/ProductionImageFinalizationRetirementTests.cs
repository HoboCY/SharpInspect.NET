using SharpInspect.Abstractions;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    [Trait("VerificationId", "V151_R07")]
    public async Task V151_R07_StopIsBoundedAndCannotCommitALateClaimBeforeReplay()
    {
        using var root = new ProductionImageTestRoot();
        using var final = new ProductionImageFinalizationTestRoot(root.Options);
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, capturePolicy: new("V151.Retirement", "1", EvidenceCaptureMode.All));
        using var issuer = new ProductionTestIssuer();
        var staged = await StageDurableProductionImageAsync(root, peer, harness, issuer);
        var target = await MigrateFinalizationStoreAsync(harness, final.Options);
        await using var store = new SqliteCommandStore(target);
        Assert.True((await store.Initialization).Committed);
        var query = new SqliteProductionImageEvidenceQuery(target);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new ProductionImageFinalizationWorker(store, target, Guid.NewGuid(), store.Initialization,
            _ => { }, _ => Task.CompletedTask, boundary =>
            {
                if (boundary != ImageFinalizationBoundary.BeforeClaimPublication) return;
                entered.TrySetResult(true);
                if (!release.Wait(TimeSpan.FromSeconds(25))) throw new TimeoutException("V151.TestClaimHoldExpired");
            });
        try
        {
            await worker.Startup.WaitAsync(TimeSpan.FromSeconds(10));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var files = ImageFilesOf(worker);
            Assert.False(await worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.False(worker.Completion.IsCompleted);
            Assert.Equal(1, files.ActiveOperationCount);
            var pending = await ReadSingleAsync(query, staged.Work.WorkId);
            Assert.Single(pending.Events);
            Assert.NotNull(pending.State.ActiveAttemptId);
            Assert.Null(pending.State.Success);
            Assert.True(File.Exists(staged.StagePath));
            Assert.Single(Directory.GetFiles(final.Path, "*.png"));
            release.Set();
            Assert.True(await worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.Equal(0, files.ActiveOperationCount);
            var retired = await ReadSingleAsync(query, staged.Work.WorkId);
            Assert.Equal(pending.Events.Count, retired.Events.Count);
            Assert.Null(retired.State.Success);
            Assert.True(File.Exists(staged.StagePath));

            var replay = new ProductionImageFinalizationWorker(store, target, Guid.NewGuid(), store.Initialization,
                _ => { }, _ => Task.CompletedTask);
            try
            {
                await replay.Startup.WaitAsync(TimeSpan.FromSeconds(10));
                var recovered = await WaitForFinalizationAsync(query, staged.Work.WorkId,
                    image => image.State.CleanupState == ProductionImageCleanupState.Released,
                    "The retired attempt must recover one verified authority", replay);
                Assert.Single(recovered.Events, e => e.Kind == ProductionImageFinalizationKind.Succeeded);
                Assert.Single(recovered.Events, e => e.Kind == ProductionImageFinalizationKind.AttemptStarted);
                Assert.Single(Directory.GetFiles(final.Path, "*.png"));
                Assert.False(File.Exists(staged.StagePath));
            }
            finally { Assert.True(await replay.StopAsync().WaitAsync(TimeSpan.FromSeconds(8))); }
        }
        finally
        {
            release.Set();
            await worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(8));
        }
    }
}
