using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ProductionAdmissionArmIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V136_D01_MaterialCommitInvalidatesBothSidesOfAuthorizationFinalization(bool finalized)
    {
        await using var station = await ProductionAdmissionArmFixture.CreateAsync();
        using var qualification = new ProductionAdmissionTestFixture();
        var runtime = station.Runtime;
        await runtime.RefreshProductionAdmissionAsync();
        var command = station.Command();
        var facts = await CaptureFactsAsync(station, qualification);
        var report = qualification.Evaluate(facts);
        var admitted = await AuthorizeAsync(station, command, facts, report);
        Assert.Equal(CommandDisposition.Accepted, admitted.Disposition);
        await station.WaitVerifiedAsync();
        var terminalWriter = (IProductionAdmissionTerminalWriter)station.Store;
        if (finalized)
        {
            var complete = await terminalWriter.CompleteProductionAdmissionAsync(command.CorrelationId,
                admitted.AttemptId!.Value, report.RuntimeEpoch, report.AdmissionGeneration,
                "ProductionAdmissionFinalized", new StoreDeadline(TimeSpan.FromSeconds(3)));
            Assert.True(complete.Committed, complete.ReasonCode);
            await station.WaitVerifiedAsync();
        }
        var generation = runtime.AdmissionGeneration;
        Assert.True(await runtime.VerifyProductionAdmissionHeadsAsync(facts.DurableHeads,
            new StoreDeadline(TimeSpan.FromSeconds(3))));
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        void BlockBeforeCommit()
        {
            entered.TrySetResult(true);
            if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException();
        }
        station.Store.ProductionAdmissionMaterialChanging += BlockBeforeCommit;
        var write = ChangeAuthorityAsync(station).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // The old database snapshot is still visible while COMMIT is held.
            // The generation fence has already invalidated that earlier capture.
            Assert.True(runtime.AdmissionGeneration > generation);
            var during = await runtime.GetSnapshotAsync();
            Assert.False(during.Ready);
            Assert.Equal(ProductionArmState.Disarmed, during.ArmState);
            Assert.True(SqliteCommandStore.ProductionAdmissionHeadsEqual(facts.DurableHeads,
                await station.Store.ReadProductionAdmissionDurableHeadsAsync(CancellationToken.None)));
        }
        finally
        {
            release.Set();
            station.Store.ProductionAdmissionMaterialChanging -= BlockBeforeCommit;
        }
        var changed = await write;
        Assert.True(changed.Committed, changed.ReasonCode);
        await station.WaitVerifiedAsync();
        Assert.False(await runtime.VerifyProductionAdmissionHeadsAsync(facts.DurableHeads,
            new StoreDeadline(TimeSpan.FromSeconds(3))));
        if (!finalized)
        {
            var complete = await terminalWriter.CompleteProductionAdmissionAsync(command.CorrelationId,
                admitted.AttemptId!.Value, report.RuntimeEpoch, report.AdmissionGeneration,
                "ProductionAdmissionFinalized", new StoreDeadline(TimeSpan.FromSeconds(3)));
            Assert.True(complete.Committed, complete.ReasonCode);
            await station.WaitVerifiedAsync();
        }
        var history = await station.History.ReadAsync(command.CorrelationId);
        Assert.True(history.Available, history.ReasonCode);
        Assert.Equal(finalized ? ProductionAdmissionEventKind.Completed : ProductionAdmissionEventKind.Failed,
            history.Latest!.Kind);
        Assert.Equal(finalized ? "ProductionAdmissionFinalized" : "ProductionAdmissionChanged",
            history.Latest.ReasonCode);
        Assert.False((await runtime.GetSnapshotAsync()).Ready);
    }

    [Fact]
    public async Task V136_D02_RolledBackCommitDoesNotAdvanceMaterialHeadBaseline()
    {
        await using var station = await ProductionAdmissionArmFixture.CreateAsync();
        var runtime = station.Runtime;
        await runtime.RefreshProductionAdmissionAsync();
        var original = await station.Store.ReadProductionAdmissionDurableHeadsAsync(CancellationToken.None);
        var notifications = 0;
        void ExceedFirstCommitDeadline()
        {
            if (Interlocked.Increment(ref notifications) == 1)
                Thread.Sleep(station.Options.CommitTimeout + TimeSpan.FromMilliseconds(250));
        }
        station.Store.ProductionAdmissionMaterialChanging += ExceedFirstCommitDeadline;
        try
        {
            var failed = await ChangeAuthorityAsync(station);
            Assert.False(failed.Committed);
            // Wait for the writer's timed-out observer and rollback to retire.
            await Task.Delay(400);
            var recoveryDeadline = DateTime.UtcNow.AddSeconds(10);
            while (station.Store.Integrity?.State != AuditIntegrityState.Verified &&
                DateTime.UtcNow < recoveryDeadline) await Task.Delay(25);
            Assert.Equal(AuditIntegrityState.Verified, station.Store.Integrity?.State);
            Assert.True(SqliteCommandStore.ProductionAdmissionHeadsEqual(original,
                await station.Store.ReadProductionAdmissionDurableHeadsAsync(CancellationToken.None)));
            await runtime.RefreshProductionAdmissionAsync();
            var generation = runtime.AdmissionGeneration;
            var retried = await ChangeAuthorityAsync(station);
            Assert.True(retried.Committed, retried.ReasonCode);
            Assert.Equal(2, notifications);
            Assert.True(runtime.AdmissionGeneration > generation);
            Assert.False((await runtime.GetSnapshotAsync()).Ready);
        }
        finally { station.Store.ProductionAdmissionMaterialChanging -= ExceedFirstCommitDeadline; }
    }

    private static ValueTask<IdentityWriteResult> ChangeAuthorityAsync(ProductionAdmissionArmFixture station) =>
        station.Store.UpdateIdentityAsync(state =>
        {
            state.Administrator!.CredentialRevision++;
            return new IdentityUpdate("V136CredentialChanged", new[]
            {
                new IdentityAuditEvent(Guid.NewGuid(), IdentityEventKind.PasswordVerifierUpgraded,
                    DateTimeOffset.UtcNow, state.StationId, state.Administrator.PrincipalId,
                    state.Administrator.CredentialId, null, null, "V136CredentialChanged")
            });
        }, CancellationToken.None);
}

public sealed partial class RecipeReleaseStorageTests
{
    [Fact]
    public async Task V136_D03_PublicReleaseServiceInvalidatesAdmissionBeforeItsDurableCommit()
    {
        await using var harness = await ReleaseHarness.CreateAsync(productionAdmission: true);
        await harness.Runtime.RefreshProductionAdmissionAsync();
        var heads = await harness.Storage.Store.ReadProductionAdmissionDurableHeadsAsync(CancellationToken.None);
        var generation = harness.Runtime.AdmissionGeneration;
        long generationAtCommit = 0;
        var notifications = 0;
        void Observe()
        {
            generationAtCommit = harness.Runtime.AdmissionGeneration;
            Interlocked.Increment(ref notifications);
        }
        harness.Storage.Store.ProductionAdmissionMaterialChanging += Observe;
        try
        {
            _ = await harness.ReleaseAsync(harness.Source);
            Assert.Equal(1, notifications); // Step-Up's ordinary identity event is not material.
            Assert.True(generationAtCommit > generation);
            Assert.False(await harness.Runtime.VerifyProductionAdmissionHeadsAsync(heads,
                new StoreDeadline(TimeSpan.FromSeconds(3))));
            Assert.False((await harness.Runtime.GetSnapshotAsync()).Ready);
        }
        finally { harness.Storage.Store.ProductionAdmissionMaterialChanging -= Observe; }
    }
}
