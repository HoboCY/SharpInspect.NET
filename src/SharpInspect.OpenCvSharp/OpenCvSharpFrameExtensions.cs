using System.Runtime.InteropServices;
using OpenCvSharp;
using SharpInspect.Abstractions;

namespace SharpInspect.OpenCvSharp;

/// <summary>
/// Callback-scoped OpenCvSharp views over Runtime-owned normalized frames.
/// The callback must not retain or mutate the supplied Mat. This boundary is an
/// in-process lifetime contract and is not an OS sandbox.
/// </summary>
public static class OpenCvSharpFrameExtensions
{
    /// <summary>
    /// Creates a zero-copy Mat header over the current frame and invokes <paramref name="use" />
    /// while both the Mat header and Runtime native read hold are alive.
    /// </summary>
    /// <remarks>
    /// The external pixel buffer remains owned by SharpInspect.NET. The callback must not retain
    /// the Mat, create a derived header that outlives the callback, or use writable Mat APIs to
    /// modify the frame. OpenCvSharp cannot enforce those restrictions at the type-system level.
    /// </remarks>
    public static void WithMat(this VisionFrame frame, Action<Mat> use)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(use);

        // OpenCV's CV_16UC1 step must be aligned to its one-element size (2 bytes).
        // Odd source strides must be normalized by the frame owner before publication;
        // fail closed here rather than handing an invalid header to native OpenCV.
        if (frame.PixelFormat == VisionPixelFormat.Mono16 && (frame.StrideBytes & 1) != 0)
            throw new InvalidOperationException("FrameMono16StrideUnaligned");

        var readLease = frame.AcquireNativeRead();
        if (readLease is null)
            throw new InvalidOperationException("VisionFrameNativeBorrowUnavailable");

        try
        {
            if (!frame.IsLoanActive)
                throw new InvalidOperationException("VisionFrameNativeBorrowUnavailable");

            var mat = CreateExternalHeader(frame, readLease);
            try
            {
                use(mat);
            }
            finally
            {
                mat.Dispose();
            }
        }
        finally
        {
            readLease.Dispose();
        }
    }

    /// <summary>Returns an independent OpenCvSharp copy of the normalized frame.</summary>
    public static Mat CloneToMat(this VisionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Mat? clone = null;
        frame.WithMat(source => clone = source.Clone());
        return clone ?? throw new InvalidOperationException("OpenCvSharpCloneUnavailable");
    }

    /// <summary>
    /// Returns a display copy. Mono8 and Bgr24 retain their normalized type. Mono16 is explicitly
    /// scaled to CV_8UC1 using the frame's declared 10, 12, or 16 valid bits.
    /// </summary>
    public static Mat CloneToDisplayMat(this VisionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.PixelFormat != VisionPixelFormat.Mono16)
            return frame.CloneToMat();

        var validBits = frame.ValidBits ??
            throw new InvalidOperationException("VisionFrameMono16ValidBitsMissing");
        if (validBits is not 10 and not 12 and not 16)
            throw new InvalidOperationException("VisionFrameMono16ValidBitsInvalid");

        Mat? display = null;
        try
        {
            frame.WithMat(source =>
            {
                display = new Mat(frame.Height, frame.Width, MatType.CV_8UC1);
                try
                {
                    FillMono16Display(source, display, validBits);
                }
                catch
                {
                    display.Dispose();
                    display = null;
                    throw;
                }
            });

            return display ?? throw new InvalidOperationException("OpenCvSharpDisplayUnavailable");
        }
        catch
        {
            display?.Dispose();
            throw;
        }
    }

    private static Mat CreateExternalHeader(VisionFrame frame, NativeFrameReadLease readLease)
    {
        var layoutLength = checked((long)frame.StrideBytes * frame.Height);
        if (readLease.DataPointer == IntPtr.Zero || readLease.BufferLength < 1 ||
            readLease.BufferLength < layoutLength)
            throw new InvalidOperationException("VisionFrameNativeBufferCoverageInvalid");

        var matType = frame.PixelFormat switch
        {
            VisionPixelFormat.Mono8 => MatType.CV_8UC1,
            VisionPixelFormat.Mono16 => MatType.CV_16UC1,
            VisionPixelFormat.Bgr24 => MatType.CV_8UC3,
            _ => throw new InvalidOperationException("VisionFramePixelFormatUnsupported")
        };

        return Mat.FromPixelData(
            new[] { frame.Height, frame.Width },
            matType,
            readLease.DataPointer,
            new[] { (long)frame.StrideBytes });
    }

    private static void FillMono16Display(Mat source, Mat destination, int validBits)
    {
        var maximum = (uint)((1 << validBits) - 1);
        for (var row = 0; row < source.Rows; row++)
        {
            var sourceRow = source.Ptr(row);
            var destinationRow = destination.Ptr(row);
            for (var column = 0; column < source.Cols; column++)
            {
                var offset = checked(column * sizeof(ushort));
                var sample = (uint)(Marshal.ReadByte(sourceRow, offset) |
                    (Marshal.ReadByte(sourceRow, offset + 1) << 8));
                if (sample > maximum)
                    throw new InvalidOperationException("VisionFrameMono16HighBitsInvalid");

                var displayValue = (sample * 255u + maximum / 2u) / maximum;
                Marshal.WriteByte(destinationRow, column, (byte)displayValue);
            }
        }
    }
}
