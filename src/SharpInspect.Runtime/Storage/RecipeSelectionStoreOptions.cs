using System.Globalization;
using System.Security.Cryptography;

namespace SharpInspect.Runtime.Storage;

/// <summary>Explicit bounded schema-31 opt-in. Startup never migrates an existing database.</summary>
public sealed class RecipeSelectionStoreOptions
{
    internal const int SchemaVersion = 31;
    internal const int MaximumPayloadBytes = 2 * 1024 * 1024;
    internal const int MaximumAuditPayloadBytes = MaximumPayloadBytes * 2 + 32 * 1024;
    internal const int SqliteValueLimitBytes = 16 * 1024 * 1024;
    internal const int ControlVerificationReserve = 64;
    public int MaximumRevisions { get; init; } = 256;
    public int MaximumHandshakeEvents { get; init; } = 10_000;
    public long MaximumTotalBytes { get; init; } = 64L * 1024 * 1024;

    internal void Validate()
    {
        if (MaximumRevisions is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(MaximumRevisions));
        if (MaximumHandshakeEvents is < 16 or > 100_000) throw new ArgumentOutOfRangeException(nameof(MaximumHandshakeEvents));
        if (MaximumTotalBytes < MaximumPayloadBytes || MaximumTotalBytes > 512L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes));
    }
    internal byte[] EncodeBinding()
    {
        Validate();
        return Integrity.AuditCanonical.Encode("RecipeSelectionStoreOptions", "1",
            MaximumRevisions.ToString(CultureInfo.InvariantCulture),
            MaximumHandshakeEvents.ToString(CultureInfo.InvariantCulture),
            MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
            MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            ControlVerificationReserve.ToString(CultureInfo.InvariantCulture));
    }
    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));
    internal byte[] EncodeActivationPayload() => Integrity.AuditCanonical.Encode("RecipeSelectionStoreActivated",
        Convert.ToBase64String(EncodeBinding()), BindingHash);
}
