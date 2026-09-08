using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeDraftContractTests
{
    [Fact]
    public void V115_A01_OmittedHelpPreservesSchemaHashFromTicket14Package()
    {
        // Observed from the isolated V114 NuGet consumer's Abstractions assembly.
        var schema = Schema(null);
        Assert.Equal("8382EE53B4BFE5DC08A0C94DD5C75C34476F8E085CAD0E8013A0E781746FBC96", schema.ContentHash);
        Assert.Equal(schema.ContentHash, Schema(string.Empty).ContentHash);
    }

    [Fact]
    public void V115_A02_ChangedAuthoringHelpRequiresExactNewSchemaBinding()
    {
        var original = Schema("计数阈值，不会修改相机。");
        var edited = Schema("计数阈值，单位为件。");
        var configuration = AlgorithmConfigurationSnapshot.Create(original,
            new[] { new AlgorithmConfigurationEntry("count", "items", AlgorithmScalarValue.FromInt64(5)) });
        Assert.NotEqual(original.ContentHash, edited.ContentHash);
        Assert.Contains(configuration.Validate(edited), issue => issue.Code == "AlgorithmSchemaContentHashMismatch");
        Assert.Equal("计数阈值，不会修改相机。", original.Fields.Single().HelpText);
        Assert.Throws<ArgumentException>(() => Schema(new string('中', 1366)));
        Assert.Throws<ArgumentException>(() => Schema("invalid\uD800"));
    }

    [Fact]
    public void V115_A03_DefaultRemainsAuthoringOnlyEvenWhenHelpIsPresent()
    {
        var schema = Schema("输入一个整数；默认值仅用于新建草稿。");
        Assert.Throws<ArgumentException>(() => AlgorithmConfigurationSnapshot.Create(schema,
            Array.Empty<AlgorithmConfigurationEntry>()));
        var materialized = AlgorithmConfigurationSnapshot.Create(schema,
            new[] { new AlgorithmConfigurationEntry("count", "items", schema.Fields.Single().AuthoringDefault!) });
        Assert.Equal(3, materialized.Values.Single().Value.AsInt64());
        Assert.Empty(materialized.Validate(schema));
    }

    [Fact]
    public void V115_A04_ConfigAuthoringHelpCannotSilentlyDisappearThroughResultArchive()
    {
        var field = new AlgorithmFieldDefinition("count", AlgorithmScalarType.Int64, "items", true,
            helpText: "仅配置编辑器使用的帮助");
        var overlay = new OverlayContract("Overlay", "1", 0, 0, 0);
        Assert.Throws<ArgumentException>(() => new AlgorithmResultSchema("Result", "1",
            new[] { field }, Array.Empty<string>(), overlay));
    }

    private static AlgorithmConfigurationSchema Schema(string? help) => new("Legacy.Config", "1",
        new[] { new AlgorithmFieldDefinition("count", AlgorithmScalarType.Int64, "items", true,
            authoringDefault: AlgorithmScalarValue.FromInt64(3), helpText: help) });

    [Fact]
    public void V115_A05_OriginsCannotInventOmitOrDuplicateAConfigurationField()
    {
        Assert.Throws<ArgumentException>(() => new RecipeDraftFieldOrigin("bad/path", RecipeDraftValueOrigin.Explicit));
        Assert.Throws<ArgumentException>(() => new RecipeDraftFieldOrigin("count", (RecipeDraftValueOrigin)99));
        Assert.Throws<ArgumentException>(() => Content(Array.Empty<RecipeDraftFieldOrigin>()));
        Assert.Throws<ArgumentException>(() => Content(new[] { new RecipeDraftFieldOrigin("unknown", RecipeDraftValueOrigin.Explicit) }));
        Assert.Throws<ArgumentException>(() => Content(new[] { new RecipeDraftFieldOrigin("count", RecipeDraftValueOrigin.Explicit),
            new RecipeDraftFieldOrigin("count", RecipeDraftValueOrigin.AuthoringDefault) }));
    }

    [Fact]
    public void V115_A06_ExplicitAndMaterializedDefaultRetainDistinctDraftProvenance()
    {
        var explicitValue = Content(null);
        var defaultValue = Content(new[] { new RecipeDraftFieldOrigin("count", RecipeDraftValueOrigin.AuthoringDefault) });
        Assert.Equal(explicitValue.Configuration.ContentHash, defaultValue.Configuration.ContentHash);
        Assert.NotEqual(explicitValue.ContentHash, defaultValue.ContentHash);
        Assert.Throws<ArgumentException>(() => Content(new[] {
            new RecipeDraftFieldOrigin("count", RecipeDraftValueOrigin.AuthoringDefault) }, value: 4));
    }

    [Fact]
    public void V115_A07_RequirementOrderDoesNotChangeContentButCameraRequestDoes()
    {
        var reference = new RecipeContractReference("Policy", "1", new string('A', 64));
        var requirements = new[] { new RecipePolicyRequirement(RecipePolicyKind.ImageAcquisition, reference),
            new RecipePolicyRequirement(RecipePolicyKind.RecipeGovernance, reference) };
        var first = Content(null, policies: requirements);
        Assert.Equal(first.ContentHash, Content(null, policies: requirements.Reverse()).ContentHash);
        Assert.NotEqual(first.ContentHash, Content(null, policies: requirements, exposure: 200).ContentHash);
    }

    private static RecipeDraftContent Content(IEnumerable<RecipeDraftFieldOrigin>? origins, long value = 3,
        IEnumerable<RecipePolicyRequirement>? policies = null, double exposure = 100)
    {
        var schema = Schema(null);
        var reference = new RecipeContractReference("Result", "1", new string('A', 64));
        return new("Recipe", "工艺草稿", new(new("Algorithm", "1"), schema, reference, reference),
            AlgorithmConfigurationSnapshot.Create(schema,
                new[] { new AlgorithmConfigurationEntry("count", "items", AlgorithmScalarValue.FromInt64(value)) }),
            "MainCamera", new(ProductionAcquisitionMode.SoftwareTrigger, exposure, 0,
                new RegionOfInterest(0, 0, 64, 48), VisionPixelFormat.Mono8, null, 500, 0, null),
            TimeSpan.FromMilliseconds(100), null, policies, origins);
    }
}
