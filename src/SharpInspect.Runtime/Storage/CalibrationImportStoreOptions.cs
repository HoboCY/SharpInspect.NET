using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>Explicit opt in for schema 20, with independent local import provenance and blob limits.</summary>
public sealed class CalibrationImportStoreOptions
{
    internal const int SchemaVersion = 20;
    internal const int FormatVersion = 1;
    internal const int MaximumEntriesHardLimit = 10_000;
    internal const int MaximumPayloadBytesHardLimit = 512 * 1024;
    internal const long MaximumTotalBytesHardLimit = 256L * 1024 * 1024;
    internal const int ControlVerificationReserve = 64;
    internal const int AuditEntriesPerEvent = 3;
    internal const int SqliteValueLimitBytes = 16 * 1024 * 1024;

    public int MaximumEntries { get; init; } = 1000;
    public int MaximumPayloadBytes { get; init; } = MaximumPayloadBytesHardLimit;
    public long MaximumTotalBytes { get; init; } = MaximumTotalBytesHardLimit;
    public CalibrationTransferArtifactOptions Artifacts { get; init; } = new();

    internal void Validate()
    {
        if (MaximumEntries is < 1 or > MaximumEntriesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries));
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes));
        if (MaximumTotalBytes < MaximumPayloadBytes || MaximumTotalBytes > MaximumTotalBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes));
        ArgumentNullException.ThrowIfNull(Artifacts);
        Artifacts.Validate();
    }

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));
    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("CalibrationImportStoreOptions", Number(FormatVersion), Number(MaximumEntries),
            Number(MaximumPayloadBytes), Number(MaximumTotalBytes), Path.GetFullPath(Artifacts.ArtifactRoot),
            Number(Artifacts.MaximumPackageBytes), Number(Artifacts.MaximumTotalBytes), Number(Artifacts.QueueCapacity),
            Number(Artifacts.OperationTimeout.Ticks), Number(ControlVerificationReserve), Number(AuditEntriesPerEvent));
    }
    internal byte[] EncodeActivationPayload() => AuditCanonical.Encode("CalibrationImportStoreActivated",
        Convert.ToBase64String(EncodeBinding()), BindingHash);
    internal static void ConfigureSqliteLimit(sqlite3 database) =>
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
