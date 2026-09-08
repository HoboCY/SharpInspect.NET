using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Storage;
using SharpInspect.Wpf;

namespace SharpInspect.SampleHost;

internal static class CameraSetupDemo
{
    private const string Role = "TopCamera";
    private const string DeviceA = "Virtual:Setup-A";
    private const string DeviceB = "Virtual:Setup-B";

    internal static int Run(ProductionStoreOptions options, string directory, string? userName, string? expectedPrincipal) =>
        Execute(() => RunCore(options, Path.GetFullPath(directory), userName!, expectedPrincipal!, ReadPassword()),
            "V117-N01 camera-setup-consumer PASS stepUp=true readBack=true partialFailureClosed=true wrongTargetRejected=true invalidRejected=true ready=false");

    internal static int Query(ProductionStoreOptions options, string directory, string? userName, string? expectedPrincipal) =>
        Execute(() => QueryCore(options, Path.GetFullPath(directory), userName!, expectedPrincipal!, ReadPassword()),
            "V117-N02 camera-setup-restart PASS exactBinding=true openedDevices=0 configurationNotInherited=true ready=false");

    private static int Execute(Func<Task> action, string marker)
    {
        try { Pump(action); Console.WriteLine(marker); return 0; }
        catch (CameraSetupCheckException exception)
        { Console.Error.WriteLine("V117 camera-setup FAIL reason=" + exception.ReasonCode); return 1; }
        catch
        { Console.Error.WriteLine("V117 camera-setup FAIL reason=CameraSetupConsumerCheckFailed"); return 1; }
    }

    private static string ReadPassword() => JsonSerializer.Deserialize<string>(Console.ReadLine() ?? "null") ??
        throw new CameraSetupCheckException("CameraSetupConsumerPasswordRequired");

    private static ServiceCollection Services(ProductionStoreOptions options, ICameraProvider camera)
    {
        var services = new ServiceCollection();
        services.AddSingleton(camera);
        services.AddSharpInspectCameraSetup(new CameraSetupOptions());
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(100));
        return services;
    }

    private static async Task RunCore(ProductionStoreOptions options, string directory, string userName,
        string expectedPrincipal, string password)
    {
        Require(File.Exists(options.DatabasePath), "CameraSetupIdentityStoreRequired");
        Directory.CreateDirectory(directory);
        using var clock = new VirtualCameraClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var camera = CreateProvider(clock);
        await using var container = Services(options, camera).BuildServiceProvider();
        var runtime = container.GetRequiredService<IStationRuntime>();
        var setup = container.GetRequiredService<ICameraSetupRuntime>();
        Require(ReferenceEquals(runtime, setup), "CameraSetupRuntimeAuthoritySplit");
        var sessions = container.GetRequiredService<IInteractiveSessionService>();
        var stepUp = container.GetRequiredService<IStepUpAuthentication>();
        await Verified(runtime);
        var anonymousDiscovery = await setup.DiscoverAsync(camera.Identity, new(CommandSource.PhysicalConsole));
        Require(!anonymousDiscovery.Succeeded && anonymousDiscovery.Devices.Count == 0,
            "CameraSetupAnonymousDiscoveryAccepted");
        await SignIn(sessions, runtime, userName, expectedPrincipal, password);
        Console.WriteLine("V117 stage=Authenticated");
        await using var model = new CameraSetupViewModel(setup, sessions, stepUp, new DispatcherUiDispatcher());
        var panel = new CameraSetupPanel(model);
        var window = new Window { Content = panel, Width = 1160, Height = 960, ShowInTaskbar = false,
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000, Top = -32000 };
        try
        {
            window.Show();
            window.UpdateLayout();
            SetInput(panel, model, nameof(model.LogicalRole), Role);
            ((ComboBox)panel.FindName("ProviderComboBox")).SelectedItem = model.Providers.Single();
            InvokeButton(panel, "RefreshButton");
            await Until(() => !model.IsBusy);
            InvokeButton(panel, "DiscoverButton");
            await Until(() => !model.IsBusy && model.CandidateCount == 2);
            Console.WriteLine("V117 stage=CandidatesDiscovered");
            Require(model.SelectedDevice is null && camera.GetDiagnostics().Devices.All(device => device.OpenCount == 0),
                "CameraSetupDiscoveryOpenedOrSelectedDevice");
            ((ListBox)panel.FindName("DeviceListBox")).SelectedItem =
                model.Devices.Single(device => device.StableDeviceIdentity == DeviceA);
            SetInput(panel, model, nameof(model.ChangeReason), "开发验证：明确选择逻辑相机设备");
            var invocation = Invocation(sessions);
            var denied = await setup.RebindAsync(new(Guid.NewGuid(), invocation, Role, 0, null,
                new(camera.Identity, DeviceA), "缺少 Step-Up 的负例"));
            Require(!denied.Succeeded && camera.GetDiagnostics().Devices.All(device => device.OpenCount == 0),
                "CameraSetupMissingStepUpChangedDevice");
            await Verified(runtime);
            Console.WriteLine("V117 stage=RebindStarting");
            await InvokeMutation(panel, model, "RebindButton", password);
            Require(model.LastOperationResult is { Succeeded: true, Audit: AuditPersistence.Persisted } &&
                model.CurrentBinding is { Revision: 1 } && model.CurrentBinding.Target.StableDeviceIdentity == DeviceA,
                "CameraSetupRebindFailed_" + (model.LastOperationResult?.ReasonCode ?? model.ErrorCode ?? "NoResult"));
            Console.WriteLine("V117 stage=BindingPersisted");
            var binding = model.CurrentBinding!;
            var wrongTarget = await model.RebindAsync(new CameraBindingTarget(camera.Identity, DeviceB), password);
            Require(wrongTarget is null && model.CurrentBinding?.RevisionHash == binding.RevisionHash,
                "CameraSetupWrongTargetAccepted");
            await model.RefreshAsync();
            SetInput(panel, model, nameof(model.ExposureTimeUsText), "100.4");
            SetInput(panel, model, nameof(model.GainDbText), "0");
            SetInput(panel, model, nameof(model.RoiWidthText), "invalid");
            Require(!model.CanApply && !((Button)panel.FindName("ApplyButton")).IsEnabled,
                "CameraSetupInvalidParameterEnabledApply");
            Require(camera.GetDiagnostics().Devices.All(device => device.ConfigurationCursor == 0),
                "CameraSetupInvalidInputAppliedConfiguration");
            SetInput(panel, model, nameof(model.RoiWidthText), "64");
            SetInput(panel, model, nameof(model.RoiHeightText), "48");
            SetInput(panel, model, nameof(model.AcquisitionTimeoutMsText), "500");
            SetInput(panel, model, nameof(model.TriggerDelayUsText), "0");
            Console.WriteLine("V117 stage=ConfigurationStarting");
            await InvokeMutation(panel, model, "ApplyButton", password);
            Require(model.LastOperationResult is { Succeeded: true, Audit: AuditPersistence.Persisted },
                "CameraSetupApplyFailed_" + (model.LastOperationResult?.ReasonCode ?? model.ErrorCode ?? "NoResult"));
            var applied = model.CurrentSetup!;
            Require(applied.Health is { Connection: CameraConnectionState.Open,
                Configuration: CameraConfigurationState.Applied, Acquisition: CameraAcquisitionState.Stopped } &&
                applied.Requested?.ExposureTimeUs == 100.4 && applied.Effective?.ExposureTimeUs == 100 &&
                applied.Differences.Count == 1 && applied.Differences[0].Setting == CameraNumericSetting.ExposureTimeUs,
                "CameraSetupReadBackEvidenceInvalid");
            Console.WriteLine("V117 stage=ConfigurationReadBackVerified");
            await AssertDisarmed(runtime);
            SaveWindow(window, Path.Combine(directory, "camera-setup-applied.png"));
            SaveReadBack(window, panel, Path.Combine(directory, "camera-setup-applied-readback.png"));
            SetInput(panel, model, nameof(model.ExposureTimeUsText), "200.4");
            await InvokeMutation(panel, model, "ApplyButton", password);
            var failed = model.CurrentSetup;
            Require(model.LastOperationResult is { Succeeded: false, Audit: AuditPersistence.Persisted } &&
                failed?.Health is { Connection: CameraConnectionState.Closed,
                    Configuration: CameraConfigurationState.Unknown, Acquisition: CameraAcquisitionState.Stopped } &&
                failed.Requested?.ExposureTimeUs == 200.4 && failed.Effective is null && failed.Differences.Count == 0,
                "CameraSetupPartialFailureNotClosed_" + (model.LastOperationResult?.ReasonCode ?? "NoResult") +
                "_" + model.LastOperationResult?.Audit + "_" + failed?.Health.Connection +
                "_" + failed?.Health.Configuration);
            Require(camera.GetDiagnostics().Devices.All(device => !device.IsOpen), "CameraSetupPartialFailureLeftDeviceOpen");
            Console.WriteLine("V117 stage=PartialFailureClosed");
            await AssertDisarmed(runtime);
            SaveWindow(window, Path.Combine(directory, "camera-setup-failed.png"));
            SaveReadBack(window, panel, Path.Combine(directory, "camera-setup-failed-readback.png"));
            SetInput(panel, model, nameof(model.ExposureTimeUsText), "100.4");
            await InvokeMutation(panel, model, "ApplyButton", password);
            Require(model.LastOperationResult is { Succeeded: true } &&
                model.CurrentSetup?.Health.Configuration == CameraConfigurationState.Applied,
                "CameraSetupExplicitReapplyFailed");
            Console.WriteLine("V117 stage=ExplicitReapplyVerified");
            var state = await runtime.GetSnapshotAsync();
            Require(state.CameraSetup is { Configuration: CameraConfigurationState.Applied } &&
                !JsonSerializer.Serialize(state).Contains(DeviceA, StringComparison.Ordinal),
                "CameraSetupOrdinarySnapshotLeaksDeviceIdentity");
            var providerDiagnostics = camera.GetDiagnostics();
            Require(providerDiagnostics.Devices.Single(device => device.StableDeviceIdentity == DeviceA).ConfigurationCursor == 3 &&
                providerDiagnostics.Devices.Single(device => device.StableDeviceIdentity == DeviceB).OpenCount == 0,
                "CameraSetupUnexpectedDeviceOrConfigurationAttempt");
            var extension = new CameraProviderExtensionRequirement(camera.Identity, "Virtual.UnknownExtension", "1", new string('A', 64));
            var extensionOperation = Guid.NewGuid();
            var extensionGrant = await stepUp.ReauthenticateAsync(new(extensionOperation, Invocation(sessions),
                new(Permission.ManageCameraBindings, extensionOperation, Role, AuditedCommandKind.ApplyCameraDebugConfiguration), password));
            Require(extensionGrant.Succeeded, "CameraSetupExtensionStepUpFailed");
            var extensionResult = await setup.ApplyDebugConfigurationAsync(new(extensionOperation,
                Invocation(sessions) with { StepUpGrantId = extensionGrant.GrantId }, Role, binding.Revision,
                binding.RevisionHash, applied.Requested!, "未知扩展拒绝验证", extension));
            Require(!extensionResult.Succeeded, "CameraSetupUnknownExtensionAccepted");
            var missingDeviceOperation = Guid.NewGuid();
            var missingDeviceGrant = await stepUp.ReauthenticateAsync(new(missingDeviceOperation,
                Invocation(sessions), new(Permission.ManageCameraBindings, missingDeviceOperation,
                    Role, AuditedCommandKind.RebindCamera), password));
            Require(missingDeviceGrant.Succeeded, "CameraSetupMissingDeviceStepUpFailed");
            var missingDevice = await setup.RebindAsync(new(missingDeviceOperation,
                Invocation(sessions) with { StepUpGrantId = missingDeviceGrant.GrantId }, Role,
                binding.Revision, binding.RevisionHash, new(camera.Identity, "Virtual:Missing"),
                "验证设备打开失败仍保留完整审计"));
            Require(missingDevice is { Succeeded: false, Audit: AuditPersistence.Persisted } &&
                missingDevice.Snapshot?.Binding?.RevisionHash == binding.RevisionHash &&
                missingDevice.Snapshot.Health is { Connection: CameraConnectionState.Closed,
                    Configuration: CameraConfigurationState.Unknown } &&
                missingDevice.Snapshot.Effective is null,
                "CameraSetupFailedRebindEvidenceInvalid_" + missingDevice.ReasonCode);
            await Verified(runtime);
            Console.WriteLine("V117 stage=FailedRebindAudited");
            var finalDiagnostics = camera.GetDiagnostics();
            Require(finalDiagnostics.Devices.Single(device => device.StableDeviceIdentity == DeviceA).ConfigurationCursor == 3 &&
                finalDiagnostics.Devices.All(device => !device.IsOpen && device.AcquisitionCursor == 0 && device.FramesProduced == 0 &&
                    device.OutstandingLeases == 0), "CameraSetupUnexpectedAcquisitionOrExtensionWrite");
            await AssertDisarmed(runtime);
            var passwordBox = (PasswordBox)panel.FindName("StepUpPasswordBox");
            passwordBox.Password = password;
            var lockResult = await sessions.LockAsync(sessions.Current.SessionId, SessionLockReason.UserRequested);
            Require(lockResult.Succeeded, "CameraSetupSessionLockFailed");
            await Until(() => model.CurrentBinding is null && model.CandidateCount == 0 && passwordBox.Password.Length == 0);
            Require(!model.CanApply && !model.CanRebind, "CameraSetupLockedCommandsEnabled");
            Console.WriteLine("V117 stage=SessionPrivacyCleared");
            await File.WriteAllTextAsync(Path.Combine(directory, "camera-setup-evidence.json"), JsonSerializer.Serialize(new
            {
                Result = "Pass", LogicalRole = Role, BindingRevision = binding.Revision, BindingRevisionHash = binding.RevisionHash,
                ProviderIdentity = camera.Identity, BoundDevice = DeviceA, StepUpEnforced = true,
                Requested = applied.Requested, Effective = applied.Effective, Differences = applied.Differences,
                PartialFailureClosed = true, FailedRequested = failed?.Requested,
                WrongTargetRejected = true, InvalidParametersRejected = true,
                UnknownExtensionRejected = true, FailedRebindAudited = true,
                SessionPrivacyCleared = true, ProductionReady = false,
                RecipeActivation = "NotRun", PhysicalDevices = "NotRun", ProviderQualification = "NotRun",
                StationAcceptance = "NotRun", FramesProduced = finalDiagnostics.Devices.Sum(device => device.FramesProduced)
            }));
        }
        catch (CameraSetupCheckException exception)
        {
            // Keep the first bounded failure reason even if Runtime shutdown
            // subsequently reaches the process deadline while draining a provider.
            Console.Error.WriteLine("V117 stage=CheckFailed reason=" + exception.ReasonCode);
            throw;
        }
        finally { window.Close(); }
    }

    private static async Task QueryCore(ProductionStoreOptions options, string directory, string userName,
        string expectedPrincipal, string password)
    {
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "camera-setup-evidence.json")));
        using var clock = new VirtualCameraClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var camera = CreateProvider(clock);
        await using var container = Services(options, camera).BuildServiceProvider();
        var runtime = container.GetRequiredService<IStationRuntime>();
        var sessions = container.GetRequiredService<IInteractiveSessionService>();
        await Verified(runtime);
        await SignIn(sessions, runtime, userName, expectedPrincipal, password);
        var databaseBefore = await DatabaseHash(options.DatabasePath);
        var result = await container.GetRequiredService<ICameraSetupRuntime>().GetSetupAsync(Role, Invocation(sessions));
        Require(databaseBefore == await DatabaseHash(options.DatabasePath), "CameraSetupQueryChangedDatabase");
        Require(result.Available && result.Snapshot?.Binding is not null, "CameraSetupRestartBindingUnavailable");
        var snapshot = result.Snapshot!;
        Require(snapshot.Binding!.RevisionHash == evidence.RootElement.GetProperty("BindingRevisionHash").GetString() &&
            snapshot.Binding.Revision == evidence.RootElement.GetProperty("BindingRevision").GetInt64() &&
            snapshot.Binding.Target.StableDeviceIdentity == DeviceA,
            "CameraSetupRestartBindingChanged");
        Require(snapshot.Health.Connection == CameraConnectionState.Closed &&
            snapshot.Health.Configuration != CameraConfigurationState.Applied &&
            camera.GetDiagnostics().Devices.All(device => device.OpenCount == 0),
            "CameraSetupRestartInheritedDeviceConfiguration");
        await AssertDisarmed(runtime);
        await File.WriteAllTextAsync(Path.Combine(directory, "camera-setup-restart.json"), JsonSerializer.Serialize(new
        {
            Result = "Pass", snapshot.Binding.Revision, snapshot.Binding.RevisionHash, ExactBinding = true,
            OpenedDevices = 0, ConfigurationNotInherited = true, SetupQueryReadOnly = true, ProductionReady = false,
            RecipeActivation = "NotRun", PhysicalDevices = "NotRun"
        }));
    }

    private static VirtualCameraProvider CreateProvider(VirtualCameraClock clock)
    {
        var capabilities = new CameraCapabilities(new[] { ProductionAcquisitionMode.SoftwareTrigger },
            new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
            new(10, 10000, 1, CameraQuantizationMode.Nearest, 0.5),
            new(0, 24, 1, CameraQuantizationMode.Exact), new(0, 10000, 1, CameraQuantizationMode.Exact),
            new(128, 96, new(0, 127, 1), new(0, 95, 1), new(1, 128, 1), new(1, 96, 1)));
        var image = VirtualCameraImage.CreateSynthetic("Setup", 64, 48, VisionPixelFormat.Mono8, null, 117);
        var scenarios = new[] {
            new VirtualCameraScenario("Setup-A", "1", 117, DeviceA, capabilities, new[] { image },
                Array.Empty<VirtualCameraAcquisitionPlan>(), new[] {
                    new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero),
                    new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.WriteFailure, TimeSpan.Zero),
                    new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero) }),
            new VirtualCameraScenario("Setup-B", "1", 117, DeviceB, capabilities, new[] { image },
                Array.Empty<VirtualCameraAcquisitionPlan>()) };
        return new(scenarios, clock);
    }

    private static async Task InvokeMutation(CameraSetupPanel panel, CameraSetupViewModel model,
        string buttonName, string password)
    {
        var previous = model.LastOperationResult;
        ((PasswordBox)panel.FindName("StepUpPasswordBox")).Password = password;
        InvokeButton(panel, buttonName);
        await Until(() => !model.IsBusy && (!ReferenceEquals(previous, model.LastOperationResult) || model.ErrorCode is not null));
        Require(((PasswordBox)panel.FindName("StepUpPasswordBox")).Password.Length == 0, "CameraSetupPasswordRetained");
    }

    private static CommandInvocation Invocation(IInteractiveSessionService sessions) =>
        new(CommandSource.PhysicalConsole, sessions.Current.PrincipalId, sessions.Current.SessionId);

    private static async Task SignIn(IInteractiveSessionService sessions, IStationRuntime runtime,
        string userName, string principal, string password)
    {
        var signedIn = await sessions.SignInAsync(new(userName, password));
        Require(signedIn.Succeeded && signedIn.Identity?.PrincipalId.ToString("D") == principal,
            "CameraSetupConsumerAuthenticationFailed");
        await Verified(runtime);
        await AssertDisarmed(runtime);
    }

    private static async Task AssertDisarmed(IStationRuntime runtime)
    {
        var state = await runtime.GetSnapshotAsync();
        Require(!state.Ready && !state.Busy && state.ArmState == ProductionArmState.Disarmed && state.ActiveRecipe is null,
            "CameraSetupGrantedProductionAuthority");
    }

    private static async Task Verified(IStationRuntime runtime)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var state = (await runtime.GetSnapshotAsync()).AuditIntegrity?.State;
            if (state == AuditIntegrityState.Verified) return;
            Require(state != AuditIntegrityState.Faulted && watch.Elapsed < TimeSpan.FromSeconds(20), "CameraSetupAuditUnavailable");
            await Task.Delay(20);
        }
    }

    private static void SetInput(DependencyObject root, object context, string property, string value)
    {
        var control = Descendants<TextBox>(root).Single(box => ReferenceEquals(box.DataContext, context) &&
            box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == property);
        control.Text = value;
        control.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
    }

    private static void InvokeButton(CameraSetupPanel panel, string name)
    {
        var button = (Button)panel.FindName(name);
        Require(button.IsEnabled, "CameraSetupButtonUnavailable_" + name);
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static async Task Until(Func<bool> complete)
    {
        var watch = Stopwatch.StartNew();
        do { await Task.Delay(20); Require(watch.Elapsed < TimeSpan.FromSeconds(25), "CameraSetupUiWaitTimeout"); }
        while (!complete());
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void SaveWindow(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),
            (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static void SaveReadBack(Window window, CameraSetupPanel panel, string path)
    {
        var scroll = (ScrollViewer)panel.FindName("SetupScrollViewer");
        scroll.ScrollToEnd();
        window.UpdateLayout();
        SaveWindow(window, path);
        scroll.ScrollToTop();
        window.UpdateLayout();
    }

    private static async Task<string> DatabaseHash(string path)
    {
        var hashes = new List<string>();
        foreach (var file in new[] { path, path + "-wal" })
        {
            if (!File.Exists(file)) { hashes.Add("Absent"); continue; }
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var sha = SHA256.Create();
            hashes.Add(Convert.ToHexString(await sha.ComputeHashAsync(stream)));
        }
        return string.Join(":", hashes);
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

    private static void Require(bool condition, string reason)
    { if (!condition) throw new CameraSetupCheckException(reason); }

    private sealed class CameraSetupCheckException : Exception
    {
        internal CameraSetupCheckException(string reasonCode) => ReasonCode = reasonCode;
        internal string ReasonCode { get; }
    }
}
