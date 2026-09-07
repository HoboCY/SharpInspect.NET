using System.Collections.ObjectModel;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>Bounded read-only history. Filter changes invalidate pending responses and selected geometry.</summary>
public sealed class AlgorithmResultHistoryViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IAlgorithmResultQuery? _query;
    private readonly IUiDispatcher _dispatcher;
    private readonly int _pageSize;
    private readonly ObservableCollection<AlgorithmResultRecord> _rows = new();
    private CancellationTokenSource? _cancellation;
    private long _generation;
    private long? _through;
    private long? _next;
    private bool _busy;
    private bool _disposed;
    private string _correlationText = string.Empty;
    private ExecutionKind _correlationKind = ExecutionKind.Manual;
    private AlgorithmResultRecord? _selected;
    private string _status = "尚未读取计算结果。";

    public AlgorithmResultHistoryViewModel(IAlgorithmResultQuery? query, IUiDispatcher? dispatcher = null,
        int pageSize = 20)
    {
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
        _query = query; _dispatcher = dispatcher ?? new DispatcherUiDispatcher(); _pageSize = pageSize;
        Rows = new ReadOnlyObservableCollection<AlgorithmResultRecord>(_rows);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !_disposed && !_busy && _query is not null);
        NextPageCommand = new AsyncRelayCommand(NextPageAsync, () => !_disposed && !_busy && _next.HasValue);
        if (_query is null) _status = "结果归档未配置。";
    }

    public ReadOnlyObservableCollection<AlgorithmResultRecord> Rows { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand NextPageCommand { get; }
    public bool IsBusy => _busy;
    public bool HasNextPage => _next.HasValue;
    public string StatusMessage => _status;
    public string ScopeLabel => "开发计算历史 · 不代表正式生产、手动或资格记录";
    public IReadOnlyList<ExecutionKind> CorrelationKinds { get; } =
        Array.AsReadOnly(new[] { ExecutionKind.Manual, ExecutionKind.Qualification });

    public string CorrelationText
    {
        get => _correlationText;
        set
        {
            EnsureUi();
            value ??= string.Empty;
            if (_correlationText == value) return;
            _correlationText = value.Length <= 64 ? value : value[..64];
            OnPropertyChanged(); InvalidateFilter();
        }
    }

    public ExecutionKind CorrelationKind
    {
        get => _correlationKind;
        set
        {
            EnsureUi();
            if (_correlationKind == value) return;
            _correlationKind = value; OnPropertyChanged(); InvalidateFilter();
        }
    }

    public AlgorithmResultRecord? SelectedRecord
    {
        get => _selected;
        set
        {
            EnsureUi();
            if (_disposed || (value is not null && !_rows.Contains(value))) value = null;
            if (ReferenceEquals(_selected, value)) return;
            _selected = value;
            OnPropertyChanged(); OnPropertyChanged(nameof(SelectedOverlay)); OnPropertyChanged(nameof(SelectionSummary));
        }
    }

    public FrameOverlaySnapshot? SelectedOverlay => _selected?.Overlay;
    public string SelectionSummary => _selected is null ? "请选择一条结果。" :
        $"{_selected.Correlation.Kind} · {_selected.Decision} · {_selected.FrameMetadata.Width} × {_selected.FrameMetadata.Height} · " +
        $"{_selected.Result.OverlaySet.Primitives.Count} 个图元 · 原图未由此查询加载";

    public Task RefreshAsync() => LoadAsync(nextPage: false);
    public Task NextPageAsync() => LoadAsync(nextPage: true);

    private void InvalidateFilter()
    {
        _generation++; _cancellation?.Cancel(); _through = null; _next = null;
        _rows.Clear(); SelectedRecord = null; _status = "筛选已变化，请刷新结果。";
        NotifyState();
    }

    private async Task LoadAsync(bool nextPage)
    {
        AlgorithmResultFilter? filter = null;
        CancellationTokenSource? cancellation = null;
        long generation = 0;
        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed || _busy) return;
            if (_query is null) { _status = "结果归档未配置。"; NotifyState(); return; }
            if (nextPage && !_next.HasValue) return;
            ExecutionCorrelationId? correlation = null;
            if (_correlationText.Length != 0)
            {
                if (!Guid.TryParse(_correlationText, out var value) || value == Guid.Empty ||
                    _correlationKind is not (ExecutionKind.Manual or ExecutionKind.Qualification))
                {
                    _status = "请输入有效的执行关联 ID。"; NotifyState(); return;
                }
                correlation = new(_correlationKind, value);
            }
            filter = new(correlation, nextPage ? _next!.Value : 0, nextPage ? _through : null, _pageSize);
            cancellation = new(); _cancellation = cancellation;
            generation = ++_generation; _busy = true;
            _rows.Clear(); SelectedRecord = null; _status = "正在读取已验证结果…";
            NotifyState();
        });
        if (filter is null || cancellation is null) return;

        AlgorithmResultPage? page = null;
        try
        {
            // Opening a query never performs synchronous storage work on the Dispatcher.
            page = await Task.Run(async () => await _query!.QueryAsync(filter, cancellation.Token)
                .ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { /* No raw exception or untrusted query reason is presented. */ }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_cancellation, cancellation)) { _cancellation = null; _busy = false; }
                if (!_disposed && generation == _generation)
                {
                    if (page is not null && IsValidPage(page, filter))
                    {
                        _through = page.ThroughPosition; _next = page.NextAfterPosition;
                        foreach (var record in page.Records) _rows.Add(record);
                        SelectedRecord = _rows.FirstOrDefault();
                        _status = _rows.Count == 0 ? "未找到匹配的计算结果。" : $"已读取 {_rows.Count} 条结果。";
                    }
                    else
                    {
                        _through = null; _next = null;
                        _status = "结果历史不可用；未显示未经验证的数据。";
                    }
                }
                NotifyState();
            });
            cancellation.Dispose();
        }
    }

    private bool IsValidPage(AlgorithmResultPage page, AlgorithmResultFilter filter)
    {
        if (!page.Available || page.Records is null || page.Records.Count > _pageSize || page.ThroughPosition < 0 ||
            (filter.ThroughPosition.HasValue && page.ThroughPosition != filter.ThroughPosition.Value)) return false;
        var previous = filter.AfterPosition;
        var identities = new HashSet<Guid>();
        foreach (var record in page.Records)
        {
            if (record is null || record.Position <= previous || record.Position > page.ThroughPosition ||
                !identities.Add(record.RecordId) || !record.DevelopmentOnly ||
                record.Correlation.Kind == ExecutionKind.Production ||
                (filter.Correlation is not null && record.Correlation != filter.Correlation)) return false;
            previous = record.Position;
        }
        return !page.NextAfterPosition.HasValue || (page.Records.Count != 0 &&
            page.NextAfterPosition.Value == previous && previous < page.ThroughPosition);
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(HasNextPage)); OnPropertyChanged(nameof(StatusMessage));
        RefreshCommand.RaiseCanExecuteChanged(); NextPageCommand.RaiseCanExecuteChanged();
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
            _disposed = true; _generation++; _cancellation?.Cancel();
            _rows.Clear(); SelectedRecord = null; _through = null; _next = null;
            _status = "结果查看器已关闭。"; NotifyState();
        });
    }
}
