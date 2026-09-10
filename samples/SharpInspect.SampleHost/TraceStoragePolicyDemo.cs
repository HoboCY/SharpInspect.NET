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
using SharpInspect.Runtime;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.StoragePolicies;
using SharpInspect.Wpf;

namespace SharpInspect.SampleHost;

internal static class TraceStoragePolicyDemo
{
    internal static TraceStoragePolicyStoreOptions StoreOptions() => new()
    { DeploymentScope = new("Sample.TraceStorage.Deployment", "1", Array.Empty<TraceStorageRouteIdentity>()) };

    internal static int Run(ProductionStoreOptions options, string directory, string? userName, string? expectedPrincipal)
    {
        try
        {
            var password = JsonSerializer.Deserialize<string>(Console.ReadLine() ?? "null") ??
                throw new InvalidOperationException("TraceStorageConsumerPasswordRequired");
            Pump(() => RunAsync(options, Path.GetFullPath(directory), userName!, expectedPrincipal!, password));
            Console.WriteLine("V139_N01 trace-storage-policy PASS uiPublished=true versions=2 oldSnapshotUnchanged=true ready=false");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("TraceStorageConsumerFailed: " + exception.GetType().Name + ": " + exception.Message);
            Console.Error.WriteLine(exception.StackTrace);
            return 1;
        }
    }

    internal static int Query(ProductionStoreOptions options, string directory)
    {
        try
        {
            QueryAsync(options, Path.GetFullPath(directory)).GetAwaiter().GetResult();
            Console.WriteLine("V139_N02 trace-storage-policy-query PASS readOnly=true databaseUnchanged=true oldSnapshotUnchanged=true");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("TraceStorageConsumerQueryFailed: " + exception.GetType().Name + ": " + exception.Message);
            return 1;
        }
    }

    private static async Task RunAsync(ProductionStoreOptions options, string directory, string userName,
        string expectedPrincipal, string password)
    {
        Require(File.Exists(options.DatabasePath), "TraceStorageConsumerRequiresPreparedIdentity");
        Directory.CreateDirectory(directory);
        var services = new ServiceCollection();
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(500));
        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IStationRuntime>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while ((await runtime.GetSnapshotAsync(timeout.Token)).AuditIntegrity?.State != AuditIntegrityState.Verified)
            await Task.Delay(20, timeout.Token);
        var sessions = provider.GetRequiredService<IInteractiveSessionService>();
        var signedIn = await sessions.SignInAsync(new(userName, password), timeout.Token);
        Require(signedIn.Succeeded && sessions.Current.PrincipalId == expectedPrincipal, "TraceStorageConsumerSignInFailed");
        var policy = provider.GetRequiredService<ITraceStoragePolicyService>();
        await using var vm = new TraceStoragePolicyViewModel(policy, sessions,
            provider.GetRequiredService<IStepUpAuthentication>(), new DispatcherUiDispatcher());
        var panel = new TraceStoragePolicyPanel { DataContext = vm };
        var window = new Window { Content = new ScrollViewer { Content = panel }, Width = 1200, Height = 900,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        window.Show();
        try
        {
            Require(!vm.Editor.TryBuild(out _, out var blankErrors) && blankErrors.Count > 0, "TraceStorageEditorHasImplicitDefaults");
            await vm.RefreshAsync(timeout.Token);
            Require(vm.Current is null, "TraceStorageConsumerRequiresNoPublishedPolicy");
            var first = await PublishThroughPanel(panel, vm, ExamplePolicy("1", 30), password, timeout.Token);
            var second = await PublishThroughPanel(panel, vm, ExamplePolicy("2", 1), password, timeout.Token);
            var old = await policy.ReadAsync(1, timeout.Token);
            Require(old.Available && old.Snapshot!.ContentHash == first.Snapshot!.ContentHash,
                "TraceStorageConsumerOldSnapshotChanged");
            var station = await runtime.GetSnapshotAsync(timeout.Token);
            Require(!station.Ready && !vm.Preflight!.CanAdmitProduction, "TraceStorageConsumerGrantedProduction");
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1200, 900, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var image = File.Create(Path.Combine(directory, "trace-storage-policy-editor.png"))) encoder.Save(image);
            await Write(directory, "trace-storage-policy-evidence.json", new
            {
                CaseId = "V139_N01", Result = "Pass", UiPublished = true, PublishedVersions = 2,
                ConsumerSha256 = HashFile(typeof(TraceStoragePolicyDemo).Assembly.Location),
                FirstPublicationHash = first.Publication!.ContentHash, FirstSnapshotHash = first.Snapshot!.ContentHash,
                SecondPublicationHash = second.Publication!.ContentHash, SecondSnapshotHash = second.Snapshot!.ContentHash,
                OldSnapshotUnchanged = true, ProductionReady = station.Ready, PreflightCanAdmit = vm.Preflight!.CanAdmitProduction,
                NoImplicitEditorDefaults = true, RealProductionQualification = "NotRun"
            });
        }
        finally { panel.Deactivate(); window.Close(); password = string.Empty; }
    }

    private static async Task<TraceStoragePolicyResult> PublishThroughPanel(TraceStoragePolicyPanel panel,
        TraceStoragePolicyViewModel vm, TraceStoragePolicyDefinition definition, string password, CancellationToken token)
    {
        vm.Editor.Load(definition);
        ((TextBox)panel.FindName("ReasonTextBox")).Text = "Controlled UI acceptance; values are not production approval";
        ((PasswordBox)panel.FindName("StepUpPasswordBox")).Password = password;
        var button = (Button)panel.FindName("PublishButton");
        Require(button.IsEnabled, "TraceStorageConsumerPublishButtonUnavailable");
        var previous = vm.LastResult;
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
        while (ReferenceEquals(vm.LastResult, previous) || vm.IsBusy) await Task.Delay(20, token);
        Require(vm.LastResult!.Succeeded, vm.LastResult.Outcome.ReasonCode);
        Require(((PasswordBox)panel.FindName("StepUpPasswordBox")).Password.Length == 0, "TraceStorageConsumerPasswordNotCleared");
        var result = vm.LastResult;
        await vm.RefreshAsync(token);
        return result;
    }

    private static async Task QueryAsync(ProductionStoreOptions options, string directory)
    {
        var before = ObserveSqliteArtifacts(options.DatabasePath);
        var query = new SqliteTraceStoragePolicyQuery(options);
        var first = await query.ReadAsync(1);
        var current = await query.ReadAsync();
        Require(first.Available && current.Available && current.Publication?.Version == 2, "TraceStorageConsumerColdHistoryInvalid");
        using var original = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "trace-storage-policy-evidence.json")));
        Require(first.Snapshot!.ContentHash == original.RootElement.GetProperty("FirstSnapshotHash").GetString(), "TraceStorageConsumerColdSnapshotChanged");
        Require(current.Snapshot!.ContentHash == original.RootElement.GetProperty("SecondSnapshotHash").GetString(), "TraceStorageConsumerColdCurrentChanged");
        var after = ObserveSqliteArtifacts(options.DatabasePath);
        var mainDatabaseUnchanged = before["Database"] == after["Database"];
        Require(mainDatabaseUnchanged, "TraceStorageConsumerColdReadWroteDatabase");
        await Write(directory, "trace-storage-policy-restart.json", new
        {
            CaseId = "V139_N02", Result = "Pass", ReadOnly = true, WriterStarted = false,
            WriterObservation = "Static query path constructs only SqliteTraceStoragePolicyQuery; no SqliteCommandStore",
            DatabaseUnchanged = mainDatabaseUnchanged, MainDatabaseUnchanged = mainDatabaseUnchanged,
            SqliteArtifactSetUnchanged = before.SequenceEqual(after),
            SqliteArtifactsBefore = before, SqliteArtifactsAfter = after, OldSnapshotUnchanged = true
        });
    }

    private static IReadOnlyDictionary<string, string?> ObserveSqliteArtifacts(string path)
    {
        var result = new Dictionary<string, string?>();
        foreach (var (name, suffix) in new[] { ("Database", ""), ("Wal", "-wal"), ("Shm", "-shm"), ("Journal", "-journal") })
        {
            try { result.Add(name, HashFile(path + suffix)); }
            catch (FileNotFoundException) { result.Add(name, null); }
        }
        return result;
    }

    private static TraceStoragePolicyDefinition ExamplePolicy(string version, int days) => new("Sample.TraceStorage", version,
        "Controlled consumer verification", "1", "Explicit non-production test values",
        Enum.GetValues<TraceRetentionClass>().Select(kind => new TraceRetentionRule(kind,
            kind == TraceRetentionClass.CompletedExport ? RetentionStartEvent.ExportCompleted : RetentionStartEvent.ArtifactCreated,
            TimeSpan.FromDays(days))), 1024 * 1024, 1, Array.Empty<TraceStorageRouteLimit>(),
        new(100, 16 * 1024 * 1024, TimeSpan.FromHours(1)), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
        new(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1), 1024 * 1024, 100),
        new(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100), 1024 * 1024, 100), 64 * 1024 * 1024);

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

    private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static Task Write(string directory, string name, object value) => File.WriteAllTextAsync(Path.Combine(directory, name),
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}
