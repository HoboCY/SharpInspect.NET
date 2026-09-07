using System.Buffers.Binary;
using System.Diagnostics;
using OpenCvSharp;
using SharpInspect.Abstractions;
using SharpInspect.OpenCvSharp;
using SharpInspect.Runtime.Frames;

namespace SharpInspect.SampleHost;

internal static class FrameConsumerDemo
{
    public static int Run()
    {
        try { RunAsync().GetAwaiter().GetResult(); return 0; }
        catch { Console.Error.WriteLine("V111-P01 frame-consumer FAIL reason=FrameConsumerCheckFailed"); return 1; }
    }

    private static async Task RunAsync()
    {
        var factory = new MeanFactory();
        var config = AlgorithmConfigurationSnapshot.Create(factory.Descriptor.ConfigurationSchema,
            Array.Empty<AlgorithmConfigurationEntry>());
        Require((await factory.ValidateConfigurationAsync(config)).Count == 0);
        await using var algorithm = await factory.CreateAsync(config);
        await algorithm.WarmUpAsync();
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(2, 64, TimeSpan.FromMilliseconds(100)));
        foreach (var specification in new[]
        {
            (VisionPixelFormat.Mono8, (int?)null), (VisionPixelFormat.Mono16, (int?)10),
            (VisionPixelFormat.Mono16, (int?)12), (VisionPixelFormat.Mono16, (int?)16),
            (VisionPixelFormat.Bgr24, (int?)null)
        })
        {
            var correlation = new ExecutionCorrelationId(ExecutionKind.Qualification, Guid.NewGuid());
            var bytesPerPixel = specification.Item1 switch
            { VisionPixelFormat.Mono8 => 1, VisionPixelFormat.Mono16 => 2, _ => 3 };
            // Exercise odd Mono16 source rows through the real pool normalization path.
            var stride = 2 * bytesPerPixel + (specification.Item1 == VisionPixelFormat.Mono16 ? 1 : 2);
            var expectedStride = specification.Item1 == VisionPixelFormat.Mono16 ? stride + 1 : stride;
            var configuration = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
                1000, 0, new RegionOfInterest(0, 0, 2, 2), specification.Item1, specification.Item2,
                1000, 0, specification.Item1 == VisionPixelFormat.Bgr24 ? new WhiteBalanceRgb(1, 1, 1) : null);
            var metadata = new FrameMetadata(correlation, "Primary", 2, 2, stride, specification.Item1,
                specification.Item2, DateTimeOffset.UtcNow, configuration);
            var provenance = Provenance(correlation);
            var input = new byte[checked((int)metadata.RequiredBufferLength)];
            for (var row = 0; row < 2; row++)
                for (var column = 0; column < 2; column++)
                {
                    var value = (ushort)((row * 2 + column + 1) * 10);
                    var offset = row * stride + column * bytesPerPixel;
                    if (metadata.PixelFormat == VisionPixelFormat.Mono16)
                        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(offset, 2), value);
                    else
                        for (var channel = 0; channel < bytesPerPixel; channel++) input[offset + channel] = (byte)(value + channel);
                }
            var acquired = pool.TryCopyFrame(metadata, provenance, input);
            Require(acquired.Succeeded && acquired.Lease is not null);
            var owner = acquired.Lease!;
            var frame = owner.Frame;
            Require(frame.StrideBytes == expectedStride && metadata.StrideBytes == stride);
            var copyEvidence = owner.Provenance.PoolCopyEvidence;
            Require(copyEvidence is not null && copyEvidence.SourceStrideBytes == stride &&
                copyEvidence.DestinationStrideBytes == expectedStride && copyEvidence.UsedPreallocatedPixelBuffer);
            Mat? borrowed = null;
            frame.WithMat(mat => { borrowed = mat; Require(mat.Step() == expectedStride && !mat.IsContinuous()); });
            Require(borrowed!.IsDisposed);
            // This is a public algorithm-contract consumer, not the later authoritative execution service.
            var result = await algorithm.ExecuteAsync(new(correlation, config, frame, new DropDiagnostics()));
            Require(result.Decision == InspectionDecision.Pass && result.Measurements.Single().Value.AsFloat64() == 25d);
            using var clone = frame.CloneToMat();
            using var display = frame.CloneToDisplayMat();
            owner.Dispose(); owner.Dispose();
            Require(!frame.IsLoanActive && pool.GetSnapshot().OutstandingLeases == 0);
            Require(Cv2.Mean(clone).Val0 == 25d && display.Type() != MatType.CV_16UC1);
            var refused = false;
            var callbackEntered = false;
            try { frame.WithMat(_ => callbackEntered = true); }
            catch (InvalidOperationException) { refused = true; }
            Require(refused && !callbackEntered);
            Console.WriteLine($"V111 prepared pixels format={metadata.PixelFormat} validBits={metadata.ValidBits?.ToString() ?? "absent"} sourceStride={stride} outputStride={frame.StrideBytes} borrowedExpired=true cloneRetained=true");
        }
        Require(pool.GetSnapshot().ActiveReaders == 0 && pool.GetSnapshot().PeakLeases <= 2);
        Console.WriteLine("V111-P01 frame-consumer PASS formats=5 padding=true strideAlignment=true cloneRetained=true staleBorrowRejected=true authoritativeExecution=NotRun productionReady=false");
    }

    private static FrameProvenance Provenance(ExecutionCorrelationId correlation)
    {
        var now = new FrameTimePoint(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
        return new(correlation, "ManagedFixture", "1", "ManagedFrameDemo", "1", "ManagedFixture", "1",
            null, "SyntheticMemory", null, null, "AlreadyNormalized", "Copy into preallocated framework buffer",
            false, false, null, null, new(Stopwatch.Frequency, null, now, now, now));
    }
    private static void Require(bool condition)
    { if (!condition) throw new InvalidOperationException("FrameConsumerCheckFailed"); }

    private sealed class MeanFactory : IVisionAlgorithmFactory
    {
        public AlgorithmDescriptor Descriptor { get; } = new(new("Demo.FrameMean", "1"),
            new("Demo.FrameMean.Config", "1", Array.Empty<AlgorithmFieldDefinition>()),
            new("Demo.FrameMean.Result", "1", new[]
                { new AlgorithmFieldDefinition("Mean", AlgorithmScalarType.Float64, "intensity", true) },
                Array.Empty<string>(), new OverlayContract("Demo.FrameMean.Overlay", "1")));
        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(configuration.Validate(Descriptor.ConfigurationSchema));
        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<IVisionAlgorithm>(new MeanAlgorithm());
    }
    private sealed class MeanAlgorithm : IVisionAlgorithm
    {
        private bool _ready;
        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var warm = new Mat(2, 2, MatType.CV_8UC1, Scalar.All(1));
            Require(Cv2.Mean(warm).Val0 == 1);
            _ready = true;
            return ValueTask.CompletedTask;
        }
        public ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Require(_ready);
            var mean = 0d;
            context.Frame.WithMat(mat => mean = Cv2.Mean(mat).Val0);
            return ValueTask.FromResult(new AlgorithmResult(InspectionDecision.Pass, null,
                new[] { new AlgorithmMeasurement("Mean", "intensity", AlgorithmScalarValue.FromFloat64(mean)) },
                new OutputOverlaySet("Demo.FrameMean.Overlay", "1")));
        }
        public ValueTask DisposeAsync() { _ready = false; return ValueTask.CompletedTask; }
    }
    private sealed class DropDiagnostics : IAlgorithmDiagnosticSink
    {
        public AlgorithmDiagnosticEmission TryEmit(AlgorithmDiagnosticEvent diagnosticEvent) => AlgorithmDiagnosticEmission.Dropped;
    }
}
