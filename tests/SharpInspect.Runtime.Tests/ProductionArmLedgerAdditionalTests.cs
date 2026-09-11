using System.Collections.Concurrent;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ProductionArmLedgerTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task V147_S13_Schema32PreservesOptionalIdentityRecoveryAndSelectionLedgers(
        bool partIdentity, bool recovery, bool selection)
    {
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(
            productionInspections: new ProductionInspectionStoreOptions(),
            productionAdmission: new ProductionAdmissionStoreOptions(),
            productionArming: new ProductionArmStoreOptions(),
            partIdentities: partIdentity ? new PartIdentityStoreOptions() : null,
            productionRecovery: recovery ? new ProductionRecoveryStoreOptions() : null,
            recipeReleases: selection ? new RecipeReleaseStoreOptions(new RecipeGovernancePolicy(
                "V147.Optional.Release", "1", RecipeGovernanceMode.SingleApproverRelease)) : null,
            recipeActivations: selection ? new RecipeActivationStoreOptions() : null,
            cameraSetup: selection ? new CameraSetupStoreOptions() : null,
            plcResultContracts: selection ? new PlcResultContractStoreOptions() : null,
            plcCommunication: selection ? new PlcCommunicationStoreOptions() : null,
            recipeSelections: selection ? new RecipeSelectionStoreOptions() : null);
        for (var open = 0; open < 2; open++)
        {
            Assert.Equal(32, fixture.Scalar("PRAGMA user_version;"));
            var arms = await new SqliteProductionArmHistoryQuery(fixture.Options).ReadCurrentAsync();
            Assert.True(arms.Available, arms.ReasonCode);
            Assert.Null(arms.Current);
            var capacity = await fixture.Store.ReadProductionInspectionCapacityAsync();
            Assert.True(capacity.Available, capacity.ReasonCode);
            var recoveryHistory = await new SqliteProductionRecoveryHistoryQuery(fixture.Options).ReadCurrentAsync();
            if (recovery) Assert.True(recoveryHistory.Available, recoveryHistory.ReasonCode);
            else Assert.Equal("ProductionRecoveryConfigurationRequired", recoveryHistory.ReasonCode);
            if (partIdentity)
            {
                var identities = await new SqlitePartIdentityHistoryQuery(fixture.Options)
                    .QueryAsync(new PartIdentityHistoryFilter());
                Assert.True(identities.Available, identities.ReasonCode);
            }
            if (selection)
            {
                var selected = await new SqliteRecipeSelectionQuery(fixture.Options).ReadCurrentAsync();
                Assert.True(selected.Available, selected.ReasonCode);
            }
            await fixture.WaitForVerifiedAsync();
            if (open == 0) await fixture.RestartStoreAsync();
        }
    }

    [Fact]
    public async Task V147_S10_OneClosedAttemptCannotReleaseAnotherAttemptsReservedTail()
    {
        await using var fixture = await ArmFixture.CreateAsync(new ProductionArmStoreOptions { MaxEvents = 8 });
        var closed = Guid.NewGuid();
        var closedEpoch = Guid.NewGuid();
        Assert.True((await fixture.AttemptedAsync(closed, closedEpoch)).Committed);
        Assert.True((await fixture.FailedAsync(closed, closedEpoch, ProductionArmReason.Interrupted)).Committed);
        Assert.True((await fixture.UndeliveredAsync(closed, closedEpoch)).Committed);
        var pending = Guid.NewGuid();
        var pendingEpoch = Guid.NewGuid();
        Assert.True((await fixture.AttemptedAsync(pending, pendingEpoch)).Committed);
        // Three actual closed rows plus four promised rows for this open attempt
        // leave only one slot. A third attempt needs its own four-slot budget.
        var excess = await fixture.AttemptedAsync(Guid.NewGuid(), Guid.NewGuid());
        Assert.False(excess.Committed);
        Assert.Equal("ProductionArmEntryCapacityExceeded", excess.ReasonCode);
        Assert.True((await fixture.FailedAsync(pending, pendingEpoch, ProductionArmReason.Interrupted)).Committed);
        Assert.True((await fixture.UndeliveredAsync(pending, pendingEpoch)).Committed);
        await fixture.ReopenAsync();
        var page = await fixture.QueryAsync(new ProductionArmHistoryFilter());
        Assert.True(page.Available, page.ReasonCode);
        Assert.Equal(6, page.Events.Count);
        Assert.False((await new SqliteProductionArmHistoryQuery(fixture.Options).ReadCurrentAsync()).RecoveryRequired);
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("epoch")]
    [InlineData("authorization")]
    [InlineData("maintenance")]
    [InlineData("generation")]
    [InlineData("before-authorization")]
    [InlineData("future")]
    public async Task V147_S11_PhysicalReadyReceiptCannotBeRepointedOrBackdated(string mismatch)
    {
        await using var fixture = await ArmFixture.CreateAsync();
        var id = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var head = ArmFixture.Hash('F');
        Assert.True((await fixture.AttemptedAsync(id, epoch, head)).Committed);
        var authorized = await fixture.WriteAsync(fixture.AuthorizationRequest(id, epoch,
            ArmFixture.CanArmReport(epoch, 0), fixture.Heads, fixture.Heads, head));
        Assert.True(authorized.Committed, authorized.ReasonCode);
        var receipt = new ProductionArmReadyReceipt(
            mismatch == "attempt" ? Guid.NewGuid() : id,
            mismatch == "epoch" ? Guid.NewGuid() : epoch,
            mismatch == "authorization" ? ArmFixture.Hash('C') : authorized.Event!.ContentHash,
            null, mismatch == "maintenance" ? ArmFixture.Hash('D') : head,
            mismatch == "generation" ? 1 : 0, 1, 1,
            mismatch == "before-authorization" ? authorized.Event!.RecordedAtUtc.AddSeconds(-1) :
            mismatch == "future" ? DateTimeOffset.UtcNow.AddDays(1) : DateTimeOffset.UtcNow);
        var request = new ProductionArmWriteRequest(id, epoch, ProductionArmCause.Startup,
            ProductionArmEventKind.ReadyConfirmed, fixture.StationId, fixture.StartupPolicy,
            fixture.PostActivationPolicy, fixture.DeploymentHash, null, null, head, 0,
            authorized.Event!.Report, fixture.Heads, fixture.Heads, ProductionArmReason.None,
            "V147.ReadyReceiptMismatch", ReadyReceipt: receipt, InputStability: authorized.Event.InputStability);
        var rejected = await fixture.WriteAsync(request);
        Assert.False(rejected.Committed);
        Assert.Equal("ProductionArmReadyReceiptContextMismatch", rejected.ReasonCode);
        var valid = new ProductionArmReadyReceipt(id, epoch, authorized.Event.ContentHash,
            null, head, 0, 1, 1, DateTimeOffset.UtcNow);
        var accepted = await fixture.WriteAsync(request with { ReadyReceipt = valid });
        Assert.True(accepted.Committed, accepted.ReasonCode);
        Assert.True((await fixture.DeliveredAsync(id, epoch)).Committed);
        await fixture.ReopenAsync();
        var history = await fixture.QueryAsync(new ProductionArmHistoryFilter(AttemptId: id));
        Assert.True(history.Available, history.ReasonCode);
        Assert.Equal(valid.ContentHash, Assert.Single(history.Events,
            value => value.Kind == ProductionArmEventKind.ReadyConfirmed).ReadyReceipt!.ContentHash);
    }

    [Fact]
    public async Task V147_S12_ArmQueriesRequireTheCurrentExternalAnchorReceipt()
    {
        var anchor = new ArmQueryAnchor();
        await using var fixture = await ArmFixture.CreateAsync(anchor: anchor);
        var id = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        Assert.True((await fixture.AttemptedAsync(id, epoch)).Committed);
        Assert.True((await fixture.FailedAsync(id, epoch, ProductionArmReason.Interrupted)).Committed);
        Assert.True((await fixture.UndeliveredAsync(id, epoch)).Committed);
        await fixture.WaitVerifiedAsync();
        await fixture.Store.DisposeAsync();
        var before = await File.ReadAllBytesAsync(fixture.Options.DatabasePath);
        var query = new SqliteProductionArmHistoryQuery(fixture.Options);
        foreach (var mode in new[] { "missing", "mismatch" })
        {
            anchor.Mode = mode;
            var page = await query.QueryAsync(new ProductionArmHistoryFilter());
            Assert.False(page.Available);
            Assert.Equal("AuditExternalAnchorMismatch", page.ReasonCode);
            Assert.Empty(page.Events);
            var current = await query.ReadCurrentAsync();
            Assert.False(current.Available);
            Assert.Equal("AuditExternalAnchorMismatch", current.ReasonCode);
            Assert.Null(current.Current);
            Assert.Null(current.Pending);
        }
        anchor.Mode = "valid";
        var valid = await query.QueryAsync(new ProductionArmHistoryFilter());
        Assert.True(valid.Available, valid.ReasonCode);
        Assert.Equal(3, valid.Events.Count);
        Assert.True((await query.ReadCurrentAsync()).Available);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Options.DatabasePath));
    }

    private sealed class ArmQueryAnchor : IExternalAuditAnchor
    {
        internal const string RouteId = "v147-arm-query-anchor";
        private readonly ConcurrentDictionary<Guid, AuditAnchorReceipt> _receipts = new();
        internal string Mode { get; set; } = "valid";

        public ValueTask<AuditAnchorReceipt> DeliverAsync(AuditCheckpoint checkpoint,
            string idempotencyKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(checkpoint.StationId + "/" + RouteId + "/" + checkpoint.CheckpointId.ToString("D"),
                idempotencyKey);
            return ValueTask.FromResult(_receipts.GetOrAdd(checkpoint.CheckpointId, _ =>
                new AuditAnchorReceipt(checkpoint.CheckpointId, checkpoint.StationId, checkpoint.Sequence,
                    checkpoint.HeadHash, RouteId, "receipt-" + checkpoint.CheckpointId.ToString("N"),
                    checkpoint.PolicyHash, checkpoint.SigningKeyId,
                    AuditChainDatabase.CheckpointDigest(checkpoint), DateTimeOffset.UtcNow)));
        }

        public ValueTask<AuditAnchorReceipt?> ReadLatestAsync(string stationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var latest = _receipts.Values.Where(value => value.StationId == stationId)
                .OrderByDescending(value => value.Sequence).FirstOrDefault();
            return ValueTask.FromResult(Mode switch
            {
                "missing" => null,
                "mismatch" when latest is not null => latest with { HeadHash = new string('0', 64) },
                _ => latest
            });
        }
    }
}
