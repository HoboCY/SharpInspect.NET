using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

public enum InspectionDecision
{
    Pass,
    Fail,
    Unknown
}

/// <summary>A typed measurement returned under the bound Algorithm Result Schema.</summary>
public sealed record AlgorithmMeasurement
{
    public AlgorithmMeasurement(string key, string unit, AlgorithmScalarValue value)
    {
        Key = AlgorithmContractValidation.Identifier(key, nameof(key));
        Unit = AlgorithmContractValidation.Identifier(unit, nameof(unit), maximumLength: 64);
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Key { get; }
    public string Unit { get; }
    public AlgorithmScalarValue Value { get; }
}

/// <summary>Complete immutable product result. Runtime owns execution status and publication.</summary>
public sealed record AlgorithmResult
{
    public AlgorithmResult(InspectionDecision decision, string? reasonCode,
        IEnumerable<AlgorithmMeasurement> measurements, OutputOverlaySet overlaySet)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        if (!Enum.IsDefined(typeof(InspectionDecision), decision))
            throw new ArgumentOutOfRangeException(nameof(decision));
        if (decision == InspectionDecision.Unknown && string.IsNullOrWhiteSpace(reasonCode))
            throw new ArgumentException("AlgorithmUnknownReasonRequired", nameof(reasonCode));
        if (reasonCode is not null)
            reasonCode = AlgorithmContractValidation.Identifier(reasonCode, nameof(reasonCode));
        var copied = AlgorithmContractValidation.Copy(measurements, nameof(measurements), maximumCount: 256);
        if (copied.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != copied.Count)
            throw new ArgumentException("AlgorithmMeasurementDuplicate", nameof(measurements));

        Decision = decision;
        ReasonCode = reasonCode;
        Measurements = copied;
        OverlaySet = overlaySet ?? throw new ArgumentNullException(nameof(overlaySet));
    }

    public InspectionDecision Decision { get; }
    public string? ReasonCode { get; }
    public ReadOnlyCollection<AlgorithmMeasurement> Measurements { get; }
    public OutputOverlaySet OverlaySet { get; }
    public OutputOverlaySet Overlays => OverlaySet;
}

/// <summary>Immutable versioned result contract published before Recipe Activation.</summary>
public sealed record AlgorithmResultSchema
{
    public AlgorithmResultSchema(string id, string version,
        IEnumerable<AlgorithmFieldDefinition> measurements,
        IEnumerable<string> reasonCodes, OverlayContract overlayContract)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        ArgumentNullException.ThrowIfNull(reasonCodes);
        Id = AlgorithmContractValidation.Identifier(id, nameof(id));
        Version = AlgorithmContractValidation.Identifier(version, nameof(version));
        var copiedMeasurements = AlgorithmContractValidation.Copy(measurements, nameof(measurements), maximumCount: 256);
        if (copiedMeasurements.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() !=
            copiedMeasurements.Count)
            throw new ArgumentException("AlgorithmMeasurementSchemaDuplicate", nameof(measurements));
        if (copiedMeasurements.Any(item => item.AuthoringDefault is not null))
            throw new ArgumentException("AlgorithmResultDefaultsForbidden", nameof(measurements));
        if (copiedMeasurements.Any(item => item.HelpText is not null))
            throw new ArgumentException("AlgorithmResultAuthoringHelpForbidden", nameof(measurements));

        var copiedReasons = AlgorithmContractValidation.Copy(reasonCodes, nameof(reasonCodes), maximumCount: 256);
        foreach (var reason in copiedReasons)
            _ = AlgorithmContractValidation.Identifier(reason, nameof(reasonCodes));
        if (copiedReasons.Distinct(StringComparer.Ordinal).Count() != copiedReasons.Count)
            throw new ArgumentException("AlgorithmReasonCodeDuplicate", nameof(reasonCodes));

        OverlayContract = overlayContract ?? throw new ArgumentNullException(nameof(overlayContract));
        Measurements = copiedMeasurements;
        ReasonCodes = copiedReasons;
        ContentHash = ComputeContentHash(copiedMeasurements, copiedReasons, OverlayContract);
    }

    public string Id { get; }
    public string Version { get; }
    public ReadOnlyCollection<AlgorithmFieldDefinition> Measurements { get; }
    public ReadOnlyCollection<string> ReasonCodes { get; }
    public OverlayContract OverlayContract { get; }
    public string ContentHash { get; }

    private string ComputeContentHash(IEnumerable<AlgorithmFieldDefinition> measurements,
        IEnumerable<string> reasonCodes, OverlayContract overlayContract)
    {
        var parts = new List<string?>
        {
            "sharpinspect-algorithm-result-schema-v1", Id, Version,
            "scalar-types-v1:Boolean|Int64|Float64|String|Enum",
            "inspection-decisions-v1:Pass|Fail|Unknown",
            overlayContract.Id, overlayContract.Version, overlayContract.ContentHash
        };

        foreach (var field in measurements.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            parts.Add("field");
            parts.Add(field.Key);
            parts.Add(((int)field.Type).ToString(CultureInfo.InvariantCulture));
            parts.Add(field.Unit);
            parts.Add(field.Required ? "1" : "0");
            AppendConstraints(parts, field.Constraints);
            // Result measurements must not have authoring defaults; retain an explicit
            // null marker in the canonical input so this invariant cannot disappear.
            parts.Add(null);
        }

        foreach (var reason in reasonCodes.OrderBy(item => item, StringComparer.Ordinal))
        {
            parts.Add("reason");
            parts.Add(reason);
        }

        return AlgorithmContractValidation.HashParts(parts);
    }

    private static void AppendConstraints(List<string?> parts, AlgorithmScalarConstraints? constraints)
    {
        if (constraints is null)
        {
            parts.Add(null);
            return;
        }

        parts.Add("constraints-v1");
        parts.Add(constraints.MinInt64?.ToString(CultureInfo.InvariantCulture));
        parts.Add(constraints.MaxInt64?.ToString(CultureInfo.InvariantCulture));
        parts.Add(constraints.MinFloat64 is { } min
            ? BitConverter.DoubleToInt64Bits(AlgorithmScalarValue.NormalizeFloat(min))
                .ToString(CultureInfo.InvariantCulture)
            : null);
        parts.Add(constraints.MaxFloat64 is { } max
            ? BitConverter.DoubleToInt64Bits(AlgorithmScalarValue.NormalizeFloat(max))
                .ToString(CultureInfo.InvariantCulture)
            : null);
        parts.Add(constraints.MinLength?.ToString(CultureInfo.InvariantCulture));
        parts.Add(constraints.MaxLength?.ToString(CultureInfo.InvariantCulture));
        if (constraints.AllowedValues is null)
        {
            parts.Add("allowed:null");
            return;
        }

        parts.Add("allowed");
        parts.Add(constraints.AllowedValues.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var value in constraints.AllowedValues.OrderBy(item => item, StringComparer.Ordinal))
            parts.Add(value);
    }
}

/// <summary>Closed overlay contract with bounded geometry and text limits.</summary>
public sealed record OverlayContract
{
    public OverlayContract(string id, string version, int maximumElements = 256,
        int maximumTotalPoints = 4096, int maximumPointsPerElement = 1024,
        int maximumTextLength = 1024)
    {
        Id = AlgorithmContractValidation.Identifier(id, nameof(id));
        Version = AlgorithmContractValidation.Identifier(version, nameof(version));
        if (maximumElements is < 0 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumElements));
        if (maximumTotalPoints is < 0 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(maximumTotalPoints));
        if (maximumPointsPerElement is < 0 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(maximumPointsPerElement));
        if (maximumTextLength is < 0 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(maximumTextLength));
        if (maximumPointsPerElement > maximumTotalPoints && maximumTotalPoints != 0)
            throw new ArgumentException("OverlayPointBudgetInvalid", nameof(maximumPointsPerElement));

        MaximumElements = maximumElements;
        MaximumTotalPoints = maximumTotalPoints;
        MaximumPointsPerElement = maximumPointsPerElement;
        MaximumTextLength = maximumTextLength;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-overlay-contract-v1", Id, Version,
            "coordinate-v1:frame-pixel",
            "primitives-v1:Marker|LineSegment|Arrow|Polyline|Polygon|AxisAlignedRectangle|RotatedRectangle|Circle|Ellipse|Text",
            "style-v1:rgba|stroke-width|fill|solid-dashed|marker-size|text-size|text-anchor",
            maximumElements.ToString(CultureInfo.InvariantCulture),
            maximumTotalPoints.ToString(CultureInfo.InvariantCulture),
            maximumPointsPerElement.ToString(CultureInfo.InvariantCulture),
            maximumTextLength.ToString(CultureInfo.InvariantCulture)
        });
    }

    public string Id { get; }
    public string Version { get; }
    public int MaximumElements { get; }
    public int MaximumTotalPoints { get; }
    public int MaximumPointsPerElement { get; }
    public int MaximumTextLength { get; }
    public int MaxElements => MaximumElements;
    public int MaxTotalPoints => MaximumTotalPoints;
    public int MaxPointsPerElement => MaximumPointsPerElement;
    public int MaxTextLength => MaximumTextLength;
    public string ContentHash { get; }
}

/// <summary>Frame-pixel coordinate used by all overlay primitives.</summary>
public readonly record struct OverlayPoint
{
    public OverlayPoint(double x, double y)
    {
        AlgorithmContractValidation.Finite(x, nameof(x));
        AlgorithmContractValidation.Finite(y, nameof(y));
        X = x;
        Y = y;
    }

    public double X { get; }
    public double Y { get; }
}

public readonly record struct OverlayColor(byte Red, byte Green, byte Blue, byte Alpha = 255);

public enum OverlayStrokePattern
{
    Solid,
    Dashed
}

public enum OverlayMarkerKind
{
    Cross,
    Plus,
    Circle,
    Square
}

public enum OverlayTextAnchor
{
    Center,
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight
}

/// <summary>Platform-neutral style values. It contains no WPF, SVG, or native image type.</summary>
public sealed record OverlayStyle
{
    public OverlayStyle(OverlayColor strokeColor, double strokeWidth = 1,
        OverlayColor? fillColor = null, OverlayStrokePattern strokePattern = OverlayStrokePattern.Solid,
        double markerSize = 4, double textSize = 12, OverlayTextAnchor textAnchor = OverlayTextAnchor.TopLeft)
    {
        if (!Enum.IsDefined(typeof(OverlayStrokePattern), strokePattern))
            throw new ArgumentOutOfRangeException(nameof(strokePattern));
        AlgorithmContractValidation.PositiveFinite(strokeWidth, nameof(strokeWidth));
        AlgorithmContractValidation.PositiveFinite(markerSize, nameof(markerSize));
        AlgorithmContractValidation.PositiveFinite(textSize, nameof(textSize));
        if (strokeWidth > 1024 || markerSize > 4096 || textSize > 4096)
            throw new ArgumentOutOfRangeException(nameof(strokeWidth));
        if (!Enum.IsDefined(typeof(OverlayTextAnchor), textAnchor))
            throw new ArgumentOutOfRangeException(nameof(textAnchor));

        StrokeColor = strokeColor;
        StrokeWidth = strokeWidth;
        FillColor = fillColor;
        StrokePattern = strokePattern;
        MarkerSize = markerSize;
        TextSize = textSize;
        TextAnchor = textAnchor;
    }

    public static OverlayStyle Default { get; } = new(new OverlayColor(255, 255, 255, 255));
    public OverlayColor StrokeColor { get; }
    public double StrokeWidth { get; }
    public OverlayColor? FillColor { get; }
    public OverlayStrokePattern StrokePattern { get; }
    public double MarkerSize { get; }
    public double TextSize { get; }
    public OverlayTextAnchor TextAnchor { get; }
}

public enum OverlayPrimitiveKind
{
    Marker,
    LineSegment,
    Arrow,
    Polyline,
    Polygon,
    AxisAlignedRectangle,
    RotatedRectangle,
    Circle,
    Ellipse,
    Text
}

public abstract class OverlayPrimitive
{
    private protected OverlayPrimitive(OverlayStyle? style)
    {
        Style = style ?? OverlayStyle.Default;
    }

    public OverlayStyle Style { get; }
    public abstract OverlayPrimitiveKind Kind { get; }
    public abstract int PointCount { get; }
}

public sealed class OverlayMarker : OverlayPrimitive
{
    public OverlayMarker(OverlayPoint center, OverlayMarkerKind markerKind, double size,
        OverlayStyle? style = null) : base(style)
    {
        if (!Enum.IsDefined(typeof(OverlayMarkerKind), markerKind))
            throw new ArgumentOutOfRangeException(nameof(markerKind));
        AlgorithmContractValidation.PositiveFinite(size, nameof(size));
        if (size > 4096) throw new ArgumentOutOfRangeException(nameof(size));
        Center = center;
        MarkerKind = markerKind;
        Size = size;
    }

    public OverlayPoint Center { get; }
    public OverlayMarkerKind MarkerKind { get; }
    public double Size { get; }
    public override OverlayPrimitiveKind Kind => OverlayPrimitiveKind.Marker;
    public override int PointCount => 1;
}

public sealed class OverlayLineSegment : OverlayPrimitive
{
    public OverlayLineSegment(OverlayPoint start, OverlayPoint end, OverlayStyle? style = null) : base(style)
    { Start = start; End = end; }

    public OverlayPoint Start { get; }
    public OverlayPoint End { get; }
    public override OverlayPrimitiveKind Kind => OverlayPrimitiveKind.LineSegment;
    public override int PointCount => 2;
}

public sealed class OverlayArrow : OverlayPrimitive
{
    public OverlayArrow(OverlayPoint start, OverlayPoint end, OverlayStyle? style = null) : base(style)
    { Start = start; End = end; }

    public OverlayPoint Start { get; }
    public OverlayPoint End { get; }
    public override OverlayPrimitiveKind Kind => OverlayPrimitiveKind.Arrow;
    public override int PointCount => 2;
}

public sealed class OverlayPolyline : OverlayPrimitive
{
    public OverlayPolyline(IEnumerable<OverlayPoint> points, OverlayStyle? style = null) : base(style)
    {
        var copied = AlgorithmContractValidation.Copy(points, nameof(points), maximumCount: 100_000);
        if (copied.Count < 2)
            throw new ArgumentException("OverlayPolylinePointCountInvalid", nameof(points));
        Points = copied;
    }

    public ReadOnlyCollection<OverlayPoint> Points { get; }
    public override OverlayPrimitiveKind Kind => OverlayPrimitiveKind.Polyline;
    public override int PointCount => Points.Count;
}

public sealed class OverlayPolygon : OverlayPrimitive
{
    public OverlayPolygon(IEnumerable<OverlayPoint> points, OverlayStyle? style = null) : base(style)
    {
        var copied = AlgorithmContractValidation.Copy(points, nameof(points), maximumCount: 100_000);
        if (copied.Count < 3)
            throw new ArgumentException("OverlayPolygonPointCountInvalid", nameof(points));
        Points = copied;
    }

    public ReadOnlyCollection<OverlayPoint> Points { get; }
    public override OverlayPrimitiveKind Kind => OverlayPrimitiveKind.Polygon;
    public override int PointCount => Points.Count;
}

public sealed class OverlayAxisAlignedRectangle : OverlayPrimitive
{
    public OverlayAxisAlignedRectangle(OverlayPoint topLeft, double width, double height,
        OverlayStyle? style = null) : base(style)
    {
        AlgorithmContractValidation.PositiveFinite(width, nameof(width));
        AlgorithmContractValidation.PositiveFinite(height, nameof(height));
        TopLeft = topLeft;
        Width = width;
        Height = height;
    }

    public OverlayPoint TopLeft { get; }
    public double Width { get; }
    public double Height { get; }
    public override OverlayPrimitiveKind Kind => OverlayPrimitiveKind.AxisAlignedRectangle;
    public override int PointCount => 4;
}

public sealed class OverlayRotatedRectangle : OverlayPrimitive
{
    public OverlayRotatedRectangle(OverlayPoint center, double width, double height,
        double rotationDegrees, OverlayStyle? style = null) : base(style)
    {
        AlgorithmContractValidation.PositiveFinite(width, nameof(width));
        AlgorithmContractValidation.PositiveFinite(height, nameof(height));
        AlgorithmContractValidation.Finite(rotationDegrees, nameof(rotationDegrees));
        Center = center;
        Width = width;
        Height = height;
        RotationDegrees = rotationDegrees;
    }

    public OverlayPoint Center { get; }
    public double Width { get; }
    public double Height { get; }
    public double RotationDegrees { get; }
    public override OverlayPrimitiveKind Kind => OverlayPrimitiveKind.RotatedRectangle;
    public override int PointCount => 4;
}

public sealed class OverlayCircle : OverlayPrimitive
{
    public OverlayCircle(OverlayPoint center, double radius, OverlayStyle? style = null) : base(style)
    {
        AlgorithmContractValidation.PositiveFinite(radius, nameof(radius));
        Center = center;
        Radius = radius;
    }

    public OverlayPoint Center { get; }
    public double Radius { get; }
    public override OverlayPrimitiveKind Kind => OverlayPrimitiveKind.Circle;
    public override int PointCount => 1;
}

public sealed class OverlayEllipse : OverlayPrimitive
{
    public OverlayEllipse(OverlayPoint center, double radiusX, double radiusY,
        double rotationDegrees, OverlayStyle? style = null) : base(style)
    {
        AlgorithmContractValidation.PositiveFinite(radiusX, nameof(radiusX));
        AlgorithmContractValidation.PositiveFinite(radiusY, nameof(radiusY));
        AlgorithmContractValidation.Finite(rotationDegrees, nameof(rotationDegrees));
        Center = center;
        RadiusX = radiusX;
        RadiusY = radiusY;
        RotationDegrees = rotationDegrees;
    }

    public OverlayPoint Center { get; }
    public double RadiusX { get; }
    public double RadiusY { get; }
    public double RotationDegrees { get; }
    public override OverlayPrimitiveKind Kind => OverlayPrimitiveKind.Ellipse;
    public override int PointCount => 1;
}

public sealed class OverlayText : OverlayPrimitive
{
    public OverlayText(OverlayPoint anchor, string text, OverlayStyle? style = null,
        OverlayTextAnchor anchorKind = OverlayTextAnchor.TopLeft) : base(style)
    {
        if (!Enum.IsDefined(typeof(OverlayTextAnchor), anchorKind))
            throw new ArgumentOutOfRangeException(nameof(anchorKind));
        Text = AlgorithmContractValidation.BoundedText(text, nameof(text), 65_536);
        Anchor = anchor;
        AnchorKind = anchorKind;
    }

    public OverlayPoint Anchor { get; }
    public string Text { get; }
    public OverlayTextAnchor AnchorKind { get; }
    public override OverlayPrimitiveKind Kind => OverlayPrimitiveKind.Text;
    public override int PointCount => 1;
}

/// <summary>Immutable output overlay set bound to one exact overlay contract identity/version.</summary>
public sealed record OutputOverlaySet
{
    public OutputOverlaySet(string contractId, string contractVersion,
        IEnumerable<OverlayPrimitive>? primitives = null)
    {
        ContractId = AlgorithmContractValidation.Identifier(contractId, nameof(contractId));
        ContractVersion = AlgorithmContractValidation.Identifier(contractVersion, nameof(contractVersion));
        var copied = AlgorithmContractValidation.Copy(primitives, nameof(primitives), maximumCount: 4096);
        if (copied.Sum(item => item.PointCount) > 1_000_000)
            throw new ArgumentException("OverlayPointCapacityExceeded", nameof(primitives));
        Primitives = copied;
    }

    public OutputOverlaySet(OverlayContract contract, IEnumerable<OverlayPrimitive>? primitives = null)
        : this((contract ?? throw new ArgumentNullException(nameof(contract))).Id,
            contract.Version, primitives)
    { }

    public string ContractId { get; }
    public string ContractVersion { get; }
    public ReadOnlyCollection<OverlayPrimitive> Primitives { get; }
    public ReadOnlyCollection<OverlayPrimitive> Elements => Primitives;
    public int TotalPointCount => Primitives.Sum(item => item.PointCount);
}
