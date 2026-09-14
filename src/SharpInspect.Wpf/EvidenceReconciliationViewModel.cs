using System.Collections.ObjectModel;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>Read-only, bounded evidence history. Deactivation invalidates even uncancellable late reads.</summary>
public sealed class EvidenceReconciliationViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IEvidenceReconciliationQuery? _query;
    private readonly IUiDispatcher _dispatcher;
    private readonly int _pageSize;
    private readonly ObservableCollection<EvidenceReconciliationRecord> _rows = new();
    private CancellationTokenSource? _cancellation;
    private long _generation;
    private long _after;
    private long _watermark;
    private bool _next;
    private bool _busy;
    private bool _disposed;
    private EvidenceReconciliationSnapshot? _snapshot;
    private string _status;
    private EvidenceReconciliationRecord? _selected;

    public EvidenceReconciliationViewModel(IEvidenceReconciliationQuery? query,
        IUiDispatcher? dispatcher = null, int pageSize = 64)
    {
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
        _query = query; _dispatcher = dispatcher ?? new DispatcherUiDispatcher(); _pageSize = pageSize;
        _status = query is null ? "证据核对查询未配置。" : "尚未读取证据核对记录。";
        Rows = new ReadOnlyObservableCollection<EvidenceReconciliationRecord>(_rows);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !_disposed && !_busy && _query is not null);
        NextPageCommand = new AsyncRelayCommand(NextPageAsync, () => !_disposed && !_busy && _next);
    }
    public ReadOnlyObservableCollection<EvidenceReconciliationRecord> Rows { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand NextPageCommand { get; }
    public bool IsBusy => _busy;
    public bool HasNextPage => _next;
    public string StatusMessage => _status;
    public string StartupSummary => Progress("最近启动核对", _snapshot?.LatestStartup);
    public string ScrubberSummary => Progress("历史巡检", _snapshot?.Scrubber);
    public string IntegritySummary => _snapshot is null ? "尚无已读取的核对结论。" :
        _snapshot.IntegrityFaultRecorded ? "已记录完整性故障，生产准入应保持关闭。" :
        _snapshot.PendingQuarantines > 0 ? $"仍有 {_snapshot.PendingQuarantines} 份文件等待确认隔离。" :
        "当前核对账本没有已记录的完整性故障。";
    public EvidenceReconciliationRecord? SelectedRecord
    {
        get => _selected;
        set
        {
            EnsureUi();
            if (_disposed || value is not null && !_rows.Contains(value)) value = null;
            if (ReferenceEquals(_selected, value)) return;
            _selected = value; OnPropertyChanged(); OnPropertyChanged(nameof(SelectionSummary));
        }
    }
    public string SelectionSummary => _selected is not { } row ? "请选择一条核对事实。" :
        $"{KindLabel(row.Kind)} · {row.ReasonCode}\n运行 {row.RunId} · 运行时 {row.RuntimeEpoch}\n" +
        $"检测 {row.Subject?.InspectionId?.ToString() ?? "—"} · 孤儿 {row.Subject?.OrphanId?.ToString() ?? "—"}\n" +
        $"源文件 {row.Subject?.SourceFileName ?? "—"} · 隔离文件 {row.Subject?.QuarantineFileName ?? "—"}\n" +
        $"内容哈希 {row.Subject?.ObservedContentHash ?? "未观察"}\n记录哈希 {row.ContentHash} · 审计序号 {row.AuditSequence}";
    public static string KindLabel(EvidenceReconciliationEventKind kind) => kind switch
    {
        EvidenceReconciliationEventKind.RunStarted => "核对开始",
        EvidenceReconciliationEventKind.ImageVerified => "图片已核验",
        EvidenceReconciliationEventKind.ImageFinalRecovered => "最终图片已接续",
        EvidenceReconciliationEventKind.OutboxVerified => "交付本地证据已核验",
        EvidenceReconciliationEventKind.QuarantineIntent => "已记录隔离意图",
        EvidenceReconciliationEventKind.Quarantined => "文件已隔离",
        EvidenceReconciliationEventKind.IntegrityFault => "完整性故障",
        EvidenceReconciliationEventKind.PageCompleted => "分页进度已保存",
        EvidenceReconciliationEventKind.RunCompleted => "本次核对已完成",
        EvidenceReconciliationEventKind.WorkDeferred => "本项延期核验",
        _ => "未知事实"
    };
    private static string Progress(string title, EvidenceReconciliationProgress? progress) => progress is null
        ? title + "：尚无已提交记录。"
        : $"{title}：{(progress.IntegrityFault ? "故障" : progress.Completed ? "已完成" : "进行中")}；" +
          $"已检查 {progress.ScannedItems}，已核验 {progress.VerifiedItems}，延期 {progress.DeferredItems}；" +
          $"游标 {progress.AfterSourcePosition}/{progress.ThroughSourcePosition}。";
    public Task RefreshAsync() => LoadAsync(false);
    public Task NextPageAsync() => LoadAsync(true);
    public void Deactivate()
    {
        EnsureUi();
        Invalidate("页面已离开；重新进入后读取最新记录。");
    }
    private void Invalidate(string message)
    {
        _generation++; _cancellation?.Cancel();
        _rows.Clear(); _snapshot = null; _after = _watermark = 0; _next = false;
        SelectedRecord = null; _status = message; Notify();
    }
    private async Task LoadAsync(bool next)
    {
        EvidenceReconciliationFilter? filter = null;
        CancellationTokenSource? cancellation = null;
        long generation = 0;
        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed || _busy || next && !_next || _query is null) return;
            if (!next) _watermark = 0;
            filter = new(next ? _after : 0, _pageSize);
            cancellation = new(); _cancellation = cancellation;
            generation = ++_generation; _busy = true; _rows.Clear(); SelectedRecord = null;
            _snapshot = null; _status = "正在读取已提交的核对事实…"; Notify();
        });
        if (filter is null || cancellation is null) return;
        EvidenceReconciliationSnapshot? snapshot = null;
        try
        {
            snapshot = await Task.Run(async () => await _query!.ReadAsync(filter, cancellation.Token)
                .ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_cancellation, cancellation)) { _cancellation = null; _busy = false; }
                if (!_disposed && generation == _generation)
                {
                    if (snapshot is not null && Valid(snapshot, filter))
                    {
                        _snapshot = snapshot; _watermark = snapshot.ThroughAuditSequence;
                        _after = snapshot.NextPosition; _next = snapshot.HasMore;
                        foreach (var row in snapshot.Records) _rows.Add(row);
                        SelectedRecord = _rows.FirstOrDefault();
                        _status = $"本页 {_rows.Count} 条 · 已验证审计水位 {_watermark}。";
                    }
                    else { _next = false; _snapshot = null; _status = "核对证据不可用，请检查存储与审计状态。"; }
                }
                Notify();
            });
            cancellation.Dispose();
        }
    }
    private bool Valid(EvidenceReconciliationSnapshot snapshot, EvidenceReconciliationFilter filter)
    {
        if (!snapshot.Available || snapshot.Records.Count > _pageSize || snapshot.ThroughAuditSequence < _watermark ||
            snapshot.HasMore && snapshot.Records.Count == 0 || snapshot.PendingQuarantines < 0 ||
            !ValidProgress(snapshot.LatestStartup, EvidenceReconciliationPhase.Startup, snapshot.ThroughAuditSequence) ||
            !ValidProgress(snapshot.Scrubber, EvidenceReconciliationPhase.HistoricalScrub, snapshot.ThroughAuditSequence)) return false;
        var position = filter.AfterPosition; long audit = 0; var seen = new HashSet<Guid>();
        foreach (var row in snapshot.Records)
        {
            if (row is null || row.Position <= position || row.AuditSequence <= audit ||
                row.AuditSequence > snapshot.ThroughAuditSequence || row.EventId == Guid.Empty || row.RunId == Guid.Empty ||
                row.RuntimeEpoch == Guid.Empty || !seen.Add(row.EventId) || !Enum.IsDefined(row.Kind) ||
                !Enum.IsDefined(row.Phase) || !Hash(row.ContentHash) || row.RecordedAtUtc == default ||
                row.RecordedAtUtc.Offset != TimeSpan.Zero || string.IsNullOrWhiteSpace(row.ReasonCode) ||
                row.ReasonCode.Length > 128 || !ValidSubject(row.Subject) ||
                (row.Kind is EvidenceReconciliationEventKind.RunStarted or EvidenceReconciliationEventKind.PageCompleted or
                    EvidenceReconciliationEventKind.RunCompleted) != (row.Subject is null)) return false;
            position = row.Position; audit = row.AuditSequence;
        }
        return snapshot.NextPosition == position;
    }
    private static bool ValidSubject(EvidenceReconciliationSubject? subject)
    {
        if (subject is null) return true;
        if (!Enum.IsDefined(subject.Kind) || subject.FileArea is { } area && !Enum.IsDefined(area) ||
            subject.ByteLength is < 0 || subject.SourceFileName?.Length > 255 || subject.QuarantineFileName?.Length > 255) return false;
        return subject.Kind != EvidenceReconciliationSubjectKind.Orphan ||
            subject.OrphanId is { } orphan && orphan != Guid.Empty && subject.InspectionId is null &&
            subject.ManifestId is null && subject.WorkId is null && subject.DeliveryId is null;
    }
    private static bool ValidProgress(EvidenceReconciliationProgress? progress, EvidenceReconciliationPhase phase, long audit) =>
        progress is null || progress.RunId != Guid.Empty && progress.Phase == phase &&
        progress.AfterSourcePosition >= 0 && progress.ThroughSourcePosition >= progress.AfterSourcePosition &&
        progress.ScannedItems >= 0 && progress.VerifiedItems >= 0 && progress.DeferredItems >= 0 &&
        progress.VerifiedItems <= progress.ScannedItems && progress.DeferredItems <= progress.ScannedItems - progress.VerifiedItems && progress.VerifiedBytes >= 0 &&
        progress.LastEventPosition > 0 && progress.LastEventPosition <= audit && Hash(progress.LastEventContentHash) &&
        progress.LastRecordedAtUtc != default && progress.LastRecordedAtUtc.Offset == TimeSpan.Zero &&
        (phase != EvidenceReconciliationPhase.HistoricalScrub || !progress.Completed ||
            progress.AfterSourcePosition == progress.ThroughSourcePosition);
    private static bool Hash(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private void Notify()
    {
        foreach (var name in new[] { nameof(IsBusy), nameof(HasNextPage), nameof(StatusMessage),
            nameof(StartupSummary), nameof(ScrubberSummary), nameof(IntegritySummary) }) OnPropertyChanged(name);
        RefreshCommand.RaiseCanExecuteChanged(); NextPageCommand.RaiseCanExecuteChanged();
    }
    private void EnsureUi()
    { if (!_dispatcher.CheckAccess) throw new PresentationStateUnavailableException(); }
    public async ValueTask DisposeAsync() => await _dispatcher.InvokeAsync(() =>
    {
        if (_disposed) return;
        _disposed = true; Invalidate("核对记录查看器已关闭。");
    });
}
