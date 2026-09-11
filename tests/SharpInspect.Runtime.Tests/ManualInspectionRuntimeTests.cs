using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Manual;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.StoragePolicies;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Runtime integration coverage for the explicitly non-production Manual
/// Inspection boundary.  The harness uses the real SQLite writer, identity,
/// authorization, camera setup, preparation, execution and Manual history
/// query; the only device is the deterministic Virtual adapter.
/// </summary>
public sealed partial class ManualInspectionRuntimeTests
{
    private const string LogicalRole = "ManualCamera";
    private const string StableDeviceIdentity = "Virtual:Manual";
    private const string RecipeKey = "V135.Manual.Recipe";
    private const uint Seed = 135;

    [Fact]
    public async Task V135_M01_RealRuntimeReusesPreparedDraftForPassFailUnknownAndGracefulExit()
    {
        await using var harness = await ManualHarness.CreateAsync();

        var start = await harness.StartAsync();
        AssertAccepted(start, "Manual start");
        var ready = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Manual preparation did not become ready");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);

        for (var index = 0; index < 3; index++)
        {
            var run = await harness.RunAsync(sessionId);
            AssertAccepted(run, $"Manual run {index + 1}");
            await harness.WaitForSnapshotAsync(snapshot =>
                snapshot.SessionId == sessionId &&
                snapshot.Phase == ManualInspectionSessionPhase.ReadyForRun,
                $"Manual run {index + 1} did not return to ready");
        }

        var exit = await harness.ExitAsync(sessionId, ManualInspectionExitMode.Graceful);
        AssertAccepted(exit, "Manual graceful exit");
        var closed = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId &&
            snapshot.Phase == ManualInspectionSessionPhase.Closed,
            "Manual graceful exit did not close");
        Assert.Equal(ManualInspectionRestorationState.NoActiveBaselineClosed,
            closed.Restoration);
        Assert.False(closed.RecoveryRequired);

        var page = await harness.History.QueryAsync(new ManualInspectionHistoryFilter(
            SessionId: sessionId, PageSize: 32));
        Assert.True(page.Available, page.ReasonCode);
        Assert.False(page.RecoveryRequired);
        var runs = page.Runs.Where(run => run.SessionId == sessionId)
            .OrderBy(run => run.Position).ToArray();
        Assert.Equal(3, runs.Length);
        Assert.All(runs, run =>
        {
            Assert.True(run.Terminal);
            Assert.Equal(ExecutionStatus.Success, run.ExecutionStatus);
            Assert.NotNull(run.Result);
            Assert.NotNull(run.ResultSchema);
            Assert.NotNull(run.FrameMetadata);
            Assert.NotNull(run.FrameProvenance);
            Assert.NotNull(run.AlgorithmResultContentHash);
        });
        Assert.Equal(new[] { InspectionDecision.Pass, InspectionDecision.Fail,
            InspectionDecision.Unknown }, runs.Select(run => run.Decision));
        Assert.Single(runs.Select(run => run.PreparedInstanceId).Distinct());
        Assert.Equal(1, harness.Factory.Created);

        var current = await harness.Activations.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Null(current.Record);
        var station = await harness.Runtime.GetSnapshotAsync();
        Assert.False(station.Ready);
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.Null(station.ActiveRecipe);
        Assert.Equal(ExclusiveMode.None, station.Mode);

        var diagnostics = harness.CameraProvider.GetDiagnostics().Devices.Single(
            device => device.StableDeviceIdentity == StableDeviceIdentity);
        Assert.Equal(3, diagnostics.FramesProduced);
        Assert.False(diagnostics.IsOpen);
    }

    [Fact]
    public async Task V135_M02_MissingPermissionIsAuditedAndDoesNotOpenCamera()
    {
        await using var harness = await ManualHarness.CreateAsync(allowManual: false,
            configureCamera: false);

        var command = new StartManualInspectionSessionCommand(Guid.NewGuid(),
            harness.Invocation(), ManualRecipeSelection.FromDraft(harness.Draft), null,
            "V135 permission denied manual start");
        var result = await harness.Runtime.SubmitAsync(command);

        Assert.Equal(CommandDisposition.Rejected, result.Disposition);
        Assert.Equal(AuditPersistence.Persisted, result.Audit);
        Assert.Contains(result.ReasonCode, new[] { "PermissionDenied", "ManualInspectionPermissionDenied" });
        var access = await harness.Manual.GetAccessAsync(harness.Invocation());
        Assert.False(access.CanRun);
        Assert.Equal("PermissionDenied", access.ReasonCode);
        Assert.Equal(0, harness.CameraProvider.GetDiagnostics().Devices.Single(
            device => device.StableDeviceIdentity == StableDeviceIdentity).OpenCount);
    }

    [Fact]
    public async Task V135_M03_DuplicateStartIsBusyAndAbortRestoresWithoutAnotherOpen()
    {
        await using var harness = await ManualHarness.CreateAsync();

        var start = await harness.StartAsync();
        AssertAccepted(start, "Manual start");
        var ready = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Manual preparation did not become ready");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        var before = harness.CameraProvider.GetDiagnostics().Devices.Single(
            device => device.StableDeviceIdentity == StableDeviceIdentity);

        var duplicate = await harness.Runtime.SubmitAsync(
            new StartManualInspectionSessionCommand(Guid.NewGuid(), harness.Invocation(),
                ManualRecipeSelection.FromDraft(harness.Draft), null,
                "V135 duplicate manual start"));
        Assert.Equal(CommandDisposition.Rejected, duplicate.Disposition);
        Assert.Equal(AuditPersistence.Persisted, duplicate.Audit);
        Assert.Contains(duplicate.ReasonCode, new[] {
            "ManualInspectionSessionInProgress", "ManualInspectionWorkflowConflict",
            "ManualInspectionCommandRejected"
        });
        var afterDuplicate = harness.CameraProvider.GetDiagnostics().Devices.Single(
            device => device.StableDeviceIdentity == StableDeviceIdentity);
        Assert.Equal(before.OpenCount, afterDuplicate.OpenCount);
        Assert.Equal(before.FramesProduced, afterDuplicate.FramesProduced);

        var exit = await harness.ExitAsync(sessionId, ManualInspectionExitMode.Abort);
        AssertAccepted(exit, "Manual abort exit");
        var closed = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId && snapshot.Phase == ManualInspectionSessionPhase.Closed,
            "Manual abort did not close");
        Assert.Equal(ManualInspectionRestorationState.NoActiveBaselineClosed,
            closed.Restoration);
        Assert.False(closed.RecoveryRequired);
        Assert.False(harness.CameraProvider.GetDiagnostics().Devices.Single(
            device => device.StableDeviceIdentity == StableDeviceIdentity).IsOpen);
    }

    [Fact]
    public async Task V135_M04_AcquisitionTimeoutIsTerminalAndRestoresCamera()
    {
        await using var harness = await ManualHarness.CreateAsync(timeout: true);

        var start = await harness.StartAsync();
        AssertAccepted(start, "Manual timeout start");
        var ready = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Manual timeout preparation did not become ready");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        var run = await harness.RunAsync(sessionId);
        AssertAccepted(run, "Manual timeout run");

        var closed = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId &&
            snapshot.Phase is ManualInspectionSessionPhase.Closed or ManualInspectionSessionPhase.RecoveryBlocked,
            "Manual timeout did not reach a terminal session");
        Assert.Equal(ManualInspectionSessionPhase.Closed, closed.Phase);
        Assert.False(closed.RecoveryRequired);

        var page = await harness.History.QueryAsync(new ManualInspectionHistoryFilter(
            SessionId: sessionId, PageSize: 16));
        Assert.True(page.Available, page.ReasonCode);
        var terminalRun = Assert.Single(page.Runs, item => item.SessionId == sessionId);
        Assert.True(terminalRun.Terminal);
        Assert.Equal(ManualInspectionRunStatus.TimedOut, terminalRun.Status);
        Assert.Equal(ExecutionStatus.Timeout, terminalRun.ExecutionStatus);
        Assert.Equal(InspectionDecision.Unknown, terminalRun.Decision);
        Assert.Null(terminalRun.Result);
        Assert.False(harness.CameraProvider.GetDiagnostics().Devices.Single(
            device => device.StableDeviceIdentity == StableDeviceIdentity).IsOpen);
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("create")]
    [InlineData("warm")]
    public async Task V135_M05_IgnoredPreparationCancellationRetainsOwnerUntilCleanup(
        string blockedStage)
    {
        await using var harness = await ManualHarness.CreateAsync(
            preparationBarrierStage: blockedStage, shutdownTimeout: TimeSpan.FromSeconds(1));

        var entered = false;
        try
        {
            var start = await harness.StartAsync();
            AssertAccepted(start, "Manual ignored-cancellation start");
            await harness.Factory.PreparationCallbackEntered.WaitAsync(TimeSpan.FromSeconds(10));
            entered = true;

            var preparing = await harness.WaitForSnapshotAsync(snapshot =>
                snapshot.Phase == ManualInspectionSessionPhase.Preparing &&
                snapshot.SessionId is not null,
                $"Manual {blockedStage} callback did not enter preparation");
            var sessionId = Assert.IsType<Guid>(preparing.SessionId);
            var before = harness.CameraProvider.GetDiagnostics().Devices.Single(
                device => device.StableDeviceIdentity == StableDeviceIdentity);

            var exit = await harness.ExitAsync(sessionId, ManualInspectionExitMode.Abort);
            AssertAccepted(exit, "Manual ignored-cancellation abort");
            var blocked = await harness.WaitForSnapshotAsync(snapshot =>
                snapshot.SessionId == sessionId &&
                snapshot.Phase == ManualInspectionSessionPhase.RecoveryBlocked,
                $"Manual {blockedStage} cancellation did not retain recovery owner");
            Assert.Equal(ManualInspectionRestorationState.RecoveryBlocked, blocked.Restoration);
            Assert.True(blocked.RecoveryRequired);

            var secondStart = await harness.StartAsync();
            Assert.Equal(CommandDisposition.Rejected, secondStart.Disposition);
            Assert.Equal(AuditPersistence.Persisted, secondStart.Audit);
            Assert.Equal("ManualInspectionRecoveryRequired", secondStart.ReasonCode);

            var after = harness.CameraProvider.GetDiagnostics().Devices.Single(
                device => device.StableDeviceIdentity == StableDeviceIdentity);
            Assert.Equal(before.OpenCount, after.OpenCount);
            Assert.Equal(before.FramesProduced, after.FramesProduced);
            Assert.Equal(before.IsOpen, after.IsOpen);
        }
        finally
        {
            // The external callback deliberately ignores the owner token.  The
            // test must release it even when an assertion fails so the real
            // preparation retirement can finish during fixture disposal.
            harness.Factory.ReleasePreparationCallback();
        }

        if (!entered) return;
        await harness.Factory.PreparationCallbackCompleted.WaitAsync(TimeSpan.FromSeconds(10));
        if (blockedStage == "validate")
        {
            Assert.Equal(0, harness.Factory.Created);
        }
        else
        {
            await harness.Factory.AlgorithmDisposeCompleted.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, harness.Factory.Created);
        }

        var retained = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == ManualInspectionSessionPhase.RecoveryBlocked,
            $"Manual {blockedStage} recovery latch was cleared after callback retirement");
        Assert.Equal(ManualInspectionRestorationState.RecoveryBlocked, retained.Restoration);
        Assert.True(retained.RecoveryRequired);
    }

    [Fact]
    public async Task V135_M06_UnpublishedAlgorithmDisposeFailureLatchesRecoveryBlocked()
    {
        await using var harness = await ManualHarness.CreateAsync(
            preparationBarrierStage: "warm", failUnpublishedDispose: true,
            shutdownTimeout: TimeSpan.FromSeconds(1));

        var start = await harness.StartAsync();
        AssertAccepted(start, "Manual unpublished-dispose start");
        await harness.Factory.PreparationCallbackEntered.WaitAsync(TimeSpan.FromSeconds(10));
        var preparing = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == ManualInspectionSessionPhase.Preparing &&
            snapshot.SessionId is not null,
            "Manual warm callback did not enter before disposal-failure abort");
        var sessionId = Assert.IsType<Guid>(preparing.SessionId);
        var before = harness.CameraProvider.GetDiagnostics().Devices.Single(
            device => device.StableDeviceIdentity == StableDeviceIdentity);

        try
        {
            var exit = await harness.ExitAsync(sessionId, ManualInspectionExitMode.Abort);
            AssertAccepted(exit, "Manual unpublished-dispose abort");
            var blocked = await harness.WaitForSnapshotAsync(snapshot =>
                snapshot.SessionId == sessionId &&
                snapshot.Phase == ManualInspectionSessionPhase.RecoveryBlocked,
                "Unpublished algorithm disposal failure did not block recovery");
            Assert.Equal(ManualInspectionRestorationState.RecoveryBlocked, blocked.Restoration);
            Assert.True(blocked.RecoveryRequired);

            var secondStart = await harness.StartAsync();
            Assert.Equal(CommandDisposition.Rejected, secondStart.Disposition);
            Assert.Equal(AuditPersistence.Persisted, secondStart.Audit);
            Assert.Equal("ManualInspectionRecoveryRequired", secondStart.ReasonCode);
            var diagnostics = harness.CameraProvider.GetDiagnostics().Devices.Single(
                device => device.StableDeviceIdentity == StableDeviceIdentity);
            Assert.Equal(before.OpenCount, diagnostics.OpenCount);
            Assert.Equal(0, diagnostics.FramesProduced);
            Assert.Equal(before.IsOpen, diagnostics.IsOpen);
        }
        finally
        {
            harness.Factory.ReleasePreparationCallback();
        }

        await harness.Factory.PreparationCallbackCompleted.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.Factory.AlgorithmDisposeCompleted.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, harness.Factory.Created);
        Assert.Equal(1, harness.Factory.AlgorithmDisposeFailures);
        var retained = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == ManualInspectionSessionPhase.RecoveryBlocked,
            "Unpublished disposal failure recovery latch was cleared");
        Assert.Equal(ManualInspectionRestorationState.RecoveryBlocked, retained.Restoration);
        Assert.True(retained.RecoveryRequired);
    }

    private static void AssertAccepted(RuntimeCommandOutcome result, string operation)
    {
        Assert.True(result.Disposition == CommandDisposition.Accepted,
            operation + ": " + result.ReasonCode + "; audit=" + result.Audit);
        Assert.True(result.Audit == AuditPersistence.Persisted, operation + ": " + result.ReasonCode);
        Assert.True(result.AttemptId is { } attempt && attempt != Guid.Empty,
            operation + " did not retain an audit attempt");
    }

    private sealed class ManualHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly ClockPump _clockPump;
        private bool _disposed;

        private ManualHarness(RecipeDraftStorageTests.Fixture fixture, ServiceProvider services,
            ClockPump clockPump, IVisionAlgorithmFactory factory, VirtualCameraProvider cameraProvider,
            IManualInspectionSessionService manual, IManualInspectionHistoryQuery history,
            IRecipeActivationQuery activations, ICameraSetupRuntime camera, IStationRuntime runtime,
            RecipeDraftRevision draft)
        {
            Fixture = fixture;
            _services = services;
            _clockPump = clockPump;
            Factory = Assert.IsType<ManualFactory>(factory);
            CameraProvider = cameraProvider;
            Manual = manual;
            History = history;
            Activations = activations;
            Camera = camera;
            Runtime = runtime;
            Draft = draft;
        }

        internal RecipeDraftStorageTests.Fixture Fixture { get; }
        internal ManualFactory Factory { get; }
        internal VirtualCameraProvider CameraProvider { get; }
        internal IManualInspectionSessionService Manual { get; }
        internal IManualInspectionHistoryQuery History { get; }
        internal IRecipeActivationQuery Activations { get; }
        internal ICameraSetupRuntime Camera { get; }
        internal IStationRuntime Runtime { get; }
        internal RecipeDraftRevision Draft { get; }

        internal T Service<T>() where T : notnull => _services.GetRequiredService<T>();
        internal void PauseClock() => _clockPump.Pause();
        internal void ResumeClock() => _clockPump.Resume();

        internal async Task StopRuntimePreservingFixtureAsync()
        {
            // Keep the provider and fixture-owned services alive so a cold runtime
            // can be composed against the same durable options after the store is
            // reopened.  The fixture remains responsible for their final disposal.
            await Assert.IsType<StationRuntime>(Runtime).DisposeAsync();
        }

        internal static async Task<ManualHarness> CreateAsync(bool allowManual = true,
            bool timeout = false, bool configureCamera = true,
            string? preparationBarrierStage = null, bool failUnpublishedDispose = false,
            TimeSpan? shutdownTimeout = null, bool requireManualStepUp = false,
            bool minimalStore = false, ManualInspectionStoreOptions? manualStoreOptions = null,
            int? maximumAuditEntries = null, bool activationReadyDraft = false, bool productionAdmission = false,
            ModbusQualificationTestServer? productionPeer = null,
            ProductionInspectionStoreOptions? productionStore = null,
            PartIdentityRequirement? productionPartRequirement = null,
            ModbusPartIdentityReadPlan? partIdentityReadPlan = null,
            PartIdentityStoreOptions? partIdentityStore = null,
            Action<IServiceCollection>? configureAdditionalServices = null,
            bool allowPartIdentityCorrection = true, bool allowProductionRecovery = true,
            bool enableProductionRecovery = false, AlarmPolicyRule? productionTestAlarm = null,
            PlcCommunicationPolicy? productionCommunicationPolicy = null, TimeSpan? heartbeatInterval = null,
            ModbusRecipeChangeBinding? recipeChangeBinding = null,
            IReadOnlyList<VirtualCameraConfigurationPlan>? cameraConfigurationPlans = null)
        {
            var policy = CreateAuthorizationPolicy(allowManual, requireManualStepUp);
            if (!allowPartIdentityCorrection)
                policy = new AuthorizationPolicy("V143.CorrectionDenied.Authorization", "1",
                    policy.RoleBundles.ToDictionary(pair => pair.Key,
                        pair => pair.Value.Where(permission => permission != Permission.CorrectHistoricalFact)),
                    policy.StepUpPermissions);
            if (!allowProductionRecovery)
                policy = new AuthorizationPolicy("V144.RecoveryDenied.Authorization", "1",
                    policy.RoleBundles.ToDictionary(pair => pair.Key,
                        pair => pair.Value.Where(permission => permission != Permission.ManualRecovery)),
                    policy.StepUpPermissions);
            if (productionPeer is not null)
                policy = new AuthorizationPolicy("V142.Production.Authorization", "1",
                    policy.RoleBundles.ToDictionary(pair => pair.Key, pair => pair.Key == HumanRoleBundle.Administrator
                        ? pair.Value.Concat(new[] { Permission.ReleaseRecipe, Permission.ManagePlcResultContract,
                            Permission.ActivateRecipe, Permission.ArmProduction, Permission.ManageProductionPolicy }).Distinct()
                        : pair.Value.AsEnumerable()), policy.StepUpPermissions);
            var alarm = CreateAlarmPolicy();
            if (productionTestAlarm is not null)
                alarm = new AlarmPolicy("V145.Production.Alarm", "1",
                    alarm.Rules.Concat(new[] { productionTestAlarm }), alarm.SourceObservationFreshness,
                    alarm.MaximumActiveInstances, alarm.MaximumPlcEntries);
            var releasePolicy = new RecipeGovernancePolicy("V135.Release", "1",
                RecipeGovernanceMode.SingleApproverRelease);
            var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(
                alarmPolicy: alarm, authorizationPolicy: policy,
                recipeReleases: minimalStore ? null : new RecipeReleaseStoreOptions(releasePolicy),
                plcResultContracts: minimalStore ? null : new PlcResultContractStoreOptions(),
                cameraSetup: new CameraSetupStoreOptions(),
                recipeActivations: minimalStore ? null : new RecipeActivationStoreOptions(),
                manualInspections: manualStoreOptions ?? new ManualInspectionStoreOptions(),
                maximumAuditEntries: maximumAuditEntries,
                productionAdmission: productionAdmission || productionPeer is not null ? new ProductionAdmissionStoreOptions() : null,
                plcCommunication: productionPeer is null ? null : new PlcCommunicationStoreOptions(),
                productionInspections: productionPeer is null ? null : productionStore ?? new ProductionInspectionStoreOptions(),
                partIdentities: partIdentityStore,
                productionRecovery: enableProductionRecovery ? new ProductionRecoveryStoreOptions() : null,
                recipeSelections: recipeChangeBinding is null ? null : new RecipeSelectionStoreOptions(),
                traceStoragePolicies: productionPeer is null ? null : new TraceStoragePolicyStoreOptions
                    { DeploymentScope = new("V142.Isolated.Station", "1", Array.Empty<TraceStorageRouteIdentity>()) });

            ServiceProvider? services = null;
            ClockPump? pump = null;
            try
            {
                var factory = new ManualFactory(preparationBarrierStage, failUnpublishedDispose);
                var content = CreateContent(fixture.Options, activationReadyDraft, productionPartRequirement);
                var saved = await fixture.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null,
                    Encode(content), "V135 create manual integration draft");
                Assert.True(saved.Saved, saved.ReasonCode);
                var draft = Assert.IsType<RecipeDraftRevision>(saved.Revision);

                var clock = new VirtualCameraClock(productionPeer is not null ? DateTimeOffset.UtcNow :
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
                var cameraProvider = CreateProvider(clock, timeout, cameraConfigurationPlans);
                var registrations = new ServiceCollection();
                if (productionPeer is not null)
                {
                    var publication = await TraceStoragePolicyRuntimeTests.Service(fixture).PublishAsync(
                        await TraceStoragePolicyRuntimeTests.AuthorizedCommand(fixture, 0, TraceStoragePolicyRuntimeTests.Policy()));
                    Assert.True(publication.Succeeded, publication.Outcome.ReasonCode);
                    ProductionPolicyDocument Document(string id, string content) => new(id, "1", content);
                    var deployment = new ProductionDeploymentManifest("V142.Isolated.Deployment", "1",
                        Document("Logging", "Isolated test workload: structured command and inspection audit only."),
                        Document("Diagnostics", "Protected test diagnostics are local and access controlled."),
                        Document("Backup", "Offline test database; retained test artifacts, no production restore qualification."),
                        Document("Startup", "Verify all enabled ledgers; pending inspection blocks before opening PLC socket."),
                        Document("Performance", "Virtual isolated station, one frame per software trigger; 2 second execution budget."),
                        Document("Conformance", "V142 isolated software contract checks, test issuer only."),
                        Document("UiWorkload", "Explicit headless test host, 20 ms snapshot observation."), Array.Empty<string>());
                    registrations.AddSingleton(new ProductionInspectionOptions(fixture.Options.LocalIdentity!.StationId,
                        ProductionEvidenceRequirement.None, productionPeer.CreateProductionProfile(
                            productionCommunicationPolicy, partIdentity: partIdentityReadPlan, recipeChange: recipeChangeBinding),
                        publication.Snapshot!.Version, publication.Snapshot.ContentHash,
                        TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), deployment));
                }
                registrations.AddSingleton(fixture.Store);
                registrations.AddSingleton(fixture.Identity);
                registrations.AddSingleton<IIdentityProvider>(fixture.Identity);
                registrations.AddSingleton<ILocalAdministratorBootstrap>(fixture.Identity);
                registrations.AddSingleton(fixture.Sessions);
                registrations.AddSingleton<IInteractiveSessionService>(fixture.Sessions);
                registrations.AddSingleton(fixture.Authorization);
                registrations.AddSingleton<IStepUpAuthentication>(fixture.Authorization);
                registrations.AddSingleton<IIdentityAdministrationQuery>(fixture.Authorization);
                registrations.AddSingleton<IVisionAlgorithmFactory>(factory);
                registrations.AddSingleton(clock);
                registrations.AddSingleton<IFrameAcquisitionClock>(clock);
                registrations.AddSharpInspectAlgorithmPreparation(
                    new AlgorithmPreparationOptions(TimeSpan.FromSeconds(5), 1, activationReadyDraft ? 2 : 1));
                registrations.AddSharpInspectAlgorithmExecution(new AlgorithmExecutionOptions(
                    fixture.Options.RecipeDrafts!.ExecutionPolicy, TimeSpan.FromSeconds(5)));
                registrations.AddSharpInspectFrameBufferPool(new FrameBufferPoolOptions(
                    2, 4096, TimeSpan.FromSeconds(1)));
                registrations.AddSingleton(new ManualInspectionSessionOptions
                {
                    PreparationTimeout = TimeSpan.FromSeconds(5),
                    ShutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(5)
                });
                registrations.AddSharpInspectCameraProvider(cameraProvider);
                registrations.AddSharpInspectCameraSetup(new CameraSetupOptions
                {
                    OperationTimeout = TimeSpan.FromSeconds(2),
                    ShutdownTimeout = TimeSpan.FromSeconds(2)
                });
                registrations.AddSharpInspectSqliteRuntime(fixture.Options,
                    heartbeatInterval ?? TimeSpan.FromMilliseconds(20));
                configureAdditionalServices?.Invoke(registrations);
                services = registrations.BuildServiceProvider();
                var runtime = services.GetRequiredService<IStationRuntime>();
                if (runtime is not StationRuntime station)
                    throw new XunitException("Manual runtime did not use StationRuntime");
                await station.WaitForManualInspectionStartupAsync()
                    .WaitAsync(TimeSpan.FromSeconds(30));
                if (productionPeer is not null)
                {
                    var startupManual = await fixture.Store.ReadManualInspectionRecoveryStateAsync(CancellationToken.None);
                    Assert.True(startupManual.Available, startupManual.ReasonCode);
                    if (fixture.Options.PreviewSessions is not null)
                    {
                        var startupPreview = await fixture.Store.ReadPreviewRecoveryStateAsync(CancellationToken.None);
                        Assert.True(startupPreview.Available, startupPreview.ReasonCode);
                    }
                    var startupActivation = await new SqliteRecipeActivationQuery(fixture.Options).ReadCurrentAsync();
                    Assert.True(startupActivation.Available, startupActivation.ReasonCode);
                    var startupState = await runtime.GetSnapshotAsync();
                    Assert.True(startupState.Mode == ExclusiveMode.None,
                        $"Startup mode={startupState.Mode}, integrity={fixture.Store.Integrity?.ReasonCode}, " +
                        $"alarm={startupState.AlarmState?.ReasonCode}, store={startupState.Store.ReasonCode}, " +
                        $"blockers={string.Join(",", startupState.AdmissionBlockers)}");
                }
                pump = ClockPump.Start(clock);

                var manual = services.GetRequiredService<IManualInspectionSessionService>();
                var history = services.GetRequiredService<IManualInspectionHistoryQuery>();
                var activations = services.GetService<IRecipeActivationQuery>() ??
                    new SqliteRecipeActivationQuery(fixture.Options);
                var camera = services.GetRequiredService<ICameraSetupRuntime>();
                if (configureCamera && allowManual)
                    await ConfigureCameraAsync(fixture, camera, cameraProvider.Identity);

                return new ManualHarness(fixture, services, pump, factory, cameraProvider,
                    manual, history, activations, camera, runtime, draft);
            }
            catch
            {
                if (pump is not null) await pump.DisposeAsync();
                if (services is not null) await services.DisposeAsync();
                await fixture.DisposeAsync();
                throw;
            }
        }

        internal CommandInvocation Invocation() => new(CommandSource.PhysicalConsole,
            Fixture.Sessions.Current.PrincipalId, Fixture.Sessions.Current.SessionId);

        internal async Task<RuntimeCommandOutcome> StartAsync()
        {
            var command = new StartManualInspectionSessionCommand(Guid.NewGuid(), Invocation(),
                ManualRecipeSelection.FromDraft(Draft), null, "V135 start manual integration session");
            return await Runtime.SubmitAsync(command);
        }

        internal async Task<RuntimeCommandOutcome> RunAsync(Guid sessionId)
        {
            var command = new RunManualInspectionCommand(Guid.NewGuid(), Invocation(), sessionId,
                null, "V135 run one manual integration frame");
            return await Runtime.SubmitAsync(command);
        }

        internal async Task<RuntimeCommandOutcome> ExitAsync(Guid sessionId,
            ManualInspectionExitMode mode)
        {
            var command = new ExitManualInspectionSessionCommand(Guid.NewGuid(), Invocation(),
                sessionId, mode, "V135 end manual integration session");
            return await Runtime.SubmitAsync(command);
        }

        internal async Task<ManualInspectionSessionSnapshot> WaitForSnapshotAsync(
            Func<ManualInspectionSessionSnapshot, bool> predicate, string timeoutReason)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            ManualInspectionSessionSnapshot? last = null;
            while (DateTime.UtcNow < deadline)
            {
                var result = await Manual.GetSnapshotAsync(Invocation());
                Assert.True(result.Available, result.ReasonCode);
                last = Assert.IsType<ManualInspectionSessionSnapshot>(result.Snapshot);
                if (predicate(last)) return last;
                if (last.Phase == ManualInspectionSessionPhase.RecoveryBlocked)
                    throw new XunitException(timeoutReason + ":" + last.ReasonCode);
                await Task.Delay(25);
            }

            throw new XunitException(timeoutReason + ":" + last?.Phase + "/" + last?.ReasonCode);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _clockPump.DisposeAsync();
            await _services.DisposeAsync();
            await Fixture.DisposeAsync();
        }

        private static async Task ConfigureCameraAsync(RecipeDraftStorageTests.Fixture fixture,
            ICameraSetupRuntime camera, CameraProviderIdentity provider)
        {
            var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
                fixture.Sessions.Current.PrincipalId, fixture.Sessions.Current.SessionId);
            var current = await camera.GetSetupAsync(LogicalRole, invocation);
            Assert.True(current.Available, current.ReasonCode);
            var target = new CameraBindingTarget(provider, StableDeviceIdentity);
            var binding = current.Snapshot?.Binding;
            if (binding is null || binding.Target != target)
            {
                var operation = Guid.NewGuid();
                var grant = await GrantCameraStepUpAsync(fixture, invocation, operation,
                    AuditedCommandKind.RebindCamera);
                var rebound = await camera.RebindAsync(new CameraRebindRequest(operation,
                    invocation with { StepUpGrantId = grant.GrantId }, LogicalRole,
                    binding?.Revision ?? 0, binding?.RevisionHash, target,
                    "V135 bind manual integration camera"));
                Assert.True(rebound.Succeeded, rebound.ReasonCode);
                binding = Assert.IsType<CameraBindingRevision>(rebound.Snapshot!.Binding);
            }
            // The selected Manual candidate applies its exact Draft configuration.
            // Binding setup need not execute a separate debug configuration change.
        }

        private static async Task<StepUpResult> GrantCameraStepUpAsync(
            RecipeDraftStorageTests.Fixture fixture, CommandInvocation invocation,
            Guid operation, AuditedCommandKind commandKind)
        {
            var result = await fixture.Authorization.ReauthenticateAsync(new StepUpRequest(
                Guid.NewGuid(), invocation,
                new StepUpBinding(Permission.ManageCameraBindings, operation, LogicalRole,
                    commandKind), fixture.Password));
            Assert.True(result.Succeeded, result.ReasonCode);
            Assert.NotNull(result.GrantId);
            return result;
        }

        private static RecipeDraftDocument Encode(RecipeDraftContent content)
        {
            Assert.True(RecipeDraftStorageCodec.TryEncodeContent(content, out var document,
                out var reason), reason);
            return document!;
        }

        private static RecipeDraftContent CreateContent(ProductionStoreOptions options, bool activationReadyDraft = false,
            PartIdentityRequirement? productionPartRequirement = null)
        {
            var schema = new AlgorithmConfigurationSchema("V135.Manual.Config", "1",
                Array.Empty<AlgorithmFieldDefinition>());
            var configuration = AlgorithmConfigurationSnapshot.Create(schema,
                Array.Empty<AlgorithmConfigurationEntry>());
            var result = new AlgorithmResultSchema("V135.Manual.Result", "1",
                new[] { new AlgorithmFieldDefinition("Score", AlgorithmScalarType.Float64,
                    "ratio", true, new AlgorithmScalarConstraints(minFloat64: 0,
                        maxFloat64: 100)) },
                new[] { "ManualSyntheticUnknown" },
                new OverlayContract("V135.Manual.Overlay", "1", 1, 4, 4, 0));
            var descriptor = new AlgorithmDescriptor(
                new AlgorithmIdentity("V135.Manual.Algorithm", "1"), schema, result);
            var binding = RecipeAlgorithmBinding.FromDescriptor(descriptor);
            var camera = RequestedCamera();
            var execution = options.RecipeDrafts!.ExecutionPolicy;
            return new RecipeDraftContent(RecipeKey, "V135 Manual Runtime Draft", binding,
                configuration, LogicalRole, camera, TimeSpan.FromSeconds(2),
                Array.Empty<RecipeAssetRequirement>(), new[]
                {
                    new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
                        new RecipeContractReference(execution.Id, execution.Version,
                            execution.ContentHash))
                }, partIdentityRequirement: activationReadyDraft ? productionPartRequirement ?? PartIdentityRequirement.None : null);
        }

        private static RequestedCameraConfiguration RequestedCamera() =>
            new(ProductionAcquisitionMode.SoftwareTrigger, 100, 0,
                new RegionOfInterest(0, 0, 16, 12), VisionPixelFormat.Mono8, null,
                250, 0, null);

        private static AuthorizationPolicy CreateAuthorizationPolicy(bool allowManual, bool requireManualStepUp)
        {
            var roles = AuthorizationPolicy.Development.RoleBundles.ToDictionary(
                pair => pair.Key,
                pair => pair.Key == HumanRoleBundle.Administrator
                    ? pair.Value.Concat(new[] { Permission.EditRecipeDraft })
                        .Concat(allowManual ? new[] { Permission.RunManualInspection } :
                            Array.Empty<Permission>())
                    : pair.Value.AsEnumerable());
            return new AuthorizationPolicy("V135.Manual.Authorization", "1", roles,
                AuthorizationPolicy.Development.StepUpPermissions.Concat(requireManualStepUp
                    ? new[] { Permission.RunManualInspection } : Array.Empty<Permission>()));
        }

        private static AlarmPolicy CreateAlarmPolicy() => new("V135.Manual.Alarm", "1",
            new[]
            {
                new AlarmPolicyRule("StartupRecoveryRequired", "Runtime.StartupRecovery",
                    AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers, true,
                    AlarmNotification.UntilCleared, null,
                    ResetPrerequisites: AlarmResetPrerequisites.RecoveryComplete |
                        AlarmResetPrerequisites.NoPendingDelivery),
                new AlarmPolicyRule("PreviewRecoveryRequired", "Runtime.Preview",
                    AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers, true,
                    AlarmNotification.UntilCleared, null,
                    ResetPrerequisites: AlarmResetPrerequisites.RecoveryComplete |
                        AlarmResetPrerequisites.NoActiveExecution),
                new AlarmPolicyRule("AlgorithmHung", "Runtime.AlgorithmExecution",
                    AlarmSeverity.Critical, ProductionImpact.BlockNewTriggers, true,
                    AlarmNotification.UntilCleared, null, 110),
                new AlarmPolicyRule("FrameBufferExhausted", "Runtime.FrameBufferPool",
                    AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, true,
                    AlarmNotification.UntilCleared, null, 200,
                    AlarmResetPrerequisites.RecoveryComplete |
                        AlarmResetPrerequisites.NoActiveExecution),
                new AlarmPolicyRule("ManualInspectionRecoveryRequired",
                    "Runtime.ManualInspection", AlarmSeverity.Warning,
                    ProductionImpact.BlockNewTriggers, true,
                    AlarmNotification.UntilCleared, null,
                    ResetPrerequisites: AlarmResetPrerequisites.RecoveryComplete |
                        AlarmResetPrerequisites.NoActiveExecution)
            }, TimeSpan.FromMinutes(1));

        internal static VirtualCameraProvider CreateProvider(VirtualCameraClock clock,
            bool timeout, IReadOnlyList<VirtualCameraConfigurationPlan>? configurationPlans = null)
        {
            var capabilities = new CameraCapabilities(
                new[] { ProductionAcquisitionMode.SoftwareTrigger },
                new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
                new(10, 10_000, 1, CameraQuantizationMode.Exact),
                new(0, 24, 1, CameraQuantizationMode.Exact),
                new(0, 10_000, 1, CameraQuantizationMode.Exact),
                new(128, 96, new(0, 127, 1), new(0, 95, 1),
                    new(1, 128, 1), new(1, 96, 1)));
            var image = VirtualCameraImage.CreateSynthetic("manual-frame", 16, 12,
                VisionPixelFormat.Mono8, null, Seed);
            var acquisitions = Enumerable.Range(0, 3).Select(_ =>
                new VirtualCameraAcquisitionPlan(timeout
                    ? Array.Empty<VirtualCameraSignal>()
                    : new[] { new VirtualCameraSignal(TimeSpan.FromMilliseconds(100),
                        VirtualCameraSignalKind.Frame, image.Id) })).ToArray();
            var configurations = configurationPlans ?? Enumerable.Repeat(
                new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success,
                    TimeSpan.Zero), 32).ToArray();
            var scenario = new VirtualCameraScenario("V135.Manual", "1", Seed,
                StableDeviceIdentity, capabilities, new[] { image }, acquisitions,
                configurations);
            return new VirtualCameraProvider(new[] { scenario }, clock, poolCapacity: 2);
        }
    }

    private sealed class ClockPump : IAsyncDisposable
    {
        private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(10);
        private readonly VirtualCameraClock _clock;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        private int _paused;

        private ClockPump(VirtualCameraClock clock)
        {
            _clock = clock;
            _worker = Task.Run(RunAsync);
        }

        internal static ClockPump Start(VirtualCameraClock clock) => new(clock);
        internal void Pause() => Interlocked.Exchange(ref _paused, 1);
        internal void Resume() => Interlocked.Exchange(ref _paused, 0);

        private async Task RunAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(Tick, _stop.Token).ConfigureAwait(false);
                    if (Volatile.Read(ref _paused) == 0) _clock.AdvanceBy(Tick);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _worker.ConfigureAwait(false); }
            finally { _stop.Dispose(); }
        }
    }

    private sealed class ManualFactory : IVisionAlgorithmFactory
    {
        private readonly string? _preparationBarrierStage;
        private readonly bool _failUnpublishedDispose;
        private readonly TaskCompletionSource<bool> _preparationCallbackEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _preparationCallbackReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _preparationCallbackCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _algorithmDisposeCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _created;
        private int _validationCalls;
        private int _algorithmDisposeFailures;
        private int _holdExecution;
        private bool _cooperativeExecutionCancellation;
        private readonly TaskCompletionSource<bool> _executionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _executionReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _holdNextCreate;
        private readonly TaskCompletionSource<bool> _nextCreateEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _nextCreateReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task NextCreateEntered => _nextCreateEntered.Task;
        internal void HoldNextCreate() => Interlocked.Exchange(ref _holdNextCreate, 1);
        internal void ReleaseNextCreate() => _nextCreateReleased.TrySetResult(true);

        internal ManualFactory(string? preparationBarrierStage = null,
            bool failUnpublishedDispose = false)
        {
            if (preparationBarrierStage is not null && preparationBarrierStage is not
                ("validate" or "create" or "warm"))
                throw new ArgumentOutOfRangeException(nameof(preparationBarrierStage));
            _preparationBarrierStage = preparationBarrierStage;
            _failUnpublishedDispose = failUnpublishedDispose;
            var configuration = new AlgorithmConfigurationSchema("V135.Manual.Config", "1",
                Array.Empty<AlgorithmFieldDefinition>());
            var result = new AlgorithmResultSchema("V135.Manual.Result", "1",
                new[] { new AlgorithmFieldDefinition("Score", AlgorithmScalarType.Float64,
                    "ratio", true, new AlgorithmScalarConstraints(minFloat64: 0,
                        maxFloat64: 100)) },
                new[] { "ManualSyntheticUnknown" },
                new OverlayContract("V135.Manual.Overlay", "1", 1, 4, 4, 0));
            Descriptor = new AlgorithmDescriptor(
                new AlgorithmIdentity("V135.Manual.Algorithm", "1"), configuration, result);
        }

        public AlgorithmDescriptor Descriptor { get; }
        internal int Created => Volatile.Read(ref _created);
        internal int AlgorithmDisposeFailures => Volatile.Read(ref _algorithmDisposeFailures);
        internal Task PreparationCallbackEntered => _preparationCallbackEntered.Task;
        internal Task PreparationCallbackCompleted => _preparationCallbackCompleted.Task;
        internal Task AlgorithmDisposeCompleted => _algorithmDisposeCompleted.Task;
        internal Task ExecutionEntered => _executionEntered.Task;
        internal void HoldExecution(bool cooperativeCancellation = false)
        {
            _cooperativeExecutionCancellation = cooperativeCancellation;
            Volatile.Write(ref _holdExecution, 1);
        }
        internal void ReleaseExecution() => _executionReleased.TrySetResult(true);

        internal void ReleasePreparationCallback() =>
            _preparationCallbackReleased.TrySetResult(true);

        public async ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default)
        {
            // The first semantic validation belongs to source admission. This
            // barrier exercises the subsequent Runtime-owned preparation call.
            if (Interlocked.Increment(ref _validationCalls) > 1)
                await WaitForPreparationCallbackAsync("validate", cancellationToken)
                    .ConfigureAwait(false);
            return configuration.Validate(Descriptor.ConfigurationSchema);
        }

        public async ValueTask<IVisionAlgorithm> CreateAsync(
            AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _holdNextCreate, 0) != 0)
            {
                _nextCreateEntered.TrySetResult(true);
                await _nextCreateReleased.Task.ConfigureAwait(false);
            }
            await WaitForPreparationCallbackAsync("create", cancellationToken)
                .ConfigureAwait(false);
            Interlocked.Increment(ref _created);
            return new ManualAlgorithm(this, Descriptor);
        }

        private async Task WaitForPreparationCallbackAsync(string stage,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(_preparationBarrierStage, stage,
                    StringComparison.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            _preparationCallbackEntered.TrySetResult(true);
            try
            {
                // This is intentionally an external callback which ignores the
                // Runtime cancellation token.  The owning Runtime must retain
                // its preparation task until this barrier actually returns.
                await _preparationCallbackReleased.Task.ConfigureAwait(false);
            }
            finally
            {
                _preparationCallbackCompleted.TrySetResult(true);
            }
        }

        private sealed class ManualAlgorithm : IVisionAlgorithm
        {
            private readonly ManualFactory _factory;
            private readonly AlgorithmDescriptor _descriptor;
            private int _sequence;
            private bool _warmed;
            private bool _disposed;

            internal ManualAlgorithm(ManualFactory factory, AlgorithmDescriptor descriptor)
            {
                _factory = factory;
                _descriptor = descriptor;
            }

            public async ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
            {
                await _factory.WaitForPreparationCallbackAsync("warm", cancellationToken)
                    .ConfigureAwait(false);
                if (_disposed) throw new InvalidOperationException("V135ManualAlgorithmDisposed");
                _warmed = true;
            }

            public async ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_warmed || _disposed)
                    throw new InvalidOperationException("V135ManualAlgorithmNotPrepared");
                if (Volatile.Read(ref _factory._holdExecution) != 0)
                {
                    _factory._executionEntered.TrySetResult(true);
                    if (_factory._cooperativeExecutionCancellation)
                        await _factory._executionReleased.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    else
                        // Model an external algorithm that ignores cancellation and retains its input.
                        await _factory._executionReleased.Task.ConfigureAwait(false);
                }
                var sequence = Interlocked.Increment(ref _sequence);
                var decision = sequence switch
                {
                    1 => InspectionDecision.Pass,
                    2 => InspectionDecision.Fail,
                    _ => InspectionDecision.Unknown
                };
                var reason = decision == InspectionDecision.Unknown
                    ? "ManualSyntheticUnknown" : null;
                var result = new AlgorithmResult(decision, reason,
                    new[] { new AlgorithmMeasurement("Score", "ratio",
                        AlgorithmScalarValue.FromFloat64(sequence * 10)) },
                    new OutputOverlaySet(_descriptor.ResultSchema.OverlayContract,
                        new OverlayPrimitive[]
                        {
                            new OverlayAxisAlignedRectangle(new OverlayPoint(1, 1), 8, 8)
                        }));
                return result;
            }

            public ValueTask DisposeAsync()
            {
                _disposed = true;
                _warmed = false;
                _factory._algorithmDisposeCompleted.TrySetResult(true);
                if (_factory._failUnpublishedDispose)
                {
                    Interlocked.Increment(ref _factory._algorithmDisposeFailures);
                    return new ValueTask(Task.FromException(
                        new InvalidOperationException("V135ManualUnpublishedDisposeFailure")));
                }
                return ValueTask.CompletedTask;
            }
        }
    }
}
