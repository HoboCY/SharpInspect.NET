using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class RecipeActivationServiceTests
{
    [Fact]
    public async Task V133_B01_RuntimeShutdownClosesStreamingPreviewAndCompletesStart()
    {
        await using var harness = await ActivationHarness.CreateAsync(enablePreview: true);
        var invocation = harness.ActivationInvocation();
        await WaitForPreviewIdleAsync(harness, invocation);

        var sessionId = Guid.NewGuid();
        var start = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            PreviewDraftReference.FromRevision(harness.Source), null,
            "V133 B01 shutdown streaming preview");
        AssertAccepted(await harness.Runtime.SubmitAsync(start));
        await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.LatestFrame is not null, "PreviewShutdownStreamingUnavailable");

        await harness.Runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(harness.CameraProvider.ActivationPreviewStopCount > 0);
        Assert.True(harness.CameraProvider.ActivationStopCalls > 0);
        Assert.True(harness.CameraProvider.ActivationPreviewDeviceDisposed);

        await harness.WaitForVerifiedAsync();
        var history = new SqlitePreviewSessionQuery(harness.Options);
        var page = await history.QueryAsync(new PreviewSessionHistoryFilter(sessionId,
            PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        var terminal = Assert.Single(page.Events, sessionEvent =>
            sessionEvent.CommandKind == AuditedCommandKind.StartPreview &&
            sessionEvent.Terminal);
        Assert.Equal(PreviewSessionPhase.Closed, terminal.Phase);
        Assert.Equal(PreviewRestorationState.NoActiveBaselineClosed, terminal.Restoration);
    }

    [Fact]
    public async Task V133_B02_ActivePreviewRejectsDifferentSessionWithoutNewCameraWork()
    {
        await using var harness = await ActivationHarness.CreateAsync(enablePreview: true);
        var invocation = harness.ActivationInvocation();
        await WaitForPreviewIdleAsync(harness, invocation);

        var draft = PreviewDraftReference.FromRevision(harness.Source);
        var firstSessionId = Guid.NewGuid();
        var first = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, firstSessionId,
            draft, null, "V133 B02 first streaming preview");
        AssertAccepted(await harness.Runtime.SubmitAsync(first));
        await WaitForPreviewAsync(harness, invocation, firstSessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.LatestFrame is not null, "PreviewFirstStreamingUnavailable");

        var openBefore = harness.CameraProvider.OpenCount;
        var applyBefore = harness.CameraProvider.ActivationApplyCount;
        var startBefore = harness.CameraProvider.ActivationPreviewStartCount;
        var secondSessionId = Guid.NewGuid();
        var second = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, secondSessionId,
            draft, null, "V133 B02 competing streaming preview");
        var rejected = await harness.Runtime.SubmitAsync(second);

        Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
        Assert.Equal(AuditPersistence.Persisted, rejected.Audit);
        Assert.Equal("PreviewSessionInProgress", rejected.ReasonCode);
        Assert.Equal(openBefore, harness.CameraProvider.OpenCount);
        Assert.Equal(applyBefore, harness.CameraProvider.ActivationApplyCount);
        Assert.Equal(startBefore, harness.CameraProvider.ActivationPreviewStartCount);

        var firstStillStreaming = await WaitForPreviewAsync(harness, invocation, firstSessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.LatestFrame is not null, "PreviewOriginalSessionDisturbed");
        Assert.Equal(firstSessionId, firstStillStreaming.PreviewSessionId);

        var exit = new ExitPreviewSessionCommand(Guid.NewGuid(), invocation, firstSessionId,
            cancel: false, "V133 B02 close original streaming preview");
        AssertAccepted(await harness.Runtime.SubmitAsync(exit));
        await WaitForPreviewAsync(harness, invocation, firstSessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Closed,
            "PreviewOriginalSessionCloseUnavailable");
    }
}
