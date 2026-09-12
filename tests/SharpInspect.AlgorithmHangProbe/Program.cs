using System.Diagnostics;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Frames;

namespace SharpInspect.AlgorithmHangProbe;

internal static class Program
{
    private const string PolicyId = "Probe.ExecutionPolicy";
    private const string PolicyVersion = "v1";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "image-finalize")
            return await ImageFinalizationProbe.RunAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
        if (args.Length > 0 && args[0] == "outbox-receiver")
            return await OutboxReceiverProbe.RunAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
        var mode = args.Length == 1 ? args[0] : string.Empty;
        if (mode is "fresh")
            return await RunFreshProcessProbeAsync().ConfigureAwait(false);
        if (mode is "lazy")
            return await RunLazyAdmissionProbeAsync().ConfigureAwait(false);
        if (mode is "alarm")
            return await AlgorithmAlarmProbe.RunAsync().ConfigureAwait(false);
        if (mode is not ("hang" or "late-pass" or "late-exception" or "cancel-callback"))
        {
            WriteEvent("probe-error", mode, "InvalidMode");
            return 2;
        }

        try
        {
            return await RunExecutionProbeAsync(mode).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Do not place exception messages, paths, or stack traces in the probe contract.
            WriteEvent("probe-error", mode, "ProbeFailure");
            return 3;
        }
    }

    private static async Task<int> RunLazyAdmissionProbeAsync()
    {
        var policy = new AlgorithmExecutionPolicy(PolicyId, PolicyVersion,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(150));
        var request = new AlgorithmExecutionRequest(
            new RecipeReference("Probe.Recipe", "v1", new string('A', 64)),
            TimeSpan.FromMilliseconds(100));
        var options = new AlgorithmExecutionOptions(policy, TimeSpan.FromMilliseconds(250));
        var firstAlgorithm = new ProbeAlgorithm("hang");
        var secondAlgorithm = new ProbeAlgorithm("late-pass");
        await using var first = await ProbeFixture.CreateAsync(firstAlgorithm).ConfigureAwait(false);
        await using var second = await ProbeFixture.CreateAsync(secondAlgorithm).ConfigureAwait(false);
        await using var firstEngine = new AlgorithmExecutionService(options, null, null,
            suppressGraceWatchdogForTesting: true);
        var firstFrame = first.Frame();
        var firstAttemptTask = firstEngine.ExecuteAsync(first.Prepared, firstFrame, request).AsTask();
        await firstAlgorithm.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var firstAttempt = await firstAttemptTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var firstOutcome = firstAttempt.Outcome;
        WriteEvent("lazy-terminal", "lazy", "Terminal", new Dictionary<string, object?>
        {
            ["status"] = SafeReason(firstOutcome?.ExecutionStatus.ToString()),
            ["decision"] = SafeReason(firstOutcome?.Decision.ToString()),
            ["reason"] = SafeReason(firstOutcome?.ReasonCode),
            ["timing"] = TimingView(firstOutcome?.Timing)
        });

        // Deliberately do not read Guard.IsHung, Snapshot, or BlockedReasonCode here.
        // The next admission must be the first lazy expiry observation.
        await Task.Delay(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);
        var secondFrame = second.Frame();
        await using var secondEngine = new AlgorithmExecutionService(options);
        var secondAttempt = await secondEngine.ExecuteAsync(second.Prepared, secondFrame, request)
            .ConfigureAwait(false);
        var preparationAfter = await second.TryPrepareAgainAsync().ConfigureAwait(false);
        WriteEvent("lazy-admission", "lazy", secondAttempt.Executed ? "Accepted" : "Rejected",
            new Dictionary<string, object?>
            {
                ["firstStatus"] = SafeReason(firstOutcome?.ExecutionStatus.ToString()),
                ["firstDecision"] = SafeReason(firstOutcome?.Decision.ToString()),
                ["preparedBeforeAdmission"] = true,
                ["executed"] = secondAttempt.Executed,
                ["reason"] = SafeReason(secondAttempt.ReasonCode),
                ["secondExecuteCalls"] = secondAlgorithm.ExecuteCalls,
                ["prepareAfterHung"] = preparationAfter.Succeeded,
                ["prepareReason"] = SafeReason(preparationAfter.ReasonCode),
                ["guardHung"] = AlgorithmExecutionGuard.CurrentProcess.IsHung
            });
        return 0;
    }

    private static async Task<int> RunFreshProcessProbeAsync()
    {
        var guard = AlgorithmExecutionGuard.CurrentProcess;
        await using var runtime = new StationRuntime();
        var snapshot = await runtime.GetSnapshotAsync().ConfigureAwait(false);
        WriteEvent("fresh-process", "fresh", "Ready",
            new Dictionary<string, object?>
            {
                ["hung"] = guard.IsHung,
                ["blockedReason"] = guard.IsHung ? "AlgorithmHung" : "None",
                ["ready"] = snapshot.Ready,
                ["startupRecoveryBlocked"] = snapshot.AdmissionBlockers.Contains("StartupRecoveryNotVerified")
            });
        return 0;
    }

    private static async Task<int> RunExecutionProbeAsync(string mode)
    {
        var algorithm = new ProbeAlgorithm(mode);
        await using var fixture = await ProbeFixture.CreateAsync(algorithm).ConfigureAwait(false);
        WriteEvent("phase", mode, "FixtureReady");
        InFlightPreparationProbe? inFlight = null;
        if (mode == "hang")
            inFlight = await InFlightPreparationProbe.StartAsync().ConfigureAwait(false);
        var executionTimeout = mode == "cancel-callback"
            ? TimeSpan.FromSeconds(2)
            : TimeSpan.FromMilliseconds(250);
        var policy = new AlgorithmExecutionPolicy(PolicyId, PolicyVersion,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(150));
        var request = new AlgorithmExecutionRequest(
            new RecipeReference("Probe.Recipe", "v1", new string('A', 64)),
            executionTimeout);
        var options = new AlgorithmExecutionOptions(policy, TimeSpan.FromMilliseconds(250));
        WriteEvent("phase", mode, "OptionsReady");
        await using var engine = new AlgorithmExecutionService(options);

        using var runtimeCancellation = new CancellationTokenSource();
        var frame = fixture.Frame();
        WriteEvent("phase", mode, "FrameReady");
        Task<AlgorithmExecutionAttempt> executionTask;
        try
        {
            executionTask = engine.ExecuteAsync(fixture.Prepared, frame, request,
                runtimeCancellation.Token).AsTask();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            WriteEvent("probe-error", mode, "ExecutionStartFailure");
            return 5;
        }
        try
        {
            await algorithm.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            WriteEvent("probe-error", mode, "AlgorithmDidNotStart");
            return 5;
        }
        if (mode == "cancel-callback")
        {
            // Cancel from a separate thread: the deliberately blocking consumer
            // callback must not block this probe before the engine publishes its
            // bounded Cancelled outcome.
            _ = Task.Run(runtimeCancellation.Cancel);
        }
        WriteEvent("algorithm-started", mode, "Running", ResourceState(fixture, engine));

        AlgorithmExecutionAttempt attempt;
        try
        {
            attempt = await executionTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            WriteEvent("probe-error", mode, "ExecutionDidNotReachBoundedOutcome", ResourceState(fixture, engine));
            return 5;
        }

        var outcome = attempt.Outcome;
        WriteEvent("execute-terminal", mode, "Terminal", Merge(
            ResourceState(fixture, engine),
            new Dictionary<string, object?>
            {
                ["executed"] = attempt.Executed,
                ["attemptReason"] = SafeReason(attempt.ReasonCode),
                ["status"] = SafeReason(outcome?.ExecutionStatus.ToString()),
                ["decision"] = SafeReason(outcome?.Decision.ToString()),
                ["reason"] = SafeReason(outcome?.ReasonCode),
                ["admittedMonotonicTimestamp"] = outcome?.AdmittedMonotonicTimestamp ?? 0,
                ["admittedMonotonicFrequency"] = outcome?.MonotonicFrequency ?? 0,
                ["timing"] = TimingView(outcome?.Timing)
            }));

        var hung = await WaitForHungAsync(engine, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        WriteEvent("hung-observed", mode, hung.IsHung ? "Hung" : "NotHung", Merge(
            ResourceState(fixture, engine),
            new Dictionary<string, object?>
            {
                ["hung"] = hung.IsHung,
                ["blockedReason"] = SafeReason(hung.BlockedReason),
                ["guardHung"] = hung.GuardHung,
                ["callbackEntered"] = algorithm.CallbackEntered.Task.IsCompleted
            }));

        if (inFlight is not null)
        {
            var inFlightResult = await inFlight.ReleaseAndWaitAsync().ConfigureAwait(false);
            WriteEvent("prepare-inflight-after-hung", mode,
                inFlightResult.Succeeded ? "Accepted" : "Rejected",
                new Dictionary<string, object?>
                {
                    ["succeeded"] = inFlightResult.Succeeded,
                    ["published"] = inFlightResult.Prepared is not null,
                    ["reason"] = SafeReason(inFlightResult.ReasonCode),
                    ["disposeCount"] = inFlight.DisposeCount
                });
            await inFlight.DisposeAsync().ConfigureAwait(false);
            inFlight = null;
        }

        var preparationAfterHung = await fixture.TryPrepareAgainAsync().ConfigureAwait(false);
        WriteEvent("prepare-after-hung", mode,
            preparationAfterHung.Succeeded ? "Accepted" : "Rejected",
            new Dictionary<string, object?>
            {
                ["succeeded"] = preparationAfterHung.Succeeded,
                ["reason"] = SafeReason(preparationAfterHung.ReasonCode)
            });

        await TryExecuteWithNewServiceAsync(options, fixture, request).ConfigureAwait(false);

        if (mode is "late-pass" or "late-exception")
        {
            var returned = await algorithm.Returned.Task.WaitAsync(TimeSpan.FromSeconds(3))
                .ConfigureAwait(false);
            await Task.Delay(50).ConfigureAwait(false);
            var lateOutcome = attempt.Outcome;
            WriteEvent("late-observed", mode, returned ? "Returned" : "Missing", Merge(
                ResourceState(fixture, engine),
                new Dictionary<string, object?>
                {
                    ["algorithmReturned"] = returned,
                    ["status"] = SafeReason(lateOutcome?.ExecutionStatus.ToString()),
                    ["decision"] = SafeReason(lateOutcome?.Decision.ToString()),
                    ["reason"] = SafeReason(lateOutcome?.ReasonCode),
                    ["guardHung"] = AlgorithmExecutionGuard.CurrentProcess.IsHung
                }));
            await engine.DisposeAsync().ConfigureAwait(false);
            return 0;
        }

        if (mode == "cancel-callback")
            await algorithm.CallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        // These modes intentionally keep the uncooperative child alive. The parent test
        // owns the hard process deadline and is the only code allowed to terminate it.
        WriteEvent("awaiting-parent-termination", mode, "IsolatedHang", ResourceState(fixture, engine));
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        return 0;
    }

    private static async Task TryExecuteWithNewServiceAsync(AlgorithmExecutionOptions options,
        ProbeFixture fixture, AlgorithmExecutionRequest request)
    {
        await using var engine = new AlgorithmExecutionService(options);
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 8, TimeSpan.FromSeconds(1)));

        try
        {
            var frame = fixture.Frame(pool);
            var attempt = await engine.ExecuteAsync(fixture.Prepared, frame, request,
                CancellationToken.None).ConfigureAwait(false);
            WriteEvent("new-engine", "probe", attempt.Executed ? "Accepted" : "Rejected",
                new Dictionary<string, object?>
                {
                    ["executed"] = attempt.Executed,
                    ["reason"] = SafeReason(attempt.ReasonCode)
                });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            WriteEvent("new-engine", "probe", "Rejected",
                new Dictionary<string, object?> { ["reason"] = "ExecutionBlocked" });
        }
        finally
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<(bool IsHung, bool GuardHung, string? BlockedReason)> WaitForHungAsync(
        AlgorithmExecutionService engine, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var guard = AlgorithmExecutionGuard.CurrentProcess;
            var blockedReason = engine.BlockedReasonCode;
            var isHung = guard.IsHung || blockedReason?.Contains("Hung", StringComparison.OrdinalIgnoreCase) == true;
            if (isHung)
                return (true, guard.IsHung, blockedReason);
            await Task.Delay(20).ConfigureAwait(false);
        }

        var finalGuard = AlgorithmExecutionGuard.CurrentProcess;
        var finalReason = engine.BlockedReasonCode;
        return (finalGuard.IsHung || finalReason?.Contains("Hung", StringComparison.OrdinalIgnoreCase) == true,
            finalGuard.IsHung, finalReason);
    }

    private static Dictionary<string, object?> ResourceState(ProbeFixture fixture,
        AlgorithmExecutionService engine) =>
        new()
        {
            ["outstandingLeases"] = fixture.Pool.GetSnapshot().OutstandingLeases,
            ["activeReaders"] = fixture.Pool.GetSnapshot().ActiveReaders,
            ["frameLoanActive"] = fixture.LastVisionFrame?.IsLoanActive ?? false,
            ["activeExecutions"] = engine.ActiveExecutionCount,
            ["blockedReason"] = SafeReason(engine.BlockedReasonCode)
        };

    private static Dictionary<string, object?> TimingView(AlgorithmExecutionTimingSnapshot? timing)
    {
        if (timing is null) return new() { ["present"] = false };
        var recipe = timing.Recipe;
        return new()
        {
            ["present"] = true,
            ["recipeId"] = SafeReason(recipe.Id),
            ["recipeVersion"] = SafeReason(recipe.Version),
            ["policyId"] = SafeReason(timing.PolicyId),
            ["policyVersion"] = SafeReason(timing.PolicyVersion),
            ["policyContentHash"] = SafeHash(timing.PolicyContentHash),
            ["timeoutMilliseconds"] = (long)timing.AlgorithmExecutionTimeout.TotalMilliseconds,
            ["graceMilliseconds"] = (long)timing.CancellationGracePeriod.TotalMilliseconds
        };
    }

    private static Dictionary<string, object?> Merge(Dictionary<string, object?> first,
        Dictionary<string, object?> second)
    {
        foreach (var item in second) first[item.Key] = item.Value;
        return first;
    }

    private static string SafeReason(string? value) =>
        value is not null && value.Length <= 128 && value.All(character =>
            char.IsLetterOrDigit(character) || character is '.' or '_' or '-') ? value : "Unknown";

    private static string SafeHash(string? value) =>
        value is not null && value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f')
            ? value : "Unknown";

    private static void WriteEvent(string eventName, string mode, string status,
        Dictionary<string, object?>? fields = null)
    {
        var payload = fields ?? new Dictionary<string, object?>();
        payload["event"] = eventName;
        payload["mode"] = mode;
        payload["eventStatus"] = status;
        if (!payload.ContainsKey("status")) payload["status"] = status;
        Console.WriteLine(JsonSerializer.Serialize(payload));
        Console.Out.Flush();
    }

    private sealed class ProbeAlgorithm : IVisionAlgorithm
    {
        private readonly string _mode;
        private int _executeCalls;
        public ProbeAlgorithm(string mode) => _mode = mode;
        public TaskCompletionSource<bool> Started { get; } = Signal();
        public TaskCompletionSource<bool> Returned { get; } = Signal();
        public TaskCompletionSource<bool> CallbackEntered { get; } = Signal();
        public int ExecuteCalls => Volatile.Read(ref _executeCalls);
        private static TaskCompletionSource<bool> Signal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public async ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _executeCalls);
            WriteEvent("algorithm-entry", _mode, "Running");
            if (_mode == "cancel-callback")
            {
                cancellationToken.Register(() =>
                {
                    CallbackEntered.TrySetResult(true);
                    Thread.Sleep(Timeout.Infinite);
                });
                Started.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            }
            else if (_mode == "hang")
            {
                Started.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            }
            else
            {
                Started.TrySetResult(true);
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                Returned.TrySetResult(true);
                if (_mode == "late-exception")
                    throw new AlgorithmExecutionException("LateFailure");
            }

            return new AlgorithmResult(InspectionDecision.Pass, null,
                Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet("Probe.Overlay", "v1"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ProbeFixture : IAsyncDisposable, IVisionAlgorithmFactory
    {
        private readonly IVisionAlgorithm _algorithm;
        private ProbeFixture(IVisionAlgorithm algorithm)
        {
            _algorithm = algorithm;
            Pool = new FrameBufferPool(new FrameBufferPoolOptions(2, 8, TimeSpan.FromSeconds(1)));
            Descriptor = new AlgorithmDescriptor(new("Probe.Algorithm", "v1"),
                new("Probe.Config", "v1", Array.Empty<AlgorithmFieldDefinition>()),
                new("Probe.Result", "v1", Array.Empty<AlgorithmFieldDefinition>(),
                    new[] { "LateFailure" }, new("Probe.Overlay", "v1")));
        }

        public AlgorithmPreparationService Preparation { get; private set; } = null!;
        public PreparedAlgorithm Prepared { get; private set; } = null!;
        public FrameBufferPool Pool { get; }
        public FrameBufferLease? LastFrame { get; private set; }
        public VisionFrame? LastVisionFrame { get; private set; }
        public AlgorithmDescriptor Descriptor { get; }

        public static async Task<ProbeFixture> CreateAsync(IVisionAlgorithm algorithm)
        {
            var fixture = new ProbeFixture(algorithm);
            fixture.Preparation = new AlgorithmPreparationService(new[] { fixture },
                new AlgorithmPreparationOptions(TimeSpan.FromSeconds(2)));
            var config = AlgorithmConfigurationSnapshot.Create(fixture.Descriptor.ConfigurationSchema,
                Array.Empty<AlgorithmConfigurationEntry>());
            var result = fixture.Descriptor.ResultSchema;
            var overlay = result.OverlayContract;
            var prepared = await fixture.Preparation.PrepareAsync(new(
                fixture.Descriptor.Identity, config, result.Id, result.Version, result.ContentHash,
                overlay.Id, overlay.Version, overlay.ContentHash, TimeSpan.FromSeconds(1)))
                .ConfigureAwait(false);
            if (!prepared.Succeeded || prepared.Prepared is null)
                throw new InvalidOperationException("PreparationFailed");
            fixture.Prepared = prepared.Prepared;
            return fixture;
        }

        public FrameBufferLease Frame()
        {
            var frame = CopyFrame(Pool);
            LastFrame = frame;
            LastVisionFrame = frame.Frame;
            return frame;
        }

        public FrameBufferLease Frame(FrameBufferPool pool) => CopyFrame(pool);

        private static FrameBufferLease CopyFrame(FrameBufferPool pool)
        {
            var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
            var camera = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
                1000, 0, new(0, 0, 1, 1), VisionPixelFormat.Mono8, null, 1000, 0, null);
            var metadata = new FrameMetadata(correlation, "Probe.Camera", 1, 1, 1,
                VisionPixelFormat.Mono8, null, DateTimeOffset.UtcNow, camera);
            var provenance = new FrameProvenance(correlation, "Probe.Provider", "v1", "Probe.Adapter", "v1",
                "Probe.Sdk", "v1", null, "Probe.Device", null, null, "Mono8", "Probe",
                false, false, null, null, new(1, null, null, null, null));
            var copied = pool.TryCopyFrame(metadata, provenance, new byte[] { 42 });
            if (!copied.Succeeded || copied.Lease is null)
                throw new InvalidOperationException("FrameUnavailable");
            return copied.Lease;
        }

        public async Task<AlgorithmPreparationResult> TryPrepareAgainAsync()
        {
            var config = AlgorithmConfigurationSnapshot.Create(Descriptor.ConfigurationSchema,
                Array.Empty<AlgorithmConfigurationEntry>());
            var result = Descriptor.ResultSchema;
            var overlay = result.OverlayContract;
            return await Preparation.PrepareAsync(new(Descriptor.Identity, config, result.Id, result.Version,
                result.ContentHash, overlay.Id, overlay.Version, overlay.ContentHash, TimeSpan.FromSeconds(1)))
                .ConfigureAwait(false);
        }

        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(
                Array.Empty<AlgorithmValidationIssue>());

        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(_algorithm);

        public async ValueTask DisposeAsync()
        {
            await Preparation.DisposeAsync().ConfigureAwait(false);
            Pool.Dispose();
        }
    }

    private sealed class InFlightPreparationProbe : IAsyncDisposable, IVisionAlgorithmFactory
    {
        private readonly TaskCompletionSource<bool> _warmUpEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseWarmUp =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCount;

        private InFlightPreparationProbe()
        {
            Descriptor = new AlgorithmDescriptor(new("Probe.InFlight", "v1"),
                new("Probe.InFlight.Config", "v1", Array.Empty<AlgorithmFieldDefinition>()),
                new("Probe.InFlight.Result", "v1", Array.Empty<AlgorithmFieldDefinition>(),
                    Array.Empty<string>(), new("Probe.InFlight.Overlay", "v1")));
            Preparation = new AlgorithmPreparationService(new[] { this },
                new AlgorithmPreparationOptions(TimeSpan.FromSeconds(2)));
        }

        public AlgorithmPreparationService Preparation { get; }
        public AlgorithmPreparationResult? Result { get; private set; }
        public AlgorithmDescriptor Descriptor { get; }
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public static async Task<InFlightPreparationProbe> StartAsync()
        {
            var probe = new InFlightPreparationProbe();
            var config = AlgorithmConfigurationSnapshot.Create(probe.Descriptor.ConfigurationSchema,
                Array.Empty<AlgorithmConfigurationEntry>());
            var result = probe.Descriptor.ResultSchema;
            var overlay = result.OverlayContract;
            var task = probe.Preparation.PrepareAsync(new(probe.Descriptor.Identity, config,
                result.Id, result.Version, result.ContentHash, overlay.Id, overlay.Version,
                overlay.ContentHash, TimeSpan.FromSeconds(1))).AsTask();
            await probe._warmUpEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            probe.ResultTask = task;
            return probe;
        }

        private Task<AlgorithmPreparationResult> ResultTask { get; set; } = null!;

        public async Task<AlgorithmPreparationResult> ReleaseAndWaitAsync()
        {
            _releaseWarmUp.TrySetResult(true);
            Result = await ResultTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
            while (DisposeCount == 0)
            {
                if (Stopwatch.GetTimestamp() >= deadline)
                    throw new TimeoutException();
                await Task.Delay(10).ConfigureAwait(false);
            }
            return Result;
        }

        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(
                Array.Empty<AlgorithmValidationIssue>());

        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IVisionAlgorithm>(new InFlightAlgorithm(this));

        public async ValueTask DisposeAsync() => await Preparation.DisposeAsync().ConfigureAwait(false);

        private sealed class InFlightAlgorithm : IVisionAlgorithm
        {
            private readonly InFlightPreparationProbe _owner;
            public InFlightAlgorithm(InFlightPreparationProbe owner) => _owner = owner;

            public async ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
            {
                _owner._warmUpEntered.TrySetResult(true);
                await _owner._releaseWarmUp.Task.ConfigureAwait(false);
            }

            public ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
                CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(new AlgorithmResult(InspectionDecision.Pass, null,
                    Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet("Probe.InFlight.Overlay", "v1")));

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref _owner._disposeCount);
                return ValueTask.CompletedTask;
            }
        }
    }
}
