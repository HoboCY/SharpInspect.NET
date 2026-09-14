using System.Collections.ObjectModel;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>Bounded presentation of verified delivery obligations and immutable attempt history.</summary>
public sealed partial class ProductionOutboxViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IProductionOutboxQuery? _query;
    private readonly IUiDispatcher _dispatcher;
    private readonly int _pageSize;
    private readonly ObservableCollection<OutboxPendingItem> _rows = new();
    private readonly ObservableCollection<ProductionOutboxEvent> _events = new();
    private CancellationTokenSource? _cancellation;
    private long _generation;
    private long? _next;
    private long? _historyNext;
    private bool _busy;
    private bool _disposed;
    private string _status = "尚未读取外部交付记录。";
    private string _historyDeliveryText = string.Empty;
    private OutboxPendingItem? _selected;

    public ProductionOutboxViewModel(IProductionOutboxQuery? query,
        IUiDispatcher? dispatcher = null, int pageSize = 20)
    {
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
        _query = query;
        _dispatcher = dispatcher ?? new DispatcherUiDispatcher();
        _pageSize = pageSize;
        Rows = new(_rows);
        Events = new(_events);
        RefreshCommand = new(RefreshAsync, () => Available);
        NextPageCommand = new(NextPageAsync, () => Available && _next.HasValue);
        ReadHistoryCommand = new(ReadHistoryAsync, () => Available && HistoryDeliveryId.HasValue);
        NextHistoryPageCommand = new(NextHistoryPageAsync, () => Available && _historyNext.HasValue);
        if (query is null) _status = "外部交付查询未配置。";
    }

    public ProductionOutboxViewModel(IProductionOutboxQuery? query,
        IProductionOutboxRecoveryService? recovery, IProductionOutboxGovernanceQuery? governance,
        IInteractiveSessionService? sessions, IStepUpAuthentication? stepUpAuthentication,
        IUiDispatcher? dispatcher = null, int pageSize = 20) : this(query, dispatcher, pageSize)
    {
        _recovery = recovery;
        _governance = governance;
        _sessions = sessions;
        _stepUp = stepUpAuthentication;
        if (_sessions is not null) _sessions.Changed += SessionChanged;
    }

    public ReadOnlyObservableCollection<OutboxPendingItem> Rows { get; }
    public ReadOnlyObservableCollection<ProductionOutboxEvent> Events { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand NextPageCommand { get; }
    public AsyncRelayCommand ReadHistoryCommand { get; }
    public AsyncRelayCommand NextHistoryPageCommand { get; }
    public bool IsBusy => _busy;
    public string StatusMessage => _status;
    private bool Available => !_disposed && !_busy && _query is not null;
    private Guid? HistoryDeliveryId => Guid.TryParse(_historyDeliveryText, out var id) && id != Guid.Empty ? id : null;
    public string HistoryDeliveryText
    {
        get => _historyDeliveryText;
        set
        {
            EnsureUi();
            var bounded = (value ?? string.Empty).Trim();
            if (bounded.Length > 64) bounded = bounded[..64];
            if (_historyDeliveryText == bounded) return;
            _historyDeliveryText = bounded;
            _generation++;
            DescribeInvalidatedWait();
            _cancellation?.Cancel();
            _events.Clear();
            ClearGovernance();
            _historyNext = null;
            if (_selected?.Delivery.DeliveryId != HistoryDeliveryId) _selected = null;
            ClearOperationInputs();
            NotifyState();
        }
    }
    public OutboxPendingItem? SelectedItem
    {
        get => _selected;
        set
        {
            EnsureUi();
            if (_disposed || value is not null && !_rows.Contains(value)) value = null;
            if (ReferenceEquals(_selected, value)) return;
            _generation++;
            DescribeInvalidatedWait();
            _cancellation?.Cancel();
            _selected = value;
            ClearOperationInputs();
            _historyDeliveryText = value?.Delivery.DeliveryId.ToString("D") ?? string.Empty;
            _events.Clear();
            ClearGovernance();
            _historyNext = null;
            NotifyState();
        }
    }
    public string SelectionSummary => _selected is null ? "请选择一条待处理交付。" :
        $"{StateLabel(_selected)} · 已尝试 {_selected.AttemptCount} 次 · " +
        (_selected.Delivery.Route.Criticality == OutboxRouteCriticality.Required
            ? "必需路由：永久阻塞或积压超限将禁止新的检测触发。"
            : "尽力路由：交付失败仅报警，已发布的检测结果保持不变。");
    public string PayloadHash => _selected?.Delivery.Payload?.ContentHash ?? string.Empty;
    public string FailureReason => _selected?.LastFailureReasonCode ?? string.Empty;

    public static string StateLabel(OutboxPendingItem item) => item.State == OutboxDeliveryState.Succeeded
        ? "已确认送达" : item.PermanentBlock ? "永久阻塞，等待授权处置" :
        item.ActiveAttemptId.HasValue ? "发送中，结果尚未确定" :
        !item.RetryEligible ? "自动重试预算已用尽" :
        item.State == OutboxDeliveryState.Failed ? "失败或结果未知，等待有界重试" : "待发送";

    public Task RefreshAsync() => LoadAsync(false, false);
    public Task NextPageAsync() => LoadAsync(true, false);
    public Task ReadHistoryAsync() => LoadAsync(false, true);
    public Task NextHistoryPageAsync() => LoadAsync(true, true);

    public void Deactivate()
    {
        EnsureUi();
        _generation++;
        _cancellation?.Cancel();
        _rows.Clear();
        _events.Clear();
        ClearGovernance();
        ClearOperationInputs();
        _selected = null;
        _historyDeliveryText = string.Empty;
        _next = _historyNext = null;
        _status = "页面已离开；重新进入后读取最新交付记录。";
        NotifyState();
    }

    private async Task LoadAsync(bool next, bool history)
    {
        CancellationTokenSource? cancellation = null;
        long generation = 0, after = 0;
        Guid? deliveryId = null;
        await _dispatcher.InvokeAsync(() =>
        {
            if (!Available || history && !HistoryDeliveryId.HasValue || next &&
                !(history ? _historyNext : _next).HasValue) return;
            after = next ? (history ? _historyNext : _next)!.Value : 0;
            deliveryId = history ? HistoryDeliveryId : null;
            _events.Clear();
            ClearGovernance();
            _historyNext = null;
            if (!history) { _rows.Clear(); _selected = null; _next = null; _historyDeliveryText = string.Empty; ClearOperationInputs(); }
            cancellation = new();
            _cancellation = cancellation;
            generation = ++_generation;
            _busy = true;
            _status = "正在读取已验证的交付证据…";
            NotifyState();
        });
        if (cancellation is null) return;
        OutboxPendingPage? pending = null;
        OutboxHistoryPage? events = null;
        ProductionOutboxGovernanceSnapshot? governance = null;
        try
        {
            if (history)
            {
                events = await Task.Run(async () => await _query!.ReadHistoryAsync(deliveryId: deliveryId,
                    afterPosition: after, pageSize: _pageSize, cancellationToken: cancellation.Token)
                    .ConfigureAwait(false)).ConfigureAwait(false);
                if (_governance is not null)
                    governance = await Task.Run(async () => await _governance.ReadAsync(deliveryId,
                        cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
            }
            else
                pending = await Task.Run(async () => await _query!.ReadPendingAsync(after, _pageSize,
                    cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_cancellation, cancellation)) { _cancellation = null; _busy = false; }
                if (!_disposed && generation == _generation)
                {
                    if (!history && ValidPending(pending, after))
                    {
                        foreach (var row in pending!.Items) _rows.Add(row);
                        _requiresFreshPending = false;
                        _selected = _rows.FirstOrDefault();
                        _historyDeliveryText = _selected?.Delivery.DeliveryId.ToString("D") ?? string.Empty;
                        _next = pending.HasMore ? pending.NextPosition : null;
                        _status = $"本页 {_rows.Count} 条待处理交付；审计位置 {pending.Backlog!.ThroughAuditSequence}。";
                    }
                    else if (history && ValidHistory(events, deliveryId!.Value, after))
                    {
                        foreach (var row in events!.Events) _events.Add(row);
                        _historyNext = events.HasMore ? events.NextPosition : null;
                        _status = $"本页 {_events.Count} 条不可变交付事实。";
                        ApplyGovernance(governance, deliveryId.Value);
                    }
                    else _status = "交付证据暂不可用，请重新刷新。";
                }
                NotifyState();
            });
            cancellation.Dispose();
        }
    }

    private bool ValidPending(OutboxPendingPage? page, long after)
    {
        if (page is not { Available: true, Backlog: { } } || page.Items is null ||
            page.Items.Count > _pageSize || page.Backlog.ThroughAuditSequence < 0) return false;
        var ids = new HashSet<Guid>();
        foreach (var row in page.Items)
        {
            if (row is null || row.Position <= after || !ids.Add(row.Delivery.DeliveryId) ||
                row.State == OutboxDeliveryState.Succeeded) return false;
            after = row.Position;
        }
        return !page.HasMore || page.Items.Count > 0 && page.NextPosition == after;
    }
    private bool ValidHistory(OutboxHistoryPage? page, Guid deliveryId, long after)
    {
        if (page is not { Available: true } || page.Events is null || page.Events.Count > _pageSize) return false;
        foreach (var row in page.Events)
        {
            if (row is null || row.DeliveryId != deliveryId || row.Position <= after) return false;
            after = row.Position;
        }
        return !page.HasMore || page.Events.Count > 0 && page.NextPosition == after;
    }
    private void NotifyState()
    {
        foreach (var name in new[] { nameof(IsBusy), nameof(StatusMessage), nameof(SelectedItem),
                     nameof(SelectionSummary), nameof(PayloadHash), nameof(FailureReason), nameof(HistoryDeliveryText) }) OnPropertyChanged(name);
        RefreshCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
        ReadHistoryCommand.RaiseCanExecuteChanged();
        NextHistoryPageCommand.RaiseCanExecuteChanged();
        NotifyOperations();
    }
    private void EnsureUi()
    {
        if (!_dispatcher.CheckAccess) throw new PresentationStateUnavailableException();
    }
    public async ValueTask DisposeAsync() => await _dispatcher.InvokeAsync(() =>
    {
        if (_disposed) return;
        _disposed = true;
        if (_sessions is not null) _sessions.Changed -= SessionChanged;
        Deactivate();
    });
}
