using System.Buffers.Binary;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Images;

/// <summary>
/// Physical boundaries of the stage file protocol at which the internal fault hook can
/// throw, pause or corrupt. The hook is a deterministic test seam, never deployment input.
/// </summary>
internal enum StageBoundary
{
    BeforeWrite,
    AfterWrite,
    BeforeFlush,
    AfterFlush,
    BeforeVerify,
    AfterVerify,
    BeforeRename,
    AfterRename
}

/// <summary>
/// Single-consumer staging of required image pixels before result publication (ADR-0069).
/// The stager owns at most one physically unfinished stage operation and rejects another
/// immediately instead of queueing it. All file work runs on the thread pool, so blocking
/// flushes and same-volume renames never block the calling runtime; the outer await returns
/// promptly at the monotonic stage deadline, and only the worker finally disposes the
/// retained frame. An abandoned worker may finish file I/O, but it can never mint a usable
/// late claim.
/// </summary>
internal sealed class ProductionImageStager
{
    private const int FileBufferBytes = 64 * 1024;
    private const int CanonicalHeaderLength = 32;

    private readonly ProductionImageStageOptions _options;
    private readonly Action<StageBoundary>? _faultHook;
    private readonly object _claimIssuer = new();

    private int _operationSlot;
    private int _activeOperations;
    private Task _physicalCompletion = Task.CompletedTask;

    internal ProductionImageStager(ProductionImageStageOptions options) : this(options, null)
    {
    }

    internal ProductionImageStager(ProductionImageStageOptions options, Action<StageBoundary>? faultHook)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _faultHook = faultHook;
    }

    /// <summary>Physically unfinished stage operations; retirement waits for zero.</summary>
    internal int ActiveOperationCount => Volatile.Read(ref _activeOperations);

    /// <summary>Completes when the most recently admitted physical operation has finished.</summary>
    internal Task PhysicalCompletion => Volatile.Read(ref _physicalCompletion);

    /// <summary>
    /// Stages the retained frame's canonical pixels. Ownership of the frame transfers on
    /// call: every rejected or failed call disposes it once its actual operation ends.
    /// Outcome before the deadline is a claim; outcome after it is only an orphan file.
    /// </summary>
    internal Task<StageCommitClaim> StageAsync(Guid inspectionId, string admissionContentHash,
        string evidencePolicyContentHash, RetainedProductionFrame frame, TimeSpan timeout,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var admitted = false;
        try
        {
            if (inspectionId == Guid.Empty)
                throw new ArgumentException("ProductionStageInspectionIdRequired", nameof(inspectionId));
            RequireContentHash(admissionContentHash, nameof(admissionContentHash));
            RequireContentHash(evidencePolicyContentHash, nameof(evidencePolicyContentHash));
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
                throw new ArgumentOutOfRangeException(nameof(timeout));
            if (Interlocked.CompareExchange(ref _operationSlot, 1, 0) != 0)
                throw new InvalidOperationException("ProductionStageOperationInProgress");
            if (token.IsCancellationRequested)
            {
                Volatile.Write(ref _operationSlot, 0);
                throw new OperationCanceledException("ProductionStageCancelled", token);
            }
            admitted = true;
        }
        finally
        {
            if (!admitted) frame.Dispose();
        }

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new StageOperation(inspectionId, admissionContentHash,
            evidencePolicyContentHash, frame, new StoreDeadline(timeout), completion);
        Interlocked.Exchange(ref _activeOperations, 1);
        Volatile.Write(ref _physicalCompletion, completion.Task);
        Task<StageCommitClaim?> worker;
        try
        {
            worker = Task.Run(() => RunStageWorker(operation));
        }
        catch
        {
            frame.Dispose();
            Interlocked.Exchange(ref _activeOperations, 0);
            Volatile.Write(ref _operationSlot, 0);
            completion.TrySetResult(null);
            throw;
        }
        ObserveLateFailure(worker);
        return AwaitStageAsync(operation, worker, token);
    }

    private async Task<StageCommitClaim> AwaitStageAsync(StageOperation operation,
        Task<StageCommitClaim?> worker, CancellationToken token)
    {
        var remaining = operation.Deadline.Remaining;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        var delay = Task.Delay(remaining, wait.Token);
        var completed = await Task.WhenAny(worker, delay).ConfigureAwait(false);
        if (token.IsCancellationRequested)
        {
            operation.Abandon();
            throw new OperationCanceledException("ProductionStageCancelled", token);
        }
        if (completed == worker)
        {
            wait.Cancel();
            // The worker is the only claim constructor; null means it was abandoned first.
            return await worker.ConfigureAwait(false) ??
                throw new InvalidOperationException("ProductionStageClaimUnavailable");
        }
        // 计时器触发时可能已有凭据在截止前发布；只接纳该凭据，截止后才完成的结果必须作废。
        if (operation.TryTakePublishedClaim() is { } published)
        {
            wait.Cancel();
            return published;
        }
        operation.Abandon();
        if (token.IsCancellationRequested)
            throw new OperationCanceledException("ProductionStageCancelled", token);
        throw new TimeoutException("ProductionStageDeadlineExceeded");
    }

    private StageCommitClaim? RunStageWorker(StageOperation operation)
    {
        try
        {
            var claim = StagePixels(operation);
            if (operation.TryPublishClaim(claim)) return claim;
            // Winner of the abandonment race: the late claim never becomes usable.
            claim.MarkAbandoned();
            claim.Dispose();
            return null;
        }
        finally
        {
            // 只有实际文件操作结束后才能释放借用帧和槽位；上层超时不能提前归还仍在读取的像素。
            operation.Frame.Dispose();
            Interlocked.Exchange(ref _activeOperations, 0);
            Volatile.Write(ref _operationSlot, 0);
            operation.Completion.TrySetResult(null);
        }
    }

    private StageCommitClaim StagePixels(StageOperation operation)
    {
        var frame = operation.Frame;
        var metadata = frame.Metadata;
        var root = _options.RequireValidatedRoot();
        var envelope = CanonicalImagePixelContent.CreateEnvelope(metadata.Width, metadata.Height,
            metadata.PixelFormat, metadata.ValidBits);
        if (envelope.Length != CanonicalHeaderLength)
            throw new InvalidOperationException("ProductionStageEnvelopeInvalid");
        var pixelBytes = checked((long)metadata.ValidRowBytes * metadata.Height);
        var canonicalByteLength = checked(pixelBytes + envelope.Length);
        EnsureCapacity(root, canonicalByteLength);
        var canonicalHash = CanonicalImagePixelContent.ComputeHash(frame, CancellationToken.None);
        RequireCanonicalHash(canonicalHash);

        var stageId = Guid.NewGuid();
        var stageFileName = stageId.ToString("N") + ".stage";
        var partialPath = Path.Combine(root, stageId.ToString("N") + ".partial");
        var stagePath = Path.Combine(root, stageFileName);

        Raise(StageBoundary.BeforeWrite);
        using (var writer = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, FileBufferBytes, FileOptions.WriteThrough))
        {
            // 暂存内容只包含规范头和每行有效像素，排除步长填充，保证相同图像得到相同摘要。
            writer.Write(envelope);
            for (var row = 0; row < metadata.Height; row++)
                writer.Write(frame.GetRowSpan(row));
            Raise(StageBoundary.AfterWrite);
            Raise(StageBoundary.BeforeFlush);
            writer.Flush(flushToDisk: true);
            Raise(StageBoundary.AfterFlush);
        }
        Raise(StageBoundary.BeforeVerify);
        using (var verification = OpenRead(partialPath))
            VerifyStageContent(verification, envelope, canonicalHash, metadata, canonicalByteLength);
        Raise(StageBoundary.AfterVerify);
        Raise(StageBoundary.BeforeRename);
        File.Move(partialPath, stagePath, overwrite: false);
        Raise(StageBoundary.AfterRename);
        var protection = OpenProtection(stagePath);
        try
        {
            // 重命名后重新打开的文件可能已被替换；从持有的句柄重验全文，并保护到凭据消费或释放。
            VerifyStageContent(protection, envelope, canonicalHash, metadata, canonicalByteLength);
            if (operation.Deadline.Expired)
                throw new TimeoutException("ProductionStageDeadlineExceeded");
        }
        catch
        {
            protection.Dispose();
            throw;
        }
        return StageCommitClaim.Create(this, _claimIssuer, stageId, operation.InspectionId, operation.AdmissionContentHash,
            operation.EvidencePolicyContentHash, frame.LeaseId, metadata, frame.Provenance,
            canonicalHash, canonicalByteLength, stageFileName, stagePath, _options.ContentHash,
            completedWithinDeadline: !operation.Deadline.Expired, protection);
    }

    private void EnsureCapacity(string root, long additionalBytes)
    {
        if (additionalBytes > _options.MaximumStageBytes)
            throw new InvalidOperationException("ProductionStageFileSizeExceeded");
        long bytes = 0;
        var files = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (Directory.Exists(entry))
                throw new InvalidOperationException("ProductionStageRootEntryInvalid");
            files++;
            bytes = checked(bytes + new FileInfo(entry).Length);
            if (files > _options.MaximumStageFiles)
                throw new InvalidOperationException("ProductionStageFileCapacityExceeded");
            if (bytes > _options.MaximumTotalStageBytes - additionalBytes)
                throw new InvalidOperationException("ProductionStageTotalCapacityExceeded");
        }
        if (files + 1 > _options.MaximumStageFiles)
            throw new InvalidOperationException("ProductionStageFileCapacityExceeded");
        if (bytes + additionalBytes > _options.MaximumTotalStageBytes)
            throw new InvalidOperationException("ProductionStageTotalCapacityExceeded");
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read, FileBufferBytes, FileOptions.SequentialScan);

    private static FileStream OpenProtection(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                FileBufferBytes, FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("ProductionStageProtectionOpenFailed", exception);
        }
    }

    /// <summary>
    /// Full structural readback: exact length, canonical envelope equality, Mono16 valid-bit
    /// ceiling per row, and SHA-256 over the whole file. The file is exactly the canonical
    /// envelope followed by the valid rows, so its digest is the canonical pixel hash.
    /// </summary>
    private static void VerifyStageContent(Stream stream, byte[] envelope, string expectedHash,
        FrameMetadata metadata, long expectedLength)
    {
        if (stream.Length != expectedLength)
            throw new InvalidOperationException("ProductionStageLengthMismatch");
        stream.Position = 0;
        var header = new byte[envelope.Length];
        ReadExactly(stream, header);
        if (!header.AsSpan().SequenceEqual(envelope))
            throw new InvalidOperationException("ProductionStageEnvelopeMismatch");
        var rows = new byte[metadata.ValidRowBytes];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header);
        for (var row = 0; row < metadata.Height; row++)
        {
            ReadExactly(stream, rows);
            if (metadata.PixelFormat == VisionPixelFormat.Mono16 && metadata.ValidBits != 16)
            {
                var maximum = (1 << metadata.ValidBits!.Value) - 1;
                for (var offset = 0; offset < rows.Length; offset += 2)
                {
                    if (BinaryPrimitives.ReadUInt16LittleEndian(rows.AsSpan(offset, 2)) > maximum)
                        throw new InvalidOperationException("ProductionStageMono16HighBitsInvalid");
                }
            }
            hash.AppendData(rows);
        }
        if (stream.Position != stream.Length)
            throw new InvalidOperationException("ProductionStageLengthMismatch");
        var digest = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(digest, expectedHash, StringComparison.Ordinal))
            throw new InvalidOperationException("ProductionStageHashMismatch");
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
                throw new InvalidOperationException("ProductionStageTruncated");
            offset += read;
        }
    }

    private void Raise(StageBoundary boundary) => _faultHook?.Invoke(boundary);

    private static void RequireContentHash(string value, string parameterName)
    {
        if (value is null) throw new ArgumentNullException(parameterName);
        if (!IsUppercaseSha256(value))
            throw new ArgumentException("ProductionStageHashInvalid", parameterName);
    }

    private static void RequireCanonicalHash(string value)
    {
        if (!IsUppercaseSha256(value))
            throw new InvalidOperationException("ProductionStageHashInvalid");
    }

    private static bool IsUppercaseSha256(string value)
    {
        if (value.Length != 64) return false;
        foreach (var character in value)
        {
            if (!(character is >= '0' and <= '9' or >= 'A' and <= 'F')) return false;
        }
        return true;
    }

    private static void ObserveLateFailure(Task worker) =>
        _ = worker.ContinueWith(static finished => _ = finished.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private sealed class StageOperation
    {
        private readonly object _sync = new();
        private bool _abandoned;
        private StageCommitClaim? _publishedClaim;

        internal StageOperation(Guid inspectionId, string admissionContentHash,
            string evidencePolicyContentHash, RetainedProductionFrame frame, StoreDeadline deadline,
            TaskCompletionSource<object?> completion)
        {
            InspectionId = inspectionId;
            AdmissionContentHash = admissionContentHash;
            EvidencePolicyContentHash = evidencePolicyContentHash;
            Frame = frame;
            Deadline = deadline;
            Completion = completion;
        }

        internal Guid InspectionId { get; }
        internal string AdmissionContentHash { get; }
        internal string EvidencePolicyContentHash { get; }
        internal RetainedProductionFrame Frame { get; }
        internal StoreDeadline Deadline { get; }
        internal TaskCompletionSource<object?> Completion { get; }

        internal bool TryPublishClaim(StageCommitClaim claim)
        {
            lock (_sync)
            {
                if (_abandoned || Deadline.Expired) return false;
                _publishedClaim = claim;
                return true;
            }
        }

        internal StageCommitClaim? TryTakePublishedClaim()
        {
            lock (_sync)
            {
                var claim = _publishedClaim;
                _publishedClaim = null;
                return claim;
            }
        }

        internal void Abandon()
        {
            StageCommitClaim? orphan;
            lock (_sync)
            {
                _abandoned = true;
                orphan = _publishedClaim;
                _publishedClaim = null;
            }
            if (orphan is null) return;
            orphan.MarkAbandoned();
            orphan.Dispose();
        }
    }

    /// <summary>
    /// Immutable binding of one verified stage file to its inspection and policy hashes.
    /// Only the successful stage worker constructs it, and it is single-use: it protects the
    /// staged file with an open read handle until the SQL commit or rejection disposes it.
    /// </summary>
    internal sealed class StageCommitClaim : IDisposable
    {
        private readonly object _sync = new();
        private readonly ProductionImageStager _owner;
        private FileStream? _protection;
        private bool _abandoned;
        private bool _consumed;
        private bool _disposed;

        internal static StageCommitClaim Create(ProductionImageStager owner, object issuer,
            Guid stageId, Guid inspectionId, string admissionContentHash,
            string evidencePolicyContentHash, Guid inputLeaseId, FrameMetadata metadata,
            FrameProvenance provenance, string canonicalPixelHash, long canonicalByteLength,
            string stageFileName, string stageFilePath, string stageRootBindingHash,
            bool completedWithinDeadline, FileStream protection)
        {
            if (!ReferenceEquals(owner._claimIssuer, issuer))
                throw new InvalidOperationException("ProductionStageClaimIssuerInvalid");
            return new StageCommitClaim(owner, stageId, inspectionId, admissionContentHash,
                evidencePolicyContentHash, inputLeaseId, metadata, provenance, canonicalPixelHash,
                canonicalByteLength, stageFileName, stageFilePath, stageRootBindingHash,
                completedWithinDeadline, protection);
        }

        private StageCommitClaim(ProductionImageStager owner, Guid stageId, Guid inspectionId, string admissionContentHash,
            string evidencePolicyContentHash, Guid inputLeaseId, FrameMetadata metadata,
            FrameProvenance provenance, string canonicalPixelHash, long canonicalByteLength,
            string stageFileName, string stageFilePath, string stageRootBindingHash,
            bool completedWithinDeadline, FileStream protection)
        {
            _owner = owner;
            StageId = stageId;
            InspectionId = inspectionId;
            AdmissionContentHash = admissionContentHash;
            EvidencePolicyContentHash = evidencePolicyContentHash;
            InputLeaseId = inputLeaseId;
            Metadata = metadata;
            Provenance = provenance;
            CanonicalPixelHash = canonicalPixelHash;
            CanonicalByteLength = canonicalByteLength;
            StageFileName = stageFileName;
            StageFilePath = stageFilePath;
            StageRootBindingHash = stageRootBindingHash;
            CompletedWithinDeadline = completedWithinDeadline;
            _protection = protection;
        }

        internal Guid StageId { get; }
        internal Guid InspectionId { get; }
        internal string AdmissionContentHash { get; }
        internal string EvidencePolicyContentHash { get; }
        internal Guid InputLeaseId { get; }
        internal FrameMetadata Metadata { get; }
        internal FrameProvenance Provenance { get; }
        internal string CanonicalPixelHash { get; }

        /// <summary>Canonical pixels plus the fixed 32-byte envelope.</summary>
        internal long CanonicalByteLength { get; }

        /// <summary>Relative file name; canonical records bind this with the root binding hash.</summary>
        internal string StageFileName { get; }

        /// <summary>Full local path of the protected stage file; held for the open handle only.</summary>
        internal string StageFilePath { get; }
        internal string StageRootBindingHash { get; }
        internal bool CompletedWithinDeadline { get; }
        internal bool IsConsumed { get { lock (_sync) return _consumed; } }
        internal bool IsDisposed { get { lock (_sync) return _disposed; } }

        /// <summary>
        /// Pre-COMMIT check that the staged file is still protected by the held handle. It
        /// performs no filesystem I/O, so it is safe on the database thread.
        /// </summary>
        internal void VerifyCommitProtection()
        {
            lock (_sync)
            {
                if (_abandoned)
                    throw new InvalidOperationException("ProductionStageClaimAbandoned");
                if (_disposed || _protection is null || _protection.SafeFileHandle.IsClosed ||
                    _owner._options.ContentHash != StageRootBindingHash)
                    throw new InvalidOperationException("ProductionStageCommitProtectionLost");
            }
        }

        /// <summary>
        /// Single-use consumption for the commit that references this stage. The recorded
        /// values must match the caller's run, policy and root binding; the stage deadline
        /// measured the file barrier, so later SQL latency never invalidates a claim that
        /// completed inside it while still protected.
        /// </summary>
        internal void ConsumeForCommit(Guid inspectionId, string admissionContentHash,
            string evidencePolicyContentHash, string rootBindingHash)
        {
            lock (_sync)
            {
                if (_abandoned)
                    throw new InvalidOperationException("ProductionStageClaimAbandoned");
                if (_disposed)
                    throw new InvalidOperationException("ProductionStageClaimDisposed");
                if (_protection is null || _protection.SafeFileHandle.IsClosed ||
                    _owner._options.ContentHash != StageRootBindingHash)
                    throw new InvalidOperationException("ProductionStageCommitProtectionLost");
                if (!CompletedWithinDeadline)
                    throw new InvalidOperationException("ProductionStageClaimDeadlineExceeded");
                if (inspectionId != InspectionId ||
                    !string.Equals(admissionContentHash, AdmissionContentHash, StringComparison.Ordinal) ||
                    !string.Equals(evidencePolicyContentHash, EvidencePolicyContentHash, StringComparison.Ordinal) ||
                    !string.Equals(rootBindingHash, StageRootBindingHash, StringComparison.Ordinal))
                    throw new InvalidOperationException("ProductionStageClaimBindingMismatch");
                if (_consumed)
                    throw new InvalidOperationException("ProductionStageClaimConsumed");
                _consumed = true;
            }
        }

        internal void MarkAbandoned()
        {
            lock (_sync)
            {
                _abandoned = true;
            }
        }

        public void Dispose()
        {
            FileStream? handle;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                handle = _protection;
                _protection = null;
            }
            handle?.Dispose();
        }
    }
}
