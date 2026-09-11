using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit opt-in for the schema-28 immutable production inspection ledger.
/// The ledger is independent from the development, manual, and qualification
/// result stores. It requires only local identity and the central audit chain.
/// </summary>
public sealed class ProductionInspectionStoreOptions
{
    internal const int SchemaVersion = 28;
    internal const int FormatVersion = 1;
    internal const int MaximumEntriesHardLimit = 100_000;
    internal const int MaximumPayloadBytesHardLimit = 16 * 1024 * 1024;
    internal const long MaximumTotalBytesHardLimit = 4L * 1024 * 1024 * 1024;
    internal const int ControlVerificationReserve = 16;
    internal const int AuditEntriesPerAdmission = 3;
    internal const int AuditEntriesPerCore = 4;
    internal const int AuditEntriesPerEvent = 1;
    internal const int SqliteValueLimitBytes = 32 * 1024 * 1024;

    public int MaximumEntries { get; init; } = 100_000;
    public int MaximumPayloadBytes { get; init; } = 4 * 1024 * 1024;
    public long MaximumTotalBytes { get; init; } = 1L * 1024 * 1024 * 1024;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (MaximumEntries is < 1 or > MaximumEntriesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries),
                "ProductionInspectionEntryCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes),
                "ProductionInspectionPayloadCapacityInvalid");
        if (MaximumTotalBytes is < 1 or > MaximumTotalBytesHardLimit ||
            MaximumTotalBytes < MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes),
                "ProductionInspectionTotalCapacityInvalid");
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("ProductionInspectionStoreOptions",
            Number(FormatVersion), Number(MaximumEntries), Number(MaximumPayloadBytes),
            Number(MaximumTotalBytes), Number(ControlVerificationReserve),
            Number(AuditEntriesPerAdmission), Number(AuditEntriesPerCore),
            Number(AuditEntriesPerEvent));
    }

    internal byte[] EncodeActivationPayload() => AuditCanonical.Encode(
        "ProductionInspectionStoreActivated", Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static void ConfigureSqliteLimit(sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
