using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Preview;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class RecipeActivationServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task V133_S02_ColdRestartRecoversDurablePreviewAdmission(bool cameraAvailable)
    {
        await using var harness = await ActivationHarness.CreateAsync(enablePreview: true);
        var invocation = harness.ActivationInvocation();
        await WaitForPreviewIdleAsync(harness, invocation);

        var admitted = await AdmitPreviewWithoutRuntimeOwnerAsync(harness,
            "V133 S02 durable Preview restart admission");
        await harness.Runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        var provider = new RestartCameraProvider(cameraAvailable);
        await using var restarted = CreateRestartedPreviewRuntime(harness, provider);
        await restarted.WaitForRecipeActivationStartupAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await restarted.WaitForPreviewStartupAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var preview = (IPreviewSessionService)restarted;
        var previewRead = await preview.GetSnapshotAsync(invocation);
        Assert.True(previewRead.Available, previewRead.ReasonCode);
        Assert.NotNull(previewRead.Snapshot);
        var previewSnapshot = previewRead.Snapshot!;
        var station = await restarted.GetSnapshotAsync();
        Assert.False(station.Ready);
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.Null(station.ActiveRecipe);

        var history = new SqlitePreviewSessionQuery(harness.Options);
        var page = await history.QueryAsync(new PreviewSessionHistoryFilter(
            admitted.Command.PreviewSessionId, PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        var terminal = Assert.Single(page.Events, value =>
            value.CommandKind == AuditedCommandKind.StartPreview && value.Terminal);

        var accepted = Assert.Single(page.Events, value =>
            value.CommandKind == AuditedCommandKind.StartPreview &&
            !value.Terminal && value.Phase == PreviewSessionPhase.Admitted);
        Assert.Equal(admitted.StartFact.AttemptId, accepted.AttemptId);
        Assert.Equal(admitted.Command.CorrelationId, accepted.CommandCorrelationId);

        if (cameraAvailable)
        {
            Assert.Equal(PreviewSessionPhase.Closed, previewSnapshot.Phase);
            Assert.Equal(PreviewRestorationState.NoActiveBaselineClosed,
                previewSnapshot.Restoration);
            Assert.False(previewSnapshot.RecoveryRequired);
            Assert.Equal(PreviewSessionPhase.Closed, terminal.Phase);
            Assert.Equal(PreviewRestorationState.NoActiveBaselineClosed,
                terminal.Restoration);
            Assert.False(page.RecoveryRequired);
            Assert.Null(page.PendingHeader);
            Assert.Equal(1, provider.OpenCalls);
            Assert.Equal(1, provider.StopCalls);
            Assert.Equal(1, provider.DisposeCalls);
            var recovery = await harness.Store.ReadPreviewRecoveryStateAsync();
            Assert.True(recovery.Available, recovery.ReasonCode);
            Assert.Equal("PreviewSessionIdle", recovery.ReasonCode);
            Assert.Null(recovery.Header);
            Assert.Null(recovery.StartFact);
            Assert.False(recovery.RecoveryRequired);
        }
        else
        {
            Assert.Equal(PreviewSessionPhase.RecoveryBlocked, previewSnapshot.Phase);
            Assert.Equal(PreviewRestorationState.RecoveryBlocked,
                previewSnapshot.Restoration);
            Assert.True(previewSnapshot.RecoveryRequired);
            Assert.Equal(PreviewSessionPhase.RecoveryBlocked, terminal.Phase);
            Assert.Equal(PreviewRestorationState.RecoveryBlocked, terminal.Restoration);
            Assert.True(page.RecoveryRequired);
            Assert.Equal(1, provider.OpenCalls);
            Assert.Equal(0, provider.StopCalls);
            Assert.Equal(0, provider.DisposeCalls);

            Assert.NotNull(station.AlarmState);
            var alarm = station.AlarmState!;
            Assert.True(alarm.Instances.Any(value =>
                value.Code == "PreviewRecoveryRequired" &&
                value.Source == "Runtime.Preview" && value.IsLatched &&
                value.ProductionImpact == ProductionImpact.BlockNewTriggers),
                $"{alarm.ReasonCode}; integrity={harness.Store.Integrity?.ReasonCode}; {string.Join(",", station.AdmissionBlockers)}");

            var retry = new StartPreviewSessionCommand(Guid.NewGuid(), invocation,
                Guid.NewGuid(), PreviewDraftReference.FromRevision(harness.Source), null,
                "V133 S02 retry after unavailable cold provider");
            var retryOutcome = await restarted.SubmitAsync(retry);
            Assert.Equal(CommandDisposition.Rejected, retryOutcome.Disposition);
            Assert.Equal("PreviewRecoveryRequired", retryOutcome.ReasonCode);
            Assert.Equal(1, provider.OpenCalls);
        }

        Assert.Equal(admitted.Command.PreviewSessionId, terminal.SessionId);
        Assert.Equal(admitted.Command.CorrelationId, terminal.CommandCorrelationId);
        Assert.Equal(admitted.StartFact.AttemptId, terminal.AttemptId);
        Assert.Equal(admitted.Header.Draft, terminal.Header.Draft);
    }

    [Fact]
    public async Task V133_S03_ColdRestartBindingConflictDoesNotOpenProviderAndBlocksPreview()
    {
        await using var harness = await ActivationHarness.CreateAsync(enablePreview: true);
        var invocation = harness.ActivationInvocation();
        await WaitForPreviewIdleAsync(harness, invocation);

        var admitted = await AdmitPreviewWithoutRuntimeOwnerAsync(harness,
            "V133 S03 durable Preview binding conflict admission");
        var before = await harness.Store.ReadCameraSetupAsync(
            harness.Source.Content.CameraRole);
        Assert.True(before.Result.Available, before.Result.ReasonCode);
        Assert.NotNull(before.State.Binding);
        var previousBinding = before.State.Binding!;

        var rebind = new CameraRebindRequest(Guid.NewGuid(), invocation, "TopCamera",
            previousBinding.Revision, previousBinding.RevisionHash,
            new CameraBindingTarget(harness.CameraProvider.Identity, "Camera:One"),
            "V133 S03 change binding after Preview admission");
        var grant = await harness.Authorization.ReauthenticateAsync(new StepUpRequest(
            Guid.NewGuid(), invocation,
            new StepUpBinding(Permission.ManageCameraBindings, rebind.OperationId,
                "TopCamera", AuditedCommandKind.RebindCamera),
            "V132 activation integration password 2026!"));
        Assert.True(grant.Succeeded, grant.ReasonCode);
        Assert.NotNull(grant.GrantId);
        var rebound = await harness.Camera.RebindAsync(rebind with
        { Invocation = invocation with { StepUpGrantId = grant.GrantId } });
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        Assert.NotNull(rebound.Snapshot?.Binding);
        Assert.NotEqual(previousBinding, rebound.Snapshot!.Binding);
        await harness.WaitForVerifiedAsync();

        await harness.Runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        var provider = new RestartCameraProvider(true);
        await using var restarted = CreateRestartedPreviewRuntime(harness, provider);
        await restarted.WaitForRecipeActivationStartupAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await restarted.WaitForPreviewStartupAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var preview = (IPreviewSessionService)restarted;
        var previewRead = await preview.GetSnapshotAsync(invocation);
        Assert.True(previewRead.Available, previewRead.ReasonCode);
        Assert.NotNull(previewRead.Snapshot);
        Assert.Equal(PreviewSessionPhase.RecoveryBlocked, previewRead.Snapshot!.Phase);
        Assert.Equal(PreviewRestorationState.RecoveryBlocked,
            previewRead.Snapshot.Restoration);
        Assert.True(previewRead.Snapshot.RecoveryRequired);

        var station = await restarted.GetSnapshotAsync();
        Assert.False(station.Ready);
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.Null(station.ActiveRecipe);
        Assert.NotNull(station.AlarmState);
        var alarm = station.AlarmState!;
        Assert.Contains(alarm.Instances, value =>
            value.Code == "PreviewRecoveryRequired" &&
            value.Source == "Runtime.Preview" && value.IsLatched &&
            value.ProductionImpact == ProductionImpact.BlockNewTriggers);
        Assert.Equal(0, provider.OpenCalls);
        Assert.Equal(0, provider.StopCalls);
        Assert.Equal(0, provider.DisposeCalls);

        var history = new SqlitePreviewSessionQuery(harness.Options);
        var page = await history.QueryAsync(new PreviewSessionHistoryFilter(
            admitted.Command.PreviewSessionId, PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        Assert.True(page.RecoveryRequired);
        var terminal = Assert.Single(page.Events, value =>
            value.CommandKind == AuditedCommandKind.StartPreview && value.Terminal);
        Assert.Equal(PreviewSessionPhase.RecoveryBlocked, terminal.Phase);
        Assert.Equal(PreviewRestorationState.RecoveryBlocked, terminal.Restoration);

        Assert.Equal("PreviewRecoveryBindingConflict", terminal.ReasonCode);

        var retry = new StartPreviewSessionCommand(Guid.NewGuid(), invocation,
            Guid.NewGuid(), PreviewDraftReference.FromRevision(harness.Source), null,
            "V133 S03 retry after binding conflict");
        var retryOutcome = await restarted.SubmitAsync(retry);
        Assert.Equal(CommandDisposition.Rejected, retryOutcome.Disposition);
        Assert.Equal("PreviewRecoveryRequired", retryOutcome.ReasonCode);
        Assert.Equal(0, provider.OpenCalls);
    }

    private static async Task<PreviewAdmissionFixture> AdmitPreviewWithoutRuntimeOwnerAsync(
        ActivationHarness harness, string changeReason)
    {
        var invocation = harness.ActivationInvocation();
        var access = await harness.Authorization.GetPreviewAccessAsync(invocation);
        Assert.True(access.CanRun, access.ReasonCode);

        var camera = await harness.Store.ReadCameraSetupAsync(
            harness.Source.Content.CameraRole);
        Assert.True(camera.Result.Available, camera.Result.ReasonCode);
        Assert.NotNull(camera.State.Binding);
        var state = await harness.Runtime.GetSnapshotAsync();
        var command = new StartPreviewSessionCommand(Guid.NewGuid(), invocation,
            Guid.NewGuid(), PreviewDraftReference.FromRevision(harness.Source), null,
            changeReason);
        var attemptId = Guid.NewGuid();
        var admission = await harness.Authorization.HandlePreviewCommandAsync(command,
            state.RuntimeEpoch, attemptId,
            new PreviewSessionAdmissionInput(harness.Source, camera.State, null),
            existing: null, forcedRejection: null,
            deadline: new StoreDeadline(harness.Store.CommitTimeout),
            cancellationToken: CancellationToken.None);
        Assert.Equal(CommandDisposition.Accepted, admission.Outcome.Disposition);
        Assert.Equal(AuditPersistence.Persisted, admission.Outcome.Audit);
        Assert.NotNull(admission.Header);
        Assert.NotNull(admission.CommandFact);
        var admittedHeader = admission.Header!;
        var admittedFact = admission.CommandFact!;
        Assert.Equal(attemptId, admittedFact.AttemptId);
        Assert.Equal(command.CorrelationId, admittedFact.CorrelationId);
        Assert.Equal(command.PreviewSessionId, admittedHeader.SessionId);
        Assert.Equal(command.Draft, admittedHeader.Draft);
        Assert.Equal(camera.State.Binding, admittedHeader.CurrentBinding);

        var persisted = await harness.Store.ReadPreviewRecoveryStateAsync();
        Assert.True(persisted.Available, persisted.ReasonCode);
        Assert.Equal("PreviewSessionRecoveryPending", persisted.ReasonCode);
        Assert.Equal(command.PreviewSessionId, persisted.Header?.SessionId);
        Assert.Equal(command.CorrelationId, persisted.StartFact?.CorrelationId);
        Assert.Equal(command.Draft, persisted.Header?.Draft);
        Assert.Equal(camera.State.Binding, persisted.Header?.CurrentBinding);

        // Authorization admission is durable, but the old Runtime has never
        // installed a Preview owner or touched the Preview camera path.
        Assert.Equal(PreviewSessionPhase.Idle,
            (await ((IPreviewSessionService)harness.Runtime).GetSnapshotAsync(invocation))
                .Snapshot?.Phase);
        Assert.Equal(0, harness.CameraProvider.PreviewStartCount);
        await harness.WaitForVerifiedAsync();
        return new(command, admittedHeader, admittedFact);
    }

    private static StationRuntime CreateRestartedPreviewRuntime(ActivationHarness harness,
        ICameraProvider provider)
    {
        var restarted = new StationRuntime(harness.Store, TimeSpan.FromMilliseconds(50),
            sessions: harness.Sessions, authorization: harness.Authorization,
            frameBufferPool: harness.FramePool, cameraProviders: new[] { provider },
            cameraSetupOptions: new CameraSetupOptions
            {
                OperationTimeout = TimeSpan.FromSeconds(2),
                ShutdownTimeout = TimeSpan.FromSeconds(2)
            }, productionStoreOptions: harness.Options);
        var service = new RecipeActivationService(harness.Drafts, harness.ReleaseHistory,
            harness.ContractHistory, harness.ActivationHistory, harness.Authorization,
            harness.Store, harness.Options, harness.Preparation, harness.PreparationOptions,
            harness.FramePool,
            (correlation, token) => restarted.ReserveRecipeActivationAsync(correlation, token),
            () => restarted.GetSnapshotAsync());
        restarted.ConfigureRecipeActivationService(service);
        restarted.ConfigurePreviewSessions(new PreviewSessionOptions(), harness.Drafts,
            harness.Options);
        return restarted;
    }

    private sealed record PreviewAdmissionFixture(StartPreviewSessionCommand Command,
        PreviewSessionHeader Header, CommandAuditFact StartFact);
}
