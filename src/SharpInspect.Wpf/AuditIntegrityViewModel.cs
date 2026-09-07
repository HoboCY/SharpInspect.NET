using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Bounded, read-only presentation over the optional audit-integrity query.
/// It owns no store connection, signing key, network client, or Runtime command.
/// </summary>
public sealed class AuditIntegrityViewModel : ObservableObject, IAsyncDisposable
{
    public const int MaximumEntries = 200;

    private sealed class AuditIntegrityResultException : InvalidOperationException
    {
        public AuditIntegrityResultException(string safeCode) : base(safeCode) => SafeCode = safeCode;

        public string SafeCode { get; }
    }

    private readonly record struct QueryStart(
        long RequestVersion,
        long AfterSequence,
        long? ExpectedThroughSequence,
        bool IsNextSegment,
        CancellationTokenSource Cancellation);

    private readonly IAuditIntegrityQuery? _query;
    private readonly object _sync = new();
    private CancellationTokenSource? _activeCancellation;
    private AuditIntegrityReport? _currentReport;
    private AuditIntegrityState _state;
    private string _statusMessage;
    private string? _errorMessage;
    private string? _reasonCode;
    private string? _stationId;
    private string? _policyVersion;
    private long? _throughSequence;
    private long? _verifiedFromSequence;
    private long? _verifiedThroughSequence;
    private long? _checkpointSequence;
    private long? _anchoredSequence;
    private long? _nextAfterSequence;
    private int _segmentCount;
    private long _requestVersion;
    private bool _isBusy;
    private bool _disposed;

    public AuditIntegrityViewModel(IAuditIntegrityQuery? query)
    {
        _query = query;
        _state = AuditIntegrityState.NotConfigured;
        _statusMessage = query is null
            ? "审计完整性不可用：未配置只读查询服务。"
            : "尚未验证，请点击“刷新验证”。";

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => CanRefresh);
        NextSegmentCommand = new AsyncRelayCommand(() => NextSegmentAsync(), () => CanNextSegment);
    }

    public const string LimitationNoticeText = "当前为有界、只读验证；部分验证不授予 Ready。";

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand NextSegmentCommand { get; }

    public bool IsConfigured => _query is not null;

    public AuditIntegrityState State
    {
        get { lock (_sync) return _state; }
    }

    public string StateLabel
    {
        get
        {
            lock (_sync)
            {
                if (_query is null) return "未配置";
                if (_isBusy) return "验证中";
                if (_currentReport is null) return "尚未验证";
                return _state switch
                {
                    AuditIntegrityState.NotConfigured => "未配置",
                    AuditIntegrityState.Verifying => "验证中",
                    AuditIntegrityState.Verified => "已验证（有界）",
                    AuditIntegrityState.Faulted => "故障",
                    _ => "状态未知"
                };
            }
        }
    }

    public bool IsBusy
    {
        get { lock (_sync) return _isBusy; }
    }

    public bool HasReport
    {
        get { lock (_sync) return _currentReport is not null; }
    }

    public bool IsVerified
    {
        get { lock (_sync) return _currentReport is not null && _state == AuditIntegrityState.Verified; }
    }

    public bool IsFaulted
    {
        get { lock (_sync) return _currentReport is not null && _state == AuditIntegrityState.Faulted; }
    }

    public bool HasError
    {
        get { lock (_sync) return _errorMessage is not null; }
    }

    public bool CanRefresh => IsConfigured && !_disposed;

    public bool CanNextSegment
    {
        get
        {
            lock (_sync)
            {
                return !_disposed && !_isBusy && _currentReport is not null &&
                    _state == AuditIntegrityState.Verified && _nextAfterSequence.HasValue &&
                    _throughSequence.HasValue;
            }
        }
    }

    public string StatusMessage
    {
        get { lock (_sync) return _statusMessage; }
    }

    public string? ErrorMessage
    {
        get { lock (_sync) return _errorMessage; }
    }

    public string LimitationNotice => LimitationNoticeText;

    public AuditIntegrityReport? CurrentReport
    {
        get { lock (_sync) return _currentReport; }
    }

    public string? ReasonCode
    {
        get { lock (_sync) return _reasonCode; }
    }

    public string? StationId
    {
        get { lock (_sync) return _stationId; }
    }

    public string? PolicyVersion
    {
        get { lock (_sync) return _policyVersion; }
    }

    public long? ThroughSequence
    {
        get { lock (_sync) return _throughSequence; }
    }

    public long? VerifiedFromSequence
    {
        get { lock (_sync) return _verifiedFromSequence; }
    }

    public long? VerifiedThroughSequence
    {
        get { lock (_sync) return _verifiedThroughSequence; }
    }

    public long? CheckpointSequence
    {
        get { lock (_sync) return _checkpointSequence; }
    }

    public long? AnchoredSequence
    {
        get { lock (_sync) return _anchoredSequence; }
    }

    public int SegmentCount
    {
        get { lock (_sync) return _segmentCount; }
    }

    public string SummaryText
    {
        get
        {
            lock (_sync)
            {
                if (_currentReport is null)
                    return "已检查范围：尚未建立 · 总上界：未建立 · checkpoint：未提供 · anchor：未提供";

                var from = _verifiedFromSequence?.ToString() ?? "未建立";
                var through = _verifiedThroughSequence?.ToString() ?? "未建立";
                var upper = _throughSequence?.ToString() ?? "未建立";
                var checkpoint = _checkpointSequence?.ToString() ?? "未提供";
                var anchor = _anchoredSequence?.ToString() ?? "未提供";
                var station = string.IsNullOrWhiteSpace(_stationId) ? "未提供" : _stationId;
                var policy = string.IsNullOrWhiteSpace(_policyVersion) ? "未提供" : _policyVersion;
                return $"状态：{StateLabel} · 已检查范围：{from}–{through} / 总上界 {upper} · " +
                    $"checkpoint：{checkpoint} · anchor：{anchor} · 工位：{station} · 策略：{policy} · 段数：{_segmentCount}";
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var start = BeginQuery(isNextSegment: false, cancellationToken);
        if (!start.HasValue) return;
        await ExecuteQueryAsync(start.Value).ConfigureAwait(true);
    }

    public async Task NextSegmentAsync(CancellationToken cancellationToken = default)
    {
        var start = BeginQuery(isNextSegment: true, cancellationToken);
        if (!start.HasValue) return;
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
            ResetReportLocked();
            _state = AuditIntegrityState.NotConfigured;
            _statusMessage = "审计完整性查询已释放。";
            _errorMessage = null;
        }

        cancellation?.Cancel();
        NotifyStateChanged();
        cancellation?.Dispose();
        await Task.CompletedTask;
    }

    private QueryStart? BeginQuery(bool isNextSegment, CancellationToken cancellationToken)
    {
        CancellationTokenSource? previousCancellation;
        QueryStart start;
        lock (_sync)
        {
            if (_disposed || _query is null || (isNextSegment &&
                (_isBusy || _currentReport is null || _state != AuditIntegrityState.Verified ||
                 !_nextAfterSequence.HasValue || !_throughSequence.HasValue)))
                return null;

            var afterSequence = isNextSegment ? _nextAfterSequence!.Value : 0;
            var expectedThroughSequence = isNextSegment ? _throughSequence : null;
            previousCancellation = _activeCancellation;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCancellation = cancellation;
            _requestVersion++;
            _isBusy = true;
            _errorMessage = null;
            _state = AuditIntegrityState.Verifying;
            if (!isNextSegment)
            {
                ResetReportLocked();
                _statusMessage = $"正在验证审计链（请求 {MaximumEntries} 条，可信前缀计入政策预算）…";
            }
            else
            {
                _statusMessage = $"正在继续验证审计链（从序号 {afterSequence} 请求 {MaximumEntries} 条，可能回溯可信前缀）…";
            }

            start = new QueryStart(_requestVersion, afterSequence, expectedThroughSequence,
                isNextSegment, cancellation);
        }

        previousCancellation?.Cancel();
        NotifyStateChanged();
        return start;
    }

    private async Task ExecuteQueryAsync(QueryStart start)
    {
        try
        {
            AuditIntegrityReport report;
            try
            {
                report = await _query!.VerifyAsync(
                    new AuditVerificationRequest(start.AfterSequence, MaximumEntries),
                    start.Cancellation.Token).ConfigureAwait(true);
                start.Cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (start.Cancellation.IsCancellationRequested)
            {
                CompleteCancellation(start);
                return;
            }
            catch
            {
                CompleteFailure(start, "审计完整性查询失败，请检查存储后刷新。", "AuditIntegrityQueryFailed");
                return;
            }

            try
            {
                ApplyReport(start, report);
            }
            catch (AuditIntegrityResultException exception)
            {
                var status = exception.SafeCode == "AuditIntegritySnapshotExpired"
                    ? "审计快照已过期，请点击“刷新验证”。"
                    : "审计完整性报告无效，请刷新后重试。";
                CompleteFailure(start, status, exception.SafeCode);
            }
            catch
            {
                CompleteFailure(start, "审计完整性报告无效，请刷新后重试。", "AuditIntegrityReportInvalid");
            }
        }
        finally
        {
            CompleteQuery(start);
        }
    }

    private void ApplyReport(QueryStart start, AuditIntegrityReport report)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            ValidateReport(report);

            if (start.IsNextSegment && report.State == AuditIntegrityState.Verified)
            {
                if (!_throughSequence.HasValue || report.ThroughSequence != start.ExpectedThroughSequence)
                    throw new AuditIntegrityResultException("AuditIntegritySnapshotExpired");
                if (!_verifiedThroughSequence.HasValue ||
                    report.VerifiedThroughSequence <= _verifiedThroughSequence.Value)
                    throw new AuditIntegrityResultException("AuditIntegrityReportInvalid");
                _verifiedThroughSequence = report.VerifiedThroughSequence;
                _segmentCount++;
            }
            else
            {
                _throughSequence = report.ThroughSequence;
                _verifiedFromSequence = report.VerifiedFromSequence;
                _verifiedThroughSequence = report.VerifiedThroughSequence;
                _segmentCount = 1;
            }

            _currentReport = report;
            _state = report.State;
            _reasonCode = report.ReasonCode;
            _stationId = report.StationId;
            _policyVersion = report.PolicyVersion;
            _checkpointSequence = report.CheckpointSequence;
            _anchoredSequence = report.AnchoredSequence;
            _nextAfterSequence = report.State == AuditIntegrityState.Verified &&
                _throughSequence.HasValue && _verifiedThroughSequence < _throughSequence
                ? _verifiedThroughSequence
                : null;
            _errorMessage = null;
            _statusMessage = BuildReportStatusLocked();
            NotifyStateChanged();
        }
    }

    private static void ValidateReport(AuditIntegrityReport report)
    {
        if (report is null || !Enum.IsDefined(typeof(AuditIntegrityState), report.State) || report.ThroughSequence < 0 ||
            report.VerifiedFromSequence < 0 || report.VerifiedThroughSequence < report.VerifiedFromSequence ||
            report.VerifiedThroughSequence > report.ThroughSequence)
            throw new AuditIntegrityResultException("AuditIntegrityReportInvalid");
    }

    private string BuildReportStatusLocked()
    {
        return _state switch
        {
            AuditIntegrityState.Verified => _nextAfterSequence.HasValue
                ? $"已验证（有界）：已检查至 {_verifiedThroughSequence}，快照上界 {_throughSequence}，可继续下一段。"
                : $"已验证（有界）：已检查至 {_verifiedThroughSequence}，快照上界 {_throughSequence}。",
            AuditIntegrityState.Faulted => $"审计完整性故障：{_reasonCode ?? "未提供原因码"}。",
            AuditIntegrityState.NotConfigured => "审计完整性不可用：查询服务报告未配置。",
            AuditIntegrityState.Verifying => "正在验证审计链…",
            _ => "审计完整性状态未知。"
        };
    }

    private void CompleteFailure(QueryStart start, string status, string errorCode)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            ResetReportLocked();
            _state = AuditIntegrityState.Faulted;
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
            ResetReportLocked();
            _state = AuditIntegrityState.NotConfigured;
            _errorMessage = null;
            _statusMessage = "审计验证已取消，可重新刷新。";
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
        ReferenceEquals(_activeCancellation, start.Cancellation);

    private void ResetReportLocked()
    {
        _currentReport = null;
        _reasonCode = null;
        _stationId = null;
        _policyVersion = null;
        _throughSequence = null;
        _verifiedFromSequence = null;
        _verifiedThroughSequence = null;
        _checkpointSequence = null;
        _anchoredSequence = null;
        _nextAfterSequence = null;
        _segmentCount = 0;
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(IsVerified));
        OnPropertyChanged(nameof(IsFaulted));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(CurrentReport));
        OnPropertyChanged(nameof(ReasonCode));
        OnPropertyChanged(nameof(StationId));
        OnPropertyChanged(nameof(PolicyVersion));
        OnPropertyChanged(nameof(ThroughSequence));
        OnPropertyChanged(nameof(VerifiedFromSequence));
        OnPropertyChanged(nameof(VerifiedThroughSequence));
        OnPropertyChanged(nameof(CheckpointSequence));
        OnPropertyChanged(nameof(AnchoredSequence));
        OnPropertyChanged(nameof(SegmentCount));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanNextSegment));
        RefreshCommand.RaiseCanExecuteChanged();
        NextSegmentCommand.RaiseCanExecuteChanged();
    }
}
