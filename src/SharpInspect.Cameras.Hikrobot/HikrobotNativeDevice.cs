using System.Runtime.InteropServices;
using SharpInspect.Abstractions;

namespace SharpInspect.Cameras.Hikrobot;

/// <summary>One exclusively owned native handle. Its owner serializes all physical calls.</summary>
internal sealed unsafe class HikrobotNativeDevice : IHikrobotSdkDevice
{
    private readonly HikrobotNativeApi _api;
    private IntPtr _handle;
    private readonly HikrobotNativeApi.ImageCallback _callback;
    private Action<HikrobotNativeFrame>? _receiver;
    private GCHandle _callbackRoot;
    private int _acceptCallbacks;
    private int _callbacksInFlight;
    private bool _started;
    private bool _registered;
    private bool _registrationAttempted;
    private bool _startAttempted;
    private bool _closeConfirmed;
    private readonly Dictionary<string, uint> _triggerSources;
    private readonly HashSet<uint> _nativePixels;

    internal HikrobotNativeDevice(HikrobotNativeApi api, IntPtr handle, HikrobotSdkDescriptor descriptor)
    {
        _api = api;
        _handle = handle;
        Descriptor = descriptor;
        _callback = OnImage;
        var actualSerial = ReadString("DeviceSerialNumber");
        if (descriptor.StableIdentity[(descriptor.StableIdentity.IndexOf(':') + 1)..] != actualSerial)
            throw new HikrobotSdkException("HikrobotOpenedIdentityMismatch");
        _triggerSources = EnumSymbols("TriggerSource");
        RequireSymbol("TriggerSelector", "FrameStart");
        RequireSymbol("TriggerMode", "On");
        RequireSymbol("AcquisitionMode", "Continuous");
        RequireSymbol("ExposureAuto", "Off");
        RequireSymbol("GainAuto", "Off");
        _nativePixels = ReadEnumValues("PixelFormat").Values.ToHashSet();
        var formats = new List<VisionPixelFormat>();
        var bits = new List<int>();
        if (_nativePixels.Contains((uint)HikrobotNativePixelFormat.Mono8)) formats.Add(VisionPixelFormat.Mono8);
        foreach (var candidate in new[] { (HikrobotNativePixelFormat.Mono10, 10),
                     (HikrobotNativePixelFormat.Mono12, 12), (HikrobotNativePixelFormat.Mono16, 16) })
            if (_nativePixels.Contains((uint)candidate.Item1)) bits.Add(candidate.Item2);
        if (bits.Count > 0) formats.Add(VisionPixelFormat.Mono16);
        // This first native candidate deliberately exposes monochrome camera output only.
        // RGB/BGR conversion is exercised at the adapter seam; a color camera's manual
        // white-balance node/unit mapping requires a separately verified model profile.
        var modes = new List<ProductionAcquisitionMode>();
        if (_triggerSources.ContainsKey("Software")) modes.Add(ProductionAcquisitionMode.SoftwareTrigger);
        // Until hardware qualification proves the Busy gate against SDK-buffered
        // pre-Busy pulses, Line0 support alone cannot authorize correlated frames.
        if (formats.Count == 0 || modes.Count == 0)
            throw new HikrobotSdkException("HikrobotNativeCapabilitiesUnsupported");
        var width = ReadInt("Width"); var height = ReadInt("Height");
        var x = ReadInt("OffsetX"); var y = ReadInt("OffsetY");
        // Device-reported ranges can depend on the currently configured ROI. Keep the
        // reported subset, rather than claiming larger ranges the device has not returned.
        var sensorWidth = checked((int)ReadInt("WidthMax").Current);
        var sensorHeight = checked((int)ReadInt("HeightMax").Current);
        Capabilities = new(modes, formats, bits,
            FloatCapability(ReadFloat("ExposureTime"), 1, double.Epsilon, 60_000_000),
            FloatCapability(ReadFloat("Gain"), 1d / 16, -1000, 1000),
            FloatCapability(ReadFloat("TriggerDelay"), 1, 0, 60_000_000),
            new CameraRoiCapabilities(sensorWidth, sensorHeight, IntCapability(x), IntCapability(y),
                IntCapability(width), IntCapability(height)));
    }

    public HikrobotSdkDescriptor Descriptor { get; }
    public CameraCapabilities Capabilities { get; }
    internal bool IsDisposed => _handle == IntPtr.Zero;

    public EffectiveCameraConfiguration Apply(RequestedCameraConfiguration requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (requested.ProductionAcquisitionMode == ProductionAcquisitionMode.HardwareTrigger)
            throw new HikrobotSdkException("HikrobotHardwareTriggerNotQualified");
        RequireOpen();
        if (_started) throw new HikrobotSdkException("HikrobotConfigurationWhileStarted");
        var validation = Capabilities.ValidateConfiguration(requested);
        if (!validation.Succeeded) throw new HikrobotSdkException(validation.ReasonCode);
        var expected = validation.Effective!;
        SetSymbol("AcquisitionMode", "Continuous");
        SetSymbol("TriggerMode", "Off");
        SetSymbol("TriggerSelector", "FrameStart");
        SetSymbol("ExposureAuto", "Off");
        SetSymbol("GainAuto", "Off");
        WriteFloat("ExposureTime", expected.ExposureTimeUs);
        WriteFloat("Gain", expected.GainDb);
        WriteFloat("TriggerDelay", expected.TriggerDelayUs);
        var roi = expected.RegionOfInterest;
        WriteInt("OffsetX", Capabilities.RegionOfInterest.OffsetX.Minimum);
        WriteInt("OffsetY", Capabilities.RegionOfInterest.OffsetY.Minimum);
        WriteInt("Width", roi.Width); WriteInt("Height", roi.Height);
        WriteInt("OffsetX", roi.OffsetX); WriteInt("OffsetY", roi.OffsetY);
        var pixel = expected.PixelFormat switch
        {
            VisionPixelFormat.Mono8 => HikrobotNativePixelFormat.Mono8,
            VisionPixelFormat.Mono16 when expected.ValidBits == 10 => HikrobotNativePixelFormat.Mono10,
            VisionPixelFormat.Mono16 when expected.ValidBits == 12 => HikrobotNativePixelFormat.Mono12,
            VisionPixelFormat.Mono16 when expected.ValidBits == 16 => HikrobotNativePixelFormat.Mono16,
            _ => throw new HikrobotSdkException("HikrobotNativePixelFormatUnsupported")
        };
        HikrobotNativeApi.Check(_api.SetEnum(_handle, "PixelFormat", (uint)pixel), "HikrobotPixelWriteFailed");
        const string source = "Software";
        SetSymbol("TriggerSource", source);
        SetSymbol("TriggerMode", "On");

        // Read every setting and every automatically controlled prerequisite back. No
        // requested value is presented as device evidence when a read/write fails.
        CheckSymbol("AcquisitionMode", "Continuous");
        CheckSymbol("TriggerSelector", "FrameStart");
        CheckSymbol("TriggerMode", "On");
        CheckSymbol("ExposureAuto", "Off");
        CheckSymbol("GainAuto", "Off");
        CheckSymbol("TriggerSource", source);
        if (ReadEnumValues("PixelFormat").Current != (uint)pixel)
            throw new HikrobotSdkException("HikrobotPixelReadbackMismatch");
        var effective = new EffectiveCameraConfiguration(expected.ProductionAcquisitionMode,
            ReadFloat("ExposureTime").Current, ReadFloat("Gain").Current,
            new(checked((int)ReadInt("OffsetX").Current), checked((int)ReadInt("OffsetY").Current),
                checked((int)ReadInt("Width").Current), checked((int)ReadInt("Height").Current)),
            expected.PixelFormat, expected.ValidBits, expected.AcquisitionTimeoutMs,
            ReadFloat("TriggerDelay").Current, null);
        if (effective != expected) throw new HikrobotSdkException("HikrobotConfigurationReadbackMismatch");
        return effective;
    }

    public void Start(Action<HikrobotNativeFrame> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        RequireOpen();
        if (_started || _registered) throw new HikrobotSdkException("HikrobotAlreadyStarted");
        HikrobotNativeApi.Check(_api.SetImageNodeNum(_handle, 2), "HikrobotNativePoolSetupFailed");
        _receiver = callback;
        // The SDK stores a function pointer, which is not a GC root. Retain this instance
        // and its delegate until actual unregister/close/destroy has quiesced the callbacks.
        _callbackRoot = GCHandle.Alloc(this);
        try
        {
            _registrationAttempted = true;
            HikrobotNativeApi.Check(_api.Register(_handle, _callback, IntPtr.Zero), "HikrobotCallbackRegistrationFailed");
            _registered = true;
            Volatile.Write(ref _acceptCallbacks, 1);
            _startAttempted = true;
            HikrobotNativeApi.Check(_api.StartGrabbing(_handle), "HikrobotStartFailed");
            _started = true;
        }
        catch
        {
            Volatile.Write(ref _acceptCallbacks, 0);
            // Retain callback roots after a partial Start. The caller retires the device.
            throw;
        }
    }

    public void TriggerSoftware()
    {
        RequireOpen();
        if (!_started) throw new HikrobotSdkException("HikrobotNotStarted");
        HikrobotNativeApi.Check(_api.Command(_handle, "TriggerSoftware"), "HikrobotSoftwareTriggerFailed");
    }

    public void Stop()
    {
        // Close is attempted only after stop, callback unregistration and drain.
        // A later adapter retirement retry must still reach DestroyHandle even
        // when Close succeeded on the previous attempt, or Dispose already won.
        if (_handle == IntPtr.Zero || _closeConfirmed) return;
        RequireOpen();
        Volatile.Write(ref _acceptCallbacks, 0);
        if (_startAttempted)
        {
            HikrobotNativeApi.Check(_api.StopGrabbing(_handle), "HikrobotStopFailed");
            _started = false;
            _startAttempted = false;
        }
        if (_registrationAttempted)
        {
            HikrobotNativeApi.Check(_api.Register(_handle, null, IntPtr.Zero), "HikrobotCallbackUnregisterFailed");
            _registered = false;
            _registrationAttempted = false;
        }
        WaitForCallbacks();
        _receiver = null;
        if (_callbackRoot.IsAllocated) _callbackRoot.Free();
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        Volatile.Write(ref _acceptCallbacks, 0);
        // Quiesce callbacks BEFORE destroying the SDK handle and its backing buffers.
        // A failed stop/unregister retains the handle, module and GC root for the actual
        // owner; it is never reported as successful cleanup merely because callers left.
        if (_startAttempted)
        {
            HikrobotNativeApi.Check(_api.StopGrabbing(_handle), "HikrobotStopFailed");
            _started = false;
            _startAttempted = false;
        }
        if (_registrationAttempted)
        {
            HikrobotNativeApi.Check(_api.Register(_handle, null, IntPtr.Zero), "HikrobotCallbackUnregisterFailed");
            _registered = false;
            _registrationAttempted = false;
        }
        WaitForCallbacks();
        if (!_closeConfirmed)
        {
            HikrobotNativeApi.Check(_api.CloseDevice(_handle), "HikrobotCloseFailed");
            _closeConfirmed = true;
        }
        HikrobotNativeApi.Check(_api.DestroyHandle(_handle), "HikrobotDestroyFailed");
        _api.ReleaseHandle();
        _handle = IntPtr.Zero;
        _registered = false;
        WaitForCallbacks();
        _receiver = null;
        if (_callbackRoot.IsAllocated) _callbackRoot.Free();
    }

    private void OnImage(IntPtr data, IntPtr info, IntPtr user)
    {
        Interlocked.Increment(ref _callbacksInFlight);
        try
        {
            if (Volatile.Read(ref _acceptCallbacks) == 0) return;
            if (data == IntPtr.Zero || info == IntPtr.Zero)
            {
                _receiver?.Invoke(default);
                return;
            }
            var width = unchecked((ushort)Marshal.ReadInt16(info));
            var height = unchecked((ushort)Marshal.ReadInt16(info, 2));
            var pixel = (HikrobotNativePixelFormat)unchecked((uint)Marshal.ReadInt32(info, 4));
            var bytesPerPixel = pixel switch
            {
                HikrobotNativePixelFormat.Mono8 => 1,
                HikrobotNativePixelFormat.Mono10 or HikrobotNativePixelFormat.Mono12 or HikrobotNativePixelFormat.Mono16 => 2,
                HikrobotNativePixelFormat.Rgb24 or HikrobotNativePixelFormat.Bgr24 => 3,
                _ => 0
            };
            var length = unchecked((uint)Marshal.ReadInt32(info, 32));
            var lostPackets = unchecked((uint)Marshal.ReadInt32(info, 96));
            if (width == 0 || height == 0 || bytesPerPixel == 0 || lostPackets != 0 ||
                length > 64 * 1024 * 1024 || length != (long)width * height * bytesPerPixel)
            {
                _receiver?.Invoke(default);
                return;
            }
            var counter = unchecked((uint)Marshal.ReadInt32(info, 8));
            var timestamp = ((ulong)unchecked((uint)Marshal.ReadInt32(info, 12)) << 32) |
                unchecked((uint)Marshal.ReadInt32(info, 16));
            _receiver?.Invoke(new(data, (int)length, width, height, width * bytesPerPixel, pixel, counter, timestamp));
        }
        catch (Exception)
        {
            // No managed exception may escape through the unmanaged callback trampoline.
            // The controlled attempt's deadline is authoritative if its receiver itself fails.
        }
        finally { Interlocked.Decrement(ref _callbacksInFlight); }
    }

    private void WaitForCallbacks()
    {
        var wait = new SpinWait();
        while (Volatile.Read(ref _callbacksInFlight) != 0) wait.SpinOnce();
    }

    private HikrobotIntValue ReadInt(string name)
    {
        HikrobotIntValue value = default;
        HikrobotNativeApi.Check(_api.GetInt(_handle, name, (IntPtr)(&value)), "HikrobotIntegerReadFailed");
        if (value.Minimum > value.Maximum || value.Current < value.Minimum || value.Current > value.Maximum || value.Increment < 1)
            throw new HikrobotSdkException("HikrobotIntegerBoundsInvalid");
        return value;
    }

    private HikrobotFloatValue ReadFloat(string name)
    {
        HikrobotFloatValue value = default;
        HikrobotNativeApi.Check(_api.GetFloat(_handle, name, (IntPtr)(&value)), "HikrobotFloatReadFailed");
        if (!float.IsFinite(value.Minimum) || !float.IsFinite(value.Maximum) || !float.IsFinite(value.Current) ||
            value.Minimum > value.Maximum || value.Current < value.Minimum || value.Current > value.Maximum)
            throw new HikrobotSdkException("HikrobotFloatBoundsInvalid");
        return value;
    }

    private (uint Current, uint[] Values) ReadEnumValues(string name)
    {
        HikrobotEnumValue value = default;
        HikrobotNativeApi.Check(_api.GetEnum(_handle, name, (IntPtr)(&value)), "HikrobotEnumReadFailed");
        if (value.Count is < 1 or > 64) throw new HikrobotSdkException("HikrobotEnumBoundsInvalid");
        var values = new uint[value.Count];
        for (var index = 0; index < value.Count; index++) values[index] = value.Values[index];
        if (values.Distinct().Count() != values.Length || !values.Contains(value.Current))
            throw new HikrobotSdkException("HikrobotEnumValuesInvalid");
        return (value.Current, values);
    }

    private string ReadString(string name)
    {
        byte* value = stackalloc byte[272];
        new Span<byte>(value, 272).Clear();
        HikrobotNativeApi.Check(_api.GetString(_handle, name, (IntPtr)value), "HikrobotStringReadFailed");
        return HikrobotNativeRuntime.ReadText((IntPtr)value, 256);
    }

    private Dictionary<string, uint> EnumSymbols(string name)
    {
        var result = new Dictionary<string, uint>(StringComparer.Ordinal);
        byte* entry = stackalloc byte[84];
        foreach (var value in ReadEnumValues(name).Values)
        {
            new Span<byte>(entry, 84).Clear();
            *(uint*)entry = value;
            HikrobotNativeApi.Check(_api.GetEnumSymbol(_handle, name, (IntPtr)entry), "HikrobotEnumSymbolReadFailed");
            if (!result.TryAdd(HikrobotNativeRuntime.ReadText((IntPtr)(entry + 4), 64), value))
                throw new HikrobotSdkException("HikrobotEnumSymbolDuplicate");
        }
        return result;
    }

    private uint RequireSymbol(string name, string symbol) => EnumSymbols(name).TryGetValue(symbol, out var value)
        ? value : throw new HikrobotSdkException("HikrobotRequiredNodeValueMissing");
    private void CheckSymbol(string name, string symbol)
    {
        var expected = RequireSymbol(name, symbol);
        if (ReadEnumValues(name).Current != expected)
            throw new HikrobotSdkException("HikrobotEnumReadbackMismatch");
    }
    private void SetSymbol(string name, string symbol) =>
        HikrobotNativeApi.Check(_api.SetSymbol(_handle, name, symbol), "HikrobotEnumWriteFailed");
    private void WriteInt(string name, long value) =>
        HikrobotNativeApi.Check(_api.SetInt(_handle, name, value), "HikrobotIntegerWriteFailed");
    private void WriteFloat(string name, double value)
    {
        if ((double)(float)value != value) throw new HikrobotSdkException("HikrobotFloatNotExactlyRepresentable");
        HikrobotNativeApi.Check(_api.SetFloat(_handle, name, (float)value), "HikrobotFloatWriteFailed");
    }
    private void RequireOpen()
    {
        if (_handle == IntPtr.Zero || _closeConfirmed) throw new HikrobotSdkException("HikrobotDeviceDisposed");
    }
    private static CameraIntCapability IntCapability(HikrobotIntValue value) =>
        new(checked((int)value.Minimum), checked((int)value.Maximum), checked((int)value.Increment));

    internal static CameraDoubleCapability FloatCapability(HikrobotFloatValue value, double baseStep,
        double allowedMinimum, double allowedMaximum)
    {
        // The C SDK reports no Float increment. Expose a conservative adapter grid of
        // exactly representable binary values, not an invented camera quantization step.
        var maximumMagnitude = Math.Max(Math.Abs((double)value.Minimum), Math.Abs((double)value.Maximum));
        var step = baseStep;
        while (maximumMagnitude / step > 8_388_608) step *= 2;
        var minimum = Math.Ceiling(Math.Max(value.Minimum, allowedMinimum) / step) * step;
        if (allowedMinimum > 0 && minimum <= 0) minimum = step;
        var maximum = Math.Floor(Math.Min(value.Maximum, allowedMaximum) / step) * step;
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
            throw new HikrobotSdkException("HikrobotPortableFloatRangeEmpty");
        return new(minimum, maximum, step, CameraQuantizationMode.Exact);
    }
}
