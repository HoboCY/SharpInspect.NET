using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Outbox;

/// <summary>
/// A bounded durable dispatcher, independent of PLC publication. Each configured route owns at
/// most one physical send; a stalled BestEffort route cannot occupy another route's slot. The
/// durable store owns attempt counts, state and all mutation authority through its single writer.
/// </summary>
internal sealed class ProductionOutboxWorker
{
    internal const string ExecutionProfile = "outbox-v1:routes=64:senders=1-per-route:wake=1:poll-ms=1000:retire-ms=5000";
    internal static readonly TimeSpan RetirementTimeout = TimeSpan.FromSeconds(5);
    private readonly SqliteCommandStore _store;
    private readonly ProductionStoreOptions _storeOptions;
    private readonly ProductionOutboxStoreOptions _options;
    private readonly ProductionOutboxOptions _transports;
    private readonly SqliteProductionOutboxQuery _query;
    private readonly Guid _epoch;
    private readonly Action<OutboxBacklogSnapshot> _publish;
    private readonly Func<string, Task> _fault;
    private readonly Dictionary<string, Task> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly TaskCompletionSource<bool> _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _run;

    internal ProductionOutboxWorker(SqliteCommandStore store, ProductionStoreOptions options,
        ProductionOutboxOptions transports, Guid epoch, Task initialization,
        Action<OutboxBacklogSnapshot> publish, Func<string, Task> fault)
    {
        _store = store; _storeOptions = options;
        _options = options.Outbox ?? throw new ArgumentException("OutboxConfigurationRequired");
        _transports = transports; _query = new(options); _epoch = epoch; _publish = publish; _fault = fault;
        _run = Task.Run(() => RunAsync(initialization));
    }
    internal Task Startup => _startup.Task;
    internal Task Completion => _run;
    internal string? FailureReason { get; private set; }
    internal void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }
    internal async Task<bool> StopAsync()
    {
        _stop.Cancel(); Wake();
        try { await _run.WaitAsync(RetirementTimeout).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }

    private async Task RunAsync(Task initialization)
    {
        try
        {
            await initialization.WaitAsync(_stop.Token).ConfigureAwait(false);
            var backlog = await _query.ReadBacklogAsync(_stop.Token).ConfigureAwait(false);
            _publish(backlog);
            _startup.TrySetResult(true); // 启动就绪只取决于本地账本校验，不能被外部接收端的响应时间拖住。
            while (true)
            {
                _stop.Token.ThrowIfCancellationRequested();
                foreach (var completed in _active.Where(value => value.Value.IsCompleted).ToArray())
                {
                    await completed.Value.ConfigureAwait(false);
                    _active.Remove(completed.Key);
                }
                try { await SweepAsync(_stop.Token).ConfigureAwait(false); }
                catch (Exception error) when (IsTransientStore(error) && !_stop.IsCancellationRequested) { }
                await _wake.WaitAsync(TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { _startup.TrySetCanceled(_stop.Token); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FailureReason = Reason(error);
            _startup.TrySetException(error);
            await _fault(FailureReason).ConfigureAwait(false);
            _stop.Cancel();
        }
        finally
        {
            // 超时只撤销本次成功资格；发送代码真正退出前，该路由的物理槽位仍被占用。
            try { await Task.WhenAll(_active.Values).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
    }

    private async Task SweepAsync(CancellationToken token)
    {
        long after = 0;
        do
        {
            var page = await _query.ReadPendingAsync(after, _options.MaximumPageSize, token).ConfigureAwait(false);
            if (!page.Available || page.Backlog is null) throw new InvalidOperationException(page.ReasonCode);
            _publish(page.Backlog);
            foreach (var item in page.Items)
            {
                token.ThrowIfCancellationRequested();
                var routeId = item.Delivery.Route.RouteId;
                if (_active.ContainsKey(routeId) || item.State == OutboxDeliveryState.Succeeded || item.PermanentBlock ||
                    item.Delivery.Payload is null) continue;
                if (item.ActiveAttemptId is null && (!item.RetryEligible || item.RetryAfterUtc > DateTimeOffset.UtcNow)) continue;
                if (_active.Count >= ProductionOutboxStoreOptions.MaximumRoutesHardLimit)
                    throw new InvalidOperationException("OutboxPhysicalRouteCapacityExceeded");
                _active.Add(routeId, ProcessAsync(item));
            }
            if (!page.HasMore) break;
            if (page.NextPosition <= after) throw new InvalidOperationException("OutboxCursorInvalid");
            after = page.NextPosition;
        } while (true);
    }

    private async Task ProcessAsync(OutboxPendingItem item)
    {
        try
        {
            if (item.ActiveAttemptId is { } interrupted)
            {
                // 已开始的尝试未留下终态，无法判断接收端是否已处理；先记为结果未知，再按原 ID 和字节重试。
                if (item.ActiveRuntimeEpoch is not { } previousEpoch)
                    throw new InvalidOperationException("OutboxInterruptedAttemptEpochMissing");
                var recordedAt = DateTimeOffset.UtcNow;
                var recovered = await _store.AppendOutboxOutcomeAsync(new OutboxFailureRequest(item.Delivery.DeliveryId,
                    interrupted, previousEpoch, recordedAt, "OutboxProcessRestartOutcomeUnknown",
                    OutboxFailureCategory.UnknownOutcome, RetryAfter(recordedAt, item.AttemptCount)), Deadline(), CancellationToken.None)
                    .ConfigureAwait(false);
                RequireCommit(recovered);
                Publish(recovered);
                return; // 必须重新读取持久化的重试预算，不能沿用恢复前的次数直接再次发送。
            }
            var delivery = item.Delivery;
            var attempt = Guid.NewGuid();
            var binding = _transports.Resolve(delivery.Route);
            var connectionHash = binding?.ConnectionBindingHash ?? OutboxValidation.HashParts(
                "sharpinspect-outbox-missing-transport-v1", delivery.Route.ContentHash);
            // 先提交 AttemptStarted，再调用外部传输；中途崩溃也能留下可恢复的尝试身份。
            var started = await _store.BeginOutboxAttemptAsync(new(delivery.DeliveryId, attempt,
                checked(item.AttemptCount + 1), _epoch, DateTimeOffset.UtcNow, connectionHash), Deadline(), _stop.Token)
                .ConfigureAwait(false);
            RequireCommit(started);
            Publish(started);
            if (binding is null)
            {
                await FailAsync(delivery, attempt, item.AttemptCount + 1, OutboxFailureCategory.Permanent,
                    "OutboxHistoricalHandlerUnavailable").ConfigureAwait(false);
                return;
            }
            using var owner = new OutboxSendOwner(delivery, binding, attempt, _epoch, _options.AttemptTimeout, _stop.Token);
            try
            {
                var observed = await owner.ObserveAsync(_stop.Token).ConfigureAwait(false);
                if (observed.Acceptance is { } claim)
                {
                    var succeeded = await _store.AppendOutboxOutcomeAsync(new OutboxSuccessRequest(delivery.DeliveryId,
                        _epoch, DateTimeOffset.UtcNow, claim), Deadline(), CancellationToken.None).ConfigureAwait(false);
                    if (!succeeded.Committed && succeeded.ReasonCode == "ProductionOutboxAcceptanceClaimRetired")
                    {
                        owner.Retire();
                        await FailAsync(delivery, attempt, item.AttemptCount + 1,
                            OutboxFailureCategory.UnknownOutcome, "OutboxAcceptanceDeadlineElapsed").ConfigureAwait(false);
                    }
                    else
                    {
                        RequireCommit(succeeded);
                        Publish(succeeded);
                    }
                }
                else
                {
                    var category = observed.Failure ?? OutboxFailureCategory.UnknownOutcome;
                    var reason = observed.ReasonCode ?? "OutboxOutcomeUnknown";
                    owner.Retire();
                    try { await owner.PhysicalCompletion.WaitAsync(RetirementTimeout).ConfigureAwait(false); }
                    catch (TimeoutException)
                    { category = OutboxFailureCategory.Permanent; reason = "OutboxTransportRetirementPending"; }
                    await FailAsync(delivery, attempt, item.AttemptCount + 1, category, reason).ConfigureAwait(false);
                }
            }
            finally
            {
                owner.Retire();
                await owner.PhysicalCompletion.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) when (IsTransientStore(error) && !_stop.IsCancellationRequested) { }
        finally { Wake(); }
    }

    private async Task FailAsync(OutboxDelivery delivery, Guid attempt, int attemptNumber,
        OutboxFailureCategory category, string reason)
    {
        var recordedAt = DateTimeOffset.UtcNow;
        var failed = await _store.AppendOutboxOutcomeAsync(new OutboxFailureRequest(delivery.DeliveryId, attempt,
            _epoch, recordedAt, reason, category,
            category == OutboxFailureCategory.Permanent ? null : RetryAfter(recordedAt, attemptNumber)), Deadline(), CancellationToken.None)
            .ConfigureAwait(false);
        RequireCommit(failed);
        Publish(failed);
    }
    private DateTimeOffset RetryAfter(DateTimeOffset recordedAt, int attempt) => recordedAt + TimeSpan.FromMilliseconds(
        Math.Min(_options.MaximumRetryDelay.TotalMilliseconds, 1000d * Math.Pow(2, Math.Min(attempt - 1, 10))));
    private StoreDeadline Deadline() => new(_storeOptions.CommitTimeout);
    private void Publish(OutboxWriteResult result) { if (result.Backlog is { } backlog) _publish(backlog); }
    private static void RequireCommit(OutboxWriteResult result)
    { if (!result.Committed) throw new InvalidOperationException(result.ReasonCode); }
    private static bool IsTransientStore(Exception error) => error is TimeoutException ||
        error is InvalidOperationException && (error.Message == "ProductionOutboxRetryWindowOpen" ||
            error.Message.Contains("Busy", StringComparison.Ordinal) ||
            error.Message.Contains("Deadline", StringComparison.Ordinal));
    private static string Reason(Exception error) => error is InvalidOperationException ? error.Message : "OutboxStoreUnavailable";
}
