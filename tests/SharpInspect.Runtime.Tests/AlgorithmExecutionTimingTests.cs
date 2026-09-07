using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Frames;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class AlgorithmExecutionTimingTests
{
    [Fact]
    public async Task V112_T01_ValidationCrossingDeadlineCannotPublishLatePassOrReturnItsLeaseEarly()
    {
        using var releaseValidation = new ManualResetEventSlim();
        var validationEntered = Signal();
        var algorithm = new ProbeAlgorithm();
        await using var fixture = await Fixture.CreateAsync(algorithm);
        await using var engine = new AlgorithmExecutionService(new(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100)),
            () => { validationEntered.TrySetResult(true); releaseValidation.Wait(TimeSpan.FromSeconds(5)); });
        var owner = fixture.Frame();
        var frame = owner.Frame;
        try
        {
            var pending = engine.ExecuteAsync(fixture.Prepared, owner, TimeSpan.FromMilliseconds(500)).AsTask();
            await validationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var terminal = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ExecutionStatus.Timeout, terminal.Outcome!.ExecutionStatus);
            Assert.Equal(InspectionDecision.Unknown, terminal.Outcome.Decision);
            Assert.Null(terminal.Outcome.ValidatedResult);
            Assert.True(frame.IsLoanActive);
            Assert.Equal(1, fixture.Pool.GetSnapshot().OutstandingLeases);
            Assert.Equal(0, algorithm.DisposeCount);
            var repeated = await engine.ExecuteAsync(fixture.Prepared, fixture.Frame(), TimeSpan.FromSeconds(1));
            Assert.False(repeated.Executed);
            Assert.Equal("AlgorithmExecutionBusy", repeated.ReasonCode);
            releaseValidation.Set();
            await EventuallyAsync(() => engine.ActiveExecutionCount == 0);
            Assert.False(frame.IsLoanActive);
            Assert.Equal(0, fixture.Pool.GetSnapshot().OutstandingLeases);
            Assert.Equal(1, algorithm.DisposeCount);
            Assert.Equal(ExecutionStatus.Timeout, terminal.Outcome.ExecutionStatus);
            Assert.Null(terminal.Outcome.ValidatedResult);
        }
        finally { releaseValidation.Set(); }
    }

    [Fact]
    public async Task V112_T02_RuntimeAbortDoesNotRunConsumerCancellationOnCallerOrReleaseDuringItsCallback()
    {
        using var releaseCallback = new ManualResetEventSlim();
        using var abort = new CancellationTokenSource();
        var algorithm = new ProbeAlgorithm(releaseCallback);
        await using var fixture = await Fixture.CreateAsync(algorithm);
        await using var engine = new AlgorithmExecutionService(new(TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(100)));
        var owner = fixture.Frame();
        var frame = owner.Frame;
        try
        {
            var pending = engine.ExecuteAsync(fixture.Prepared, owner, TimeSpan.FromSeconds(3), abort.Token).AsTask();
            await algorithm.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var cancelCaller = Task.Run(abort.Cancel);
            await cancelCaller.WaitAsync(TimeSpan.FromSeconds(1));
            await algorithm.CallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var terminal = await pending.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(ExecutionStatus.Cancelled, terminal.Outcome!.ExecutionStatus);
            algorithm.ReturnNow.TrySetResult(true);
            await algorithm.Returned.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(frame.IsLoanActive);
            Assert.Equal(1, fixture.Pool.GetSnapshot().OutstandingLeases);
            Assert.Equal(0, algorithm.DisposeCount);
            Assert.Equal(1, engine.ActiveExecutionCount);
            releaseCallback.Set();
            await EventuallyAsync(() => engine.ActiveExecutionCount == 0);
            Assert.False(frame.IsLoanActive);
            Assert.Equal(1, algorithm.DisposeCount);
            Assert.Equal(ExecutionStatus.Cancelled, terminal.Outcome.ExecutionStatus);
        }
        finally { algorithm.ReturnNow.TrySetResult(true); releaseCallback.Set(); }
    }

    [Fact]
    public async Task V112_T03_CompletedDiagnosticSinkCannotEmitIntoTheNextExecution()
    {
        var algorithm = new ProbeAlgorithm();
        await using var fixture = await Fixture.CreateAsync(algorithm);
        await using var engine = new AlgorithmExecutionService(new(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100)));
        var first = await engine.ExecuteAsync(fixture.Prepared, fixture.Frame(), TimeSpan.FromSeconds(1));
        Assert.Equal(ExecutionStatus.Success, first.Outcome!.ExecutionStatus);
        var oldSink = algorithm.LastSink!;
        Assert.Equal(1, engine.DroppedDiagnosticCount);
        Assert.Equal(AlgorithmDiagnosticEmission.Dropped, oldSink.TryEmit(new("Late", new[]
            { new AlgorithmDiagnosticField("Data", AlgorithmScalarValue.FromString("password=do-not-format")) })));
        Assert.Equal(1, engine.DroppedDiagnosticCount);
        var second = await engine.ExecuteAsync(fixture.Prepared, fixture.Frame(), TimeSpan.FromSeconds(1));
        Assert.Equal(ExecutionStatus.Success, second.Outcome!.ExecutionStatus);
        Assert.NotSame(oldSink, algorithm.LastSink);
        Assert.Equal(2, engine.DroppedDiagnosticCount);
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    [Fact]
    public async Task V112_T04_RetirementBetweenExecutionBorrowAndTaskStartWaitsForThatBorrow()
    {
        using var releaseStart = new ManualResetEventSlim();
        var borrowed = Signal();
        var algorithm = new ProbeAlgorithm();
        await using var fixture = await Fixture.CreateAsync(algorithm);
        await using var engine = new AlgorithmExecutionService(new(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100)),
            null, () => { borrowed.TrySetResult(true); releaseStart.Wait(TimeSpan.FromSeconds(5)); });
        var owner = fixture.Frame();
        try
        {
            var execution = Task.Run(async () => await engine.ExecuteAsync(fixture.Prepared, owner, TimeSpan.FromSeconds(2)));
            await borrowed.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var retirement = fixture.Prepared.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            Assert.Equal(0, algorithm.DisposeCount);
            Assert.Equal(1, fixture.Pool.GetSnapshot().OutstandingLeases);
            releaseStart.Set();
            var outcome = await execution.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ExecutionStatus.Success, outcome.Outcome!.ExecutionStatus);
            await retirement.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1, algorithm.DisposeCount);
            Assert.Equal(0, fixture.Pool.GetSnapshot().OutstandingLeases);
        }
        finally { releaseStart.Set(); }
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class ProbeAlgorithm : IVisionAlgorithm
    {
        private readonly ManualResetEventSlim? _releaseCallback;
        private int _disposeCount;
        public ProbeAlgorithm(ManualResetEventSlim? releaseCallback = null) => _releaseCallback = releaseCallback;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public IAlgorithmDiagnosticSink? LastSink { get; private set; }
        public TaskCompletionSource<bool> Entered { get; } = Signal();
        public TaskCompletionSource<bool> ReturnNow { get; } = Signal();
        public TaskCompletionSource<bool> Returned { get; } = Signal();
        public TaskCompletionSource<bool> CallbackEntered { get; } = Signal();
        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public async ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context, CancellationToken cancellationToken = default)
        {
            LastSink = context.Diagnostics;
            context.Diagnostics.TryEmit(new("UnconfiguredDiagnostic"));
            if (_releaseCallback is not null)
                cancellationToken.Register(() => { CallbackEntered.TrySetResult(true); _releaseCallback.Wait(TimeSpan.FromSeconds(5)); });
            Entered.TrySetResult(true);
            if (_releaseCallback is not null) await ReturnNow.Task;
            Assert.Equal((byte)42, context.Frame.GetRowSpan(0)[0]);
            Returned.TrySetResult(true);
            return new(InspectionDecision.Pass, null, Array.Empty<AlgorithmMeasurement>(), new("Overlay", "1"));
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref _disposeCount); return ValueTask.CompletedTask; }
    }

    private sealed class Fixture : IAsyncDisposable, IVisionAlgorithmFactory
    {
        private readonly IVisionAlgorithm _algorithm;
        private AlgorithmPreparationService _preparation = null!;
        public PreparedAlgorithm Prepared { get; private set; } = null!;
        public FrameBufferPool Pool { get; } = new(new(2, 8, TimeSpan.FromSeconds(1)));
        public AlgorithmDescriptor Descriptor { get; } = new(new("Timing", "1"),
            new("Config", "1", Array.Empty<AlgorithmFieldDefinition>()),
            new("Result", "1", Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>(), new("Overlay", "1")));
        private Fixture(IVisionAlgorithm algorithm) => _algorithm = algorithm;
        public static async Task<Fixture> CreateAsync(IVisionAlgorithm algorithm)
        {
            var fixture = new Fixture(algorithm);
            fixture._preparation = new(new[] { fixture }, new(TimeSpan.FromSeconds(2)));
            var descriptor = fixture.Descriptor;
            var config = AlgorithmConfigurationSnapshot.Create(descriptor.ConfigurationSchema, Array.Empty<AlgorithmConfigurationEntry>());
            var prepared = await fixture._preparation.PrepareAsync(new(descriptor.Identity, config,
                descriptor.ResultSchema.Id, descriptor.ResultSchema.Version, descriptor.ResultSchema.ContentHash,
                descriptor.ResultSchema.OverlayContract.Id, descriptor.ResultSchema.OverlayContract.Version,
                descriptor.ResultSchema.OverlayContract.ContentHash, TimeSpan.FromSeconds(1)));
            Assert.True(prepared.Succeeded, prepared.ReasonCode);
            fixture.Prepared = prepared.Prepared!;
            return fixture;
        }
        public FrameBufferLease Frame()
        {
            var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
            var config = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 1000, 0,
                new(0, 0, 1, 1), VisionPixelFormat.Mono8, null, 1000, 0, null);
            var metadata = new FrameMetadata(correlation, "Primary", 1, 1, 1, VisionPixelFormat.Mono8,
                null, DateTimeOffset.UtcNow, config);
            var provenance = new FrameProvenance(correlation, "Fixture", "1", "Fixture", "1", "None", "1",
                null, "Memory", null, null, "Mono8", "Identity", false, false, null, null,
                new(1, null, null, null, null));
            var copied = Pool.TryCopyFrame(metadata, provenance, new byte[] { 42 });
            Assert.True(copied.Succeeded, copied.ReasonCode);
            return copied.Lease!;
        }
        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(Array.Empty<AlgorithmValidationIssue>());
        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(_algorithm);
        public async ValueTask DisposeAsync() { await _preparation.DisposeAsync(); Pool.Dispose(); }
    }
}
