using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Deployment;

/// <summary>
/// Machine-wide exclusion for one station installation. The lock is a named kernel mutex
/// (<c>Global\SharpInspect.StationDeployment.&lt;IdentityHash&gt;</c>) created with one protected DACL
/// for the configured runtime and coordinator identities plus LocalSystem and
/// BuiltinAdministrators; an existing object whose stored ACL or owner is not exactly that is
/// refused, never repaired. Win32 mutex ownership is thread-affine, so a dedicated background
/// thread owns the mutex for the whole lifetime of the lock. The mutex is not reentrant for this
/// process: a second acquisition waits on the named object and fails on its bounded timeout. An
/// abandoned object reports <see cref="WasAbandoned"/> as exclusion information only; it never
/// authorizes maintenance, repair or configuration changes by itself.
/// </summary>
public sealed class StationDeploymentLock : IDisposable
{
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitAbandoned = 0x00000080;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const int AcquisitionGraceMilliseconds = 5000;
    private const int ReleaseGraceMilliseconds = 30000;

    private readonly StationInstallationOptions _options;
    private readonly StationDeploymentOwner _owner;
    private readonly SafeWaitHandle _handle;
    private readonly ManualResetEventSlim _release = new(false);
    private readonly TaskCompletionSource<bool> _acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _holder;
    private volatile bool _isHeld;
    private volatile bool _wasAbandoned;
    private int _retained;
    private int _disposed;

    private StationDeploymentLock(StationInstallationOptions options, StationDeploymentOwner owner,
        SafeWaitHandle handle)
    {
        _options = options;
        _owner = owner;
        _handle = handle;
        OwnershipId = Guid.NewGuid();
    }

    /// <summary>The exact installation this lock excludes.</summary>
    public StationInstallationOptions Options => _options;

    /// <summary>The deployment role that asked for this acquisition.</summary>
    public StationDeploymentOwner Owner => _owner;

    /// <summary>Identity of this acquisition, for diagnostics and audit correlation only.</summary>
    public Guid OwnershipId { get; }

    /// <summary>True while the named mutex is owned by this lock's dedicated holder thread.</summary>
    public bool IsHeld => _isHeld;

    /// <summary>True when the wait found an abandoned object, meaning the previous owner ended
    /// without releasing. This is exclusion information only, never a maintenance authorization.</summary>
    public bool WasAbandoned => _wasAbandoned;

    /// <summary>Acquires the machine-wide station deployment lock. The installation registry is
    /// validated first; a missing or conflicting installation never reaches the mutex. The wait is
    /// bounded: on timeout no acquisition, holder thread or handle remains behind.</summary>
    public static StationDeploymentLock Acquire(StationInstallationOptions options, StationDeploymentOwner owner,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (owner is not (StationDeploymentOwner.Runtime or StationDeploymentOwner.Coordinator))
            throw new ArgumentOutOfRangeException(nameof(owner), "StationDeploymentOwnerInvalid");
        if (timeout < TimeSpan.FromMilliseconds(1) || timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(timeout), "StationDeploymentLockTimeoutOutOfRange");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("StationDeploymentLockWindowsRequired");

        _ = StationInstallationRegistry.RequireBinding(options);
        var identities = StationDeploymentOwnership.DeploymentIdentities(options);
        var handle = StationDeploymentOwnership.CreateOrOpenProtectedMutex(
            StationDeploymentOwnership.MutexName(options.IdentityHash), identities);
        return new StationDeploymentLock(options, owner, handle).Start(timeout);
    }

    /// <summary>Retains the mutex until the process exits. Once set, Dispose never releases and the
    /// dedicated holder thread stays blocked until the process ends; the operating system then
    /// releases the object as abandoned. Call only while the lock is held and before Dispose.</summary>
    internal void RetainUntilProcessExit()
    {
        ThrowIfDisposed();
        if (!IsHeld) throw new InvalidOperationException("StationDeploymentLockNotHeld");
        Interlocked.Exchange(ref _retained, 1);
    }

    /// <summary>Releases the mutex through its owning thread. Idempotent. After
    /// <see cref="RetainUntilProcessExit"/> this call is a no-op and the lock stays held until the
    /// process exits.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (Volatile.Read(ref _retained) != 0) return;

        _release.Set();
        var holder = _holder;
        if (holder is not null && !holder.Join(TimeSpan.FromMilliseconds(ReleaseGraceMilliseconds)))
            return; // Fail closed: keep the owning thread and handle instead of corrupting ownership.
        _isHeld = false;
        _handle.Dispose();
        _release.Dispose();
    }

    private StationDeploymentLock Start(TimeSpan timeout)
    {
        _holder = new Thread(() => Hold(timeout))
        {
            IsBackground = true,
            Name = "SharpInspect.StationDeploymentLock"
        };
        _holder.Start();

        var wait = (int)Math.Min(timeout.TotalMilliseconds + AcquisitionGraceMilliseconds, int.MaxValue);
        if (!_acquired.Task.Wait(wait))
        {
            Discard();
            throw new TimeoutException("StationDeploymentLockTimeout");
        }

        bool acquired;
        try
        {
            acquired = _acquired.Task.GetAwaiter().GetResult();
        }
        catch
        {
            Discard();
            throw;
        }

        if (!acquired)
        {
            Discard();
            throw new TimeoutException("StationDeploymentLockTimeout");
        }

        _isHeld = true;
        return this;
    }

    private void Hold(TimeSpan timeout)
    {
        uint result;
        try
        {
            result = WaitForSingleObject(_handle, (uint)timeout.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _acquired.TrySetException(ex);
            return;
        }

        if (result == WaitFailed)
        {
            _acquired.TrySetException(new InvalidOperationException("StationDeploymentLockWaitFailed",
                new Win32Exception(Marshal.GetLastWin32Error())));
            return;
        }

        if (result == WaitTimeout)
        {
            _acquired.TrySetResult(false);
            return;
        }

        if (result != WaitObject0 && result != WaitAbandoned)
        {
            _acquired.TrySetException(new InvalidOperationException("StationDeploymentLockWaitFailed"));
            return;
        }

        _wasAbandoned = result == WaitAbandoned;
        _acquired.TrySetResult(true);

        _release.Wait();
        if (!ReleaseMutex(_handle))
        {
            // The holder thread is about to exit; Windows abandons the mutex so the next acquirer
            // still observes exclusion (as WasAbandoned) instead of a silently free lock.
            _ = Marshal.GetLastWin32Error();
        }
    }

    private void Discard()
    {
        var holder = _holder;
        if (holder is not null)
        {
            _release.Set();
            if (!holder.Join(TimeSpan.FromMilliseconds(ReleaseGraceMilliseconds)))
                return; // Fail closed: never close a handle that a live thread may still own.
        }

        _handle.Dispose();
        _release.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(StationDeploymentLock));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseMutex(SafeWaitHandle handle);
}
