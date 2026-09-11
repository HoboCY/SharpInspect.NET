using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Strict canonical codec for one complete Recipe Draft content snapshot. Revision
/// attribution is deliberately outside this codec; storage binds it to the revision hash.
/// </summary>
internal static partial class RecipeDraftStorageCodec
{
    internal const int MaximumPayloadBytes = 2 * 1024 * 1024;
    private const int FormatVersion = 1;
    private const int ProviderExtensionFormatVersion = 2;
    private const int CalibrationRequirementFormatVersion = 3;
    private const int MigrationLineageFormatVersion = 4;
    private const int PartIdentityFormatVersion = 5;
    private const int TransferClassificationFormatVersion = 6;
    private const int LifecycleLineageFormatVersion = 7;
    private const int CanonicalizationVersion = 1;
    private const int MaximumDepth = 32;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        Indented = false,
        SkipValidation = false
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaximumDepth
    };

    internal static bool TryEncodeContent(RecipeDraftContent content,
        out RecipeDraftDocument? document, out string reasonCode)
    {
        document = null;
        reasonCode = string.Empty;
        try
        {
            ValidateContent(content);
            using var writer = new BoundedBufferWriter(MaximumPayloadBytes);
            using (var json = new Utf8JsonWriter(writer, WriterOptions))
            {
                WriteContent(json, content);
                json.Flush();
            }

            var bytes = writer.ToArray();
            var payloadJson = StrictUtf8String(bytes);
            var payloadHash = Convert.ToHexString(SHA256.HashData(bytes));
            document = new RecipeDraftDocument(content, payloadJson, payloadHash);
            reasonCode = "RecipeDraftContentEncoded";
            return true;
        }
        catch (PayloadCapacityExceededException)
        {
            reasonCode = "RecipeDraftPayloadTooLarge";
            return false;
        }
        catch (Exception exception) when (IsBoundedValidationException(exception))
        {
            reasonCode = exception is InvalidOperationException invalid &&
                IsStableReason(invalid.Message) ? invalid.Message : "RecipeDraftContentInvalid";
            return false;
        }
    }

    internal static bool TryDecodeContent(string payloadJson,
        out RecipeDraftContent? content, out string reasonCode) =>
        TryDecodeContentCore(payloadJson, null, out content, out reasonCode);

    internal static bool TryDecodeContent(string payloadJson, string expectedPayloadHash,
        out RecipeDraftContent? content, out string reasonCode) =>
        TryDecodeContentCore(payloadJson, expectedPayloadHash, out content, out reasonCode);

    private static bool TryDecodeContentCore(string? payloadJson, string? expectedPayloadHash,
        out RecipeDraftContent? content, out string reasonCode)
    {
        content = null;
        reasonCode = string.Empty;
        try
        {
            var bytes = StrictUtf8(payloadJson, MaximumPayloadBytes);
            var payloadHash = Convert.ToHexString(SHA256.HashData(bytes));
            if (expectedPayloadHash is not null &&
                !string.Equals(payloadHash, expectedPayloadHash, StringComparison.OrdinalIgnoreCase))
                return Failure("RecipeDraftPayloadHashMismatch", out reasonCode);

            using var json = JsonDocument.Parse(bytes, DocumentOptions);
            var decoded = ReadContent(json.RootElement);
            ValidateContent(decoded);
            using var canonicalWriter = new BoundedBufferWriter(MaximumPayloadBytes);
            using (var writer = new Utf8JsonWriter(canonicalWriter, WriterOptions))
            {
                WriteContent(writer, decoded);
                writer.Flush();
            }

            var canonical = canonicalWriter.ToArray();
            if (!bytes.AsSpan().SequenceEqual(canonical))
                return Failure("RecipeDraftPayloadCanonicalMismatch", out reasonCode);

            content = decoded;
            reasonCode = "RecipeDraftContentDecoded";
            return true;
        }
        catch (PayloadCapacityExceededException)
        {
            return Failure("RecipeDraftPayloadTooLarge", out reasonCode);
        }
        catch (Exception exception) when (IsBoundedValidationException(exception))
        {
            return Failure(exception is InvalidOperationException invalid &&
                IsStableReason(invalid.Message) ? invalid.Message : "RecipeDraftPayloadInvalid",
                out reasonCode);
        }
    }

    private static void ValidateContent(RecipeDraftContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var schema = content.Algorithm.ConfigurationSchema;
        var configurationIssues = content.Configuration.Validate(schema);
        if (configurationIssues.Count != 0)
            throw Invalid("RecipeDraftConfigurationInvalid");

        var entries = content.Configuration.Values.ToDictionary(item => item.Key, StringComparer.Ordinal);
        if (content.ValueOrigins.Count != entries.Count)
            throw Invalid("RecipeDraftValueOriginsMismatch");
        var seenOrigins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var origin in content.ValueOrigins)
        {
            if (!seenOrigins.Add(origin.Key) || !entries.TryGetValue(origin.Key, out var entry))
                throw Invalid("RecipeDraftValueOriginsMismatch");
            if (!Enum.IsDefined(typeof(RecipeDraftValueOrigin), origin.Origin))
                throw Invalid("RecipeDraftValueOriginInvalid");
            if (origin.Origin == RecipeDraftValueOrigin.AuthoringDefault)
            {
                var field = schema.Fields.SingleOrDefault(item => item.Key == origin.Key);
                if (field?.AuthoringDefault is null || !field.AuthoringDefault.Equals(entry.Value))
                    throw Invalid("RecipeDraftDefaultOriginMismatch");
            }
        }

        if (!seenOrigins.SetEquals(entries.Keys))
            throw Invalid("RecipeDraftValueOriginsMismatch");

        var rebuilt = new RecipeDraftContent(content.LifecycleLineage, content.MigrationLineage, content.RecipeKey,
            content.DisplayName, content.Algorithm, content.Configuration, content.CameraRole, content.Camera,
            content.AlgorithmExecutionTimeout, content.AssetRequirements, content.PolicyRequirements,
            content.ValueOrigins, content.CameraProviderExtension, content.CalibrationRequirements,
            content.PartIdentityRequirement);
        if (!string.Equals(rebuilt.ContentHash, content.ContentHash, StringComparison.Ordinal))
            throw Invalid("RecipeDraftContentHashMismatch");
    }

    private static void WriteContent(Utf8JsonWriter writer, RecipeDraftContent content)
    {
        var hasTransferClassification = content.Algorithm.ConfigurationSchema.Fields.Any(field =>
            field.TransferClassification != AlgorithmConfigurationTransferClassification.LocalOnly);
        var hasLifecycleLineage = content.LifecycleLineage is not null;
        // A lifecycle-derived Draft states the complete extension set, so the newest
        // format never leans on an earlier format's omitted-property default.
        var hasExtendedShape = hasTransferClassification || hasLifecycleLineage ||
            content.CalibrationRequirements.Count != 0 || content.MigrationLineage is not null ||
            content.PartIdentityRequirement is not null;
        writer.WriteStartObject();
        writer.WriteNumber("FormatVersion", hasLifecycleLineage ? LifecycleLineageFormatVersion
            : hasTransferClassification ? TransferClassificationFormatVersion : content.PartIdentityRequirement is not null
            ? PartIdentityFormatVersion : content.MigrationLineage is not null
            ? MigrationLineageFormatVersion : content.CalibrationRequirements.Count != 0
            ? CalibrationRequirementFormatVersion : content.CameraProviderExtension is null
                ? FormatVersion : ProviderExtensionFormatVersion);
        writer.WriteNumber("CanonicalizationVersion", CanonicalizationVersion);
        writer.WriteString("RecipeKey", content.RecipeKey);
        writer.WriteString("DisplayName", content.DisplayName);
        writer.WritePropertyName("Algorithm");
        // Format 7 always restates per-field transfer classification, including the
        // all-LocalOnly schema, so the shape is unambiguous without payload sniffing.
        WriteBinding(writer, content.Algorithm, hasTransferClassification || hasLifecycleLineage);
        writer.WritePropertyName("Configuration");
        WriteConfiguration(writer, content.Configuration);
        writer.WriteString("CameraRole", content.CameraRole);
        writer.WritePropertyName("Camera");
        WriteCamera(writer, content.Camera);
        if (content.CameraProviderExtension is { } extension)
        {
            writer.WritePropertyName("CameraProviderExtension");
            writer.WriteStartObject();
            writer.WritePropertyName("Provider");
            writer.WriteStartObject();
            writer.WriteString("Id", extension.Provider.Id);
            writer.WriteString("Version", extension.Provider.Version);
            writer.WriteString("AdapterPackageId", extension.Provider.AdapterPackageId);
            writer.WriteString("AdapterVersion", extension.Provider.AdapterVersion);
            writer.WriteEndObject();
            writer.WriteString("ContractId", extension.ContractId);
            writer.WriteString("ContractVersion", extension.ContractVersion);
            writer.WriteString("ConfigurationContentHash", extension.ConfigurationContentHash);
            writer.WriteEndObject();
        }
        else if (hasExtendedShape)
            writer.WriteNull("CameraProviderExtension");
        if (hasExtendedShape)
        {
            writer.WritePropertyName("CalibrationRequirements");
            writer.WriteStartArray();
            foreach (var requirement in content.CalibrationRequirements
                         .OrderBy(item => item.LogicalCameraRole, StringComparer.Ordinal)
                         .ThenBy(item => item.Kind).ThenBy(item => item.LogicalPurpose, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("LogicalCameraRole", requirement.LogicalCameraRole);
                writer.WriteString("Kind", requirement.Kind.ToString());
                writer.WriteString("LogicalPurpose", requirement.LogicalPurpose);
                writer.WritePropertyName("CoefficientContract");
                WriteContractReference(writer, requirement.CoefficientContract);
                writer.WritePropertyName("AcceptancePolicy");
                WriteContractReference(writer, requirement.AcceptancePolicy);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        if (content.MigrationLineage is { } migration)
        {
            writer.WritePropertyName("MigrationLineage");
            WriteMigrationLineage(writer, migration);
        }
        else if (content.PartIdentityRequirement is not null || hasTransferClassification || hasLifecycleLineage)
            writer.WriteNull("MigrationLineage");
        if (content.PartIdentityRequirement is { } partIdentity)
        {
            writer.WritePropertyName("PartIdentityRequirement");
            writer.WriteStartObject();
            writer.WriteString("Mode", partIdentity.Mode.ToString());
            writer.WriteString("LogicalRole", partIdentity.LogicalRole);
            writer.WritePropertyName("Format");
            if (partIdentity.Format is { } format) WriteContractReference(writer, format);
            else writer.WriteNullValue();
            writer.WriteString("ContentHash", partIdentity.ContentHash);
            writer.WriteEndObject();
        }
        else if (hasTransferClassification || hasLifecycleLineage) writer.WriteNull("PartIdentityRequirement");
        if (content.LifecycleLineage is { } lifecycle)
        {
            writer.WritePropertyName("LifecycleLineage");
            WriteLifecycleLineage(writer, lifecycle);
        }
        writer.WriteNumber("AlgorithmExecutionTimeoutTicks", content.AlgorithmExecutionTimeout.Ticks);
        writer.WritePropertyName("AssetRequirements");
        writer.WriteStartArray();
        foreach (var asset in content.AssetRequirements.OrderBy(item => item.Kind).ThenBy(item => item.Role,
                     StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("Kind", Enum.GetName(typeof(RecipeAssetKind), asset.Kind));
            writer.WriteString("Role", asset.Role);
            writer.WritePropertyName("Contract");
            WriteContractReference(writer, asset.Contract);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("PolicyRequirements");
        writer.WriteStartArray();
        foreach (var policy in content.PolicyRequirements.OrderBy(item => item.Kind))
        {
            writer.WriteStartObject();
            writer.WriteString("Kind", Enum.GetName(typeof(RecipePolicyKind), policy.Kind));
            writer.WritePropertyName("Contract");
            WriteContractReference(writer, policy.Contract);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("ValueOrigins");
        writer.WriteStartArray();
        foreach (var origin in content.ValueOrigins.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("Key", origin.Key);
            writer.WriteString("Origin", Enum.GetName(typeof(RecipeDraftValueOrigin), origin.Origin));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteString("ContentHash", content.ContentHash);
        writer.WriteEndObject();
    }

    private static void WriteBinding(Utf8JsonWriter writer, RecipeAlgorithmBinding binding, bool classified = false)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("Algorithm");
        writer.WriteStartObject();
        writer.WriteString("Id", binding.Algorithm.Id);
        writer.WriteString("Version", binding.Algorithm.Version);
        writer.WriteEndObject();
        writer.WritePropertyName("ConfigurationSchema");
        WriteSchema(writer, binding.ConfigurationSchema, classified);
        writer.WritePropertyName("ResultSchema");
        WriteContractReference(writer, binding.ResultSchema);
        writer.WritePropertyName("OverlayContract");
        WriteContractReference(writer, binding.OverlayContract);
        writer.WriteEndObject();
    }

    private static void WriteSchema(Utf8JsonWriter writer, AlgorithmConfigurationSchema schema,
        bool classified = false)
    {
        var declaresClassification = classified || schema.Fields.Any(field =>
            field.TransferClassification != AlgorithmConfigurationTransferClassification.LocalOnly);
        writer.WriteStartObject();
        writer.WriteString("Id", schema.Id);
        writer.WriteString("Version", schema.Version);
        writer.WriteString("ContentHash", schema.ContentHash);
        writer.WritePropertyName("Fields");
        writer.WriteStartArray();
        foreach (var field in schema.Fields.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("Key", field.Key);
            writer.WriteString("Type", Enum.GetName(typeof(AlgorithmScalarType), field.Type));
            writer.WriteString("Unit", field.Unit);
            writer.WriteBoolean("Required", field.Required);
            writer.WritePropertyName("Constraints");
            WriteConstraints(writer, field.Constraints);
            writer.WritePropertyName("AuthoringDefault");
            WriteScalar(writer, field.AuthoringDefault);
            if (field.HelpText is null) writer.WriteNull("HelpText");
            else writer.WriteString("HelpText", field.HelpText);
            if (declaresClassification)
                writer.WriteString("TransferClassification", field.TransferClassification.ToString());
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteConfiguration(Utf8JsonWriter writer, AlgorithmConfigurationSnapshot configuration)
    {
        writer.WriteStartObject();
        writer.WriteString("SchemaId", configuration.SchemaId);
        writer.WriteString("SchemaVersion", configuration.SchemaVersion);
        writer.WriteString("SchemaContentHash", configuration.SchemaContentHash);
        writer.WriteString("CanonicalizationVersion", configuration.CanonicalizationVersion);
        writer.WriteString("ContentHash", configuration.ContentHash);
        writer.WritePropertyName("Values");
        writer.WriteStartArray();
        foreach (var entry in configuration.Values.OrderBy(item => item.Key, StringComparer.Ordinal)
                     .ThenBy(item => item.Unit, StringComparer.Ordinal)
                     .ThenBy(item => (int)item.Value.Type))
        {
            writer.WriteStartObject();
            writer.WriteString("Key", entry.Key);
            writer.WriteString("Unit", entry.Unit);
            writer.WritePropertyName("Value");
            WriteScalar(writer, entry.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteScalar(Utf8JsonWriter writer, AlgorithmScalarValue? value)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString("Type", Enum.GetName(typeof(AlgorithmScalarType), value.Type));
        switch (value.Type)
        {
            case AlgorithmScalarType.Boolean:
                writer.WriteBoolean("Boolean", value.AsBoolean());
                writer.WriteNull("Int64"); writer.WriteNull("Float64"); writer.WriteNull("Text");
                break;
            case AlgorithmScalarType.Int64:
                writer.WriteNull("Boolean"); writer.WriteNumber("Int64", value.AsInt64());
                writer.WriteNull("Float64"); writer.WriteNull("Text");
                break;
            case AlgorithmScalarType.Float64:
                writer.WriteNull("Boolean"); writer.WriteNull("Int64");
                writer.WriteNumber("Float64", value.AsFloat64()); writer.WriteNull("Text");
                break;
            case AlgorithmScalarType.String:
                writer.WriteNull("Boolean"); writer.WriteNull("Int64"); writer.WriteNull("Float64");
                writer.WriteString("Text", value.AsString());
                break;
            case AlgorithmScalarType.Enum:
                writer.WriteNull("Boolean"); writer.WriteNull("Int64"); writer.WriteNull("Float64");
                writer.WriteString("Text", value.AsEnum());
                break;
            default: throw Invalid("RecipeDraftScalarTypeInvalid");
        }
        writer.WriteEndObject();
    }

    private static void WriteConstraints(Utf8JsonWriter writer, AlgorithmScalarConstraints? constraints)
    {
        if (constraints is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        WriteNullable(writer, "MinInt64", constraints.MinInt64);
        WriteNullable(writer, "MaxInt64", constraints.MaxInt64);
        WriteNullable(writer, "MinFloat64", constraints.MinFloat64);
        WriteNullable(writer, "MaxFloat64", constraints.MaxFloat64);
        WriteNullable(writer, "MinLength", constraints.MinLength);
        WriteNullable(writer, "MaxLength", constraints.MaxLength);
        writer.WritePropertyName("AllowedValues");
        if (constraints.AllowedValues is null) writer.WriteNullValue();
        else
        {
            writer.WriteStartArray();
            foreach (var value in constraints.AllowedValues.OrderBy(value => value, StringComparer.Ordinal))
                writer.WriteStringValue(value);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private static void WriteCamera(Utf8JsonWriter writer, RequestedCameraConfiguration camera)
    {
        writer.WriteStartObject();
        writer.WriteString("ProductionAcquisitionMode", Enum.GetName(typeof(ProductionAcquisitionMode), camera.ProductionAcquisitionMode));
        writer.WriteNumber("ExposureTimeUs", camera.ExposureTimeUs);
        writer.WriteNumber("GainDb", camera.GainDb);
        writer.WritePropertyName("RegionOfInterest");
        writer.WriteStartObject();
        writer.WriteNumber("OffsetX", camera.RegionOfInterest.OffsetX);
        writer.WriteNumber("OffsetY", camera.RegionOfInterest.OffsetY);
        writer.WriteNumber("Width", camera.RegionOfInterest.Width);
        writer.WriteNumber("Height", camera.RegionOfInterest.Height);
        writer.WriteEndObject();
        writer.WriteString("PixelFormat", Enum.GetName(typeof(VisionPixelFormat), camera.PixelFormat));
        WriteNullable(writer, "ValidBits", camera.ValidBits);
        writer.WriteNumber("AcquisitionTimeoutMs", camera.AcquisitionTimeoutMs);
        writer.WriteNumber("TriggerDelayUs", camera.TriggerDelayUs);
        writer.WritePropertyName("WhiteBalanceRgb");
        if (camera.WhiteBalanceRgb is null) writer.WriteNullValue();
        else
        {
            writer.WriteStartObject();
            writer.WriteNumber("Red", camera.WhiteBalanceRgb.Red);
            writer.WriteNumber("Green", camera.WhiteBalanceRgb.Green);
            writer.WriteNumber("Blue", camera.WhiteBalanceRgb.Blue);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static void WriteContractReference(Utf8JsonWriter writer, RecipeContractReference reference)
    {
        writer.WriteStartObject();
        writer.WriteString("Id", reference.Id);
        writer.WriteString("Version", reference.Version);
        writer.WriteString("ContentHash", reference.ContentHash);
        writer.WriteEndObject();
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteNumber(name, value.Value);
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteNumber(name, value.Value);
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteNumber(name, value.Value);
    }

    private static RecipeDraftContent ReadContent(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw Invalid("RecipeDraftPayloadObjectInvalid");
        var format = Int32(root, "FormatVersion");
        EnsureObject(root, format is PartIdentityFormatVersion or TransferClassificationFormatVersion ? PartIdentityTopProperties :
            format == LifecycleLineageFormatVersion ? LifecycleTopProperties :
            format == MigrationLineageFormatVersion ? MigrationTopProperties :
            format == CalibrationRequirementFormatVersion ? CalibrationTopProperties :
            format == ProviderExtensionFormatVersion ? ExtendedTopProperties : TopProperties,
            "RecipeDraftPayload");
        var canonical = Int32(root, "CanonicalizationVersion");
        if (format is not (FormatVersion or ProviderExtensionFormatVersion or CalibrationRequirementFormatVersion or
            MigrationLineageFormatVersion or PartIdentityFormatVersion or TransferClassificationFormatVersion or
            LifecycleLineageFormatVersion) ||
            canonical != CanonicalizationVersion)
            throw Invalid("RecipeDraftPayloadVersionUnsupported");
        var binding = ReadBinding(RequiredObject(root, "Algorithm"),
            format is TransferClassificationFormatVersion or LifecycleLineageFormatVersion);
        var configuration = ReadConfiguration(RequiredObject(root, "Configuration"));
        if (!string.Equals(configuration.SchemaId, binding.ConfigurationSchema.Id, StringComparison.Ordinal) ||
            !string.Equals(configuration.SchemaVersion, binding.ConfigurationSchema.Version, StringComparison.Ordinal) ||
            !string.Equals(configuration.SchemaContentHash, binding.ConfigurationSchema.ContentHash,
                StringComparison.Ordinal))
            throw Invalid("RecipeDraftConfigurationSchemaBindingMismatch");
        var migration = format >= MigrationLineageFormatVersion &&
            RequiredValue(root, "MigrationLineage").ValueKind != JsonValueKind.Null
            ? ReadMigrationLineage(RequiredObject(root, "MigrationLineage")) : null;
        RecipeDraftLifecycleLineage? lifecycle = null;
        if (format >= LifecycleLineageFormatVersion)
        {
            // The field is mandatory in format 7: a lifecycle-derived Draft can never
            // decode as if it carried no lifecycle provenance, and every earlier format
            // rejects the unknown property instead of accepting a partial projection.
            if (RequiredValue(root, "LifecycleLineage").ValueKind == JsonValueKind.Null)
                throw Invalid("RecipeDraftLifecycleLineageRequired");
            lifecycle = ReadLifecycleLineage(RequiredObject(root, "LifecycleLineage"));
        }
        var content = new RecipeDraftContent(
            lifecycle, migration, RequiredString(root, "RecipeKey"), RequiredString(root, "DisplayName"),
            binding, configuration, RequiredString(root, "CameraRole"), ReadCamera(RequiredObject(root, "Camera")),
            TimeSpan.FromTicks(Int64(root, "AlgorithmExecutionTimeoutTicks")),
            ReadAssets(RequiredArray(root, "AssetRequirements")),
            ReadPolicies(RequiredArray(root, "PolicyRequirements")),
            ReadOrigins(RequiredArray(root, "ValueOrigins")),
            format >= ProviderExtensionFormatVersion &&
                RequiredValue(root, "CameraProviderExtension").ValueKind != JsonValueKind.Null
                ? ReadCameraExtension(RequiredObject(root, "CameraProviderExtension")) : null,
            format >= CalibrationRequirementFormatVersion
                ? ReadCalibrations(RequiredArray(root, "CalibrationRequirements"), format >= MigrationLineageFormatVersion) : null,
            format == PartIdentityFormatVersion || ((format is TransferClassificationFormatVersion or
                LifecycleLineageFormatVersion) &&
                RequiredValue(root, "PartIdentityRequirement").ValueKind != JsonValueKind.Null)
                ? ReadPartIdentity(RequiredObject(root, "PartIdentityRequirement")) : null);
        var suppliedHash = RequiredString(root, "ContentHash");
        if (!string.Equals(suppliedHash, content.ContentHash, StringComparison.Ordinal))
            throw Invalid("RecipeDraftContentHashMismatch");
        return content;
    }

    private static PartIdentityRequirement ReadPartIdentity(JsonElement element)
    {
        EnsureObject(element, PartIdentityProperties, "RecipeDraftPartIdentity");
        var requirement = new PartIdentityRequirement(
            ParseEnum<PartIdentityRequirementMode>(RequiredString(element, "Mode"), "RecipeDraftPartIdentityModeInvalid"),
            NullableString(element, "LogicalRole"),
            RequiredValue(element, "Format").ValueKind == JsonValueKind.Null
                ? null : ReadContractReference(RequiredObject(element, "Format")));
        if (requirement.ContentHash != RequiredString(element, "ContentHash"))
            throw Invalid("RecipeDraftPartIdentityHashMismatch");
        return requirement;
    }

    /// <summary>
    /// Rebuilds the exact preserved source transition. The lineage is provenance only:
    /// decoding proves the copied evidence, never a lifecycle authority.
    /// </summary>
    private static RecipeDraftLifecycleLineage ReadLifecycleLineage(JsonElement element)
    {
        EnsureObject(element, LifecycleLineageProperties, "RecipeDraftLifecycleLineage");
        var transitionElement = RequiredObject(element, "Transition");
        EnsureObject(transitionElement, LifecycleTransitionProperties, "RecipeDraftLifecycleTransition");
        var position = Int64(transitionElement, "Position");
        if (position < 1) throw Invalid("RecipeDraftLifecycleTransitionInvalid");
        var sourceElement = RequiredObject(element, "SourceDraft");
        EnsureObject(sourceElement, LifecycleSourceDraftProperties, "RecipeDraftLifecycleSourceDraft");
        var revision = Int64(sourceElement, "Revision");
        if (revision < 1) throw Invalid("RecipeDraftLifecycleSourceDraftInvalid");
        var kind = ParseEnum<RecipeLifecycleKind>(RequiredString(element, "Kind"),
            "RecipeDraftLifecycleKindInvalid");
        var sourceRecipe = RequiredValue(element, "SourceRecipe");
        var recipe = sourceRecipe.ValueKind == JsonValueKind.Null
            ? null : ReadLifecycleSourceRecipe(RequiredObject(element, "SourceRecipe"));
        var releaseId = NullableGuid(element, "SourceReleaseId", "RecipeDraftLifecycleSourceReleaseInvalid");
        var releaseHash = NullableString(element, "SourceReleaseRecordContentHash");
        if (kind == RecipeLifecycleKind.DraftAbandoned &&
            (recipe is not null || releaseId is not null || releaseHash is not null) ||
            kind == RecipeLifecycleKind.ReleasedRetired &&
            (recipe is null || releaseId is null || releaseHash is null))
            throw Invalid("RecipeDraftLifecycleLineageSourceInvalid");
        var lineage = new RecipeDraftLifecycleLineage(new RecipeLifecycleReference(position,
                ParseGuid(RequiredString(transitionElement, "TransitionId"), "RecipeDraftLifecycleTransitionInvalid"),
                RequiredString(transitionElement, "ContentHash")),
            kind, new RecipeDraftRevisionReference(
                ParseGuid(RequiredString(sourceElement, "DraftId"), "RecipeDraftLifecycleSourceDraftInvalid"),
                revision, RequiredString(sourceElement, "RevisionContentHash")),
            RequiredString(element, "SourceContentHash"), recipe, releaseId, releaseHash);
        if (!string.Equals(lineage.ContentHash, RequiredString(element, "ContentHash"), StringComparison.Ordinal))
            throw Invalid("RecipeDraftLifecycleLineageHashMismatch");
        return lineage;
    }

    private static RecipeReference ReadLifecycleSourceRecipe(JsonElement element)
    {
        EnsureObject(element, LifecycleSourceRecipeProperties, "RecipeDraftLifecycleSourceRecipe");
        var reference = new RecipeContractReference(RequiredString(element, "Id"),
            RequiredString(element, "Version"), RequiredString(element, "ContentHash"));
        return new RecipeReference(reference.Id, reference.Version, reference.ContentHash);
    }

    private static CameraProviderExtensionRequirement ReadCameraExtension(JsonElement element)
    {
        EnsureObject(element, CameraExtensionProperties, "RecipeDraftCameraExtension");
        var provider = RequiredObject(element, "Provider");
        EnsureObject(provider, CameraProviderProperties, "RecipeDraftCameraProvider");
        return new(new CameraProviderIdentity(RequiredString(provider, "Id"), RequiredString(provider, "Version"),
                RequiredString(provider, "AdapterPackageId"), RequiredString(provider, "AdapterVersion")),
            RequiredString(element, "ContractId"), RequiredString(element, "ContractVersion"),
            RequiredString(element, "ConfigurationContentHash"));
    }

    private static RecipeAlgorithmBinding ReadBinding(JsonElement element, bool classified = false)
    {
        EnsureObject(element, BindingProperties, "RecipeDraftAlgorithm");
        var algorithmElement = RequiredObject(element, "Algorithm");
        EnsureObject(algorithmElement, AlgorithmProperties, "RecipeDraftAlgorithmIdentity");
        var algorithm = new AlgorithmIdentity(RequiredString(algorithmElement, "Id"),
            RequiredString(algorithmElement, "Version"));
        var schema = ReadSchema(RequiredObject(element, "ConfigurationSchema"), classified);
        return new RecipeAlgorithmBinding(algorithm, schema,
            ReadContractReference(RequiredObject(element, "ResultSchema")),
            ReadContractReference(RequiredObject(element, "OverlayContract")));
    }

    private static AlgorithmConfigurationSchema ReadSchema(JsonElement element, bool classified = false)
    {
        EnsureObject(element, SchemaProperties, "RecipeDraftConfigurationSchema");
        var fields = new List<AlgorithmFieldDefinition>();
        var array = RequiredArray(element, "Fields");
        if (array.GetArrayLength() > 256) throw Invalid("RecipeDraftSchemaCapacityExceeded");
        foreach (var item in array.EnumerateArray())
        {
            EnsureObject(item, classified ? ClassifiedFieldProperties : FieldProperties, "RecipeDraftSchemaField");
            var type = ParseEnum<AlgorithmScalarType>(RequiredString(item, "Type"), "RecipeDraftScalarTypeInvalid");
            var classification = classified ? ParseEnum<AlgorithmConfigurationTransferClassification>(RequiredString(item, "TransferClassification"),
                "RecipeDraftTransferClassificationInvalid") : AlgorithmConfigurationTransferClassification.LocalOnly;
            fields.Add(new AlgorithmFieldDefinition(classification, RequiredString(item, "Key"), type,
                RequiredString(item, "Unit"), Bool(item, "Required"),
                ReadConstraints(RequiredValue(item, "Constraints"), type),
                ReadScalar(RequiredValue(item, "AuthoringDefault"), type, allowNull: true),
                NullableString(item, "HelpText")));
        }
        var schema = new AlgorithmConfigurationSchema(RequiredString(element, "Id"),
            RequiredString(element, "Version"), fields);
        if (!string.Equals(schema.ContentHash, RequiredString(element, "ContentHash"), StringComparison.Ordinal))
            throw Invalid("RecipeDraftSchemaContentHashMismatch");
        return schema;
    }

    private static AlgorithmConfigurationSnapshot ReadConfiguration(JsonElement element)
    {
        EnsureObject(element, ConfigurationProperties, "RecipeDraftConfiguration");
        var schemaId = RequiredString(element, "SchemaId");
        var schemaVersion = RequiredString(element, "SchemaVersion");
        var schemaHash = RequiredString(element, "SchemaContentHash");
        var canonical = RequiredString(element, "CanonicalizationVersion");
        var values = new List<AlgorithmConfigurationEntry>();
        var array = RequiredArray(element, "Values");
        if (array.GetArrayLength() > 256) throw Invalid("RecipeDraftConfigurationCapacityExceeded");
        foreach (var item in array.EnumerateArray())
        {
            EnsureObject(item, EntryProperties, "RecipeDraftConfigurationEntry");
            var value = ReadScalar(RequiredValue(item, "Value"), null, allowNull: false) ??
                throw Invalid("RecipeDraftScalarRequired");
            values.Add(new AlgorithmConfigurationEntry(RequiredString(item, "Key"),
                RequiredString(item, "Unit"), value));
        }
        var suppliedHash = RequiredString(element, "ContentHash");
        // The schema is reconstructed by ReadContent before this object is validated. The
        // public constructor remains useful here to preserve the exact serialized identity;
        // ReadContent then runs the schema-bound validation.
        return new AlgorithmConfigurationSnapshot(schemaId, schemaVersion, schemaHash, canonical,
            suppliedHash, values);
    }

    private static AlgorithmScalarConstraints? ReadConstraints(JsonElement element, AlgorithmScalarType type)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        EnsureObject(element, ConstraintProperties, "RecipeDraftConstraints");
        var allowedElement = RequiredValue(element, "AllowedValues");
        IEnumerable<string>? allowed = null;
        if (allowedElement.ValueKind != JsonValueKind.Null)
        {
            if (allowedElement.ValueKind != JsonValueKind.Array || allowedElement.GetArrayLength() > 256)
                throw Invalid("RecipeDraftAllowedValuesInvalid");
            allowed = allowedElement.EnumerateArray().Select(value =>
            {
                if (value.ValueKind != JsonValueKind.String) throw Invalid("RecipeDraftAllowedValuesInvalid");
                return value.GetString()!;
            }).ToArray();
        }
        return new AlgorithmScalarConstraints(NullableInt64(element, "MinInt64"),
            NullableInt64(element, "MaxInt64"), NullableDouble(element, "MinFloat64"),
            NullableDouble(element, "MaxFloat64"), NullableInt32(element, "MinLength"),
            NullableInt32(element, "MaxLength"), allowed);
    }

    private static AlgorithmScalarValue? ReadScalar(JsonElement element, AlgorithmScalarType? expected,
        bool allowNull)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            if (allowNull) return null;
            throw Invalid("RecipeDraftScalarRequired");
        }
        EnsureObject(element, ScalarProperties, "RecipeDraftScalar");
        var type = ParseEnum<AlgorithmScalarType>(RequiredString(element, "Type"), "RecipeDraftScalarTypeInvalid");
        if (expected.HasValue && type != expected.Value)
            throw Invalid("RecipeDraftScalarTypeMismatch");
        var boolean = RequiredValue(element, "Boolean");
        var integer = RequiredValue(element, "Int64");
        var floating = RequiredValue(element, "Float64");
        var text = RequiredValue(element, "Text");
        return type switch
        {
            AlgorithmScalarType.Boolean => boolean.ValueKind == JsonValueKind.True || boolean.ValueKind == JsonValueKind.False
                ? AlgorithmScalarValue.FromBoolean(boolean.GetBoolean())
                : throw Invalid("RecipeDraftScalarShapeInvalid"),
            AlgorithmScalarType.Int64 => integer.ValueKind == JsonValueKind.Number && integer.TryGetInt64(out var value)
                ? AlgorithmScalarValue.FromInt64(value)
                : throw Invalid("RecipeDraftScalarShapeInvalid"),
            AlgorithmScalarType.Float64 => floating.ValueKind == JsonValueKind.Number && floating.TryGetDouble(out var number)
                ? AlgorithmScalarValue.FromFloat64(number)
                : throw Invalid("RecipeDraftScalarShapeInvalid"),
            AlgorithmScalarType.String => text.ValueKind == JsonValueKind.String
                ? AlgorithmScalarValue.FromString(text.GetString()!)
                : throw Invalid("RecipeDraftScalarShapeInvalid"),
            AlgorithmScalarType.Enum => text.ValueKind == JsonValueKind.String
                ? AlgorithmScalarValue.FromEnum(text.GetString()!)
                : throw Invalid("RecipeDraftScalarShapeInvalid"),
            _ => throw Invalid("RecipeDraftScalarTypeInvalid")
        };
    }

    private static RequestedCameraConfiguration ReadCamera(JsonElement element)
    {
        EnsureObject(element, CameraProperties, "RecipeDraftCamera");
        var mode = ParseEnum<ProductionAcquisitionMode>(RequiredString(element, "ProductionAcquisitionMode"),
            "RecipeDraftCameraEnumInvalid");
        var roiElement = RequiredObject(element, "RegionOfInterest");
        EnsureObject(roiElement, RoiProperties, "RecipeDraftRoi");
        var validBits = NullableInt32(element, "ValidBits");
        var white = RequiredValue(element, "WhiteBalanceRgb");
        WhiteBalanceRgb? whiteBalance = null;
        if (white.ValueKind != JsonValueKind.Null)
        {
            EnsureObject(white, WhiteBalanceProperties, "RecipeDraftWhiteBalance");
            whiteBalance = new WhiteBalanceRgb(Number(white, "Red"), Number(white, "Green"), Number(white, "Blue"));
        }
        return new RequestedCameraConfiguration(mode, Number(element, "ExposureTimeUs"),
            Number(element, "GainDb"), new RegionOfInterest(Int32(roiElement, "OffsetX"),
                Int32(roiElement, "OffsetY"), Int32(roiElement, "Width"), Int32(roiElement, "Height")),
            ParseEnum<VisionPixelFormat>(RequiredString(element, "PixelFormat"), "RecipeDraftCameraEnumInvalid"),
            validBits, Int32(element, "AcquisitionTimeoutMs"), Number(element, "TriggerDelayUs"), whiteBalance);
    }

    private static IReadOnlyList<RecipeAssetRequirement> ReadAssets(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 32)
            throw Invalid("RecipeDraftAssetCapacityExceeded");
        return element.EnumerateArray().Select(item =>
        {
            EnsureObject(item, AssetProperties, "RecipeDraftAsset");
            return new RecipeAssetRequirement(ParseEnum<RecipeAssetKind>(RequiredString(item, "Kind"),
                "RecipeDraftAssetKindInvalid"), RequiredString(item, "Role"),
                ReadContractReference(RequiredObject(item, "Contract")));
        }).ToArray();
    }

    private static IReadOnlyList<CalibrationRequirement> ReadCalibrations(JsonElement element, bool allowEmpty = false)
    {
        if (element.ValueKind != JsonValueKind.Array ||
            (!allowEmpty && element.GetArrayLength() < 1) || element.GetArrayLength() > 8)
            throw Invalid("RecipeCalibrationRequirementCapacityExceeded");
        return element.EnumerateArray().Select(item =>
        {
            EnsureObject(item, CalibrationProperties, "RecipeCalibrationRequirement");
            return new CalibrationRequirement(RequiredString(item, "LogicalCameraRole"),
                ParseEnum<CalibrationKind>(RequiredString(item, "Kind"), "RecipeCalibrationKindInvalid"),
                RequiredString(item, "LogicalPurpose"),
                ReadContractReference(RequiredObject(item, "CoefficientContract")),
                ReadContractReference(RequiredObject(item, "AcceptancePolicy")));
        }).ToArray();
    }

    private static IReadOnlyList<RecipePolicyRequirement> ReadPolicies(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 16)
            throw Invalid("RecipeDraftPolicyCapacityExceeded");
        return element.EnumerateArray().Select(item =>
        {
            EnsureObject(item, PolicyProperties, "RecipeDraftPolicy");
            return new RecipePolicyRequirement(ParseEnum<RecipePolicyKind>(RequiredString(item, "Kind"),
                "RecipeDraftPolicyKindInvalid"), ReadContractReference(RequiredObject(item, "Contract")));
        }).ToArray();
    }

    private static IReadOnlyList<RecipeDraftFieldOrigin> ReadOrigins(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 256)
            throw Invalid("RecipeDraftOriginCapacityExceeded");
        return element.EnumerateArray().Select(item =>
        {
            EnsureObject(item, OriginProperties, "RecipeDraftOrigin");
            return new RecipeDraftFieldOrigin(RequiredString(item, "Key"),
                ParseEnum<RecipeDraftValueOrigin>(RequiredString(item, "Origin"),
                    "RecipeDraftValueOriginInvalid"));
        }).ToArray();
    }

    private static RecipeContractReference ReadContractReference(JsonElement element)
    {
        EnsureObject(element, ContractProperties, "RecipeDraftContractReference");
        return new RecipeContractReference(RequiredString(element, "Id"),
            RequiredString(element, "Version"), RequiredString(element, "ContentHash"));
    }

    private static void WriteMigrationLineage(Utf8JsonWriter writer, RecipeDraftMigrationLineage lineage)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("Plan");
        WriteMigrationPlan(writer, lineage.Plan);
        writer.WritePropertyName("Descriptor");
        WriteMigrationDescriptor(writer, lineage.Descriptor);
        writer.WriteString("InputConfigurationContentHash", lineage.InputConfigurationContentHash);
        writer.WriteString("InitialOutputConfigurationContentHash", lineage.InitialOutputConfigurationContentHash);
        writer.WritePropertyName("Warnings");
        writer.WriteStartArray();
        foreach (var warning in lineage.Warnings)
        {
            writer.WriteStartObject();
            writer.WriteString("Code", warning.Code);
            if (warning.FieldKey is null) writer.WriteNull("FieldKey");
            else writer.WriteString("FieldKey", warning.FieldKey);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteString("ContentHash", lineage.ContentHash);
        writer.WriteEndObject();
    }

    private static void WriteMigrationPlan(Utf8JsonWriter writer, RecipeDraftMigrationPlan plan)
    {
        writer.WriteStartObject();
        writer.WriteString("OperationId", plan.OperationId.ToString("D"));
        writer.WritePropertyName("Source");
        writer.WriteStartObject();
        writer.WriteString("DraftId", plan.Source.DraftId.ToString("D"));
        writer.WriteNumber("Revision", plan.Source.Revision);
        writer.WriteString("RevisionContentHash", plan.Source.RevisionContentHash);
        writer.WriteEndObject();
        writer.WriteString("TargetDraftId", plan.TargetDraftId.ToString("D"));
        writer.WritePropertyName("TargetAlgorithm");
        WriteAlgorithmIdentity(writer, plan.TargetAlgorithm);
        writer.WritePropertyName("TargetSchema");
        WriteContractReference(writer, plan.TargetSchema);
        writer.WritePropertyName("Migrator");
        WriteContractReference(writer, plan.Migrator);
        writer.WriteString("ChangeReason", plan.ChangeReason);
        writer.WriteString("ContentHash", plan.ContentHash);
        writer.WriteEndObject();
    }

    private static void WriteMigrationDescriptor(Utf8JsonWriter writer,
        AlgorithmConfigurationMigrationDescriptor descriptor)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("Migrator");
        WriteContractReference(writer, descriptor.Migrator);
        writer.WritePropertyName("SourceAlgorithm");
        WriteAlgorithmIdentity(writer, descriptor.SourceAlgorithm);
        writer.WritePropertyName("SourceSchema");
        WriteContractReference(writer, descriptor.SourceSchema);
        writer.WritePropertyName("TargetAlgorithm");
        WriteAlgorithmIdentity(writer, descriptor.TargetAlgorithm);
        writer.WritePropertyName("TargetSchema");
        WriteContractReference(writer, descriptor.TargetSchema);
        writer.WriteString("ContentHash", descriptor.ContentHash);
        writer.WriteEndObject();
    }

    private static void WriteAlgorithmIdentity(Utf8JsonWriter writer, AlgorithmIdentity identity)
    {
        writer.WriteStartObject();
        writer.WriteString("Id", identity.Id);
        writer.WriteString("Version", identity.Version);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes the complete preserved lifecycle source. Every field is restated on every
    /// derived Draft: nothing is inherited from the origin's mutable current state.
    /// </summary>
    private static void WriteLifecycleLineage(Utf8JsonWriter writer, RecipeDraftLifecycleLineage lineage)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("Transition");
        writer.WriteStartObject();
        writer.WriteNumber("Position", lineage.Transition.Position);
        writer.WriteString("TransitionId", lineage.Transition.TransitionId.ToString("D"));
        writer.WriteString("ContentHash", lineage.Transition.ContentHash);
        writer.WriteEndObject();
        writer.WriteString("Kind", lineage.Kind.ToString());
        writer.WritePropertyName("SourceDraft");
        writer.WriteStartObject();
        writer.WriteString("DraftId", lineage.SourceDraft.DraftId.ToString("D"));
        writer.WriteNumber("Revision", lineage.SourceDraft.Revision);
        writer.WriteString("RevisionContentHash", lineage.SourceDraft.RevisionContentHash);
        writer.WriteEndObject();
        writer.WriteString("SourceContentHash", lineage.SourceContentHash);
        writer.WritePropertyName("SourceRecipe");
        if (lineage.SourceRecipe is { } recipe)
        {
            writer.WriteStartObject();
            writer.WriteString("Id", recipe.Id);
            writer.WriteString("Version", recipe.Version);
            writer.WriteString("ContentHash", recipe.ContentHash);
            writer.WriteEndObject();
        }
        else writer.WriteNullValue();
        if (lineage.SourceReleaseId is { } releaseId) writer.WriteString("SourceReleaseId", releaseId.ToString("D"));
        else writer.WriteNull("SourceReleaseId");
        if (lineage.SourceReleaseRecordContentHash is { } releaseHash)
            writer.WriteString("SourceReleaseRecordContentHash", releaseHash);
        else writer.WriteNull("SourceReleaseRecordContentHash");
        writer.WriteString("ContentHash", lineage.ContentHash);
        writer.WriteEndObject();
    }

    private static RecipeDraftMigrationLineage ReadMigrationLineage(JsonElement element)
    {
        EnsureObject(element, MigrationLineageProperties, "RecipeDraftMigrationLineage");
        var plan = ReadMigrationPlan(RequiredObject(element, "Plan"));
        var descriptor = ReadMigrationDescriptor(RequiredObject(element, "Descriptor"));
        var lineage = new RecipeDraftMigrationLineage(plan, descriptor,
            RequiredString(element, "InputConfigurationContentHash"),
            RequiredString(element, "InitialOutputConfigurationContentHash"),
            ReadMigrationWarnings(RequiredArray(element, "Warnings")));
        if (!string.Equals(lineage.ContentHash, RequiredString(element, "ContentHash"), StringComparison.Ordinal))
            throw Invalid("RecipeDraftMigrationLineageHashMismatch");
        return lineage;
    }

    private static RecipeDraftMigrationPlan ReadMigrationPlan(JsonElement element)
    {
        EnsureObject(element, MigrationPlanProperties, "RecipeDraftMigrationPlan");
        var sourceElement = RequiredObject(element, "Source");
        EnsureObject(sourceElement, MigrationSourceProperties, "RecipeDraftMigrationSource");
        var source = new RecipeDraftRevisionReference(
            ParseGuid(RequiredString(sourceElement, "DraftId"), "RecipeDraftMigrationSourceInvalid"),
            Int64(sourceElement, "Revision"), RequiredString(sourceElement, "RevisionContentHash"));
        var plan = new RecipeDraftMigrationPlan(
            ParseGuid(RequiredString(element, "OperationId"), "RecipeDraftMigrationPlanInvalid"), source,
            ParseGuid(RequiredString(element, "TargetDraftId"), "RecipeDraftMigrationPlanInvalid"),
            ReadAlgorithmIdentity(RequiredObject(element, "TargetAlgorithm")),
            ReadContractReference(RequiredObject(element, "TargetSchema")),
            ReadContractReference(RequiredObject(element, "Migrator")),
            RequiredString(element, "ChangeReason"));
        if (!string.Equals(plan.ContentHash, RequiredString(element, "ContentHash"), StringComparison.Ordinal))
            throw Invalid("RecipeDraftMigrationPlanHashMismatch");
        return plan;
    }

    private static AlgorithmConfigurationMigrationDescriptor ReadMigrationDescriptor(JsonElement element)
    {
        EnsureObject(element, MigrationDescriptorProperties, "RecipeDraftMigrationDescriptor");
        var descriptor = new AlgorithmConfigurationMigrationDescriptor(
            ReadContractReference(RequiredObject(element, "Migrator")),
            ReadAlgorithmIdentity(RequiredObject(element, "SourceAlgorithm")),
            ReadContractReference(RequiredObject(element, "SourceSchema")),
            ReadAlgorithmIdentity(RequiredObject(element, "TargetAlgorithm")),
            ReadContractReference(RequiredObject(element, "TargetSchema")));
        if (!string.Equals(descriptor.ContentHash, RequiredString(element, "ContentHash"), StringComparison.Ordinal))
            throw Invalid("RecipeDraftMigrationDescriptorHashMismatch");
        return descriptor;
    }

    private static AlgorithmIdentity ReadAlgorithmIdentity(JsonElement element)
    {
        EnsureObject(element, AlgorithmProperties, "RecipeDraftAlgorithmIdentity");
        return new AlgorithmIdentity(RequiredString(element, "Id"), RequiredString(element, "Version"));
    }

    private static IReadOnlyList<AlgorithmValidationIssue> ReadMigrationWarnings(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 32)
            throw Invalid("RecipeDraftMigrationWarningCapacityExceeded");
        return element.EnumerateArray().Select(item =>
        {
            EnsureObject(item, WarningProperties, "RecipeDraftMigrationWarning");
            return new AlgorithmValidationIssue(RequiredString(item, "Code"), NullableString(item, "FieldKey"));
        }).ToArray();
    }

    private static Guid ParseGuid(string value, string reason)
    {
        return Guid.TryParseExact(value, "D", out var parsed) ? parsed : throw Invalid(reason);
    }

    private static void EnsureObject(JsonElement element, string[] expected, string reasonPrefix)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid(reasonPrefix + "ObjectInvalid");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw Invalid(reasonPrefix + "DuplicateProperty");
            if (!expected.Contains(property.Name, StringComparer.Ordinal))
                throw Invalid(reasonPrefix + "UnknownProperty");
        }
        if (names.Count != expected.Length || expected.Any(name => !names.Contains(name)))
            throw Invalid(reasonPrefix + "PropertyMissing");
    }

    private static JsonElement RequiredValue(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) throw Invalid("RecipeDraftPayloadPropertyMissing");
        return value;
    }

    private static JsonElement RequiredObject(JsonElement element, string name)
    {
        var value = RequiredValue(element, name);
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("RecipeDraftObjectInvalid");
        return value;
    }

    private static JsonElement RequiredArray(JsonElement element, string name)
    {
        var value = RequiredValue(element, name);
        if (value.ValueKind != JsonValueKind.Array) throw Invalid("RecipeDraftArrayInvalid");
        return value;
    }

    private static string RequiredString(JsonElement element, string name)
    {
        var value = RequiredValue(element, name);
        if (value.ValueKind != JsonValueKind.String) throw Invalid("RecipeDraftStringInvalid");
        return value.GetString()!;
    }

    private static string? NullableString(JsonElement element, string name)
    {
        var value = RequiredValue(element, name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid("RecipeDraftStringInvalid");
        return value.GetString();
    }

    private static Guid? NullableGuid(JsonElement element, string name, string reason)
    {
        var value = RequiredValue(element, name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid(reason);
        return ParseGuid(value.GetString()!, reason);
    }

    private static int Int32(JsonElement element, string name)
    {
        var value = RequiredValue(element, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw Invalid("RecipeDraftNumberInvalid");
        return result;
    }

    private static long Int64(JsonElement element, string name)
    {
        var value = RequiredValue(element, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
            throw Invalid("RecipeDraftNumberInvalid");
        return result;
    }

    private static double Number(JsonElement element, string name)
    {
        var value = RequiredValue(element, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result) || !double.IsFinite(result))
            throw Invalid("RecipeDraftNumberInvalid");
        return result;
    }

    private static bool Bool(JsonElement element, string name)
    {
        var value = RequiredValue(element, name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Invalid("RecipeDraftBooleanInvalid");
        return value.GetBoolean();
    }

    private static long? NullableInt64(JsonElement element, string name)
    {
        var value = RequiredValue(element, name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result)
            ? result : throw Invalid("RecipeDraftNumberInvalid");
    }

    private static int? NullableInt32(JsonElement element, string name)
    {
        var value = RequiredValue(element, name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result : throw Invalid("RecipeDraftNumberInvalid");
    }

    private static double? NullableDouble(JsonElement element, string name)
    {
        var value = RequiredValue(element, name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result) && double.IsFinite(result)
            ? result : throw Invalid("RecipeDraftNumberInvalid");
    }

    private static T ParseEnum<T>(string value, string reason) where T : struct, Enum
    {
        if (Enum.GetNames<T>().Any(name => string.Equals(name, value, StringComparison.Ordinal)))
            return (T)Enum.Parse(typeof(T), value, ignoreCase: false);
        throw Invalid(reason);
    }

    private static byte[] StrictUtf8(string? value, int maximumBytes)
    {
        if (value is null) throw Invalid("RecipeDraftPayloadRequired");
        try
        {
            var encoding = new UTF8Encoding(false, true);
            if (value.Length > maximumBytes || encoding.GetByteCount(value) > maximumBytes)
                throw Invalid("RecipeDraftPayloadTooLarge");
            return encoding.GetBytes(value);
        }
        catch (EncoderFallbackException exception) { throw Invalid("RecipeDraftPayloadUtf8Invalid", exception); }
    }

    private static string StrictUtf8String(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException exception) { throw Invalid("RecipeDraftPayloadUtf8Invalid", exception); }
    }

    private static bool Failure(string reason, out string reasonCode)
    {
        reasonCode = reason;
        return false;
    }

    private static InvalidOperationException Invalid(string reason, Exception? inner = null) =>
        new(reason, inner);

    private static bool IsBoundedValidationException(Exception exception) =>
        exception is ArgumentException or FormatException or OverflowException or JsonException or
        NotSupportedException or InvalidOperationException;

    private static bool IsStableReason(string reason) => reason.StartsWith("RecipeDraft", StringComparison.Ordinal);

    private static readonly string[] TopProperties =
    {
        "FormatVersion", "CanonicalizationVersion", "RecipeKey", "DisplayName", "Algorithm",
        "Configuration", "CameraRole", "Camera", "AlgorithmExecutionTimeoutTicks", "AssetRequirements",
        "PolicyRequirements", "ValueOrigins", "ContentHash"
    };
    private static readonly string[] BindingProperties = { "Algorithm", "ConfigurationSchema", "ResultSchema", "OverlayContract" };
    private static readonly string[] ExtendedTopProperties = TopProperties.Concat(new[] { "CameraProviderExtension" }).ToArray();
    private static readonly string[] CalibrationTopProperties = ExtendedTopProperties.Concat(new[] { "CalibrationRequirements" }).ToArray();
    private static readonly string[] MigrationTopProperties = CalibrationTopProperties.Concat(new[] { "MigrationLineage" }).ToArray();
    private static readonly string[] PartIdentityTopProperties = MigrationTopProperties.Concat(new[] { "PartIdentityRequirement" }).ToArray();
    private static readonly string[] LifecycleTopProperties = PartIdentityTopProperties.Concat(new[] { "LifecycleLineage" }).ToArray();
    private static readonly string[] PartIdentityProperties = { "Mode", "LogicalRole", "Format", "ContentHash" };
    private static readonly string[] LifecycleLineageProperties = { "Transition", "Kind", "SourceDraft",
        "SourceContentHash", "SourceRecipe", "SourceReleaseId", "SourceReleaseRecordContentHash", "ContentHash" };
    private static readonly string[] LifecycleTransitionProperties = { "Position", "TransitionId", "ContentHash" };
    private static readonly string[] LifecycleSourceDraftProperties = { "DraftId", "Revision", "RevisionContentHash" };
    private static readonly string[] LifecycleSourceRecipeProperties = { "Id", "Version", "ContentHash" };
    private static readonly string[] CalibrationProperties = { "LogicalCameraRole", "Kind", "LogicalPurpose", "CoefficientContract", "AcceptancePolicy" };
    private static readonly string[] CameraExtensionProperties = { "Provider", "ContractId", "ContractVersion", "ConfigurationContentHash" };
    private static readonly string[] CameraProviderProperties = { "Id", "Version", "AdapterPackageId", "AdapterVersion" };
    private static readonly string[] AlgorithmProperties = { "Id", "Version" };
    private static readonly string[] SchemaProperties = { "Id", "Version", "ContentHash", "Fields" };
    private static readonly string[] FieldProperties = { "Key", "Type", "Unit", "Required", "Constraints", "AuthoringDefault", "HelpText" };
    private static readonly string[] ClassifiedFieldProperties = FieldProperties.Concat(new[] { "TransferClassification" }).ToArray();
    private static readonly string[] ConfigurationProperties = { "SchemaId", "SchemaVersion", "SchemaContentHash", "CanonicalizationVersion", "ContentHash", "Values" };
    private static readonly string[] EntryProperties = { "Key", "Unit", "Value" };
    private static readonly string[] ScalarProperties = { "Type", "Boolean", "Int64", "Float64", "Text" };
    private static readonly string[] ConstraintProperties = { "MinInt64", "MaxInt64", "MinFloat64", "MaxFloat64", "MinLength", "MaxLength", "AllowedValues" };
    private static readonly string[] ContractProperties = { "Id", "Version", "ContentHash" };
    private static readonly string[] CameraProperties = { "ProductionAcquisitionMode", "ExposureTimeUs", "GainDb", "RegionOfInterest", "PixelFormat", "ValidBits", "AcquisitionTimeoutMs", "TriggerDelayUs", "WhiteBalanceRgb" };
    private static readonly string[] RoiProperties = { "OffsetX", "OffsetY", "Width", "Height" };
    private static readonly string[] WhiteBalanceProperties = { "Red", "Green", "Blue" };
    private static readonly string[] AssetProperties = { "Kind", "Role", "Contract" };
    private static readonly string[] PolicyProperties = { "Kind", "Contract" };
    private static readonly string[] OriginProperties = { "Key", "Origin" };
    private static readonly string[] MigrationLineageProperties = { "Plan", "Descriptor",
        "InputConfigurationContentHash", "InitialOutputConfigurationContentHash", "Warnings", "ContentHash" };
    private static readonly string[] MigrationPlanProperties = { "OperationId", "Source", "TargetDraftId",
        "TargetAlgorithm", "TargetSchema", "Migrator", "ChangeReason", "ContentHash" };
    private static readonly string[] MigrationSourceProperties = { "DraftId", "Revision", "RevisionContentHash" };
    private static readonly string[] MigrationDescriptorProperties = { "Migrator", "SourceAlgorithm", "SourceSchema",
        "TargetAlgorithm", "TargetSchema", "ContentHash" };
    private static readonly string[] WarningProperties = { "Code", "FieldKey" };

    private sealed class PayloadCapacityExceededException : IOException { }

    private sealed class BoundedBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private const int TokenAllowance = 512 * 1024;
        private readonly int _maximumBytes;
        private readonly int _allocationLimit;
        private byte[]? _buffer;
        private int _committed;
        private int _pending;

        internal BoundedBufferWriter(int maximumBytes)
        {
            _maximumBytes = maximumBytes;
            _allocationLimit = checked(maximumBytes + TokenAllowance);
            _buffer = ArrayPool<byte>.Shared.Rent(_allocationLimit);
        }

        public void Advance(int count)
        {
            if (count < 0 || count > _pending || _buffer is null)
                throw new PayloadCapacityExceededException();
            var committed = checked(_committed + count);
            if (committed > _maximumBytes) throw new PayloadCapacityExceededException();
            _committed = committed;
            _pending = 0;
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => GetWritable(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => GetWritable(sizeHint).Span;

        private Memory<byte> GetWritable(int sizeHint)
        {
            if (sizeHint < 0 || _pending != 0 || _buffer is null)
                throw new PayloadCapacityExceededException();
            var requested = sizeHint == 0 ? 1 : sizeHint;
            if (requested > TokenAllowance) throw new PayloadCapacityExceededException();
            var available = _allocationLimit - _committed;
            if (available < requested) throw new PayloadCapacityExceededException();
            _pending = available;
            return _buffer.AsMemory(_committed, available);
        }

        internal byte[] ToArray()
        {
            if (_pending != 0 || _buffer is null) throw new PayloadCapacityExceededException();
            return _buffer.AsSpan(0, _committed).ToArray();
        }

        public void Dispose()
        {
            var buffer = _buffer;
            _buffer = null;
            _committed = 0;
            _pending = 0;
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
