using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeDraftMigrationStorageTests
{
    [Fact]
    public void V128_S01_LineageUsesCumulativeV4PayloadAndRoundTripsExactly()
    {
        var content = CreateMigratedContent();

        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(content, out var document, out var reason), reason);
        Assert.NotNull(document);
        Assert.Contains("\"FormatVersion\":4", document!.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"CameraProviderExtension\":null", document.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"CalibrationRequirements\":[]", document.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"MigrationLineage\":", document.PayloadJson, StringComparison.Ordinal);

        Assert.True(RecipeDraftStorageCodec.TryDecodeContent(document.PayloadJson, document.PayloadHash,
            out var decoded, out reason), reason);
        Assert.NotNull(decoded);
        Assert.Equal(content.ContentHash, decoded!.ContentHash);
        Assert.Equal(content.MigrationLineage!.ContentHash, decoded.MigrationLineage!.ContentHash);
        Assert.Equal(content.MigrationLineage.Plan.ContentHash, decoded.MigrationLineage.Plan.ContentHash);
        Assert.Equal(content.MigrationLineage.Descriptor.ContentHash, decoded.MigrationLineage.Descriptor.ContentHash);

        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(decoded, out var reencoded, out reason), reason);
        Assert.Equal(document.PayloadJson, reencoded!.PayloadJson);
        Assert.Equal(document.PayloadHash, reencoded.PayloadHash);
    }

    [Fact]
    public void V128_S02_LineageHashTamperingIsRejectedAfterPayloadHashRecalculation()
    {
        var content = CreateMigratedContent();
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(content, out var document, out var reason), reason);

        var root = JsonNode.Parse(document!.PayloadJson)!.AsObject();
        var lineage = root["MigrationLineage"]!.AsObject();
        var supplied = lineage["ContentHash"]!.GetValue<string>();
        lineage["ContentHash"] = (supplied[0] == 'A' ? 'B' : 'A') + supplied[1..];
        var tampered = root.ToJsonString();
        var tamperedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tampered)));

        Assert.False(RecipeDraftStorageCodec.TryDecodeContent(tampered, tamperedHash,
            out var decoded, out var decodeReason));
        Assert.Null(decoded);
        Assert.Equal("RecipeDraftMigrationLineageHashMismatch", decodeReason);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void V128_S03_MigrationRetainsOptionalProviderAndCalibrationDependencies(bool extension, bool calibration)
    {
        var content = CreateMigratedContent(extension, calibration);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(content, out var document, out var reason), reason);
        Assert.True(RecipeDraftStorageCodec.TryDecodeContent(document!.PayloadJson, document.PayloadHash,
            out var decoded, out reason), reason);
        Assert.Equal(content.CameraProviderExtension, decoded!.CameraProviderExtension);
        Assert.Equal(content.CalibrationRequirements.Select(item => item.ContentHash),
            decoded.CalibrationRequirements.Select(item => item.ContentHash));
        Assert.Equal(content.MigrationLineage!.ContentHash, decoded.MigrationLineage!.ContentHash);
    }

    private static RecipeDraftContent CreateMigratedContent(bool extension = false, bool calibration = false)
    {
        var source = CalibrationRequirementTests.Content(calibration ?
            new[] { CalibrationRequirementTests.Requirement(CalibrationKind.Intrinsic, "Inspection") } :
            Array.Empty<CalibrationRequirement>(), extension);
        var sourceSchema = source.Algorithm.ConfigurationSchema;
        var sourceSchemaReference = new RecipeContractReference(sourceSchema.Id, sourceSchema.Version,
            sourceSchema.ContentHash);
        var targetSchema = new AlgorithmConfigurationSchema("Migration.Target.Config", "2",
            Array.Empty<AlgorithmFieldDefinition>());
        var targetSchemaReference = new RecipeContractReference(targetSchema.Id, targetSchema.Version,
            targetSchema.ContentHash);
        var targetAlgorithm = new AlgorithmIdentity("Migration.Target.Algorithm", "2");
        var migrator = new RecipeContractReference("Migration.Test.Migrator", "1", new string('C', 64));
        var plan = new RecipeDraftMigrationPlan(Guid.NewGuid(),
            new RecipeDraftRevisionReference(Guid.NewGuid(), 1, new string('B', 64)), Guid.NewGuid(),
            targetAlgorithm, targetSchemaReference, migrator, "migrate immutable draft");
        var descriptor = new AlgorithmConfigurationMigrationDescriptor(migrator,
            source.Algorithm.Algorithm, sourceSchemaReference, targetAlgorithm, targetSchemaReference);
        var lineage = new RecipeDraftMigrationLineage(plan, descriptor, source.Configuration.ContentHash,
            AlgorithmConfigurationSnapshot.Create(targetSchema, Array.Empty<AlgorithmConfigurationEntry>()).ContentHash,
            new[] { new AlgorithmValidationIssue("MigratedField", "Threshold") });
        var configuration = AlgorithmConfigurationSnapshot.Create(targetSchema,
            Array.Empty<AlgorithmConfigurationEntry>());
        var binding = new RecipeAlgorithmBinding(targetAlgorithm, targetSchema, source.Algorithm.ResultSchema,
            source.Algorithm.OverlayContract);
        return new RecipeDraftContent(lineage, source.RecipeKey, source.DisplayName, binding, configuration,
            source.CameraRole, source.Camera, source.AlgorithmExecutionTimeout, source.AssetRequirements,
            source.PolicyRequirements, Array.Empty<RecipeDraftFieldOrigin>(), source.CameraProviderExtension,
            source.CalibrationRequirements);
    }
}
