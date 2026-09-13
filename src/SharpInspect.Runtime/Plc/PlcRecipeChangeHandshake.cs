using System.Diagnostics;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Plc;

internal sealed record RecipeChangeDecision(RecipeChangeOutcome Outcome, RecipeChangeReason Reason,
    string ReasonCode, RecipeActivationReference? Activation = null);

internal sealed record RecipeChangeTransition(RecipeChangeEventKind Kind, uint RequestSequence,
    uint SelectionCode, RecipeChangeDecision? Decision = null, string? ReasonCode = null);

/// <summary>
/// Created synchronously at first observation. A rejected operation contains a fixed decision;
/// an admissible operation already owns its non-waiting Runtime reservation. Execute must
/// persist the request/deduplication decision before any activation I/O.
/// </summary>
internal sealed class PlcRecipeChangeOperation
{
    private readonly Func<CancellationToken, Task<RecipeChangeDecision>> _execute;
    private readonly Action _cancel;
    private int _started;
    internal PlcRecipeChangeOperation(Func<CancellationToken, Task<RecipeChangeDecision>> execute, Action cancel)
    { _execute = execute; _cancel = cancel; }
    internal Task<RecipeChangeDecision> Start(CancellationToken token) => Interlocked.Exchange(ref _started, 1) == 0
        ? _execute(token) : throw new InvalidOperationException("RecipeChangeOperationAlreadyStarted");
    internal void Cancel() => _cancel();
}

/// <summary>
/// One dedicated four-phase handshake. Observe calls are serialized by the production
/// observer's sample gate; activation runs independently so an active inspection still
/// receives an immediate fixed rejection. No controller field is ever written here.
/// The owner creates a fresh instance for each synchronized controller epoch/connection.
/// Persistent deduplication uses endpoint + controller epoch + sequence before activation;
/// the bounded in-memory set is only an additional fence within this connection.
/// </summary>
internal sealed class PlcRecipeChangeHandshake : IDisposable
{
    private enum Phase { Initial, Idle, Executing, AwaitingAcknowledgement, AwaitingReset, Faulted }
    private readonly TimeSpan _timeout;
    private readonly Func<ModbusRecipeChangeControllerSignals, PlcRecipeChangeOperation> _begin;
    private readonly Func<RecipeChangeTransition, Task> _record;
    private readonly Func<bool, ushort, ushort, uint, uint, CancellationToken, Task> _write;
    private readonly Func<long> _clock;
    private readonly HashSet<uint> _seen = new();
    private readonly CancellationTokenSource _stop = new();
    private Phase _phase;
    private long _phaseStarted;
    private uint _sequence;
    private uint _code;
    private PlcRecipeChangeOperation? _operation;
    private Task<RecipeChangeDecision>? _result;
    private RecipeChangeDecision? _decision;
    private Task _cancellationNotification = Task.CompletedTask;
    private bool _cancellationRequested;
    private bool _disposed;

    internal PlcRecipeChangeHandshake(TimeSpan timeout,
        Func<ModbusRecipeChangeControllerSignals, PlcRecipeChangeOperation> begin,
        Func<RecipeChangeTransition, Task> record,
        Func<bool, ushort, ushort, uint, uint, CancellationToken, Task> write,
        Func<long>? clock = null)
    {
        if (timeout < TimeSpan.FromMilliseconds(1) || timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeout = timeout; _begin = begin; _record = record; _write = write;
        _clock = clock ?? Stopwatch.GetTimestamp;
    }
    internal bool Active => _phase is Phase.Executing or Phase.AwaitingAcknowledgement or Phase.AwaitingReset;
    internal Task Completion => (Task?)_result ?? Task.CompletedTask;

    internal async Task InitializeAsync(ModbusRecipeChangeControllerSignals controller,
        ModbusRecipeChangeRuntimeSignals runtime)
    {
        if (_phase != Phase.Initial) throw new InvalidOperationException("RecipeChangeAlreadyInitialized");
        if (!Zero(controller) || runtime.ResponseValid || runtime.Outcome != 0 || runtime.Reason != 0 ||
            runtime.RequestSequence != 0 || runtime.SelectionCode != 0)
        {
            _sequence = controller.RequestSequence; _code = controller.SelectionCode;
            await FaultAsync(RecipeChangeReason.InitialStateInvalid, "RecipeChangeInitialStateInvalid").ConfigureAwait(false);
        }
        _phase = Phase.Idle;
    }

    internal async Task ObserveAsync(ModbusRecipeChangeControllerSignals value, CancellationToken token)
    {
        if (_disposed || _phase is Phase.Initial or Phase.Faulted)
            throw new InvalidOperationException("RecipeChangeHandshakeUnavailable");
        token.ThrowIfCancellationRequested();
        if (Active && (_clock() - _phaseStarted) / (double)Stopwatch.Frequency >= _timeout.TotalSeconds)
            await FaultAsync(RecipeChangeReason.HandshakeDeadlineExceeded, "RecipeChangeHandshakeDeadlineExceeded").ConfigureAwait(false);

        if (_phase == Phase.Idle)
        {
            if (Zero(value)) return;
            _sequence = value.RequestSequence; _code = value.SelectionCode;
            if (!value.Request || value.Acknowledgement || _sequence == 0 || _code == 0)
                await FaultAsync(RecipeChangeReason.InitialStateInvalid, "RecipeChangeRequestStateInvalid").ConfigureAwait(false);
            if (_seen.Count >= 100_000)
                await FaultAsync(RecipeChangeReason.ActivationFailed, "RecipeChangeObservationCapacityExceeded").ConfigureAwait(false);
            if (!_seen.Add(_sequence))
                await FaultAsync(RecipeChangeReason.DuplicateRequest, "RecipeChangeRequestIdentityReused").ConfigureAwait(false);
            // 此回调必须立即取得 Runtime 预约或确定 RejectedBusy；启动一个等待预约的 Task 会把请求隐式排队。
            try
            {
                _operation = _begin(value);
                _phase = Phase.Executing; _phaseStarted = _clock();
                _result = _operation.Start(_stop.Token);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { await FaultAsync(RecipeChangeReason.ActivationFailed, "RecipeChangeAdmissionUnavailable").ConfigureAwait(false); }
            return;
        }

        if (_phase == Phase.AwaitingReset && Zero(value))
        {
            await RecordAsync(RecipeChangeEventKind.ResetObserved).ConfigureAwait(false);
            _operation = null; _result = null; _decision = null;
            _sequence = 0; _code = 0; _phase = Phase.Idle;
            return;
        }
        if (!value.Request || value.RequestSequence != _sequence || value.SelectionCode != _code)
            await FaultAsync(RecipeChangeReason.RequestChanged, "RecipeChangeRequestChangedDuringHandshake").ConfigureAwait(false);

        if (_phase == Phase.Executing)
        {
            if (value.Acknowledgement)
                await FaultAsync(RecipeChangeReason.UnexpectedAcknowledgement, "RecipeChangeAcknowledgementBeforeResponse").ConfigureAwait(false);
            if (_result is null || !_result.IsCompleted) return;
            try { _decision = await _result.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                await FaultAsync(RecipeChangeReason.AuditUnavailable, "RecipeChangeDecisionUnavailable").ConfigureAwait(false);
                return;
            }
            if (!Enum.IsDefined(_decision.Outcome) || !Enum.IsDefined(_decision.Reason) ||
                _decision.Outcome == RecipeChangeOutcome.Succeeded &&
                    (_decision.Reason != RecipeChangeReason.None || _decision.Activation is null) ||
                _decision.Outcome != RecipeChangeOutcome.Succeeded && _decision.Reason == RecipeChangeReason.None)
                await FaultAsync(RecipeChangeReason.ActivationFailed, "RecipeChangeDecisionInvalid").ConfigureAwait(false);
            if (_decision.Outcome == RecipeChangeOutcome.ProtocolFault)
                await FaultAsync(_decision.Reason, _decision.ReasonCode).ConfigureAwait(false);
            // 操作只在不可变结果持久化后返回；物理响应在设备确认写入后才记录。
            await WriteAsync(true, (ushort)_decision.Outcome, (ushort)_decision.Reason,
                _sequence, _code, token).ConfigureAwait(false);
            await RecordAsync(RecipeChangeEventKind.ResponsePublished).ConfigureAwait(false);
            _phase = Phase.AwaitingAcknowledgement; _phaseStarted = _clock();
            return;
        }
        if (_phase == Phase.AwaitingAcknowledgement)
        {
            if (!value.Acknowledgement) return;
            await RecordAsync(RecipeChangeEventKind.AcknowledgementObserved).ConfigureAwait(false);
            await WriteAsync(false, 0, 0, 0, 0, token).ConfigureAwait(false);
            await RecordAsync(RecipeChangeEventKind.ResponseCleared).ConfigureAwait(false);
            _phase = Phase.AwaitingReset; _phaseStarted = _clock();
            return;
        }
        if (_phase == Phase.AwaitingReset && !value.Acknowledgement)
            await FaultAsync(RecipeChangeReason.RequestChanged, "RecipeChangePartialResetInvalid").ConfigureAwait(false);
    }

    private async Task RecordAsync(RecipeChangeEventKind kind)
    {
        try { await _record(new(kind, _sequence, _code, _decision)).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { await FaultAsync(RecipeChangeReason.AuditUnavailable, "RecipeChangeAuditUnavailable").ConfigureAwait(false); }
    }

    private async Task WriteAsync(bool valid, ushort outcome, ushort reason, uint sequence, uint code,
        CancellationToken token)
    {
        try { await _write(valid, outcome, reason, sequence, code, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { await FaultAsync(RecipeChangeReason.CommunicationLost, "RecipeChangeResponseWriteFailed").ConfigureAwait(false); }
    }
    private async Task FaultAsync(RecipeChangeReason reason, string code)
    {
        _phase = Phase.Faulted;
        // 同步使提交权威失效；通知/回滚由激活所有者负责，且不会把故障变成成功。
        RequestCancellation();
        try
        {
            await _record(new(RecipeChangeEventKind.ProtocolFault, _sequence, _code,
                _decision ?? new(RecipeChangeOutcome.ProtocolFault, reason, code), code)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { throw new InvalidOperationException(code, exception); }
        throw new InvalidOperationException(code);
    }
    private static bool Zero(ModbusRecipeChangeControllerSignals value) => !value.Request &&
        !value.Acknowledgement && value.RequestSequence == 0 && value.SelectionCode == 0;

    private void RequestCancellation()
    {
        if (_cancellationRequested) return;
        _cancellationRequested = true;
        // The closed Runtime callback only invalidates its commit capability; it
        // must not call provider cancellation handlers on the observer thread.
        _operation?.Cancel();
        _cancellationNotification = Task.Run(() =>
        {
            try { _stop.Cancel(); }
            catch (AggregateException) { /* 通知不能恢复已撤销的权威。 */ }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RequestCancellation();
        // 挂起的激活在恢复硬件时仍可能观察此令牌；Completion 保留给外层生产所有者做有界汇合。
        var completion = Task.WhenAll(Completion, _cancellationNotification);
        if (!completion.IsCompleted)
            _ = completion.ContinueWith(task => { _ = task.Exception; _stop.Dispose(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        else _stop.Dispose();
    }
}
