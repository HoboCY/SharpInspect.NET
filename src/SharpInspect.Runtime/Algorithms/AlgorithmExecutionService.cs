using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Frames;

namespace SharpInspect.Runtime.Algorithms;

/// <summary>Explicit deployment timing policy and bounded host shutdown wait.</summary>
public sealed class AlgorithmExecutionOptions
{
    public AlgorithmExecutionOptions(AlgorithmExecutionPolicy policy, TimeSpan shutdownWaitTimeout)
    {
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (shutdownWaitTimeout <= TimeSpan.Zero || shutdownWaitTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(shutdownWaitTimeout));
        ShutdownWaitTimeout = shutdownWaitTimeout;
    }
    public AlgorithmExecutionPolicy Policy { get; }
    public TimeSpan MaximumExecutionTimeout => Policy.MaximumExecutionTimeout;
    public TimeSpan ShutdownWaitTimeout { get; }
}

/// <summary>Runtime-owned computation outcome. It is not a committed Inspection Record or publication permit.</summary>
public sealed class AlgorithmExecutionOutcome
{
    internal AlgorithmExecutionOutcome(PreparedAlgorithm prepared, FrameMetadata frame,
        ExecutionStatus status, string? reasonCode, AlgorithmResult? validatedResult,
        AlgorithmExecutionTimingSnapshot timing, long admittedMonotonicTimestamp)
    {
        Correlation = frame.Correlation; FrameMetadata = frame; PreparedInstanceId = prepared.InstanceId;
        Algorithm = prepared.Descriptor.Identity; ResultSchema = prepared.Descriptor.ResultSchema;
        ConfigurationContentHash = prepared.Configuration.ContentHash;
        ConfigurationSchemaId = prepared.Descriptor.ConfigurationSchema.Id;
        ConfigurationSchemaVersion = prepared.Descriptor.ConfigurationSchema.Version;
        ConfigurationSchemaContentHash = prepared.Descriptor.ConfigurationSchema.ContentHash;
        ExecutionStatus = status; ReasonCode = reasonCode;
        ValidatedResult = status == ExecutionStatus.Success ? validatedResult : null;
        Decision = ValidatedResult?.Decision ?? InspectionDecision.Unknown;
        Timing = timing; AdmittedMonotonicTimestamp = admittedMonotonicTimestamp;
    }
    public ExecutionCorrelationId Correlation { get; }
    public FrameMetadata FrameMetadata { get; }
    public Guid PreparedInstanceId { get; }
    public AlgorithmIdentity Algorithm { get; }
    public AlgorithmResultSchema ResultSchema { get; }
    public string ConfigurationContentHash { get; }
    public string ConfigurationSchemaId { get; }
    public string ConfigurationSchemaVersion { get; }
    public string ConfigurationSchemaContentHash { get; }
    public ExecutionStatus ExecutionStatus { get; }
    public InspectionDecision Decision { get; }
    public string? ReasonCode { get; }
    public AlgorithmResult? ValidatedResult { get; }
    public AlgorithmExecutionTimingSnapshot Timing { get; }
    public long AdmittedMonotonicTimestamp { get; }
    public long MonotonicFrequency => Stopwatch.Frequency;
}

/// <summary>A refused call has no per-frame outcome. The supplied owner token is consumed either way.</summary>
public sealed record AlgorithmExecutionAttempt(bool Executed, string ReasonCode, AlgorithmExecutionOutcome? Outcome);

/// <summary>
/// One bounded, non-queuing computation engine. Production remains closed until the
/// station's Trigger, timing-policy, recovery, trace and publication gates are wired.
/// No algorithm, diagnostic consumer or cancellation callback runs on the admission caller.
/// </summary>
public sealed class AlgorithmExecutionService : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly AlgorithmExecutionOptions _options;
    private readonly AlgorithmExecutionGuard _guard = AlgorithmExecutionGuard.CurrentProcess;
    private readonly Action? _beforeResultValidationForTesting;
    private readonly Action? _beforeExecutionStartForTesting;
    private readonly bool _suppressGraceWatchdogForTesting;
    private Attempt? _running;
    private ExecutionCorrelationId? _cancelledProduction;
    private string? _blockedReason;
    private bool _disposed;
    private long _droppedDiagnostics;

    public AlgorithmExecutionService(AlgorithmExecutionOptions options) : this(options, null) { }

    // 仅供内部确定性调度探针使用；公共构造函数不能注入回调。
    internal AlgorithmExecutionService(AlgorithmExecutionOptions options, Action? beforeResultValidationForTesting,
        Action? beforeExecutionStartForTesting = null, bool suppressGraceWatchdogForTesting = false)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _beforeResultValidationForTesting = beforeResultValidationForTesting;
        _beforeExecutionStartForTesting = beforeExecutionStartForTesting;
        _suppressGraceWatchdogForTesting = suppressGraceWatchdogForTesting;
    }

    public int ActiveExecutionCount { get { lock (_sync) return _running is null ? 0 : 1; } }
    public string? BlockedReasonCode { get { lock (_sync) return _guard.IsHung ? "AlgorithmHung" : _blockedReason; } }
    public long DroppedDiagnosticCount => Interlocked.Read(ref _droppedDiagnostics);

    /// <summary>Consumes a frame owner for computation; the token is the Runtime's abort signal, not a UI wait token.</summary>
    public ValueTask<AlgorithmExecutionAttempt> ExecuteAsync(PreparedAlgorithm prepared,
        FrameBufferLease frameLease, AlgorithmExecutionRequest request, CancellationToken runtimeCancellationToken = default) =>
        ExecuteCoreAsync(prepared, frameLease, request, null, runtimeCancellationToken);

    // 只有内部共享周期协调器能提供已接受的生产身份；公共计算入口继续拒绝生产帧。
    internal ValueTask<AlgorithmExecutionAttempt> ExecuteProductionOwnedAsync(PreparedAlgorithm prepared,
        FrameBufferLease frameLease, AlgorithmExecutionRequest request, ExecutionCorrelationId correlation,
        CancellationToken runtimeCancellationToken) =>
        ExecuteCoreAsync(prepared, frameLease, request, correlation, runtimeCancellationToken);

    // Runtime 先固定语义结果，再调度消费者取消回调；相关性锁存也覆盖尚未开始调用的物理认领。
    internal void RequestProductionCancellation(ExecutionCorrelationId correlation)
    {
        if (correlation.Kind != ExecutionKind.Production || correlation.Value == Guid.Empty)
            throw new ArgumentException("ProductionExecutionCancellationIdentityRequired", nameof(correlation));
        lock (_sync)
        {
            _cancelledProduction = correlation;
            if (_running is { } pending && pending.Correlation == correlation)
                pending.FixCancellation();
        }
    }

    private async ValueTask<AlgorithmExecutionAttempt> ExecuteCoreAsync(PreparedAlgorithm prepared,
        FrameBufferLease frameLease, AlgorithmExecutionRequest request, ExecutionCorrelationId? productionOwner,
        CancellationToken runtimeCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared); ArgumentNullException.ThrowIfNull(frameLease);
        var owner = frameLease.Transfer();
        if (owner is null) return Rejected("FrameLeaseNotOwned");
        var frame = new FrameBufferLease(owner);
        Attempt? attempt = null;
        try
        {
            if (owner.Metadata.Correlation.Kind == ExecutionKind.Production && productionOwner is null)
                return Rejected("ProductionExecutionAdmissionUnavailable");
            if (productionOwner is not null && (productionOwner.Kind != ExecutionKind.Production ||
                    productionOwner.Value == Guid.Empty || owner.Metadata.Correlation != productionOwner))
                return Rejected("ProductionExecutionOwnerMismatch");
            if (_guard.IsHung) return Rejected("AlgorithmHung");
            if (!_options.Policy.TryBind(request, out var timing, out var timingReason))
                return Rejected(timingReason);
            if (runtimeCancellationToken.IsCancellationRequested) return Rejected("AlgorithmExecutionCancelledBeforeStart");
            lock (_sync)
            {
                if (_disposed) return Rejected("AlgorithmExecutionServiceDisposed");
                if (productionOwner is not null && productionOwner == _cancelledProduction)
                    return Rejected("AlgorithmExecutionCancelledBeforeStart");
                if (_blockedReason is not null) return Rejected(_blockedReason);
                if (_running is not null) return Rejected("AlgorithmExecutionBusy");
                if (!prepared.TryBeginExecution(out var algorithm))
                    return Rejected(prepared.IsRetired ? "AlgorithmInstanceRetired" : "AlgorithmInstanceBusy");
                try
                {
                    attempt = new Attempt(this, prepared, algorithm!, frame, timing!, runtimeCancellationToken);
                    _beforeExecutionStartForTesting?.Invoke();
                    var admitted = attempt;
                    if (!_guard.TryRunIfHealthy(() => { _running = admitted; admitted.Start(); }))
                    {
                        attempt.CloseUnstarted(); attempt = null; prepared.EndExecution();
                        return Rejected("AlgorithmHung");
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    attempt?.CloseUnstarted();
                    attempt = null; _running = null; prepared.EndExecution();
                    return Rejected("AlgorithmExecutionStartFailed");
                }
            }
            var remaining = attempt.Remaining;
            try
            {
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                await attempt.Completion.Task.WaitAsync(remaining, runtimeCancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) { attempt.FixNonSuccess(ExecutionStatus.Timeout, "AlgorithmExecutionTimeout"); }
            catch (OperationCanceledException) { attempt.FixCancellation(); }
            var outcome = await attempt.Completion.Task.ConfigureAwait(false);
            if (outcome.ExecutionStatus is ExecutionStatus.Success or ExecutionStatus.Error)
                await attempt.PhysicalCompletion.Task.ConfigureAwait(false);
            return new(true, outcome.ReasonCode ?? "AlgorithmExecutionCompleted", outcome);
        }
        finally { if (attempt is null) frame.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        Attempt? pending;
        lock (_sync) { _disposed = true; pending = _running; }
        if (pending is null) return;
        pending.FixCancellation();
        try { await pending.PhysicalCompletion.Task.WaitAsync(_options.ShutdownWaitTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { /* Actual work keeps its owner and instance reservation. */ }
    }

    private static AlgorithmExecutionAttempt Rejected(string reason) => new(false, reason, null);

    private sealed class Attempt : IAlgorithmDiagnosticSink
    {
        private readonly object _sync = new();
        private readonly AlgorithmExecutionService _service;
        private readonly PreparedAlgorithm _prepared;
        private readonly IVisionAlgorithm _algorithm;
        private readonly FrameBufferLease _frame;
        private readonly FrameMetadata _metadata;
        private readonly CancellationTokenSource _algorithmCancellation = new();
        private readonly CancellationToken _caller;
        private readonly TimeSpan _timeout;
        private readonly AlgorithmExecutionTimingSnapshot _timing;
        private readonly Guid _frameLeaseId;
        private readonly long _started = Stopwatch.GetTimestamp();
        private CancellationTokenRegistration _callerRegistration;
        private Task? _cancellationCallbacks;
        private Task? _retirement;
        private bool _closed;
        private bool _cancelRequested;
        private bool _diagnosticsSealed;
        private bool _invocationQuiesced;
        private long? _fixedAt;
        private AlgorithmExecutionGuard.GraceRegistration? _grace;
        private CancellationTokenSource? _graceWatchdogCancellation;

        public Attempt(AlgorithmExecutionService service, PreparedAlgorithm prepared, IVisionAlgorithm algorithm,
            FrameBufferLease frame, AlgorithmExecutionTimingSnapshot timing, CancellationToken caller)
        {
            _service = service; _prepared = prepared; _algorithm = algorithm; _frame = frame;
            _metadata = frame.Frame.Metadata; _timeout = timing.AlgorithmExecutionTimeout; _caller = caller;
            _timing = timing; _frameLeaseId = frame.LeaseId;
        }
        public TaskCompletionSource<AlgorithmExecutionOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ExecutionCorrelationId Correlation => _metadata.Correlation;
        public TaskCompletionSource<bool> PhysicalCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan Remaining => _timeout - TimeSpan.FromSeconds(
            (Stopwatch.GetTimestamp() - _started) / (double)Stopwatch.Frequency);

        public void Start()
        {
            _ = Task.Run(RunObservedAsync);
        }

        public void CloseUnstarted()
        {
            lock (_sync) { _closed = true; _diagnosticsSealed = true; }
            _callerRegistration.Dispose();
            // 如果注册过程同时观察到终止请求，先等无消费者的取消任务结束，再释放 token source。
            if (_cancellationCallbacks is { } callbacks)
                _ = callbacks.ContinueWith(_ => _algorithmCancellation.Dispose(), TaskScheduler.Default);
            else _algorithmCancellation.Dispose();
        }

        public AlgorithmDiagnosticEmission TryEmit(AlgorithmDiagnosticEvent diagnosticEvent)
        {
            // 这一执行切片没有获准的日志策略；未知事件和载荷在格式化、序列化或分发前直接丢弃。
            lock (_sync)
            {
                if (_diagnosticsSealed) return AlgorithmDiagnosticEmission.Dropped;
                Interlocked.Increment(ref _service._droppedDiagnostics);
            }
            return AlgorithmDiagnosticEmission.Dropped;
        }

        public void FixCancellation()
        {
            lock (_sync)
            {
                _cancelRequested = true;
                var timedOut = Remaining <= TimeSpan.Zero;
                FixNonSuccess(timedOut ? ExecutionStatus.Timeout : ExecutionStatus.Cancelled,
                    timedOut ? "AlgorithmExecutionTimeout" : "AlgorithmExecutionCancelled");
            }
        }

        public void FixNonSuccess(ExecutionStatus status, string reason)
        {
            lock (_sync)
            {
                if (_closed || Completion.Task.IsCompleted) return;
                _diagnosticsSealed = true;
                _fixedAt = Stopwatch.GetTimestamp();
                _grace = _service._guard.RegisterGrace(_metadata.Correlation, _prepared.InstanceId,
                    _frameLeaseId, status, _timing, _fixedAt.Value);
                Completion.TrySetResult(NewOutcome(status, reason, null));
                _retirement ??= _prepared.DisposeAsync().AsTask();
                _cancellationCallbacks ??= Task.Run(() =>
                {
                    try { _algorithmCancellation.Cancel(); }
                    catch (Exception exception) when (exception is not OutOfMemoryException) { }
                });
                if (!_service._suppressGraceWatchdogForTesting)
                {
                    _graceWatchdogCancellation = new CancellationTokenSource();
                    _ = WatchGraceAsync(_graceWatchdogCancellation);
                }
            }
        }

        private AlgorithmExecutionOutcome NewOutcome(ExecutionStatus status, string? reason, AlgorithmResult? result) =>
            new(_prepared, _metadata, status, reason, result, _timing, _started);

        // 看门狗与物理退出路径共用这个单调时钟边界；延迟的看门狗不能把逾期退出改判为宽限期内恢复。
        private bool GraceExpiredLocked() => _fixedAt is { } fixedAt &&
            TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - fixedAt) / (double)Stopwatch.Frequency) >=
                _timing.CancellationGracePeriod;

        private async Task WatchGraceAsync(CancellationTokenSource cancellation)
        {
            var cancellationToken = cancellation.Token;
            try
            {
                while (true)
                {
                    TimeSpan remaining;
                    lock (_sync)
                    {
                        if (_invocationQuiesced || _fixedAt is null) return;
                        if (GraceExpiredLocked()) { _ = _service._guard.IsHung; return; }
                        remaining = _timing.CancellationGracePeriod - TimeSpan.FromSeconds(
                            (Stopwatch.GetTimestamp() - _fixedAt.Value) / (double)Stopwatch.Frequency);
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(remaining.TotalMilliseconds))),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_graceWatchdogCancellation, cancellation)) _graceWatchdogCancellation = null;
                    cancellation.Dispose();
                }
            }
        }

        private async Task RunAsync()
        {
            AlgorithmResult? result = null;
            string? failure = null;
            try
            {
                _callerRegistration = _caller.Register(static state => ((Attempt)state!).FixCancellation(), this);
                if (!Completion.Task.IsCompleted)
                {
                    try
                    {
                        // 在准备与准入共用的锁存下认领调用；消费者代码始终在该锁外执行。
                        var withinDeadline = false;
                        if (!_service._guard.TryRunIfHealthy(() => withinDeadline = Remaining > TimeSpan.Zero))
                        {
                            FixNonSuccess(ExecutionStatus.Cancelled, "AlgorithmHung");
                            return;
                        }
                        if (!withinDeadline)
                        {
                            FixNonSuccess(ExecutionStatus.Timeout, "AlgorithmExecutionTimeout");
                            return;
                        }
                        result = await _algorithm.ExecuteAsync(new(_metadata.Correlation, _prepared.Configuration,
                            _frame.Frame, this), _algorithmCancellation.Token).ConfigureAwait(false);
                        _service._beforeResultValidationForTesting?.Invoke();
                        if (AlgorithmResultValidator.Validate(result, _prepared.Descriptor.ResultSchema).Count != 0)
                            failure = "AlgorithmResultContractViolation";
                    }
                    catch (AlgorithmExecutionException exception)
                    {
                        failure = _prepared.Descriptor.ResultSchema.ReasonCodes.Contains(exception.ReasonCode,
                            StringComparer.Ordinal) ? exception.ReasonCode : "AlgorithmExecutionError";
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        // 不读取或保留原始异常属性；受保护的细节只能由后续单独授权的诊断存储采集。
                        failure = "AlgorithmExecutionError";
                    }
                }
                lock (_sync)
                {
                    if (Remaining <= TimeSpan.Zero)
                        FixNonSuccess(ExecutionStatus.Timeout, "AlgorithmExecutionTimeout");
                    else if (_cancelRequested || _caller.IsCancellationRequested)
                        FixCancellation();
                    else if (!Completion.Task.IsCompleted)
                    {
                        _diagnosticsSealed = true;
                        Completion.TrySetResult(NewOutcome(
                            failure is null ? ExecutionStatus.Success : ExecutionStatus.Error,
                            failure ?? result?.ReasonCode, failure is null ? result : null));
                    }
                    _closed = true;
                }
            }
            finally
            {
                _callerRegistration.Dispose();
                Task? callbacks; Task? retirement;
                lock (_sync) { _closed = true; callbacks = _cancellationCallbacks; retirement = _retirement; }
                var retiredSafely = true;
                try
                {
                    if (callbacks is not null) await callbacks.ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (_grace is not null) _service._guard.CompleteGrace(_grace);
                        _invocationQuiesced = true;
                        // 此私有 token 只有 Task.Delay 一个消费者；立即结束监视，避免按部署的长宽限期保留已退役尝试。
                        _graceWatchdogCancellation?.Cancel();
                    }
                    // token 回调也可能是使用帧的消费者代码；调用和回调都退出前必须保留帧所有者与算法实例。
                    _frame.Dispose();
                    _prepared.EndExecution();
                    if (retirement is not null) await retirement.ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException) { retiredSafely = false; }
                finally
                {
                    _algorithmCancellation.Dispose();
                    lock (_service._sync)
                    {
                        if (!retiredSafely) _service._blockedReason = "AlgorithmRetirementFailed";
                        else if (ReferenceEquals(_service._running, this)) _service._running = null;
                    }
                    PhysicalCompletion.TrySetResult(true);
                }
            }
        }

        private async Task RunObservedAsync()
        {
            try { await RunAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // 基础设施故障不能留下未观察的 Task；此兜底只发布稳定代码，不格式化异常。
                lock (_sync)
                {
                    _closed = true; _diagnosticsSealed = true;
                    Completion.TrySetResult(NewOutcome(ExecutionStatus.Error,
                        "AlgorithmExecutionError", null));
                }
                PhysicalCompletion.TrySetResult(true);
            }
        }
    }
}
