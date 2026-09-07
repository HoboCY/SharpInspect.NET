using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit opt-in bounds for the schema 8 development computation archive.
/// The archive is deliberately disabled when this option is null; an existing
/// schema 7 database is never upgraded implicitly.
/// </summary>
public sealed class AlgorithmResultArchiveOptions
{
    public const int DefaultMaximumRecordBytes = 1 * 1024 * 1024;
    public const long DefaultMaximumTotalBytes = 256L * 1024 * 1024;
    public const int DefaultMaximumRecords = 10_000;
    public const int DefaultMaximumPageBytes = 4 * 1024 * 1024;

    public int MaximumRecordBytes { get; init; } = DefaultMaximumRecordBytes;
    public long MaximumTotalBytes { get; init; } = DefaultMaximumTotalBytes;
    public int MaximumRecords { get; init; } = DefaultMaximumRecords;
    public int MaximumPageBytes { get; init; } = DefaultMaximumPageBytes;

    internal const int SchemaVersion = 8;
    // Schema 8 keeps a fixed, signed reserve for identity/alarm/command control
    // evidence.  Development archive rows may not consume this reserve.
    internal const int ControlVerificationReserve = 64;
    private const string BindingVersion = "2";
    // The signed audit row stores only a small binding. The full canonical
    // envelope is kept in the result row and is always re-read by verification.
    internal const int MaximumBindingPayloadBytes = 128 * 1024;
    internal const int SqliteValueLimitBytes = 2 * 1024 * 1024;

    internal void Validate()
    {
        if (MaximumRecordBytes is < 1 or > 1 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumRecordBytes));
        if (MaximumTotalBytes is < 1 or > 256L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes));
        if (MaximumRecords is < 1 or > DefaultMaximumRecords)
            throw new ArgumentOutOfRangeException(nameof(MaximumRecords));
        if (MaximumPageBytes is < 1 or > DefaultMaximumPageBytes)
            throw new ArgumentOutOfRangeException(nameof(MaximumPageBytes));
        if ((long)MaximumRecordBytes > MaximumTotalBytes)
            throw new ArgumentException("AlgorithmResultArchiveCapacityInvalid");
        if (MaximumPageBytes < MaximumRecordBytes)
            throw new ArgumentException("AlgorithmResultArchivePageCapacityInvalid");
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("AlgorithmResultArchiveOptions", BindingVersion,
            MaximumRecordBytes.ToString(CultureInfo.InvariantCulture),
            MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
            MaximumRecords.ToString(CultureInfo.InvariantCulture),
            MaximumPageBytes.ToString(CultureInfo.InvariantCulture),
            ControlVerificationReserve.ToString(CultureInfo.InvariantCulture));
    }

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal byte[] EncodeActivationPayload() =>
        AuditCanonical.Encode("AlgorithmArchiveActivated", Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static void ConfigureSqliteLimit(sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    }
}
