using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class AlgorithmResultCodecTests
{
    [Fact]
    public async Task V114_C01_TenPrimitivesRoundTripWithCompleteSchemaAndTiming()
    {
        await using var fixture = Artifact.Create(ExecutionKind.Manual, includeOverlay: true);
        var document = Encode(fixture);

        var recorded = DateTimeOffset.UtcNow;
        var record = AlgorithmResultStorageCodec.Decode(17, recorded, document.PayloadJson, document.PayloadHash);

        Assert.Equal(17, record.Position);
        Assert.Equal(document.RecordId, record.RecordId);
        Assert.Equal(recorded, record.RecordedAtUtc);
        Assert.Equal(document.PayloadHash, record.ContentHash);
        Assert.Equal(fixture.Outcome.Correlation, record.Correlation);
        Assert.Equal(fixture.Outcome.PreparedInstanceId, record.PreparedInstanceId);
        Assert.Equal(fixture.Outcome.Algorithm, record.Algorithm);
        Assert.Equal(fixture.Outcome.ConfigurationContentHash, record.ConfigurationContentHash);
        Assert.Equal(fixture.Outcome.ConfigurationSchemaId, record.ConfigurationSchemaId);
        Assert.Equal(fixture.Outcome.ConfigurationSchemaVersion, record.ConfigurationSchemaVersion);
        Assert.Equal(fixture.Outcome.ConfigurationSchemaContentHash, record.ConfigurationSchemaContentHash);
        Assert.Equal(fixture.Outcome.FrameMetadata.Width, record.FrameMetadata.Width);
        Assert.Equal(fixture.Outcome.FrameMetadata.RequiredBufferLength, record.FrameMetadata.RequiredBufferLength);
        Assert.Equal(fixture.Outcome.ResultSchema.ContentHash, record.ResultSchema.ContentHash);
        Assert.Equal(fixture.Outcome.ResultSchema.Measurements.Count, record.ResultSchema.Measurements.Count);
        var scoreSchema = Assert.Single(fixture.Outcome.ResultSchema.Measurements,
            item => item.Key == "Score");
        var decodedScoreSchema = Assert.Single(record.ResultSchema.Measurements,
            item => item.Key == "Score");
        Assert.Equal(scoreSchema.Constraints!.MinFloat64, decodedScoreSchema.Constraints!.MinFloat64);
        Assert.Equal(fixture.Outcome.ValidatedResult!.Decision, record.Result.Decision);
        Assert.Equal(fixture.Outcome.ValidatedResult.Measurements.Count, record.Result.Measurements.Count);
        Assert.Equal(10, record.Result.OverlaySet.Primitives.Count);
        Assert.Equal(12345, record.AdmittedMonotonicTimestamp);
        Assert.Equal(fixture.Outcome.MonotonicFrequency, record.MonotonicFrequency);
        Assert.Equal(fixture.Outcome.Timing.Recipe.Id, record.Timing.Recipe.Id);
        Assert.Equal(fixture.Outcome.Timing.PolicyContentHash, record.Timing.PolicyContentHash);
        Assert.Equal(OverlayRenderingContract.ContentHash, record.Overlay.RendererContractContentHash);
    }

    [Fact]
    public async Task V114_C02_EmptyOverlayAndLargeFiniteScalarsRemainExact()
    {
        await using var fixture = Artifact.Create(ExecutionKind.Qualification, includeOverlay: false,
            score: double.MaxValue);
        var document = Encode(fixture);
        var record = AlgorithmResultStorageCodec.Decode(1, DateTimeOffset.UtcNow,
            document.PayloadJson, document.PayloadHash);

        Assert.Empty(record.Result.OverlaySet.Primitives);
        var score = Assert.Single(record.Result.Measurements, item => item.Key == "Score");
        Assert.Equal(double.MaxValue, score.Value.AsFloat64());
        Assert.Equal(fixture.Outcome.ResultSchema.OverlayContract.ContentHash,
            record.ResultSchema.OverlayContract.ContentHash);
    }

    [Fact]
    public async Task V114_C03_PainterOrderAndGeometryStyleFieldsStayDistinct()
    {
        await using var fixture = Artifact.Create(ExecutionKind.Manual, includeOverlay: true);
        var document = Encode(fixture);
        var record = AlgorithmResultStorageCodec.Decode(2, DateTimeOffset.UtcNow,
            document.PayloadJson, document.PayloadHash);
        var marker = Assert.IsType<OverlayMarker>(record.Result.OverlaySet.Primitives[0]);
        var text = Assert.IsType<OverlayText>(record.Result.OverlaySet.Primitives[^1]);

        Assert.Equal(7.5, marker.Size);
        Assert.Equal(17.25, marker.Style.MarkerSize);
        Assert.Equal(OverlayTextAnchor.BottomRight, text.AnchorKind);
        Assert.Equal(OverlayTextAnchor.TopLeft, text.Style.TextAnchor);
        Assert.Equal(fixture.Outcome.ValidatedResult!.OverlaySet.Primitives[4].Kind,
            record.Result.OverlaySet.Primitives[4].Kind);
    }

    [Fact]
    public async Task V114_C04_ProductionAndNonSuccessOutcomesAreRejected()
    {
        await using var production = Artifact.Create(ExecutionKind.Production, includeOverlay: false);
        Assert.False(AlgorithmResultStorageCodec.TryEncode(Guid.NewGuid(), production.Outcome,
            out var productionDocument, out var productionReason));
        Assert.Null(productionDocument);
        Assert.Equal("AlgorithmResultProductionForbidden", productionReason);

        await using var failed = Artifact.Create(ExecutionKind.Manual, includeOverlay: false);
        var error = new AlgorithmExecutionOutcome(failed.Prepared, failed.Frame,
            ExecutionStatus.Error, "AlgorithmExecutionError", null, failed.Outcome.Timing, 12345);
        Assert.False(AlgorithmResultStorageCodec.TryEncode(Guid.NewGuid(), error,
            out var errorDocument, out var errorReason));
        Assert.Null(errorDocument);
        Assert.Equal("AlgorithmResultExecutionStatusInvalid", errorReason);
    }

    [Fact]
    public async Task V114_C05_HashUnknownDuplicateAndMalformedEnumsFailClosed()
    {
        await using var fixture = Artifact.Create(ExecutionKind.Manual, includeOverlay: false);
        var document = Encode(fixture);

        var changed = document.PayloadJson.Replace("\"Decision\":\"Pass\"", "\"Decision\":\"Bogus\"",
            StringComparison.Ordinal);
        var exception = Assert.Throws<InvalidOperationException>(() => AlgorithmResultStorageCodec.Decode(
            1, DateTimeOffset.UtcNow, changed, Hash(changed)));
        Assert.Equal("AlgorithmResultDecisionInvalid", exception.Message);

        var unknown = document.PayloadJson[..^1] + ",\"Unknown\":1}";
        exception = Assert.Throws<InvalidOperationException>(() => AlgorithmResultStorageCodec.Decode(
            1, DateTimeOffset.UtcNow, unknown, Hash(unknown)));
        Assert.Equal("AlgorithmResultPayloadUnknownProperty", exception.Message);

        var duplicate = document.PayloadJson[..^1] + ",\"RecordId\":\"" + document.RecordId.ToString("D") + "\"}";
        exception = Assert.Throws<InvalidOperationException>(() => AlgorithmResultStorageCodec.Decode(
            1, DateTimeOffset.UtcNow, duplicate, Hash(duplicate)));
        Assert.Equal("AlgorithmResultPayloadDuplicateProperty", exception.Message);

        exception = Assert.Throws<InvalidOperationException>(() => AlgorithmResultStorageCodec.Decode(
            1, DateTimeOffset.UtcNow, document.PayloadJson + " ", document.PayloadHash));
        Assert.Equal("AlgorithmResultPayloadHashMismatch", exception.Message);
    }

    [Fact]
    public async Task V114_C06_BoundsAndZeroCapacityReturnStableReasonsWithoutRepair()
    {
        await using var fixture = Artifact.Create(ExecutionKind.Manual, includeOverlay: false);
        Assert.False(AlgorithmResultStorageCodec.TryEncode(Guid.NewGuid(), fixture.Outcome, 0,
            out var zero, out var zeroReason));
        Assert.Null(zero);
        Assert.Equal("AlgorithmResultPayloadCapacityInvalid", zeroReason);

        var oversized = new string('x', AlgorithmResultStorageCodec.MaximumPayloadBytes + 1);
        var exception = Assert.Throws<InvalidOperationException>(() => AlgorithmResultStorageCodec.Decode(
            3, DateTimeOffset.UtcNow, oversized, Hash(oversized)));
        Assert.Equal("AlgorithmResultPayloadInvalid", exception.Message);

        var tooLargeCapacity = AlgorithmResultStorageCodec.TryEncode(Guid.NewGuid(), fixture.Outcome,
            AlgorithmResultStorageCodec.MaximumPayloadBytes + 1, out _, out var capacityReason);
        Assert.False(tooLargeCapacity);
        Assert.Equal("AlgorithmResultPayloadCapacityInvalid", capacityReason);
    }

    [Fact]
    public async Task V114_C07_LegalOversizeIsRejectedBeforeProjectionAndNearLimitRoundTrips()
    {
        await using var oversize = Artifact.Create(ExecutionKind.Manual, includeOverlay: true,
            hugeTextCount: 16, textLength: 65_536);
        Assert.False(AlgorithmResultStorageCodec.TryEncode(Guid.NewGuid(), oversize.Outcome,
            out var rejected, out var rejectedReason));
        Assert.Null(rejected);
        Assert.Equal("AlgorithmResultPayloadTooLarge", rejectedReason);

        await using var nearLimit = Artifact.Create(ExecutionKind.Manual, includeOverlay: true,
            hugeTextCount: 14, textLength: 65_000);
        Assert.True(AlgorithmResultStorageCodec.TryEncode(Guid.NewGuid(), nearLimit.Outcome,
            out var document, out var acceptedReason), acceptedReason);
        var encoded = Assert.IsType<AlgorithmResultArchiveDocument>(document);
        Assert.InRange(Encoding.UTF8.GetByteCount(encoded.PayloadJson), 900_000, 1_048_576);
        var decoded = AlgorithmResultStorageCodec.Decode(11, DateTimeOffset.UtcNow,
            encoded.PayloadJson, encoded.PayloadHash);
        Assert.Equal(14, decoded.Result.OverlaySet.Primitives.Count);
        Assert.Equal(nearLimit.Outcome.ResultSchema.ContentHash, decoded.ResultSchema.ContentHash);
    }

    [Fact]
    public async Task V114_C08_RehashedNonCanonicalPrimitiveAndNumericEnumFailClosed()
    {
        await using var fixture = Artifact.Create(ExecutionKind.Manual, includeOverlay: true);
        var document = Encode(fixture);

        var shapeTampered = document.PayloadJson.Replace("\"Size\":7.5,\"Text\":null",
            "\"Size\":7.5,\"Text\":\"unexpected\"", StringComparison.Ordinal);
        Assert.NotEqual(document.PayloadJson, shapeTampered);
        var shapeError = Assert.Throws<InvalidOperationException>(() => AlgorithmResultStorageCodec.Decode(
            1, DateTimeOffset.UtcNow, shapeTampered, Hash(shapeTampered)));
        Assert.Equal("AlgorithmResultOverlayPrimitiveShapeInvalid", shapeError.Message);

        var enumTampered = document.PayloadJson.Replace("\"MarkerKind\":\"Cross\"",
            "\"MarkerKind\":\"1\"", StringComparison.Ordinal);
        var enumError = Assert.Throws<InvalidOperationException>(() => AlgorithmResultStorageCodec.Decode(
            1, DateTimeOffset.UtcNow, enumTampered, Hash(enumTampered)));
        Assert.Equal("AlgorithmResultOverlayEnumInvalid", enumError.Message);

        var correlationTampered = document.PayloadJson.Replace("\"Kind\":\"Manual\"",
            "\"Kind\":\"1\"", StringComparison.Ordinal);
        var correlationError = Assert.Throws<InvalidOperationException>(() => AlgorithmResultStorageCodec.Decode(
            1, DateTimeOffset.UtcNow, correlationTampered, Hash(correlationTampered)));
        Assert.Equal("AlgorithmResultCorrelationInvalid", correlationError.Message);
    }

    [Fact]
    public async Task V114_C09_LongUnicodeOverlayTextRoundTripsWithinPayloadLimit()
    {
        await using var fixture = Artifact.Create(ExecutionKind.Manual, includeOverlay: true,
            hugeTextCount: 1, textLength: 65_000, unicodeText: true);
        Assert.True(AlgorithmResultStorageCodec.TryEncode(Guid.NewGuid(), fixture.Outcome,
            out var document, out var reason), reason);
        var encoded = Assert.IsType<AlgorithmResultArchiveDocument>(document);
        Assert.InRange(Encoding.UTF8.GetByteCount(encoded.PayloadJson), 350_000, 1_048_576);
        var decoded = AlgorithmResultStorageCodec.Decode(12, DateTimeOffset.UtcNow,
            encoded.PayloadJson, encoded.PayloadHash);
        var text = Assert.IsType<OverlayText>(Assert.Single(decoded.Result.OverlaySet.Primitives));
        Assert.Equal(65_000, text.Text.Length);
        Assert.All(text.Text, character => Assert.Equal('\u4e00', character));
    }

    private static AlgorithmResultArchiveDocument Encode(Artifact fixture)
    {
        var ok = AlgorithmResultStorageCodec.TryEncode(Guid.NewGuid(), fixture.Outcome,
            out var document, out var reason);
        Assert.True(ok, reason);
        return Assert.IsType<AlgorithmResultArchiveDocument>(document);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class Artifact : IAsyncDisposable
    {
        private Artifact(PreparedAlgorithm prepared, FrameMetadata frame, AlgorithmExecutionOutcome outcome)
        {
            Prepared = prepared;
            Frame = frame;
            Outcome = outcome;
        }

        public PreparedAlgorithm Prepared { get; }
        public FrameMetadata Frame { get; }
        public AlgorithmExecutionOutcome Outcome { get; }

        public static Artifact Create(ExecutionKind kind, bool includeOverlay,
            double score = 123456789.12345679, int hugeTextCount = 0, int textLength = 5,
            bool unicodeText = false)
        {
            var correlation = new ExecutionCorrelationId(kind, Guid.NewGuid());
            var camera = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
                1000.25, 2.5, new RegionOfInterest(0, 0, 4, 3), VisionPixelFormat.Mono8, null,
                1000, 0.25, null);
            var frame = new FrameMetadata(correlation, "Primary", 4, 3, 8, VisionPixelFormat.Mono8,
                null, DateTimeOffset.UtcNow, camera);
            var configurationSchema = new AlgorithmConfigurationSchema("Codec.Config", "1", new[]
            {
                new AlgorithmFieldDefinition("Exposure", AlgorithmScalarType.Float64, "us", true,
                    new AlgorithmScalarConstraints(minFloat64: 1, maxFloat64: 100000))
            });
            var configuration = AlgorithmConfigurationSnapshot.Create(configurationSchema, new[]
            {
                new AlgorithmConfigurationEntry("Exposure", "us", AlgorithmScalarValue.FromFloat64(1000))
            });
            var overlayContract = new OverlayContract("Codec.Overlay", "1",
                hugeTextCount > 0 ? 64 : 16, hugeTextCount > 0 ? 4096 : 128,
                hugeTextCount > 0 ? 1024 : 32,
                hugeTextCount > 0 ? 65_536 : 256);
            var resultSchema = new AlgorithmResultSchema("Codec.Result", "1", new[]
            {
                new AlgorithmFieldDefinition("Score", AlgorithmScalarType.Float64, "score", true,
                    new AlgorithmScalarConstraints(minFloat64: -double.MaxValue, maxFloat64: double.MaxValue)),
                new AlgorithmFieldDefinition("Mode", AlgorithmScalarType.Enum, "state", false,
                    new AlgorithmScalarConstraints(allowedValues: new[] { "A", "B" })),
                new AlgorithmFieldDefinition("Label", AlgorithmScalarType.String, "text", false,
                    new AlgorithmScalarConstraints(minLength: 1, maxLength: 64))
            }, new[] { "Defect" }, overlayContract);
            var result = new AlgorithmResult(InspectionDecision.Pass, null,
                new[]
                {
                    new AlgorithmMeasurement("Score", "score", AlgorithmScalarValue.FromFloat64(score)),
                    new AlgorithmMeasurement("Mode", "state", AlgorithmScalarValue.FromEnum("A")),
                    new AlgorithmMeasurement("Label", "text", AlgorithmScalarValue.FromString("ok"))
                }, new OutputOverlaySet(overlayContract, includeOverlay
                    ? hugeTextCount > 0 ? Primitives(hugeTextCount, textLength, unicodeText) : Primitives()
                    : Array.Empty<OverlayPrimitive>()));
            var descriptor = new AlgorithmDescriptor(new("Codec.Algorithm", "1"), configurationSchema, resultSchema);
            var algorithm = new TestAlgorithm();
            var prepared = new PreparedAlgorithm(descriptor, configuration, algorithm,
                (instance, _) => instance.DisposeAsync().AsTask());
            var policy = new AlgorithmExecutionPolicy("Codec.Policy", "1", TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            Assert.True(policy.TryBind(new AlgorithmExecutionRequest(
                new RecipeReference("Codec.Recipe", "1", new string('A', 64)),
                TimeSpan.FromMilliseconds(250)), out var timing, out var timingReason), timingReason);
            var outcome = new AlgorithmExecutionOutcome(prepared, frame, ExecutionStatus.Success, null,
                result, Assert.IsType<AlgorithmExecutionTimingSnapshot>(timing), 12345);
            return new Artifact(prepared, frame, outcome);
        }

        private static IReadOnlyList<OverlayPrimitive> Primitives() => new OverlayPrimitive[]
        {
            new OverlayMarker(new(1, 2), OverlayMarkerKind.Cross, 7.5,
                new OverlayStyle(new(1, 2, 3, 255), 2, markerSize: 17.25)),
            new OverlayLineSegment(new(0, 0), new(1, 1)),
            new OverlayArrow(new(1, 1), new(2, 2)),
            new OverlayPolyline(new[] { new OverlayPoint(0, 0), new OverlayPoint(1, 1) }),
            new OverlayPolygon(new[] { new OverlayPoint(0, 0), new OverlayPoint(1, 0), new OverlayPoint(0, 1) }),
            new OverlayAxisAlignedRectangle(new(0, 0), 2, 3),
            new OverlayRotatedRectangle(new(2, 2), 3, 4, 30),
            new OverlayCircle(new(2, 2), 1),
            new OverlayEllipse(new(2, 2), 1, 2, 15),
            new OverlayText(new(3, 2), "label", new OverlayStyle(new(4, 5, 6), textAnchor: OverlayTextAnchor.TopLeft),
                OverlayTextAnchor.BottomRight)
        };

        private static IReadOnlyList<OverlayPrimitive> Primitives(int count, int textLength,
            bool unicodeText) =>
            Enumerable.Range(0, count)
                .Select(index => (OverlayPrimitive)new OverlayText(new(1, 1),
                    new string(unicodeText ? '\u4e00' : (char)('a' + index % 20), textLength)))
                .ToArray();

        public async ValueTask DisposeAsync() => await Prepared.DisposeAsync();
    }

    private sealed class TestAlgorithm : IVisionAlgorithm
    {
        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
