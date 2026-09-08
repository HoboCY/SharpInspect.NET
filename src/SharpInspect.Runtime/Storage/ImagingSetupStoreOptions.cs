using System.Globalization;
using System.Security.Cryptography;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit, bounded opt-in for the schema 13 imaging setup declaration ledger.
/// The ledger depends only on camera setup, identity, and the signed audit chain;
/// recovery and network ledgers remain optional.
/// </summary>
public sealed class ImagingSetupStoreOptions
{
    internal const int SchemaVersion = 13;
    internal const int FormatVersion = 1;
    internal const int MaximumRevisionCountHardLimit = 10_000;
    internal const int MaximumPayloadBytesHardLimit = 128 * 1024;
    internal const long MaximumTotalBytesHardLimit = 256L * 1024 * 1024;
    internal const int ControlVerificationReserve = 64;
    internal const int SqliteValueLimitBytes = 512 * 1024;
    private const string BindingVersion = "1";

    public int MaximumRevisionCount { get; init; } = 10_000;
    public int MaximumPayloadBytes { get; init; } = 128 * 1024;
    public long MaximumTotalBytes { get; init; } = 256L * 1024 * 1024;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (MaximumRevisionCount is < 1 or > MaximumRevisionCountHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumRevisionCount), "ImagingSetupRevisionCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes), "ImagingSetupPayloadCapacityInvalid");
        if (MaximumTotalBytes is < 1 or > MaximumTotalBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes), "ImagingSetupTotalCapacityInvalid");
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return Integrity.AuditCanonical.Encode("ImagingSetupStoreOptions", BindingVersion,
            MaximumRevisionCount.ToString(CultureInfo.InvariantCulture),
            MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
            ControlVerificationReserve.ToString(CultureInfo.InvariantCulture));
    }

    internal byte[] EncodeActivationPayload() => Integrity.AuditCanonical.Encode(
        "ImagingSetupStoreActivated", Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static void ConfigureSqliteLimit(sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    }
}
