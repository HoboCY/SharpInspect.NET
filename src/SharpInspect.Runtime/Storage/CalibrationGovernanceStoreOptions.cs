using System.Globalization;
using System.Security.Cryptography;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit, bounded opt-in for the schema-15 calibration-governance ledger.
/// Governance rows depend on the schema-14 calibration-session evidence store;
/// the option therefore never enables an independent or implicitly migrated
/// database.
/// </summary>
public sealed class CalibrationGovernanceStoreOptions
{
    internal const int SchemaVersion = 15;
    internal const int FormatVersion = 1;
    internal const int MaximumEntriesHardLimit = 10_000;
    internal const int MaximumPayloadBytesHardLimit = 512 * 1024;
    internal const long MaximumTotalBytesHardLimit = 256L * 1024 * 1024;
    internal const int ControlVerificationReserve = 64;
    // A governance command contributes one command fact, one identity
    // authorization event, and one governance ledger audit entry.
    internal const int AuditEntriesPerEvent = 3;
    // SQLite stores canonical payloads and signed binding envelopes as base64
    // text.  Keep the connection limit above the raw codec bound.
    internal const int SqliteValueLimitBytes = 1 * 1024 * 1024;
    private const string BindingVersion = "1";

    public int MaximumEntries { get; init; } = 10_000;
    public int MaximumPayloadBytes { get; init; } = 512 * 1024;
    public long MaximumTotalBytes { get; init; } = 256L * 1024 * 1024;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (MaximumEntries is < 1 or > MaximumEntriesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries),
                "CalibrationGovernanceEntryCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes),
                "CalibrationGovernancePayloadCapacityInvalid");
        if (MaximumTotalBytes is < 1 or > MaximumTotalBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes),
                "CalibrationGovernanceTotalCapacityInvalid");
        if ((long)MaximumPayloadBytes > MaximumTotalBytes)
            throw new ArgumentException("CalibrationGovernanceCapacityInvalid", nameof(MaximumTotalBytes));
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return Integrity.AuditCanonical.Encode("CalibrationGovernanceStoreOptions", BindingVersion,
            MaximumEntries.ToString(CultureInfo.InvariantCulture),
            MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
            ControlVerificationReserve.ToString(CultureInfo.InvariantCulture),
            AuditEntriesPerEvent.ToString(CultureInfo.InvariantCulture));
    }

    internal byte[] EncodeActivationPayload() => Integrity.AuditCanonical.Encode(
        "CalibrationGovernanceStoreActivated", Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static void ConfigureSqliteLimit(sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    }
}
