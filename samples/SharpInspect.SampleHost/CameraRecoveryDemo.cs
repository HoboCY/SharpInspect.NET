using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.SampleHost;

/// <summary>
/// Independent public-interface consumer for the bounded camera-recovery
/// capability. It uses one virtual provider and one exact binding. The sample
/// deliberately keeps qualification separate from production admission.
/// </summary>
internal static class CameraRecoveryDemo
{
    private const string Role = "RecoveryCamera";
    private const string Device = "Virtual:Recovery-A";
    private const string EvidenceVersion = "sharpinspect-camera-recovery-consumer-v1";
    private const string ScenarioVersion = "1";
    private const uint Seed = 0x1190_C0DE;
    private static readonly DateTimeOffset InitialUtc =
        new(2026, 1, 3, 4, 5, 6, TimeSpan.Zero);

    internal static int Run(ProductionStoreOptions options, string directory,
        string? userName, string? expectedPrincipal) => Execute(
        () => RunCore(options, Path.GetFullPath(directory), userName!, expectedPrincipal!,
            ReadPassword()),
        "V119-N01 camera-recovery-consumer PASS initialDisconnect=errorUnknown " +
        "finiteRecovery=true exhausted=20 restartAuth=true auditBeforeStart=true " +
        "alarmLatched=true ready=false leasesReturned=true productionReady=false");

    internal static int Query(ProductionStoreOptions options, string directory,
        string? userName, string? expectedPrincipal) => Execute(
        () => QueryCore(options, Path.GetFullPath(directory), userName!, expectedPrincipal!,
            ReadPassword()),
        "V119-N02 camera-recovery-restart PASS typedStartAudited=true openedDevices=0 " +
        "alarmLatched=true databaseReadOnly=true ready=false productionReady=false");

    private static int Execute(Func<Task> operation, string marker)
    {
        try
        {
            operation().WaitAsync(TimeSpan.FromSeconds(120)).GetAwaiter().GetResult();
            Console.WriteLine(marker);
            return 0;
        }
        catch (CameraRecoveryCheckException exception)
        {
            Console.Error.WriteLine("V119 camera-recovery FAIL reason=" + exception.ReasonCode);
            return 1;
        }
        catch
        {
            Console.Error.WriteLine("V119 camera-recovery FAIL reason=CameraRecoveryConsumerCheckFailed");
            return 1;
        }
    }

    private static async Task RunCore(ProductionStoreOptions options, string directory,
        string userName, string expectedPrincipal, string password)
    {
        if (options.CameraRecovery is null || options.CameraSetup is null ||
            options.LocalIdentity is null || options.AuditIntegrityPolicy is null ||
            options.AlarmPolicy is null)
            throw new CameraRecoveryCheckException("CameraRecoveryStoreOptionsRequired");
        if (!File.Exists(options.DatabasePath))
            throw new CameraRecoveryCheckException("CameraRecoveryIdentityStoreRequired");

        Directory.CreateDirectory(directory);
        using var clock = new VirtualCameraClock(InitialUtc);
        var scenario = CreateScenario();
        var provider = new VirtualCameraProvider(new[] { scenario }, clock, poolCapacity: 2);
        CameraRecoveryService? recovery = null;
        ServiceProvider? container = null;
        try
        {
            var requested = RequestedConfiguration();
            var target = new CameraBindingTarget(provider.Identity, Device);
            var initial = await OpenInitialAcquisitionAsync(provider, requested, clock)
                .ConfigureAwait(false);
            recovery = new CameraRecoveryService(provider, target, Role, requested,
                initial, clock, new CameraRecoveryOptions());

            container = BuildServices(options, recovery);
            var runtime = container.GetRequiredService<IStationRuntime>();
            var sessions = container.GetRequiredService<IInteractiveSessionService>();
            var stepUp = container.GetRequiredService<IStepUpAuthentication>();
            var trace = container.GetRequiredService<ICommandTraceQuery>();
            var alarmHistory = container.GetRequiredService<IAlarmHistoryQuery>();

            await WaitForRuntimeAsync(runtime, state =>
                state.AuditIntegrity?.State == AuditIntegrityState.Verified &&
                state.AlarmState is { Available: true }).ConfigureAwait(false);
            await SignInAsync(sessions, runtime, userName, expectedPrincipal, password)
                .ConfigureAwait(false);
            await WaitForRecoveryHealthyAsync(recovery).ConfigureAwait(false);

            var evidence = new RecoveryEvidenceCollector(scenario, provider.Identity,
                clock.GetTimePoint());

            var firstDisconnect = await AcquireAndAdvanceAsync(recovery, clock)
                .ConfigureAwait(false);
            evidence.AddAttempt("InitialDisconnect", firstDisconnect, expectFrame: false);
            Require(firstDisconnect.Accepted && firstDisconnect.Outcome is
                { Succeeded: false, FailureKind: CameraAcquisitionFailureKind.Disconnected,
                  ExecutionStatus: ExecutionStatus.Error, Decision: InspectionDecision.Unknown },
                "InitialDisconnectNotErrorUnknown");

            await recovery.RefreshAsync().ConfigureAwait(false);
            await DriveCycleAsync(recovery, clock, expectedState: CameraRecoveryState.Healthy,
                evidence, "AutomaticRecovery").ConfigureAwait(false);
            var firstRecovered = await AcquireAndAdvanceAsync(recovery, clock)
                .ConfigureAwait(false);
            evidence.AddAttempt("RecoveredQualification", firstRecovered, expectFrame: true);
            var firstFrameHash = ConsumeFrame(firstRecovered, Role);
            evidence.SetFrameHash("RecoveredQualification", firstFrameHash);
            Require(firstFrameHash is not null, "FirstRecoveryFrameMissing");
            // A successful lease return is synchronous at the public boundary;
            // the acquisition owner's final physical-drain continuation is
            // deliberately asynchronous. Let that bounded ownership barrier
            // settle before admitting the next request.
            await Task.Delay(100).ConfigureAwait(false);

            var secondDisconnect = await AcquireAndAdvanceAsync(recovery, clock)
                .ConfigureAwait(false);
            evidence.AddAttempt("ExhaustionDisconnect", secondDisconnect, expectFrame: false);
            Require(secondDisconnect.Accepted && secondDisconnect.Outcome is
                { Succeeded: false, FailureKind: CameraAcquisitionFailureKind.Disconnected,
                  ExecutionStatus: ExecutionStatus.Error, Decision: InspectionDecision.Unknown },
                "ExhaustionDisconnectNotErrorUnknown");

            await recovery.RefreshAsync().ConfigureAwait(false);
            await DriveCycleAsync(recovery, clock, expectedState: CameraRecoveryState.Exhausted,
                evidence, "ExhaustionCycle").ConfigureAwait(false);
            var exhausted = recovery.GetSnapshot();
            Require(exhausted.State == CameraRecoveryState.Exhausted &&
                exhausted.AttemptCount == exhausted.MaximumAttempts &&
                exhausted.CycleId is not null, "RecoveryDidNotExhaustAtTwenty");
            var exhaustedCycleId = exhausted.CycleId ?? throw new CameraRecoveryCheckException(
                "RecoveryCycleIdMissing");
            var attemptsAtExhaustion = provider.GetDiagnostics().Devices.Single().OpenCount;

            // A healthy device appearing after exhaustion cannot cause a new open.
            // The next success is intentionally held as the explicit restart's open.
            await Task.Delay(10).ConfigureAwait(false);
            Require(provider.GetDiagnostics().Devices.Single().OpenCount == attemptsAtExhaustion,
                "ExhaustedRecoveryRetriedWithoutCommand");

            var anonymousCorrelation = Guid.NewGuid();
            var anonymous = await runtime.SubmitAsync(new StartCameraRecoveryCycleCommand(
                anonymousCorrelation, new(CommandSource.PhysicalConsole), Role,
                exhaustedCycleId, "AnonymousRecoveryRestart")).ConfigureAwait(false);
            Require(anonymous.Disposition == CommandDisposition.Rejected &&
                anonymous.Audit == AuditPersistence.Persisted &&
                provider.GetDiagnostics().Devices.Single().OpenCount == attemptsAtExhaustion,
                "AnonymousRecoveryRestartAccepted");

            var invocation = CurrentInvocation(sessions);
            var missingGrantCorrelation = Guid.NewGuid();
            var missingGrant = await runtime.SubmitAsync(new StartCameraRecoveryCycleCommand(
                missingGrantCorrelation, invocation, Role, exhaustedCycleId,
                "MissingStepUpRecoveryRestart")).ConfigureAwait(false);
            Require(missingGrant.Disposition == CommandDisposition.Rejected &&
                missingGrant.Audit == AuditPersistence.Persisted &&
                provider.GetDiagnostics().Devices.Single().OpenCount == attemptsAtExhaustion,
                "MissingStepUpRecoveryRestartAccepted_" + missingGrant.Disposition +
                "_" + missingGrant.ReasonCode + "_" + missingGrant.Audit);

            var authorizedCorrelation = Guid.NewGuid();
            var stepUpResult = await stepUp.ReauthenticateAsync(new StepUpRequest(
                Guid.NewGuid(), invocation,
                new StepUpBinding(Permission.ManageCameraBindings, authorizedCorrelation,
                    Role, AuditedCommandKind.StartCameraRecoveryCycle), password))
                .ConfigureAwait(false);
            Require(stepUpResult.Succeeded && stepUpResult.GrantId is not null,
                "RecoveryRestartStepUpFailed");
            var stepUpGrantId = stepUpResult.GrantId ?? throw new CameraRecoveryCheckException(
                "RecoveryRestartStepUpGrantMissing");
            var authorized = await runtime.SubmitAsync(new StartCameraRecoveryCycleCommand(
                authorizedCorrelation, invocation with { StepUpGrantId = stepUpGrantId },
                Role, exhaustedCycleId, "AuthorizedRecoveryRestart")).ConfigureAwait(false);
            Require(authorized.Disposition == CommandDisposition.Accepted &&
                authorized.Audit == AuditPersistence.Persisted,
                "AuthorizedRecoveryRestartRejected_" + authorized.Disposition +
                "_" + authorized.ReasonCode + "_" + authorized.Audit);

            // SubmitAsync returns only after the authorization and terminal facts have
            // committed. The virtual clock has not advanced, so no physical open can
            // precede the durable command fact.
            var beforePhysical = provider.GetDiagnostics().Devices.Single().OpenCount;
            var commandFacts = await trace.QueryAsync(new(CorrelationId: authorizedCorrelation,
                PageSize: 16)).ConfigureAwait(false);
            Require(commandFacts.Records.Any(item => item.CommandKind ==
                    AuditedCommandKind.StartCameraRecoveryCycle &&
                    item.Phase == CommandAuditPhase.Outcome &&
                    item.Disposition == CommandDisposition.Accepted) &&
                commandFacts.Records.Any(item => item.CommandKind ==
                    AuditedCommandKind.StartCameraRecoveryCycle &&
                    item.Phase == CommandAuditPhase.Completed),
                "RecoveryRestartAuditMissingBeforePhysicalStart");
            Require(provider.GetDiagnostics().Devices.Single().OpenCount == beforePhysical,
                "RecoveryRestartOpenedBeforeAudit");
            evidence.AuthorizedRestart = new AuthorizedRestartEvidence(authorizedCorrelation,
                stepUpGrantId, exhaustedCycleId, beforePhysical,
                commandFacts.ThroughPosition);

            await DriveCycleAsync(recovery, clock, expectedState: CameraRecoveryState.Healthy,
                evidence, "AuthorizedRecovery").ConfigureAwait(false);
            var restarted = await AcquireAndAdvanceAsync(recovery, clock)
                .ConfigureAwait(false);
            evidence.AddAttempt("RestartQualification", restarted, expectFrame: true);
            var secondFrameHash = ConsumeFrame(restarted, Role);
            evidence.SetFrameHash("RestartQualification", secondFrameHash);
            Require(secondFrameHash is not null && secondFrameHash != firstFrameHash,
                "RecoveryFramesNotUnique");

            var final = await WaitForRuntimeAsync(runtime, state =>
                state.CameraRecovery is { State: CameraRecoveryState.Healthy,
                    SourceHealthy: true } &&
                state.AlarmState?.Instances.Any(item =>
                    item.Code == "CameraRecoveryFailed" && item.IsLatched &&
                    item.SourceHealthy && item.Lifecycle == AlarmLifecycle.RecoveredLatched &&
                    !item.Acknowledged) == true).ConfigureAwait(false);
            Require(!final.Ready && final.ArmState == ProductionArmState.Disarmed,
                "CameraRecoveryGrantedProductionReady");
            var failureAlarmHistory = await alarmHistory.QueryAsync(
                new AlarmHistoryFilter(code: "CameraRecoveryFailed", pageSize: 64))
                .ConfigureAwait(false);
            Require(failureAlarmHistory.Available && failureAlarmHistory.Records.Any(record =>
                record.Instance is { IsLatched: true, SourceHealthy: true,
                    Lifecycle: AlarmLifecycle.RecoveredLatched, Acknowledged: false }),
                "CameraRecoveryFailedAlarmHistoryMissing");

            evidence.FinalSnapshot = final.CameraRecovery;
            evidence.ObserveCycle(final.CameraRecovery!, "Final");
            await container.DisposeAsync().ConfigureAwait(false);
            container = null;
            evidence.Diagnostics = provider.GetDiagnostics();
            evidence.Events = ReadEvents(recovery).ToList();
            evidence.AddEventCycles();
            Require(evidence.Events.Count <= 64, "RecoveryEventReadCapacityExceeded");
            Require(evidence.Diagnostics.InfrastructureFailures == 0 &&
                evidence.Diagnostics.Devices.All(item => item.OutstandingLeases == 0 &&
                    !item.IsOpen), "RecoveryProviderLeaseOrDeviceLeak");
            Require(evidence.Diagnostics.Devices.Single().StableDeviceIdentity == Device &&
                evidence.Diagnostics.Devices.Single().OpenCount == 4 &&
                evidence.Diagnostics.Devices.Single().OpeningCursor == 25 &&
                evidence.Diagnostics.Devices.Single().ConfigurationCursor == 4 &&
                evidence.Diagnostics.Devices.Single().AcquisitionCursor == 4,
                "RecoveryProviderIdentityOrCursorMismatch");

            await WriteEvidenceAsync(directory, evidence).ConfigureAwait(false);
        }
        finally
        {
            if (container is not null)
                await container.DisposeAsync().ConfigureAwait(false);
            else if (recovery is not null)
                await recovery.DisposeAsync().ConfigureAwait(false);
            else
                await provider.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task QueryCore(ProductionStoreOptions options, string directory,
        string userName, string expectedPrincipal, string _password)
    {
        if (options.CameraRecovery is null || options.CameraSetup is null ||
            options.LocalIdentity is null || options.AuditIntegrityPolicy is null ||
            options.AlarmPolicy is null)
            throw new CameraRecoveryCheckException("CameraRecoveryStoreOptionsRequired");
        var evidencePath = Path.Combine(directory, "camera-recovery-evidence.json");
        if (!File.Exists(evidencePath))
            throw new CameraRecoveryCheckException("CameraRecoveryEvidenceMissing");

        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(evidencePath)
            .ConfigureAwait(false));
        Require(evidence.RootElement.GetProperty("result").GetString() == "Pass",
            "CameraRecoveryEvidenceNotPassed");
        Require(evidence.RootElement.GetProperty("ready").GetBoolean() == false,
            "CameraRecoveryEvidenceReady");

        var services = new ServiceCollection();
        // The restart proof intentionally registers no provider or recovery engine.
        // It only reopens the signed store and reads persisted facts.
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(20));
        await using var container = services.BuildServiceProvider();
        var trace = container.GetRequiredService<ICommandTraceQuery>();
        var alarms = container.GetRequiredService<IAlarmHistoryQuery>();

        var correlation = Guid.Parse(evidence.RootElement.GetProperty("authorizedRestart")
            .GetProperty("correlationId").GetString()!);
        var facts = await trace.QueryAsync(new(CorrelationId: correlation,
            PageSize: 16)).ConfigureAwait(false);
        Require(facts.Records.Any(item => item.CommandKind ==
                AuditedCommandKind.StartCameraRecoveryCycle &&
                item.Phase == CommandAuditPhase.Outcome &&
                item.Disposition == CommandDisposition.Accepted) &&
            facts.Records.Any(item => item.CommandKind == AuditedCommandKind.StartCameraRecoveryCycle &&
                item.Phase == CommandAuditPhase.Completed), "RecoveryRestartTraceMissing");
        var history = await alarms.QueryAsync(new AlarmHistoryFilter(
            code: "CameraRecoveryFailed", pageSize: 64)).ConfigureAwait(false);
        Require(history.Available && history.Records.Any(item => item.Instance is
            { IsLatched: true, SourceHealthy: true, Lifecycle: AlarmLifecycle.RecoveredLatched,
              Acknowledged: false }), "RecoveryRestartAlarmHistoryMissing");
        await File.WriteAllTextAsync(Path.Combine(directory, "camera-recovery-restart.json"),
            JsonSerializer.Serialize(new
            {
                Result = "Pass", TypedStartAudited = true, OpenedDevices = 0,
                DatabaseReadOnly = true, CameraRecoveryFailedLatched = true,
                ProductionReady = false, PhysicalHardwareQualification = "NotRun",
                StationAcceptance = "NotRun", Production = "NotRun",
                NativeCrashIsolation = "NotRun"
            }, JsonOptions())).ConfigureAwait(false);
    }

    private static ServiceProvider BuildServices(ProductionStoreOptions options,
        CameraRecoveryService recovery)
    {
        var services = new ServiceCollection();
        services.AddSharpInspectCameraRecovery(_ => recovery);
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(20));
        return services.BuildServiceProvider();
    }

    private static async Task<CameraAcquisitionService> OpenInitialAcquisitionAsync(
        VirtualCameraProvider provider, RequestedCameraConfiguration requested,
        VirtualCameraClock clock)
    {
        var discovery = await provider.DiscoverAsync().ConfigureAwait(false);
        Require(discovery.Succeeded && discovery.Devices.Count == 1 &&
            discovery.Devices[0].StableDeviceIdentity == Device,
            "RecoveryInitialDiscoveryMismatch");
        var opened = await provider.OpenAsync(Device).ConfigureAwait(false);
        Require(opened.Succeeded && opened.Device is IControlledCameraDevice,
            "RecoveryInitialOpenFailed");
        var device = (IControlledCameraDevice)opened.Device!;
        var configured = await device.ApplyConfigurationAsync(requested).ConfigureAwait(false);
        Require(configured.Succeeded && configured.Effective is not null,
            "RecoveryInitialConfigurationFailed");
        var started = await device.StartAsync().ConfigureAwait(false);
        Require(started.Succeeded, "RecoveryInitialStartFailed");
        return new CameraAcquisitionService(device, configured.Effective!, clock,
            new CameraAcquisitionOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 64));
    }

    private static async Task<CameraAcquisitionAttempt> AcquireAndAdvanceAsync(
        CameraRecoveryService recovery, VirtualCameraClock clock)
    {
        var observedPendingEvents = clock.PendingEventCount;
        var task = recovery.AcquireAsync(ExecutionKind.Qualification, Role).AsTask();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            var pendingEvents = clock.PendingEventCount;
            if (pendingEvents != observedPendingEvents)
            {
                // The acquisition service schedules its signal and deadline on
                // the worker thread. Observe a count change before advancing so
                // an unrelated stale schedule cannot consume this one millisecond
                // step before the request has installed its own events.
                observedPendingEvents = pendingEvents;
                clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
            }
            else
                await Task.Delay(1).ConfigureAwait(false);
        }
        Require(task.IsCompleted, "RecoveryAcquisitionNotScheduled");
        return await task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    private static async Task DriveCycleAsync(CameraRecoveryService recovery,
        VirtualCameraClock clock, CameraRecoveryState expectedState,
        RecoveryEvidenceCollector evidence, string label)
    {
        while (true)
        {
            var snapshot = recovery.GetSnapshot();
            if (snapshot.State == expectedState)
                return;
            if (snapshot.State == CameraRecoveryState.Exhausted &&
                expectedState != CameraRecoveryState.Exhausted)
                throw new CameraRecoveryCheckException(label + "UnexpectedExhaustion");
            if (snapshot.NextAttemptAt is { } next)
            {
                if (next.MonotonicTimestamp > clock.Timestamp)
                    clock.AdvanceTo(next.MonotonicTimestamp);
                else if (next.MonotonicTimestamp == clock.Timestamp && clock.PendingEventCount > 0)
                {
                    // A worker may register another first-attempt callback at the same
                    // timestamp after an earlier callback has already been driven.
                    clock.AdvanceBy(TimeSpan.Zero);
                }
            }
            // Let scheduled recovery and physical retirement continuations settle.
            await Task.Delay(1).ConfigureAwait(false);

            if (snapshot.AttemptCount > 0)
                evidence.ObserveCycle(snapshot, label);
            if (evidence.ElapsedWallTime > TimeSpan.FromSeconds(30))
            {
                var stalled = recovery.GetSnapshot();
                throw new CameraRecoveryCheckException(label + "DeadlineExceeded_" +
                    stalled.State + "_" + stalled.AttemptCount + "_" + stalled.ReasonCode +
                    "_" + clock.Timestamp + "_" + clock.PendingEventCount);
            }
        }
    }

    private static string? ConsumeFrame(CameraAcquisitionAttempt attempt, string role)
    {
        var outcome = attempt.Outcome;
        Require(outcome is { Succeeded: true, Lease: not null } &&
            outcome.ExecutionStatus == ExecutionStatus.Success &&
            outcome.Decision == InspectionDecision.Unknown &&
            outcome.LogicalCameraRole == role, "RecoveryFrameOutcomeInvalid");
        var lease = outcome!.TakeFrame();
        Require(lease is not null && !lease.IsReturned && lease.Frame.LogicalCameraRole == role &&
            lease.Frame.Correlation == outcome.Correlation &&
            lease.Provenance.Correlation == outcome.Correlation &&
            lease.Provenance.Milestones.NormalizedFrameReady is not null,
            "RecoveryFrameCorrelationInvalid");
        var ownedLease = lease!;
        var hash = HashRows(ownedLease.Frame);
        ownedLease.Dispose();
        Require(ownedLease.IsReturned, "RecoveryFrameLeaseNotReturned");
        outcome.Dispose();
        return hash;
    }

    private static string HashRows(VisionFrame frame)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var row = 0; row < frame.Height; row++) hash.AppendData(frame.GetRowSpan(row));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IReadOnlyList<RecoveryEventEvidence> ReadEvents(CameraRecoveryService recovery)
    {
        var page = recovery.ReadEvents(0, 64);
        return page.Events.Select(item => new RecoveryEventEvidence(item.Sequence,
            item.CycleId, item.AttemptNumber, item.Kind.ToString(), item.ReasonCode,
            item.Timestamp.HostObservedAtUtc, item.Timestamp.MonotonicTimestamp)).ToArray();
    }

    private static VirtualCameraScenario CreateScenario()
    {
        var first = VirtualCameraImage.CreateSynthetic("recovered-frame", 2, 2,
            VisionPixelFormat.Mono8, null, Seed + 1);
        var second = VirtualCameraImage.CreateSynthetic("restarted-frame", 2, 2,
            VisionPixelFormat.Mono8, null, Seed + 2);
        var acquisitions = new[]
        {
            new VirtualCameraAcquisitionPlan(new[] { new VirtualCameraSignal(
                TimeSpan.FromMilliseconds(1), VirtualCameraSignalKind.Disconnect) }),
            new VirtualCameraAcquisitionPlan(new[] { new VirtualCameraSignal(
                TimeSpan.FromMilliseconds(1), VirtualCameraSignalKind.Frame, first.Id) }),
            new VirtualCameraAcquisitionPlan(new[] { new VirtualCameraSignal(
                TimeSpan.FromMilliseconds(1), VirtualCameraSignalKind.Disconnect) }),
            new VirtualCameraAcquisitionPlan(new[] { new VirtualCameraSignal(
                TimeSpan.FromMilliseconds(1), VirtualCameraSignalKind.Frame, second.Id) })
        };
        var openings = new[] { VirtualCameraOpenOutcome.Success,
            VirtualCameraOpenOutcome.ConnectionFailure,
            VirtualCameraOpenOutcome.Success, VirtualCameraOpenOutcome.Success }
            .Concat(Enumerable.Repeat(VirtualCameraOpenOutcome.ConnectionFailure, 20))
            .Append(VirtualCameraOpenOutcome.Success);
        return new VirtualCameraScenario("V119-recovery", ScenarioVersion, Seed, Device,
            Capabilities(), new[] { first, second }, acquisitions,
            new[] { new VirtualCameraConfigurationPlan(
                VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero),
                new VirtualCameraConfigurationPlan(
                    VirtualCameraConfigurationOutcome.ReadBackFailure, TimeSpan.Zero),
                new VirtualCameraConfigurationPlan(
                    VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero),
                new VirtualCameraConfigurationPlan(
                    VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero) }, openings);
    }

    private static CameraCapabilities Capabilities() => new(
        new[] { ProductionAcquisitionMode.SoftwareTrigger },
        new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
        new CameraDoubleCapability(10, 100, 10, CameraQuantizationMode.Exact),
        new CameraDoubleCapability(-10, 10, 1, CameraQuantizationMode.Exact),
        new CameraDoubleCapability(0, 100, 5, CameraQuantizationMode.Exact),
        new CameraRoiCapabilities(2, 2, new(0, 0, 1), new(0, 0, 1),
            new(2, 2, 1), new(2, 2, 1)));

    private static RequestedCameraConfiguration RequestedConfiguration() => new(
        ProductionAcquisitionMode.SoftwareTrigger, 20, 0, new RegionOfInterest(0, 0, 2, 2),
        VisionPixelFormat.Mono8, null, 10, 0, null);

    private static async Task SignInAsync(IInteractiveSessionService sessions,
        IStationRuntime runtime, string userName, string expectedPrincipal, string password)
    {
        var result = await sessions.SignInAsync(new PasswordSignInRequest(userName, password))
            .ConfigureAwait(false);
        Require(result.Succeeded && result.Identity?.PrincipalId.ToString("D") == expectedPrincipal,
            "CameraRecoveryConsumerAuthenticationFailed");
        await WaitForRuntimeAsync(runtime, state =>
            state.Session.State == InteractiveSessionState.Authenticated &&
            state.Session.PrincipalId == expectedPrincipal).ConfigureAwait(false);
    }

    private static CommandInvocation CurrentInvocation(IInteractiveSessionService sessions)
    {
        var current = sessions.Current;
        Require(current is { State: InteractiveSessionState.Authenticated,
            PrincipalId: not null, SessionId: not null }, "RecoverySessionUnavailable");
        return new CommandInvocation(CommandSource.PhysicalConsole, current.PrincipalId,
            current.SessionId);
    }

    private static async Task<StationStateSnapshot> WaitForRuntimeAsync(
        IStationRuntime runtime, Func<StationStateSnapshot, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await runtime.GetSnapshotAsync().ConfigureAwait(false);
            if (predicate(snapshot)) return snapshot;
            await Task.Delay(10).ConfigureAwait(false);
        }
        throw new CameraRecoveryCheckException("CameraRecoveryRuntimeObservationTimeout");
    }

    private static async Task WaitForRecoveryHealthyAsync(CameraRecoveryService recovery)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await recovery.RefreshAsync().ConfigureAwait(false);
            var snapshot = recovery.GetSnapshot();
            if (snapshot.State == CameraRecoveryState.Healthy && snapshot.SourceHealthy)
                return;
            await Task.Delay(10).ConfigureAwait(false);
        }
        throw new CameraRecoveryCheckException("RecoveryInitialHealthObservationTimeout");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string reason)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(1).ConfigureAwait(false);
        }
        throw new CameraRecoveryCheckException(reason);
    }

    private static async Task WriteEvidenceAsync(string directory,
        RecoveryEvidenceCollector evidence)
    {
        var diagnostics = evidence.Diagnostics ?? throw new CameraRecoveryCheckException(
            "RecoveryDiagnosticsMissing");
        var consumer = typeof(CameraRecoveryDemo).Assembly.Location;
        Require(File.Exists(consumer), "CameraRecoveryConsumerAssemblyUnavailable");
        var document = new
        {
            Result = "Pass", ContractVersion = EvidenceVersion,
            ConsumerSha256 = Convert.ToHexString(SHA256.HashData(
                await File.ReadAllBytesAsync(consumer).ConfigureAwait(false))),
            Provider = evidence.Provider, LogicalRole = Role, StableDeviceIdentity = Device,
            InitialUtc = evidence.InitialUtc, RecoveryEpoch = evidence.RecoveryEpoch,
            Ready = false, InitialDisconnectErrorUnknown = true,
            SameIdentity = true, FailedOpenObserved = true, ReadBackFailureObserved = true,
            NewUniqueQualificationFrame = true, ExhaustionMaximumAttempts = 20,
            AppearanceAfterExhaustionDidNotRetry = true, AnonymousRestartRejected = true,
            MissingStepUpRestartRejected = true, AuthorizedRestart = evidence.AuthorizedRestart,
            AuditBeforePhysicalStart = true, CameraRecoveryFailedLatched = true,
            FinalSnapshot = evidence.FinalSnapshot,
            Attempts = evidence.Attempts, Events = evidence.Events,
            Diagnostics = new { diagnostics.IsDisposed, diagnostics.InfrastructureFailures,
                Devices = diagnostics.Devices },
            PhysicalHardwareQualification = "NotRun", StationAcceptance = "NotRun",
            Production = "NotRun", NativeCrashIsolation = "NotRun"
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "camera-recovery-evidence.json"),
            JsonSerializer.Serialize(document, JsonOptions())).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"),
            JsonSerializer.Serialize(new
            {
                Result = "Pass", RuntimeComponent = "Pass", ConsumerSha256 =
                    document.ConsumerSha256, RecoveryEpoch = evidence.RecoveryEpoch,
                CycleIds = evidence.CycleIds,
                RecoveryAttempts = evidence.Events.Count(item => item.Kind == "AttemptStarted"),
                AcquisitionAttempts = evidence.Attempts.Count,
                MaximumAttempts = 20, Ready = false, AlarmLatched = true,
                LeasesReturned = true, OutstandingLeases = 0,
                PhysicalHardwareQualification = "NotRun", StationAcceptance = "NotRun",
                Production = "NotRun", NativeCrashIsolation = "NotRun"
            }, JsonOptions())).ConfigureAwait(false);
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

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
            hash.AppendData(SHA256.HashData(await File.ReadAllBytesAsync(candidate)
                .ConfigureAwait(false)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ReadPassword() => JsonSerializer.Deserialize<string>(
        Console.ReadLine() ?? "null") ?? throw new CameraRecoveryCheckException(
        "CameraRecoveryPasswordRequired");

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new CameraRecoveryCheckException(reason);
    }

    private sealed class CameraRecoveryCheckException : Exception
    {
        internal CameraRecoveryCheckException(string reasonCode) => ReasonCode = reasonCode;
        internal string ReasonCode { get; }
    }

    private sealed class RecoveryEvidenceCollector
    {
        private readonly Stopwatch _wallClock = Stopwatch.StartNew();
        internal RecoveryEvidenceCollector(VirtualCameraScenario scenario,
            CameraProviderIdentity provider, FrameTimePoint initial)
        {
            Scenario = scenario;
            Provider = provider;
            InitialUtc = initial.HostObservedAtUtc;
            RecoveryEpoch = Guid.Empty;
        }

        internal VirtualCameraScenario Scenario { get; }
        internal CameraProviderIdentity Provider { get; }
        internal DateTimeOffset InitialUtc { get; }
        internal Guid RecoveryEpoch { get; private set; }
        internal List<AttemptEvidence> Attempts { get; } = new();
        internal List<RecoveryEventEvidence> Events { get; set; } = new();
        internal List<Guid> CycleIds { get; } = new();
        internal AuthorizedRestartEvidence? AuthorizedRestart { get; set; }
        internal CameraRecoverySnapshot? FinalSnapshot { get; set; }
        internal VirtualCameraProviderDiagnostics? Diagnostics { get; set; }
        internal TimeSpan ElapsedWallTime => _wallClock.Elapsed;

        internal void AddAttempt(string caseId, CameraAcquisitionAttempt attempt, bool expectFrame)
        {
            var outcome = attempt.Outcome ?? throw new CameraRecoveryCheckException(
                caseId + "OutcomeMissing_" + attempt.ReasonCode);
            Attempts.Add(new AttemptEvidence(caseId, attempt.Accepted,
                attempt.Correlation?.Value, outcome.Succeeded, outcome.ReasonCode,
                outcome.FailureKind?.ToString(), outcome.ExecutionStatus.ToString(),
                outcome.Decision.ToString(), outcome.Start?.BusyAt.MonotonicTimestamp,
                outcome.Start?.BusyAt.HostObservedAtUtc,
                expectFrame ? outcome.Lease is not null : outcome.Lease is null));
        }

        internal void SetFrameHash(string caseId, string? hash)
        {
            Require(hash is not null, caseId + "FrameHashMissing");
            var index = Attempts.FindIndex(item => item.CaseId == caseId);
            Require(index >= 0, caseId + "AttemptEvidenceMissing");
            Attempts[index] = Attempts[index] with { FrameHash = hash };
        }

        internal void ObserveCycle(CameraRecoverySnapshot snapshot, string label)
        {
            RecoveryEpoch = snapshot.RecoveryEpoch;
            if (snapshot.CycleId is { } cycle && !CycleIds.Contains(cycle)) CycleIds.Add(cycle);
        }

        internal void AddEventCycles()
        {
            foreach (var cycle in Events.Select(item => item.CycleId).Distinct())
                if (!CycleIds.Contains(cycle)) CycleIds.Add(cycle);
        }
    }

    private sealed record AttemptEvidence(string CaseId, bool Accepted, Guid? CorrelationId,
        bool Succeeded, string ReasonCode, string? FailureKind, string ExecutionStatus,
        string Decision, long? BusyTimestamp, DateTimeOffset? BusyUtc, bool FrameExpected,
        string? FrameHash = null);

    private sealed record RecoveryEventEvidence(long Sequence, Guid CycleId, int AttemptNumber,
        string Kind, string ReasonCode, DateTimeOffset HostObservedAtUtc,
        long MonotonicTimestamp);

    private sealed record AuthorizedRestartEvidence(Guid CorrelationId, Guid StepUpGrantId,
        Guid ExpectedCycleId, int OpenCountBeforePhysicalStart, long AuditThroughPosition);
}
