using System.Collections.ObjectModel;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>Bounded read-only projection of verified persisted image lifecycle facts.</summary>
public sealed class ProductionImageEvidenceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IProductionImageEvidenceQuery? _query;
    private readonly IUiDispatcher _dispatcher;
    private readonly int _pageSize;
    private readonly ObservableCollection<ProductionImageEvidenceRecord> _rows = new();
    private CancellationTokenSource? _cancellation;
    private long _generation;
    private long? _through;
    private long? _next;
    private bool _busy;
    private bool _disposed;
    private string _inspectionText = string.Empty;
    private string _status = "尚未读取生产原图证据。";
    private ProductionImageEvidenceRecord? _selected;

    public ProductionImageEvidenceViewModel(IProductionImageEvidenceQuery? query,
        IUiDispatcher? dispatcher = null, int pageSize = 20)
    {
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
        _query = query;
        _dispatcher = dispatcher ?? new DispatcherUiDispatcher();
        _pageSize = pageSize;
        Rows = new ReadOnlyObservableCollection<ProductionImageEvidenceRecord>(_rows);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !_disposed && !_busy && _query is not null);
        NextPageCommand = new AsyncRelayCommand(NextPageAsync, () => !_disposed && !_busy && _next.HasValue);
        if (query is null) _status = "生产原图证据查询未配置。";
    }

    public ReadOnlyObservableCollection<ProductionImageEvidenceRecord> Rows { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand NextPageCommand { get; }
    public bool IsBusy => _busy;
    public bool HasNextPage => _next.HasValue;
    public string StatusMessage => _status;
    public string InspectionText
    {
        get => _inspectionText;
        set
        {
            EnsureUi();
            var bounded = (value ?? string.Empty).Trim();
            if (bounded.Length > 64) bounded = bounded[..64];
            if (_inspectionText == bounded) return;
            _inspectionText = bounded;
            OnPropertyChanged();
            Invalidate("筛选已变化，请刷新原图证据。");
        }
    }
    public ProductionImageEvidenceRecord? SelectedRecord
    {
        get => _selected;
        set
        {
            EnsureUi();
            if (_disposed || value is not null && !_rows.Contains(value)) value = null;
            if (ReferenceEquals(_selected, value)) return;
            _selected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedEvents));
            OnPropertyChanged(nameof(SelectionSummary));
            OnPropertyChanged(nameof(ContentHash));
            OnPropertyChanged(nameof(FinalFileSummary));
        }
    }
    public IReadOnlyList<ProductionImageFinalizationEvent> SelectedEvents =>
        _selected?.Events ?? (IReadOnlyList<ProductionImageFinalizationEvent>)Array.Empty<ProductionImageFinalizationEvent>();
    public string ContentHash => _selected?.Identity.CanonicalPixelHash ?? string.Empty;
    public string SelectionSummary => _selected is null ? "请选择一条生产原图证据。" :
        $"{StateLabel(_selected.State.State)} · {_selected.Identity.Width} × {_selected.Identity.Height} · " +
        $"{_selected.Identity.PixelFormat}" +
        (_selected.Identity.ValidBits is { } bits ? $" · 有效位 {bits}" : string.Empty) +
        $" · 已尝试 {_selected.State.AttemptCount} 次";
    public string FinalFileSummary => _selected?.State.Success is { } success
        ? $"PNG · {success.EncodedByteLength} 字节 · {success.FinalFileName} · " +
          (_selected.State.CleanupState == ProductionImageCleanupState.Released ? "暂存已清理" : "暂存清理待确认")
        : _selected?.State.LastFailureReasonCode is { } reason ? $"最近失败：{reason}" +
          (_selected.State is { State: ProductionImageFinalizationState.Failed, RetryEligible: false, IntegrityConflict: false }
              ? " · 自动重试次数已用尽，等待人工处置。" : string.Empty)
          : "最终 PNG 尚未完成。";
    public static string StateLabel(ProductionImageFinalizationState state) => state switch
    {
        ProductionImageFinalizationState.Pending => "待生成",
        ProductionImageFinalizationState.Failed => "生成失败",
        ProductionImageFinalizationState.Succeeded => "已持久完成",
        _ => "状态未知"
    };

    public Task RefreshAsync() => LoadAsync(false);
    public Task NextPageAsync() => LoadAsync(true);
    public void Deactivate()
    {
        EnsureUi();
        Invalidate("页面已离开；重新进入后读取最新证据。");
    }
    private void Invalidate(string message)
    {
        _generation++;
        _cancellation?.Cancel();
        _through = null;
        _next = null;
        _rows.Clear();
        SelectedRecord = null;
        _status = message;
        NotifyState();
    }
    private async Task LoadAsync(bool nextPage)
    {
        ProductionImageEvidenceFilter? filter = null;
        CancellationTokenSource? cancellation = null;
        long generation = 0;
        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed || _busy || nextPage && !_next.HasValue) return;
            if (_query is null) { _status = "生产原图证据查询未配置。"; NotifyState(); return; }
            Guid? inspection = null;
            if (_inspectionText.Length != 0)
            {
                if (!Guid.TryParse(_inspectionText, out var value) || value == Guid.Empty)
                { Invalidate("请输入有效的检测 ID。"); return; }
                inspection = value;
            }
            filter = new(inspection, null, nextPage ? _next!.Value : 0, nextPage ? _through : null, _pageSize);
            cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            generation = ++_generation;
            _busy = true;
            _rows.Clear();
            SelectedRecord = null;
            _status = "正在读取持久证据…";
            NotifyState();
        });
        if (filter is null || cancellation is null) return;
        ProductionImageEvidencePage? page = null;
        try
        {
            page = await Task.Run(async () => await _query!.QueryAsync(filter, cancellation.Token)
                .ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_cancellation, cancellation)) { _cancellation = null; _busy = false; }
                if (!_disposed && generation == _generation)
                {
                    if (page is not null && ValidPage(page, filter))
                    {
                        _through = page.ThroughPosition;
                        _next = page.NextAfterPosition;
                        foreach (var row in page.Items) _rows.Add(row);
                        SelectedRecord = _rows.FirstOrDefault();
                        _status = _rows.Count == 0 ? "未找到匹配的生产原图证据。" :
                            $"已读取 {_rows.Count} 条持久证据；审计位置 {page.ThroughAuditSequence}。";
                    }
                    else
                    {
                        _through = null;
                        _next = null;
                        _rows.Clear();
                        SelectedRecord = null;
                        _status = "原图证据暂不可用，请重新刷新。";
                    }
                }
                NotifyState();
            });
            cancellation.Dispose();
        }
    }
    private bool ValidPage(ProductionImageEvidencePage page, ProductionImageEvidenceFilter filter)
    {
        if (!page.Available || page.Items is null || page.Items.Count > _pageSize ||
            page.ThroughPosition < filter.AfterPosition || page.ThroughAuditSequence < 0 ||
            filter.ThroughPosition.HasValue && page.ThroughPosition != filter.ThroughPosition) return false;
        long previous = filter.AfterPosition;
        var seen = new HashSet<Guid>();
        foreach (var row in page.Items)
        {
            if (row is null || row.Identity is null || row.State is null || row.Position <= previous ||
                row.Position > page.ThroughPosition || !seen.Add(row.Identity.WorkId) ||
                filter.InspectionId.HasValue && row.Identity.InspectionId != filter.InspectionId ||
                row.State.WorkId != row.Identity.WorkId || row.State.ManifestId != row.Identity.ManifestId ||
                row.State.InspectionId != row.Identity.InspectionId ||
                row.State.WorkContentHash != row.Identity.WorkContentHash ||
                row.State.ManifestContentHash != row.Identity.ManifestContentHash ||
                !Enum.IsDefined(row.State.State) || !Enum.IsDefined(row.State.CleanupState) ||
                (row.State.State == ProductionImageFinalizationState.Succeeded) != (row.State.Success is not null)) return false;
            if (row.State.Success is { } success &&
                (success.CanonicalPixelHash != row.Identity.CanonicalPixelHash ||
                 success.Width != row.Identity.Width || success.Height != row.Identity.Height ||
                 success.PixelFormat != row.Identity.PixelFormat || success.ValidBits != row.Identity.ValidBits)) return false;
            previous = row.Position;
        }
        return !page.NextAfterPosition.HasValue || page.NextAfterPosition > filter.AfterPosition &&
            page.NextAfterPosition <= page.ThroughPosition && page.NextAfterPosition >= previous;
    }
    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(StatusMessage));
        RefreshCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
    }
    private void EnsureUi()
    {
        if (!_dispatcher.CheckAccess) throw new PresentationStateUnavailableException();
    }
    public async ValueTask DisposeAsync()
    {
        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            _disposed = true;
            Invalidate("原图证据查看器已关闭。");
        });
    }
}
