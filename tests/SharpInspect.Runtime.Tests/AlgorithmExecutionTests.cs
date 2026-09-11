using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Frames;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class AlgorithmExecutionTests
{
    [Fact]
    public async Task V112_E01_NormalDecisionsReuseOnePreparedInstanceWithoutRewarmOrCreate()
    {
        var contract = CreateContract("E01");
        var decisions = new Queue<AlgorithmResult>(new[]
        {
            Result(contract, InspectionDecision.Pass),
            Result(contract, InspectionDecision.Fail, "Defect"),
            Result(contract, InspectionDecision.Unknown, "Uncertain")
        });
        var algorithm = new TestAlgorithm((_, _) =>
            ValueTask.FromResult(decisions.Dequeue()));
        await using var fixture = await PrepareAsync(contract, algorithm);
        await using var execution = new AlgorithmExecutionService(ExecutionOptions());
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(3, 8, TimeSpan.FromSeconds(1)));

        var expected = new[] { InspectionDecision.Pass, InspectionDecision.Fail, InspectionDecision.Unknown };
        for (var index = 0; index < expected.Length; index++)
        {
            var lease = CopyFrame(pool);
            var frameMetadata = lease.Frame.Metadata;
            var attempt = await execution.ExecuteAsync(fixture.Prepared, lease, ExecutionRequest(TimeSpan.FromSeconds(1)));

            Assert.True(attempt.Executed, attempt.ReasonCode);
            var outcome = Assert.IsType<AlgorithmExecutionOutcome>(attempt.Outcome);
            Assert.Equal(ExecutionStatus.Success, outcome.ExecutionStatus);
            Assert.Equal(expected[index], outcome.Decision);
            Assert.Equal(expected[index] == InspectionDecision.Pass ? null : expected[index] == InspectionDecision.Fail ? "Defect" : "Uncertain",
                outcome.ValidatedResult!.ReasonCode);
            Assert.Same(frameMetadata, outcome.FrameMetadata);
            Assert.Equal(fixture.Prepared.InstanceId, outcome.PreparedInstanceId);
            Assert.NotNull(outcome.ValidatedResult);
            Assert.Equal(expected[index] == InspectionDecision.Pass
                ? "AlgorithmExecutionCompleted" : outcome.ReasonCode, attempt.ReasonCode);
        }

        Assert.Equal(1, fixture.Factory.CreateCalls);
        Assert.Equal(1, algorithm.WarmCalls);
        Assert.Equal(3, algorithm.ExecuteCalls);
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
    }

    [Fact]
    public async Task V112_E02_AlgorithmExceptionsMapToSanitizedStableReasons()
    {
        var cases = new[]
        {
            ("E02Typed", "AlgorithmFailure", (Func<Exception>)(() =>
                new AlgorithmExecutionException("AlgorithmFailure",
                    new InvalidOperationException("secret-inner-detail")))),
            ("E02Unknown", "AlgorithmExecutionError", (Func<Exception>)(() =>
                new AlgorithmExecutionException("NotInSchema", new Exception("secret-proposed-detail")))),
            ("E02Generic", "AlgorithmExecutionError", (Func<Exception>)(() =>
                new InvalidOperationException("secret-generic-detail"))),
            ("E02Oce", "AlgorithmExecutionError", (Func<Exception>)(() =>
                new OperationCanceledException("self-cancel-without-runtime-request")))
        };

        foreach (var (suffix, expectedReason, createException) in cases)
        {
            var contract = CreateContract(suffix);
            var algorithm = new TestAlgorithm((_, _) =>
                ValueTask.FromException<AlgorithmResult>(createException()));
            await using var fixture = await PrepareAsync(contract, algorithm);
            await using var execution = new AlgorithmExecutionService(ExecutionOptions());
            using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));

            var attempt = await execution.ExecuteAsync(fixture.Prepared, CopyFrame(pool),
                ExecutionRequest(TimeSpan.FromSeconds(1)));

            Assert.True(attempt.Executed);
            Assert.Equal(expectedReason, attempt.ReasonCode);
            var outcome = Assert.IsType<AlgorithmExecutionOutcome>(attempt.Outcome);
            Assert.Equal(ExecutionStatus.Error, outcome.ExecutionStatus);
            Assert.Equal(InspectionDecision.Unknown, outcome.Decision);
            Assert.Equal(expectedReason, outcome.ReasonCode);
            Assert.Null(outcome.ValidatedResult);
            Assert.DoesNotContain(typeof(AlgorithmExecutionOutcome).GetProperties(), property =>
                typeof(Exception).IsAssignableFrom(property.PropertyType));
            Assert.DoesNotContain("secret", attempt.ReasonCode, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
        }
    }

    [Fact]
    public async Task V112_E03_InvalidAlgorithmResultIsRejectedAsAWholeWithoutPartialSuccess()
    {
        var contract = CreateContract("E03");
        var invalid = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet("WrongOverlay", "1"));
        var algorithm = new TestAlgorithm((_, _) => ValueTask.FromResult(invalid));
        await using var fixture = await PrepareAsync(contract, algorithm);
        await using var execution = new AlgorithmExecutionService(ExecutionOptions());
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));

        var attempt = await execution.ExecuteAsync(fixture.Prepared, CopyFrame(pool),
            ExecutionRequest(TimeSpan.FromSeconds(1)));

        Assert.True(attempt.Executed);
        Assert.Equal("AlgorithmResultContractViolation", attempt.ReasonCode);
        var outcome = Assert.IsType<AlgorithmExecutionOutcome>(attempt.Outcome);
        Assert.Equal(ExecutionStatus.Error, outcome.ExecutionStatus);
        Assert.Equal(InspectionDecision.Unknown, outcome.Decision);
        Assert.Equal("AlgorithmResultContractViolation", outcome.ReasonCode);
        Assert.Null(outcome.ValidatedResult);
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
    }

    [Fact]
    public async Task V112_E04_InvalidAdmissionAndCancelledCallsConsumeFrameWithoutExecuting()
    {
        var contract = CreateContract("E04");
        var algorithm = new TestAlgorithm((_, _) => ValueTask.FromResult(Result(contract,
            InspectionDecision.Pass)));
        await using var fixture = await PrepareAsync(contract, algorithm);
        await using var execution = new AlgorithmExecutionService(ExecutionOptions());
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(4, 8, TimeSpan.FromSeconds(1)));

        var invalidTimeout = await execution.ExecuteAsync(fixture.Prepared, CopyFrame(pool), ExecutionRequest(TimeSpan.Zero));
        AssertRejected(invalidTimeout, "AlgorithmExecutionTimeoutInvalid");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await execution.ExecuteAsync(fixture.Prepared, CopyFrame(pool),
            ExecutionRequest(TimeSpan.FromSeconds(1)), cancellation.Token);
        AssertRejected(cancelled, "AlgorithmExecutionCancelledBeforeStart");

        var production = await execution.ExecuteAsync(fixture.Prepared, CopyFrame(pool, ExecutionKind.Production),
            ExecutionRequest(TimeSpan.FromSeconds(1)));
        AssertRejected(production, "ProductionExecutionAdmissionUnavailable");

        var disposedLease = CopyFrame(pool);
        disposedLease.Dispose();
        var notOwned = await execution.ExecuteAsync(fixture.Prepared, disposedLease, ExecutionRequest(TimeSpan.FromSeconds(1)));
        AssertRejected(notOwned, "FrameLeaseNotOwned");

        Assert.Equal(0, algorithm.ExecuteCalls);
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);

        await execution.DisposeAsync();
        var afterDispose = await execution.ExecuteAsync(fixture.Prepared, CopyFrame(pool), ExecutionRequest(TimeSpan.FromSeconds(1)));
        AssertRejected(afterDispose, "AlgorithmExecutionServiceDisposed");
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
    }

    [Fact]
    public async Task V112_E05_SameServiceRejectsConcurrentExecutionWithoutQueueing()
    {
        var contract = CreateContract("E05");
        var started = NewSignal();
        var release = NewSignal();
        var algorithm = new TestAlgorithm(async (_, _) =>
        {
            started.TrySetResult(true);
            await release.Task;
            return Result(contract, InspectionDecision.Pass);
        });
        await using var fixture = await PrepareAsync(contract, algorithm);
        await using var execution = new AlgorithmExecutionService(ExecutionOptions());
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(2, 8, TimeSpan.FromSeconds(1)));

        var first = execution.ExecuteAsync(fixture.Prepared, CopyFrame(pool), ExecutionRequest(TimeSpan.FromSeconds(2))).AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, execution.ActiveExecutionCount);

            var second = await execution.ExecuteAsync(fixture.Prepared, CopyFrame(pool),
                ExecutionRequest(TimeSpan.FromSeconds(1)));
            AssertRejected(second, "AlgorithmExecutionBusy");
            Assert.Equal(1, pool.GetSnapshot().OutstandingLeases);

            release.TrySetResult(true);
            var firstAttempt = await first.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(firstAttempt.Executed, firstAttempt.ReasonCode);
            Assert.Equal(ExecutionStatus.Success, firstAttempt.Outcome!.ExecutionStatus);
            Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
            Assert.Equal(0, execution.ActiveExecutionCount);
            Assert.Equal(1, algorithm.ExecuteCalls);
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task V112_E06_MultipleServicesRejectTheSamePreparedInstanceAndRetiredHandle()
    {
        var contract = CreateContract("E06");
        var started = NewSignal();
        var release = NewSignal();
        var algorithm = new TestAlgorithm(async (_, _) =>
        {
            started.TrySetResult(true);
            await release.Task;
            return Result(contract, InspectionDecision.Pass);
        });
        await using var fixture = await PrepareAsync(contract, algorithm);
        await using var firstService = new AlgorithmExecutionService(ExecutionOptions());
        await using var secondService = new AlgorithmExecutionService(ExecutionOptions());
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(2, 8, TimeSpan.FromSeconds(1)));

        var first = firstService.ExecuteAsync(fixture.Prepared, CopyFrame(pool), ExecutionRequest(TimeSpan.FromSeconds(2))).AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var crossService = await secondService.ExecuteAsync(fixture.Prepared, CopyFrame(pool),
                ExecutionRequest(TimeSpan.FromSeconds(1)));
            AssertRejected(crossService, "AlgorithmInstanceBusy");
            Assert.Equal(1, pool.GetSnapshot().OutstandingLeases);

            release.TrySetResult(true);
            Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(2))).Executed);
            await fixture.Prepared.DisposeAsync();
            Assert.Equal(1, algorithm.DisposeCalls);

            var retired = await secondService.ExecuteAsync(fixture.Prepared, CopyFrame(pool),
                ExecutionRequest(TimeSpan.FromSeconds(1)));
            AssertRejected(retired, "AlgorithmInstanceRetired");
            Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task V112_E07_ServiceAndPreparedDisposeWaitForPhysicalExecutionAndFrameReturn()
    {
        var contract = CreateContract("E07");
        var started = NewSignal();
        var release = NewSignal();
        var algorithm = new TestAlgorithm(async (_, _) =>
        {
            started.TrySetResult(true);
            await release.Task;
            return Result(contract, InspectionDecision.Pass);
        });
        await using var fixture = await PrepareAsync(contract, algorithm);
        await using var execution = new AlgorithmExecutionService(new AlgorithmExecutionOptions(
            new AlgorithmExecutionPolicy("Test.AlgorithmExecution", "v1", TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)), TimeSpan.FromMilliseconds(100)));
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));

        var running = execution.ExecuteAsync(fixture.Prepared, CopyFrame(pool), ExecutionRequest(TimeSpan.FromSeconds(5))).AsTask();
        Task? preparedDisposal = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            preparedDisposal = fixture.Prepared.DisposeAsync().AsTask();
            Assert.False(preparedDisposal.IsCompleted);

            var shutdown = execution.DisposeAsync().AsTask();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
            var cancelled = await running.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(cancelled.Executed);
            Assert.Equal(ExecutionStatus.Cancelled, cancelled.Outcome!.ExecutionStatus);
            Assert.False(preparedDisposal.IsCompleted);
            Assert.Equal(0, algorithm.DisposeCalls);
            Assert.Equal(1, pool.GetSnapshot().OutstandingLeases);
            Assert.Equal(1, execution.ActiveExecutionCount);

            release.TrySetResult(true);
            await preparedDisposal.WaitAsync(TimeSpan.FromSeconds(2));
            await EventuallyAsync(() => pool.GetSnapshot().OutstandingLeases == 0 &&
                execution.ActiveExecutionCount == 0);
            Assert.Equal(1, algorithm.DisposeCalls);
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task V112_E08_DeadlineProducesTimeoutButRetainsFrameUntilAlgorithmReturns()
    {
        var contract = CreateContract("E08");
        var started = NewSignal();
        var release = NewSignal();
        var algorithm = new TestAlgorithm(async (_, _) =>
        {
            started.TrySetResult(true);
            await release.Task;
            return Result(contract, InspectionDecision.Pass);
        });
        await using var fixture = await PrepareAsync(contract, algorithm);
        await using var execution = new AlgorithmExecutionService(ExecutionOptions());
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));

        var running = execution.ExecuteAsync(fixture.Prepared, CopyFrame(pool),
            ExecutionRequest(TimeSpan.FromMilliseconds(100))).AsTask();
        Task? preparedDisposal = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            preparedDisposal = fixture.Prepared.DisposeAsync().AsTask();
            var timedOut = await running.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(timedOut.Executed);
            Assert.Equal("AlgorithmExecutionTimeout", timedOut.ReasonCode);
            Assert.Equal(ExecutionStatus.Timeout, timedOut.Outcome!.ExecutionStatus);
            Assert.Equal(InspectionDecision.Unknown, timedOut.Outcome.Decision);
            Assert.Null(timedOut.Outcome.ValidatedResult);
            Assert.False(preparedDisposal.IsCompleted);
            Assert.Equal(0, algorithm.DisposeCalls);
            Assert.Equal(1, pool.GetSnapshot().OutstandingLeases);

            release.TrySetResult(true);
            await preparedDisposal.WaitAsync(TimeSpan.FromSeconds(2));
            await EventuallyAsync(() => pool.GetSnapshot().OutstandingLeases == 0);
            Assert.Equal(1, algorithm.DisposeCalls);
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task V112_E09_ReusingOneFrameLeaseCannotCloseTheFirstPhysicalOwner()
    {
        var contract = CreateContract("E09");
        var started = NewSignal();
        var release = NewSignal();
        var algorithm = new TestAlgorithm(async (_, _) =>
        {
            started.TrySetResult(true);
            await release.Task;
            return Result(contract, InspectionDecision.Pass);
        });
        await using var fixture = await PrepareAsync(contract, algorithm);
        await using var firstService = new AlgorithmExecutionService(ExecutionOptions());
        await using var secondService = new AlgorithmExecutionService(ExecutionOptions());
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));
        var lease = CopyFrame(pool);
        var first = firstService.ExecuteAsync(fixture.Prepared, lease, ExecutionRequest(TimeSpan.FromSeconds(2))).AsTask();

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var duplicate = await secondService.ExecuteAsync(fixture.Prepared, lease,
                ExecutionRequest(TimeSpan.FromSeconds(1)));
            AssertRejected(duplicate, "FrameLeaseNotOwned");
            Assert.Equal(1, firstService.ActiveExecutionCount);
            Assert.Equal(0, secondService.ActiveExecutionCount);
            Assert.Equal(1, pool.GetSnapshot().OutstandingLeases);

            release.TrySetResult(true);
            var completed = await first.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(completed.Executed, completed.ReasonCode);
            Assert.Equal(ExecutionStatus.Success, completed.Outcome!.ExecutionStatus);
            Assert.Equal(1, algorithm.ExecuteCalls);
            Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
            Assert.Equal(0, firstService.ActiveExecutionCount);
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task V112_E10_AlgorithmExecutionDIRequiresExplicitOptionsAndRejectsDuplicateRegistration()
    {
        var options = ExecutionOptions();
        var services = new ServiceCollection();
        Assert.Same(services, services.AddSharpInspectAlgorithmExecution(options));

        var duplicate = Assert.Throws<ArgumentException>(() =>
            services.AddSharpInspectAlgorithmExecution(ExecutionOptions()));
        Assert.Contains("AlgorithmExecutionAlreadyRegistered", duplicate.Message,
            StringComparison.Ordinal);

        await using (var provider = services.BuildServiceProvider())
        {
            var resolved = provider.GetRequiredService<AlgorithmExecutionService>();
            Assert.Same(resolved, provider.GetRequiredService<AlgorithmExecutionService>());
        }

        var missingOptions = new ServiceCollection();
        missingOptions.AddSingleton<AlgorithmExecutionService>();
        await using var missingProvider = missingOptions.BuildServiceProvider();
        var missing = Assert.Throws<InvalidOperationException>(() =>
            missingProvider.GetRequiredService<AlgorithmExecutionService>());
        Assert.Contains(nameof(AlgorithmExecutionOptions), missing.Message,
            StringComparison.Ordinal);
    }

    private static AlgorithmExecutionOptions ExecutionOptions() =>
        new(new AlgorithmExecutionPolicy("Test.AlgorithmExecution", "v1", TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)), TimeSpan.FromSeconds(1));

    private static AlgorithmExecutionRequest ExecutionRequest(TimeSpan timeout) =>
        new(new RecipeReference("test-recipe", "1", new string('a', 64)), timeout);

    private static void AssertRejected(AlgorithmExecutionAttempt attempt, string reasonCode)
    {
        Assert.False(attempt.Executed);
        Assert.Equal(reasonCode, attempt.ReasonCode);
        Assert.Null(attempt.Outcome);
    }

    private static async Task<PreparedFixture> PrepareAsync(Contract contract, TestAlgorithm algorithm)
    {
        var factory = new TestFactory(contract.Descriptor, algorithm);
        var preparation = new AlgorithmPreparationService(new[] { factory },
            new AlgorithmPreparationOptions(TimeSpan.FromSeconds(5)));
        var prepared = await preparation.PrepareAsync(Request(contract));
        if (!prepared.Succeeded || prepared.Prepared is null)
        {
            await preparation.DisposeAsync();
            throw new Xunit.Sdk.XunitException("Preparation failed: " + prepared.ReasonCode);
        }

        return new PreparedFixture(preparation, prepared.Prepared, factory, algorithm, contract);
    }

    private static AlgorithmPreparationRequest Request(Contract contract)
    {
        var result = contract.Descriptor.ResultSchema;
        var overlay = result.OverlayContract;
        return new(contract.Descriptor.Identity, contract.Configuration, result.Id, result.Version,
            result.ContentHash, overlay.Id, overlay.Version, overlay.ContentHash,
            TimeSpan.FromSeconds(2));
    }

    private static Contract CreateContract(string suffix)
    {
        var configurationSchema = new AlgorithmConfigurationSchema("Config" + suffix, "1",
            new[] { new AlgorithmFieldDefinition("Threshold", AlgorithmScalarType.Int64, "px", required: true) });
        var overlay = new OverlayContract("Overlay" + suffix, "1");
        var resultSchema = new AlgorithmResultSchema("Result" + suffix, "1",
            Array.Empty<AlgorithmFieldDefinition>(),
            new[] { "Defect", "Uncertain", "AlgorithmFailure" }, overlay);
        var descriptor = new AlgorithmDescriptor(new AlgorithmIdentity("Algorithm" + suffix, "1"),
            configurationSchema, resultSchema);
        var configuration = AlgorithmConfigurationSnapshot.Create(configurationSchema,
            new[] { new AlgorithmConfigurationEntry("Threshold", "px", AlgorithmScalarValue.FromInt64(1)) });
        return new Contract(descriptor, configuration);
    }

    private static AlgorithmResult Result(Contract contract, InspectionDecision decision,
        string? reasonCode = null) =>
        new(decision, reasonCode, Array.Empty<AlgorithmMeasurement>(),
            new OutputOverlaySet(contract.Descriptor.ResultSchema.OverlayContract));

    private static FrameBufferLease CopyFrame(FrameBufferPool pool,
        ExecutionKind kind = ExecutionKind.Manual)
    {
        var correlation = new ExecutionCorrelationId(kind, Guid.NewGuid());
        var configuration = new EffectiveCameraConfiguration(
            ProductionAcquisitionMode.HardwareTrigger, 500, 1.5,
            new RegionOfInterest(0, 0, 2, 1), VisionPixelFormat.Mono8, null,
            500, 0, null);
        var metadata = new FrameMetadata(correlation, "TopCamera", 2, 1, 2,
            VisionPixelFormat.Mono8, null, Utc(10), configuration);
        var provenance = new FrameProvenance(correlation, "vendor-a", "1", "adapter-a", "1",
            "sdk-a", "1", null, "device-1", null, null, "Mono8", "normalized-v1",
            false, false, null, null, Milestones());
        var result = pool.TryCopyFrame(metadata, provenance, new byte[] { 1, 2 });
        return Assert.IsType<FrameBufferLease>(result.Lease);
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }

        Assert.True(predicate(), "The expected asynchronous cleanup did not complete.");
    }

    private static FrameAcquisitionMilestones Milestones() =>
        new(1_000_000, new FrameTimePoint(Utc(1), 10),
            new FrameTimePoint(Utc(2), 12), new FrameTimePoint(Utc(3), 14),
            new FrameTimePoint(Utc(4), 16));

    private static DateTimeOffset Utc(int second) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(second);

    private sealed record Contract(AlgorithmDescriptor Descriptor,
        AlgorithmConfigurationSnapshot Configuration);

    private sealed class PreparedFixture : IAsyncDisposable
    {
        public PreparedFixture(AlgorithmPreparationService preparation, PreparedAlgorithm prepared,
            TestFactory factory, TestAlgorithm algorithm, Contract contract)
        {
            Preparation = preparation; Prepared = prepared; Factory = factory;
            Algorithm = algorithm; Contract = contract;
        }

        public AlgorithmPreparationService Preparation { get; }
        public PreparedAlgorithm Prepared { get; }
        public TestFactory Factory { get; }
        public TestAlgorithm Algorithm { get; }
        public Contract Contract { get; }

        public async ValueTask DisposeAsync()
        {
            await Prepared.DisposeAsync();
            await Preparation.DisposeAsync();
        }
    }

    private sealed class TestFactory : IVisionAlgorithmFactory
    {
        private readonly TestAlgorithm _algorithm;
        private int _createCalls;

        public TestFactory(AlgorithmDescriptor descriptor, TestAlgorithm algorithm)
        { Descriptor = descriptor; _algorithm = algorithm; }

        public AlgorithmDescriptor Descriptor { get; }
        public int CreateCalls => Volatile.Read(ref _createCalls);

        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(
                Array.Empty<AlgorithmValidationIssue>());

        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _createCalls);
            return ValueTask.FromResult<IVisionAlgorithm>(_algorithm);
        }
    }

    private sealed class TestAlgorithm : IVisionAlgorithm
    {
        private readonly Func<AlgorithmExecutionContext, CancellationToken, ValueTask<AlgorithmResult>> _execute;
        private int _warmCalls;
        private int _executeCalls;
        private int _disposeCalls;

        public TestAlgorithm(Func<AlgorithmExecutionContext, CancellationToken, ValueTask<AlgorithmResult>> execute)
        { _execute = execute; }

        public int WarmCalls => Volatile.Read(ref _warmCalls);
        public int ExecuteCalls => Volatile.Read(ref _executeCalls);
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _warmCalls);
            return ValueTask.CompletedTask;
        }

        public async ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _executeCalls);
            return await _execute(context, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            return ValueTask.CompletedTask;
        }
    }
}
