using System.Globalization;
using System.Security.Cryptography;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit bounds for the schema 12 camera network maintenance ledger.
/// Network maintenance is an optional deployment extension and is never added
/// to an existing schema 10/11 database implicitly.
/// </summary>
public sealed class CameraNetworkStoreOptions
{
    internal const int SchemaVersion = 12;
    internal const int FormatVersion = 1;
    internal const int MaximumEventsHardLimit = 10_000;
    internal const int MaximumPayloadBytesHardLimit = 128 * 1024;
    internal const long MaximumTotalBytesHardLimit = 256L * 1024 * 1024;
    internal const int MaximumPendingOperationsHardLimit = 16;
    internal const int ControlVerificationReserve = 64;
    // One admission/terminal operation contributes an identity event, a
    // command fact, and a network event to the signed audit chain.
    internal const int AuditEntriesPerAdmission = 3;
    internal const int AuditEntriesPerTerminal = 3;
    internal const int SqliteValueLimitBytes = 512 * 1024;
    private const string BindingVersion = "1";

    public int MaximumEvents { get; init; } = MaximumEventsHardLimit;
    public int MaximumPayloadBytes { get; init; } = MaximumPayloadBytesHardLimit;
    public long MaximumTotalBytes { get; init; } = MaximumTotalBytesHardLimit;
    public int MaximumPendingOperations { get; init; } = MaximumPendingOperationsHardLimit;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (MaximumEvents is < 1 or > MaximumEventsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumEvents), "CameraNetworkEventCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes), "CameraNetworkPayloadCapacityInvalid");
        if (MaximumTotalBytes is < 1 or > MaximumTotalBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes), "CameraNetworkTotalCapacityInvalid");
        if (MaximumPendingOperations is < 1 or > MaximumPendingOperationsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPendingOperations), "CameraNetworkPendingCapacityInvalid");
        if ((long)MaximumPayloadBytes > MaximumTotalBytes)
            throw new ArgumentException("CameraNetworkCapacityInvalid");
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return Integrity.AuditCanonical.Encode("CameraNetworkStoreOptions", BindingVersion,
            MaximumEvents.ToString(CultureInfo.InvariantCulture),
            MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
            MaximumPendingOperations.ToString(CultureInfo.InvariantCulture),
            ControlVerificationReserve.ToString(CultureInfo.InvariantCulture));
    }

    internal byte[] EncodeActivationPayload() => Integrity.AuditCanonical.Encode(
        "CameraNetworkStoreActivated", Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static void ConfigureSqliteLimit(sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    }
}
