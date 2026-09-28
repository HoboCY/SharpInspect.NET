using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>The initial image state committed with a production Core.</summary>
public enum ProductionImageEvidenceState : byte { NotRequired = 0, NotAvailable = 1, Pending = 2 }

/// <summary>
/// An immutable obligation to finalize exactly one staged canonical image. This
/// value describes durable work; constructing or reading it grants no file or
/// production publication authority. Pending obligations cannot be expired or
/// treated as finalized images.
/// </summary>
public sealed class PendingImageManifest
{
    internal PendingImageManifest(Guid manifestId, Guid inspectionId, string admissionContentHash,
        string evidencePolicyContentHash, Guid stageId, Guid inputLeaseId, string stageRootBindingHash,
        string stageFileName, int width, int height, VisionPixelFormat pixelFormat, int? validBits,
        string canonicalPixelHash, long canonicalByteLength, string inputMetadataHash,
        string inputProvenanceHash, string tracePolicySnapshotHash, string retentionRuleHash,
        DateTimeOffset createdAtUtc)
    {
        if (manifestId == Guid.Empty || inspectionId == Guid.Empty || stageId == Guid.Empty || inputLeaseId == Guid.Empty)
            throw new ArgumentException("ProductionImageManifestIdentityInvalid");
        _ = CanonicalImagePixelContent.CreateEnvelope(width, height, pixelFormat, validBits);
        var pixelBytes = checked((long)width * height * (pixelFormat == VisionPixelFormat.Mono8 ? 1 :
            pixelFormat == VisionPixelFormat.Mono16 ? 2 : 3));
        if (canonicalByteLength != checked(32 + pixelBytes))
            throw new ArgumentException("ProductionImageManifestLengthInvalid", nameof(canonicalByteLength));
        if (string.IsNullOrWhiteSpace(stageFileName) || stageFileName.Length > 128 ||
            !stageFileName.EndsWith(".stage", StringComparison.Ordinal) ||
            stageFileName.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_')) ||
            stageFileName.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("ProductionImageManifestFileNameInvalid", nameof(stageFileName));
        CreatedAtUtc = TraceRetentionObligation.ValidateUtc(createdAtUtc, nameof(createdAtUtc));
        ManifestId = manifestId;
        InspectionId = inspectionId;
        AdmissionContentHash = Hash(admissionContentHash);
        EvidencePolicyContentHash = Hash(evidencePolicyContentHash);
        StageId = stageId;
        InputLeaseId = inputLeaseId;
        StageRootBindingHash = Hash(stageRootBindingHash);
        StageFileName = stageFileName;
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        ValidBits = validBits;
        CanonicalPixelHash = Hash(canonicalPixelHash);
        CanonicalByteLength = canonicalByteLength;
        InputMetadataHash = Hash(inputMetadataHash);
        InputProvenanceHash = Hash(inputProvenanceHash);
        TracePolicySnapshotHash = Hash(tracePolicySnapshotHash);
        RetentionRuleHash = Hash(retentionRuleHash);
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-pending-image-manifest-v1", ManifestId.ToString("D"), InspectionId.ToString("D"),
            AdmissionContentHash, EvidencePolicyContentHash, StageId.ToString("D"), InputLeaseId.ToString("D"),
            StageRootBindingHash, StageFileName, Number(Width), Number(Height), PixelFormat.ToString(),
            ValidBits?.ToString(CultureInfo.InvariantCulture), HashScheme, Number(HashSchemeVersion),
            CanonicalPixelHash, Number(CanonicalByteLength), InputMetadataHash, InputProvenanceHash,
            TracePolicySnapshotHash, RetentionRuleHash, CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        });
    }

    public Guid ManifestId { get; }
    public Guid InspectionId { get; }
    public string AdmissionContentHash { get; }
    public string EvidencePolicyContentHash { get; }
    public Guid StageId { get; }
    public Guid InputLeaseId { get; }
    public string StageRootBindingHash { get; }
    public string StageFileName { get; }
    public int Width { get; }
    public int Height { get; }
    public VisionPixelFormat PixelFormat { get; }
    public int? ValidBits { get; }
    public string HashScheme => CanonicalImagePixelContent.HashScheme;
    public int HashSchemeVersion => CanonicalImagePixelContent.HashSchemeVersion;
    public string CanonicalPixelHash { get; }
    /// <summary>The 32-byte canonical envelope plus all valid pixel bytes.</summary>
    public long CanonicalByteLength { get; }
    public string InputMetadataHash { get; }
    public string InputProvenanceHash { get; }
    public string TracePolicySnapshotHash { get; }
    /// <summary>The frozen authoritative-image retention rule, whose start event may still be pending.</summary>
    public string RetentionRuleHash { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string ContentHash { get; }
    private static string Hash(string value) => AlgorithmConfigurationValidation.Hash(value, nameof(value)).ToUpperInvariant();
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The immutable initial outbox item for asynchronous PNG finalization.</summary>
public sealed class PendingImageFinalizationWork
{
    internal PendingImageFinalizationWork(Guid workId, PendingImageManifest manifest)
    {
        if (workId == Guid.Empty) throw new ArgumentException("ProductionImageWorkIdentityInvalid", nameof(workId));
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        WorkId = workId;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-pending-image-work-v1", WorkId.ToString("D"), Kind, State,
            Manifest.InspectionId.ToString("D"), Manifest.ManifestId.ToString("D"), Manifest.StageId.ToString("D"),
            Manifest.ContentHash
        });
    }
    public Guid WorkId { get; }
    public string Kind => "FinalizePng";
    public string State => "Pending";
    public PendingImageManifest Manifest { get; }
    public string ContentHash { get; }
}

/// <summary>A frozen policy decision and its initial, atomically committed image obligation.</summary>
public sealed class ProductionImageEvidenceSnapshot
{
    internal ProductionImageEvidenceSnapshot(ProductionImageEvidenceState state, string reasonCode,
        PendingImageFinalizationWork? work = null)
    {
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if ((state == ProductionImageEvidenceState.Pending) != (work is not null))
            throw new ArgumentException("ProductionImageEvidenceWorkMismatch", nameof(work));
        State = state;
        ReasonCode = AlgorithmConfigurationValidation.Identifier(reasonCode, nameof(reasonCode));
        Work = work;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        { "sharpinspect-production-image-evidence-v1", State.ToString(), ReasonCode, work?.Manifest.ContentHash, work?.ContentHash });
    }
    public ProductionImageEvidenceState State { get; }
    public string ReasonCode { get; }
    public PendingImageManifest? Manifest => Work?.Manifest;
    public PendingImageFinalizationWork? Work { get; }
    public string ContentHash { get; }
}
