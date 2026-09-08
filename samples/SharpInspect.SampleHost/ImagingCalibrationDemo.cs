using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Storage;
using SharpInspect.Wpf;

namespace SharpInspect.SampleHost;

/// <summary>
/// A bounded, development-only consumer of the public imaging setup and
/// calibration contracts.  The fixture uses only the deterministic virtual
/// camera; it never loads a vendor SDK or attempts physical discovery.
/// </summary>
internal static class ImagingCalibrationDemo
{
    private const string Role = "TopCamera";
    private const string Device = "Virtual:Imaging-Calibration";
    private const string ProcedureId = "SharpInspect.CalibrationProcedure.Fixture";
    private const string ProcedureVersion = "1";
    private const string CoefficientContractId = "SharpInspect.Calibration.Coefficients.Intrinsic";
    private const string CoefficientContractVersion = "1";
    private const string AcceptancePolicyId = "SharpInspect.Calibration.Acceptance";
    private const string AcceptancePolicyVersion = "1";

    internal static int Run(ProductionStoreOptions options, string directory, string? userName,
        string? expectedPrincipal) => Execute(
            () => RunCore(options, Path.GetFullPath(directory), userName!, expectedPrincipal!, ReadPassword()),
            "V123-N01 imaging-calibration-consumer PASS revisions=2 exactProfiles=true oldProfileRejected=true ready=false");

    internal static int Query(ProductionStoreOptions options, string directory, string? userName,
        string? expectedPrincipal) => Execute(
            () => QueryCore(options, Path.GetFullPath(directory), userName!, expectedPrincipal!, ReadPassword()),
            "V123-N02 imaging-calibration-restart PASS revisions=2 hashesPersisted=true openedDevices=0 ready=false");

    private static int Execute(Func<Task> action, string marker)
    {
        try
        {
            Pump(action);
            Console.WriteLine(marker);
            return 0;
        }
        catch (ImagingCalibrationCheckException exception)
        {
            Console.Error.WriteLine("V123 imaging-calibration FAIL reason=" + exception.ReasonCode);
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("V123 imaging-calibration FAIL reason=" +
                exception.GetType().Name);
            return 1;
        }
    }

    private static string ReadPassword() => JsonSerializer.Deserialize<string>(Console.ReadLine() ?? "null") ??
        throw new ImagingCalibrationCheckException("ImagingCalibrationConsumerPasswordRequired");

    private static ServiceCollection Services(ProductionStoreOptions options, ICameraProvider camera)
    {
        var services = new ServiceCollection();
        services.AddSingleton(camera);
        services.AddSharpInspectCameraSetup(new CameraSetupOptions());
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(100));
        return services;
    }

    private static async Task RunCore(ProductionStoreOptions options, string directory,
        string userName, string expectedPrincipal, string password)
    {
        Require(options.ImagingSetup is not null, "ImagingSetupStoreNotEnabled");
        Require(File.Exists(options.DatabasePath), "ImagingCalibrationIdentityStoreRequired");
        Directory.CreateDirectory(directory);
        using var clock = new VirtualCameraClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var camera = CreateProvider(clock);
        await using var container = Services(options, camera).BuildServiceProvider();
        var runtime = container.GetRequiredService<IStationRuntime>();
        var setup = container.GetRequiredService<ICameraSetupRuntime>();
        var imaging = container.GetRequiredService<IImagingSetupRuntime>();
        var sessions = container.GetRequiredService<IInteractiveSessionService>();
        var stepUp = container.GetRequiredService<IStepUpAuthentication>();
        await Verified(runtime);

        var anonymous = await setup.DiscoverAsync(camera.Identity, new(CommandSource.PhysicalConsole));
        Require(!anonymous.Succeeded && anonymous.Devices.Count == 0,
            "ImagingCalibrationAnonymousDiscoveryAccepted");
        await SignIn(sessions, runtime, userName, expectedPrincipal, password);

        var target = new CameraBindingTarget(camera.Identity, Device);
        var binding = await RebindAsync(setup, stepUp, sessions, target, password);
        var requested = RequestedConfiguration();
        var applied = await ApplyAsync(setup, stepUp, sessions, binding, requested, password);
        Require(applied.Health is { Connection: CameraConnectionState.Open,
                Configuration: CameraConfigurationState.Applied,
                Acquisition: CameraAcquisitionState.Stopped } &&
            applied.Requested is not null && applied.Effective is not null,
            "ImagingCalibrationDebugApplyReadBackInvalid");

        var definition1 = new ImagingSetupDefinition("FixtureLens-A", "Focus-100",
            "Mount-Top-A", 250, "SensorUp");
        var missingStepUp = await imaging.DeclareImagingSetupAsync(new(Guid.NewGuid(),
            Invocation(sessions), Role, binding.Revision, binding.RevisionHash, 0, null,
            definition1, "Initial imaging setup without Step-Up"));
        Require(!missingStepUp.Succeeded && missingStepUp.ReasonCode == "StepUpRequired",
            "ImagingCalibrationMissingStepUpAccepted");

        var revision1Result = await DeclareAsync(imaging, stepUp, sessions, binding, 0, null,
            definition1, "Declare initial imaging setup", password);
        Require(revision1Result.Succeeded && revision1Result.AuditPersistence == AuditPersistence.Persisted &&
            revision1Result.Revision is not null,
            "ImagingCalibrationRevisionOneDeclarationFailed_" + revision1Result.ReasonCode);
        var revision1 = revision1Result.Revision!;
        Require(revision1.Revision == 1 && revision1.PreviousRevisionHash is null &&
            revision1.Origin == ImagingSetupChangeOrigin.OperatorDeclared,
            "ImagingCalibrationRevisionOneInvalid");

        var replay = await DeclareAsync(imaging, stepUp, sessions, binding, 0, null,
            definition1, "Declare initial imaging setup", password, revision1.OperationId);
        Require(replay.Succeeded && replay.Revision?.RevisionHash == revision1.RevisionHash,
            "ImagingCalibrationExactReplayFailed_" + replay.ReasonCode);
        var conflictingReplay = await DeclareAsync(imaging, stepUp, sessions, binding, 0, null,
            new ImagingSetupDefinition("ConflictingLens", "Focus-100", "Mount-Top-A", 250, "SensorUp"),
            "Changed intent with an existing operation ID", password, revision1.OperationId);
        Require(!conflictingReplay.Succeeded && conflictingReplay.ReasonCode == "ImagingSetupOperationConflict",
            "ImagingCalibrationConflictingReplayAccepted_" + conflictingReplay.ReasonCode);
        var replayHistory = await imaging.QueryImagingSetupHistoryAsync(Role, Invocation(sessions));
        Require(replayHistory.Available && replayHistory.Revisions.Count == 1,
            "ImagingCalibrationReplayAppendedRevision");

        var wrongRevision = await DeclareAsync(imaging, stepUp, sessions, binding, 0, null,
            new ImagingSetupDefinition("FixtureLens-WrongRevision", "Focus-100", "Mount-Top-A", 250, "SensorUp"),
            "Wrong expected imaging revision", password);
        Require(!wrongRevision.Succeeded && wrongRevision.ReasonCode == "ImagingSetupRevisionConflict",
            "ImagingCalibrationWrongExpectedRevisionAccepted");

        var wrongHash = await DeclareAsync(imaging, stepUp, sessions, binding, 1,
            new string('F', 64), new ImagingSetupDefinition("FixtureLens-WrongHash", "Focus-100", "Mount-Top-A", 250, "SensorUp"),
            "Wrong expected imaging revision hash", password);
        Require(!wrongHash.Succeeded && wrongHash.ReasonCode == "ImagingSetupRevisionConflict",
            "ImagingCalibrationWrongExpectedHashAccepted");

        var requirement = Requirement();
        var recipe = Recipe(requirement);
        var candidateContent = ProfileContent(requirement, target, revision1, requested, applied.Effective!,
            actor: Guid.Parse(expectedPrincipal), sourceEvidence: new string('C', 64));
        var historicalContent = ProfileContent(requirement, target, revision1, requested, applied.Effective!,
            actor: Guid.Parse(expectedPrincipal), sourceEvidence: new string('B', 64));
        var candidate = new CalibrationFixtureProfile(
            Guid.Parse("2c7ef1a2-72a7-4a9b-b0e7-7778c0c6f101"), 1, candidateContent,
            CalibrationFixtureProvenance.Candidate);
        var historical = new CalibrationFixtureProfile(
            Guid.Parse("2c7ef1a2-72a7-4a9b-b0e7-7778c0c6f102"), 1, historicalContent,
            CalibrationFixtureProvenance.Historical);
        var resolver = new CalibrationRequirementResolver(setup, imaging,
            new CalibrationFixtureCatalog(new[] { candidate, historical }));
        var invocation = Invocation(sessions);
        var candidateCheck = await resolver.CheckAsync(recipe,
            new[] { new CalibrationProfileSelection(requirement.ContentHash, candidate.Reference) }, invocation);
        Require(candidateCheck.Available && candidateCheck.Compatible &&
            candidateCheck.ReasonCode == "CalibrationProfileAuthorityUnavailable" &&
            !candidateCheck.ProductionAuthority && !candidateCheck.CanActivate &&
            candidateCheck.EvidencePurpose == "DevelopmentOnly" &&
            candidateCheck.CalibrationActivationGateSatisfied == false,
            "ImagingCalibrationCandidateProfileCheckInvalid");
        var historicalCheck = await resolver.CheckAsync(recipe,
            new[] { new CalibrationProfileSelection(requirement.ContentHash, historical.Reference) }, invocation);
        Require(historicalCheck.Available && historicalCheck.Compatible &&
            !historicalCheck.ProductionAuthority && !historicalCheck.CanActivate,
            "ImagingCalibrationHistoricalProfileCheckInvalid");

        var definition2 = new ImagingSetupDefinition("FixtureLens-B", "Focus-101",
            "Mount-Top-B", 251, "SensorRotated");
        var revision2Result = await DeclareAsync(imaging, stepUp, sessions, binding, revision1.Revision,
            revision1.RevisionHash, definition2, "Declare lens replacement before recalibration", password);
        Require(revision2Result.Succeeded && revision2Result.AuditPersistence == AuditPersistence.Persisted &&
            revision2Result.Revision is { Revision: 2 },
            "ImagingCalibrationRevisionTwoDeclarationFailed_" + revision2Result.ReasonCode);
        var revision2 = revision2Result.Revision!;
        Require(revision2.Revision is 2 &&
            revision2.PreviousRevisionHash == revision1.RevisionHash &&
            revision2.ActorPrincipalId == Guid.Parse(expectedPrincipal) &&
            revision2.ChangeReason == "Declare lens replacement before recalibration" &&
            revision2.Origin == ImagingSetupChangeOrigin.OperatorDeclared &&
            !revision2.AutomaticallyDetectsAllPhysicalChanges &&
            revision2.InvalidatesEarlierCalibrationProfiles,
            "ImagingCalibrationRevisionTwoInvalid");

        var oldProfileCheck = await resolver.CheckAsync(recipe,
            new[] { new CalibrationProfileSelection(requirement.ContentHash, candidate.Reference) }, invocation);
        Require(!oldProfileCheck.Compatible &&
            oldProfileCheck.ReasonCode == "CalibrationImagingSetupRevisionMismatch" &&
            !oldProfileCheck.CalibrationAllowsNonProductionDebug && !oldProfileCheck.CanActivate,
            "ImagingCalibrationOldProfileFallbackAccepted");

        var noRequirement = await resolver.CheckAsync(Recipe(null), Array.Empty<CalibrationProfileSelection>(), invocation);
        Require(noRequirement.Available && noRequirement.ReasonCode == "CalibrationNotRequired" &&
            noRequirement.CalibrationAllowsNonProductionDebug &&
            noRequirement.CalibrationActivationGateSatisfied && !noRequirement.CanActivate,
            "ImagingCalibrationNoRequirementDebugGateInvalid");

        await RenderImagingSetupPanelAsync(imaging, setup, sessions, stepUp, binding, revision2, directory);
        await Verified(runtime);
        var state = await runtime.GetSnapshotAsync();
        Require(!state.Ready && state.ArmState == ProductionArmState.Disarmed,
            "ImagingCalibrationGrantedProductionAuthority");
        var diagnostics = camera.GetDiagnostics();
        var device = diagnostics.Devices.Single(item => item.StableDeviceIdentity == Device);
        Require(device.OpenCount == 2 && device.FramesProduced == 0 &&
            device.OutstandingLeases == 0 && device.IsOpen,
            "ImagingCalibrationUnexpectedAcquisitionOrOpen");

        await File.WriteAllTextAsync(Path.Combine(directory, "imaging-calibration-evidence.json"),
            JsonSerializer.Serialize(new
            {
                Result = "Pass", LogicalRole = Role, ProviderIdentity = camera.Identity,
                BoundDevice = Device, BindingRevision = binding.Revision, BindingRevisionHash = binding.RevisionHash,
                ImagingRevision1 = revision1.Revision, ImagingRevision1Id = revision1.RevisionId,
                ImagingRevision1Hash = revision1.RevisionHash, ImagingRevision2 = revision2.Revision,
                ImagingRevision2Id = revision2.RevisionId, ImagingRevision2Hash = revision2.RevisionHash,
                ImagingRevision2PredecessorHash = revision2.PreviousRevisionHash,
                Revision1Actor = revision1.ActorPrincipalId, Revision2Actor = revision2.ActorPrincipalId,
                Revision1Reason = revision1.ChangeReason, Revision2Reason = revision2.ChangeReason,
                Revision1Origin = revision1.Origin, Revision2Origin = revision2.Origin,
                CandidateProfileId = candidate.Reference.ProfileId, CandidateProfileVersion = candidate.Reference.Version,
                CandidateProfileHash = candidate.Reference.ContentHash,
                HistoricalProfileId = historical.Reference.ProfileId, HistoricalProfileVersion = historical.Reference.Version,
                HistoricalProfileHash = historical.Reference.ContentHash,
                CandidateCompatible = candidateCheck.Compatible, HistoricalCompatible = historicalCheck.Compatible,
                CandidateEvidencePurpose = candidateCheck.EvidencePurpose,
                HistoricalEvidencePurpose = historicalCheck.EvidencePurpose,
                CandidateProductionAuthority = candidateCheck.ProductionAuthority,
                CandidateCanActivate = candidateCheck.CanActivate,
                UiPanelScreenshot = "imaging-setup-panel.png",
                UiPanelFormScreenshot = "imaging-setup-panel-form.png",
                UiPanelShowsBindingAndHistory = true, UiPanelSubmissionFormEnabled = true,
                UiPasswordBoxEmpty = true,
                OldProfileRejected = !oldProfileCheck.Compatible,
                OldProfileReason = oldProfileCheck.ReasonCode,
                MissingStepUpRejected = !missingStepUp.Succeeded, MissingStepUpReason = missingStepUp.ReasonCode,
                WrongExpectedRevisionRejected = !wrongRevision.Succeeded,
                WrongExpectedRevisionReason = wrongRevision.ReasonCode,
                WrongExpectedHashRejected = !wrongHash.Succeeded, WrongExpectedHashReason = wrongHash.ReasonCode,
                ExactOperationReplayPreserved = replay.Succeeded && replay.Revision?.RevisionHash == revision1.RevisionHash,
                ConflictingOperationReplayRejected = !conflictingReplay.Succeeded,
                ConflictingOperationReplayReason = conflictingReplay.ReasonCode,
                NoRequirementDebugOnly = noRequirement.CalibrationAllowsNonProductionDebug,
                NoAutomaticPhysicalDetection = !revision2.AutomaticallyDetectsAllPhysicalChanges,
                NoDefaultCoefficientContract = true, ProductionReady = false, CanActivate = false,
                Ready = state.Ready, ArmState = state.ArmState, OpenCount = device.OpenCount,
                DebugDeviceOpen = device.IsOpen, FramesProduced = device.FramesProduced, PhysicalDevices = "NotRun",
                PhysicalHardwareQualification = "NotRun", ProviderQualification = "NotRun",
                StationAcceptance = "NotRun", Production = "NotRun",
                NativeCrashIsolation = "NotRun"
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task RenderImagingSetupPanelAsync(IImagingSetupRuntime imaging,
        ICameraSetupRuntime setup, IInteractiveSessionService sessions, IStepUpAuthentication stepUp,
        CameraBindingRevision binding, ImagingSetupRevision revision, string directory)
    {
        await using var model = new ImagingSetupViewModel(imaging, setup, sessions, stepUp,
            new DispatcherUiDispatcher(Dispatcher.CurrentDispatcher));
        model.LogicalCameraRole = Role;
        var panel = new ImagingSetupPanel(model);
        var window = new Window
        {
            Content = panel,
            Width = 1160,
            Height = 960,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000
        };

        try
        {
            window.Show();
            await Flush();
            await model.RefreshAsync();
            await Until(() => !model.IsBusy);
            Require(model.CurrentBinding is { } currentBinding &&
                currentBinding.Revision == binding.Revision &&
                currentBinding.RevisionHash == binding.RevisionHash &&
                model.CurrentRevision is { } currentRevision &&
                currentRevision.Revision == revision.Revision &&
                currentRevision.RevisionHash == revision.RevisionHash &&
                model.History.Count == 2,
                "ImagingCalibrationUiReadBackInvalid");

            var declareButton = (Button)panel.FindName("DeclareButton");
            var passwordBox = (PasswordBox)panel.FindName("StepUpPasswordBox");
            Require(declareButton.IsEnabled && passwordBox.IsEnabled && passwordBox.Password.Length == 0,
                "ImagingCalibrationUiSubmissionFormUnavailable");

            var scroll = (ScrollViewer)panel.FindName("ImagingScrollViewer");
            scroll.ScrollToHome();
            await Flush();
            SaveWindow(window, Path.Combine(directory, "imaging-setup-panel.png"));
            scroll.ScrollToEnd();
            await Flush();
            SaveWindow(window, Path.Combine(directory, "imaging-setup-panel-form.png"));
            scroll.ScrollToHome();
            await Flush();
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task QueryCore(ProductionStoreOptions options, string directory,
        string userName, string expectedPrincipal, string password)
    {
        Require(options.ImagingSetup is not null, "ImagingSetupStoreNotEnabled");
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(directory, "imaging-calibration-evidence.json")));
        var root = evidence.RootElement;
        using var clock = new VirtualCameraClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var camera = CreateProvider(clock);
        await using var container = Services(options, camera).BuildServiceProvider();
        var runtime = container.GetRequiredService<IStationRuntime>();
        var setup = container.GetRequiredService<ICameraSetupRuntime>();
        var imaging = container.GetRequiredService<IImagingSetupRuntime>();
        var sessions = container.GetRequiredService<IInteractiveSessionService>();
        await Verified(runtime);
        await SignIn(sessions, runtime, userName, expectedPrincipal, password);

        var databaseBefore = await DatabaseHash(options.DatabasePath);
        var binding = await setup.GetSetupAsync(Role, Invocation(sessions));
        var current = await imaging.GetImagingSetupAsync(Role, Invocation(sessions));
        var history = await imaging.QueryImagingSetupHistoryAsync(Role, Invocation(sessions), pageSize: 10);
        var databaseAfter = await DatabaseHash(options.DatabasePath);
        Require(databaseBefore == databaseAfter, "ImagingCalibrationReadOnlyQueryChangedDatabase");
        Require(binding.Available && binding.Snapshot?.Binding is not null &&
            binding.Snapshot.Binding.Revision == root.GetProperty("BindingRevision").GetInt64() &&
            binding.Snapshot.Binding.RevisionHash == root.GetProperty("BindingRevisionHash").GetString(),
            "ImagingCalibrationRestartBindingUnavailable");
        Require(current.Available && current.Current is not null && history.Available &&
            history.Revisions.Count == 2 && current.Current.Revision == 2,
            "ImagingCalibrationRestartHistoryUnavailable");
        var first = history.Revisions.OrderBy(item => item.Revision).First();
        var second = history.Revisions.OrderBy(item => item.Revision).Last();
        Require(first.Revision == root.GetProperty("ImagingRevision1").GetInt64() &&
            first.RevisionHash == root.GetProperty("ImagingRevision1Hash").GetString() &&
            second.Revision == root.GetProperty("ImagingRevision2").GetInt64() &&
            second.RevisionHash == root.GetProperty("ImagingRevision2Hash").GetString() &&
            second.PreviousRevisionHash == first.RevisionHash &&
            first.ActorPrincipalId == Guid.Parse(root.GetProperty("Revision1Actor").GetString()!) &&
            first.ChangeReason == root.GetProperty("Revision1Reason").GetString() &&
            second.ActorPrincipalId == Guid.Parse(root.GetProperty("Revision2Actor").GetString()!) &&
            second.ChangeReason == root.GetProperty("Revision2Reason").GetString() &&
            first.Origin == ImagingSetupChangeOrigin.OperatorDeclared &&
            second.Origin == ImagingSetupChangeOrigin.OperatorDeclared &&
            !second.AutomaticallyDetectsAllPhysicalChanges,
            "ImagingCalibrationRestartRevisionEvidenceInvalid");
        var exposesCoefficient = JsonSerializer.Serialize(current.Current)
            .Contains("Coefficient", StringComparison.OrdinalIgnoreCase);
        Require(!exposesCoefficient, "ImagingCalibrationRestartExposedDefaultCoefficient");
        var state = await runtime.GetSnapshotAsync();
        var diagnostics = camera.GetDiagnostics();
        Require(!state.Ready && state.ArmState == ProductionArmState.Disarmed &&
            diagnostics.Devices.Single(item => item.StableDeviceIdentity == Device).OpenCount == 0 &&
            diagnostics.Devices.All(item => item.FramesProduced == 0),
            "ImagingCalibrationRestartInheritedDeviceState");

        await File.WriteAllTextAsync(Path.Combine(directory, "imaging-calibration-restart.json"),
            JsonSerializer.Serialize(new
            {
                Result = "Pass", LogicalRole = Role, CurrentRevision = current.Current.Revision,
                CurrentRevisionHash = current.Current.RevisionHash, HistoryCount = history.Revisions.Count,
                Revision1Id = first.RevisionId, Revision1Hash = first.RevisionHash,
                Revision1Actor = first.ActorPrincipalId, Revision1Reason = first.ChangeReason,
                Revision2Id = second.RevisionId, Revision2Hash = second.RevisionHash,
                Revision2PredecessorHash = second.PreviousRevisionHash, Revision2Actor = second.ActorPrincipalId,
                Revision2Reason = second.ChangeReason, HashesPersisted = true, ExactBinding = true,
                ReadOnlyQueryDatabaseBytesUnchanged = true, OpenedDevices = 0, FramesProduced = 0,
                ConfigurationNotInherited = true, NoDefaultCoefficientContract = !exposesCoefficient,
                ProductionReady = false, CanActivate = false,
                Ready = state.Ready, PhysicalDevices = "NotRun", ProviderQualification = "NotRun",
                PhysicalHardwareQualification = "NotRun", StationAcceptance = "NotRun",
                Production = "NotRun", NativeCrashIsolation = "NotRun"
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static VirtualCameraProvider CreateProvider(VirtualCameraClock clock)
    {
        var capabilities = new CameraCapabilities(new[] { ProductionAcquisitionMode.SoftwareTrigger },
            new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
            new(10, 10000, 1, CameraQuantizationMode.Nearest, 0.5),
            new(0, 24, 1, CameraQuantizationMode.Exact),
            new(0, 10000, 1, CameraQuantizationMode.Exact),
            new(128, 96, new(0, 127, 1), new(0, 95, 1), new(1, 128, 1), new(1, 96, 1)));
        var image = VirtualCameraImage.CreateSynthetic("Calibration", 64, 48,
            VisionPixelFormat.Mono8, null, 123);
        var scenario = new VirtualCameraScenario("ImagingCalibration", "1", 123, Device,
            capabilities, new[] { image }, Array.Empty<VirtualCameraAcquisitionPlan>(),
            new[] { new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero) });
        return new VirtualCameraProvider(new[] { scenario }, clock);
    }

    private static RequestedCameraConfiguration RequestedConfiguration() => new(
        ProductionAcquisitionMode.SoftwareTrigger, 1000.4, 0,
        new RegionOfInterest(0, 0, 64, 48), VisionPixelFormat.Mono8, null, 1000, 0, null);

    private static CalibrationRequirement Requirement() => new(Role, CalibrationKind.Intrinsic,
        "Undistortion", new(CoefficientContractId, CoefficientContractVersion, new string('C', 64)),
        new(AcceptancePolicyId, AcceptancePolicyVersion, new string('A', 64)));

    private static RecipeDraftContent Recipe(CalibrationRequirement? requirement)
    {
        var schema = new AlgorithmConfigurationSchema("ImagingCalibration.Config", "1",
            Array.Empty<AlgorithmFieldDefinition>());
        var configuration = AlgorithmConfigurationSnapshot.Create(schema,
            Array.Empty<AlgorithmConfigurationEntry>());
        var overlay = new OverlayContract("ImagingCalibration.Overlay", "1", 0, 0, 0);
        var result = new AlgorithmResultSchema("ImagingCalibration.Result", "1",
            Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>(), overlay);
        var algorithm = new RecipeAlgorithmBinding(
            new AlgorithmIdentity("ImagingCalibration.Algorithm", "1"), schema,
            new(result.Id, result.Version, result.ContentHash),
            new(overlay.Id, overlay.Version, overlay.ContentHash));
        var camera = RequestedConfiguration();
        return new RecipeDraftContent("ImagingCalibrationRecipe", "Imaging calibration fixture recipe",
            algorithm, configuration, Role, camera, TimeSpan.FromMilliseconds(250), null, null,
            calibrationRequirements: requirement is null ? null : new[] { requirement });
    }

    private static CalibrationProfileContent ProfileContent(CalibrationRequirement requirement,
        CameraBindingTarget target, ImagingSetupRevision revision,
        RequestedCameraConfiguration requested, EffectiveCameraConfiguration effective,
        Guid actor, string sourceEvidence) => new(requirement, target,
            ImagingSetupRevisionReference.FromRevision(revision),
            CalibrationFrameGeometry.FromRequested(requested), CalibrationFrameGeometry.FromEffective(effective),
            new CalibrationCoefficientPayload(requirement.CoefficientContract,
                Encoding.UTF8.GetBytes("fixture-intrinsic-v1;no-default")),
            new RecipeContractReference(ProcedureId, ProcedureVersion, new string('D', 64)),
            sourceEvidence, actor, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static async Task<CameraBindingRevision> RebindAsync(ICameraSetupRuntime setup,
        IStepUpAuthentication stepUp, IInteractiveSessionService sessions,
        CameraBindingTarget target, string password)
    {
        var operation = Guid.NewGuid();
        var grant = await GrantAsync(stepUp, sessions, operation,
            AuditedCommandKind.RebindCamera, password);
        var result = await setup.RebindAsync(new(operation,
            Invocation(sessions) with { StepUpGrantId = grant.GrantId }, Role, 0, null,
            target, "Bind virtual imaging calibration camera"));
        Require(result.Succeeded && result.Audit == AuditPersistence.Persisted &&
            result.Snapshot?.Binding is not null, "ImagingCalibrationRebindFailed_" + result.ReasonCode);
        return result.Snapshot!.Binding!;
    }

    private static async Task<CameraSetupSnapshot> ApplyAsync(ICameraSetupRuntime setup,
        IStepUpAuthentication stepUp, IInteractiveSessionService sessions,
        CameraBindingRevision binding, RequestedCameraConfiguration requested, string password)
    {
        var operation = Guid.NewGuid();
        var grant = await GrantAsync(stepUp, sessions, operation,
            AuditedCommandKind.ApplyCameraDebugConfiguration, password);
        var result = await setup.ApplyDebugConfigurationAsync(new(operation,
            Invocation(sessions) with { StepUpGrantId = grant.GrantId }, Role,
            binding.Revision, binding.RevisionHash, requested,
            "Apply complete virtual imaging calibration configuration"));
        Require(result.Succeeded && result.Audit == AuditPersistence.Persisted &&
            result.Snapshot is not null, "ImagingCalibrationDebugApplyFailed_" + result.ReasonCode);
        return result.Snapshot!;
    }

    private static async Task<ImagingSetupChangeResult> DeclareAsync(IImagingSetupRuntime imaging,
        IStepUpAuthentication stepUp, IInteractiveSessionService sessions,
        CameraBindingRevision binding, long expectedRevision, string? expectedHash,
        ImagingSetupDefinition definition, string reason, string password, Guid? operationId = null)
    {
        var operation = operationId ?? Guid.NewGuid();
        var invocation = Invocation(sessions);
        var intended = new ImagingSetupChangeRequest(operation, invocation, Role,
            binding.Revision, binding.RevisionHash, expectedRevision, expectedHash,
            definition, reason);
        var grant = await GrantAsync(stepUp, sessions, operation,
            AuditedCommandKind.DeclareImagingSetup, password, intended.AuthorizationTarget);
        return await imaging.DeclareImagingSetupAsync(new(operation,
            invocation with { StepUpGrantId = grant.GrantId }, Role,
            binding.Revision, binding.RevisionHash, expectedRevision, expectedHash,
            definition, reason));
    }

    private static async Task<StepUpResult> GrantAsync(IStepUpAuthentication stepUp,
        IInteractiveSessionService sessions, Guid operation, AuditedCommandKind command,
        string password, string? targetId = null)
    {
        var invocation = Invocation(sessions);
        var grant = await stepUp.ReauthenticateAsync(new(Guid.NewGuid(), invocation,
            new StepUpBinding(Permission.ManageCameraBindings, operation, targetId ?? Role, command), password));
        Require(grant.Succeeded && grant.GrantId is not null,
            "ImagingCalibrationStepUpFailed_" + grant.ReasonCode);
        return grant;
    }

    private static CommandInvocation Invocation(IInteractiveSessionService sessions) =>
        new(CommandSource.PhysicalConsole, sessions.Current.PrincipalId, sessions.Current.SessionId);

    private static async Task SignIn(IInteractiveSessionService sessions, IStationRuntime runtime,
        string userName, string expectedPrincipal, string password)
    {
        var signedIn = await sessions.SignInAsync(new(userName, password));
        Require(signedIn.Succeeded && signedIn.Identity?.PrincipalId.ToString("D") == expectedPrincipal,
            "ImagingCalibrationConsumerAuthenticationFailed");
        await Verified(runtime);
    }

    private static async Task Verified(IStationRuntime runtime)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var state = await runtime.GetSnapshotAsync(timeout.Token);
            var integrity = state.AuditIntegrity?.State;
            if (integrity == AuditIntegrityState.Verified) return;
            Require(integrity != AuditIntegrityState.Faulted, "ImagingCalibrationAuditUnavailable");
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task<string> DatabaseHash(string path)
    {
        var hashes = new List<string>();
        foreach (var file in new[] { path, path + "-wal" })
        {
            if (!File.Exists(file)) { hashes.Add("Absent"); continue; }
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var algorithm = SHA256.Create();
            hashes.Add(Convert.ToHexString(await algorithm.ComputeHashAsync(stream)));
        }
        return string.Join(":", hashes);
    }

    private static async Task Until(Func<bool> complete)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            await Task.Delay(20);
            Require(watch.Elapsed < TimeSpan.FromSeconds(25), "ImagingCalibrationUiWaitTimeout");
        }
        while (!complete());

        await Flush();
    }

    private static async Task Flush() =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    private static void SaveWindow(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var width = Math.Max(1, (int)Math.Ceiling(content.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(content.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Pump(Func<Task> operation)
    {
        Exception? failure = null;
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            try { await operation(); }
            catch (Exception exception) { failure = exception; }
            finally { frame.Continue = false; }
        }));
        Dispatcher.PushFrame(frame);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Require([DoesNotReturnIf(false)] bool condition, string reason)
    {
        if (!condition) throw new ImagingCalibrationCheckException(reason);
    }

    private sealed class ImagingCalibrationCheckException : Exception
    {
        internal ImagingCalibrationCheckException(string reasonCode) => ReasonCode = reasonCode;
        internal string ReasonCode { get; }
    }

}
