using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit opt in for the independent schema-21 Manual Inspection ledger.
/// Manual records are non-production evidence and are never stored in the
/// development algorithm-result archive or any production result table.
/// </summary>
public sealed class ManualInspectionStoreOptions
{
    internal const int SchemaVersion = 21;
    internal const int FormatVersion = 1;
    internal const int MaximumEntriesHardLimit = 10_000;
    internal const int MaximumPayloadBytesHardLimit = 16 * 1024 * 1024;
    internal const long MaximumTotalBytesHardLimit = 512L * 1024 * 1024;
    internal const int ControlVerificationReserve = 64;
    // An action may carry an outcome/terminal command pair, one identity event
    // and one Manual ledger entry. A run row is part of that same ledger payload.
    internal const int AuditEntriesPerEvent = 4;
    // Payload is stored in the SQLite text column as Base64. A legal 16 MiB
    // raw payload therefore needs more than 16 MiB for one value; keep the
    // connection limit bounded while leaving room for the encoded ledger and
    // audit values in the same transaction.
    internal const int SqliteValueLimitBytes = 64 * 1024 * 1024;

    public int MaximumEntries { get; init; } = 10_000;
    public int MaximumPayloadBytes { get; init; } = 8 * 1024 * 1024;
    public long MaximumTotalBytes { get; init; } = 512L * 1024 * 1024;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (MaximumEntries is < 1 or > MaximumEntriesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries),
                "ManualInspectionEntryCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes),
                "ManualInspectionPayloadCapacityInvalid");
        if (MaximumTotalBytes is < 1 or > MaximumTotalBytesHardLimit ||
            MaximumTotalBytes < MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes),
                "ManualInspectionTotalCapacityInvalid");
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("ManualInspectionStoreOptions",
            Number(FormatVersion), Number(MaximumEntries), Number(MaximumPayloadBytes),
            Number(MaximumTotalBytes), Number(ControlVerificationReserve),
            Number(AuditEntriesPerEvent));
    }

    internal byte[] EncodeActivationPayload() => AuditCanonical.Encode(
        "ManualInspectionStoreActivated", Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static void ConfigureSqliteLimit(sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
