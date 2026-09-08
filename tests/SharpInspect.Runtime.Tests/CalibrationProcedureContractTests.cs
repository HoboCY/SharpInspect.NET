using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CalibrationProcedureContractTests
{
    [Fact]
    public void V124_P01_InputPayloadDefensivelyCopiesBytesAndBindsCompleteIdentity()
    {
        var contract = Contract("calibration.input");
        var bytes = new byte[] { 1, 2, 3, 4 };
        var expectedCanonicalHash = Convert.ToHexString(SHA256.HashData(bytes));
        var payload = new CalibrationProcedureInputPayload(contract, bytes);

        bytes[0] = 99;
        Assert.Equal(expectedCanonicalHash, payload.CanonicalBytesHash);
        Assert.NotEqual(expectedCanonicalHash, payload.ContentHash);

        var exposedBytes = payload.GetBytes();
        exposedBytes[1] = 98;
        exposedBytes[2] = 97;

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, payload.GetBytes());
        Assert.Equal(4, payload.Length);

        var otherContract = Contract("calibration.other");
        var other = new CalibrationProcedureInputPayload(otherContract, new byte[] { 1, 2, 3, 4 });
        Assert.Equal(payload.CanonicalBytesHash, other.CanonicalBytesHash);
        Assert.NotEqual(payload.ContentHash, other.ContentHash);
        Assert.Same(contract, payload.InputContract);
    }

    [Fact]
    public void V124_P02_CodecRequiresExactInputContractBeforeDecode()
    {
        var codec = new FixtureCodec(Contract("calibration.input"));
        var input = new FixtureInput("corner-0", 10.5, 20.25);
        var payload = codec.EncodePayload(input);

        var decoded = codec.Decode(payload);

        Assert.Equal(input, decoded);
        Assert.Same(codec.InputContract, payload.InputContract);
        Assert.Equal(codec.Encode(input).ToArray(), payload.GetBytes());

        var wrong = new CalibrationProcedureInputPayload(Contract("calibration.unknown"),
            payload.GetBytes());
        AssertCode(() => codec.Decode(wrong), "CalibrationInputContractMismatch");
    }

    [Fact]
    public void V124_P03_DescriptorBindsProcedureInputAndCalibrationKind()
    {
        var procedure = Contract("procedure.checkerboard");
        var input = Contract("calibration.input");
        var descriptor = new CalibrationProcedureDescriptor(procedure, input, CalibrationKind.Intrinsic);
        var differentKind = new CalibrationProcedureDescriptor(procedure, input,
            CalibrationKind.PlanarHomography);

        Assert.Same(procedure, descriptor.Procedure);
        Assert.Same(input, descriptor.InputContract);
        Assert.Equal(CalibrationKind.Intrinsic, descriptor.CalibrationKind);
        Assert.Equal(descriptor.ContentHash, new CalibrationProcedureDescriptor(procedure, input,
            CalibrationKind.Intrinsic).ContentHash);
        Assert.NotEqual(descriptor.ContentHash, differentKind.ContentHash);
    }

    [Fact]
    public void V124_P04_AutomaticFeaturesAndObservationsAreDefensivelyCopied()
    {
        var featureSource = new List<CalibrationImageFeature>
        {
            new("corner-0", 1.25, 2.5),
            new("corner-1", 5.75, 6.5)
        };
        var sessionId = Guid.NewGuid();
        var frameId = Guid.NewGuid();
        var frame = new TestFrame();
        var sourceHash = Hash("source-frame");

        var extraction = new CalibrationExtractionResult(featureSource,
            new[] { new CalibrationProcedureDiagnostic("corner.detected", "2") });
        var observation = new CalibrationObservationInput(frame, featureSource, sessionId, frameId,
            sourceHash);
        featureSource.Clear();

        Assert.Equal(2, extraction.Features.Count);
        Assert.Equal(2, observation.Features.Count);
        Assert.Equal("corner-0", observation.Features[0].StableFeatureId);
        Assert.Equal(1.25, observation.Features[0].PixelX);
        Assert.Equal(sourceHash, observation.SourceHash);
        Assert.Equal(sessionId, observation.SessionId);
        Assert.Equal(frameId, observation.FrameId);
        Assert.Throws<NotSupportedException>(() => ((IList<CalibrationImageFeature>)observation.Features)
            .Clear());
        AssertCode(() => new CalibrationExtractionResult(new[]
        {
            new CalibrationImageFeature("duplicate", 1, 2),
            new CalibrationImageFeature("duplicate", 3, 4)
        }), "CalibrationFeatureDuplicate");
        Assert.Throws<InvalidOperationException>(() => new CalibrationObservationInput(
            new TestFrame(active: false), Array.Empty<CalibrationImageFeature>(), sessionId,
            Guid.NewGuid(), sourceHash));
    }

    [Fact]
    public void V124_P05_BoundsRejectInvalidNumbersDuplicatesAndOversizedCollections()
    {
        var contract = Contract("calibration.input");
        AssertCode(() => new CalibrationProcedureInputPayload(contract, ReadOnlyMemory<byte>.Empty),
            "CalibrationProcedureInputPayloadSizeInvalid");
        AssertCode(() => new CalibrationProcedureInputPayload(contract,
                new byte[CalibrationProcedureInputPayload.MaximumBytes + 1]),
            "CalibrationProcedureInputPayloadSizeInvalid");
        Assert.Throws<ArgumentOutOfRangeException>(() => new CalibrationImageFeature("nan", double.NaN, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CalibrationImageFeature("infinity",
            double.PositiveInfinity, 1));
        AssertCode(() => new CalibrationImageFeature("bad/id", 1, 1), "AlgorithmIdentifierInvalid");

        AssertCode(() => new CalibrationExtractionResult(
            Enumerable.Range(0, CalibrationExtractionResult.MaximumFeatureCount + 1)
                .Select(index => new CalibrationImageFeature("f" + index.ToString(CultureInfo.InvariantCulture),
                    index, 0))), "AlgorithmCollectionCapacityExceeded");
        AssertCode(() => new CalibrationExtractionResult(Array.Empty<CalibrationImageFeature>(),
            Enumerable.Range(0, CalibrationExtractionResult.MaximumDiagnosticCount + 1)
                .Select(index => new CalibrationProcedureDiagnostic("d" + index.ToString(CultureInfo.InvariantCulture)))),
            "AlgorithmCollectionCapacityExceeded");
        AssertCode(() => new CalibrationExtractionResult(Array.Empty<CalibrationImageFeature>(), new[]
        {
            new CalibrationProcedureDiagnostic("same"), new CalibrationProcedureDiagnostic("same")
        }), "CalibrationDiagnosticDuplicate");

        var frame = new TestFrame();
        var observation = new CalibrationObservationInput(frame, Array.Empty<CalibrationImageFeature>(),
            Guid.NewGuid(), Guid.NewGuid(), Hash("observation"));
        var coefficient = new CalibrationCoefficientPayload(contract, new byte[] { 1 });
        AssertCode(() => new CalibrationProcedureComputationResult(coefficient,
            Enumerable.Range(0, CalibrationProcedureComputationResult.MaximumMetricCount + 1)
                .Select(index => new CalibrationQualityMetric("m" + index.ToString(CultureInfo.InvariantCulture), 1))),
            "AlgorithmCollectionCapacityExceeded");
        AssertCode(() => new CalibrationProcedureComputationResult(coefficient,
            new[] { new CalibrationQualityMetric("same", 1), new CalibrationQualityMetric("same", 2) }),
            "CalibrationMetricDuplicate");

        var tooManyObservations = Enumerable.Range(0,
                CalibrationComputationContext<FixtureInput>.MaximumObservationCount + 1)
            .Select(_ => new CalibrationObservationInput(new TestFrame(),
                Array.Empty<CalibrationImageFeature>(), observation.SessionId, Guid.NewGuid(),
                Hash("observation")))
            .ToArray();
        AssertCode(() => new CalibrationComputationContext<FixtureInput>(
            new FixtureInput("target", 1, 2), tooManyObservations),
            "AlgorithmCollectionCapacityExceeded");
        var duplicateFrameId = Guid.NewGuid();
        var first = new CalibrationObservationInput(new TestFrame(), Array.Empty<CalibrationImageFeature>(),
            observation.SessionId, duplicateFrameId, Hash("first"));
        var second = new CalibrationObservationInput(new TestFrame(), Array.Empty<CalibrationImageFeature>(),
            observation.SessionId, duplicateFrameId, Hash("second"));
        AssertCode(() => new CalibrationComputationContext<FixtureInput>(
            new FixtureInput("target", 1, 2), new[] { first, second }),
            "CalibrationObservationDuplicate");
        var differentSession = new CalibrationObservationInput(new TestFrame(),
            Array.Empty<CalibrationImageFeature>(), Guid.NewGuid(), Guid.NewGuid(), Hash("other-session"));
        AssertCode(() => new CalibrationComputationContext<FixtureInput>(
            new FixtureInput("target", 1, 2), new[] { first, differentSession }),
            "CalibrationObservationSessionMismatch");
    }

    [Fact]
    public void V124_P06_ComputationContextAndResultRetainOnlyImmutableEvidence()
    {
        var sessionId = Guid.NewGuid();
        var observations = new List<CalibrationObservationInput>
        {
            new(new TestFrame(), new[] { new CalibrationImageFeature("corner", 1, 2) },
                sessionId, Guid.NewGuid(), Hash("frame"))
        };
        var context = new CalibrationComputationContext<FixtureInput>(
            new FixtureInput("corner", 3, 4), observations);
        observations.Clear();

        var metrics = new List<CalibrationQualityMetric> { new("rms", 0.25, "pixel") };
        var diagnostics = new List<CalibrationProcedureDiagnostic> { new("fit.complete") };
        var result = new CalibrationProcedureComputationResult(
            new CalibrationCoefficientPayload(Contract("coefficients.v1"), new byte[] { 9, 8 }),
            metrics, diagnostics);
        metrics.Clear();
        diagnostics.Clear();

        Assert.Single(context.Observations);
        Assert.Equal("corner", context.Observations[0].Features[0].StableFeatureId);
        Assert.Single(result.QualityMetrics);
        Assert.Equal(0.25, result.QualityMetrics[0].Value);
        Assert.Single(result.Diagnostics);
        Assert.Equal("fit.complete", result.Diagnostics[0].Key);
        Assert.Throws<NotSupportedException>(() => ((IList<CalibrationQualityMetric>)result.QualityMetrics)
            .Clear());
    }

    [Fact]
    public void V124_P07_ProcedureSurfaceIsTypedAndHasNoAuthorityOrServiceCapability()
    {
        var procedureType = typeof(ICalibrationProcedure<FixtureInput>);
        var publicMethods = procedureType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .ToArray();
        var publicProperties = procedureType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(new[] { "ComputeAsync", "ExtractAsync" }, publicMethods.OrderBy(name => name).ToArray());
        Assert.Contains(nameof(ICalibrationProcedure<FixtureInput>.Descriptor), publicProperties);
        Assert.Contains(nameof(ICalibrationProcedure<FixtureInput>.InputCodec), publicProperties);

        var resultPropertyNames = typeof(CalibrationProcedureComputationResult).GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(resultPropertyNames, name => name is "Succeeded" or "Success" or
            "CanActivate" or "ProductionAuthority" or "Published" or "Authorized");

        var surfaceTypes = procedureType.GetProperties().Select(property => property.PropertyType)
            .Concat(procedureType.GetMethods().SelectMany(method => method.GetParameters()
                .Select(parameter => parameter.ParameterType).Append(method.ReturnType)))
            .Concat(new[] { typeof(CalibrationExtractionContext<FixtureInput>),
                typeof(CalibrationComputationContext<FixtureInput>),
                typeof(CalibrationProcedureComputationResult) })
            .ToArray();
        Assert.DoesNotContain(surfaceTypes, type => type == typeof(IServiceProvider) ||
            type == typeof(ICameraProvider) || type == typeof(ICameraDevice) ||
            type == typeof(Stream));
        Assert.DoesNotContain(typeof(CalibrationImageFeature).GetProperties(),
            property => property.PropertyType == typeof(OverlayPrimitive));
    }

    private static RecipeContractReference Contract(string id) =>
        new(id, "1", new string('A', 64));

    private static string Hash(string seed) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed)));

    private static void AssertCode(Action action, string expectedCode)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(action);
        Assert.StartsWith(expectedCode, exception.Message, StringComparison.Ordinal);
    }

    private sealed record FixtureInput(string StableFeatureId, double TargetX, double TargetY);

    private sealed class FixtureCodec : ICalibrationInputCodec<FixtureInput>
    {
        public FixtureCodec(RecipeContractReference inputContract) => InputContract = inputContract;

        public RecipeContractReference InputContract { get; }

        public FixtureInput Decode(ReadOnlyMemory<byte> canonicalBytes)
        {
            if (canonicalBytes.Length != 24)
                throw new ArgumentException("FixtureInputBytesInvalid", nameof(canonicalBytes));
            return new FixtureInput("corner-0", BitConverter.ToDouble(canonicalBytes.Span[..8]),
                BitConverter.ToDouble(canonicalBytes.Span[8..16]));
        }

        public ReadOnlyMemory<byte> Encode(FixtureInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            var bytes = new byte[24];
            BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), input.TargetX);
            BitConverter.TryWriteBytes(bytes.AsSpan(8, 8), input.TargetY);
            // The stable feature identity is part of the typed value's canonical identity.
            var identityBytes = System.Text.Encoding.UTF8.GetBytes(input.StableFeatureId);
            identityBytes.CopyTo(bytes.AsSpan(16, Math.Min(identityBytes.Length, 8)));
            return bytes;
        }
    }

    private sealed class TestFrame : VisionFrame
    {
        private readonly bool _active;
        private readonly byte[] _bytes = { 1, 2, 3, 4 };

        public TestFrame(bool active = true)
            : base(FrameDetails()) => _active = active;

        public override bool IsLoanActive => _active;

        public override ReadOnlySpan<byte> GetRowSpan(int row)
        {
            if (row < 0 || row >= Height) throw new ArgumentOutOfRangeException(nameof(row));
            return new ReadOnlySpan<byte>(_bytes, checked(row * StrideBytes), StrideBytes);
        }
    }

    private static FrameMetadata FrameDetails() =>
        new(new ExecutionCorrelationId(ExecutionKind.Qualification, Guid.NewGuid()),
            "camera.calibration", 2, 2, 2, VisionPixelFormat.Mono8, null,
            new DateTimeOffset(2026, 9, 8, 1, 2, 3, TimeSpan.Zero),
            new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 1000, 0,
                new RegionOfInterest(0, 0, 2, 2), VisionPixelFormat.Mono8, null, 1000, 0, null));
}
