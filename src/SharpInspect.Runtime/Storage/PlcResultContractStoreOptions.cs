using System.Globalization;
using System.Security.Cryptography;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit opt-in for the immutable PLC result contract ledger (schema 17).
/// The option requires the recipe-draft and released-recipe ledgers together with
/// local identity and audit integrity; it never upgrades an existing database implicitly.
/// </summary>
public sealed class PlcResultContractStoreOptions
{
    internal const int SchemaVersion = 17;
    internal const int FormatVersion = 1;
    internal const int MaximumRevisionsHardLimit = 10_000;
    internal const int MaximumPayloadBytesHardLimit = 8 * 1024 * 1024;
    internal const long MaximumTotalBytesHardLimit = 512L * 1024 * 1024;
    internal const int ControlVerificationReserve = 64;
    // A contract change contributes a command fact, identity authorization and
    // one contract-ledger event.
    internal const int AuditEntriesPerEvent = 3;
    internal const int SqliteValueLimitBytes = 16 * 1024 * 1024;

    public int MaximumRevisions { get; init; } = MaximumRevisionsHardLimit;
    public int MaximumPayloadBytes { get; init; } = MaximumPayloadBytesHardLimit;
    public long MaximumTotalBytes { get; init; } = MaximumTotalBytesHardLimit;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (MaximumRevisions is < 1 or > MaximumRevisionsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumRevisions),
                "PlcResultContractRevisionCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes),
                "PlcResultContractPayloadCapacityInvalid");
        if (MaximumTotalBytes < 1 || MaximumTotalBytes > MaximumTotalBytesHardLimit ||
            MaximumTotalBytes < MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes),
                "PlcResultContractTotalCapacityInvalid");
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return Integrity.AuditCanonical.Encode(
            "PlcResultContractStoreOptions",
            FormatVersion.ToString(CultureInfo.InvariantCulture),
            MaximumRevisions.ToString(CultureInfo.InvariantCulture),
            MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
            ControlVerificationReserve.ToString(CultureInfo.InvariantCulture),
            AuditEntriesPerEvent.ToString(CultureInfo.InvariantCulture));
    }

    internal byte[] EncodeActivationPayload() => Integrity.AuditCanonical.Encode(
        "PlcResultContractStoreActivated", Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static void ConfigureSqliteLimit(SQLitePCL.sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = SQLitePCL.raw.sqlite3_limit(database, SQLitePCL.raw.SQLITE_LIMIT_LENGTH,
            SqliteValueLimitBytes);
    }
}
