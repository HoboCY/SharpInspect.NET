using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeTransferFieldClassificationTests
{
    [Fact]
    public void V138_F01_LegacyConstructorAndAllLocalOnlyHashRemainStable()
    {
        var legacy = new AlgorithmFieldDefinition("threshold", AlgorithmScalarType.Int64, "mm", true,
            new AlgorithmScalarConstraints(minInt64: 0, maxInt64: 100),
            AlgorithmScalarValue.FromInt64(10), "optional");
        var explicitLocal = new AlgorithmFieldDefinition(
            AlgorithmConfigurationTransferClassification.LocalOnly, "threshold", AlgorithmScalarType.Int64,
            "mm", true, new AlgorithmScalarConstraints(minInt64: 0, maxInt64: 100),
            AlgorithmScalarValue.FromInt64(10), "optional");

        var legacySchema = new AlgorithmConfigurationSchema("transfer.config", "1", new[] { legacy });
        var explicitSchema = new AlgorithmConfigurationSchema("transfer.config", "1", new[] { explicitLocal });

        Assert.Equal(AlgorithmConfigurationTransferClassification.LocalOnly, legacy.TransferClassification);
        Assert.Equal(legacySchema.ContentHash, explicitSchema.ContentHash);
        Assert.NotNull(typeof(AlgorithmFieldDefinition).GetConstructor(new[]
        {
            typeof(string), typeof(AlgorithmScalarType), typeof(string), typeof(bool),
            typeof(AlgorithmScalarConstraints), typeof(AlgorithmScalarValue), typeof(string)
        }));
    }

    [Fact]
    public void V138_F02_NonLocalClassificationIsBoundIntoSchemaHash()
    {
        var local = new AlgorithmConfigurationSchema("transfer.config", "1", new[]
        {
            new AlgorithmFieldDefinition("threshold", AlgorithmScalarType.Int64, "mm", true)
        });
        var portable = new AlgorithmConfigurationSchema("transfer.config", "1", new[]
        {
            new AlgorithmFieldDefinition(AlgorithmConfigurationTransferClassification.PortableRecipeData,
                "threshold", AlgorithmScalarType.Int64, "mm", true)
        });
        var sensitive = new AlgorithmConfigurationSchema("transfer.config", "1", new[]
        {
            new AlgorithmFieldDefinition(AlgorithmConfigurationTransferClassification.Sensitive,
                "threshold", AlgorithmScalarType.Int64, "mm", true)
        });

        Assert.NotEqual(local.ContentHash, portable.ContentHash);
        Assert.NotEqual(portable.ContentHash, sensitive.ContentHash);
        Assert.Equal(AlgorithmConfigurationTransferClassification.PortableRecipeData,
            portable.Fields[0].TransferClassification);
        Assert.Equal(AlgorithmConfigurationTransferClassification.Sensitive,
            sensitive.Fields[0].TransferClassification);
    }

    [Fact]
    public void V138_F03_ClassificationExtensionCoversEveryFieldAndOrder()
    {
        var first = new AlgorithmConfigurationSchema("transfer.config", "1", new[]
        {
            new AlgorithmFieldDefinition(AlgorithmConfigurationTransferClassification.PortableRecipeData,
                "portable", AlgorithmScalarType.String, "text", false),
            new AlgorithmFieldDefinition("local", AlgorithmScalarType.Boolean, "none", false)
        });
        var reordered = new AlgorithmConfigurationSchema("transfer.config", "1", first.Fields.Reverse());

        Assert.Equal(first.ContentHash, reordered.ContentHash);
        Assert.NotEqual(first.ContentHash, new AlgorithmConfigurationSchema("transfer.config", "1", new[]
        {
            new AlgorithmFieldDefinition(AlgorithmConfigurationTransferClassification.Sensitive,
                "portable", AlgorithmScalarType.String, "text", false),
            new AlgorithmFieldDefinition("local", AlgorithmScalarType.Boolean, "none", false)
        }).ContentHash);
    }

    [Fact]
    public void V138_F04_InvalidClassificationIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new AlgorithmFieldDefinition(
            (AlgorithmConfigurationTransferClassification)99, "x", AlgorithmScalarType.Int64,
            "none", false));
    }
}
