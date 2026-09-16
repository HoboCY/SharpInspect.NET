using SharpInspect.Abstractions;
using SharpInspect.Runtime.StoragePolicies;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class TraceStorageCapacityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);

    [Theory, Trait("VerificationId", "V155_C01")]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void V155_C01_WalAndOwnedBudgetsBlockAtExactUpperBound(int delta, bool healthy)
    {
        var policy = TraceStoragePolicyRuntimeTests.Policy();
        Assert.Equal(healthy, Evaluate(policy, wal: policy.MaximumWalBytes + delta).Health == TraceStorageHealth.Healthy);
        Assert.Equal(healthy, Evaluate(policy, files: 100 + delta).Health == TraceStorageHealth.Healthy);
        Assert.Equal(healthy, Evaluate(policy, bytes: 1000 + delta).Health == TraceStorageHealth.Healthy);
        Assert.Equal(healthy, Evaluate(policy, images: new(1, policy.ImageBacklog.MaximumItems + delta,
            100, Now.AddMinutes(-1))).Health == TraceStorageHealth.Healthy);
        Assert.Equal(healthy, Evaluate(policy, images: new(1, 1, policy.ImageBacklog.MaximumBytes + delta,
            Now.AddMinutes(-1))).Health == TraceStorageHealth.Healthy);
        Assert.Equal(healthy, Evaluate(policy, images: new(1, 1, 100,
            Now - policy.ImageBacklog.MaximumOldestAge - TimeSpan.FromTicks(delta))).Health == TraceStorageHealth.Healthy);
    }

    [Fact, Trait("VerificationId", "V155_C02")]
    public void V155_C02_ReserveUsesLargerFixedOrPercentageAndEqualityIsHealthy()
    {
        foreach (var policy in new[]
                 { TraceStoragePolicyRuntimeTests.Policy(minimumReserveBytes: 30L << 30),
                   TraceStoragePolicyRuntimeTests.Policy(minimumReserveBytes: 1, minimumReservePercent: 10) })
        {
            var reserve = TraceStoragePolicyValidator.RequiredReserve(policy, 100L << 30);
            Assert.Equal(TraceStorageHealth.Healthy, Evaluate(policy, free: reserve).Health);
            Assert.Contains("TraceStorageReserveViolated.FinalImages", Evaluate(policy, free: reserve - 1).AdmissionBlockers);
        }
    }

    [Fact, Trait("VerificationId", "V155_C03")]
    public void V155_C03_CompleteRecoverableBacklogIsAllowedButClockRollbackAndPartialInventoryBlock()
    {
        var policy = TraceStoragePolicyRuntimeTests.Policy();
        var valid = Evaluate(policy, images: new(1, 3, 100, Now.AddMinutes(-1)));
        Assert.True(valid.Available);
        Assert.Equal(TraceStorageHealth.Healthy, valid.Health);
        Assert.Contains("TraceStorageImageClockRollback", Evaluate(policy,
            images: new(1, 1, 100, Now.AddTicks(1))).AdmissionBlockers);
        var incomplete = Evaluate(policy, complete: false);
        Assert.Contains("TraceStorageInventoryBoundReached.FinalImages", incomplete.AdmissionBlockers);
        Assert.False(Assert.Single(incomplete.Areas).InventoryComplete);
        Assert.Equal(TraceStorageHealth.Unavailable, Evaluate(policy, images: new(1, 0, 0, Now)).Health);
    }

    private static TraceStorageCapacitySnapshot Evaluate(TraceStoragePolicyDefinition policy,
        long wal = 0, long files = 1, long bytes = 1, long? free = null, bool complete = true,
        ImageBacklogSnapshot? images = null)
    {
        var publication = new TraceStoragePolicyPublication(1, policy, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            1, Guid.NewGuid(), Now.AddDays(-1), null);
        var current = new TraceStoragePolicyReadResult(true, "Available", publication, new(publication));
        const long total = 100L << 30;
        var physical = new TraceStoragePhysicalObservation(123, wal, new[]
        {
            new TraceStorageAreaObservation(TraceStorageArea.FinalImages, new string('A', 64), total,
                free ?? total / 2, TraceStoragePolicyValidator.RequiredReserve(policy, total), bytes, files,
                1000, 100, complete)
        });
        return TraceStorageCapacityEvaluator.Evaluate(Guid.NewGuid(), 1, Now, current,
            new("capacity-fixture", "1", Array.Empty<TraceStorageRouteIdentity>()), physical, images,
            null, new(TraceCheckpointStatus.Awaiting, "AwaitingStoppedMaintenance", null, null, null, null, null, null));
    }
}
