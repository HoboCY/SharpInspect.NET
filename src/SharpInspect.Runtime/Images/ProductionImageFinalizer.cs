using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Images;

internal enum ImageFinalizationBoundary
{
    BeforeStageOpen, BeforeEncode, AfterEncode, BeforeFlush, AfterFlush,
    BeforeTemporaryVerify, AfterTemporaryVerify, BeforeRename, AfterRename,
    BeforeFinalVerify, AfterFinalVerify, BeforeClaim, BeforeClaimPublication, BeforeTimeoutDecision
}

internal sealed record ProductionPngFileLimits(PngCodecLimits Codec, long MaximumFinalBytes, int MaximumFinalFiles);

/// <summary>
/// One physical PNG operation at a time, driven only by persisted work and attempt
/// locators. A timed-out operation retains its slot until its real file I/O has exited.
/// The Runtime scheduler owns SQL ordering; this component never deletes the stage.
/// </summary>
internal sealed partial class ProductionImageFinalizer
{
    private readonly ProductionImageStageOptions _stage;
    private readonly string _finalRoot;
    private readonly string _finalRootBindingHash;
    private readonly ProductionPngFileLimits _limits;
    private readonly Action<ImageFinalizationBoundary>? _faultHook;
    private readonly object _claimIssuer = new();
    private int _operationSlot;
    private Task _physicalCompletion = Task.CompletedTask;

    internal ProductionImageFinalizer(ProductionImageStageOptions stage, string finalRoot,
        string finalRootBindingHash, ProductionPngFileLimits limits,
        Action<ImageFinalizationBoundary>? faultHook = null)
    {
        _stage = stage ?? throw new ArgumentNullException(nameof(stage));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        if (!Path.IsPathFullyQualified(finalRoot) || !Directory.Exists(finalRoot))
            throw new ArgumentException("ProductionImageFinalRootRequired", nameof(finalRoot));
        _finalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(finalRoot));
        if (!IsHash(finalRootBindingHash)) throw new ArgumentException("ProductionImageFinalRootHashInvalid", nameof(finalRootBindingHash));
        _finalRootBindingHash = finalRootBindingHash;
        if (limits.MaximumFinalBytes < limits.Codec.MaximumEncodedBytes || limits.MaximumFinalFiles is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(limits));
        _faultHook = faultHook;
        RequireRoots();
    }

    internal int ActiveOperationCount => Volatile.Read(ref _operationSlot);
    internal Task PhysicalCompletion => Volatile.Read(ref _physicalCompletion);

    /// <param name="allowPreviouslyBoundFinal">
    /// True only for a locator already bound by an earlier durable AttemptStarted. A new
    /// attempt must check for pre-existing output before recording ownership in SQL.
    /// </param>
    internal Task<VerifiedPngCommitClaim> FinalizeAsync(PendingImageFinalizationWork work, Guid attemptId,
        string temporaryFileName, string finalFileName, bool allowPreviouslyBoundFinal,
        TimeSpan timeout, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (attemptId == Guid.Empty) throw new ArgumentException("ProductionImageAttemptRequired", nameof(attemptId));
        RequireRelativeName(temporaryFileName, ".tmp");
        RequireRelativeName(finalFileName, ".png");
        if (temporaryFileName != work.Manifest.ManifestId.ToString("N") + "." + attemptId.ToString("N") + ".tmp" ||
            finalFileName != work.Manifest.ManifestId.ToString("N") + ".png" ||
            work.Manifest.StageRootBindingHash != _stage.ContentHash)
            throw new InvalidOperationException("ProductionImageFinalizationBindingMismatch");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
        token.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _operationSlot, 1, 0) != 0)
            throw new InvalidOperationException("ProductionImageFinalizationInProgress");
        var operation = new FileOperation(work, attemptId, temporaryFileName, finalFileName,
            allowPreviouslyBoundFinal, new StoreDeadline(timeout));
        var retired = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _physicalCompletion, retired.Task);
        Task worker;
        try
        {
            worker = Task.Run(() =>
            {
                try
                {
                    VerifiedPngCommitClaim? claim = Run(operation);
                    try
                    {
                        _faultHook?.Invoke(ImageFinalizationBoundary.BeforeClaimPublication);
                        if (operation.TryPublish(claim)) claim = null;
                    }
                    finally { claim?.Dispose(); }
                }
                finally
                {
                    Interlocked.Exchange(ref _operationSlot, 0);
                    retired.TrySetResult(null);
                }
            });
        }
        catch
        {
            Interlocked.Exchange(ref _operationSlot, 0);
            retired.TrySetResult(null);
            throw;
        }
        _ = worker.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return AwaitAsync(operation, worker, token);
    }

    private async Task<VerifiedPngCommitClaim> AwaitAsync(FileOperation operation, Task worker,
        CancellationToken token)
    {
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task completed;
        do
        {
            var remaining = operation.Deadline.Remaining;
            // System timers may wake before the monotonic deadline after truncation
            // to milliseconds. Recheck it before making an irreversible timeout decision.
            var delay = Task.Delay(remaining > TimeSpan.FromMilliseconds(1) ? remaining : TimeSpan.FromMilliseconds(1),
                delayCancellation.Token);
            completed = await Task.WhenAny(worker, delay).ConfigureAwait(false);
        } while (completed != worker && !token.IsCancellationRequested && !operation.Deadline.Expired);
        if (token.IsCancellationRequested)
        {
            operation.Abandon();
            throw new OperationCanceledException("ProductionImageFinalizationCancelled", token);
        }
        if (completed == worker)
        {
            delayCancellation.Cancel();
            await worker.ConfigureAwait(false);
            return operation.TakeClaim() ?? throw new TimeoutException("ProductionImageFinalizationDeadlineExceeded");
        }
        // A claim published within the deadline remains usable for its separate SQL budget.
        _faultHook?.Invoke(ImageFinalizationBoundary.BeforeTimeoutDecision);
        if (operation.TakeClaim() is { } claim) return claim;
        operation.Abandon();
        throw new TimeoutException("ProductionImageFinalizationDeadlineExceeded");
    }

    private VerifiedPngCommitClaim Run(FileOperation operation)
    {
        operation.Check();
        RequireRoots();
        var manifest = operation.Work.Manifest;
        var descriptor = Descriptor(manifest);
        var finalPath = Path.Combine(_finalRoot, operation.FinalFileName);
        var temporaryPath = Path.Combine(_finalRoot, operation.TemporaryFileName);
        if (File.Exists(finalPath))
        {
            if (!operation.AllowPreviouslyBoundFinal)
                throw new InvalidOperationException("ProductionImageFinalPathAlreadyOccupied");
            return ProtectVerifiedFinal(operation, finalPath, descriptor);
        }
        Raise(ImageFinalizationBoundary.BeforeStageOpen, operation);
        // Stage data is the entire canonical envelope and tight rows, never a released frame.
        using (var stage = OpenProtected(Path.Combine(_stage.StageRoot, manifest.StageFileName)))
        {
            if (File.Exists(temporaryPath))
                throw new InvalidOperationException("ProductionImageTemporaryPathAlreadyOccupied");
            EnsureOutputCapacity(operation);
            using (var temporary = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, CanonicalPngCodec.ChunkBufferBytes, FileOptions.SequentialScan))
            {
                RequireHandlePath(temporary, temporaryPath);
                Raise(ImageFinalizationBoundary.BeforeEncode, operation);
                _ = CanonicalPngCodec.Encode(stage, temporary, descriptor, _limits.Codec, operation.Deadline);
                Raise(ImageFinalizationBoundary.AfterEncode, operation);
                Raise(ImageFinalizationBoundary.BeforeFlush, operation);
                temporary.Flush(flushToDisk: true);
                Raise(ImageFinalizationBoundary.AfterFlush, operation);
                temporary.Position = 0;
                Raise(ImageFinalizationBoundary.BeforeTemporaryVerify, operation);
                _ = CanonicalPngCodec.Verify(temporary, descriptor, _limits.Codec, operation.Deadline);
                Raise(ImageFinalizationBoundary.AfterTemporaryVerify, operation);
            }
            Raise(ImageFinalizationBoundary.BeforeRename, operation);
            RequireRoots();
            File.Move(temporaryPath, finalPath, overwrite: false);
            Raise(ImageFinalizationBoundary.AfterRename, operation);
            // Retain stage until the final handle has been re-opened and verified. The
            // final handle then protects the same pixels through subsequent SQL commit.
            return ProtectVerifiedFinal(operation, finalPath, descriptor);
        }
    }

    private VerifiedPngCommitClaim ProtectVerifiedFinal(FileOperation operation, string path,
        CanonicalPngDescriptor descriptor)
    {
        Raise(ImageFinalizationBoundary.BeforeFinalVerify, operation);
        var protection = OpenProtected(path);
        try
        {
            var verified = CanonicalPngCodec.Verify(protection, descriptor, _limits.Codec, operation.Deadline);
            Raise(ImageFinalizationBoundary.AfterFinalVerify, operation);
            Raise(ImageFinalizationBoundary.BeforeClaim, operation);
            var claim = VerifiedPngCommitClaim.Create(this, _claimIssuer, operation.Work, operation.AttemptId,
                operation.FinalFileName, verified, protection);
            protection = null!;
            return claim;
        }
        finally { protection?.Dispose(); }
    }

    private void EnsureOutputCapacity(FileOperation operation)
    {
        long bytes = 0;
        var count = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(_finalRoot))
        {
            operation.Check();
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidOperationException("ProductionImageFinalRootEntryInvalid");
            if (++count >= _limits.MaximumFinalFiles)
                throw new InvalidOperationException("ProductionImageFinalFileCapacityExceeded");
            bytes = checked(bytes + new FileInfo(path).Length);
            if (bytes > _limits.MaximumFinalBytes - _limits.Codec.MaximumEncodedBytes)
                throw new InvalidOperationException("ProductionImageFinalByteCapacityExceeded");
        }
        operation.Check();
    }

    private void RequireRoots()
    {
        _ = _stage.RequireValidatedRoot();
        if (!StoragePathValidator.TryValidate(new ProductionStoreOptions(Path.Combine(_finalRoot, ".probe")),
            out _, out var reason)) throw new InvalidOperationException("ProductionImageFinalRootRejected:" + reason);
        var stagePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_stage.StageRoot));
        if (_finalRoot.Equals(stagePath, StringComparison.OrdinalIgnoreCase) ||
            _finalRoot.StartsWith(stagePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            stagePath.StartsWith(_finalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ProductionImageRootsMustBeSeparate");
    }

    internal static CanonicalPngDescriptor Descriptor(PendingImageManifest manifest) => new(manifest.Width,
        manifest.Height, manifest.PixelFormat, manifest.ValidBits, manifest.CanonicalPixelHash);

    internal static void RequireRelativeName(string name, string suffix)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128 || !name.EndsWith(suffix, StringComparison.Ordinal) ||
            name.Contains("..", StringComparison.Ordinal) ||
            name.Any(c => c is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and
                not (>= '0' and <= '9') and not '.' and not '_' and not '-'))
            throw new InvalidOperationException("ProductionImageRelativeFileNameInvalid");
    }

    internal static FileStream OpenProtected(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidOperationException("ProductionImageFileKindInvalid");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            CanonicalPngCodec.ChunkBufferBytes, FileOptions.SequentialScan);
        try { RequireHandlePath(stream, path); return stream; }
        catch { stream.Dispose(); throw; }
    }

    private static void RequireHandlePath(FileStream stream, string expectedPath)
    {
        // Validate the opened object, closing the path-check/open reparse race. The file
        // handle denies write/delete sharing and stays open through its authority boundary.
        var name = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(stream.SafeFileHandle, name, (uint)name.Capacity, 0);
        if (length == 0) throw new IOException("ProductionImageHandlePathUnavailable", new Win32Exception(Marshal.GetLastWin32Error()));
        if (length >= name.Capacity) throw new IOException("ProductionImageHandlePathTooLong");
        var actual = name.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(actual, Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ProductionImageHandlePathMismatch");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    private void Raise(ImageFinalizationBoundary boundary, FileOperation operation)
    {
        operation.Check();
        _faultHook?.Invoke(boundary);
        operation.Check();
    }
    private static bool IsHash(string? hash) => hash is { Length: 64 } &&
        hash.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private sealed class FileOperation
    {
        private readonly object _sync = new();
        private bool _abandoned;
        private VerifiedPngCommitClaim? _claim;
        internal FileOperation(PendingImageFinalizationWork work, Guid attemptId, string temporaryFileName,
            string finalFileName, bool allowPreviouslyBoundFinal, StoreDeadline deadline)
        {
            Work = work; AttemptId = attemptId; TemporaryFileName = temporaryFileName;
            FinalFileName = finalFileName; AllowPreviouslyBoundFinal = allowPreviouslyBoundFinal; Deadline = deadline;
        }
        internal PendingImageFinalizationWork Work { get; }
        internal Guid AttemptId { get; }
        internal string TemporaryFileName { get; }
        internal string FinalFileName { get; }
        internal bool AllowPreviouslyBoundFinal { get; }
        internal StoreDeadline Deadline { get; }
        internal void Check()
        {
            lock (_sync)
            {
                if (_abandoned) throw new OperationCanceledException("ProductionImageFinalizationAbandoned");
                if (Deadline.Expired) throw new TimeoutException("ProductionImageFinalizationDeadlineExceeded");
            }
        }
        internal bool TryPublish(VerifiedPngCommitClaim claim)
        {
            lock (_sync)
            {
                if (_abandoned || Deadline.Expired) return false;
                _claim = claim;
                return true;
            }
        }
        internal VerifiedPngCommitClaim? TakeClaim()
        {
            lock (_sync) { var claim = _claim; _claim = null; return claim; }
        }
        internal void Abandon()
        {
            VerifiedPngCommitClaim? claim;
            lock (_sync) { _abandoned = true; claim = _claim; _claim = null; }
            claim?.Dispose();
        }
    }
}
