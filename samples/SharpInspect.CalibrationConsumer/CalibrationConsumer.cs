using System.IO;
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
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using SharpInspect.Wpf;

namespace SharpInspect.CalibrationConsumer;

internal enum CalibrationConsumerMode
{
    Bootstrap,
    Run,
    Restart,
    Wpf,
    All
}

internal sealed record CalibrationConsumerArguments(
    CalibrationConsumerMode Mode,
    string Directory,
    string DatabasePath,
    string EvidenceRoot,
    string AuditKey,
    string AuditKeyDirectory,
    string StationId,
    string? IdentityPolicyPath,
    string? AlarmPolicyPath,
    string UserName,
    string DisplayName,
    string? ExpectedPrincipal,
    bool Render)
{
    internal static bool TryParse(string[] args, out CalibrationConsumerArguments arguments,
        out string error)
    {
        arguments = null!;
        error = string.Empty;

        string? Option(string name)
        {
            var index = Array.FindIndex(args, value =>
                string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        var modeText = Option("--mode");
        var directory = Option("--directory") ?? Option("--calibration-directory");
        if ((Option("--calibration-check") ?? Option("--calibration-session-check")) is { } runDirectory)
        {
            directory ??= runDirectory;
            modeText ??= "run";
        }
        if ((Option("--calibration-restart") ?? Option("--calibration-session-restart")) is { } restartDirectory)
        {
            directory ??= restartDirectory;
            modeText ??= "restart";
        }
        if ((Option("--calibration-wpf") ?? Option("--calibration-session-wpf")) is { } wpfDirectory)
        {
            directory ??= wpfDirectory;
            modeText ??= "wpf";
        }
        if ((Option("--calibration-all") ?? Option("--calibration-session-all")) is { } allDirectory)
        {
            directory ??= allDirectory;
            modeText ??= "all";
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            error = "directory-required";
            return false;
        }

        CalibrationConsumerMode mode;
        switch (modeText?.Trim().ToLowerInvariant())
        {
            case "bootstrap": mode = CalibrationConsumerMode.Bootstrap; break;
            case "run": mode = CalibrationConsumerMode.Run; break;
            case "restart": mode = CalibrationConsumerMode.Restart; break;
            case "wpf": mode = CalibrationConsumerMode.Wpf; break;
            case "all": mode = CalibrationConsumerMode.All; break;
            default:
                error = "mode-must-be-bootstrap-run-restart-wpf-or-all";
                return false;
        }

        try
        {
            var fullDirectory = Path.GetFullPath(directory);
            var database = Path.GetFullPath(Option("--trace-db") ??
                Path.Combine(fullDirectory, "calibration-session.sqlite"));
            var evidence = Path.GetFullPath(Option("--evidence-root") ??
                Path.Combine(fullDirectory, "calibration-evidence"));
            var auditKeyDirectory = Path.GetFullPath(Option("--audit-key-directory") ??
                Path.Combine(fullDirectory, "private-keys"));
            var auditKey = Option("--audit-key") ?? "SharpInspect.CalibrationConsumer";
            var stationId = Option("--station-id") ?? "SampleDevelopmentStation";
            var userName = Option("--user-name") ?? string.Empty;
            var displayName = Option("--display-name") ?? "Calibration Consumer Administrator";
            var expectedPrincipal = Option("--expected-principal");
            if (expectedPrincipal is not null && !Guid.TryParse(expectedPrincipal, out _))
            {
                error = "expected-principal-invalid";
                return false;
            }

            arguments = new CalibrationConsumerArguments(mode, fullDirectory, database, evidence,
                auditKey, auditKeyDirectory, stationId,
                Option("--identity-policy") is { } identityPolicy
                    ? Path.GetFullPath(identityPolicy) : null,
                Option("--alarm-policy") is { } alarmPolicy
                    ? Path.GetFullPath(alarmPolicy) : null,
                userName, displayName,
                expectedPrincipal, !args.Contains("--no-wpf", StringComparer.OrdinalIgnoreCase));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            error = "path-invalid";
            return false;
        }
    }
}

internal static class CalibrationConsumer
{
    private const string Role = "TopCamera";
    private const string Device = "Virtual:Calibration-Consumer";
    private const string FixtureId = "CalibrationConsumerDeterministicFixture";
    private const string ContractVersion = "sharpinspect-calibration-session-consumer-v1";
    private const int SchemaVersion = 14;
    private const uint ScenarioSeed = 0xC0DE_1240;
    private static readonly DateTimeOffset InitialUtc =
        new(2026, 9, 9, 1, 2, 3, TimeSpan.Zero);

    internal static string ReadPassword()
    {
        var input = new StringBuilder();
        for (var index = 0; index <= 16 * 1024; index++)
        {
            var next = Console.In.Read();
            if (next is -1 or '\n')
            {
                try
                {
                    return JsonSerializer.Deserialize<string>(input.ToString()) ??
                        throw new CalibrationConsumerCheckException("password-required");
                }
                catch (JsonException)
                {
                    throw new CalibrationConsumerCheckException("password-input-invalid");
                }
            }
            if (next != '\r') input.Append((char)next);
        }
        throw new CalibrationConsumerCheckException("password-input-too-long");
    }

    internal static async Task<string> BootstrapAsync(
        CalibrationConsumerArguments arguments, string password)
    {
        if (string.IsNullOrWhiteSpace(arguments.UserName))
            throw new CalibrationConsumerCheckException("bootstrap-user-name-required");
        if (string.IsNullOrWhiteSpace(arguments.DisplayName))
            throw new CalibrationConsumerCheckException("bootstrap-display-name-required");

        Directory.CreateDirectory(arguments.Directory);
        Directory.CreateDirectory(arguments.EvidenceRoot);
        var options = CreateStoreOptions(arguments);
        Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
        Require(!File.Exists(options.DatabasePath) &&
            !File.Exists(options.DatabasePath + "-wal") &&
            !File.Exists(options.DatabasePath + "-shm"),
            "bootstrap-database-must-be-fresh");

        HumanIdentity? identity = null;
        await using (var container = new ServiceCollection()
            .AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(20))
            .BuildServiceProvider())
        {
            var bootstrap = container.GetRequiredService<ILocalAdministratorBootstrap>();
            var status = await bootstrap.GetStatusAsync().ConfigureAwait(true);
            Require(status.BootstrapRequired && status.UsableAdministratorCount == 0,
                "bootstrap-identity-store-is-not-empty");

            var issued = await bootstrap.ProvisionBootstrapTokenAsync()
                .ConfigureAwait(true);
            Require(issued.Succeeded && issued.Token is not null,
                "bootstrap-token-issuance-failed-" + issued.ReasonCode);
            var tokenSecret = issued.Token!;
            using (tokenSecret)
            {
                var token = tokenSecret.TakeForDisplay();
                var created = await bootstrap.CreateFirstAdministratorAsync(
                    new BootstrapAdministratorRequest(arguments.StationId, token,
                        arguments.UserName, arguments.DisplayName, password))
                    .ConfigureAwait(true);
                created.RecoveryKit?.Dispose();
                Require(created.Succeeded && created.Identity is not null,
                    "bootstrap-administrator-creation-failed-" + created.ReasonCode);
                identity = created.Identity!;
            }
        }

        Require(identity is not null, "bootstrap-identity-result-missing");
        var evidencePath = Path.Combine(arguments.Directory, "calibration-bootstrap.json");
        var json = JsonSerializer.Serialize(new
        {
            result = "Pass",
            schema = SchemaVersion,
            stationId = arguments.StationId,
            userName = identity!.UserName,
            displayName = identity!.DisplayName,
            principalId = identity.PrincipalId,
            databasePath = options.DatabasePath,
            evidencePath,
            freshSchema14 = true,
            bootstrap = "ILocalAdministratorBootstrap.ProvisionBootstrapTokenAsync + CreateFirstAdministratorAsync",
            recoveryKitDelivered = false,
            physicalHardwareQualification = "NotRun",
            stationAcceptance = "NotRun",
            production = "NotRun"
        }, JsonOptions());
        await File.WriteAllTextAsync(evidencePath, json).ConfigureAwait(true);
        return json;
    }

    internal static async Task RunAsync(CalibrationConsumerArguments arguments,
        string password, bool render)
    {
        Directory.CreateDirectory(arguments.Directory);
        Directory.CreateDirectory(arguments.EvidenceRoot);
        var options = CreateStoreOptions(arguments);
        var phaseA = await RunPhaseAAsync(arguments, options, password).ConfigureAwait(true);
        var plan = CreatePlan();
        var fixture = new DevelopmentCalibrationFixture(FixtureId, phaseA.Binding,
            ImagingSetupRevisionReference.FromRevision(phaseA.ImagingRevision),
            phaseA.BaselineRequested, phaseA.BaselineEffective, plan, options);
        var phaseB = await RunPhaseBAsync(arguments, options, fixture, phaseA, password,
            render && arguments.Render).ConfigureAwait(true);
        var databaseHash = await DatabaseHashAsync(options.DatabasePath).ConfigureAwait(true);
        await WriteRunEvidenceAsync(arguments, options, fixture, phaseA, phaseB,
            databaseHash).ConfigureAwait(true);
    }

    internal static async Task RestartAsync(CalibrationConsumerArguments arguments,
        string password)
    {
        var evidencePath = Path.Combine(arguments.Directory, "calibration-session-evidence.json");
        if (!File.Exists(evidencePath))
            throw new CalibrationConsumerCheckException("run-evidence-required-for-restart");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(evidencePath)
            .ConfigureAwait(true));
        var root = document.RootElement;
        Require(root.GetProperty("result").GetString() == "Pass" &&
            root.GetProperty("schema").GetInt32() == SchemaVersion,
            "run-evidence-contract-invalid");
        var sessionId = ParseGuid(root, "sessionId");
        var options = CreateStoreOptions(arguments);
        if (!File.Exists(options.DatabasePath))
            throw new CalibrationConsumerCheckException("calibration-database-required-for-restart");

        var services = new ServiceCollection();
        // The restart process is intentionally query-only: no provider, device,
        // acquisition or recovery owner is constructed here.
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(20));
        await using var container = services.BuildServiceProvider();
        var runtime = container.GetRequiredService<IStationRuntime>();
        var sessions = container.GetRequiredService<IInteractiveSessionService>();
        var query = container.GetRequiredService<ICalibrationSessionQuery>();
        var setup = container.GetRequiredService<ICameraSetupRuntime>();
        var imaging = container.GetRequiredService<IImagingSetupRuntime>();
        await WaitForRuntimeAsync(runtime, state =>
            state.Lifecycle == RuntimeLifecycle.Running &&
            state.AuditIntegrity?.State == AuditIntegrityState.Verified).ConfigureAwait(true);
        await SignInAsync(sessions, runtime, arguments.UserName,
            arguments.ExpectedPrincipal, password).ConfigureAwait(true);

        // Sign-in itself is an auditable state change. Establish the read-only
        // comparison after authentication and before the evidence queries.
        var before = await DatabaseHashAsync(options.DatabasePath).ConfigureAwait(true);
        var invocation = CurrentInvocation(sessions);
        var result = await query.QueryCalibrationSessionAsync(sessionId, invocation)
            .ConfigureAwait(true);
        Require(result.Available && result.Evidence is not null,
            "restart-calibration-evidence-unavailable-" + result.ReasonCode);
        var evidence = result.Evidence!;
        var persistedSetup = await setup.GetSetupAsync(Role, invocation).ConfigureAwait(true);
        Require(persistedSetup.Available && persistedSetup.Snapshot is { } persistedSnapshot &&
            persistedSnapshot.Binding is { } persistedBinding &&
            persistedBinding.Revision == evidence.Header.Binding.Revision &&
            persistedBinding.RevisionHash == evidence.Header.Binding.RevisionHash &&
            persistedSnapshot.Health.Connection == CameraConnectionState.Closed &&
            persistedSnapshot.Effective is null,
            "restart-camera-baseline-not-persisted-readonly");
        var persistedImaging = await imaging.GetImagingSetupAsync(Role, invocation)
            .ConfigureAwait(true);
        Require(persistedImaging.Available && persistedImaging.Current is { } currentImaging &&
            currentImaging.RevisionHash == evidence.Header.Command.ExpectedImagingSetup.RevisionHash &&
            currentImaging.Binding.RevisionHash == evidence.Header.Binding.RevisionHash,
            "restart-imaging-baseline-not-persisted-readonly");
        Require(evidence.State.Phase == CalibrationSessionPhase.Restored &&
            evidence.State.Outcome == CalibrationSessionOutcome.Completed &&
            evidence.State.RestorationVerified, "restart-session-not-restored");
        Require(evidence.Frames.Count == 3 && evidence.Observations.Count == 3 &&
            evidence.Exclusions.Count == 1 && evidence.Candidate is not null,
            "restart-evidence-shape-invalid");

        var frame = evidence.Frames[0];
        var image = await query.ReadCalibrationFrameAsync(sessionId, frame.FrameId,
            frame.SourceHash, invocation).ConfigureAwait(true);
        Require(image.Available && image.Image is not null,
            "restart-frame-image-unavailable-" + image.ReasonCode);
        var imageValue = image.Image!;
        Require(imageValue.GetBytes().Length == frame.ByteLength,
            "restart-frame-image-length-invalid");
        var after = await DatabaseHashAsync(options.DatabasePath).ConfigureAwait(true);
        Require(string.Equals(before, after, StringComparison.Ordinal),
            "restart-read-only-query-mutated-database");

        var state = await runtime.GetSnapshotAsync().ConfigureAwait(true);
        Require(!state.Ready && state.ArmState == ProductionArmState.Disarmed,
            "restart-production-admission-changed");
        Require(state.Handshake == HandshakePhase.Unknown &&
            state.Recovery == RecoveryState.Required,
            "restart-global-safety-state-changed");
        await File.WriteAllTextAsync(Path.Combine(arguments.Directory,
            "calibration-session-restart.json"), JsonSerializer.Serialize(new
            {
                result = "Pass",
                schema = SchemaVersion,
                sessionId,
                state = evidence.State,
                frameCount = evidence.Frames.Count,
                observationCount = evidence.Observations.Count,
                exclusionCount = evidence.Exclusions.Count,
                candidateHash = evidence.Candidate!.ContentHash,
                frameImageBytes = imageValue.GetBytes().Length,
                readOnlyQueryDatabaseUnchanged = true,
                openedDevices = 0,
                providerRegistered = false,
                ready = state.Ready,
                handshake = state.Handshake,
                recovery = state.Recovery,
                productionOutputsAbsent = true,
                physicalHardwareQualification = "NotRun",
                providerQualification = "NotRun",
                stationAcceptance = "NotRun",
                production = "NotRun"
            }, JsonOptions())).ConfigureAwait(true);
    }

    private static async Task<PhaseAResult> RunPhaseAAsync(
        CalibrationConsumerArguments arguments, ProductionStoreOptions options, string password)
    {
        if (!File.Exists(options.DatabasePath))
            throw new CalibrationConsumerCheckException("identity-store-required-for-phase-a");

        using var clock = new VirtualCameraClock(InitialUtc);
        var scenario = CreateScenario();
        var provider = new VirtualCameraProvider(new[] { scenario }, clock, poolCapacity: 2);
        ServiceProvider? container = null;
        CameraBindingRevision? binding = null;
        ImagingSetupRevision? imagingRevision = null;
        CameraSetupOperationResult? rebind = null;
        CameraSetupOperationResult? apply = null;
        ImagingSetupChangeResult? imaging = null;
        var missingStepUpReason = string.Empty;
        var baselineRequested = BaselineRequestedConfiguration();
        try
        {
            var services = new ServiceCollection();
            services.AddSharpInspectCameraProvider(provider);
            services.AddSharpInspectCameraSetup(new CameraSetupOptions
            {
                OperationTimeout = TimeSpan.FromSeconds(15),
                ShutdownTimeout = TimeSpan.FromSeconds(10)
            });
            // No CameraAcquisitionService, CameraRecoveryService or calibration
            // owner is registered in phase A.
            services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(20));
            container = services.BuildServiceProvider();
            var runtime = container.GetRequiredService<IStationRuntime>();
            var setup = container.GetRequiredService<ICameraSetupRuntime>();
            var imagingRuntime = container.GetRequiredService<IImagingSetupRuntime>();
            var sessions = container.GetRequiredService<IInteractiveSessionService>();
            var stepUp = container.GetRequiredService<IStepUpAuthentication>();

            await WaitForRuntimeAsync(runtime, state =>
                state.Lifecycle == RuntimeLifecycle.Running &&
                state.AuditIntegrity?.State == AuditIntegrityState.Verified).ConfigureAwait(true);
            var anonymous = await setup.DiscoverAsync(provider.Identity,
                new CommandInvocation(CommandSource.PhysicalConsole)).ConfigureAwait(true);
            Require(!anonymous.Succeeded && anonymous.Devices.Count == 0,
                "phase-a-anonymous-discovery-accepted");
            await SignInAsync(sessions, runtime, arguments.UserName,
                arguments.ExpectedPrincipal, password).ConfigureAwait(true);

            var discovered = await setup.DiscoverAsync(provider.Identity,
                CurrentInvocation(sessions)).ConfigureAwait(true);
            Require(discovered.Succeeded && discovered.Devices.Count == 1 &&
                discovered.Devices[0].StableDeviceIdentity == Device,
                "phase-a-authorized-discovery-invalid");

            var target = new CameraBindingTarget(provider.Identity, Device);
            var missingStepUpOperation = Guid.NewGuid();
            var missingStepUp = await setup.RebindAsync(new CameraRebindRequest(
                missingStepUpOperation, CurrentInvocation(sessions), Role, 0, null,
                target, "Missing Step-Up must be rejected")).ConfigureAwait(true);
            Require(!missingStepUp.Succeeded, "phase-a-rebind-without-step-up-accepted");
            missingStepUpReason = missingStepUp.ReasonCode;

            var rebindResult = await RebindAsync(setup, stepUp, sessions, target, password)
                .ConfigureAwait(true);
            Require(rebindResult.Succeeded && rebindResult.Snapshot?.Binding is not null,
                "phase-a-rebind-failed-" + rebindResult.ReasonCode);
            rebind = rebindResult;
            var actualBinding = rebindResult.Snapshot!.Binding!;
            binding = actualBinding;

            var applyResult = await ApplyAsync(setup, stepUp, sessions, actualBinding, baselineRequested,
                password).ConfigureAwait(true);
            Require(applyResult.Succeeded && applyResult.Snapshot?.Effective is not null &&
                applyResult.Snapshot.Health.Connection == CameraConnectionState.Open &&
                applyResult.Snapshot.Health.Configuration == CameraConfigurationState.Applied &&
                applyResult.Snapshot.Health.Acquisition == CameraAcquisitionState.Stopped,
                "phase-a-apply-stopped-readback-failed-" + applyResult.ReasonCode);
            apply = applyResult;

            var imagingDefinition = new ImagingSetupDefinition("FixtureLens-Calibration",
                "Focus-100", "Mount-Top", 250, "SensorUp");
            var imagingResult = await DeclareImagingAsync(imagingRuntime, stepUp, sessions, actualBinding,
                imagingDefinition, "Record physical imaging baseline before calibration",
                password).ConfigureAwait(true);
            Require(imagingResult.Succeeded && imagingResult.Revision is not null &&
                imagingResult.Revision.Origin == ImagingSetupChangeOrigin.OperatorDeclared,
                "phase-a-imaging-declaration-failed-" + imagingResult.ReasonCode);
            imaging = imagingResult;
            imagingRevision = imagingResult.Revision!;
        }
        finally
        {
            if (container is not null)
                await container.DisposeAsync().ConfigureAwait(true);
            // A preconstructed provider registered as an instance remains Host-owned.
            // Camera Setup has already retired its device before the Host closes the provider.
            await provider.DisposeAsync().ConfigureAwait(true);
        }

        var diagnostics = provider.GetDiagnostics();
        Require(diagnostics.IsDisposed && diagnostics.Devices.All(device =>
            !device.IsOpen && device.OutstandingLeases == 0 && device.OpenCount >= 1 &&
            device.ConfigurationCursor >= 1),
            "phase-a-provider-disposal-or-call-proof-invalid");
        return new PhaseAResult(scenario.ContentHash, provider.Identity, binding!,
            imagingRevision!, baselineRequested, apply!.Snapshot!.Effective!, rebind!, apply!, imaging!,
            missingStepUpReason, diagnostics);
    }

    private static async Task<PhaseBResult> RunPhaseBAsync(
        CalibrationConsumerArguments arguments, ProductionStoreOptions options,
        DevelopmentCalibrationFixture fixture, PhaseAResult phaseA, string password,
        bool render)
    {
        using var clock = new VirtualCameraClock(InitialUtc);
        var scenario = CreateScenario();
        Require(scenario.ContentHash == phaseA.ScenarioHash,
            "phase-b-scenario-fingerprint-changed");
        var provider = new VirtualCameraProvider(new[] { scenario }, clock, poolCapacity: 2);
        CameraAcquisitionService? initialAcquisition = null;
        CameraRecoveryService? recovery = null;
        ServiceProvider? container = null;
        RuntimeCommandOutcome? startOutcome = null;
        RuntimeCommandOutcome? exitOutcome = null;
        CalibrationSessionEvidence? beforeExit = null;
        CalibrationSessionEvidence? afterExit = null;
        CalibrationFrameQueryResult? imageResult = null;
        StationStateSnapshot? finalState = null;
        var screenshots = Array.Empty<string>();
        var acceptedBeforeTerminal = false;
        try
        {
            var seed = await OpenSeedAsync(provider, fixture.Binding.Target,
                fixture.Plan.TemporaryConfiguration, fixture.Binding, phaseA.BaselineRequested,
                phaseA.BaselineEffective, clock).ConfigureAwait(true);
            var seedAcquisition = seed.Acquisition;
            initialAcquisition = seedAcquisition;
            var recoveryService = new CameraRecoveryService(provider, fixture.Binding.Target, Role,
                phaseA.BaselineRequested, seedAcquisition, clock,
                new CameraRecoveryOptions(TimeSpan.FromMilliseconds(10), 20,
                    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)));
            recovery = recoveryService;

            var services = new ServiceCollection();
            services.AddSharpInspectCameraRecovery(_ => recoveryService);
            services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(20));
            services.AddSharpInspectCalibrationProcedure<CalibrationFixtureInput>(
                new DeterministicCalibrationProcedure());
            services.AddSharpInspectCalibrationSessions(new CalibrationSessionOptions
            {
                OperationTimeout = TimeSpan.FromSeconds(5),
                DevelopmentFixture = fixture
            });
            Require(!services.Any(item => item.ServiceType == typeof(ICameraProvider)),
                "phase-b-provider-must-not-be-camera-provider-di");
            container = services.BuildServiceProvider();
            var runtime = container.GetRequiredService<IStationRuntime>();
            var query = container.GetRequiredService<ICalibrationSessionQuery>();
            var setup = container.GetRequiredService<ICameraSetupRuntime>();
            var imaging = container.GetRequiredService<IImagingSetupRuntime>();
            var sessions = container.GetRequiredService<IInteractiveSessionService>();
            var stepUp = container.GetRequiredService<IStepUpAuthentication>();

            await WaitForRuntimeAsync(runtime, state =>
                state.Lifecycle == RuntimeLifecycle.Running &&
                state.AuditIntegrity?.State == AuditIntegrityState.Verified).ConfigureAwait(true);
            await recoveryService.RefreshAsync().ConfigureAwait(true);
            Require(recoveryService.GetSnapshot() is { State: CameraRecoveryState.Healthy,
                SourceHealthy: true }, "phase-b-seed-recovery-not-healthy");
            await SignInAsync(sessions, runtime, arguments.UserName,
                arguments.ExpectedPrincipal, password).ConfigureAwait(true);

            var viewModel = new CalibrationSessionViewModel(runtime, query, setup, imaging,
                sessions, stepUp, new DispatcherUiDispatcher(Dispatcher.CurrentDispatcher));
            await using (viewModel)
            {
                viewModel.Configure(fixture.Plan);
                viewModel.StartReason = "Run deterministic calibration evidence fixture";
                await viewModel.RefreshAsync().ConfigureAwait(true);
                Require(viewModel.CurrentBinding is { } currentBinding &&
                    currentBinding.RevisionHash == fixture.Binding.RevisionHash &&
                    viewModel.CurrentImagingRevision is { } currentImaging &&
                    currentImaging.RevisionHash == fixture.ImagingSetup.RevisionHash,
                    "phase-b-authorized-baseline-refresh-invalid");

                var preStart = await runtime.GetSnapshotAsync().ConfigureAwait(true);
                Require(IsDevelopmentSafetyStop(preStart) && viewModel.CanStart,
                    "phase-b-start-precondition-invalid");
                startOutcome = await viewModel.StartCalibrationSessionAsync(password)
                    .ConfigureAwait(true);
                Require(startOutcome is { Disposition: CommandDisposition.Accepted,
                    Audit: AuditPersistence.Persisted },
                    "phase-b-start-rejected-" + startOutcome?.ReasonCode);
                var acceptedStart = startOutcome!;
                acceptedBeforeTerminal = viewModel.CalibrationSession is
                    { Outcome: CalibrationSessionOutcome.Pending };
                await WaitForCalibrationAsync(viewModel, clock,
                    state => state is { Phase: CalibrationSessionPhase.Collecting, OperationInProgress: false },
                    "phase-b-session-did-not-enter-collecting").ConfigureAwait(true);
                var sessionId = viewModel.CurrentSessionId ??
                    throw new CalibrationConsumerCheckException("runtime-session-id-missing");

                var initialQuery = await query.QueryCalibrationSessionAsync(sessionId,
                    CurrentInvocation(sessions)).ConfigureAwait(true);
                Require(initialQuery.Available && initialQuery.Evidence is not null,
                    "phase-b-start-evidence-unavailable-" + initialQuery.ReasonCode);
                var admissionEvidence = initialQuery.Evidence!;
                beforeExit = admissionEvidence;
                Require(admissionEvidence.Header.SessionId == sessionId &&
                    admissionEvidence.Header.Command.CorrelationId == acceptedStart.CorrelationId &&
                    admissionEvidence.Header.Command.Invocation.StepUpGrantId is not null &&
                    admissionEvidence.Header.Command.AuthorizationTarget ==
                    new StartCalibrationSessionCommand(acceptedStart.CorrelationId,
                        CurrentInvocation(sessions), fixture.Plan,
                        fixture.Binding.Revision, fixture.Binding.RevisionHash,
                        fixture.ImagingSetup, admissionEvidence.Header.Command.Reason).AuthorizationTarget,
                    "phase-b-start-step-up-target-mismatch");
                Require(admissionEvidence.Header.BaselineRequestedHash == fixture.BaselineRequestedHash &&
                    admissionEvidence.Header.BaselineEffectiveHash == fixture.BaselineEffectiveHash,
                    "phase-b-baseline-header-mismatch");

                for (var index = 0; index < 3; index++)
                {
                    var capture = await viewModel.CaptureCalibrationFrameAsync()
                        .ConfigureAwait(true);
                    Require(capture is { Disposition: CommandDisposition.Accepted },
                        "phase-b-capture-rejected-" + capture?.ReasonCode);
                    var expectedCount = index + 1;
                    await WaitForCalibrationAsync(viewModel, clock,
                        state => viewModel.Frames.Count == expectedCount &&
                            viewModel.Observations.Count == expectedCount &&
                            state is { Phase: CalibrationSessionPhase.Collecting, OperationInProgress: false },
                        "phase-b-capture-not-retained-" + expectedCount).ConfigureAwait(true);
                }
                Require(viewModel.Frames.Count == 3 && viewModel.Observations.Count == 3,
                    "phase-b-frame-observation-count-invalid");
                var excludedFrame = viewModel.Frames[0];
                viewModel.SelectedFrame = excludedFrame;
                viewModel.ExcludeReason = "第一帧整帧排除：夹具审阅示范，不删除原始证据";
                var excluded = await viewModel.ExcludeCalibrationFrameAsync(
                    excludedFrame.FrameId, viewModel.ExcludeReason).ConfigureAwait(true);
                Require(excluded is { Disposition: CommandDisposition.Accepted },
                    "phase-b-whole-frame-exclusion-rejected-" + excluded?.ReasonCode);
                await WaitForCalibrationAsync(viewModel, clock,
                    state => state is { ExcludedFrameCount: 1, OperationInProgress: false },
                    "phase-b-exclusion-not-retained").ConfigureAwait(true);
                Require(viewModel.Frames.Count == 3 && viewModel.Exclusions.Count == 1 &&
                    viewModel.CanCompute && viewModel.CanCapture,
                    "phase-b-exclusion-mutability-guard-invalid");

                var compute = await viewModel.ComputeCalibrationCandidateAsync()
                    .ConfigureAwait(true);
                Require(compute is { Disposition: CommandDisposition.Accepted },
                    "phase-b-compute-rejected-" + compute?.ReasonCode);
                await WaitForCalibrationAsync(viewModel, clock,
                    state => state is { Phase: CalibrationSessionPhase.CandidateRetained, OperationInProgress: false } &&
                        viewModel.Candidate is not null,
                    "phase-b-candidate-not-retained").ConfigureAwait(true);
                Require(viewModel.Candidate is { DevelopmentOnly: true,
                    CanPublish: false, CanActivate: false } &&
                    viewModel.SelectionEvaluation is { Sufficient: true,
                        IncludedFrameCount: 2 } && !viewModel.CanCapture &&
                    !viewModel.CanExclude && !viewModel.CanCompute,
                    "phase-b-candidate-authority-or-selection-invalid");

                imageResult = await viewModel.ReadSelectedFrameAsync().ConfigureAwait(true);
                Require(imageResult is { Available: true, Image: not null },
                    "phase-b-read-only-frame-failed-" + imageResult?.ReasonCode);
                var selectedImage = imageResult!.Image!;
                var imageBytes = selectedImage.GetBytes();
                Require(imageBytes.Length == selectedImage.Frame.ByteLength &&
                    selectedImage.Frame.Metadata.StrideBytes ==
                    selectedImage.Frame.Metadata.ValidRowBytes &&
                    Convert.ToHexString(SHA256.HashData(imageBytes)) ==
                    selectedImage.Frame.PixelHash && viewModel.HasSelectedFrameImage,
                    "phase-b-read-only-frame-layout-invalid");

                var candidateQuery = await query.QueryCalibrationSessionAsync(sessionId,
                    CurrentInvocation(sessions)).ConfigureAwait(true);
                Require(candidateQuery.Available && candidateQuery.Evidence is not null &&
                    candidateQuery.Evidence.Candidate is not null,
                    "phase-b-candidate-query-failed-" + candidateQuery.ReasonCode);
                beforeExit = candidateQuery.Evidence!;
                if (render)
                    screenshots = await RenderPanelAsync(viewModel, arguments.Directory)
                        .ConfigureAwait(true);

                viewModel.ExitReason = "退出确定性标定会话并验证原始基线恢复";
                exitOutcome = await viewModel.ExitCalibrationSessionAsync(viewModel.ExitReason)
                    .ConfigureAwait(true);
                Require(exitOutcome is { Disposition: CommandDisposition.Accepted },
                    "phase-b-exit-rejected-" + exitOutcome?.ReasonCode);
                var acceptedExit = exitOutcome!;
                await WaitForCalibrationAsync(viewModel, clock,
                    state => state is { Phase: CalibrationSessionPhase.Restored,
                        RestorationVerified: true }, "phase-b-baseline-not-restored").ConfigureAwait(true);
                finalState = await runtime.GetSnapshotAsync().ConfigureAwait(true);
                var finalSnapshot = finalState!;
                Require(IsFinalSafeState(finalSnapshot), "phase-b-final-safety-state-invalid");
                var terminalQuery = await query.QueryCalibrationSessionAsync(sessionId,
                    CurrentInvocation(sessions)).ConfigureAwait(true);
                Require(terminalQuery.Available && terminalQuery.Evidence is not null,
                    "phase-b-terminal-query-failed-" + terminalQuery.ReasonCode);
                var terminalEvidence = terminalQuery.Evidence!;
                afterExit = terminalEvidence;
                Require(terminalEvidence.State.Phase == CalibrationSessionPhase.Restored &&
                    terminalEvidence.State.Outcome == CalibrationSessionOutcome.Completed &&
                    terminalEvidence.State.RestorationVerified && terminalEvidence.Candidate is not null,
                    "phase-b-terminal-evidence-invalid");

                // Navigation/deactivation only clears the view and cannot submit
                // another Runtime command. The restored projection remains present.
                var lastExitCorrelation = acceptedExit.CorrelationId;
                viewModel.Deactivate();
                var afterDeactivate = await runtime.GetSnapshotAsync().ConfigureAwait(true);
                Require(afterDeactivate.CalibrationSession?.SessionId == sessionId &&
                    afterDeactivate.LastCommand?.CorrelationId == lastExitCorrelation,
                    "phase-b-navigation-submitted-or-cleared-runtime-exit");
            }

        }
        finally
        {
            if (container is not null)
                await container.DisposeAsync().ConfigureAwait(true);
            else if (recovery is not null)
                await recovery.DisposeAsync().ConfigureAwait(true);
            else if (initialAcquisition is not null)
                await initialAcquisition.DisposeAsync().ConfigureAwait(true);
            else
                await provider.DisposeAsync().ConfigureAwait(true);
        }

        var diagnostics = provider.GetDiagnostics();
        Require(diagnostics.IsDisposed && diagnostics.Devices.All(device =>
            !device.IsOpen && device.OutstandingLeases == 0 && device.OpenCount >= 1),
            "phase-b-provider-disposal-or-call-proof-invalid");
        return new PhaseBResult(scenario.ContentHash, startOutcome!, exitOutcome!,
            beforeExit!, afterExit!, imageResult!, finalState!, screenshots,
            acceptedBeforeTerminal, diagnostics);
    }

    private static async Task<SeedResult> OpenSeedAsync(VirtualCameraProvider provider,
        CameraBindingTarget target, RequestedCameraConfiguration temporary,
        CameraBindingRevision binding, RequestedCameraConfiguration baselineRequested,
        EffectiveCameraConfiguration baselineEffective, VirtualCameraClock clock)
    {
        var discovered = await provider.DiscoverAsync().ConfigureAwait(true);
        Require(discovered.Succeeded && discovered.Devices.Count == 1 &&
            discovered.Devices[0].StableDeviceIdentity == target.StableDeviceIdentity,
            "phase-b-seed-discovery-invalid");
        var opened = await provider.OpenAsync(target.StableDeviceIdentity).ConfigureAwait(true);
        Require(opened.Succeeded && opened.Device is IControlledCameraDevice,
            "phase-b-seed-open-failed");
        var device = (IControlledCameraDevice)opened.Device!;
        var configured = await device.ApplyConfigurationAsync(baselineRequested)
            .ConfigureAwait(true);
        Require(configured.Succeeded && configured.Effective is not null &&
            configured.Effective.Equals(baselineEffective),
            "phase-b-seed-baseline-readback-invalid");
        var started = await device.StartAsync().ConfigureAwait(true);
        Require(started.Succeeded, "phase-b-seed-start-failed");
        Require(device.Descriptor.Provider == target.Provider &&
            device.Descriptor.StableDeviceIdentity == binding.Target.StableDeviceIdentity,
            "phase-b-seed-device-identity-invalid");
        var acquisition = new CameraAcquisitionService(device, configured.Effective!, clock,
            new CameraAcquisitionOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 64));
        return new SeedResult(acquisition, configured.Effective!, temporary);
    }

    private static async Task<CameraSetupOperationResult> RebindAsync(
        ICameraSetupRuntime setup, IStepUpAuthentication stepUp,
        IInteractiveSessionService sessions, CameraBindingTarget target, string password)
    {
        var operation = Guid.NewGuid();
        var grant = await GrantAsync(stepUp, sessions, Permission.ManageCameraBindings,
            operation, Role, AuditedCommandKind.RebindCamera, password).ConfigureAwait(true);
        return await setup.RebindAsync(new CameraRebindRequest(operation,
            CurrentInvocation(sessions) with { StepUpGrantId = grant.GrantId }, Role, 0, null,
            target, "Bind deterministic Virtual calibration camera")).ConfigureAwait(true);
    }

    private static async Task<CameraSetupOperationResult> ApplyAsync(
        ICameraSetupRuntime setup, IStepUpAuthentication stepUp,
        IInteractiveSessionService sessions, CameraBindingRevision binding,
        RequestedCameraConfiguration requested, string password)
    {
        var operation = Guid.NewGuid();
        var grant = await GrantAsync(stepUp, sessions, Permission.ManageCameraBindings,
            operation, Role, AuditedCommandKind.ApplyCameraDebugConfiguration, password)
            .ConfigureAwait(true);
        return await setup.ApplyDebugConfigurationAsync(new CameraDebugConfigurationRequest(
            operation, CurrentInvocation(sessions) with { StepUpGrantId = grant.GrantId }, Role,
            binding.Revision, binding.RevisionHash, requested,
            "Apply stopped deterministic Virtual calibration baseline")).ConfigureAwait(true);
    }

    private static async Task<ImagingSetupChangeResult> DeclareImagingAsync(
        IImagingSetupRuntime imaging, IStepUpAuthentication stepUp,
        IInteractiveSessionService sessions, CameraBindingRevision binding,
        ImagingSetupDefinition definition, string reason, string password)
    {
        var operation = Guid.NewGuid();
        var invocation = CurrentInvocation(sessions);
        var intended = new ImagingSetupChangeRequest(operation, invocation, Role,
            binding.Revision, binding.RevisionHash, 0, null, definition, reason);
        var grant = await GrantAsync(stepUp, sessions, Permission.ManageCameraBindings,
            operation, intended.AuthorizationTarget, AuditedCommandKind.DeclareImagingSetup,
            password).ConfigureAwait(true);
        return await imaging.DeclareImagingSetupAsync(new ImagingSetupChangeRequest(operation,
            invocation with { StepUpGrantId = grant.GrantId }, Role, binding.Revision,
            binding.RevisionHash, 0, null, definition, reason)).ConfigureAwait(true);
    }

    private static async Task<StepUpResult> GrantAsync(IStepUpAuthentication stepUp,
        IInteractiveSessionService sessions, Permission permission, Guid operation,
        string target, AuditedCommandKind commandKind, string password)
    {
        var result = await stepUp.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(),
            CurrentInvocation(sessions), new StepUpBinding(permission, operation, target,
                commandKind), password)).ConfigureAwait(true);
        Require(result.Succeeded && result.GrantId is not null,
            "step-up-failed-" + result.ReasonCode);
        return result;
    }

    private static async Task SignInAsync(IInteractiveSessionService sessions,
        IStationRuntime runtime, string userName, string? expectedPrincipal, string password)
    {
        var signedIn = await sessions.SignInAsync(new PasswordSignInRequest(userName, password))
            .ConfigureAwait(true);
        Require(signedIn.Succeeded && signedIn.Identity is not null,
            "calibration-consumer-authentication-failed-" + signedIn.ReasonCode);
        if (expectedPrincipal is not null)
            Require(signedIn.Identity!.PrincipalId.ToString("D") == expectedPrincipal,
                "calibration-consumer-principal-mismatch");
        await WaitForRuntimeAsync(runtime, state =>
            state.Session.State == InteractiveSessionState.Authenticated &&
            state.Session.SessionId is not null &&
            state.Session.PrincipalId == signedIn.Identity!.PrincipalId.ToString("D"))
            .ConfigureAwait(true);
    }

    private static CommandInvocation CurrentInvocation(IInteractiveSessionService sessions)
    {
        var current = sessions.Current;
        Require(current is { State: InteractiveSessionState.Authenticated,
            PrincipalId: not null, SessionId: not null }, "interactive-session-unavailable");
        return new CommandInvocation(CommandSource.PhysicalConsole, current.PrincipalId,
            current.SessionId);
    }

    private static async Task WaitForRuntimeAsync(IStationRuntime runtime,
        Func<StationStateSnapshot, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            var state = await runtime.GetSnapshotAsync().ConfigureAwait(true);
            if (predicate(state)) return;
            await Task.Delay(10).ConfigureAwait(true);
        }
        throw new CalibrationConsumerCheckException("runtime-observation-timeout");
    }

    private static async Task WaitForCalibrationAsync(CalibrationSessionViewModel viewModel,
        VirtualCameraClock clock, Func<CalibrationSessionState?, bool> predicate, string reason)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            await viewModel.RefreshAsync().ConfigureAwait(true);
            if (predicate(viewModel.CalibrationSession)) return;
            // An acquisition may have scheduled its first frame before this wait begins.
            // Advance virtual time even when the pending event count stays unchanged.
            clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
            await Task.Delay(1).ConfigureAwait(true);
        }
        throw new CalibrationConsumerCheckException(reason + "-" +
            viewModel.CalibrationSession?.ReasonCode + "-" + viewModel.ErrorCode);
    }

    private static bool IsDevelopmentSafetyStop(StationStateSnapshot state) =>
        state.Lifecycle == RuntimeLifecycle.Running && !state.Ready &&
        state.ArmState == ProductionArmState.Disarmed && !state.Busy &&
        state.Mode == ExclusiveMode.None && state.Handshake == HandshakePhase.Unknown &&
        state.Recovery == RecoveryState.Required && state.ActiveRecipe is null &&
        state.CurrentExecution is null && state.Evidence.PendingDeliveries == 0 &&
        state.Plc.Connection == HealthState.Unconfigured;

    private static bool IsFinalSafeState(StationStateSnapshot state) =>
        state.Lifecycle == RuntimeLifecycle.Running && !state.Ready &&
        state.ArmState == ProductionArmState.Disarmed && !state.Busy &&
        state.Mode == ExclusiveMode.None && state.Handshake == HandshakePhase.Unknown &&
        state.Recovery == RecoveryState.Required && state.ActiveRecipe is null &&
        state.CurrentExecution is null;

    private static CalibrationSessionPlan CreatePlan()
    {
        var procedure = new DeterministicCalibrationProcedure();
        var requirement = new CalibrationRequirement(Role, CalibrationKind.Intrinsic,
            "FixtureIntrinsic", DeterministicCalibrationProcedure.CoefficientContract,
            DeterministicCalibrationProcedure.AcceptanceContract);
        var input = new CalibrationFixtureInputCodec().EncodePayload(
            new CalibrationFixtureInput(unchecked((int)ScenarioSeed), 4));
        return new CalibrationSessionPlan(requirement, procedure.Descriptor, input,
            TemporaryConfiguration(), new CalibrationEvidenceSelectionPolicy(
                "CalibrationConsumerSelection", "1", 2, 4, 0.5));
    }

    private static VirtualCameraProvider CreateProvider(VirtualCameraClock clock) =>
        new(new[] { CreateScenario() }, clock, poolCapacity: 2);

    private static VirtualCameraScenario CreateScenario()
    {
        var images = new[]
        {
            VirtualCameraImage.CreateSynthetic("calibration-frame-1", 64, 48,
                VisionPixelFormat.Mono8, null, ScenarioSeed + 1),
            VirtualCameraImage.CreateSynthetic("calibration-frame-2", 64, 48,
                VisionPixelFormat.Mono8, null, ScenarioSeed + 2),
            VirtualCameraImage.CreateSynthetic("calibration-frame-3", 64, 48,
                VisionPixelFormat.Mono8, null, ScenarioSeed + 3)
        };
        var acquisitions = images.Select(image => new VirtualCameraAcquisitionPlan(new[]
        {
            new VirtualCameraSignal(TimeSpan.FromMilliseconds(1),
                VirtualCameraSignalKind.Frame, image.Id)
        })).ToArray();
        var configurations = new[]
        {
            new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success,
                TimeSpan.Zero),
            new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success,
                TimeSpan.Zero),
            new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success,
                TimeSpan.Zero)
        };
        return new VirtualCameraScenario("CalibrationConsumer", "1", ScenarioSeed, Device,
            Capabilities(), images, acquisitions, configurations);
    }

    private static CameraCapabilities Capabilities() => new(
        new[] { ProductionAcquisitionMode.SoftwareTrigger },
        new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
        new CameraDoubleCapability(10, 10000, 1, CameraQuantizationMode.Nearest, 0.5),
        new CameraDoubleCapability(0, 24, 1, CameraQuantizationMode.Exact),
        new CameraDoubleCapability(0, 10000, 1, CameraQuantizationMode.Exact),
        new CameraRoiCapabilities(128, 96, new(0, 127, 1), new(0, 95, 1),
            new(1, 128, 1), new(1, 96, 1)));

    private static RequestedCameraConfiguration BaselineRequestedConfiguration() => new(
        ProductionAcquisitionMode.SoftwareTrigger, 1000.4, 0,
        new RegionOfInterest(0, 0, 64, 48), VisionPixelFormat.Mono8, null, 1000, 0, null);

    private static RequestedCameraConfiguration TemporaryConfiguration() => new(
        ProductionAcquisitionMode.SoftwareTrigger, 800.2, 0,
        new RegionOfInterest(0, 0, 64, 48), VisionPixelFormat.Mono8, null, 1000, 0, null);

    private static ProductionStoreOptions CreateStoreOptions(
        CalibrationConsumerArguments arguments)
    {
        var audit = new AuditIntegrityPolicy(arguments.StationId, "development-v1",
            arguments.AuditKey)
        {
            AllowInitialKeyCreation = true,
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1),
            KeyDirectory = arguments.AuditKeyDirectory
        };
        return new ProductionStoreOptions(arguments.DatabasePath)
        {
            AuditIntegrityPolicy = audit,
            LocalIdentity = ReadIdentityOptions(arguments),
            AlarmPolicy = ReadAlarmPolicy(arguments),
            CameraSetup = new CameraSetupStoreOptions(),
            CameraRecovery = new CameraRecoveryStoreOptions(),
            ImagingSetup = new ImagingSetupStoreOptions(),
            CalibrationSessions = new CalibrationSessionStoreOptions
            {
                EvidenceRoot = arguments.EvidenceRoot,
                MaximumSessions = 4,
                MaximumEvents = 256,
                MaximumEventPayloadBytes = 256 * 1024,
                MaximumFramesPerSession = 4,
                MaximumFrameBytes = 16L * 1024 * 1024,
                MaximumTotalFrameBytes = 64L * 1024 * 1024
            }
        };
    }

    private static AlarmPolicy ReadAlarmPolicy(CalibrationConsumerArguments arguments)
    {
        if (arguments.AlarmPolicyPath is not { } path)
            return RecoveryAlarmPolicy();
        if (!File.Exists(path) || new FileInfo(path).Length > 128 * 1024)
            throw new CalibrationConsumerCheckException("alarm-policy-file-invalid");
        var document = JsonSerializer.Deserialize<AlarmPolicyDocument>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new CalibrationConsumerCheckException("alarm-policy-invalid");
        if (document.Rules is null)
            throw new CalibrationConsumerCheckException("alarm-policy-rules-required");
        try
        {
            return new AlarmPolicy(document.Id, document.Version, document.Rules,
                document.SourceObservationFreshness, document.MaximumActiveInstances,
                document.MaximumPlcEntries);
        }
        catch (ArgumentException exception)
        {
            throw new CalibrationConsumerCheckException("alarm-policy-invalid-" + exception.Message);
        }
    }

    private static AlarmPolicy RecoveryAlarmPolicy() => new("V124-calibration-consumer", "1",
        new[]
        {
            new AlarmPolicyRule("StartupRecoveryRequired", "Runtime.StartupRecovery",
                AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null, 0,
                AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoPendingDelivery),
            new AlarmPolicyRule("CameraDisconnected", "Runtime.CameraRecovery",
                AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, false,
                AlarmNotification.UntilCleared, null, 0,
                AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution |
                AlarmResetPrerequisites.NoPendingDelivery),
            new AlarmPolicyRule("CameraRecoveryFailed", "Runtime.CameraRecovery",
                AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null, 0,
                AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution |
                AlarmResetPrerequisites.NoPendingDelivery)
        }.Concat(Enum.GetValues<CameraProtocolViolationKind>().Select(kind =>
            new AlarmPolicyRule(CameraAcquisitionAlarmCode(kind), "Runtime.CameraAcquisition",
                AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null, 0,
                AlarmResetPrerequisites.RecoveryComplete |
                AlarmResetPrerequisites.NoActiveExecution))).ToArray(),
        TimeSpan.FromSeconds(10), maximumActiveInstances: 256, maximumPlcEntries: 16);

    private static string CameraAcquisitionAlarmCode(CameraProtocolViolationKind kind) => kind switch
    {
        CameraProtocolViolationKind.EarlyFrame => "CameraEarlyFrame",
        CameraProtocolViolationKind.ExtraFrame => "CameraExtraFrame",
        CameraProtocolViolationKind.LateFrame => "CameraLateFrame",
        CameraProtocolViolationKind.CorrelationMismatch => "CameraCorrelationMismatch",
        CameraProtocolViolationKind.EarlyHardwarePulse => "CameraEarlyHardwarePulse",
        CameraProtocolViolationKind.DuplicateHardwarePulse => "CameraDuplicateHardwarePulse",
        CameraProtocolViolationKind.TriggerWhileBusy => "CameraTriggerWhileBusy",
        CameraProtocolViolationKind.InvalidFrame => "CameraInvalidFrame",
        CameraProtocolViolationKind.ObservationGap => "CameraObservationGap",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static LocalIdentityOptions ReadIdentityOptions(
        CalibrationConsumerArguments arguments)
    {
        if (arguments.IdentityPolicyPath is { } path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024)
                throw new CalibrationConsumerCheckException("identity-policy-file-invalid");
            var configuration = JsonSerializer.Deserialize<IdentityDevelopmentConfiguration>(
                File.ReadAllText(path), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? throw new CalibrationConsumerCheckException("identity-policy-invalid");
            var blocklist = new PasswordBlocklist(configuration.BlocklistId,
                configuration.BlocklistVersion, configuration.BlocklistContentHash,
                configuration.BlocklistValues);
            var authorization = configuration.AuthorizationPolicy ??
                throw new CalibrationConsumerCheckException("authorization-policy-required");
            var policy = new AuthorizationPolicy(authorization.Id, authorization.Version,
                authorization.RoleBundles.ToDictionary(pair => pair.Key,
                    pair => (IEnumerable<Permission>)pair.Value), authorization.StepUpPermissions);
            Require(policy.GetPermissions(HumanRoleBundle.Administrator)
                    .Contains(Permission.RunCalibration),
                "identity-policy-must-explicitly-grant-run-calibration-32");
            return new LocalIdentityOptions(arguments.StationId,
                new LocalPasswordPolicy
                {
                    Version = configuration.PasswordPolicyVersion,
                    Blocklist = blocklist
                }, new Pbkdf2PasswordHasher(new PasswordHashBaseline(
                    configuration.HashBaselineVersion, configuration.WorkFactor)),
                configuration.AuthenticationPolicy ??
                    throw new CalibrationConsumerCheckException("authentication-policy-required"),
                policy);
        }

        // This default is explicit and intentionally differs from
        // AuthorizationPolicy.Development by granting RunCalibration only to
        // Technician and Administrator; it is never silently inferred from a
        // display identity or page visibility.
        var roles = AuthorizationPolicy.Development.RoleBundles.ToDictionary(
            pair => pair.Key, pair => (IEnumerable<Permission>)pair.Value);
        roles[HumanRoleBundle.Technician] = roles[HumanRoleBundle.Technician]
            .Append(Permission.RunCalibration);
        roles[HumanRoleBundle.Administrator] = roles[HumanRoleBundle.Administrator]
            .Append(Permission.RunCalibration);
        return new LocalIdentityOptions(arguments.StationId,
            new LocalPasswordPolicy
            {
                Blocklist = PasswordBlocklist.Create(
                    "calibration-consumer-blocklist", "1", new[] { "passwordpassword" })
            }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
            new AuthorizationPolicy("calibration-consumer", "calibration-consumer-2026-09",
                roles));
    }

    private static async Task WriteRunEvidenceAsync(
        CalibrationConsumerArguments arguments, ProductionStoreOptions options,
        DevelopmentCalibrationFixture fixture, PhaseAResult phaseA, PhaseBResult phaseB,
        string databaseHash)
    {
        var before = EvidenceSummary.Create(phaseB.BeforeExit);
        var after = EvidenceSummary.Create(phaseB.AfterExit);
        var final = phaseB.FinalState;
        var candidate = phaseB.AfterExit.Candidate!;
        var rawImage = phaseB.ImageResult.Image!;
        await File.WriteAllTextAsync(Path.Combine(arguments.Directory,
            "calibration-session-evidence.json"), JsonSerializer.Serialize(new
            {
                result = "Pass",
                contractVersion = ContractVersion,
                schema = SchemaVersion,
                modes = new[] { "run", "restart", "wpf", "all" },
                startupMethod = "PhaseA setup owner then PhaseB Recovery owner; restart query-only",
                validationIds = new[] { "V124_U01", "V124_U02", "V124_U03", "V124_U04",
                    "V124_U05", "V124_U06", "V124_U07", "V124_U08" },
                fixture = new
                {
                    id = fixture.FixtureId,
                    contentHash = fixture.ContentHash,
                    scenarioHash = phaseB.ScenarioHash,
                    developmentOnly = fixture.DevelopmentOnly,
                    productionAuthority = fixture.ProductionAuthority,
                    productionOutputsAbsent = true,
                    schema14Store = options.CalibrationSessions is not null
                },
                plan = new
                {
                    contentHash = fixture.Plan.ContentHash,
                    requirement = fixture.Plan.Requirement.ContentHash,
                    procedure = fixture.Plan.Procedure.ContentHash,
                    inputCanonicalBytesHash = fixture.Plan.Input.CanonicalBytesHash,
                    inputContentHash = fixture.Plan.Input.ContentHash,
                    temporaryConfigurationHash = fixture.Plan.TemporaryConfigurationHash,
                    selectionPolicy = fixture.Plan.SelectionPolicy.ContentHash
                },
                sessionId = phaseB.BeforeExit.Header.SessionId,
                binding = new
                {
                    logicalRole = Role,
                    device = Device,
                    provider = phaseA.Provider,
                    revision = fixture.Binding.Revision,
                    revisionHash = fixture.Binding.RevisionHash,
                    targetHash = fixture.Binding.Target.ContentHash
                },
                imaging = new
                {
                    revision = phaseA.ImagingRevision.Revision,
                    revisionId = phaseA.ImagingRevision.RevisionId,
                    revisionHash = phaseA.ImagingRevision.RevisionHash,
                    reason = phaseA.ImagingRevision.ChangeReason,
                    referenceHash = fixture.ImagingSetup.RevisionHash,
                    automaticallyDetectsAllPhysicalChanges =
                        phaseA.ImagingRevision.AutomaticallyDetectsAllPhysicalChanges
                },
                baseline = new
                {
                    requestedHash = fixture.BaselineRequestedHash,
                    effectiveHash = fixture.BaselineEffectiveHash,
                    requested = phaseA.BaselineRequested,
                    effective = phaseA.BaselineEffective,
                    temporary = fixture.Plan.TemporaryConfiguration
                },
                start = new
                {
                    accepted = phaseB.StartOutcome.Disposition == CommandDisposition.Accepted,
                    acceptedBeforeTerminal = phaseB.AcceptedBeforeTerminal,
                    correlationId = phaseB.StartOutcome.CorrelationId,
                    authorizationTarget = phaseB.BeforeExit.Header.Command.AuthorizationTarget,
                    stepUpGrantId = phaseB.BeforeExit.Header.Command.Invocation.StepUpGrantId,
                    auditedCommand = AuditedCommandKind.StartCalibrationSession.ToString(),
                    sessionId = phaseB.BeforeExit.Header.SessionId
                },
                evidenceBeforeExit = before,
                evidenceAfterExit = after,
                rawFrameRead = new
                {
                    available = true,
                    bytes = rawImage.GetBytes().Length,
                    byteLength = rawImage.Frame.ByteLength,
                    pixelHash = rawImage.Frame.PixelHash,
                    tightStride = rawImage.Frame.Metadata.StrideBytes ==
                        rawImage.Frame.Metadata.ValidRowBytes
                },
                exit = new
                {
                    accepted = phaseB.ExitOutcome.Disposition == CommandDisposition.Accepted,
                    correlationId = phaseB.ExitOutcome.CorrelationId,
                    reason = phaseB.AfterExit.State.ReasonCode,
                    restorationVerified = phaseB.AfterExit.State.RestorationVerified,
                    outcome = phaseB.AfterExit.State.Outcome
                },
                global = new
                {
                    ready = final.Ready,
                    armState = final.ArmState,
                    mode = final.Mode,
                    handshake = final.Handshake,
                    recovery = final.Recovery,
                    activeRecipe = final.ActiveRecipe,
                    productionOutputsAbsent = true,
                    physicalHardwareQualification = "NotRun",
                    providerQualification = "NotRun",
                    stationAcceptance = "NotRun",
                    production = "NotRun"
                },
                phaseA = new
                {
                    owner = "CameraSetupRuntime",
                    registeredCameraProvider = true,
                    registeredRecovery = false,
                    registeredAcquisition = false,
                    anonymousDiscoveryRejected = true,
                    missingStepUpRejected = true,
                    rebind = new { succeeded = phaseA.Rebind.Succeeded,
                        reason = phaseA.Rebind.ReasonCode },
                    applyStopped = new { succeeded = phaseA.Apply.Succeeded,
                        reason = phaseA.Apply.ReasonCode },
                    imaging = new { succeeded = phaseA.Imaging.Succeeded,
                        reason = phaseA.Imaging.ReasonCode },
                    diagnostics = phaseA.Diagnostics
                },
                phaseB = new
                {
                    owner = "CameraRecoveryService",
                    registeredCameraProvider = false,
                    registeredRecovery = true,
                    seedStarted = true,
                    exactScenario = true,
                    diagnostics = phaseB.Diagnostics
                },
                screenshots = phaseB.Screenshots,
                databaseHash,
                noAutomaticPhysicalDetection = true,
                noCoordinateEditing = true,
                noFakeProfilePublication = true,
                candidate = new
                {
                    contentHash = candidate.ContentHash,
                    coefficientHash = candidate.Result.Coefficients.ContentHash,
                    developmentOnly = candidate.DevelopmentOnly,
                    canPublish = candidate.CanPublish,
                    canActivate = candidate.CanActivate,
                    acceptanceReasonCode = candidate.AcceptanceReasonCode
                }
            }, JsonOptions())).ConfigureAwait(true);
    }

    private static async Task<string[]> RenderPanelAsync(CalibrationSessionViewModel model,
        string directory)
    {
        var panel = new CalibrationSessionPanel(model);
        var window = new Window
        {
            Content = panel,
            Width = 1200,
            Height = 1040,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000
        };
        try
        {
            window.Show();
            await FlushAsync().ConfigureAwait(true);
            var frames = (DataGrid)panel.FindName("FramesGrid");
            var observations = (DataGrid)panel.FindName("ObservationsGrid");
            var password = (PasswordBox)panel.FindName("StartPasswordBox");
            Require(frames.IsReadOnly && !frames.CanUserAddRows && observations.IsReadOnly &&
                !observations.CanUserAddRows && password.Password.Length == 0,
                "wpf-read-only-or-password-state-invalid");
            var scroll = (ScrollViewer)panel.FindName("CalibrationScrollViewer");
            scroll.ScrollToHome();
            await FlushAsync().ConfigureAwait(true);
            var top = "calibration-session-panel.png";
            SaveWindow(window, Path.Combine(directory, top));
            scroll.ScrollToEnd();
            await FlushAsync().ConfigureAwait(true);
            var bottom = "calibration-session-panel-form.png";
            SaveWindow(window, Path.Combine(directory, bottom));
            return new[] { top, bottom };
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task FlushAsync() =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { },
            DispatcherPriority.ApplicationIdle).Task.ConfigureAwait(true);

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

    private static async Task<string> DatabaseHashAsync(string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var candidate in new[] { path, path + "-wal" })
        {
            if (!File.Exists(candidate))
            {
                hash.AppendData(Encoding.UTF8.GetBytes("Absent"));
                continue;
            }
            await using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var fileHash = SHA256.Create();
            hash.AppendData(await fileHash.ComputeHashAsync(stream).ConfigureAwait(true));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static Guid ParseGuid(JsonElement root, string property)
    {
        var text = root.GetProperty(property).GetString();
        return text is not null && Guid.TryParse(text, out var value)
            ? value : throw new CalibrationConsumerCheckException(property + "-invalid");
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new CalibrationConsumerCheckException(reason);
    }

    internal sealed class CalibrationConsumerCheckException : Exception
    {
        internal CalibrationConsumerCheckException(string reasonCode) => ReasonCode = reasonCode;
        internal string ReasonCode { get; }
    }

    private sealed record IdentityDevelopmentConfiguration(string PasswordPolicyVersion,
        string BlocklistId, string BlocklistVersion, string BlocklistContentHash,
        string[] BlocklistValues, string HashBaselineVersion, int WorkFactor,
        AuthenticationPolicy AuthenticationPolicy,
        AuthorizationDevelopmentConfiguration AuthorizationPolicy);

    private sealed record AlarmPolicyDocument(string Id, string Version,
        AlarmPolicyRule[] Rules, TimeSpan SourceObservationFreshness,
        int MaximumActiveInstances, int MaximumPlcEntries);

    private sealed record AuthorizationDevelopmentConfiguration(string Id, string Version,
        Dictionary<HumanRoleBundle, Permission[]> RoleBundles,
        Permission[]? StepUpPermissions);

    private sealed record PhaseAResult(string ScenarioHash, CameraProviderIdentity Provider,
        CameraBindingRevision Binding, ImagingSetupRevision ImagingRevision,
        RequestedCameraConfiguration BaselineRequested,
        EffectiveCameraConfiguration BaselineEffective,
        CameraSetupOperationResult Rebind, CameraSetupOperationResult Apply,
        ImagingSetupChangeResult Imaging, string MissingStepUpReason,
        VirtualCameraProviderDiagnostics Diagnostics);

    private sealed record SeedResult(CameraAcquisitionService Acquisition,
        EffectiveCameraConfiguration Effective, RequestedCameraConfiguration Temporary);

    private sealed record PhaseBResult(string ScenarioHash,
        RuntimeCommandOutcome StartOutcome, RuntimeCommandOutcome ExitOutcome,
        CalibrationSessionEvidence BeforeExit, CalibrationSessionEvidence AfterExit,
        CalibrationFrameQueryResult ImageResult, StationStateSnapshot FinalState,
        string[] Screenshots, bool AcceptedBeforeTerminal,
        VirtualCameraProviderDiagnostics Diagnostics);

    private sealed record EvidenceSummary(Guid SessionId, CalibrationSessionPhase Phase,
        CalibrationSessionOutcome Outcome, bool RestorationVerified, int FrameCount,
        int ObservationCount, int ExclusionCount, CalibrationSelectionEvaluation Selection,
        FrameSummary[] Frames, ObservationSummary[] Observations,
        ExclusionSummary[] Exclusions, CandidateSummary? Candidate)
    {
        internal static EvidenceSummary Create(CalibrationSessionEvidence evidence)
        {
            var exclusions = evidence.Exclusions.ToDictionary(item => item.FrameId,
                item => item.Reason);
            var featureCounts = evidence.Observations.GroupBy(item => item.Frame.FrameId)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.Result.Features.Count));
            return new EvidenceSummary(evidence.Header.SessionId, evidence.State.Phase,
                evidence.State.Outcome, evidence.State.RestorationVerified, evidence.Frames.Count,
                evidence.Observations.Count, evidence.Exclusions.Count, evidence.Selection,
                evidence.Frames.Select(frame => new FrameSummary(frame.FrameId,
                    frame.SourceHash, frame.PixelHash, frame.Metadata.PixelFormat,
                    frame.Metadata.Width, frame.Metadata.Height, frame.SourceStrideBytes,
                    frame.Metadata.StrideBytes, frame.ByteLength,
                    featureCounts.GetValueOrDefault(frame.FrameId),
                    exclusions.GetValueOrDefault(frame.FrameId))).ToArray(),
                evidence.Observations.Select(observation => new ObservationSummary(
                    observation.ObservationId, observation.Frame.FrameId,
                    observation.Frame.SourceHash, observation.Result.Features.Select(feature =>
                        new FeatureSummary(feature.StableFeatureId, feature.PixelX,
                            feature.PixelY)).ToArray())).ToArray(),
                evidence.Exclusions.Select(item => new ExclusionSummary(item.FrameId,
                    item.Reason, item.ActorPrincipalId, item.InteractiveSessionId,
                    item.RecordedAtUtc)).ToArray(), evidence.Candidate is { } candidate
                    ? CandidateSummary.Create(candidate) : null);
        }
    }

    private sealed record FrameSummary(Guid FrameId, string SourceHash, string PixelHash,
        VisionPixelFormat PixelFormat, int Width, int Height, int SourceStrideBytes,
        int MetadataStrideBytes, long ByteLength, int FeatureCount, string? ExclusionReason);

    private sealed record FeatureSummary(string Id, double PixelX, double PixelY);

    private sealed record ObservationSummary(Guid ObservationId, Guid FrameId,
        string SourceHash, FeatureSummary[] Features);

    private sealed record ExclusionSummary(Guid FrameId, string Reason,
        Guid ActorPrincipalId, Guid InteractiveSessionId, DateTimeOffset RecordedAtUtc);

    private sealed record CandidateSummary(Guid CandidateId, string ContentHash,
        string CoefficientHash, bool DevelopmentOnly, bool CanPublish, bool CanActivate,
        string AcceptanceReasonCode)
    {
        internal static CandidateSummary Create(CalibrationCandidateEvidence candidate) =>
            new(candidate.CandidateId, candidate.ContentHash,
                candidate.Result.Coefficients.ContentHash, candidate.DevelopmentOnly,
                candidate.CanPublish, candidate.CanActivate, candidate.AcceptanceReasonCode);
    }
}
