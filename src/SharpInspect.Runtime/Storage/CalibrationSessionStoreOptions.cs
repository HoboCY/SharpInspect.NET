using System.Globalization;
using System.Security.Cryptography;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit, bounded configuration for the schema 14 calibration-session
/// evidence store.  The store records only bounded metadata in SQLite; frame
/// bytes are written by the session layer below the configured evidence root.
/// </summary>
public sealed class CalibrationSessionStoreOptions
{
    internal const int SchemaVersion = 14;
    internal const int FormatVersion = 1;
    internal const int MaximumSessionsHardLimit = 1_000;
    internal const int MaximumEventsHardLimit = 100_000;
    internal const int MaximumEventPayloadBytesHardLimit = 512 * 1024;
    internal const int MaximumFramesPerSessionHardLimit = 64;
    internal const long MaximumFrameBytesHardLimit = 64L * 1024 * 1024;
    internal const long MaximumTotalFrameBytesHardLimit = 1L * 1024 * 1024 * 1024;
    // SQLite stores the signed audit payload as base64 text.  Keep a margin
    // over the raw 512 KiB event bound for that representation and envelopes.
    internal const int SqliteValueLimitBytes = 1 * 1024 * 1024;
    internal const int ControlVerificationReserve = 64;
    private const string BindingVersion = "1";

    public string EvidenceRoot { get; init; } = string.Empty;
    public int MaximumSessions { get; init; } = 100;
    public int MaximumEvents { get; init; } = 10_000;
    public int MaximumEventPayloadBytes { get; init; } = 256 * 1024;
    public int MaximumFramesPerSession { get; init; } = 64;
    public long MaximumFrameBytes { get; init; } = 16L * 1024 * 1024;
    public long MaximumTotalFrameBytes { get; init; } = 256L * 1024 * 1024;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(EvidenceRoot) ||
            !Path.IsPathFullyQualified(EvidenceRoot) ||
            EvidenceRoot.Any(char.IsControl))
            throw new ArgumentException("CalibrationEvidenceRootInvalid", nameof(EvidenceRoot));
        if (MaximumSessions is < 1 or > MaximumSessionsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumSessions), "CalibrationSessionCapacityInvalid");
        if (MaximumEvents is < 1 or > MaximumEventsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumEvents), "CalibrationEventCapacityInvalid");
        if (MaximumEventPayloadBytes is < 1 or > MaximumEventPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumEventPayloadBytes), "CalibrationEventPayloadCapacityInvalid");
        if (MaximumFramesPerSession is < 1 or > MaximumFramesPerSessionHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumFramesPerSession), "CalibrationFrameCapacityInvalid");
        if (MaximumFrameBytes is < 1 or > MaximumFrameBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumFrameBytes), "CalibrationFramePayloadCapacityInvalid");
        if (MaximumTotalFrameBytes is < 1 or > MaximumTotalFrameBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalFrameBytes), "CalibrationTotalFrameCapacityInvalid");
        if (MaximumFrameBytes > MaximumTotalFrameBytes)
            throw new ArgumentException("CalibrationFrameCapacityInvalid", nameof(MaximumTotalFrameBytes));
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return Integrity.AuditCanonical.Encode("CalibrationSessionStoreOptions", BindingVersion,
            EvidenceRoot,
            MaximumSessions.ToString(CultureInfo.InvariantCulture),
            MaximumEvents.ToString(CultureInfo.InvariantCulture),
            MaximumEventPayloadBytes.ToString(CultureInfo.InvariantCulture),
            MaximumFramesPerSession.ToString(CultureInfo.InvariantCulture),
            MaximumFrameBytes.ToString(CultureInfo.InvariantCulture),
            MaximumTotalFrameBytes.ToString(CultureInfo.InvariantCulture),
            ControlVerificationReserve.ToString(CultureInfo.InvariantCulture));
    }

    internal byte[] EncodeActivationPayload() => Integrity.AuditCanonical.Encode(
        "CalibrationStoreActivated", Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static void ConfigureSqliteLimit(sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    }
}
