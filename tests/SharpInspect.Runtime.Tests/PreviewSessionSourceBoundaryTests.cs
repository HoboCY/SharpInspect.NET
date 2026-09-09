using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class RecipeActivationServiceTests
{
    [Fact]
    public async Task V133_P11_IntegrationSaveIsDurablyRejectedBeforeDraftAndGrantMutation()
    {
        await using var harness = await ActivationHarness.CreateAsync(
            CreateStrictPreviewAuthorizationPolicy(Permission.EditRecipeDraft), enablePreview: true);
        var physicalInvocation = harness.ActivationInvocation();
        await WaitForPreviewIdleAsync(harness, physicalInvocation);

        var draft = PreviewDraftReference.FromRevision(harness.Source);
        var beforeDrafts = await harness.Drafts.QueryAsync(new RecipeDraftFilter(
            DraftId: draft.DraftId, PageSize: 20));
        Assert.True(beforeDrafts.Available, beforeDrafts.ReasonCode);
        var beforeRevision = Assert.Single(beforeDrafts.Revisions);

        var sessionId = Guid.NewGuid();
        var start = new StartPreviewSessionCommand(Guid.NewGuid(), physicalInvocation, sessionId,
            draft, expectedActive: null, "V133 P11 start physical-console preview");
        AssertAccepted(await harness.Runtime.SubmitAsync(start));
        await WaitForPreviewAsync(harness, physicalInvocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.LatestFrame is not null, "PreviewSourceBoundaryStartUnavailable");

        var freeze = new FreezePreviewSettingsCommand(Guid.NewGuid(), physicalInvocation, sessionId,
            "V133 P11 freeze physical-console preview");
        AssertAccepted(await harness.Runtime.SubmitAsync(freeze));
        var frozen = await WaitForPreviewAsync(harness, physicalInvocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.FrozenSettings is not null && snapshot.Configuration is
                { ExposureMode: PreviewAutomaticControlMode.Off,
                  GainMode: PreviewAutomaticControlMode.Off,
                  WhiteBalanceMode: PreviewAutomaticControlMode.Off },
            "PreviewSourceBoundaryFreezeUnavailable");
        var frozenSettings = Assert.IsType<PreviewCameraProcessSettings>(frozen.FrozenSettings);

        var integrationInvocation = new CommandInvocation(CommandSource.Integration,
            physicalInvocation.PrincipalId, physicalInvocation.SessionId);
        var integrationSave = new SavePreviewToDraftCommand(Guid.NewGuid(), integrationInvocation,
            sessionId, draft, frozenSettings.ContentHash,
            "V133 P11 reject non-console Preview Draft save");
        var grant = await IssuePreviewGrantAsync(harness, physicalInvocation,
            integrationSave.CreateDraftSaveStepUpBinding());

        var rejected = await harness.Runtime.SubmitAsync(integrationSave with
        { Invocation = integrationInvocation with { StepUpGrantId = grant.GrantId } });
        Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
        Assert.Equal("LocalConsoleRequired", rejected.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, rejected.Audit);

        await harness.WaitForVerifiedAsync();
        var afterDrafts = await harness.Drafts.QueryAsync(new RecipeDraftFilter(
            DraftId: draft.DraftId, PageSize: 20));
        Assert.True(afterDrafts.Available, afterDrafts.ReasonCode);
        var afterRevision = Assert.Single(afterDrafts.Revisions);
        Assert.Equal(beforeRevision.Revision, afterRevision.Revision);
        Assert.Equal(beforeRevision.RevisionContentHash, afterRevision.RevisionContentHash);

        var trace = await new SqliteCommandTraceQuery(harness.Options).QueryAsync(
            new CommandTraceFilter(CorrelationId: integrationSave.CorrelationId, PageSize: 20));
        var rejectedFact = Assert.Single(trace.Records);
        Assert.Equal(AuditedCommandKind.SaveRecipeDraft, rejectedFact.CommandKind);
        Assert.Equal(CommandSource.Integration, rejectedFact.Source);
        Assert.Equal(CommandDisposition.Rejected, rejectedFact.Disposition);
        Assert.Equal("LocalConsoleRequired", rejectedFact.ReasonCode);
        Assert.Equal(grant.GrantId, rejectedFact.ClaimedStepUpGrantId);
        Assert.DoesNotContain(trace.Records,
            record => record.Disposition == CommandDisposition.Accepted);

        // The durable source rejection contains the presented grant but no
        // accepted Draft command. This avoids replaying a rejected correlation
        // merely to infer whether the one-time grant was consumed.
        var previewHistory = await new SqlitePreviewSessionQuery(harness.Options).QueryAsync(
            new PreviewSessionHistoryFilter(sessionId, PageSize: 128));
        Assert.True(previewHistory.Available, previewHistory.ReasonCode);
        Assert.DoesNotContain(previewHistory.Events, value =>
            value.CommandCorrelationId == integrationSave.CorrelationId &&
            value.CommandKind == AuditedCommandKind.SaveRecipeDraft &&
            value.Phase == PreviewSessionPhase.SavingDraft);

        var exit = new ExitPreviewSessionCommand(Guid.NewGuid(), physicalInvocation, sessionId,
            cancel: false, "V133 P11 close physical-console preview");
        AssertAccepted(await harness.Runtime.SubmitAsync(exit));
        var closed = await WaitForPreviewAsync(harness, physicalInvocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Closed &&
                snapshot.Restoration == PreviewRestorationState.NoActiveBaselineClosed,
            "PreviewSourceBoundaryCloseUnavailable");
        Assert.Equal(PreviewRestorationState.NoActiveBaselineClosed, closed.Restoration);

        // Verify the grant's in-memory state through an independent normal Draft
        // mutation with the same exact Draft binding. This is deliberately not a
        // replay of the rejected Preview command or its Preview session state.
        var normalDraftSave = new RecipeDraftSaveRequest(
            integrationSave.CorrelationId, draft.DraftId, draft.Revision,
            draft.RevisionContentHash, harness.Source.Content,
            "V133 P11 verify grant through normal Draft save",
            physicalInvocation with { StepUpGrantId = grant.GrantId }, grant.GrantId);
        var grantVerification = await harness.Drafts.SaveAsync(normalDraftSave);
        Assert.True(grantVerification.Saved, grantVerification.ReasonCode);
        Assert.NotNull(grantVerification.Revision);
        Assert.Equal(draft.Revision + 1, grantVerification.Revision!.Revision);
        await harness.WaitForVerifiedAsync();
    }
}
