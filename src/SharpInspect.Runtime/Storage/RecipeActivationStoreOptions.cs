using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit bounded opt in for the append-only recipe activation ledger (schema 18).
/// Activation depends on the exact camera setup, draft, release, PLC, identity and
/// central audit stores. Calibration and imaging ledgers remain conditional on the
/// selected Recipe's declared requirements; this option never migrates an older store.
/// </summary>
public sealed class RecipeActivationStoreOptions
{
    internal const int SchemaVersion = 18;
    internal const int FormatVersion = 1;
    internal const int MaximumEntriesHardLimit = 10_000;
    internal const int MaximumPayloadBytesHardLimit = 8 * 1024 * 1024;
    internal const long MaximumTotalBytesHardLimit = 512L * 1024 * 1024;
    internal const int ControlVerificationReserve = 64;
    // An activation attempt can append admission, terminal and command/audit facts.
    internal const int AuditEntriesPerEvent = 5;
    internal const int SqliteValueLimitBytes = 16 * 1024 * 1024;

    public int MaximumEntries { get; init; } = 10_000;
    public int MaximumPayloadBytes { get; init; } = 8 * 1024 * 1024;
    public long MaximumTotalBytes { get; init; } = 512L * 1024 * 1024;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (MaximumEntries is < 1 or > MaximumEntriesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries),
                "RecipeActivationEntryCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes),
                "RecipeActivationPayloadCapacityInvalid");
        if (MaximumTotalBytes is < 1 or > MaximumTotalBytesHardLimit ||
            MaximumTotalBytes < MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes),
                "RecipeActivationTotalCapacityInvalid");
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return Integrity.AuditCanonical.Encode("RecipeActivationStoreOptions",
            FormatVersion.ToString(CultureInfo.InvariantCulture),
            MaximumEntries.ToString(CultureInfo.InvariantCulture),
            MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
            ControlVerificationReserve.ToString(CultureInfo.InvariantCulture),
            AuditEntriesPerEvent.ToString(CultureInfo.InvariantCulture));
    }

    internal byte[] EncodeActivationPayload() => Integrity.AuditCanonical.Encode(
        "RecipeActivationStoreActivated", Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static void ConfigureSqliteLimit(sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    }
}
