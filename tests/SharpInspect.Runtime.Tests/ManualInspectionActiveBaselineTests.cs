using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V135_M16_ManualSessionRestoresRealActiveBaselineAfterGracefulExit()
    {
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true);
        var baseline = await CreateActiveBaselineAsync(harness);
        var baselineSnapshot = Assert.IsType<RecipeActivationSnapshot>(baseline.SuccessfulSnapshot);
        var baselineCamera = baselineSnapshot.CameraSetup;
        var before = harness.CameraProvider.GetDiagnostics().Devices.Single();

        var start = new StartManualInspectionSessionCommand(Guid.NewGuid(), harness.Invocation(),
            ManualRecipeSelection.FromDraft(harness.Draft), baseline.Reference,
            "V135 manual session with active development baseline");
        AssertAccepted(await harness.Runtime.SubmitAsync(start),
            "Manual active-baseline start");
        var ready = await harness.WaitForSnapshotAsync(value =>
            value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Manual active-baseline session did not become ready");
        var sessionId = ready.SessionId!.Value;

        var admittedPage = await harness.History.QueryAsync(new(SessionId: sessionId));
        Assert.True(admittedPage.Available, admittedPage.ReasonCode);
        var admittedHeader = admittedPage.Events[0].Header;
        Assert.Equal(baseline.Reference, admittedHeader.ActiveActivation);
        Assert.Equal(baselineSnapshot.ContentHash, admittedHeader.ActiveSnapshotContentHash);
        Assert.Equal(RecipeActivationValidation.CameraHash(baselineCamera),
            admittedHeader.ActiveCameraContentHash);

        AssertAccepted(await harness.ExitAsync(sessionId, ManualInspectionExitMode.Graceful),
            "Manual active-baseline graceful exit");
        var closed = await harness.WaitForSnapshotAsync(value =>
            value.SessionId == sessionId && value.Phase == ManualInspectionSessionPhase.Closed,
            "Manual active-baseline session did not close");
        Assert.Equal(ManualInspectionRestorationState.Restored, closed.Restoration);
        Assert.False(closed.RecoveryRequired);

        var setup = await harness.Camera.GetSetupAsync("ManualCamera", harness.Invocation());
        Assert.True(setup.Available, setup.ReasonCode);
        Assert.Equal("CameraActivationRestored", setup.ReasonCode);
        var restoredCamera = Assert.IsType<CameraSetupSnapshot>(setup.Snapshot);
        Assert.Equal(baselineCamera.LogicalRole, restoredCamera.LogicalRole);
        Assert.Equal(baselineCamera.Binding, restoredCamera.Binding);
        Assert.Equal(baselineCamera.Differences, restoredCamera.Differences);
        Assert.Equal(baselineCamera.Extension, restoredCamera.Extension);
        Assert.Equal(baselineCamera.Capabilities?.ContentHash,
            restoredCamera.Capabilities?.ContentHash);
        Assert.Equal(baselineCamera.Requested, restoredCamera.Requested);
        Assert.Equal(baselineCamera.Effective, restoredCamera.Effective);
        Assert.Equal(CameraProviderAvailability.Available,
            restoredCamera.Health.ProviderAvailability);
        Assert.Equal(CameraConnectionState.Open, restoredCamera.Health.Connection);
        Assert.Equal(CameraConfigurationState.Applied, restoredCamera.Health.Configuration);
        Assert.Equal(CameraAcquisitionState.Stopped, restoredCamera.Health.Acquisition);
        // The read-back is a new observation with CameraActivationRestored as its
        // reason. The persisted baseline hash above stays immutable; its process
        // settings and binding, rather than its observation time, are restored.
        Assert.True(restoredCamera.Health.ObservedAt.MonotonicTimestamp >=
            baselineCamera.Health.ObservedAt.MonotonicTimestamp);

        var after = harness.CameraProvider.GetDiagnostics().Devices.Single();
        Assert.True(after.IsOpen);
        Assert.True(after.OpenCount >= before.OpenCount + 2);
        Assert.True(after.ConfigurationCursor >= before.ConfigurationCursor + 2);

        var exact = await harness.Activations.ReadAsync(baseline.Reference);
        Assert.True(exact.Available, exact.ReasonCode);
        Assert.Equal(baseline.ContentHash, exact.Record!.ContentHash);
    }

    [Fact]
    public async Task V135_M17_ActiveBaselineRestoreFailureRetainsRecoveryFence()
    {
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true);
        var baseline = await CreateActiveBaselineAsync(harness);

        var start = new StartManualInspectionSessionCommand(Guid.NewGuid(), harness.Invocation(),
            ManualRecipeSelection.FromDraft(harness.Draft), baseline.Reference,
            "V135 manual restore-failure baseline");
        AssertAccepted(await harness.Runtime.SubmitAsync(start),
            "Manual restore-failure start");
        var ready = await harness.WaitForSnapshotAsync(value =>
            value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Manual restore-failure session did not become ready");
        var sessionId = ready.SessionId!.Value;

        // Dispose the real virtual provider after the candidate was applied.  The
        // lease must observe that it can no longer reopen the durable baseline and
        // retain the recovery fence instead of reporting a false restoration.
        await harness.CameraProvider.DisposeAsync();
        Assert.True(harness.CameraProvider.GetDiagnostics().IsDisposed);

        AssertAccepted(await harness.ExitAsync(sessionId, ManualInspectionExitMode.Graceful),
            "Manual restore-failure exit admission");
        var blocked = await WaitForManualSnapshotAsync(harness, value =>
            value.SessionId == sessionId && value.Phase == ManualInspectionSessionPhase.RecoveryBlocked,
            "Manual restore failure did not retain recovery fence");
        Assert.Equal(ManualInspectionRestorationState.RecoveryBlocked, blocked.Restoration);
        Assert.True(blocked.RecoveryRequired);
        Assert.False(blocked.Ready);

        var station = await harness.Runtime.GetSnapshotAsync();
        Assert.False(station.Ready);
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.Contains("ManualInspectionRecoveryRequired", station.AdmissionBlockers);

        var beforeRetry = harness.CameraProvider.GetDiagnostics().Devices.Single();
        var retry = await harness.Runtime.SubmitAsync(new StartManualInspectionSessionCommand(
            Guid.NewGuid(), harness.Invocation(), ManualRecipeSelection.FromDraft(harness.Draft),
            baseline.Reference, "V135 retry after active-baseline restore failure"));
        Assert.Equal(CommandDisposition.Rejected, retry.Disposition);
        Assert.Equal("ManualInspectionRecoveryRequired", retry.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, retry.Audit);
        var afterRetry = harness.CameraProvider.GetDiagnostics().Devices.Single();
        Assert.Equal(beforeRetry.OpenCount, afterRetry.OpenCount);
        Assert.False(afterRetry.IsOpen);

        var history = await harness.History.QueryAsync(new(SessionId: sessionId));
        Assert.True(history.Available, history.ReasonCode);
        Assert.Contains(history.Events, value => value.Phase == ManualInspectionSessionPhase.RecoveryBlocked &&
            value.Restoration == ManualInspectionRestorationState.RecoveryBlocked);
    }

    private static async Task<RecipeActivationRecord> CreateActiveBaselineAsync(
        ManualHarness harness)
    {
        var fixture = harness.Fixture;
        var invocation = harness.Invocation();
        var releaseCommand = new ReleaseRecipeCommand(Guid.NewGuid(), invocation,
            harness.Draft.DraftId, harness.Draft.Revision, harness.Draft.RevisionContentHash,
            fixture.Options.RecipeReleases!.Policy.Reference,
            "V135 release active manual baseline");
        var releaseGrant = await GrantAsync(fixture, invocation,
            Permission.ReleaseRecipe, releaseCommand.CorrelationId,
            releaseCommand.AuthorizationTarget, AuditedCommandKind.ReleaseRecipe);
        var releasedResult = await harness.Service<IRecipeReleaseService>().ReleaseAsync(
            releaseCommand with { Invocation = invocation with { StepUpGrantId = releaseGrant.GrantId } });
        AssertAccepted(releasedResult.Outcome, "Release active manual baseline");
        var released = Assert.IsType<ReleasedRecipe>(releasedResult.Recipe);

        var contract = PlcResultContractTestSupport.Contract(harness.Factory.Descriptor.ResultSchema);
        var contractCommand = new ChangePlcResultContractCommand(Guid.NewGuid(), invocation,
            contract, null, "V135 bind active manual baseline PLC contract");
        var contractGrant = await GrantAsync(fixture, invocation,
            Permission.ManagePlcResultContract, contractCommand.CorrelationId,
            contractCommand.AuthorizationTarget, AuditedCommandKind.ChangePlcResultContract);
        var contractResult = await harness.Runtime.SubmitAsync(contractCommand with
        {
            Invocation = invocation with { StepUpGrantId = contractGrant.GrantId }
        });
        AssertAccepted(contractResult, "Bind active manual baseline PLC contract");

        var activationCommand = new ActivateRecipeCommand(Guid.NewGuid(), invocation,
            released.Reference, released.Record.ReleaseId, released.Record.ContentHash,
            null, null, "V135 create active manual baseline");
        var activationGrant = await GrantAsync(fixture, invocation,
            Permission.ActivateRecipe, activationCommand.CorrelationId,
            activationCommand.AuthorizationTarget, AuditedCommandKind.ActivateRecipe);
        var authorizedActivation = activationCommand with
        {
            Invocation = invocation with { StepUpGrantId = activationGrant.GrantId }
        };
        var station = Assert.IsType<StationRuntime>(harness.Runtime);
        var activation = new RecipeActivationService(
            harness.Service<RecipeDraftService>(), harness.Service<IReleasedRecipeQuery>(),
            harness.Service<IPlcResultContractQuery>(), harness.Activations, fixture.Authorization,
            fixture.Store, fixture.Options, harness.Service<AlgorithmPreparationService>(),
            harness.Service<AlgorithmPreparationOptions>(), harness.Service<FrameBufferPool>(),
            (correlation, token) => station.ReserveRecipeActivationAsync(correlation, token),
            () => station.GetSnapshotAsync(), RecipeActivationInternalFixture.CreateForContractTests());
        var activationResult = await activation.ActivateAsync(authorizedActivation);
        AssertAccepted(activationResult.Outcome, "Create active manual baseline");
        var record = Assert.IsType<RecipeActivationRecord>(activationResult.Record);
        Assert.Equal(RecipeActivationOutcomeState.Succeeded, record.Outcome.State);
        Assert.Equal(RecipeActivationEvidenceKind.InternalContractFixture, record.EvidenceKind);
        Assert.True(record.DevelopmentOnly);
        Assert.False(record.ProductionAuthority);
        Assert.NotNull(record.SuccessfulSnapshot);
        Assert.Equal(record.ResultingRecipe, record.SuccessfulSnapshot!.Recipe);
        return record;
    }

    private static async Task<StepUpResult> GrantAsync(
        RecipeDraftStorageTests.Fixture fixture, CommandInvocation invocation,
        Permission permission, Guid operationId, string authorizationTarget,
        AuditedCommandKind commandKind)
    {
        var grant = await fixture.Authorization.ReauthenticateAsync(new StepUpRequest(
            Guid.NewGuid(), invocation,
            new StepUpBinding(permission, operationId, authorizationTarget, commandKind),
            fixture.Password));
        Assert.True(grant.Succeeded, grant.ReasonCode);
        Assert.NotNull(grant.GrantId);
        return grant;
    }

    private static async Task<ManualInspectionSessionSnapshot> WaitForManualSnapshotAsync(
        ManualHarness harness, Func<ManualInspectionSessionSnapshot, bool> predicate,
        string timeoutReason)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        ManualInspectionSessionSnapshot? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var result = await harness.Manual.GetSnapshotAsync(harness.Invocation());
            Assert.True(result.Available, result.ReasonCode);
            last = Assert.IsType<ManualInspectionSessionSnapshot>(result.Snapshot);
            if (predicate(last)) return last;
            await Task.Delay(25);
        }

        throw new XunitException(timeoutReason + ":" + last?.Phase + "/" + last?.ReasonCode);
    }

}
