using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace SharpInspect.Abstractions;

/// <summary>
/// An opaque, immutable calibration export container. The bytes are untrusted until
/// the Runtime export codec validates its structure and all contained evidence.
/// </summary>
public sealed class CalibrationExportPackage
{
    public const int MaximumBytes = 64 * 1024 * 1024;

    private readonly byte[] _bytes;

    /// <summary>Creates an import package from an external byte copy.</summary>
    public CalibrationExportPackage(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length is < 1 or > MaximumBytes)
            throw new ArgumentException("CalibrationExportPackageSizeInvalid", nameof(bytes));

        _bytes = (byte[])bytes.Clone();
        ContentHash = Convert.ToHexString(SHA256.HashData(_bytes));
    }

    public int Length => _bytes.Length;

    /// <summary>SHA-256 of the exact opaque container bytes.</summary>
    public string ContentHash { get; }

    /// <summary>Returns a fresh copy; mutating it cannot change this package.</summary>
    public byte[] GetBytes() => (byte[])_bytes.Clone();
}

/// <summary>The closed set of payload members accepted by the export container.</summary>
public enum CalibrationExportMemberKind : byte
{
    SessionEvidence = 1,
    AcceptancePolicy = 2,
    FrameImages = 3,
    PublishedProfile = 4
}

/// <summary>One exact payload member entry in an export manifest.</summary>
public sealed class CalibrationExportMemberManifest
{
    internal CalibrationExportMemberManifest(CalibrationExportMemberKind kind, int length,
        string contentHash)
    {
        if (!Enum.IsDefined(typeof(CalibrationExportMemberKind), kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (length is < 1 or > CalibrationExportPackage.MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(length));
        Kind = kind;
        Length = length;
        ContentHash = AlgorithmConfigurationValidation.Hash(contentHash, nameof(contentHash))
            .ToUpperInvariant();
    }

    public CalibrationExportMemberKind Kind { get; }
    public int Length { get; }
    public string ContentHash { get; }
}

/// <summary>
/// Exact source identity retained for revalidation. These values do not confer
/// local acceptance, publication, or production authority.
/// </summary>
public sealed class CalibrationExportSourceIdentity
{
    internal CalibrationExportSourceIdentity(Guid sessionId,
        CalibrationCandidateReference? candidate, CalibrationProfileReference? profile)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("CalibrationExportSessionIdentityInvalid", nameof(sessionId));
        if (candidate is not null && candidate.SessionId != sessionId)
            throw new ArgumentException("CalibrationExportCandidateSessionMismatch", nameof(candidate));
        SessionId = sessionId;
        Candidate = candidate;
        Profile = profile;
    }

    public Guid SessionId { get; }
    public CalibrationCandidateReference? Candidate { get; }
    public CalibrationProfileReference? Profile { get; }
}

/// <summary>
/// Versioned portability metadata. It describes compatibility and provenance only;
/// it is never a local production authority or an activation certificate.
/// </summary>
public sealed class CalibrationExportManifest
{
    public const int CurrentFormatVersion = 1;

    internal CalibrationExportManifest(int formatVersion, Guid packageId, string sourceStationId,
        CalibrationExportSourceIdentity source, RecipeContractReference procedure,
        RecipeContractReference acceptancePolicy, CameraBindingTarget device,
        ImagingSetupRevisionReference imagingSetup, CalibrationFrameGeometry requestedGeometry,
        CalibrationFrameGeometry effectiveGeometry,
        IEnumerable<CalibrationExportMemberManifest> members, string contentHash)
    {
        if (formatVersion != CurrentFormatVersion)
            throw new ArgumentOutOfRangeException(nameof(formatVersion));
        if (packageId == Guid.Empty)
            throw new ArgumentException("CalibrationExportPackageIdentityInvalid", nameof(packageId));
        FormatVersion = formatVersion;
        PackageId = packageId;
        SourceStationId = AlgorithmConfigurationValidation.Identifier(sourceStationId,
            nameof(sourceStationId));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Procedure = procedure ?? throw new ArgumentNullException(nameof(procedure));
        AcceptancePolicy = acceptancePolicy ?? throw new ArgumentNullException(nameof(acceptancePolicy));
        Device = device ?? throw new ArgumentNullException(nameof(device));
        ImagingSetup = imagingSetup ?? throw new ArgumentNullException(nameof(imagingSetup));
        RequestedGeometry = requestedGeometry ?? throw new ArgumentNullException(nameof(requestedGeometry));
        EffectiveGeometry = effectiveGeometry ?? throw new ArgumentNullException(nameof(effectiveGeometry));

        var copied = AlgorithmConfigurationValidation.CopyBounded(members,
            maximum: 4, capacityCode: "CalibrationExportMemberCapacityExceeded",
            parameterName: nameof(members));
        if (copied.Length is < 3 or > 4 ||
            copied.Select(member => member.Kind).Distinct().Count() != copied.Length)
            throw new ArgumentException("CalibrationExportMemberSetInvalid", nameof(members));
        Members = new ReadOnlyCollection<CalibrationExportMemberManifest>(copied);
        ContentHash = AlgorithmConfigurationValidation.Hash(contentHash, nameof(contentHash))
            .ToUpperInvariant();
    }

    public int FormatVersion { get; }
    public Guid PackageId { get; }
    public string SourceStationId { get; }
    public CalibrationExportSourceIdentity Source { get; }
    public RecipeContractReference Procedure { get; }
    public RecipeContractReference AcceptancePolicy { get; }
    public CameraBindingTarget Device { get; }
    public ImagingSetupRevisionReference ImagingSetup { get; }
    public CalibrationFrameGeometry RequestedGeometry { get; }
    public CalibrationFrameGeometry EffectiveGeometry { get; }
    public ReadOnlyCollection<CalibrationExportMemberManifest> Members { get; }
    public string ContentHash { get; }

    /// <summary>Export provenance remains external evidence, never production authority.</summary>
    public bool ProductionAuthority => false;
}
