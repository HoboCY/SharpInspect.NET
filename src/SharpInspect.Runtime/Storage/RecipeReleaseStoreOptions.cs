using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit bounded opt in for the immutable released recipe ledger (schema 16).
/// A release store is meaningful only alongside the exact draft, identity and
/// central audit stores; supplying this option never migrates an older database.
/// </summary>
public sealed class RecipeReleaseStoreOptions
{
    internal const int SchemaVersion = 16;
    internal const int FormatVersion = 1;
    internal const int MaximumEntriesHardLimit = 10_000;
    internal const int MaximumPayloadBytesHardLimit = 8 * 1024 * 1024;
    internal const long MaximumTotalBytesHardLimit = 512L * 1024 * 1024;
    internal const int ControlVerificationReserve = 64;
    // A release transaction contributes a command fact, identity authorization,
    // and one release ledger entry.
    internal const int AuditEntriesPerEvent = 3;
    internal const int SqliteValueLimitBytes = 16 * 1024 * 1024;

    public RecipeReleaseStoreOptions(RecipeGovernancePolicy policy)
    {
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        Validate();
    }

    public RecipeGovernancePolicy Policy { get; }

    public int MaximumEntries { get; init; } = 10_000;
    public int MaximumPayloadBytes { get; init; } = 8 * 1024 * 1024;
    public long MaximumTotalBytes { get; init; } = 512L * 1024 * 1024;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Policy);
        if (MaximumEntries is < 1 or > MaximumEntriesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries),
                "RecipeReleaseEntryCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes),
                "RecipeReleasePayloadCapacityInvalid");
        if (MaximumTotalBytes is < 1 or > MaximumTotalBytesHardLimit ||
            MaximumTotalBytes < MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes),
                "RecipeReleaseTotalCapacityInvalid");
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return Integrity.AuditCanonical.Encode("RecipeReleaseStoreOptions", FormatVersion.ToString(CultureInfo.InvariantCulture),
            Policy.Id, Policy.Version, Policy.Mode.ToString(), Policy.ContentHash,
            MaximumEntries.ToString(CultureInfo.InvariantCulture),
            MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
            ControlVerificationReserve.ToString(CultureInfo.InvariantCulture),
            AuditEntriesPerEvent.ToString(CultureInfo.InvariantCulture));
    }

    internal byte[] EncodeActivationPayload() => Integrity.AuditCanonical.Encode(
        "RecipeReleaseStoreActivated", Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static void ConfigureSqliteLimit(sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    }
}
