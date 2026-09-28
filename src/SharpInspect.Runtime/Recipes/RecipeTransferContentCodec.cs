using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Recipes;

/// <summary>Explicit portable projection. No factory, provider, dependency loader or semantic callback is invoked.</summary>
internal static class RecipeTransferContentCodec
{
    private const string PortableKey = "transfer";
    private const string PortableDisplayName = "Transferred recipe";
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 32 };

    internal static IReadOnlyList<RecipeTransferDependency> Dependencies(RecipeDraftContent content)
    {
        var algorithm = content.Algorithm;
        var schema = algorithm.ConfigurationSchema;
        var algorithmBindingHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-transfer-algorithm-binding-v1", algorithm.Algorithm.Id, algorithm.Algorithm.Version,
            schema.Id, schema.Version, schema.ContentHash,
            algorithm.ResultSchema.Id, algorithm.ResultSchema.Version, algorithm.ResultSchema.ContentHash,
            algorithm.OverlayContract.Id, algorithm.OverlayContract.Version, algorithm.OverlayContract.ContentHash
        });
        var values = new List<RecipeTransferDependency>
        {
            new("Algorithm", new(algorithm.Algorithm.Id, algorithm.Algorithm.Version, algorithmBindingHash)),
            new("ConfigurationSchema", new(schema.Id, schema.Version, schema.ContentHash)),
            new("ResultSchema", algorithm.ResultSchema), new("OverlayContract", algorithm.OverlayContract)
        };
        values.AddRange(content.AssetRequirements.Select(item => item.Kind == RecipeAssetKind.AlgorithmModel
            ? new RecipeTransferDependency("AlgorithmModel", item.Contract)
            : throw new InvalidOperationException("RecipeTransferLocalDependencyForbidden")));
        values.AddRange(content.PolicyRequirements.Select(item => new RecipeTransferDependency(item.Kind switch
        {
            RecipePolicyKind.AlgorithmExecution => "AlgorithmExecutionPolicy",
            RecipePolicyKind.ImageAcquisition => "ImageAcquisitionPolicy",
            RecipePolicyKind.RecipeGovernance => "RecipeGovernancePolicy",
            RecipePolicyKind.CalibrationAcceptance => "CalibrationAcceptancePolicy",
            RecipePolicyKind.EvidenceCapture => "EvidenceCapturePolicy",
            _ => throw new InvalidOperationException("RecipeTransferDependencyInvalid")
        }, item.Contract)));
        foreach (var requirement in content.CalibrationRequirements)
        {
            values.Add(new("CalibrationCoefficientContract", requirement.CoefficientContract));
            values.Add(new("CalibrationAcceptancePolicy", requirement.AcceptancePolicy));
        }
        if (content.PartIdentityRequirement?.Format is { } format) values.Add(new("PartIdentityFormat", format));
        return values.GroupBy(item => (item.Kind, item.Contract.Id, item.Contract.Version, item.Contract.ContentHash))
            .Select(group => group.First()).OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Contract.Id, StringComparer.Ordinal).ThenBy(item => item.Contract.Version, StringComparer.Ordinal)
            .ThenBy(item => item.Contract.ContentHash, StringComparer.Ordinal).ToArray();
    }

    internal static bool DependenciesMatch(RecipeTransferPackageManifest manifest, RecipeDraftContent content) =>
        manifest.Dependencies.Select(item => (item.Kind, item.Contract.Id, item.Contract.Version, item.Contract.ContentHash))
            .OrderBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Version, StringComparer.Ordinal).ThenBy(item => item.ContentHash, StringComparer.Ordinal)
            .SequenceEqual(Dependencies(content).Select(item => (item.Kind, item.Contract.Id, item.Contract.Version, item.Contract.ContentHash)));

    internal static bool TryEncode(RecipeDraftContent source, RecipeTransferPortablePolicy policy,
        out byte[]? bytes, out RecipeDraftContent? projection, out string reason)
    {
        bytes = null; projection = null; reason = "RecipeTransferUnsupportedPortableContent";
        try
        {
            RequirePortable(source, policy);
            projection = Project(source, PortableKey);
            if (!RecipeDraftStorageCodec.TryEncodeContent(projection, out var document, out reason)) return false;
            var root = JsonNode.Parse(document!.PayloadJson, documentOptions: JsonOptions)!.AsObject();
            root["Algorithm"]!["ConfigurationSchema"]!.AsObject().Remove("Fields");
            root.Add("TransferFormatVersion", 1);
            bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
            if (bytes.Length > RecipeDraftStorageCodec.MaximumPayloadBytes)
                throw new InvalidOperationException("RecipeTransferContentTooLarge");
            reason = "RecipeTransferPortableContentEncoded";
            return true;
        }
        catch (Exception exception) when (Bounded(exception))
        {
            bytes = null; projection = null; reason = Reason(exception); return false;
        }
    }

    internal static bool TryDecode(byte[] bytes, IReadOnlyList<AlgorithmDescriptor> localDescriptors,
        RecipeTransferPortablePolicy policy, Guid newDraftId, out RecipeDraftDocument? document, out string reason)
    {
        document = null; reason = "RecipeTransferContentInvalid";
        try
        {
            if (bytes.Length == 0 || bytes.Length > RecipeDraftStorageCodec.MaximumPayloadBytes || newDraftId == Guid.Empty)
                throw new InvalidOperationException("RecipeTransferContentTooLarge");
            using (var parsed = JsonDocument.Parse(bytes, JsonOptions)) RequireUniqueProperties(parsed.RootElement);
            var root = JsonNode.Parse(bytes, documentOptions: JsonOptions)!.AsObject();
            if (root["TransferFormatVersion"]?.GetValue<int>() != 1)
                throw new InvalidOperationException("RecipeTransferContentVersionUnsupported");
            root.Remove("TransferFormatVersion");
            var algorithm = root["Algorithm"]!.AsObject();
            var identity = algorithm["Algorithm"]!.AsObject();
            var schema = algorithm["ConfigurationSchema"]!.AsObject();
            if (schema.Count != 3 || !schema.ContainsKey("Id") || !schema.ContainsKey("Version") || !schema.ContainsKey("ContentHash"))
                throw new InvalidOperationException("RecipeTransferSchemaMetadataForbidden");
            var matching = localDescriptors.Where(item => item.Identity.Id == identity["Id"]?.GetValue<string>() &&
                item.Identity.Version == identity["Version"]?.GetValue<string>()).Take(2).ToArray();
            if (matching.Length != 1) throw new InvalidOperationException("RecipeTransferAlgorithmUnavailable");
            var descriptor = matching[0];
            if (descriptor.ConfigurationSchema.Id != schema["Id"]?.GetValue<string>() ||
                descriptor.ConfigurationSchema.Version != schema["Version"]?.GetValue<string>() ||
                descriptor.ConfigurationSchema.ContentHash != schema["ContentHash"]?.GetValue<string>())
                throw new InvalidOperationException("RecipeTransferSchemaIncompatible");
            algorithm["ConfigurationSchema"] = JsonNode.Parse(RecipeDraftStorageCodec.EncodeSchemaForTransfer(descriptor.ConfigurationSchema));
            if (!RecipeDraftStorageCodec.TryDecodeContent(root.ToJsonString(), out var content, out reason)) return false;
            if (content!.RecipeKey != PortableKey || content.DisplayName != PortableDisplayName ||
                content.MigrationLineage is not null || content.LifecycleLineage is not null ||
                content.ValueOrigins.Any(item => item.Origin != RecipeDraftValueOrigin.Explicit))
                throw new InvalidOperationException("RecipeTransferAuthorityContentForbidden");
            RequirePortable(content, policy);
            if (content.Algorithm.ResultSchema.Id != descriptor.ResultSchema.Id ||
                content.Algorithm.ResultSchema.Version != descriptor.ResultSchema.Version ||
                content.Algorithm.ResultSchema.ContentHash != descriptor.ResultSchema.ContentHash ||
                content.Algorithm.OverlayContract.Id != descriptor.ResultSchema.OverlayContract.Id ||
                content.Algorithm.OverlayContract.Version != descriptor.ResultSchema.OverlayContract.Version ||
                content.Algorithm.OverlayContract.ContentHash != descriptor.ResultSchema.OverlayContract.ContentHash)
                throw new InvalidOperationException("RecipeTransferResultSchemaIncompatible");
            if (content.Configuration.Validate(descriptor.ConfigurationSchema).Count != 0)
                throw new InvalidOperationException("RecipeTransferConfigurationInvalid");
            return RecipeDraftStorageCodec.TryEncodeContent(Project(content, "import." + newDraftId.ToString("N")),
                out document, out reason);
        }
        catch (Exception exception) when (Bounded(exception))
        { document = null; reason = Reason(exception); return false; }
    }

    private static RecipeDraftContent Project(RecipeDraftContent source, string recipeKey) => new(recipeKey,
        PortableDisplayName, source.Algorithm, source.Configuration, source.CameraRole, source.Camera,
        source.AlgorithmExecutionTimeout, source.AssetRequirements, source.PolicyRequirements,
        source.Configuration.Values.Select(item => new RecipeDraftFieldOrigin(item.Key, RecipeDraftValueOrigin.Explicit)),
        calibrationRequirements: source.CalibrationRequirements, partIdentityRequirement: source.PartIdentityRequirement);

    private static void RequirePortable(RecipeDraftContent content, RecipeTransferPortablePolicy policy)
    {
        if (content.CameraProviderExtension is not null || content.AssetRequirements.Any(item => item.Kind == RecipeAssetKind.Calibration))
            throw new InvalidOperationException("RecipeTransferLocalDependencyForbidden");
        var declared = policy.Contracts.SingleOrDefault(item => item.Algorithm == content.Algorithm.Algorithm &&
            item.ConfigurationSchema.Id == content.Algorithm.ConfigurationSchema.Id &&
            item.ConfigurationSchema.Version == content.Algorithm.ConfigurationSchema.Version &&
            item.ConfigurationSchema.ContentHash == content.Algorithm.ConfigurationSchema.ContentHash);
        if (declared is null || content.Configuration.Values.Any(item => !declared.PortableFieldKeys.Contains(item.Key)))
            throw new InvalidOperationException("RecipeTransferPortablePolicyDenied");
        if (content.Configuration.Values.Any(item => content.Algorithm.ConfigurationSchema.Fields.Single(field => field.Key == item.Key)
                .TransferClassification != AlgorithmConfigurationTransferClassification.PortableRecipeData))
            throw new InvalidOperationException("RecipeTransferFieldNotPortable");
        Token(content.CameraRole);
        Token(content.Algorithm.Algorithm.Id); Token(content.Algorithm.Algorithm.Version);
        Token(content.Algorithm.ConfigurationSchema.Id); Token(content.Algorithm.ConfigurationSchema.Version);
        Reference(content.Algorithm.ResultSchema); Reference(content.Algorithm.OverlayContract);
        foreach (var entry in content.Configuration.Values) Token(entry.Key);
        foreach (var requirement in content.AssetRequirements) { Token(requirement.Role); Reference(requirement.Contract); }
        foreach (var requirement in content.PolicyRequirements) Reference(requirement.Contract);
        foreach (var requirement in content.CalibrationRequirements)
        {
            Token(requirement.LogicalCameraRole); Token(requirement.LogicalPurpose);
            Reference(requirement.CoefficientContract); Reference(requirement.AcceptancePolicy);
        }
        if (content.PartIdentityRequirement is { } part)
        { if (part.LogicalRole is { } role) Token(role); if (part.Format is { } format) Reference(format); }
    }

    private static void Reference(RecipeContractReference value) { Token(value.Id); Token(value.Version); }
    private static void Token(string value)
    {
        if (value.Length is < 1 or > 128 || value is "." or ".." || value.Any(c =>
                !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')))
            throw new InvalidOperationException("RecipeTransferLogicalIdentifierInvalid");
    }

    private static void RequireUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidOperationException("RecipeTransferDuplicateProperty");
                RequireUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RequireUniqueProperties(item);
    }
    private static bool Bounded(Exception exception) => exception is JsonException or ArgumentException or
        InvalidOperationException or FormatException or OverflowException or KeyNotFoundException or NullReferenceException;
    private static string Reason(Exception exception) => exception is InvalidOperationException &&
        exception.Message.StartsWith("RecipeTransfer", StringComparison.Ordinal) ? exception.Message : "RecipeTransferContentInvalid";
}
