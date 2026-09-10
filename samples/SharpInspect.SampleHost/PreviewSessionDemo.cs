using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Preview;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.SampleHost;

/// <summary>
/// Public-package consumer for the non-production Preview session boundary.
/// The sample creates an ordinary Draft and camera binding through public
/// capabilities, then drives only the public Runtime commands.  It does not
/// construct an internal fixture, activate a recipe, or provide a production
/// qualification result.
/// </summary>
internal static class PreviewSessionDemo
{
    private const string CaseId = "V133_N01";
    private const string LogicalRole = "PreviewCamera";
    private const string StableDeviceIdentity = "Virtual:Preview";
    private const string RecipeKey = "PreviewSessionConsumerRecipe";
    private const int Seed = 133;
    private static readonly DateTimeOffset InitialUtc =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static int Run(ProductionStoreOptions options, string directory,
        string? configuredUserName, string? expectedPrincipal)
    {
        try
        {
            var password = JsonSerializer.Deserialize<string>(Console.ReadLine() ?? "null")
                ?? throw new PreviewSessionDemoException("PreviewConsumerPasswordRequired");
            RunCoreAsync(options, Path.GetFullPath(directory), configuredUserName,
                expectedPrincipal, password).GetAwaiter().GetResult();
            Console.WriteLine("V133_N01 preview-session PASS access=true requiresStepUp=false frame=true tuned=true frozen=true saved=true exit=true restoration=NoActiveBaselineClosed ready=false active=false armed=false");
            return 0;
        }
        catch (PreviewSessionDemoException exception)
        {
            Console.Error.WriteLine($"{CaseId} preview-session FAIL reason={exception.ReasonCode}");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{CaseId} preview-session FAIL reason=PreviewConsumerCheckFailed");
            Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Adds the Preview recovery mapping without replacing a host policy.  A
    /// present rule with different semantics is rejected rather than hidden.
    /// </summary>
    internal static AlarmPolicy? EnsureRecoveryAlarmPolicy(AlarmPolicy? existing,
        bool previewEnabled = true)
    {
        if (!previewEnabled) return existing;
        var rule = new AlarmPolicyRule("PreviewRecoveryRequired", "Runtime.Preview",
            AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers, true,
            AlarmNotification.UntilCleared, null, ResetPrerequisites:
                AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution);
        if (existing is { } policy)
        {
            if (policy.TryGetRule(rule.Code, out var configured) && configured is not null)
            {
                Require(configured.Source == rule.Source && configured.IsLatched &&
                    configured.ProductionImpact == rule.ProductionImpact &&
                    (configured.ResetPrerequisites & (AlarmResetPrerequisites.RecoveryComplete |
                        AlarmResetPrerequisites.NoActiveExecution)) ==
                    (AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution),
                    "PreviewRecoveryAlarmMappingMismatch");
                return policy;
            }

            return new AlarmPolicy(policy.Id, policy.Version, policy.Rules.Append(rule),
                policy.SourceObservationFreshness, policy.MaximumActiveInstances,
                policy.MaximumPlcEntries);
        }

        var startup = new AlarmPolicyRule("StartupRecoveryRequired", "Runtime.StartupRecovery",
            AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers, true,
            AlarmNotification.UntilCleared, null, ResetPrerequisites:
                AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoPendingDelivery);
        return new AlarmPolicy("Sample.Preview", "development-v1", new[] { startup, rule },
            TimeSpan.FromMinutes(1));
    }

    /// <summary>Explicit public virtual setup used by the CLI and optional WPF entry.</summary>
    internal static VirtualCameraProvider CreateProvider(VirtualCameraClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var capabilities = new CameraCapabilities(
            new[] { ProductionAcquisitionMode.SoftwareTrigger },
            new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
            new(10, 10_000, 1, CameraQuantizationMode.Exact),
            new(0, 24, 1, CameraQuantizationMode.Exact),
            new(0, 10_000, 1, CameraQuantizationMode.Exact),
            new(128, 96, new(0, 127, 1), new(0, 95, 1),
                new(1, 128, 1), new(1, 96, 1)));
        var image = VirtualCameraImage.CreateSynthetic("preview-frame", 16, 12,
            VisionPixelFormat.Mono8, null, Seed);
        var process = new PreviewCameraProcessSettings(500, 0,
            new RegionOfInterest(0, 0, image.Width, image.Height),
            VisionPixelFormat.Mono8, null);
        var automaticReadback = new PreviewCameraProcessSettings(650, 4,
            process.RegionOfInterest, process.PixelFormat, process.ValidBits);
        var preview = new VirtualCameraPreviewScenario(new[] { image },
            automaticOnceReadback: automaticReadback,
            frameInterval: TimeSpan.FromMilliseconds(100));
        var configurations = Enumerable.Repeat(
            new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero), 8);
        var scenario = VirtualCameraScenario.WithPreview("Preview", "1", Seed,
            StableDeviceIdentity, capabilities, new[] { image },
            Array.Empty<VirtualCameraAcquisitionPlan>(), preview, configurations);
        return new VirtualCameraProvider(new[] { scenario }, clock);
    }

    private static async Task RunCoreAsync(ProductionStoreOptions options, string directory,
        string? configuredUserName, string? expectedPrincipal, string password)
    {
        RequireConfiguredRun(options);
        Require(!string.IsNullOrWhiteSpace(configuredUserName), "PreviewConsumerUserRequired");
        Require(Guid.TryParse(expectedPrincipal, out var expectedPrincipalId) &&
            expectedPrincipalId != Guid.Empty, "PreviewConsumerPrincipalRequired");
        Directory.CreateDirectory(directory);
        var evidencePath = Path.Combine(directory, "preview-evidence.json");
        Require(!File.Exists(evidencePath), "PreviewEvidenceAlreadyExists");

        using var clock = new VirtualCameraClock(InitialUtc);
        var cameraProvider = CreateProvider(clock);
        var services = new ServiceCollection();
        services.AddSingleton(RecipeDraftDemo.CreateFactory());
        services.AddSharpInspectCameraProvider(cameraProvider);
        services.AddSingleton(new PreviewSessionOptions());
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(100));
        await using var container = services.BuildServiceProvider();
        using var clockPump = PreviewClockPump.Start(clock);

        var runtime = container.GetRequiredService<IStationRuntime>();
        var startup = await WaitForStartupFenceAsync(runtime).ConfigureAwait(true);
        Require(startup.Lifecycle == RuntimeLifecycle.Running &&
            !startup.AdmissionBlockers.Contains("RecipeActivationStartupRecoveryPending", StringComparer.Ordinal) &&
            !startup.AdmissionBlockers.Contains("PreviewStartupRecoveryPending", StringComparer.Ordinal) &&
            !startup.AdmissionBlockers.Contains("RecipeActivationStartupRecoveryRequired", StringComparer.Ordinal) &&
            !startup.AdmissionBlockers.Contains("PreviewStartupRecoveryRequired", StringComparer.Ordinal),
            "PreviewStartupRecoveryUnavailable");
        var initial = await runtime.GetSnapshotAsync().ConfigureAwait(true);
        Require(!initial.Ready && initial.ArmState == ProductionArmState.Disarmed &&
            initial.ActiveRecipe is null, "PreviewInitialStationNotDisarmed");

        var sessions = container.GetRequiredService<IInteractiveSessionService>();
        var login = await sessions.SignInAsync(new PasswordSignInRequest(configuredUserName!, password))
            .ConfigureAwait(true);
        Require(login.Succeeded && login.Identity?.PrincipalId == expectedPrincipalId,
            "PreviewConsumerAuthenticationFailed");
        var session = sessions.Current;
        Require(session.State == InteractiveSessionState.Authenticated &&
            session.SessionId is not null && session.PrincipalId == expectedPrincipalId.ToString("D"),
            "PreviewConsumerSessionUnavailable");
        var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
            session.PrincipalId, session.SessionId);

        var editor = container.GetRequiredService<IRecipeDraftEditor>();
        var draft = await SaveDraftAsync(options, editor, invocation).ConfigureAwait(true);
        var draftReference = PreviewDraftReference.FromRevision(draft);
        var cameraConfiguration = draft.Content.Camera;
        var setup = container.GetRequiredService<ICameraSetupRuntime>();
        var setupSnapshot = await EnsureCameraSetupAsync(setup,
            container.GetRequiredService<IStepUpAuthentication>(), sessions,
            cameraProvider.Identity, cameraConfiguration, password).ConfigureAwait(true);
        Require(setupSnapshot.Binding is not null && setupSnapshot.Requested is not null &&
            setupSnapshot.Requested.ProductionAcquisitionMode == ProductionAcquisitionMode.SoftwareTrigger,
            "PreviewCameraSetupUnavailable");

        var preview = container.GetRequiredService<IPreviewSessionService>();
        var access = await preview.GetAccessAsync(invocation).ConfigureAwait(true);
        Require(access.CanRun && !access.RequiresStepUp, "PreviewAccessUnavailable");

        var previewSessionId = Guid.NewGuid();
        var start = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, previewSessionId,
            draftReference, expectedActive: null, "V133 start public Draft Preview");
        var startOutcome = await runtime.SubmitAsync(start).ConfigureAwait(true);
        RequireAccepted(startOutcome, "PreviewStartRejected");
        var streaming = await WaitForPreviewAsync(preview, invocation,
            snapshot => snapshot.PreviewSessionId == previewSessionId &&
                snapshot.Phase == PreviewSessionPhase.Streaming && snapshot.LatestFrame is not null,
            "PreviewFrameUnavailable").ConfigureAwait(true);
        var frame = streaming.LatestFrame!;
        var secondStreaming = await WaitForPreviewAsync(preview, invocation,
            snapshot => snapshot.PreviewSessionId == previewSessionId &&
                snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.LatestFrame is { } latest && latest.SessionId == frame.SessionId &&
                latest.Sequence > frame.Sequence,
            "PreviewContinuousFrameUnavailable").ConfigureAwait(true);
        var secondFrame = secondStreaming.LatestFrame!;

        var initialProcess = new PreviewCameraProcessSettings(500, 0,
            cameraConfiguration.RegionOfInterest, cameraConfiguration.PixelFormat,
            cameraConfiguration.ValidBits);
        var tuning = new PreviewTuningConfiguration(initialProcess,
            PreviewAutomaticControlMode.Once, PreviewAutomaticControlMode.Once);
        var tune = new ApplyPreviewTuningCommand(Guid.NewGuid(), invocation,
            previewSessionId, tuning, "V133 tune public Preview automatic controls");
        var tuneOutcome = await runtime.SubmitAsync(tune).ConfigureAwait(true);
        RequireAccepted(tuneOutcome, "PreviewTuneRejected");
        var tuned = await WaitForPreviewAsync(preview, invocation,
            snapshot => snapshot.PreviewSessionId == previewSessionId &&
                snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.Configuration?.ExposureMode == PreviewAutomaticControlMode.Once &&
                snapshot.Configuration.GainMode == PreviewAutomaticControlMode.Once &&
                snapshot.Configuration.ProcessSettings.ExposureTimeUs == 650 &&
                snapshot.Configuration.ProcessSettings.GainDb == 4,
            "PreviewTuneReadBackUnavailable").ConfigureAwait(true);

        var freeze = new FreezePreviewSettingsCommand(Guid.NewGuid(), invocation,
            previewSessionId, "V133 freeze public Preview settings");
        var freezeOutcome = await runtime.SubmitAsync(freeze).ConfigureAwait(true);
        RequireAccepted(freezeOutcome, "PreviewFreezeRejected");
        var frozen = await WaitForPreviewAsync(preview, invocation,
            snapshot => snapshot.PreviewSessionId == previewSessionId &&
                snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.FrozenSettings is not null && snapshot.Configuration is
                { ExposureMode: PreviewAutomaticControlMode.Off,
                  GainMode: PreviewAutomaticControlMode.Off,
                  WhiteBalanceMode: PreviewAutomaticControlMode.Off },
            "PreviewFreezeReadBackUnavailable").ConfigureAwait(true);
        var frozenSettings = frozen.FrozenSettings!;
        var frozenHash = frozen.FrozenSettingsContentHash!;

        var save = new SavePreviewToDraftCommand(Guid.NewGuid(), invocation,
            previewSessionId, draftReference, frozenHash, "V133 save frozen Preview settings to Draft");
        var saveOutcome = await runtime.SubmitAsync(save).ConfigureAwait(true);
        RequireAccepted(saveOutcome, "PreviewSaveRejected");
        var saved = await WaitForPreviewAsync(preview, invocation,
            snapshot => snapshot.PreviewSessionId == previewSessionId &&
                snapshot.Phase == PreviewSessionPhase.Streaming && snapshot.LastSavedDraft is not null,
            "PreviewDraftSaveReadBackUnavailable").ConfigureAwait(true);
        var savedReference = saved.LastSavedDraft!;
        var savedRead = await editor.ReadAsync(savedReference.DraftId, savedReference.Revision)
            .ConfigureAwait(true);
        Require(savedRead.Available && savedRead.Revision is not null &&
            savedRead.Revision.Revision == savedReference.Revision,
            "PreviewSavedDraftUnavailable");
        var savedDraft = savedRead.Revision!;
        Require(savedDraft.Content.Camera.ProductionAcquisitionMode ==
                draft.Content.Camera.ProductionAcquisitionMode &&
            savedDraft.Content.Camera.ExposureTimeUs == frozenSettings.ExposureTimeUs &&
            savedDraft.Content.Camera.GainDb == frozenSettings.GainDb &&
            savedDraft.Content.Camera.RegionOfInterest == cameraConfiguration.RegionOfInterest &&
            savedDraft.Content.Camera.PixelFormat == cameraConfiguration.PixelFormat,
            "PreviewDraftProductionTriggerChanged");

        var exit = new ExitPreviewSessionCommand(Guid.NewGuid(), invocation,
            previewSessionId, cancel: false, "V133 exit public Preview safely");
        var exitOutcome = await runtime.SubmitAsync(exit).ConfigureAwait(true);
        RequireAccepted(exitOutcome, "PreviewExitRejected");
        var closed = await WaitForPreviewAsync(preview, invocation,
            snapshot => snapshot.PreviewSessionId == previewSessionId &&
                snapshot.Phase == PreviewSessionPhase.Closed &&
                snapshot.Restoration == PreviewRestorationState.NoActiveBaselineClosed,
            "PreviewSafeCloseUnavailable").ConfigureAwait(true);
        var final = await runtime.GetSnapshotAsync().ConfigureAwait(true);
        Require(!final.Ready && final.ArmState == ProductionArmState.Disarmed &&
            final.ActiveRecipe is null && final.Mode == ExclusiveMode.None,
            "PreviewChangedProductionAuthority");

        var diagnostics = cameraProvider.GetDiagnostics();
        var device = diagnostics.Devices.Single(value =>
            value.StableDeviceIdentity == StableDeviceIdentity);
        var alarm = options.AlarmPolicy is not null &&
            options.AlarmPolicy.TryGetRule("PreviewRecoveryRequired", out var alarmRule)
            ? alarmRule : null;
        Require(alarm is not null && alarm.Source == "Runtime.Preview" && alarm.IsLatched &&
            alarm.ProductionImpact == ProductionImpact.BlockNewTriggers &&
            (alarm.ResetPrerequisites & (AlarmResetPrerequisites.RecoveryComplete |
                AlarmResetPrerequisites.NoActiveExecution)) ==
            (AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution),
            "PreviewRecoveryAlarmMappingUnavailable");

        var evidence = new
        {
            Result = "Pass", CaseId, ExternalNuGetConsumer =
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                    "SHARPINSPECT_PREVIEW_SESSION_CONSUMER")),
            ConsumerSha256 = HashFile(typeof(PreviewSessionDemo).Assembly.Location),
            StartupFenceCleared = true, PrincipalId = expectedPrincipalId,
            SessionId = session.SessionId!.Value, DraftId = draft.DraftId,
            InitialDraftRevision = draft.Revision,
            SavedDraftRevision = savedDraft.Revision,
            InitialDraftContentHash = draft.RevisionContentHash,
            SavedDraftContentHash = savedDraft.RevisionContentHash,
            FrozenSettingsContentHash = frozenHash,
            FirstFrameSequence = frame.Sequence,
            FirstFrameSessionId = frame.SessionId,
            FrameSequence = secondFrame.Sequence, FrameSessionId = secondFrame.SessionId,
            PreviewFrameObserved = frame.Sequence >= 0 && secondFrame.Sequence > frame.Sequence &&
                secondFrame.SessionId == frame.SessionId,
            TuneConfigurationContentHash = tuned.Configuration!.ContentHash,
            FrozenExposureTimeUs = frozenSettings.ExposureTimeUs,
            FrozenGainDb = frozenSettings.GainDb,
            ProductionAcquisitionModeBefore = draft.Content.Camera.ProductionAcquisitionMode.ToString(),
            ProductionAcquisitionModeAfter = savedDraft.Content.Camera.ProductionAcquisitionMode.ToString(),
            OldTriggerConfigurationPreserved = true,
            StartAccessAvailable = access.CanRun,
            StartRequiresStepUp = access.RequiresStepUp,
            TuneRequiresStepUp = access.RequiresStepUp,
            FreezeRequiresStepUp = access.RequiresStepUp,
            SaveRequiresStepUp = access.RequiresStepUp,
            ExitRequiresStepUp = access.RequiresStepUp,
            StartDisposition = startOutcome.Disposition.ToString(),
            TuneDisposition = tuneOutcome.Disposition.ToString(),
            FreezeDisposition = freezeOutcome.Disposition.ToString(),
            SaveDisposition = saveOutcome.Disposition.ToString(),
            ExitDisposition = exitOutcome.Disposition.ToString(),
            ExitReasonCode = closed.ReasonCode,
            Restoration = closed.Restoration.ToString(),
            Ready = final.Ready, Active = final.ActiveRecipe is not null,
            ArmState = final.ArmState.ToString(),
            Inspection = "NotRun", Algorithm = "NotRun", Plc = "NotRun",
            AlgorithmFactoryCreate = "NotRun", PlcHandshake = "NotRun",
            CameraOpenCount = device.OpenCount, CameraConfigurationAttempts = device.ConfigurationCursor,
            CameraFramesProduced = device.FramesProduced, CameraOpenAfterExit = device.IsOpen,
            AlarmCode = alarm.Code, AlarmSource = alarm.Source,
            AlarmLatched = alarm.IsLatched, AlarmImpact = alarm.ProductionImpact.ToString(),
            AlarmResetPrerequisites = alarm.ResetPrerequisites.ToString()
        };
        await File.WriteAllTextAsync(evidencePath,
            JsonSerializer.Serialize(evidence, JsonOptions)).ConfigureAwait(true);
    }

    private static async Task<RecipeDraftRevision> SaveDraftAsync(ProductionStoreOptions options,
        IRecipeDraftEditor editor, CommandInvocation invocation)
    {
        var factory = RecipeDraftDemo.CreateFactory();
        var configuration = AlgorithmConfigurationSnapshot.Create(
            factory.Descriptor.ConfigurationSchema, new[]
            {
                new AlgorithmConfigurationEntry("enabled", "none",
                    AlgorithmScalarValue.FromBoolean(true)),
                new AlgorithmConfigurationEntry("count", "items",
                    AlgorithmScalarValue.FromInt64(20)),
                new AlgorithmConfigurationEntry("threshold", "mm",
                    AlgorithmScalarValue.FromFloat64(10.25)),
                new AlgorithmConfigurationEntry("label", "text",
                    AlgorithmScalarValue.FromString("预览草稿")),
                new AlgorithmConfigurationEntry("mode", "none",
                    AlgorithmScalarValue.FromEnum("fast")),
                new AlgorithmConfigurationEntry("tag", "none",
                    AlgorithmScalarValue.FromEnum("preview")),
                new AlgorithmConfigurationEntry("optionalFlag", "none",
                    AlgorithmScalarValue.FromBoolean(false))
            });
        var camera = new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            500, 0, new RegionOfInterest(0, 0, 16, 12), VisionPixelFormat.Mono8, null, 1000, 0, null);
        var content = new RecipeDraftContent(RecipeKey, "T33 public Preview consumer recipe",
            RecipeAlgorithmBinding.FromDescriptor(factory.Descriptor), configuration, LogicalRole,
            camera, TimeSpan.FromSeconds(1), Array.Empty<RecipeAssetRequirement>(), new[]
            {
                new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
                    new RecipeContractReference(options.RecipeDrafts!.ExecutionPolicy.Id,
                        options.RecipeDrafts.ExecutionPolicy.Version,
                        options.RecipeDrafts.ExecutionPolicy.ContentHash)),
                new RecipePolicyRequirement(RecipePolicyKind.RecipeGovernance,
                    options.RecipeReleases!.Policy.Reference)
            }, partIdentityRequirement: PartIdentityRequirement.None);
        var result = await editor.SaveAsync(new RecipeDraftSaveRequest(Guid.NewGuid(), Guid.NewGuid(),
            0, null, content, "V133 save exact public Preview Draft", invocation)).ConfigureAwait(true);
        Require(result.Saved && result.Revision is not null, "PreviewDraftSaveFailed");
        return result.Revision!;
    }

    private static async Task<CameraSetupSnapshot> EnsureCameraSetupAsync(ICameraSetupRuntime setup,
        IStepUpAuthentication stepUp, IInteractiveSessionService sessions,
        CameraProviderIdentity provider, RequestedCameraConfiguration requested, string password)
    {
        var invocation = Invocation(sessions);
        var target = new CameraBindingTarget(provider, StableDeviceIdentity);
        var current = await setup.GetSetupAsync(LogicalRole, invocation).ConfigureAwait(true);
        var binding = current.Snapshot?.Binding;
        if (binding is null || binding.Target != target)
        {
            var operation = Guid.NewGuid();
            var grant = await GrantCameraStepUpAsync(stepUp, invocation, operation,
                AuditedCommandKind.RebindCamera, password).ConfigureAwait(true);
            var rebound = await setup.RebindAsync(new(operation, invocation with
            {
                StepUpGrantId = grant.GrantId
            }, LogicalRole, binding?.Revision ?? 0, binding?.RevisionHash, target,
                "V133 bind public Preview camera")).ConfigureAwait(true);
            Require(rebound.Succeeded && rebound.Snapshot?.Binding is not null,
                "PreviewCameraRebindFailed_" + rebound.ReasonCode);
            binding = rebound.Snapshot!.Binding;
        }

        var applyOperation = Guid.NewGuid();
        var applyGrant = await GrantCameraStepUpAsync(stepUp, invocation, applyOperation,
            AuditedCommandKind.ApplyCameraDebugConfiguration, password).ConfigureAwait(true);
        var applied = await setup.ApplyDebugConfigurationAsync(new(applyOperation,
            invocation with { StepUpGrantId = applyGrant.GrantId }, LogicalRole,
            binding!.Revision, binding.RevisionHash, requested,
            "V133 apply public Preview camera trigger baseline")).ConfigureAwait(true);
        Require(applied.Succeeded && applied.Snapshot is not null,
            "PreviewCameraApplyFailed_" + applied.ReasonCode);
        return applied.Snapshot!;
    }

    private static async Task<StepUpResult> GrantCameraStepUpAsync(IStepUpAuthentication stepUp,
        CommandInvocation invocation, Guid operation, AuditedCommandKind command, string password)
    {
        var grant = await stepUp.ReauthenticateAsync(new(Guid.NewGuid(), invocation,
            new(Permission.ManageCameraBindings, operation, LogicalRole, command), password))
            .ConfigureAwait(true);
        Require(grant.Succeeded && grant.GrantId is not null,
            "PreviewCameraStepUpFailed_" + grant.ReasonCode);
        return grant;
    }

    private static async Task<PreviewSessionSnapshot> WaitForPreviewAsync(
        IPreviewSessionService service, CommandInvocation invocation,
        Func<PreviewSessionSnapshot, bool> predicate, string timeoutReason)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            var result = await service.GetSnapshotAsync(invocation).ConfigureAwait(true);
            Require(result.Available && result.Snapshot is not null,
                "PreviewSnapshotUnavailable_" + result.ReasonCode);
            var snapshot = result.Snapshot!;
            if (predicate(snapshot)) return snapshot;
            if (snapshot.Phase is PreviewSessionPhase.Closed or PreviewSessionPhase.RecoveryBlocked)
                throw new PreviewSessionDemoException(timeoutReason + "_" + snapshot.ReasonCode);
            await Task.Delay(20).ConfigureAwait(true);
        }
        throw new PreviewSessionDemoException(timeoutReason);
    }

    private static async Task<StationStateSnapshot> WaitForStartupFenceAsync(IStationRuntime runtime)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            var snapshot = await runtime.GetSnapshotAsync().ConfigureAwait(true);
            var pending = snapshot.AdmissionBlockers.Contains(
                    "RecipeActivationStartupRecoveryPending", StringComparer.Ordinal) ||
                snapshot.AdmissionBlockers.Contains(
                    "PreviewStartupRecoveryPending", StringComparer.Ordinal);
            // Recovery completion and audit projection are published independently.
            // A heartbeat may still expose Verifying after recovery fences clear.
            if (!pending && snapshot.AuditIntegrity?.State == AuditIntegrityState.Verified)
                return snapshot;
            Require(snapshot.AuditIntegrity?.State is not (AuditIntegrityState.Faulted or AuditIntegrityState.NotConfigured),
                "PreviewAuditStartupUnavailable");
            await Task.Delay(20).ConfigureAwait(true);
        }
        throw new PreviewSessionDemoException("PreviewStartupFenceTimeout");
    }

    private static CommandInvocation Invocation(IInteractiveSessionService sessions) =>
        new(CommandSource.PhysicalConsole, sessions.Current.PrincipalId,
            sessions.Current.SessionId);

    private static void RequireAccepted(RuntimeCommandOutcome outcome, string reason)
    {
        Require(outcome.Disposition == CommandDisposition.Accepted &&
            outcome.Audit == AuditPersistence.Persisted, reason + "_" + outcome.ReasonCode);
    }

    private static void RequireConfiguredRun(ProductionStoreOptions options)
    {
        Require(options.PreviewSessions is not null && options.RecipeActivations is not null &&
            options.RecipeReleases is not null && options.RecipeDrafts is not null &&
            options.PlcResultContracts is not null && options.CameraSetup is not null &&
            options.LocalIdentity is not null && options.AuditIntegrityPolicy is not null &&
            options.AlarmPolicy is not null, "PreviewStoreConfigurationIncomplete");
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(stream));
    }

    private static void Require([DoesNotReturnIf(false)] bool condition, string reason)
    {
        if (!condition) throw new PreviewSessionDemoException(reason);
    }

    private sealed class PreviewSessionDemoException : Exception
    {
        internal PreviewSessionDemoException(string reasonCode) => ReasonCode = reasonCode;
        internal string ReasonCode { get; }
    }
}

/// <summary>
/// Advances only the SampleHost virtual Preview fixture. The Runtime never
/// owns this pump; disposing it cancels and joins the bounded worker before
/// the virtual clock or provider can be released.
/// </summary>
internal sealed class PreviewClockPump : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
    private readonly VirtualCameraClock _clock;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private int _disposed;

    private PreviewClockPump(VirtualCameraClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _worker = Task.Run(RunAsync);
    }

    internal static PreviewClockPump Start(VirtualCameraClock clock) => new(clock);

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(Tick, _stop.Token).ConfigureAwait(false);
                _clock.AdvanceBy(Tick);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { _worker.GetAwaiter().GetResult(); }
        finally { _stop.Dispose(); }
    }
}
