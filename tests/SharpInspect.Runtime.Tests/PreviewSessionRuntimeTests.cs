using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

public sealed partial class RecipeActivationServiceTests
{
    [Fact]
    public async Task V133_P01_NoActivePreviewTunesFreezesSavesAndClosesWithoutProductionWork()
    {
        await using var harness = await ActivationHarness.CreateAsync(enablePreview: true);
        var invocation = harness.ActivationInvocation();
        await WaitForPreviewIdleAsync(harness, invocation);

        var draft = PreviewDraftReference.FromRevision(harness.Source);
        var sessionId = Guid.NewGuid();
        var start = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            draft, expectedActive: null, "V133 P01 start preview");
        AssertAccepted(await harness.Runtime.SubmitAsync(start));

        var streaming = await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.LatestFrame is not null, "PreviewFrameUnavailable");
        Assert.Equal(sessionId, streaming.LatestFrame!.SessionId);
        Assert.Equal(640, streaming.LatestFrame.Width);
        Assert.Equal(480, streaming.LatestFrame.Height);
        Assert.Equal(640, streaming.LatestFrame.StrideBytes);

        var tunedConfiguration = new PreviewTuningConfiguration(
            new PreviewCameraProcessSettings(50, 5, harness.Source.Content.Camera.RegionOfInterest,
                harness.Source.Content.Camera.PixelFormat, harness.Source.Content.Camera.ValidBits),
            PreviewAutomaticControlMode.Once, PreviewAutomaticControlMode.Once);
        var tune = new ApplyPreviewTuningCommand(Guid.NewGuid(), invocation, sessionId,
            tunedConfiguration, "V133 P01 tune preview controls");
        AssertAccepted(await harness.Runtime.SubmitAsync(tune));
        var tuned = await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.Configuration is { ExposureMode: PreviewAutomaticControlMode.Once,
                    GainMode: PreviewAutomaticControlMode.Once } configuration &&
                configuration.ProcessSettings.ExposureTimeUs == 50 &&
                configuration.ProcessSettings.GainDb == 5, "PreviewTuneReadBackUnavailable");

        var freeze = new FreezePreviewSettingsCommand(Guid.NewGuid(), invocation, sessionId,
            "V133 P01 freeze preview controls");
        AssertAccepted(await harness.Runtime.SubmitAsync(freeze));
        var frozen = await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.FrozenSettings is not null && snapshot.Configuration is
                { ExposureMode: PreviewAutomaticControlMode.Off,
                  GainMode: PreviewAutomaticControlMode.Off,
                  WhiteBalanceMode: PreviewAutomaticControlMode.Off },
            "PreviewFreezeReadBackUnavailable");
        var frozenSettings = frozen.FrozenSettings!;

        var save = new SavePreviewToDraftCommand(Guid.NewGuid(), invocation, sessionId, draft,
            frozenSettings.ContentHash, "V133 P01 save frozen preview settings");
        var saveOutcome = await harness.Runtime.SubmitAsync(save);
        AssertAccepted(saveOutcome);
        Assert.Equal("PreviewDraftSaved", saveOutcome.ReasonCode);
        var saved = await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.LastSavedDraft is not null, "PreviewDraftSaveUnavailable");
        var savedReference = saved.LastSavedDraft!;
        var savedResult = await harness.Drafts.ReadAsync(savedReference.DraftId,
            savedReference.Revision);
        Assert.True(savedResult.Available, savedResult.ReasonCode);
        var savedRevision = Assert.IsType<RecipeDraftRevision>(savedResult.Revision);
        Assert.Equal(harness.Source.Content.Camera.ProductionAcquisitionMode,
            savedRevision.Content.Camera.ProductionAcquisitionMode);
        Assert.Equal(harness.Source.Content.Camera.AcquisitionTimeoutMs,
            savedRevision.Content.Camera.AcquisitionTimeoutMs);
        Assert.Equal(harness.Source.Content.Camera.TriggerDelayUs,
            savedRevision.Content.Camera.TriggerDelayUs);
        Assert.Equal(frozenSettings.ExposureTimeUs, savedRevision.Content.Camera.ExposureTimeUs);
        Assert.Equal(frozenSettings.GainDb, savedRevision.Content.Camera.GainDb);

        var previewHistory = new SqlitePreviewSessionQuery(harness.Options);
        var persistedHistory = await previewHistory.QueryAsync(
            new PreviewSessionHistoryFilter(sessionId, PageSize: 128));
        Assert.True(persistedHistory.Available, persistedHistory.ReasonCode);
        Assert.DoesNotContain(persistedHistory.Events,
            sessionEvent => sessionEvent.Phase == PreviewSessionPhase.RecoveryBlocked);
        var savedEvent = Assert.Single(persistedHistory.Events, sessionEvent =>
            sessionEvent.Phase == PreviewSessionPhase.SavingDraft &&
            sessionEvent.ReasonCode == "PreviewDraftSaved");
        Assert.NotNull(savedEvent.SavedDraft);
        var eventDraft = savedEvent.SavedDraft!;
        Assert.Equal(savedReference.DraftId, eventDraft.DraftId);
        Assert.Equal(savedReference.Revision, eventDraft.Revision);
        Assert.Equal(savedReference.RevisionContentHash, eventDraft.RevisionContentHash);

        var exit = new ExitPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            cancel: false, "V133 P01 exit preview");
        AssertAccepted(await harness.Runtime.SubmitAsync(exit));
        var closed = await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Closed &&
                snapshot.Restoration == PreviewRestorationState.NoActiveBaselineClosed,
            "PreviewSafeCloseUnavailable");
        Assert.Equal(PreviewRestorationState.NoActiveBaselineClosed, closed.Restoration);

        var station = await harness.Runtime.GetSnapshotAsync();
        Assert.Equal(ExclusiveMode.None, station.Mode);
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.False(station.Ready);
        Assert.Null(station.ActiveRecipe);
        Assert.Null(station.CurrentExecution);
        Assert.True(harness.CameraProvider.ActivationPreviewDeviceDisposed);
        Assert.Equal(1, harness.CameraProvider.ActivationPreviewStartCount);
        Assert.True(harness.CameraProvider.ActivationPreviewStopCount >= 1);
        Assert.Equal(1, harness.CameraProvider.ActivationApplyCount);
        Assert.True(harness.CameraProvider.ActivationPreviewReadCount >= 1);
        Assert.Equal(tunedConfiguration.ContentHash, tuned.Configuration!.ContentHash);
    }

    [Fact]
    public async Task V133_P02_InternalDevelopmentActiveBaselineIsRestoredAfterPreview()
    {
        await using var harness = await ActivationHarness.CreateAsync(enablePreview: true);
        var activation = await harness.CreateFixtureService().ActivateAsync(
            await harness.AuthorizedActivationCommand());
        Assert.Equal(CommandDisposition.Accepted, activation.Outcome.Disposition);
        Assert.Equal(RecipeActivationOutcomeState.Succeeded, activation.Record!.Outcome.State);
        var baseline = Assert.IsType<RecipeActivationRecord>(activation.Record);
        Assert.True(baseline.DevelopmentOnly);
        Assert.False(baseline.ProductionAuthority);
        Assert.NotNull(baseline.SuccessfulSnapshot);
        await harness.WaitForVerifiedAsync();

        var invocation = harness.ActivationInvocation();
        await WaitForPreviewIdleAsync(harness, invocation);
        var sessionId = Guid.NewGuid();
        var start = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            PreviewDraftReference.FromRevision(harness.Source), baseline.Reference,
            "V133 P02 start over exact internal baseline");
        AssertAccepted(await harness.Runtime.SubmitAsync(start));
        await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.LatestFrame is not null, "PreviewBaselineFrameUnavailable");

        var exit = new ExitPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            cancel: false, "V133 P02 restore exact internal baseline");
        AssertAccepted(await harness.Runtime.SubmitAsync(exit));
        var closed = await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Closed &&
                snapshot.Restoration == PreviewRestorationState.Restored,
            "PreviewBaselineRestoreUnavailable");
        Assert.Equal(PreviewRestorationState.Restored, closed.Restoration);

        var setup = await harness.Camera.GetSetupAsync("TopCamera", invocation);
        Assert.True(setup.Available, setup.ReasonCode);
        Assert.Equal(baseline.SuccessfulSnapshot!.CameraSetup.Effective,
            setup.Snapshot!.Effective);
        var station = await harness.Runtime.GetSnapshotAsync();
        Assert.Equal(ExclusiveMode.None, station.Mode);
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.False(station.Ready);
        Assert.Null(station.ActiveRecipe);
        Assert.True(harness.CameraProvider.ReplacementDeviceDisposed);
        Assert.True(harness.CameraProvider.RestoreApplyCalls > 0);
    }

    [Fact]
    public async Task V133_P03_CallerCancellationAfterFrameSafelyRestoresAndNeverEnablesProduction()
    {
        await using var harness = await ActivationHarness.CreateAsync(enablePreview: true);
        var invocation = harness.ActivationInvocation();
        await WaitForPreviewIdleAsync(harness, invocation);
        using var callerCancellation = new CancellationTokenSource();
        var sessionId = Guid.NewGuid();
        var start = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            PreviewDraftReference.FromRevision(harness.Source), null,
            "V133 P03 cancellable preview");
        AssertAccepted(await harness.Runtime.SubmitAsync(start, callerCancellation.Token));
        await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.LatestFrame is not null, "PreviewCancellationFrameUnavailable");

        callerCancellation.Cancel();
        var closed = await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Closed &&
                snapshot.Restoration == PreviewRestorationState.NoActiveBaselineClosed,
            "PreviewCancellationRestoreUnavailable");
        Assert.Equal("PreviewCancelled", closed.ReasonCode);
        Assert.True(harness.CameraProvider.ActivationPreviewDeviceDisposed);
        var station = await harness.Runtime.GetSnapshotAsync();
        Assert.Equal(ExclusiveMode.None, station.Mode);
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.False(station.Ready);
        Assert.Null(station.ActiveRecipe);
    }

    [Fact]
    public async Task V133_P04_LogoutRevokesReaderAndSafelyClosesPreview()
    {
        await using var harness = await ActivationHarness.CreateAsync(enablePreview: true);
        var invocation = harness.ActivationInvocation();
        await WaitForPreviewIdleAsync(harness, invocation);
        var sessionId = Guid.NewGuid();
        var start = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            PreviewDraftReference.FromRevision(harness.Source), null,
            "V133 P04 logout preview");
        AssertAccepted(await harness.Runtime.SubmitAsync(start));
        await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.LatestFrame is not null, "PreviewLogoutFrameUnavailable");

        var logout = await harness.Sessions.LogoutAsync(invocation.SessionId);
        Assert.True(logout.Succeeded, logout.ReasonCode);
        await WaitForStationAsync(harness,
            snapshot => snapshot.Mode == ExclusiveMode.None &&
                harness.CameraProvider.ActivationPreviewDeviceDisposed,
            "PreviewLogoutCloseUnavailable");

        var station = await harness.Runtime.GetSnapshotAsync();
        Assert.Equal(InteractiveSessionState.Unauthenticated, station.Session.State);
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.False(station.Ready);
        Assert.Null(station.ActiveRecipe);
    }

    [Fact]
    public async Task V133_P05_RestoreFailureLatchesPreviewRecoveryAlarmAndBlocksProduction()
    {
        await using var harness = await ActivationHarness.CreateAsync(enablePreview: true);
        var invocation = harness.ActivationInvocation();
        await WaitForPreviewIdleAsync(harness, invocation);
        var sessionId = Guid.NewGuid();
        var start = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            PreviewDraftReference.FromRevision(harness.Source), null,
            "V133 P05 preview restore failure");
        AssertAccepted(await harness.Runtime.SubmitAsync(start));
        await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.LatestFrame is not null, "PreviewRestoreFailureFrameUnavailable");

        harness.CameraProvider.ActivationPreviewDeviceFailDispose = true;
        try
        {
            var exit = new ExitPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
                cancel: false, "V133 P05 force camera restore failure");
            AssertAccepted(await harness.Runtime.SubmitAsync(exit));
            var blocked = await WaitForPreviewAsync(harness, invocation, sessionId,
                snapshot => snapshot.Phase == PreviewSessionPhase.RecoveryBlocked &&
                    snapshot.Restoration == PreviewRestorationState.RecoveryBlocked,
                "PreviewRecoveryBlockUnavailable");
            Assert.True(blocked.RecoveryRequired);

            var alarmed = await WaitForStationAsync(harness,
                snapshot => snapshot.AlarmState?.Instances.Any(instance =>
                    instance.Code == "PreviewRecoveryRequired" &&
                    instance.Source == "Runtime.Preview" && instance.IsLatched &&
                    instance.ProductionImpact == ProductionImpact.BlockNewTriggers) == true,
                "PreviewRecoveryAlarmUnavailable");
            Assert.NotNull(alarmed.AlarmState);
            var alarm = alarmed.AlarmState!;
            Assert.Contains(alarm.Instances, instance => instance.Code == "PreviewRecoveryRequired" &&
                instance.Source == "Runtime.Preview" && instance.IsLatched);
            Assert.Equal(ProductionImpact.BlockNewTriggers,
                harness.Options.AlarmPolicy!.Rules.Single(rule => rule.Code == "PreviewRecoveryRequired")
                    .ProductionImpact);
            Assert.Contains(AlarmResetPrerequisites.RecoveryComplete.ToString(),
                harness.Options.AlarmPolicy.Rules.Single(rule => rule.Code == "PreviewRecoveryRequired")
                    .ResetPrerequisites.ToString(), StringComparison.Ordinal);
            Assert.Contains(AlarmResetPrerequisites.NoActiveExecution.ToString(),
                harness.Options.AlarmPolicy.Rules.Single(rule => rule.Code == "PreviewRecoveryRequired")
                    .ResetPrerequisites.ToString(), StringComparison.Ordinal);
            var station = await harness.Runtime.GetSnapshotAsync();
            Assert.False(station.Ready);
            Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        }
        finally
        {
            harness.CameraProvider.ActivationPreviewDeviceFailDispose = false;
        }
    }

    [Fact]
    public async Task V133_P06_DuplicateStartCorrelationDoesNotRepeatCandidateHardware()
    {
        await using var harness = await ActivationHarness.CreateAsync(enablePreview: true);
        var invocation = harness.ActivationInvocation();
        await WaitForPreviewIdleAsync(harness, invocation);
        var sessionId = Guid.NewGuid();
        var start = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            PreviewDraftReference.FromRevision(harness.Source), null,
            "V133 P06 duplicate preview start");
        var first = await harness.Runtime.SubmitAsync(start);
        AssertAccepted(first);
        await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.LatestFrame is not null, "PreviewDuplicateInitialFrameUnavailable");
        var applyBeforeDuplicate = harness.CameraProvider.ActivationApplyCount;
        var startsBeforeDuplicate = harness.CameraProvider.ActivationPreviewStartCount;
        var duplicate = await harness.Runtime.SubmitAsync(start);
        Assert.Equal(CommandDisposition.Rejected, duplicate.Disposition);
        Assert.Equal(applyBeforeDuplicate, harness.CameraProvider.ActivationApplyCount);
        Assert.Equal(startsBeforeDuplicate, harness.CameraProvider.ActivationPreviewStartCount);

        await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.LatestFrame is not null, "PreviewDuplicateFrameUnavailable");
        Assert.Equal(1, harness.CameraProvider.ActivationPreviewStartCount);
        var exit = new ExitPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            cancel: false, "V133 P06 close duplicate preview");
        AssertAccepted(await harness.Runtime.SubmitAsync(exit));
        await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Closed,
            "PreviewDuplicateCloseUnavailable");
    }

    [Fact]
    public async Task V133_P07_LocalStopDuringApplyPreventsPreviewStartAndSafelyCloses()
    {
        await using var harness = await ActivationHarness.CreateAsync(enablePreview: true);
        var invocation = harness.ActivationInvocation();
        await WaitForPreviewIdleAsync(harness, invocation);
        var sessionId = Guid.NewGuid();
        var start = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            PreviewDraftReference.FromRevision(harness.Source), null,
            "V133 P07 local stop fence preview");
        var localStop = new GracefulProductionStopCommand(Guid.NewGuid(), invocation);
        var releaseApply = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.CameraProvider.HoldActivationApply(() => releaseApply.Task);

        var startTask = harness.Runtime.SubmitAsync(start).AsTask();
        try
        {
            // The provider barrier proves that the admitted candidate Apply has
            // started and is still in flight.  The stop is then admitted while
            // that same physical operation is held.
            await harness.CameraProvider.ActivationApplyStarted.WaitAsync(
                TimeSpan.FromSeconds(15));
            var startOutcome = await startTask.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(CommandDisposition.Accepted, startOutcome.Disposition);
            Assert.Equal(AuditPersistence.Persisted, startOutcome.Audit);
            var stopTask = harness.Runtime.SubmitAsync(localStop).AsTask();
            var stopOutcome = await stopTask.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(CommandDisposition.Accepted, stopOutcome.Disposition);
            Assert.Equal(AuditPersistence.Persisted, stopOutcome.Audit);
            await WaitForPreviewAsync(harness, invocation, sessionId,
                snapshot => snapshot.Phase == PreviewSessionPhase.Restoring,
                "PreviewLocalStopFenceUnavailable");

            // Restoring is the durable/runtime fence: once observed, the held
            // Apply may retire, but the stopped session must not enter Start.
            releaseApply.TrySetResult(true);

            var closed = await WaitForPreviewAsync(harness, invocation, sessionId,
                snapshot => snapshot.Phase == PreviewSessionPhase.Closed &&
                    snapshot.Restoration == PreviewRestorationState.NoActiveBaselineClosed,
                "PreviewLocalStopTerminalUnavailable");
            Assert.Equal(PreviewRestorationState.NoActiveBaselineClosed, closed.Restoration);
            Assert.Equal(1, harness.CameraProvider.ActivationApplyCount);
            Assert.Equal(0, harness.CameraProvider.ActivationPreviewStartCount);
            Assert.True(harness.CameraProvider.ActivationPreviewDeviceDisposed);

            var station = await WaitForStationAsync(harness,
                snapshot => snapshot.Mode == ExclusiveMode.None,
                "PreviewLocalStopStationRecoveryUnavailable");
            Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
            Assert.False(station.Ready);
            Assert.Null(station.ActiveRecipe);
        }
        finally
        {
            // Also release the provider if an assertion or timeout occurs so
            // fixture disposal cannot leave the fake operation retained.
            releaseApply.TrySetResult(true);
        }
    }

    [Fact]
    public async Task V133_P08_ProviderCancellationWithoutCallerCancellationCannotLeaveStreamingOrphan()
    {
        await using var harness = await ActivationHarness.CreateAsync(enablePreview: true);
        var invocation = harness.ActivationInvocation();
        await WaitForPreviewIdleAsync(harness, invocation);
        var sessionId = Guid.NewGuid();
        var start = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            PreviewDraftReference.FromRevision(harness.Source), null,
            "V133 P08 provider-owned cancellation preview");
        harness.CameraProvider.ActivationPreviewReadThrowsOperationCanceled = true;

        try
        {
            AssertAccepted(await harness.Runtime.SubmitAsync(start));
            var terminal = await WaitForPreviewAsync(harness, invocation, sessionId,
                snapshot => snapshot.Phase is PreviewSessionPhase.Closed or
                    PreviewSessionPhase.RecoveryBlocked,
                "PreviewProviderCancellationTerminalUnavailable");

            Assert.True(terminal.Phase is PreviewSessionPhase.Closed or
                PreviewSessionPhase.RecoveryBlocked);
            Assert.NotEqual(PreviewSessionPhase.Streaming, terminal.Phase);
            Assert.True(harness.CameraProvider.ActivationPreviewReadObservedUncancelledToken);
            Assert.True(harness.CameraProvider.ActivationPreviewStopCount > 0);
            Assert.True(harness.CameraProvider.ActivationPreviewDeviceDisposed);

            await harness.WaitForVerifiedAsync();
            var history = new SqlitePreviewSessionQuery(harness.Options);
            var page = await history.QueryAsync(new PreviewSessionHistoryFilter(sessionId,
                PageSize: 128));
            Assert.True(page.Available, page.ReasonCode);
            var startTerminal = Assert.Single(page.Events, sessionEvent =>
                sessionEvent.CommandKind == AuditedCommandKind.StartPreview &&
                sessionEvent.Terminal);
            Assert.True(startTerminal.Phase is PreviewSessionPhase.Closed or
                PreviewSessionPhase.RecoveryBlocked);
            Assert.Equal(terminal.Phase, startTerminal.Phase);
        }
        finally
        {
            harness.CameraProvider.ActivationPreviewReadThrowsOperationCanceled = false;
        }
    }

    private static AuthorizationPolicy CreatePreviewAuthorizationPolicy()
    {
        var baseline = RecipeDraftTestPolicies.Authoring;
        var roles = baseline.RoleBundles.ToDictionary(pair => pair.Key,
            pair => pair.Key == HumanRoleBundle.Administrator
                ? pair.Value.Append(Permission.RunPreview)
                : pair.Value.AsEnumerable());
        return new AuthorizationPolicy("v133-preview", "v133-preview-2026-09-v1", roles,
            baseline.StepUpPermissions);
    }

    private static AlarmPolicy CreatePreviewAlarmPolicy() => new("v133-preview", "1",
        new[]
        {
            new AlarmPolicyRule("StartupRecoveryRequired", "Runtime.StartupRecovery",
                AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null, ResetPrerequisites:
                    AlarmResetPrerequisites.RecoveryComplete |
                    AlarmResetPrerequisites.NoPendingDelivery),
            new AlarmPolicyRule("FrameBufferExhausted", "Runtime.FrameBufferPool",
                AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null, ResetPrerequisites:
                    AlarmResetPrerequisites.RecoveryComplete |
                    AlarmResetPrerequisites.NoActiveExecution),
            new AlarmPolicyRule("PreviewRecoveryRequired", "Runtime.Preview",
                AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null, ResetPrerequisites:
                    AlarmResetPrerequisites.RecoveryComplete |
                    AlarmResetPrerequisites.NoActiveExecution)
        }, TimeSpan.FromMinutes(1));

    private static void AssertAccepted(RuntimeCommandOutcome outcome)
    {
        Assert.True(outcome.Disposition == CommandDisposition.Accepted,
            $"{outcome.ReasonCode} (disposition={outcome.Disposition}, audit={outcome.Audit})");
        Assert.Equal(AuditPersistence.Persisted, outcome.Audit);
    }

    private static async Task WaitForPreviewIdleAsync(ActivationHarness harness,
        CommandInvocation invocation)
    {
        var service = (IPreviewSessionService)harness.Runtime;
        var deadline = Stopwatch.GetTimestamp() +
            (long)(Stopwatch.Frequency * TimeSpan.FromSeconds(15).TotalSeconds);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var read = await service.GetSnapshotAsync(invocation);
            Assert.True(read.Available, read.ReasonCode);
            Assert.NotNull(read.Snapshot);
            if (read.Snapshot!.Phase == PreviewSessionPhase.Idle &&
                read.Snapshot.PreviewSessionId is null) return;
            Assert.NotEqual(PreviewSessionPhase.RecoveryBlocked, read.Snapshot!.Phase);
            await Task.Delay(25);
        }
        throw new XunitException("PreviewStartupFenceTimeout");
    }

    private static async Task<PreviewSessionSnapshot> WaitForPreviewAsync(
        ActivationHarness harness, CommandInvocation invocation, Guid sessionId,
        Func<PreviewSessionSnapshot, bool> predicate, string timeoutReason)
    {
        var service = (IPreviewSessionService)harness.Runtime;
        var deadline = Stopwatch.GetTimestamp() +
            (long)(Stopwatch.Frequency * TimeSpan.FromSeconds(30).TotalSeconds);
        PreviewSessionSnapshot? last = null;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var read = await service.GetSnapshotAsync(invocation);
            Assert.True(read.Available, read.ReasonCode);
            Assert.NotNull(read.Snapshot);
            var snapshot = read.Snapshot!;
            last = snapshot;
            if (snapshot.PreviewSessionId == sessionId && predicate(snapshot)) return snapshot;
            if (snapshot.PreviewSessionId == sessionId &&
                snapshot.Phase == PreviewSessionPhase.RecoveryBlocked)
                throw new XunitException(timeoutReason + ":" + snapshot.ReasonCode);
            await Task.Delay(25);
        }
        throw new XunitException($"{timeoutReason}: {last?.Phase}/{last?.ReasonCode}");
    }

    private static async Task<StationStateSnapshot> WaitForStationAsync(
        ActivationHarness harness, Func<StationStateSnapshot, bool> predicate,
        string timeoutReason)
    {
        var deadline = Stopwatch.GetTimestamp() +
            (long)(Stopwatch.Frequency * TimeSpan.FromSeconds(30).TotalSeconds);
        StationStateSnapshot? last = null;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            last = await harness.Runtime.GetSnapshotAsync();
            if (predicate(last)) return last;
            await Task.Delay(25);
        }
        throw new XunitException(timeoutReason + ":" + last?.LastCommand?.ReasonCode);
    }

    private sealed partial class VirtualCameraProvider
    {
        internal int ActivationApplyCount => _activationDevice.ApplyCount;
        internal int ActivationPreviewStartCount => _activationDevice.PreviewStartCalls;
        internal int ActivationPreviewStopCount => _activationDevice.PreviewStopCalls;
        internal int ActivationPreviewReadCount => _activationDevice.PreviewReadCalls;
        internal bool ActivationPreviewDeviceDisposed => _activationDevice.IsDisposed;
        internal bool ActivationPreviewDeviceFailDispose
        {
            get => _activationDevice.FailDispose;
            set => _activationDevice.FailDispose = value;
        }
        internal bool ActivationPreviewReadThrowsOperationCanceled
        {
            get => _activationDevice.ThrowPreviewReadOperationCanceled;
            set => _activationDevice.ThrowPreviewReadOperationCanceled = value;
        }
        internal bool ActivationPreviewReadObservedUncancelledToken =>
            _activationDevice.PreviewReadObservedUncancelledToken;
        internal int PreviewStartCount => _all.Sum(device => device.PreviewStartCalls);
        internal int PreviewStopCount => _all.Sum(device => device.PreviewStopCalls);
    }

    private sealed partial class VirtualCameraDevice
    {
        private readonly object _previewSync = new();
        private bool _previewing;
        private Guid _previewSessionId;
        private long _previewSequence = -1;
        private PreviewTuningConfiguration? _previewConfiguration;
        private int _previewStartCalls;
        private int _previewTuneCalls;
        private int _previewFreezeCalls;
        private int _previewReadCalls;
        private int _previewStopCalls;
        private int _previewReadThrowsOperationCanceled;
        private int _previewReadObservedUncancelledToken;

        internal bool FailDispose { get; set; }
        internal int PreviewStartCalls => Volatile.Read(ref _previewStartCalls);
        internal int PreviewTuneCalls => Volatile.Read(ref _previewTuneCalls);
        internal int PreviewFreezeCalls => Volatile.Read(ref _previewFreezeCalls);
        internal int PreviewReadCalls => Volatile.Read(ref _previewReadCalls);
        internal int PreviewStopCalls => Volatile.Read(ref _previewStopCalls);
        internal bool ThrowPreviewReadOperationCanceled
        {
            get => Volatile.Read(ref _previewReadThrowsOperationCanceled) != 0;
            set => Volatile.Write(ref _previewReadThrowsOperationCanceled, value ? 1 : 0);
        }
        internal bool PreviewReadObservedUncancelledToken =>
            Volatile.Read(ref _previewReadObservedUncancelledToken) != 0;
        public CameraPreviewCapabilities PreviewCapabilities { get; } =
            new(true, true, true, false);

        internal CameraAcquisitionState GetPreviewAcquisitionState()
        {
            lock (_previewSync) return _previewing
                ? CameraAcquisitionState.Previewing : CameraAcquisitionState.Stopped;
        }

        internal void StopPreviewState()
        {
            lock (_previewSync)
            {
                _previewing = false;
                _previewSessionId = Guid.Empty;
                _previewConfiguration = null;
            }
        }

        internal void DisposePreviewState() => StopPreviewState();

        public ValueTask<CameraPreviewConfigurationResult> StartPreviewAsync(Guid sessionId,
            PreviewTuningConfiguration configuration, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _previewStartCalls);
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                    "PreviewOperationCancelled"));
            if (sessionId == Guid.Empty)
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                    "PreviewSessionIdInvalid"));
            if (_disposed)
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                    "T33VirtualCameraClosed"));
            lock (_previewSync)
            {
                if (_previewing)
                    return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                        "PreviewAlreadyStarted"));
                _previewing = true;
                _previewSessionId = sessionId;
                _previewSequence = -1;
                _previewConfiguration = configuration;
            }
            return ValueTask.FromResult(CameraPreviewConfigurationResult.Success(configuration));
        }

        public ValueTask<CameraPreviewConfigurationResult> ApplyPreviewTuningAsync(
            PreviewTuningConfiguration configuration, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _previewTuneCalls);
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                    "PreviewOperationCancelled"));
            lock (_previewSync)
            {
                if (!_previewing)
                    return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                        "PreviewNotStarted"));
                _previewConfiguration = configuration;
            }
            return ValueTask.FromResult(CameraPreviewConfigurationResult.Success(configuration));
        }

        public ValueTask<CameraPreviewConfigurationResult> FreezeFixedPreviewConfigurationAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _previewFreezeCalls);
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                    "PreviewOperationCancelled"));
            lock (_previewSync)
            {
                if (!_previewing || _previewConfiguration is null)
                    return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                        "PreviewNotStarted"));
                var fixedConfiguration = new PreviewTuningConfiguration(
                    _previewConfiguration.ProcessSettings);
                _previewConfiguration = fixedConfiguration;
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Success(
                    fixedConfiguration));
            }
        }

        public ValueTask<CameraPreviewFrameResult> ReadLatestPreviewFrameAsync(long afterSequence,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _previewReadCalls);
            if (TryHoldNextPreviewReadUntilCancellation(cancellationToken, out var cancellationRead))
                return cancellationRead;
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromResult(CameraPreviewFrameResult.Failure(
                    "PreviewOperationCancelled"));
            if (Interlocked.Exchange(ref _previewReadThrowsOperationCanceled, 0) != 0)
            {
                if (!cancellationToken.IsCancellationRequested)
                    Volatile.Write(ref _previewReadObservedUncancelledToken, 1);
                throw new OperationCanceledException("V133 provider-owned cancellation");
            }
            Guid sessionId;
            long sequence;
            lock (_previewSync)
            {
                if (!_previewing)
                    return ValueTask.FromResult(CameraPreviewFrameResult.Failure(
                        "PreviewNotStarted"));
                if (afterSequence == long.MaxValue)
                    return ValueTask.FromResult(CameraPreviewFrameResult.Failure(
                        "PreviewFrameSequenceInvalid"));
                sequence = Math.Max(_previewSequence, afterSequence) + 1;
                _previewSequence = sequence;
                sessionId = _previewSessionId;
            }
            var pixels = new byte[640 * 480];
            var frame = new CameraPreviewFrame(sessionId, sequence,
                new FrameTimePoint(DateTimeOffset.UtcNow,
                    Math.Max(0, Stopwatch.GetTimestamp())), 640, 480, 640,
                VisionPixelFormat.Mono8, null, pixels);
            return ValueTask.FromResult(CameraPreviewFrameResult.Success(frame));
        }

        public ValueTask<CameraOperationResult> StopPreviewAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _previewStopCalls);
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromResult(CameraOperationResult.Failure(
                    "PreviewOperationCancelled"));
            lock (_previewSync)
            {
                _previewing = false;
                _previewSessionId = Guid.Empty;
                _previewConfiguration = null;
            }
            return ValueTask.FromResult(CameraOperationResult.Success("PreviewStopped"));
        }
    }
}
