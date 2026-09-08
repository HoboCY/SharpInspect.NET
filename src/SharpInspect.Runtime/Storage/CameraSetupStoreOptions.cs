using System.Globalization;
using System.Security.Cryptography;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit, bounded opt-in for the schema 10 camera setup evidence stream.
/// Camera setup is deployment state and is therefore never silently added to an
/// existing schema 7, 8 or 9 database.
/// </summary>
public sealed class CameraSetupStoreOptions
{
    internal const int SchemaVersion = 10;
    internal const int FormatVersion = 1;
    internal const int MaximumEventsHardLimit = 10_000;
    internal const int MaximumPayloadBytesHardLimit = 128 * 1024;
    internal const long MaximumTotalBytesHardLimit = 256L * 1024 * 1024;
    internal const int MaximumPendingOperationsHardLimit = 16;
    internal const int ControlVerificationReserve = 64;
    internal const int SqliteValueLimitBytes = 512 * 1024;
    private const string BindingVersion = "1";

    public int MaximumEvents { get; init; } = 10_000;
    public int MaximumPayloadBytes { get; init; } = 128 * 1024;
    public long MaximumTotalBytes { get; init; } = 256L * 1024 * 1024;
    public int MaximumPendingOperations { get; init; } = 16;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (MaximumEvents is < 1 or > MaximumEventsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumEvents), "CameraSetupEventCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes), "CameraSetupPayloadCapacityInvalid");
        if (MaximumTotalBytes is < 1 or > MaximumTotalBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes), "CameraSetupTotalCapacityInvalid");
        if (MaximumPendingOperations is < 1 or > MaximumPendingOperationsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPendingOperations), "CameraSetupPendingCapacityInvalid");
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return Integrity.AuditCanonical.Encode("CameraSetupStoreOptions", BindingVersion,
            MaximumEvents.ToString(CultureInfo.InvariantCulture),
            MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
            MaximumPendingOperations.ToString(CultureInfo.InvariantCulture),
            ControlVerificationReserve.ToString(CultureInfo.InvariantCulture));
    }

    internal byte[] EncodeActivationPayload() => Integrity.AuditCanonical.Encode(
        "CameraSetupStoreActivated", Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static void ConfigureSqliteLimit(sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    }
}
