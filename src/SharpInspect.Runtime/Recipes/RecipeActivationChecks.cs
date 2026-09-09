using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Recipes;

/// <summary>Stable activation-stage observations, including explicit unexecuted checks.</summary>
internal sealed class RecipeActivationChecks
{
    private static readonly string[] Subjects =
    {
        "Authorization", "Quiescence", "ReleasedRecipe", "PartIdentity", "AssetsAndPolicies",
        "AlgorithmPreparation", "PlcResultContract", "CameraBinding", "CameraConfiguration", "Calibration",
        "FrameBufferCapacity", "FrameworkQualification", "ProviderQualification", "ProductionAcquisition",
        "PlcDeployment", "EvidenceAdmission", "DeploymentPolicies", "ProductionCycle",
        "PerformanceQualification", "StationAcceptance"
    };
    private readonly Dictionary<string, RecipeActivationCheck> _major = new(StringComparer.Ordinal);
    private readonly List<RecipeActivationCheck> _detail = new();

    internal RecipeActivationChecks()
    {
        for (var index = 0; index < Subjects.Length; index++)
            Set(index + 1, RecipeActivationCheckStatus.NotRun, "RecipeActivationCheckNotRun");
        Set(19, RecipeActivationCheckStatus.NotRun, "PerformanceQualificationRequiredBeforeArm");
        Set(20, RecipeActivationCheckStatus.NotRun, "StationAcceptanceRequiredBeforeArm");
    }
    internal void Set(int number, RecipeActivationCheckStatus status, string reason,
        string? requestedHash = null, string? effectiveHash = null)
    {
        if (number is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(number));
        var id = "V132.A" + number.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
        _major[id] = new(id, Subjects[number - 1], status, reason, requestedHash, effectiveHash);
    }
    internal void Observe(int number, bool passed, string reason, string? requestedHash = null,
        string? effectiveHash = null) => Set(number, passed ? RecipeActivationCheckStatus.Passed :
            RecipeActivationCheckStatus.Failed, reason, requestedHash, effectiveHash);
    internal void Calibration(RecipeActivationCalibrationEvaluation evaluation, bool applicable)
    {
        _detail.RemoveAll(value => value.CheckId.StartsWith("V132.K", StringComparison.Ordinal));
        Set(10, !applicable && evaluation.Allowed ? RecipeActivationCheckStatus.NotApplicable : evaluation.Allowed
            ? RecipeActivationCheckStatus.Passed : RecipeActivationCheckStatus.Failed, evaluation.ReasonCode);
        _detail.AddRange(evaluation.Observations.Select(value => new RecipeActivationCheck(value.CheckId,
            value.Subject, value.Passed ? RecipeActivationCheckStatus.Passed : RecipeActivationCheckStatus.Failed,
            value.ReasonCode, value.EvidenceHash)));
    }
    internal IReadOnlyList<RecipeActivationCheck> Snapshot() => _major.Values.OrderBy(value => value.CheckId,
        StringComparer.Ordinal).Concat(_detail.OrderBy(value => value.Subject, StringComparer.Ordinal)
            .ThenBy(value => value.CheckId, StringComparer.Ordinal)).ToArray();
    internal string? Failure => Snapshot().FirstOrDefault(value => value.Status == RecipeActivationCheckStatus.Failed)?.ReasonCode;

    internal void VerifyInstalledAuthorities(RecipeActivationInternalFixture? fixture)
    {
        foreach (var item in new[]
        {
            (12, "FrameworkQualificationAuthorityUnavailable"), (13, "ProviderQualificationAuthorityUnavailable"),
            (14, "ProductionAcquisitionAuthorityUnavailable"), (15, "PlcDeploymentAuthorityUnavailable"),
            (16, "EvidenceAdmissionAuthorityUnavailable"), (17, "DeploymentPolicyAuthorityUnavailable"),
            (18, "ProductionCycleUnavailable")
        })
            Observe(item.Item1, fixture is not null, fixture is null ? item.Item2 :
                "InternalContractFixtureAssumption", fixture?.ContentHash);
    }
}

/// <summary>
/// Closed friend-test witness for prerequisites delivered by future tickets. This never enters
/// public DI, Options, a command, or a production-authority record. Actual preparation, camera
/// configuration, calibration, authorization and storage are not replaced by this witness.
/// </summary>
internal sealed class RecipeActivationInternalFixture
{
    private RecipeActivationInternalFixture() { }
    internal static RecipeActivationInternalFixture CreateForContractTests() => new();
    internal string ContentHash { get; } = AlgorithmContractValidation.HashParts(new[]
    {
        "sharpinspect-recipe-activation-internal-fixture-v1", "DevelopmentOnly",
        "FrameworkQualification", "ProviderQualification", "ProductionAcquisition", "PlcDeployment",
        "EvidenceAdmission", "DeploymentPolicies", "ProductionCycle"
    });
}
