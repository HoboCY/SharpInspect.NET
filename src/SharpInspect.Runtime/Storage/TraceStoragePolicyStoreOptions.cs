using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.StoragePolicies;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit opt-in for the schema-25 trace-storage-policy ledger.
///
/// The ledger stores approved deployment-policy publications and their frozen
/// policy snapshots.  It does not manufacture per-run retention obligations;
/// those are created by the production trace workflow once that workflow owns
/// the corresponding inspection aggregate.
/// </summary>
public sealed class TraceStoragePolicyStoreOptions
{
    internal const int SchemaVersion = 25;
    internal const int FormatVersion = 1;
    internal const int MaximumEntriesHardLimit = 10_000;
    internal const int MaximumPayloadBytesHardLimit = 8 * 1024 * 1024;
    internal const long MaximumTotalBytesHardLimit = 512L * 1024 * 1024;
    internal const int ControlVerificationReserve = 64;
    internal const int AuditEntriesPerEvent = 4;
    // Manual and qualification stores can legally use 64 MiB SQLite values;
    // this feature must not lower the connection-wide limit when co-enabled.
    internal const int SqliteValueLimitBytes = 64 * 1024 * 1024;

    public int MaximumEntries { get; init; } = 1_000;
    public int MaximumPayloadBytes { get; init; } = 1 * 1024 * 1024;
    public long MaximumTotalBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// The explicit route inventory against which the policy is bound.  Null is
    /// invalid; an empty collection is the explicit no-required-route case.
    /// </summary>
    public TraceStorageDeploymentScope DeploymentScope { get; init; } = null!;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (MaximumEntries is < 1 or > MaximumEntriesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries),
                "TraceStoragePolicyEntryCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes),
                "TraceStoragePolicyPayloadCapacityInvalid");
        if (MaximumTotalBytes < MaximumPayloadBytes || MaximumTotalBytes > MaximumTotalBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes),
                "TraceStoragePolicyTotalCapacityInvalid");
        ArgumentNullException.ThrowIfNull(DeploymentScope);
        _ = DeploymentScope.ContentHash;
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("TraceStoragePolicyStoreOptions",
            Number(FormatVersion), Number(MaximumEntries), Number(MaximumPayloadBytes),
            Number(MaximumTotalBytes),
            Number(ControlVerificationReserve), Number(AuditEntriesPerEvent));
    }

    internal byte[] EncodeActivationPayload() => AuditCanonical.Encode(
        "TraceStoragePolicyStoreActivated", Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static void ConfigureSqliteLimit(sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
