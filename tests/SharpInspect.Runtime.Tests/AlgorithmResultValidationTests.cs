using System.Collections.ObjectModel;
using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Frames;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class AlgorithmResultValidationTests
{
    [Fact]
    public async Task V112_C01_ValidWholeResultPassesDirectAndPublicExecutionValidation()
    {
        var overlay = new OverlayContract("overlay", "1", maximumElements: 4,
            maximumTotalPoints: 8, maximumPointsPerElement: 4, maximumTextLength: 64);
        var schema = ResultSchema(overlay,
            new[]
            {
                Field("score", AlgorithmScalarType.Float64, "percent", true,
                    new AlgorithmScalarConstraints(minFloat64: 0, maxFloat64: 100)),
                Field("mode", AlgorithmScalarType.Enum, "none", false,
                    new AlgorithmScalarConstraints(allowedValues: new[] { "fast", "accurate" }))
            },
            new[] { "NO_FRAME" });
        var result = new AlgorithmResult(InspectionDecision.Pass, null,
            new[]
            {
                new AlgorithmMeasurement("score", "percent", AlgorithmScalarValue.FromFloat64(42.5)),
                new AlgorithmMeasurement("mode", "none", AlgorithmScalarValue.FromEnum("fast"))
            },
            new OutputOverlaySet(overlay, new OverlayPrimitive[]
            {
                new OverlayMarker(new OverlayPoint(1000, -1000), OverlayMarkerKind.Cross, 2)
            }));

        Assert.Empty(AlgorithmResultValidator.Validate(result, schema));
        var outcome = await ExecuteThroughRuntimeAsync(result, schema);
        Assert.Equal(ExecutionStatus.Success, outcome.ExecutionStatus);
        Assert.Equal(InspectionDecision.Pass, outcome.Decision);
        Assert.Same(result, outcome.ValidatedResult);
    }

    [Fact]
    public async Task V112_C02_DecisionAndReasonCodeAreValidatedExactly()
    {
        var overlay = new OverlayContract("overlay", "1");
        var schema = ResultSchema(overlay, Array.Empty<AlgorithmFieldDefinition>(), new[] { "NO_FRAME" });
        var missingReason = new AlgorithmResult(InspectionDecision.Unknown, "NO_FRAME",
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlay));
        SetProperty(missingReason, nameof(AlgorithmResult.ReasonCode), null);

        var missingOutcome = await ExecuteThroughRuntimeAsync(missingReason, schema);
        AssertContractViolation(missingOutcome);
        Assert.Contains(AlgorithmResultValidator.Validate(missingReason, schema),
            issue => issue.Code == "AlgorithmResultReasonCodeRequired");

        var unknownReason = new AlgorithmResult(InspectionDecision.Pass, "NO_FRAME",
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlay));
        SetProperty(unknownReason, nameof(AlgorithmResult.ReasonCode), "NOT_DECLARED");
        var unknownOutcome = await ExecuteThroughRuntimeAsync(unknownReason, schema);
        AssertContractViolation(unknownOutcome);
        Assert.Contains(AlgorithmResultValidator.Validate(unknownReason, schema),
            issue => issue.Code == "AlgorithmResultReasonCodeUnknown");

        var invalidDecision = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlay));
        SetProperty(invalidDecision, nameof(AlgorithmResult.Decision), (InspectionDecision)99);
        var invalidDecisionOutcome = await ExecuteThroughRuntimeAsync(invalidDecision, schema);
        AssertContractViolation(invalidDecisionOutcome);
        Assert.Contains(AlgorithmResultValidator.Validate(invalidDecision, schema),
            issue => issue.Code == "AlgorithmResultDecisionInvalid");
    }

    [Fact]
    public async Task V112_C03_MeasurementsReportUnknownDuplicateMissingTypeUnitAndValueFailures()
    {
        var overlay = new OverlayContract("overlay", "1");
        var schema = ResultSchema(overlay,
            new[]
            {
                Field("required", AlgorithmScalarType.Int64, "count", true),
                Field("mode", AlgorithmScalarType.Enum, "none", false,
                    new AlgorithmScalarConstraints(allowedValues: new[] { "fast" }))
            },
            Array.Empty<string>());
        var result = new AlgorithmResult(InspectionDecision.Pass, null,
            new[]
            {
                new AlgorithmMeasurement("required", "count", AlgorithmScalarValue.FromInt64(1)),
                new AlgorithmMeasurement("mode", "none", AlgorithmScalarValue.FromEnum("fast"))
            },
            new OutputOverlaySet(overlay));
        var measurements = new ReadOnlyCollection<AlgorithmMeasurement>(new[]
        {
            new AlgorithmMeasurement("required", "wrong", AlgorithmScalarValue.FromBoolean(true)),
            new AlgorithmMeasurement("required", "count", AlgorithmScalarValue.FromInt64(2)),
            new AlgorithmMeasurement("unknown", "none", AlgorithmScalarValue.FromBoolean(true))
        });
        SetProperty(result, nameof(AlgorithmResult.Measurements), measurements);

        var outcome = await ExecuteThroughRuntimeAsync(result, schema);
        AssertContractViolation(outcome);
        var issues = AlgorithmResultValidator.Validate(result, schema);
        Assert.Contains(issues, issue => issue.Code == "AlgorithmResultMeasurementUnitMismatch" &&
            issue.FieldKey == "required");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmResultMeasurementTypeMismatch" &&
            issue.FieldKey == "required");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmResultMeasurementDuplicate" &&
            issue.FieldKey == "required");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmResultMeasurementUnknown" &&
            issue.FieldKey == "unknown");

        var missing = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlay));
        var missingOutcome = await ExecuteThroughRuntimeAsync(missing, schema);
        AssertContractViolation(missingOutcome);
        Assert.Contains(AlgorithmResultValidator.Validate(missing, schema),
            issue => issue.Code == "AlgorithmResultMeasurementMissingRequired" &&
                issue.FieldKey == "required");

        var nullValue = new AlgorithmMeasurement("required", "count", AlgorithmScalarValue.FromInt64(1));
        SetProperty(nullValue, nameof(AlgorithmMeasurement.Value), null);
        var nullValueResult = new AlgorithmResult(InspectionDecision.Pass, null,
            new[] { nullValue }, new OutputOverlaySet(overlay));
        var nullValueOutcome = await ExecuteThroughRuntimeAsync(nullValueResult, schema);
        AssertContractViolation(nullValueOutcome);
        Assert.Contains(AlgorithmResultValidator.Validate(nullValueResult, schema),
            issue => issue.Code == "AlgorithmResultMeasurementValueRequired" &&
                issue.FieldKey == "required");
    }

    [Fact]
    public async Task V112_C04_FiniteValuesAndEverySchemaConstraintAreEnforced()
    {
        var overlay = new OverlayContract("overlay", "1");
        var schema = ResultSchema(overlay,
            new[]
            {
                Field("integer", AlgorithmScalarType.Int64, "count", true,
                    new AlgorithmScalarConstraints(minInt64: 2, maxInt64: 4)),
                Field("floating", AlgorithmScalarType.Float64, "ratio", true,
                    new AlgorithmScalarConstraints(minFloat64: 0, maxFloat64: 1)),
                Field("label", AlgorithmScalarType.String, "text", true,
                    new AlgorithmScalarConstraints(minLength: 2, maxLength: 4)),
                Field("mode", AlgorithmScalarType.Enum, "none", true,
                    new AlgorithmScalarConstraints(allowedValues: new[] { "fast", "accurate" }))
            },
            Array.Empty<string>());

        var nonFinite = AlgorithmScalarValue.FromFloat64(0.5);
        SetPrivateField(nonFinite, "_value", double.NaN);
        var result = new AlgorithmResult(InspectionDecision.Pass, null,
            new[]
            {
                new AlgorithmMeasurement("integer", "count", AlgorithmScalarValue.FromInt64(9)),
                new AlgorithmMeasurement("floating", "ratio", nonFinite),
                new AlgorithmMeasurement("label", "text", AlgorithmScalarValue.FromString("x")),
                new AlgorithmMeasurement("mode", "none", AlgorithmScalarValue.FromEnum("slow"))
            },
            new OutputOverlaySet(overlay));

        var outcome = await ExecuteThroughRuntimeAsync(result, schema);
        AssertContractViolation(outcome);
        var issues = AlgorithmResultValidator.Validate(result, schema);
        Assert.Contains(issues, issue => issue.Code == "AlgorithmResultMeasurementConstraintViolation" &&
            issue.FieldKey == "integer");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmResultMeasurementNonFinite" &&
            issue.FieldKey == "floating");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmResultMeasurementConstraintViolation" &&
            issue.FieldKey == "label");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmResultMeasurementConstraintViolation" &&
            issue.FieldKey == "mode");
    }

    [Fact]
    public async Task V112_C05_OverlayBindingAndLimitsAreExactButFiniteOutOfFrameGeometryIsAllowed()
    {
        var schemaOverlay = new OverlayContract("overlay", "1", maximumElements: 1,
            maximumTotalPoints: 1, maximumPointsPerElement: 1, maximumTextLength: 16);
        var schema = ResultSchema(schemaOverlay, Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>());
        var resultOverlay = new OutputOverlaySet("other-overlay", "2", new OverlayPrimitive[]
        {
            new OverlayLineSegment(new OverlayPoint(-1_000_000, -2_000_000),
                new OverlayPoint(3_000_000, 4_000_000))
        });
        var result = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), resultOverlay);

        var outcome = await ExecuteThroughRuntimeAsync(result, schema);
        AssertContractViolation(outcome);
        var issues = AlgorithmResultValidator.Validate(result, schema);
        Assert.Contains(issues, issue => issue.Code == "AlgorithmResultOverlayContractIdMismatch");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmResultOverlayContractVersionMismatch");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmResultOverlayPointLimitExceeded");
        Assert.DoesNotContain(issues, issue => issue.Code == "AlgorithmResultOverlayNonFinite");

        var validOutOfFrame = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(schemaOverlay,
                new OverlayPrimitive[]
                {
                    new OverlayMarker(new OverlayPoint(-1000, 1000), OverlayMarkerKind.Cross, 1)
                }));
        Assert.Empty(AlgorithmResultValidator.Validate(validOutOfFrame, schema));
    }

    [Fact]
    public async Task V112_C06_AllTenClosedOverlayPrimitivesAndStylesPassAsStructuredData()
    {
        var overlay = new OverlayContract("overlay", "1", maximumElements: 10,
            maximumTotalPoints: 64, maximumPointsPerElement: 16, maximumTextLength: 64);
        var style = new OverlayStyle(new OverlayColor(10, 20, 30, 255), strokeWidth: 2,
            fillColor: null, strokePattern: OverlayStrokePattern.Dashed,
            markerSize: 5, textSize: 14, textAnchor: OverlayTextAnchor.Center);
        var fillStyle = new OverlayStyle(new OverlayColor(10, 20, 30, 255), strokeWidth: 2,
            fillColor: new OverlayColor(1, 2, 3, 100), strokePattern: OverlayStrokePattern.Dashed,
            markerSize: 5, textSize: 14, textAnchor: OverlayTextAnchor.Center);
        var primitives = new OverlayPrimitive[]
        {
            new OverlayMarker(new OverlayPoint(0, 0), OverlayMarkerKind.Circle, 5, fillStyle),
            new OverlayLineSegment(new OverlayPoint(0, 0), new OverlayPoint(1, 1), style),
            new OverlayArrow(new OverlayPoint(1, 1), new OverlayPoint(2, 2), style),
            new OverlayPolyline(new[] { new OverlayPoint(0, 0), new OverlayPoint(1, 1) }, style),
            new OverlayPolygon(new[] { new OverlayPoint(0, 0), new OverlayPoint(1, 0),
                new OverlayPoint(0, 1) }, fillStyle),
            new OverlayAxisAlignedRectangle(new OverlayPoint(0, 0), 2, 3, fillStyle),
            new OverlayRotatedRectangle(new OverlayPoint(0, 0), 2, 3, 45, fillStyle),
            new OverlayCircle(new OverlayPoint(0, 0), 2, fillStyle),
            new OverlayEllipse(new OverlayPoint(0, 0), 2, 3, 45, fillStyle),
            new OverlayText(new OverlayPoint(0, 0), "良品标签", style, OverlayTextAnchor.Center)
        };
        var result = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlay, primitives));

        Assert.Empty(AlgorithmResultValidator.Validate(result,
            ResultSchema(overlay, Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>())));
        var outcome = await ExecuteThroughRuntimeAsync(result,
            ResultSchema(overlay, Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>()));
        Assert.Equal(ExecutionStatus.Success, outcome.ExecutionStatus);
    }

    [Fact]
    public async Task V112_C07_OverlayTextSafetyAndPrimitiveStyleValidationRejectsHostileValues()
    {
        var overlay = new OverlayContract("overlay", "1", maximumTextLength: 128);
        var style = new OverlayStyle(new OverlayColor(1, 2, 3));
        var text = new OverlayText(new OverlayPoint(0, 0), "普通标签", style);
        SetProperty(text, nameof(OverlayText.Text), "<script>powershell C:\\secret\\x.ps1</script>");
        SetProperty(style, nameof(OverlayStyle.StrokeWidth), double.NaN);
        var marker = new OverlayMarker(new OverlayPoint(0, 0), OverlayMarkerKind.Cross, 1, style);
        SetProperty(marker, nameof(OverlayMarker.MarkerKind), (OverlayMarkerKind)99);
        var result = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlay,
                new OverlayPrimitive[] { text, marker }));
        var schema = ResultSchema(overlay, Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>());

        var outcome = await ExecuteThroughRuntimeAsync(result, schema);
        AssertContractViolation(outcome);
        var issues = AlgorithmResultValidator.Validate(result, schema);
        Assert.Contains(issues, issue => issue.Code == "AlgorithmResultOverlayTextUnsafe");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmResultOverlayPositiveFiniteInvalid");
        Assert.Contains(issues, issue => issue.Code == "AlgorithmResultOverlayEnumInvalid");

        var commandText = new OverlayText(new OverlayPoint(0, 0), "普通标签");
        SetProperty(commandText, nameof(OverlayText.Text), "rm -rf tmp");
        var commandResult = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlay,
                new OverlayPrimitive[] { commandText }));
        var commandOutcome = await ExecuteThroughRuntimeAsync(commandResult, schema);
        AssertContractViolation(commandOutcome);
        Assert.Contains(AlgorithmResultValidator.Validate(commandResult, schema),
            issue => issue.Code == "AlgorithmResultOverlayTextUnsafe");

        var indentedCommandText = new OverlayText(new OverlayPoint(0, 0), "普通标签");
        SetProperty(indentedCommandText, nameof(OverlayText.Text), "  \trm -rf tmp");
        var indentedCommandResult = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlay,
                new OverlayPrimitive[] { indentedCommandText }));
        AssertContractViolation(await ExecuteThroughRuntimeAsync(indentedCommandResult, schema));
        Assert.Contains(AlgorithmResultValidator.Validate(indentedCommandResult, schema),
            issue => issue.Code == "AlgorithmResultOverlayTextUnsafe");

        var bidiText = new OverlayText(new OverlayPoint(0, 0), "普通标签");
        SetProperty(bidiText, nameof(OverlayText.Text), "正常\u202E标签");
        var bidiResult = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlay,
                new OverlayPrimitive[] { bidiText }));
        var bidiOutcome = await ExecuteThroughRuntimeAsync(bidiResult, schema);
        AssertContractViolation(bidiOutcome);
        Assert.Contains(AlgorithmResultValidator.Validate(bidiResult, schema),
            issue => issue.Code == "AlgorithmResultOverlayTextUnsafe");

        var fillStyle = new OverlayStyle(new OverlayColor(1, 2, 3), fillColor: new OverlayColor(4, 5, 6));
        var lineWithFill = new OverlayLineSegment(new OverlayPoint(0, 0), new OverlayPoint(1, 1), fillStyle);
        var fillResult = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlay,
                new OverlayPrimitive[] { lineWithFill }));
        var fillOutcome = await ExecuteThroughRuntimeAsync(fillResult, schema);
        AssertContractViolation(fillOutcome);
        Assert.Contains(AlgorithmResultValidator.Validate(fillResult, schema),
            issue => issue.Code == "AlgorithmResultOverlayFillUnsupported");
    }

    [Fact]
    public async Task V112_C08_ReflectionMalformedScalarAndNullOverlayAreRejectedWithoutRepair()
    {
        var overlay = new OverlayContract("overlay", "1");
        var schema = ResultSchema(overlay,
            new[] { Field("score", AlgorithmScalarType.Float64, "percent", true) },
            Array.Empty<string>());
        var value = AlgorithmScalarValue.FromFloat64(1);
        SetPrivateField(value, "_value", "not-a-double");
        var result = new AlgorithmResult(InspectionDecision.Pass, null,
            new[] { new AlgorithmMeasurement("score", "percent", value) },
            new OutputOverlaySet(overlay));

        var scalarOutcome = await ExecuteThroughRuntimeAsync(result, schema);
        AssertContractViolation(scalarOutcome);
        Assert.Contains(AlgorithmResultValidator.Validate(result, schema),
            issue => issue.Code == "AlgorithmResultMeasurementValueInvalid");

        var nullOverlay = new AlgorithmResult(InspectionDecision.Pass, null,
            new[] { new AlgorithmMeasurement("score", "percent", AlgorithmScalarValue.FromFloat64(1)) },
            new OutputOverlaySet(overlay));
        SetProperty(nullOverlay, nameof(AlgorithmResult.OverlaySet), null);
        var nullOverlayOutcome = await ExecuteThroughRuntimeAsync(nullOverlay, schema);
        AssertContractViolation(nullOverlayOutcome);
        Assert.Contains(AlgorithmResultValidator.Validate(nullOverlay, schema),
            issue => issue.Code == "AlgorithmResultOverlayRequired");
    }

    [Fact]
    public async Task V112_C09_ValidationIsBoundedAtThirtyTwoIssues()
    {
        var overlay = new OverlayContract("overlay", "1");
        var schema = ResultSchema(overlay, Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>());
        var measurements = Enumerable.Range(0, 256)
            .Select(index => new AlgorithmMeasurement("unknown" + index, "none",
                AlgorithmScalarValue.FromBoolean(true)))
            .ToArray();
        var result = new AlgorithmResult(InspectionDecision.Pass, null, measurements,
            new OutputOverlaySet(overlay));

        var issues = AlgorithmResultValidator.Validate(result, schema);
        Assert.Equal(32, issues.Count);
        var outcome = await ExecuteThroughRuntimeAsync(result, schema);
        AssertContractViolation(outcome);
    }

    [Fact]
    public async Task V112_C10_NullResultIsContractViolationAndNoValidatedResultIsPublished()
    {
        var overlay = new OverlayContract("overlay", "1");
        var schema = ResultSchema(overlay, Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>());

        var outcome = await ExecuteThroughRuntimeAsync(null, schema);

        AssertContractViolation(outcome);
        Assert.Null(outcome.ValidatedResult);
        Assert.Equal(InspectionDecision.Unknown, outcome.Decision);
    }

    [Fact]
    public void V112_C11_NullInputsProduceBoundedIssuesWithoutThrowing()
    {
        var resultIssues = AlgorithmResultValidator.Validate(null, null!);
        Assert.Single(resultIssues);
        Assert.Equal("AlgorithmResultRequired", resultIssues[0].Code);

        var overlay = new OverlayContract("overlay", "1");
        var schema = ResultSchema(overlay, Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>());
        var schemaIssues = AlgorithmResultValidator.Validate(
            new AlgorithmResult(InspectionDecision.Pass, null, Array.Empty<AlgorithmMeasurement>(),
                new OutputOverlaySet(overlay)), null!);
        Assert.Single(schemaIssues);
        Assert.Equal("AlgorithmResultSchemaRequired", schemaIssues[0].Code);
        _ = schema;
    }

    [Fact]
    public async Task V112_C12_MaximumOverlayTextContractMayBe65536ForAnEmptyOverlay()
    {
        var overlay = new OverlayContract("overlay", "1", maximumTextLength: 65_536);
        var schema = ResultSchema(overlay, Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>());
        var result = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlay));

        Assert.Empty(AlgorithmResultValidator.Validate(result, schema));
        var outcome = await ExecuteThroughRuntimeAsync(result, schema);
        Assert.Equal(ExecutionStatus.Success, outcome.ExecutionStatus);
        Assert.Equal(InspectionDecision.Pass, outcome.Decision);
    }

    [Fact]
    public async Task V112_C13_OverlayTextUsesCharacterLimitAndAllows1500ChineseCharacters()
    {
        var overlay = new OverlayContract("overlay", "1", maximumTextLength: 4096);
        var schema = ResultSchema(overlay, Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>());
        var text = new OverlayText(new OverlayPoint(0, 0), new string('界', 1500));
        var result = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlay,
                new OverlayPrimitive[] { text }));

        Assert.Empty(AlgorithmResultValidator.Validate(result, schema));
        var outcome = await ExecuteThroughRuntimeAsync(result, schema);
        Assert.Equal(ExecutionStatus.Success, outcome.ExecutionStatus);
    }

    [Fact]
    public async Task V112_C14_TotalPointLimitStopsScanningFollowingOverlayElements()
    {
        var overlay = new OverlayContract("overlay", "1", maximumElements: 2,
            maximumTotalPoints: 1, maximumPointsPerElement: 1);
        var schema = ResultSchema(overlay, Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>());
        var laterMarker = new OverlayMarker(new OverlayPoint(0, 0), OverlayMarkerKind.Cross, 1);
        SetProperty(laterMarker, nameof(OverlayMarker.MarkerKind), (OverlayMarkerKind)99);
        var result = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlay,
                new OverlayPrimitive[]
                {
                    new OverlayLineSegment(new OverlayPoint(0, 0), new OverlayPoint(1, 1)),
                    laterMarker
                }));

        var outcome = await ExecuteThroughRuntimeAsync(result, schema);
        AssertContractViolation(outcome);
        var issues = AlgorithmResultValidator.Validate(result, schema);
        Assert.Contains(issues, issue => issue.Code == "AlgorithmResultOverlayTotalPointLimitExceeded");
        Assert.DoesNotContain(issues, issue => issue.Code == "AlgorithmResultOverlayEnumInvalid" &&
            issue.FieldKey == "overlay[1].markerKind");
    }

    [Fact]
    public async Task V112_C15_OverlayText4097AsciiPassesWhenContractAllows65536Characters()
    {
        var overlay = new OverlayContract("overlay", "1", maximumTextLength: 65_536);
        var schema = ResultSchema(overlay, Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>());
        var result = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlay,
                new OverlayPrimitive[] { new OverlayText(new OverlayPoint(0, 0), new string('A', 4097)) }));

        Assert.Empty(AlgorithmResultValidator.Validate(result, schema));
        var outcome = await ExecuteThroughRuntimeAsync(result, schema);
        Assert.Equal(ExecutionStatus.Success, outcome.ExecutionStatus);
    }

    [Fact]
    public async Task V112_C16_OverlayTextOverContractCharacterLimitIsRejected()
    {
        var overlay = new OverlayContract("overlay", "1", maximumTextLength: 4096);
        var schema = ResultSchema(overlay, Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>());
        var result = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlay,
                new OverlayPrimitive[] { new OverlayText(new OverlayPoint(0, 0), new string('A', 4097)) }));

        var outcome = await ExecuteThroughRuntimeAsync(result, schema);
        AssertContractViolation(outcome);
        Assert.Contains(AlgorithmResultValidator.Validate(result, schema),
            issue => issue.Code == "AlgorithmResultOverlayTextUnsafe" &&
                issue.FieldKey == "overlay[0].text");
    }

    [Fact]
    public async Task V112_C17_OverlayTextAllowsEmojiAndExtensionHanCharacters()
    {
        var overlay = new OverlayContract("overlay", "1", maximumTextLength: 64);
        var schema = ResultSchema(overlay, Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>());
        var result = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlay,
                new OverlayPrimitive[] { new OverlayText(new OverlayPoint(0, 0), "良品🙂𠮷标签") }));

        Assert.Empty(AlgorithmResultValidator.Validate(result, schema));
        var outcome = await ExecuteThroughRuntimeAsync(result, schema);
        Assert.Equal(ExecutionStatus.Success, outcome.ExecutionStatus);
    }

    [Fact]
    public async Task V112_C18_OverlayTextRejectsIsolatedSurrogateWhileBidiControlsRemainRejected()
    {
        var overlay = new OverlayContract("overlay", "1", maximumTextLength: 64);
        var schema = ResultSchema(overlay, Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>());
        var isolatedSurrogate = new OverlayText(new OverlayPoint(0, 0), "placeholder");
        SetProperty(isolatedSurrogate, nameof(OverlayText.Text), "bad\uD800");
        var result = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlay,
                new OverlayPrimitive[] { isolatedSurrogate }));

        var outcome = await ExecuteThroughRuntimeAsync(result, schema);
        AssertContractViolation(outcome);
        Assert.Contains(AlgorithmResultValidator.Validate(result, schema),
            issue => issue.Code == "AlgorithmResultOverlayTextUnsafe" &&
                issue.FieldKey == "overlay[0].text");

        var bidi = new OverlayText(new OverlayPoint(0, 0), "正常\u202E标签");
        var bidiResult = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlay,
                new OverlayPrimitive[] { bidi }));
        AssertContractViolation(await ExecuteThroughRuntimeAsync(bidiResult, schema));
    }

    private static void AssertContractViolation(AlgorithmExecutionOutcome outcome)
    {
        Assert.Equal(ExecutionStatus.Error, outcome.ExecutionStatus);
        Assert.Equal("AlgorithmResultContractViolation", outcome.ReasonCode);
        Assert.Equal(InspectionDecision.Unknown, outcome.Decision);
        Assert.Null(outcome.ValidatedResult);
    }

    private static AlgorithmResultSchema ResultSchema(OverlayContract overlay,
        IEnumerable<AlgorithmFieldDefinition> fields, IEnumerable<string> reasons) =>
        new("result", "1", fields, reasons, overlay);

    private static AlgorithmFieldDefinition Field(string key, AlgorithmScalarType type,
        string unit, bool required, AlgorithmScalarConstraints? constraints = null) =>
        new(key, type, unit, required, constraints);

    private static async Task<AlgorithmExecutionOutcome> ExecuteThroughRuntimeAsync(
        AlgorithmResult? result, AlgorithmResultSchema resultSchema)
    {
        var configurationSchema = new AlgorithmConfigurationSchema("config", "1", new[]
        {
            new AlgorithmFieldDefinition("threshold", AlgorithmScalarType.Int64, "count", true)
        });
        var configuration = AlgorithmConfigurationSnapshot.Create(configurationSchema, new[]
        {
            new AlgorithmConfigurationEntry("threshold", "count", AlgorithmScalarValue.FromInt64(1))
        });
        var descriptor = new AlgorithmDescriptor(new AlgorithmIdentity("algorithm", "1"),
            configurationSchema, resultSchema);
        var factory = new ReturningFactory(descriptor, result);
        await using var preparation = new AlgorithmPreparationService(new[] { factory },
            new AlgorithmPreparationOptions(TimeSpan.FromSeconds(2)));
        var overlay = resultSchema.OverlayContract;
        var request = new AlgorithmPreparationRequest(descriptor.Identity, configuration,
            resultSchema.Id, resultSchema.Version, resultSchema.ContentHash,
            overlay.Id, overlay.Version, overlay.ContentHash, TimeSpan.FromSeconds(1));
        var preparedResult = await preparation.PrepareAsync(request);
        Assert.True(preparedResult.Succeeded, preparedResult.ReasonCode);
        var prepared = Assert.IsType<PreparedAlgorithm>(preparedResult.Prepared);

        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 64, TimeSpan.FromSeconds(1)));
        var frameInput = FrameInput();
        var copied = pool.TryCopyFrame(frameInput.Metadata, frameInput.Provenance, new byte[] { 1 });
        Assert.True(copied.Succeeded, copied.ReasonCode);
        var lease = Assert.IsType<FrameBufferLease>(copied.Lease);
        await using var execution = new AlgorithmExecutionService(
            new AlgorithmExecutionOptions(new AlgorithmExecutionPolicy("Test.ResultValidation", "v1",
                TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)),
                TimeSpan.FromSeconds(2)));
        var attempt = await execution.ExecuteAsync(prepared, lease,
            new AlgorithmExecutionRequest(new RecipeReference("result-validation-recipe", "1",
                new string('a', 64)), TimeSpan.FromSeconds(1)));
        var outcome = Assert.IsType<AlgorithmExecutionOutcome>(attempt.Outcome);
        await prepared.DisposeAsync();
        return outcome;
    }

    private static (FrameMetadata Metadata, FrameProvenance Provenance) FrameInput()
    {
        var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
        var camera = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            1000, 0, new RegionOfInterest(0, 0, 1, 1), VisionPixelFormat.Mono8, null, 1000, 0, null);
        var metadata = new FrameMetadata(correlation, "camera.primary", 1, 1, 1,
            VisionPixelFormat.Mono8, null, DateTimeOffset.UtcNow, camera);
        var milestones = new FrameAcquisitionMilestones(1, null, null, null, null);
        var provenance = new FrameProvenance(correlation, "test.provider", "1", "test.adapter", "1",
            "test.sdk", "1", null, "test-device", null, null, "Mono8", "test", true, false,
            null, null, milestones);
        return (metadata, provenance);
    }

    private static void SetProperty(object target, string propertyName, object? value)
    {
        var field = target.GetType().GetField("<" + propertyName + ">k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private sealed class ReturningFactory : IVisionAlgorithmFactory
    {
        private readonly AlgorithmResult? _result;

        public ReturningFactory(AlgorithmDescriptor descriptor, AlgorithmResult? result)
        {
            Descriptor = descriptor;
            _result = result;
        }

        public AlgorithmDescriptor Descriptor { get; }

        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(
                Array.Empty<AlgorithmValidationIssue>());

        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IVisionAlgorithm>(new ReturningAlgorithm(_result));
    }

    private sealed class ReturningAlgorithm : IVisionAlgorithm
    {
        private readonly AlgorithmResult? _result;

        public ReturningAlgorithm(AlgorithmResult? result) => _result = result;

        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_result!);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
