using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Algorithms;

/// <summary>
/// Validates a complete algorithm return against the exact result schema selected by Runtime.
/// This validator is deliberately side-effect free: it never repairs, truncates, clamps, or
/// otherwise changes the consumer supplied result.
/// </summary>
internal static class AlgorithmResultValidator
{
    private const int MaximumIssues = 32;
    private const int MaximumMeasurements = 256;
    private const int MaximumOverlayElements = 4096;
    private const int MaximumOverlayPointsPerElement = 100_000;
    private const int MaximumOverlayTextLength = 65_536;
    private const int MaximumScalarTextBytes = 4096;

    internal static IReadOnlyList<AlgorithmValidationIssue> Validate(
        AlgorithmResult? result, AlgorithmResultSchema schema)
    {
        var issues = new IssueSink();
        if (result is null)
        {
            issues.Add("AlgorithmResultRequired");
            return issues.ToReadOnly();
        }

        if (schema is null)
        {
            issues.Add("AlgorithmResultSchemaRequired");
            return issues.ToReadOnly();
        }

        var schemaView = ReadSchema(schema, issues);
        ValidateDecision(result, schemaView.ReasonCodes, issues);
        ValidateMeasurements(result, schemaView.Measurements, issues);
        ValidateOverlay(result, schemaView.OverlayContract, issues);
        return issues.ToReadOnly();
    }

    private static SchemaView ReadSchema(AlgorithmResultSchema schema, IssueSink issues)
    {
        var measurements = new List<AlgorithmFieldDefinition>();
        var measurementKeys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var source = schema.Measurements;
            if (source is null)
            {
                issues.Add("AlgorithmResultSchemaMeasurementsRequired");
            }
            else
            {
                var index = 0;
                foreach (var definition in source)
                {
                    if (index == MaximumMeasurements)
                    {
                        issues.Add("AlgorithmResultSchemaMeasurementCapacityExceeded");
                        break;
                    }

                    index++;
                    if (definition is null)
                    {
                        issues.Add("AlgorithmResultSchemaMeasurementInvalid");
                        continue;
                    }

                    var key = definition.Key;
                    if (!IsIdentifier(key, 128))
                        issues.Add("AlgorithmResultSchemaMeasurementKeyInvalid", key);
                    else if (!measurementKeys.Add(key))
                        issues.Add("AlgorithmResultSchemaMeasurementDuplicate", key);

                    if (!Enum.IsDefined(typeof(AlgorithmScalarType), definition.Type))
                        issues.Add("AlgorithmResultSchemaMeasurementTypeInvalid", key);
                    if (!IsIdentifier(definition.Unit, 64))
                        issues.Add("AlgorithmResultSchemaMeasurementUnitInvalid", key);
                    if (definition.AuthoringDefault is not null)
                        issues.Add("AlgorithmResultSchemaDefaultsForbidden", key);
                    ValidateConstraintShape(definition, key, issues);
                    measurements.Add(definition);
                }
            }
        }
        catch (Exception exception) when (IsValidationException(exception))
        {
            issues.Add("AlgorithmResultSchemaMeasurementsInvalid");
        }

        var reasonCodes = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var source = schema.ReasonCodes;
            if (source is null)
            {
                issues.Add("AlgorithmResultSchemaReasonCodesRequired");
            }
            else
            {
                var index = 0;
                foreach (var reason in source)
                {
                    if (index == MaximumMeasurements)
                    {
                        issues.Add("AlgorithmResultSchemaReasonCodeCapacityExceeded");
                        break;
                    }

                    index++;
                    if (!IsIdentifier(reason, 128))
                    {
                        issues.Add("AlgorithmResultSchemaReasonCodeInvalid");
                        continue;
                    }

                    if (!reasonCodes.Add(reason))
                        issues.Add("AlgorithmResultSchemaReasonCodeDuplicate", reason);
                }
            }
        }
        catch (Exception exception) when (IsValidationException(exception))
        {
            issues.Add("AlgorithmResultSchemaReasonCodesInvalid");
        }

        OverlayContract? overlayContract = null;
        try
        {
            overlayContract = schema.OverlayContract;
            if (overlayContract is null)
            {
                issues.Add("AlgorithmResultSchemaOverlayContractRequired");
            }
            else
            {
                ValidateOverlayContract(overlayContract, issues);
            }
        }
        catch (Exception exception) when (IsValidationException(exception))
        {
            issues.Add("AlgorithmResultSchemaOverlayContractInvalid");
        }

        return new SchemaView(measurements, reasonCodes, overlayContract);
    }

    private static void ValidateDecision(AlgorithmResult result, IReadOnlySet<string> reasonCodes,
        IssueSink issues)
    {
        InspectionDecision decision;
        try
        {
            decision = result.Decision;
        }
        catch (Exception exception) when (IsValidationException(exception))
        {
            issues.Add("AlgorithmResultDecisionInvalid");
            return;
        }

        if (!Enum.IsDefined(typeof(InspectionDecision), decision))
            issues.Add("AlgorithmResultDecisionInvalid");

        string? reasonCode;
        try
        {
            reasonCode = result.ReasonCode;
        }
        catch (Exception exception) when (IsValidationException(exception))
        {
            issues.Add("AlgorithmResultReasonCodeInvalid");
            return;
        }

        if (decision == InspectionDecision.Unknown && string.IsNullOrWhiteSpace(reasonCode))
            issues.Add("AlgorithmResultReasonCodeRequired");
        if (reasonCode is not null && !IsIdentifier(reasonCode, 128))
            issues.Add("AlgorithmResultReasonCodeInvalid");
        if (reasonCode is not null && !reasonCodes.Contains(reasonCode))
            issues.Add("AlgorithmResultReasonCodeUnknown", reasonCode);
    }

    private static void ValidateMeasurements(AlgorithmResult result,
        IReadOnlyList<AlgorithmFieldDefinition> definitions, IssueSink issues)
    {
        ReadOnlyCollection<AlgorithmMeasurement>? measurements;
        try
        {
            measurements = result.Measurements;
        }
        catch (Exception exception) when (IsValidationException(exception))
        {
            issues.Add("AlgorithmResultMeasurementsInvalid");
            return;
        }

        if (measurements is null)
        {
            issues.Add("AlgorithmResultMeasurementsRequired");
            return;
        }

        var definitionByKey = new Dictionary<string, AlgorithmFieldDefinition>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (definition is null || !IsIdentifier(definition.Key, 128)) continue;
            definitionByKey.TryAdd(definition.Key, definition);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var measurement in measurements)
        {
            if (count == MaximumMeasurements)
            {
                issues.Add("AlgorithmResultMeasurementCapacityExceeded");
                break;
            }

            var index = count++;
            if (measurement is null)
            {
                issues.Add("AlgorithmResultMeasurementInvalid", MeasurementPath(index));
                continue;
            }

            string? key;
            try { key = measurement.Key; }
            catch (Exception exception) when (IsValidationException(exception))
            {
                issues.Add("AlgorithmResultMeasurementInvalid", MeasurementPath(index));
                continue;
            }

            if (!IsIdentifier(key, 128))
            {
                issues.Add("AlgorithmResultMeasurementKeyInvalid", key ?? MeasurementPath(index));
                continue;
            }

            if (!seen.Add(key))
                issues.Add("AlgorithmResultMeasurementDuplicate", key);
            if (!definitionByKey.TryGetValue(key, out var definition))
            {
                issues.Add("AlgorithmResultMeasurementUnknown", key);
                continue;
            }

            string? unit;
            try { unit = measurement.Unit; }
            catch (Exception exception) when (IsValidationException(exception))
            {
                issues.Add("AlgorithmResultMeasurementUnitInvalid", key);
                unit = null;
            }

            if (!string.Equals(unit, definition.Unit, StringComparison.Ordinal))
                issues.Add("AlgorithmResultMeasurementUnitMismatch", key);

            AlgorithmScalarValue? value;
            try { value = measurement.Value; }
            catch (Exception exception) when (IsValidationException(exception))
            {
                issues.Add("AlgorithmResultMeasurementValueInvalid", key);
                continue;
            }

            if (value is null)
            {
                issues.Add("AlgorithmResultMeasurementValueRequired", key);
                continue;
            }

            ValidateMeasurementValue(value, definition, key, issues);
        }

        var present = seen;
        foreach (var definition in definitions)
        {
            if (issues.IsFull || definition is null || !definition.Required ||
                !IsIdentifier(definition.Key, 128)) continue;
            if (!present.Contains(definition.Key))
                issues.Add("AlgorithmResultMeasurementMissingRequired", definition.Key);
        }
    }

    private static void ValidateMeasurementValue(AlgorithmScalarValue value,
        AlgorithmFieldDefinition definition, string fieldKey, IssueSink issues)
    {
        AlgorithmScalarType actualType;
        try { actualType = value.Type; }
        catch (Exception exception) when (IsValidationException(exception))
        {
            issues.Add("AlgorithmResultMeasurementValueInvalid", fieldKey);
            return;
        }

        if (!Enum.IsDefined(typeof(AlgorithmScalarType), actualType))
        {
            issues.Add("AlgorithmResultMeasurementTypeInvalid", fieldKey);
            return;
        }

        if (actualType != definition.Type)
        {
            issues.Add("AlgorithmResultMeasurementTypeMismatch", fieldKey);
            return;
        }

        try
        {
            switch (actualType)
            {
                case AlgorithmScalarType.Boolean:
                    _ = value.AsBoolean();
                    break;
                case AlgorithmScalarType.Int64:
                    _ = value.AsInt64();
                    break;
                case AlgorithmScalarType.Float64:
                    var floating = value.AsFloat64();
                    if (!double.IsFinite(floating))
                        issues.Add("AlgorithmResultMeasurementNonFinite", fieldKey);
                    break;
                case AlgorithmScalarType.String:
                    ValidateScalarText(value.AsString(), fieldKey, issues);
                    break;
                case AlgorithmScalarType.Enum:
                    ValidateScalarText(value.AsEnum(), fieldKey, issues);
                    break;
            }
        }
        catch (Exception exception) when (IsValidationException(exception))
        {
            issues.Add("AlgorithmResultMeasurementValueInvalid", fieldKey);
            return;
        }

        if (issues.IsFull || definition.Constraints is null) return;
        try
        {
            if (!definition.Constraints.TryValidate(value, definition.Type, out _))
                issues.Add("AlgorithmResultMeasurementConstraintViolation", fieldKey);
        }
        catch (Exception exception) when (IsValidationException(exception))
        {
            issues.Add("AlgorithmResultMeasurementConstraintInvalid", fieldKey);
        }
    }

    private static void ValidateScalarText(string? value, string fieldKey, IssueSink issues)
    {
        if (value is null || value.Length > MaximumScalarTextBytes)
        {
            issues.Add("AlgorithmResultMeasurementValueInvalid", fieldKey);
            return;
        }

        try
        {
            var bytes = new UTF8Encoding(false, true).GetBytes(value);
            if (bytes.Length > MaximumScalarTextBytes)
                issues.Add("AlgorithmResultMeasurementValueInvalid", fieldKey);
        }
        catch (EncoderFallbackException)
        {
            issues.Add("AlgorithmResultMeasurementValueInvalid", fieldKey);
        }
    }

    private static void ValidateOverlay(AlgorithmResult result, OverlayContract? contract,
        IssueSink issues)
    {
        OutputOverlaySet? overlaySet;
        try { overlaySet = result.OverlaySet; }
        catch (Exception exception) when (IsValidationException(exception))
        {
            issues.Add("AlgorithmResultOverlayInvalid");
            return;
        }

        if (overlaySet is null)
        {
            issues.Add("AlgorithmResultOverlayRequired");
            return;
        }

        if (contract is null)
        {
            issues.Add("AlgorithmResultOverlaySchemaUnavailable");
            return;
        }

        if (!string.Equals(overlaySet.ContractId, contract.Id, StringComparison.Ordinal))
            issues.Add("AlgorithmResultOverlayContractIdMismatch");
        if (!string.Equals(overlaySet.ContractVersion, contract.Version, StringComparison.Ordinal))
            issues.Add("AlgorithmResultOverlayContractVersionMismatch");

        ReadOnlyCollection<OverlayPrimitive>? primitives;
        try { primitives = overlaySet.Primitives; }
        catch (Exception exception) when (IsValidationException(exception))
        {
            issues.Add("AlgorithmResultOverlayElementsInvalid");
            return;
        }

        if (primitives is null)
        {
            issues.Add("AlgorithmResultOverlayElementsRequired");
            return;
        }

        var maximumElements = ValidMaximum(contract.MaximumElements, MaximumOverlayElements,
            "AlgorithmResultOverlayContractInvalid", issues);
        var maximumPoints = ValidMaximum(contract.MaximumPointsPerElement,
            MaximumOverlayPointsPerElement, "AlgorithmResultOverlayContractInvalid", issues);
        var maximumTotalPoints = ValidMaximum(contract.MaximumTotalPoints, 1_000_000,
            "AlgorithmResultOverlayContractInvalid", issues);
        var maximumTextLength = ValidMaximum(contract.MaximumTextLength, 65_536,
            "AlgorithmResultOverlayContractInvalid", issues);

        if (primitives.Count > maximumElements)
            issues.Add("AlgorithmResultOverlayElementLimitExceeded");
        if (primitives.Count > MaximumOverlayElements)
            issues.Add("AlgorithmResultOverlayElementCapacityExceeded");

        long totalPoints = 0;
        var count = Math.Min(primitives.Count, MaximumOverlayElements);
        for (var index = 0; index < count && !issues.IsFull; index++)
        {
            var primitive = primitives[index];
            var pointCount = ValidatePrimitive(primitive, index, maximumPoints, maximumTextLength, issues);
            if (pointCount > maximumPoints)
                issues.Add("AlgorithmResultOverlayPointLimitExceeded", OverlayPath(index));
            totalPoints = checked(totalPoints + Math.Max(0, pointCount));
            if (totalPoints > maximumTotalPoints)
            {
                issues.Add("AlgorithmResultOverlayTotalPointLimitExceeded");
                break;
            }
        }
    }

    private static int ValidMaximum(int value, int hardMaximum, string issueCode, IssueSink issues)
    {
        if (value < 0 || value > hardMaximum)
        {
            issues.Add(issueCode);
            return Math.Clamp(value, 0, hardMaximum);
        }

        return value;
    }

    private static int ValidatePrimitive(OverlayPrimitive? primitive, int index,
        int maximumPoints, int maximumTextLength, IssueSink issues)
    {
        var path = OverlayPath(index);
        if (primitive is null)
        {
            issues.Add("AlgorithmResultOverlayElementRequired", path);
            return 0;
        }

        if (primitive is not OverlayMarker && primitive is not OverlayLineSegment &&
            primitive is not OverlayArrow && primitive is not OverlayPolyline &&
            primitive is not OverlayPolygon && primitive is not OverlayAxisAlignedRectangle &&
            primitive is not OverlayRotatedRectangle && primitive is not OverlayCircle &&
            primitive is not OverlayEllipse && primitive is not OverlayText)
        {
            issues.Add("AlgorithmResultOverlayPrimitiveUnknown", path);
            return 0;
        }

        var style = primitive.Style;
        ValidateStyle(style, path, issues);
        if (style?.FillColor is not null && !SupportsFill(primitive))
            issues.Add("AlgorithmResultOverlayFillUnsupported", path + ".style.fillColor");
        switch (primitive)
        {
            case OverlayMarker marker:
                ValidatePoint(marker.Center, path + ".center", issues);
                ValidateEnum(marker.MarkerKind, path + ".markerKind", issues);
                ValidatePositive(marker.Size, 4096, path + ".size", issues);
                return 1;
            case OverlayLineSegment line:
                ValidatePoint(line.Start, path + ".start", issues);
                ValidatePoint(line.End, path + ".end", issues);
                return 2;
            case OverlayArrow arrow:
                ValidatePoint(arrow.Start, path + ".start", issues);
                ValidatePoint(arrow.End, path + ".end", issues);
                return 2;
            case OverlayPolyline polyline:
                return ValidatePoints(polyline.Points, 2, path, issues);
            case OverlayPolygon polygon:
                return ValidatePoints(polygon.Points, 3, path, issues);
            case OverlayAxisAlignedRectangle rectangle:
                ValidatePoint(rectangle.TopLeft, path + ".topLeft", issues);
                ValidatePositive(rectangle.Width, 0, path + ".width", issues);
                ValidatePositive(rectangle.Height, 0, path + ".height", issues);
                return 4;
            case OverlayRotatedRectangle rotated:
                ValidatePoint(rotated.Center, path + ".center", issues);
                ValidatePositive(rotated.Width, 0, path + ".width", issues);
                ValidatePositive(rotated.Height, 0, path + ".height", issues);
                ValidateFinite(rotated.RotationDegrees, path + ".rotation", issues);
                return 4;
            case OverlayCircle circle:
                ValidatePoint(circle.Center, path + ".center", issues);
                ValidatePositive(circle.Radius, 0, path + ".radius", issues);
                return 1;
            case OverlayEllipse ellipse:
                ValidatePoint(ellipse.Center, path + ".center", issues);
                ValidatePositive(ellipse.RadiusX, 0, path + ".radiusX", issues);
                ValidatePositive(ellipse.RadiusY, 0, path + ".radiusY", issues);
                ValidateFinite(ellipse.RotationDegrees, path + ".rotation", issues);
                return 1;
            case OverlayText text:
                ValidatePoint(text.Anchor, path + ".anchor", issues);
                ValidateEnum(text.AnchorKind, path + ".anchorKind", issues);
                ValidateText(text.Text, maximumTextLength, path + ".text", issues);
                return 1;
            default:
                issues.Add("AlgorithmResultOverlayPrimitiveUnknown", path);
                return 0;
        }
    }

    private static int ValidatePoints(ReadOnlyCollection<OverlayPoint>? points, int minimum,
        string path, IssueSink issues)
    {
        if (points is null)
        {
            issues.Add("AlgorithmResultOverlayPointsRequired", path);
            return 0;
        }

        if (points.Count < minimum)
            issues.Add("AlgorithmResultOverlayPointCountInvalid", path);
        if (points.Count > MaximumOverlayPointsPerElement)
            issues.Add("AlgorithmResultOverlayPointCapacityExceeded", path);

        var count = Math.Min(points.Count, MaximumOverlayPointsPerElement);
        for (var index = 0; index < count && !issues.IsFull; index++)
            ValidatePoint(points[index], path + ".points[" + index + "]", issues);
        return points.Count;
    }

    private static void ValidateStyle(OverlayStyle? style, string path, IssueSink issues)
    {
        if (style is null)
        {
            issues.Add("AlgorithmResultOverlayStyleRequired", path + ".style");
            return;
        }

        ValidatePositive(style.StrokeWidth, 1024, path + ".style.strokeWidth", issues);
        ValidatePositive(style.MarkerSize, 4096, path + ".style.markerSize", issues);
        ValidatePositive(style.TextSize, 4096, path + ".style.textSize", issues);
        ValidateEnum(style.StrokePattern, path + ".style.strokePattern", issues);
        ValidateEnum(style.TextAnchor, path + ".style.textAnchor", issues);
    }

    private static bool SupportsFill(OverlayPrimitive primitive) => primitive switch
    {
        OverlayMarker marker => marker.MarkerKind is OverlayMarkerKind.Circle or OverlayMarkerKind.Square,
        OverlayPolygon or OverlayAxisAlignedRectangle or OverlayRotatedRectangle or
            OverlayCircle or OverlayEllipse => true,
        _ => false
    };

    private static void ValidatePoint(OverlayPoint point, string path, IssueSink issues)
    {
        ValidateFinite(point.X, path + ".x", issues);
        ValidateFinite(point.Y, path + ".y", issues);
    }

    private static void ValidateFinite(double value, string path, IssueSink issues)
    {
        if (!double.IsFinite(value))
            issues.Add("AlgorithmResultOverlayNonFinite", path);
    }

    private static void ValidatePositive(double value, double maximum, string path, IssueSink issues)
    {
        if (!double.IsFinite(value) || value <= 0 || maximum > 0 && value > maximum)
            issues.Add("AlgorithmResultOverlayPositiveFiniteInvalid", path);
    }

    private static void ValidateEnum<T>(T value, string path, IssueSink issues) where T : struct, Enum
    {
        if (!Enum.IsDefined(typeof(T), value))
            issues.Add("AlgorithmResultOverlayEnumInvalid", path);
    }

    private static void ValidateText(string? text, int maximumLength, string path, IssueSink issues)
    {
        if (text is null || text.Length == 0 || text.Length > maximumLength ||
            text.Length > MaximumOverlayTextLength)
        {
            issues.Add("AlgorithmResultOverlayTextUnsafe", path);
            return;
        }

        try
        {
            _ = new UTF8Encoding(false, true).GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            issues.Add("AlgorithmResultOverlayTextUnsafe", path);
            return;
        }

        if (!IsSafeOverlayText(text))
            issues.Add("AlgorithmResultOverlayTextUnsafe", path);
    }

    private static bool IsSafeOverlayText(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format ||
                rune.Value is '\u2028' or '\u2029')
                return false;
        }

        foreach (var character in text)
        {
            if (character is '<' or '>' or '/' or '\\' or '`' or '|')
                return false;
        }

        if (text.Contains("&lt;", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("&gt;", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("&#", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("://", StringComparison.Ordinal) ||
            text.Contains("$(", StringComparison.Ordinal) ||
            text.Contains("&&", StringComparison.Ordinal) ||
            text.Contains(";", StringComparison.Ordinal))
            return false;

        if (text.Length >= 2 && char.IsLetter(text[0]) && text[1] == ':')
            return false;

        var lower = text.TrimStart().ToLowerInvariant();
        var commandPrefixes = new[]
        {
            "bash ", "cat ", "chmod ", "chown ", "copy ", "curl ", "del ", "dotnet ",
            "echo ", "erase ", "exec ", "format ", "git ", "kill ", "mkdir ", "move ",
            "nc ", "net ", "new-item ", "node ", "python ", "remove-item ", "rm ",
            "rmdir ", "run ", "select ", "set-content ", "sh ", "start ", "sudo ",
            "update ", "insert ", "delete ", "drop ", "alter ", "create ", "wget "
        };
        if (commandPrefixes.Any(prefix => lower.StartsWith(prefix, StringComparison.Ordinal)))
            return false;

        var forbiddenTokens = new[]
        {
            "<script", "javascript:", "vbscript:", "data:text", "powershell", "pwsh",
            "cmd.exe", "cmd /", "bash -c", "sh -c", "invoke-expression", "processstartinfo",
            "system.diagnostics", "stacktrace", "stack trace", "innerexception", "aggregateexception",
            "exception"
        };
        return forbiddenTokens.All(token => !lower.Contains(token, StringComparison.Ordinal));
    }

    private static void ValidateConstraintShape(AlgorithmFieldDefinition definition,
        string? fieldKey, IssueSink issues)
    {
        var constraints = definition.Constraints;
        if (constraints is null) return;

        try
        {
            if (constraints.MinInt64 is { } minInteger && constraints.MaxInt64 is { } maxInteger &&
                minInteger > maxInteger)
                issues.Add("AlgorithmResultSchemaConstraintInvalid", fieldKey);
            if (constraints.MinFloat64 is { } minFloat && !double.IsFinite(minFloat) ||
                constraints.MaxFloat64 is { } maxFloat && !double.IsFinite(maxFloat) ||
                constraints.MinFloat64 is { } lower && constraints.MaxFloat64 is { } upper && lower > upper)
                issues.Add("AlgorithmResultSchemaConstraintInvalid", fieldKey);
            if (constraints.MinLength is < 0 or > MaximumScalarTextBytes ||
                constraints.MaxLength is < 0 or > MaximumScalarTextBytes ||
                constraints.MinLength is { } minLength && constraints.MaxLength is { } maxLength &&
                minLength > maxLength)
                issues.Add("AlgorithmResultSchemaConstraintInvalid", fieldKey);

            if (constraints.AllowedValues is { } allowed)
            {
                if (allowed.Count > MaximumMeasurements)
                    issues.Add("AlgorithmResultSchemaConstraintCapacityExceeded", fieldKey);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var index = 0;
                foreach (var value in allowed)
                {
                    if (index++ == MaximumMeasurements) break;
                    if (value is null || !IsScalarText(value) || !seen.Add(value))
                        issues.Add("AlgorithmResultSchemaConstraintInvalid", fieldKey);
                }
            }
        }
        catch (Exception exception) when (IsValidationException(exception))
        {
            issues.Add("AlgorithmResultSchemaConstraintInvalid", fieldKey);
        }
    }

    private static bool IsScalarText(string value)
    {
        if (value.Length > MaximumScalarTextBytes) return false;
        try
        {
            return new UTF8Encoding(false, true).GetByteCount(value) <= MaximumScalarTextBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static void ValidateOverlayContract(OverlayContract contract, IssueSink issues)
    {
        if (!IsIdentifier(contract.Id, 128) || !IsIdentifier(contract.Version, 128))
            issues.Add("AlgorithmResultSchemaOverlayContractIdentityInvalid");
        if (contract.MaximumElements < 0 || contract.MaximumElements > MaximumOverlayElements ||
            contract.MaximumTotalPoints < 0 || contract.MaximumTotalPoints > 1_000_000 ||
            contract.MaximumPointsPerElement < 0 || contract.MaximumPointsPerElement > MaximumOverlayPointsPerElement ||
            contract.MaximumTextLength < 0 || contract.MaximumTextLength > 65_536 ||
            contract.MaximumPointsPerElement > contract.MaximumTotalPoints && contract.MaximumTotalPoints != 0)
            issues.Add("AlgorithmResultSchemaOverlayContractBoundsInvalid");
    }

    private static bool IsIdentifier(string? value, int maximumLength)
    {
        if (value is null || value.Length < 1 || value.Length > maximumLength || value.Trim() != value ||
            value.Any(char.IsControl)) return false;
        return value.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
            >= '0' and <= '9' or '.' or '_' or '-');
    }

    private static string MeasurementPath(int index) => "measurement[" + index + "]";
    private static string OverlayPath(int index) => "overlay[" + index + "]";

    private static bool IsValidationException(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException;

    private sealed class SchemaView
    {
        public SchemaView(IReadOnlyList<AlgorithmFieldDefinition> measurements,
            IReadOnlySet<string> reasonCodes, OverlayContract? overlayContract)
        {
            Measurements = measurements;
            ReasonCodes = reasonCodes;
            OverlayContract = overlayContract;
        }

        public IReadOnlyList<AlgorithmFieldDefinition> Measurements { get; }
        public IReadOnlySet<string> ReasonCodes { get; }
        public OverlayContract? OverlayContract { get; }
    }

    private sealed class IssueSink
    {
        private readonly List<AlgorithmValidationIssue> _issues = new(MaximumIssues);

        public bool IsFull => _issues.Count >= MaximumIssues;

        public void Add(string code, string? fieldKey = null)
        {
            if (!IsFull) _issues.Add(new AlgorithmValidationIssue(code, fieldKey));
        }

        public IReadOnlyList<AlgorithmValidationIssue> ToReadOnly() =>
            new ReadOnlyCollection<AlgorithmValidationIssue>(_issues.ToArray());
    }
}
