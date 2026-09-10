using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Plc;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class PlcResultPayloadTests
{
    [Fact]
    public async Task V137_E01_QualificationUsesActualEncoderBytesWithoutProductionPayloadAuthority()
    {
        var schema = Schema();
        var binding = Bind(schema, Contract(schema));
        var outcome = await ExecuteAsync(schema, Result(schema), ExecutionKind.Qualification);
        var encoder = new PlcResultPayloadEncoder();
        var session = Guid.NewGuid();
        var run = new QualificationRunId(outcome.Correlation.Value);
        var encoded = encoder.EncodeQualification(session, run, new string('A', 64), binding,
            0x11223344, 0x55667788, outcome);
        var payload = Assert.IsType<StationQualificationPayload>(encoded.Payload);
        Assert.Equal("117AFE8D996141090BA9C4702EC18934638C91C6A85760999FA770D201AB706F",
            payload.WireContentHash);
        Assert.Equal(session, payload.SessionId);
        Assert.Equal(run, payload.RunId);
        Assert.False(payload.ProductionAuthority);
        Assert.False(encoder.Encode(binding, new(0x11223344, 0x55667788), outcome).Succeeded);
        Assert.False(encoder.EncodePreview(binding, 0x11223344, 0x55667788, outcome).Succeeded);
        var otherContext = encoder.EncodeQualification(session, run, new string('B', 64), binding,
            0x11223344, 0x55667788, outcome).Payload!;
        Assert.Equal(payload.WireContentHash, otherContext.WireContentHash);
        Assert.NotEqual(payload.ContentHash, otherContext.ContentHash);
    }

    [Fact]
    public async Task V137_E02_QualificationEncodingRejectsUnrelatedRunAndManualIdentity()
    {
        var schema = Schema();
        var binding = Bind(schema, Contract(schema));
        var outcome = await ExecuteAsync(schema, Result(schema), ExecutionKind.Qualification);
        var encoder = new PlcResultPayloadEncoder();
        var wrongRun = encoder.EncodeQualification(Guid.NewGuid(), new QualificationRunId(Guid.NewGuid()),
            new string('A', 64), binding, 1, 2, outcome);
        Assert.Null(wrongRun.Payload);
        Assert.Equal("PlcResultQualificationCorrelationRequired", wrongRun.ReasonCode);
        var manual = await ExecuteAsync(schema, Result(schema));
        var wrongKind = encoder.EncodeQualification(Guid.NewGuid(), new QualificationRunId(manual.Correlation.Value),
            new string('A', 64), binding, 1, 2, manual);
        Assert.Null(wrongKind.Payload);
        Assert.Equal("PlcResultQualificationCorrelationRequired", wrongKind.ReasonCode);
    }

    [Fact]
    public async Task V131_E01_PublicAlgorithmOutcomeProducesIndependentGoldenRegisterImageAndHash()
    {
        var schema = Schema();
        var contract = Contract(schema);
        var binding = Bind(schema, contract);
        var outcome = await ExecuteAsync(schema, Result(schema));
        var encoded = new PlcResultPayloadEncoder().EncodePreview(binding, 0x11223344, 0x55667788, outcome);
        Assert.True(encoded.Succeeded, encoded.DetailReasonCode);
        var snapshot = Assert.IsType<PlcResultPayloadPreview>(encoded.Preview);
        // Independent register-image fixture; no production encoder or hash helper generates expected values.
        Assert.Equal(new[] { "10:11223344", "12:55667788", "14:0009", "15:0001", "16:0000",
            "20:0019", "25:000A", "30:0001", "40:A55A" }, Lines(snapshot));
        Assert.Equal("117AFE8D996141090BA9C4702EC18934638C91C6A85760999FA770D201AB706F", snapshot.WireContentHash);
        Assert.Equal(22, snapshot.PayloadBytes);
        Assert.Equal(outcome.Correlation, snapshot.SourceExecution);
        Assert.Equal(contract.Reference, snapshot.Contract);
        Assert.Equal(snapshot.ContentHash, new PlcResultPayloadEncoder().EncodePreview(binding, snapshot.SampleControllerEpoch, snapshot.SampleCycleSequence, outcome).Preview!.ContentHash);
        Assert.DoesNotContain(snapshot.Segments, value => value.StartRegister == 17);
    }

    [Theory]
    [InlineData(PlcByteOrder.BigEndian, PlcWordOrder.HighWordFirst, "11223344")]
    [InlineData(PlcByteOrder.LittleEndian, PlcWordOrder.HighWordFirst, "22114433")]
    [InlineData(PlcByteOrder.BigEndian, PlcWordOrder.LowWordFirst, "33441122")]
    [InlineData(PlcByteOrder.LittleEndian, PlcWordOrder.LowWordFirst, "44332211")]
    public async Task V131_E02_ByteAndWordOrderAreIndependentOfHost(PlcByteOrder bytes, PlcWordOrder words, string expected)
    {
        var schema = Schema();
        var contract = Contract(schema, framework: FrameworkFields().Select(field =>
            field.Field == PlcFrameworkResultField.ControllerEpoch ? new PlcFrameworkFieldMapping(field.Field,
                field.RegisterRange, new(PlcWireRepresentation.UInt32, bytes, words, PlcRoundingMode.Exact, PlcOverflowBehavior.EncodingFault)) : field));
        var outcome = await ExecuteAsync(schema, Result(schema));
        var encoded = new PlcResultPayloadEncoder().EncodePreview(Bind(schema, contract), 0x11223344, uint.MaxValue, outcome);
        Assert.True(encoded.Succeeded, encoded.DetailReasonCode);
        Assert.Equal(expected, Hex(encoded.Preview!, 10));
        Assert.Equal("FFFFFFFF", Hex(encoded.Preview!, 12));
    }

    [Theory]
    [InlineData(InspectionDecision.Pass, null, "0001", "0000")]
    [InlineData(InspectionDecision.Fail, "Defect", "0002", "0014")]
    [InlineData(InspectionDecision.Unknown, "Uncertain", "0003", "0015")]
    public async Task V131_E03_ProductDecisionAndReasonRemainSeparateFromExecutionStatus(InspectionDecision decision,
        string? reason, string expectedDecision, string expectedReason)
    {
        var schema = Schema();
        var outcome = await ExecuteAsync(schema, Result(schema, decision, reason));
        var encoded = new PlcResultPayloadEncoder().EncodePreview(Bind(schema, Contract(schema)), 1, 2, outcome);
        Assert.True(encoded.Succeeded, encoded.DetailReasonCode);
        Assert.Equal("0009", Hex(encoded.Preview!, 14));
        Assert.Equal(expectedDecision, Hex(encoded.Preview!, 15));
        Assert.Equal(expectedReason, Hex(encoded.Preview!, 16));
    }

    [Fact]
    public async Task V131_E04_OptionalSentinelAndNonSuccessDataAreExplicitAndComplete()
    {
        var schema = Schema();
        var binding = Bind(schema, Contract(schema));
        var encoder = new PlcResultPayloadEncoder();
        var absent = await ExecuteAsync(schema, Result(schema, score: null));
        var snapshot = encoder.EncodePreview(binding, 1, 2, absent).Preview!;
        Assert.Equal("FFFF", Hex(snapshot, 25));
        var error = await ExecuteAsync(schema, null);
        var failed = encoder.EncodePreview(binding, 1, 2, error);
        Assert.True(failed.Succeeded, failed.DetailReasonCode);
        Assert.Equal("000A", Hex(failed.Preview!, 14));
        Assert.Equal("0003", Hex(failed.Preview!, 15));
        Assert.Equal("0021", Hex(failed.Preview!, 16)); // explicit AlgorithmExecutionError=33
        Assert.Equal("8000", Hex(failed.Preview!, 20));
        Assert.Equal("EEEE", Hex(failed.Preview!, 25));
        Assert.Equal("FFFF", Hex(failed.Preview!, 30));
        Assert.Equal(9, failed.Preview!.Segments.Count);
    }

    [Fact]
    public async Task V131_E05_ValidAlgorithmOutcomeForDifferentBindingProducesFaultWithNoPartialPayload()
    {
        var schema = Schema();
        var outcome = await ExecuteAsync(schema, Result(schema));
        Assert.Equal(ExecutionStatus.Success, outcome.ExecutionStatus);
        Assert.NotNull(outcome.ValidatedResult);
        var differentRecipe = new RecipeReference("OtherRecipe", "1", new string('B', 64));
        var binding = new PlcResultContractBinder().Bind(differentRecipe, Identity, schema, Contract(schema)).Binding!;
        var encoded = new PlcResultPayloadEncoder().EncodePreview(binding, 1, 2, outcome);
        Assert.False(encoded.Succeeded);
        Assert.Equal("PlcResultEncodingFault", encoded.ReasonCode);
        Assert.Equal("PlcResultBindingOutcomeMismatch", encoded.DetailReasonCode);
        Assert.Null(encoded.Preview);
        var manual = await ExecuteAsync(schema, Result(schema), ExecutionKind.Manual);
        var manualEncoded = new PlcResultPayloadEncoder().Encode(Bind(schema, Contract(schema)), new(1, 2), manual);
        Assert.False(manualEncoded.Succeeded);
        Assert.Null(manualEncoded.Snapshot);
        Assert.Equal("PlcResultProductionCorrelationRequired", manualEncoded.DetailReasonCode);
    }

    [Fact]
    public async Task V131_E06_ImmutableInputsAndOutputsAndCycleIdentityAreHashBound()
    {
        var schema = Schema();
        var input = new byte[] { 0xA5, 0x5A };
        var literal = new PlcWireLiteral(input);
        var contract = Contract(schema, constants: new[] { new PlcConstantField("Marker", new(40, 1), U16, literal) });
        var binding = Bind(schema, contract);
        var outcome = await ExecuteAsync(schema, Result(schema));
        var encoder = new PlcResultPayloadEncoder();
        var snapshot = encoder.EncodePreview(binding, 1, 2, outcome).Preview!;
        input[0] = 0;
        var exported = literal.ToArray(); exported[0] = 0;
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(literal.Memory, out var exportedMemory));
        exportedMemory.Array![exportedMemory.Offset] = 0;
        Assert.Equal("A55A", Hex(snapshot, 40));
        Assert.Throws<NotSupportedException>(() => ((IList<byte>)snapshot.Segments.Last().RegisterBytes)[0] = 0);
        Assert.Equal(snapshot.ContentHash, encoder.EncodePreview(binding, 1, 2, outcome).Preview!.ContentHash);
        Assert.NotEqual(snapshot.ContentHash, encoder.EncodePreview(binding, 1, 3, outcome).Preview!.ContentHash);
        Assert.NotEqual(snapshot.WireContentHash, encoder.EncodePreview(binding, 1, 3, outcome).Preview!.WireContentHash);
    }

    [Fact]
    public void V131_B10_MissingMappingsOverlapsCapacityAndSentinelCollisionRejectBinding()
    {
        var schema = Schema();
        var fields = FrameworkFields().ToArray();
        Refused(Contract(schema, framework: fields.Skip(1)), "PlcResultFrameworkMappingIncomplete");
        Refused(Contract(schema, mappings: Mappings().Skip(1)), "PlcResultMeasurementDispositionIncomplete");
        Refused(Contract(schema, constants: new[] { new PlcConstantField("Overlap", new(20, 1), U16, Literal("A55A")) }),
            "PlcResultRegisterOverlap");
        Refused(Contract(schema, maximumBytes: 20), "PlcResultAddressOrPayloadCapacityExceeded");
        Refused(Contract(schema, mappings: Mappings(scoreSentinel: "000A")), "PlcResultSentinelCollision");
        void Refused(PlcResultContract contract, string reason)
        {
            var result = new PlcResultContractBinder().Bind(Recipe, Identity, schema, contract);
            Assert.False(result.Bound);
            Assert.Null(result.Binding);
            Assert.Contains(result.Checks, value => !value.Passed && value.ReasonCode == reason);
        }
    }

    [Fact]
    public void V131_B11_UnboundedNumericRangeAndUnmappedReasonCannotBind()
    {
        var schema = new AlgorithmResultSchema("Result.Plc31.Unbounded", "1",
            new[] { new AlgorithmFieldDefinition("Value", AlgorithmScalarType.Int64, "count", true) },
            new[] { "NewReason" }, new("Overlay.Plc31", "1"));
        var mapping = new PlcMeasurementMapping("Value", new PlcRegisterRange(20, 1),
            new("count", "count", new(1), new(0)), U16, Literal("FFFF"));
        var contract = new PlcResultContract("Plc31.Contract", "1", 100, 100, FrameworkFields(),
            new[] { new PlcResultSchemaMap(new(schema.Id, schema.Version, schema.ContentHash), new[] { mapping }) });
        var result = new PlcResultContractBinder().ValidateSchema(contract, schema);
        Assert.False(result.Valid);
        Assert.Null(result.Validation);
        Assert.Contains(result.Checks, value => value.ReasonCode == "PlcResultReasonCodeCoverageInvalid");
        Assert.Contains(result.Checks, value => value.ValidationId == "V131.B06" && !value.Passed);
    }

    [Fact]
    public void V131_B12_MutuallyExclusiveSchemaBranchesMayReuseAddresses()
    {
        var first = Schema();
        var second = new AlgorithmResultSchema("Result.Plc31.Second", "1", first.Measurements, first.ReasonCodes, first.OverlayContract);
        var contract = new PlcResultContract("Plc31.Contract", "1", 100, 100, FrameworkFields(), new[]
        {
            new PlcResultSchemaMap(new(first.Id, first.Version, first.ContentHash), Mappings()),
            new PlcResultSchemaMap(new(second.Id, second.Version, second.ContentHash), Mappings())
        });
        Assert.True(new PlcResultContractBinder().ValidateSchema(contract, first).Valid);
        Assert.True(new PlcResultContractBinder().ValidateSchema(contract, second).Valid);
        var reordered = new AlgorithmResultSchema(first.Id, first.Version, first.Measurements.Reverse(),
            first.ReasonCodes.Reverse(), first.OverlayContract);
        Assert.Equal(first.ContentHash, reordered.ContentHash);
        Assert.Equal(Bind(first, contract).ContentHash, Bind(reordered, contract).ContentHash);
    }

    [Fact]
    public async Task V131_E07_FullInt64DomainUsesExplicitIndependentValidityForAbsence()
    {
        var schema = new AlgorithmResultSchema("Result.Plc31.Validity", "1", new[]
            { new AlgorithmFieldDefinition("Value", AlgorithmScalarType.Int64, "count", false) },
            Array.Empty<string>(), new("Overlay.Plc31.Validity", "1"));
        var mapping = new PlcMeasurementMapping("Value", new PlcRegisterRange(20, 4),
            new("count", "count", new(1), new(0)), Wire(PlcWireRepresentation.Int64), Literal("AAAAAAAAAAAAAAAA"),
            new PlcOptionalAbsence(new PlcValidityField("ValuePresent", new(24, 1), U16, Literal("0001"), Literal("0000")),
                Literal("DEADBEEFDEADBEEF")));
        var contract = new PlcResultContract("Plc31.Validity", "1", 100, 100, FrameworkFields(), new[]
            { new PlcResultSchemaMap(new(schema.Id, schema.Version, schema.ContentHash), new[] { mapping }) });
        var binding = Bind(schema, contract);
        var presentResult = new AlgorithmResult(InspectionDecision.Pass, null, new[]
            { new AlgorithmMeasurement("Value", "count", AlgorithmScalarValue.FromInt64(long.MinValue)) }, new OutputOverlaySet(schema.OverlayContract));
        var present = new PlcResultPayloadEncoder().EncodePreview(binding, 0, uint.MaxValue, await ExecuteAsync(schema, presentResult));
        Assert.True(present.Succeeded, present.DetailReasonCode);
        Assert.Equal("8000000000000000", Hex(present.Preview!, 20));
        Assert.Equal("0001", Hex(present.Preview!, 24));
        var absentResult = new AlgorithmResult(InspectionDecision.Pass, null, Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(schema.OverlayContract));
        var absent = new PlcResultPayloadEncoder().EncodePreview(binding, 0, uint.MaxValue, await ExecuteAsync(schema, absentResult));
        Assert.True(absent.Succeeded, absent.DetailReasonCode);
        Assert.Equal("DEADBEEFDEADBEEF", Hex(absent.Preview!, 20));
        Assert.Equal("0000", Hex(absent.Preview!, 24));
        var error = new PlcResultPayloadEncoder().EncodePreview(binding, 0, uint.MaxValue, await ExecuteAsync(schema, null));
        Assert.True(error.Succeeded, error.DetailReasonCode);
        Assert.Equal("AAAAAAAAAAAAAAAA", Hex(error.Preview!, 20));
        Assert.Equal("0000", Hex(error.Preview!, 24));
    }

    [Fact]
    public async Task V131_E08_InternalProductionFixtureMatchesPreviewBytesButCannotShareItsIdentityOrHashDomain()
    {
        var schema = Schema();
        AlgorithmExecutionOutcome? internalProductionFixture = null;
        var manual = await ExecuteAsync(schema, Result(schema),
            observeInternalProductionFixture: fixture => internalProductionFixture = fixture);
        Assert.Equal(ExecutionKind.Manual, manual.Correlation.Kind);
        Assert.Equal(ExecutionKind.Production, internalProductionFixture!.Correlation.Kind);
        var binding = Bind(schema, Contract(schema));
        var encoder = new PlcResultPayloadEncoder();
        var preview = encoder.EncodePreview(binding, 0x11223344, 0x55667788, manual).Preview!;
        var production = encoder.Encode(binding, new(0x11223344, 0x55667788), internalProductionFixture);
        Assert.True(production.Succeeded, production.DetailReasonCode);
        var snapshot = Assert.IsType<PlcResultPayloadSnapshot>(production.Snapshot);
        Assert.Equal("117AFE8D996141090BA9C4702EC18934638C91C6A85760999FA770D201AB706F", snapshot.WireContentHash);
        Assert.Equal(preview.WireContentHash, snapshot.WireContentHash);
        Assert.NotEqual(preview.ContentHash, snapshot.ContentHash);
        Assert.Equal(manual.Correlation.Value, snapshot.InspectionId);
        Assert.Null(typeof(PlcResultPayloadPreview).GetProperty("InspectionId"));
        Assert.False(typeof(PlcResultPayloadSnapshot).IsAssignableFrom(typeof(PlcResultPayloadPreview)));
        Assert.Empty(typeof(PlcResultPayloadPreview).GetInterfaces().Intersect(typeof(PlcResultPayloadSnapshot).GetInterfaces()));
        var wrongEntry = encoder.EncodePreview(binding, 1, 2, internalProductionFixture);
        Assert.False(wrongEntry.Succeeded);
        Assert.Null(wrongEntry.Preview);
        Assert.Equal("PlcResultPreviewManualCorrelationRequired", wrongEntry.DetailReasonCode);
        Assert.Equal(ExecutionKind.Manual, manual.Correlation.Kind);
    }

    [Theory]
    [InlineData(ExecutionStatus.Timeout, "000B", "001F")]
    [InlineData(ExecutionStatus.Cancelled, "000C", "0020")]
    public async Task V131_E09_ActualRuntimeTimeoutAndCancellationEncodeUnknownAndExplicitUnavailableData(
        ExecutionStatus terminal, string expectedStatus, string expectedReason)
    {
        var schema = Schema();
        var outcome = await ExecuteAsync(schema, Result(schema), runtimeTerminal: terminal);
        Assert.Equal(terminal, outcome.ExecutionStatus);
        Assert.Null(outcome.ValidatedResult);
        var encoded = new PlcResultPayloadEncoder().EncodePreview(Bind(schema, Contract(schema)), 1, 2, outcome);
        Assert.True(encoded.Succeeded, encoded.DetailReasonCode);
        Assert.Equal(expectedStatus, Hex(encoded.Preview!, 14));
        Assert.Equal("0003", Hex(encoded.Preview!, 15));
        Assert.Equal(expectedReason, Hex(encoded.Preview!, 16));
        Assert.Equal("8000", Hex(encoded.Preview!, 20));
        Assert.Equal("EEEE", Hex(encoded.Preview!, 25));
        Assert.Equal("FFFF", Hex(encoded.Preview!, 30));
        Assert.Equal(9, encoded.Preview!.Segments.Count);
    }

    [Fact]
    public async Task V131_E10_BooleanAndFiniteUnicodeStringCodesAreTypedAndComplete()
    {
        var schema = new AlgorithmResultSchema("Result.Plc31.Scalars", "1", new[]
        {
            new AlgorithmFieldDefinition("Present", AlgorithmScalarType.Boolean, "state", true),
            new AlgorithmFieldDefinition("Colour", AlgorithmScalarType.String, "text", true, new(allowedValues: new[] { "红", "蓝" }))
        }, Array.Empty<string>(), new("Overlay.Plc31.Scalars", "1"));
        var mappings = new[]
        {
            new PlcMeasurementMapping("Present", new PlcRegisterRange(20, 1), new("state", "state", new(1), new(0)), U16,
                Literal("FFFF"), codeTable: new[] { new PlcScalarCode(AlgorithmScalarValue.FromBoolean(false), 3), new(AlgorithmScalarValue.FromBoolean(true), 9) }),
            new PlcMeasurementMapping("Colour", new PlcRegisterRange(21, 1), new("text", "text", new(1), new(0)), U16,
                Literal("FFFF"), codeTable: new[] { new PlcScalarCode(AlgorithmScalarValue.FromString("红"), 0x1234), new(AlgorithmScalarValue.FromString("蓝"), 0xABCD) })
        };
        PlcResultContract Create(IEnumerable<PlcMeasurementMapping> values) => new("Plc31.Scalars", "1", 100, 100, FrameworkFields(), new[]
            { new PlcResultSchemaMap(new(schema.Id, schema.Version, schema.ContentHash), values) });
        var binding = Bind(schema, Create(mappings));
        var outcome = await ExecuteAsync(schema, new(InspectionDecision.Pass, null, new[]
        {
            new AlgorithmMeasurement("Present", "state", AlgorithmScalarValue.FromBoolean(true)),
            new AlgorithmMeasurement("Colour", "text", AlgorithmScalarValue.FromString("蓝"))
        }, new OutputOverlaySet(schema.OverlayContract)));
        var encoded = new PlcResultPayloadEncoder().EncodePreview(binding, 0, 0, outcome);
        Assert.True(encoded.Succeeded, encoded.DetailReasonCode);
        Assert.Equal("0009", Hex(encoded.Preview!, 20));
        Assert.Equal("ABCD", Hex(encoded.Preview!, 21));
        var wrongType = new PlcMeasurementMapping("Present", new PlcRegisterRange(20, 1), new("state", "state", new(1), new(0)), U16,
            Literal("FFFF"), codeTable: new[] { new PlcScalarCode(AlgorithmScalarValue.FromString("false"), 3), new(AlgorithmScalarValue.FromString("true"), 9) });
        var rejected = new PlcResultContractBinder().ValidateSchema(Create(new[] { wrongType, mappings[1] }), schema);
        Assert.False(rejected.Valid);
        Assert.Null(rejected.Validation);
        Assert.Contains(rejected.Checks, value => value.ReasonCode == "PlcResultFiniteScalarDomainMappingRequired");
    }

    internal static readonly AlgorithmIdentity Identity = new("Algorithm.Plc31.Golden", "1");
    internal static readonly RecipeReference Recipe = new("Recipe.Plc31.Golden", "1", new string('A', 64));
    internal static readonly PlcWireEncoding U16 = Wire(PlcWireRepresentation.UInt16);
    internal static PlcWireEncoding Wire(PlcWireRepresentation representation, PlcRoundingMode rounding = PlcRoundingMode.Exact) =>
        new(representation, PlcByteOrder.BigEndian,
            representation is PlcWireRepresentation.UInt16 or PlcWireRepresentation.Int16 ? PlcWordOrder.NotApplicable : PlcWordOrder.HighWordFirst,
            rounding, PlcOverflowBehavior.EncodingFault);
    internal static PlcWireLiteral Literal(string hex) => new(Convert.FromHexString(hex));
    internal static AlgorithmResultSchema Schema() => new("Result.Plc31.Golden", "1", new[]
    {
        new AlgorithmFieldDefinition("Length", AlgorithmScalarType.Float64, "mm", true, new(minFloat64: -3, maxFloat64: 3)),
        new AlgorithmFieldDefinition("Score", AlgorithmScalarType.Int64, "count", false, new(minInt64: 0, maxInt64: 100)),
        new AlgorithmFieldDefinition("Label", AlgorithmScalarType.Enum, "state", true, new(allowedValues: new[] { "Good", "Bad" })),
        new AlgorithmFieldDefinition("Note", AlgorithmScalarType.String, "text", false)
    }, new[] { "Defect", "Uncertain" }, new("Overlay.Plc31.Golden", "1"));

    internal static IEnumerable<PlcFrameworkFieldMapping> FrameworkFields()
    {
        yield return new(PlcFrameworkResultField.ControllerEpoch, new(10, 2), Wire(PlcWireRepresentation.UInt32));
        yield return new(PlcFrameworkResultField.ResultSequence, new(12, 2), Wire(PlcWireRepresentation.UInt32));
        yield return new(PlcFrameworkResultField.ExecutionStatus, new(14, 1), U16, executionStatusCodes: new[]
        { new PlcExecutionStatusCode(ExecutionStatus.Success, 9), new(ExecutionStatus.Error, 10), new(ExecutionStatus.Timeout, 11), new(ExecutionStatus.Cancelled, 12) });
        yield return new(PlcFrameworkResultField.InspectionDecision, new(15, 1), U16, inspectionDecisionCodes: new[]
        { new PlcInspectionDecisionCode(InspectionDecision.Pass, 1), new(InspectionDecision.Fail, 2), new(InspectionDecision.Unknown, 3) });
        yield return new(PlcFrameworkResultField.ResultReasonCode, new(16, 1), U16, reasonCodes: new[]
        { new PlcReasonCode(null, 0), new("Defect", 20), new("Uncertain", 21), new("AlgorithmHung", 30),
            new("AlgorithmExecutionTimeout", 31), new("AlgorithmExecutionCancelled", 32), new("AlgorithmExecutionError", 33),
            new("AlgorithmResultContractViolation", 34) });
    }
    internal static IEnumerable<PlcMeasurementMapping> Mappings(string scoreSentinel = "FFFF")
    {
        yield return new("Length", new PlcRegisterRange(20, 1), new("mm", "tenth_mm", new(10), new(0)),
            Wire(PlcWireRepresentation.Int16, PlcRoundingMode.ToNearestTiesToEven), Literal("8000"));
        yield return new("Score", new PlcRegisterRange(25, 1), new("count", "count", new(1), new(0)), U16,
            Literal("EEEE"), new PlcOptionalAbsence(Literal(scoreSentinel)));
        yield return new("Label", new PlcRegisterRange(30, 1), new("state", "state", new(1), new(0)), U16,
            Literal("FFFF"), codeTable: new[] { new PlcScalarCode(AlgorithmScalarValue.FromEnum("Good"), 1),
                new(AlgorithmScalarValue.FromEnum("Bad"), 2) });
        yield return new("Note", PlcMeasurementDisposition.Excluded);
    }
    internal static PlcResultContract Contract(AlgorithmResultSchema schema, IEnumerable<PlcFrameworkFieldMapping>? framework = null,
        IEnumerable<PlcMeasurementMapping>? mappings = null, IEnumerable<PlcConstantField>? constants = null, int maximumBytes = 100) =>
        new("Plc31.Contract", "1", maximumBytes, 100, framework ?? FrameworkFields(), new[]
        { new PlcResultSchemaMap(new(schema.Id, schema.Version, schema.ContentHash), mappings ?? Mappings(),
            constants ?? new[] { new PlcConstantField("Marker", new(40, 1), U16, Literal("A55A")) }) });
    internal static PlcResultContractBinding Bind(AlgorithmResultSchema schema, PlcResultContract contract)
    {
        var result = new PlcResultContractBinder().Bind(Recipe, Identity, schema, contract);
        Assert.True(result.Bound, string.Join(",", result.Checks.Where(value => !value.Passed).Select(value => value.ReasonCode)));
        return Assert.IsType<PlcResultContractBinding>(result.Binding);
    }
    internal static AlgorithmResult Result(AlgorithmResultSchema schema, InspectionDecision decision = InspectionDecision.Pass,
        string? reason = null, long? score = 10) => new(decision, reason, new[]
        {
            new AlgorithmMeasurement("Length", "mm", AlgorithmScalarValue.FromFloat64(2.5)),
            new AlgorithmMeasurement("Label", "state", AlgorithmScalarValue.FromEnum("Good"))
        }.Concat(score is { } value ? new[] { new AlgorithmMeasurement("Score", "count", AlgorithmScalarValue.FromInt64(value)) } :
            Array.Empty<AlgorithmMeasurement>()), new OutputOverlaySet(schema.OverlayContract));
    private static string[] Lines(PlcResultPayloadPreview snapshot) => snapshot.Segments.Select(value =>
        $"{value.StartRegister}:{Convert.ToHexString(value.RegisterBytes.ToArray())}").ToArray();
    private static string Hex(PlcResultPayloadPreview snapshot, int address) =>
        Convert.ToHexString(snapshot.Segments.Single(value => value.StartRegister == address).RegisterBytes.ToArray());

    internal static async Task<AlgorithmExecutionOutcome> ExecuteAsync(AlgorithmResultSchema schema, AlgorithmResult? result,
        ExecutionKind kind = ExecutionKind.Manual, Action<AlgorithmExecutionOutcome>? observeInternalProductionFixture = null,
        ExecutionStatus? runtimeTerminal = null)
    {
        var configurationSchema = new AlgorithmConfigurationSchema("Config.Plc31.Golden", "1", Array.Empty<AlgorithmFieldDefinition>());
        var configuration = AlgorithmConfigurationSnapshot.Create(configurationSchema, Array.Empty<AlgorithmConfigurationEntry>());
        var started = runtimeTerminal is null ? null : new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new GoldenFactory(new(Identity, configurationSchema, schema), result, started);
        await using var preparation = new AlgorithmPreparationService(new[] { factory }, new(TimeSpan.FromSeconds(5)));
        var prepared = await preparation.PrepareAsync(new(Identity, configuration, schema.Id, schema.Version, schema.ContentHash,
            schema.OverlayContract.Id, schema.OverlayContract.Version, schema.OverlayContract.ContentHash, TimeSpan.FromSeconds(2)));
        Assert.True(prepared.Succeeded, prepared.ReasonCode);
        await using var instance = prepared.Prepared!;
        await using var execution = new AlgorithmExecutionService(new(new("Execution.Plc31", "1", TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)), TimeSpan.FromSeconds(1)));
        using var pool = new FrameBufferPool(new(1, 8, TimeSpan.FromSeconds(1)));
        var correlation = new ExecutionCorrelationId(kind, Guid.Parse("13100000-0000-0000-0000-000000000001"));
        var effective = new EffectiveCameraConfiguration(ProductionAcquisitionMode.HardwareTrigger, 500, 1,
            new(0, 0, 2, 1), VisionPixelFormat.Mono8, null, 500, 0, null);
        var utc = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        var metadata = new FrameMetadata(correlation, "Camera.Plc31", 2, 1, 2, VisionPixelFormat.Mono8, null, utc, effective);
        var milestones = new FrameAcquisitionMilestones(1000000, new(utc, 10), new(utc, 12), new(utc, 14), new(utc, 16));
        var provenance = new FrameProvenance(correlation, "virtual", "1", "adapter", "1", "sdk", "1", null,
            "device", null, null, "Mono8", "normalized-v1", false, false, null, null, milestones);
        var frame = pool.TryCopyFrame(metadata, provenance, new byte[] { 0, 255 });
        Assert.NotNull(frame.Lease);
        using var cancellation = new CancellationTokenSource();
        var operation = execution.ExecuteAsync(instance, frame.Lease!,
            new(Recipe, runtimeTerminal == ExecutionStatus.Timeout ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1)),
            cancellation.Token).AsTask();
        if (runtimeTerminal == ExecutionStatus.Cancelled)
        {
            await started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
        }
        var attempt = await operation;
        Assert.True(attempt.Executed, attempt.ReasonCode);
        var drainedDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (pool.GetSnapshot().OutstandingLeases != 0 && DateTime.UtcNow < drainedDeadline) await Task.Delay(1);
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
        var actual = Assert.IsType<AlgorithmExecutionOutcome>(attempt.Outcome);
        if (observeInternalProductionFixture is not null)
        {
            // Test-only construction through InternalsVisibleTo. No public production admission is asserted,
            // and the actual Manual outcome is never modified or relabelled.
            var productionMetadata = new FrameMetadata(new(ExecutionKind.Production, actual.Correlation.Value),
                metadata.LogicalCameraRole, metadata.Width, metadata.Height, metadata.StrideBytes, metadata.PixelFormat,
                metadata.ValidBits, metadata.HostCaptureUtc, metadata.EffectiveCameraConfiguration);
            observeInternalProductionFixture(new(instance, productionMetadata, actual.ExecutionStatus, actual.ReasonCode,
                actual.ValidatedResult, actual.Timing, actual.AdmittedMonotonicTimestamp));
        }
        return actual;
    }
    private sealed class GoldenFactory : IVisionAlgorithmFactory
    {
        private readonly AlgorithmResult? _result;
        private readonly TaskCompletionSource<bool>? _started;
        public GoldenFactory(AlgorithmDescriptor descriptor, AlgorithmResult? result, TaskCompletionSource<bool>? started = null)
        { Descriptor = descriptor; _result = result; _started = started; }
        public AlgorithmDescriptor Descriptor { get; }
        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(Array.Empty<AlgorithmValidationIssue>());
        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IVisionAlgorithm>(new GoldenAlgorithm(_result, _started));
    }
    private sealed class GoldenAlgorithm : IVisionAlgorithm
    {
        private readonly AlgorithmResult? _result;
        private readonly TaskCompletionSource<bool>? _started;
        public GoldenAlgorithm(AlgorithmResult? result, TaskCompletionSource<bool>? started)
        { _result = result; _started = started; }
        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public async ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context, CancellationToken cancellationToken = default)
        {
            if (_started is not null)
            {
                _started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return _result ?? throw new InvalidOperationException("not exported");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
