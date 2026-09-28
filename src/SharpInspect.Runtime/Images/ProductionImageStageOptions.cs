using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Images;

/// <summary>
/// Bounded local deployment settings for the required-image-pixels stage (ADR-0069). This
/// is deployment configuration and never Recipe content, and it claims no
/// sudden-power-loss durability: that boundary belongs to deployment qualification, not
/// to this type.
/// </summary>
public sealed class ProductionImageStageOptions
{
    /// <summary>Upper bound accepted for one staged file, envelope included (512 MiB).</summary>
    public const long MaximumAllowedStageBytes = 512L * 1024 * 1024;

    /// <summary>Upper bound accepted for every file under the staging root (8 GiB).</summary>
    public const long MaximumAllowedTotalStageBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>Upper bound accepted for the number of files under the staging root.</summary>
    public const int MaximumAllowedStageFiles = 10_000;

    public ProductionImageStageOptions(string stageRoot, long maximumStageBytes,
        long maximumTotalStageBytes, int maximumStageFiles)
    {
        if (string.IsNullOrWhiteSpace(stageRoot))
            throw new ArgumentException("ProductionImageStageRootRequired", nameof(stageRoot));
        if (!Path.IsPathFullyQualified(stageRoot) ||
            stageRoot.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            stageRoot.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            stageRoot.StartsWith(@"\??\", StringComparison.Ordinal))
            throw new ArgumentException("ProductionImageStageRootMustBeExplicitLocalPath", nameof(stageRoot));
        string normalized;
        try
        {
            normalized = Normalize(stageRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
                                           PathTooLongException)
        {
            throw new ArgumentException("ProductionImageStageRootInvalid", nameof(stageRoot), exception);
        }
        if (!Directory.Exists(normalized))
            throw new ArgumentException("ProductionImageStageRootMissing", nameof(stageRoot));
        if (maximumStageBytes is < 1 or > MaximumAllowedStageBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumStageBytes));
        if (maximumTotalStageBytes < maximumStageBytes ||
            maximumTotalStageBytes > MaximumAllowedTotalStageBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumTotalStageBytes));
        if (maximumStageFiles is < 1 or > MaximumAllowedStageFiles)
            throw new ArgumentOutOfRangeException(nameof(maximumStageFiles));

        StageRoot = normalized;
        MaximumStageBytes = maximumStageBytes;
        MaximumTotalStageBytes = maximumTotalStageBytes;
        MaximumStageFiles = maximumStageFiles;
        ContentHash = ComputeContentHash(normalized, maximumStageBytes, maximumTotalStageBytes,
            maximumStageFiles);
        _ = RequireValidatedRoot();
    }

    /// <summary>Normalized fully qualified staging root that must already exist locally.</summary>
    public string StageRoot { get; }

    /// <summary>Largest single staged file, canonical envelope included.</summary>
    public long MaximumStageBytes { get; }

    /// <summary>Largest total byte count of every file under the staging root.</summary>
    public long MaximumTotalStageBytes { get; }

    /// <summary>Largest number of files under the staging root.</summary>
    public int MaximumStageFiles { get; }

    /// <summary>
    /// Binds the normalized root and the limits. A canonical pending record binds this
    /// value plus the relative stage file name instead of an absolute persisted locator.
    /// </summary>
    public string ContentHash { get; }

    /// <summary>
    /// Re-runs the fixed/local/NTFS/no-cloud/no-reparse checks before every write. The root
    /// is revalidated rather than trusted from construction time.
    /// </summary>
    internal string RequireValidatedRoot()
    {
        var probe = Path.Combine(StageRoot, ".probe");
        if (!StoragePathValidator.TryValidate(new ProductionStoreOptions(probe), out _, out var reason))
            throw new InvalidOperationException("ProductionImageStageRootRejected:" + reason);
        return StageRoot;
    }

    private static string Normalize(string stageRoot)
    {
        var full = Path.GetFullPath(stageRoot);
        return full.Length > 3
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }

    private static string ComputeContentHash(string root, long maximumStageBytes,
        long maximumTotalStageBytes, int maximumStageFiles)
    {
        var canonical = new StringBuilder()
            .Append("SharpInspect.ProductionImageStageRootBinding|1|")
            .Append(root.ToUpperInvariant()).Append('|')
            .Append(maximumStageBytes.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(maximumTotalStageBytes.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(maximumStageFiles.ToString(CultureInfo.InvariantCulture))
            .ToString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
