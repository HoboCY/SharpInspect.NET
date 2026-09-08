using System.Buffers.Binary;
using SharpInspect.Abstractions;

namespace SharpInspect.Cameras.Hikrobot;

internal readonly record struct HikrobotRawFrame(
    HikrobotNativePixelFormat NativePixelFormat,
    int Width,
    int Height,
    int StrideBytes,
    int DataLength,
    ulong FrameCounter,
    ulong DeviceTimestamp,
    FrameTimePoint ReceivedAt,
    bool BusyAtReceipt);

internal readonly record struct HikrobotNormalizedFrame(
    VisionPixelFormat PixelFormat,
    int? ValidBits,
    int StrideBytes,
    int DataLength,
    bool Transformed,
    string Details);

/// <summary>
/// Bounded conversion from the vendor's six declared formats to the three
/// portable formats.  All pixel bytes are written into the caller's preallocated
/// scratch span; this type never allocates a pixel array.
/// </summary>
internal static class HikrobotPixelNormalizer
{
    internal static bool TryCopyRaw(HikrobotNativeFrame frame, Span<byte> scratch,
        FrameTimePoint receivedAt, bool busyAtReceipt, out HikrobotRawFrame raw,
        out string reasonCode)
    {
        raw = default;
        reasonCode = "HikrobotFrameInvalid";

        if (!Enum.IsDefined(typeof(HikrobotNativePixelFormat), frame.PixelFormat))
        {
            reasonCode = "HikrobotNativePixelFormatUnsupported";
            return false;
        }
        if (frame.Width is < 1 or > 32_768 || frame.Height is < 1 or > 32_768)
        {
            reasonCode = "HikrobotFrameDimensionsInvalid";
            return false;
        }

        var bytesPerPixel = NativeBytesPerPixel(frame.PixelFormat);
        if (frame.StrideBytes < checked(frame.Width * bytesPerPixel))
        {
            reasonCode = "HikrobotFrameStrideInvalid";
            return false;
        }

        long required;
        try { required = checked((long)frame.StrideBytes * (frame.Height - 1) +
            frame.Width * bytesPerPixel); }
        catch (OverflowException)
        {
            reasonCode = "HikrobotFrameLengthInvalid";
            return false;
        }

        if (frame.Data == IntPtr.Zero || frame.DataLength < 0 || required > frame.DataLength ||
            required > scratch.Length || required > int.MaxValue)
        {
            reasonCode = "HikrobotFrameBytesUnavailable";
            return false;
        }

        try
        {
            unsafe
            {
                var source = new ReadOnlySpan<byte>((void*)frame.Data, checked((int)required));
                source.CopyTo(scratch);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reasonCode = "HikrobotFrameCopyFailed";
            return false;
        }

        raw = new HikrobotRawFrame(frame.PixelFormat, frame.Width, frame.Height,
            frame.StrideBytes, checked((int)required), frame.FrameCounter,
            frame.DeviceTimestamp, receivedAt, busyAtReceipt);
        reasonCode = string.Empty;
        return true;
    }

    internal static bool TryNormalize(Span<byte> scratch, HikrobotRawFrame raw,
        EffectiveCameraConfiguration expected, out HikrobotNormalizedFrame normalized,
        out string reasonCode)
    {
        normalized = default;
        reasonCode = "HikrobotFrameConfigurationMismatch";
        if (raw.DataLength < 1 || raw.DataLength > scratch.Length)
        {
            reasonCode = "HikrobotFrameBytesUnavailable";
            return false;
        }

        switch (expected.PixelFormat)
        {
            case VisionPixelFormat.Mono8:
                if (raw.NativePixelFormat != HikrobotNativePixelFormat.Mono8)
                    return false;
                normalized = new(VisionPixelFormat.Mono8, null, raw.StrideBytes,
                    raw.DataLength, false, "mono8-identity");
                return true;

            case VisionPixelFormat.Mono16:
                if (raw.NativePixelFormat is not (HikrobotNativePixelFormat.Mono10 or
                    HikrobotNativePixelFormat.Mono12 or HikrobotNativePixelFormat.Mono16) ||
                    expected.ValidBits is not (10 or 12 or 16))
                    return false;
                if ((raw.NativePixelFormat == HikrobotNativePixelFormat.Mono10 &&
                        expected.ValidBits != 10) ||
                    (raw.NativePixelFormat == HikrobotNativePixelFormat.Mono12 &&
                        expected.ValidBits != 12) ||
                    (raw.NativePixelFormat == HikrobotNativePixelFormat.Mono16 &&
                        expected.ValidBits != 16))
                {
                    reasonCode = "HikrobotMono16ValidBitsMismatch";
                    return false;
                }
                if ((raw.StrideBytes & 1) != 0 || raw.Width * 2 > raw.StrideBytes)
                {
                    reasonCode = "HikrobotMono16StrideInvalid";
                    return false;
                }

                var validBits = expected.ValidBits.Value;
                var maximum = (1 << validBits) - 1;
                for (var row = 0; row < raw.Height; row++)
                {
                    var rowStart = checked(row * raw.StrideBytes);
                    var rowEnd = checked(rowStart + raw.Width * 2);
                    for (var offset = rowStart; offset < rowEnd; offset += 2)
                    {
                        var value = BinaryPrimitives.ReadUInt16LittleEndian(
                            scratch.Slice(offset, 2));
                        // PFNC Mono10 and Mono12 are the declared unpacked,
                        // right-aligned formats. Their high padding bits must be
                        // zero; guessing alignment from a sample value would
                        // silently corrupt valid low-intensity right-aligned data.
                        if (value > maximum)
                        {
                            reasonCode = "HikrobotMono16HighBitsInvalid";
                            return false;
                        }
                    }
                }

                normalized = new(VisionPixelFormat.Mono16, validBits, raw.StrideBytes,
                    raw.DataLength, false, $"mono{NativeBits(raw.NativePixelFormat)}-identity");
                return true;

            case VisionPixelFormat.Bgr24:
                if (raw.NativePixelFormat is not (HikrobotNativePixelFormat.Rgb24 or
                    HikrobotNativePixelFormat.Bgr24))
                    return false;
                if (raw.StrideBytes < checked(raw.Width * 3))
                {
                    reasonCode = "HikrobotBgr24StrideInvalid";
                    return false;
                }

                if (raw.NativePixelFormat == HikrobotNativePixelFormat.Rgb24)
                {
                    for (var row = 0; row < raw.Height; row++)
                    {
                        var rowStart = checked(row * raw.StrideBytes);
                        for (var offset = rowStart; offset < rowStart + raw.Width * 3; offset += 3)
                        {
                            var red = scratch[offset];
                            scratch[offset] = scratch[offset + 2];
                            scratch[offset + 2] = red;
                        }
                    }
                }

                normalized = new(VisionPixelFormat.Bgr24, null, raw.StrideBytes,
                    raw.DataLength, raw.NativePixelFormat == HikrobotNativePixelFormat.Rgb24,
                    raw.NativePixelFormat == HikrobotNativePixelFormat.Rgb24
                        ? "rgb24-to-bgr24" : "bgr24-identity");
                return true;

            default:
                reasonCode = "HikrobotPixelFormatUnsupported";
                return false;
        }
    }

    internal static bool IsNativeFormatCompatible(HikrobotNativePixelFormat nativeFormat,
        VisionPixelFormat expected)
    {
        return expected switch
        {
            VisionPixelFormat.Mono8 => nativeFormat == HikrobotNativePixelFormat.Mono8,
            VisionPixelFormat.Mono16 => nativeFormat is HikrobotNativePixelFormat.Mono10 or
                HikrobotNativePixelFormat.Mono12 or HikrobotNativePixelFormat.Mono16,
            VisionPixelFormat.Bgr24 => nativeFormat is HikrobotNativePixelFormat.Rgb24 or
                HikrobotNativePixelFormat.Bgr24,
            _ => false
        };
    }

    private static int NativeBytesPerPixel(HikrobotNativePixelFormat format) => format switch
    {
        HikrobotNativePixelFormat.Mono8 => 1,
        HikrobotNativePixelFormat.Mono10 or HikrobotNativePixelFormat.Mono12 or
            HikrobotNativePixelFormat.Mono16 => 2,
        HikrobotNativePixelFormat.Rgb24 or HikrobotNativePixelFormat.Bgr24 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static int NativeBits(HikrobotNativePixelFormat format) => format switch
    {
        HikrobotNativePixelFormat.Mono10 => 10,
        HikrobotNativePixelFormat.Mono12 => 12,
        HikrobotNativePixelFormat.Mono16 => 16,
        _ => 16
    };
}
