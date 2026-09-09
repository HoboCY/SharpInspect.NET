using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// A bounded immutable display copy of one public Preview frame.  This type is
/// deliberately independent from <see cref="VisionFrame"/> and therefore has
/// no inspection correlation or production evidence identity.
/// </summary>
public sealed class PreviewDisplayImage
{
    public const string PreviewContractId = "SharpInspect.PreviewDisplay";
    public const string PreviewContractVersion = "1";
    public const int MaximumPixelBytes = checked((int)CameraPreviewFrame.MaximumBytes);

    private PreviewDisplayImage(BitmapSource bitmapSource, CameraPreviewFrame frame,
        string sourcePixelHash)
    {
        BitmapSource = bitmapSource;
        SessionId = frame.SessionId;
        Sequence = frame.Sequence;
        CapturedAt = frame.CapturedAt;
        Width = frame.Width;
        Height = frame.Height;
        SourceStrideBytes = frame.StrideBytes;
        PixelFormat = frame.PixelFormat;
        ValidBits = frame.ValidBits;
        SourcePixelHash = sourcePixelHash;
    }

    public BitmapSource BitmapSource { get; }
    public BitmapSource SourceImage => BitmapSource;
    public Guid SessionId { get; }
    public long Sequence { get; }
    public FrameTimePoint CapturedAt { get; }
    public int Width { get; }
    public int Height { get; }
    public int SourceStrideBytes { get; }
    public VisionPixelFormat PixelFormat { get; }
    public int? ValidBits { get; }
    public string SourcePixelHash { get; }
    public string PixelHash => SourcePixelHash;
    public long DisplayBufferLength => checked((long)BitmapSource.PixelWidth *
        BitmapSource.PixelHeight * ((BitmapSource.Format.BitsPerPixel + 7) / 8));

    /// <summary>
    /// Copies the public Preview frame into a frozen WPF bitmap.  Mono16 values
    /// are scaled using their declared 10/12/16-bit range; Mono8 and Bgr24 keep
    /// their canonical row bytes.  Row padding is never displayed or hashed.
    /// </summary>
    public static PreviewDisplayImage CopyFromFrame(CameraPreviewFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var sourceBytesPerPixel = frame.PixelFormat switch
        {
            VisionPixelFormat.Mono8 => 1,
            VisionPixelFormat.Mono16 => 2,
            VisionPixelFormat.Bgr24 => 3,
            _ => throw new InvalidOperationException("PreviewDisplayPixelFormatUnsupported")
        };
        var sourceRowBytes = checked(frame.Width * sourceBytesPerPixel);
        var displayBytesPerPixel = frame.PixelFormat == VisionPixelFormat.Bgr24 ? 3 : 1;
        var displayStride = checked(frame.Width * displayBytesPerPixel);
        var displayBytes = checked((long)displayStride * frame.Height);
        if (displayBytes > MaximumPixelBytes || displayBytes > int.MaxValue)
            throw new InvalidOperationException("PreviewDisplayBudgetExceeded");

        var display = new byte[checked((int)displayBytes)];
        using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var validBits = frame.PixelFormat == VisionPixelFormat.Mono16
            ? frame.ValidBits switch
            {
                10 or 12 or 16 => frame.ValidBits.Value,
                _ => throw new InvalidOperationException("PreviewDisplayMono16ValidBitsInvalid")
            }
            : 0;
        var maximumSample = validBits == 0 ? 0u : (uint)((1 << validBits) - 1);

        for (var rowIndex = 0; rowIndex < frame.Height; rowIndex++)
        {
            var sourceRow = frame.GetRowSpan(rowIndex);
            if (sourceRow.Length < sourceRowBytes)
                throw new InvalidOperationException("PreviewDisplayRowCoverageInvalid");
            sourceRow = sourceRow[..sourceRowBytes];
            sourceHash.AppendData(sourceRow);
            var destination = display.AsSpan(checked(rowIndex * displayStride), displayStride);

            if (frame.PixelFormat is VisionPixelFormat.Mono8 or VisionPixelFormat.Bgr24)
            {
                sourceRow.CopyTo(destination);
                continue;
            }

            for (var column = 0; column < frame.Width; column++)
            {
                var sample = BinaryPrimitives.ReadUInt16LittleEndian(
                    sourceRow.Slice(checked(column * sizeof(ushort)), sizeof(ushort)));
                if (sample > maximumSample)
                    throw new InvalidOperationException("PreviewDisplayMono16HighBitsInvalid");
                destination[column] = (byte)((sample * 255u + maximumSample / 2u) /
                    maximumSample);
            }
        }

        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96,
            frame.PixelFormat == VisionPixelFormat.Bgr24 ? PixelFormats.Bgr24 : PixelFormats.Gray8,
            null, display, displayStride);
        bitmap.Freeze();
        return new PreviewDisplayImage(bitmap, frame,
            Convert.ToHexString(sourceHash.GetHashAndReset()));
    }

    public static PreviewDisplayImage CopyFromPreviewFrame(CameraPreviewFrame frame) =>
        CopyFromFrame(frame);
}
