using System.Collections.ObjectModel;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Bounded, read-only presentation over the command trace query capability.
/// It never owns a store connection and never submits a Runtime command.
/// </summary>
public sealed class CommandTraceViewModel : ObservableObject, IAsyncDisposable
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxPageHistory = 256;

    private enum PageMove
    {
        Refresh,
        Next,
        Previous
    }

    private sealed class TraceQueryResultException : InvalidOperationException
    {
        public TraceQueryResultException(string safeCode) : base(safeCode) => SafeCode = safeCode;

        public string SafeCode { get; }
    }

    private readonly record struct QueryStart(
        long RequestVersion,
        long FilterVersion,
        CommandTraceFilter Filter,
        long AfterPosition,
        long? ThroughPosition,
        int PageNumber,
        PageMove Move,
        CancellationTokenSource Cancellation);

    private readonly ICommandTraceQuery? _query;
    private readonly int _pageSize;
    private readonly ObservableCollection<CommandTraceRecord> _rows = new();
    private readonly ReadOnlyObservableCollection<CommandTraceRecord> _readOnlyRows;
    private readonly object _sync = new();
    private readonly List<long> _pageStartPositions = new();
    private CancellationTokenSource? _activeCancellation;
    private long _requestVersion;
    private long _filterVersion;
    private long _loadedFilterVersion;
    private bool _isBusy;
    private bool _hasCurrentPage;
    private bool _disposed;
    private int _currentPage;
    private long? _throughPosition;
    private long? _nextAfterPosition;
    private bool _previousHistoryLimited;
    private string _correlationIdText = string.Empty;
    private string _claimedPrincipalIdText = string.Empty;
    private string _statusMessage;
    private string? _errorMessage;

    public CommandTraceViewModel(ICommandTraceQuery? query, int pageSize = DefaultPageSize)
    {
        if (pageSize < 1 || pageSize > MaxPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize), $"Page size must be between 1 and {MaxPageSize}.");

        _query = query;
        _pageSize = pageSize;
        _readOnlyRows = new ReadOnlyObservableCollection<CommandTraceRecord>(_rows);
        _statusMessage = query is null
            ? "追溯不可用：未配置只读查询服务。"
            : "尚未查询，请点击“刷新”。";

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => CanRefresh);
        PreviousPageCommand = new AsyncRelayCommand(() => PreviousPageAsync(), () => CanPreviousPage);
        NextPageCommand = new AsyncRelayCommand(() => NextPageAsync(), () => CanNextPage);
    }

    public ReadOnlyObservableCollection<CommandTraceRecord> Rows => _readOnlyRows;

    public string CorrelationIdText
    {
        get => _correlationIdText;
        set => SetFilterText(ref _correlationIdText, value, nameof(CorrelationIdText));
    }

    public string ClaimedPrincipalIdText
    {
        get => _claimedPrincipalIdText;
        set => SetFilterText(ref _claimedPrincipalIdText, value, nameof(ClaimedPrincipalIdText));
    }

    public string AuthenticationNotice { get; } =
        "声称主体仅是命令输入归因，未经认证；不等同于 Human Principal。Accepted 仅表示 Runtime 受理，不表示操作已完成。";

    public bool IsTraceAvailable => _query is not null;
    public bool IsUnavailable => !IsTraceAvailable;

    public bool IsBusy
    {
        get { lock (_sync) return _isBusy; }
    }

    public bool HasCurrentPage
    {
        get { lock (_sync) return _hasCurrentPage; }
    }

    public bool HasRows
    {
        get { lock (_sync) return _rows.Count != 0; }
    }

    public bool HasError
    {
        get { lock (_sync) return _errorMessage is not null; }
    }

    public bool IsPreviousHistoryLimited
    {
        get { lock (_sync) return _previousHistoryLimited; }
    }

    public int CurrentPage
    {
        get { lock (_sync) return _currentPage; }
    }

    public int PageNumber => CurrentPage;

    /// <summary>The fixed upper Position used by the current stable query view.</summary>
    public long? ThroughPosition
    {
        get { lock (_sync) return _throughPosition; }
    }

    public long? SnapshotUpperBound => ThroughPosition;

    public string StatusMessage
    {
        get { lock (_sync) return _statusMessage; }
    }

    public string? ErrorMessage
    {
        get { lock (_sync) return _errorMessage; }
    }

    /// <summary>Refreshing may replace an in-flight read; page navigation cannot.</summary>
    public bool CanRefresh => IsTraceAvailable && !_disposed;

    public bool CanPreviousPage
    {
        get
        {
            lock (_sync)
                return !_isBusy && _hasCurrentPage && _pageStartPositions.Count > 1;
        }
    }

    public bool CanNextPage
    {
        get
        {
            lock (_sync)
                return !_isBusy && _hasCurrentPage && _nextAfterPosition.HasValue &&
                    _loadedFilterVersion == _filterVersion;
        }
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand PreviousPageCommand { get; }
    public AsyncRelayCommand NextPageCommand { get; }

    public Task StartAsync(CancellationToken cancellationToken = default) => RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var start = BeginQuery(PageMove.Refresh, cancellationToken);
        if (!start.HasValue) return;
        await ExecuteQueryAsync(start.Value).ConfigureAwait(true);
    }

    public async Task NextPageAsync(CancellationToken cancellationToken = default)
    {
        QueryStart? start;
        lock (_sync)
        {
            if (_disposed || _query is null || _isBusy || !_hasCurrentPage ||
                !_nextAfterPosition.HasValue || _pageStartPositions.Count == 0)
                return;

            var afterPosition = _nextAfterPosition.Value;
            start = BeginQueryLocked(PageMove.Next, afterPosition, _throughPosition,
                _currentPage + 1, cancellationToken);
        }

        if (start.HasValue)
            await ExecuteQueryAsync(start.Value).ConfigureAwait(true);
    }

    public async Task PreviousPageAsync(CancellationToken cancellationToken = default)
    {
        QueryStart? start;
        lock (_sync)
        {
            if (_disposed || _query is null || _isBusy || !_hasCurrentPage ||
                _pageStartPositions.Count < 2)
                return;

            var previousStart = _pageStartPositions[^2];
            start = BeginQueryLocked(PageMove.Previous, previousStart, _throughPosition,
                _currentPage - 1, cancellationToken);
        }

        if (start.HasValue)
            await ExecuteQueryAsync(start.Value).ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _requestVersion++;
            cancellation = _activeCancellation;
            _activeCancellation = null;
            _isBusy = false;
            ResetPageLocked();
        }

        cancellation?.Cancel();
        NotifyStateChanged();
        await Task.CompletedTask;
    }

    private void SetFilterText(ref string field, string? value, string propertyName)
    {
        value ??= string.Empty;
        CancellationTokenSource? cancellation = null;
        bool changed;
        lock (_sync)
        {
            changed = !string.Equals(field, value, StringComparison.Ordinal);
            if (!changed) return;

            field = value;
            _filterVersion++;
            _requestVersion++;
            cancellation = _activeCancellation;
            _activeCancellation = null;
            _isBusy = false;
            ResetPageLocked();
            _statusMessage = IsTraceAvailable
                ? "筛选已修改，请点击“刷新”。"
                : "追溯不可用：未配置只读查询服务。";
            _errorMessage = null;
        }

        cancellation?.Cancel();
        OnPropertyChanged(propertyName);
        NotifyStateChanged();
    }

    private QueryStart? BeginQuery(PageMove move, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_disposed || _query is null) return null;

            if (!TryBuildFilterLocked(out _, out var validationError))
            {
                _requestVersion++;
                _activeCancellation?.Cancel();
                _activeCancellation = null;
                _isBusy = false;
                ResetPageLocked();
                _errorMessage = validationError;
                _statusMessage = "筛选无效，请修正后刷新。";
                NotifyStateChanged();
                return null;
            }

            return BeginQueryLocked(move, 0, null, 1, cancellationToken);
        }
    }

    private QueryStart? BeginQueryLocked(PageMove move, long afterPosition, long? throughPosition,
        int pageNumber, CancellationToken cancellationToken)
    {
        if (_disposed || _query is null || (move != PageMove.Refresh && _isBusy)) return null;
        if (!TryBuildFilterLocked(out var currentFilter, out var validationError))
        {
            _requestVersion++;
            _activeCancellation?.Cancel();
            _activeCancellation = null;
            _isBusy = false;
            ResetPageLocked();
            _errorMessage = validationError;
            _statusMessage = "筛选无效，请修正后刷新。";
            return null;
        }

        // A refresh uses the filter parsed while holding the same lock as the request
        // version. Navigation keeps the current identity filters while replacing only
        // the keyset cursor and the stable upper bound.
        var filter = currentFilter with { AfterPosition = afterPosition, ThroughPosition = throughPosition };
        var previousCancellation = _activeCancellation;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeCancellation = cancellation;
        _requestVersion++;
        _isBusy = true;
        _hasCurrentPage = false;
        _rows.Clear();
        _currentPage = 0;
        _throughPosition = move == PageMove.Refresh ? null : throughPosition;
        _nextAfterPosition = null;
        if (move == PageMove.Refresh)
        {
            _pageStartPositions.Clear();
            _previousHistoryLimited = false;
        }
        _errorMessage = null;
        _statusMessage = "正在查询追溯记录…";
        var start = new QueryStart(_requestVersion, _filterVersion, filter, afterPosition,
            throughPosition, pageNumber, move, cancellation);
        NotifyStateChanged();
        previousCancellation?.Cancel();
        return start;
    }

    private async Task ExecuteQueryAsync(QueryStart start)
    {
        try
        {
            CommandTracePage page;
            try
            {
                page = await _query!.QueryAsync(start.Filter, start.Cancellation.Token).ConfigureAwait(true);
                start.Cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (start.Cancellation.IsCancellationRequested)
            {
                CompleteCancellation(start);
                return;
            }
            catch
            {
                CompleteFailure(start, "追溯查询失败，请检查存储后重试。", "TraceQueryFailed");
                return;
            }

            try
            {
                ApplyPage(start, page);
            }
            catch (TraceQueryResultException exception)
            {
                var status = exception.SafeCode == "TraceQuerySnapshotExpired"
                    ? "追溯快照已过期，请点击“刷新”。"
                    : "追溯查询结果无效，请刷新后重试。";
                CompleteFailure(start, status, exception.SafeCode);
            }
            catch
            {
                CompleteFailure(start, "追溯查询结果无效，请刷新后重试。", "TraceQueryPageInvalid");
            }
        }
        finally
        {
            CompleteQuery(start);
        }
    }

    private void ApplyPage(QueryStart start, CommandTracePage page)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            if (page.Records is null || page.Records.Count > _pageSize || page.ThroughPosition < 0 ||
                (page.NextAfterPosition.HasValue &&
                    (page.NextAfterPosition.Value <= start.AfterPosition ||
                     page.NextAfterPosition.Value > page.ThroughPosition)) ||
                (!page.Records.Any() && page.NextAfterPosition.HasValue))
                throw new TraceQueryResultException("TraceQueryPageInvalid");

            if (start.Move != PageMove.Refresh &&
                (!_throughPosition.HasValue || page.ThroughPosition != start.ThroughPosition))
                throw new TraceQueryResultException("TraceQuerySnapshotExpired");

            _rows.Clear();
            foreach (var row in page.Records) _rows.Add(row);

            if (start.Move == PageMove.Refresh)
            {
                _pageStartPositions.Clear();
                _pageStartPositions.Add(0);
            }
            else if (start.Move == PageMove.Next)
            {
                if (_pageStartPositions.Count == 0 || _pageStartPositions[^1] != start.AfterPosition)
                    _pageStartPositions.Add(start.AfterPosition);
                if (_pageStartPositions.Count > MaxPageHistory)
                {
                    _pageStartPositions.RemoveAt(0);
                    _previousHistoryLimited = true;
                }
            }
            else if (_pageStartPositions.Count > 1)
            {
                _pageStartPositions.RemoveAt(_pageStartPositions.Count - 1);
            }

            _currentPage = start.PageNumber;
            _throughPosition = page.ThroughPosition;
            _nextAfterPosition = page.NextAfterPosition;
            _loadedFilterVersion = start.FilterVersion;
            _hasCurrentPage = true;
            _errorMessage = null;
            _statusMessage = BuildPageStatusLocked();
            NotifyStateChanged();
        }
    }

    private void CompleteFailure(QueryStart start, string status, string errorCode)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            ResetPageLocked();
            _errorMessage = $"{status}（{errorCode}）";
            _statusMessage = status;
            NotifyStateChanged();
        }
    }

    private void CompleteCancellation(QueryStart start)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            ResetPageLocked();
            _errorMessage = null;
            _statusMessage = "查询已取消，可重新刷新。";
            NotifyStateChanged();
        }
    }

    private void CompleteQuery(QueryStart start)
    {
        bool changed = false;
        lock (_sync)
        {
            if (IsCurrentLocked(start))
            {
                _isBusy = false;
                _activeCancellation = null;
                changed = true;
            }
        }

        start.Cancellation.Dispose();
        if (changed) NotifyStateChanged();
    }

    private bool IsCurrentLocked(QueryStart start) =>
        !_disposed && _requestVersion == start.RequestVersion &&
        _filterVersion == start.FilterVersion &&
        ReferenceEquals(_activeCancellation, start.Cancellation);

    private bool TryBuildFilterLocked(out CommandTraceFilter filter, out string? error)
    {
        var correlationText = _correlationIdText.Trim();
        if (correlationText.Length != 0 && !Guid.TryParse(correlationText, out var correlationId))
        {
            filter = default!;
            error = "相关 Guid 无效：请输入完整的 GUID。";
            return false;
        }

        Guid? parsedCorrelation = correlationText.Length == 0 ? null : Guid.Parse(correlationText);
        var principal = _claimedPrincipalIdText.Trim();
        filter = new CommandTraceFilter(parsedCorrelation,
            principal.Length == 0 ? null : principal, 0, null, _pageSize);
        error = null;
        return true;
    }

    private void ResetPageLocked()
    {
        _rows.Clear();
        _pageStartPositions.Clear();
        _hasCurrentPage = false;
        _currentPage = 0;
        _throughPosition = null;
        _nextAfterPosition = null;
        _previousHistoryLimited = false;
        _loadedFilterVersion = -1;
    }

    private string BuildPageStatusLocked()
    {
        var status = $"已载入第 {_currentPage} 页 · 快照上界 {_throughPosition!.Value}。";
        return _previousHistoryLimited
            ? $"{status} 早期上一页游标已回收，可点击“刷新”重新查询。"
            : status;
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(HasCurrentPage));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsPreviousHistoryLimited));
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(PageNumber));
        OnPropertyChanged(nameof(ThroughPosition));
        OnPropertyChanged(nameof(SnapshotUpperBound));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(CanPreviousPage));
        OnPropertyChanged(nameof(CanNextPage));
        RefreshCommand?.RaiseCanExecuteChanged();
        PreviousPageCommand?.RaiseCanExecuteChanged();
        NextPageCommand?.RaiseCanExecuteChanged();
    }
}
