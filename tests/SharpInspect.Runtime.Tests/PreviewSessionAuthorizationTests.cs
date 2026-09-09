using System.Linq;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class RecipeActivationServiceTests
{
    [Fact]
    public async Task V133_P09_StrictEditDraftSaveUsesExactDraftBindingAndConsumesGrantOnce()
    {
        await using var harness = await ActivationHarness.CreateAsync(
            CreateStrictPreviewAuthorizationPolicy(Permission.EditRecipeDraft), enablePreview: true);
        var invocation = harness.ActivationInvocation();
        await WaitForPreviewIdleAsync(harness, invocation);

        var draftAccess = await harness.Drafts.GetAccessAsync(invocation);
        Assert.True(draftAccess.CanSave, draftAccess.ReasonCode);
        Assert.True(draftAccess.RequiresStepUp);

        var draft = PreviewDraftReference.FromRevision(harness.Source);
        var sessionId = Guid.NewGuid();
        var start = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            draft, null, "V133 P09 start strict EditRecipeDraft preview");
        AssertAccepted(await harness.Runtime.SubmitAsync(start));
        await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.LatestFrame is not null, "PreviewStrictEditStartUnavailable");

        var freeze = new FreezePreviewSettingsCommand(Guid.NewGuid(), invocation, sessionId,
            "V133 P09 freeze strict EditRecipeDraft preview");
        AssertAccepted(await harness.Runtime.SubmitAsync(freeze));
        var frozen = await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.FrozenSettings is not null && snapshot.Configuration is
                { ExposureMode: PreviewAutomaticControlMode.Off,
                  GainMode: PreviewAutomaticControlMode.Off,
                  WhiteBalanceMode: PreviewAutomaticControlMode.Off },
            "PreviewStrictEditFreezeUnavailable");
        var frozenSettings = Assert.IsType<PreviewCameraProcessSettings>(frozen.FrozenSettings);

        var missingGrant = new SavePreviewToDraftCommand(Guid.NewGuid(), invocation, sessionId,
            draft, frozenSettings.ContentHash, "V133 P09 save without EditRecipeDraft grant");
        var missing = await harness.Runtime.SubmitAsync(missingGrant);
        Assert.Equal(CommandDisposition.Rejected, missing.Disposition);
        Assert.Equal("StepUpRequired", missing.ReasonCode);
        await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming,
            "PreviewStrictEditMissingGrantRecoveryUnavailable");

        var wrongTarget = new SavePreviewToDraftCommand(Guid.NewGuid(), invocation, sessionId,
            draft, frozenSettings.ContentHash, "V133 P09 save with Preview intent binding");
        Assert.NotEqual(wrongTarget.ExpectedDraft.DraftId.ToString("D"), wrongTarget.AuthorizationTarget);
        var wrongGrant = await IssuePreviewGrantAsync(harness, invocation,
            new StepUpBinding(Permission.EditRecipeDraft, wrongTarget.CorrelationId,
                wrongTarget.AuthorizationTarget, AuditedCommandKind.SaveRecipeDraft));
        var wrong = await harness.Runtime.SubmitAsync(wrongTarget with
        { Invocation = invocation with { StepUpGrantId = wrongGrant.GrantId } });
        Assert.Equal(CommandDisposition.Rejected, wrong.Disposition);
        Assert.Equal("StepUpInvalid", wrong.ReasonCode);
        await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming,
            "PreviewStrictEditWrongBindingRecoveryUnavailable");

        var exact = new SavePreviewToDraftCommand(Guid.NewGuid(), invocation, sessionId,
            draft, frozenSettings.ContentHash, "V133 P09 save with exact Draft binding");
        var exactBinding = exact.CreateDraftSaveStepUpBinding();
        Assert.Equal(Permission.EditRecipeDraft, exactBinding.Permission);
        Assert.Equal(exact.CorrelationId, exactBinding.CommandCorrelationId);
        Assert.Equal(exact.ExpectedDraft.DraftId.ToString("D"), exactBinding.TargetId);
        Assert.Equal(AuditedCommandKind.SaveRecipeDraft, exactBinding.CommandKind);
        var exactGrant = await IssuePreviewGrantAsync(harness, invocation, exactBinding);
        var savedOutcome = await harness.Runtime.SubmitAsync(exact with
        { Invocation = invocation with { StepUpGrantId = exactGrant.GrantId } });
        AssertAccepted(savedOutcome);
        var saved = await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.LastSavedDraft is not null, "PreviewStrictEditSaveUnavailable");
        var savedReference = saved.LastSavedDraft!;
        var savedRead = await harness.Drafts.ReadAsync(savedReference.DraftId,
            savedReference.Revision);
        Assert.True(savedRead.Available, savedRead.ReasonCode);
        Assert.NotNull(savedRead.Revision);

        // The exact grant is one-way consumed by the Draft transaction. A new
        // Save command must fail even though the caller presents that grant.
        var reused = new SavePreviewToDraftCommand(Guid.NewGuid(), invocation, sessionId,
            savedReference, frozenSettings.ContentHash,
            "V133 P09 reuse consumed EditRecipeDraft grant");
        var reusedOutcome = await harness.Runtime.SubmitAsync(reused with
        { Invocation = invocation with { StepUpGrantId = exactGrant.GrantId } });
        Assert.Equal(CommandDisposition.Rejected, reusedOutcome.Disposition);
        Assert.Equal("StepUpInvalid", reusedOutcome.ReasonCode);
        await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming,
            "PreviewStrictEditConsumedGrantRecoveryUnavailable");

        var exit = new ExitPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            cancel: false, "V133 P09 close strict EditRecipeDraft preview");
        AssertAccepted(await harness.Runtime.SubmitAsync(exit));
        var closed = await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Closed &&
                snapshot.Restoration == PreviewRestorationState.NoActiveBaselineClosed,
            "PreviewStrictEditCloseUnavailable");
        Assert.Equal(PreviewRestorationState.NoActiveBaselineClosed, closed.Restoration);
    }

    [Fact]
    public async Task V133_P10_StrictRunPreviewRequiresExactStartGrantBeforeCandidateIo()
    {
        await using var harness = await ActivationHarness.CreateAsync(
            CreateStrictPreviewAuthorizationPolicy(Permission.RunPreview), enablePreview: true);
        var invocation = harness.ActivationInvocation();
        await WaitForPreviewIdleAsync(harness, invocation);

        var previewAccess = await ((IPreviewSessionService)harness.Runtime).GetAccessAsync(invocation);
        Assert.True(previewAccess.CanRun, previewAccess.ReasonCode);
        Assert.True(previewAccess.RequiresStepUp);

        var beforeApply = harness.CameraProvider.ActivationApplyCount;
        var beforePreviewStart = harness.CameraProvider.ActivationPreviewStartCount;
        var draft = PreviewDraftReference.FromRevision(harness.Source);
        var sessionId = Guid.NewGuid();
        var missingGrant = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            draft, null, "V133 P10 start without RunPreview grant");
        var missing = await harness.Runtime.SubmitAsync(missingGrant);
        Assert.Equal(CommandDisposition.Rejected, missing.Disposition);
        Assert.Equal("StepUpRequired", missing.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, missing.Audit);
        Assert.Equal(beforeApply, harness.CameraProvider.ActivationApplyCount);
        Assert.Equal(beforePreviewStart, harness.CameraProvider.ActivationPreviewStartCount);
        await WaitForPreviewIdleAsync(harness, invocation);

        var exact = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            draft, null, "V133 P10 start with exact RunPreview grant");
        var exactBinding = new StepUpBinding(Permission.RunPreview, exact.CorrelationId,
            exact.AuthorizationTarget, AuditedCommandKind.StartPreview);
        Assert.Equal(exact.AuthorizationTarget, exactBinding.TargetId);
        var exactGrant = await IssuePreviewGrantAsync(harness, invocation, exactBinding);
        var admitted = await harness.Runtime.SubmitAsync(exact with
        { Invocation = invocation with { StepUpGrantId = exactGrant.GrantId } });
        AssertAccepted(admitted);
        var streaming = await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.LatestFrame is not null, "PreviewStrictRunStartUnavailable");
        Assert.Equal(sessionId, streaming.LatestFrame!.SessionId);

        var exit = new ExitPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            cancel: false, "V133 P10 close strict RunPreview session");
        var exitGrant = await IssuePreviewGrantAsync(harness, invocation,
            new StepUpBinding(Permission.RunPreview, exit.CorrelationId,
                exit.AuthorizationTarget, AuditedCommandKind.Exit));
        AssertAccepted(await harness.Runtime.SubmitAsync(exit with
        { Invocation = invocation with { StepUpGrantId = exitGrant.GrantId } }));
        var closed = await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Closed &&
                snapshot.Restoration == PreviewRestorationState.NoActiveBaselineClosed,
            "PreviewStrictRunCloseUnavailable");
        Assert.Equal(PreviewRestorationState.NoActiveBaselineClosed, closed.Restoration);

        var station = await harness.Runtime.GetSnapshotAsync();
        Assert.Equal(ExclusiveMode.None, station.Mode);
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.False(station.Ready);
        Assert.Null(station.ActiveRecipe);
    }

    private static AuthorizationPolicy CreateStrictPreviewAuthorizationPolicy(Permission stepUpPermission)
    {
        var baseline = CreatePreviewAuthorizationPolicy();
        var suffix = stepUpPermission == Permission.EditRecipeDraft ? "edit" : "run";
        return new AuthorizationPolicy("v133-preview-strict-" + suffix,
            "v133-preview-strict-2026-09-" + suffix,
            baseline.RoleBundles.ToDictionary(pair => pair.Key,
                pair => pair.Value.AsEnumerable()),
            baseline.StepUpPermissions.Append(stepUpPermission));
    }

    private static async Task<StepUpResult> IssuePreviewGrantAsync(
        ActivationHarness harness, CommandInvocation invocation, StepUpBinding binding)
    {
        var grant = await harness.Authorization.ReauthenticateAsync(new StepUpRequest(
            Guid.NewGuid(), invocation, binding,
            "V132 activation integration password 2026!"));
        Assert.True(grant.Succeeded, grant.ReasonCode);
        Assert.NotNull(grant.GrantId);
        await harness.WaitForVerifiedAsync();
        return grant;
    }
}
