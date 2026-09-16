using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>Fatal process boundary. A handled Dispatcher exception always ends in shutdown/exit.</summary>
public sealed class ManagedFaultShutdown : IDisposable
{
    private readonly Application _application;
    private readonly IManagedFaultBoundary _boundary;
    private readonly TimeSpan _maximumWait;
    private readonly Action<int> _exit;
    private int _started, _disposed, _exited;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ManagedFaultShutdown(Application application, IManagedFaultBoundary boundary, TimeSpan maximumShutdownWait)
        : this(application, boundary, maximumShutdownWait, TerminateCurrentProcess) { }

    internal ManagedFaultShutdown(Application application, IManagedFaultBoundary boundary, TimeSpan maximumShutdownWait, Action<int> exit)
    {
        if (maximumShutdownWait <= TimeSpan.Zero || maximumShutdownWait > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(maximumShutdownWait));
        _application = application; _boundary = boundary; _maximumWait = maximumShutdownWait; _exit = exit;
        application.DispatcherUnhandledException += DispatcherFault;
        AppDomain.CurrentDomain.UnhandledException += DomainFault;
    }
    public bool ShutdownStarted => Volatile.Read(ref _started) != 0;

    public void Begin(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // Physical shutdown ownership is retained if the caller wait expires.
                var shutdown = Task.Run(async () => await _boundary.HandleAsync(exception).ConfigureAwait(false));
                try { await shutdown.WaitAsync(_maximumWait).ConfigureAwait(false); }
                catch (Exception failure) when (failure is not OutOfMemoryException) { }
                // Do not begin normal WPF/CLR process-exit callbacks after a fatal fault.
            }
            finally { _completion.TrySetResult(true); ExitOnce(); }
        });
        // Optional UI callbacks must not prevent the owned shutdown worker from starting.
        try
        {
            if (_application.Dispatcher.CheckAccess())
                foreach (Window window in _application.Windows) window.IsEnabled = false;
            else
            {
                try { _application.Dispatcher.BeginInvoke(new Action(() =>
                    { foreach (Window window in _application.Windows) window.IsEnabled = false; }), DispatcherPriority.Send); }
                catch (Exception failure) when (failure is not OutOfMemoryException) { }
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException) { }
    }

    private void DispatcherFault(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        Begin(args.Exception);
        // Only keeps the Dispatcher alive for controlled close. Begin has already disabled
        // operator input and latched irrevocable shutdown; this is never a resume path.
        args.Handled = true;
    }
    private void DomainFault(object sender, UnhandledExceptionEventArgs args)
    {
        try
        {
            Begin(args.ExceptionObject as Exception ?? new InvalidOperationException("ManagedUnhandledFault"));
            try { _completion.Task.Wait(_maximumWait + TimeSpan.FromSeconds(1)); }
            catch (Exception failure) when (failure is not OutOfMemoryException) { }
        }
        finally
        {
            // An in-progress exit on another thread is not proof that this handler may
            // return: the CLR would print the original unclassified exception. Shutdown
            // remains once-latched, but every unhandled thread terminates synchronously.
            _exit(1);
        }
    }
    private void ExitOnce() { if (Interlocked.Exchange(ref _exited, 1) == 0) _exit(1); }
    private static void TerminateCurrentProcess(int exitCode)
    {
        // This is only the terminal step after a fatal, bounded Runtime shutdown attempt.
        // Self-termination does not return on supported Windows and runs no ProcessExit
        // or DLL detach callbacks. Normal application close never uses this path.
        _ = TerminateProcess(GetCurrentProcess(), unchecked((uint)exitCode));
        // An unexpected OS failure is outside the in-process termination guarantee.
        // Stay terminal for an external supervisor instead of resuming or dumping data.
        Thread.Sleep(Timeout.Infinite);
    }
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _application.DispatcherUnhandledException -= DispatcherFault;
        AppDomain.CurrentDomain.UnhandledException -= DomainFault;
    }
}
