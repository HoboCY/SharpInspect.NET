using System.Runtime.InteropServices;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Cameras.Hikrobot;

/// <summary>Unpackaged probe-only native runtime; no application-facing factory exposes it.</summary>
internal sealed unsafe class HikrobotNativeRuntime : IHikrobotSdkRuntime
{
    private readonly HikrobotNativeApi _api;
    private readonly object _gate = new();
    private HikrobotNativeDevice? _device;
    private IntPtr _pendingRetirementHandle;
    private bool _pendingHandleClosed;
    private bool _disposed;

    internal HikrobotNativeRuntime(string libraryPath) => _api = new(libraryPath);
    internal HikrobotNativeRuntime(HikrobotNativeApi managedAbiProbe) => _api = managedAbiProbe;
    public string RuntimeVersion => _api.RuntimeVersion;

    public IReadOnlyList<HikrobotSdkDescriptor> Discover()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return Enumerate().Select(item => item.Descriptor).ToArray();
        }
    }

    public IHikrobotSdkDevice Open(string stableDeviceIdentity)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_pendingRetirementHandle != IntPtr.Zero)
                throw new HikrobotSdkException("HikrobotNativeCleanupUnresolved");
            if (_device is { IsDisposed: false }) throw new HikrobotSdkException("HikrobotDeviceAlreadyOwned");
            var match = Enumerate().SingleOrDefault(item => item.Descriptor.StableIdentity == stableDeviceIdentity)
                ?? throw new HikrobotSdkException("HikrobotDeviceMissing");
            IntPtr handle = IntPtr.Zero;
            var createdSuccessfully = false;
            var openedSuccessfully = false;
            try
            {
                var created = _api.CreateHandle(out handle, match.Pointer);
                HikrobotNativeApi.Check(created, "HikrobotCreateFailed");
                if (handle == IntPtr.Zero) throw new HikrobotSdkException("HikrobotInvalidNativeHandle");
                _api.RetainHandle();
                createdSuccessfully = true;
                // Exclusive access; this neither changes the camera IP nor configures the host NIC.
                HikrobotNativeApi.Check(_api.OpenDevice(handle, 1, 0), "HikrobotOpenFailed");
                openedSuccessfully = true;
                _device = new HikrobotNativeDevice(_api, handle, match.Descriptor);
                handle = IntPtr.Zero;
                return _device;
            }
            finally
            {
                if (createdSuccessfully && handle != IntPtr.Zero)
                {
                    _pendingRetirementHandle = handle;
                    // Close is valid only after a successful Open. A created but
                    // unopened handle goes directly to Destroy, with retries still
                    // retaining ownership if actual destruction fails.
                    _pendingHandleClosed = !openedSuccessfully;
                    RetirePendingHandle();
                }
            }
        }
    }

    private List<Discovery> Enumerate()
    {
        HikrobotDeviceList list = default;
        // Only physical GigE and USB3 Vision cameras, never virtual devices or frame grabbers.
        HikrobotNativeApi.Check(_api.EnumDevices(1 | 4, (IntPtr)(&list)), "HikrobotDiscoveryFailed");
        if (list.Count > 64) throw new HikrobotSdkException("HikrobotDiscoveryCapacityExceeded");
        var discovered = new List<Discovery>((int)list.Count);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < list.Count; index++)
        {
            var pointer = (IntPtr)list.Devices[index];
            if (pointer == IntPtr.Zero) throw new HikrobotSdkException("HikrobotDiscoveryRecordInvalid");
            var type = unchecked((uint)Marshal.ReadInt32(pointer, 12));
            var info = pointer + 32;
            var descriptor = type switch
            {
                1 => new HikrobotSdkDescriptor("GigE:" + ReadText(info + 164, 16),
                    ReadOptionalText(info + 52, 32), ReadOptionalText(info + 84, 32)),
                4 => new HikrobotSdkDescriptor("Usb3:" + ReadText(info + 396, 64),
                    ReadOptionalText(info + 140, 64), ReadOptionalText(info + 268, 64)),
                _ => throw new HikrobotSdkException("HikrobotTransportUnsupported")
            };
            if (!IsStableIdentity(descriptor.StableIdentity) || !identities.Add(descriptor.StableIdentity))
                throw new HikrobotSdkException("HikrobotDiscoveryIdentityInvalid");
            discovered.Add(new(descriptor, pointer));
        }
        return discovered;
    }

    internal static string ReadText(IntPtr pointer, int capacity)
    {
        var bytes = new ReadOnlySpan<byte>((void*)pointer, capacity);
        var end = bytes.IndexOf((byte)0);
        if (end < 1) throw new HikrobotSdkException("HikrobotNativeTextInvalid");
        bytes = bytes[..end];
        foreach (var value in bytes)
            if (value is < 32 or > 126) throw new HikrobotSdkException("HikrobotNativeTextInvalid");
        return Encoding.ASCII.GetString(bytes);
    }

    private static bool IsStableIdentity(string value) => value.Length <= 128 &&
        value.Split(':').Length == 2 && value[(value.IndexOf(':') + 1)..].Length > 0 &&
        value.All(item => item is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or ':' or '-' or '_' or '.');

    internal static string? ReadOptionalText(IntPtr pointer, int capacity)
    {
        var bytes = new ReadOnlySpan<byte>((void*)pointer, capacity);
        var end = bytes.IndexOf((byte)0);
        if (end < 1) return null;
        try
        {
            var value = new UTF8Encoding(false, true).GetString(bytes[..end]);
            return string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ? null : value;
        }
        catch (DecoderFallbackException) { return null; }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new HikrobotSdkException("HikrobotRuntimeDisposed");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _device?.Dispose();
            RetirePendingHandle();
            _api.Dispose();
            _disposed = true;
        }
    }

    private void RetirePendingHandle()
    {
        if (_pendingRetirementHandle == IntPtr.Zero) return;
        if (!_pendingHandleClosed)
        {
            HikrobotNativeApi.Check(_api.CloseDevice(_pendingRetirementHandle), "HikrobotOpenCloseFailed");
            _pendingHandleClosed = true;
        }
        HikrobotNativeApi.Check(_api.DestroyHandle(_pendingRetirementHandle), "HikrobotOpenCleanupFailed");
        _api.ReleaseHandle();
        _pendingRetirementHandle = IntPtr.Zero;
        _pendingHandleClosed = false;
    }

    private sealed record Discovery(HikrobotSdkDescriptor Descriptor, IntPtr Pointer);
}
