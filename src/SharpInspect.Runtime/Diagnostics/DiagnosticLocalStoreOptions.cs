using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Diagnostics;

/// <summary>
/// Immutable bounded settings for one local diagnostic channel: a rolling UTF-8 JSON Lines
/// Operational Log or a separately rooted Protected Diagnostic Store. The constructor performs
/// no IO; installation and runtime verification stay explicit operations. No production number
/// is hidden here: every budget, window and retention period is supplied by the caller.
/// </summary>
public sealed class DiagnosticLocalStoreOptions
{
    internal const long MaximumRecordBytesHardLimit = 16L * 1024 * 1024;
    internal const long MaximumFileBytesHardLimit = 1024L * 1024 * 1024;
    internal const int MaximumFilesHardLimit = 4096;
    internal const long MaximumTotalBytesHardLimit = 64L * 1024 * 1024 * 1024;
    internal static readonly TimeSpan MinimumWindow = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaximumRollAfter = TimeSpan.FromDays(365);
    internal static readonly TimeSpan MaximumRetention = TimeSpan.FromDays(3650);
    private const int MaximumDirectoryLength = 32767;

    /// <summary>Creates one bounded channel description. The directory must be an absolute named
    /// local path; nothing is created or read here.</summary>
    public DiagnosticLocalStoreOptions(string directory, bool isProtected, long maximumRecordBytes,
        long maximumFileBytes, int maximumFiles, long maximumTotalBytes, TimeSpan rollAfter, TimeSpan retention)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("DiagnosticStoreDirectoryRequired", nameof(directory));
        if (directory.Length > MaximumDirectoryLength)
            throw new ArgumentException("DiagnosticStoreDirectoryTooLong", nameof(directory));
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("DiagnosticStoreDirectoryMustBeAbsolute", nameof(directory));
        if (directory.StartsWith(@"\\", StringComparison.Ordinal) || directory.StartsWith("//", StringComparison.Ordinal) ||
            directory.StartsWith(@"\??\", StringComparison.Ordinal))
            throw new ArgumentException("DiagnosticStoreDirectoryMustBeExplicitLocalPath", nameof(directory));

        string canonical;
        try { canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ArgumentException("DiagnosticStoreDirectoryInvalid", nameof(directory), ex); }
        var leaf = Path.GetFileName(canonical);
        if (leaf.Length == 0 || string.Equals(canonical, Path.GetPathRoot(canonical), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("DiagnosticStoreDirectoryMustBeNamed", nameof(directory));
        if (leaf.EndsWith(' ') || leaf.EndsWith('.'))
            throw new ArgumentException("DiagnosticStoreDirectoryInvalidName", nameof(directory));

        if (maximumRecordBytes < 1 || maximumRecordBytes > MaximumRecordBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(maximumRecordBytes), "DiagnosticStoreRecordBytesOutOfRange");
        if (maximumFileBytes < maximumRecordBytes || maximumFileBytes > MaximumFileBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes), "DiagnosticStoreFileBytesOutOfRange");
        if (maximumFiles < 1 || maximumFiles > MaximumFilesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(maximumFiles), "DiagnosticStoreFileCountOutOfRange");
        if (maximumTotalBytes < maximumFileBytes || maximumTotalBytes > MaximumTotalBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(maximumTotalBytes), "DiagnosticStoreTotalBytesOutOfRange");
        if (rollAfter < MinimumWindow || rollAfter > MaximumRollAfter)
            throw new ArgumentOutOfRangeException(nameof(rollAfter), "DiagnosticStoreRollAfterOutOfRange");
        if (retention < MinimumWindow || retention > MaximumRetention)
            throw new ArgumentOutOfRangeException(nameof(retention), "DiagnosticStoreRetentionOutOfRange");

        Directory = canonical;
        IsProtected = isProtected;
        MaximumRecordBytes = maximumRecordBytes;
        MaximumFileBytes = maximumFileBytes;
        MaximumFiles = maximumFiles;
        MaximumTotalBytes = maximumTotalBytes;
        RollAfter = rollAfter;
        Retention = retention;
        BindingHash = Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode(
            "DiagnosticLocalStoreOptionsV1", canonical.ToUpperInvariant(), isProtected ? "1" : "0",
            Number(maximumRecordBytes), Number(maximumFileBytes), Number(maximumFiles),
            Number(maximumTotalBytes), Number(rollAfter.Ticks), Number(retention.Ticks))));
    }

    /// <summary>Canonical absolute local directory. No IO is performed to produce this value.</summary>
    public string Directory { get; }

    /// <summary>True for the separately rooted Protected Diagnostic Store, false for the safe
    /// Operational Log channel. Both use the same installation-validated restricted ACL.</summary>
    public bool IsProtected { get; }

    /// <summary>Maximum stored record bytes including the terminating newline.</summary>
    public long MaximumRecordBytes { get; }

    /// <summary>Maximum bytes of one rolling JSON Lines file.</summary>
    public long MaximumFileBytes { get; }

    /// <summary>Maximum number of owned files in the root.</summary>
    public int MaximumFiles { get; }

    /// <summary>Maximum total bytes of owned files in the root.</summary>
    public long MaximumTotalBytes { get; }

    /// <summary>Append window sealed into each file name at creation.</summary>
    public TimeSpan RollAfter { get; }

    /// <summary>Minimum retention covered by the sealed expiry of every file.</summary>
    public TimeSpan Retention { get; }

    /// <summary>Stable uppercase SHA-256 over every setting of this channel.</summary>
    public string BindingHash { get; }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
