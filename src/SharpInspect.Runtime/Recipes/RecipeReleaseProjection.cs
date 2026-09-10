using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Recipes;

/// <summary>Deterministic replay of audited author contributions and portable release dependencies.</summary>
internal static class RecipeReleaseProjection
{
    internal const string ContributionVersion = RecipeReleaseRecord.ContributionProjectionVersion;

    internal static IReadOnlyList<RecipeReleaseChange> Contributions(
        IReadOnlyList<RecipeDraftRevision> history, RecipeDraftRevision source)
    {
        if (history.Count > 10_000) throw Invalid("HistoryCapacityExceeded");
        var byDraft = history.GroupBy(value => value.DraftId).ToDictionary(group => group.Key,
            group => group.OrderBy(value => value.Revision).ToArray());
        var visiting = new HashSet<(Guid, long)>();
        var cache = new Dictionary<(Guid, long), Dictionary<string, RecipeReleaseChange>>();

        Dictionary<string, RecipeReleaseChange> Replay(RecipeDraftRevision target, int depth)
        {
            var key = (target.DraftId, target.Revision);
            if (cache.TryGetValue(key, out var saved)) return new(saved, StringComparer.Ordinal);
            if (depth > 64 || !visiting.Add(key)) throw Invalid("MigrationHistoryCycle");
            try
            {
                if (!byDraft.TryGetValue(target.DraftId, out var all)) throw Invalid("DraftHistoryMissing");
                var revisions = all.Where(value => value.Revision <= target.Revision).ToArray();
                if (revisions.Length != target.Revision || revisions.Length == 0 ||
                    revisions[^1].RevisionContentHash != target.RevisionContentHash)
                    throw Invalid("DraftHistoryMismatch");
                var first = revisions[0];
                var state = new Dictionary<string, RecipeReleaseChange>(StringComparer.Ordinal);
                var previous = new Dictionary<string, string>(StringComparer.Ordinal);
                if (first.Content.MigrationLineage is { } migration)
                {
                    var reference = migration.Plan.Source;
                    if (!byDraft.TryGetValue(reference.DraftId, out var sourceHistory))
                        throw Invalid("MigrationSourceMissing");
                    var origin = sourceHistory.SingleOrDefault(value => value.Revision == reference.Revision);
                    if (origin is null || origin.RevisionContentHash != reference.RevisionContentHash ||
                        origin.Position >= first.Position ||
                        origin.Content.Configuration.ContentHash != migration.InputConfigurationContentHash)
                        throw Invalid("MigrationSourceMismatch");
                    state = Replay(origin, depth + 1);
                    previous = Facets(origin.Content);
                }

                string? previousHash = null;
                for (var index = 0; index < revisions.Length; index++)
                {
                    var revision = revisions[index];
                    if (revision.Revision != index + 1 || revision.PreviousRevisionContentHash != previousHash ||
                        revision.AuthorPrincipalId == Guid.Empty ||
                        revision.Content.MigrationLineage?.ContentHash != first.Content.MigrationLineage?.ContentHash)
                        throw Invalid("DraftHistoryMismatch");
                    var current = Facets(revision.Content);
                    foreach (var path in previous.Keys.Union(current.Keys, StringComparer.Ordinal))
                    {
                        previous.TryGetValue(path, out var before);
                        current.TryGetValue(path, out var after);
                        if (before != after)
                            state[path] = new(path, before, after, revision.AuthorPrincipalId,
                                revision.DraftId, revision.Revision);
                    }
                    if (state.Count > 4096) throw Invalid("ContributionCapacityExceeded");
                    previous = current;
                    previousHash = revision.RevisionContentHash;
                }
                cache.Add(key, new(state, StringComparer.Ordinal));
                return state;
            }
            finally { visiting.Remove(key); }
        }

        return Replay(source, 0).Values.OrderBy(value => value.Path, StringComparer.Ordinal).ToArray();
    }

    internal static IReadOnlyList<RecipeReleaseValidationCheck> ValidationChecks(RecipeDraftContent content) => new[]
    {
        new RecipeReleaseValidationCheck("Structure", content.ContentHash, true, "RecipeStructureValidated"),
        new RecipeReleaseValidationCheck("Configuration", content.Configuration.ContentHash, true,
            "RecipeConfigurationValidated", new(content.Configuration.SchemaId,
                content.Configuration.SchemaVersion, content.Configuration.SchemaContentHash)),
        new RecipeReleaseValidationCheck("AlgorithmSemantic", content.Algorithm.Algorithm.Id + "/" +
            content.Algorithm.Algorithm.Version, true, "RecipeAlgorithmSemanticValidated",
            content.Algorithm.ResultSchema)
    };

    internal static IReadOnlyList<RecipeReleaseValidationCheck> ValidateDependencies(RecipeDraftContent content,
        RecipeReleaseStoreOptions releaseOptions, RecipeDraftStoreOptions draftOptions,
        IReadOnlyList<CalibrationAcceptancePolicyRevision> calibrationPolicies)
    {
        var result = new List<RecipeReleaseValidationCheck>
        {
            new("GovernancePolicy", "Deployment", true, "RecipeGovernancePolicyResolved", releaseOptions.Policy.Reference)
        };
        var execution = content.PolicyRequirements.Where(value => value.Kind == RecipePolicyKind.AlgorithmExecution).ToArray();
        if (execution.Length != 1)
            result.Add(new("PolicyDependency", "AlgorithmExecution", false, "RecipeReleaseExecutionPolicyRequired"));
        foreach (var policy in content.PolicyRequirements.OrderBy(value => value.Kind))
        {
            var resolved = policy.Kind switch
            {
                RecipePolicyKind.AlgorithmExecution => policy.Contract == new RecipeContractReference(
                    draftOptions.ExecutionPolicy.Id, draftOptions.ExecutionPolicy.Version, draftOptions.ExecutionPolicy.ContentHash),
                RecipePolicyKind.RecipeGovernance => policy.Contract == releaseOptions.Policy.Reference,
                _ => false
            };
            result.Add(new("PolicyDependency", policy.Kind.ToString(), resolved,
                resolved ? "RecipePolicyResolved" : "RecipeReleasePolicyDependencyUnavailable", policy.Contract));
        }
        foreach (var asset in content.AssetRequirements.OrderBy(value => value.Kind).ThenBy(value => value.Role, StringComparer.Ordinal))
            result.Add(new("AssetDependency", asset.Kind + "/" + asset.Role, false,
                "RecipeReleaseAssetAuthorityUnavailable", asset.Contract));
        foreach (var requirement in content.CalibrationRequirements.OrderBy(value => value.LogicalCameraRole, StringComparer.Ordinal)
                     .ThenBy(value => value.Kind).ThenBy(value => value.LogicalPurpose, StringComparer.Ordinal))
        {
            var matches = calibrationPolicies.Where(value => value.Policy.Reference == requirement.AcceptancePolicy).ToArray();
            var resolved = matches.Length == 1 && matches[0].Policy.Kind == requirement.Kind &&
                matches[0].Policy.LogicalPurpose == requirement.LogicalPurpose &&
                matches[0].Policy.CoefficientContract == requirement.CoefficientContract;
            result.Add(new("CalibrationDependency", requirement.LogicalCameraRole + "/" + requirement.Kind + "/" +
                requirement.LogicalPurpose, resolved, resolved ? "RecipeCalibrationPolicyResolved" :
                    "RecipeReleaseCalibrationDependencyUnavailable", requirement.AcceptancePolicy));
        }
        if (content.CameraProviderExtension is { } extension)
            result.Add(new("CameraExtensionDependency", extension.Provider.Id, false,
                "RecipeReleaseCameraExtensionAuthorityUnavailable"));
        if (result.Count > 253) throw Invalid("DependencyCapacityExceeded");
        return result;
    }

    /// <summary>Rebuilds recorded portable evidence without loading an algorithm or probing a station.</summary>
    internal static void ValidateRecord(RecipeReleaseRecord record, IReadOnlyList<RecipeDraftRevision> history,
        RecipeReleaseStoreOptions releaseOptions, RecipeDraftStoreOptions draftOptions,
        IReadOnlyList<CalibrationAcceptancePolicyRevision> calibrationPolicies)
    {
        var source = history.SingleOrDefault(value => value.DraftId == record.Source.DraftId &&
            value.Revision == record.Source.Revision);
        if (source is null || source.RevisionContentHash != record.Source.RevisionContentHash ||
            source.Content.ContentHash != record.Source.Content.ContentHash ||
            source.Position != record.Source.Position || source.OperationId != record.Source.OperationId ||
            source.PreviousRevisionContentHash != record.Source.PreviousRevisionContentHash ||
            source.AuthorPrincipalId != record.Source.AuthorPrincipalId || source.AuthorSessionId != record.Source.AuthorSessionId ||
            source.AuthorAuthorizationRevision != record.Source.AuthorAuthorizationRevision ||
            source.ChangeReason != record.Source.ChangeReason || source.RecordedAtUtc != record.Source.RecordedAtUtc ||
            record.GovernancePolicy.Reference != releaseOptions.Policy.Reference)
            throw Invalid("RecordSourceMismatch");
        var changes = Contributions(history, source);
        if (!changes.Select(value => value.ContentHash).SequenceEqual(record.Changes.Select(value => value.ContentHash)))
            throw Invalid("RecordAttributionMismatch");
        var checks = ValidationChecks(source.Content).Concat(ValidateDependencies(source.Content, releaseOptions,
            draftOptions, calibrationPolicies.Where(value => value.RecordedAtUtc <= record.ReleasedAtUtc).ToArray())).ToArray();
        if (checks.Any(value => !value.Passed) ||
            !checks.Select(value => value.ContentHash).SequenceEqual(record.Checks.Select(value => value.ContentHash)))
            throw Invalid("RecordValidationMismatch");
        if (record.GovernancePolicy.Mode == RecipeGovernanceMode.MakerCheckerRelease &&
            record.ContributingAuthors.Contains(record.ApproverPrincipalId))
            throw Invalid("MakerCheckerConflict");
        var command = new ReleaseRecipeCommand(record.OperationId,
            new(CommandSource.PhysicalConsole, record.ApproverPrincipalId.ToString("D"), record.ApproverSessionId, record.StepUpGrantId),
            source.DraftId, source.Revision, source.RevisionContentHash, record.GovernancePolicy.Reference, record.ReleaseReason);
        if (command.AuthorizationTarget != record.AuthorizationTarget || record.ReleasedAtUtc < source.RecordedAtUtc)
            throw Invalid("RecordApprovalMismatch");
    }

    private static Dictionary<string, string> Facets(RecipeDraftContent content)
    {
        if (!RecipeDraftStorageCodec.TryEncodeContent(content, out var encoded, out _))
            throw Invalid("ContributionContentInvalid");
        using var document = JsonDocument.Parse(encoded!.PayloadJson);
        var root = document.RootElement;
        if (root.GetProperty("FormatVersion").GetInt32() is < 1 or > 6 ||
            root.GetProperty("CanonicalizationVersion").GetInt32() != 1)
            throw Invalid("ContributionFormatUnsupported");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string path, JsonElement value) => result.Add(path, value.GetRawText());
        void Flatten(string path, JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) { Add(path, value); return; }
            foreach (var property in value.EnumerateObject()) Flatten(path + "/" + property.Name, property.Value);
        }
        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "FormatVersion": case "CanonicalizationVersion": case "ContentHash": break;
                case "RecipeKey": case "DisplayName": case "CameraRole": case "AlgorithmExecutionTimeoutTicks":
                    Add(property.Name, property.Value); break;
                case "PartIdentityRequirement":
                    Add(property.Name, property.Value); break;
                case "CameraProviderExtension": case "MigrationLineage":
                    if (property.Value.ValueKind != JsonValueKind.Null) Add(property.Name, property.Value);
                    break;
                case "Camera": Flatten("Camera", property.Value); break;
                case "Algorithm":
                    foreach (var binding in property.Value.EnumerateObject())
                    {
                        if (binding.Name == "ConfigurationSchema")
                            result.Add("Algorithm/ConfigurationSchema", JsonSerializer.Serialize(new[]
                            {
                                binding.Value.GetProperty("Id").GetString(), binding.Value.GetProperty("Version").GetString(),
                                binding.Value.GetProperty("ContentHash").GetString()
                            }));
                        else if (binding.Name is "Algorithm" or "ResultSchema" or "OverlayContract")
                            Add("Algorithm/" + binding.Name, binding.Value);
                        else throw Invalid("ContributionFormatUnsupported");
                    }
                    break;
                case "Configuration":
                    foreach (var field in property.Value.EnumerateObject())
                    {
                        if (field.Name == "Values")
                            foreach (var entry in field.Value.EnumerateArray())
                                Add("Configuration/" + entry.GetProperty("Key").GetString(), entry);
                        else if (field.Name is not ("SchemaId" or "SchemaVersion" or "SchemaContentHash" or
                                     "CanonicalizationVersion" or "ContentHash"))
                            throw Invalid("ContributionFormatUnsupported");
                    }
                    break;
                case "ValueOrigins":
                    foreach (var entry in property.Value.EnumerateArray()) Add("ValueOrigin/" + entry.GetProperty("Key").GetString(), entry);
                    break;
                case "AssetRequirements":
                    foreach (var entry in property.Value.EnumerateArray())
                        Add("Asset/" + entry.GetProperty("Kind").GetString() + "/" + entry.GetProperty("Role").GetString(), entry);
                    break;
                case "PolicyRequirements":
                    foreach (var entry in property.Value.EnumerateArray()) Add("Policy/" + entry.GetProperty("Kind").GetString(), entry);
                    break;
                case "CalibrationRequirements":
                    foreach (var entry in property.Value.EnumerateArray())
                        Add("Calibration/" + entry.GetProperty("LogicalCameraRole").GetString() + "/" +
                            entry.GetProperty("Kind").GetString() + "/" + entry.GetProperty("LogicalPurpose").GetString(), entry);
                    break;
                default: throw Invalid("ContributionFormatUnsupported");
            }
        }
        return result;
    }

    private static InvalidOperationException Invalid(string suffix) => new("RecipeRelease" + suffix);
}
