using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// The qualified, existing local final-image root. Deployment configuration only: it is never
/// Recipe content and never a caller-supplied path at write time. The declared binding is part
/// of the immutable configuration row, so a store can never be opened against a root whose
/// location or capacity bounds changed.
/// </summary>
public sealed class ProductionImageFinalizationRootOptions
{
    /// <summary>Upper bound accepted for one final PNG file (512 MiB).</summary>
    public const long MaximumAllowedFinalFileBytes = 512L * 1024 * 1024;

    /// <summary>Upper bound accepted for every file under the final root (8 GiB).</summary>
    public const long MaximumAllowedTotalFinalBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>Upper bound accepted for the number of files under the final root.</summary>
    public const int MaximumAllowedFinalFiles = 100_000;

    public ProductionImageFinalizationRootOptions(string finalRoot, long maximumFinalFileBytes,
        long maximumTotalFinalBytes, int maximumFinalFiles)
    {
        if (string.IsNullOrWhiteSpace(finalRoot))
            throw new ArgumentException("ProductionImageFinalizationRootRequired", nameof(finalRoot));
        if (!Path.IsPathFullyQualified(finalRoot) ||
            finalRoot.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            finalRoot.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            finalRoot.StartsWith(@"\??\", StringComparison.Ordinal))
            throw new ArgumentException("ProductionImageFinalizationRootMustBeExplicitLocalPath",
                nameof(finalRoot));
        string normalized;
        try
        {
            normalized = Normalize(finalRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
                                           PathTooLongException)
        {
            throw new ArgumentException("ProductionImageFinalizationRootInvalid", nameof(finalRoot),
                exception);
        }
        if (!Directory.Exists(normalized))
            throw new ArgumentException("ProductionImageFinalizationRootMissing", nameof(finalRoot));
        if (maximumFinalFileBytes is < 1 or > MaximumAllowedFinalFileBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumFinalFileBytes));
        if (maximumTotalFinalBytes < maximumFinalFileBytes ||
            maximumTotalFinalBytes > MaximumAllowedTotalFinalBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumTotalFinalBytes));
        if (maximumFinalFiles is < 1 or > MaximumAllowedFinalFiles)
            throw new ArgumentOutOfRangeException(nameof(maximumFinalFiles));
        FinalRoot = normalized;
        MaximumFinalFileBytes = maximumFinalFileBytes;
        MaximumTotalFinalBytes = maximumTotalFinalBytes;
        MaximumFinalFiles = maximumFinalFiles;
        ContentHash = ComputeContentHash();
        _ = RequireValidatedRoot();
    }

    /// <summary>Normalized fully qualified final root that must already exist locally.</summary>
    public string FinalRoot { get; }

    public long MaximumFinalFileBytes { get; }

    public long MaximumTotalFinalBytes { get; }

    public int MaximumFinalFiles { get; }

    /// <summary>Binds the normalized root and every limit; persisted instead of a raw path.</summary>
    public string ContentHash { get; }

    /// <summary>
    /// Re-runs the fixed/local/NTFS/no-cloud/no-reparse checks before a write. The root is
    /// revalidated rather than trusted from construction time.
    /// </summary>
    internal string RequireValidatedRoot()
    {
        var probe = Path.Combine(FinalRoot, ".probe");
        if (!StoragePathValidator.TryValidate(new ProductionStoreOptions(probe), out _, out var reason))
            throw new InvalidOperationException("ProductionImageFinalizationRootRejected:" + reason);
        return FinalRoot;
    }

    private static string Normalize(string root)
    {
        var full = Path.GetFullPath(root);
        return full.Length > 3
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }

    private string ComputeContentHash()
    {
        var canonical = new StringBuilder()
            .Append("SharpInspect.ProductionImageFinalizationRootBinding|1|")
            .Append(FinalRoot.ToUpperInvariant()).Append('|')
            .Append(MaximumFinalFileBytes.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(MaximumTotalFinalBytes.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(MaximumFinalFiles.ToString(CultureInfo.InvariantCulture))
            .ToString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>
/// Explicit, bounded, optional storage for the schema-35 production image finalization
/// lifecycle. The ledger stores only immutable finalization facts and the current per-work
/// projection; the authorized local NTFS final root owns every final PNG file. The declared
/// final root binding and every limit are hashed into the immutable configuration row, so a
/// store can never be opened against different deployment bounds. One physical worker is
/// fixed by design (WorkerCount is always one); this type never starts a worker.
/// </summary>
public sealed class ProductionImageFinalizationStoreOptions
{
    internal const int SchemaVersion = 35;
    internal const int FormatVersion = 1;
    internal const int MaximumAttemptsHardLimit = 32;
    internal const int MaximumEventsHardLimit = 100_000;
    internal const int MaximumPayloadBytesHardLimit = 1024 * 1024;
    internal const long MaximumTotalBytesHardLimit = 512L * 1024 * 1024;
    internal const int MaximumPageSizeHardLimit = 512;
    internal const int MaximumAuditPayloadBytes = MaximumPayloadBytesHardLimit * 2 + 32 * 1024;
    internal const int ControlVerificationReserve = 64;
    internal const int AuditEntriesPerEvent = 1;
    internal const int SqliteValueLimitBytes = 16 * 1024 * 1024;

    /// <summary>Future Succeeded plus StageReleased facts reserved for one open obligation.</summary>
    internal const int ReserveEventsPerOpenObligation = 2;

    /// <summary>The StageReleased fact still reserved for one succeeded-but-unreleased work.</summary>
    internal const int ReserveEventsPerUnreleasedSuccess = 1;

    /// <summary>The terminal failure fact reserved for one active attempt.</summary>
    internal const int ReserveEventsPerActiveAttempt = 1;

    /// <summary>The fixed single writer worker; the Runtime never runs a second finalizer.</summary>
    internal const int FixedWorkerCount = 1;

    public ProductionImageFinalizationStoreOptions(ProductionImageFinalizationRootOptions finalRoot,
        ProductionImageEvidenceStoreOptions imageEvidence)
    {
        FinalRoot = finalRoot ?? throw new ArgumentNullException(nameof(finalRoot));
        ImageEvidence = imageEvidence ?? throw new ArgumentNullException(nameof(imageEvidence));
    }

    /// <summary>The qualified, existing local final root the evidence files are bound to.</summary>
    public ProductionImageFinalizationRootOptions FinalRoot { get; }

    /// <summary>
    /// The exact image evidence store this ledger finalizes. Its binding hash is part of this
    /// store's immutable configuration, so a finalization ledger can never be opened against a
    /// different evidence store than the one it committed to.
    /// </summary>
    public ProductionImageEvidenceStoreOptions ImageEvidence { get; }

    /// <summary>Persisted per-work attempt budget; a later successful recovery is never blocked by it.</summary>
    public int MaximumAttempts { get; init; } = 5;

    /// <summary>Upper bound of one automatically retryable delay.</summary>
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    public int MaximumEvents { get; init; } = 20_000;
    public int MaximumPayloadBytes { get; init; } = 64 * 1024;
    public long MaximumTotalBytes { get; init; } = 128L * 1024 * 1024;
    public int MaximumPageSize { get; init; } = 128;

    /// <summary>The always-fixed single physical worker count.</summary>
    public int WorkerCount => FixedWorkerCount;

    /// <summary>The exact qualified final-root binding stored in the configuration row.</summary>
    internal string FinalRootBindingHash => FinalRoot.ContentHash;

    /// <summary>The exact image evidence binding this ledger is coupled to.</summary>
    internal string ImageEvidenceBindingHash => ImageEvidence.BindingHash;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(FinalRoot);
        ArgumentNullException.ThrowIfNull(ImageEvidence);
        if (MaximumAttempts is < 1 or > MaximumAttemptsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumAttempts),
                "ImageFinalizationAttemptCapacityInvalid");
        if (MaximumRetryDelay < TimeSpan.FromMilliseconds(1) ||
            MaximumRetryDelay > TimeSpan.FromMinutes(60))
            throw new ArgumentOutOfRangeException(nameof(MaximumRetryDelay),
                "ImageFinalizationRetryDelayInvalid");
        if (MaximumEvents is < 1 or > MaximumEventsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumEvents),
                "ImageFinalizationEntryCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes),
                "ImageFinalizationPayloadCapacityInvalid");
        if (MaximumTotalBytes < MaximumPayloadBytes || MaximumTotalBytes > MaximumTotalBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes),
                "ImageFinalizationTotalCapacityInvalid");
        if (MaximumPageSize is < 1 or > MaximumPageSizeHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPageSize),
                "ImageFinalizationPageCapacityInvalid");
        if (!IsHash(FinalRootBindingHash))
            throw new ArgumentException("ImageFinalizationRootBindingInvalid", nameof(FinalRoot));
        if (!IsHash(ImageEvidenceBindingHash))
            throw new ArgumentException("ImageFinalizationEvidenceBindingInvalid", nameof(ImageEvidence));
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("ProductionImageFinalizationStoreOptions", Number(FormatVersion),
            FinalRootBindingHash, ImageEvidenceBindingHash, Number(MaximumAttempts),
            Number((long)MaximumRetryDelay.TotalMilliseconds),
            Number(MaximumEvents), Number(MaximumPayloadBytes), Number(MaximumTotalBytes),
            Number(MaximumPageSize), Number(FixedWorkerCount), Number(ControlVerificationReserve));
    }

    internal byte[] EncodeActivationPayload() => AuditCanonical.Encode("ImageFinalizationStoreActivated",
        Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static void ConfigureSqliteLimit(sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    }

    private static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
