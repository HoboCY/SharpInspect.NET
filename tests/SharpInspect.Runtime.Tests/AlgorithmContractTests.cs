using System.Collections;
using System.Globalization;
using System.Reflection;
using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class AlgorithmContractTests
{
    [Fact]
    public void V110_A01_DescriptorExposesTheImmutablePreparationSurface()
    {
        var configuration = ConfigurationSchema();
        var overlay = new OverlayContract("overlay.demo", "1", maximumElements: 8,
            maximumTotalPoints: 64, maximumPointsPerElement: 8, maximumTextLength: 64);
        var result = ResultSchema(overlay);
        var descriptor = new AlgorithmDescriptor(
            new AlgorithmIdentity("algorithm.demo", "2026.1"), configuration, result);

        Assert.Equal("algorithm.demo", descriptor.Identity.Id);
        Assert.Equal("2026.1", descriptor.Identity.Version);
        Assert.Same(configuration, descriptor.ConfigurationSchema);
        Assert.Same(result, descriptor.ResultSchema);
        Assert.Null(typeof(AlgorithmIdentity).GetProperty(nameof(AlgorithmIdentity.Id))!.SetMethod);
        Assert.Null(typeof(AlgorithmDescriptor).GetProperty(nameof(AlgorithmDescriptor.ResultSchema))!.SetMethod);
        Assert.Throws<ArgumentException>(() => new AlgorithmIdentity("algorithm/demo", "1"));
        Assert.Throws<ArgumentException>(() => new AlgorithmIdentity("algorithm.demo", " "));
    }

    [Fact]
    public void V110_A02_ExecutionContextBindsAValidCorrelationToTheBorrowedFrame()
    {
        var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
        var frame = new TestFrame(correlation);
        var snapshot = AlgorithmConfigurationSnapshot.Create(ConfigurationSchema(),
            new[] { new AlgorithmConfigurationEntry("threshold", "mm", AlgorithmScalarValue.FromFloat64(3.5)) });
        var sink = new RecordingDiagnosticSink();

        var context = new AlgorithmExecutionContext(correlation, snapshot, frame, sink);

        Assert.Same(correlation, context.Correlation);
        Assert.Same(snapshot, context.Configuration);
        Assert.Same(frame, context.Frame);
        Assert.Same(sink, context.Diagnostics);

        var otherCorrelation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => new AlgorithmExecutionContext(
            correlation, snapshot, new TestFrame(otherCorrelation), sink));
        Assert.Throws<ArgumentException>(() => new AlgorithmExecutionContext(
            new ExecutionCorrelationId((ExecutionKind)99, Guid.NewGuid()), snapshot, frame, sink));
        Assert.Throws<ArgumentException>(() => new AlgorithmExecutionContext(
            new ExecutionCorrelationId(ExecutionKind.Manual, Guid.Empty), snapshot, frame, sink));
    }

    [Fact]
    public void V110_A03_VisionFrameExposesOnlyReadOnlyLoanRowsAndMetadata()
    {
        var correlation = new ExecutionCorrelationId(ExecutionKind.Production, Guid.NewGuid());
        var frame = new TestFrame(correlation);

        Assert.True(frame.IsLoanActive);
        Assert.Equal(correlation, frame.Correlation);
        Assert.Equal("camera.primary", frame.LogicalCameraId);
        Assert.Equal(2, frame.Width);
        Assert.Equal(2, frame.Height);
        Assert.Equal(2, frame.StrideBytes);
        Assert.Equal(VisionPixelFormat.Mono8, frame.PixelFormat);
        Assert.Null(frame.ValidBits);
        Assert.Equal(new byte[] { 3, 4 }, frame.GetRowSpan(1).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.GetRowSpan(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.GetRowSpan(frame.Height));
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(VisionFrame)));
        Assert.Equal(10, new Mono16Frame(correlation, 10).ValidBits);
        Assert.Equal(12, new Mono16Frame(correlation, 12).ValidBits);
        Assert.Equal(16, new Mono16Frame(correlation, 16).ValidBits);
        Assert.Throws<ArgumentOutOfRangeException>(() => new InvalidFrame(correlation));
        Assert.Throws<ArgumentException>(() => new Mono8WithBitsFrame(correlation));
    }

    [Fact]
    public void V110_A04_ResultSchemaCopiesInputsAndHashesCanonicalContent()
    {
        var overlay = new OverlayContract("overlay.demo", "1", maximumElements: 8,
            maximumTotalPoints: 64, maximumPointsPerElement: 8, maximumTextLength: 64);
        var first = new List<AlgorithmFieldDefinition>
        {
            new("score", AlgorithmScalarType.Float64, "percent", required: true,
                constraints: new AlgorithmScalarConstraints(minFloat64: 0, maxFloat64: 100)),
            new("label", AlgorithmScalarType.String, "text", required: false)
        };
        var reasons = new List<string> { "NO_FRAME", "MODEL_UNKNOWN" };
        var schema = new AlgorithmResultSchema("result.demo", "1", first, reasons, overlay);
        var expectedHash = schema.ContentHash;
        first.Clear();
        reasons.Add("MUTATED_INPUT");

        var reordered = new AlgorithmResultSchema("result.demo", "1",
            new[] { new AlgorithmFieldDefinition("label", AlgorithmScalarType.String, "text", false),
                new AlgorithmFieldDefinition("score", AlgorithmScalarType.Float64, "percent", true,
                    new AlgorithmScalarConstraints(minFloat64: 0, maxFloat64: 100)) },
            new[] { "MODEL_UNKNOWN", "NO_FRAME" }, overlay);

        Assert.Equal(expectedHash, schema.ContentHash);
        Assert.Equal(expectedHash, reordered.ContentHash);
        Assert.Equal(2, schema.Measurements.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<AlgorithmFieldDefinition>)schema.Measurements)
            .Add(new AlgorithmFieldDefinition("new", AlgorithmScalarType.String, "text", false)));
        Assert.Throws<ArgumentException>(() => new AlgorithmResultSchema("result.demo", "1",
            new[] { new AlgorithmFieldDefinition("score", AlgorithmScalarType.Float64, "percent", true,
                authoringDefault: AlgorithmScalarValue.FromFloat64(50)) }, Array.Empty<string>(), overlay));
        Assert.NotEqual(expectedHash, new AlgorithmResultSchema("result.demo", "2",
            schema.Measurements, schema.ReasonCodes, overlay).ContentHash);
        var negativeZero = new AlgorithmResultSchema("result.zero", "1", new[]
        {
            new AlgorithmFieldDefinition("score", AlgorithmScalarType.Float64, "percent", true,
                new AlgorithmScalarConstraints(minFloat64: -0d, maxFloat64: 100))
        }, Array.Empty<string>(), overlay);
        var positiveZero = new AlgorithmResultSchema("result.zero", "1", new[]
        {
            new AlgorithmFieldDefinition("score", AlgorithmScalarType.Float64, "percent", true,
                new AlgorithmScalarConstraints(minFloat64: 0d, maxFloat64: 100))
        }, Array.Empty<string>(), overlay);
        Assert.Equal(negativeZero.ContentHash, positiveZero.ContentHash);
        Assert.NotEqual(overlay.ContentHash, new OverlayContract("overlay.demo", "2", 8, 64, 8, 64).ContentHash);
    }

    [Fact]
    public void V110_A05_ResultIsWholeAndRejectsUnknownWithoutReasonOrDuplicateMeasurements()
    {
        var overlay = new OverlayContract("overlay.demo", "1");
        var overlaySet = new OutputOverlaySet(overlay, new OverlayPrimitive[]
        {
            new OverlayMarker(new OverlayPoint(1, 2), OverlayMarkerKind.Cross, 4)
        });
        var measurement = new AlgorithmMeasurement("score", "percent", AlgorithmScalarValue.FromFloat64(99));

        var result = new AlgorithmResult(InspectionDecision.Pass, null, new[] { measurement }, overlaySet);

        Assert.Equal(InspectionDecision.Pass, result.Decision);
        Assert.Null(result.ReasonCode);
        Assert.Same(overlaySet, result.OverlaySet);
        Assert.Throws<ArgumentException>(() => new AlgorithmResult(InspectionDecision.Unknown, null,
            Array.Empty<AlgorithmMeasurement>(), overlaySet));
        Assert.Throws<ArgumentException>(() => new AlgorithmResult(InspectionDecision.Unknown, " ",
            Array.Empty<AlgorithmMeasurement>(), overlaySet));
        Assert.Throws<ArgumentException>(() => new AlgorithmResult(InspectionDecision.Pass, null,
            new[] { measurement, measurement }, overlaySet));
        Assert.Throws<ArgumentException>(() => new AlgorithmResult(InspectionDecision.Pass, "not allowed/space",
            Array.Empty<AlgorithmMeasurement>(), overlaySet));
        Assert.Throws<ArgumentException>(() => new AlgorithmMeasurement("score", "percent per frame",
            AlgorithmScalarValue.FromFloat64(1)));
        Assert.Throws<NotSupportedException>(() => ((IList<AlgorithmMeasurement>)result.Measurements).Clear());
    }

    [Fact]
    public void V110_A06_OverlayContractIsClosedFiniteAndBounded()
    {
        var style = new OverlayStyle(new OverlayColor(10, 20, 30), strokeWidth: 2,
            fillColor: new OverlayColor(10, 20, 30, 40), markerSize: 5, textSize: 14);
        var primitives = new OverlayPrimitive[]
        {
            new OverlayMarker(new OverlayPoint(1, 2), OverlayMarkerKind.Circle, 5, style),
            new OverlayLineSegment(new OverlayPoint(0, 0), new OverlayPoint(10, 10), style),
            new OverlayArrow(new OverlayPoint(10, 10), new OverlayPoint(20, 20), style),
            new OverlayPolyline(new[] { new OverlayPoint(0, 0), new OverlayPoint(1, 1) }, style),
            new OverlayPolygon(new[] { new OverlayPoint(0, 0), new OverlayPoint(1, 0), new OverlayPoint(0, 1) }, style),
            new OverlayAxisAlignedRectangle(new OverlayPoint(0, 0), 10, 8, style),
            new OverlayRotatedRectangle(new OverlayPoint(4, 4), 10, 8, 45, style),
            new OverlayCircle(new OverlayPoint(4, 4), 2, style),
            new OverlayEllipse(new OverlayPoint(4, 4), 2, 3, 90, style),
            new OverlayText(new OverlayPoint(4, 4), "plain text", style)
        };
        var set = new OutputOverlaySet("overlay.demo", "1", primitives);

        Assert.Equal(10, set.Primitives.Count);
        Assert.Equal(21, set.TotalPointCount);
        Assert.Equal(OverlayPrimitiveKind.Text, set.Primitives[^1].Kind);
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayCircle(new OverlayPoint(0, 0), double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayStyle(new OverlayColor(0, 0, 0), 0));
        Assert.Throws<ArgumentException>(() => new OverlayText(new OverlayPoint(0, 0), "bad\ntext"));
        Assert.Throws<ArgumentException>(() => new OverlayPolyline(Array.Empty<OverlayPoint>()));
        Assert.Throws<ArgumentException>(() => new OutputOverlaySet("overlay.demo", "1",
            Enumerable.Repeat<OverlayPrimitive>(new OverlayMarker(new OverlayPoint(0, 0), OverlayMarkerKind.Cross, 1), 4097)));
        var baseConstructors = typeof(OverlayPrimitive).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.Single(baseConstructors);
        Assert.All(baseConstructors, constructor =>
            Assert.True(constructor.IsFamilyAndAssembly));
    }

    [Fact]
    public void V110_A07_DiagnosticsAreBoundedTypedAndDefensivelyCopied()
    {
        var fields = new List<AlgorithmDiagnosticField>
        {
            new("camera", AlgorithmScalarValue.FromEnum("primary")),
            new("retry", AlgorithmScalarValue.FromInt64(2))
        };
        var diagnostic = new AlgorithmDiagnosticEvent("FrameUnavailable", fields);
        fields.Clear();

        Assert.Equal(2, diagnostic.Fields.Count);
        Assert.Equal(AlgorithmScalarType.Enum, diagnostic.Fields[0].Value.Type);
        Assert.Throws<NotSupportedException>(() => ((IList<AlgorithmDiagnosticField>)diagnostic.Fields).Clear());
        Assert.Throws<ArgumentException>(() => new AlgorithmDiagnosticEvent("FrameUnavailable",
            new[] { new AlgorithmDiagnosticField("camera", AlgorithmScalarValue.FromEnum("a")),
                new AlgorithmDiagnosticField("camera", AlgorithmScalarValue.FromEnum("b")) }));
        Assert.Throws<ArgumentException>(() => new AlgorithmDiagnosticEvent("FrameUnavailable",
            Enumerable.Range(0, 33).Select(index => new AlgorithmDiagnosticField(
                "f" + index.ToString(CultureInfo.InvariantCulture), AlgorithmScalarValue.FromInt64(index)))));
    }

    [Fact]
    public void V110_A08_ContentHashesDoNotDependOnCurrentCulture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var baseline = new OverlayContract("overlay.culture", "1", 9, 101, 7, 33).ContentHash;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var changed = new OverlayContract("overlay.culture", "1", 9, 101, 7, 33).ContentHash;

            Assert.Equal(baseline, changed);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void V110_A09_FactoryAndAlgorithmInterfacesStayLifecycleAndDataBounded()
    {
        var factoryMethods = typeof(IVisionAlgorithmFactory).GetMethods()
            .Select(method => method.Name)
            .ToArray();
        var algorithmMethods = typeof(IVisionAlgorithm).GetMethods()
            .Select(method => method.Name)
            .ToArray();

        Assert.Contains(nameof(IVisionAlgorithmFactory.ValidateConfigurationAsync), factoryMethods);
        Assert.Contains(nameof(IVisionAlgorithmFactory.CreateAsync), factoryMethods);
        Assert.Contains(nameof(IVisionAlgorithm.WarmUpAsync), algorithmMethods);
        Assert.Contains(nameof(IVisionAlgorithm.ExecuteAsync), algorithmMethods);
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(IVisionAlgorithm)));
        Assert.DoesNotContain(typeof(IVisionAlgorithmFactory).GetProperties(),
            property => property.PropertyType == typeof(IServiceProvider));
        Assert.DoesNotContain(typeof(IVisionAlgorithm).GetProperties(),
            property => property.PropertyType == typeof(Stream));
    }

    private static AlgorithmConfigurationSchema ConfigurationSchema() =>
        new("configuration.demo", "1", new[]
        {
            new AlgorithmFieldDefinition("threshold", AlgorithmScalarType.Float64, "mm", true,
                new AlgorithmScalarConstraints(minFloat64: 0, maxFloat64: 100))
        });

    private static AlgorithmResultSchema ResultSchema(OverlayContract overlay) =>
        new("result.demo", "1", new[]
        {
            new AlgorithmFieldDefinition("score", AlgorithmScalarType.Float64, "percent", true)
        }, new[] { "NO_FRAME" }, overlay);

    private sealed class RecordingDiagnosticSink : IAlgorithmDiagnosticSink
    {
        public AlgorithmDiagnosticEmission TryEmit(AlgorithmDiagnosticEvent diagnosticEvent) =>
            AlgorithmDiagnosticEmission.Accepted;
    }

    private sealed class TestFrame : VisionFrame
    {
        private readonly byte[] _bytes = { 1, 2, 3, 4 };

        public TestFrame(ExecutionCorrelationId correlation)
            : base(correlation, "camera.primary", 2, 2, 2, VisionPixelFormat.Mono8, null,
                new DateTimeOffset(2026, 9, 8, 1, 2, 3, TimeSpan.Zero))
        { }

        public override bool IsLoanActive => true;

        public override ReadOnlySpan<byte> GetRowSpan(int row)
        {
            if (row < 0 || row >= Height) throw new ArgumentOutOfRangeException(nameof(row));
            return new ReadOnlySpan<byte>(_bytes, checked(row * StrideBytes), StrideBytes);
        }
    }

    private sealed class InvalidFrame : VisionFrame
    {
        public InvalidFrame(ExecutionCorrelationId correlation)
            : base(correlation, "camera.primary", 1, 1, 2, VisionPixelFormat.Mono16, 8,
                new DateTimeOffset(2026, 9, 8, 1, 2, 3, TimeSpan.Zero))
        { }

        public override bool IsLoanActive => false;
        public override ReadOnlySpan<byte> GetRowSpan(int row) => ReadOnlySpan<byte>.Empty;
    }

    private sealed class Mono16Frame : VisionFrame
    {
        public Mono16Frame(ExecutionCorrelationId correlation, int validBits)
            : base(correlation, "camera.primary", 1, 1, 2, VisionPixelFormat.Mono16, validBits,
                new DateTimeOffset(2026, 9, 8, 1, 2, 3, TimeSpan.Zero))
        { }

        public override bool IsLoanActive => true;
        public override ReadOnlySpan<byte> GetRowSpan(int row) => new byte[] { 0, 0 };
    }

    private sealed class Mono8WithBitsFrame : VisionFrame
    {
        public Mono8WithBitsFrame(ExecutionCorrelationId correlation)
            : base(correlation, "camera.primary", 1, 1, 1, VisionPixelFormat.Mono8, 8,
                new DateTimeOffset(2026, 9, 8, 1, 2, 3, TimeSpan.Zero))
        { }

        public override bool IsLoanActive => true;
        public override ReadOnlySpan<byte> GetRowSpan(int row) => new byte[] { 0 };
    }
}
