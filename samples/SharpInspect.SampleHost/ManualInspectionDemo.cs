using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Manual;
using SharpInspect.Runtime.Storage;
using SharpInspect.Wpf;

namespace SharpInspect.SampleHost;

/// <summary>
/// Public-package consumer for the non-production Manual Inspection boundary.
/// The sample creates a Draft and camera binding through public capabilities,
/// then drives typed Runtime commands through the public WPF view model.  The
/// result is read back from the independent Manual history query; no ledger
/// row is manufactured by this sample.
/// </summary>
internal static partial class ManualInspectionDemo
{
    private const string CaseId = "V135_N01";
    private const string LogicalRole = "ManualCamera";
    private const string StableDeviceIdentity = "Virtual:Manual";
    private const string RecipeKey = "ManualInspection.Sample";
    private const uint Seed = 135;
    private static readonly DateTimeOffset InitialUtc =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Runs the bounded public consumer.  The password is read as one JSON
    /// string from stdin, so it is never placed in command arguments or the
    /// evidence document.
    /// </summary>
    internal static int Run(ProductionStoreOptions options, string directory,
        string? configuredUserName, string? expectedPrincipal)
    {
        try
        {
            var password = JsonSerializer.Deserialize<string>(Console.ReadLine() ?? "null")
                ?? throw new ManualInspectionDemoException("ManualConsumerPasswordRequired");
            Pump(() => RunCoreAsync(options, Path.GetFullPath(directory), configuredUserName,
                expectedPrincipal, password));
            Console.WriteLine("V135_N01 manual-inspection PASS draft=true start=true runs=3 decisions=Pass,Fail,Unknown preparedInstanceStable=true exit=true ready=false active=false productionFacts=0");
            return 0;
        }
        catch (ManualInspectionDemoException exception)
        {
            Console.Error.WriteLine($"{CaseId} manual-inspection FAIL reason={exception.ReasonCode}");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{CaseId} manual-inspection FAIL reason=ManualInspectionConsumerCheckFailed");
            Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static async Task RunCoreAsync(ProductionStoreOptions options, string directory,
        string? configuredUserName, string? expectedPrincipal, string password)
    {
        RequireConfiguredRun(options);
        Require(!string.IsNullOrWhiteSpace(configuredUserName), "ManualConsumerUserRequired");
        Require(Guid.TryParse(expectedPrincipal, out var expectedPrincipalId) &&
            expectedPrincipalId != Guid.Empty, "ManualConsumerPrincipalRequired");
        Require(File.Exists(options.DatabasePath), "ManualConsumerIdentityStoreRequired");

        Directory.CreateDirectory(directory);
        var evidencePath = Path.Combine(directory, "manual-inspection-evidence.json");
        Require(!File.Exists(evidencePath), "ManualInspectionEvidenceAlreadyExists");

        using var clock = new VirtualCameraClock(InitialUtc);
        var services = new ServiceCollection();
        ConfigureRuntimeServices(services, clock, options.RecipeDrafts!.ExecutionPolicy);
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(100));
        services.AddSingleton<ManualInspectionSessionViewModel>(provider =>
            new ManualInspectionSessionViewModel(
                provider.GetRequiredService<IStationRuntime>(),
                provider.GetRequiredService<IManualInspectionSessionService>(),
                provider.GetRequiredService<IInteractiveSessionService>(),
                provider.GetService<IRecipeDraftHistoryQuery>(),
                provider.GetService<IReleasedRecipeQuery>(),
                provider.GetService<IRecipeActivationQuery>(),
                new DispatcherUiDispatcher(Dispatcher.CurrentDispatcher),
                provider.GetService<IStepUpAuthentication>(),
                provider.GetService<IManualInspectionHistoryQuery>()));

        await using var container = services.BuildServiceProvider();
        using var clockPump = ManualClockPump.Start(clock);
        var factory = container.GetRequiredService<IVisionAlgorithmFactory>() as
            ManualInspectionAlgorithmFactory ??
            throw new ManualInspectionDemoException("ManualFactoryRegistrationUnavailable");
        var cameraProvider = container.GetRequiredService<ICameraProvider>() as
            VirtualCameraProvider ??
            throw new ManualInspectionDemoException("ManualCameraProviderRegistrationUnavailable");

        var runtime = container.GetRequiredService<IStationRuntime>();
        var startup = await WaitForStartupFenceAsync(runtime).ConfigureAwait(true);
        Require(startup.Lifecycle == RuntimeLifecycle.Running &&
            !startup.AdmissionBlockers.Contains("ManualInspectionStartupRecoveryPending",
                StringComparer.Ordinal) &&
            !startup.AdmissionBlockers.Contains("ManualInspectionStartupRecoveryRequired",
                StringComparer.Ordinal), "ManualInspectionStartupRecoveryUnavailable");
        var initial = await runtime.GetSnapshotAsync().ConfigureAwait(true);
        Require(!initial.Ready && initial.ArmState == ProductionArmState.Disarmed &&
            initial.ActiveRecipe is null, "ManualInitialStationNotDisarmed");

        var sessions = container.GetRequiredService<IInteractiveSessionService>();
        var login = await sessions.SignInAsync(new PasswordSignInRequest(
            configuredUserName!, password)).ConfigureAwait(true);
        Require(login.Succeeded && login.Identity?.PrincipalId == expectedPrincipalId,
            "ManualConsumerAuthenticationFailed");
        var session = sessions.Current;
        Require(session.State == InteractiveSessionState.Authenticated &&
            session.SessionId is not null && session.PrincipalId == expectedPrincipalId.ToString("D"),
            "ManualConsumerSessionUnavailable");
        var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
            session.PrincipalId, session.SessionId);

        var editor = container.GetRequiredService<IRecipeDraftEditor>();
        var draft = await SaveDraftAsync(options, editor, invocation, factory.Descriptor)
            .ConfigureAwait(true);
        var setup = container.GetRequiredService<ICameraSetupRuntime>();
        var configured = await EnsureCameraBindingAsync(setup,
            container.GetRequiredService<IStepUpAuthentication>(), sessions,
            cameraProvider.Identity, password).ConfigureAwait(true);
        Require(configured.Binding is not null,
            "ManualCameraSetupUnavailable");

        var viewModel = container.GetRequiredService<ManualInspectionSessionViewModel>();
        await using (viewModel)
        {
            viewModel.SelectedDraft = draft;
            viewModel.StartReason = "V135 start public Manual Inspection Draft";
            viewModel.RunReason = "V135 run one public Manual Inspection frame";
            viewModel.ExitReason = "V135 close public Manual Inspection safely";
            await viewModel.RefreshAsync().ConfigureAwait(true);
            Require(viewModel.Selection == ManualRecipeSelection.FromDraft(draft) &&
                viewModel.ExpectedActive is null && viewModel.Access?.CanRun == true,
                "ManualDraftSelectionUnavailable");

            var start = await viewModel.StartAsync().ConfigureAwait(true);
            RequireAccepted(start, "ManualStartRejected");
            var ready = await WaitForPhaseAsync(viewModel,
                snapshot => snapshot.Phase == ManualInspectionSessionPhase.ReadyForRun,
                "ManualPreparationUnavailable").ConfigureAwait(true);
            Require(ready.SessionId.HasValue && ready.SessionId.Value != Guid.Empty,
                "ManualRuntimeSessionIdUnavailable");
            var sessionId = ready.SessionId!.Value;

            await using var competingPreview = new PreviewSessionViewModel(runtime,
                container.GetRequiredService<IPreviewSessionService>(), sessions,
                new DispatcherUiDispatcher(Dispatcher.CurrentDispatcher));
            var previewConflict = await competingPreview.StartPreviewSessionAsync(
                PreviewDraftReference.FromRevision(draft), null, "V135 Preview conflicts with Manual")
                .ConfigureAwait(true);
            Require(previewConflict is { Disposition: CommandDisposition.Rejected,
                ReasonCode: "ManualInspectionSessionInProgress" }, "ManualPreviewCompetitionNotRejected_" +
                (previewConflict?.ReasonCode ?? competingPreview.ErrorCode ?? "NoOutcome"));

            var runOutcomes = new List<RuntimeCommandOutcome>(3);
            RuntimeCommandOutcome? busyConflict = null;
            for (var index = 0; index < 3; index++)
            {
                if (index == 0) clockPump.Pause();
                var run = await viewModel.RunOneAsync().ConfigureAwait(true);
                RequireAccepted(run, "ManualRunRejected_" + (index + 1).ToString(CultureInfo.InvariantCulture));
                runOutcomes.Add(run!);
                if (index == 0)
                {
                    try
                    {
                        busyConflict = await viewModel.RunOneAsync().ConfigureAwait(true);
                        Require(busyConflict is { Disposition: CommandDisposition.Rejected,
                            ReasonCode: "ManualInspectionRunInProgress" }, "ManualBusyRunNotRejected");
                    }
                    finally { clockPump.Resume(); }
                }
                await WaitForPhaseAsync(viewModel,
                    snapshot => snapshot.SessionId == sessionId &&
                        snapshot.Phase == ManualInspectionSessionPhase.ReadyForRun,
                    "ManualRunDidNotRetire_" + (index + 1).ToString(CultureInfo.InvariantCulture))
                    .ConfigureAwait(true);
            }

            var exit = await viewModel.GracefulExitAsync().ConfigureAwait(true);
            RequireAccepted(exit, "ManualExitRejected");
            var closed = await WaitForPhaseAsync(viewModel,
                snapshot => snapshot.SessionId == sessionId &&
                    snapshot.Phase == ManualInspectionSessionPhase.Closed &&
                    snapshot.Restoration == ManualInspectionRestorationState.NoActiveBaselineClosed,
                "ManualSafeCloseUnavailable").ConfigureAwait(true);
            Require(!closed.RecoveryRequired, "ManualSafeCloseRecoveryRequired");

            var final = await runtime.GetSnapshotAsync().ConfigureAwait(true);
            Require(!final.Ready && final.ArmState == ProductionArmState.Disarmed &&
                final.ActiveRecipe is null && final.Mode == ExclusiveMode.None,
                "ManualChangedProductionAuthority");

            var history = container.GetRequiredService<IManualInspectionHistoryQuery>();
            // AddSharpInspectSqliteRuntime registers SqliteManualInspectionQuery as
            // an independent read-only capability; this query never uses the writer.
            var page = await history.QueryAsync(new ManualInspectionHistoryFilter(
                SessionId: sessionId, PageSize: 20)).ConfigureAwait(true);
            Require(page.Available && !page.RecoveryRequired,
                "ManualHistoryUnavailable_" + page.ReasonCode);
            var runs = page.Runs.Where(run => run.SessionId == sessionId)
                .OrderBy(run => run.Position).ToArray();
            Require(runs.Length == 3 && runs.All(run => run.Terminal),
                "ManualHistoryRunCountInvalid");
            Require(runs.Select(run => run.CommandCorrelationId).SequenceEqual(
                runOutcomes.Select(run => run.CorrelationId)) &&
                runs.Select(run => run.RunId).Distinct().Count() == 3,
                "ManualHistoryAcceptedCommandBindingMismatch");
            Require(runs.Select(run => run.Decision).SequenceEqual(new[]
                { InspectionDecision.Pass, InspectionDecision.Fail, InspectionDecision.Unknown }),
                "ManualHistoryDecisionSequenceInvalid");
            Require(runs.All(run => run.ExecutionStatus == ExecutionStatus.Success &&
                run.Result is not null && run.ResultSchema is not null &&
                run.FrameOverlay is not null && run.AlgorithmResultContentHash is not null),
                "ManualTypedResultUnavailable");
            Require(runs.Select(run => run.PreparedInstanceId).Distinct().Count() == 1 &&
                runs[0].PreparedInstanceId is { } prepared && prepared != Guid.Empty &&
                factory.Created == 1, "ManualPreparedInstanceWasNotReused");
            var header = page.Events.Select(item => item.Header)
                .FirstOrDefault(item => item.SessionId == sessionId) ?? page.PendingHeader;
            Require(header is not null && header.Selection.Kind == ManualRecipeSourceKind.Draft &&
                header.Selection.DraftId == draft.DraftId &&
                header.Selection.DraftRevision == draft.Revision &&
                header.Selection.DraftRevisionContentHash == draft.RevisionContentHash &&
                header.SessionId == sessionId && header.StartCorrelationId == start!.CorrelationId &&
                header.ActorPrincipalId == expectedPrincipalId &&
                header.ActorSessionId == session.SessionId.Value,
                "ManualHistorySourceAttributionInvalid");

            var edgeCases = await RunFailureCasesAsync(viewModel, factory, runtime, history,
                busyConflict!, previewConflict!, sessionId, runs.Select(run => run.RunId)).ConfigureAwait(true);

            var traceEvidence = await ReadProductionFactsAsync(container,
                CancellationToken.None).ConfigureAwait(true);
            Require(traceEvidence.ProductionFactCount == 0,
                "ManualProductionFactWasWritten");

            var evidence = new ManualInspectionEvidence(
                Result: "Pass", CaseId: CaseId, PrincipalId: header.ActorPrincipalId,
                ActorSessionId: header.ActorSessionId, ActorAuthorizationRevision:
                    header.ActorAuthorizationRevision, Source: new ManualSourceEvidence(
                        header.Selection.Kind.ToString(), header.Selection.DraftId!.Value,
                        header.Selection.DraftRevision!.Value,
                        header.Selection.DraftRevisionContentHash!, header.SourceContentHash),
                SessionId: sessionId, RuntimeEpoch: header.RuntimeEpoch,
                StartCorrelationId: header.StartCorrelationId,
                StartDisposition: runOutcomes.Count == 3 ? "Accepted" : "Invalid",
                ExitDisposition: exit!.Disposition.ToString(),
                Restoration: closed.Restoration.ToString(),
                Ready: final.Ready, Active: final.ActiveRecipe is not null,
                ProductionFactCount: traceEvidence.ProductionFactCount,
                QueriedCommandFactCount: traceEvidence.CommandFactCount,
                HistoryEventCount: page.Events.Count, HistoryRunCount: runs.Length,
                Runs: runs.Select(CreateRunEvidence).ToArray(),
                CameraOpenCount: cameraProvider.GetDiagnostics().Devices
                    .Single(device => device.StableDeviceIdentity == StableDeviceIdentity).OpenCount,
                CameraFramesProduced: cameraProvider.GetDiagnostics().Devices
                    .Single(device => device.StableDeviceIdentity == StableDeviceIdentity).FramesProduced,
                CameraOpenAfterExit: cameraProvider.GetDiagnostics().Devices
                    .Single(device => device.StableDeviceIdentity == StableDeviceIdentity).IsOpen,
                ConsumerAssemblySha256: HashFile(typeof(ManualInspectionDemo).Assembly.Location),
                EdgeCases: edgeCases, AcceptedRunCorrelations: runOutcomes.Select(run => run.CorrelationId).ToArray());
            await File.WriteAllTextAsync(evidencePath,
                JsonSerializer.Serialize(evidence, JsonOptions)).ConfigureAwait(true);
        }
    }

    private static ManualRunEvidence CreateRunEvidence(ManualInspectionRunRecord run) =>
        new(run.RunId, run.CommandCorrelationId, run.AttemptId, run.Status.ToString(),
            run.Decision.ToString(), run.ExecutionStatus?.ToString(), run.ReasonCode,
            run.PreparedInstanceId, run.Algorithm?.Id, run.Algorithm?.Version,
            run.ResultSchema?.Id, run.ResultSchema?.Version, run.ResultSchemaContentHash,
            run.AlgorithmResultContentHash, run.Result is { } result
                ? new ManualResultEvidence(result.Decision.ToString(), result.ReasonCode,
                    result.Measurements.Select(measurement => new ManualMeasurementEvidence(
                        measurement.Key, measurement.Unit, ScalarValue(measurement.Value))).ToArray(),
                    result.OverlaySet.ContractId, result.OverlaySet.ContractVersion,
                    result.OverlaySet.Primitives.Count)
                : null,
            run.FrameOverlayContentHash, run.Evidence.Select(item => item.ContentHash).ToArray());

    private static object ScalarValue(AlgorithmScalarValue value) => value.Type switch
    {
        AlgorithmScalarType.Boolean => value.AsBoolean(),
        AlgorithmScalarType.Int64 => value.AsInt64(),
        AlgorithmScalarType.Float64 => value.AsFloat64(),
        AlgorithmScalarType.String => value.AsString(),
        AlgorithmScalarType.Enum => value.AsEnum(),
        _ => throw new ManualInspectionDemoException("ManualResultScalarInvalid")
    };

    private static async Task<RecipeDraftRevision> SaveDraftAsync(ProductionStoreOptions options,
        IRecipeDraftEditor editor, CommandInvocation invocation, AlgorithmDescriptor descriptor)
    {
        var configuration = AlgorithmConfigurationSnapshot.Create(
            descriptor.ConfigurationSchema, Array.Empty<AlgorithmConfigurationEntry>());
        var camera = new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            500, 0, new RegionOfInterest(0, 0, 16, 12), VisionPixelFormat.Mono8,
            null, 500, 0, null);
        var content = new RecipeDraftContent(RecipeKey, "V135 public Manual Inspection Draft",
            RecipeAlgorithmBinding.FromDescriptor(descriptor), configuration, LogicalRole,
            camera, TimeSpan.FromSeconds(2),
            Array.Empty<RecipeAssetRequirement>(), new[]
            {
                new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
                    new RecipeContractReference(options.RecipeDrafts!.ExecutionPolicy.Id,
                        options.RecipeDrafts.ExecutionPolicy.Version,
                        options.RecipeDrafts.ExecutionPolicy.ContentHash))
            }, partIdentityRequirement: PartIdentityRequirement.None);
        var result = await editor.SaveAsync(new RecipeDraftSaveRequest(Guid.NewGuid(), Guid.NewGuid(),
            0, null, content, "V135 save exact public Manual Inspection Draft", invocation))
            .ConfigureAwait(true);
        Require(result.Saved && result.Revision is not null, "ManualDraftSaveFailed_" + result.ReasonCode);
        return result.Revision!;
    }

    private static async Task<CameraSetupSnapshot> EnsureCameraBindingAsync(ICameraSetupRuntime setup,
        IStepUpAuthentication stepUp, IInteractiveSessionService sessions,
        CameraProviderIdentity provider, string password)
    {
        var invocation = CurrentInvocation(sessions);
        var target = new CameraBindingTarget(provider, StableDeviceIdentity);
        var current = await setup.GetSetupAsync(LogicalRole, invocation).ConfigureAwait(true);
        var binding = current.Snapshot?.Binding;
        if (binding is null || binding.Target != target)
        {
            var operation = Guid.NewGuid();
            var grant = await GrantCameraStepUpAsync(stepUp, invocation, operation,
                AuditedCommandKind.RebindCamera, password).ConfigureAwait(true);
            var rebound = await setup.RebindAsync(new CameraRebindRequest(operation,
                invocation with { StepUpGrantId = grant.GrantId }, LogicalRole,
                binding?.Revision ?? 0, binding?.RevisionHash, target,
                "V135 bind public Manual Inspection camera")).ConfigureAwait(true);
            Require(rebound.Succeeded && rebound.Snapshot?.Binding is not null,
                "ManualCameraRebindFailed_" + rebound.ReasonCode);
            return rebound.Snapshot!;
        }
        // The Manual candidate owns applying and reading back its exact Draft
        // configuration. A separate debug Apply is not an entry prerequisite.
        return current.Snapshot!;
    }

    private static async Task<StepUpResult> GrantCameraStepUpAsync(IStepUpAuthentication stepUp,
        CommandInvocation invocation, Guid operation, AuditedCommandKind command, string password)
    {
        var grant = await stepUp.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(), invocation,
            new StepUpBinding(Permission.ManageCameraBindings, operation, LogicalRole, command), password))
            .ConfigureAwait(true);
        Require(grant.Succeeded && grant.GrantId is { } grantId && grantId != Guid.Empty,
            "ManualCameraStepUpFailed_" + grant.ReasonCode);
        return grant;
    }

    private static async Task<ManualInspectionSessionSnapshot> WaitForPhaseAsync(
        ManualInspectionSessionViewModel viewModel,
        Func<ManualInspectionSessionSnapshot, bool> predicate, string timeoutReason)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            await viewModel.RefreshAsync().ConfigureAwait(true);
            if (viewModel.Snapshot is { } snapshot)
            {
                if (predicate(snapshot)) return snapshot;
                if (snapshot.Phase == ManualInspectionSessionPhase.RecoveryBlocked ||
                    snapshot.Restoration == ManualInspectionRestorationState.RecoveryBlocked)
                    throw new ManualInspectionDemoException(timeoutReason + "_" + snapshot.ReasonCode);
            }
            await Task.Delay(20).ConfigureAwait(true);
        }
        throw new ManualInspectionDemoException(timeoutReason + "_" +
            viewModel.Snapshot?.Phase + "_" + (viewModel.Snapshot?.ReasonCode ?? viewModel.ErrorCode));
    }

    private static async Task<StationStateSnapshot> WaitForStartupFenceAsync(IStationRuntime runtime)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            var snapshot = await runtime.GetSnapshotAsync().ConfigureAwait(true);
            var pending = snapshot.AdmissionBlockers.Contains(
                    "ManualInspectionStartupRecoveryPending", StringComparer.Ordinal) ||
                snapshot.AdmissionBlockers.Contains(
                    "ManualInspectionStartupRecoveryRequired", StringComparer.Ordinal);
            if (!pending && snapshot.AuditIntegrity?.State == AuditIntegrityState.Verified)
                return snapshot;
            Require(snapshot.AuditIntegrity?.State is not
                (AuditIntegrityState.Faulted or AuditIntegrityState.NotConfigured),
                "ManualAuditStartupUnavailable");
            await Task.Delay(20).ConfigureAwait(true);
        }
        throw new ManualInspectionDemoException("ManualStartupFenceTimeout");
    }

    private static async Task<ProductionFactEvidence> ReadProductionFactsAsync(
        IServiceProvider provider, CancellationToken cancellationToken)
    {
        var query = provider.GetRequiredService<ICommandTraceQuery>();
        var records = new List<CommandTraceRecord>();
        long after = 0;
        long? through = null;
        var complete = false;
        for (var pageNumber = 0; pageNumber < 8; pageNumber++)
        {
            var page = await query.QueryAsync(new CommandTraceFilter(
                AfterPosition: after, ThroughPosition: through, PageSize: 200), cancellationToken)
                .ConfigureAwait(true);
            if (through is { } expectedThrough && page.ThroughPosition != expectedThrough)
                throw new ManualInspectionDemoException("ManualCommandTraceUpperBoundChanged");
            records.AddRange(page.Records);
            if (page.NextAfterPosition is not { } next)
            {
                complete = true;
                break;
            }
            if (next <= after || next > page.ThroughPosition)
                throw new ManualInspectionDemoException("ManualCommandTracePageInvalid");
            after = next;
            through = page.ThroughPosition;
        }
        Require(complete, "ManualCommandTraceRangeExceeded");

        var productionKinds = records.Where(record => record.CommandKind is
            AuditedCommandKind.ArmProduction or AuditedCommandKind.GracefulProductionStop or
            AuditedCommandKind.ActivateRecipe).ToArray();
        return new ProductionFactEvidence(records.Count, productionKinds.Length);
    }

    /// <summary>
    /// Registers the bounded Manual Inspection dependencies for either the
    /// command-line consumer or the WPF host.  The supplied clock is the one
    /// passed to the Virtual provider and to Runtime's frame-clock service.
    /// </summary>
    internal static void ConfigureRuntimeServices(IServiceCollection services,
        VirtualCameraClock clock) => ConfigureRuntimeServices(services, clock,
            RecipeDraftDemo.ExecutionPolicy);

    internal static void ConfigureRuntimeServices(IServiceCollection services,
        VirtualCameraClock clock, AlgorithmExecutionPolicy executionPolicy)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(executionPolicy);
        services.AddSingleton(clock);
        services.AddSingleton<IVisionAlgorithmFactory>(CreateFactory());
        services.AddSingleton<IFrameAcquisitionClock>(clock);
        services.AddSharpInspectAlgorithmPreparation(new AlgorithmPreparationOptions(
            TimeSpan.FromSeconds(5), maximumConcurrentPreparations: 1, maximumOwnedInstances: 2));
        services.AddSharpInspectAlgorithmExecution(new AlgorithmExecutionOptions(
            executionPolicy, TimeSpan.FromSeconds(5)));
        services.AddSharpInspectFrameBufferPool(new FrameBufferPoolOptions(
            capacity: 2, maximumFrameBytes: 4096, callbackBudget: TimeSpan.FromSeconds(1)));
        services.AddSingleton(new ManualInspectionSessionOptions());
        services.AddSharpInspectCameraProvider(CreateProvider(clock));
    }

    /// <summary>
    /// Adds the mappings required by the Manual Runtime boundary while
    /// retaining any policy rules already supplied by the host.  A conflicting
    /// mapping is an explicit configuration error; it is never silently fixed.
    /// </summary>
    internal static AlarmPolicy? EnsureRecoveryAlarmPolicy(AlarmPolicy? source,
        bool manualEnabled = true)
    {
        if (!manualEnabled) return source;

        var required = new[]
        {
            new AlarmPolicyRule("StartupRecoveryRequired", "Runtime.StartupRecovery",
                AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null,
                ResetPrerequisites: AlarmResetPrerequisites.RecoveryComplete |
                    AlarmResetPrerequisites.NoPendingDelivery),
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
        };

        var rules = source?.Rules.ToList() ?? new List<AlarmPolicyRule>();
        var changed = source is null;
        foreach (var expected in required)
        {
            var configured = rules.FirstOrDefault(rule =>
                string.Equals(rule.Code, expected.Code, StringComparison.Ordinal));
            if (configured is not null)
            {
                Require(IsAlarmRuleCompatible(expected, configured),
                    expected.Code + "AlarmMappingMismatch");
                continue;
            }

            rules.Add(expected);
            changed = true;
        }

        if (!changed) return source;
        return new AlarmPolicy(source?.Id ?? "Sample.ManualInspection",
            source?.Version ?? "development-v1", rules,
            source?.SourceObservationFreshness ?? TimeSpan.FromMinutes(1),
            source?.MaximumActiveInstances ?? 256,
            source?.MaximumPlcEntries ?? 16);
    }

    private static bool IsAlarmRuleCompatible(AlarmPolicyRule expected,
        AlarmPolicyRule configured)
    {
        var expectedPrerequisites = expected.ResetPrerequisites;
        var configuredPrerequisites = configured.ResetPrerequisites;
        var prerequisitesMatch = (configuredPrerequisites & expectedPrerequisites) ==
            expectedPrerequisites;
        var impactMatch = expected.Code == "AlgorithmHung"
            ? configured.ProductionImpact is ProductionImpact.BlockNewTriggers or ProductionImpact.FaultAbort
            : configured.ProductionImpact == expected.ProductionImpact;
        return configured.Source == expected.Source && configured.IsLatched && impactMatch &&
            prerequisitesMatch;
    }

    internal static IVisionAlgorithmFactory CreateFactory() => new ManualInspectionAlgorithmFactory();

    internal static VirtualCameraProvider CreateProvider(VirtualCameraClock clock)
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
        var acquisitions = Enumerable.Range(0, 4).Select(_ =>
            new VirtualCameraAcquisitionPlan(new[]
            {
                new VirtualCameraSignal(TimeSpan.FromMilliseconds(100),
                    VirtualCameraSignalKind.Frame, image.Id)
            })).Append(new VirtualCameraAcquisitionPlan(new[]
            {
                new VirtualCameraSignal(TimeSpan.FromMilliseconds(100), VirtualCameraSignalKind.Disconnect)
            })).ToArray();
        var configurations = Enumerable.Repeat(
            new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success,
                TimeSpan.Zero), 16);
        var scenario = new VirtualCameraScenario("Manual", "1", Seed,
            StableDeviceIdentity, capabilities, new[] { image }, acquisitions,
            configurations);
        return new VirtualCameraProvider(new[] { scenario }, clock, poolCapacity: 2);
    }

    private static CommandInvocation CurrentInvocation(IInteractiveSessionService sessions) =>
        new(CommandSource.PhysicalConsole, sessions.Current.PrincipalId,
            sessions.Current.SessionId);

    private static void RequireAccepted(RuntimeCommandOutcome? outcome, string reason)
    {
        Require(outcome is { Disposition: CommandDisposition.Accepted,
            Audit: AuditPersistence.Persisted }, reason + "_" + outcome?.ReasonCode);
    }

    private static void RequireConfiguredRun(ProductionStoreOptions options)
    {
        Require(options.ManualInspections is not null && options.RecipeDrafts is not null &&
            options.CameraSetup is not null && options.LocalIdentity is not null &&
            options.AuditIntegrityPolicy is not null && options.AlarmPolicy is not null,
            "ManualStoreConfigurationIncomplete");
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(stream));
    }

    private static void Pump(Func<Task> operation)
    {
        Exception? failure = null;
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            try { await operation().ConfigureAwait(true); }
            catch (Exception exception) { failure = exception; }
            finally { frame.Continue = false; }
        }));
        Dispatcher.PushFrame(frame);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Require([DoesNotReturnIf(false)] bool condition, string reason)
    {
        if (!condition) throw new ManualInspectionDemoException(reason);
    }

    private sealed class ManualInspectionDemoException : Exception
    {
        internal ManualInspectionDemoException(string reasonCode) => ReasonCode = reasonCode;
        internal string ReasonCode { get; }
    }

    private sealed record ProductionFactEvidence(int CommandFactCount, int ProductionFactCount);

    private sealed record ManualInspectionEvidence(string Result, string CaseId,
        Guid PrincipalId, Guid ActorSessionId, long ActorAuthorizationRevision,
        ManualSourceEvidence Source, Guid SessionId, Guid RuntimeEpoch,
        Guid StartCorrelationId, string StartDisposition, string ExitDisposition,
        string Restoration, bool Ready, bool Active, int ProductionFactCount,
        int QueriedCommandFactCount, int HistoryEventCount, int HistoryRunCount,
        IReadOnlyList<ManualRunEvidence> Runs, int CameraOpenCount,
        long CameraFramesProduced, bool CameraOpenAfterExit,
        string ConsumerAssemblySha256, ManualFailureEvidence EdgeCases, IReadOnlyList<Guid> AcceptedRunCorrelations);

    private sealed record ManualSourceEvidence(string Kind, Guid DraftId,
        long DraftRevision, string DraftRevisionContentHash, string SourceContentHash);

    private sealed record ManualRunEvidence(Guid RunId, Guid CommandCorrelationId,
        Guid AttemptId, string Status, string Decision, string? ExecutionStatus,
        string ReasonCode, Guid? PreparedInstanceId, string? AlgorithmId,
        string? AlgorithmVersion, string? ResultSchemaId, string? ResultSchemaVersion,
        string? ResultSchemaContentHash, string? ResultContentHash,
        ManualResultEvidence? Result, string? FrameOverlayContentHash,
        IReadOnlyList<string> EvidenceHashes);

    private sealed record ManualResultEvidence(string Decision, string? ReasonCode,
        IReadOnlyList<ManualMeasurementEvidence> Measurements, string OverlayContractId,
        string OverlayContractVersion, int OverlayElementCount);

    private sealed record ManualMeasurementEvidence(string Key, string Unit, object Value);
}

/// <summary>Small bounded clock driver owned by the sample host.</summary>
internal sealed class ManualClockPump : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(10);
    private readonly VirtualCameraClock _clock;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private int _disposed;
    private int _paused;

    private ManualClockPump(VirtualCameraClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _worker = Task.Run(RunAsync);
    }

    internal static ManualClockPump Start(VirtualCameraClock clock) => new(clock);
    internal void Pause() => Volatile.Write(ref _paused, 1);
    internal void Resume() => Volatile.Write(ref _paused, 0);

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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { _worker.GetAwaiter().GetResult(); }
        finally { _stop.Dispose(); }
    }
}

/// <summary>
/// Explicit sample algorithm registration.  One prepared instance keeps a
/// bounded sequence so the three real Runtime runs produce Pass, Fail and
/// Unknown typed results in order.
/// </summary>
internal sealed class ManualInspectionAlgorithmFactory : IVisionAlgorithmFactory
{
    private int _created;
    private int _timeoutNext;
    internal void TimeoutNextExecution() => Interlocked.Exchange(ref _timeoutNext, 1);

    internal ManualInspectionAlgorithmFactory()
    {
        Descriptor = new AlgorithmDescriptor(
            new AlgorithmIdentity("Sample.ManualInspection", "1"),
            new AlgorithmConfigurationSchema("Sample.ManualInspection.Config", "1",
                Array.Empty<AlgorithmFieldDefinition>()),
            new AlgorithmResultSchema("Sample.ManualInspection.Result", "1",
                new[]
                {
                    new AlgorithmFieldDefinition("Score", AlgorithmScalarType.Float64,
                        "ratio", required: true,
                        constraints: new AlgorithmScalarConstraints(minFloat64: 0, maxFloat64: 100))
                },
                new[] { "ManualSyntheticUnknown" },
                new OverlayContract("Sample.ManualInspection.Overlay", "1",
                    maximumElements: 1, maximumTotalPoints: 4,
                    maximumPointsPerElement: 4, maximumTextLength: 0)));
    }

    public AlgorithmDescriptor Descriptor { get; }
    internal int Created => Volatile.Read(ref _created);

    public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
        AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(configuration.Validate(Descriptor.ConfigurationSchema));
    }

    public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _created);
        return ValueTask.FromResult<IVisionAlgorithm>(
            new ManualInspectionAlgorithm(Descriptor, this));
    }

    private sealed class ManualInspectionAlgorithm : IVisionAlgorithm
    {
        private readonly AlgorithmDescriptor _descriptor;
        private readonly ManualInspectionAlgorithmFactory _factory;
        private int _sequence;
        private bool _warmed;
        private bool _disposed;

        internal ManualInspectionAlgorithm(AlgorithmDescriptor descriptor, ManualInspectionAlgorithmFactory factory)
        { _descriptor = descriptor; _factory = factory; }

        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) throw new InvalidOperationException("ManualAlgorithmDisposed");
            _warmed = true;
            return ValueTask.CompletedTask;
        }

        public async ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_warmed || _disposed) throw new InvalidOperationException("ManualAlgorithmNotPrepared");
            if (Interlocked.Exchange(ref _factory._timeoutNext, 0) != 0)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            var sequence = Interlocked.Increment(ref _sequence);
            var decision = sequence switch
            {
                1 => InspectionDecision.Pass,
                2 => InspectionDecision.Fail,
                _ => InspectionDecision.Unknown
            };
            var reason = decision == InspectionDecision.Unknown ? "ManualSyntheticUnknown" : null;
            var measurement = new AlgorithmMeasurement("Score", "ratio",
                AlgorithmScalarValue.FromFloat64(sequence switch
                {
                    1 => 10,
                    2 => 20,
                    _ => 30
                }));
            var overlay = new OutputOverlaySet(_descriptor.ResultSchema.OverlayContract,
                new OverlayPrimitive[]
                {
                    new OverlayAxisAlignedRectangle(new OverlayPoint(1, 1), 8, 8)
                });
            return new AlgorithmResult(decision, reason, new[] { measurement }, overlay);
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            _warmed = false;
            return ValueTask.CompletedTask;
        }
    }
}
