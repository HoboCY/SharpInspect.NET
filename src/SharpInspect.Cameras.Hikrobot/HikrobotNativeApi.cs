using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace SharpInspect.Cameras.Hikrobot;

/// <summary>
/// Fixed adapter-private C ABI. Only the separately built qualification executable can
/// enter this loader. Neither a successful load nor a development test admits production.
/// See docs/verification/hikrobot-sdk-sources.md for the source/version boundary.
/// </summary>
internal sealed class HikrobotNativeApi : IDisposable
{
    internal const string CandidateRuntimeVersion = "4.8.1.2";
    internal const uint NativeLoadFlags = 0x00000100 | 0x00000800; // DLL_LOAD_DIR | SYSTEM32
    private static int _activeRuntime;
    private static IntPtr _quarantinedModule;
    private IntPtr _module;
    private bool _initialized;
    private int _nativeHandles;
    private readonly bool _managedAbiProbe;
    private bool _disposed;
    private readonly NoArgs _finalize;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate uint VersionCall();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int EnumCall(uint layers, IntPtr list);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int CreateCall(out IntPtr handle, IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int HandleCall(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int OpenCall(IntPtr handle, uint access, ushort key);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    internal delegate int GetNodeCall(IntPtr handle, string name, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    internal delegate int SetIntCall(IntPtr handle, string name, long value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    internal delegate int SetEnumCall(IntPtr handle, string name, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    internal delegate int SetSymbolCall(IntPtr handle, string name, string value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    internal delegate int SetFloatCall(IntPtr handle, string name, float value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    internal delegate int CommandCall(IntPtr handle, string name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int RegisterCall(IntPtr handle, ImageCallback? callback, IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void ImageCallback(IntPtr data, IntPtr frameInfo, IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int NodeCountCall(IntPtr handle, uint count);

    internal readonly EnumCall EnumDevices;
    internal readonly CreateCall CreateHandle;
    internal readonly OpenCall OpenDevice;
    internal readonly HandleCall CloseDevice, DestroyHandle, StartGrabbing, StopGrabbing;
    internal readonly GetNodeCall GetInt, GetFloat, GetEnum, GetString, GetEnumSymbol;
    internal readonly SetIntCall SetInt;
    internal readonly SetEnumCall SetEnum;
    internal readonly SetSymbolCall SetSymbol;
    internal readonly SetFloatCall SetFloat;
    internal readonly CommandCall Command;
    internal readonly RegisterCall Register;
    internal readonly NodeCountCall SetImageNodeNum;
    internal string RuntimeVersion { get; }

    internal HikrobotNativeApi(string libraryPath)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new HikrobotSdkException("HikrobotWindowsX64Required");
        if (!Path.IsPathFullyQualified(libraryPath) ||
            !string.Equals(Path.GetFileName(libraryPath), "MvCameraControl.dll", StringComparison.OrdinalIgnoreCase))
            throw new HikrobotSdkException("HikrobotRuntimePathInvalid");
        using (var stream = File.OpenRead(libraryPath))
        using (var pe = new PEReader(stream))
            if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.HasMetadata)
                throw new HikrobotSdkException("HikrobotNativeArchitectureMismatch");
        var file = FileVersionInfo.GetVersionInfo(libraryPath);
        var fileVersion = $"{file.FileMajorPart}.{file.FileMinorPart}.{file.FileBuildPart}.{file.FilePrivatePart}";
        if (fileVersion != CandidateRuntimeVersion)
            throw new HikrobotSdkException("HikrobotQualificationCandidateVersionMismatch");
        if (Interlocked.CompareExchange(ref _activeRuntime, 1, 0) != 0)
            throw new HikrobotSdkException(_quarantinedModule == IntPtr.Zero
                ? "HikrobotRuntimeAlreadyOwned" : "HikrobotRuntimeCleanupUnresolved");
        var initializationAttempted = false;
        try
        {
            // Exact DLL directory plus System32 only; no current-directory/PATH probing or global search-path mutation.
            _module = LoadLibraryExW(Path.GetFullPath(libraryPath), IntPtr.Zero, NativeLoadFlags);
            if (_module == IntPtr.Zero) throw new HikrobotSdkException("HikrobotNativeLoadFailed");
            var initialize = Export<NoArgs>("MV_CC_Initialize");
            _finalize = Export<NoArgs>("MV_CC_Finalize");
            EnumDevices = Export<EnumCall>("MV_CC_EnumDevices");
            CreateHandle = Export<CreateCall>("MV_CC_CreateHandle");
            OpenDevice = Export<OpenCall>("MV_CC_OpenDevice");
            CloseDevice = Export<HandleCall>("MV_CC_CloseDevice");
            DestroyHandle = Export<HandleCall>("MV_CC_DestroyHandle");
            StartGrabbing = Export<HandleCall>("MV_CC_StartGrabbing");
            StopGrabbing = Export<HandleCall>("MV_CC_StopGrabbing");
            GetInt = Export<GetNodeCall>("MV_CC_GetIntValueEx");
            GetFloat = Export<GetNodeCall>("MV_CC_GetFloatValue");
            GetEnum = Export<GetNodeCall>("MV_CC_GetEnumValue");
            GetString = Export<GetNodeCall>("MV_CC_GetStringValue");
            GetEnumSymbol = Export<GetNodeCall>("MV_CC_GetEnumEntrySymbolic");
            SetInt = Export<SetIntCall>("MV_CC_SetIntValueEx");
            SetEnum = Export<SetEnumCall>("MV_CC_SetEnumValue");
            SetSymbol = Export<SetSymbolCall>("MV_CC_SetEnumValueByString");
            SetFloat = Export<SetFloatCall>("MV_CC_SetFloatValue");
            Command = Export<CommandCall>("MV_CC_SetCommandValue");
            Register = Export<RegisterCall>("MV_CC_RegisterImageCallBackEx");
            SetImageNodeNum = Export<NodeCountCall>("MV_CC_SetImageNodeNum");
            var version = Export<VersionCall>("MV_CC_GetSDKVersion")();
            RuntimeVersion = $"{version >> 24}.{(version >> 16) & 255}.{(version >> 8) & 255}.{version & 255}";
            if (RuntimeVersion != fileVersion)
                throw new HikrobotSdkException("HikrobotRuntimeVersionReadbackMismatch");
            initializationAttempted = true;
            Check(initialize(), "HikrobotRuntimeInitializeFailed");
            _initialized = true;
        }
        catch
        {
            if (_module != IntPtr.Zero && !TryCleanupFailedConstruction(initializationAttempted,
                    () => _finalize is null ? -1 : _finalize(), () => FreeLibrary(_module)))
            {
                // A failed constructor still owns potentially active SDK code. Keep an
                // explicit process-wide quarantine until process exit; another provider
                // cannot initialize over this unresolved native state.
                _quarantinedModule = _module;
                throw new HikrobotSdkException("HikrobotRuntimeCleanupUnresolved");
            }
            _module = IntPtr.Zero;
            Interlocked.Exchange(ref _activeRuntime, 0);
            throw;
        }
    }

    internal static bool TryCleanupFailedConstruction(bool initializationAttempted,
        Func<int> finalize, Func<bool> unload)
    {
        try
        {
            if (initializationAttempted && finalize() != 0) return false;
            return unload();
        }
        catch (Exception) { return false; }
    }

    // Tests exercise the same pointer/structure and node transaction code with a bounded
    // managed function table. This entry cannot install or alter production compatibility.
    internal HikrobotNativeApi(Func<string, Delegate> managedAbiProbe)
    {
        _managedAbiProbe = true;
        T Get<T>(string name) where T : Delegate => (T)managedAbiProbe(name);
        _finalize = Get<NoArgs>("MV_CC_Finalize");
        EnumDevices = Get<EnumCall>("MV_CC_EnumDevices");
        CreateHandle = Get<CreateCall>("MV_CC_CreateHandle");
        OpenDevice = Get<OpenCall>("MV_CC_OpenDevice");
        CloseDevice = Get<HandleCall>("MV_CC_CloseDevice");
        DestroyHandle = Get<HandleCall>("MV_CC_DestroyHandle");
        StartGrabbing = Get<HandleCall>("MV_CC_StartGrabbing");
        StopGrabbing = Get<HandleCall>("MV_CC_StopGrabbing");
        GetInt = Get<GetNodeCall>("MV_CC_GetIntValueEx");
        GetFloat = Get<GetNodeCall>("MV_CC_GetFloatValue");
        GetEnum = Get<GetNodeCall>("MV_CC_GetEnumValue");
        GetString = Get<GetNodeCall>("MV_CC_GetStringValue");
        GetEnumSymbol = Get<GetNodeCall>("MV_CC_GetEnumEntrySymbolic");
        SetInt = Get<SetIntCall>("MV_CC_SetIntValueEx");
        SetEnum = Get<SetEnumCall>("MV_CC_SetEnumValue");
        SetSymbol = Get<SetSymbolCall>("MV_CC_SetEnumValueByString");
        SetFloat = Get<SetFloatCall>("MV_CC_SetFloatValue");
        Command = Get<CommandCall>("MV_CC_SetCommandValue");
        Register = Get<RegisterCall>("MV_CC_RegisterImageCallBackEx");
        SetImageNodeNum = Get<NodeCountCall>("MV_CC_SetImageNodeNum");
        RuntimeVersion = CandidateRuntimeVersion;
        _initialized = true;
    }

    private T Export<T>(string name) where T : Delegate =>
        NativeLibrary.TryGetExport(_module, name, out var pointer)
            ? Marshal.GetDelegateForFunctionPointer<T>(pointer)
            : throw new HikrobotSdkException("HikrobotRequiredExportMissing");

    internal static void Check(int status, string reason)
    {
        if (status != 0) throw new HikrobotSdkException(reason, unchecked((uint)status));
    }

    internal void RetainHandle() => Interlocked.Increment(ref _nativeHandles);
    internal void ReleaseHandle() => Interlocked.Decrement(ref _nativeHandles);

    public void Dispose()
    {
        if (_disposed || (!_managedAbiProbe && _module == IntPtr.Zero)) return;
        if (Volatile.Read(ref _nativeHandles) != 0)
            throw new HikrobotSdkException("HikrobotNativeHandlesStillOwned");
        // The owning runtime retires every device before reaching this point. If Finalize
        // fails, retain the module and the process owner instead of unloading active code.
        if (_initialized) { Check(_finalize(), "HikrobotRuntimeFinalizeFailed"); _initialized = false; }
        if (_managedAbiProbe) { _disposed = true; return; }
        if (!FreeLibrary(_module)) throw new HikrobotSdkException("HikrobotNativeUnloadFailed");
        _module = IntPtr.Zero;
        _disposed = true;
        Interlocked.Exchange(ref _activeRuntime, 0);
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(IntPtr module);
}

// Layouts follow the vendor Windows C headers. The native callback owns the larger
// FRAME_OUT_INFO_EX allocation; only documented prefix fields are read here.
[StructLayout(LayoutKind.Explicit, Size = 2056)]
internal unsafe struct HikrobotDeviceList
{
    [FieldOffset(0)] internal uint Count;
    [FieldOffset(8)] internal fixed ulong Devices[256];
}
[StructLayout(LayoutKind.Explicit, Size = 96)]
internal struct HikrobotIntValue
{
    [FieldOffset(0)] internal long Current;
    [FieldOffset(8)] internal long Maximum;
    [FieldOffset(16)] internal long Minimum;
    [FieldOffset(24)] internal long Increment;
}
[StructLayout(LayoutKind.Explicit, Size = 28)]
internal struct HikrobotFloatValue
{
    [FieldOffset(0)] internal float Current;
    [FieldOffset(4)] internal float Maximum;
    [FieldOffset(8)] internal float Minimum;
}
[StructLayout(LayoutKind.Explicit, Size = 280)]
internal unsafe struct HikrobotEnumValue
{
    [FieldOffset(0)] internal uint Current;
    [FieldOffset(4)] internal uint Count;
    [FieldOffset(8)] internal fixed uint Values[64];
}
