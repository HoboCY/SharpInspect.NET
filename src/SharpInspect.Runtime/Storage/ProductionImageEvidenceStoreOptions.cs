using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit bounded storage for the schema-34 production image evidence store.
/// The store keeps only the pending manifest and pending work facts of one
/// inspection: the qualified local NTFS stage owns the image bytes and remains
/// the sole owner of every staged file. The declared stage binding is hashed
/// into the immutable configuration row, so a store can never be opened against
/// a stage whose qualified root or capacity bounds changed.
/// </summary>
public sealed class ProductionImageEvidenceStoreOptions
{
    internal const int SchemaVersion = 34;
    internal const int FormatVersion = 1;
    internal const int MaximumImagesHardLimit = 100_000;
    internal const int MaximumPayloadBytesHardLimit = 65536;
    internal const long MaximumTotalBytesHardLimit = 512L * 1024 * 1024;
    internal const int MaximumAuditPayloadBytes = 128 * 1024;
    internal const int ControlVerificationReserve = 64;

    public ProductionImageEvidenceStoreOptions(Images.ProductionImageStageOptions stage)
    {
        Stage = stage ?? throw new ArgumentNullException(nameof(stage));
    }

    /// <summary>The qualified, existing local NTFS stage the evidence is bound to.</summary>
    public Images.ProductionImageStageOptions Stage { get; }

    public int MaxImages { get; init; } = 10_000;
    public int MaximumPayloadBytes { get; init; } = 16384;
    public long MaxTotalBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>The exact qualified stage binding stored in the configuration row.</summary>
    internal string StageRootBindingHash => Stage.ContentHash;

    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Stage);
        if (MaxImages is < 1 or > MaximumImagesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaxImages), "ImageEvidenceEntryCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes),
                "ImageEvidencePayloadCapacityInvalid");
        if (MaxTotalBytes < MaximumPayloadBytes || MaxTotalBytes > MaximumTotalBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaxTotalBytes), "ImageEvidenceTotalCapacityInvalid");
        if (!IsHash(StageRootBindingHash))
            throw new ArgumentException("ImageEvidenceStageBindingInvalid", nameof(Stage));
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("ProductionImageEvidenceStoreOptions", StageRootBindingHash,
            Number(FormatVersion), Number(MaxImages), Number(MaximumPayloadBytes), Number(MaxTotalBytes));
    }

    internal byte[] EncodeActivationPayload() => AuditCanonical.Encode("ImageEvidenceStoreActivated",
        Convert.ToBase64String(EncodeBinding()), BindingHash);

    private static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
