using SharpInspect.Abstractions;

namespace SharpInspect.Cameras.Hikrobot;

// Adapter-private seam. It is never supplied through application DI or production options.
internal interface IHikrobotSdkRuntime : IDisposable
{
    string RuntimeVersion { get; }
    IReadOnlyList<HikrobotSdkDescriptor> Discover();
    IHikrobotSdkDevice Open(string stableDeviceIdentity);
}

internal sealed record HikrobotSdkDescriptor(string StableIdentity, string? Model, string? Firmware);

internal interface IHikrobotSdkDevice : IDisposable
{
    HikrobotSdkDescriptor Descriptor { get; }
    CameraCapabilities Capabilities { get; }
    // Returns actual, complete read-back. On any exception the caller retires the whole device.
    EffectiveCameraConfiguration Apply(RequestedCameraConfiguration requested);
    void Start(Action<HikrobotNativeFrame> callback);
    void TriggerSoftware();
    void Stop();
}

// The pointer is valid only during callback. No SDK-owned bytes cross this seam by ownership.
internal readonly record struct HikrobotNativeFrame(IntPtr Data, int DataLength, int Width,
    int Height, int StrideBytes, HikrobotNativePixelFormat PixelFormat, ulong FrameCounter,
    ulong DeviceTimestamp);

internal enum HikrobotNativePixelFormat : uint
{
    Mono8 = 0x01080001,
    Mono10 = 0x01100003,
    Mono12 = 0x01100005,
    Mono16 = 0x01100007,
    Rgb24 = 0x02180014,
    Bgr24 = 0x02180015
}

internal sealed class HikrobotSdkException : Exception
{
    internal HikrobotSdkException(string reasonCode, uint? nativeStatus = null)
        : base(reasonCode) { ReasonCode = reasonCode; NativeStatus = nativeStatus; }
    internal string ReasonCode { get; }
    internal uint? NativeStatus { get; }
}
