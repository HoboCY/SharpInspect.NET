using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeTransferContentCodecTests
{
    [Fact]
    public void V138_C01_PortableProjectionOmitsLocalMetadataAndUsesNewLocalIdentity()
    {
        var (descriptor, content, policy) = Fixture();
        Assert.True(RecipeTransferContentCodec.TryEncode(content, policy, out var bytes, out _, out var reason), reason);
        var text = Encoding.UTF8.GetString(bytes!);
        Assert.DoesNotContain("DO_NOT_EXPORT_HELP", text);
        Assert.DoesNotContain("DO_NOT_EXPORT_DEFAULT", text);
        Assert.DoesNotContain(content.DisplayName, text);
        Assert.DoesNotContain(content.RecipeKey, text);
        Assert.DoesNotContain("\"Fields\"", text);
        var target = Guid.NewGuid();
        Assert.True(RecipeTransferContentCodec.TryDecode(bytes!, new[] { descriptor }, policy, target,
            out var draft, out reason), reason);
        Assert.Equal("import." + target.ToString("N"), draft!.Content.RecipeKey);
        Assert.Equal(content.Configuration.ContentHash, draft.Content.Configuration.ContentHash);
        Assert.Null(draft.Content.MigrationLineage);
        Assert.Null(draft.Content.CameraProviderExtension);
        Assert.All(draft.Content.ValueOrigins, item => Assert.Equal(RecipeDraftValueOrigin.Explicit, item.Origin));
        Assert.Equal(PartIdentityRequirement.None, draft.Content.PartIdentityRequirement);
    }

    [Fact]
    public void V138_C02_LocalPortableDeclarationIsRequiredInBothDirections()
    {
        var (descriptor, content, policy) = Fixture();
        var deny = new RecipeTransferPortablePolicy("portable", "denied", Array.Empty<RecipeTransferPortableContract>());
        Assert.False(RecipeTransferContentCodec.TryEncode(content, deny, out _, out _, out var reason));
        Assert.Equal("RecipeTransferPortablePolicyDenied", reason);
        Assert.True(RecipeTransferContentCodec.TryEncode(content, policy, out var bytes, out _, out reason), reason);
        Assert.False(RecipeTransferContentCodec.TryDecode(bytes!, new[] { descriptor }, deny, Guid.NewGuid(), out _, out reason));
        Assert.Equal("RecipeTransferPortablePolicyDenied", reason);
    }

    [Theory]
    [InlineData("Fields")]
    [InlineData("Published")]
    [InlineData("Active")]
    [InlineData("TrustStore")]
    public void V138_C03_PackageCannotInjectSchemaOrLocalAuthority(string field)
    {
        var (descriptor, content, policy) = Fixture();
        Assert.True(RecipeTransferContentCodec.TryEncode(content, policy, out var bytes, out _, out var reason), reason);
        var root = JsonNode.Parse(bytes!)!.AsObject();
        if (field == "Fields") root["Algorithm"]!["ConfigurationSchema"]![field] = new JsonArray();
        else root[field] = true;
        Assert.False(RecipeTransferContentCodec.TryDecode(Encoding.UTF8.GetBytes(root.ToJsonString()),
            new[] { descriptor }, policy, Guid.NewGuid(), out var draft, out _));
        Assert.Null(draft);
    }

    [Fact]
    public void V138_C04_UnknownExactSchemaIsRejectedWithoutMigrationOrDefaults()
    {
        var (descriptor, content, policy) = Fixture();
        Assert.True(RecipeTransferContentCodec.TryEncode(content, policy, out var bytes, out _, out var reason), reason);
        var root = JsonNode.Parse(bytes!)!.AsObject();
        root["Algorithm"]!["ConfigurationSchema"]!["ContentHash"] = new string('A', 64);
        Assert.False(RecipeTransferContentCodec.TryDecode(Encoding.UTF8.GetBytes(root.ToJsonString()),
            new[] { descriptor }, policy, Guid.NewGuid(), out var draft, out reason));
        Assert.Equal("RecipeTransferSchemaIncompatible", reason);
        Assert.Null(draft);
    }

    [Fact]
    public void V138_C05_DuplicateJsonPropertiesAndTrailingObjectsFailClosed()
    {
        var (descriptor, content, policy) = Fixture();
        Assert.True(RecipeTransferContentCodec.TryEncode(content, policy, out var bytes, out _, out var reason), reason);
        var text = Encoding.UTF8.GetString(bytes!);
        foreach (var malformed in new[] { "{\"TransferFormatVersion\":1," + text[1..], text + "{}" })
        {
            Assert.False(RecipeTransferContentCodec.TryDecode(Encoding.UTF8.GetBytes(malformed),
                new[] { descriptor }, policy, Guid.NewGuid(), out var draft, out _));
            Assert.Null(draft);
        }
    }

    [Theory]
    [InlineData(AlgorithmConfigurationTransferClassification.LocalOnly)]
    [InlineData(AlgorithmConfigurationTransferClassification.Sensitive)]
    public void V138_C06_LocalAllowlistCannotOverrideSchemaCredentialClassification(AlgorithmConfigurationTransferClassification classification)
    {
        var (_, content, policy) = Fixture(classification, "ApiToken", "secret-api-token");
        Assert.Contains("ApiToken", policy.Contracts.Single().PortableFieldKeys);
        Assert.False(RecipeTransferContentCodec.TryEncode(content, policy, out var bytes, out _, out var reason));
        Assert.Equal("RecipeTransferFieldNotPortable", reason);
        Assert.Null(bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V138_C07_ClassifiedDraftRetainsClassificationAcrossStorageRoundTrip(bool declaresPartIdentity)
    {
        var (_, source, _) = Fixture();
        var content = new RecipeDraftContent(source.RecipeKey, source.DisplayName, source.Algorithm,
            source.Configuration, source.CameraRole, source.Camera, source.AlgorithmExecutionTimeout,
            source.AssetRequirements, source.PolicyRequirements,
            partIdentityRequirement: declaresPartIdentity ? PartIdentityRequirement.None : null);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(content, out var encoded, out var reason), reason);
        Assert.StartsWith("{\"FormatVersion\":6,", encoded!.PayloadJson, StringComparison.Ordinal);
        Assert.True(RecipeDraftStorageCodec.TryDecodeContent(encoded.PayloadJson, encoded.PayloadHash,
            out var decoded, out reason), reason);
        Assert.Equal(content.ContentHash, decoded!.ContentHash);
        Assert.Equal(content.PartIdentityRequirement, decoded.PartIdentityRequirement);
        Assert.Equal(AlgorithmConfigurationTransferClassification.PortableRecipeData,
            decoded.Algorithm.ConfigurationSchema.Fields.Single().TransferClassification);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(decoded, out var roundTrip, out reason), reason);
        Assert.Equal(encoded.PayloadJson, roundTrip!.PayloadJson);
        Assert.Equal(encoded.PayloadHash, roundTrip.PayloadHash);

        var changed = JsonNode.Parse(encoded.PayloadJson)!.AsObject();
        changed["Algorithm"]!["ConfigurationSchema"]!["Fields"]![0]!["TransferClassification"] = "LocalOnly";
        Assert.False(RecipeDraftStorageCodec.TryDecodeContent(changed.ToJsonString(), out _, out _));
        changed["Algorithm"]!["ConfigurationSchema"]!["Fields"]![0]!.AsObject().Remove("TransferClassification");
        Assert.False(RecipeDraftStorageCodec.TryDecodeContent(changed.ToJsonString(), out _, out _));
    }

    [Fact]
    public void V138_C08_ResultMeasurementsCannotAcquireConfigurationTransferAuthority()
    {
        Assert.Throws<ArgumentException>(() => new AlgorithmResultSchema("Result", "1", new[]
        {
            new AlgorithmFieldDefinition(AlgorithmConfigurationTransferClassification.PortableRecipeData,
                "Measurement", AlgorithmScalarType.Int64, "count", false)
        }, Array.Empty<string>(), new OverlayContract("Overlay", "1", 0, 0, 0)));
    }

    [Fact]
    public void V138_K01_RecipePrivateKeyIsMachineProtectedAndBoundToStationAndFingerprint()
    {
        var now = DateTimeOffset.UtcNow;
        var command = new CreateRecipeSigningKeyCommand(Guid.NewGuid(), new(CommandSource.PhysicalConsole),
            "recipe-key", "recipe-data", now.AddMinutes(-1), now.AddDays(1), "generate test key");
        var material = RecipeSigningKeyProtection.Generate(command, "Station.A");
        Assert.Equal(124, material.Signer.PublicKeyBase64.Length);
        var data = Encoding.UTF8.GetBytes("recipe-domain-test");
        var signature = RecipeSigningKeyProtection.Sign(material.ProtectedPrivateKeyBase64, material.Signer, "Station.A", data);
        using var publicKey = ECDsa.Create();
        publicKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(material.Signer.PublicKeyBase64), out _);
        Assert.True(publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.Throws<CryptographicException>(() => RecipeSigningKeyProtection.Sign(material.ProtectedPrivateKeyBase64,
            material.Signer, "Station.B", data));
        Assert.Equal("[protected recipe signing key]", material.ToString());
    }

    internal static (AlgorithmDescriptor Descriptor, RecipeDraftContent Content, RecipeTransferPortablePolicy Policy) Fixture(
        AlgorithmConfigurationTransferClassification classification = AlgorithmConfigurationTransferClassification.PortableRecipeData,
        string fieldKey = "Label", string fieldValue = "logical-product-label")
    {
        var schema = new AlgorithmConfigurationSchema("Transfer.Config", "1", new[]
        {
            new AlgorithmFieldDefinition(classification, fieldKey, AlgorithmScalarType.String, "text", true,
                authoringDefault: AlgorithmScalarValue.FromString("DO_NOT_EXPORT_DEFAULT"), helpText: "DO_NOT_EXPORT_HELP")
        });
        var result = new AlgorithmResultSchema("Transfer.Result", "1", Array.Empty<AlgorithmFieldDefinition>(),
            Array.Empty<string>(), new OverlayContract("Transfer.Overlay", "1", 0, 0, 0));
        var descriptor = new AlgorithmDescriptor(new("Transfer.Algorithm", "1"), schema, result);
        var configuration = AlgorithmConfigurationSnapshot.Create(schema,
            new[] { new AlgorithmConfigurationEntry(fieldKey, "text", AlgorithmScalarValue.FromString(fieldValue)) });
        var content = new RecipeDraftContent("LOCAL_RECIPE_ID", "LOCAL_DISPLAY_NAME", RecipeAlgorithmBinding.FromDescriptor(descriptor),
            configuration, "MainCamera", new(ProductionAcquisitionMode.SoftwareTrigger, 100, 0,
                new RegionOfInterest(0, 0, 16, 12), VisionPixelFormat.Mono8, null, 1000, 0, null),
            TimeSpan.FromMilliseconds(100), null, null, partIdentityRequirement: PartIdentityRequirement.None);
        var policy = new RecipeTransferPortablePolicy("Portable", "1", new[] { new RecipeTransferPortableContract(descriptor.Identity,
            new(schema.Id, schema.Version, schema.ContentHash), new[] { fieldKey }) });
        return (descriptor, content, policy);
    }
}
