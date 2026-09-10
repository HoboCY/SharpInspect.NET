using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit opt in for the independent schema-23 station qualification ledger.
/// Qualification is non-production evidence and is never folded into Manual,
/// production results, or the production admission report.
/// </summary>
public sealed class StationQualificationStoreOptions
{
    internal const int SchemaVersion = 23;
    internal const int FormatVersion = 1;
    internal const int MaximumEntriesHardLimit = 10_000;
    internal const int MaximumPayloadBytesHardLimit = 16 * 1024 * 1024;
    internal const long MaximumTotalBytesHardLimit = 512L * 1024 * 1024;
    internal const int ControlVerificationReserve = 64;
    // One phase row carries identity, command and central references. A
    // qualification run is stored inside that row, so the bounded ledger keeps
    // one conservative control reservation per pending phase/run transition.
    internal const int AuditEntriesPerEvent = 5;
    internal const int SqliteValueLimitBytes = 64 * 1024 * 1024;

    public int MaximumEntries { get; init; } = 10_000;
    public int MaximumPayloadBytes { get; init; } = 8 * 1024 * 1024;
    public long MaximumTotalBytes { get; init; } = 512L * 1024 * 1024;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (MaximumEntries is < 1 or > MaximumEntriesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries),
                "StationQualificationEntryCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes),
                "StationQualificationPayloadCapacityInvalid");
        if (MaximumTotalBytes is < 1 or > MaximumTotalBytesHardLimit ||
            MaximumTotalBytes < MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes),
                "StationQualificationTotalCapacityInvalid");
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("StationQualificationStoreOptions",
            FormatVersion.ToString(CultureInfo.InvariantCulture),
            MaximumEntries.ToString(CultureInfo.InvariantCulture),
            MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
            ControlVerificationReserve.ToString(CultureInfo.InvariantCulture),
            AuditEntriesPerEvent.ToString(CultureInfo.InvariantCulture));
    }

    internal byte[] EncodeActivationPayload() => AuditCanonical.Encode(
        "StationQualificationStoreActivated", Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static void ConfigureSqliteLimit(sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    }
}
