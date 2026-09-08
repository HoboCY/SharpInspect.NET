using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeDraftStorageCodecTests
{
    [Fact]
    public void V117_D20_CommonDraftRetainsPreExtensionCanonicalPayload()
    {
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(CreateContent(), out var document, out var reason), reason);
        // Measured from the pre-extension codec before this change; optional
        // provider metadata must not rewrite existing format 1 payloads.
        Assert.Equal("B08617404C7F03A62B65A9DA6714F2F84A823D0BC6B3C308FFADACAB666B842E", document!.PayloadHash);
        Assert.DoesNotContain("CameraProviderExtension", document.PayloadJson);
    }

    [Fact]
    public void V117_D21_ProviderBoundDraftRoundTripsAllExtensionIdentityFields()
    {
        var content = WithExtension();
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(content, out var document, out var reason), reason);
        Assert.StartsWith("{\"FormatVersion\":2,", document!.PayloadJson, StringComparison.Ordinal);
        Assert.True(RecipeDraftStorageCodec.TryDecodeContent(document.PayloadJson, document.PayloadHash,
            out var decoded, out var decodeReason), decodeReason);
        Assert.Equal(content.ContentHash, decoded!.ContentHash);
        Assert.Equal(content.CameraProviderExtension, decoded.CameraProviderExtension);
        Assert.False(decoded.IsCameraConfigurationPortable);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(decoded, out var reencoded, out reason), reason);
        Assert.Equal(document.PayloadJson, reencoded!.PayloadJson);
        Assert.Equal(document.PayloadHash, reencoded.PayloadHash);
    }

    [Theory]
    [InlineData("\"ContractVersion\":\"3\"", "\"ContractVersion\":\"4\"")]
    [InlineData("\"AdapterVersion\":\"2\"", "\"AdapterVersion\":\"9\"")]
    [InlineData("\"ContractId\":\"Camera.FixedGamma\",", "")]
    [InlineData("\"ContractVersion\":\"3\"", "\"ContractVersion\":\"3\",\"Unknown\":true")]
    [InlineData("\"FormatVersion\":2", "\"FormatVersion\":1")]
    public void V117_D22_ExtensionMutationMissingFieldsAndFormatDowngradeAreRejected(string before, string after)
    {
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(WithExtension(), out var document, out var reason), reason);
        var changed = document!.PayloadJson.Replace(before, after, StringComparison.Ordinal);
        Assert.NotEqual(document.PayloadJson, changed);
        Assert.False(RecipeDraftStorageCodec.TryDecodeContent(changed, Hash(changed), out var decoded, out _));
        Assert.Null(decoded);
    }

    private static RecipeDraftContent WithExtension()
    {
        var common = CreateContent();
        return new(common.RecipeKey, common.DisplayName, common.Algorithm, common.Configuration,
            common.CameraRole, common.Camera, common.AlgorithmExecutionTimeout, common.AssetRequirements,
            common.PolicyRequirements, common.ValueOrigins,
            new(new("Camera.Provider", "1", "Camera.Adapter", "2"), "Camera.FixedGamma", "3", new string('C', 64)));
    }

    [Fact]
    public void V115_C01_CompleteDraftRoundTripsWithHistoricalSchemaAndOrigins()
    {
        var content = CreateContent();

        var encoded = RecipeDraftStorageCodec.TryEncodeContent(content, out var document, out var reasonCode);

        Assert.True(encoded, reasonCode);
        Assert.NotNull(document);
        Assert.Equal(content.ContentHash, document!.Content.ContentHash);
        Assert.True(RecipeDraftStorageCodec.TryDecodeContent(document.PayloadJson, out var decoded, out var decodeReason),
            decodeReason);
        Assert.NotNull(decoded);

        Assert.Equal(content.ContentHash, decoded!.ContentHash);
        Assert.Equal(content.Algorithm.ConfigurationSchema.ContentHash,
            decoded.Algorithm.ConfigurationSchema.ContentHash);
        Assert.Equal("编辑器帮助：默认值只用于新建草稿。",
            decoded.Algorithm.ConfigurationSchema.Fields.Single().HelpText);
        Assert.Equal(RecipeDraftValueOrigin.AuthoringDefault,
            decoded.ValueOrigins.Single(origin => origin.Key == "Threshold").Origin);
        Assert.Equal(content.Configuration.Values.Single().Value.AsInt64(),
            decoded.Configuration.Values.Single().Value.AsInt64());
    }

    [Fact]
    public void V115_C02_HashTamperingAndNonCanonicalPayloadAreRejected()
    {
        var content = CreateContent();
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(content, out var document, out var reasonCode), reasonCode);
        Assert.NotNull(document);

        var changed = document!.PayloadJson.Replace("Demo recipe", "Changed recipe", StringComparison.Ordinal);
        Assert.False(RecipeDraftStorageCodec.TryDecodeContent(changed, document.PayloadHash, out _, out var hashReason));
        Assert.Equal("RecipeDraftPayloadHashMismatch", hashReason);

        var nonCanonical = document.PayloadJson.Replace(",", ", ", StringComparison.Ordinal);
        var changedBytes = Encoding.UTF8.GetBytes(nonCanonical);
        var changedHash = Convert.ToHexString(SHA256.HashData(changedBytes));
        Assert.False(RecipeDraftStorageCodec.TryDecodeContent(nonCanonical, changedHash, out _, out var canonicalReason));
        Assert.Equal("RecipeDraftPayloadCanonicalMismatch", canonicalReason);
    }

    [Fact]
    public void V115_C03_UnknownPropertyAndMissingRequiredFieldAreRejected()
    {
        var content = CreateContent();
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(content, out var document, out var reasonCode), reasonCode);
        Assert.NotNull(document);

        var unknown = document!.PayloadJson.Replace("{\"FormatVersion\":1,",
            "{\"Unknown\":1,\"FormatVersion\":1,", StringComparison.Ordinal);
        Assert.False(RecipeDraftStorageCodec.TryDecodeContent(unknown, Hash(unknown), out _, out var unknownReason));
        Assert.Equal("RecipeDraftPayloadUnknownProperty", unknownReason);

        var missing = document.PayloadJson.Replace(",\"DisplayName\":\"Demo recipe\"", string.Empty,
            StringComparison.Ordinal);
        Assert.False(RecipeDraftStorageCodec.TryDecodeContent(missing, Hash(missing), out _, out var missingReason));
        Assert.Equal("RecipeDraftPayloadPropertyMissing", missingReason);
    }

    [Fact]
    public void V115_C04_EnumStringFloatAndConstraintValuesRoundTripExactly()
    {
        var schema = new AlgorithmConfigurationSchema("Typed.Config", "1", new[]
        {
            new AlgorithmFieldDefinition("Mode", AlgorithmScalarType.Enum, "state", true,
                new AlgorithmScalarConstraints(allowedValues: new[] { "Fast", "Accurate" }),
                AlgorithmScalarValue.FromEnum("Accurate")),
            new AlgorithmFieldDefinition("Label", AlgorithmScalarType.String, "name", true,
                new AlgorithmScalarConstraints(minLength: 1, maxLength: 32),
                AlgorithmScalarValue.FromString("工位")),
            new AlgorithmFieldDefinition("Gain", AlgorithmScalarType.Float64, "ratio", true,
                new AlgorithmScalarConstraints(minFloat64: -10, maxFloat64: 10),
                AlgorithmScalarValue.FromFloat64(0.12345678901234567))
        });
        var content = CreateContent(schema, new[]
        {
            new AlgorithmConfigurationEntry("Mode", "state", AlgorithmScalarValue.FromEnum("Fast")),
            new AlgorithmConfigurationEntry("Label", "name", AlgorithmScalarValue.FromString("工位")),
            new AlgorithmConfigurationEntry("Gain", "ratio", AlgorithmScalarValue.FromFloat64(0.12345678901234567))
        });

        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(content, out var document, out var reason), reason);
        Assert.NotNull(document);
        Assert.True(RecipeDraftStorageCodec.TryDecodeContent(document!.PayloadJson, document.PayloadHash,
            out var decoded, out var decodeReason), decodeReason);
        Assert.NotNull(decoded);
        Assert.Equal("Fast", decoded!.Configuration.Values.Single(item => item.Key == "Mode").Value.AsEnum());
        Assert.Equal("工位", decoded.Configuration.Values.Single(item => item.Key == "Label").Value.AsString());
        Assert.Equal(0.12345678901234567,
            decoded.Configuration.Values.Single(item => item.Key == "Gain").Value.AsFloat64());
        Assert.Equal(content.ContentHash, decoded.ContentHash);
    }

    [Fact]
    public void V115_C05_DuplicateAndNumericEnumPropertiesAreRejected()
    {
        var content = CreateContent();
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(content, out var document, out var reason), reason);
        Assert.NotNull(document);

        var duplicate = document!.PayloadJson.Replace("{\"FormatVersion\":1,",
            "{\"FormatVersion\":1,\"FormatVersion\":1,", StringComparison.Ordinal);
        Assert.False(RecipeDraftStorageCodec.TryDecodeContent(duplicate, Hash(duplicate), out _, out var duplicateReason));
        Assert.Equal("RecipeDraftPayloadDuplicateProperty", duplicateReason);

        var numericEnum = document.PayloadJson.Replace("\"Type\":\"Int64\"", "\"Type\":1",
            StringComparison.Ordinal);
        Assert.False(RecipeDraftStorageCodec.TryDecodeContent(numericEnum, Hash(numericEnum), out _, out var enumReason));
        Assert.StartsWith("RecipeDraft", enumReason, StringComparison.Ordinal);
    }

    [Fact]
    public void V115_C06_LongUnicodePayloadNearLimitRoundTripsAndOversizeIsRejected()
    {
        var nearLimit = CreateLargeContent(120, 1300);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(nearLimit, out var document, out var nearReason), nearReason);
        Assert.NotNull(document);
        Assert.InRange(Encoding.UTF8.GetByteCount(document!.PayloadJson), 900_000, 2 * 1024 * 1024);
        Assert.True(RecipeDraftStorageCodec.TryDecodeContent(document.PayloadJson, document.PayloadHash,
            out var decoded, out var decodeReason), decodeReason);
        Assert.Equal(nearLimit.ContentHash, decoded!.ContentHash);

        var oversized = CreateLargeContent(256, 1365);
        Assert.False(RecipeDraftStorageCodec.TryEncodeContent(oversized, out _, out var oversizeReason));
        Assert.Equal("RecipeDraftPayloadTooLarge", oversizeReason);
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static RecipeDraftContent CreateContent()
    {
        var configurationSchema = new AlgorithmConfigurationSchema("Demo.Config", "1", new[]
        {
            new AlgorithmFieldDefinition("Threshold", AlgorithmScalarType.Int64, "items", true,
                new AlgorithmScalarConstraints(minInt64: 0, maxInt64: 100),
                AlgorithmScalarValue.FromInt64(5), "编辑器帮助：默认值只用于新建草稿。")
        });
        var configuration = AlgorithmConfigurationSnapshot.Create(configurationSchema, new[]
        {
            new AlgorithmConfigurationEntry("Threshold", "items", AlgorithmScalarValue.FromInt64(5))
        });
        var overlay = new OverlayContract("DemoOverlay", "1", 8, 64, 16);
        var result = new AlgorithmResultSchema("Demo.Result", "1", Array.Empty<AlgorithmFieldDefinition>(),
            new[] { "NoDefect" }, overlay);
        var binding = new RecipeAlgorithmBinding(new AlgorithmIdentity("Demo.Algorithm", "1"),
            configurationSchema,
            new RecipeContractReference(result.Id, result.Version, result.ContentHash),
            new RecipeContractReference(overlay.Id, overlay.Version, overlay.ContentHash));
        var camera = new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            1000, 1.5, new RegionOfInterest(0, 0, 16, 12), VisionPixelFormat.Mono8, null,
            1000, 0, null);
        var policy = new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
            new RecipeContractReference("ExecutionPolicy", "1", new string('A', 64)));
        return new RecipeDraftContent("DemoRecipe", "Demo recipe", binding, configuration, "TopCamera", camera,
            TimeSpan.FromMilliseconds(250), Array.Empty<RecipeAssetRequirement>(), new[] { policy },
            new[] { new RecipeDraftFieldOrigin("Threshold", RecipeDraftValueOrigin.AuthoringDefault) });
    }

    private static RecipeDraftContent CreateContent(AlgorithmConfigurationSchema schema,
        IEnumerable<AlgorithmConfigurationEntry> values)
    {
        var configuration = AlgorithmConfigurationSnapshot.Create(schema, values);
        var overlay = new OverlayContract("TypedOverlay", "1", 8, 64, 16);
        var result = new AlgorithmResultSchema("Typed.Result", "1", Array.Empty<AlgorithmFieldDefinition>(),
            new[] { "NoDefect" }, overlay);
        var binding = new RecipeAlgorithmBinding(new AlgorithmIdentity("Typed.Algorithm", "1"), schema,
            new RecipeContractReference(result.Id, result.Version, result.ContentHash),
            new RecipeContractReference(overlay.Id, overlay.Version, overlay.ContentHash));
        var camera = new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            1000, 1.5, new RegionOfInterest(0, 0, 16, 12), VisionPixelFormat.Mono8, null, 1000, 0, null);
        var policy = new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
            new RecipeContractReference("ExecutionPolicy", "1", new string('A', 64)));
        return new RecipeDraftContent("TypedRecipe", "Typed recipe", binding, configuration, "TopCamera", camera,
            TimeSpan.FromMilliseconds(250), Array.Empty<RecipeAssetRequirement>(), new[] { policy });
    }

    private static RecipeDraftContent CreateLargeContent(int fieldCount, int helpCharacters)
    {
        var help = new string('中', helpCharacters);
        var fields = Enumerable.Range(0, fieldCount)
            .Select(index => new AlgorithmFieldDefinition("Field" + index.ToString("D3"),
                AlgorithmScalarType.Int64, "items", true, authoringDefault: null, helpText: help))
            .ToArray();
        var schema = new AlgorithmConfigurationSchema("Large.Config", "1", fields);
        var values = fields.Select((field, index) => new AlgorithmConfigurationEntry(field.Key, field.Unit,
            AlgorithmScalarValue.FromInt64(index))).ToArray();
        return CreateContent(schema, values);
    }
}
