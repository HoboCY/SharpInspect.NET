using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

public enum FrameOverlayRenderStatus
{
    Unavailable,
    Available,
    Empty
}

/// <summary>
/// WPF-only display surface for one Runtime-validated immutable overlay snapshot.
/// It accepts the typed FramePreviewImage rather than an arbitrary ImageSource so a
/// caller cannot accidentally present an already annotated bitmap as source evidence.
/// </summary>
public sealed class FrameOverlayPresenter : FrameworkElement, INotifyPropertyChanged
{
    public static readonly DependencyProperty SnapshotProperty =
        DependencyProperty.Register(nameof(Snapshot), typeof(FrameOverlaySnapshot),
            typeof(FrameOverlayPresenter), new PropertyMetadata(null, InputChanged));
    public static readonly DependencyProperty SourceImageProperty =
        DependencyProperty.Register(nameof(SourceImage), typeof(FramePreviewImage),
            typeof(FrameOverlayPresenter), new PropertyMetadata(null, InputChanged));
    public static readonly DependencyProperty ZoomProperty =
        DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(FrameOverlayPresenter),
            new PropertyMetadata(1d, InputChanged));
    public static readonly DependencyProperty PanXProperty =
        DependencyProperty.Register(nameof(PanX), typeof(double), typeof(FrameOverlayPresenter),
            new PropertyMetadata(0d, InputChanged));
    public static readonly DependencyProperty PanYProperty =
        DependencyProperty.Register(nameof(PanY), typeof(double), typeof(FrameOverlayPresenter),
            new PropertyMetadata(0d, InputChanged));

    private OverlayProjection _projection = OverlayProjection.Unavailable("OverlaySnapshotUnavailable");

    public FrameOverlaySnapshot? Snapshot
    {
        get => (FrameOverlaySnapshot?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    public FramePreviewImage? SourceImage
    {
        get => (FramePreviewImage?)GetValue(SourceImageProperty);
        set => SetValue(SourceImageProperty, value);
    }

    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public double PanX
    {
        get => (double)GetValue(PanXProperty);
        set => SetValue(PanXProperty, value);
    }

    public double PanY
    {
        get => (double)GetValue(PanYProperty);
        set => SetValue(PanYProperty, value);
    }

    public bool IsAvailable => _projection.Available;
    public bool IsEmpty => _projection.IsEmpty;
    public FrameOverlayRenderStatus RenderStatus => !_projection.Available
        ? FrameOverlayRenderStatus.Unavailable
        : _projection.IsEmpty ? FrameOverlayRenderStatus.Empty : FrameOverlayRenderStatus.Available;
    public string RenderReasonCode => _projection.ReasonCode;
    public string ReasonCode => RenderReasonCode;
    public string StatusMessage => ToStatusMessage(RenderReasonCode);
    public bool SourceImageAvailable { get; private set; }
    public string SourceImageReasonCode { get; private set; } = "SourceImageUnavailable";
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Renders a derived bitmap with source and overlay identities retained separately.</summary>
    public RenderedOverlayPreview RenderPreview()
    {
        var projection = CurrentProjection();
        if (!projection.Available || Snapshot is null)
            throw new InvalidOperationException(projection.ReasonCode);

        var dpi = GetDpiScale();
        var widthDip = Snapshot.FrameMetadata.Width * Zoom;
        var heightDip = Snapshot.FrameMetadata.Height * Zoom;
        if (!double.IsFinite(widthDip) || !double.IsFinite(heightDip) || widthDip <= 0 || heightDip <= 0)
            throw new InvalidOperationException("OverlayPreviewBudgetExceeded");
        var pixelWidthValue = widthDip * dpi.DpiScaleX;
        var pixelHeightValue = heightDip * dpi.DpiScaleY;
        if (!double.IsFinite(pixelWidthValue) || !double.IsFinite(pixelHeightValue) ||
            pixelWidthValue < 1 || pixelHeightValue < 1 ||
            pixelWidthValue > int.MaxValue || pixelHeightValue > int.MaxValue)
            throw new InvalidOperationException("OverlayPreviewBudgetExceeded");
        var pixelWidth = (int)Math.Ceiling(pixelWidthValue);
        var pixelHeight = (int)Math.Ceiling(pixelHeightValue);
        var pixelCount = (long)pixelWidth * pixelHeight;
        if (pixelCount > FramePreviewImage.MaximumPixelBytes / 4)
            throw new InvalidOperationException("OverlayPreviewBudgetExceeded");

        try
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                PushFrameClip(drawing, out var popClip);
                try
                {
                    DrawSource(drawing);
                    DrawProjection(drawing, projection.Commands, Zoom, dpi.PixelsPerDip);
                }
                finally
                {
                    popClip();
                }
            }

            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight,
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return new RenderedOverlayPreview(bitmap, Snapshot, ValidSourceImage(), Zoom, PanX, PanY,
                dpi.DpiScaleX, dpi.DpiScaleY);
        }
        catch (ArgumentException)
        {
            MarkDrawingUnavailable();
            throw new InvalidOperationException("OverlayDrawingUnavailable");
        }
        catch (InvalidOperationException)
        {
            MarkDrawingUnavailable();
            throw new InvalidOperationException("OverlayDrawingUnavailable");
        }
        catch (OverflowException)
        {
            MarkDrawingUnavailable();
            throw new InvalidOperationException("OverlayDrawingUnavailable");
        }
        catch (NotSupportedException)
        {
            MarkDrawingUnavailable();
            throw new InvalidOperationException("OverlayDrawingUnavailable");
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var projection = CurrentProjection();
        if (!projection.Available) return;
        drawingContext.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        PushFrameClip(drawingContext, out var popFrameClip);
        var drawingFailed = false;
        try
        {
            DrawSource(drawingContext);
            var dpi = GetDpiScale();
            DrawProjection(drawingContext, projection.Commands, Zoom, dpi.PixelsPerDip);
        }
        catch (ArgumentException)
        {
            drawingFailed = true;
        }
        catch (InvalidOperationException)
        {
            drawingFailed = true;
        }
        catch (OverflowException)
        {
            drawingFailed = true;
        }
        catch (NotSupportedException)
        {
            drawingFailed = true;
        }
        finally
        {
            popFrameClip();
            drawingContext.Pop();
        }
        if (drawingFailed)
            MarkDrawingUnavailable();
    }

    private OverlayProjection CurrentProjection() => _projection;

    private void RebuildProjection()
    {
        var projection = OverlayGeometryProjector.Project(Snapshot, Zoom, PanX, PanY);
        PublishProjection(projection, updateSourceState: true);
    }

    private void MarkDrawingUnavailable()
    {
        PublishProjection(OverlayProjection.Unavailable("OverlayDrawingUnavailable"),
            updateSourceState: false);
    }

    private void PublishProjection(OverlayProjection projection, bool updateSourceState)
    {
        var previousReason = _projection.ReasonCode;
        var previousAvailable = _projection.Available;
        var previousEmpty = _projection.IsEmpty;
        var previousStatus = RenderStatus;
        var previousSourceAvailable = SourceImageAvailable;
        var previousSourceReason = SourceImageReasonCode;
        _projection = projection;
        if (updateSourceState)
            UpdateSourceState();
        InvalidateVisual();
        if (previousReason != _projection.ReasonCode)
        {
            OnPropertyChanged(nameof(RenderReasonCode));
            OnPropertyChanged(nameof(ReasonCode));
            OnPropertyChanged(nameof(StatusMessage));
        }
        if (previousAvailable != _projection.Available)
            OnPropertyChanged(nameof(IsAvailable));
        if (previousEmpty != _projection.IsEmpty)
            OnPropertyChanged(nameof(IsEmpty));
        if (previousStatus != RenderStatus)
            OnPropertyChanged(nameof(RenderStatus));
        if (previousSourceAvailable != SourceImageAvailable)
            OnPropertyChanged(nameof(SourceImageAvailable));
        if (previousSourceReason != SourceImageReasonCode)
            OnPropertyChanged(nameof(SourceImageReasonCode));
    }

    private static void InputChanged(DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((FrameOverlayPresenter)dependencyObject).RebuildProjection();

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string ToStatusMessage(string reasonCode) => reasonCode switch
    {
        "OverlayRendered" => "标注已显示",
        "OverlayEmpty" => "结果没有标注图元",
        "OverlayOutsideViewport" => "标注在当前视口外",
        "OverlaySnapshotUnavailable" => "没有可显示的结果标注",
        "SourceImageUnavailable" => "原始图像预览不可用",
        "SourceImageMetadataMismatch" => "原始图像与结果帧不匹配",
        "OverlayViewportInvalid" => "视图参数不可用",
        _ => "结果标注暂不可用"
    };

    private void UpdateSourceState()
    {
        if (SourceImage is null)
        {
            SourceImageAvailable = false;
            SourceImageReasonCode = "SourceImageUnavailable";
            return;
        }
        if (Snapshot is null)
        {
            SourceImageAvailable = false;
            SourceImageReasonCode = "OverlaySnapshotUnavailable";
            return;
        }
        SourceImageAvailable = SourceImage.FrameMetadata.Equals(Snapshot.FrameMetadata);
        SourceImageReasonCode = SourceImageAvailable ? "SourceImageReady" : "SourceImageMetadataMismatch";
    }

    private FramePreviewImage? ValidSourceImage() => SourceImageAvailable ? SourceImage : null;

    private void DrawSource(DrawingContext drawing)
    {
        var source = ValidSourceImage();
        if (source is null || Snapshot is null) return;
        var width = Snapshot.FrameMetadata.Width * Zoom;
        var height = Snapshot.FrameMetadata.Height * Zoom;
        drawing.DrawImage(source.BitmapSource,
            new Rect(PanX, PanY, width, height));
    }

    private void PushFrameClip(DrawingContext drawing, out Action pop)
    {
        var snapshot = Snapshot;
        if (snapshot is null)
        {
            pop = static () => { };
            return;
        }
        var frameRect = new RectangleGeometry(new Rect(PanX, PanY,
            snapshot.FrameMetadata.Width * Zoom, snapshot.FrameMetadata.Height * Zoom));
        drawing.PushClip(frameRect);
        pop = drawing.Pop;
    }

    private static void DrawProjection(DrawingContext drawing,
        IReadOnlyList<OverlayDrawCommand> commands, double zoom, double pixelsPerDip)
    {
        foreach (var command in commands)
        {
            var penBrush = CreateBrush(command.Style.StrokeColor);
            var pen = new Pen(penBrush, command.Style.StrokeWidth * zoom);
            if (command.Style.StrokePattern == OverlayStrokePattern.Dashed)
                pen.DashStyle = DashStyles.Dash;
            pen.Freeze();
            var fill = command.Style.FillColor.HasValue
                ? CreateBrush(command.Style.FillColor.Value) : null;

            switch (command.Kind)
            {
                case OverlayPrimitiveKind.Marker:
                    DrawMarker(drawing, command, pen, fill);
                    break;
                case OverlayPrimitiveKind.LineSegment:
                    DrawSegments(drawing, command.Segments, pen);
                    break;
                case OverlayPrimitiveKind.Arrow:
                    DrawArrow(drawing, command, pen);
                    break;
                case OverlayPrimitiveKind.Polyline:
                    DrawPolyline(drawing, command.Segments, pen, command.SegmentStartsNewFigure);
                    break;
                case OverlayPrimitiveKind.Polygon:
                case OverlayPrimitiveKind.AxisAlignedRectangle:
                case OverlayPrimitiveKind.RotatedRectangle:
                    DrawPolygon(drawing, command.Points, command.StrokeSegments,
                        command.SegmentStartsNewFigure, pen, fill);
                    break;
                case OverlayPrimitiveKind.Circle:
                case OverlayPrimitiveKind.Ellipse:
                    DrawEllipse(drawing, command, pen, fill);
                    break;
                case OverlayPrimitiveKind.Text:
                    DrawText(drawing, command, penBrush, zoom, pixelsPerDip);
                    break;
            }
        }
    }

    private static void DrawMarker(DrawingContext drawing, OverlayDrawCommand command,
        Pen pen, Brush? fill)
    {
        var center = command.Points[0];
        var half = command.Size / 2;
        switch (command.MarkerKind)
        {
            case OverlayMarkerKind.Cross:
                drawing.DrawLine(pen, new Point(center.X - half, center.Y - half),
                    new Point(center.X + half, center.Y + half));
                drawing.DrawLine(pen, new Point(center.X - half, center.Y + half),
                    new Point(center.X + half, center.Y - half));
                break;
            case OverlayMarkerKind.Plus:
                drawing.DrawLine(pen, new Point(center.X - half, center.Y),
                    new Point(center.X + half, center.Y));
                drawing.DrawLine(pen, new Point(center.X, center.Y - half),
                    new Point(center.X, center.Y + half));
                break;
            case OverlayMarkerKind.Circle:
                drawing.DrawEllipse(fill, pen, center, half, half);
                break;
            case OverlayMarkerKind.Square:
                drawing.DrawRectangle(fill, pen,
                    new Rect(center.X - half, center.Y - half, command.Size, command.Size));
                break;
        }
    }

    private static void DrawSegments(DrawingContext drawing,
        IReadOnlyList<OverlaySegment> segments, Pen pen)
    {
        foreach (var segment in segments)
            drawing.DrawLine(pen, segment.Start, segment.End);
    }

    private static void DrawPolyline(DrawingContext drawing,
        IReadOnlyList<OverlaySegment> segments, Pen pen,
        IReadOnlyList<bool>? segmentStartsNewFigure = null)
    {
        if (segments.Count == 0) return;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var previousEnd = segments[0].End;
            context.BeginFigure(segments[0].Start, false, false);
            context.LineTo(previousEnd, true, true);
            for (var index = 1; index < segments.Count; index++)
            {
                var segment = segments[index];
                var forcedBreak = segmentStartsNewFigure is not null &&
                    index < segmentStartsNewFigure.Count && segmentStartsNewFigure[index];
                if (forcedBreak || !SamePoint(previousEnd, segment.Start))
                    context.BeginFigure(segment.Start, false, false);
                context.LineTo(segment.End, true, true);
                previousEnd = segment.End;
            }
        }
        geometry.Freeze();
        drawing.DrawGeometry(null, pen, geometry);
    }

    private static bool SamePoint(Point first, Point second) =>
        Math.Abs(first.X - second.X) <= 1e-9 && Math.Abs(first.Y - second.Y) <= 1e-9;

    private static void DrawArrow(DrawingContext drawing, OverlayDrawCommand command, Pen pen)
    {
        DrawSegments(drawing, command.Segments, pen);
        DrawSegments(drawing, command.ArrowHeadSegments, pen);
    }

    private static void DrawPolygon(DrawingContext drawing, IReadOnlyList<Point> points,
        IReadOnlyList<OverlaySegment> strokeSegments,
        IReadOnlyList<bool> segmentStartsNewFigure, Pen pen, Brush? fill)
    {
        if (points.Count >= 3)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(points[0], fill is not null, true);
                context.PolyLineTo(points.Skip(1).ToArray(), true, true);
            }
            geometry.Freeze();
            if (fill is not null)
                drawing.DrawGeometry(fill, null, geometry);
        }
        DrawPolyline(drawing, strokeSegments, pen, segmentStartsNewFigure);
    }

    private static void DrawEllipse(DrawingContext drawing, OverlayDrawCommand command,
        Pen pen, Brush? fill)
    {
        var center = command.Points[0];
        if (command.RotationDegrees == 0)
        {
            drawing.DrawEllipse(fill, pen, center, command.RadiusX, command.RadiusY);
            return;
        }
        drawing.PushTransform(new RotateTransform(command.RotationDegrees, center.X, center.Y));
        try { drawing.DrawEllipse(fill, pen, center, command.RadiusX, command.RadiusY); }
        finally { drawing.Pop(); }
    }

    private static void DrawText(DrawingContext drawing, OverlayDrawCommand command,
        Brush brush, double zoom, double pixelsPerDip)
    {
        var formatted = new FormattedText(command.Text ?? string.Empty, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"),
            command.Style.TextSize * zoom, brush, pixelsPerDip);
        var anchor = command.Points[0];
        var x = command.AnchorKind switch
        {
            OverlayTextAnchor.TopCenter or OverlayTextAnchor.BottomCenter or OverlayTextAnchor.Center =>
                anchor.X - formatted.WidthIncludingTrailingWhitespace / 2,
            OverlayTextAnchor.TopRight or OverlayTextAnchor.BottomRight =>
                anchor.X - formatted.WidthIncludingTrailingWhitespace,
            _ => anchor.X
        };
        var y = command.AnchorKind switch
        {
            OverlayTextAnchor.Center => anchor.Y - formatted.Height / 2,
            OverlayTextAnchor.BottomLeft or OverlayTextAnchor.BottomCenter or OverlayTextAnchor.BottomRight =>
                anchor.Y - formatted.Height,
            _ => anchor.Y
        };
        drawing.DrawText(formatted, new Point(x, y));
    }

    private static SolidColorBrush CreateBrush(OverlayColor color)
    {
        var brush = new SolidColorBrush(Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));
        brush.Freeze();
        return brush;
    }

    private DpiScale GetDpiScale()
    {
        try { return VisualTreeHelper.GetDpi(this); }
        catch (InvalidOperationException) { return new DpiScale(1, 1); }
    }
}
