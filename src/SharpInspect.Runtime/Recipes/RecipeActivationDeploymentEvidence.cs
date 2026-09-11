using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Recipes;

/// <summary>
/// The typed observation captured after a real activation has staged its resources.
/// It is an observation of independently configured authorities; it is not a caller
/// supplied set of gate results and it never grants production authority by itself.
/// </summary>
internal sealed class RecipeActivationDeploymentEvidence
{
    internal RecipeActivationDeploymentEvidence(ProductionConfiguration configuration,
        ProductionQualificationInputs qualifications, ProductionInspectionOptions inspectionOptions,
        ProductionStoreOptions storeOptions, TraceStoragePolicySnapshot tracePolicy,
        bool productionWriterAvailable, bool startupReconciled,
        PartIdentityBindingObservation? partIdentity = null, bool partIdentityWriterAvailable = false)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Qualifications = qualifications ?? throw new ArgumentNullException(nameof(qualifications));
        InspectionOptions = inspectionOptions ?? throw new ArgumentNullException(nameof(inspectionOptions));
        StoreOptions = storeOptions ?? throw new ArgumentNullException(nameof(storeOptions));
        TracePolicy = tracePolicy ?? throw new ArgumentNullException(nameof(tracePolicy));
        ProductionWriterAvailable = productionWriterAvailable;
        StartupReconciled = startupReconciled;
        PartIdentity = partIdentity;
        PartIdentityWriterAvailable = partIdentityWriterAvailable;
        ContentHash = ComputeContentHash();
    }

    internal ProductionConfiguration Configuration { get; }
    internal ProductionQualificationInputs Qualifications { get; }
    internal ProductionInspectionOptions InspectionOptions { get; }
    internal ProductionStoreOptions StoreOptions { get; }
    internal TraceStoragePolicySnapshot TracePolicy { get; }
    internal bool ProductionWriterAvailable { get; }
    internal bool StartupReconciled { get; }
    internal PartIdentityBindingObservation? PartIdentity { get; }
    internal bool PartIdentityWriterAvailable { get; }
    internal string ContentHash { get; }

    private string ComputeContentHash()
    {
        var parts = new List<string?>
        {
            PartIdentity is null ? "sharpinspect-recipe-activation-deployment-evidence-v1" :
                "sharpinspect-recipe-activation-deployment-evidence-v2",
            Configuration.ObservedBindingsHash,
            Configuration.FrameworkFingerprint,
            Configuration.ProviderFingerprint,
            Configuration.PerformanceFingerprint,
            Configuration.StationAcceptanceFingerprint,
            Configuration.ProfileHash,
            InspectionOptions.ContentHash,
            TracePolicy.ContentHash,
            StoreOptions.LocalIdentity?.PolicyContentHash,
            StoreOptions.AuditIntegrityPolicy?.ContentHash,
            StoreOptions.TraceStoragePolicies?.BindingHash,
            StoreOptions.TraceStoragePolicies?.DeploymentScope.ContentHash,
            StoreOptions.PlcCommunication?.BindingHash,
            StoreOptions.ProductionInspections?.BindingHash,
            ProductionWriterAvailable ? "writer-present" : "writer-missing",
            StartupReconciled ? "startup-reconciled" : "startup-unreconciled"
        };

        parts.Add(Configuration.Bindings.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var pair in Configuration.Bindings.OrderBy(value => value.Key))
        {
            parts.Add(((int)pair.Key).ToString(CultureInfo.InvariantCulture));
            parts.Add(pair.Value);
        }

        parts.Add(QualificationsHash(Qualifications));
        if (PartIdentity is not null)
        {
            parts.Add(PartIdentity.StaticHash);
            parts.Add(StoreOptions.PartIdentities?.BindingHash);
            parts.Add(PartIdentityWriterAvailable.ToString());
        }
        return AlgorithmContractValidation.HashParts(parts);
    }

    private static string QualificationsHash(ProductionQualificationInputs value)
    {
        var parts = new List<string?>
        {
            "sharpinspect-recipe-activation-qualification-observation-v1",
            value.Authority.ContentHash,
            value.PowerLossRequired ? "power-loss-required" : "power-loss-excluded",
            value.PowerLossExclusionHash,
            value.Requirements.Count.ToString(CultureInfo.InvariantCulture)
        };
        foreach (var requirement in value.Requirements.OrderBy(pair => pair.Key))
        {
            parts.Add(((int)requirement.Key).ToString(CultureInfo.InvariantCulture));
            parts.Add(requirement.Value.ScopeHash);
            parts.Add(requirement.Value.ContextHash);
            parts.Add(requirement.Value.Checks.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var check in requirement.Value.Checks.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                parts.Add(check.Key);
                parts.Add(check.Value.Mandatory ? "mandatory" : "optional");
                parts.Add(check.Value.Applicable ? "applicable" : "excluded");
                parts.Add(check.Value.ExclusionProofHash);
            }
        }

        parts.Add(value.Records.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var record in value.Records.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            parts.Add(record.Value.ContentHash);
        parts.Add(value.CurrentRecordHeads.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var head in value.CurrentRecordHeads.OrderBy(pair => pair.Key))
        {
            parts.Add(((int)head.Key).ToString(CultureInfo.InvariantCulture));
            parts.Add(head.Value);
        }
        return AlgorithmContractValidation.HashParts(parts);
    }
}
