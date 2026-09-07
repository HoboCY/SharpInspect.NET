namespace SharpInspect.Abstractions;

/// <summary>Pixel formats supported by the platform-neutral frame contract.</summary>
public enum VisionPixelFormat
{
    Mono8,
    Mono16,
    Bgr24
}

/// <summary>
/// Framework-owned frame borrowed by an algorithm for one call. The base contract exposes
/// metadata and read-only row spans only; ownership, pooling and loan revocation stay with the
/// frame provider and Runtime.
/// </summary>
public abstract class VisionFrame
{
    protected VisionFrame(ExecutionCorrelationId correlation, string logicalCameraId,
        int width, int height, int strideBytes, VisionPixelFormat pixelFormat,
        int? validBits, DateTimeOffset hostCaptureUtc)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        AlgorithmContractValidation.Correlation(correlation, nameof(correlation));
        Correlation = correlation;
        LogicalCameraId = AlgorithmContractValidation.Identifier(logicalCameraId, nameof(logicalCameraId));
        if (width is < 1 or > 32_768) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is < 1 or > 32_768) throw new ArgumentOutOfRangeException(nameof(height));
        if (!Enum.IsDefined(typeof(VisionPixelFormat), pixelFormat))
            throw new ArgumentOutOfRangeException(nameof(pixelFormat));
        var bytesPerPixel = pixelFormat switch
        {
            VisionPixelFormat.Mono8 => 1,
            VisionPixelFormat.Mono16 => 2,
            VisionPixelFormat.Bgr24 => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat))
        };
        var minimumStride = checked(width * bytesPerPixel);
        if (strideBytes < minimumStride || strideBytes > 256 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(strideBytes));
        if (pixelFormat == VisionPixelFormat.Mono16)
        {
            if (validBits is not 10 and not 12 and not 16)
                throw new ArgumentOutOfRangeException(nameof(validBits));
        }
        else if (validBits.HasValue)
        {
            throw new ArgumentException("VisionFrameValidBitsAbsentForPixelFormat", nameof(validBits));
        }
        if (hostCaptureUtc == default || hostCaptureUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("VisionFrameCaptureTimeRequired", nameof(hostCaptureUtc));

        Width = width;
        Height = height;
        StrideBytes = strideBytes;
        PixelFormat = pixelFormat;
        ValidBits = validBits;
        HostCaptureUtc = hostCaptureUtc;
    }

    public ExecutionCorrelationId Correlation { get; }
    public ExecutionCorrelationId ExecutionCorrelation => Correlation;
    public string LogicalCameraId { get; }
    public int Width { get; }
    public int Height { get; }
    public int StrideBytes { get; }
    public VisionPixelFormat PixelFormat { get; }
    public int? ValidBits { get; }
    public DateTimeOffset HostCaptureUtc { get; }

    /// <summary>True only while Runtime still lends this frame to the current call.</summary>
    public abstract bool IsLoanActive { get; }

    /// <summary>
    /// Returns one read-only row. Implementations must reject rows outside [0, Height) and
    /// must never return writable memory or a span whose lifetime outlives the active loan.
    /// </summary>
    public abstract ReadOnlySpan<byte> GetRowSpan(int row);
}
