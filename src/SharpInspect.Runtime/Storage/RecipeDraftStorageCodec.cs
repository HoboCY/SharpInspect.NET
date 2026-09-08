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
internal static class RecipeDraftStorageCodec
{
    internal const int MaximumPayloadBytes = 2 * 1024 * 1024;
    private const int FormatVersion = 1;
    private const int ProviderExtensionFormatVersion = 2;
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

        var rebuilt = new RecipeDraftContent(content.RecipeKey, content.DisplayName, content.Algorithm,
            content.Configuration, content.CameraRole, content.Camera, content.AlgorithmExecutionTimeout,
            content.AssetRequirements, content.PolicyRequirements, content.ValueOrigins,
            content.CameraProviderExtension);
        if (!string.Equals(rebuilt.ContentHash, content.ContentHash, StringComparison.Ordinal))
            throw Invalid("RecipeDraftContentHashMismatch");
    }

    private static void WriteContent(Utf8JsonWriter writer, RecipeDraftContent content)
    {
        writer.WriteStartObject();
        writer.WriteNumber("FormatVersion", content.CameraProviderExtension is null
            ? FormatVersion : ProviderExtensionFormatVersion);
        writer.WriteNumber("CanonicalizationVersion", CanonicalizationVersion);
        writer.WriteString("RecipeKey", content.RecipeKey);
        writer.WriteString("DisplayName", content.DisplayName);
        writer.WritePropertyName("Algorithm");
        WriteBinding(writer, content.Algorithm);
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

    private static void WriteBinding(Utf8JsonWriter writer, RecipeAlgorithmBinding binding)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("Algorithm");
        writer.WriteStartObject();
        writer.WriteString("Id", binding.Algorithm.Id);
        writer.WriteString("Version", binding.Algorithm.Version);
        writer.WriteEndObject();
        writer.WritePropertyName("ConfigurationSchema");
        WriteSchema(writer, binding.ConfigurationSchema);
        writer.WritePropertyName("ResultSchema");
        WriteContractReference(writer, binding.ResultSchema);
        writer.WritePropertyName("OverlayContract");
        WriteContractReference(writer, binding.OverlayContract);
        writer.WriteEndObject();
    }

    private static void WriteSchema(Utf8JsonWriter writer, AlgorithmConfigurationSchema schema)
    {
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
        EnsureObject(root, format == ProviderExtensionFormatVersion ? ExtendedTopProperties : TopProperties,
            "RecipeDraftPayload");
        var canonical = Int32(root, "CanonicalizationVersion");
        if (format is not (FormatVersion or ProviderExtensionFormatVersion) || canonical != CanonicalizationVersion)
            throw Invalid("RecipeDraftPayloadVersionUnsupported");
        var binding = ReadBinding(RequiredObject(root, "Algorithm"));
        var configuration = ReadConfiguration(RequiredObject(root, "Configuration"));
        if (!string.Equals(configuration.SchemaId, binding.ConfigurationSchema.Id, StringComparison.Ordinal) ||
            !string.Equals(configuration.SchemaVersion, binding.ConfigurationSchema.Version, StringComparison.Ordinal) ||
            !string.Equals(configuration.SchemaContentHash, binding.ConfigurationSchema.ContentHash,
                StringComparison.Ordinal))
            throw Invalid("RecipeDraftConfigurationSchemaBindingMismatch");
        var content = new RecipeDraftContent(
            RequiredString(root, "RecipeKey"), RequiredString(root, "DisplayName"), binding, configuration,
            RequiredString(root, "CameraRole"), ReadCamera(RequiredObject(root, "Camera")),
            TimeSpan.FromTicks(Int64(root, "AlgorithmExecutionTimeoutTicks")),
            ReadAssets(RequiredArray(root, "AssetRequirements")),
            ReadPolicies(RequiredArray(root, "PolicyRequirements")),
            ReadOrigins(RequiredArray(root, "ValueOrigins")),
            format == ProviderExtensionFormatVersion
                ? ReadCameraExtension(RequiredObject(root, "CameraProviderExtension")) : null);
        var suppliedHash = RequiredString(root, "ContentHash");
        if (!string.Equals(suppliedHash, content.ContentHash, StringComparison.Ordinal))
            throw Invalid("RecipeDraftContentHashMismatch");
        return content;
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

    private static RecipeAlgorithmBinding ReadBinding(JsonElement element)
    {
        EnsureObject(element, BindingProperties, "RecipeDraftAlgorithm");
        var algorithmElement = RequiredObject(element, "Algorithm");
        EnsureObject(algorithmElement, AlgorithmProperties, "RecipeDraftAlgorithmIdentity");
        var algorithm = new AlgorithmIdentity(RequiredString(algorithmElement, "Id"),
            RequiredString(algorithmElement, "Version"));
        var schema = ReadSchema(RequiredObject(element, "ConfigurationSchema"));
        return new RecipeAlgorithmBinding(algorithm, schema,
            ReadContractReference(RequiredObject(element, "ResultSchema")),
            ReadContractReference(RequiredObject(element, "OverlayContract")));
    }

    private static AlgorithmConfigurationSchema ReadSchema(JsonElement element)
    {
        EnsureObject(element, SchemaProperties, "RecipeDraftConfigurationSchema");
        var fields = new List<AlgorithmFieldDefinition>();
        var array = RequiredArray(element, "Fields");
        if (array.GetArrayLength() > 256) throw Invalid("RecipeDraftSchemaCapacityExceeded");
        foreach (var item in array.EnumerateArray())
        {
            EnsureObject(item, FieldProperties, "RecipeDraftSchemaField");
            var type = ParseEnum<AlgorithmScalarType>(RequiredString(item, "Type"), "RecipeDraftScalarTypeInvalid");
            fields.Add(new AlgorithmFieldDefinition(RequiredString(item, "Key"), type,
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
    private static readonly string[] CameraExtensionProperties = { "Provider", "ContractId", "ContractVersion", "ConfigurationContentHash" };
    private static readonly string[] CameraProviderProperties = { "Id", "Version", "AdapterPackageId", "AdapterVersion" };
    private static readonly string[] AlgorithmProperties = { "Id", "Version" };
    private static readonly string[] SchemaProperties = { "Id", "Version", "ContentHash", "Fields" };
    private static readonly string[] FieldProperties = { "Key", "Type", "Unit", "Required", "Constraints", "AuthoringDefault", "HelpText" };
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
