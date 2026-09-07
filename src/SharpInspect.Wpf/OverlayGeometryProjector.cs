using System.Collections.ObjectModel;
using System.Windows;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

internal readonly record struct OverlaySegment(Point Start, Point End);

internal sealed class OverlayDrawCommand
{
    public OverlayDrawCommand(OverlayPrimitiveKind kind, OverlayStyle style,
        IReadOnlyList<Point>? points = null, IReadOnlyList<OverlaySegment>? segments = null,
        IReadOnlyList<OverlaySegment>? strokeSegments = null,
        IReadOnlyList<OverlaySegment>? arrowHeadSegments = null,
        IReadOnlyList<bool>? segmentStartsNewFigure = null,
        string? text = null, OverlayMarkerKind markerKind = OverlayMarkerKind.Cross,
        OverlayTextAnchor anchorKind = OverlayTextAnchor.TopLeft, double size = 0,
        double radiusX = 0, double radiusY = 0, double rotationDegrees = 0)
    {
        Kind = kind;
        Style = style;
        Points = points ?? Array.Empty<Point>();
        Segments = segments ?? Array.Empty<OverlaySegment>();
        StrokeSegments = strokeSegments ?? Array.Empty<OverlaySegment>();
        ArrowHeadSegments = arrowHeadSegments ?? Array.Empty<OverlaySegment>();
        SegmentStartsNewFigure = segmentStartsNewFigure ?? Array.Empty<bool>();
        Text = text;
        MarkerKind = markerKind;
        AnchorKind = anchorKind;
        Size = size;
        RadiusX = radiusX;
        RadiusY = radiusY;
        RotationDegrees = rotationDegrees;
    }

    public OverlayPrimitiveKind Kind { get; }
    public OverlayStyle Style { get; }
    public IReadOnlyList<Point> Points { get; }
    public IReadOnlyList<OverlaySegment> Segments { get; }
    public IReadOnlyList<OverlaySegment> StrokeSegments { get; }
    public IReadOnlyList<OverlaySegment> ArrowHeadSegments { get; }
    public IReadOnlyList<bool> SegmentStartsNewFigure { get; }
    public string? Text { get; }
    public OverlayMarkerKind MarkerKind { get; }
    public OverlayTextAnchor AnchorKind { get; }
    public double Size { get; }
    public double RadiusX { get; }
    public double RadiusY { get; }
    public double RotationDegrees { get; }
}

internal sealed class OverlayProjection
{
    private OverlayProjection(bool available, string reasonCode,
        IReadOnlyList<OverlayDrawCommand> commands, bool isEmpty)
    {
        Available = available;
        ReasonCode = reasonCode;
        Commands = commands;
        IsEmpty = isEmpty;
    }

    public bool Available { get; }
    public string ReasonCode { get; }
    public IReadOnlyList<OverlayDrawCommand> Commands { get; }
    public bool IsEmpty { get; }

    public static OverlayProjection Unavailable(string reasonCode) =>
        new(false, reasonCode, Array.Empty<OverlayDrawCommand>(), false);

    public static OverlayProjection AvailableEmpty { get; } =
        new(true, "OverlayEmpty", Array.Empty<OverlayDrawCommand>(), true);

    public static OverlayProjection From(IReadOnlyList<OverlayDrawCommand> commands,
        bool sourceHasElements) => commands.Count == 0
        ? sourceHasElements
            ? new(true, "OverlayOutsideViewport", commands, false)
            : AvailableEmpty
        : new(true, "OverlayRendered", commands, false);
}

/// <summary>
/// Converts validated Frame Pixel geometry into bounded WPF drawing commands. It never
/// mutates the persisted Overlay Set; an unsafe representable element invalidates the
/// whole view, while geometry fully outside the frame clips away normally.
/// </summary>
internal static class OverlayGeometryProjector
{
    internal const string RendererContractId = OverlayRenderingContract.Id;
    internal const string RendererContractVersion = OverlayRenderingContract.Version;
    internal const int MaximumRenderElements = 4096;
    internal const int MaximumRenderPoints = 1_000_000;
    internal const int MaximumRenderTextCharacters = 1_048_576;
    private const double MinimumZoom = 0.05;
    private const double MaximumZoom = 64;
    private const double MaximumPan = 1_000_000;
    private const double MaximumMappedCoordinate = 1_000_000_000;

    public static OverlayProjection Project(FrameOverlaySnapshot? snapshot,
        double zoom, double panX, double panY)
    {
        if (snapshot is null)
            return OverlayProjection.Unavailable("OverlaySnapshotUnavailable");
        if (!IsFinite(zoom) || zoom < MinimumZoom || zoom > MaximumZoom ||
            !IsFinite(panX) || !IsFinite(panY) || Math.Abs(panX) > MaximumPan ||
            Math.Abs(panY) > MaximumPan)
            return OverlayProjection.Unavailable("OverlayViewportInvalid");

        var frame = snapshot.FrameMetadata;
        var contract = snapshot.ResultSchema.OverlayContract;
        var set = snapshot.OverlaySet;
        if (set.ContractId != contract.Id || set.ContractVersion != contract.Version)
            return OverlayProjection.Unavailable("OverlaySnapshotContractMismatch");
        if (set.Primitives.Count > contract.MaximumElements ||
            set.Primitives.Count > MaximumRenderElements)
            return OverlayProjection.Unavailable("OverlayDisplayBudgetExceeded");

        var totalPoints = 0L;
        var totalText = 0L;
        var bounds = new FrameBounds(-0.5, frame.Width - 0.5, -0.5, frame.Height - 0.5);
        var commands = new List<OverlayDrawCommand>(set.Primitives.Count);
        foreach (var primitive in set.Primitives)
        {
            if (primitive is null)
                return OverlayProjection.Unavailable("OverlayElementInvalid");
            totalPoints = checked(totalPoints + primitive.PointCount);
            if (primitive.PointCount > contract.MaximumPointsPerElement ||
                totalPoints > contract.MaximumTotalPoints || totalPoints > MaximumRenderPoints)
                return OverlayProjection.Unavailable("OverlayDisplayBudgetExceeded");

            if (!ValidateStyle(primitive, zoom, out var styleReason))
                return OverlayProjection.Unavailable(styleReason);
            if (primitive is OverlayText text)
            {
                totalText = checked(totalText + text.Text.Length);
                if (text.Text.Length > contract.MaximumTextLength ||
                    totalText > MaximumRenderTextCharacters || !IsSafeText(text.Text) ||
                    (double)text.Text.Length * text.Style.TextSize > 10_000_000)
                    return OverlayProjection.Unavailable("OverlayTextNotRenderable");
            }

            object? projected;
            try
            {
                projected = ProjectPrimitive(primitive, bounds, zoom, panX, panY);
            }
            catch (ProjectionException projectionFailure)
            {
                return OverlayProjection.Unavailable(projectionFailure.ReasonCode);
            }
            catch (ArgumentException)
            {
                // A reflection-created or otherwise malformed primitive must not
                // escape into WPF geometry construction.
                return OverlayProjection.Unavailable("OverlayGeometryNotRepresentable");
            }
            catch (OverflowException)
            {
                return OverlayProjection.Unavailable("OverlayGeometryNotRepresentable");
            }
            if (projected is null) continue; // Entirely outside the frame.
            if (projected is ProjectionFailure failure)
                return OverlayProjection.Unavailable(failure.ReasonCode);
            commands.Add(((ProjectionCommand)projected).Command);
        }

        return OverlayProjection.From(new ReadOnlyCollection<OverlayDrawCommand>(commands),
            set.Primitives.Count != 0);
    }

    private static object? ProjectPrimitive(OverlayPrimitive primitive, FrameBounds bounds,
        double zoom, double panX, double panY)
    {
        return primitive switch
        {
            OverlayMarker marker => ProjectMarker(marker, bounds, zoom, panX, panY),
            OverlayLineSegment line => ProjectLine(line.Start, line.End, line.Style,
                OverlayPrimitiveKind.LineSegment, bounds, zoom, panX, panY),
            OverlayArrow arrow => ProjectArrow(arrow, bounds, zoom, panX, panY),
            OverlayPolyline polyline => ProjectPolyline(polyline, bounds, zoom, panX, panY),
            OverlayPolygon polygon => ProjectPolygon(polygon.Points, polygon.Style,
                OverlayPrimitiveKind.Polygon, bounds, zoom, panX, panY),
            OverlayAxisAlignedRectangle rectangle => ProjectPolygon(new[]
            {
                rectangle.TopLeft,
                new OverlayPoint(rectangle.TopLeft.X + rectangle.Width, rectangle.TopLeft.Y),
                new OverlayPoint(rectangle.TopLeft.X + rectangle.Width,
                    rectangle.TopLeft.Y + rectangle.Height),
                new OverlayPoint(rectangle.TopLeft.X, rectangle.TopLeft.Y + rectangle.Height)
            }, rectangle.Style, OverlayPrimitiveKind.AxisAlignedRectangle, bounds, zoom, panX, panY),
            OverlayRotatedRectangle rotated => ProjectRotatedRectangle(rotated, bounds, zoom, panX, panY),
            OverlayCircle circle => ProjectEllipse(circle.Center, circle.Radius, circle.Radius,
                0, circle.Style, OverlayPrimitiveKind.Circle, bounds, zoom, panX, panY),
            OverlayEllipse ellipse => ProjectEllipse(ellipse.Center, ellipse.RadiusX, ellipse.RadiusY,
                ellipse.RotationDegrees, ellipse.Style, OverlayPrimitiveKind.Ellipse,
                bounds, zoom, panX, panY),
            OverlayText text => ProjectText(text, bounds, zoom, panX, panY),
            _ => new ProjectionFailure("OverlayPrimitiveUnsupported")
        };
    }

    private static object? ProjectArrow(OverlayArrow arrow, FrameBounds bounds,
        double zoom, double panX, double panY)
    {
        // The shaft and the head are independent geometry.  A shaft can be
        // completely outside the frame while one of the original head wings
        // still crosses into it; do not discard that visible head merely
        // because ProjectLine returned null for the shaft.
        var projected = ProjectLine(arrow.Start, arrow.End, arrow.Style,
            OverlayPrimitiveKind.Arrow, bounds, zoom, panX, panY);
        if (projected is ProjectionFailure shaftFailure)
            return shaftFailure;
        var shaftSegments = projected is ProjectionCommand command
            ? command.Command.Segments
            : Array.Empty<OverlaySegment>();
        var arrowHeadSegments = ProjectArrowHeadSegments(arrow, bounds, zoom, panX, panY);
        if (arrowHeadSegments is ProjectionFailure failure)
            return failure;
        var headSegments = (IReadOnlyList<OverlaySegment>)arrowHeadSegments;
        if (shaftSegments.Count == 0 && headSegments.Count == 0)
            return null;
        return new ProjectionCommand(new OverlayDrawCommand(OverlayPrimitiveKind.Arrow,
            arrow.Style, segments: shaftSegments, arrowHeadSegments: headSegments));
    }

    private static object ProjectArrowHeadSegments(OverlayArrow arrow, FrameBounds bounds,
        double zoom, double panX, double panY)
    {
        var coordinateScale = Math.Max(1, Math.Max(Math.Max(Math.Abs(arrow.Start.X),
            Math.Abs(arrow.End.X)), Math.Max(Math.Abs(arrow.Start.Y), Math.Abs(arrow.End.Y))));
        var dx = arrow.End.X / coordinateScale - arrow.Start.X / coordinateScale;
        var dy = arrow.End.Y / coordinateScale - arrow.Start.Y / coordinateScale;
        var directionLength = Math.Sqrt(dx * dx + dy * dy);
        if (!IsFinite(directionLength))
            return new ProjectionFailure("OverlayGeometryNotRepresentable");
        if (directionLength == 0)
            return Array.Empty<OverlaySegment>();

        var ux = dx / directionLength;
        var uy = dy / directionLength;
        var requestedHead = arrow.Style.MarkerSize * 2;
        var sourceLength = directionLength * coordinateScale;
        var head = IsFinite(sourceLength)
            ? Math.Min(requestedHead, sourceLength)
            : requestedHead;
        if (!IsFinitePositive(head))
            return Array.Empty<OverlaySegment>();
        var basePoint = new OverlayPoint(arrow.End.X - ux * head, arrow.End.Y - uy * head);
        var wing = head * 0.45;
        var left = new OverlayPoint(basePoint.X + uy * wing, basePoint.Y - ux * wing);
        var right = new OverlayPoint(basePoint.X - uy * wing, basePoint.Y + ux * wing);
        var sourceSegments = new[]
        {
            (Start: arrow.End, End: left),
            (Start: arrow.End, End: right)
        };
        var mapped = new List<OverlaySegment>(2);
        foreach (var source in sourceSegments)
        {
            if (!TryClipSegment(source.Start, source.End, bounds,
                    out var start, out var end, out var representable))
            {
                if (!representable)
                    return new ProjectionFailure("OverlayGeometryNotRepresentable");
                continue;
            }
            if (!TryMap(start, zoom, panX, panY, out var mappedStart) ||
                !TryMap(end, zoom, panX, panY, out var mappedEnd))
                return new ProjectionFailure("OverlayGeometryNotRepresentable");
            mapped.Add(new OverlaySegment(mappedStart, mappedEnd));
        }
        return mapped;
    }

    private static object? ProjectMarker(OverlayMarker marker, FrameBounds bounds,
        double zoom, double panX, double panY)
    {
        var half = marker.Size / 2;
        if (!PotentiallyVisible(marker.Center.X, marker.Center.Y, half, bounds)) return null;
        if (!TryMap(marker.Center.X, marker.Center.Y, zoom, panX, panY, out var center))
            return new ProjectionFailure("OverlayGeometryNotRepresentable");
        return new ProjectionCommand(new OverlayDrawCommand(marker.Kind, marker.Style,
            new[] { center }, markerKind: marker.MarkerKind, size: CheckedMapped( marker.Size, zoom)));
    }

    private static object? ProjectLine(OverlayPoint start, OverlayPoint end, OverlayStyle style,
        OverlayPrimitiveKind kind, FrameBounds bounds, double zoom, double panX, double panY)
    {
        if (!TryClipSegment(start, end, bounds, out var clippedStart, out var clippedEnd,
                out var representable))
            return representable
                ? null
                : new ProjectionFailure("OverlayGeometryNotRepresentable");
        if (!TryMap(clippedStart, zoom, panX, panY, out var first) ||
            !TryMap(clippedEnd, zoom, panX, panY, out var second))
            return new ProjectionFailure("OverlayGeometryNotRepresentable");
        return new ProjectionCommand(new OverlayDrawCommand(kind, style,
            segments: new[] { new OverlaySegment(first, second) }));
    }

    private static object? ProjectPolyline(OverlayPolyline polyline, FrameBounds bounds,
        double zoom, double panX, double panY)
    {
        var segments = new List<OverlaySegment>();
        var startsNewFigure = new List<bool>();
        var previousVisible = false;
        for (var index = 1; index < polyline.Points.Count; index++)
        {
            if (!TryClipSegment(polyline.Points[index - 1], polyline.Points[index], bounds,
                    out var start, out var end, out var representable))
            {
                if (!representable)
                    return new ProjectionFailure("OverlayGeometryNotRepresentable");
                previousVisible = false;
                continue;
            }
            if (!TryMap(start, zoom, panX, panY, out var mappedStart) ||
                !TryMap(end, zoom, panX, panY, out var mappedEnd))
                return new ProjectionFailure("OverlayGeometryNotRepresentable");
            segments.Add(new OverlaySegment(mappedStart, mappedEnd));
            startsNewFigure.Add(!previousVisible || !IsInside(polyline.Points[index - 1], bounds));
            previousVisible = true;
        }

        return segments.Count == 0 ? null : new ProjectionCommand(
            new OverlayDrawCommand(OverlayPrimitiveKind.Polyline, polyline.Style, segments: segments,
                segmentStartsNewFigure: startsNewFigure));
    }

    private static object? ProjectPolygon(IReadOnlyList<OverlayPoint> points, OverlayStyle style,
        OverlayPrimitiveKind kind, FrameBounds bounds, double zoom, double panX, double panY)
    {
        if (!TryClipPolygon(points, bounds, out var clipped))
            return new ProjectionFailure("OverlayGeometryNotRepresentable");
        if (clipped.Count < 3) return null;
        var mapped = new List<Point>(clipped.Count);
        foreach (var point in clipped)
        {
            if (!TryMap(point, zoom, panX, panY, out var value))
                return new ProjectionFailure("OverlayGeometryNotRepresentable");
            mapped.Add(value);
        }

        var strokeSegments = new List<OverlaySegment>(points.Count);
        var strokeStartsNewFigure = new List<bool>(points.Count);
        var previousStrokeVisible = false;
        for (var index = 0; index < points.Count; index++)
        {
            var next = (index + 1) % points.Count;
            if (!TryClipSegment(points[index], points[next], bounds,
                    out var start, out var end, out var representable))
            {
                if (!representable)
                    return new ProjectionFailure("OverlayGeometryNotRepresentable");
                previousStrokeVisible = false;
                continue;
            }
            if (!TryMap(start, zoom, panX, panY, out var mappedStart) ||
                !TryMap(end, zoom, panX, panY, out var mappedEnd))
                return new ProjectionFailure("OverlayGeometryNotRepresentable");
            strokeSegments.Add(new OverlaySegment(mappedStart, mappedEnd));
            strokeStartsNewFigure.Add(!previousStrokeVisible || !IsInside(points[index], bounds));
            previousStrokeVisible = true;
        }

        return new ProjectionCommand(new OverlayDrawCommand(kind, style, mapped,
            strokeSegments: strokeSegments, segmentStartsNewFigure: strokeStartsNewFigure));
    }

    private static object? ProjectRotatedRectangle(OverlayRotatedRectangle rectangle,
        FrameBounds bounds, double zoom, double panX, double panY)
    {
        if (!IsFinite(rectangle.Center.X) || !IsFinite(rectangle.Center.Y) ||
            !IsFinite(rectangle.RotationDegrees))
            return new ProjectionFailure("OverlayGeometryNotRepresentable");
        var angle = NormalizeDegrees(rectangle.RotationDegrees) * Math.PI / 180.0;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var halfWidth = rectangle.Width / 2;
        var halfHeight = rectangle.Height / 2;
        var corners = new[]
        {
            RotateOffset(rectangle.Center, -halfWidth, -halfHeight, cos, sin),
            RotateOffset(rectangle.Center, halfWidth, -halfHeight, cos, sin),
            RotateOffset(rectangle.Center, halfWidth, halfHeight, cos, sin),
            RotateOffset(rectangle.Center, -halfWidth, halfHeight, cos, sin)
        };
        if (corners.Any(point => !IsFinite(point.X) || !IsFinite(point.Y)))
            return new ProjectionFailure("OverlayGeometryNotRepresentable");
        return ProjectPolygon(corners, rectangle.Style, OverlayPrimitiveKind.RotatedRectangle,
            bounds, zoom, panX, panY);
    }

    private static object? ProjectEllipse(OverlayPoint center, double radiusX, double radiusY,
        double rotationDegrees, OverlayStyle style, OverlayPrimitiveKind kind, FrameBounds bounds,
        double zoom, double panX, double panY)
    {
        var extent = Math.Max(radiusX, radiusY);
        if (!PotentiallyVisible(center.X, center.Y, extent, bounds)) return null;
        if (!TryMap(center, zoom, panX, panY, out var mappedCenter))
            return new ProjectionFailure("OverlayGeometryNotRepresentable");
        return new ProjectionCommand(new OverlayDrawCommand(kind, style,
            new[] { mappedCenter }, radiusX: CheckedMapped(radiusX, zoom),
            radiusY: CheckedMapped(radiusY, zoom), rotationDegrees: NormalizeDegrees(rotationDegrees)));
    }

    private static object? ProjectText(OverlayText text, FrameBounds bounds,
        double zoom, double panX, double panY)
    {
        var extent = Math.Min(10_000_000, Math.Max(4096,
            checked((double)text.Text.Length * text.Style.TextSize)));
        if (!PotentiallyVisible(text.Anchor.X, text.Anchor.Y, extent, bounds)) return null;
        if (!TryMap(text.Anchor, zoom, panX, panY, out var anchor))
            return new ProjectionFailure("OverlayGeometryNotRepresentable");
        return new ProjectionCommand(new OverlayDrawCommand(OverlayPrimitiveKind.Text, text.Style,
            new[] { anchor }, text: text.Text, anchorKind: text.AnchorKind));
    }

    private static bool ValidateStyle(OverlayPrimitive primitive, double zoom,
        out string reasonCode)
    {
        var style = primitive.Style;
        if (!Enum.IsDefined(typeof(OverlayStrokePattern), style.StrokePattern) ||
            !Enum.IsDefined(typeof(OverlayTextAnchor), style.TextAnchor) ||
            !IsFinitePositive(style.StrokeWidth) || !IsFinitePositive(style.MarkerSize) ||
            !IsFinitePositive(style.TextSize))
        {
            reasonCode = "OverlayStyleNotRenderable";
            return false;
        }

        // WPF's drawing primitives have a smaller practical numeric envelope than
        // the platform-neutral contract. Reject a legal-but-unrenderable style as a
        // whole view rather than allowing a partial DrawingContext failure.
        var markerDimension = primitive is OverlayArrow ? style.MarkerSize * 2 * zoom : style.MarkerSize * zoom;
        if (!IsFinitePositive(style.StrokeWidth * zoom) ||
            !IsFinitePositive(markerDimension) ||
            !IsFinitePositive(style.TextSize * zoom) ||
            style.StrokeWidth * zoom > 32_768 || markerDimension > 32_768 ||
            style.TextSize * zoom > 32_768)
        {
            reasonCode = "OverlayStyleNotRenderable";
            return false;
        }

        if (style.FillColor.HasValue && !SupportsFill(primitive))
        {
            reasonCode = "OverlayFillUnsupported";
            return false;
        }

        if (primitive is OverlayMarker marker && style.FillColor.HasValue &&
            marker.MarkerKind is OverlayMarkerKind.Cross or OverlayMarkerKind.Plus)
        {
            reasonCode = "OverlayFillUnsupported";
            return false;
        }

        reasonCode = string.Empty;
        return true;
    }

    private static bool SupportsFill(OverlayPrimitive primitive) => primitive switch
    {
        OverlayMarker marker => marker.MarkerKind is OverlayMarkerKind.Circle or OverlayMarkerKind.Square,
        OverlayPolygon or OverlayAxisAlignedRectangle or OverlayRotatedRectangle or
            OverlayCircle or OverlayEllipse => true,
        _ => false
    };

    private static bool IsInside(OverlayPoint point, FrameBounds bounds) =>
        point.X >= bounds.MinX && point.X <= bounds.MaxX &&
        point.Y >= bounds.MinY && point.Y <= bounds.MaxY;

    private static bool IsSafeText(string value)
    {
        if (value.Length == 0 || value.Length > 65_536)
            return false;
        try
        {
            _ = new System.Text.UTF8Encoding(false, true).GetBytes(value);
        }
        catch (System.Text.EncoderFallbackException)
        {
            return false;
        }

        foreach (var rune in value.EnumerateRunes())
        {
            var category = System.Text.Rune.GetUnicodeCategory(rune);
            if (category is System.Globalization.UnicodeCategory.Control or
                System.Globalization.UnicodeCategory.Format or
                System.Globalization.UnicodeCategory.Surrogate ||
                rune.Value is '\u2028' or '\u2029')
                return false;
        }

        foreach (var character in value)
        {
            if (character is '<' or '>' or '/' or '\\' or '`' or '|')
                return false;
        }

        var lower = value.TrimStart().ToLowerInvariant();
        if (lower.Contains("&lt;", StringComparison.Ordinal) ||
            lower.Contains("&gt;", StringComparison.Ordinal) ||
            lower.Contains("&#", StringComparison.Ordinal) ||
            lower.Contains("://", StringComparison.Ordinal) ||
            lower.Contains("$(", StringComparison.Ordinal) ||
            lower.Contains("&&", StringComparison.Ordinal) ||
            lower.Contains(";", StringComparison.Ordinal))
            return false;
        if (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':')
            return false;

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

    private static bool TryClipPolygon(IReadOnlyList<OverlayPoint> points,
        FrameBounds bounds, out List<OverlayPoint> result)
    {
        result = new List<OverlayPoint>();
        if (points.Count == 0) return true;
        var scale = 1d;
        foreach (var point in points)
        {
            if (!IsFinite(point.X) || !IsFinite(point.Y)) return false;
            scale = Math.Max(scale, Math.Max(Math.Abs(point.X), Math.Abs(point.Y)));
        }
        if (!IsFinite(scale)) return false;
        var current = points.Select(point => new NormalizedPoint(point.X / scale, point.Y / scale)).ToList();
        var minX = bounds.MinX / scale; var maxX = bounds.MaxX / scale;
        var minY = bounds.MinY / scale; var maxY = bounds.MaxY / scale;
        current = ClipPolygonEdge(current, value => value.X >= minX,
            (first, second) => IntersectVertical(first, second, minX));
        current = ClipPolygonEdge(current, value => value.X <= maxX,
            (first, second) => IntersectVertical(first, second, maxX));
        current = ClipPolygonEdge(current, value => value.Y >= minY,
            (first, second) => IntersectHorizontal(first, second, minY));
        current = ClipPolygonEdge(current, value => value.Y <= maxY,
            (first, second) => IntersectHorizontal(first, second, maxY));
        result = new List<OverlayPoint>(current.Count);
        foreach (var point in current)
        {
            var x = point.X * scale;
            var y = point.Y * scale;
            if (!IsFinite(x) || !IsFinite(y))
            {
                result.Clear();
                return false;
            }
            result.Add(new OverlayPoint(x, y));
        }
        return true;
    }

    private static List<NormalizedPoint> ClipPolygonEdge(List<NormalizedPoint> points,
        Func<NormalizedPoint, bool> inside, Func<NormalizedPoint, NormalizedPoint, NormalizedPoint> intersection)
    {
        if (points.Count == 0) return points;
        var output = new List<NormalizedPoint>(points.Count + 2);
        var previous = points[^1];
        var previousInside = inside(previous);
        foreach (var current in points)
        {
            var currentInside = inside(current);
            if (currentInside != previousInside)
                output.Add(intersection(previous, current));
            if (currentInside) output.Add(current);
            previous = current;
            previousInside = currentInside;
        }
        return output;
    }

    private static NormalizedPoint IntersectVertical(NormalizedPoint first,
        NormalizedPoint second, double x)
    {
        var denominator = second.X - first.X;
        if (denominator == 0) return new(x, first.Y);
        var t = (x - first.X) / denominator;
        return new(x, first.Y + (second.Y - first.Y) * t);
    }

    private static NormalizedPoint IntersectHorizontal(NormalizedPoint first,
        NormalizedPoint second, double y)
    {
        var denominator = second.Y - first.Y;
        if (denominator == 0) return new(first.X, y);
        var t = (y - first.Y) / denominator;
        return new(first.X + (second.X - first.X) * t, y);
    }

    private static bool TryClipSegment(OverlayPoint first, OverlayPoint second, FrameBounds bounds,
        out OverlayPoint clippedFirst, out OverlayPoint clippedSecond, out bool representable)
    {
        clippedFirst = default; clippedSecond = default;
        representable = true;
        if (!IsFinite(first.X) || !IsFinite(first.Y) || !IsFinite(second.X) || !IsFinite(second.Y))
        {
            representable = false;
            return false;
        }
        var scale = Math.Max(1d, Math.Max(Math.Max(Math.Abs(first.X), Math.Abs(first.Y)),
            Math.Max(Math.Abs(second.X), Math.Abs(second.Y))));
        if (!IsFinite(scale))
        {
            representable = false;
            return false;
        }
        var a = new NormalizedPoint(first.X / scale, first.Y / scale);
        var b = new NormalizedPoint(second.X / scale, second.Y / scale);
        var minX = bounds.MinX / scale; var maxX = bounds.MaxX / scale;
        var minY = bounds.MinY / scale; var maxY = bounds.MaxY / scale;
        var t0 = 0d; var t1 = 1d;
        var entering = ClipBoundary.None;
        var exiting = ClipBoundary.None;
        if (!ClipTest(-(b.X - a.X), a.X - minX, ref t0, ref t1,
                ClipBoundary.MinX, ref entering, ref exiting) ||
            !ClipTest(b.X - a.X, maxX - a.X, ref t0, ref t1,
                ClipBoundary.MaxX, ref entering, ref exiting) ||
            !ClipTest(-(b.Y - a.Y), a.Y - minY, ref t0, ref t1,
                ClipBoundary.MinY, ref entering, ref exiting) ||
            !ClipTest(b.Y - a.Y, maxY - a.Y, ref t0, ref t1,
                ClipBoundary.MaxY, ref entering, ref exiting) || t0 > t1)
            return false;
        var firstX = (a.X + (b.X - a.X) * t0) * scale;
        var firstY = (a.Y + (b.Y - a.Y) * t0) * scale;
        var secondX = (a.X + (b.X - a.X) * t1) * scale;
        var secondY = (a.Y + (b.Y - a.Y) * t1) * scale;
        // When a line spans a vastly larger coordinate range than the frame, the
        // interpolation above can round a half-pixel boundary to zero. Preserve the
        // exact clipping edge selected by Liang-Barsky before mapping to WPF.
        switch (entering)
        {
            case ClipBoundary.MinX: firstX = bounds.MinX; break;
            case ClipBoundary.MaxX: firstX = bounds.MaxX; break;
            case ClipBoundary.MinY: firstY = bounds.MinY; break;
            case ClipBoundary.MaxY: firstY = bounds.MaxY; break;
        }
        switch (exiting)
        {
            case ClipBoundary.MinX: secondX = bounds.MinX; break;
            case ClipBoundary.MaxX: secondX = bounds.MaxX; break;
            case ClipBoundary.MinY: secondY = bounds.MinY; break;
            case ClipBoundary.MaxY: secondY = bounds.MaxY; break;
        }
        if (!IsFinite(firstX) || !IsFinite(firstY) || !IsFinite(secondX) || !IsFinite(secondY))
        {
            representable = false;
            return false;
        }
        clippedFirst = new OverlayPoint(firstX, firstY);
        clippedSecond = new OverlayPoint(secondX, secondY);
        return true;
    }

    private static bool ClipTest(double p, double q, ref double t0, ref double t1,
        ClipBoundary boundary, ref ClipBoundary entering, ref ClipBoundary exiting)
    {
        if (p == 0) return q >= 0;
        var ratio = q / p;
        if (p < 0)
        {
            if (ratio > t1) return false;
            if (ratio > t0)
            {
                t0 = ratio;
                entering = boundary;
            }
        }
        else
        {
            if (ratio < t0) return false;
            if (ratio < t1)
            {
                t1 = ratio;
                exiting = boundary;
            }
        }
        return true;
    }

    private static bool PotentiallyVisible(double x, double y, double extent, FrameBounds bounds)
    {
        if (!IsFinite(x) || !IsFinite(y) || !IsFinite(extent) || extent < 0) return false;
        return Math.Abs(x) <= Math.Max(Math.Abs(bounds.MinX), Math.Abs(bounds.MaxX)) + extent &&
            Math.Abs(y) <= Math.Max(Math.Abs(bounds.MinY), Math.Abs(bounds.MaxY)) + extent;
    }

    private static bool TryMap(OverlayPoint point, double zoom, double panX, double panY,
        out Point mapped) => TryMap(point.X, point.Y, zoom, panX, panY, out mapped);

    private static bool TryMap(double x, double y, double zoom, double panX, double panY,
        out Point mapped)
    {
        // Pan is expressed in WPF DIPs. Zoom applies to frame-pixel coordinates
        // before the user translation, matching the host's drag delta semantics.
        var mappedX = (x + 0.5) * zoom + panX;
        var mappedY = (y + 0.5) * zoom + panY;
        if (!IsFinite(mappedX) || !IsFinite(mappedY) ||
            Math.Abs(mappedX) > MaximumMappedCoordinate || Math.Abs(mappedY) > MaximumMappedCoordinate)
        {
            mapped = default;
            return false;
        }
        mapped = new Point(mappedX, mappedY);
        return true;
    }

    private static double CheckedMapped(double value, double zoom)
    {
        var result = value * zoom;
        if (!IsFinite(result) || Math.Abs(result) > MaximumMappedCoordinate)
            throw new ProjectionException("OverlayGeometryNotRepresentable");
        return result;
    }

    private static OverlayPoint RotateOffset(OverlayPoint center, double x, double y,
        double cos, double sin) => new(center.X + x * cos - y * sin,
        center.Y + x * sin + y * cos);

    private static double NormalizeDegrees(double degrees)
    {
        var normalized = degrees % 360d;
        return normalized < 0 ? normalized + 360d : normalized;
    }

    private static bool IsFinite(double value) => double.IsFinite(value);
    private static bool IsFinitePositive(double value) => IsFinite(value) && value > 0;

    private readonly record struct FrameBounds(double MinX, double MaxX, double MinY, double MaxY);
    private readonly record struct NormalizedPoint(double X, double Y);
    private enum ClipBoundary { None, MinX, MaxX, MinY, MaxY }
    private sealed record ProjectionCommand(OverlayDrawCommand Command);
    private sealed record ProjectionFailure(string ReasonCode);
    private sealed class ProjectionException : InvalidOperationException
    {
        public ProjectionException(string reasonCode) : base(reasonCode) => ReasonCode = reasonCode;
        public string ReasonCode { get; }
    }
}
