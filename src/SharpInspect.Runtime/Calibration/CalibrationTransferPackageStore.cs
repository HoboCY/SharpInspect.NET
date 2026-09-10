using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Calibration;

/// <summary>
/// Content-addressed storage for opaque Calibration Transfer package bytes.
/// A blob's name proves only the bytes read from this directory; package structure,
/// provenance, trust, and local calibration authority are decided by other layers.
/// </summary>
internal sealed class CalibrationTransferPackageStore : IAsyncDisposable, IDisposable
{
    private const int IoBufferBytes = 64 * 1024;
    private const int MaximumGateWaitMilliseconds = 50;
    private const int MaximumTemporaryNameAttempts = 16;

    private readonly CalibrationTransferArtifactOptions _options;
    private readonly string _root;
    private readonly string _mutexName;
    private readonly Channel<TransferWorkItem> _queue;
    private readonly SemaphoreSlim _queueSlots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _disposed;

    internal CalibrationTransferPackageStore(CalibrationTransferArtifactOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _root = ValidateRoot(options.ArtifactRoot);
        _mutexName = CreateMutexName(_root);
        _queue = Channel.CreateBounded<TransferWorkItem>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _queueSlots = new SemaphoreSlim(options.QueueCapacity, options.QueueCapacity);
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Canonical ArtifactRoot after the same path checks used for local stores.</summary>
    internal string ArtifactRoot => _root;

    /// <summary>
    /// Retains a copy of the exact opaque package bytes under its SHA-256 name. An existing
    /// same-hash file is read back and verified; no existing path is ever overwritten.
    /// </summary>
    internal Task<CalibrationTransferArtifactReceipt> PreserveAsync(
        CalibrationExportPackage package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();

        // GetBytes is a fresh copy in the public contract. Copy once more so this store never
        // retains caller-owned memory if a future contract exposes a memory-backed view.
        var source = package.GetBytes();
        var bytes = source.ToArray();
        if (bytes.Length < 1 || bytes.Length > _options.MaximumPackageBytes)
            throw new InvalidOperationException("CalibrationTransferPackageCapacityExceeded");

        return EnqueueAsync(token => PreserveCoreAsync(bytes, token), cancellationToken);
    }

    /// <summary>
    /// Reads and re-hashes one exact content-addressed artifact. The hash argument is a
    /// filename identity only; it cannot name a package member or confer package authority.
    /// </summary>
    internal Task<CalibrationExportPackage> ReadAsync(string expectedSha256, int expectedLength,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryNormalizeHash(expectedSha256, out var canonicalHash))
            throw new ArgumentException("CalibrationTransferArtifactHashInvalid", nameof(expectedSha256));
        if (expectedLength < 1 || expectedLength > _options.MaximumPackageBytes)
            throw new ArgumentOutOfRangeException(nameof(expectedLength),
                "CalibrationTransferPackageLengthInvalid");

        return EnqueueAsync(token => ReadCoreAsync(canonicalHash, expectedLength, token),
            cancellationToken);
    }

    private async Task<T> EnqueueAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken callerToken)
    {
        ThrowIfDisposed();
        var item = new TransferWorkItem(async token =>
            (object?)await operation(token).ConfigureAwait(false), _shutdown.Token, callerToken,
            _options.OperationTimeout);
        var slotHeld = false;
        var queued = false;
        try
        {
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(
                callerToken, _shutdown.Token);
            admission.CancelAfter(_options.OperationTimeout);
            var acquired = await _queueSlots.WaitAsync(
                _options.OperationTimeout, admission.Token).ConfigureAwait(false);
            if (!acquired)
                throw new InvalidOperationException("CalibrationTransferArtifactQueueDeadlineExceeded");
            slotHeld = true;

            if (Volatile.Read(ref _disposed) != 0 || _worker.IsCompleted)
                throw new ObjectDisposedException(nameof(CalibrationTransferPackageStore));
            if (!_queue.Writer.TryWrite(item))
                throw new InvalidOperationException("CalibrationTransferArtifactQueueUnavailable");
            slotHeld = false;
            queued = true;

            // Caller cancellation after queue admission cancels the file operation, while the
            // worker still retires stream disposal and releases its slot in RunAsync.
            var result = await item.Completion.Task.WaitAsync(callerToken).ConfigureAwait(false);
            return (T)result!;
        }
        catch
        {
            if (slotHeld)
            {
                _queueSlots.Release();
                item.CancelBeforeQueue();
            }
            else if (!queued)
            {
                item.CancelBeforeQueue();
            }
            throw;
        }
    }

    private async Task RunAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            object? result = null;
            Exception? failure = null;
            try
            {
                result = await item.ExecuteAsync(item.OperationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failure = exception;
            }
            finally
            {
                // The operation, including stream disposal, has retired before this slot is
                // returned. A cancelled write therefore leaves its partial bytes accounted for.
                item.DisposeOperation();
                _queueSlots.Release();
            }

            if (failure is null)
                item.Completion.TrySetResult(result);
            else
                item.Completion.TrySetException(failure);
        }
    }

    private Task<CalibrationTransferArtifactReceipt> PreserveCoreAsync(byte[] bytes,
        CancellationToken cancellationToken) =>
        Task.FromResult(PreserveCore(bytes, cancellationToken));

    private CalibrationTransferArtifactReceipt PreserveCore(byte[] bytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var contentHash = Convert.ToHexString(SHA256.HashData(bytes));
        var receipt = new CalibrationTransferArtifactReceipt(contentHash, bytes.Length);
        var root = ValidateRoot(_root);
        using var gate = AcquireRootGate(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        root = ValidateRoot(_root);

        var destination = ValidateArtifactPath(root, receipt.RelativePath);
        if (File.Exists(destination))
        {
            _ = ReadAndVerify(destination, receipt.Sha256, receipt.Length, cancellationToken);
            return receipt;
        }
        if (Directory.Exists(destination))
            throw new InvalidOperationException("CalibrationTransferArtifactPathNotFile");

        RequireCapacity(root, bytes.Length);
        var temporary = CreateTemporaryPath(root);
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, IoBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
        {
            WriteAll(stream, bytes, cancellationToken);
        }

        // Do not promote a cancelled/expired operation. The completed partial remains in
        // the root and is deliberately included in the next capacity scan.
        cancellationToken.ThrowIfCancellationRequested();
        _ = ValidateRoot(_root);
        destination = ValidateArtifactPath(root, receipt.RelativePath);
        try
        {
            File.Move(temporary, destination, overwrite: false);
        }
        catch (IOException) when (File.Exists(destination))
        {
            // Another instance may have published the same digest without sharing this
            // process's gate. Verify it and never replace it with our temporary file.
            _ = ReadAndVerify(destination, receipt.Sha256, receipt.Length, CancellationToken.None);
            TryDeleteTemporary(temporary);
            temporary = string.Empty;
            return receipt;
        }

        // Move is the retirement point for the temporary reservation. If verification
        // below fails, the immutable destination remains for diagnosis and no temporary
        // path is available to be mistaken for a committed artifact.
        temporary = string.Empty;

        // Verify the bytes after the atomic move. A later read cannot silently trust a
        // damaged destination, and a failed verification leaves the immutable path intact.
        _ = ReadAndVerify(destination, receipt.Sha256, receipt.Length, cancellationToken);
        return receipt;
    }

    private Task<CalibrationExportPackage> ReadCoreAsync(string hash, int expectedLength,
        CancellationToken cancellationToken) =>
        Task.FromResult(ReadCore(hash, expectedLength, cancellationToken));

    private CalibrationExportPackage ReadCore(string hash, int expectedLength,
        CancellationToken cancellationToken)
    {
        var root = ValidateRoot(_root);
        using var gate = AcquireRootGate(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        root = ValidateRoot(_root);
        var path = ValidateArtifactPath(root, hash + ".bin");
        if (!File.Exists(path))
        {
            if (Directory.Exists(path))
                throw new InvalidOperationException("CalibrationTransferArtifactPathNotFile");
            throw new InvalidOperationException("CalibrationTransferArtifactMissing");
        }

        var bytes = ReadAndVerify(path, hash, expectedLength, cancellationToken);
        return new CalibrationExportPackage(bytes);
    }

    private byte[] ReadAndVerify(string path, string expectedHash,
        int expectedLength, CancellationToken cancellationToken)
    {
        var validated = ValidateArtifactPath(_root, Path.GetFileName(path)!);
        using var stream = new FileStream(validated, FileMode.Open, FileAccess.Read,
            FileShare.Read, IoBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedLength)
            throw new InvalidOperationException("CalibrationTransferArtifactLengthMismatch");

        var bytes = new byte[expectedLength];
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
                throw new InvalidOperationException("CalibrationTransferArtifactTruncated");
            offset += read;
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            throw new InvalidOperationException("CalibrationTransferArtifactContentHashMismatch");
        return bytes;
    }

    private static void WriteAll(FileStream stream, byte[] bytes, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(IoBufferBytes, bytes.Length - offset);
            stream.Write(bytes, offset, count);
            offset += count;
        }

        stream.Flush(flushToDisk: true);
    }

    private void RequireCapacity(string root, int additionalBytes)
    {
        if ((long)additionalBytes > _options.MaximumTotalBytes)
            throw new InvalidOperationException("CalibrationTransferArtifactTotalCapacityExceeded");

        long existingBytes = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("CalibrationTransferArtifactReparsePoint");
            if (Directory.Exists(entry))
                throw new InvalidOperationException("CalibrationTransferArtifactDirectoryEntry");

            var validated = ValidateArtifactPath(root, Path.GetFileName(entry)!);
            var length = new FileInfo(validated).Length;
            existingBytes = checked(existingBytes + length);
            if (existingBytes > _options.MaximumTotalBytes - additionalBytes)
                throw new InvalidOperationException("CalibrationTransferArtifactTotalCapacityExceeded");
        }

        if (existingBytes > _options.MaximumTotalBytes - additionalBytes)
            throw new InvalidOperationException("CalibrationTransferArtifactTotalCapacityExceeded");
    }

    private string CreateTemporaryPath(string root)
    {
        for (var attempt = 0; attempt < MaximumTemporaryNameAttempts; attempt++)
        {
            var name = Guid.NewGuid().ToString("N") + ".partial";
            var path = ValidateArtifactPath(root, name);
            if (!File.Exists(path) && !Directory.Exists(path))
                return path;
        }

        throw new IOException("CalibrationTransferArtifactTemporaryNameUnavailable");
    }

    private static string ValidateRoot(string configuredRoot)
    {
        string root;
        try
        {
            root = NormalizeRootPath(Path.GetFullPath(configuredRoot));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
                                           PathTooLongException)
        {
            throw new InvalidOperationException("CalibrationTransferArtifactRootInvalid", exception);
        }

        if (!Directory.Exists(root))
            throw new InvalidOperationException("CalibrationTransferArtifactRootMissing");
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("CalibrationTransferArtifactReparsePoint");
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidOperationException("CalibrationTransferArtifactRootMissing", exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new InvalidOperationException("CalibrationTransferArtifactRootMissing", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException("CalibrationTransferArtifactRootUnavailable", exception);
        }

        var probe = Path.Combine(root, ".calibration-transfer-root-validation");
        if (!StoragePathValidator.TryValidate(new ProductionStoreOptions(probe), out var validated,
                out var reason))
            throw new InvalidOperationException(reason);

        var validatedRoot = Path.GetDirectoryName(validated);
        if (string.IsNullOrEmpty(validatedRoot) ||
            !string.Equals(Path.GetFullPath(validatedRoot).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CalibrationTransferArtifactRootInvalid");
        return root;
    }

    private static string NormalizeRootPath(string path)
    {
        var volumeRoot = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(volumeRoot) &&
            string.Equals(path, volumeRoot, StringComparison.OrdinalIgnoreCase))
            return volumeRoot;
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string ValidateArtifactPath(string root, string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || Path.GetFileName(fileName) != fileName ||
            fileName.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            fileName.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("CalibrationTransferArtifactPathInvalid", nameof(fileName));

        var path = Path.Combine(root, fileName);
        if (!StoragePathValidator.TryValidate(new ProductionStoreOptions(path), out var validated,
                out var reason))
            throw new InvalidOperationException(reason);
        if ((File.Exists(validated) || Directory.Exists(validated)) &&
            (File.GetAttributes(validated) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("CalibrationTransferArtifactReparsePoint");
        return validated;
    }

    private IDisposable AcquireRootGate(CancellationToken cancellationToken)
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, _mutexName);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (mutex.WaitOne(TimeSpan.FromMilliseconds(MaximumGateWaitMilliseconds)))
                        return new MutexLease(mutex);
                }
                catch (AbandonedMutexException)
                {
                    return new MutexLease(mutex);
                }
            }
        }
        catch
        {
            mutex?.Dispose();
            throw;
        }
    }

    private static string CreateMutexName(string root)
    {
        var bytes = Encoding.UTF8.GetBytes(root.ToUpperInvariant());
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return "Local\\SharpInspect.CalibrationTransfer." + hash;
    }

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The orphan remains visible to RequireCapacity and can be diagnosed or cleaned
            // by the owning retention workflow. A failed cleanup never rewrites a blob.
        }
    }

    internal static bool TryNormalizeHash(string? value, out string canonical)
    {
        canonical = string.Empty;
        if (value is null || value.Length != 64)
            return false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            var valid = character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';
            if (!valid) return false;
        }

        canonical = value.ToUpperInvariant();
        return true;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(CalibrationTransferPackageStore));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        _shutdown.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        finally
        {
            _queueSlots.Dispose();
            _shutdown.Dispose();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class TransferWorkItem
    {
        internal TransferWorkItem(Func<CancellationToken, Task<object?>> execute,
            CancellationToken shutdown, CancellationToken caller, TimeSpan timeout)
        {
            ExecuteAsync = execute;
            OperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown, caller);
            OperationCancellation.CancelAfter(timeout);
        }

        internal Func<CancellationToken, Task<object?>> ExecuteAsync { get; }
        internal CancellationTokenSource OperationCancellation { get; }
        internal CancellationToken OperationToken => OperationCancellation.Token;
        internal TaskCompletionSource<object?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void CancelBeforeQueue()
        {
            OperationCancellation.Cancel();
            OperationCancellation.Dispose();
        }

        internal void DisposeOperation() => OperationCancellation.Dispose();
    }

    private sealed class MutexLease : IDisposable
    {
        private Mutex? _mutex;

        internal MutexLease(Mutex mutex) => _mutex = mutex;

        public void Dispose()
        {
            var mutex = Interlocked.Exchange(ref _mutex, null);
            if (mutex is null) return;
            try { mutex.ReleaseMutex(); }
            finally { mutex.Dispose(); }
        }
    }
}
