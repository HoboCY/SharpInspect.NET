using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Identity;
using SharpInspect.Wpf;

namespace SharpInspect.SampleHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? Option(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        if (Option("--conformance-demo") is { } conformanceDemoDirectory)
            return ConformanceDemo.RunDemo(conformanceDemoDirectory, Option("--conformance-source"));
        if (Option("--conformance-query") is { } conformanceQueryDirectory)
            return ConformanceDemo.RunQuery(conformanceQueryDirectory);
        if (args.Contains("--algorithm-prepare-check", StringComparer.OrdinalIgnoreCase))
            return AlgorithmPreparationDemo.Run();
        if (args.Contains("--frame-consumer-check", StringComparer.OrdinalIgnoreCase))
            return FrameConsumerDemo.Run();
        if (args.Contains("--algorithm-execution-check", StringComparer.OrdinalIgnoreCase))
            return AlgorithmExecutionDemo.Run();
        if (Option("--overlay-check") is { } overlayDirectory)
            return AlgorithmOverlayDemo.Run(overlayDirectory);
        if (Option("--overlay-query") is { } overlayQueryDirectory)
            return AlgorithmOverlayDemo.Query(overlayQueryDirectory);
        var identitySmoke = args.Contains("--identity-login-smoke", StringComparer.OrdinalIgnoreCase);
        var administratorRecoveryCheck = args.Contains("--administrator-recovery-check", StringComparer.OrdinalIgnoreCase);
        var smoke = identitySmoke || args.Contains("--smoke", StringComparer.OrdinalIgnoreCase);
        var databasePath = Path.GetFullPath(Option("--trace-db") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SharpInspect.SampleHost", "trace.sqlite"));
        if (Option("--trace-db") is null) Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var auditKey = Option("--audit-key");
        var draftCheckDirectory = Option("--recipe-draft-check");
        var draftQueryDirectory = Option("--recipe-draft-query");
        var draftEnabled = draftCheckDirectory is not null || draftQueryDirectory is not null ||
            args.Contains("--recipe-drafts", StringComparer.OrdinalIgnoreCase);
        var storeOptions = new ProductionStoreOptions(databasePath)
        {
            LocalIdentity = Option("--identity-policy") is { } identityPolicy ? ReadIdentityOptions(identityPolicy) : null,
            AlarmPolicy = Option("--alarm-policy") is { } alarmPolicy ? AlarmDemo.ReadPolicy(alarmPolicy) : null,
            AlgorithmResultArchive = args.Contains("--algorithm-result-archive", StringComparer.OrdinalIgnoreCase)
                ? new AlgorithmResultArchiveOptions() : null,
            RecipeDrafts = draftEnabled ? new RecipeDraftStoreOptions(RecipeDraftDemo.ExecutionPolicy) : null,
            AuditIntegrityPolicy = auditKey is null ? null : new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1", auditKey)
            {
                AllowInitialKeyCreation = true, CheckpointEveryEntries = 2, VerificationInterval = TimeSpan.FromSeconds(1),
                KeyDirectory = Option("--audit-key-directory") ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SharpInspect.AuditKeys")
            }
        };
        if (draftCheckDirectory is not null)
            return RecipeDraftDemo.Run(storeOptions, draftCheckDirectory, Option("--user-name"), Option("--expected-principal"));
        if (draftQueryDirectory is not null)
            return RecipeDraftDemo.Query(storeOptions, draftQueryDirectory);
        if (administratorRecoveryCheck)
            return AdministratorRecoveryDemo.Run(storeOptions);
        if (args.Contains("--alarm-check", StringComparer.OrdinalIgnoreCase))
            return AlarmDemo.Run(storeOptions);
        if (Option("--verify-trace") is { } verificationFile)
        {
            try { VerifyRestartAsync(storeOptions, verificationFile).GetAwaiter().GetResult(); return 0; }
            catch (Exception exception) { Console.Error.WriteLine($"V102 RESTART FAIL {exception.GetType().Name}: {exception.Message}"); return 1; }
        }
        var screenshotIndex = Array.IndexOf(args, "--screenshot");
        var screenshot = screenshotIndex >= 0 && screenshotIndex + 1 < args.Length
            ? Path.GetFullPath(args[screenshotIndex + 1]) : null;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var services = new ServiceCollection();
        if (draftEnabled) services.AddSingleton(RecipeDraftDemo.CreateFactory());
        services.AddSharpInspectSqliteRuntime(storeOptions, TimeSpan.FromMilliseconds(500));
        services.AddSingleton<StationShellViewModel>(p =>
        {
            var sessions = p.GetService<IInteractiveSessionService>();
            return new StationShellViewModel(p.GetRequiredService<IStationRuntime>(),
                new DispatcherUiDispatcher(app.Dispatcher), invocationFactory: () =>
                {
                    var current = sessions?.Current;
                    return current is
                        { State: InteractiveSessionState.Authenticated, PrincipalId: not null, SessionId: not null }
                        ? new CommandInvocation(CommandSource.PhysicalConsole, current.PrincipalId, current.SessionId)
                        : new CommandInvocation(CommandSource.PhysicalConsole);
                });
        });
        services.AddSingleton<CommandTraceViewModel>();
        services.AddSingleton<AuditIntegrityViewModel>();
        services.AddSingleton(p => new AlgorithmResultHistoryViewModel(p.GetService<IAlgorithmResultQuery>(),
            new DispatcherUiDispatcher(app.Dispatcher)));
        services.AddSingleton(p => new RecipeDraftEditorViewModel(p.GetService<IRecipeDraftEditor>(),
            p.GetService<IInteractiveSessionService>(), new DispatcherUiDispatcher(app.Dispatcher),
            storeOptions.RecipeDrafts?.ExecutionPolicy, p.GetService<IStepUpAuthentication>()));
        services.AddSingleton(p => new IdentityViewModel(p.GetService<ILocalAdministratorBootstrap>(),
            p.GetService<IIdentityProvider>(), "SampleDevelopmentStation", p.GetService<IInteractiveSessionService>(),
            new DispatcherUiDispatcher(app.Dispatcher)));
        services.AddSingleton(p => new IdentityAdministrationViewModel(
            p.GetRequiredService<IStationRuntime>(), p.GetService<IInteractiveSessionService>(),
            p.GetService<IStepUpAuthentication>(), p.GetService<IIdentityAdministrationQuery>(),
            new DispatcherUiDispatcher(app.Dispatcher)));
        services.AddSingleton(p => new AdministratorRecoveryViewModel(
            p.GetService<ILocalAdministratorRecovery>(), p.GetService<IInteractiveSessionService>(),
            new DispatcherUiDispatcher(app.Dispatcher)));
        services.AddSingleton(p => new AlarmViewModel(p.GetRequiredService<StationShellViewModel>(),
            p.GetRequiredService<IStationRuntime>(), p.GetService<IInteractiveSessionService>(),
            p.GetService<IIdentityAdministrationQuery>(), p.GetService<IStepUpAuthentication>(),
            p.GetService<IAlarmHistoryQuery>(), new DispatcherUiDispatcher(app.Dispatcher),
            acknowledgeRequiresStepUp: storeOptions.LocalIdentity?.AuthorizationPolicy.RequiresStepUp(Permission.AcknowledgeAlarm) == true));
        var provider = services.BuildServiceProvider();
        var vm = provider.GetRequiredService<StationShellViewModel>();
        var runtime = provider.GetRequiredService<IStationRuntime>();
        var trace = provider.GetRequiredService<CommandTraceViewModel>();
        var integrity = provider.GetRequiredService<AuditIntegrityViewModel>();
        var identity = provider.GetRequiredService<IdentityViewModel>();
        var identityAdministration = provider.GetRequiredService<IdentityAdministrationViewModel>();
        var recovery = provider.GetRequiredService<AdministratorRecoveryViewModel>();
        var alarms = provider.GetRequiredService<AlarmViewModel>();
        var window = new ShellWindow(vm, trace, integrity, identity, identityAdministration, recovery, alarms,
            provider.GetRequiredService<AlgorithmResultHistoryViewModel>(),
            provider.GetRequiredService<RecipeDraftEditorViewModel>());
        var exitCode = 0;
        if (smoke)
        {
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
        }
        app.Startup += async (_, _) =>
        {
            try
            {
                window.Show();
                await vm.StartAsync();
                if (smoke)
                {
                    if (auditKey is not null)
                        await WaitAsync(() => vm.CurrentSnapshot?.AuditIntegrity?.State == AuditIntegrityState.Verified);
                    if (storeOptions.AlarmPolicy is not null)
                        await WaitAsync(() => vm.CurrentSnapshot?.AlarmState is { Available: true, Instances.Count: > 0 } &&
                            vm.CurrentSnapshot.AuditIntegrity?.State == AuditIntegrityState.Verified);
                    if (identitySmoke)
                    {
                        vm.NavigateTo("Maintenance");
                        window.VerifyMaintenanceLayout();
                        await WaitAsync(() => !identity.IsBusy && identity.CanAuthenticate);
                        var password = ReadSmokePassword();
                        await window.SubmitIdentityLoginSmokeAsync(Option("--user-name") ?? "", password);
                        Require(identity.CurrentIdentity?.PrincipalId == Guid.Parse(Option("--expected-principal") ?? ""), "identity login principal");
                        var signedIn = await runtime.GetSnapshotAsync();
                        Require(signedIn.Session.State == InteractiveSessionState.Authenticated && signedIn.Session.SessionId is not null &&
                            signedIn.Session.PrincipalId == identity.CurrentIdentity!.PrincipalId.ToString("D"), "Runtime session projection");
                        Require(!vm.State.Ready, "identity login must not bypass production admission");
                        await WaitAsync(() => vm.CurrentSnapshot?.Session.State == InteractiveSessionState.Authenticated &&
                            vm.Freshness == SnapshotFreshness.Fresh);
                        var authenticatedArm = await vm.ArmProductionAsync();
                        Require(authenticatedArm.Disposition == CommandDisposition.Rejected &&
                            authenticatedArm.ReasonCode == "DeploymentPoliciesMissing",
                            "authenticated Arm must reach production qualification and remain rejected");
                        Require(identityAdministration.IsConfigured, "identity administration services must be configured");
                        await window.RefreshIdentityAdministrationSmokeAsync();
                        Require(identityAdministration.CanCreateAccount, "authenticated administrator can create an account");
                        var createdUserName = "rui.li." + Guid.NewGuid().ToString("N")[..8];
                        var createOutcome = await window.SubmitIdentityAdministrationCreateSmokeAsync(
                            createdUserName, "开发验收个人", password, password);
                        var createdCommand = createOutcome ?? throw new SmokeAssertionException(
                            "governed account creation returned no Runtime outcome");
                        Require(createdCommand.Disposition == CommandDisposition.Accepted,
                            "governed account creation must be accepted: " + createdCommand.ReasonCode);
                        var stepUpBinding = identityAdministration.LastStepUpBinding;
                        Require(stepUpBinding is not null &&
                            stepUpBinding.Permission == Permission.ManageAccounts &&
                            stepUpBinding.CommandKind == AuditedCommandKind.CreateHumanAccount &&
                            stepUpBinding.CommandCorrelationId == createdCommand.CorrelationId,
                            "create action Step-Up binding must match the submitted command");
                        await window.RefreshIdentityAdministrationSmokeAsync();
                        var createdAccount = identityAdministration.Accounts.SingleOrDefault(
                            account => account.UserName == createdUserName);
                        Require(createdAccount is not null && createdAccount.DisplayName == "开发验收个人" &&
                            createdAccount.RoleBundle == HumanRoleBundle.Operator && createdAccount.CredentialEnabled,
                            "created Operator account must appear after authoritative directory refresh");
                        await WaitAsync(() => vm.State.LastCommand is
                            { CorrelationId: var correlationId, State: OperationState.Completed } &&
                            correlationId == createdCommand.CorrelationId);
                        Require(!vm.State.Ready, "governed account creation must not make production Ready");
                        var commandFacts = await WaitForCommandTraceAsync(
                            provider.GetRequiredService<ICommandTraceQuery>(), createdCommand.CorrelationId);
                        Require(commandFacts.Length == 2 &&
                            commandFacts[0].CommandKind == AuditedCommandKind.CreateHumanAccount &&
                            commandFacts[0].Phase == CommandAuditPhase.Outcome &&
                            commandFacts[0].Disposition == CommandDisposition.Accepted &&
                            commandFacts[1].CommandKind == AuditedCommandKind.CreateHumanAccount &&
                            commandFacts[1].Phase == CommandAuditPhase.Completed &&
                            commandFacts.All(fact => fact.AuthenticatedHumanPrincipalId == signedIn.Session.PrincipalId &&
                                fact.SystemPrincipalId == SystemPrincipalId.Runtime),
                            "governed account trace must contain accepted and completed facts");
                        Require(createdAccount is not null && stepUpBinding is not null &&
                            stepUpBinding.TargetId == createdAccount.PrincipalId.ToString("D"),
                            "create action Step-Up target must bind to the created account");
                        window.BringIdentityAdministrationIntoViewForSmoke();
                        if (screenshot is not null)
                            RenderScreenshot(window, Path.Combine(Path.GetDirectoryName(screenshot)!, "identity-administration.png"));
                        window.VerifyIdentityAdministrationBottomReachableForSmoke();
                        Console.WriteLine("V106-P01 independent-process WPF governed account creation PASS");
                        if (screenshot is not null) RenderScreenshot(window, screenshot);
                        window.Close();
                        await window.WaitForSessionLockAsync();
                        Require(window.IsPrivacyLocked, "identity ordinary Close remains privacy lock");
                        var locked = await runtime.GetSnapshotAsync();
                        Require(locked.Session is { State: InteractiveSessionState.Locked, SessionId: null, PrincipalId: null } &&
                            locked.Ready == signedIn.Ready && locked.ArmState == signedIn.ArmState && locked.Plc == signedIn.Plc &&
                            locked.Lifecycle == RuntimeLifecycle.Running, "session lock preserves independent production axes");
                        window.RevealPage();
                        Require(window.IsPrivacyLocked && !vm.CanArmProduction && vm.CanStopProduction, "old session cannot reveal privileged UI");
                        if (screenshot is not null) RenderScreenshot(window, Path.Combine(Path.GetDirectoryName(screenshot)!, "identity-locked.png"));
                        var sessions = provider.GetRequiredService<IInteractiveSessionService>();
                        var oldActivity = await sessions.ReportActivityAsync(signedIn.Session.SessionId!.Value);
                        Require(!oldActivity.Succeeded, "locked session rejects old activity proof");
                        await window.SubmitLockedLoginSmokeAsync(Option("--user-name") ?? "", password);
                        var reauthenticated = await runtime.GetSnapshotAsync();
                        Require(reauthenticated.Session.State == InteractiveSessionState.Authenticated,
                            "locked page authentication: " + identity.ErrorMessage);
                        Require(!window.IsPrivacyLocked &&
                            reauthenticated.Session.SessionId != signedIn.Session.SessionId &&
                            reauthenticated.Session.PrincipalId == signedIn.Session.PrincipalId,
                            "locked page requires fresh authentication and a new session");
                        await window.SubmitLogoutSmokeAsync();
                        var loggedOut = await runtime.GetSnapshotAsync();
                        Require(window.IsPrivacyLocked && loggedOut.Session is
                            { State: InteractiveSessionState.Unauthenticated, PrincipalId: null, SessionId: null } &&
                            loggedOut.Ready == signedIn.Ready && loggedOut.ArmState == signedIn.ArmState &&
                            loggedOut.Plc == signedIn.Plc && loggedOut.Lifecycle == RuntimeLifecycle.Running,
                            "logout clears only the interactive session");
                        Console.WriteLine("V105-P01 native paste/Runtime session/window lock/reauthentication/logout/production independence PASS");
                        Console.WriteLine("V104-P01 independent-process WPF password login/immutable identity/privacy PASS");
                    }
                    else await RunSmokeAsync(window, vm, runtime, trace, integrity, recovery, alarms,
                        screenshot, Option("--trace-manifest"));
                }
            }
            catch (Exception exception)
            {
                exitCode = 1;
                if (identitySmoke) Console.Error.WriteLine(exception is SmokeAssertionException assertion
                    ? $"V104 SMOKE FAIL {assertion.Message}" : $"V104 SMOKE FAIL {exception.GetType().Name}");
                else if (smoke) Console.Error.WriteLine($"SMOKE FAIL {exception.GetType().Name}: {exception.Message}");
                else window.ShowUnavailable();
            }
            finally
            {
                if (smoke)
                {
                    // Dispose while the Dispatcher is alive; ordinary Close never takes this path.
                    try { await provider.DisposeAsync(); }
                    finally { window.AllowSmokeShutdown(); app.Shutdown(exitCode); }
                }
            }
        };
        app.Run();
        return exitCode;
    }

    private sealed record IdentityDevelopmentConfiguration(string PasswordPolicyVersion, string BlocklistId, string BlocklistVersion,
        string BlocklistContentHash, string[] BlocklistValues, string HashBaselineVersion, int WorkFactor,
        AuthenticationPolicy AuthenticationPolicy, AuthorizationDevelopmentConfiguration AuthorizationPolicy);

    private sealed record AuthorizationDevelopmentConfiguration(string Id, string Version,
        Dictionary<HumanRoleBundle, Permission[]> RoleBundles, Permission[]? StepUpPermissions);

    private static LocalIdentityOptions ReadIdentityOptions(string path)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidOperationException("IdentityPolicyFileTooLarge");
        var configuration = JsonSerializer.Deserialize<IdentityDevelopmentConfiguration>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("IdentityPolicyInvalid");
        var policy = new LocalPasswordPolicy { Version = configuration.PasswordPolicyVersion,
            Blocklist = new PasswordBlocklist(configuration.BlocklistId, configuration.BlocklistVersion,
                configuration.BlocklistContentHash, configuration.BlocklistValues) };
        var authorization = configuration.AuthorizationPolicy ?? throw new InvalidOperationException("AuthorizationPolicyRequired");
        var authorizationPolicy = new AuthorizationPolicy(authorization.Id, authorization.Version,
            authorization.RoleBundles.ToDictionary(pair => pair.Key, pair => (IEnumerable<Permission>)pair.Value),
            authorization.StepUpPermissions);
        return new LocalIdentityOptions("SampleDevelopmentStation", policy,
            new Pbkdf2PasswordHasher(new PasswordHashBaseline(configuration.HashBaselineVersion, configuration.WorkFactor)),
            configuration.AuthenticationPolicy ?? throw new InvalidOperationException("AuthenticationPolicyRequired"), authorizationPolicy);
    }

    private static string ReadSmokePassword()
    {
        // Development acceptance supplies this through a private stdin pipe, never arguments or logs.
        var input = new System.Text.StringBuilder();
        for (var i = 0; i <= 8192; i++)
        {
            var next = Console.In.Read();
            if (next is -1 or '\n') return JsonSerializer.Deserialize<string>(input.ToString())
                ?? throw new InvalidOperationException("IdentitySmokeInputInvalid");
            input.Append((char)next);
        }
        throw new InvalidOperationException("IdentitySmokeInputTooLong");
    }

    private static async Task RunSmokeAsync(ShellWindow window, StationShellViewModel vm,
        IStationRuntime runtime, CommandTraceViewModel trace, AuditIntegrityViewModel integrity,
        AdministratorRecoveryViewModel recovery, AlarmViewModel alarms, string? screenshot, string? traceManifest)
    {
        var startup = await runtime.GetSnapshotAsync();
        Require(startup.Lifecycle == RuntimeLifecycle.Running && !startup.Ready &&
            startup.ArmState == ProductionArmState.Disarmed, "startup must remain disarmed");
        if (recovery.IsConfigured)
        {
            await recovery.RefreshAsync();
            Require(recovery.CurrentStatus is { RecoveryAvailable: false, ReasonCode: "SafetyStopUnverified" },
                "administrator recovery must remain fail-closed without physical stop proof");
            Console.WriteLine("V108-P03 WPF recovery status fail-closed PASS reason=SafetyStopUnverified");
        }
        Console.WriteLine($"V101-P01 startup PASS epoch={startup.RuntimeEpoch} revision={startup.Revision}");
        vm.NavigateTo("Maintenance");
        window.VerifyMaintenanceLayout();
        vm.NavigateTo("Production");
        var arm = await vm.ArmProductionAsync();
        var authorizationConfigured = startup.AdmissionBlockers.Contains(
            "AuthorizationQualificationMissing", StringComparer.Ordinal);
        var expectedArmReason = authorizationConfigured
            ? startup.Session.State == InteractiveSessionState.Authenticated
                ? "SessionMismatch" : "AuthenticationRequired"
            : "DeploymentPoliciesMissing";
        Require(arm.Disposition == CommandDisposition.Rejected && arm.ReasonCode == expectedArmReason,
            "Arm rejection must reflect the configured authorization path: " + expectedArmReason);
        Require(arm.Audit == AuditPersistence.Persisted, "rejected Arm must be durable");
        Console.WriteLine($"V101-R02 consumer Arm PASS correlation={arm.CorrelationId} reason={arm.ReasonCode}");
        if (startup.AuditIntegrity is { State: AuditIntegrityState.Verified } initialIntegrity)
            await WaitAsync(() => vm.CurrentSnapshot?.AuditIntegrity is { State: AuditIntegrityState.Verified } current &&
                current.VerifiedThroughSequence > initialIntegrity.ThroughSequence);
        var stop = await vm.GracefulStopAsync();
        Require(stop.Disposition == CommandDisposition.Accepted && stop.ReasonCode == "StopAdmitted", "Stop admission");
        Require(stop.Audit == AuditPersistence.Persisted, "accepted Stop must be durable");
        await WaitAsync(() => vm.State.LastCommand is { State: OperationState.Completed } p && p.CorrelationId == stop.CorrelationId);
        Console.WriteLine($"V101-U05 consumer Stop PASS admitted={stop.ReasonCode} final={vm.State.LastCommand!.ReasonCode}");
        await VerifyFeedAsync(startup);

        var before = await runtime.GetSnapshotAsync();
        var outcome = vm.LastCommandOutcome?.CorrelationId;
        vm.NavigateTo("Alarms");
        vm.NavigateTo("Production");
        if (screenshot is not null) RenderScreenshot(window, screenshot);
        vm.NavigateTo("Trace");
        trace.CorrelationIdText = stop.CorrelationId.ToString();
        await trace.RefreshAsync();
        await integrity.RefreshAsync();
        window.VerifyAuditLayout();
        if (startup.AuditIntegrity?.State == AuditIntegrityState.Verified)
        {
            Require(integrity.CurrentReport?.State == AuditIntegrityState.Verified,
                "signed audit chain is verified by independent read-only capability");
            Require(!vm.State.Ready, "integrity verification cannot substitute for missing production qualification");
            Console.WriteLine($"V103-P01 signed chain/visible read-only status PASS through={integrity.ThroughSequence}");
        }
        Require(trace.Rows.Count == 2 && trace.Rows.Any(x => x.Phase == CommandAuditPhase.Completed), "trace page rebuilds stop facts");
        Require(trace.Rows.All(x => x.AuthenticatedHumanPrincipalId is null && x.SystemPrincipalId == "SharpInspect.Runtime"), "system actor must not impersonate a person");
        if (screenshot is not null) RenderScreenshot(window, Path.Combine(Path.GetDirectoryName(screenshot)!, "consumer-trace.png"));
        if (traceManifest is not null)
        {
            var manifest = new TraceSmokeManifest(arm.CorrelationId, stop.CorrelationId, trace.Rows.Select(x => x.EventId).ToArray());
            File.WriteAllText(traceManifest, JsonSerializer.Serialize(manifest));
        }
        if (startup.AlarmState?.Policy is not null)
        {
            vm.NavigateTo("Alarms");
            await WaitAsync(() => alarms.IsSnapshotFresh && alarms.Instances.Count > 0);
            await alarms.RefreshHistoryAsync();
            window.VerifyAlarmLayout();
            var selectedAlarmId = alarms.Instances[0].InstanceId;
            var selectionRevision = vm.CurrentSnapshot!.Revision;
            window.SelectAlarmForSmoke(selectedAlarmId);
            await WaitAsync(() => vm.CurrentSnapshot!.Revision >= selectionRevision + 2);
            window.VerifyAlarmSelectionForSmoke(selectedAlarmId);
            window.VerifyAlarmSummaryForSmoke();
            Require(alarms.TotalAlarmCount == startup.AlarmState.Instances.Count &&
                alarms.PlcTotalUncleared == alarms.TotalAlarmCount &&
                alarms.Instances.Any(instance => instance.Code == "StartupRecoveryRequired" &&
                    instance.Severity == AlarmSeverity.Warning && instance.ProductionImpact == ProductionImpact.BlockNewTriggers) &&
                alarms.HistoryRows.Any(record => record.Code == "StartupRecoveryRequired"),
                "WPF alarm list, bounded history and PLC summary must describe the same startup instance");
            if (screenshot is not null)
                RenderScreenshot(window, Path.Combine(Path.GetDirectoryName(screenshot)!, "consumer-alarms.png"));
            Console.WriteLine("V109-P02 WPF alarm list/history/PLC projection PASS ready=false physicalDevices=NotRun");
        }
        window.Close();
        await window.WaitForSessionLockAsync();
        Require(window.IsPrivacyLocked && window.IsVisible, "ordinary Close must privacy-lock the existing window");
        var after = await runtime.GetSnapshotAsync();
        Require(after.Lifecycle == RuntimeLifecycle.Running && after.ArmState == before.ArmState &&
            after.LastCommand == before.LastCommand && vm.LastCommandOutcome?.CorrelationId == outcome,
            "navigation/ordinary Close must not submit commands or stop Runtime");
        Require(vm.CanStopProduction, "local Stop remains reachable while privacy locked");
        window.RevealPage();
        Require(window.IsPrivacyLocked == (after.Session.State == InteractiveSessionState.Locked), "page reveal cannot restore an authoritative locked session");
        Console.WriteLine("V101-U04 Close/navigation/privacy PASS runtime=Running");
        if (recovery.IsConfigured)
        {
            await window.OpenAdministratorRecoverySmokeAsync();
            var recoverySnapshot = await runtime.GetSnapshotAsync();
            Require(window.IsAdministratorRecoveryPrivacyVisible && window.IsPrivacyLocked &&
                recoverySnapshot.Session.State == InteractiveSessionState.Unauthenticated && !recoverySnapshot.Ready &&
                recovery.CurrentStatus is { RecoveryAvailable: false, ReasonCode: "SafetyStopUnverified" },
                "privacy recovery entry must stay covered, end the session and reject unverified physical stop");
            if (screenshot is not null)
                RenderScreenshot(window, Path.Combine(Path.GetDirectoryName(screenshot)!, "consumer-recovery.png"));
            Console.WriteLine("V108-P04 WPF privacy recovery entry PASS covered=true session=Unauthenticated ready=false recoveryAvailable=false");
        }
        Console.WriteLine($"V102-P01 durable outcome/lifecycle/trace page PASS correlation={stop.CorrelationId}");
        Console.WriteLine($"V101 CONSUMER SMOKE PASS runtime={Environment.Version} os={Environment.OSVersion.Version}");
    }

    private sealed record TraceSmokeManifest(Guid RejectedCorrelation, Guid AcceptedCorrelation, Guid[] AcceptedEventIds);

    private static async Task VerifyRestartAsync(ProductionStoreOptions options, string manifestFile)
    {
        var manifest = JsonSerializer.Deserialize<TraceSmokeManifest>(File.ReadAllText(manifestFile))!;
        var query = new SqliteCommandTraceQuery(options);
        var rejected = await query.QueryAsync(new CommandTraceFilter(CorrelationId: manifest.RejectedCorrelation));
        Require(rejected.Records.Count == 1 && rejected.Records[0].Disposition == CommandDisposition.Rejected, "rejected outcome survives restart");
        var first = await query.QueryAsync(new CommandTraceFilter(CorrelationId: manifest.AcceptedCorrelation, PageSize: 1));
        Require(first.Records.Count == 1 && first.NextAfterPosition is not null, "bounded first page");
        var second = await query.QueryAsync(new CommandTraceFilter(CorrelationId: manifest.AcceptedCorrelation,
            AfterPosition: first.NextAfterPosition!.Value, ThroughPosition: first.ThroughPosition, PageSize: 1));
        Require(second.Records.Count == 1 && second.NextAfterPosition is null, "bounded second page");
        var events = first.Records.Concat(second.Records).ToArray();
        Require(events.Select(x => x.EventId).SequenceEqual(manifest.AcceptedEventIds), "same immutable identities after process restart");
        Require(events[0].Disposition == CommandDisposition.Accepted && events[1].Phase == CommandAuditPhase.Completed,
            "projection rebuilt from accepted and completed facts");
        Console.WriteLine($"V102-P02 independent-process read-only restart PASS accepted={manifest.AcceptedCorrelation} events={events.Length}");
        if (options.AuditIntegrityPolicy is not null)
        {
            var report = await new SqliteAuditIntegrityQuery(options).VerifyAsync(new AuditVerificationRequest());
            Require(report.State == AuditIntegrityState.Verified, "signed chain and machine key survive independent process restart: " + report.ReasonCode);
            Console.WriteLine($"V103-P02 independent signed chain restart PASS through={report.ThroughSequence} checkpoint={report.CheckpointSequence}");
        }
    }

    private static async Task VerifyFeedAsync(StationStateSnapshot template)
    {
        // Private isolated test source: it has no reference or write path to StationRuntime.
        var epoch = Guid.NewGuid();
        var first = template with { RuntimeEpoch = epoch, Revision = 4, Ready = true };
        var clock = new ProbeClock();
        var source = new ProbeRuntime(first);
        await using var vm = new StationShellViewModel(source, new DispatcherUiDispatcher(), clock,
            new SnapshotFreshnessPolicy(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(20)));
        await vm.StartAsync();
        await WaitAsync(() => source.Subscriptions > 0);
        source.Publish(first with { Revision = 2, Ready = false });
        await Task.Delay(40);
        Require(vm.State.Revision == 4 && vm.State.Ready, "old revision must not replace current state");
        clock.Advance(TimeSpan.FromMilliseconds(120));
        await WaitAsync(() => vm.Freshness == SnapshotFreshness.Stale);
        Require(!vm.State.Ready && !vm.State.DisplayedHealthy && !vm.CanArmProduction && vm.CanStopProduction,
            "stale state removes ready/healthy/privileged affordances");
        Console.WriteLine("V101-U01/U02 live feed ordering/staleness PASS");
        var second = template with { RuntimeEpoch = Guid.NewGuid(), Revision = 3 };
        source.FullSnapshot = second;
        source.Publish(second with { Revision = 99 });
        await WaitAsync(() => vm.State.RuntimeEpoch == second.RuntimeEpoch && vm.State.Revision == 3);
        Require(source.Reads >= 2, "epoch change must obtain full state");
        source.Publish(first with { Revision = 100 });
        await Task.Delay(30);
        Require(vm.State.RuntimeEpoch == second.RuntimeEpoch && vm.State.Revision == 3, "late old epoch must not return");
        source.FullSnapshot = second with { Revision = 4 };
        var reads = source.Reads;
        source.Reconnect();
        await WaitAsync(() => source.Subscriptions >= 2 && vm.State.Revision == 4);
        Require(source.Reads > reads, "reconnection must request full state");
        Console.WriteLine("V101-U03 live feed epoch/disconnect/reconnect PASS");
    }

    private static async Task WaitAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private static async Task<CommandTraceRecord[]> WaitForCommandTraceAsync(
        ICommandTraceQuery query, Guid correlationId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            var page = await query.QueryAsync(
                new CommandTraceFilter(CorrelationId: correlationId, PageSize: 10), timeout.Token);
            if (page.Records.Count >= 2) return page.Records.ToArray();
            await Task.Delay(20, timeout.Token);
        }
    }

    private static void RenderScreenshot(ShellWindow window, string path)
    {
        window.UpdateLayout();
        var visual = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),
            (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        Console.WriteLine($"V101 screenshot={path}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new SmokeAssertionException(message);
    }

    private sealed class SmokeAssertionException : Exception
    {
        internal SmokeAssertionException(string safeAssertion) : base(safeAssertion) { }
    }

    private sealed class ProbeClock : IMonotonicClock
    {
        private long _ticks;
        public long GetTimestamp() => Interlocked.Read(ref _ticks);
        public TimeSpan ElapsedSince(long timestamp) => TimeSpan.FromTicks(GetTimestamp() - timestamp);
        public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
    }

    private sealed class ProbeRuntime : IStationRuntime
    {
        private Channel<StationStateSnapshot> _feed = Channel.CreateBounded<StationStateSnapshot>(4);
        private StationStateSnapshot _full;
        private int _subscriptions;
        private int _reads;
        public ProbeRuntime(StationStateSnapshot value) => _full = value;
        public StationStateSnapshot FullSnapshot { get => Volatile.Read(ref _full); set => Volatile.Write(ref _full, value); }
        public int Subscriptions => Volatile.Read(ref _subscriptions);
        public int Reads => Volatile.Read(ref _reads);
        public void Publish(StationStateSnapshot value) => Volatile.Read(ref _feed).Writer.TryWrite(value);
        public void Reconnect() => Interlocked.Exchange(ref _feed, Channel.CreateBounded<StationStateSnapshot>(4)).Writer.TryComplete();
        public ValueTask<StationStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _reads);
            return ValueTask.FromResult(FullSnapshot);
        }
        public async IAsyncEnumerable<StationStateSnapshot> WatchSnapshotsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var feed = Volatile.Read(ref _feed);
            Interlocked.Increment(ref _subscriptions);
            await foreach (var snapshot in feed.Reader.ReadAllAsync(cancellationToken)) yield return snapshot;
        }
        public ValueTask<RuntimeCommandOutcome> SubmitAsync(RuntimeCommand command, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Rejected, "ProbeOnly"));
    }
}
