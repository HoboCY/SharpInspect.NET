using SharpInspect.Abstractions;
using SharpInspect.Runtime.Frames;

namespace SharpInspect.Runtime.Images;

/// <summary>
/// One production frame retained for the single stage consumer. Capture acquires an owned
/// native read hold without transferring the algorithm's pool ownership, so closing the
/// original lease cannot revoke the pixels the stage still has to read. No full image copy
/// is made anywhere in this type. Disposal belongs to the physical stage worker's finally.
/// </summary>
internal sealed class RetainedProductionFrame : VisionFrame, IDisposable
{
    private readonly NativeFrameReadLease _read;
    private readonly IntPtr _pointer;
    private readonly int _bufferLength;
    private int _disposed;

    private RetainedProductionFrame(NativeFrameReadLease read,
        Guid leaseId, FrameMetadata metadata, FrameProvenance provenance)
        : base(metadata)
    {
        _read = read;
        _pointer = read.DataPointer;
        _bufferLength = read.BufferLength;
        LeaseId = leaseId;
        Provenance = provenance;
    }

    /// <summary>Identity of the frame lease this retention was captured from.</summary>
    internal Guid LeaseId { get; }

    /// <summary>Acquisition provenance retained for the evidence record.</summary>
    internal FrameProvenance Provenance { get; }

    public override bool IsLoanActive => Volatile.Read(ref _disposed) == 0;

    /// <summary>
    /// Captures an independent native read hold before the algorithm ownership transfer, so an ordinary lease
    /// close afterwards keeps the pool slot and its pinned pixels alive for this frame.
    /// </summary>
    internal static RetainedProductionFrame Capture(IFrameBufferLease bufferLease)
    {
        ArgumentNullException.ThrowIfNull(bufferLease);
        if (bufferLease is not FrameBufferLease)
            throw new InvalidOperationException("ProductionFrameRetentionLeaseUnsupported");
        // Leave the lease token intact: the coordinator transfers it to the algorithm next.
        var frame = bufferLease.Frame;
        var provenance = bufferLease.Provenance;
        var leaseId = bufferLease.LeaseId;
        var read = frame.AcquireNativeRead();
        try
        {
            return new RetainedProductionFrame(read, leaseId, frame.Metadata, provenance);
        }
        catch
        {
            read.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads one valid pixel row directly from the held native buffer, excluding padding.
    /// The row bounds are checked against the native buffer length before any pointer use.
    /// </summary>
    public override unsafe ReadOnlySpan<byte> GetRowSpan(int row)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(RetainedProductionFrame));
        if ((uint)row >= (uint)Metadata.Height)
            throw new ArgumentOutOfRangeException(nameof(row));
        var offset = (long)row * Metadata.StrideBytes;
        var length = Metadata.ValidRowBytes;
        if (offset > _bufferLength || length > _bufferLength - offset)
            throw new InvalidOperationException("ProductionFrameRetainedRowOutOfBounds");
        return new ReadOnlySpan<byte>((byte*)_pointer + (int)offset, length);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // The pool returns the slot once both the algorithm owner and this read hold close.
        _read.Dispose();
    }
}
