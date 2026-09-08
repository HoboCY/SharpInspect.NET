using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.SampleHost;

/// <summary>
/// Independent consumer for the schema 12 camera-network maintenance seam.
/// Every provider call is a bounded virtual observation; no operating-system
/// network API is used by this sample.
/// </summary>
internal static class CameraNetworkDemo
{
    private const string ConsumerContract = "sharpinspect-camera-network-consumer-v1";
    private const string SuccessDevice = "Virtual:Network-Success";
    private const string ConflictDevice = "Virtual:Network-Conflict";
    private const string FailureDevice = "Virtual:Network-Failure";
    private const string WrongIdentityDevice = "Virtual:Network-WrongIdentity";
    private const string UnsupportedDevice = "Virtual:Network-Unsupported";
    private const string InterfaceId = "virtual-network";
    private const uint Seed = 0x1200_C0DE;
    private static readonly DateTimeOffset FixtureUtc =
        new(2026, 1, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly CameraIpv4Configuration StationConfiguration =
        new("192.168.10.1", 24);
    private static readonly CameraIpv4Configuration SuccessPrevious =
        new("192.168.10.2", 24);
    private static readonly CameraIpv4Configuration SuccessRequested =
        new("192.168.10.42", 24);

    internal static int Run(ProductionStoreOptions options, string directory, string? userName,
        string? expectedPrincipal) => Execute(
        () => RunCore(options, Path.GetFullPath(directory), userName!, expectedPrincipal!, ReadPassword()),
        "V120-N01 camera-network-consumer PASS stepUp=true previousRequestedAudited=true sameIdentity=true ready=false requiresRecipeActivation=true");

    internal static int Query(ProductionStoreOptions options, string directory, string? userName,
        string? expectedPrincipal) => Execute(
        () => QueryCore(options, Path.GetFullPath(directory)),
        "V120-N02 camera-network-restart PASS typedChangeAudited=true openedDevices=0 registeredProviders=0 databaseReadOnly=true");

    private static int Execute(Func<Task> operation, string marker)
    {
        try
        {
            operation().GetAwaiter().GetResult();
            Console.WriteLine(marker);
            return 0;
        }
        catch (CameraNetworkCheckException exception)
        {
            Console.Error.WriteLine("V120 camera-network FAIL reason=" + exception.ReasonCode);
            return 1;
        }
        catch
        {
            Console.Error.WriteLine("V120 camera-network FAIL reason=CameraNetworkConsumerCheckFailed");
            return 1;
        }
    }

    private static string ReadPassword() => JsonSerializer.Deserialize<string>(
        Console.ReadLine() ?? "null") ??
        throw new CameraNetworkCheckException("CameraNetworkConsumerPasswordRequired");

    private static async Task RunCore(ProductionStoreOptions options, string directory,
        string userName, string expectedPrincipal, string password)
    {
        Require(options.CameraNetwork is not null && options.CameraSetup is not null &&
            options.CameraRecovery is not null && options.LocalIdentity is not null &&
            options.AuditIntegrityPolicy is not null && options.AlarmPolicy is not null,
            "CameraNetworkStoreOptionsRequired");
        Require(File.Exists(options.DatabasePath), "CameraNetworkIdentityStoreRequired");
        Directory.CreateDirectory(directory);

        using var clock = new VirtualCameraClock(FixtureUtc);
        await using var networkProvider = CreateNetworkProvider(clock);
        await using var unsupportedProvider = CreateUnsupportedProvider(clock);
        await using var container = Services(options, networkProvider, unsupportedProvider)
            .BuildServiceProvider();
        var runtime = container.GetRequiredService<IStationRuntime>();
        var maintenance = container.GetRequiredService<ICameraNetworkMaintenanceRuntime>();
        var sessions = container.GetRequiredService<IInteractiveSessionService>();
        var stepUp = container.GetRequiredService<IStepUpAuthentication>();
        var trace = container.GetRequiredService<ICommandTraceQuery>();
        networkProvider.AttachTrace(trace);

        await Verified(runtime);
        await StartupAlarmReady(runtime);
        var startup = await runtime.GetSnapshotAsync();
        var startupQualificationOnly = !startup.Ready &&
            startup.ArmState == ProductionArmState.Disarmed &&
            startup.ActiveRecipe is null && startup.CameraSetup is null &&
            startup.Handshake == HandshakePhase.Unknown &&
            startup.Recovery == RecoveryState.Required &&
            startup.Evidence.PendingDeliveries == 0 &&
            startup.CurrentExecution is null &&
            startup.Plc.Connection == HealthState.Unconfigured;
        Require(startupQualificationOnly,
            "CameraNetworkStartupGrantedProductionAuthority");

        var target = new CameraBindingTarget(networkProvider.Identity, SuccessDevice);
        var unsupportedTarget = new CameraBindingTarget(unsupportedProvider.Identity, UnsupportedDevice);
        var anonymousOperation = Guid.NewGuid();
        var anonymousBefore = await runtime.GetSnapshotAsync();
        var anonymous = await maintenance.ChangeNetworkConfigurationAsync(new(
            anonymousOperation, new(CommandSource.PhysicalConsole), target, SuccessRequested,
            "匿名请求必须拒绝"));
        await Verified(runtime);
        var anonymousAfter = await runtime.GetSnapshotAsync();
        var anonymousAuthorityUnchanged = AuthorityUnchanged(anonymousBefore, anonymousAfter);
        ReportActual("anonymous-maintenance", anonymous.Succeeded, anonymous.ReasonCode,
            anonymous.Audit, anonymousAuthorityUnchanged);
        ReportAuthorityActual("anonymous-maintenance", anonymousBefore, anonymousAfter);
        Require(!anonymous.Succeeded, "CameraNetworkAnonymousMutationAccepted");
        Require(anonymous.Audit == AuditPersistence.Persisted,
            "CameraNetworkAnonymousAuditUnavailable");
        Require(anonymousAuthorityUnchanged, "CameraNetworkAnonymousProjectedAuthority");
        var anonymousRejectedWithoutProjection = !anonymous.Succeeded &&
            anonymous.Audit == AuditPersistence.Persisted && anonymousAuthorityUnchanged;

        await SignIn(sessions, runtime, userName, expectedPrincipal, password);
        var invocation = Invocation(sessions);
        var missingStepUpBefore = await runtime.GetSnapshotAsync();
        var missingStepUp = await maintenance.ChangeNetworkConfigurationAsync(new(
            Guid.NewGuid(), invocation, target, SuccessRequested, "缺少Step-Up必须拒绝"));
        await Verified(runtime);
        var missingStepUpAfter = await runtime.GetSnapshotAsync();
        var missingStepUpAuthorityUnchanged = AuthorityUnchanged(missingStepUpBefore, missingStepUpAfter);
        ReportActual("missing-step-up-maintenance", missingStepUp.Succeeded,
            missingStepUp.ReasonCode, missingStepUp.Audit, missingStepUpAuthorityUnchanged);
        ReportAuthorityActual("missing-step-up-maintenance", missingStepUpBefore,
            missingStepUpAfter);
        Require(!missingStepUp.Succeeded, "CameraNetworkMissingStepUpAccepted");
        Require(missingStepUp.Audit == AuditPersistence.Persisted,
            "CameraNetworkMissingStepUpAuditUnavailable");
        Require(missingStepUpAuthorityUnchanged, "CameraNetworkMissingStepUpProjectedAuthority");
        var missingStepUpRejectedWithoutProjection = !missingStepUp.Succeeded &&
            missingStepUp.Audit == AuditPersistence.Persisted && missingStepUpAuthorityUnchanged;

        // Capture the authority baseline only after Step-Up has committed and
        // the runtime has observed a fully Verified audit state. This keeps the
        // comparison scoped to the unsupported maintenance command itself.
        var unsupported = await SubmitAuthorizedWithSnapshotAsync(maintenance, stepUp,
            sessions, runtime, unsupportedTarget, SuccessRequested, password,
            "不支持网络扩展必须拒绝");
        var unsupportedAuthorityUnchanged = AuthorityUnchanged(unsupported.Before,
            unsupported.After);
        ReportActual("unsupported-provider-maintenance", unsupported.Result.Succeeded,
            unsupported.Result.ReasonCode, unsupported.Result.Audit, unsupportedAuthorityUnchanged);
        ReportAuthorityActual("unsupported-provider-maintenance", unsupported.Before,
            unsupported.After);
        Require(!unsupported.Result.Succeeded, "CameraNetworkUnsupportedProviderAccepted");
        Require(unsupported.Result.Audit == AuditPersistence.Persisted,
            "CameraNetworkUnsupportedProviderAuditUnavailable");
        Require(unsupported.Result.ReasonCode == "CameraNetworkMaintenanceUnsupported",
            "CameraNetworkUnsupportedProviderReasonInvalid");
        Require(unsupportedAuthorityUnchanged, "CameraNetworkUnsupportedProviderProjectedAuthority");
        var unsupportedRejectedWithoutProjection = !unsupported.Result.Succeeded &&
            unsupported.Result.Audit == AuditPersistence.Persisted &&
            unsupported.Result.ReasonCode == "CameraNetworkMaintenanceUnsupported" &&
            unsupportedAuthorityUnchanged;

        var busyLease = await networkProvider.TryBeginMaintenanceAsync(FailureDevice);
        Require(busyLease.Succeeded && busyLease.Session is not null,
            "CameraNetworkBusyFixtureLeaseUnavailable");
        var busyTarget = new CameraBindingTarget(networkProvider.Identity, FailureDevice);
        var busy = await SubmitAuthorizedAsync(maintenance, stepUp, sessions, busyTarget,
            new CameraIpv4Configuration("192.168.10.45", 24), password,
            "已有维护租约必须拒绝", networkProvider);
        Require(!busy.Result.Succeeded && busy.Result.Audit == AuditPersistence.Persisted &&
            busy.Result.ReasonCode == "VirtualNetworkMaintenanceLeaseActive",
            "CameraNetworkBusyMutationAccepted");
        await Verified(runtime);
        await busyLease.Session!.DisposeAsync();

        var success = await SubmitAuthorizedAsync(maintenance, stepUp, sessions, target,
            SuccessRequested, password, "开发验收修改相机网络地址", networkProvider);
        Require(success.Result.Succeeded && success.Result.Audit == AuditPersistence.Persisted &&
            success.Result.Snapshot is { State: CameraNetworkMaintenanceState.Succeeded } snapshot &&
            snapshot.Previous == SuccessPrevious && snapshot.Requested == SuccessRequested &&
            snapshot.Observed == SuccessRequested && snapshot.IdentityVerified &&
            snapshot.RequiresRecipeActivation && !snapshot.ProductionReady,
            "CameraNetworkSuccessfulChangeEvidenceInvalid");
        var successSnapshot = success.Result.Snapshot!;
        var successAuditBeforePhysicalChange = networkProvider.AuditObservedBeforePhysicalChange;
        Require(successAuditBeforePhysicalChange, "CameraNetworkAuditOrderNotObserved");
        var queried = await maintenance.GetNetworkMaintenanceAsync(target, Invocation(sessions));
        Require(queried.Available && queried.Snapshot is { } queriedSnapshot &&
            queriedSnapshot.OperationId == successSnapshot.OperationId &&
            queriedSnapshot.Target == successSnapshot.Target &&
            queriedSnapshot.State == successSnapshot.State &&
            queriedSnapshot.Previous == SuccessPrevious &&
            queriedSnapshot.Requested == SuccessRequested &&
            queriedSnapshot.Observed == SuccessRequested && queriedSnapshot.IdentityVerified,
            "CameraNetworkSameIdentityRediscoveryUnavailable");

        var successTrace = await trace.QueryAsync(new CommandTraceFilter(
            CorrelationId: success.OperationId, PageSize: 16));
        Require(successTrace.Records.Count >= 2 &&
            successTrace.Records.Any(item => item.CommandKind == AuditedCommandKind.ChangeCameraNetworkConfiguration &&
                item.Phase == CommandAuditPhase.Outcome &&
                item.Disposition == CommandDisposition.Accepted) &&
            successTrace.Records.Any(item => item.CommandKind == AuditedCommandKind.ChangeCameraNetworkConfiguration &&
                item.Phase == CommandAuditPhase.Completed),
            "CameraNetworkTypedCommandTraceMissing");

        var successDevice = networkProvider.GetNetworkDiagnostics().Devices.Single(
            item => item.StableDeviceIdentity == SuccessDevice);
        Require(successDevice.CurrentConfiguration == SuccessRequested &&
            !successDevice.IsStale && successDevice.BeginCount == 1 &&
            successDevice.SuccessfulBeginCount == 1 && successDevice.ApplyCount == 1 &&
            successDevice.SuccessfulApplyCount == 1 && successDevice.OutstandingLeases == 0 &&
            successDevice.ActiveOperations == 0,
            "CameraNetworkSuccessProviderDiagnosticsInvalid");
        await Verified(runtime);

        var failureTarget = new CameraBindingTarget(networkProvider.Identity, FailureDevice);
        var failureRequested = new CameraIpv4Configuration("192.168.10.45", 24);
        var failure = await SubmitAuthorizedAsync(maintenance, stepUp, sessions, failureTarget,
            failureRequested, password, "验证写入后失败保留观测", networkProvider);
        Require(!failure.Result.Succeeded && failure.Result.Audit == AuditPersistence.Persisted &&
            failure.Result.ReasonCode == "CameraNetworkApplyFailed" &&
            failure.Result.Snapshot is { State: CameraNetworkMaintenanceState.Failed } failureSnapshot &&
            failureSnapshot.Previous == new CameraIpv4Configuration("192.168.10.4", 24) &&
            failureSnapshot.Requested == failureRequested &&
            failureSnapshot.Observed == failureRequested && failureSnapshot.IdentityVerified,
            "CameraNetworkApplyFailureEvidenceInvalid");
        var failureDevice = networkProvider.GetNetworkDiagnostics().Devices.Single(
            item => item.StableDeviceIdentity == FailureDevice);
        Require(failureDevice.CurrentConfiguration == failureRequested &&
            failureDevice.FailedApplyCount == 1 && failureDevice.OutstandingLeases == 0 &&
            failureDevice.ActiveOperations == 0,
            "CameraNetworkApplyFailureProviderDiagnosticsInvalid");
        await Verified(runtime);

        var conflictTarget = new CameraBindingTarget(networkProvider.Identity, ConflictDevice);
        var conflict = await SubmitAuthorizedAsync(maintenance, stepUp, sessions, conflictTarget,
            SuccessRequested, password, "验证地址冲突拒绝", networkProvider);
        Require(!conflict.Result.Succeeded && conflict.Result.Audit == AuditPersistence.Persisted &&
            conflict.Result.ReasonCode == "CameraNetworkAddressConflict" &&
            conflict.Result.Snapshot is { State: CameraNetworkMaintenanceState.Failed } conflictSnapshot &&
            conflictSnapshot.Previous == new CameraIpv4Configuration("192.168.10.5", 24) &&
            conflictSnapshot.Requested == SuccessRequested &&
            conflictSnapshot.Observed == conflictSnapshot.Previous && !conflictSnapshot.IdentityVerified,
            "CameraNetworkConflictEvidenceInvalid");
        var conflictDevice = networkProvider.GetNetworkDiagnostics().Devices.Single(
            item => item.StableDeviceIdentity == ConflictDevice);
        Require(conflictDevice.ApplyCount == 0 && conflictDevice.OutstandingLeases == 0 &&
            conflictDevice.ActiveOperations == 0,
            "CameraNetworkConflictChangedDevice");
        await Verified(runtime);

        // Keep the identity-mismatch case last: the provider deliberately latches
        // stale state and the Runtime must fail closed for subsequent mutations.
        var wrongTarget = new CameraBindingTarget(networkProvider.Identity, WrongIdentityDevice);
        var wrongRequested = new CameraIpv4Configuration("192.168.10.46", 24);
        var wrong = await SubmitAuthorizedAsync(maintenance, stepUp, sessions, wrongTarget,
            wrongRequested, password, "验证重发现身份不匹配", networkProvider);
        Require(!wrong.Result.Succeeded && wrong.Result.Audit == AuditPersistence.Persisted &&
            wrong.Result.ReasonCode == "CameraNetworkRediscoveryIdentityMismatch" &&
            wrong.Result.Snapshot is { State: CameraNetworkMaintenanceState.Unknown } wrongSnapshot &&
            wrongSnapshot.Previous == new CameraIpv4Configuration("192.168.10.6", 24) &&
            wrongSnapshot.Requested == wrongRequested && !wrongSnapshot.IdentityVerified,
            "CameraNetworkWrongIdentityEvidenceInvalid");
        var wrongDevice = networkProvider.GetNetworkDiagnostics().Devices.Single(
            item => item.StableDeviceIdentity == WrongIdentityDevice);
        Require(wrongDevice.CurrentConfiguration == wrongRequested && wrongDevice.IsStale &&
            wrongDevice.OutstandingLeases == 0 && wrongDevice.ActiveOperations == 0,
            "CameraNetworkWrongIdentityProviderDiagnosticsInvalid");
        await Verified(runtime);

        var final = await runtime.GetSnapshotAsync();
        Require(!final.Ready && !final.Busy && final.ArmState == ProductionArmState.Disarmed &&
            final.ActiveRecipe is null && final.AdmissionBlockers.Contains(
                "CameraNetworkReconciliationRequired", StringComparer.Ordinal),
            "CameraNetworkChangedProductionAuthority");
        var diagnostics = networkProvider.GetNetworkDiagnostics();
        Require(diagnostics.InfrastructureFailures == 0 && diagnostics.Devices.All(item =>
            item.OutstandingLeases == 0 && item.ActiveOperations == 0),
            "CameraNetworkProviderLeaseLeak");

        // The first Runtime has completed every provider observation. Dispose its
        // container before opening a second SQLite-backed Runtime. The second
        // container deliberately registers no provider, acquisition, or recovery
        // service; it exercises only durable command admission after restart.
        await container.DisposeAsync();
        var armRestart = await RunArmRestartAsync(options, directory, userName,
            expectedPrincipal, password, target);

        var consumerHash = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(typeof(CameraNetworkDemo).Assembly.Location)));
        var evidence = new
        {
            Result = "Pass",
            ContractVersion = ConsumerContract,
            ConsumerSha256 = consumerHash,
            Provider = networkProvider.Identity,
            LogicalRole = "CameraNetworkMaintenance",
            StableDeviceIdentity = SuccessDevice,
            StationNetwork = new CameraStationNetwork(InterfaceId, StationConfiguration),
            Initial = SuccessPrevious,
            Requested = SuccessRequested,
            PreviousAddress = successSnapshot.Previous!.Address,
            RequestedAddress = successSnapshot.Requested.Address,
            ObservedAddress = successSnapshot.Observed!.Address,
            SuccessfulOperationId = success.OperationId,
            Request = new
            {
                OperationId = success.OperationId,
                Invocation = new
                {
                    Source = CommandSource.PhysicalConsole,
                    PrincipalId = final.Session.PrincipalId,
                    SessionId = final.Session.SessionId
                },
                Target = target,
                Requested = SuccessRequested,
                ChangeReason = "开发验收修改相机网络地址"
            },
            AuthorizationTarget = target.ContentHash,
            StepUpPermission = (int)Permission.ManageCameraBindings,
            StepUpCommandKind = (int)AuditedCommandKind.ChangeCameraNetworkConfiguration,
            PreviousRequestedObserved = successSnapshot.Previous == SuccessPrevious &&
                successSnapshot.Requested == SuccessRequested && successSnapshot.Observed == SuccessRequested,
            SameIdentity = queried.Snapshot is { } queriedEvidence &&
                queriedEvidence.Target == target && queriedEvidence.IdentityVerified,
            IdentityVerified = successSnapshot.IdentityVerified,
            RequiresRecipeActivation = successSnapshot.RequiresRecipeActivation,
            Ready = final.Ready,
            NoExistingBinding = startup.CameraSetup is null,
            StartupQualificationOnly = startupQualificationOnly,
            AnonymousRejected = !anonymous.Succeeded,
            AnonymousRejectedWithoutProjection = anonymousRejectedWithoutProjection,
            MissingStepUpRejected = !missingStepUp.Succeeded,
            MissingStepUpRejectedWithoutProjection = missingStepUpRejectedWithoutProjection,
            UnsupportedProviderRejected = !unsupported.Result.Succeeded,
            UnsupportedProviderSucceeded = unsupported.Result.Succeeded,
            UnsupportedProviderReason = unsupported.Result.ReasonCode,
            UnsupportedProviderAudit = unsupported.Result.Audit.ToString(),
            UnsupportedProviderRejectedWithoutProjection = unsupportedRejectedWithoutProjection,
            UnsupportedProviderAuthorityUnchanged = unsupportedAuthorityUnchanged,
            UnsupportedProviderBefore = AuthorityEvidence(unsupported.Before),
            UnsupportedProviderAfter = AuthorityEvidence(unsupported.After),
            BusyRejected = !busy.Result.Succeeded,
            ApplyFailureObserved = !failure.Result.Succeeded,
            ConflictRejected = !conflict.Result.Succeeded,
            WrongIdentityObserved = !wrong.Result.Succeeded &&
                wrong.Result.Snapshot?.State == CameraNetworkMaintenanceState.Unknown,
            ArmRestartRejectedByReconciliation = armRestart.RejectedByReconciliation,
            ArmRestartReadyFalse = armRestart.ReadyFalse,
            ArmRestartNoProviderRegistered = armRestart.NoProviderRegistered,
            ArmRestartNoProviderOpened = armRestart.NoProviderRegistered,
            ArmRestartRequiresRecipeActivation = armRestart.RequiresRecipeActivation,
            ArmRestartMalformedRejected = armRestart.MalformedInvocationRejected,
            ArmRestartEmptyCorrelationRejected = armRestart.EmptyCorrelationRejected,
            ArmRestartAnonymousRejected = armRestart.AnonymousRejected,
            AuditBeforePhysicalChange = successAuditBeforePhysicalChange,
            AuditBeforePhysicalStart = successAuditBeforePhysicalChange,
            TypedCommandAudited = successTrace.Records.Any(item => item.CommandKind ==
                AuditedCommandKind.ChangeCameraNetworkConfiguration && item.Phase == CommandAuditPhase.Completed),
            Diagnostics = diagnostics,
            PhysicalHardwareQualification = "NotRun",
            ProviderQualification = "NotRun",
            StationAcceptance = "NotRun",
            Production = "NotRun",
            NativeCrashIsolation = "NotRun",
            HostNetworkMutation = "NotRun"
        };
        await WriteJsonAsync(Path.Combine(directory, "camera-network-evidence.json"), evidence);
        await WriteJsonAsync(Path.Combine(directory, "summary.json"), new
        {
            Result = "Pass",
            ContractVersion = ConsumerContract,
            ConsumerSha256 = consumerHash,
            AppliedCount = diagnostics.Devices.Sum(item => item.SuccessfulApplyCount),
            MaintenanceLeaseHeld = diagnostics.Devices.Any(item => item.OutstandingLeases != 0),
            StartupQualificationOnly = startupQualificationOnly,
            ArmRestartRejectedByReconciliation = armRestart.RejectedByReconciliation,
            ArmRestartReadyFalse = armRestart.ReadyFalse,
            ArmRestartNoProviderRegistered = armRestart.NoProviderRegistered,
            ArmRestartNoProviderOpened = armRestart.NoProviderRegistered,
            ArmRestartRequiresRecipeActivation = armRestart.RequiresRecipeActivation,
            ArmRestartMalformedRejected = armRestart.MalformedInvocationRejected,
            ArmRestartEmptyCorrelationRejected = armRestart.EmptyCorrelationRejected,
            ArmRestartAnonymousRejected = armRestart.AnonymousRejected,
            SuccessfulOperationId = success.OperationId,
            PreviousAddress = successSnapshot.Previous!.Address,
            RequestedAddress = successSnapshot.Requested.Address,
            ObservedAddress = successSnapshot.Observed!.Address,
            SameIdentity = successSnapshot.IdentityVerified,
            Ready = final.Ready,
            RequiresRecipeActivation = successSnapshot.RequiresRecipeActivation,
            AuditPersisted = success.Result.Audit == AuditPersistence.Persisted,
            RejectedCases = new[] { "Anonymous", "MissingStepUp", "UnsupportedProvider", "Busy", "Conflict" },
            PhysicalHardwareQualification = "NotRun",
            ProviderQualification = "NotRun",
            StationAcceptance = "NotRun",
            Production = "NotRun",
            NativeCrashIsolation = "NotRun",
            HostNetworkMutation = "NotRun"
        });
    }

    private static async Task QueryCore(ProductionStoreOptions options, string directory)
    {
        Require(options.CameraNetwork is not null && options.CameraSetup is not null &&
            options.CameraRecovery is not null && options.LocalIdentity is not null &&
            options.AuditIntegrityPolicy is not null && options.AlarmPolicy is not null,
            "CameraNetworkRestartStoreOptionsRequired");
        var evidencePath = Path.Combine(directory, "camera-network-evidence.json");
        Require(File.Exists(evidencePath), "CameraNetworkRestartEvidenceMissing");
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(evidencePath));
        Require(evidence.RootElement.GetProperty("result").GetString() == "Pass" &&
            !evidence.RootElement.GetProperty("ready").GetBoolean() &&
            evidence.RootElement.GetProperty("requiresRecipeActivation").GetBoolean(),
            "CameraNetworkRestartEvidenceInvalid");
        var successfulOperation = Guid.Parse(evidence.RootElement
            .GetProperty("successfulOperationId").GetString()!);
        var before = await DatabaseHashAsync(options.DatabasePath);

        // This process intentionally resolves only independent read-only
        // capabilities. It does not construct a ServiceProvider, Runtime,
        // camera provider, device, or command writer.
        var trace = new SqliteCommandTraceQuery(options);
        var integrity = new SqliteAuditIntegrityQuery(options);
        var records = await trace.QueryAsync(new CommandTraceFilter(
            CorrelationId: successfulOperation, PageSize: 16));
        Require(records.Records.Any(item => item.CommandKind == AuditedCommandKind.ChangeCameraNetworkConfiguration &&
            item.Phase == CommandAuditPhase.Outcome && item.Disposition == CommandDisposition.Accepted) &&
            records.Records.Any(item => item.CommandKind == AuditedCommandKind.ChangeCameraNetworkConfiguration &&
                item.Phase == CommandAuditPhase.Completed),
            "CameraNetworkRestartTypedCommandMissing");
        var report = await integrity.VerifyAsync(new AuditVerificationRequest());
        Require(report.State == AuditIntegrityState.Verified,
            "CameraNetworkRestartAuditIntegrityUnavailable");
        var after = await DatabaseHashAsync(options.DatabasePath);
        Require(before == after, "CameraNetworkRestartChangedDatabase");

        await WriteJsonAsync(Path.Combine(directory, "camera-network-restart.json"), new
        {
            Result = "Pass",
            ContractVersion = ConsumerContract,
            TypedChangeAudited = true,
            OpenedDevices = 0,
            RegisteredProviders = 0,
            RegisteredWriter = false,
            DatabaseReadOnly = true,
            AuditIntegrityVerified = true,
            MainAndNonEmptyWalHashStable = true,
            WalAbsentOrEmptyEquivalent = true,
            DatabaseHashBefore = before,
            DatabaseHashAfter = after,
            Ready = false,
            RequiresRecipeActivation = true,
            SuccessfulOperationId = successfulOperation,
            TraceThroughPosition = records.ThroughPosition,
            IntegrityThroughSequence = report.ThroughSequence,
            PhysicalHardwareQualification = "NotRun",
            ProviderQualification = "NotRun",
            StationAcceptance = "NotRun",
            Production = "NotRun",
            NativeCrashIsolation = "NotRun",
            HostNetworkMutation = "NotRun"
        });
    }

    private static async Task<ArmRestartObservation> RunArmRestartAsync(
        ProductionStoreOptions options, string directory, string userName,
        string expectedPrincipal, string password, CameraBindingTarget target)
    {
        var services = new ServiceCollection();
        // Deliberately omit CameraSetupOptions, ICameraProvider, acquisition, and
        // recovery registrations. SQLite Runtime remains the only command owner.
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(100));
        await using var replacement = services.BuildServiceProvider();

        var runtime = replacement.GetRequiredService<IStationRuntime>();
        var setup = replacement.GetRequiredService<ICameraSetupRuntime>();
        var sessions = replacement.GetRequiredService<IInteractiveSessionService>();
        Require(setup.Providers.Count == 0, "CameraNetworkArmRestartRegisteredProvider");
        await Verified(runtime);
        await StartupAlarmReady(runtime);
        var startup = await runtime.GetSnapshotAsync();
        var startupQualificationOnly = !startup.Ready &&
            startup.ArmState == ProductionArmState.Disarmed &&
            startup.Handshake == HandshakePhase.Unknown &&
            startup.Recovery == RecoveryState.Required &&
            startup.Evidence.PendingDeliveries == 0 &&
            startup.CurrentExecution is null &&
            startup.Plc.Connection == HealthState.Unconfigured;
        Require(startupQualificationOnly, "CameraNetworkArmRestartStartupNotQualificationOnly");

        var malformedBefore = await runtime.GetSnapshotAsync();
        var malformed = await runtime.SubmitAsync(new ArmProductionCommand(Guid.NewGuid(),
            new((CommandSource)999, new string('x', 257), Guid.NewGuid())));
        await Verified(runtime);
        var malformedAfter = await runtime.GetSnapshotAsync();
        var malformedAuthorityUnchanged = AuthorityUnchanged(malformedBefore, malformedAfter);
        ReportActual("arm-restart-malformed-invocation", malformed.Disposition ==
            CommandDisposition.Accepted, malformed.ReasonCode, malformed.Audit,
            malformedAuthorityUnchanged);
        Require(malformed.Disposition == CommandDisposition.Rejected,
            "CameraNetworkArmRestartMalformedAccepted");
        Require(malformed.ReasonCode == "AuthenticationRequired",
            "CameraNetworkArmRestartMalformedReasonInvalid");
        Require(malformed.Audit == AuditPersistence.Persisted,
            "CameraNetworkArmRestartMalformedAuditUnavailable");
        Require(malformedAuthorityUnchanged, "CameraNetworkArmRestartMalformedProjectedAuthority");
        var malformedInvocationRejected = malformed.Disposition == CommandDisposition.Rejected &&
            malformed.ReasonCode == "AuthenticationRequired" &&
            malformed.Audit == AuditPersistence.Persisted && malformedAuthorityUnchanged;

        var anonymousBefore = await runtime.GetSnapshotAsync();
        var anonymous = await runtime.SubmitAsync(new ArmProductionCommand(Guid.NewGuid(),
            new(CommandSource.PhysicalConsole)));
        await Verified(runtime);
        var anonymousAfter = await runtime.GetSnapshotAsync();
        var anonymousAuthorityUnchanged = AuthorityUnchanged(anonymousBefore, anonymousAfter);
        ReportActual("arm-restart-anonymous", anonymous.Disposition == CommandDisposition.Accepted,
            anonymous.ReasonCode, anonymous.Audit, anonymousAuthorityUnchanged);
        Require(anonymous.Disposition == CommandDisposition.Rejected,
            "CameraNetworkArmRestartAnonymousAccepted");
        Require(anonymous.ReasonCode == "AuthenticationRequired",
            "CameraNetworkArmRestartAnonymousReasonInvalid");
        Require(anonymous.Audit == AuditPersistence.Persisted,
            "CameraNetworkArmRestartAnonymousAuditUnavailable");
        Require(anonymousAuthorityUnchanged, "CameraNetworkArmRestartAnonymousProjectedAuthority");
        var anonymousRejected = anonymous.Disposition == CommandDisposition.Rejected &&
            anonymous.ReasonCode == "AuthenticationRequired" &&
            anonymous.Audit == AuditPersistence.Persisted && anonymousAuthorityUnchanged;

        await SignIn(sessions, runtime, userName, expectedPrincipal, password);
        var maintenance = replacement.GetRequiredService<ICameraNetworkMaintenanceRuntime>();
        var persistedMaintenance = await maintenance.GetNetworkMaintenanceAsync(target,
            Invocation(sessions));
        var requiresRecipeActivation = persistedMaintenance.Available &&
            persistedMaintenance.Snapshot is { RequiresRecipeActivation: true };
        Require(requiresRecipeActivation, "CameraNetworkArmRestartMaintenanceEvidenceMissing");
        var armBefore = await VerifiedSnapshot(runtime);
        var armCorrelation = Guid.NewGuid();
        var arm = await runtime.SubmitAsync(new ArmProductionCommand(armCorrelation,
            Invocation(sessions)));
        await Verified(runtime);
        var armAfter = await runtime.GetSnapshotAsync();
        var armAuthorityUnchanged = AuthorityUnchanged(armBefore, armAfter);
        ReportAuthorityActual("arm-restart-authenticated", armBefore, armAfter);
        ReportActual("arm-restart-authenticated", arm.Disposition == CommandDisposition.Accepted,
            arm.ReasonCode, arm.Audit, armAuthorityUnchanged);
        Require(arm.Disposition == CommandDisposition.Rejected,
            "CameraNetworkArmRestartReconciliationAccepted");
        Require(arm.ReasonCode == "CameraNetworkReconciliationRequired",
            "CameraNetworkArmRestartReconciliationReasonInvalid");
        Require(arm.Audit == AuditPersistence.Persisted,
            "CameraNetworkArmRestartReconciliationAuditUnavailable");
        Require(armAuthorityUnchanged, "CameraNetworkArmRestartProjectedAuthority");
        var armRejectedByReconciliation = arm.Disposition == CommandDisposition.Rejected &&
            arm.ReasonCode == "CameraNetworkReconciliationRequired" &&
            arm.Audit == AuditPersistence.Persisted;
        var armReadyFalse = !armAfter.Ready &&
            armAfter.ArmState == ProductionArmState.Disarmed;

        // An empty correlation cannot be audited. Preserve the existing local
        // audit-fault projection, and run this only after the independent
        // reconciliation barrier has been observed on a healthy restarted store.
        var emptyCorrelation = await runtime.SubmitAsync(new ArmProductionCommand(
            Guid.Empty, new(CommandSource.PhysicalConsole)));
        var emptyCorrelationAfter = await runtime.GetSnapshotAsync();
        var emptyCorrelationRejected = emptyCorrelation.Disposition == CommandDisposition.Rejected &&
            emptyCorrelation.ReasonCode == "InvalidCommandContext" &&
            emptyCorrelation.Audit == AuditPersistence.Unavailable &&
            !emptyCorrelationAfter.Ready &&
            emptyCorrelationAfter.ArmState == ProductionArmState.Disarmed &&
            emptyCorrelationAfter.AdmissionBlockers.Contains("TraceAuditUnavailable", StringComparer.Ordinal);
        ReportActual("arm-restart-empty-correlation", emptyCorrelation.Disposition ==
            CommandDisposition.Accepted, emptyCorrelation.ReasonCode, emptyCorrelation.Audit,
            AuthorityUnchanged(armAfter, emptyCorrelationAfter));
        Require(emptyCorrelationRejected, "CameraNetworkArmRestartEmptyCorrelationBehaviorInvalid");

        var observation = new ArmRestartObservation(armRejectedByReconciliation, armReadyFalse,
            setup.Providers.Count == 0, malformedInvocationRejected, emptyCorrelationRejected,
            anonymousRejected, startupQualificationOnly, requiresRecipeActivation);
        await WriteJsonAsync(Path.Combine(directory, "camera-network-arm-restart.json"), new
        {
            Result = "Pass",
            ContractVersion = ConsumerContract,
            StartupHandshake = startup.Handshake.ToString(),
            StartupRecovery = startup.Recovery.ToString(),
            StartupPendingDeliveries = startup.Evidence.PendingDeliveries,
            StartupQualificationOnly = startupQualificationOnly,
            EmptyCorrelationRejected = emptyCorrelationRejected,
            EmptyCorrelationReason = emptyCorrelation.ReasonCode,
            MalformedInvocationRejected = malformedInvocationRejected,
            MalformedInvocationReason = malformed.ReasonCode,
            AnonymousRejected = anonymousRejected,
            AnonymousReason = anonymous.ReasonCode,
            ArmCorrelationId = armCorrelation,
            ArmRejectedByReconciliation = armRejectedByReconciliation,
            ArmReasonCode = arm.ReasonCode,
            ArmAuditPersisted = arm.Audit == AuditPersistence.Persisted,
            ArmAuthorityUnchanged = AuthorityUnchanged(armBefore, armAfter),
            Ready = armAfter.Ready,
            RequiresRecipeActivation = requiresRecipeActivation,
            ArmState = armAfter.ArmState.ToString(),
            AdmissionBlockers = armAfter.AdmissionBlockers,
            NoProviderRegistered = setup.Providers.Count == 0,
            NoProviderOpened = setup.Providers.Count == 0,
            OpenedDevices = 0,
            RegisteredWriter = true,
            DatabaseReadOnly = false,
            IndependentReadOnlyRestart = false,
            PhysicalHardwareQualification = "NotRun",
            ProviderQualification = "NotRun",
            StationAcceptance = "NotRun",
            Production = "NotRun",
            NativeCrashIsolation = "NotRun",
            HostNetworkMutation = "NotRun"
        });
        return observation;
    }

    private static ServiceCollection Services(ProductionStoreOptions options,
        ICameraProvider networkProvider, ICameraProvider unsupportedProvider)
    {
        var services = new ServiceCollection();
        services.AddSharpInspectCameraSetup(new CameraSetupOptions(new[]
        {
            networkProvider, unsupportedProvider
        }) { StationNetwork = new CameraStationNetwork(InterfaceId, StationConfiguration) });
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(100));
        return services;
    }

    private static AuditedNetworkProvider CreateNetworkProvider(VirtualCameraClock clock)
    {
        var image = VirtualCameraImage.CreateSynthetic("Network", 64, 48,
            VisionPixelFormat.Mono8, null, Seed);
        var scenarios = new[]
        {
            FixtureScenario(SuccessDevice, image),
            FixtureScenario(ConflictDevice, image),
            FixtureScenario(FailureDevice, image),
            FixtureScenario(WrongIdentityDevice, image)
        };
        var fixtures = new[]
        {
            new VirtualNetworkCameraFixture(scenarios[0],
                VirtualNetworkCameraOptions.Success(SuccessPrevious, InterfaceId)),
            new VirtualNetworkCameraFixture(scenarios[1],
                VirtualNetworkCameraOptions.DetectableConflict(SuccessRequested,
                    new CameraIpv4Configuration("192.168.10.5", 24), InterfaceId)),
            new VirtualNetworkCameraFixture(scenarios[2],
                VirtualNetworkCameraOptions.ApplyFailureAfterChange(
                    new CameraIpv4Configuration("192.168.10.4", 24), InterfaceId)),
            new VirtualNetworkCameraFixture(scenarios[3],
                VirtualNetworkCameraOptions.WrongRediscoveredIdentity(
                    new CameraIpv4Configuration("192.168.10.6", 24), InterfaceId,
                    "Virtual:Network-Other"))
        };
        return new AuditedNetworkProvider(new VirtualNetworkCameraProvider(
            fixtures, clock, poolCapacity: 2));
    }

    private static UnsupportedNetworkProvider CreateUnsupportedProvider(VirtualCameraClock clock)
    {
        var image = VirtualCameraImage.CreateSynthetic("Unsupported", 16, 12,
            VisionPixelFormat.Mono8, null, Seed + 1);
        var scenario = FixtureScenario(UnsupportedDevice, image);
        return new UnsupportedNetworkProvider(new VirtualCameraProvider(
            new[] { scenario }, clock, poolCapacity: 1));
    }

    private static VirtualCameraScenario FixtureScenario(string stableIdentity,
        VirtualCameraImage image) => new("network-fixture", "1", Seed, stableIdentity,
        CreateCapabilities(), new[] { image }, Array.Empty<VirtualCameraAcquisitionPlan>());

    private static CameraCapabilities CreateCapabilities() => new(
        new[] { ProductionAcquisitionMode.SoftwareTrigger },
        new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
        new(10, 10000, 1, CameraQuantizationMode.Nearest, 0.5),
        new(0, 24, 1, CameraQuantizationMode.Exact),
        new(0, 10000, 1, CameraQuantizationMode.Exact),
        new(128, 96, new(0, 127, 1), new(0, 95, 1),
            new(1, 128, 1), new(1, 96, 1)));

    private static async Task<(CameraNetworkOperationResult Result, Guid OperationId)>
        SubmitAuthorizedAsync(ICameraNetworkMaintenanceRuntime maintenance,
            IStepUpAuthentication stepUp, IInteractiveSessionService sessions,
            CameraBindingTarget target, CameraIpv4Configuration requested, string password,
            string reason, AuditedNetworkProvider? auditedProvider = null)
    {
        var operationId = Guid.NewGuid();
        var invocation = Invocation(sessions);
        var binding = new StepUpBinding(Permission.ManageCameraBindings, operationId,
            target.ContentHash, AuditedCommandKind.ChangeCameraNetworkConfiguration);
        var grant = await stepUp.ReauthenticateAsync(new(Guid.NewGuid(), invocation, binding,
            password));
        Require(grant.Succeeded && grant.GrantId is { } grantId,
            "CameraNetworkStepUpFailed_" + grant.ReasonCode);
        auditedProvider?.ExpectCorrelation(operationId);
        var result = await maintenance.ChangeNetworkConfigurationAsync(new(operationId,
            invocation with { StepUpGrantId = grant.GrantId }, target, requested, reason));
        return (result, operationId);
    }

    private static async Task<AuthorizedMaintenanceObservation>
        SubmitAuthorizedWithSnapshotAsync(ICameraNetworkMaintenanceRuntime maintenance,
            IStepUpAuthentication stepUp, IInteractiveSessionService sessions,
            IStationRuntime runtime, CameraBindingTarget target,
            CameraIpv4Configuration requested, string password, string reason)
    {
        var operationId = Guid.NewGuid();
        var invocation = Invocation(sessions);
        var binding = new StepUpBinding(Permission.ManageCameraBindings, operationId,
            target.ContentHash, AuditedCommandKind.ChangeCameraNetworkConfiguration);
        var grant = await stepUp.ReauthenticateAsync(new(Guid.NewGuid(), invocation, binding,
            password));
        Require(grant.Succeeded && grant.GrantId is { } grantId,
            "CameraNetworkStepUpFailed_" + grant.ReasonCode);

        // A Step-Up write briefly drives the audit projection through
        // verification. Wait for the station snapshot to observe the settled
        // state before taking the authority baseline for this command.
        var before = await VerifiedSnapshot(runtime);
        var result = await maintenance.ChangeNetworkConfigurationAsync(new(operationId,
            invocation with { StepUpGrantId = grant.GrantId }, target, requested, reason));
        var after = await VerifiedSnapshot(runtime);
        return new(result, operationId, before, after);
    }

    private static CommandInvocation Invocation(IInteractiveSessionService sessions) =>
        new(CommandSource.PhysicalConsole, sessions.Current.PrincipalId,
            sessions.Current.SessionId);

    private static bool AuthorityUnchanged(StationStateSnapshot before, StationStateSnapshot after) =>
        before.Ready == after.Ready && before.ArmState == after.ArmState &&
        before.AdmissionBlockers.SequenceEqual(after.AdmissionBlockers, StringComparer.Ordinal);

    private static object AuthorityEvidence(StationStateSnapshot state) => new
    {
        Ready = state.Ready,
        ArmState = state.ArmState.ToString(),
        AdmissionBlockers = state.AdmissionBlockers.ToArray()
    };

    private static void ReportAuthorityActual(string scenario, StationStateSnapshot before,
        StationStateSnapshot after) => Console.Error.WriteLine(
        $"V120 camera-network {scenario} authority-before ready={before.Ready} " +
        $"arm={before.ArmState} blockers=[{string.Join(',', before.AdmissionBlockers)}] " +
        $"authority-after ready={after.Ready} arm={after.ArmState} " +
        $"blockers=[{string.Join(',', after.AdmissionBlockers)}]");

    private static async Task SignIn(IInteractiveSessionService sessions, IStationRuntime runtime,
        string userName, string expectedPrincipal, string password)
    {
        var signedIn = await sessions.SignInAsync(new PasswordSignInRequest(userName, password));
        Require(signedIn.Succeeded && signedIn.Identity?.PrincipalId.ToString("D") == expectedPrincipal,
            "CameraNetworkConsumerAuthenticationFailed");
        await Verified(runtime);
        var state = await runtime.GetSnapshotAsync();
        Require(!state.Ready && state.ArmState == ProductionArmState.Disarmed &&
            state.ActiveRecipe is null, "CameraNetworkAuthenticationGrantedProductionAuthority");
    }

    private static async Task Verified(IStationRuntime runtime)
    {
        _ = await VerifiedSnapshot(runtime);
    }

    private static async Task StartupAlarmReady(IStationRuntime runtime)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var snapshot = await runtime.GetSnapshotAsync(timeout.Token);
            if (snapshot.AuditIntegrity?.State == AuditIntegrityState.Verified &&
                snapshot.AlarmState is { Available: true } alarms &&
                alarms.Instances.Any(instance => instance.Code == "StartupRecoveryRequired" &&
                    instance.Lifecycle != AlarmLifecycle.Cleared) &&
                snapshot.AdmissionBlockers.Contains("AlarmProductionBlocked",
                    StringComparer.Ordinal)) return;
            Require(snapshot.AuditIntegrity?.State != AuditIntegrityState.Faulted,
                "CameraNetworkStartupAlarmUnavailable");
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task<StationStateSnapshot> VerifiedSnapshot(IStationRuntime runtime)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // The station can still expose a Verified report from before the
        // preceding write. Wait for a new verifier report to reach the station;
        // a different station revision alone could be an unrelated projection.
        var initialReport = (await runtime.GetSnapshotAsync(timeout.Token)).AuditIntegrity;
        while (true)
        {
            var snapshot = await runtime.GetSnapshotAsync(timeout.Token);
            var state = snapshot.AuditIntegrity?.State;
            if (state == AuditIntegrityState.Verified &&
                !ReferenceEquals(initialReport, snapshot.AuditIntegrity) &&
                !snapshot.AdmissionBlockers.Contains("AuditIntegrityUnavailable",
                    StringComparer.Ordinal)) return snapshot;
            Require(state != AuditIntegrityState.Faulted, "CameraNetworkAuditUnavailable");
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task WriteJsonAsync(string path, object value) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        }));

    private static async Task<string> DatabaseHashAsync(string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var candidate in new[] { path, path + "-wal" })
        {
            var wal = candidate.EndsWith("-wal", StringComparison.Ordinal);
            if (!File.Exists(candidate) || (wal && new FileInfo(candidate).Length == 0))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(wal ? "AbsentOrEmptyWal" : "Absent"));
                continue;
            }
            hash.AppendData(SHA256.HashData(await File.ReadAllBytesAsync(candidate)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void ReportActual(string scenario, bool succeeded, string reasonCode,
        AuditPersistence audit, bool authorityUnchanged) => Console.Error.WriteLine(
        $"V120 camera-network {scenario} actual succeeded={succeeded} reason={reasonCode} " +
        $"audit={audit} authorityUnchanged={authorityUnchanged}");

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new CameraNetworkCheckException(reason);
    }

    private sealed record ArmRestartObservation(bool RejectedByReconciliation, bool ReadyFalse,
        bool NoProviderRegistered, bool MalformedInvocationRejected, bool EmptyCorrelationRejected,
        bool AnonymousRejected, bool StartupQualificationOnly, bool RequiresRecipeActivation);

    private sealed record AuthorizedMaintenanceObservation(CameraNetworkOperationResult Result,
        Guid OperationId, StationStateSnapshot Before, StationStateSnapshot After);

    private sealed class CameraNetworkCheckException : Exception
    {
        internal CameraNetworkCheckException(string reasonCode) => ReasonCode = reasonCode;
        internal string ReasonCode { get; }
    }

    /// <summary>
    /// Fixture-only observer that makes the ordering proof externally visible:
    /// the inner provider's Apply is called only after the read-only trace query
    /// finds the accepted typed command for the current operation.
    /// </summary>
    private sealed class AuditedNetworkProvider : ICameraProvider, ICameraNetworkConfigurator
    {
        private readonly VirtualNetworkCameraProvider _inner;
        private readonly object _gate = new();
        private ICommandTraceQuery? _trace;
        private Guid? _expectedCorrelation;
        private bool _auditObservedBeforePhysicalChange;

        internal AuditedNetworkProvider(VirtualNetworkCameraProvider inner) =>
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        public CameraProviderIdentity Identity => _inner.Identity;

        internal void AttachTrace(ICommandTraceQuery trace) =>
            _trace = trace ?? throw new ArgumentNullException(nameof(trace));

        internal void ExpectCorrelation(Guid operationId)
        {
            if (operationId == Guid.Empty) throw new ArgumentException("CameraNetworkOperationIdRequired");
            lock (_gate)
            {
                _expectedCorrelation = operationId;
                _auditObservedBeforePhysicalChange = false;
            }
        }

        internal bool AuditObservedBeforePhysicalChange
        {
            get { lock (_gate) return _auditObservedBeforePhysicalChange; }
        }

        internal VirtualNetworkCameraProviderDiagnostics GetNetworkDiagnostics() =>
            _inner.GetNetworkDiagnostics();

        public ValueTask<CameraDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken = default) =>
            _inner.DiscoverAsync(cancellationToken);

        public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
            CancellationToken cancellationToken = default) =>
            _inner.OpenAsync(stableDeviceIdentity, cancellationToken);

        public async ValueTask<CameraNetworkMaintenanceLeaseResult> TryBeginMaintenanceAsync(
            string stableDeviceIdentity, CancellationToken cancellationToken = default)
        {
            var lease = await _inner.TryBeginMaintenanceAsync(stableDeviceIdentity,
                cancellationToken);
            if (!lease.Succeeded || lease.Session is null) return lease;
            return CameraNetworkMaintenanceLeaseResult.Success(
                new AuditedNetworkSession(this, lease.Session));
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        private sealed class AuditedNetworkSession : ICameraNetworkMaintenanceSession
        {
            private readonly AuditedNetworkProvider _provider;
            private readonly ICameraNetworkMaintenanceSession _inner;

            internal AuditedNetworkSession(AuditedNetworkProvider provider,
                ICameraNetworkMaintenanceSession inner)
            {
                _provider = provider;
                _inner = inner;
            }

            public CameraBindingTarget Target => _inner.Target;

            public ValueTask<CameraNetworkReadResult> ReadCurrentAsync(
                CancellationToken cancellationToken = default) =>
                _inner.ReadCurrentAsync(cancellationToken);

            public ValueTask<CameraNetworkConflictResult> DetectConflictAsync(
                CameraIpv4Configuration requested, CameraStationNetwork stationNetwork,
                CancellationToken cancellationToken = default) =>
                _inner.DetectConflictAsync(requested, stationNetwork, cancellationToken);

            public async ValueTask<CameraNetworkApplyResult> ApplyAsync(
                CameraIpv4Configuration expectedPrevious, CameraIpv4Configuration requested,
                CancellationToken cancellationToken = default)
            {
                var trace = _provider._trace;
                Guid? correlation;
                lock (_provider._gate) correlation = _provider._expectedCorrelation;
                if (trace is null || correlation is null)
                    return new CameraNetworkApplyResult(false,
                        "CameraNetworkAuditOrderUnavailable");

                var page = await trace.QueryAsync(new CommandTraceFilter(
                    CorrelationId: correlation.Value, PageSize: 16), cancellationToken);
                var admitted = page.Records.Any(item =>
                    item.CommandKind == AuditedCommandKind.ChangeCameraNetworkConfiguration &&
                    item.Phase == CommandAuditPhase.Outcome &&
                    item.Disposition == CommandDisposition.Accepted);
                lock (_provider._gate)
                    _provider._auditObservedBeforePhysicalChange = admitted;
                if (!admitted)
                    return new CameraNetworkApplyResult(false,
                        "CameraNetworkAuditOrderUnavailable");
                return await _inner.ApplyAsync(expectedPrevious, requested, cancellationToken);
            }

            public ValueTask DisposeAsync() => _inner.DisposeAsync();
        }
    }

    /// <summary>Provider with a distinct identity and no network extension.</summary>
    private sealed class UnsupportedNetworkProvider : ICameraProvider
    {
        private readonly ICameraProvider _inner;
        internal UnsupportedNetworkProvider(ICameraProvider inner)
        {
            _inner = inner;
            Identity = new CameraProviderIdentity("SharpInspect.Virtual.Unsupported", "1",
                "SharpInspect.NET.Cameras.Virtual.Unsupported", "0.1.0-dev.1");
        }

        public CameraProviderIdentity Identity { get; }

        public async ValueTask<CameraDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            var result = await _inner.DiscoverAsync(cancellationToken);
            if (!result.Succeeded) return CameraDiscoveryResult.Failure(result.ReasonCode);
            return CameraDiscoveryResult.Success(result.Devices.Select(item =>
                new CameraDeviceDescriptor(Identity, item.StableDeviceIdentity,
                    item.DisplayName, item.ReportedModel)));
        }

        public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraOpenResult.Failure("UnsupportedProviderOpen"));

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
