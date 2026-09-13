using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// A bounded, unannotated display copy of one borrowed normalized frame. The copy is
/// presentation data; its hash is deliberately not the authoritative image-evidence hash.
/// </summary>
public sealed class FramePreviewImage
{
    public const string PreviewContractId = "SharpInspect.FramePreview";
    public const string PreviewContractVersion = "1";
    public const int MaximumPixelBytes = 64 * 1024 * 1024;

    private FramePreviewImage(BitmapSource bitmapSource, FrameMetadata frameMetadata,
        Guid sourcePreviewId, string sourcePixelHash)
    {
        BitmapSource = bitmapSource;
        FrameMetadata = frameMetadata;
        SourcePreviewId = sourcePreviewId;
        SourcePixelHash = sourcePixelHash;
    }

    public BitmapSource BitmapSource { get; }
    public BitmapSource SourceImage => BitmapSource;
    public FrameMetadata FrameMetadata { get; }
    public FrameMetadata Metadata => FrameMetadata;
    public Guid SourcePreviewId { get; }
    public string SourcePixelHash { get; }

    /// <summary>
    /// Copies only valid row pixels while the framework loan is active. Mono16 is
    /// explicitly scaled from its declared valid-bit range to Gray8 for display.
    /// </summary>
    public static FramePreviewImage CopyFromFrame(VisionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        // 只在框架借用期内读取源帧；复制完成后，显示位图与采集帧的生命周期完全脱钩。
        if (!frame.IsLoanActive)
            throw new InvalidOperationException("FramePreviewLoanInactive");

        var metadata = frame.Metadata;
        var width = metadata.Width;
        var height = metadata.Height;
        var sourceBytesPerPixel = metadata.PixelFormat switch
        {
            VisionPixelFormat.Mono8 => 1,
            VisionPixelFormat.Mono16 => 2,
            VisionPixelFormat.Bgr24 => 3,
            _ => throw new InvalidOperationException("FramePreviewPixelFormatUnsupported")
        };
        var sourceBytes = checked((long)width * height * sourceBytesPerPixel);
        var displayBytesPerPixel = metadata.PixelFormat == VisionPixelFormat.Bgr24 ? 3 : 1;
        var displayBytes = checked((long)width * height * displayBytesPerPixel);
        if (sourceBytes > MaximumPixelBytes || displayBytes > MaximumPixelBytes)
            throw new InvalidOperationException("FramePreviewBudgetExceeded");

        var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var display = new byte[checked((int)displayBytes)];
        var rowBytes = metadata.ValidRowBytes;
        var displayStride = checked(width * displayBytesPerPixel);
        var validBits = metadata.PixelFormat == VisionPixelFormat.Mono16
            ? metadata.ValidBits switch
            {
                10 or 12 or 16 => metadata.ValidBits.Value,
                _ => throw new InvalidOperationException("FramePreviewMono16ValidBitsInvalid")
            }
            : 0;
        var maximumSample = validBits == 0 ? 0u : (uint)((1 << validBits) - 1);

        try
        {
            // 每行只读取有效像素，行尾 padding 不进入显示缓冲区，也不进入源像素哈希。
            for (var rowIndex = 0; rowIndex < height; rowIndex++)
            {
                if (!frame.IsLoanActive)
                    throw new InvalidOperationException("FramePreviewLoanInactive");
                var row = frame.GetRowSpan(rowIndex);
                if (row.Length < rowBytes)
                    throw new InvalidOperationException("FramePreviewRowCoverageInvalid");
                var sourceRow = row[..rowBytes];
                sourceHash.AppendData(sourceRow);
                var destination = display.AsSpan(checked(rowIndex * displayStride), displayStride);

                if (metadata.PixelFormat is VisionPixelFormat.Mono8 or VisionPixelFormat.Bgr24)
                {
                    sourceRow.CopyTo(destination);
                    continue;
                }

                for (var column = 0; column < width; column++)
                {
                    var sample = BinaryPrimitives.ReadUInt16LittleEndian(
                        sourceRow.Slice(checked(column * 2), sizeof(ushort)));
                    if (sample > maximumSample)
                        throw new InvalidOperationException("FramePreviewMono16HighBitsInvalid");
                    destination[column] = (byte)((sample * 255u + maximumSample / 2u) / maximumSample);
                }
            }

            var bitmap = BitmapSource.Create(width, height, 96, 96,
                metadata.PixelFormat switch
                {
                    VisionPixelFormat.Bgr24 => PixelFormats.Bgr24,
                    _ => PixelFormats.Gray8
                }, null, display, displayStride);
            bitmap.Freeze();
            return new FramePreviewImage(bitmap, metadata, Guid.NewGuid(),
                Convert.ToHexString(sourceHash.GetHashAndReset()));
        }
        finally
        {
            sourceHash.Dispose();
        }
    }
}

/// <summary>Derived visual output. It never replaces the unannotated FramePreviewImage.</summary>
public sealed class RenderedOverlayPreview
{
    internal RenderedOverlayPreview(BitmapSource bitmapSource, FrameOverlaySnapshot snapshot,
        FramePreviewImage? sourceImage, double zoom, double panX, double panY,
        double dpiScaleX, double dpiScaleY)
    {
        // 叠加结果是独立的派生位图，保留源图与 Overlay 身份，不能回写原始显示副本。
        BitmapSource = bitmapSource;
        DerivedPreviewId = Guid.NewGuid();
        DerivedPixelHash = ComputeBitmapHash(bitmapSource);
        SourcePreviewId = sourceImage?.SourcePreviewId;
        SourcePixelHash = sourceImage?.SourcePixelHash;
        OverlaySetId = snapshot.OverlaySetId;
        OverlayContentHash = snapshot.ContentHash;
        RendererContractId = snapshot.RendererContractId;
        RendererContractVersion = snapshot.RendererContractVersion;
        RendererContractContentHash = snapshot.RendererContractContentHash;
        Zoom = zoom;
        PanX = panX;
        PanY = panY;
        DpiScaleX = dpiScaleX;
        DpiScaleY = dpiScaleY;
    }

    public BitmapSource BitmapSource { get; }
    public Guid DerivedPreviewId { get; }
    public string DerivedPixelHash { get; }
    public Guid? SourcePreviewId { get; }
    public string? SourcePixelHash { get; }
    public Guid OverlaySetId { get; }
    public string OverlayContentHash { get; }
    public string OverlaySetContentHash => OverlayContentHash;
    public string RendererContractId { get; }
    public string RendererContractVersion { get; }
    public string RendererContractContentHash { get; }
    public double Zoom { get; }
    public double PanX { get; }
    public double PanY { get; }
    public double DpiScaleX { get; }
    public double DpiScaleY { get; }

    private static string ComputeBitmapHash(BitmapSource bitmap)
    {
        var stride = checked(bitmap.PixelWidth * ((bitmap.Format.BitsPerPixel + 7) / 8));
        var bytes = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(bytes, stride, 0);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
