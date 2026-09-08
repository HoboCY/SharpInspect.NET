using System.Globalization;
using System.Security.Cryptography;

namespace SharpInspect.Abstractions;

/// <summary>Versioned, bounded coefficient data. The format contract defines units and coordinate conventions.</summary>
public sealed class CalibrationCoefficientPayload
{
    public const int MaximumBytes = 64 * 1024;
    private readonly byte[] _bytes;
    public CalibrationCoefficientPayload(RecipeContractReference format, ReadOnlyMemory<byte> canonicalBytes)
    {
        Format = format ?? throw new ArgumentNullException(nameof(format));
        if (canonicalBytes.Length is < 1 or > MaximumBytes)
            throw new ArgumentException("CalibrationCoefficientPayloadSizeInvalid", nameof(canonicalBytes));
        _bytes = canonicalBytes.ToArray();
        ContentHash = Convert.ToHexString(SHA256.HashData(_bytes));
    }
    public RecipeContractReference Format { get; }
    public int Length => _bytes.Length;
    public string ContentHash { get; }
    public byte[] GetBytes() => (byte[])_bytes.Clone();
}

/// <summary>Exact public frame geometry. It does not include exposure, gain or a vendor pixel-format enum.</summary>
public sealed record CalibrationFrameGeometry
{
    public CalibrationFrameGeometry(RegionOfInterest regionOfInterest, int frameWidth, int frameHeight,
        VisionPixelFormat pixelFormat, int? validBits)
    {
        RegionOfInterest = regionOfInterest ?? throw new ArgumentNullException(nameof(regionOfInterest));
        if (frameWidth < 1 || frameHeight < 1 || frameWidth != regionOfInterest.Width || frameHeight != regionOfInterest.Height)
            throw new ArgumentException("CalibrationFrameGeometryInvalid");
        // Reuse the complete public camera value-domain rules for canonical format and valid bits.
        _ = new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 1, 0,
            regionOfInterest, pixelFormat, validBits, 1, 0, null);
        FrameWidth = frameWidth; FrameHeight = frameHeight; PixelFormat = pixelFormat; ValidBits = validBits;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-geometry-v1", Number(regionOfInterest.OffsetX), Number(regionOfInterest.OffsetY),
            Number(regionOfInterest.Width), Number(regionOfInterest.Height), Number(frameWidth), Number(frameHeight),
            pixelFormat.ToString(), validBits?.ToString(CultureInfo.InvariantCulture)
        });
    }
    public RegionOfInterest RegionOfInterest { get; }
    public int FrameWidth { get; }
    public int FrameHeight { get; }
    public VisionPixelFormat PixelFormat { get; }
    public int? ValidBits { get; }
    public string ContentHash { get; }
    public static CalibrationFrameGeometry FromRequested(RequestedCameraConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new(configuration.RegionOfInterest, configuration.RegionOfInterest.Width,
            configuration.RegionOfInterest.Height, configuration.PixelFormat, configuration.ValidBits);
    }
    public static CalibrationFrameGeometry FromEffective(EffectiveCameraConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new(configuration.RegionOfInterest, configuration.RegionOfInterest.Width,
            configuration.RegionOfInterest.Height, configuration.PixelFormat, configuration.ValidBits);
    }
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

public sealed record ImagingSetupRevisionReference
{
    public ImagingSetupRevisionReference(string logicalCameraRole, Guid revisionId, long revision, string revisionHash)
    {
        LogicalCameraRole = AlgorithmConfigurationValidation.Identifier(logicalCameraRole, nameof(logicalCameraRole));
        if (revisionId == Guid.Empty || revision < 1) throw new ArgumentException("ImagingSetupReferenceInvalid");
        RevisionId = revisionId; Revision = revision;
        RevisionHash = CameraSetupValidation.Hash(revisionHash, nameof(revisionHash));
    }
    public string LogicalCameraRole { get; }
    public Guid RevisionId { get; }
    public long Revision { get; }
    public string RevisionHash { get; }
    public static ImagingSetupRevisionReference FromRevision(ImagingSetupRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return new(revision.LogicalCameraRole, revision.RevisionId, revision.Revision, revision.RevisionHash);
    }
}

/// <summary>
/// Immutable equipment-asset data with computed identity. Construction is not acceptance,
/// publication, current validity or permission to use these coefficients in production.
/// </summary>
public sealed class CalibrationProfileContent
{
    public CalibrationProfileContent(CalibrationRequirement requirement, CameraBindingTarget device,
        ImagingSetupRevisionReference imagingSetup, CalibrationFrameGeometry requestedGeometry,
        CalibrationFrameGeometry effectiveGeometry, CalibrationCoefficientPayload coefficients,
        RecipeContractReference procedure, string sourceEvidenceHash, Guid createdBy, DateTimeOffset createdAtUtc)
    {
        Requirement = requirement ?? throw new ArgumentNullException(nameof(requirement));
        Device = device ?? throw new ArgumentNullException(nameof(device));
        ImagingSetup = imagingSetup ?? throw new ArgumentNullException(nameof(imagingSetup));
        RequestedGeometry = requestedGeometry ?? throw new ArgumentNullException(nameof(requestedGeometry));
        EffectiveGeometry = effectiveGeometry ?? throw new ArgumentNullException(nameof(effectiveGeometry));
        Coefficients = coefficients ?? throw new ArgumentNullException(nameof(coefficients));
        Procedure = procedure ?? throw new ArgumentNullException(nameof(procedure));
        if (imagingSetup.LogicalCameraRole != requirement.LogicalCameraRole ||
            coefficients.Format != requirement.CoefficientContract || createdBy == Guid.Empty)
            throw new ArgumentException("CalibrationProfileContentBindingInvalid");
        SourceEvidenceHash = CameraSetupValidation.Hash(sourceEvidenceHash, nameof(sourceEvidenceHash));
        CreatedBy = createdBy; CreatedAtUtc = createdAtUtc.ToUniversalTime();
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-profile-content-v1", requirement.ContentHash, device.ContentHash,
            imagingSetup.LogicalCameraRole, imagingSetup.RevisionId.ToString("D"),
            imagingSetup.Revision.ToString(CultureInfo.InvariantCulture), imagingSetup.RevisionHash,
            requestedGeometry.ContentHash, effectiveGeometry.ContentHash,
            coefficients.Format.Id, coefficients.Format.Version, coefficients.Format.ContentHash,
            coefficients.ContentHash, procedure.Id, procedure.Version, procedure.ContentHash,
            SourceEvidenceHash, CreatedBy.ToString("D"), CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        });
    }
    public CalibrationRequirement Requirement { get; }
    public CameraBindingTarget Device { get; }
    public ImagingSetupRevisionReference ImagingSetup { get; }
    public CalibrationFrameGeometry RequestedGeometry { get; }
    public CalibrationFrameGeometry EffectiveGeometry { get; }
    public CalibrationCoefficientPayload Coefficients { get; }
    public RecipeContractReference Procedure { get; }
    public string SourceEvidenceHash { get; }
    public Guid CreatedBy { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string ContentHash { get; }
}

/// <summary>A selection input with exact identity and bytes; it never searches for a latest version.</summary>
public sealed record CalibrationProfileReference
{
    public CalibrationProfileReference(Guid profileId, long version, string contentHash)
    {
        if (profileId == Guid.Empty || version < 1) throw new ArgumentException("CalibrationProfileReferenceInvalid");
        ProfileId = profileId; Version = version;
        ContentHash = CameraSetupValidation.Hash(contentHash, nameof(contentHash));
    }
    public Guid ProfileId { get; }
    public long Version { get; }
    public string ContentHash { get; }
}

public sealed record CalibrationProfileSelection
{
    public CalibrationProfileSelection(string requirementContentHash, CalibrationProfileReference profile)
    {
        RequirementContentHash = CameraSetupValidation.Hash(requirementContentHash, nameof(requirementContentHash));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }
    public string RequirementContentHash { get; }
    public CalibrationProfileReference Profile { get; }
}

public sealed record CalibrationRequirementObservation(string RequirementContentHash, bool Compatible,
    string ReasonCode, CalibrationProfileReference? SelectedProfile = null);

/// <summary>Compatibility diagnostics only. The publication and current-validity authorities are not delivered by this check.</summary>
public sealed class CalibrationRequirementCheckResult
{
    internal CalibrationRequirementCheckResult(bool available, bool hasRequirements, bool compatible,
        string reasonCode, IEnumerable<CalibrationRequirementObservation> observations,
        ImagingSetupRevisionReference? imagingSetup = null)
    {
        Available = available; HasRequirements = hasRequirements; Compatible = compatible;
        ReasonCode = reasonCode; ImagingSetup = imagingSetup;
        Observations = AlgorithmContractValidation.Copy(observations, nameof(observations), 8);
    }
    public bool Available { get; }
    public bool HasRequirements { get; }
    public bool Compatible { get; }
    public string ReasonCode { get; }
    public IReadOnlyList<CalibrationRequirementObservation> Observations { get; }
    public ImagingSetupRevisionReference? ImagingSetup { get; }
    public string EvidencePurpose => "DevelopmentOnly";
    public bool ProductionAuthority => false;
    public bool CanActivate => false;
    /// <summary>Only the calibration dependency's debug check; all ordinary debug/session authorization remains required.</summary>
    public bool CalibrationAllowsNonProductionDebug => Available && !HasRequirements;
    public bool CalibrationActivationGateSatisfied => Available && !HasRequirements;
}

public interface ICalibrationRequirementResolver
{
    ValueTask<CalibrationRequirementCheckResult> CheckAsync(RecipeDraftContent recipe,
        IEnumerable<CalibrationProfileSelection> selections, CommandInvocation invocation,
        CancellationToken cancellationToken = default);
}
