using System.Runtime.InteropServices;
using System.Text;
using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Cameras.Hikrobot.Tests;

public sealed class HikrobotNativeBackendTests
{
    [Fact]
    public void V121_B01_WindowsX64LayoutsMatchCompiledVendorHeaderAssertions()
    {
        Assert.Equal(2056, Marshal.SizeOf<HikrobotDeviceList>());
        Assert.Equal(8, Marshal.OffsetOf<HikrobotDeviceList>("Devices").ToInt32());
        Assert.Equal(96, Marshal.SizeOf<HikrobotIntValue>());
        Assert.Equal(16, Marshal.OffsetOf<HikrobotIntValue>("Minimum").ToInt32());
        Assert.Equal(28, Marshal.SizeOf<HikrobotFloatValue>());
        Assert.Equal(280, Marshal.SizeOf<HikrobotEnumValue>());
        Assert.Equal(0x900U, HikrobotNativeApi.NativeLoadFlags);
        Assert.Equal(0U, HikrobotNativeApi.NativeLoadFlags & 0x1000U);
    }

    [Fact]
    public void V121_B02_DiscoveryUsesSerialAndOpenReadbackRejectsReplacedDevice()
    {
        using var native = new NativeFunctionFixture();
        using var runtime = native.CreateRuntime();
        Assert.Equal("GigE:SERIAL-01", Assert.Single(runtime.Discover()).StableIdentity);
        native.ActualSerial = "REPLACED";
        var failure = Assert.Throws<HikrobotSdkException>(() => runtime.Open("GigE:SERIAL-01"));
        Assert.Equal("HikrobotOpenedIdentityMismatch", failure.ReasonCode);
        Assert.Equal(1, native.DestroyCount);
    }

    [Fact]
    public void V121_B03_CompleteNodeTransactionReturnsActualReadback()
    {
        using var native = new NativeFunctionFixture();
        using var runtime = native.CreateRuntime();
        using var device = runtime.Open("GigE:SERIAL-01");
        var requested = Configuration();
        var result = device.Apply(requested);
        Assert.Equal(1000, result.ExposureTimeUs);
        Assert.Equal(1, result.GainDb);
        Assert.Equal(requested.RegionOfInterest, result.RegionOfInterest);
        Assert.Equal("On", native.Symbols["TriggerMode"]);
        Assert.Equal("Off", native.Symbols["ExposureAuto"]);
        Assert.Equal("Off", native.Symbols["GainAuto"]);
        Assert.Contains("read-float:TriggerDelay", native.Calls);
        Assert.Contains("read-int:OffsetY", native.Calls);
        Assert.DoesNotContain(device.Capabilities.PixelFormats, value => value == VisionPixelFormat.Bgr24);
    }

    [Fact]
    public void V121_B17_NativeHardwareTriggerCannotBypassQualificationThroughConfiguration()
    {
        using var native = new NativeFunctionFixture();
        using var runtime = native.CreateRuntime();
        using var device = runtime.Open("GigE:SERIAL-01");
        Assert.DoesNotContain(ProductionAcquisitionMode.HardwareTrigger,
            device.Capabilities.AcquisitionModes);
        var before = native.Calls.ToArray();
        var request = new RequestedCameraConfiguration(ProductionAcquisitionMode.HardwareTrigger,
            1000, 1, new(0, 0, 2, 2), VisionPixelFormat.Mono8, null, 1000, 0, null);
        var rejection = Assert.Throws<HikrobotSdkException>(() => device.Apply(request));
        Assert.Equal("HikrobotHardwareTriggerNotQualified", rejection.ReasonCode);
        Assert.Equal(before, native.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V121_B18_FailedOpenDestroysUnopenedHandleWithoutClose(bool destroyFails)
    {
        using var native = new NativeFunctionFixture();
        using var runtime = native.CreateRuntime();
        native.FailOpen = true;
        native.FailClose = true;
        native.DestroyFailuresRemaining = destroyFails ? 1 : 0;
        Assert.Equal(destroyFails ? "HikrobotOpenCleanupFailed" : "HikrobotOpenFailed",
            Assert.Throws<HikrobotSdkException>(() => runtime.Open("GigE:SERIAL-01")).ReasonCode);
        Assert.DoesNotContain("close", native.Calls);
        if (destroyFails)
        {
            Assert.Equal("HikrobotNativeCleanupUnresolved",
                Assert.Throws<HikrobotSdkException>(() => runtime.Open("GigE:SERIAL-01")).ReasonCode);
            Assert.DoesNotContain("finalize", native.Calls);
            runtime.Dispose();
            Assert.Equal(1, native.DestroyCount);
            Assert.DoesNotContain("close", native.Calls);
        }
        else
        {
            Assert.Equal(1, native.DestroyCount);
            native.FailOpen = false;
            native.FailClose = false;
            using var reopened = runtime.Open("GigE:SERIAL-01");
            Assert.Equal("GigE:SERIAL-01", reopened.Descriptor.StableIdentity);
        }
    }

    [Fact]
    public void V121_B19_FailedCreateOutputIsNeverUsedAsOwnedNativeHandle()
    {
        using var native = new NativeFunctionFixture();
        using var runtime = native.CreateRuntime();
        native.FailCreate = true;
        Assert.Equal("HikrobotCreateFailed",
            Assert.Throws<HikrobotSdkException>(() => runtime.Open("GigE:SERIAL-01")).ReasonCode);
        Assert.DoesNotContain("open", native.Calls);
        Assert.DoesNotContain("close", native.Calls);
        Assert.DoesNotContain("destroy", native.Calls);
        native.FailCreate = false;
        using var opened = runtime.Open("GigE:SERIAL-01");
        Assert.Equal("GigE:SERIAL-01", opened.Descriptor.StableIdentity);
    }

    [Theory]
    [InlineData("Gain")]
    [InlineData("Width")]
    [InlineData("TriggerSource")]
    public void V121_B04_NodeWriteFailureNeverReturnsPartialEffective(string node)
    {
        using var native = new NativeFunctionFixture();
        using var runtime = native.CreateRuntime();
        using var device = runtime.Open("GigE:SERIAL-01");
        native.FailWrite = node;
        Assert.Throws<HikrobotSdkException>(() => device.Apply(Configuration()));
    }

    [Fact]
    public void V121_B05_ReadbackMismatchIsRejected()
    {
        using var native = new NativeFunctionFixture();
        using var runtime = native.CreateRuntime();
        using var device = runtime.Open("GigE:SERIAL-01");
        native.IgnoreGainWrite = true;
        Assert.Equal("HikrobotConfigurationReadbackMismatch",
            Assert.Throws<HikrobotSdkException>(() => device.Apply(Configuration())).ReasonCode);
    }

    [Fact]
    public void V121_B06_RawCallbackDecodesPrefixAndRetiresBeforeUnload()
    {
        using var native = new NativeFunctionFixture();
        using var runtime = native.CreateRuntime();
        using var device = runtime.Open("GigE:SERIAL-01");
        device.Apply(Configuration());
        HikrobotNativeFrame? frame = null;
        device.Start(value => frame = value);
        device.TriggerSoftware();
        Assert.NotNull(frame);
        Assert.Equal(2, frame.Value.Width);
        Assert.Equal(2, frame.Value.Height);
        Assert.Equal(4, frame.Value.DataLength);
        Assert.Equal(42UL, frame.Value.FrameCounter);
        Assert.Equal(0x100000002UL, frame.Value.DeviceTimestamp);
        Assert.Equal(HikrobotNativePixelFormat.Mono8, frame.Value.PixelFormat);
        Assert.Equal("TriggerSoftware", native.LastCommand);
        Assert.Equal(2U, native.ImageNodeCount);
        device.Stop();
        frame = null;
        native.EmitRetiredCallback();
        Assert.Null(frame);
        device.Dispose();
        runtime.Dispose();
        Assert.True(native.Calls.IndexOf("destroy") < native.Calls.IndexOf("finalize"));
    }

    [Fact]
    public void V121_B07_InvalidPayloadAndLostPacketsDoNotEscapeAsValidFrames()
    {
        using var native = new NativeFunctionFixture();
        using var runtime = native.CreateRuntime();
        using var device = runtime.Open("GigE:SERIAL-01");
        var observations = new List<HikrobotNativeFrame>();
        device.Start(observations.Add);
        native.Emit(lostPackets: 1);
        native.Emit(length: 3);
        Assert.Equal(2, observations.Count);
        Assert.All(observations, frame => Assert.Equal(IntPtr.Zero, frame.Data));
    }

    [Fact]
    public void V121_B08_AdapterFloatGridIsBinaryExactWithinDeviceRange()
    {
        var cap = HikrobotNativeDevice.FloatCapability(new() { Minimum = 0.0001f, Maximum = 60_000_000 },
            1, double.Epsilon, 60_000_000);
        Assert.True(cap.Minimum > 0);
        Assert.True(cap.Minimum >= 0.0001f);
        Assert.Equal(CameraQuantizationMode.Exact, cap.QuantizationMode);
        foreach (var value in new[] { cap.Minimum, cap.Minimum + cap.Increment, cap.Maximum })
            Assert.Equal(value, (double)(float)value);
    }

    private static RequestedCameraConfiguration Configuration() => new(
        ProductionAcquisitionMode.SoftwareTrigger, 1000, 1, new(0, 0, 2, 2),
        VisionPixelFormat.Mono8, null, 1000, 0, null);

    [Fact]
    public async Task V121_B09_DirectDisposeWaitsForCallbackBeforeDestroy()
    {
        using var native = new NativeFunctionFixture();
        using var runtime = native.CreateRuntime();
        using var device = runtime.Open("GigE:SERIAL-01");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Start(_ => { entered.TrySetResult(true); release.Wait(TimeSpan.FromSeconds(10)); });
        var callback = Task.Run(() => native.Emit());
        Task? dispose = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            dispose = Task.Run(device.Dispose);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Volatile.Read(ref native.StopCount) == 0) await Task.Delay(1, deadline.Token);
            Assert.Equal(0, Volatile.Read(ref native.DestroyCount));
            Assert.False(dispose.IsCompleted);
        }
        finally { release.Set(); }
        await callback.WaitAsync(TimeSpan.FromSeconds(5));
        if (dispose is not null) await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, native.DestroyCount);
    }

    [Fact]
    public void V121_B10_UnconfirmedStopRetainsHandleAndRuntime()
    {
        using var native = new NativeFunctionFixture();
        using var runtime = native.CreateRuntime();
        using var device = runtime.Open("GigE:SERIAL-01");
        device.Start(_ => { });
        try
        {
            native.FailStop = true;
            Assert.Equal("HikrobotStopFailed", Assert.Throws<HikrobotSdkException>(device.Dispose).ReasonCode);
            Assert.Equal(0, native.DestroyCount);
            Assert.Throws<HikrobotSdkException>(runtime.Dispose);
            Assert.DoesNotContain("finalize", native.Calls);
        }
        finally { native.FailStop = false; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void V121_B11_PartialStartOrRegistrationIsRetired(bool registrationFailure)
    {
        using var native = new NativeFunctionFixture();
        using var runtime = native.CreateRuntime();
        using var device = runtime.Open("GigE:SERIAL-01");
        native.FailRegistration = registrationFailure;
        native.FailStart = !registrationFailure;
        Assert.Throws<HikrobotSdkException>(() => device.Start(_ => { }));
        device.Dispose();
        Assert.Contains("unregister", native.Calls);
        Assert.True(native.Calls.IndexOf("unregister") < native.Calls.IndexOf("destroy"));
        if (!registrationFailure) Assert.Equal(1, native.StopCount);
        Assert.Equal(1, native.DestroyCount);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("相机A", "固件B")]
    public void V121_B12_OptionalMetadataDoesNotInvalidateStableSerial(string? model, string? firmware)
    {
        using var native = new NativeFunctionFixture();
        native.SetOptionalMetadata(model, firmware);
        using var runtime = native.CreateRuntime();
        var device = Assert.Single(runtime.Discover());
        Assert.Equal("GigE:SERIAL-01", device.StableIdentity);
        Assert.Equal(model, device.Model);
        Assert.Equal(firmware, device.Firmware);
    }

    [Fact]
    public void V121_B13_FailedOpenCleanupRetainsHandleAndBlocksReopen()
    {
        using var native = new NativeFunctionFixture();
        using var runtime = native.CreateRuntime();
        native.ActualSerial = "REPLACED";
        try
        {
            native.FailClose = true;
            Assert.Equal("HikrobotOpenCloseFailed",
                Assert.Throws<HikrobotSdkException>(() => runtime.Open("GigE:SERIAL-01")).ReasonCode);
            Assert.Equal(0, native.DestroyCount);
            Assert.Equal("HikrobotNativeCleanupUnresolved",
                Assert.Throws<HikrobotSdkException>(() => runtime.Open("GigE:SERIAL-01")).ReasonCode);
            Assert.Throws<HikrobotSdkException>(runtime.Dispose);
            Assert.DoesNotContain("finalize", native.Calls);
        }
        finally { native.FailClose = false; }
        runtime.Dispose();
        Assert.Equal(1, native.DestroyCount);
        Assert.Contains("finalize", native.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V121_B14_DestroyRetryDoesNotRepeatConfirmedClose(bool openingFailure)
    {
        using var native = new NativeFunctionFixture();
        using var runtime = native.CreateRuntime();
        native.DestroyFailuresRemaining = 1;
        native.RejectRepeatedClose = true;
        IHikrobotSdkDevice? openedDevice = null;
        if (openingFailure)
        {
            native.ActualSerial = "REPLACED";
            Assert.Throws<HikrobotSdkException>(() => runtime.Open("GigE:SERIAL-01"));
        }
        else
        {
            var device = runtime.Open("GigE:SERIAL-01");
            openedDevice = device;
            Assert.Throws<HikrobotSdkException>(device.Dispose);
            // The adapter retries Stop -> Dispose, not Dispose alone. A
            // confirmed Close must not cause Stop to block the remaining destroy.
            device.Stop();
        }
        Assert.Equal(1, native.CloseCount);
        Assert.Equal(0, native.DestroyCount);
        Assert.DoesNotContain("finalize", native.Calls);
        runtime.Dispose();
        Assert.Equal(1, native.CloseCount);
        Assert.Equal(1, native.DestroyCount);
        Assert.Contains("finalize", native.Calls);
        var completedCalls = native.Calls.ToArray();
        openedDevice?.Stop();
        Assert.Equal(completedCalls, native.Calls.ToArray());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("USB相机", "固件USB")]
    public void V121_B15_Usb3DiscoveryUsesItsOwnLayoutAndSerial(string? model, string? firmware)
    {
        using var native = new NativeFunctionFixture();
        native.SetUsbIdentityMetadata(model, firmware);
        using var runtime = native.CreateRuntime();
        var descriptor = Assert.Single(runtime.Discover());
        Assert.Equal("Usb3:SERIAL-01", descriptor.StableIdentity);
        Assert.Equal(model, descriptor.Model);
        Assert.Equal(firmware, descriptor.Firmware);
        using var device = runtime.Open(descriptor.StableIdentity);
        Assert.Equal(descriptor, device.Descriptor);
    }

    [Theory]
    [InlineData(false, 0, true, true, "unload")]
    [InlineData(true, -1, true, false, "finalize")]
    [InlineData(true, 0, false, false, "finalize,unload")]
    [InlineData(true, 0, true, true, "finalize,unload")]
    public void V121_B16_FailedConstructionRequiresConfirmedCleanupBeforeOwnerRelease(
        bool attempted, int finalizeStatus, bool unloaded, bool expectedReleased, string expectedCalls)
    {
        var calls = new List<string>();
        var released = HikrobotNativeApi.TryCleanupFailedConstruction(attempted,
            () => { calls.Add("finalize"); return finalizeStatus; },
            () => { calls.Add("unload"); return unloaded; });
        Assert.Equal(expectedReleased, released);
        Assert.Equal(expectedCalls, string.Join(',', calls));
    }

    private sealed class NativeFunctionFixture : IDisposable
    {
        private readonly IntPtr _deviceInfo = Allocate(572);
        private readonly IntPtr _frameInfo = Allocate(256);
        private readonly IntPtr _pixels = Allocate(4);
        private readonly Dictionary<string, Delegate> _functions;
        private HikrobotNativeApi.ImageCallback? _callback;
        private HikrobotNativeApi.ImageCallback? _retiredCallback;
        private readonly Dictionary<string, long> _ints = new()
        { ["Width"] = 2, ["Height"] = 2, ["OffsetX"] = 0, ["OffsetY"] = 0, ["WidthMax"] = 16, ["HeightMax"] = 16 };
        private readonly Dictionary<string, float> _floats = new()
        { ["ExposureTime"] = 1000, ["Gain"] = 0, ["TriggerDelay"] = 0 };
        private readonly Dictionary<string, string[]> _choices = new()
        {
            ["TriggerSource"] = new[] { "Software", "Line0" }, ["TriggerSelector"] = new[] { "FrameStart" },
            ["TriggerMode"] = new[] { "Off", "On" }, ["AcquisitionMode"] = new[] { "Continuous" },
            ["ExposureAuto"] = new[] { "Off", "Continuous" }, ["GainAuto"] = new[] { "Off", "Continuous" },
            ["TriggerActivation"] = new[] { "RisingEdge" }
        };
        internal readonly Dictionary<string, string> Symbols = new();
        internal readonly List<string> Calls = new();
        internal string ActualSerial = "SERIAL-01";
        internal string? FailWrite;
        internal bool IgnoreGainWrite;
        internal string? LastCommand;
        internal uint ImageNodeCount;
        internal int DestroyCount;
        internal int StopCount;
        internal bool FailStop;
        internal bool FailRegistration;
        internal bool FailStart;
        internal bool FailClose;
        internal bool FailCreate;
        internal bool FailOpen;
        internal int CloseCount;
        internal int DestroyFailuresRemaining;
        internal bool RejectRepeatedClose;

        internal NativeFunctionFixture()
        {
            Marshal.WriteInt32(_deviceInfo, 12, 1);
            WriteText(_deviceInfo + 84, "MODEL");
            WriteText(_deviceInfo + 116, "FIRMWARE");
            WriteText(_deviceInfo + 196, "SERIAL-01");
            foreach (var entry in _choices) Symbols.Add(entry.Key, entry.Value[0]);
            _functions = new()
            {
                ["MV_CC_Finalize"] = new HikrobotNativeApi.NoArgs(() => { Calls.Add("finalize"); return 0; }),
                ["MV_CC_EnumDevices"] = new HikrobotNativeApi.EnumCall((layers, list) =>
                { Assert.Equal(5U, layers); Marshal.WriteInt32(list, 1); Marshal.WriteIntPtr(list, 8, _deviceInfo); return 0; }),
                ["MV_CC_CreateHandle"] = new HikrobotNativeApi.CreateCall((out IntPtr handle, IntPtr info) =>
                { Assert.Equal(_deviceInfo, info); handle = new IntPtr(123); return FailCreate ? -1 : 0; }),
                ["MV_CC_OpenDevice"] = new HikrobotNativeApi.OpenCall((_, access, _) =>
                { Calls.Add("open"); Assert.Equal(1U, access); return FailOpen ? -1 : 0; }),
                ["MV_CC_CloseDevice"] = new HikrobotNativeApi.HandleCall(_ =>
                {
                    Calls.Add("close");
                    if (FailClose || (RejectRepeatedClose && CloseCount > 0)) return -1;
                    CloseCount++; return 0;
                }),
                ["MV_CC_DestroyHandle"] = new HikrobotNativeApi.HandleCall(_ =>
                {
                    if (DestroyFailuresRemaining > 0) { DestroyFailuresRemaining--; return -1; }
                    Calls.Add("destroy"); DestroyCount++; return 0;
                }),
                ["MV_CC_StartGrabbing"] = new HikrobotNativeApi.HandleCall(_ => FailStart ? -1 : 0),
                ["MV_CC_StopGrabbing"] = new HikrobotNativeApi.HandleCall(_ =>
                { Interlocked.Increment(ref StopCount); Calls.Add("stop"); return FailStop ? -1 : 0; }),
                ["MV_CC_GetIntValueEx"] = new HikrobotNativeApi.GetNodeCall((_, key, pointer) =>
                {
                    Calls.Add("read-int:" + key);
                    var min = key.StartsWith("Offset", StringComparison.Ordinal) ? 0 : 1;
                    Marshal.StructureToPtr(new HikrobotIntValue { Current = _ints[key], Minimum = min,
                        Maximum = key.StartsWith("Offset", StringComparison.Ordinal) ? 14 : 16, Increment = 1 }, pointer, false);
                    return 0;
                }),
                ["MV_CC_GetFloatValue"] = new HikrobotNativeApi.GetNodeCall((_, key, pointer) =>
                {
                    Calls.Add("read-float:" + key);
                    Marshal.StructureToPtr(new HikrobotFloatValue { Current = _floats[key],
                        Minimum = key == "ExposureTime" ? 1 : 0, Maximum = key == "Gain" ? 16 : 10000 }, pointer, false);
                    return 0;
                }),
                ["MV_CC_GetEnumValue"] = new HikrobotNativeApi.GetNodeCall((_, key, pointer) =>
                {
                    var values = key == "PixelFormat" ? new[] { (uint)HikrobotNativePixelFormat.Mono8 } :
                        Enumerable.Range(0, _choices[key].Length).Select(value => (uint)value).ToArray();
                    var current = key == "PixelFormat" ? values[0] : (uint)Array.IndexOf(_choices[key], Symbols[key]);
                    Marshal.WriteInt32(pointer, unchecked((int)current));
                    Marshal.WriteInt32(pointer, 4, values.Length);
                    for (var i = 0; i < values.Length; i++) Marshal.WriteInt32(pointer, 8 + i * 4, unchecked((int)values[i]));
                    return 0;
                }),
                ["MV_CC_GetStringValue"] = new HikrobotNativeApi.GetNodeCall((_, key, pointer) =>
                { Assert.Equal("DeviceSerialNumber", key); WriteText(pointer, ActualSerial); return 0; }),
                ["MV_CC_GetEnumEntrySymbolic"] = new HikrobotNativeApi.GetNodeCall((_, key, pointer) =>
                { WriteText(pointer + 4, _choices[key][Marshal.ReadInt32(pointer)]); return 0; }),
                ["MV_CC_SetIntValueEx"] = new HikrobotNativeApi.SetIntCall((_, key, value) =>
                { if (FailWrite == key) return -1; _ints[key] = value; return 0; }),
                ["MV_CC_SetFloatValue"] = new HikrobotNativeApi.SetFloatCall((_, key, value) =>
                { if (FailWrite == key) return -1; if (!IgnoreGainWrite || key != "Gain") _floats[key] = value; return 0; }),
                ["MV_CC_SetEnumValue"] = new HikrobotNativeApi.SetEnumCall((_, key, value) =>
                { Assert.Equal("PixelFormat", key); Assert.Equal((uint)HikrobotNativePixelFormat.Mono8, value); return 0; }),
                ["MV_CC_SetEnumValueByString"] = new HikrobotNativeApi.SetSymbolCall((_, key, value) =>
                { if (FailWrite == key) return -1; Symbols[key] = value; return 0; }),
                ["MV_CC_SetCommandValue"] = new HikrobotNativeApi.CommandCall((_, key) =>
                { LastCommand = key; Emit(); return 0; }),
                ["MV_CC_RegisterImageCallBackEx"] = new HikrobotNativeApi.RegisterCall((_, callback, _) =>
                {
                    _callback = callback;
                    if (callback is not null) _retiredCallback = callback;
                    else Calls.Add("unregister");
                    return callback is not null && FailRegistration ? -1 : 0;
                }),
                ["MV_CC_SetImageNodeNum"] = new HikrobotNativeApi.NodeCountCall((_, count) =>
                { ImageNodeCount = count; return 0; })
            };
        }

        internal HikrobotNativeRuntime CreateRuntime() => new(new HikrobotNativeApi(name => _functions[name]));
        internal void SetOptionalMetadata(string? model, string? firmware)
        {
            foreach (var entry in new[] { (Offset: 84, Value: model), (Offset: 116, Value: firmware) })
            {
                Marshal.Copy(new byte[32], 0, _deviceInfo + entry.Offset, 32);
                if (entry.Value is null) continue;
                var bytes = Encoding.UTF8.GetBytes(entry.Value + '\0');
                Marshal.Copy(bytes, 0, _deviceInfo + entry.Offset, bytes.Length);
            }
        }
        internal void SetUsbIdentityMetadata(string? model, string? firmware)
        {
            Marshal.Copy(new byte[572], 0, _deviceInfo, 572);
            Marshal.WriteInt32(_deviceInfo, 12, 4);
            WriteText(_deviceInfo + 428, "SERIAL-01");
            foreach (var entry in new[] { (Offset: 172, Value: model), (Offset: 300, Value: firmware) })
            {
                if (entry.Value is null) continue;
                var bytes = Encoding.UTF8.GetBytes(entry.Value + '\0');
                Marshal.Copy(bytes, 0, _deviceInfo + entry.Offset, bytes.Length);
            }
        }
        internal void Emit(int lostPackets = 0, int length = 4)
        {
            Marshal.WriteInt16(_frameInfo, 0, 2); Marshal.WriteInt16(_frameInfo, 2, 2);
            Marshal.WriteInt32(_frameInfo, 4, (int)HikrobotNativePixelFormat.Mono8);
            Marshal.WriteInt32(_frameInfo, 8, 42);
            Marshal.WriteInt32(_frameInfo, 12, 1); Marshal.WriteInt32(_frameInfo, 16, 2);
            Marshal.WriteInt32(_frameInfo, 32, length); Marshal.WriteInt32(_frameInfo, 96, lostPackets);
            _callback?.Invoke(_pixels, _frameInfo, IntPtr.Zero);
        }
        internal void EmitRetiredCallback() => _retiredCallback?.Invoke(_pixels, _frameInfo, IntPtr.Zero);
        public void Dispose() { Marshal.FreeHGlobal(_deviceInfo); Marshal.FreeHGlobal(_frameInfo); Marshal.FreeHGlobal(_pixels); }
        private static IntPtr Allocate(int count)
        {
            var pointer = Marshal.AllocHGlobal(count);
            Marshal.Copy(new byte[count], 0, pointer, count);
            return pointer;
        }
        private static void WriteText(IntPtr target, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value + '\0');
            Marshal.Copy(bytes, 0, target, bytes.Length);
        }
    }
}
