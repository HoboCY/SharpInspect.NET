using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Wpf;

namespace SharpInspect.SampleHost;

/// <summary>Independent process development probe; never configures or accepts production work.</summary>
internal static class DiagnosticFatalProbe
{
    internal static int Run(string outputPath, string mode)
    {
        if (!Path.IsPathFullyQualified(outputPath) || mode is not ("dispatcher" or "domain" or "hang" or "domain-race" or "process-exit")) return 2;
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var runtime = new StationRuntime(TimeSpan.FromMilliseconds(25));
        var boundary = new ProbeBoundary(runtime, outputPath, mode);
        using var firstExit = new ManualResetEventSlim(false);
        var exitCalls = 0;
        Action<int> racingExit = code =>
        {
            if (Interlocked.Increment(ref exitCalls) == 1)
            {
                File.WriteAllText(outputPath + ".exit-started", "1");
                firstExit.Set();
                // Retain the first physical exit call after its once latch. A real unhandled
                // thread must still terminate directly instead of returning to the CLR.
                Thread.Sleep(Timeout.Infinite);
            }
            File.WriteAllText(outputPath + ".exit-calls", exitCalls.ToString(System.Globalization.CultureInfo.InvariantCulture));
            typeof(ManagedFaultShutdown).GetMethod("TerminateCurrentProcess", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { code });
        };
        // Only H02 injects scheduling through the existing internal test constructor. The
        // three H01 modes consume the public API from an ordinary non-friend assembly.
        using var shutdown = mode == "domain-race"
            ? (ManagedFaultShutdown)Activator.CreateInstance(typeof(ManagedFaultShutdown),
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                args: new object[] { application, boundary, TimeSpan.FromMilliseconds(750), racingExit }, culture: null)!
            : new ManagedFaultShutdown(application, boundary, TimeSpan.FromMilliseconds(750));
        if (mode == "process-exit") AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            File.WriteAllText(outputPath + ".process-exit", "entered");
            throw new InvalidOperationException("SECRET-BAIT-process-exit");
        };
        application.Startup += (_, _) =>
        {
            if (mode == "domain-race")
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    if (!firstExit.Wait(TimeSpan.FromSeconds(5))) Environment.Exit(3);
                    File.WriteAllText(outputPath + ".domain-raised", "1");
                    throw new InvalidOperationException("SECRET-BAIT-fatal-race");
                });
                shutdown.Begin(new InvalidOperationException("SECRET-BAIT-first"));
            }
            else if (mode == "domain") ThreadPool.QueueUserWorkItem(_ => throw new InvalidOperationException("SECRET-BAIT-fatal"));
            else application.Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("SECRET-BAIT-fatal")));
        };
        var result = application.Run();
        if (mode == "domain-race")
        {
            // Application.Shutdown precedes the exit callback; keep the main thread alive
            // until the controlled process termination, bounded by the parent harness.
            Thread.Sleep(TimeSpan.FromSeconds(10));
            return 3;
        }
        return result;
    }

    private sealed class ProbeBoundary : IManagedFaultBoundary
    {
        private readonly StationRuntime _runtime;
        private readonly string _output, _mode;
        private int _calls;
        internal ProbeBoundary(StationRuntime runtime, string output, string mode)
        { _runtime = runtime; _output = output; _mode = mode; }
        public async ValueTask HandleAsync(Exception exception, CancellationToken cancellationToken = default)
        {
            var calls = Interlocked.Increment(ref _calls);
            await _runtime.HandleAsync(exception, cancellationToken).ConfigureAwait(false);
            var state = await _runtime.GetSnapshotAsync().ConfigureAwait(false);
            File.WriteAllText(_output, JsonSerializer.Serialize(new
            {
                VerificationId = _mode == "domain-race" ? "V156_H02" : _mode == "process-exit" ? "V156_H03" : "V156_H01", Mode = _mode, Calls = calls, ControlledShutdownObserved = true,
                Lifecycle = state.Lifecycle.ToString(), state.Ready, Arm = state.ArmState.ToString(),
                ProductionConfigured = false, ProductionAcceptance = "NotRun"
            }));
            if (_mode == "hang") await new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task.ConfigureAwait(false);
        }
    }
}
