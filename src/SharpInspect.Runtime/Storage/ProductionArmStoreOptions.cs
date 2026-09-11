using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit, immutable opt-in for the schema-32 append-only production-arm
/// audit ledger. The configuration is bounded and is bound byte-for-byte into
/// one signed activation entry; a schema-32 database whose stored binding does
/// not match the running configuration fails closed instead of degrading.
/// </summary>
public sealed class ProductionArmStoreOptions
{
    internal const int SchemaVersion = 32;
    internal const int FormatVersion = 1;
    internal const int MaximumEventsHardLimit = 100_000;
    internal const int MaximumPayloadBytesHardLimit = 1 * 1024 * 1024;
    internal const int MaximumAuditPayloadBytes = MaximumPayloadBytesHardLimit * 2 + 32 * 1024;
    internal const long MaximumTotalBytesHardLimit = 512L * 1024 * 1024;
    // One open attempt can still require Authorized, ReadyConfirmed and one
    // status-delivery observation; a terminal attempt can require only the
    // optional status observation. The reserve keeps the central audit budget
    // for those state transitions even when unrelated writers reach the limit.
    internal const int OpenAttemptReserveEntries = 3;
    internal const int TerminalAttemptReserveEntries = 1;
    internal const int ControlVerificationReserve = 64;
    internal const int SqliteValueLimitBytes = 16 * 1024 * 1024;

    public int MaxEvents { get; init; } = 20_000;
    public int MaximumPayloadBytes { get; init; } = 256 * 1024;
    public long MaxTotalBytes { get; init; } = 64L * 1024 * 1024;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (MaxEvents is < 1 or > MaximumEventsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaxEvents), "ProductionArmEntryCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes), "ProductionArmPayloadCapacityInvalid");
        if (MaxTotalBytes < MaximumPayloadBytes || MaxTotalBytes > MaximumTotalBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaxTotalBytes), "ProductionArmTotalCapacityInvalid");
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("ProductionArmStoreOptions", Number(FormatVersion), Number(MaxEvents),
            Number(MaximumPayloadBytes), Number(MaxTotalBytes), Number(OpenAttemptReserveEntries),
            Number(TerminalAttemptReserveEntries), Number(ControlVerificationReserve));
    }

    internal byte[] EncodeActivationPayload() => AuditCanonical.Encode("ProductionArmStoreActivated",
        Convert.ToBase64String(EncodeBinding()), BindingHash);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
