using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

/// <summary>Explicit bounded storage for irreversible Draft and Released Recipe lifecycle facts.</summary>
public sealed class RecipeLifecycleStoreOptions
{
    internal const int SchemaVersion = 33;
    internal const int FormatVersion = 1;
    internal const int MaximumEventsHardLimit = 100_000;
    internal const int MaximumPayloadBytesHardLimit = 1024 * 1024;
    internal const int MaximumAuditPayloadBytes = MaximumPayloadBytesHardLimit * 2 + 32 * 1024;
    internal const long MaximumTotalBytesHardLimit = 512L * 1024 * 1024;
    internal const int ControlVerificationReserve = 64;
    internal const int SqliteValueLimitBytes = 16 * 1024 * 1024;

    public int MaxEvents { get; init; } = 20_000;
    public int MaximumPayloadBytes { get; init; } = 256 * 1024;
    public long MaxTotalBytes { get; init; } = 64L * 1024 * 1024;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (MaxEvents is < 1 or > MaximumEventsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaxEvents), "RecipeLifecycleEntryCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes), "RecipeLifecyclePayloadCapacityInvalid");
        if (MaxTotalBytes < MaximumPayloadBytes || MaxTotalBytes > MaximumTotalBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaxTotalBytes), "RecipeLifecycleTotalCapacityInvalid");
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("RecipeLifecycleStoreOptions", Number(FormatVersion), Number(MaxEvents),
            Number(MaximumPayloadBytes), Number(MaxTotalBytes), Number(ControlVerificationReserve));
    }

    internal byte[] EncodeActivationPayload() => AuditCanonical.Encode("RecipeLifecycleStoreActivated",
        Convert.ToBase64String(EncodeBinding()), BindingHash);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
