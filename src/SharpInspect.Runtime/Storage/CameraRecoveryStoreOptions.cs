using System.Globalization;
using System.Security.Cryptography;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit opt-in for the bounded camera recovery authorization and terminal
/// evidence ledger. The presence of this object selects schema 11; it has no
/// deployment knobs because recovery evidence has one fixed, reviewable bound.
/// </summary>
public sealed class CameraRecoveryStoreOptions
{
    internal const int SchemaVersion = 11;
    internal const int FormatVersion = 1;
    internal const int MaximumEventsHardLimit = 1024;
    internal const int MaximumPayloadBytesHardLimit = 16 * 1024;
    internal const long MaximumTotalBytesHardLimit = 16L * 1024 * 1024;
    internal const int SqliteValueLimitBytes = 512 * 1024;
    private const string BindingVersion = "1";

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        // Keep the validation method even though the public type is parameterless.
        // It makes the configuration contract explicit at every store boundary and
        // leaves room for a governed versioned bound in a future schema.
        if (FormatVersion != 1 || MaximumEventsHardLimit < 1 ||
            MaximumPayloadBytesHardLimit < 1 || MaximumTotalBytesHardLimit < 1)
            throw new ArgumentException("CameraRecoveryConfigurationInvalid");
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return Integrity.AuditCanonical.Encode("CameraRecoveryStoreOptions", BindingVersion,
            MaximumEventsHardLimit.ToString(CultureInfo.InvariantCulture),
            MaximumPayloadBytesHardLimit.ToString(CultureInfo.InvariantCulture),
            MaximumTotalBytesHardLimit.ToString(CultureInfo.InvariantCulture));
    }

    internal byte[] EncodeActivationPayload() => Integrity.AuditCanonical.Encode(
        "CameraRecoveryStoreActivated", Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static void ConfigureSqliteLimit(sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    }
}
