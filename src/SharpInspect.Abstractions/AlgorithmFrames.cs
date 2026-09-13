namespace SharpInspect.Abstractions;

public enum VisionPixelFormat { Mono8, Mono16, Bgr24 }
public enum ExecutionStatus { Success, Error, Timeout, Cancelled }

/// <summary>
/// Framework-owned image lent only for the actual algorithm call. Retaining a span or
/// derived native header beyond that call violates the loan contract; this is not a sandbox.
/// </summary>
public abstract class VisionFrame
{
    protected VisionFrame(FrameMetadata metadata) =>
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));

    public FrameMetadata Metadata { get; }
    public ExecutionCorrelationId Correlation => Metadata.Correlation;
    public ExecutionCorrelationId ExecutionCorrelation => Correlation;
    public string LogicalCameraRole => Metadata.LogicalCameraRole;
    public string LogicalCameraId => LogicalCameraRole;
    public int Width => Metadata.Width;
    public int Height => Metadata.Height;
    public int StrideBytes => Metadata.StrideBytes;
    public VisionPixelFormat PixelFormat => Metadata.PixelFormat;
    public int? ValidBits => Metadata.ValidBits;
    public DateTimeOffset HostCaptureUtc => Metadata.HostCaptureUtc;
    public EffectiveCameraConfiguration EffectiveCameraConfiguration => Metadata.EffectiveCameraConfiguration;

    public abstract bool IsLoanActive { get; }

    /// <summary>
    /// Reads one valid pixel row, excluding padding. Runtime retains the owner until the
    /// actual call returns. Already returned spans cannot be revoked; never retain them.
    /// </summary>
    public abstract ReadOnlySpan<byte> GetRowSpan(int row);

    // 只有框架独立的 native 视图桥接可以取得受控的读取保持。
    // 外部子类不能通过此 API 提供未经证明、仅在回调期间有效的指针。
    internal virtual NativeFrameReadLease AcquireNativeRead() =>
        throw new InvalidOperationException("VisionFrameNativeBorrowUnavailable");
}

internal abstract class NativeFrameReadLease : IDisposable
{
    public abstract IntPtr DataPointer { get; }
    public abstract int BufferLength { get; }
    public abstract void Dispose();
}
