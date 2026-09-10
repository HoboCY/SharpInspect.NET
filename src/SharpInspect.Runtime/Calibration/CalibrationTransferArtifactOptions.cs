using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Calibration;

/// <summary>
/// Explicit bounds for the content-addressed Calibration Transfer artifact directory.
/// The directory contains opaque, untrusted package bytes only; it is not a trust or
/// production-authority store.  A caller must supply an existing local ArtifactRoot.
/// </summary>
public sealed class CalibrationTransferArtifactOptions
{
    /// <summary>The largest package accepted by the public package contract.</summary>
    public const int MaximumPackageBytesHardLimit = CalibrationExportPackage.MaximumBytes;

    /// <summary>The largest aggregate size of committed, partial, and orphan files.</summary>
    public const long MaximumTotalBytesHardLimit = 1L * 1024 * 1024 * 1024;

    /// <summary>Default per-package bound: 16 MiB.</summary>
    public const int DefaultMaximumPackageBytes = 16 * 1024 * 1024;

    /// <summary>Default aggregate bound: 256 MiB.</summary>
    public const long DefaultMaximumTotalBytes = 256L * 1024 * 1024;

    /// <summary>Default number of queued or running file operations per store instance.</summary>
    public const int DefaultQueueCapacity = 4;

    /// <summary>Maximum number of queued or running file operations per instance.</summary>
    public const int MaximumQueueCapacityHardLimit = 64;

    /// <summary>Maximum operation deadline accepted by this bounded store.</summary>
    public static readonly TimeSpan MaximumOperationTimeoutHardLimit = TimeSpan.FromMinutes(5);

    /// <summary>Existing local directory in which opaque package blobs are retained.</summary>
    public string ArtifactRoot { get; init; } = string.Empty;

    /// <summary>Per-package byte bound. It may not exceed the public 64 MiB contract.</summary>
    public int MaximumPackageBytes { get; init; } = DefaultMaximumPackageBytes;

    /// <summary>
    /// Aggregate byte bound. Existing files with unknown names, orphan files and partial
    /// files all consume this quota, so a failed write cannot silently free capacity.
    /// </summary>
    public long MaximumTotalBytes { get; init; } = DefaultMaximumTotalBytes;

    /// <summary>Bound on admitted operations for this store instance.</summary>
    public int QueueCapacity { get; init; } = DefaultQueueCapacity;

    /// <summary>
    /// Deadline for queue admission and the actual file operation. Cancellation after
    /// admission does not interrupt retirement bookkeeping; the worker retires the item
    /// before returning its queue capacity.
    /// </summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public CalibrationTransferArtifactOptions()
    {
    }

    public CalibrationTransferArtifactOptions(string artifactRoot)
    {
        ArtifactRoot = artifactRoot;
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ArtifactRoot) ||
            !Path.IsPathFullyQualified(ArtifactRoot) ||
            ArtifactRoot.Any(char.IsControl))
            throw new ArgumentException("CalibrationTransferArtifactRootInvalid", nameof(ArtifactRoot));

        if (MaximumPackageBytes is < 1 or > MaximumPackageBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPackageBytes),
                "CalibrationTransferPackageCapacityInvalid");

        if (MaximumTotalBytes is < 1 or > MaximumTotalBytesHardLimit ||
            MaximumTotalBytes < MaximumPackageBytes)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes),
                "CalibrationTransferTotalCapacityInvalid");

        if (QueueCapacity is < 1 or > MaximumQueueCapacityHardLimit)
            throw new ArgumentOutOfRangeException(nameof(QueueCapacity),
                "CalibrationTransferQueueCapacityInvalid");

        if (OperationTimeout <= TimeSpan.Zero ||
            OperationTimeout > MaximumOperationTimeoutHardLimit)
            throw new ArgumentOutOfRangeException(nameof(OperationTimeout),
                "CalibrationTransferOperationTimeoutInvalid");
    }
}

/// <summary>
/// Immutable proof of the exact bytes retained by the transfer artifact store.
/// The receipt carries identity only; it does not validate the package or grant any
/// calibration, local-collection, publication, or production authority.
/// </summary>
internal sealed record CalibrationTransferArtifactReceipt
{
    internal CalibrationTransferArtifactReceipt(string sha256, int length)
    {
        if (!CalibrationTransferPackageStore.TryNormalizeHash(sha256, out var canonical))
            throw new ArgumentException("CalibrationTransferArtifactHashInvalid", nameof(sha256));
        if (length < 1 || length > CalibrationExportPackage.MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(length));

        Sha256 = canonical;
        Length = length;
    }

    /// <summary>Canonical uppercase SHA-256 of the exact opaque package bytes.</summary>
    public string Sha256 { get; }

    /// <summary>Length of the exact opaque package bytes.</summary>
    public int Length { get; }

    // These aliases keep the receipt usable by callers that use the package vocabulary.
    public string ContentHash => Sha256;
    public string Hash => Sha256;
    public int ByteLength => Length;
    public string RelativePath => Sha256 + ".bin";
}
