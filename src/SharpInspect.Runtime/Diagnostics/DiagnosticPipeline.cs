using System.Diagnostics;
using System.Runtime.CompilerServices;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Diagnostics;

/// <summary>Optional host forwarding receives only the already classified safe projection.</summary>
public interface ISafeDiagnosticSink
{
    ValueTask WriteAsync(DiagnosticRecord record, CancellationToken cancellationToken);
}

internal sealed class DiagnosticDropCounter { internal long Value; }

/// <summary>A scoped capability with no reference to an attempt, frame, recipe or algorithm instance.</summary>
internal sealed class ExecutionDiagnosticScope : IAlgorithmDiagnosticSink
{
    private readonly DiagnosticPipeline? _pipeline;
    private readonly DiagnosticDropCounter _counter;
    internal ExecutionDiagnosticScope(DiagnosticPipeline? pipeline, ExecutionCorrelationId correlation, DiagnosticDropCounter counter)
    { _pipeline = pipeline; Correlation = correlation; _counter = counter; }
    internal ExecutionCorrelationId Correlation { get; }
    internal int Closed;
    internal long Events, Bytes;
    internal void Seal()
    {
        if (_pipeline is null) Interlocked.Exchange(ref Closed, 1);
        else _pipeline.SealScope(this);
    }
    public AlgorithmDiagnosticEmission TryEmit(AlgorithmDiagnosticEvent diagnosticEvent)
    {
        if (_pipeline is not null && _pipeline.TryEmitAlgorithm(this, diagnosticEvent) == DiagnosticEmission.Accepted)
            return AlgorithmDiagnosticEmission.Accepted;
        // Preserve the execution-service counter's existing active-scope semantics.
        // The pipeline independently counts late calls after this scope is sealed.
        if (Volatile.Read(ref Closed) == 0) Interlocked.Increment(ref _counter.Value);
        return AlgorithmDiagnosticEmission.Dropped;
    }
}

/// <summary>Runtime-only producer authority and bounded fan-out. Ordinary logging never commits Core or audit facts.</summary>
internal sealed class DiagnosticPipeline : IDiagnosticPipelineHealthQuery, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly LoggingDiagnosticsPolicy _policy;
    private readonly DiagnosticClassifier _classifier;
    private readonly Guid _epoch;
    private readonly DiagnosticOutputLane _safe, _protected;
    private readonly DiagnosticOutputLane? _forwarded;
    private readonly ConditionalWeakTable<Exception, object> _observedExceptions = new();
    private long _submitted, _accepted, _rejected, _quotaDropped, _saturationDropped, _lateDropped;
    private long _windowStart = Stopwatch.GetTimestamp(), _windowEvents, _runtimeEvents, _runtimeBytes;
    private long _healthStart = Stopwatch.GetTimestamp(), _priorDrops;
    private bool _dropUnhealthy;
    private int _activeScopes, _stopping;

    internal DiagnosticPipeline(LoggingDiagnosticsPolicy policy, Guid epoch,
        Func<DiagnosticEnvelope, ValueTask> safe, Func<DiagnosticEnvelope, ValueTask> protectedOutput,
        ISafeDiagnosticSink? forwarded = null, Func<ValueTask>? closeSafe = null, Func<ValueTask>? closeProtected = null)
    {
        if (epoch == Guid.Empty) throw new ArgumentException("DiagnosticRuntimeEpochRequired", nameof(epoch));
        _policy = policy; _classifier = new(policy); _epoch = epoch;
        _safe = new(policy.SafeQueue, safe, closeSafe); _protected = new(policy.ProtectedQueue, protectedOutput, closeProtected);
        if (forwarded is not null) _forwarded = new(policy.ForwardedQueue, item => forwarded.WriteAsync(item.Record, CancellationToken.None));
    }

    internal ExecutionDiagnosticScope OpenScope(ExecutionCorrelationId correlation, DiagnosticDropCounter counter)
    {
        if (!Monitor.TryEnter(_sync)) { Interlocked.Increment(ref _saturationDropped); return new(null, correlation, counter); }
        try
        {
            if (_stopping != 0 || _activeScopes >= _policy.Producers.MaximumActiveExecutionScopes)
            { Interlocked.Increment(ref _quotaDropped); return new(null, correlation, counter); }
            _activeScopes++; return new(this, correlation, counter);
        }
        finally { Monitor.Exit(_sync); }
    }
    internal void SealScope(ExecutionDiagnosticScope scope)
    {
        // Shares the bounded, callback-free admission lock with TryEmit. When Seal returns,
        // no producer that observed the previous scope state can still enqueue a record.
        lock (_sync) if (Interlocked.Exchange(ref scope.Closed, 1) == 0) _activeScopes--;
    }

    internal DiagnosticEmission TryEmitAlgorithm(ExecutionDiagnosticScope scope, AlgorithmDiagnosticEvent? value)
    {
        // No raw object, exception or input-controlled correlation crosses this adapter.
        if (value is null) { Interlocked.Increment(ref _rejected); return DiagnosticEmission.Dropped; }
        if (Volatile.Read(ref scope.Closed) != 0) { Interlocked.Increment(ref _lateDropped); return DiagnosticEmission.Dropped; }
        var properties = new DiagnosticPropertyRequest[value.Fields.Count];
        for (var index = 0; index < properties.Length; index++)
        {
            var field = value.Fields[index];
            object? scalar = field.Value.Type switch
            {
                AlgorithmScalarType.Boolean => field.Value.AsBoolean(),
                AlgorithmScalarType.Int64 => field.Value.AsInt64(),
                AlgorithmScalarType.Float64 => field.Value.AsFloat64(),
                AlgorithmScalarType.String => field.Value.AsString(),
                AlgorithmScalarType.Enum => field.Value.AsEnum(),
                _ => null
            };
            properties[index] = new(field.Key, scalar);
        }
        return TryEmit(new(value.Code, 1, properties), scope, null);
    }

    internal DiagnosticEmission TryEmit(DiagnosticEmissionRequest request, ExecutionDiagnosticScope? scope = null,
        Guid? commandCorrelation = null, ExecutionCorrelationId? trustedExecution = null)
    {
        Interlocked.Increment(ref _submitted);
        if (!Monitor.TryEnter(_sync)) { Interlocked.Increment(ref _saturationDropped); return DiagnosticEmission.Dropped; }
        try
        {
            if (_stopping != 0 || scope is not null && Volatile.Read(ref scope.Closed) != 0)
            { Interlocked.Increment(ref _lateDropped); return DiagnosticEmission.Dropped; }
            var now = Stopwatch.GetTimestamp();
            if ((now - _windowStart) / (double)Stopwatch.Frequency >= _policy.Producers.RateWindow.TotalSeconds)
            { _windowStart = now; _windowEvents = _runtimeEvents = _runtimeBytes = 0; }
            if (++_windowEvents > _policy.Producers.MaximumEventsPerWindow ||
                (scope is null ? ++_runtimeEvents : ++scope.Events) > _policy.Producers.MaximumEvents)
            { Interlocked.Increment(ref _quotaDropped); return DiagnosticEmission.Dropped; }
            if (!_classifier.TryClassify(request, scope is not null, out var contract, out var safe, out var protectedValues))
            { Interlocked.Increment(ref _rejected); return DiagnosticEmission.Dropped; }
            var eventId = Guid.NewGuid(); var observed = DateTimeOffset.UtcNow;
            DiagnosticRecord Record(DiagnosticProperty[] values) => new(eventId, contract!.Code, contract.SchemaVersion,
                contract.Level, contract.Component, _epoch, observed, scope?.Correlation ?? trustedExecution, commandCorrelation,
                _policy.ContentHash, Array.AsReadOnly(values));
            var safeEnvelope = DiagnosticJson.Encode(Record(safe), _policy.SafeFiles.MaximumRecordBytes);
            var protectedEnvelope = protectedValues.Length == 0 ? null :
                DiagnosticJson.Encode(Record(protectedValues), _policy.ProtectedFiles.MaximumRecordBytes);
            if (safeEnvelope is null || protectedValues.Length != 0 && protectedEnvelope is null)
            { Interlocked.Increment(ref _quotaDropped); return DiagnosticEmission.Dropped; }
            var bytes = safeEnvelope.Line.Length + (protectedEnvelope?.Line.Length ?? 0L);
            var consumed = scope?.Bytes ?? _runtimeBytes;
            if (bytes > _policy.Producers.MaximumBytes - consumed)
            { Interlocked.Increment(ref _quotaDropped); return DiagnosticEmission.Dropped; }
            if (scope is null) _runtimeBytes += bytes; else scope.Bytes += bytes;
            var accepted = _safe.TryWrite(safeEnvelope);
            if (protectedEnvelope is not null) accepted &= _protected.TryWrite(protectedEnvelope);
            if (_forwarded is not null) _ = _forwarded.TryWrite(safeEnvelope);
            if (accepted) { Interlocked.Increment(ref _accepted); return DiagnosticEmission.Accepted; }
            Interlocked.Increment(ref _saturationDropped); return DiagnosticEmission.Dropped;
        }
        finally { Monitor.Exit(_sync); }
    }

    internal void ObserveException(Exception exception, string owner, ExecutionCorrelationId? execution = null)
    {
        // One owner-boundary observation per exception instance. The weak key never retains
        // an exception graph. Neither Message, Data, StackTrace nor virtual formatting is read.
        lock (_observedExceptions)
        {
            if (_observedExceptions.TryGetValue(exception, out _)) return;
            _observedExceptions.Add(exception, new object());
        }
        var type = exception.GetType();
        var category = type == typeof(OperationCanceledException) ? "Cancellation" :
            type == typeof(AlgorithmExecutionException) ? "Contract" : type == typeof(IOException) ? "IO" :
            type == typeof(TimeoutException) ? "Timeout" : "Unknown";
        // Runtime owns event correlation. Faults are emitted through the Runtime producer,
        // including after the algorithm scope has been sealed; no request may supply IDs.
        TryEmit(new("Runtime.OwnerFault", 1, new("Owner", owner), new("Category", category),
            new("Fingerprint", DiagnosticStandardContracts.Fingerprint(owner, category)), new("HResult", exception.HResult)),
            trustedExecution: execution);
    }

    internal void ObserveAlgorithmOutcome(ExecutionCorrelationId execution, ExecutionStatus status, InspectionDecision decision) =>
        TryEmit(new("Runtime.AlgorithmOutcome", 1, new("Status", status.ToString()), new("Decision", decision.ToString())),
            trustedExecution: execution);

    public DiagnosticPipelineHealthSnapshot ReadHealth()
    {
        var safe = _safe.ReadHealth(); var protectedHealth = _protected.ReadHealth(); var forwarded = _forwarded?.ReadHealth();
        var drops = Interlocked.Read(ref _rejected) + Interlocked.Read(ref _quotaDropped) +
            Interlocked.Read(ref _saturationDropped) + Interlocked.Read(ref _lateDropped);
        long summary = 0;
        lock (_sync)
        {
            if ((Stopwatch.GetTimestamp() - _healthStart) / (double)Stopwatch.Frequency >= _policy.HealthWindow.TotalSeconds)
            {
                summary = Math.Max(0, drops - _priorDrops); _priorDrops = drops; _healthStart = Stopwatch.GetTimestamp();
                _dropUnhealthy = summary >= _policy.MaximumDropsPerHealthWindow;
            }
        }
        if (summary > 0 && _stopping == 0)
            TryEmit(new("Runtime.DiagnosticDrops", 1, new DiagnosticPropertyRequest("Count", Math.Min(int.MaxValue, summary))));
        var unhealthy = _dropUnhealthy || safe.State == DiagnosticSinkState.Unavailable ||
            protectedHealth.State == DiagnosticSinkState.Unavailable || forwarded?.State == DiagnosticSinkState.Unavailable;
        return new(true, _epoch, _policy.ContentHash, DateTimeOffset.UtcNow, Interlocked.Read(ref _submitted),
            Interlocked.Read(ref _accepted), Interlocked.Read(ref _rejected), Interlocked.Read(ref _quotaDropped),
            Interlocked.Read(ref _saturationDropped), Interlocked.Read(ref _lateDropped), Volatile.Read(ref _activeScopes),
            safe, protectedHealth, forwarded, unhealthy, unhealthy ? "DiagnosticPipelineUnhealthy" : "DiagnosticPipelineHealthy");
    }

    internal async ValueTask<bool> FlushAsync(CancellationToken token = default)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var health = ReadHealth();
            if (health.Safe!.QueuedRecords == 0 && health.Protected!.QueuedRecords == 0 && (health.Forwarded?.QueuedRecords ?? 0) == 0)
                return !health.Unhealthy;
            if (token.IsCancellationRequested || (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency >= _policy.FlushTimeout.TotalSeconds)
                return false;
            try { await Task.Delay(10, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        var retired = Task.WhenAll(_safe.Stop(), _protected.Stop(), _forwarded?.Stop() ?? Task.CompletedTask);
        await Task.WhenAny(retired, Task.Delay(_policy.FlushTimeout)).ConfigureAwait(false);
        // Timed-out physical callbacks retain their lane, payload and local writer ownership.
        _ = ReadHealth();
    }
}
