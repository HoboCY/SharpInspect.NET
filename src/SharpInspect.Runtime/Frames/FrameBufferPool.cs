using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Frames;

public sealed class FrameBufferPoolOptions
{
    public FrameBufferPoolOptions(int capacity, int maximumFrameBytes, TimeSpan callbackBudget)
    {
        if (capacity is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maximumFrameBytes is < 1 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
        if (checked((long)capacity * maximumFrameBytes) > 512L * 1024 * 1024)
            throw new ArgumentException("FramePoolTotalMemoryExceeded");
        if (callbackBudget <= TimeSpan.Zero || callbackBudget > TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(callbackBudget));
        Capacity = capacity; MaximumFrameBytes = maximumFrameBytes; CallbackBudget = callbackBudget;
    }
    public int Capacity { get; }
    public int MaximumFrameBytes { get; }
    public TimeSpan CallbackBudget { get; }
}

public sealed record FrameBufferPoolSnapshot(int Capacity, int MaximumFrameBytes, int OutstandingLeases,
    int ActiveReaders, int PeakLeases, long ExhaustionCount, long CallbackBudgetExceededCount,
    bool IsDisposed, bool ProductionFaultLatched);

/// <summary>Acquisition failure data, not an authoritative per-part execution record.</summary>
public sealed record FrameCopyResult(bool Succeeded, string ReasonCode, FrameBufferLease? Lease,
    ExecutionStatus? ExecutionStatus, InspectionDecision? Decision);

/// <summary>
/// Preallocated, pinned pixel buffers. No blocking wait or pixel allocation fallback occurs
/// in TryCopyFrame. Small ownership objects are allocated only after a slot is acquired.
/// </summary>
public sealed class FrameBufferPool : IDisposable
{
    private readonly FrameBufferPoolOptions _options;
    private readonly Slot[] _slots;
    private readonly Action? _beforePublicationForTesting;
    private int _disposed;
    private int _outstanding;
    private int _activeReaders;
    private int _peak;
    private long _exhaustions;
    private long _budgetExceeded;
    private int _productionFault;

    public FrameBufferPool(FrameBufferPoolOptions options) : this(options, null) { }

    // Internal deterministic scheduling probe. Ordinary DI and adapter paths cannot install it.
    internal FrameBufferPool(FrameBufferPoolOptions options, Action? beforePublicationForTesting)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _beforePublicationForTesting = beforePublicationForTesting;
        _slots = new Slot[options.Capacity];
        // All pixel memory is acquired before the pool can be registered or used.
        for (var index = 0; index < _slots.Length; index++)
        {
            var buffer = GC.AllocateUninitializedArray<byte>(options.MaximumFrameBytes, pinned: true);
            buffer.AsSpan().Clear();
            _slots[index] = new Slot(buffer);
        }
    }

    public bool ProductionFaultLatched => Volatile.Read(ref _productionFault) != 0;
    public FrameBufferPoolSnapshot GetSnapshot() => new(_slots.Length, _options.MaximumFrameBytes,
        Volatile.Read(ref _outstanding), Volatile.Read(ref _activeReaders), Volatile.Read(ref _peak),
        Interlocked.Read(ref _exhaustions), Interlocked.Read(ref _budgetExceeded),
        Volatile.Read(ref _disposed) != 0, ProductionFaultLatched);

    public FrameCopyResult TryCopyFrame(FrameMetadata metadata, FrameProvenance provenance,
        ReadOnlySpan<byte> source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata); ArgumentNullException.ThrowIfNull(provenance);
        var began = Stopwatch.GetTimestamp();
        if (metadata.Correlation != provenance.Correlation) return Failure("FrameProvenanceCorrelationMismatch");
        if (cancellationToken.IsCancellationRequested) return Failure("FrameAcquisitionCancelled", cancelled: true);
        if (Volatile.Read(ref _disposed) != 0) return Failure("FramePoolDisposed");
        if (metadata.Correlation.Kind == ExecutionKind.Production && ProductionFaultLatched)
            return Failure("FrameBufferExhausted");
        var rowBytes = metadata.ValidRowBytes;
        var required = metadata.RequiredBufferLength;
        // OpenCV requires each row step to be a multiple of the scalar element size.
        // Keep source layout authoritative for reads; normalize only odd Mono16 rows
        // into the preallocated destination, never by allocating pixels in the bridge.
        var destinationStride = metadata.PixelFormat == VisionPixelFormat.Mono16
            ? checked(metadata.StrideBytes + (metadata.StrideBytes & 1)) : metadata.StrideBytes;
        var layoutBytes = checked((long)destinationStride * metadata.Height);
        // The source may omit final padding, but native Mat datalimit describes every
        // full physical row. The owned backing must cover that entire declared layout.
        if (layoutBytes > _options.MaximumFrameBytes)
            return metadata.Correlation.Kind == ExecutionKind.Production
                ? ExhaustionFailure(production: true) : Failure("FrameExceedsPoolCapacity");
        if (source.Length < required) return Failure("FrameBufferCoverageInvalid");
        Slot? claimed = null;
        foreach (var slot in _slots)
        {
            if (Interlocked.CompareExchange(ref slot.State, 1, 0) != 0) continue;
            claimed = slot;
            var count = Interlocked.Increment(ref _outstanding);
            int peak;
            do { peak = Volatile.Read(ref _peak); }
            while (count > peak && Interlocked.CompareExchange(ref _peak, count, peak) != peak);
            break;
        }
        if (claimed is null)
        {
            if (Volatile.Read(ref _disposed) != 0) return Failure("FramePoolDisposed");
            return ExhaustionFailure(metadata.Correlation.Kind == ExecutionKind.Production);
        }

        var handedOff = false;
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return Failure("FramePoolDisposed");
            if (cancellationToken.IsCancellationRequested) return Failure("FrameAcquisitionCancelled", cancelled: true);
            if (BudgetExpired(began)) return CallbackBudgetFailure();
            var buffer = claimed.Buffer!;
            // Clear padding and the last row's available padding too. No prior-frame bytes
            // remain in the Mat-visible layout, including after an earlier partial copy.
            var visibleBytes = checked((int)layoutBytes);
            buffer.AsSpan(0, visibleBytes).Clear();
            for (var row = 0; row < metadata.Height; row++)
            {
                if (cancellationToken.IsCancellationRequested) return Failure("FrameAcquisitionCancelled", cancelled: true);
                if (BudgetExpired(began)) return CallbackBudgetFailure();
                var offset = checked(row * metadata.StrideBytes);
                var input = source.Slice(offset, rowBytes);
                if (metadata.PixelFormat == VisionPixelFormat.Mono16 && metadata.ValidBits != 16)
                {
                    var maximum = (1 << metadata.ValidBits!.Value) - 1;
                    for (var column = 0; column < input.Length; column += 2)
                        if (BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(column, 2)) > maximum)
                            return Failure("FrameMono16HighBitsInvalid");
                }
                input.CopyTo(buffer.AsSpan(checked(row * destinationStride), rowBytes));
            }
            if (cancellationToken.IsCancellationRequested) return Failure("FrameAcquisitionCancelled", cancelled: true);
            if (BudgetExpired(began)) return CallbackBudgetFailure();
            if (Volatile.Read(ref _disposed) != 0) return Failure("FramePoolDisposed");
            if (metadata.Correlation.Kind == ExecutionKind.Production && ProductionFaultLatched)
                return Failure("FrameBufferExhausted");
            var outputMetadata = destinationStride == metadata.StrideBytes ? metadata :
                new FrameMetadata(metadata.Correlation, metadata.LogicalCameraRole, metadata.Width,
                    metadata.Height, destinationStride, metadata.PixelFormat, metadata.ValidBits,
                    metadata.HostCaptureUtc, metadata.EffectiveCameraConfiguration);
            var outputProvenance = provenance.WithPoolCopyEvidence(metadata.StrideBytes, destinationStride);
            var owner = new FrameOwner(this, claimed, outputMetadata, outputProvenance, rowBytes, checked((int)layoutBytes));
            var lease = new FrameBufferLease(owner);
            var success = new FrameCopyResult(true, "FramePrepared", lease, null, null);
            _beforePublicationForTesting?.Invoke();
            // Dispose can revoke a copying slot without touching its still-used buffer.
            // The CAS is the ownership publication point; a winner is an existing owner
            // for a later Dispose, which must retain its pixels until that owner returns.
            if (Interlocked.CompareExchange(ref claimed.State, 2, 1) != 1 ||
                Volatile.Read(ref _disposed) != 0) return Failure("FramePoolDisposed");
            if (cancellationToken.IsCancellationRequested) return Failure("FrameAcquisitionCancelled", cancelled: true);
            if (BudgetExpired(began)) return CallbackBudgetFailure();
            if (metadata.Correlation.Kind == ExecutionKind.Production && ProductionFaultLatched)
                return Failure("FrameBufferExhausted");
            handedOff = true;
            return success;
        }
        finally { if (!handedOff) ReleaseSlot(claimed); }
    }

    private bool BudgetExpired(long began) =>
        (Stopwatch.GetTimestamp() - began) / (double)Stopwatch.Frequency >= _options.CallbackBudget.TotalSeconds;
    private FrameCopyResult ExhaustionFailure(bool production)
    {
        Interlocked.Increment(ref _exhaustions);
        if (production) Interlocked.Exchange(ref _productionFault, 1);
        return Failure("FrameBufferExhausted");
    }
    private FrameCopyResult CallbackBudgetFailure()
    {
        Interlocked.Increment(ref _budgetExceeded);
        return Failure("FrameCallbackBudgetExceeded");
    }
    private static FrameCopyResult Failure(string reason, bool cancelled = false) =>
        new(false, reason, null, cancelled ? Abstractions.ExecutionStatus.Cancelled : Abstractions.ExecutionStatus.Error,
            InspectionDecision.Unknown);

    private void ReleaseSlot(Slot slot)
    {
        Interlocked.Decrement(ref _outstanding);
        if (Volatile.Read(ref _disposed) != 0)
        { slot.Buffer = null; Volatile.Write(ref slot.State, 3); return; }
        Volatile.Write(ref slot.State, 0);
        if (Volatile.Read(ref _disposed) != 0 && Interlocked.CompareExchange(ref slot.State, 3, 0) == 0)
            slot.Buffer = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var slot in _slots)
        {
            if (Interlocked.CompareExchange(ref slot.State, 3, 0) == 0) slot.Buffer = null;
            else Interlocked.CompareExchange(ref slot.State, 4, 1);
        }
        // Active owners/readers retain pinned buffers. The final release drops their roots.
    }

    internal sealed class Slot
    {
        public Slot(byte[] buffer) => Buffer = buffer;
        public byte[]? Buffer;
        public int State; // 0 free, 1 copying, 2 owned, 3 permanently closed, 4 copy revoked by Dispose
    }

    internal sealed class FrameOwner
    {
        private readonly object _sync = new();
        private readonly FrameBufferPool _pool;
        private readonly Slot _slot;
        private readonly int _rowBytes;
        private readonly int _requiredBytes;
        private bool _closed;
        private bool _returned;
        private int _readers;
        internal FrameOwner(FrameBufferPool pool, Slot slot, FrameMetadata metadata,
            FrameProvenance provenance, int rowBytes, int requiredBytes)
        {
            _pool = pool; _slot = slot; Metadata = metadata; Provenance = provenance;
            _rowBytes = rowBytes; _requiredBytes = requiredBytes; LeaseId = Guid.NewGuid();
            Frame = new PooledFrame(this);
        }
        public Guid LeaseId { get; }
        public FrameMetadata Metadata { get; }
        public FrameProvenance Provenance { get; }
        public VisionFrame Frame { get; }
        public bool IsOpen { get { lock (_sync) return !_closed; } }
        public bool IsReturned { get { lock (_sync) return _returned; } }

        public ReadOnlySpan<byte> ReadRow(int row)
        {
            lock (_sync)
            {
                if (_closed) throw new InvalidOperationException("FrameLoanExpired");
                if (row < 0 || row >= Metadata.Height) throw new ArgumentOutOfRangeException(nameof(row));
                return _slot.Buffer!.AsSpan(checked(row * Metadata.StrideBytes), _rowBytes);
            }
        }
        public NativeFrameReadLease AcquireRead()
        {
            lock (_sync)
            {
                if (_closed) throw new InvalidOperationException("FrameLoanExpired");
                var lease = new ReadLease(this, Marshal.UnsafeAddrOfPinnedArrayElement(_slot.Buffer!, 0), _requiredBytes);
                _readers++; Interlocked.Increment(ref _pool._activeReaders);
                return lease;
            }
        }
        public void Close()
        {
            lock (_sync)
            {
                if (_closed) return;
                _closed = true;
                ReturnWhenSafe();
            }
        }
        private void ReleaseRead()
        {
            lock (_sync)
            {
                _readers--; Interlocked.Decrement(ref _pool._activeReaders);
                ReturnWhenSafe();
            }
        }
        private void ReturnWhenSafe()
        {
            if (!_closed || _readers != 0 || _returned) return;
            _returned = true;
            _pool.ReleaseSlot(_slot);
        }
        private sealed class PooledFrame : VisionFrame
        {
            private readonly FrameOwner _owner;
            public PooledFrame(FrameOwner owner) : base(owner.Metadata) => _owner = owner;
            public override bool IsLoanActive => _owner.IsOpen;
            public override ReadOnlySpan<byte> GetRowSpan(int row) => _owner.ReadRow(row);
            internal override NativeFrameReadLease AcquireNativeRead() => _owner.AcquireRead();
        }
        private sealed class ReadLease : NativeFrameReadLease
        {
            private FrameOwner? _owner;
            private readonly IntPtr _pointer;
            private readonly int _length;
            public ReadLease(FrameOwner owner, IntPtr pointer, int length)
            { _owner = owner; _pointer = pointer; _length = length; }
            public override IntPtr DataPointer => Volatile.Read(ref _owner) is null
                ? throw new InvalidOperationException("FrameReadLeaseExpired") : _pointer;
            public override int BufferLength => Volatile.Read(ref _owner) is null
                ? throw new InvalidOperationException("FrameReadLeaseExpired") : _length;
            public override void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseRead();
        }
    }
}

/// <summary>Framework/adapter ownership token. Never pass this token to an algorithm.</summary>
public sealed class FrameBufferLease : IDisposable
{
    private readonly FrameBufferPool.FrameOwner _owner;
    private int _transferredOrClosed;
    internal FrameBufferLease(FrameBufferPool.FrameOwner owner) => _owner = owner;
    public Guid LeaseId => _owner.LeaseId;
    public bool IsReturned => _owner.IsReturned;
    public VisionFrame Frame { get { EnsureOwned(); return _owner.Frame; } }
    public FrameProvenance Provenance { get { EnsureOwned(); return _owner.Provenance; } }
    private void EnsureOwned()
    {
        if (Volatile.Read(ref _transferredOrClosed) != 0 || !_owner.IsOpen)
            throw new InvalidOperationException("FrameLeaseNotOwned");
    }
    internal FrameBufferPool.FrameOwner? Transfer() =>
        Interlocked.CompareExchange(ref _transferredOrClosed, 1, 0) == 0 ? _owner : null;
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _transferredOrClosed, 1, 0) == 0) _owner.Close();
    }
}
