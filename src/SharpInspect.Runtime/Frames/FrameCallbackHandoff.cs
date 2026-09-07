using System.Threading.Channels;

namespace SharpInspect.Runtime.Frames;

/// <summary>
/// Bounded callback-to-acquisition handoff. TryPublish consumes the owner's token on
/// both success and failure. No consumer continuation runs inline on the SDK callback.
/// </summary>
public sealed class FrameCallbackHandoff : IDisposable
{
    private readonly Channel<FrameBufferPool.FrameOwner> _channel;
    private int _waitingReader;
    private int _disposed;
    private long _rejected;
    public FrameCallbackHandoff(int capacity)
    {
        if (capacity is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(capacity));
        _channel = Channel.CreateBounded<FrameBufferPool.FrameOwner>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false,
            SingleReader = false, SingleWriter = false
        });
    }
    public long RejectedCount => Interlocked.Read(ref _rejected);
    public bool TryPublish(FrameBufferLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var owner = lease.Transfer();
        if (owner is null) { Interlocked.Increment(ref _rejected); return false; }
        if (Volatile.Read(ref _disposed) == 0 && _channel.Writer.TryWrite(owner)) return true;
        owner.Close(); Interlocked.Increment(ref _rejected); return false;
    }
    public async ValueTask<FrameBufferLease> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _waitingReader, 1, 0) != 0)
            throw new InvalidOperationException("FrameHandoffReaderAlreadyWaiting");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) != 0 || cancellationToken.IsCancellationRequested)
            {
                owner.Close();
                cancellationToken.ThrowIfCancellationRequested();
                throw new ObjectDisposedException(nameof(FrameCallbackHandoff));
            }
            return new FrameBufferLease(owner);
        }
        finally { Volatile.Write(ref _waitingReader, 0); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel.Writer.TryComplete();
        while (_channel.Reader.TryRead(out var owner)) owner.Close();
    }
}
