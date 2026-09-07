using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Frames;

namespace SharpInspect.SampleHost;

/// <summary>
/// A bounded consumer example for the managed algorithm execution slice. It exercises
/// preparation, pooled frame ownership and result-contract isolation without entering
/// the station's authoritative Inspection/qualification/Ready path.
/// </summary>
internal static class AlgorithmExecutionDemo
{
    public static int Run()
    {
        try
        {
            RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("V112-P01 algorithm-execution FAIL reason=AlgorithmConsumerCheckFailed");
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        var factory = new ExecutionDemoFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IVisionAlgorithmFactory>(factory);
        services.AddSharpInspectAlgorithmPreparation(new AlgorithmPreparationOptions(TimeSpan.FromSeconds(5)));
        services.AddSharpInspectFrameBufferPool(new FrameBufferPoolOptions(1, 1024,
            TimeSpan.FromMilliseconds(100)));
        services.AddSharpInspectRuntime();

        await using var provider = services.BuildServiceProvider();
        var preparer = provider.GetRequiredService<AlgorithmPreparationService>();
        var pool = provider.GetRequiredService<FrameBufferPool>();
        var runtime = provider.GetRequiredService<IStationRuntime>();
        await using var execution = new AlgorithmExecutionService(new AlgorithmExecutionOptions(
            new AlgorithmExecutionPolicy("Sample.AlgorithmExecution", "v1", TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)), TimeSpan.FromSeconds(1)));

        var before = await runtime.GetSnapshotAsync();
        Require(!before.Ready);

        var configuration = AlgorithmConfigurationSnapshot.Create(
            factory.Descriptor.ConfigurationSchema, Array.Empty<AlgorithmConfigurationEntry>());
        var preparedResult = await preparer.PrepareAsync(Request(factory.Descriptor, configuration));
        Require(preparedResult.Succeeded && preparedResult.Prepared is not null);
        var prepared = preparedResult.Prepared!;

        var pass = await ExecuteMarkerAsync(10, pool, execution, prepared);
        var fail = await ExecuteMarkerAsync(20, pool, execution, prepared);
        var unknown = await ExecuteMarkerAsync(30, pool, execution, prepared);

        Require(pass.Executed && pass.Outcome is not null &&
            pass.Outcome.ExecutionStatus == ExecutionStatus.Success &&
            pass.Outcome.Decision == InspectionDecision.Pass &&
            pass.Outcome.ValidatedResult is not null &&
            pass.Outcome.ValidatedResult.OverlaySet.Primitives.Count == 0);
        Require(fail.Executed && fail.Outcome is not null &&
            fail.Outcome.ExecutionStatus == ExecutionStatus.Success &&
            fail.Outcome.Decision == InspectionDecision.Fail &&
            fail.Outcome.ValidatedResult is not null &&
            fail.Outcome.ValidatedResult.OverlaySet.Primitives.Count == 0);
        Require(unknown.Executed && unknown.Outcome is not null &&
            unknown.Outcome.ExecutionStatus == ExecutionStatus.Success &&
            unknown.Outcome.Decision == InspectionDecision.Unknown &&
            unknown.Outcome.ReasonCode == "UnsupportedFrame" &&
            unknown.Outcome.ValidatedResult is not null &&
            unknown.Outcome.ValidatedResult.OverlaySet.Primitives.Count == 0);

        var wrongSchema = await ExecuteMarkerAsync(40, pool, execution, prepared);
        var unsafeOverlay = await ExecuteMarkerAsync(50, pool, execution, prepared);
        var typedException = await ExecuteMarkerAsync(60, pool, execution, prepared);
        var genericException = await ExecuteMarkerAsync(70, pool, execution, prepared);
        foreach (var attempt in new[] { wrongSchema, unsafeOverlay, typedException, genericException })
        {
            Require(attempt.Executed && attempt.Outcome is not null &&
                attempt.Outcome.ExecutionStatus == ExecutionStatus.Error &&
                attempt.Outcome.Decision == InspectionDecision.Unknown &&
                attempt.Outcome.ValidatedResult is null);
        }
        Require(wrongSchema.ReasonCode == "AlgorithmResultContractViolation");
        Require(unsafeOverlay.ReasonCode == "AlgorithmResultContractViolation");
        Require(typedException.ReasonCode == "SyntheticTypedFailure");
        Require(genericException.ReasonCode == "AlgorithmExecutionError");
        foreach (var attempt in new[] { pass, fail, unknown, wrongSchema, unsafeOverlay, typedException, genericException })
        {
            Require(attempt.Outcome!.Timing.PolicyId == "Sample.AlgorithmExecution" &&
                attempt.Outcome.Timing.PolicyVersion == "v1" &&
                attempt.Outcome.Timing.PolicyContentHash.Length == 64 &&
                attempt.Outcome.Timing.Recipe.Id == "sample-execution-recipe" &&
                attempt.Outcome.Timing.AlgorithmExecutionTimeout == TimeSpan.FromSeconds(2) &&
                attempt.Outcome.Timing.CancellationGracePeriod == TimeSpan.FromSeconds(1) &&
                attempt.Outcome.AdmittedMonotonicTimestamp > 0 && attempt.Outcome.MonotonicFrequency > 0);
        }

        Require(factory.Created == 1 && factory.Warmed == 1);
        await prepared.DisposeAsync();
        Require(factory.Disposed == 1);

        var after = await runtime.GetSnapshotAsync();
        Require(!after.Ready && after.ArmState == ProductionArmState.Disarmed &&
            after.CurrentExecution is null);
        Console.WriteLine("V112-P01 algorithm-execution PASS decisions=3 contractNegatives=true " +
            "exceptionSanitized=true preparedOnce=true productionReady=false");
        Console.WriteLine("V113-N01 execution-policy-consumer PASS recipeBound=true policyBound=true " +
            "monotonic=true productionReady=false");
    }

    private static async Task<AlgorithmExecutionAttempt> ExecuteMarkerAsync(byte marker,
        FrameBufferPool pool, AlgorithmExecutionService execution, PreparedAlgorithm prepared)
    {
        var input = CreateInput(marker);
        var copied = pool.TryCopyFrame(input.Metadata, input.Provenance, input.Bytes);
        Require(copied.Succeeded && copied.Lease is not null);

        var lease = copied.Lease!;
        var oldFrame = lease.Frame;
        try
        {
            var attempt = await execution.ExecuteAsync(prepared, lease,
                ExecutionRequest(TimeSpan.FromSeconds(2)));
            await WaitForReturnedFrameAsync(oldFrame);
            Require(!oldFrame.IsLoanActive);
            var staleRejected = false;
            try { _ = oldFrame.GetRowSpan(0); }
            catch (InvalidOperationException) { staleRejected = true; }
            Require(staleRejected);
            return attempt;
        }
        finally
        {
            // ExecuteAsync transfers ownership. This is intentionally idempotent and
            // also closes the owner if a pre-admission rejection happens in the future.
            lease.Dispose();
        }
    }

    private static async Task WaitForReturnedFrameAsync(VisionFrame frame)
    {
        for (var attempt = 0; attempt < 100 && frame.IsLoanActive; attempt++)
            await Task.Delay(1).ConfigureAwait(false);
        Require(!frame.IsLoanActive);
    }

    private static FrameInput CreateInput(byte marker)
    {
        var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
        var camera = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            1000, 0, new RegionOfInterest(0, 0, 2, 2), VisionPixelFormat.Mono8, null, 1000, 0, null);
        var metadata = new FrameMetadata(correlation, "Primary", 2, 2, 2, VisionPixelFormat.Mono8,
            null, DateTimeOffset.UtcNow, camera);
        var point = new FrameTimePoint(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
        var milestones = new FrameAcquisitionMilestones(Stopwatch.Frequency, point, point, point, point);
        var provenance = new FrameProvenance(correlation, "ManagedFixture", "1", "AlgorithmExecutionDemo",
            "1", "ManagedFixture", "1", null, "SampleDevice", "SampleCamera", "1",
            "Mono8", "AlreadyNormalized", false, false, null, null, milestones);
        return new FrameInput(metadata, provenance, new[] { marker, marker, marker, marker });
    }

    private static AlgorithmPreparationRequest Request(AlgorithmDescriptor descriptor,
        AlgorithmConfigurationSnapshot configuration) =>
        new(descriptor.Identity, configuration, descriptor.ResultSchema.Id, descriptor.ResultSchema.Version,
            descriptor.ResultSchema.ContentHash, descriptor.ResultSchema.OverlayContract.Id,
            descriptor.ResultSchema.OverlayContract.Version, descriptor.ResultSchema.OverlayContract.ContentHash,
            TimeSpan.FromSeconds(3));

    private static AlgorithmExecutionRequest ExecutionRequest(TimeSpan timeout) =>
        new(new RecipeReference("sample-execution-recipe", "1", new string('a', 64)), timeout);

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("AlgorithmConsumerCheckFailed");
    }

    private sealed record FrameInput(FrameMetadata Metadata, FrameProvenance Provenance, byte[] Bytes);

    private sealed class ExecutionDemoFactory : IVisionAlgorithmFactory
    {
        public ExecutionDemoFactory()
        {
            Descriptor = new AlgorithmDescriptor(new AlgorithmIdentity("Demo.Execution", "1"),
                new AlgorithmConfigurationSchema("Demo.Execution.Config", "1",
                    Array.Empty<AlgorithmFieldDefinition>()),
                new AlgorithmResultSchema("Demo.Execution.Result", "1",
                    new[] { new AlgorithmFieldDefinition("Value", AlgorithmScalarType.Float64,
                        "intensity", true) },
                    new[] { "UnsupportedFrame", "NoPixels", "SyntheticTypedFailure" },
                    new OverlayContract("Demo.Execution.Overlay", "1")));
        }

        public AlgorithmDescriptor Descriptor { get; }
        public int Created { get; private set; }
        public int Warmed { get; private set; }
        public int Disposed { get; private set; }

        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(configuration.Validate(Descriptor.ConfigurationSchema));
        }

        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Created++;
            return ValueTask.FromResult<IVisionAlgorithm>(new ExecutionDemoAlgorithm(Descriptor,
                () => Warmed++, () => Disposed++));
        }
    }

    private sealed class ExecutionDemoAlgorithm : IVisionAlgorithm
    {
        private readonly AlgorithmDescriptor _descriptor;
        private readonly Action _warmed;
        private readonly Action _disposed;
        private bool _ready;
        private bool _isDisposed;

        public ExecutionDemoAlgorithm(AlgorithmDescriptor descriptor, Action warmed, Action disposed)
        {
            _descriptor = descriptor;
            _warmed = warmed;
            _disposed = disposed;
        }

        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ready = true;
            _warmed();
            return ValueTask.CompletedTask;
        }

        public ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_ready || _isDisposed) throw new InvalidOperationException("AlgorithmNotPrepared");

            var marker = context.Frame.GetRowSpan(0)[0];
            var value = AlgorithmScalarValue.FromFloat64(marker);
            var measurement = new AlgorithmMeasurement("Value", "intensity", value);
            var contract = _descriptor.ResultSchema.OverlayContract;
            return marker switch
            {
                10 => ValueTask.FromResult(new AlgorithmResult(InspectionDecision.Pass, null,
                    new[] { measurement }, new OutputOverlaySet(contract))),
                20 => ValueTask.FromResult(new AlgorithmResult(InspectionDecision.Fail, null,
                    new[] { measurement }, new OutputOverlaySet(contract))),
                30 => ValueTask.FromResult(new AlgorithmResult(InspectionDecision.Unknown, "UnsupportedFrame",
                    new[] { measurement }, new OutputOverlaySet(contract))),
                40 => ValueTask.FromResult(new AlgorithmResult(InspectionDecision.Pass, null,
                    new[] { new AlgorithmMeasurement("Unexpected", "intensity", value) },
                    new OutputOverlaySet(contract))),
                50 => ValueTask.FromResult(new AlgorithmResult(InspectionDecision.Pass, null,
                    new[] { measurement }, new OutputOverlaySet("Demo.UnsafeOverlay", "1"))),
                60 => throw new AlgorithmExecutionException("SyntheticTypedFailure",
                    new InvalidOperationException("private typed detail")),
                70 => throw new InvalidOperationException("private generic detail"),
                _ => ValueTask.FromResult(new AlgorithmResult(InspectionDecision.Unknown, "NoPixels",
                    new[] { measurement }, new OutputOverlaySet(contract)))
            };
        }

        public ValueTask DisposeAsync()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                _ready = false;
                _disposed();
            }
            return ValueTask.CompletedTask;
        }
    }
}
