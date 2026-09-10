using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit opt-in for the schema-24 recipe transfer ledger.  The transfer
/// ledger is deliberately independent from the camera, activation, release,
/// and qualification ledgers.  It still requires a local identity, audit
/// integrity, and the recipe-draft ledger because an import creates exactly
/// one local draft in the same transaction.
/// </summary>
public sealed class RecipeTransferStoreOptions
{
    internal const int SchemaVersion = 24;
    internal const int FormatVersion = 1;
    internal const int MaximumEntriesHardLimit = 10_000;
    internal const int MaximumPayloadBytesHardLimit = RecipeTransferPackageLimits.MaximumPackageBytes;
    internal const long MaximumTotalBytesHardLimit = 512L * 1024 * 1024;
    internal const int ControlVerificationReserve = 64;
    internal const int AuditEntriesPerEvent = 4;
    internal const int SqliteValueLimitBytes = 64 * 1024 * 1024;

    public int MaximumEntries { get; init; } = 1_000;
    public int MaximumPayloadBytes { get; init; } = RecipeTransferPackageLimits.MaximumPackageBytes;
    public long MaximumTotalBytes { get; init; } = MaximumTotalBytesHardLimit;

    /// <summary>
    /// The host must explicitly declare which algorithm/configuration fields
    /// are portable.  A package cannot broaden this local policy.
    /// </summary>
    public RecipeTransferPortablePolicy PortablePolicy { get; init; } = null!;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (MaximumEntries is < 1 or > MaximumEntriesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries),
                "RecipeTransferEntryCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes),
                "RecipeTransferPayloadCapacityInvalid");
        if (MaximumTotalBytes < MaximumPayloadBytes || MaximumTotalBytes > MaximumTotalBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes),
                "RecipeTransferTotalCapacityInvalid");
        ArgumentNullException.ThrowIfNull(PortablePolicy);
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("RecipeTransferStoreOptions",
            Number(FormatVersion), Number(MaximumEntries), Number(MaximumPayloadBytes),
            Number(MaximumTotalBytes), PortablePolicy.Id, PortablePolicy.Version,
            PortablePolicy.ContentHash, Number(ControlVerificationReserve),
            Number(AuditEntriesPerEvent));
    }

    internal byte[] EncodeActivationPayload() => AuditCanonical.Encode(
        "RecipeTransferStoreActivated", Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static void ConfigureSqliteLimit(sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
