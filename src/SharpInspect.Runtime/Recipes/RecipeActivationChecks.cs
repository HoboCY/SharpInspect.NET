using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.Production;

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
    internal PartIdentityBindingObservation? PartIdentity { get; set; }

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

    /// <summary>
    /// Converts the independent, typed deployment observations into activation checks.
    /// Every condition is evaluated from the current snapshot and the current store
    /// configuration; no caller supplied Passed/Failed flags are accepted.
    /// </summary>
    internal void VerifyDeploymentEvidence(RecipeActivationSnapshot snapshot,
        RecipeActivationDeploymentEvidence? evidence)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (evidence is null)
        {
            SetDeploymentUnavailable();
            return;
        }

        IReadOnlyList<QualificationEvidenceResult> qualification;
        try
        {
            qualification = ProductionQualificationMatcher.Evaluate(evidence.Configuration,
                evidence.Qualifications, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            Observe(12, false, "ProductionFrameworkQualificationEvidenceInvalid");
            Observe(13, false, "ProductionProviderQualificationEvidenceInvalid");
            Observe(14, false, "ProductionCameraCapabilityEvidenceInvalid");
            Observe(15, false, "ProductionPlcDeploymentEvidenceInvalid");
            Observe(16, false, "ProductionEvidencePolicyEvidenceInvalid");
            Observe(17, false, "ProductionDeploymentConfigurationEvidenceInvalid");
            Observe(18, false, "ProductionCycleWriterEvidenceInvalid");
            return;
        }

        var framework = Find(ProductionQualificationLayer.Framework);
        Observe(12, framework.Status == QualificationEvidenceStatus.Passed,
            framework.Status == QualificationEvidenceStatus.Passed ?
                "ProductionFrameworkQualificationExactlyMatches" : framework.ReasonCode,
            evidence.Configuration.FrameworkFingerprint, framework.EvidenceRecordHash);

        var provider = Find(ProductionQualificationLayer.Provider);
        var providerHardware = Find(ProductionQualificationLayer.ProviderHardware);
        var providerPassed = provider.Status == QualificationEvidenceStatus.Passed &&
            providerHardware.Status == QualificationEvidenceStatus.Passed;
        Observe(13, providerPassed, providerPassed ?
            "ProductionProviderAndHardwareQualificationExactlyMatches" :
            provider.Status != QualificationEvidenceStatus.Passed ? provider.ReasonCode : providerHardware.ReasonCode,
            evidence.Configuration.ProviderFingerprint, QualificationHash(provider, providerHardware));

        var camera = snapshot.CameraSetup;
        var cameraReason = "ProductionCameraCapabilityExactlyMatches";
        var cameraPassed = false;
        if (camera.Binding is null)
            cameraReason = "ProductionCameraBindingMissing";
        else if (camera.Capabilities is null || camera.Requested is null || camera.Effective is null)
            cameraReason = "ProductionCameraCapabilityReadbackMissing";
        else
        {
            var reported = CameraConfigurationResult.Success(camera.Effective, camera.Differences);
            var validation = camera.Capabilities.ValidateReadBack(camera.Requested, reported);
            cameraPassed = validation.Succeeded && camera.Extension is null;
            if (!cameraPassed)
                cameraReason = camera.Extension is not null ? "ProductionCameraProviderExtensionUnsupported" :
                    validation.ReasonCode;
        }
        var cameraHash = RecipeActivationValidation.CameraHash(camera);
        Observe(14, cameraPassed, cameraReason, cameraHash, cameraHash);

        var profile = evidence.InspectionOptions.Profile;
        var plc = snapshot.PlcResultContract;
        var plcEndpoint = evidence.Configuration.Get(ProductionConfigurationBinding.PlcEndpoint);
        var plcPolicy = evidence.Configuration.Get(ProductionConfigurationBinding.PlcPolicy);
        var plcContract = evidence.Configuration.Get(ProductionConfigurationBinding.PlcResultContract);
        var plcPassed = evidence.StoreOptions.PlcCommunication is not null &&
            plcEndpoint == profile.EndpointBindingHash &&
            plcPolicy == profile.ContentHash &&
            plcContract == plc.ContentHash;
        Observe(15, plcPassed, plcPassed ? "ProductionPlcDeploymentExactlyMatches" :
            evidence.StoreOptions.PlcCommunication is null ? "ProductionPlcCommunicationStoreUnavailable" :
            plcEndpoint != profile.EndpointBindingHash ? "ProductionPlcEndpointMismatch" :
            plcPolicy != profile.ContentHash ? "ProductionPlcPolicyMismatch" :
            "ProductionPlcResultContractMismatch", profile.ContentHash, plc.ContentHash);

        var routesMatch = Outbox.ProductionOutboxBinding.RoutesMatch(evidence.StoreOptions, evidence.TracePolicy);
        var traceMatchesOptions = evidence.TracePolicy.Version == evidence.InspectionOptions.TracePolicyVersion &&
            evidence.TracePolicy.ContentHash == evidence.InspectionOptions.TracePolicySnapshotHash;
        var policyPassed = evidence.InspectionOptions.EvidenceRequirement == ProductionEvidenceRequirement.None &&
            Images.ProductionImageEvidenceBinding.IsAvailable(evidence.InspectionOptions, evidence.StoreOptions,
                snapshot.Release.Source.Content) && routesMatch && traceMatchesOptions && evidence.StartupReconciled;
        Observe(16, policyPassed, policyPassed ? "ProductionEvidencePolicyExactlyMatches" :
            evidence.InspectionOptions.EvidenceRequirement != ProductionEvidenceRequirement.None ?
                "ProductionEvidenceRequirementMustBeNone" : !routesMatch ? "ProductionOutboxRoutesUnavailable" :
            !traceMatchesOptions ? "ProductionTracePolicySnapshotMismatch" : !evidence.StartupReconciled ?
                "ProductionStartupReconciliationRequired" : "ProductionEvidenceCapturePolicyUnavailable",
            evidence.TracePolicy.ContentHash, evidence.InspectionOptions.TracePolicySnapshotHash);

        var expectedStation = ProductionAdmissionCanonical.Hash("production-station-v1",
            evidence.InspectionOptions.StationId, evidence.StoreOptions.LocalIdentity?.StationId,
            evidence.StoreOptions.AuditIntegrityPolicy?.StationId);
        var expectedPolicy = Images.ProductionImageEvidenceBinding.PolicyFingerprint(evidence.InspectionOptions,
            evidence.StoreOptions, evidence.TracePolicy);
        var deployment = evidence.InspectionOptions.Deployment;
        var deploymentPassed = evidence.Configuration.MissingBindings.Count == 0 &&
            deployment is not null &&
            evidence.Configuration.Get(ProductionConfigurationBinding.Station) == expectedStation &&
            evidence.Configuration.Get(ProductionConfigurationBinding.EvidencePolicy) == expectedPolicy &&
            evidence.Configuration.Get(ProductionConfigurationBinding.DeploymentPolicy) == deployment?.ContentHash &&
            evidence.Configuration.Get(ProductionConfigurationBinding.ConformanceProfile) == deployment?.Conformance.ContentHash;
        Observe(17, deploymentPassed, deploymentPassed ? "ProductionDeploymentConfigurationExactlyMatches" :
            evidence.Configuration.MissingBindings.Count != 0 ? "ProductionDeploymentConfigurationIncomplete" :
            deployment is null ? "ProductionDeploymentManifestMissing" :
            evidence.Configuration.Get(ProductionConfigurationBinding.Station) != expectedStation ?
                "ProductionDeploymentStationMismatch" : evidence.Configuration.Get(ProductionConfigurationBinding.EvidencePolicy) != expectedPolicy ?
                "ProductionDeploymentPolicyMismatch" : evidence.Configuration.Get(ProductionConfigurationBinding.DeploymentPolicy) != deployment.ContentHash ?
                "ProductionDeploymentManifestMismatch" : evidence.Configuration.Get(ProductionConfigurationBinding.ConformanceProfile) != deployment.Conformance.ContentHash ?
                "ProductionConformanceProfileMismatch" : "ProductionDeploymentConfigurationMismatch",
            evidence.Configuration.ObservedBindingsHash, evidence.Configuration.Get(ProductionConfigurationBinding.DeploymentPolicy));

        var content = snapshot.Release.Source.Content;
        var partIdentityPassed = content.PartIdentityRequirement?.Mode == PartIdentityRequirementMode.None ||
            PartIdentity is { } preparedIdentity && evidence.PartIdentity is { } observedIdentity &&
            preparedIdentity.RequirementHash == content.PartIdentityRequirement?.ContentHash &&
            preparedIdentity.StaticHash == observedIdentity.StaticHash &&
            evidence.StoreOptions.PartIdentities is not null && evidence.PartIdentityWriterAvailable;
        var cyclePassed = snapshot.ProductionAuthority && evidence.ProductionWriterAvailable &&
            evidence.StoreOptions.ProductionInspections is not null &&
            evidence.Configuration.Get(ProductionConfigurationBinding.PlcEndpoint) == profile.EndpointBindingHash &&
            partIdentityPassed &&
            content.AssetRequirements.Count == 0 && content.CameraProviderExtension is null &&
            Images.ProductionImageEvidenceBinding.IsAvailable(evidence.InspectionOptions, evidence.StoreOptions, content) &&
            evidence.InspectionOptions.EvidenceRequirement == ProductionEvidenceRequirement.None;
        Observe(18, cyclePassed, cyclePassed ? "ProductionCycleWriterExactlyMatches" :
            !snapshot.ProductionAuthority ? "ProductionActivationAuthorityRequired" :
            !evidence.ProductionWriterAvailable ? "ProductionInspectionWriterUnavailable" :
            evidence.StoreOptions.ProductionInspections is null ? "ProductionInspectionStoreUnavailable" :
            !partIdentityPassed ? "ProductionPartIdentityBindingUnavailable" :
            content.AssetRequirements.Count != 0 || content.CameraProviderExtension is not null ?
                "ProductionRecipeAssetsUnsupported" : "ProductionInspectionEvidenceRequirementMismatch",
            snapshot.ContentHash, evidence.ContentHash);

        QualificationEvidenceResult Find(ProductionQualificationLayer layer) =>
            qualification.Single(value => value.Layer == layer);
    }

    private void SetDeploymentUnavailable()
    {
        Observe(12, false, "ProductionDeploymentEvidenceUnavailable");
        Observe(13, false, "ProductionDeploymentEvidenceUnavailable");
        Observe(14, false, "ProductionDeploymentEvidenceUnavailable");
        Observe(15, false, "ProductionDeploymentEvidenceUnavailable");
        Observe(16, false, "ProductionDeploymentEvidenceUnavailable");
        Observe(17, false, "ProductionDeploymentEvidenceUnavailable");
        Observe(18, false, "ProductionDeploymentEvidenceUnavailable");
    }

    private static string QualificationHash(params QualificationEvidenceResult[] values) =>
        AlgorithmContractValidation.HashParts(new[] { "recipe-activation-qualification-evidence-v1" }
            .Concat(values.OrderBy(value => value.Layer).SelectMany(value => new[]
            {
                value.Layer.ToString(), value.Status.ToString(), value.ReasonCode,
                value.ExpectedFingerprint, value.ObservedFingerprint, value.EvidenceRecordHash
            })));
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
