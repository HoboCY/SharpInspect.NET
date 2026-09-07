using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Wpf;

namespace SharpInspect.SampleHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var smoke = args.Contains("--smoke", StringComparer.OrdinalIgnoreCase);
        var screenshotIndex = Array.IndexOf(args, "--screenshot");
        var screenshot = screenshotIndex >= 0 && screenshotIndex + 1 < args.Length
            ? Path.GetFullPath(args[screenshotIndex + 1]) : null;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var services = new ServiceCollection();
        services.AddSharpInspectRuntime(TimeSpan.FromMilliseconds(500));
        services.AddSingleton<StationShellViewModel>(p => new StationShellViewModel(
            p.GetRequiredService<IStationRuntime>(), new DispatcherUiDispatcher(app.Dispatcher)));
        var provider = services.BuildServiceProvider();
        var vm = provider.GetRequiredService<StationShellViewModel>();
        var runtime = provider.GetRequiredService<IStationRuntime>();
        var window = new ShellWindow(vm);
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
            window.Show();
            try
            {
                await vm.StartAsync();
                if (smoke) await RunSmokeAsync(window, vm, runtime, screenshot);
            }
            catch (Exception exception)
            {
                exitCode = 1;
                if (smoke) Console.Error.WriteLine($"SMOKE FAIL {exception.GetType().Name}: {exception.Message}");
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

    private static async Task RunSmokeAsync(ShellWindow window, StationShellViewModel vm,
        IStationRuntime runtime, string? screenshot)
    {
        var startup = await runtime.GetSnapshotAsync();
        Require(startup.Lifecycle == RuntimeLifecycle.Running && !startup.Ready &&
            startup.ArmState == ProductionArmState.Disarmed, "startup must remain disarmed");
        Console.WriteLine($"V101-P01 startup PASS epoch={startup.RuntimeEpoch} revision={startup.Revision}");
        var arm = await vm.ArmProductionAsync();
        Require(arm.Disposition == CommandDisposition.Rejected && arm.ReasonCode == "DeploymentPoliciesMissing", "unconfigured Arm rejection");
        Console.WriteLine($"V101-R02 consumer Arm PASS correlation={arm.CorrelationId} reason={arm.ReasonCode}");
        var stop = await vm.GracefulStopAsync();
        Require(stop.Disposition == CommandDisposition.Accepted && stop.ReasonCode == "StopAdmitted", "Stop admission");
        await WaitAsync(() => vm.State.LastCommand is { State: OperationState.Completed } p && p.CorrelationId == stop.CorrelationId);
        Console.WriteLine($"V101-U05 consumer Stop PASS admitted={stop.ReasonCode} final={vm.State.LastCommand!.ReasonCode}");
        await VerifyFeedAsync(startup);

        var before = await runtime.GetSnapshotAsync();
        var outcome = vm.LastCommandOutcome?.CorrelationId;
        vm.NavigateTo("Alarms");
        vm.NavigateTo("Production");
        if (screenshot is not null) RenderScreenshot(window, screenshot);
        window.Close();
        Require(window.IsPrivacyLocked && window.IsVisible, "ordinary Close must privacy-lock the existing window");
        var after = await runtime.GetSnapshotAsync();
        Require(after.Lifecycle == RuntimeLifecycle.Running && after.ArmState == before.ArmState &&
            after.LastCommand == before.LastCommand && vm.LastCommandOutcome?.CorrelationId == outcome,
            "navigation/ordinary Close must not submit commands or stop Runtime");
        Require(vm.CanStopProduction, "local Stop remains reachable while privacy locked");
        window.RevealPage();
        Require(!window.IsPrivacyLocked, "page reveal changes presentation only");
        Console.WriteLine("V101-U04 Close/navigation/privacy PASS runtime=Running");
        Console.WriteLine($"V101 CONSUMER SMOKE PASS runtime={Environment.Version} os={Environment.OSVersion.Version}");
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
        if (!condition) throw new InvalidOperationException(message);
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
