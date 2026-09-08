using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class OverlayRenderingTests
{
    [Fact]
    public void V114_W01_PresenterStartsUnavailableWithoutValidatedSnapshot()
    {
        RunSta(() =>
        {
            var presenter = new FrameOverlayPresenter();
            Assert.False(presenter.IsAvailable);
            Assert.Equal(FrameOverlayRenderStatus.Unavailable, presenter.RenderStatus);
            Assert.Equal("OverlaySnapshotUnavailable", presenter.RenderReasonCode);
            Assert.False(presenter.SourceImageAvailable);
            return 0;
        });
    }

    [Fact]
    public void V114_W02_AllClosedPrimitivesRemainInPainterOrderAndUsePrimitiveGeometry()
    {
        var style = new OverlayStyle(new OverlayColor(255, 20, 30), 1.5,
            new OverlayColor(10, 20, 30, 120), OverlayStrokePattern.Dashed,
            markerSize: 99, textSize: 14, textAnchor: OverlayTextAnchor.BottomRight);
        var strokeOnly = new OverlayStyle(new OverlayColor(255, 20, 30), 1.5,
            strokePattern: OverlayStrokePattern.Dashed, markerSize: 99, textSize: 14,
            textAnchor: OverlayTextAnchor.BottomRight);
        var primitives = new OverlayPrimitive[]
        {
            new OverlayMarker(new(2, 2), OverlayMarkerKind.Circle, 3, style),
            new OverlayLineSegment(new(0, 1), new(5, 1), strokeOnly),
            new OverlayArrow(new(1, 2), new(5, 2), strokeOnly),
            new OverlayPolyline(new[] { new OverlayPoint(0, 3), new(2, 4), new(5, 3) },
                strokeOnly),
            new OverlayPolygon(new[] { new OverlayPoint(1, 1), new(3, 1), new(2, 3) }, style),
            new OverlayAxisAlignedRectangle(new(3, 0), 2, 2, style),
            new OverlayRotatedRectangle(new(3, 3), 2, 2, 30, style),
            new OverlayCircle(new(5, 1), 1, style),
            new OverlayEllipse(new(5, 4), 1, 0.5, 15, style),
            new OverlayText(new(1, 4), "Diameter=🙂", strokeOnly,
                OverlayTextAnchor.TopLeft)
        };

        var projection = OverlayGeometryProjector.Project(Snapshot(primitives,
            Frame(VisionPixelFormat.Mono8, null, stride: 6, width: 6, height: 6)), 1, 0, 0);

        Assert.True(projection.Available, projection.ReasonCode);
        Assert.Equal(10, projection.Commands.Count);
        Assert.Equal(primitives.Select(item => item.Kind), projection.Commands.Select(item => item.Kind));
        Assert.Equal(3, projection.Commands[0].Size);
        Assert.Equal(OverlayTextAnchor.TopLeft, projection.Commands[^1].AnchorKind);
        Assert.Equal(OverlayStrokePattern.Dashed, projection.Commands[1].Style.StrokePattern);
    }

    [Fact]
    public void V114_W03_PixelCentersZoomPanAndExtremeCrossingLineAreFinite()
    {
        var marker = new OverlayMarker(new(1, 2), OverlayMarkerKind.Cross, 2,
            new OverlayStyle(new OverlayColor(255, 255, 255), markerSize: 100));
        var projection = OverlayGeometryProjector.Project(Snapshot(new OverlayPrimitive[] { marker }),
            zoom: 2, panX: 3, panY: -1);
        Assert.True(projection.Available, projection.ReasonCode);
        var point = projection.Commands.Single().Points.Single();
        Assert.Equal(6.0, point.X, 10); // (x + 0.5) * zoom + pan DIP
        Assert.Equal(4.0, point.Y, 10); // (y + 0.5) * zoom + pan DIP
        Assert.Equal(4, projection.Commands.Single().Size); // Marker.Size wins over style.MarkerSize, then zoom scales it.

        var extreme = OverlayGeometryProjector.Project(Snapshot(new OverlayPrimitive[]
        {
            new OverlayLineSegment(new(-1e308, 1), new(1e308, 1))
        }), 1, 0, 0);
        Assert.True(extreme.Available, extreme.ReasonCode);
        var segment = extreme.Commands.Single().Segments.Single();
        Assert.All(new[] { segment.Start, segment.End }, value =>
        {
            Assert.True(double.IsFinite(value.X));
            Assert.True(double.IsFinite(value.Y));
        });
        Assert.Equal(0, segment.Start.X, 6);
        Assert.Equal(4, segment.End.X, 6);
    }

    [Fact]
    public void V114_W04_FullyOutsideIsEmptyButUnrepresentableRadiusIsUnavailable()
    {
        var outside = OverlayGeometryProjector.Project(Snapshot(new OverlayPrimitive[]
        {
            new OverlayLineSegment(new(100, 100), new(200, 200))
        }), 1, 0, 0);
        Assert.True(outside.Available);
        Assert.False(outside.IsEmpty);
        Assert.Equal("OverlayOutsideViewport", outside.ReasonCode);

        var huge = OverlayGeometryProjector.Project(Snapshot(new OverlayPrimitive[]
        {
            new OverlayCircle(new(2, 2), 1e308)
        }), 1, 0, 0);
        Assert.False(huge.Available);
        Assert.Equal("OverlayGeometryNotRepresentable", huge.ReasonCode);
    }

    [Fact]
    public void V114_W05_FramePreviewCopiesMono16SafelyAndKeepsSourceIdentitySeparate()
    {
        var data = new byte[]
        {
            0x00, 0x00, 0x00, 0x02, 0xAA, 0xAA,
            0xFF, 0x03, 0x00, 0x02, 0xBB, 0xBB
        };
        var frame = new TestVisionFrame(Frame(VisionPixelFormat.Mono16, 10, stride: 6,
            width: 2, height: 2), data);

        var first = RunSta(() => FramePreviewImage.CopyFromFrame(frame));
        var second = RunSta(() => FramePreviewImage.CopyFromFrame(frame));

        Assert.True(first.BitmapSource.IsFrozen);
        Assert.Equal(PixelFormats.Gray8, first.BitmapSource.Format);
        Assert.Equal(first.SourcePixelHash, second.SourcePixelHash);
        Assert.NotEqual(first.SourcePreviewId, second.SourcePreviewId);
        Assert.Equal(0, ReadPixel(first.BitmapSource, 0, 0));
        Assert.Equal(128, ReadPixel(first.BitmapSource, 1, 0));
        Assert.Equal(255, ReadPixel(first.BitmapSource, 0, 1));
        Assert.Equal(128, ReadPixel(first.BitmapSource, 1, 1));
    }

    [Fact]
    public void V114_W06_PresenterRejectsMismatchedSourceButRendersStructuredOverlay()
    {
        var overlayFrame = Frame(VisionPixelFormat.Mono8, null, stride: 4);
        var differentFrame = Frame(VisionPixelFormat.Mono8, null, stride: 4,
            correlation: new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid()));
        var snapshot = Snapshot(new OverlayPrimitive[]
        {
            new OverlayText(new(1, 1), "结果🙂", new OverlayStyle(new OverlayColor(255, 255, 0)))
        }, overlayFrame);
        var source = RunSta(() => FramePreviewImage.CopyFromFrame(
            new TestVisionFrame(differentFrame, new byte[differentFrame.FullBufferLayoutLength])));

        var rendered = RunSta(() =>
        {
            var presenter = new FrameOverlayPresenter { Snapshot = snapshot, SourceImage = source };
            Assert.True(presenter.IsAvailable);
            Assert.False(presenter.SourceImageAvailable);
            Assert.Equal("SourceImageMetadataMismatch", presenter.SourceImageReasonCode);
            return RenderHidden(presenter);
        });

        Assert.True(rendered.BitmapSource.IsFrozen);
        Assert.Null(rendered.SourcePreviewId);
        Assert.Null(rendered.SourcePixelHash);
        Assert.Equal(snapshot.OverlaySetId, rendered.OverlaySetId);
        Assert.Equal(snapshot.ContentHash, rendered.OverlayContentHash);
    }

    [Fact]
    public void V114_W07_MatchingSourceAndEmptyOverlayRenderWithFrozenDerivedIdentity()
    {
        var metadata = Frame(VisionPixelFormat.Mono8, null, stride: 4);
        var snapshot = Snapshot(Array.Empty<OverlayPrimitive>(), metadata);
        var source = RunSta(() => FramePreviewImage.CopyFromFrame(
            new TestVisionFrame(metadata, new byte[metadata.FullBufferLayoutLength])));

        var rendered = RunSta(() =>
        {
            var presenter = new FrameOverlayPresenter { Snapshot = snapshot, SourceImage = source };
            Assert.True(presenter.IsAvailable);
            Assert.True(presenter.IsEmpty);
            Assert.Equal("OverlayEmpty", presenter.RenderReasonCode);
            return RenderHidden(presenter);
        });

        Assert.True(rendered.BitmapSource.IsFrozen);
        Assert.NotEqual(Guid.Empty, rendered.DerivedPreviewId);
        Assert.Equal(64, rendered.DerivedPixelHash.Length);
        Assert.NotEqual(source.SourcePreviewId, rendered.DerivedPreviewId);
        Assert.Equal(source.SourcePreviewId, rendered.SourcePreviewId);
        Assert.Equal(source.SourcePixelHash, rendered.SourcePixelHash);
        Assert.NotEqual(source.SourcePreviewId, rendered.OverlaySetId);
        Assert.Equal(1, rendered.Zoom);
    }

    [Fact]
    public void V114_W08_BudgetsAndUnsafeTextFailClosedWithoutTruncation()
    {
        var contract = new OverlayContract("Test.Overlay", "1", maximumElements: 1,
            maximumTotalPoints: 4, maximumPointsPerElement: 4, maximumTextLength: 65_536);
        var schema = Schema(contract);
        var frame = Frame(VisionPixelFormat.Mono8, null, stride: 4);
        var tooMany = new OutputOverlaySet(contract, new OverlayPrimitive[]
        {
            new OverlayMarker(new(1, 1), OverlayMarkerKind.Cross, 1),
            new OverlayMarker(new(2, 2), OverlayMarkerKind.Cross, 1)
        });
        var snapshot = new FrameOverlaySnapshot(Guid.NewGuid(), frame, schema, tooMany, Hash());
        var projection = OverlayGeometryProjector.Project(snapshot, 1, 0, 0);
        Assert.False(projection.Available);
        Assert.Equal("OverlayDisplayBudgetExceeded", projection.ReasonCode);

        var unsafeText = Snapshot(new OverlayPrimitive[]
        {
            new OverlayText(new(1, 1), "  rm -rf tmp")
        });
        projection = OverlayGeometryProjector.Project(unsafeText, 1, 0, 0);
        Assert.False(projection.Available);
        Assert.Equal("OverlayTextNotRenderable", projection.ReasonCode);
    }

    [Fact]
    public void V114_W09_Bgr24PreviewKeepsRawUnannotatedBytes()
    {
        var metadata = Frame(VisionPixelFormat.Bgr24, null, stride: 9, width: 2, height: 2);
        var data = Enumerable.Range(1, checked((int)metadata.FullBufferLayoutLength))
            .Select(value => (byte)value).ToArray();
        var preview = RunSta(() => FramePreviewImage.CopyFromFrame(new TestVisionFrame(metadata, data)));

        Assert.True(preview.BitmapSource.IsFrozen);
        Assert.Equal(PixelFormats.Bgr24, preview.BitmapSource.Format);
        var copied = new byte[metadata.ValidRowBytes * metadata.Height];
        preview.BitmapSource.CopyPixels(copied, metadata.ValidRowBytes, 0);
        Assert.Equal(data.Take(metadata.ValidRowBytes)
            .Concat(data.Skip(metadata.StrideBytes).Take(metadata.ValidRowBytes)), copied);
    }

    [Fact]
    public void V114_W10_ZoomChangesDipViewportOnceAndKeepsDerivedOutputDistinct()
    {
        var snapshot = Snapshot(new OverlayPrimitive[]
        {
            new OverlayLineSegment(new(0, 0), new(3, 1),
                new OverlayStyle(new OverlayColor(255, 255, 255), strokeWidth: 1))
        });
        var outputs = RunSta(() =>
        {
            var presenter = new FrameOverlayPresenter { Snapshot = snapshot };
            var first = presenter.RenderPreview();
            presenter.Zoom = 2;
            var second = presenter.RenderPreview();
            return (first, second);
        });

        Assert.True(outputs.first.BitmapSource.PixelWidth >= 1);
        Assert.True(outputs.first.BitmapSource.PixelHeight >= 1);
        Assert.Equal(outputs.first.BitmapSource.PixelWidth * 2, outputs.second.BitmapSource.PixelWidth);
        Assert.Equal(outputs.first.BitmapSource.PixelHeight * 2, outputs.second.BitmapSource.PixelHeight);
        Assert.NotEqual(outputs.first.DerivedPixelHash, outputs.second.DerivedPixelHash);
    }

    [Fact]
    public void V114_W11_RasterStrokeArrowAndTextScaleWithZoomAndClippedArrowHasNoMovedHead()
    {
        var metadata = Frame(VisionPixelFormat.Mono8, null, stride: 8, width: 8, height: 6);
        var lineSnapshot = Snapshot(new OverlayPrimitive[]
        {
            new OverlayLineSegment(new(0, 2), new(7, 2),
                new OverlayStyle(new OverlayColor(255, 255, 255), strokeWidth: 2))
        }, metadata);
        var lineRenders = RunSta(() =>
        {
            var presenter = new FrameOverlayPresenter { Snapshot = lineSnapshot };
            var first = presenter.RenderPreview();
            presenter.Zoom = 2;
            var second = presenter.RenderPreview();
            return (first, second);
        });
        var lineFirst = InkBounds(lineRenders.first.BitmapSource);
        var lineSecond = InkBounds(lineRenders.second.BitmapSource);
        var firstCenterColumn = lineRenders.first.BitmapSource.PixelWidth / 2;
        var secondCenterColumn = lineRenders.second.BitmapSource.PixelWidth / 2;
        var firstAlphaCoverage = AlphaCoverage(lineRenders.first.BitmapSource, firstCenterColumn);
        var secondAlphaCoverage = AlphaCoverage(lineRenders.second.BitmapSource, secondCenterColumn);
        Assert.True(firstAlphaCoverage > 0 && secondAlphaCoverage > firstAlphaCoverage,
            $"stroke coverage disappeared at center: {firstAlphaCoverage}->{secondAlphaCoverage}");
        var coverageRatio = secondAlphaCoverage / (double)firstAlphaCoverage;
        Assert.InRange(coverageRatio, 1.75, 2.25);
        Assert.True(lineSecond.Width >= lineFirst.Width * 2 - 2,
            $"stroke extent did not scale: {lineFirst.Width}->{lineSecond.Width}");

        var arrowSnapshot = Snapshot(new OverlayPrimitive[]
        {
            new OverlayArrow(new(0, 3), new(8, 3),
                new OverlayStyle(new OverlayColor(255, 255, 255), strokeWidth: 1, markerSize: 0.1))
        }, metadata);
        var arrowProjection = OverlayGeometryProjector.Project(arrowSnapshot, 1, 0, 0);
        Assert.True(arrowProjection.Available, arrowProjection.ReasonCode);
        Assert.Empty(arrowProjection.Commands.Single().ArrowHeadSegments);
        var partialArrow = Snapshot(new OverlayPrimitive[]
        {
            new OverlayArrow(new(0, 3), new(8, 3),
                new OverlayStyle(new OverlayColor(255, 255, 255), strokeWidth: 1, markerSize: 3))
        }, metadata);
        var partialProjection = OverlayGeometryProjector.Project(partialArrow, 1, 0, 0);
        Assert.True(partialProjection.Available, partialProjection.ReasonCode);
        Assert.NotEmpty(partialProjection.Commands.Single().ArrowHeadSegments);
        Assert.All(partialProjection.Commands.Single().ArrowHeadSegments,
            segment => Assert.True(segment.Start.X <= metadata.Width && segment.End.X <= metadata.Width));

        var textSnapshot = Snapshot(new OverlayPrimitive[]
        {
            new OverlayText(new(1, 1), "Zoom", new OverlayStyle(new OverlayColor(255, 255, 255),
                textSize: 12), OverlayTextAnchor.TopLeft)
        }, metadata);
        var textRenders = RunSta(() =>
        {
            var presenter = new FrameOverlayPresenter { Snapshot = textSnapshot };
            var first = presenter.RenderPreview();
            presenter.Zoom = 2;
            var second = presenter.RenderPreview();
            return (first, second);
        });
        var textFirst = InkBounds(textRenders.first.BitmapSource);
        var textSecond = InkBounds(textRenders.second.BitmapSource);
        Assert.True(textSecond.Width >= textFirst.Width * 1.5,
            $"text extent did not scale: {textFirst.Width}->{textSecond.Width}");
    }

    [Fact]
    public void V114_W16_ArrowHeadRemainsVisibleWhenShaftIsOutsideFrame()
    {
        var metadata = Frame(VisionPixelFormat.Mono8, null, stride: 8, width: 8, height: 8);
        var snapshot = Snapshot(new OverlayPrimitive[]
        {
            // The shaft is wholly left of x=-0.5.  The original head wing
            // crosses into the frame and must still be independently clipped.
            new OverlayArrow(new(-1, 1), new(-1, 5),
                new OverlayStyle(new OverlayColor(255, 255, 255),
                    strokeWidth: 1, markerSize: 2))
        }, metadata);

        var projection = OverlayGeometryProjector.Project(snapshot, 1, 0, 0);

        Assert.True(projection.Available, projection.ReasonCode);
        var command = Assert.Single(projection.Commands);
        Assert.Empty(command.Segments);
        Assert.NotEmpty(command.ArrowHeadSegments);
    }

    [Fact]
    public void V114_W12_SourcePixelCentersAlignWithDipPanAndFrameClipRemovesOutsideMarkerInk()
    {
        var metadata = Frame(VisionPixelFormat.Mono8, null, stride: 4, width: 4, height: 4);
        var source = RunSta(() => FramePreviewImage.CopyFromFrame(
            new TestVisionFrame(metadata, new byte[checked((int)metadata.FullBufferLayoutLength)])));
        var snapshot = Snapshot(new OverlayPrimitive[]
        {
            new OverlayMarker(new(1, 2), OverlayMarkerKind.Cross, 1,
                new OverlayStyle(new OverlayColor(255, 255, 255), strokeWidth: 1)),
            new OverlayMarker(new(-0.25, 2), OverlayMarkerKind.Cross, 1,
                new OverlayStyle(new OverlayColor(255, 255, 255), strokeWidth: 1)),
            new OverlayEllipse(new(-0.25, 1), 1, 0.75, 0,
                new OverlayStyle(new OverlayColor(255, 255, 255), strokeWidth: 1))
        }, metadata);

        var rendered = RunSta(() =>
        {
            var presenter = new FrameOverlayPresenter
            {
                Snapshot = snapshot,
                SourceImage = source,
                PanX = 1,
                PanY = 0
            };
            return presenter.RenderPreview();
        });

        var bounds = InkBounds(rendered.BitmapSource);
        var frameLeftPixel = (int)Math.Ceiling(rendered.DpiScaleX);
        Assert.True(bounds.Left >= frameLeftPixel,
            $"ink escaped frame clip: left={bounds.Left}, frameLeft={frameLeftPixel}");
        var expectedCenter = (int)Math.Round((1.5 + 1) * rendered.DpiScaleX,
            MidpointRounding.AwayFromZero);
        Assert.InRange(expectedCenter, bounds.Left, bounds.Right);
        Assert.Equal(source.SourcePreviewId, rendered.SourcePreviewId);
    }

    [Fact]
    public void V114_W13_LargeFilledPolygonHasNoSyntheticFrameStrokeAndPainterOrderWins()
    {
        var metadata = Frame(VisionPixelFormat.Mono8, null, stride: 4, width: 4, height: 4);
        var outerStyle = new OverlayStyle(new OverlayColor(255, 255, 255), strokeWidth: 2,
            fillColor: new OverlayColor(255, 0, 0));
        var innerStyle = new OverlayStyle(new OverlayColor(0, 0, 255), strokeWidth: 1,
            fillColor: new OverlayColor(0, 0, 255));
        var snapshot = Snapshot(new OverlayPrimitive[]
        {
            new OverlayPolygon(new[]
            {
                new OverlayPoint(-10, -10), new OverlayPoint(10, -10),
                new OverlayPoint(10, 10), new OverlayPoint(-10, 10)
            }, outerStyle),
            new OverlayPolygon(new[]
            {
                new OverlayPoint(1, 1), new OverlayPoint(3, 1),
                new OverlayPoint(3, 3), new OverlayPoint(1, 3)
            }, innerStyle)
        }, metadata);

        var rendered = RunSta(() => new FrameOverlayPresenter { Snapshot = snapshot }.RenderPreview());
        var edge = PixelAt(rendered.BitmapSource, 0, 0);
        var center = PixelAt(rendered.BitmapSource,
            rendered.BitmapSource.PixelWidth / 2, rendered.BitmapSource.PixelHeight / 2);
        Assert.True(edge.A > 0 && edge.R >= 200 && edge.G <= 80 && edge.B <= 80,
            $"outer fill or edge stroke was wrong at frame boundary: {edge}");
        Assert.True(center.A > 0 && center.B > center.R + 50,
            $"painter order did not preserve inner fill: {center}");
    }

    [Fact]
    public void V114_W14_LegalMaximumTextAtLargeZoomBecomesWholeViewUnavailable()
    {
        var metadata = Frame(VisionPixelFormat.Mono8, null, stride: 4, width: 4, height: 4);
        var snapshot = Snapshot(new OverlayPrimitive[]
        {
            new OverlayText(new(1, 1), "safe", new OverlayStyle(new OverlayColor(255, 255, 255),
                textSize: 4096))
        }, metadata);

        var state = RunSta(() =>
        {
            var presenter = new FrameOverlayPresenter { Snapshot = snapshot, Zoom = 64 };
            return (presenter.IsAvailable, presenter.RenderReasonCode, presenter.StatusMessage);
        });

        Assert.False(state.IsAvailable);
        Assert.Equal("OverlayStyleNotRenderable", state.RenderReasonCode);
        Assert.Contains("不可用", state.StatusMessage);
    }

    [Fact]
    public void V114_W15_DashedPolylineKeepsPhaseAcrossContinuousVertexAndGapsStaySeparate()
    {
        var metadata = Frame(VisionPixelFormat.Mono8, null, stride: 8, width: 8, height: 4);
        var style = new OverlayStyle(new OverlayColor(255, 255, 255), strokeWidth: 1,
            strokePattern: OverlayStrokePattern.Dashed);
        var snapshot = Snapshot(new OverlayPrimitive[]
        {
            new OverlayPolyline(new[]
            {
                new OverlayPoint(0, 1), new OverlayPoint(2, 1), new OverlayPoint(5, 1)
            }, style)
        }, metadata);
        var rendered = RunSta(() => new FrameOverlayPresenter { Snapshot = snapshot }.RenderPreview());

        var equivalentStraightLine = Snapshot(new OverlayPrimitive[]
        {
            new OverlayLineSegment(new(0, 1), new(5, 1), style)
        }, metadata);
        var straightRendered = RunSta(() =>
            new FrameOverlayPresenter { Snapshot = equivalentStraightLine }.RenderPreview());
        Assert.Equal(ReadPixels(rendered.BitmapSource), ReadPixels(straightRendered.BitmapSource));

        var inkColumns = InkColumns(rendered.BitmapSource);
        var firstInk = Array.FindIndex(inkColumns, value => value);
        var lastInk = Array.FindLastIndex(inkColumns, value => value);
        Assert.True(firstInk >= 0 && lastInk > firstInk, "expected dashed line ink");
        var gapStart = -1;
        for (var index = firstInk + 1; index < lastInk; index++)
        {
            if (!inkColumns[index] && inkColumns[..index].Any(value => value) &&
                inkColumns[(index + 1)..].Any(value => value))
            {
                gapStart = index;
                break;
            }
        }
        Assert.True(gapStart >= 0,
            $"expected a transparent dash gap between solid runs: {string.Join(string.Empty, inkColumns.Select(value => value ? '#' : '.'))}");

        var splitSnapshot = Snapshot(new OverlayPrimitive[]
        {
            new OverlayPolyline(new[]
            {
                new OverlayPoint(1, 1), new OverlayPoint(10, 1), new OverlayPoint(10, 3),
                new OverlayPoint(1, 3)
            }, style)
        }, metadata);
        var split = OverlayGeometryProjector.Project(splitSnapshot, 1, 0, 0);
        Assert.True(split.Available, split.ReasonCode);
        Assert.Equal(2, split.Commands.Single().Segments.Count);
        Assert.Equal(new[] { true, true }, split.Commands.Single().SegmentStartsNewFigure);
    }

    private static FrameOverlaySnapshot Snapshot(IEnumerable<OverlayPrimitive> primitives,
        FrameMetadata? metadata = null)
    {
        metadata ??= Frame(VisionPixelFormat.Mono8, null, stride: 4);
        var contract = new OverlayContract("Test.Overlay", "1", maximumElements: 64,
            maximumTotalPoints: 256, maximumPointsPerElement: 64, maximumTextLength: 65_536);
        var schema = Schema(contract);
        return new FrameOverlaySnapshot(Guid.NewGuid(), metadata, schema,
            new OutputOverlaySet(contract, primitives), Hash());
    }

    private static AlgorithmResultSchema Schema(OverlayContract contract) =>
        new("Test.Result", "1", Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>(), contract);

    private static FrameMetadata Frame(VisionPixelFormat pixelFormat, int? validBits, int stride,
        ExecutionCorrelationId? correlation = null, int width = 4, int height = 2)
    {
        var bytesPerPixel = pixelFormat switch
        {
            VisionPixelFormat.Mono8 => 1,
            VisionPixelFormat.Mono16 => 2,
            VisionPixelFormat.Bgr24 => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat))
        };
        var camera = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            1000, 0, new RegionOfInterest(0, 0, width, height), pixelFormat, validBits, 1000, 0,
            pixelFormat == VisionPixelFormat.Bgr24 ? new WhiteBalanceRgb(1, 1, 1) : null);
        return new FrameMetadata(correlation ?? new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid()),
            "Primary", width, height, Math.Max(stride, width * bytesPerPixel), pixelFormat, validBits,
            DateTimeOffset.UtcNow, camera);
    }

    private static string Hash() => new('A', 64);

    private static byte ReadPixel(BitmapSource bitmap, int x, int y)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth, 0);
        return pixels[y * bitmap.PixelWidth + x];
    }

    private static PixelBounds InkBounds(BitmapSource bitmap)
    {
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        var left = bitmap.PixelWidth;
        var top = bitmap.PixelHeight;
        var right = -1;
        var bottom = -1;
        var count = 0;
        for (var y = 0; y < bitmap.PixelHeight; y++)
        {
            for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                if (pixels[y * stride + x * 4 + 3] < 16) continue;
                count++;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        Assert.True(count > 0, "expected at least one rendered pixel");
        return new PixelBounds(left, top, right, bottom, count);
    }

    private static PixelColor PixelAt(BitmapSource bitmap, int x, int y)
    {
        x = Math.Clamp(x, 0, bitmap.PixelWidth - 1);
        y = Math.Clamp(y, 0, bitmap.PixelHeight - 1);
        var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        var stride = checked(bitmap.PixelWidth * 4);
        bitmap.CopyPixels(pixels, stride, 0);
        var offset = checked(y * stride + x * 4);
        return new PixelColor(pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
    }

    private static int AlphaCoverage(BitmapSource bitmap, int column)
    {
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        var coverage = 0;
        for (var y = 0; y < bitmap.PixelHeight; y++)
            coverage += pixels[y * stride + column * 4 + 3];
        return coverage;
    }

    private static bool[] InkColumns(BitmapSource bitmap)
    {
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        var columns = new bool[bitmap.PixelWidth];
        for (var x = 0; x < bitmap.PixelWidth; x++)
        {
            for (var y = 0; y < bitmap.PixelHeight; y++)
            {
                if (pixels[y * stride + x * 4 + 3] < 16) continue;
                columns[x] = true;
                break;
            }
        }
        return columns;
    }

    private static byte[] ReadPixels(BitmapSource bitmap)
    {
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static RenderedOverlayPreview RenderHidden(FrameOverlayPresenter presenter)
    {
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new Window
        {
            Content = presenter,
            Width = presenter.Snapshot!.FrameMetadata.Width + 20,
            Height = presenter.Snapshot.FrameMetadata.Height + 20,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        try
        {
            window.Show();
            presenter.Measure(new Size(100, 100));
            presenter.Arrange(new Rect(0, 0, 100, 100));
            presenter.UpdateLayout();
            return presenter.RenderPreview();
        }
        finally
        {
            window.Close();
            if (Application.Current is null && application.Dispatcher.HasShutdownStarted == false)
                application.Shutdown();
        }
    }

    private static T RunSta<T>(Func<T> callback)
    {
        T? value = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { value = callback(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));
        if (thread.IsAlive) throw new TimeoutException("STA callback did not complete");
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
        return value!;
    }

    private readonly record struct PixelBounds(int Left, int Top, int Right, int Bottom, int Count)
    {
        public int Width => Right - Left + 1;
        public int Height => Bottom - Top + 1;
    }

    private readonly record struct PixelColor(byte B, byte G, byte R, byte A);

    private sealed class TestVisionFrame : VisionFrame
    {
        private readonly byte[] _buffer;
        public TestVisionFrame(FrameMetadata metadata, byte[] buffer) : base(metadata)
        { _buffer = buffer; }
        public override bool IsLoanActive => true;
        public override ReadOnlySpan<byte> GetRowSpan(int row) =>
            _buffer.AsSpan(checked(row * StrideBytes), Metadata.ValidRowBytes);
    }
}
