using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Frames;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class AlgorithmExecutionGovernanceTests
{
    [Fact]
    public async Task V113_E01_MissingRequestAndRecipeAreRejectedAfterConsumingFrame()
    {
        await using var fixture = await Fixture.CreateAsync(() => new TestAlgorithm());
        await using var engine = new AlgorithmExecutionService(new(Policy("Policy.E01", 500),
            TimeSpan.FromSeconds(1)));

        var missing = await engine.ExecuteAsync(fixture.Prepared, fixture.Frame(), null!);
        AssertRejected(missing, "AlgorithmExecutionRequestRequired");

        var missingRecipe = await engine.ExecuteAsync(fixture.Prepared, fixture.Frame(),
            new AlgorithmExecutionRequest(null!, TimeSpan.FromMilliseconds(100)));
        AssertRejected(missingRecipe, "RecipeRequired");

        Assert.Equal(0, fixture.CurrentAlgorithm.ExecuteCalls);
        Assert.Equal(0, fixture.Pool.GetSnapshot().OutstandingLeases);
    }

    [Fact]
    public async Task V113_E02_InvalidRecipeHashAndTimeoutShapesAreRejectedWithoutExecution()
    {
        await using var fixture = await Fixture.CreateAsync(() => new TestAlgorithm());
        await using var engine = new AlgorithmExecutionService(new(Policy("Policy.E02", 500, minimumMs: 100),
            TimeSpan.FromSeconds(1)));
        var cases = new[]
        {
            (new AlgorithmExecutionRequest(new RecipeReference("recipe", "1", "not-a-sha256"),
                TimeSpan.FromMilliseconds(100)), "RecipeContentHashInvalid"),
            (Request("recipe-below", 99), "AlgorithmExecutionTimeoutBelowMinimum"),
            (Request("recipe-above", 501), "AlgorithmExecutionTimeoutAboveMaximum"),
            (Request("recipe-fractional", TimeSpan.FromMilliseconds(100) + TimeSpan.FromTicks(1)),
                "AlgorithmExecutionTimeoutInvalid"),
            (Request("recipe-unrepresentable", TimeSpan.FromTicks(
                (long)int.MaxValue * TimeSpan.TicksPerMillisecond + 1)),
                "AlgorithmExecutionTimeoutInvalid")
        };

        foreach (var (request, reason) in cases)
        {
            var attempt = await engine.ExecuteAsync(fixture.Prepared, fixture.Frame(), request);
            AssertRejected(attempt, reason);
            Assert.Equal(0, fixture.Pool.GetSnapshot().OutstandingLeases);
        }

        Assert.Equal(0, fixture.CurrentAlgorithm.ExecuteCalls);
    }

    [Fact]
    public async Task V113_E03_AdmittedOutcomeRetainsOldTimingWhenNewEngineUsesNewPolicy()
    {
        await using var fixture = await Fixture.CreateAsync(() => new TestAlgorithm());
        var oldPolicy = Policy("Policy.old", 3_000, graceMs: 5_000);
        var newPolicy = Policy("Policy.new", 4_000, graceMs: 5_000);
        using var releaseValidation = new ManualResetEventSlim();
        var validationEntered = Signal();
        await using var oldEngine = new AlgorithmExecutionService(
            new AlgorithmExecutionOptions(oldPolicy, TimeSpan.FromSeconds(1)),
            () =>
            {
                validationEntered.TrySetResult(true);
                releaseValidation.Wait(TimeSpan.FromSeconds(5));
            });
        var first = oldEngine.ExecuteAsync(fixture.Prepared, fixture.Frame(),
            Request("recipe.old", 2_000)).AsTask();

        try
        {
            await fixture.CurrentAlgorithm.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await validationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await using var newEngine = new AlgorithmExecutionService(
                new AlgorithmExecutionOptions(newPolicy, TimeSpan.FromSeconds(1)));
            releaseValidation.Set();

            var oldAttempt = await first.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(oldAttempt.Executed, oldAttempt.ReasonCode);
            var oldOutcome = Assert.IsType<AlgorithmExecutionOutcome>(oldAttempt.Outcome);
            AssertTiming(oldOutcome.Timing, oldPolicy, "recipe.old", 2_000);

            var newAttempt = await newEngine.ExecuteAsync(fixture.Prepared, fixture.Frame(),
                Request("recipe.new", 3_000));
            Assert.True(newAttempt.Executed, newAttempt.ReasonCode);
            var newOutcome = Assert.IsType<AlgorithmExecutionOutcome>(newAttempt.Outcome);
            AssertTiming(newOutcome.Timing, newPolicy, "recipe.new", 3_000);
            Assert.Equal(0, fixture.Pool.GetSnapshot().OutstandingLeases);
        }
        finally
        {
            releaseValidation.Set();
        }
    }

    [Fact]
    public async Task V113_E04_TimeoutFixesUnknownRetiresInstanceAndAllowsReplacement()
    {
        var cancellationObserved = Signal();
        var createCount = 0;
        await using var fixture = await Fixture.CreateAsync(() =>
            Interlocked.Increment(ref createCount) == 1
                ? CooperativeAlgorithm(cancellationObserved)
                : new TestAlgorithm());
        await using var engine = new AlgorithmExecutionService(new(Policy("Policy.E04", 2_000, graceMs: 5_000),
            TimeSpan.FromSeconds(1)));
        var retired = fixture.Prepared;
        var oldInstanceId = retired.InstanceId;
        var owner = fixture.Frame();
        var frame = owner.Frame;
        var attempt = await engine.ExecuteAsync(retired, owner, Request("recipe.timeout", 20));

        Assert.True(attempt.Executed, attempt.ReasonCode);
        var outcome = Assert.IsType<AlgorithmExecutionOutcome>(attempt.Outcome);
        Assert.Equal(ExecutionStatus.Timeout, outcome.ExecutionStatus);
        Assert.Equal(InspectionDecision.Unknown, outcome.Decision);
        Assert.Null(outcome.ValidatedResult);
        AssertTiming(outcome.Timing, enginePolicy: "Policy.E04", recipeId: "recipe.timeout", timeoutMs: 20,
            graceMs: 5_000);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await retired.DisposeAsync();
        await WaitUntilAsync(() => engine.ActiveExecutionCount == 0, TimeSpan.FromSeconds(2));
        Assert.False(frame.IsLoanActive);
        Assert.Equal(0, fixture.Pool.GetSnapshot().OutstandingLeases);
        Assert.Equal(1, fixture.CurrentAlgorithm.DisposeCalls);
        Assert.False(AlgorithmExecutionGuard.CurrentProcess.IsHung);

        var reused = await engine.ExecuteAsync(retired, fixture.Frame(), Request("recipe.reused", 20));
        AssertRejected(reused, "AlgorithmInstanceRetired");
        var replacement = await fixture.PrepareNextAsync();
        var replacementAttempt = await engine.ExecuteAsync(replacement, fixture.Frame(),
            Request("recipe.replacement", 1_000));
        Assert.True(replacementAttempt.Executed, replacementAttempt.ReasonCode);
        Assert.Equal(ExecutionStatus.Success, replacementAttempt.Outcome!.ExecutionStatus);
        Assert.NotEqual(oldInstanceId, replacementAttempt.Outcome.PreparedInstanceId);
        Assert.Equal(0, fixture.Pool.GetSnapshot().OutstandingLeases);
        Assert.False(AlgorithmExecutionGuard.CurrentProcess.IsHung);
    }

    [Fact]
    public async Task V113_E05_FaultAbortFixesUnknownAndReleasesCooperatively()
    {
        var cancellationObserved = Signal();
        await using var fixture = await Fixture.CreateAsync(() => CooperativeAlgorithm(cancellationObserved));
        await using var engine = new AlgorithmExecutionService(new(Policy("Policy.E05", 2_000, graceMs: 5_000),
            TimeSpan.FromSeconds(1)));
        using var abort = new CancellationTokenSource();
        var owner = fixture.Frame();
        var frame = owner.Frame;
        var pending = engine.ExecuteAsync(fixture.Prepared, owner, Request("recipe.abort", 1_000), abort.Token).AsTask();

        await fixture.CurrentAlgorithm.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        abort.Cancel();
        var attempt = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(attempt.Executed, attempt.ReasonCode);
        var outcome = Assert.IsType<AlgorithmExecutionOutcome>(attempt.Outcome);
        Assert.Equal(ExecutionStatus.Cancelled, outcome.ExecutionStatus);
        Assert.Equal(InspectionDecision.Unknown, outcome.Decision);
        Assert.Null(outcome.ValidatedResult);
        Assert.Equal("AlgorithmExecutionCancelled", outcome.ReasonCode);
        AssertTiming(outcome.Timing, enginePolicy: "Policy.E05", recipeId: "recipe.abort", timeoutMs: 1_000,
            graceMs: 5_000);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Prepared.DisposeAsync();
        Assert.False(frame.IsLoanActive);
        Assert.Equal(0, fixture.Pool.GetSnapshot().OutstandingLeases);
        Assert.Equal(1, fixture.CurrentAlgorithm.DisposeCalls);
        Assert.False(AlgorithmExecutionGuard.CurrentProcess.IsHung);
    }

    [Fact]
    public async Task V113_E06_ProductionCorrelationRemainsClosedAfterTimingBinding()
    {
        await using var fixture = await Fixture.CreateAsync(() => new TestAlgorithm());
        await using var engine = new AlgorithmExecutionService(new(Policy("Policy.E06", 500),
            TimeSpan.FromSeconds(1)));
        var owner = fixture.Frame(ExecutionKind.Production);
        var attempt = await engine.ExecuteAsync(fixture.Prepared, owner, Request("recipe.production", 100));

        AssertRejected(attempt, "ProductionExecutionAdmissionUnavailable");
        Assert.Equal(0, fixture.CurrentAlgorithm.ExecuteCalls);
        Assert.Equal(0, fixture.Pool.GetSnapshot().OutstandingLeases);
        Assert.False(AlgorithmExecutionGuard.CurrentProcess.IsHung);
    }

    [Fact]
    public async Task V113_E07_StartBarrierCrossingDeadlineNeverCallsAlgorithmAndStillRetires()
    {
        await using var fixture = await Fixture.CreateAsync(() => new TestAlgorithm());
        using var releaseStart = new ManualResetEventSlim();
        var startEntered = Signal();
        await using var engine = new AlgorithmExecutionService(
            new AlgorithmExecutionOptions(Policy("Policy.E07", 200, graceMs: 5_000),
                TimeSpan.FromSeconds(1)), null,
            beforeExecutionStartForTesting: () =>
            {
                startEntered.TrySetResult(true);
                releaseStart.Wait(TimeSpan.FromSeconds(5));
            });
        var owner = fixture.Frame();
        var frame = owner.Frame;
        var pending = Task.Run(async () => await engine.ExecuteAsync(fixture.Prepared, owner,
            Request("recipe.start-barrier", 20)));

        try
        {
            await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(100);
            Assert.Equal(0, fixture.CurrentAlgorithm.ExecuteCalls);
            releaseStart.Set();

            var attempt = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(attempt.Executed, attempt.ReasonCode);
            var outcome = Assert.IsType<AlgorithmExecutionOutcome>(attempt.Outcome);
            Assert.Equal(ExecutionStatus.Timeout, outcome.ExecutionStatus);
            Assert.Equal(InspectionDecision.Unknown, outcome.Decision);
            Assert.Null(outcome.ValidatedResult);
            Assert.Equal(0, fixture.CurrentAlgorithm.ExecuteCalls);
            await fixture.Prepared.DisposeAsync();
            Assert.False(frame.IsLoanActive);
            Assert.Equal(0, fixture.Pool.GetSnapshot().OutstandingLeases);
            Assert.Equal(1, fixture.CurrentAlgorithm.DisposeCalls);
            Assert.False(AlgorithmExecutionGuard.CurrentProcess.IsHung);
        }
        finally
        {
            releaseStart.Set();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { }
        }
    }

    private static TestAlgorithm CooperativeAlgorithm(TaskCompletionSource<bool> cancellationObserved) =>
        new(async (_, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult(true);
            }

            return Result();
        });

    private static AlgorithmExecutionPolicy Policy(string id, int maximumMs,
        int minimumMs = 1, int graceMs = 5_000) =>
        new(id, "v1", TimeSpan.FromMilliseconds(minimumMs), TimeSpan.FromMilliseconds(maximumMs),
            TimeSpan.FromMilliseconds(graceMs));

    private static AlgorithmExecutionRequest Request(string recipeId, int timeoutMs) =>
        Request(recipeId, TimeSpan.FromMilliseconds(timeoutMs));

    private static AlgorithmExecutionRequest Request(string recipeId, TimeSpan timeout) =>
        new(new RecipeReference(recipeId, "1", new string('a', 64)), timeout);

    private static TaskCompletionSource<bool> Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
            await Task.Delay(10, cancellation.Token);
    }

    private static void AssertRejected(AlgorithmExecutionAttempt attempt, string reasonCode)
    {
        Assert.False(attempt.Executed);
        Assert.Equal(reasonCode, attempt.ReasonCode);
        Assert.Null(attempt.Outcome);
    }

    private static void AssertTiming(AlgorithmExecutionTimingSnapshot timing,
        AlgorithmExecutionPolicy policy, string recipeId, int timeoutMs) =>
        AssertTiming(timing, policy.Id, recipeId, timeoutMs, (int)policy.CancellationGracePeriod.TotalMilliseconds);

    private static void AssertTiming(AlgorithmExecutionTimingSnapshot timing, string enginePolicy,
        string recipeId, int timeoutMs, int graceMs)
    {
        Assert.Equal(enginePolicy, timing.PolicyId);
        Assert.Equal("v1", timing.PolicyVersion);
        Assert.Equal(recipeId, timing.Recipe.Id);
        Assert.Equal("1", timing.Recipe.Version);
        Assert.Equal(new string('a', 64), timing.Recipe.ContentHash);
        Assert.Equal(TimeSpan.FromMilliseconds(timeoutMs), timing.AlgorithmExecutionTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(graceMs), timing.CancellationGracePeriod);
    }

    private static AlgorithmResult Result() =>
        new(InspectionDecision.Pass, null, Array.Empty<AlgorithmMeasurement>(), new("Test.Overlay", "1"));

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly AlgorithmPreparationService _preparation;

        private Fixture(Func<TestAlgorithm> create)
        {
            Factory = new TestFactory(create);
            _preparation = new(new[] { Factory }, new AlgorithmPreparationOptions(TimeSpan.FromSeconds(5)));
            Pool = new(new FrameBufferPoolOptions(2, 64, TimeSpan.FromSeconds(1)));
        }

        public TestFactory Factory { get; }
        public FrameBufferPool Pool { get; }
        public PreparedAlgorithm Prepared { get; private set; } = null!;
        public TestAlgorithm CurrentAlgorithm => Factory.Algorithms[^1];

        public static async Task<Fixture> CreateAsync(Func<TestAlgorithm> create)
        {
            var fixture = new Fixture(create);
            await fixture.PrepareNextAsync();
            return fixture;
        }

        public async Task<PreparedAlgorithm> PrepareNextAsync()
        {
            var descriptor = Factory.Descriptor;
            var configuration = AlgorithmConfigurationSnapshot.Create(
                descriptor.ConfigurationSchema, Array.Empty<AlgorithmConfigurationEntry>());
            var resultSchema = descriptor.ResultSchema;
            var overlay = resultSchema.OverlayContract;
            var result = await _preparation.PrepareAsync(new AlgorithmPreparationRequest(
                descriptor.Identity, configuration, resultSchema.Id, resultSchema.Version,
                resultSchema.ContentHash, overlay.Id, overlay.Version, overlay.ContentHash,
                TimeSpan.FromSeconds(2)));
            Assert.True(result.Succeeded, result.ReasonCode);
            Prepared = Assert.IsType<PreparedAlgorithm>(result.Prepared);
            return Prepared;
        }

        public FrameBufferLease Frame(ExecutionKind kind = ExecutionKind.Manual)
        {
            var correlation = new ExecutionCorrelationId(kind, Guid.NewGuid());
            var camera = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
                1000, 0, new RegionOfInterest(0, 0, 1, 1), VisionPixelFormat.Mono8, null,
                1000, 0, null);
            var metadata = new FrameMetadata(correlation, "Primary", 1, 1, 1,
                VisionPixelFormat.Mono8, null, DateTimeOffset.UtcNow, camera);
            var provenance = new FrameProvenance(correlation, "Fixture", "1", "Fixture", "1",
                "Fixture", "1", null, "Memory", null, null, "Mono8", "Identity", false, false,
                null, null, new FrameAcquisitionMilestones(1, null, null, null, null));
            var copied = Pool.TryCopyFrame(metadata, provenance, new byte[] { 42 });
            Assert.True(copied.Succeeded, copied.ReasonCode);
            return Assert.IsType<FrameBufferLease>(copied.Lease);
        }

        public async ValueTask DisposeAsync()
        {
            await Prepared.DisposeAsync();
            await _preparation.DisposeAsync();
            Pool.Dispose();
        }
    }

    private sealed class TestFactory : IVisionAlgorithmFactory
    {
        private readonly Func<TestAlgorithm> _create;

        public TestFactory(Func<TestAlgorithm> create)
        {
            _create = create;
            Descriptor = new AlgorithmDescriptor(new("Governance.TestAlgorithm", "1"),
                new("Governance.Config", "1", Array.Empty<AlgorithmFieldDefinition>()),
                new("Governance.Result", "1", Array.Empty<AlgorithmFieldDefinition>(),
                    Array.Empty<string>(), new("Test.Overlay", "1")));
        }

        public AlgorithmDescriptor Descriptor { get; }
        public List<TestAlgorithm> Algorithms { get; } = new();

        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(
                configuration.Validate(Descriptor.ConfigurationSchema));

        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var algorithm = _create();
            Algorithms.Add(algorithm);
            return ValueTask.FromResult<IVisionAlgorithm>(algorithm);
        }
    }

    private sealed class TestAlgorithm : IVisionAlgorithm
    {
        private readonly Func<AlgorithmExecutionContext, CancellationToken, ValueTask<AlgorithmResult>> _execute;
        private int _warmCalls;
        private int _executeCalls;
        private int _disposeCalls;

        public TestAlgorithm(
            Func<AlgorithmExecutionContext, CancellationToken, ValueTask<AlgorithmResult>>? execute = null) =>
            _execute = execute ?? ((_, _) => ValueTask.FromResult(Result()));

        public TaskCompletionSource<bool> Entered { get; } = Signal();
        public int WarmCalls => Volatile.Read(ref _warmCalls);
        public int ExecuteCalls => Volatile.Read(ref _executeCalls);
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _warmCalls);
            return ValueTask.CompletedTask;
        }

        public async ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _executeCalls);
            Entered.TrySetResult(true);
            return await _execute(context, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            return ValueTask.CompletedTask;
        }
    }
}
