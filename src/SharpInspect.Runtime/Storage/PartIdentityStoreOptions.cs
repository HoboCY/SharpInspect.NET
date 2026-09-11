using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit opt-in for the schema-29 Part Identity rejection/correction
/// ledger.  Accepted identity evidence remains part of the production
/// admission/Core; this ledger never manufactures an Inspection ID.
/// </summary>
public sealed class PartIdentityStoreOptions
{
    internal const int SchemaVersion = 29;
    internal const int FormatVersion = 1;
    internal const int MaximumEntriesHardLimit = 100_000;
    internal const int MaximumPayloadBytesHardLimit = 4 * 1024 * 1024;
    internal const long MaximumTotalBytesHardLimit = 1L * 1024 * 1024 * 1024;
    internal const int ControlVerificationReserve = 16;
    internal const int AuditEntriesPerEvent = 4;
    internal const int SqliteValueLimitBytes = 64 * 1024 * 1024;

    public int MaximumEntries { get; init; } = 10_000;
    public int MaximumPayloadBytes { get; init; } = 512 * 1024;
    public long MaximumTotalBytes { get; init; } = 128L * 1024 * 1024;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (MaximumEntries is < 1 or > MaximumEntriesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries),
                "PartIdentityEntryCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes),
                "PartIdentityPayloadCapacityInvalid");
        if (MaximumTotalBytes is < 1 or > MaximumTotalBytesHardLimit ||
            MaximumTotalBytes < MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes),
                "PartIdentityTotalCapacityInvalid");
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("PartIdentityStoreOptions",
            Number(FormatVersion), Number(MaximumEntries), Number(MaximumPayloadBytes),
            Number(MaximumTotalBytes), Number(ControlVerificationReserve),
            Number(AuditEntriesPerEvent));
    }

    internal byte[] EncodeActivationPayload() => AuditCanonical.Encode(
        "PartIdentityStoreActivated", Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static void ConfigureSqliteLimit(sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
