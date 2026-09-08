using System.Globalization;

namespace SharpInspect.Abstractions;

public enum CalibrationVerificationState : byte { NotRequired = 1, Missing = 2, Current = 3, Failed = 4, Expired = 5 }
public enum CalibrationCompatibilityState : byte
{
    Compatible = 1, DeviceMismatch = 2, ImagingSetupMismatch = 3,
    RequestedGeometryMismatch = 4, EffectiveGeometryMismatch = 5, Unavailable = 6
}

/// <summary>An observation at a specified time, not a mutable validity flag or production admission token.</summary>
public sealed class CalibrationProfileValiditySnapshot
{
    internal CalibrationProfileValiditySnapshot(PublishedCalibrationProfileVersion profile,
        PhysicalCalibrationVerificationReference? latestVerification, CalibrationVerificationState verification,
        CalibrationCompatibilityState compatibility, DateTimeOffset evaluatedAtUtc, DateTimeOffset? validUntilUtc,
        IEnumerable<string> reasonCodes)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Profile = profile.Reference; Policy = profile.AcceptancePolicy; Evaluation = profile.Evaluation;
        SourceCandidate = profile.SourceCandidate; ImagingSetup = profile.Content.ImagingSetup;
        LatestVerification = latestVerification; Verification = verification; Compatibility = compatibility;
        EvaluatedAtUtc = evaluatedAtUtc.ToUniversalTime(); ValidUntilUtc = validUntilUtc?.ToUniversalTime();
        ReasonCodes = AlgorithmContractValidation.Copy(reasonCodes, nameof(reasonCodes), 16);
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-validity-development-v1", profile.ContentHash,
            latestVerification?.VerificationId.ToString("D"), latestVerification?.ContentHash,
            verification.ToString(), compatibility.ToString(), EvaluatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ValidUntilUtc?.ToString("O", CultureInfo.InvariantCulture), "DevelopmentOnly"
        }.Concat(ReasonCodes));
    }
    public CalibrationProfileReference Profile { get; }
    public RecipeContractReference Policy { get; }
    public CalibrationPolicyEvaluationReference Evaluation { get; }
    public CalibrationCandidateReference SourceCandidate { get; }
    public ImagingSetupRevisionReference ImagingSetup { get; }
    public PhysicalCalibrationVerificationReference? LatestVerification { get; }
    public CalibrationVerificationState Verification { get; }
    public CalibrationCompatibilityState Compatibility { get; }
    public DateTimeOffset EvaluatedAtUtc { get; }
    public DateTimeOffset? ValidUntilUtc { get; }
    public IReadOnlyList<string> ReasonCodes { get; }
    public string ContentHash { get; }
    public bool DevelopmentOnly => true;
    public bool ProductionAuthority => false;
    public bool CanAdmitNewProductionTrigger => false;
    public bool CanActivate => false;
}

/// <summary>
/// Exact calibration dependency data for a future accepted-cycle transaction.
/// This release has no production-cycle writer and cannot create a production admission snapshot.
/// </summary>
public sealed class CalibrationRunProfileBinding
{
    internal CalibrationRunProfileBinding(string requirementContentHash, PublishedCalibrationProfileVersion profile,
        PhysicalCalibrationVerificationReference? verification, DateTimeOffset? validUntilUtc)
    {
        RequirementContentHash = CameraSetupValidation.Hash(requirementContentHash, nameof(requirementContentHash));
        Profile = profile.Reference; ImagingSetup = profile.Content.ImagingSetup; Policy = profile.AcceptancePolicy;
        Evaluation = profile.Evaluation; Verification = verification; SourceCandidate = profile.SourceCandidate;
        ValidUntilUtc = validUntilUtc?.ToUniversalTime();
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-run-binding-v1", RequirementContentHash, profile.ContentHash,
            verification?.VerificationId.ToString("D"), verification?.ContentHash,
            ValidUntilUtc?.ToString("O", CultureInfo.InvariantCulture)
        });
    }
    public string RequirementContentHash { get; }
    public CalibrationProfileReference Profile { get; }
    public ImagingSetupRevisionReference ImagingSetup { get; }
    public RecipeContractReference Policy { get; }
    public CalibrationPolicyEvaluationReference Evaluation { get; }
    public PhysicalCalibrationVerificationReference? Verification { get; }
    public CalibrationCandidateReference SourceCandidate { get; }
    public DateTimeOffset? ValidUntilUtc { get; }
    public string ContentHash { get; }
}

public sealed class CalibrationRunSnapshot
{
    internal CalibrationRunSnapshot(DateTimeOffset acceptedAtUtc, IEnumerable<CalibrationRunProfileBinding> profiles)
    {
        AcceptedAtUtc = acceptedAtUtc.ToUniversalTime();
        Profiles = AlgorithmContractValidation.Copy(profiles, nameof(profiles), 8);
        if (Profiles.Select(value => value.RequirementContentHash).Distinct(StringComparer.Ordinal).Count() != Profiles.Count)
            throw new ArgumentException("CalibrationRunRequirementDuplicate");
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-run-snapshot-v1", AcceptedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        }.Concat(Profiles.OrderBy(value => value.RequirementContentHash, StringComparer.Ordinal).Select(value => value.ContentHash)));
    }
    public DateTimeOffset AcceptedAtUtc { get; }
    public IReadOnlyList<CalibrationRunProfileBinding> Profiles { get; }
    public string ContentHash { get; }
}
