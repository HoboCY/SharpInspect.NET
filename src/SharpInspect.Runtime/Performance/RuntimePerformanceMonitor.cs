using System.Diagnostics;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Performance;

internal sealed partial class RuntimePerformanceMonitor : IPerformanceMonitoringQuery, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly PerformanceMonitoringOptions _options;
    private readonly Guid _epoch;
    private readonly long[] _cycleTimes = new long[Enum.GetValues<PerformanceEventKind>().Length];
    private readonly PerformanceSpanBudget[] _budgets;
    private readonly PerformanceCaptureBuffer? _capture;
    private readonly CancellationTokenSource _stop = new();
    private ExecutionCorrelationId? _current;
    private PlcControllerCycle? _controller;
    private long _previousReady, _cycleReady, _violations, _lost;
    private long _cycleAllocatedAt, _lastCycleAllocation = -1;
    private int _violation, _healthyCycles, _physicalOperations;
    private bool _cycleViolated;
    private string _reason = "PerformanceObservationPending";
    private Task? _resourceWorker;
    private PerformanceRawCapture? _sealedCapture;
    private int _terminalPublication;
    private Func<long, PerformanceEvent[], PerformanceResourceSample[], PerformanceFrameObservation[],
        PerformanceCycleBinding[], Task<PerformanceRawCapture>>? _seal;

    internal RuntimePerformanceMonitor(PerformanceMonitoringOptions options, Guid epoch, PerformanceRunHeader? header)
    {
        _options = options; _epoch = epoch;
        _budgets = options.Contract.Spans.ToArray();
        if (header is not null) _capture = new(options.Contract, header);
    }
    internal PerformanceMonitoringOptions Options => _options;
    internal bool BudgetViolated => Volatile.Read(ref _violation) != 0;
    internal long ObservationLossCount => Interlocked.Read(ref _lost) + (_capture?.Drops ?? 0);
    internal long FirstMissingSequence => _capture?.FirstMissingSequence ?? 0;
    internal bool CaptureRetirementIncomplete => _capture?.RetirementIncomplete == true;
    internal long? LastCycleAllocation => Interlocked.Read(ref _lastCycleAllocation) is var value && value >= 0 ? value : null;
    internal void BindCycle(ExecutionCorrelationId correlation, string? targetFingerprint) =>
        _capture?.Binding(new(correlation, targetFingerprint));

    internal void Observe(PerformanceEventKind kind, ExecutionCorrelationId? correlation = null,
        PlcControllerCycle? controller = null, PerformanceObservationOutcome outcome = PerformanceObservationOutcome.Observed,
        string? reason = null, long? observedAt = null)
    {
        var timestamp = observedAt ?? Stopwatch.GetTimestamp();
        // Producers are closed framework call sites; still reject malformed technical context before retention.
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(outcome) || timestamp < 0 ||
            correlation is not null && (correlation.Value == Guid.Empty || !Enum.IsDefined(correlation.Kind)) ||
            reason is not null && (reason.Length is < 1 or > 128 || reason.Any(character => !char.IsAscii(character) || char.IsControl(character))))
        { Lost(); return; }
        _capture?.Event(kind, timestamp, correlation, controller, outcome, reason);
        if (!Monitor.TryEnter(_sync)) { Lost(); return; }
        try
        {
            if (kind == PerformanceEventKind.TriggerAccepted && correlation is not null)
            {
                if (_current is not null && _cycleTimes[(int)PerformanceEventKind.CycleCompleted] == 0 &&
                    _cycleTimes[(int)PerformanceEventKind.CycleFaulted] == 0) Lost();
                _current = correlation; _controller = controller; _cycleReady = _previousReady;
                _cycleAllocatedAt = GC.GetTotalAllocatedBytes(precise: false);
                Array.Clear(_cycleTimes); _cycleViolated = false;
            }
            if (kind == PerformanceEventKind.ReadyAsserted)
            {
                if (_current is not null)
                {
                    var allocated = GC.GetTotalAllocatedBytes(precise: false) - _cycleAllocatedAt;
                    Interlocked.Exchange(ref _lastCycleAllocation, allocated >= 0 ? allocated : -1);
                    CheckBudget(PerformanceSpan.AcknowledgementResetToReady,
                        _cycleTimes[(int)PerformanceEventKind.AcknowledgementReset], timestamp);
                    CheckBudget(PerformanceSpan.ReadyToReady, _cycleReady, timestamp);
                    if (_cycleTimes[(int)PerformanceEventKind.CycleCompleted] != 0 && !_cycleViolated && !BudgetViolated)
                        _reason = "PerformanceWithinBudget";
                    if (_cycleTimes[(int)PerformanceEventKind.CycleCompleted] != 0 && !_cycleViolated)
                    {
                        if (++_healthyCycles >= _options.HealthyCyclesToClear)
                        { Volatile.Write(ref _violation, 0); _reason = "PerformanceWithinBudget"; }
                    }
                    else _healthyCycles = 0;
                    _current = null; _controller = null;
                }
                _previousReady = timestamp;
                return;
            }
            if (_current is null || correlation != _current) return;
            if (controller is not null && controller != _controller) { Lost(); return; }
            _cycleTimes[(int)kind] = timestamp;
            if (outcome != PerformanceObservationOutcome.Observed) return;
            foreach (var definition in PerformanceSpanDefinitions.All)
                if (definition.End == kind)
                    CheckBudget(definition.Span, _cycleTimes[(int)definition.Start], timestamp);
        }
        finally { Monitor.Exit(_sync); }
    }

    private void CheckBudget(PerformanceSpan span, long start, long end)
    {
        if (start <= 0 || end < start) return;
        var budget = _budgets[(int)span];
        if (budget.Applicable && (end - start) * 1000d / Stopwatch.Frequency > budget.ObservedMaxMilliseconds)
            Violation("PerformanceBudget." + span);
    }
    private void Violation(string reason)
    {
        Interlocked.Increment(ref _violations); Volatile.Write(ref _violation, 1);
        _cycleViolated = true; _healthyCycles = 0; _reason = "PerformanceBudgetViolation";
        _capture?.Event(PerformanceEventKind.BudgetViolation, Stopwatch.GetTimestamp(), _current, _controller,
            PerformanceObservationOutcome.Failed, reason);
    }
    private void Lost()
    { Interlocked.Increment(ref _lost); _reason = "PerformanceObservationIncomplete"; }

    public PerformanceMonitorSnapshot ReadPerformance() => new(true, _options.Contract.ContentHash,
        BudgetViolated, Interlocked.Read(ref _violations), ObservationLossCount, Volatile.Read(ref _healthyCycles),
        _capture?.Header.RunId, _capture is null ? null : _capture.IsSealed
            ? _sealedCapture?.State ?? PerformanceEvidenceState.Incomplete : PerformanceEvidenceState.Capturing,
        _capture?.EventCount ?? 0, _capture?.SampleCount ?? 0, Volatile.Read(ref _physicalOperations), _reason);

    public ValueTask<PerformanceRawCapture?> ReadCaptureAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Volatile.Read(ref _sealedCapture)); }

    internal void StartResources(Func<long, PerformanceResourceSample> sample,
        Func<long, PerformanceEvent[], PerformanceResourceSample[], PerformanceFrameObservation[],
            PerformanceCycleBinding[], Task<PerformanceRawCapture>>? seal)
    {
        if (_resourceWorker is not null) throw new InvalidOperationException("PerformanceResourceWorkerAlreadyStarted");
        _seal = seal;
        _resourceWorker = Task.Run(() => RunResourcesAsync(sample));
    }

    private async Task RunResourcesAsync(Func<long, PerformanceResourceSample> sample)
    {
        long sequence = 0;
        Task<PerformanceResourceSample>? physical = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                // Exactly one physical sampling slot. Caller timeout never releases it or launches a replacement.
                Interlocked.Exchange(ref _physicalOperations, 1);
                physical = Task.Run(() => sample(++sequence));
                PerformanceResourceSample value;
                try { value = await physical.WaitAsync(_options.Contract.Capture.ResourceSampleTimeout).ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { Lost(); break; }
                Interlocked.Exchange(ref _physicalOperations, 0);
                _capture?.Sample(value);
                if (Monitor.TryEnter(_sync))
                {
                    try
                    {
                        foreach (var observation in value.Values)
                        {
                            var budget = _options.Contract.Resources[(int)observation.Resource];
                            if (budget.Required && observation.Value is { } number && number > budget.Maximum)
                                Violation("PerformanceBudget." + observation.Resource);
                        }
                    }
                    finally { Monitor.Exit(_sync); }
                }
                else Lost();
                if (_capture is { IsSealed: false } capture &&
                    (Stopwatch.GetTimestamp() - capture.Header.StartedAt) / (double)Stopwatch.Frequency >=
                    _options.Contract.Scenarios.Single(item => item.Id == capture.Header.ScenarioId).MaximumDuration.TotalSeconds)
                    await SealAsync().ConfigureAwait(false);
                await Task.Delay(_options.Contract.Capture.ResourceSampleInterval, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            // A hung native/property read remains physically owned. Evidence records that unknown retirement.
            if (physical is { IsCompleted: false })
                _ = physical.ContinueWith(completed =>
                { _ = completed.Exception; Interlocked.Exchange(ref _physicalOperations, 0); }, TaskScheduler.Default);
            else Interlocked.Exchange(ref _physicalOperations, 0);
            await SealAsync().ConfigureAwait(false);
        }
    }

    private async Task SealAsync()
    {
        if (_capture is not { IsSealed: false } capture || _seal is null) return;
        var endedAt = Stopwatch.GetTimestamp();
        var raw = capture.Seal();
        var result = await _seal(endedAt, raw.Events, raw.Samples, raw.Frames, raw.Bindings).ConfigureAwait(false);
        if (Interlocked.CompareExchange(ref _terminalPublication, 1, 0) == 0)
            Volatile.Write(ref _sealedCapture, result);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_resourceWorker is not null)
        {
            try { await _resourceWorker.WaitAsync(_options.Contract.Capture.ShutdownTimeout).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                Lost();
                // Close the evidence window within shutdown's budget even if the physical sampler
                // or ledger seal has not retired. A late successful seal cannot replace this failure.
                if (_capture is { } capture && Interlocked.CompareExchange(ref _terminalPublication, 2, 0) == 0)
                {
                    var endedAt = Stopwatch.GetTimestamp();
                    var raw = capture.Seal();
                    var incomplete = new PerformanceRawCapture(_options.Contract, capture.Header, PerformanceEvidenceState.Incomplete,
                        endedAt, ObservationLossCount, FirstMissingSequence, "PerformanceShutdownTimeout", raw.Events, raw.Samples,
                        Array.Empty<PerformanceDurableFact>(), false, "PerformanceLedgerNotReadBeforeShutdownTimeout", raw.Frames, raw.Bindings);
                    Volatile.Write(ref _sealedCapture, incomplete);
                }
            }
        }
    }
}
