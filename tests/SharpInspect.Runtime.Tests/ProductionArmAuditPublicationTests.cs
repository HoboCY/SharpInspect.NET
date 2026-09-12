using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ProductionArmLedgerTests
{
    [Fact]
    [Trait("VerificationId", "V152_T12")]
    public async Task V152_T12_ArmCommitPublishesAuditRecheckBeforeReturningSuccess()
    {
        await using var fixture = await ArmFixture.CreateAsync();
        var result = await StoreAuditPublicationBarrier.AssertPublishedBeforeCompletionAsync(fixture.Store,
            () => fixture.AttemptedAsync(Guid.NewGuid(), Guid.NewGuid()).AsTask(),
            () => fixture.Scalar("SELECT COUNT(*) FROM production_arm_events;"), 1);
        Assert.True(result.Committed, result.ReasonCode);
        await fixture.WaitVerifiedAsync();
        Assert.Single((await fixture.QueryAsync(new())).Events);
    }
}
