using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit opt-in for the schema-27 append-only PLC communication ledger.
/// Communication facts are transport observations and never grant production
/// authority or replace the qualification-cycle ledger.
/// </summary>
public sealed class PlcCommunicationStoreOptions
{
    internal const int SchemaVersion = 27;
    internal const int FormatVersion = 1;
    internal const int MaximumEntriesHardLimit = 100_000;
    internal const int MaximumPayloadBytesHardLimit = 256 * 1024;
    internal const long MaximumTotalBytesHardLimit = 512L * 1024 * 1024;
    internal const int ControlVerificationReserve = 64;
    internal const int AuditEntriesPerEvent = 1;
    internal const int SqliteValueLimitBytes = 1 * 1024 * 1024;

    public int MaximumEntries { get; init; } = 10_000;
    public int MaximumPayloadBytes { get; init; } = 64 * 1024;
    public long MaximumTotalBytes { get; init; } = 256L * 1024 * 1024;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (MaximumEntries is < 1 or > MaximumEntriesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries),
                "PlcCommunicationEntryCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes),
                "PlcCommunicationPayloadCapacityInvalid");
        if (MaximumTotalBytes is < 1 or > MaximumTotalBytesHardLimit ||
            MaximumTotalBytes < MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes),
                "PlcCommunicationTotalCapacityInvalid");
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("PlcCommunicationStoreOptions",
            FormatVersion.ToString(CultureInfo.InvariantCulture),
            MaximumEntries.ToString(CultureInfo.InvariantCulture),
            MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
            ControlVerificationReserve.ToString(CultureInfo.InvariantCulture),
            AuditEntriesPerEvent.ToString(CultureInfo.InvariantCulture));
    }

    internal byte[] EncodeActivationPayload() => AuditCanonical.Encode(
        "PlcCommunicationStoreActivated", Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static void ConfigureSqliteLimit(sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    }
}
