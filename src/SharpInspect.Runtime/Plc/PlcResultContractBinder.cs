using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Plc;

/// <summary>
/// Fixed framework validator for complete schema domains and explicit PLC register layouts.
/// A binding proves encodability for review; it does not authorize a deployment change or
/// assert that the supplied Recipe is released. Publication must separately bind the committed
/// deployment revision, current Recipe and durable result through the production authority.
/// </summary>
public sealed class PlcResultContractBinder
{
    public PlcResultBindingResult Bind(RecipeReference recipe, AlgorithmIdentity algorithm,
        AlgorithmResultSchema schema, PlcResultContract contract)
    {
        var validation = ValidateSchema(contract, schema);
        if (!validation.Valid || validation.Validation is null)
            return new(false, validation.ReasonCode, null, validation.Checks);
        try
        {
            var binding = new PlcResultContractBinding(recipe, algorithm, validation.Validation);
            return new(true, "PlcResultContractBound", binding, validation.Checks);
        }
        catch (ArgumentException)
        { return new(false, "PlcResultContractBindingIdentityInvalid", null, validation.Checks); }
    }

    public PlcResultSchemaValidationResult ValidateSchema(PlcResultContract contract, AlgorithmResultSchema schema)
    {
        var checks = new List<PlcResultValidationCheck>();
        bool Check(string id, string subject, bool passed, string reason)
        {
            checks.Add(new(id, subject, passed, passed ? "PlcResultInvariantSatisfied" : reason));
            return passed;
        }
        PlcResultSchemaValidationResult Result()
        {
            var failed = checks.FirstOrDefault(value => !value.Passed);
            var copied = checks.AsReadOnly();
            return failed is not null ? new(false, failed.ReasonCode, null, copied) :
                new(true, "PlcResultSchemaValidated", new(contract!, schema!, copied), copied);
        }
        if (contract is null || schema is null)
        {
            Check("V131.B01", "contract-and-schema", false, "PlcResultContractAndSchemaRequired");
            return new(false, "PlcResultContractAndSchemaRequired", null, checks.AsReadOnly());
        }
        Check("V131.B01", "contract-and-schema", true, "PlcResultContractAndSchemaRequired");
        var reference = new RecipeContractReference(schema.Id, schema.Version, schema.ContentHash);
        var branches = contract.SchemaMaps.Where(value => value.ResultSchema == reference).ToArray();
        if (!Check("V131.B01", "schema-map", branches.Length == 1 &&
                contract.SchemaMaps.Select(value => (value.ResultSchema.Id, value.ResultSchema.Version)).Distinct().Count() == contract.SchemaMaps.Count,
                "PlcResultContractSchemaMapMissingOrDuplicate")) return Result();
        var branch = branches[0];
        if (!Check("V131.B02", "framework-fields", contract.FrameworkFields.Count == 5 &&
                Enum.GetValues<PlcFrameworkResultField>().All(field => contract.FrameworkFields.Count(value => value.Field == field) == 1),
                "PlcResultFrameworkMappingIncomplete")) return Result();

        var ranges = new List<(string Subject, PlcRegisterRange Range)>();
        bool Wire(string subject, PlcRegisterRange? range, PlcWireEncoding? encoding)
        {
            var valid = range is not null && encoding is not null &&
                range.RegisterCount == PlcNumericEncoding.RegisterWidth(encoding.Representation) &&
                (range.RegisterCount == 1 ? encoding.WordOrder == PlcWordOrder.NotApplicable :
                    encoding.WordOrder is PlcWordOrder.HighWordFirst or PlcWordOrder.LowWordFirst) &&
                encoding.Overflow == PlcOverflowBehavior.EncodingFault;
            if (!Check("V131.B03", subject, valid, "PlcResultWireWidthOrOrderInvalid")) return false;
            ranges.Add((subject, range!));
            return true;
        }
        foreach (var field in contract.FrameworkFields)
        {
            var subject = $"framework.{field.Field}";
            if (!Wire(subject, field.RegisterRange, field.Encoding)) continue;
            var tableShape = field.Field switch
            {
                PlcFrameworkResultField.ExecutionStatus => field.InspectionDecisionCodes.Count == 0 && field.ReasonCodes.Count == 0,
                PlcFrameworkResultField.InspectionDecision => field.ExecutionStatusCodes.Count == 0 && field.ReasonCodes.Count == 0,
                PlcFrameworkResultField.ResultReasonCode => field.ExecutionStatusCodes.Count == 0 && field.InspectionDecisionCodes.Count == 0,
                _ => field.ExecutionStatusCodes.Count == 0 && field.InspectionDecisionCodes.Count == 0 && field.ReasonCodes.Count == 0
            };
            Check("V131.B02", subject, tableShape, "PlcResultFrameworkCodeTableTypeInvalid");
            if (field.Field is PlcFrameworkResultField.ControllerEpoch or PlcFrameworkResultField.ResultSequence)
            {
                Check("V131.B02", subject, field.Encoding.Representation == PlcWireRepresentation.UInt32,
                    "PlcResultControllerIdentityMustBeUInt32");
                continue;
            }
            var integerWire = IsInteger(field.Encoding.Representation);
            Check("V131.B04", subject, integerWire, "PlcResultCodeTableRequiresIntegerWire");
            if (!integerWire) continue;
            if (field.Field == PlcFrameworkResultField.ExecutionStatus)
            {
                Check("V131.B04", subject, field.ExecutionStatusCodes.Count == Enum.GetValues<ExecutionStatus>().Length &&
                    Enum.GetValues<ExecutionStatus>().All(value => field.ExecutionStatusCodes.Count(code => code.Value == value) == 1),
                    "PlcResultExecutionStatusCoverageInvalid");
                ValidateCodes(field.ExecutionStatusCodes.Select(value => value.WireCode), field.Encoding, subject, checks);
            }
            else if (field.Field == PlcFrameworkResultField.InspectionDecision)
            {
                Check("V131.B04", subject, field.InspectionDecisionCodes.Count == Enum.GetValues<InspectionDecision>().Length &&
                    Enum.GetValues<InspectionDecision>().All(value => field.InspectionDecisionCodes.Count(code => code.Value == value) == 1),
                    "PlcResultInspectionDecisionCoverageInvalid");
                ValidateCodes(field.InspectionDecisionCodes.Select(value => value.WireCode), field.Encoding, subject, checks);
            }
            else
            {
                var required = PlcResultContract.FrameworkReasonCodes.Concat(schema.ReasonCodes).Append(null).Distinct(StringComparer.Ordinal);
                var declared = field.ReasonCodes.ToLookup(value => value.ReasonCode, StringComparer.Ordinal);
                Check("V131.B04", subject, required.All(value => declared[value].Count() == 1) && declared.Count == field.ReasonCodes.Count,
                    "PlcResultReasonCodeCoverageInvalid");
                ValidateCodes(field.ReasonCodes.Select(value => value.WireCode), field.Encoding, subject, checks);
            }
        }

        if (!Check("V131.B05", "measurements", branch.Measurements.Count == schema.Measurements.Count &&
                schema.Measurements.All(field => branch.Measurements.Count(value => value.FieldKey == field.Key) == 1),
                "PlcResultMeasurementDispositionIncomplete")) return Result();
        foreach (var definition in schema.Measurements.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            var mapping = branch.Measurements.Single(value => value.FieldKey == definition.Key);
            var subject = $"measurement.{definition.Key}";
            if (mapping.Disposition == PlcMeasurementDisposition.Excluded)
            {
                Check("V131.B05", subject, mapping.RegisterRange is null && mapping.Encoding is null && mapping.Conversion is null &&
                    mapping.CodeTable.Count == 0 && mapping.OptionalAbsence is null && mapping.NonSuccessData is null,
                    "PlcResultExcludedFieldHasMapping");
                continue;
            }
            if (!Wire(subject, mapping.RegisterRange, mapping.Encoding)) continue;
            var encoding = mapping.Encoding!;
            Check("V131.B06", subject, mapping.Conversion?.SourceUnit == definition.Unit, "PlcResultSourceUnitMismatch");
            Check("V131.B07", subject, LiteralFits(mapping.NonSuccessData, mapping.RegisterRange!), "PlcResultNonSuccessDataRequired");
            if (definition.Type is AlgorithmScalarType.Int64 or AlgorithmScalarType.Float64)
            {
                Check("V131.B06", subject, mapping.CodeTable.Count == 0, "PlcResultNumericCodeTableForbidden");
                var domain = PlcNumericEncoding.TryValidateDomain(definition, mapping, out var domainReason);
                Check("V131.B06", subject, domain, domainReason);
            }
            else ValidateScalarDomain(definition, mapping, checks);

            if (definition.Required)
                Check("V131.B07", subject, mapping.OptionalAbsence is null, "PlcResultRequiredFieldAbsenceForbidden");
            else if (Check("V131.B07", subject, mapping.OptionalAbsence is not null, "PlcResultOptionalAbsenceRequired"))
            {
                var absence = mapping.OptionalAbsence!;
                if (absence.Kind == PlcOptionalAbsenceKind.Sentinel)
                {
                    Check("V131.B07", subject, LiteralFits(absence.Sentinel, mapping.RegisterRange!), "PlcResultSentinelWidthInvalid");
                    if (definition.Type is AlgorithmScalarType.Int64 or AlgorithmScalarType.Float64)
                    {
                        var safe = PlcNumericEncoding.TryValidateSentinel(definition, mapping, absence.Sentinel!, out var reason);
                        Check("V131.B07", subject, safe, reason);
                    }
                    else
                    {
                        var safe = absence.Sentinel is not null && mapping.CodeTable.All(code =>
                            PlcNumericEncoding.TryEncodeCode(code.WireCode, encoding, out var bytes, out _) &&
                            !bytes.SequenceEqual(absence.Sentinel.Bytes));
                        Check("V131.B07", subject, safe, "PlcResultSentinelCollision");
                    }
                }
                else if (absence.ValidityField is { } validity)
                {
                    Wire(subject + ".validity", validity.RegisterRange, validity.Encoding);
                    Check("V131.B07", subject, IsInteger(validity.Encoding.Representation) &&
                        LiteralFits(validity.PresentData, validity.RegisterRange) && LiteralFits(validity.AbsentData, validity.RegisterRange) &&
                        !validity.PresentData!.Equals(validity.AbsentData) && LiteralFits(absence.AbsentData, mapping.RegisterRange!),
                        "PlcResultValidityRepresentationInvalid");
                }
                else Check("V131.B07", subject, false, "PlcResultValidityFieldRequired");
            }
        }
        Check("V131.B08", "constants", branch.ConstantFields.Select(value => value.FieldKey).Distinct(StringComparer.Ordinal).Count() ==
            branch.ConstantFields.Count, "PlcResultConstantDuplicate");
        foreach (var constant in branch.ConstantFields)
        {
            Wire($"constant.{constant.FieldKey}", constant.RegisterRange, constant.Encoding);
            Check("V131.B08", $"constant.{constant.FieldKey}", LiteralFits(constant.Value, constant.RegisterRange), "PlcResultConstantWidthInvalid");
        }
        var ordered = ranges.OrderBy(value => value.Range.StartRegister).ToArray();
        Check("V131.B09", "register-layout", ordered.All(value => value.Range.EndRegisterExclusive <= contract.MaximumRegisterCount) &&
            ordered.Sum(value => (long)value.Range.RegisterCount * 2) <= contract.MaximumPayloadBytes,
            "PlcResultAddressOrPayloadCapacityExceeded");
        Check("V131.B09", "register-layout", !ordered.Skip(1).Where((value, index) =>
            ordered[index].Range.EndRegisterExclusive > value.Range.StartRegister).Any(), "PlcResultRegisterOverlap");
        return Result();
    }

    private static bool IsInteger(PlcWireRepresentation representation) => representation is not
        (PlcWireRepresentation.Ieee754Binary32 or PlcWireRepresentation.Ieee754Binary64);
    private static bool LiteralFits(PlcWireLiteral? literal, PlcRegisterRange range) => literal?.Length == range.RegisterCount * 2;

    private static void ValidateCodes(IEnumerable<long> source, PlcWireEncoding encoding, string subject,
        List<PlcResultValidationCheck> checks)
    {
        var codes = source.ToArray();
        var valid = codes.Distinct().Count() == codes.Length &&
            codes.All(value => PlcNumericEncoding.TryEncodeCode(value, encoding, out _, out _));
        checks.Add(new("V131.B04", subject, valid, valid ? "PlcResultInvariantSatisfied" : "PlcResultCodeCollisionOrOverflow"));
    }

    private static void ValidateScalarDomain(AlgorithmFieldDefinition definition, PlcMeasurementMapping mapping,
        List<PlcResultValidationCheck> checks)
    {
        var subject = $"measurement.{definition.Key}";
        var domain = new List<AlgorithmScalarValue>();
        if (definition.Type == AlgorithmScalarType.Boolean)
        { domain.Add(AlgorithmScalarValue.FromBoolean(false)); domain.Add(AlgorithmScalarValue.FromBoolean(true)); }
        else if (definition.Constraints?.AllowedValues is { Count: > 0 } allowed)
        {
            foreach (var value in allowed)
            {
                var scalar = definition.Type == AlgorithmScalarType.String ? AlgorithmScalarValue.FromString(value) : AlgorithmScalarValue.FromEnum(value);
                if (definition.Constraints.TryValidate(scalar, definition.Type, out _)) domain.Add(scalar);
            }
        }
        var identityConversion = mapping.Conversion is { } conversion && conversion.Multiplier.Numerator == 1 &&
            conversion.Multiplier.Denominator == 1 && conversion.Offset.Numerator == 0;
        var valid = domain.Count > 0 && identityConversion && IsInteger(mapping.Encoding!.Representation) &&
            mapping.CodeTable.Count == domain.Count && domain.All(value => mapping.CodeTable.Count(code => code.Value.Equals(value)) == 1);
        checks.Add(new("V131.B06", subject, valid, valid ? "PlcResultInvariantSatisfied" : "PlcResultFiniteScalarDomainMappingRequired"));
        ValidateCodes(mapping.CodeTable.Select(value => value.WireCode), mapping.Encoding!, subject, checks);
    }
}
