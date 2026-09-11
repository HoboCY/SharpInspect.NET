using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;

namespace SharpInspect.Runtime.Plc;

/// <summary>
/// Framework-owned complete payload encoder. Register bytes are final PDU image bytes;
/// a later transport must preserve each address and byte without host-endian conversion.
/// No ResultValid or other PLC I/O occurs here.
/// </summary>
public sealed class PlcResultPayloadEncoder
{
    public PlcResultEncodingResult Encode(PlcResultContractBinding binding, PlcControllerCycle cycle,
        AlgorithmExecutionOutcome outcome)
    {
        PlcResultEncodingResult Fault(string detail) => new(false, "PlcResultEncodingFault", null, detail);
        if (binding is null || cycle is null || outcome is null) return Fault("PlcResultEncodingInputsRequired");
        var encoded = EncodeSegments(binding, cycle.ControllerEpoch, cycle.CycleSequence, outcome, ExecutionKind.Production);
        if (encoded.Segments is null) return Fault(encoded.Reason!);
        try
        {
            return new(true, "PlcResultPayloadEncoded", new(binding, outcome.Correlation.Value, cycle,
                outcome.ExecutionStatus, outcome.Decision, outcome.ReasonCode, encoded.Segments));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Fault("PlcResultCompleteEncodingFailed"); }
    }

    /// <summary>
    /// Encodes a typed production failure when acquisition produced no frame and therefore no
    /// algorithm outcome. The contract's non-success measurement bytes are used exactly as
    /// declared; no algorithm result, frame metadata, overlay or success evidence is fabricated.
    /// </summary>
    public PlcResultEncodingResult EncodeFailure(PlcResultContractBinding binding, Guid inspectionId,
        PlcControllerCycle cycle, ExecutionStatus status, string reasonCode)
    {
        PlcResultEncodingResult Fault(string detail) => new(false, "PlcResultEncodingFault", null, detail);
        if (binding is null || cycle is null || inspectionId == Guid.Empty ||
            status is not (ExecutionStatus.Error or ExecutionStatus.Timeout or ExecutionStatus.Cancelled) ||
            string.IsNullOrWhiteSpace(reasonCode))
            return Fault("PlcResultFailureEncodingInputsRequired");
        if (!binding.Contract.FrameworkReasonCatalog.Contains(reasonCode, StringComparer.Ordinal))
            return Fault("PlcResultFrameworkReasonCatalogMismatch");

        try
        {
            var encoded = EncodeFailureSegments(binding, cycle.ControllerEpoch, cycle.CycleSequence,
                status, reasonCode);
            if (encoded.Segments is null) return Fault(encoded.Reason!);
            return new(true, "PlcResultFailurePayloadEncoded", new(binding, inspectionId, cycle,
                status, InspectionDecision.Unknown, reasonCode, encoded.Segments));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Fault("PlcResultCompleteEncodingFailed"); }
    }

    /// <summary>Reviews real Manual results with sample controller values; returns no production snapshot.</summary>
    public PlcResultPreviewResult EncodePreview(PlcResultContractBinding binding, uint sampleControllerEpoch,
        uint sampleCycleSequence, AlgorithmExecutionOutcome outcome)
    {
        PlcResultPreviewResult Fault(string detail) => new(false, "PlcResultEncodingFault", null, detail);
        if (binding is null || outcome is null) return Fault("PlcResultEncodingInputsRequired");
        var encoded = EncodeSegments(binding, sampleControllerEpoch, sampleCycleSequence, outcome, ExecutionKind.Manual);
        if (encoded.Segments is null) return Fault(encoded.Reason!);
        try
        {
            return new(true, "PlcResultPayloadPreviewEncoded", new(binding, outcome.Correlation, sampleControllerEpoch,
                sampleCycleSequence, outcome.ExecutionStatus, outcome.Decision, outcome.ReasonCode, encoded.Segments));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Fault("PlcResultCompleteEncodingFailed"); }
    }

    private sealed record EncodingData(IReadOnlyList<PlcRegisterSegment>? Segments, string? Reason);

    private static EncodingData EncodeFailureSegments(PlcResultContractBinding binding,
        uint controllerEpoch, uint resultSequence, ExecutionStatus status, string reasonCode)
    {
        EncodingData Fault(string detail) => new(null, detail);
        var branch = binding.Contract.SchemaMaps.SingleOrDefault(value =>
            value.ResultSchema == new RecipeContractReference(binding.ResultSchema.Id,
                binding.ResultSchema.Version, binding.ResultSchema.ContentHash));
        if (branch is null) return Fault("PlcResultBindingSchemaMapMissing");

        try
        {
            var segments = new List<PlcRegisterSegment>();
            foreach (var field in binding.Contract.FrameworkFields)
            {
                long value;
                switch (field.Field)
                {
                    case PlcFrameworkResultField.ControllerEpoch: value = controllerEpoch; break;
                    case PlcFrameworkResultField.ResultSequence: value = resultSequence; break;
                    case PlcFrameworkResultField.ExecutionStatus:
                        value = field.ExecutionStatusCodes.Single(code => code.Value == status).WireCode;
                        break;
                    case PlcFrameworkResultField.InspectionDecision:
                        value = field.InspectionDecisionCodes.Single(code =>
                            code.Value == InspectionDecision.Unknown).WireCode;
                        break;
                    case PlcFrameworkResultField.ResultReasonCode:
                        var reason = field.ReasonCodes.SingleOrDefault(code =>
                            code.ReasonCode == reasonCode);
                        if (reason is null) return Fault("PlcResultReasonCodeUnmapped");
                        value = reason.WireCode;
                        break;
                    default: return Fault("PlcResultFrameworkFieldInvalid");
                }
                if (!PlcNumericEncoding.TryEncodeCode(value, field.Encoding, out var bytes, out var why))
                    return Fault(why);
                segments.Add(new(field.RegisterRange.StartRegister, bytes));
            }

            foreach (var mapping in branch.Measurements)
            {
                if (mapping.Disposition == PlcMeasurementDisposition.Excluded) continue;
                if (mapping.NonSuccessData is null) return Fault("PlcResultNonSuccessMappingMissing");
                var bytes = mapping.NonSuccessData.ToArray();
                if (bytes.Length != mapping.RegisterRange!.RegisterCount * 2)
                    return Fault("PlcResultEncodedFieldWidthMismatch");
                segments.Add(new(mapping.RegisterRange.StartRegister, bytes));
                if (mapping.OptionalAbsence?.ValidityField is { } validity)
                    segments.Add(new(validity.RegisterRange.StartRegister,
                        validity.AbsentData!.ToArray()));
            }
            foreach (var constant in branch.ConstantFields)
                segments.Add(new(constant.RegisterRange.StartRegister, constant.Value.ToArray()));

            if (segments.Sum(value => value.RegisterBytes.Count) > binding.Contract.MaximumPayloadBytes ||
                segments.Any(value => value.StartRegister + value.RegisterCount > binding.Contract.MaximumRegisterCount))
                return Fault("PlcResultPayloadCapacityExceeded");
            return new(segments.AsReadOnly(), null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Fault("PlcResultCompleteEncodingFailed"); }
    }

    internal (StationQualificationPayload? Payload, string ReasonCode) EncodeQualification(
        Guid sessionId, QualificationRunId runId, string contextHash, PlcResultContractBinding binding,
        uint controllerEpoch, uint cycleSequence, AlgorithmExecutionOutcome outcome)
    {
        if (sessionId == Guid.Empty || runId is null || binding is null || outcome is null ||
            outcome.Correlation.Kind != ExecutionKind.Qualification || outcome.Correlation.Value != runId.Value)
            return (null, "PlcResultQualificationCorrelationRequired");
        var encoded = EncodeSegments(binding, controllerEpoch, cycleSequence, outcome, ExecutionKind.Qualification);
        if (encoded.Segments is null) return (null, encoded.Reason ?? "PlcResultEncodingFault");
        try
        {
            return (new StationQualificationPayload(sessionId, runId, contextHash, binding,
                controllerEpoch, cycleSequence, outcome.ExecutionStatus, outcome.Decision,
                outcome.ReasonCode, encoded.Segments), "PlcResultQualificationPayloadEncoded");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return (null, "PlcResultCompleteEncodingFailed"); }
    }

    private static EncodingData EncodeSegments(PlcResultContractBinding binding, uint controllerEpoch, uint resultSequence,
        AlgorithmExecutionOutcome outcome, ExecutionKind requiredKind)
    {
        EncodingData Fault(string detail) => new(null, detail);
        if (outcome.Correlation.Kind != requiredKind || outcome.Correlation.Value == Guid.Empty)
            return Fault(requiredKind == ExecutionKind.Production ? "PlcResultProductionCorrelationRequired" :
                requiredKind == ExecutionKind.Qualification ? "PlcResultQualificationCorrelationRequired" :
                "PlcResultPreviewManualCorrelationRequired");
        if (binding.Recipe != outcome.Timing.Recipe || binding.Algorithm != outcome.Algorithm ||
            binding.ResultSchema.Id != outcome.ResultSchema.Id || binding.ResultSchema.Version != outcome.ResultSchema.Version ||
            binding.ResultSchema.ContentHash != outcome.ResultSchema.ContentHash)
            return Fault("PlcResultBindingOutcomeMismatch");
        if (!Enum.IsDefined(outcome.ExecutionStatus) || !Enum.IsDefined(outcome.Decision) ||
            (outcome.ExecutionStatus != ExecutionStatus.Success &&
                (outcome.Decision != InspectionDecision.Unknown || outcome.ValidatedResult is not null)))
            return Fault("PlcResultExecutionDecisionMismatch");
        if (outcome.ExecutionStatus != ExecutionStatus.Success &&
            (outcome.ReasonCode is null || !binding.Contract.FrameworkReasonCatalog.Contains(outcome.ReasonCode, StringComparer.Ordinal) &&
                !binding.ResultSchema.ReasonCodes.Contains(outcome.ReasonCode, StringComparer.Ordinal)))
            return Fault("PlcResultFrameworkReasonCatalogMismatch");
        if (outcome.ExecutionStatus == ExecutionStatus.Success &&
            (outcome.ValidatedResult is not { } result || result.ReasonCode != outcome.ReasonCode ||
                result.Decision != outcome.Decision || AlgorithmResultValidator.Validate(result, binding.ResultSchema).Count != 0))
            return Fault("PlcResultAlgorithmResultInvalid");

        try
        {
            var segments = new List<PlcRegisterSegment>();
            foreach (var field in binding.Contract.FrameworkFields)
            {
                long value;
                switch (field.Field)
                {
                    case PlcFrameworkResultField.ControllerEpoch: value = controllerEpoch; break;
                    case PlcFrameworkResultField.ResultSequence: value = resultSequence; break;
                    case PlcFrameworkResultField.ExecutionStatus:
                        value = field.ExecutionStatusCodes.Single(code => code.Value == outcome.ExecutionStatus).WireCode; break;
                    case PlcFrameworkResultField.InspectionDecision:
                        value = field.InspectionDecisionCodes.Single(code => code.Value == outcome.Decision).WireCode; break;
                    case PlcFrameworkResultField.ResultReasonCode:
                        var reason = field.ReasonCodes.SingleOrDefault(code => code.ReasonCode == outcome.ReasonCode);
                        if (reason is null) return Fault("PlcResultReasonCodeUnmapped");
                        value = reason.WireCode; break;
                    default: return Fault("PlcResultFrameworkFieldInvalid");
                }
                if (!PlcNumericEncoding.TryEncodeCode(value, field.Encoding, out var bytes, out var why)) return Fault(why);
                segments.Add(new(field.RegisterRange.StartRegister, bytes));
            }
            var reference = new RecipeContractReference(binding.ResultSchema.Id, binding.ResultSchema.Version, binding.ResultSchema.ContentHash);
            var branch = binding.Contract.SchemaMaps.Single(value => value.ResultSchema == reference);
            foreach (var mapping in branch.Measurements)
            {
                if (mapping.Disposition == PlcMeasurementDisposition.Excluded) continue;
                var measurement = outcome.ValidatedResult?.Measurements.SingleOrDefault(value => value.Key == mapping.FieldKey);
                byte[] bytes;
                if (outcome.ExecutionStatus != ExecutionStatus.Success)
                    bytes = mapping.NonSuccessData!.ToArray();
                else if (measurement is null)
                {
                    var absence = mapping.OptionalAbsence!;
                    bytes = (absence.Kind == PlcOptionalAbsenceKind.Sentinel ? absence.Sentinel! : absence.AbsentData!).ToArray();
                }
                else if (measurement.Value.Type is AlgorithmScalarType.Int64 or AlgorithmScalarType.Float64)
                {
                    if (!PlcNumericEncoding.TryEncode(measurement.Value, mapping.Conversion!, mapping.Encoding!, out bytes, out var why))
                        return Fault(why);
                }
                else
                {
                    var code = mapping.CodeTable.Single(value => value.Value.Equals(measurement.Value));
                    if (!PlcNumericEncoding.TryEncodeCode(code.WireCode, mapping.Encoding!, out bytes, out var why)) return Fault(why);
                }
                if (bytes.Length != mapping.RegisterRange!.RegisterCount * 2) return Fault("PlcResultEncodedFieldWidthMismatch");
                segments.Add(new(mapping.RegisterRange.StartRegister, bytes));
                if (mapping.OptionalAbsence?.ValidityField is { } validity)
                {
                    var present = outcome.ExecutionStatus == ExecutionStatus.Success && measurement is not null;
                    segments.Add(new(validity.RegisterRange.StartRegister,
                        (present ? validity.PresentData! : validity.AbsentData!).ToArray()));
                }
            }
            foreach (var constant in branch.ConstantFields)
                segments.Add(new(constant.RegisterRange.StartRegister, constant.Value.ToArray()));

            if (segments.Sum(value => value.RegisterBytes.Count) > binding.Contract.MaximumPayloadBytes ||
                segments.Any(value => value.StartRegister + value.RegisterCount > binding.Contract.MaximumRegisterCount))
                return Fault("PlcResultPayloadCapacityExceeded");
            return new(segments.AsReadOnly(), null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // No external diagnostic, partial segment or numeric value escapes a failed encoding attempt.
            return Fault("PlcResultCompleteEncodingFailed");
        }
    }
}
