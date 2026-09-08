using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Hikrobot;
using SharpInspect.Runtime.Frames;

namespace SharpInspect.Cameras.Hikrobot.Tests;

internal sealed class HikrobotTestClock : IFrameAcquisitionClock
{
    private readonly object _sync = new();
    private readonly List<ScheduledCallback> _callbacks = new();
    private long _timestamp;
    private DateTimeOffset _utc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long _registrationSequence;

    public long Frequency => 1_000_000;

    public FrameTimePoint GetTimePoint()
    {
        lock (_sync) return new FrameTimePoint(_utc, _timestamp);
    }

    public IDisposable Schedule(long dueTimestamp, FrameAcquisitionClockPhase phase,
        Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_sync)
        {
            if (dueTimestamp < _timestamp)
                throw new ArgumentOutOfRangeException(nameof(dueTimestamp));
            var item = new ScheduledCallback(dueTimestamp, phase,
                ++_registrationSequence, callback);
            _callbacks.Add(item);
            return item;
        }
    }

    internal void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(amount));
        var ticks = checked((long)Math.Ceiling(amount.TotalSeconds * Frequency));
        lock (_sync)
        {
            _timestamp = checked(_timestamp + ticks);
            _utc = _utc.AddTicks(amount.Ticks);
        }
        RunDue();
    }

    internal void RunDue()
    {
        while (true)
        {
            ScheduledCallback? callback;
            lock (_sync)
            {
                callback = _callbacks.Where(item => !item.Disposed &&
                        item.DueTimestamp <= _timestamp)
                    .OrderBy(item => item.DueTimestamp)
                    .ThenBy(item => item.Phase)
                    .ThenBy(item => item.RegistrationSequence)
                    .FirstOrDefault();
                if (callback is null) return;
                callback.Disposed = true;
                _callbacks.Remove(callback);
            }
            callback.Callback();
        }
    }

    private sealed class ScheduledCallback : IDisposable
    {
        internal ScheduledCallback(long dueTimestamp, FrameAcquisitionClockPhase phase,
            long registrationSequence, Action callback)
        {
            DueTimestamp = dueTimestamp;
            Phase = phase;
            RegistrationSequence = registrationSequence;
            Callback = callback;
        }

        internal long DueTimestamp { get; }
        internal FrameAcquisitionClockPhase Phase { get; }
        internal long RegistrationSequence { get; }
        internal Action Callback { get; }
        internal bool Disposed { get; set; }

        public void Dispose() => Disposed = true;
    }
}

internal sealed class HikrobotTestSdk : IHikrobotSdkDevice
{
    [ThreadStatic]
    private static bool _inNativeCallback;

    private readonly object _sync = new();
    private Action<HikrobotNativeFrame>? _callback;
    private Action<HikrobotNativeFrame>? _lastCallback;
    private int _activeCalls;
    private bool _disposed;

    internal HikrobotTestSdk(CameraCapabilities capabilities,
        string stableIdentity = "GigE:Fixture001", string model = "FixtureModel")
    {
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        Descriptor = new HikrobotSdkDescriptor(stableIdentity, model, "FixtureFirmware");
    }

    internal HikrobotSdkDescriptor Descriptor { get; }
    HikrobotSdkDescriptor IHikrobotSdkDevice.Descriptor => Descriptor;
    internal CameraCapabilities Capabilities { get; }
    CameraCapabilities IHikrobotSdkDevice.Capabilities => Capabilities;

    internal int ApplyCalls { get; private set; }
    internal int StartCalls { get; private set; }
    internal int TriggerCalls { get; private set; }
    internal int StopCalls { get; private set; }
    internal int DisposeCalls { get; private set; }
    internal int MaximumConcurrentCalls { get; private set; }
    internal bool IsDisposed => Volatile.Read(ref _disposed);
    internal ManualResetEventSlim ApplyEntered { get; } = new();
    internal ManualResetEventSlim ApplyRelease { get; } = new(true);
    internal ManualResetEventSlim StartEntered { get; } = new();
    internal ManualResetEventSlim StartRelease { get; } = new(true);
    internal ManualResetEventSlim TriggerEntered { get; } = new();
    internal ManualResetEventSlim TriggerRelease { get; } = new(true);
    internal ManualResetEventSlim StopEntered { get; } = new();
    internal ManualResetEventSlim StopRelease { get; } = new(true);
    internal ManualResetEventSlim DisposeEntered { get; } = new();
    internal ManualResetEventSlim DisposeRelease { get; } = new(true);
    internal bool FailApply { get; set; }
    internal bool FailStart { get; set; }
    internal bool FailTrigger { get; set; }
    internal bool FailTriggerAfterEmit { get; set; }
    internal bool FailStop { get; set; }
    internal bool FailDispose { get; set; }
    internal bool EmitOnTrigger { get; set; } = true;
    internal bool EmitTwiceOnTrigger { get; set; }
    internal HikrobotNativePixelFormat TriggerPixelFormat { get; set; } =
        HikrobotNativePixelFormat.Mono8;
    internal int TriggerWidth { get; set; } = 1;
    internal int TriggerHeight { get; set; } = 1;
    internal int TriggerStrideBytes { get; set; } = 1;
    internal byte[] TriggerBytes { get; set; } = new byte[] { 42 };
    internal ulong FrameCounter { get; set; } = 1;
    internal ulong DeviceTimestamp { get; set; } = 1;
    internal Func<RequestedCameraConfiguration, EffectiveCameraConfiguration?>? ApplyOverride { get; set; }
    internal ConcurrentQueue<string> CallEvents { get; } = new();

    EffectiveCameraConfiguration IHikrobotSdkDevice.Apply(
        RequestedCameraConfiguration requested) => Apply(requested);

    internal EffectiveCameraConfiguration Apply(RequestedCameraConfiguration requested)
    {
        EnterCall("apply");
        try
        {
            ApplyCalls++;
            ApplyEntered.Set();
            ApplyRelease.Wait();
            if (FailApply) throw new HikrobotSdkException("HikrobotFixtureApplyFailed");
            var result = ApplyOverride?.Invoke(requested) ??
                Capabilities.ValidateConfiguration(requested).Effective;
            if (result is null) throw new HikrobotSdkException("HikrobotFixtureApplyMissing");
            return result;
        }
        finally { ExitCall("apply"); }
    }

    void IHikrobotSdkDevice.Start(Action<HikrobotNativeFrame> callback) => Start(callback);

    internal void Start(Action<HikrobotNativeFrame> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        EnterCall("start");
        try
        {
            StartCalls++;
            StartEntered.Set();
            StartRelease.Wait();
            if (FailStart) throw new HikrobotSdkException("HikrobotFixtureStartFailed");
            lock (_sync)
            {
                _callback = callback;
                _lastCallback = callback;
            }
        }
        finally { ExitCall("start"); }
    }

    void IHikrobotSdkDevice.TriggerSoftware() => TriggerSoftware();

    internal void TriggerSoftware()
    {
        EnterCall("trigger");
        try
        {
            TriggerCalls++;
            TriggerEntered.Set();
            TriggerRelease.Wait();
            if (FailTrigger) throw new HikrobotSdkException("HikrobotFixtureTriggerFailed");
            if (!EmitOnTrigger) return;
            Emit(TriggerPixelFormat, TriggerWidth, TriggerHeight, TriggerStrideBytes,
                TriggerBytes, FrameCounter, DeviceTimestamp, useLastCallback: false);
            if (EmitTwiceOnTrigger)
                Emit(TriggerPixelFormat, TriggerWidth, TriggerHeight, TriggerStrideBytes,
                    TriggerBytes, FrameCounter + 1, DeviceTimestamp + 1, useLastCallback: false);
            if (FailTriggerAfterEmit)
                throw new HikrobotSdkException("HikrobotFixtureTriggerFailedAfterCallback");
        }
        finally { ExitCall("trigger"); }
    }

    void IHikrobotSdkDevice.Stop() => Stop();

    internal void Stop()
    {
        EnterCall("stop");
        try
        {
            StopCalls++;
            StopEntered.Set();
            StopRelease.Wait();
            if (FailStop) throw new HikrobotSdkException("HikrobotFixtureStopFailed");
            lock (_sync) _callback = null;
        }
        finally { ExitCall("stop"); }
    }

    public void Dispose()
    {
        EnterCall("dispose");
        try
        {
            DisposeCalls++;
            DisposeEntered.Set();
            DisposeRelease.Wait();
            if (FailDispose) throw new HikrobotSdkException("HikrobotFixtureDisposeFailed");
            lock (_sync)
            {
                _callback = null;
                _lastCallback = null;
                _disposed = true;
            }
        }
        finally { ExitCall("dispose"); }
    }

    internal void Emit(HikrobotNativePixelFormat format, int width, int height,
        int strideBytes, byte[] bytes, ulong frameCounter, ulong deviceTimestamp,
        bool useLastCallback = true)
    {
        Action<HikrobotNativeFrame>? callback;
        lock (_sync) callback = useLastCallback ? _lastCallback : _callback;
        if (callback is null) return;
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var previousCallbackState = _inNativeCallback;
            _inNativeCallback = true;
            try
            {
                callback(new HikrobotNativeFrame(pointer, bytes.Length, width, height,
                    strideBytes, format, frameCounter, deviceTimestamp));
            }
            finally { _inNativeCallback = previousCallbackState; }
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    internal static bool IsInNativeCallback => _inNativeCallback;

    internal void EmitAfterStop()
    {
        Emit(TriggerPixelFormat, TriggerWidth, TriggerHeight, TriggerStrideBytes,
            TriggerBytes, FrameCounter + 20, DeviceTimestamp + 20, useLastCallback: true);
    }

    private void EnterCall(string name)
    {
        lock (_sync)
        {
            var active = ++_activeCalls;
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, active);
            CallEvents.Enqueue("enter:" + name);
        }
    }

    private void ExitCall(string name)
    {
        lock (_sync)
        {
            --_activeCalls;
            CallEvents.Enqueue("exit:" + name);
        }
    }
}

internal static class HikrobotTestData
{
    internal static CameraProviderIdentity Identity() =>
        new("Hikrobot", "1", "SharpInspect.NET.Cameras.Hikrobot", "1");

    internal static CameraCapabilities Capabilities(VisionPixelFormat format,
        int? validBits = null, bool hardware = false)
    {
        var modes = hardware
            ? new[] { ProductionAcquisitionMode.SoftwareTrigger,
                ProductionAcquisitionMode.HardwareTrigger }
            : new[] { ProductionAcquisitionMode.SoftwareTrigger };
        return new CameraCapabilities(modes, new[] { format },
            format == VisionPixelFormat.Mono16 ? new[] { validBits ?? 16 } : Array.Empty<int>(),
            new CameraDoubleCapability(1, 100, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(-10, 10, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0, 10, 1, CameraQuantizationMode.Exact),
            new CameraRoiCapabilities(1, 1, new(0, 0, 1), new(0, 0, 1),
                new(1, 1, 1), new(1, 1, 1)),
            format == VisionPixelFormat.Bgr24
                ? new CameraWhiteBalanceCapabilities(
                    new CameraDoubleCapability(1, 2, 1, CameraQuantizationMode.Exact),
                    new CameraDoubleCapability(1, 2, 1, CameraQuantizationMode.Exact),
                    new CameraDoubleCapability(1, 2, 1, CameraQuantizationMode.Exact))
                : null);
    }

    internal static RequestedCameraConfiguration Requested(VisionPixelFormat format,
        int? validBits = null, ProductionAcquisitionMode mode =
            ProductionAcquisitionMode.SoftwareTrigger, int timeoutMs = 100) =>
        new(mode, 10, 0, new RegionOfInterest(0, 0, 1, 1), format, validBits,
            timeoutMs, 0, format == VisionPixelFormat.Bgr24 ? new WhiteBalanceRgb(1, 1, 1) : null);

    internal static FrameBufferPoolOptions Pool(int capacity = 2, int maximumBytes = 64,
        TimeSpan? callbackBudget = null) =>
        new(capacity, maximumBytes, callbackBudget ?? TimeSpan.FromMilliseconds(500));
}
