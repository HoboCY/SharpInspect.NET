using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Calibration;

/// <summary>Content-addressed, immutable canonical pixels. SQLite manifests decide which files are authoritative.</summary>
internal sealed class CalibrationFrameEvidenceStore
{
    private readonly CalibrationSessionStoreOptions _options;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _io = new(1, 1);
    private int _pending;

    internal CalibrationFrameEvidenceStore(CalibrationSessionStoreOptions options, TimeSpan timeout)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeout = timeout;
    }

    /// <summary>The caller owns the camera lease until this file and its SQL manifest have committed.</summary>
    internal async Task<CalibrationFrameEvidence> PreserveAsync(CalibrationSessionHeader header,
        Guid frameId, IFrameBufferLease lease, CancellationToken cancellationToken)
    {
        using var admission = await EnterAsync(cancellationToken).ConfigureAwait(false);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(_timeout);
        var token = bounded.Token;
        var root = ValidateRoot();
        var source = lease.Frame;
        var provenance = lease.Provenance;
        if (lease.IsReturned || !source.IsLoanActive || source.Correlation != new ExecutionCorrelationId(ExecutionKind.Calibration, frameId) ||
            provenance.Correlation != source.Correlation || source.LogicalCameraRole != header.Binding.LogicalRole ||
            provenance.ProviderId != header.Binding.Target.Provider.Id ||
            provenance.ProviderVersion != header.Binding.Target.Provider.Version ||
            provenance.StableDeviceIdentity != header.Binding.Target.StableDeviceIdentity)
            throw new InvalidOperationException("CalibrationFrameSourceMismatch");
        var length = checked((long)source.Metadata.ValidRowBytes * source.Height);
        if (length > _options.MaximumFrameBytes || length > int.MaxValue)
            throw new InvalidOperationException("CalibrationFrameCapacityExceeded");
        var pixels = new byte[(int)length];
        for (var row = 0; row < source.Height; row++)
        {
            token.ThrowIfCancellationRequested();
            source.GetRowSpan(row).CopyTo(pixels.AsSpan(checked(row * source.Metadata.ValidRowBytes), source.Metadata.ValidRowBytes));
        }
        var pixelHash = Convert.ToHexString(SHA256.HashData(pixels));
        var name = pixelHash + ".bin";
        var destination = Path.Combine(root, name);
        var metadata = new FrameMetadata(source.Correlation, source.LogicalCameraRole, source.Width, source.Height,
            source.Metadata.ValidRowBytes, source.PixelFormat, source.ValidBits, source.HostCaptureUtc,
            source.EffectiveCameraConfiguration);
        var evidence = new CalibrationFrameEvidence(header.SessionId, frameId, metadata, provenance,
            pixelHash, length, name, source.StrideBytes);
        if (File.Exists(destination))
        {
            _ = await ReadCoreAsync(evidence, token).ConfigureAwait(false);
            return evidence;
        }
        RequireCapacity(root, length);
        var temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".partial");
        // An interrupted partial remains as evidence and counts against capacity.
        // It cannot appear in a query because no committed manifest names it.
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(pixels, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        token.ThrowIfCancellationRequested();
        _ = ValidateRoot();
        File.Move(temporary, destination, overwrite: false);
        _ = await ReadCoreAsync(evidence, token).ConfigureAwait(false);
        return evidence;
    }

    internal async Task<CalibrationFrameImage> ReadAsync(CalibrationFrameEvidence frame,
        CancellationToken cancellationToken)
    {
        using var admission = await EnterAsync(cancellationToken).ConfigureAwait(false);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(_timeout);
        return await ReadCoreAsync(frame, bounded.Token).ConfigureAwait(false);
    }

    private async Task<CalibrationFrameImage> ReadCoreAsync(CalibrationFrameEvidence frame, CancellationToken token)
    {
        var root = ValidateRoot();
        if (frame.RelativePath != frame.PixelHash + ".bin" || frame.ByteLength > _options.MaximumFrameBytes ||
            frame.ByteLength > int.MaxValue)
            throw new InvalidOperationException("CalibrationFrameManifestInvalid");
        var path = Path.Combine(root, frame.RelativePath);
        if (!StoragePathValidator.TryValidate(new ProductionStoreOptions(path), out var validated, out var reason))
            throw new InvalidOperationException(reason);
        await using var stream = new FileStream(validated, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != frame.ByteLength) throw new InvalidOperationException("CalibrationFrameLengthMismatch");
        var pixels = new byte[(int)frame.ByteLength];
        var offset = 0;
        while (offset < pixels.Length)
        {
            var read = await stream.ReadAsync(pixels.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0) throw new InvalidOperationException("CalibrationFrameTruncated");
            offset += read;
        }
        return new CalibrationFrameImage(frame, pixels);
    }

    private string ValidateRoot()
    {
        var probe = Path.Combine(_options.EvidenceRoot, "calibration-root-validation.bin");
        if (!StoragePathValidator.TryValidate(new ProductionStoreOptions(probe), out var validated, out var reason))
            throw new InvalidOperationException(reason);
        return Path.GetDirectoryName(validated)!;
    }

    private void RequireCapacity(string root, long additional)
    {
        long bytes = 0;
        var count = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            if (++count > _options.MaximumEvents + _options.MaximumSessions || Directory.Exists(path))
                throw new InvalidOperationException("CalibrationEvidenceDirectoryCapacityExceeded");
            if (!StoragePathValidator.TryValidate(new ProductionStoreOptions(path), out var validated, out var reason))
                throw new InvalidOperationException(reason);
            bytes = checked(bytes + new FileInfo(validated).Length);
            if (bytes > _options.MaximumTotalFrameBytes - additional)
                throw new InvalidOperationException("CalibrationTotalFrameCapacityExceeded");
        }
        if (bytes > _options.MaximumTotalFrameBytes - additional)
            throw new InvalidOperationException("CalibrationTotalFrameCapacityExceeded");
    }

    private async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        if (Interlocked.Increment(ref _pending) > 16)
        {
            Interlocked.Decrement(ref _pending);
            throw new InvalidOperationException("CalibrationEvidenceQueryCapacityExceeded");
        }
        try
        {
            if (!await _io.WaitAsync(_timeout, token).ConfigureAwait(false))
                throw new InvalidOperationException("CalibrationEvidenceQueryDeadlineExceeded");
            return new Admission(this);
        }
        catch { Interlocked.Decrement(ref _pending); throw; }
    }
    private sealed class Admission : IDisposable
    {
        private CalibrationFrameEvidenceStore? _owner;
        internal Admission(CalibrationFrameEvidenceStore owner) => _owner = owner;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            owner._io.Release();
            Interlocked.Decrement(ref owner._pending);
        }
    }
}

/// <summary>A managed, revocable loan reconstructed from verified immutable source bytes.</summary>
internal sealed class CalibrationBorrowedFrame : VisionFrame, IDisposable
{
    private readonly byte[] _pixels;
    private int _active = 1;
    internal CalibrationBorrowedFrame(CalibrationFrameImage image) : base(image.Frame.Metadata) => _pixels = image.GetBytes();
    public override bool IsLoanActive => Volatile.Read(ref _active) != 0;
    public override ReadOnlySpan<byte> GetRowSpan(int row)
    {
        if (!IsLoanActive) throw new ObjectDisposedException(nameof(CalibrationBorrowedFrame));
        if ((uint)row >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(row));
        return _pixels.AsSpan(checked(row * Metadata.ValidRowBytes), Metadata.ValidRowBytes);
    }
    public void Dispose() => Interlocked.Exchange(ref _active, 0);
}
