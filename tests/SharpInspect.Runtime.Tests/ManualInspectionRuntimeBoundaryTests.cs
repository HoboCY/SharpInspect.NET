using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Manual;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V135_M07_AvailableReleasedRecipeRunsWithoutBecomingActive()
    {
        await using var harness = await ManualHarness.CreateAsync();
        var fixture = harness.Fixture;
        var releaseCommand = new ReleaseRecipeCommand(Guid.NewGuid(), harness.Invocation(),
            harness.Draft.DraftId, harness.Draft.Revision, harness.Draft.RevisionContentHash,
            fixture.Options.RecipeReleases!.Policy.Reference, "V135 release manual source");
        var grant = await fixture.Authorization.ReauthenticateAsync(new(Guid.NewGuid(),
            harness.Invocation(), new(Permission.ReleaseRecipe, releaseCommand.CorrelationId,
                releaseCommand.AuthorizationTarget, AuditedCommandKind.ReleaseRecipe), fixture.Password));
        Assert.True(grant.Succeeded, grant.ReasonCode);
        var released = await harness.Service<IRecipeReleaseService>().ReleaseAsync(releaseCommand with
        { Invocation = harness.Invocation() with { StepUpGrantId = grant.GrantId } });
        AssertAccepted(released.Outcome, "Release manual source");
        var source = Assert.IsType<ReleasedRecipe>(released.Recipe);
        var selection = ManualRecipeSelection.FromReleased(source);
        AssertAccepted(await harness.Runtime.SubmitAsync(new StartManualInspectionSessionCommand(
            Guid.NewGuid(), harness.Invocation(), selection, null, "V135 manual released selection")),
            "Manual released start");
        var ready = await harness.WaitForSnapshotAsync(value =>
            value.Phase == ManualInspectionSessionPhase.ReadyForRun, "Released source not ready");
        var session = ready.SessionId!.Value;
        AssertAccepted(await harness.RunAsync(session), "Released source run");
        await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Released run did not finish");
        AssertAccepted(await harness.ExitAsync(session, ManualInspectionExitMode.Graceful), "Released exit");
        await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.Closed,
            "Released session did not close");
        var page = await harness.History.QueryAsync(new(SessionId: session));
        Assert.True(page.Available, page.ReasonCode);
        Assert.Equal(selection, page.Events[0].Header.Selection);
        Assert.Equal(ExecutionStatus.Success, Assert.Single(page.Runs).ExecutionStatus);
        var active = await harness.Activations.ReadCurrentAsync();
        Assert.True(active.Available, active.ReasonCode);
        Assert.Null(active.Record);
        Assert.Null((await harness.Runtime.GetSnapshotAsync()).ActiveRecipe);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task V135_M08_ColdRecoveryClosesAcceptedRunAndExitWithoutManufacturingSuccess(bool cameraAvailable)
    {
        await using var harness = await ManualHarness.CreateAsync();
        var fixture = harness.Fixture;
        var before = await harness.Runtime.GetSnapshotAsync();
        var binding = await fixture.Store.ReadCameraSetupAsync(LogicalRole);
        Assert.True(binding.Result.Available, binding.Result.ReasonCode);
        var input = new ManualInspectionAdmissionInput(harness.Draft, null, binding.State, null);
        var start = new StartManualInspectionSessionCommand(Guid.NewGuid(), harness.Invocation(),
            ManualRecipeSelection.FromDraft(harness.Draft), null, "V135 cold admission start");
        var admitted = await fixture.Authorization.HandleManualInspectionCommandAsync(start,
            before.RuntimeEpoch, Guid.NewGuid(), input, null, null,
            new StoreDeadline(fixture.Store.CommitTimeout), CancellationToken.None);
        AssertAccepted(admitted.Outcome, "Cold start admission");
        var header = Assert.IsType<ManualInspectionSessionHeader>(admitted.Header);
        foreach (var phase in new[] { ManualInspectionSessionPhase.Preparing, ManualInspectionSessionPhase.ReadyForRun })
        {
            var progressed = await fixture.Authorization.CompleteManualInspectionCommandAsync(start,
                header, admitted.CommandFact!, new(true, "ManualInspectionPreparationComplete", phase,
                    ManualInspectionRestorationState.Pending), new StoreDeadline(fixture.Store.CommitTimeout));
            AssertAccepted(progressed.Outcome, "Cold preparation progress");
            header = Assert.IsType<ManualInspectionSessionHeader>(progressed.Header);
        }
        var run = new RunManualInspectionCommand(Guid.NewGuid(), harness.Invocation(), header.SessionId,
            new ManualPartIdentityInput("Manual-Cold-Part"), "V135 cold accepted run");
        var acceptedRun = await fixture.Authorization.HandleManualInspectionCommandAsync(run,
            before.RuntimeEpoch, Guid.NewGuid(), null, header, null,
            new StoreDeadline(fixture.Store.CommitTimeout), CancellationToken.None);
        AssertAccepted(acceptedRun.Outcome, "Cold run admission");
        header = acceptedRun.Header!;
        var exit = new ExitManualInspectionSessionCommand(Guid.NewGuid(), harness.Invocation(),
            header.SessionId, ManualInspectionExitMode.Graceful, "V135 cold accepted exit");
        var acceptedExit = await fixture.Authorization.HandleManualInspectionCommandAsync(exit,
            before.RuntimeEpoch, Guid.NewGuid(), null, header, null,
            new StoreDeadline(fixture.Store.CommitTimeout), CancellationToken.None);
        AssertAccepted(acceptedExit.Outcome, "Cold exit admission");

        // Only command admission was executed. No old Runtime owner can repair
        // these durable pending commands during disposal.
        await ((StationRuntime)harness.Runtime).DisposeAsync();
        var clock = new VirtualCameraClock(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        await using var pump = ClockPump.Start(clock);
        var providers = cameraAvailable
            ? new ICameraProvider[] { ManualHarness.CreateProvider(clock, false) }
            : Array.Empty<ICameraProvider>();
        await using var restarted = CreateRestartedManualRuntime(harness, providers, clock);
        await restarted.WaitForManualInspectionStartupAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var snapshot = await ((IManualInspectionSessionService)restarted).GetSnapshotAsync(harness.Invocation());
        Assert.True(snapshot.Available, snapshot.ReasonCode);
        Assert.Equal(cameraAvailable ? ManualInspectionSessionPhase.Closed : ManualInspectionSessionPhase.RecoveryBlocked,
            snapshot.Snapshot!.Phase);
        Assert.Equal(!cameraAvailable, snapshot.Snapshot.RecoveryRequired);
        var page = await harness.History.QueryAsync(new(SessionId: header.SessionId));
        Assert.True(page.Available, page.ReasonCode);
        var terminal = Assert.Single(page.Runs);
        Assert.True(terminal.Terminal);
        Assert.Equal(acceptedRun.Run!.RunId, terminal.RunId);
        Assert.Equal(run.CorrelationId, terminal.CommandCorrelationId);
        Assert.Equal(ExecutionStatus.Cancelled, terminal.ExecutionStatus);
        Assert.Equal(InspectionDecision.Unknown, terminal.Decision);
        Assert.Equal("ManualInspectionInterruptedByRestart", terminal.ReasonCode);
        Assert.Null(terminal.Result);
        Assert.Null(terminal.FrameOverlay);
        Assert.Null(terminal.FrameMetadata);
        Assert.Equal(ManualInspectionPartIdentitySource.HumanEntered, terminal.PartIdentitySource);
        Assert.Equal("Manual-Cold-Part", terminal.PartIdentity?.Value);
        var trace = await new SqliteCommandTraceQuery(fixture.Options).QueryAsync(new CommandTraceFilter(PageSize: 128));
        foreach (var correlation in new[] { start.CorrelationId, run.CorrelationId, exit.CorrelationId })
            Assert.Single(trace.Records, fact => fact.CorrelationId == correlation && fact.Phase == CommandAuditPhase.Failed);
        Assert.False((await restarted.GetSnapshotAsync()).Ready);
        Assert.Equal(0, harness.Factory.Created);
    }

    [Fact]
    public async Task V135_M09_ExplicitStepUpPolicyProtectsStartRunAndExit()
    {
        await using var harness = await ManualHarness.CreateAsync(requireManualStepUp: true);
        var start = new StartManualInspectionSessionCommand(Guid.NewGuid(), harness.Invocation(),
            ManualRecipeSelection.FromDraft(harness.Draft), null, "V135 step-up start");
        var deniedStart = await harness.Runtime.SubmitAsync(start);
        Assert.Equal(CommandDisposition.Rejected, deniedStart.Disposition);
        Assert.Equal("StepUpRequired", deniedStart.ReasonCode);
        start = new(Guid.NewGuid(), harness.Invocation(), start.Selection, null, start.Reason);
        start = start with { Invocation = await GrantManualAsync(harness, start, AuditedCommandKind.StartManualInspectionSession) };
        AssertAccepted(await harness.Runtime.SubmitAsync(start), "Step-up start");
        var ready = await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Step-up start not ready");
        var session = ready.SessionId!.Value;
        var run = new RunManualInspectionCommand(Guid.NewGuid(), harness.Invocation(), session, null, "V135 step-up run");
        Assert.Equal("StepUpRequired", (await harness.Runtime.SubmitAsync(run)).ReasonCode);
        run = new(Guid.NewGuid(), harness.Invocation(), session, null, run.Reason);
        run = run with { Invocation = await GrantManualAsync(harness, run, AuditedCommandKind.RunManualInspection) };
        AssertAccepted(await harness.Runtime.SubmitAsync(run), "Step-up run");
        await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Step-up run incomplete");
        var exit = new ExitManualInspectionSessionCommand(Guid.NewGuid(), harness.Invocation(), session,
            ManualInspectionExitMode.Graceful, "V135 step-up exit");
        Assert.Equal("StepUpRequired", (await harness.Runtime.SubmitAsync(exit)).ReasonCode);
        exit = new(Guid.NewGuid(), harness.Invocation(), session, exit.Mode, exit.Reason);
        exit = exit with { Invocation = await GrantManualAsync(harness, exit, AuditedCommandKind.ExitManualInspectionSession) };
        AssertAccepted(await harness.Runtime.SubmitAsync(exit), "Step-up exit");
        await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.Closed,
            "Step-up session incomplete");
    }

    private static async Task<CommandInvocation> GrantManualAsync(ManualHarness harness,
        ManualInspectionCommand command, AuditedCommandKind kind)
    {
        var grant = await harness.Fixture.Authorization.ReauthenticateAsync(new(Guid.NewGuid(),
            harness.Invocation(), new(Permission.RunManualInspection, command.CorrelationId,
                command.AuthorizationTarget, kind), harness.Fixture.Password));
        Assert.True(grant.Succeeded, grant.ReasonCode);
        return harness.Invocation() with { StepUpGrantId = grant.GrantId };
    }

    [Fact]
    public async Task V135_M10_LogoutCancelsAcceptedAcquisitionAndRevokesSessionAccess()
    {
        await using var harness = await ManualHarness.CreateAsync(timeout: true);
        AssertAccepted(await harness.StartAsync(), "Logout start");
        var ready = await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Logout session not ready");
        var invocation = harness.Invocation();
        var session = ready.SessionId!.Value;
        harness.PauseClock();
        AssertAccepted(await harness.RunAsync(session), "Logout accepted run");
        var logout = await harness.Fixture.Sessions.LogoutAsync(invocation.SessionId);
        Assert.True(logout.Succeeded, logout.ReasonCode);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((await harness.Runtime.GetSnapshotAsync()).Mode != ExclusiveMode.None && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        var station = await harness.Runtime.GetSnapshotAsync();
        Assert.Equal(ExclusiveMode.None, station.Mode);
        Assert.False(station.Ready);
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.False((await harness.Manual.GetSnapshotAsync(invocation)).Available);
        var history = await harness.History.QueryAsync(new(SessionId: session));
        Assert.True(history.Available, history.ReasonCode);
        var run = Assert.Single(history.Runs);
        Assert.True(run.Terminal);
        Assert.Equal(ExecutionStatus.Cancelled, run.ExecutionStatus);
        Assert.Null(run.Result);
        Assert.False(harness.CameraProvider.GetDiagnostics().Devices.Single().IsOpen);
    }

    [Fact]
    public async Task V135_M11_ReplayingRunCorrelationCannotAcquireOrAllocateAnotherRun()
    {
        await using var harness = await ManualHarness.CreateAsync();
        AssertAccepted(await harness.StartAsync(), "Replay start");
        var ready = await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Replay session not ready");
        var session = ready.SessionId!.Value;
        var command = new RunManualInspectionCommand(Guid.NewGuid(), harness.Invocation(), session, null, "V135 replay run");
        var accepted = await harness.Runtime.SubmitAsync(command);
        AssertAccepted(accepted, "Replay first admission");
        await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Replay first run incomplete");
        var replay = await harness.Runtime.SubmitAsync(command);
        Assert.Equal(AuditPersistence.Persisted, replay.Audit);
        AssertAccepted(await harness.ExitAsync(session, ManualInspectionExitMode.Graceful), "Replay exit");
        await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.Closed,
            "Replay session incomplete");
        var history = await harness.History.QueryAsync(new(SessionId: session));
        Assert.True(history.Available, history.ReasonCode);
        Assert.Equal(command.CorrelationId, Assert.Single(history.Runs).CommandCorrelationId);
        Assert.Equal(1, harness.CameraProvider.GetDiagnostics().Devices.Single().FramesProduced);
    }

    [Fact]
    public async Task V135_M12_LocalGracefulStopDrainsTheAcceptedFrameAndClosesAdmission()
    {
        await using var harness = await ManualHarness.CreateAsync();
        AssertAccepted(await harness.StartAsync(), "Local stop start");
        var ready = await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Local stop session not ready");
        var session = ready.SessionId!.Value;
        harness.PauseClock();
        var stopCorrelation = Guid.NewGuid();
        try
        {
            AssertAccepted(await harness.RunAsync(session), "Local stop accepted run");
            AssertAccepted(await harness.Runtime.SubmitAsync(new GracefulProductionStopCommand(stopCorrelation,
                harness.Invocation())), "Local graceful stop");
            var draining = await harness.Manual.GetSnapshotAsync(harness.Invocation());
            Assert.True(draining.Snapshot!.ExitRequested);
            var lateRun = await harness.RunAsync(session);
            Assert.Equal(CommandDisposition.Rejected, lateRun.Disposition);
            var beforeResume = await harness.Runtime.GetSnapshotAsync();
            Assert.Equal(stopCorrelation, beforeResume.LastCommand?.CorrelationId);
            Assert.Equal(OperationState.Pending, beforeResume.LastCommand?.State);
            var beforeResumeTrace = await new SqliteCommandTraceQuery(harness.Fixture.Options)
                .QueryAsync(new CommandTraceFilter(CorrelationId: stopCorrelation, PageSize: 20));
            Assert.Single(beforeResumeTrace.Records, record => record.Phase == CommandAuditPhase.Outcome &&
                record.ReasonCode == "StopAdmitted");
            Assert.DoesNotContain(beforeResumeTrace.Records,
                record => record.Phase == CommandAuditPhase.Completed &&
                    record.ReasonCode == "LocallyDisarmed");
            Assert.Equal(0, harness.CameraProvider.GetDiagnostics().Devices.Single().FramesProduced);
        }
        finally { harness.ResumeClock(); }
        await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.Closed,
            "Local stop did not drain and close");
        CommandTracePage? finalStopTrace = null;
        using (var terminalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            while (true)
            {
                var trace = await new SqliteCommandTraceQuery(harness.Fixture.Options)
                    .QueryAsync(new CommandTraceFilter(CorrelationId: stopCorrelation, PageSize: 20), terminalTimeout.Token);
                finalStopTrace = trace;
                if (trace.Records.Any(record => record.Phase == CommandAuditPhase.Completed)) break;
                await Task.Delay(25, terminalTimeout.Token);
            }
        }
        var stopTerminal = Assert.Single(finalStopTrace!.Records, record => record.Phase == CommandAuditPhase.Completed &&
            record.ReasonCode == "LocallyDisarmed");
        var history = await harness.History.QueryAsync(new(SessionId: session));
        Assert.True(history.Available, history.ReasonCode);
        var closedEvent = Assert.Single(history.Events,
            record => record.Phase == ManualInspectionSessionPhase.Closed);
        var manualTrace = await new SqliteCommandTraceQuery(harness.Fixture.Options)
            .QueryAsync(new CommandTraceFilter(CorrelationId: closedEvent.Header.StartCorrelationId, PageSize: 20));
        var manualTerminal = Assert.Single(manualTrace.Records, record => record.AggregateSequence == 2);
        // The original Start terminal and Closed event commit in one transaction.
        // Ordered fact positions prove Stop completed after that transaction,
        // including the interval after the frame clock resumed.
        Assert.True(stopTerminal.Position > manualTerminal.Position);
        Assert.Equal(ExecutionStatus.Success, Assert.Single(history.Runs).ExecutionStatus);
        Assert.Equal(1, harness.CameraProvider.GetDiagnostics().Devices.Single().FramesProduced);
        var station = await harness.Runtime.GetSnapshotAsync();
        Assert.False(station.Ready);
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.Equal(stopCorrelation, station.LastCommand?.CorrelationId);
        Assert.Equal(OperationState.Completed, station.LastCommand?.State);
        Assert.Equal("LocallyDisarmed", station.LastCommand?.ReasonCode);
    }

    [Fact]
    public async Task V135_M13_ManualDraftRequiresNoReleaseActivationPreviewOrImportLedger()
    {
        await using var harness = await ManualHarness.CreateAsync(minimalStore: true);
        AssertAccepted(await harness.StartAsync(), "Minimal Manual start");
        var ready = await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Minimal Manual session not ready");
        var session = ready.SessionId!.Value;
        AssertAccepted(await harness.RunAsync(session), "Minimal Manual run");
        await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Minimal Manual run incomplete");
        AssertAccepted(await harness.ExitAsync(session, ManualInspectionExitMode.Graceful), "Minimal Manual exit");
        var closed = await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.Closed,
            "Minimal Manual session incomplete");
        Assert.Equal(ManualInspectionRestorationState.NoActiveBaselineClosed, closed.Restoration);
        var history = await harness.History.QueryAsync(new(SessionId: session));
        Assert.True(history.Available, history.ReasonCode);
        Assert.Equal(ExecutionStatus.Success, Assert.Single(history.Runs).ExecutionStatus);
        var audit = await new SharpInspect.Runtime.Integrity.SqliteAuditIntegrityQuery(harness.Fixture.Options)
            .VerifyAsync(new AuditVerificationRequest(0, 200));
        Assert.Equal(AuditIntegrityState.Verified, audit.State);
    }

    [Fact]
    public async Task V135_M14_ClosedStartReplayKeepsOriginalSessionAndRequiresCurrentAuthority()
    {
        await using var harness = await ManualHarness.CreateAsync();
        var command = new StartManualInspectionSessionCommand(Guid.NewGuid(), harness.Invocation(),
            ManualRecipeSelection.FromDraft(harness.Draft), null, "V135 closed start replay");
        var original = await harness.Runtime.SubmitAsync(command);
        AssertAccepted(original, "Closed replay start");
        var ready = await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Closed replay session not ready");
        var session = ready.SessionId!.Value;
        AssertAccepted(await harness.ExitAsync(session, ManualInspectionExitMode.Graceful), "Closed replay exit");
        await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.Closed,
            "Closed replay session incomplete");
        var before = await harness.History.QueryAsync(new());
        Assert.True(before.Available, before.ReasonCode);
        var opens = harness.CameraProvider.GetDiagnostics().Devices.Single().OpenCount;
        var replay = await harness.Runtime.SubmitAsync(command);
        AssertAccepted(replay, "Closed replay outcome");
        Assert.Equal(original.AttemptId, replay.AttemptId);
        var after = await harness.History.QueryAsync(new());
        Assert.True(after.Available, after.ReasonCode);
        Assert.Equal(before.ThroughPosition, after.ThroughPosition);
        Assert.Equal(session, Assert.Single(after.Events.Select(value => value.SessionId).Distinct()));
        Assert.Equal(opens, harness.CameraProvider.GetDiagnostics().Devices.Single().OpenCount);
        Assert.Equal(ExclusiveMode.None, (await harness.Runtime.GetSnapshotAsync()).Mode);

        var logout = await harness.Fixture.Sessions.LogoutAsync(command.Invocation.SessionId);
        Assert.True(logout.Succeeded, logout.ReasonCode);
        var stale = await harness.Runtime.SubmitAsync(command);
        Assert.Equal(CommandDisposition.Rejected, stale.Disposition);
        var final = await harness.History.QueryAsync(new());
        Assert.True(final.Available, final.ReasonCode);
        Assert.Equal(before.ThroughPosition, final.ThroughPosition);
        Assert.Equal(opens, harness.CameraProvider.GetDiagnostics().Devices.Single().OpenCount);
    }

    [Fact]
    public async Task V135_M15_CancellingCallerAfterAcceptedRunDoesNotCancelPhysicalRun()
    {
        await using var harness = await ManualHarness.CreateAsync();
        AssertAccepted(await harness.StartAsync(), "Caller cancellation start");
        var ready = await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Caller cancellation session not ready");
        var session = ready.SessionId!.Value;
        using var cancellation = new CancellationTokenSource();
        harness.PauseClock();
        try
        {
            var command = new RunManualInspectionCommand(Guid.NewGuid(), harness.Invocation(), session,
                null, "V135 cancel only caller wait");
            AssertAccepted(await harness.Runtime.SubmitAsync(command, cancellation.Token), "Caller cancellation run");
            cancellation.Cancel();
            Assert.Equal(0, harness.CameraProvider.GetDiagnostics().Devices.Single().FramesProduced);
        }
        finally { harness.ResumeClock(); }
        await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Accepted run was cancelled with its caller");
        AssertAccepted(await harness.ExitAsync(session, ManualInspectionExitMode.Graceful), "Caller cancellation exit");
        await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.Closed,
            "Caller cancellation session incomplete");
        var history = await harness.History.QueryAsync(new(SessionId: session));
        Assert.True(history.Available, history.ReasonCode);
        Assert.Equal(ExecutionStatus.Success, Assert.Single(history.Runs).ExecutionStatus);
        Assert.Equal(1, harness.CameraProvider.GetDiagnostics().Devices.Single().FramesProduced);
    }

    private static StationRuntime CreateRestartedManualRuntime(ManualHarness harness,
        IReadOnlyList<ICameraProvider> providers, IFrameAcquisitionClock clock,
        SharpInspect.Runtime.Admission.IProductionAdmissionFactsSource? admissionFactsSource = null,
        bool productionDeploymentEvidence = false)
    {
        var fixture = harness.Fixture;
        var restarted = new StationRuntime(fixture.Store, TimeSpan.FromMilliseconds(20),
            fixture.Sessions, fixture.Authorization, harness.Service<FrameBufferPool>(),
            cameraProviders: providers, cameraSetupOptions: new CameraSetupOptions(),
            productionStoreOptions: fixture.Options, productionAdmissionFactsSource: admissionFactsSource);
        var drafts = harness.Service<RecipeDraftService>();
        var preparation = harness.Service<AlgorithmPreparationService>();
        var preparationOptions = harness.Service<AlgorithmPreparationOptions>();
        var releases = harness.Service<IReleasedRecipeQuery>();
        restarted.ConfigureRecipeActivationService(new RecipeActivationService(drafts, releases,
            harness.Service<IPlcResultContractQuery>(), harness.Activations, fixture.Authorization,
            fixture.Store, fixture.Options, preparation, preparationOptions, harness.Service<FrameBufferPool>(),
            (correlation, token) => restarted.ReserveRecipeActivationAsync(correlation, token),
            () => restarted.GetSnapshotAsync(), deploymentEvidence: productionDeploymentEvidence
                ? restarted.CaptureRecipeActivationDeploymentEvidenceAsync : null));
        if (fixture.Options.RecipeSelections is not null)
        {
            var selectionQuery = new SqliteRecipeSelectionQuery(fixture.Options);
            restarted.ConfigureRecipeSelectionService(new RecipeSelectionService(drafts, selectionQuery,
                selectionQuery, fixture.Authorization, fixture.Options,
                restarted.ReserveRecipeSelectionChangeAsync, () => restarted.GetSnapshotAsync()));
        }
        restarted.ConfigureManualInspectionSessions(new ManualInspectionSessionOptions(), drafts, releases,
            preparation, preparationOptions, harness.Service<AlgorithmExecutionOptions>(), fixture.Options, clock);
        return restarted;
    }
}
