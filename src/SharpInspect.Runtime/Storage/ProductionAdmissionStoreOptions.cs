using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit opt-in for the schema-22 production-admission report ledger.
/// Reports are immutable observations of the gates evaluated by Runtime.  They
/// do not issue qualification, change Ready, or replace any lower-layer
/// qualification record.
/// </summary>
public sealed class ProductionAdmissionStoreOptions
{
    internal const int SchemaVersion = 22;
    internal const int FormatVersion = 1;
    internal const int MaximumEntriesHardLimit = 10_000;
    internal const int MaximumPayloadBytesHardLimit = 1 * 1024 * 1024;
    internal const long MaximumTotalBytesHardLimit = 64L * 1024 * 1024;
    // One report can require an outcome and a terminal fact, an authorization
    // event, and one central admission entry.  Reserve enough room for a
    // pending admitted Arm to record its terminal outcome after generic report
    // traffic reaches the configured limit.
    internal const int ControlVerificationReserve = 64;
    internal const int AuditEntriesPerEvent = 4;
    internal const int SqliteValueLimitBytes = 8 * 1024 * 1024;

    public int MaximumEntries { get; init; } = 10_000;
    public int MaximumPayloadBytes { get; init; } = 256 * 1024;
    public long MaximumTotalBytes { get; init; } = 64L * 1024 * 1024;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (MaximumEntries is < 1 or > MaximumEntriesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries),
                "ProductionAdmissionEntryCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes),
                "ProductionAdmissionPayloadCapacityInvalid");
        if (MaximumTotalBytes is < 1 or > MaximumTotalBytesHardLimit ||
            MaximumTotalBytes < MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes),
                "ProductionAdmissionTotalCapacityInvalid");
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("ProductionAdmissionStoreOptions",
            Number(FormatVersion), Number(MaximumEntries), Number(MaximumPayloadBytes),
            Number(MaximumTotalBytes), Number(ControlVerificationReserve),
            Number(AuditEntriesPerEvent));
    }

    internal byte[] EncodeActivationPayload() => AuditCanonical.Encode(
        "ProductionAdmissionStoreActivated", Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static void ConfigureSqliteLimit(sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
