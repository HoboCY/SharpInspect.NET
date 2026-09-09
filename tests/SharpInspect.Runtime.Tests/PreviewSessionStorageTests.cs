using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class RecipeActivationServiceTests
{
    [Fact]
    public async Task V133_S01_ClosedSessionIsNotPendingAndSecondStartUsesItsOwnAuditFact()
    {
        await using var harness = await ActivationHarness.CreateAsync(enablePreview: true);
        var invocation = harness.ActivationInvocation();
        await WaitForPreviewIdleAsync(harness, invocation);

        var draft = PreviewDraftReference.FromRevision(harness.Source);
        var firstSessionId = Guid.NewGuid();
        var first = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, firstSessionId,
            draft, expectedActive: null, "V133 storage first preview");
        AssertAccepted(await harness.Runtime.SubmitAsync(first));
        await WaitForPreviewAsync(harness, invocation, firstSessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming,
            "PreviewFirstSessionUnavailable");

        var firstExit = new ExitPreviewSessionCommand(Guid.NewGuid(), invocation,
            firstSessionId, cancel: false, "V133 storage close first preview");
        AssertAccepted(await harness.Runtime.SubmitAsync(firstExit));
        await WaitForPreviewAsync(harness, invocation, firstSessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Closed,
            "PreviewFirstSessionCloseUnavailable");
        await harness.WaitForVerifiedAsync();

        var idle = await harness.Store.ReadPreviewRecoveryStateAsync();
        Assert.True(idle.Available, idle.ReasonCode);
        Assert.Equal("PreviewSessionIdle", idle.ReasonCode);
        Assert.Null(idle.Header);
        Assert.Null(idle.StartFact);
        Assert.False(idle.RecoveryRequired);

        var secondSessionId = Guid.NewGuid();
        var second = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, secondSessionId,
            draft, expectedActive: null, "V133 storage second preview");
        AssertAccepted(await harness.Runtime.SubmitAsync(second));
        await WaitForPreviewAsync(harness, invocation, secondSessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming,
            "PreviewSecondSessionUnavailable");
        await harness.WaitForVerifiedAsync();

        var pending = await harness.Store.ReadPreviewRecoveryStateAsync();
        Assert.True(pending.Available, pending.ReasonCode);
        Assert.Equal(secondSessionId, pending.Header?.SessionId);
        Assert.Equal(second.CorrelationId, pending.StartFact?.CorrelationId);
        Assert.NotEqual(first.CorrelationId, pending.StartFact?.CorrelationId);

        var secondExit = new ExitPreviewSessionCommand(Guid.NewGuid(), invocation,
            secondSessionId, cancel: false, "V133 storage close second preview");
        AssertAccepted(await harness.Runtime.SubmitAsync(secondExit));
        await WaitForPreviewAsync(harness, invocation, secondSessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Closed,
            "PreviewSecondSessionCloseUnavailable");
        await harness.WaitForVerifiedAsync();

        var final = await harness.Store.ReadPreviewRecoveryStateAsync();
        Assert.True(final.Available, final.ReasonCode);
        Assert.Null(final.Header);
        Assert.Null(final.StartFact);
        Assert.False(final.RecoveryRequired);
    }
}
