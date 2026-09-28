using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Recipes;

internal sealed record RecipeActivationPreparedInputs(RecipeReleaseRecord Release,
    AlgorithmDescriptor Algorithm, PlcResultContractBinding PlcBinding, RecipeContractReference PlcRevision);

/// <summary>Exact local references and portable dependencies, before any camera write.</summary>
internal sealed class RecipeActivationPreparation
{
    private readonly RecipeDraftService _drafts;
    private readonly IReleasedRecipeQuery _releases;
    private readonly IPlcResultContractQuery _contracts;
    private readonly ProductionStoreOptions _options;
    private readonly PartIdentityBindingRegistry? _partIdentities;

    internal RecipeActivationPreparation(RecipeDraftService drafts, IReleasedRecipeQuery releases,
        IPlcResultContractQuery contracts, ProductionStoreOptions options, PartIdentityBindingRegistry? partIdentities = null)
    { _drafts = drafts; _releases = releases; _contracts = contracts; _options = options; _partIdentities = partIdentities; }

    internal async ValueTask<RecipeActivationPreparedInputs?> PrepareAsync(ActivateRecipeCommand command,
        RecipeActivationChecks checks, CancellationToken token)
    {
        var read = await _releases.ReadAsync(command.Candidate, token).ConfigureAwait(false);
        var release = read.Recipe;
        var exact = read.Available && release is { Available: true } &&
            release.Reference == command.Candidate && release.Record.ReleaseId == command.ReleaseId &&
            release.Record.ContentHash == command.ReleaseRecordContentHash;
        checks.Observe(3, exact, exact ? "RecipeActivationExactReleaseResolved" :
            read.Available ? "RecipeActivationReleaseIdentityMismatch" : read.ReasonCode,
            command.ReleaseRecordContentHash, release?.Record.ContentHash);
        if (!exact) return null;
        var content = release!.Content;
        if (content.PartIdentityRequirement?.Mode == PartIdentityRequirementMode.None)
            checks.Set(4, RecipeActivationCheckStatus.NotApplicable, "PartIdentityExplicitNone",
                content.PartIdentityRequirement.ContentHash);
        else
        {
            var identity = _partIdentities is null ? new PartIdentityBindingReadResult(
                content.PartIdentityRequirement is null ? "PartIdentityDeclarationMissing" : "PartIdentityBindingUnavailable") :
                await _partIdentities.CaptureAsync(content.PartIdentityRequirement, token).ConfigureAwait(false);
            checks.PartIdentity = identity.Observation;
            checks.Observe(4, identity.Available, identity.ReasonCode,
                content.PartIdentityRequirement?.ContentHash, identity.Observation?.StaticHash);
        }

        var execution = _options.RecipeDrafts!.ExecutionPolicy;
        var executionReference = new RecipeContractReference(execution.Id, execution.Version, execution.ContentHash);
        // A declared Evidence Capture requirement must resolve from the deployment catalog's
        // exact identity; an undeclared requirement keeps the existing legacy outcome.
        var evidenceCapturePolicies = _options.RecipeReleases!.EvidenceCapturePolicies;
        var policyValid = content.PolicyRequirements.Count(value => value.Kind == RecipePolicyKind.AlgorithmExecution) == 1 &&
            content.PolicyRequirements.All(value => value.Kind switch
            {
                RecipePolicyKind.AlgorithmExecution => value.Contract == executionReference,
                RecipePolicyKind.RecipeGovernance => value.Contract == _options.RecipeReleases!.Policy.Reference,
                RecipePolicyKind.EvidenceCapture => evidenceCapturePolicies is not null &&
                    evidenceCapturePolicies.Resolve(value.Contract) is not null,
                _ => false
            });
        var assetsValid = content.AssetRequirements.Count == 0 && content.CameraProviderExtension is null;
        var dependencies = assetsValid && policyValid && content.AlgorithmExecutionTimeout <= execution.MaximumExecutionTimeout;
        checks.Observe(5, dependencies, !assetsValid ? "RecipeActivationAssetAuthorityUnavailable" :
            !policyValid ? "RecipeActivationCurrentPolicyMismatch" : content.AlgorithmExecutionTimeout > execution.MaximumExecutionTimeout ?
                "RecipeActivationExecutionTimeoutPolicyMismatch" : "RecipeActivationExactDependenciesResolved", content.ContentHash);

        var algorithm = _drafts.Algorithms.SingleOrDefault(value => value.Identity == content.Algorithm.Algorithm &&
            value.ConfigurationSchema.Id == content.Algorithm.ConfigurationSchema.Id &&
            value.ConfigurationSchema.Version == content.Algorithm.ConfigurationSchema.Version &&
            value.ConfigurationSchema.ContentHash == content.Algorithm.ConfigurationSchema.ContentHash &&
            new RecipeContractReference(value.ResultSchema.Id, value.ResultSchema.Version, value.ResultSchema.ContentHash) ==
                content.Algorithm.ResultSchema &&
            new RecipeContractReference(value.ResultSchema.OverlayContract.Id, value.ResultSchema.OverlayContract.Version,
                value.ResultSchema.OverlayContract.ContentHash) == content.Algorithm.OverlayContract);
        if (algorithm is null)
        {
            checks.Observe(6, false, "RecipeActivationExactAlgorithmUnavailable");
            return null;
        }
        var current = await _contracts.ReadCurrentAsync(token).ConfigureAwait(false);
        if (!current.Available || current.Revision is null)
        {
            checks.Observe(7, false, current.Available ? "RecipeActivationPlcContractMissing" : current.ReasonCode);
            return null;
        }
        var bound = new PlcResultContractBinder().Bind(command.Candidate, algorithm.Identity,
            algorithm.ResultSchema, current.Revision.Contract);
        var publishedBinding = bound.Bound && bound.Binding is not null && current.Revision.Bindings.Any(value =>
            value.ReleaseId == release.Record.ReleaseId && value.ReleaseRecordContentHash == release.Record.ContentHash &&
            value.Binding.ContentHash == bound.Binding.ContentHash);
        checks.Observe(7, publishedBinding, bound.Bound && !publishedBinding
                ? "RecipeActivationPlcReleaseBindingMissing" : bound.ReasonCode,
            current.Revision.Reference.ContentHash, bound.Binding?.ContentHash);
        if (!publishedBinding || bound.Binding is null) return null;
        if (content.CalibrationRequirements.Count == 0)
            checks.Calibration(RecipeActivationCalibrationEvaluator.EvaluateRecords(content,
                command.CalibrationSelections, null, null, Array.Empty<object>(), DateTimeOffset.UtcNow), false);
        return new(release.Record, algorithm, bound.Binding, current.Revision.Reference);
    }
}
