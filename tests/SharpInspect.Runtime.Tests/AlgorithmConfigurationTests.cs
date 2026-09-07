using System.Globalization;
using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class AlgorithmConfigurationTests
{
    [Fact]
    public void V110_C01_ScalarValuesPreserveTypeAndRejectWrongAccessors()
    {
        var boolean = AlgorithmScalarValue.FromBoolean(true);
        var integer = AlgorithmScalarValue.FromInt64(long.MaxValue);
        var floating = AlgorithmScalarValue.FromFloat64(
            BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000)));
        var text = AlgorithmScalarValue.FromString(string.Empty);
        var enumeration = AlgorithmScalarValue.FromEnum("accurate");

        Assert.Equal(AlgorithmScalarType.Boolean, boolean.Type);
        Assert.Equal(AlgorithmScalarType.Boolean, boolean.ScalarType);
        Assert.True(boolean.AsBoolean());
        Assert.Equal(long.MaxValue, integer.AsInt64());
        Assert.Equal(0d, floating.AsFloat64());
        Assert.Equal(string.Empty, text.AsString());
        Assert.Equal("accurate", enumeration.AsEnum());
        Assert.Throws<InvalidOperationException>(() => integer.AsString());
        Assert.Throws<InvalidOperationException>(() => text.AsInt64());
        Assert.Throws<InvalidOperationException>(() => enumeration.AsString());
        Assert.Throws<InvalidOperationException>(() => text.AsEnum());
        Assert.Throws<ArgumentException>(() => AlgorithmScalarValue.FromFloat64(double.NaN));
        Assert.Throws<ArgumentException>(() => AlgorithmScalarValue.FromFloat64(double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => AlgorithmScalarValue.FromString("bad\uD800"));
    }

    [Fact]
    public void V110_C02_SchemaIsImmutableAndCanonicalHashIsOrderAndCultureIndependent()
    {
        var fields = new List<AlgorithmFieldDefinition>
        {
            Field("mode", AlgorithmScalarType.Enum, "none", false,
                new AlgorithmScalarConstraints(allowedValues: new[] { "fast", "accurate" }),
                AlgorithmScalarValue.FromEnum("fast")),
            Field("threshold", AlgorithmScalarType.Int64, "mm", true,
                new AlgorithmScalarConstraints(minInt64: 0, maxInt64: 100),
                AlgorithmScalarValue.FromInt64(10))
        };
        var schema = new AlgorithmConfigurationSchema("edge", "v1", fields);
        fields[0] = Field("replaced", AlgorithmScalarType.String, "text", false);

        Assert.Equal("mode", schema.Fields[0].Key);
        Assert.Throws<NotSupportedException>(() => ((IList<AlgorithmFieldDefinition>)schema.Fields)[0] =
            Field("mutated", AlgorithmScalarType.String, "text", false));

        var reordered = new AlgorithmConfigurationSchema("edge", "v1", schema.Fields.Reverse());
        var reorderedAllowedValues = new AlgorithmConfigurationSchema("edge", "v1", new[]
        {
            Field("mode", AlgorithmScalarType.Enum, "none", false,
                new AlgorithmScalarConstraints(allowedValues: new[] { "accurate", "fast" }),
                AlgorithmScalarValue.FromEnum("fast")),
            Field("threshold", AlgorithmScalarType.Int64, "mm", true,
                new AlgorithmScalarConstraints(minInt64: 0, maxInt64: 100),
                AlgorithmScalarValue.FromInt64(10))
        });

        Assert.Equal(schema.ContentHash, reordered.ContentHash);
        Assert.Equal(schema.ContentHash, reorderedAllowedValues.ContentHash);

        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");
            var underTurkishCulture = new AlgorithmConfigurationSchema("edge", "v1", schema.Fields);
            Assert.Equal(schema.ContentHash, underTurkishCulture.ContentHash);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void V110_C03_CreateKeepsOptionalFieldsOmittedAndNeverUsesDefaultsForRequiredFields()
    {
        var schema = SchemaWithDefaults();
        var entries = new List<AlgorithmConfigurationEntry>
        {
            Entry("threshold", "mm", AlgorithmScalarValue.FromInt64(25))
        };

        var snapshot = AlgorithmConfigurationSnapshot.Create(schema, entries);
        entries.Clear();

        Assert.Single(snapshot.Values);
        Assert.Equal("threshold", snapshot.Values[0].Key);
        Assert.DoesNotContain(snapshot.Values, item => item.Key == "mode");
        Assert.Empty(snapshot.Validate(schema));
        Assert.Throws<ArgumentException>(() => AlgorithmConfigurationSnapshot.Create(
            schema, Array.Empty<AlgorithmConfigurationEntry>()));
    }

    [Fact]
    public void V110_C04_SnapshotHashBindsIdentityValuesAndUsesExactInt64Precision()
    {
        var schema = new AlgorithmConfigurationSchema("edge", "v1", new[]
        {
            Field("count", AlgorithmScalarType.Int64, "items", true)
        });
        var maximum = AlgorithmConfigurationSnapshot.Create(schema, new[]
        {
            Entry("count", "items", AlgorithmScalarValue.FromInt64(long.MaxValue))
        });
        var adjacent = AlgorithmConfigurationSnapshot.Create(schema, new[]
        {
            Entry("count", "items", AlgorithmScalarValue.FromInt64(long.MaxValue - 1))
        });
        var reorderedSchema = new AlgorithmConfigurationSchema("edge", "v2", new[]
        {
            Field("count", AlgorithmScalarType.Int64, "items", true)
        });
        var changedUnitSchema = new AlgorithmConfigurationSchema("edge", "v1", new[]
        {
            Field("count", AlgorithmScalarType.Int64, "pieces", true)
        });

        Assert.NotEqual(maximum.ContentHash, adjacent.ContentHash);
        Assert.NotEqual(maximum.SchemaContentHash, reorderedSchema.ContentHash);
        Assert.NotEqual(maximum.ContentHash, AlgorithmConfigurationSnapshot.Create(reorderedSchema,
            new[] { Entry("count", "items", AlgorithmScalarValue.FromInt64(long.MaxValue)) }).ContentHash);
        Assert.NotEqual(maximum.ContentHash, AlgorithmConfigurationSnapshot.Create(changedUnitSchema,
            new[] { Entry("count", "pieces", AlgorithmScalarValue.FromInt64(long.MaxValue)) }).ContentHash);

        var negativeZero = AlgorithmConfigurationSnapshot.Create(
            new AlgorithmConfigurationSchema("float", "v1", new[]
            {
                Field("value", AlgorithmScalarType.Float64, "none", true)
            }),
            new[] { Entry("value", "none", AlgorithmScalarValue.FromFloat64(
                BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000)))) });
        var positiveZero = AlgorithmConfigurationSnapshot.Create(
            new AlgorithmConfigurationSchema("float", "v1", new[]
            {
                Field("value", AlgorithmScalarType.Float64, "none", true)
            }),
            new[] { Entry("value", "none", AlgorithmScalarValue.FromFloat64(0d)) });
        Assert.Equal(negativeZero.ContentHash, positiveZero.ContentHash);
    }

    [Fact]
    public void V110_C05_ConstraintsRejectIncompatibleOrInvalidDefinitions()
    {
        Assert.Throws<ArgumentException>(() => new AlgorithmScalarConstraints(minInt64: 3, maxInt64: 2));
        Assert.Throws<ArgumentException>(() => new AlgorithmScalarConstraints(minFloat64: double.NaN));
        Assert.Throws<ArgumentException>(() => new AlgorithmScalarConstraints(maxFloat64: double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => new AlgorithmScalarConstraints(minLength: 4, maxLength: 3));
        Assert.Throws<ArgumentException>(() => new AlgorithmScalarConstraints(
            allowedValues: new[] { "same", "same" }));
        Assert.Throws<ArgumentException>(() => new AlgorithmFieldDefinition("flag", AlgorithmScalarType.Boolean,
            "none", false, new AlgorithmScalarConstraints(minInt64: 0)));
        Assert.Throws<ArgumentException>(() => new AlgorithmFieldDefinition("count", AlgorithmScalarType.Int64,
            "none", false, null, AlgorithmScalarValue.FromString("wrong")));
        Assert.Throws<ArgumentException>(() => new AlgorithmFieldDefinition("count", AlgorithmScalarType.Int64,
            "none", false, new AlgorithmScalarConstraints(minInt64: 1),
            AlgorithmScalarValue.FromInt64(0)));
        Assert.Throws<ArgumentException>(() => new AlgorithmFieldDefinition("bad/key", AlgorithmScalarType.Int64,
            "none", false));
        Assert.Throws<ArgumentException>(() => new AlgorithmFieldDefinition("count", AlgorithmScalarType.Int64,
            "unit/value", false));
    }

    [Fact]
    public void V110_C06_ValidateReportsIdentityAndEveryStructuralFieldFailure()
    {
        var schema = new AlgorithmConfigurationSchema("edge", "v1", new[]
        {
            Field("required", AlgorithmScalarType.Int64, "mm", true,
                new AlgorithmScalarConstraints(minInt64: 0, maxInt64: 10)),
            Field("mode", AlgorithmScalarType.Enum, "none", false,
                new AlgorithmScalarConstraints(allowedValues: new[] { "fast" }))
        });
        var snapshot = new AlgorithmConfigurationSnapshot(
            "other", "v2", new string('0', 64), "future-config-v2", new string('F', 64),
            new[]
            {
                Entry("mode", "wrong", AlgorithmScalarValue.FromString("fast")),
                Entry("mode", "none", AlgorithmScalarValue.FromEnum("slow")),
                Entry("unknown", "none", AlgorithmScalarValue.FromBoolean(true))
            });

        var issues = snapshot.Validate(schema);

        Assert.Contains(issues, issue => issue.Code == "AlgorithmSchemaIdMismatch");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmSchemaVersionMismatch");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmSchemaContentHashMismatch");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmCanonicalizationVersionMismatch");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmFieldDuplicate" && issue.FieldKey == "mode");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmFieldUnitMismatch" && issue.FieldKey == "mode");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmFieldTypeMismatch" && issue.FieldKey == "mode");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmFieldUnknown" && issue.FieldKey == "unknown");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmFieldMissingRequired" && issue.FieldKey == "required");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmContentHashMismatch");
    }

    [Fact]
    public void V110_C07_ConstraintCollectionsAndSnapshotsDefensivelyCopyInputs()
    {
        var allowed = new List<string> { "fast", "accurate" };
        var constraints = new AlgorithmScalarConstraints(allowedValues: allowed);
        allowed[0] = "changed";
        var schema = new AlgorithmConfigurationSchema("edge", "v1", new[]
        {
            Field("mode", AlgorithmScalarType.Enum, "none", false, constraints)
        });
        var values = new List<AlgorithmConfigurationEntry>
        {
            Entry("mode", "none", AlgorithmScalarValue.FromEnum("fast"))
        };
        var snapshot = AlgorithmConfigurationSnapshot.Create(schema, values);
        values.Clear();

        Assert.Equal("fast", constraints.AllowedValues![0]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)constraints.AllowedValues)[0] = "changed");
        Assert.Single(snapshot.Values);
        Assert.Throws<NotSupportedException>(() => ((IList<AlgorithmConfigurationEntry>)snapshot.Values).Clear());
    }

    [Fact]
    public void V110_C08_BoundedCollectionsAllowEmptySchemaAndRejectOverflow()
    {
        var emptySchema = new AlgorithmConfigurationSchema("empty", "v1", Array.Empty<AlgorithmFieldDefinition>());
        var emptySnapshot = AlgorithmConfigurationSnapshot.Create(emptySchema,
            Array.Empty<AlgorithmConfigurationEntry>());
        Assert.Empty(emptySnapshot.Values);
        Assert.Empty(emptySnapshot.Validate(emptySchema));

        var fields = Enumerable.Range(0, 256)
            .Select(index => Field($"f{index:D3}", AlgorithmScalarType.Int64, "none", false))
            .ToArray();
        _ = new AlgorithmConfigurationSchema("large", "v1", fields);
        Assert.Throws<ArgumentException>(() => new AlgorithmConfigurationSchema("too-large", "v1",
            fields.Concat(new[] { Field("f256", AlgorithmScalarType.Int64, "none", false) })));
        Assert.Throws<ArgumentException>(() => AlgorithmConfigurationSnapshot.Create(emptySchema,
            Enumerable.Range(0, 257).Select(index => Entry($"f{index:D3}", "none",
                AlgorithmScalarValue.FromInt64(index)))));
    }

    [Fact]
    public void V110_C09_CreateRejectsUnknownDuplicateWrongUnitAndConstraintViolations()
    {
        var schema = new AlgorithmConfigurationSchema("edge", "v1", new[]
        {
            Field("count", AlgorithmScalarType.Int64, "items", true,
                new AlgorithmScalarConstraints(minInt64: 0, maxInt64: 4))
        });

        Assert.Throws<ArgumentException>(() => AlgorithmConfigurationSnapshot.Create(schema, new[]
        {
            Entry("count", "items", AlgorithmScalarValue.FromInt64(1)),
            Entry("count", "items", AlgorithmScalarValue.FromInt64(2))
        }));
        Assert.Throws<ArgumentException>(() => AlgorithmConfigurationSnapshot.Create(schema, new[]
        {
            Entry("unknown", "items", AlgorithmScalarValue.FromInt64(1)),
            Entry("count", "items", AlgorithmScalarValue.FromInt64(2))
        }));
        Assert.Throws<ArgumentException>(() => AlgorithmConfigurationSnapshot.Create(schema, new[]
        {
            Entry("count", "wrong", AlgorithmScalarValue.FromInt64(2))
        }));
        Assert.Throws<ArgumentException>(() => AlgorithmConfigurationSnapshot.Create(schema, new[]
        {
            Entry("count", "items", AlgorithmScalarValue.FromInt64(5))
        }));
        Assert.Throws<ArgumentException>(() => new AlgorithmConfigurationSnapshot(
            "edge", "v1", new string('0', 63), AlgorithmConfigurationSchema.CanonicalizationVersion,
            new string('0', 64), Array.Empty<AlgorithmConfigurationEntry>()));
    }

    [Fact]
    public void V110_C10_EnumConfigurationUsesClosedOptionsForValidationAndStableHashing()
    {
        var fastSchema = new AlgorithmConfigurationSchema("edge", "v1", new[]
        {
            Field("mode", AlgorithmScalarType.Enum, "none", true,
                new AlgorithmScalarConstraints(allowedValues: new[] { "fast", "accurate" }))
        });
        var reorderedOptionsSchema = new AlgorithmConfigurationSchema("edge", "v1", new[]
        {
            Field("mode", AlgorithmScalarType.Enum, "none", true,
                new AlgorithmScalarConstraints(allowedValues: new[] { "accurate", "fast" }))
        });
        var fast = AlgorithmConfigurationSnapshot.Create(fastSchema, new[]
        {
            Entry("mode", "none", AlgorithmScalarValue.FromEnum("fast"))
        });
        var reorderedOptions = AlgorithmConfigurationSnapshot.Create(reorderedOptionsSchema, new[]
        {
            Entry("mode", "none", AlgorithmScalarValue.FromEnum("fast"))
        });
        var accurate = AlgorithmConfigurationSnapshot.Create(fastSchema, new[]
        {
            Entry("mode", "none", AlgorithmScalarValue.FromEnum("accurate"))
        });
        var invalid = new AlgorithmConfigurationSnapshot(fast.SchemaId, fast.SchemaVersion,
            fast.SchemaContentHash, fast.CanonicalizationVersion, fast.ContentHash, new[]
            {
                Entry("mode", "none", AlgorithmScalarValue.FromEnum("unsupported"))
            });

        Assert.Empty(fast.Validate(fastSchema));
        Assert.Empty(reorderedOptions.Validate(reorderedOptionsSchema));
        Assert.Equal(fastSchema.ContentHash, reorderedOptionsSchema.ContentHash);
        Assert.Equal(fast.ContentHash, reorderedOptions.ContentHash);
        Assert.NotEqual(fast.ContentHash, accurate.ContentHash);
        Assert.Contains(invalid.Validate(fastSchema), issue =>
            issue.Code == "AlgorithmFieldConstraintViolation" && issue.FieldKey == "mode");
    }

    [Fact]
    public void V110_C11_StringAndEnumDefaultsConstraintsAndSnapshotsUseTheirOwnAccessors()
    {
        var schema = new AlgorithmConfigurationSchema("edge", "v1", new[]
        {
            Field("label", AlgorithmScalarType.String, "text", true,
                new AlgorithmScalarConstraints(minLength: 2, maxLength: 5),
                AlgorithmScalarValue.FromString("ok")),
            Field("mode", AlgorithmScalarType.Enum, "none", true,
                new AlgorithmScalarConstraints(allowedValues: new[] { "fast", "accurate" }),
                AlgorithmScalarValue.FromEnum("fast"))
        });
        var valid = AlgorithmConfigurationSnapshot.Create(schema, new[]
        {
            Entry("label", "text", AlgorithmScalarValue.FromString("good")),
            Entry("mode", "none", AlgorithmScalarValue.FromEnum("fast"))
        });

        Assert.Empty(valid.Validate(schema));
        Assert.Throws<ArgumentException>(() => AlgorithmConfigurationSnapshot.Create(schema, new[]
        {
            Entry("label", "text", AlgorithmScalarValue.FromString("x")),
            Entry("mode", "none", AlgorithmScalarValue.FromEnum("fast"))
        }));
        Assert.Throws<ArgumentException>(() => AlgorithmConfigurationSnapshot.Create(schema, new[]
        {
            Entry("label", "text", AlgorithmScalarValue.FromString("good")),
            Entry("mode", "none", AlgorithmScalarValue.FromEnum("unsupported"))
        }));

        var invalid = new AlgorithmConfigurationSnapshot(valid.SchemaId, valid.SchemaVersion,
            valid.SchemaContentHash, valid.CanonicalizationVersion, valid.ContentHash, new[]
            {
                Entry("label", "text", AlgorithmScalarValue.FromString("x")),
                Entry("mode", "none", AlgorithmScalarValue.FromEnum("unsupported"))
            });
        var issues = invalid.Validate(schema);

        Assert.Contains(issues, issue => issue.Code == "AlgorithmFieldConstraintViolation" &&
            issue.FieldKey == "label");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmFieldConstraintViolation" &&
            issue.FieldKey == "mode");
    }

    private static AlgorithmConfigurationSchema SchemaWithDefaults() =>
        new("edge", "v1", new[]
        {
            Field("threshold", AlgorithmScalarType.Int64, "mm", true,
                new AlgorithmScalarConstraints(minInt64: 0, maxInt64: 100),
                AlgorithmScalarValue.FromInt64(10)),
            Field("mode", AlgorithmScalarType.Enum, "none", false,
                new AlgorithmScalarConstraints(allowedValues: new[] { "fast", "accurate" }),
                AlgorithmScalarValue.FromEnum("fast"))
        });

    private static AlgorithmFieldDefinition Field(string key, AlgorithmScalarType type, string unit,
        bool required, AlgorithmScalarConstraints? constraints = null,
        AlgorithmScalarValue? authoringDefault = null) =>
        new(key, type, unit, required, constraints, authoringDefault);

    private static AlgorithmConfigurationEntry Entry(string key, string unit, AlgorithmScalarValue value) =>
        new(key, unit, value);
}
