using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Images;

internal static class ProductionImageEvidenceBinding
{
    internal static EvidenceCapturePolicySnapshot? Resolve(ProductionInspectionOptions options,
        ProductionStoreOptions store, RecipeDraftContent recipe)
    {
        var reference = recipe.PolicyRequirements.SingleOrDefault(value =>
            value.Kind == RecipePolicyKind.EvidenceCapture)?.Contract;
        if (options.ImageStage is null)
        {
            if (reference is not null) throw new InvalidOperationException("ProductionImageStageConfigurationRequired");
            return null;
        }
        if (reference is null) throw new InvalidOperationException("ProductionEvidenceCaptureDeclarationRequired");
        if (store.ImageEvidence?.Stage.ContentHash != options.ImageStage.ContentHash)
            throw new InvalidOperationException("ProductionImageEvidenceConfigurationMismatch");
        return store.RecipeReleases?.EvidenceCapturePolicies?.Resolve(reference) ??
            throw new InvalidOperationException("ProductionEvidenceCapturePolicyUnavailable");
    }

    internal static bool IsAvailable(ProductionInspectionOptions? options, ProductionStoreOptions? store,
        RecipeDraftContent? recipe)
    {
        if (options is null || store is null || recipe is null) return false;
        try { _ = Resolve(options, store, recipe); return true; }
        catch (InvalidOperationException) { return false; }
    }

    internal static string PolicyFingerprint(ProductionInspectionOptions options, ProductionStoreOptions store,
        TraceStoragePolicySnapshot trace)
    {
        var legacy = ProductionAdmissionCanonical.Hash("production-evidence-policy-v1",
            options.EvidenceRequirement.ToString(), trace.ContentHash, store.TraceStoragePolicies?.DeploymentScope.ContentHash);
        return options.ImageStage is null ? legacy : ProductionAdmissionCanonical.Hash("production-evidence-policy-v2",
            legacy, options.ImageStage.ContentHash, store.ImageEvidence?.BindingHash,
            store.RecipeReleases?.EvidenceCapturePolicies?.ContentHash);
    }
}
