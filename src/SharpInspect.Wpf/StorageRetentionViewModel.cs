using System.Collections.ObjectModel;
using System.Globalization;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Bounded operator view over verified trace-storage capacity and evidence-retention facts.
/// Reads never grant authority: every hold/release/extend still passes the Runtime's own
/// authorization and a mandatory fresh Step-Up bound to the exact command payload.
/// </summary>
public sealed partial class StorageRetentionViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaximumReasonLength = 512;
    private const int MaximumExtensionLength = 64;
    private readonly ITraceStorageCapacityQuery? _capacity;
    private readonly IEvidenceRetentionService? _retention;
    private readonly IInteractiveSessionService? _sessions;
    private readonly IStepUpAuthentication? _stepUp;
    private readonly IUiDispatcher _dispatcher;
    private readonly int _pageSize;
    private readonly ObservableCollection<EvidenceRetentionStatus> _subjects = new();
    private readonly ObservableCollection<EvidenceRetentionRecord> _history = new();
    private CancellationTokenSource? _cancellation;
    private long _generation;
    private long _after;
    private long? _through;
    private bool _next;
    private bool _busy;
    private bool _submittedChange;
    private bool _disposed;
    private string _status;
    private string _capacityText;
    private string _policyText;
    private EvidenceRetentionStatus? _selected;
    private string _reasonText = string.Empty;
    private Guid? _selectedHoldId;
    private string _extendedUntilText = string.Empty;

    public StorageRetentionViewModel(ITraceStorageCapacityQuery? capacity, IEvidenceRetentionService? retention,
        IInteractiveSessionService? sessions, IStepUpAuthentication? stepUp,
        IUiDispatcher? dispatcher = null, int pageSize = 50)
    {
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
        _capacity = capacity;
        _retention = retention;
        _sessions = sessions;
        _stepUp = stepUp;
        _dispatcher = dispatcher ?? new DispatcherUiDispatcher();
        _pageSize = pageSize;
        Subjects = new ReadOnlyObservableCollection<EvidenceRetentionStatus>(_subjects);
        History = new ReadOnlyObservableCollection<EvidenceRetentionRecord>(_history);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync,
            () => !_disposed && !_busy && (_retention is not null || _capacity is not null));
        NextPageCommand = new AsyncRelayCommand(NextPageAsync,
            () => !_disposed && !_busy && _retention is not null && _next);
        _status = retention is null ? "保留查询未配置。" : "尚未读取容量与保留记录。";
        _capacityText = capacity is null ? "容量查询未配置；容量数值未知。" : "尚未读取容量观察。";
        _policyText = retention is null ? "保留查询未配置。" : "尚未读取保留策略与统计。";
        if (_sessions is not null) _sessions.Changed += SessionChanged;
    }

    public ReadOnlyObservableCollection<EvidenceRetentionStatus> Subjects { get; }
    public ReadOnlyObservableCollection<EvidenceRetentionRecord> History { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand NextPageCommand { get; }
    public bool IsLoading => _busy;
    public bool HasNextPage => _next;
    public string StatusText => _status;
    public string CapacityText => _capacityText;
    public string PolicyText => _policyText;

    public EvidenceRetentionStatus? SelectedSubject
    {
        get => _selected;
        set
        {
            EnsureUi();
            if (_disposed || value is not null && !_subjects.Contains(value)) value = null;
            if (ReferenceEquals(_selected, value)) return;
            _selected = value;
            _selectedHoldId = null;
            InvalidatePendingAuthorization();
            Notify();
        }
    }

    public string ReasonText
    {
        get => _reasonText;
        set
        {
            EnsureUi();
            var filtered = new string((value ?? string.Empty).Where(character => !char.IsControl(character)).ToArray());
            var bounded = filtered.Trim();
            if (bounded.Length > MaximumReasonLength) bounded = bounded[..MaximumReasonLength];
            if (_reasonText == bounded) return;
            _reasonText = bounded;
            InvalidatePendingAuthorization();
            Notify();
        }
    }

    public Guid? SelectedHoldId
    {
        get => _selectedHoldId;
        set
        {
            EnsureUi();
            if (_disposed || value is { } hold && (_selected is null || !_selected.ActiveHolds.Contains(hold))) value = null;
            if (_selectedHoldId == value) return;
            _selectedHoldId = value;
            InvalidatePendingAuthorization();
            Notify();
        }
    }

    public string ExtendedUntilText
    {
        get => _extendedUntilText;
        set
        {
            EnsureUi();
            var bounded = (value ?? string.Empty).Trim();
            if (bounded.Length > MaximumExtensionLength) bounded = bounded[..MaximumExtensionLength];
            if (_extendedUntilText == bounded) return;
            _extendedUntilText = bounded;
            InvalidatePendingAuthorization();
            Notify();
        }
    }

    public bool CanPlaceHold => CanChange(EvidenceRetentionChange.PlaceHold);
    public bool CanReleaseHold => CanChange(EvidenceRetentionChange.ReleaseHold);
    public bool CanExtend => CanChange(EvidenceRetentionChange.Extend);

    public string SelectionSummary => _selected is not { } subject
        ? "请选择一个证据主体。"
        : $"{DispositionLabel(subject.Disposition)} · {OwnerKindLabel(subject.Obligation.Owner.Kind)} {subject.Obligation.Owner.OwnerId:D}\n" +
          $"类别 {ClassLabel(subject.Obligation.EvidenceClass)} · 起算 {StartLabel(subject.Obligation.StartsAt)} · " +
          $"文件 {subject.Obligation.FileName} · {Number(subject.Obligation.ByteLength)} 字节\n" +
          $"初始保留至（UTC）{Time(subject.Obligation.RetainUntilUtc)} · 生效保留至（UTC）{Time(subject.EffectiveUntilUtc)}\n" +
          $"修订 {Number(subject.Revision)} · 活动人工保留 {Number(subject.ActiveHolds.Count)} 个 · " +
          $"删除尝试 {Number(subject.DeleteAttempts)} 次 · 删除操作 {subject.DeleteOperationId?.ToString("D") ?? "—"}\n" +
          $"修订哈希 {subject.RevisionHash}";

    public Task RefreshAsync() => LoadAsync(false);
    public Task NextPageAsync() => LoadAsync(true);

    public void Deactivate()
    {
        EnsureUi();
        Invalidate("页面已离开；重新进入后读取最新容量与保留记录。");
    }

    private async Task LoadAsync(bool next)
    {
        CancellationTokenSource? cancellation = null;
        long generation = 0;
        long after = 0;
        long? through = null;
        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed || _busy) return;
            if (next && (!_next || _retention is null)) return;
            if (_retention is null && _capacity is null) return;
            after = next ? _after : 0;
            through = next ? _through : null;
            ClearRows();
            cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _cancellation = cancellation;
            generation = ++_generation;
            _busy = true;
            _status = "正在读取容量观察与已提交的保留事实…";
            Notify();
        });
        if (cancellation is null) return;
        var filter = new EvidenceRetentionFilter(AfterPosition: after, ThroughPosition: through, PageSize: _pageSize);
        TraceStorageCapacitySnapshot? capacity = null;
        EvidenceRetentionSnapshot? snapshot = null;
        try
        {
            if (_capacity is not null)
                capacity = await Task.Run(async () => await _capacity.ReadAsync(cancellation.Token)
                    .ConfigureAwait(false)).WaitAsync(cancellation.Token).ConfigureAwait(false);
            if (_retention is not null)
                snapshot = await Task.Run(async () => await _retention.ReadAsync(filter, cancellation.Token)
                    .ConfigureAwait(false)).WaitAsync(cancellation.Token).ConfigureAwait(false);
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
                    if (_capacity is not null) _capacityText = CapacityDisplay(capacity);
                    if (_retention is null) _status = "保留查询未配置。";
                    else if (snapshot is not null && Valid(snapshot, filter))
                    {
                        Apply(snapshot);
                        _policyText = FormatPolicy(snapshot);
                        _status = $"本页 {_subjects.Count} 个主体 · {_history.Count} 条保留事实 · " +
                            $"已读取至位置 {snapshot.ThroughPosition}" + (_next ? "，可继续下一页。" : "，已到末页。");
                    }
                    else
                    {
                        _policyText = "保留记录不可用；策略与统计未显示。";
                        _status = "保留记录不可用，请检查存储与审计状态。";
                    }
                }
                Notify();
            });
            cancellation.Dispose();
        }
    }

    private void Apply(EvidenceRetentionSnapshot snapshot)
    {
        foreach (var subject in snapshot.Subjects) _subjects.Add(subject);
        foreach (var record in snapshot.Records) _history.Add(record);
        _through = snapshot.ThroughPosition;
        _after = snapshot.NextAfterPosition ?? 0;
        _next = snapshot.NextAfterPosition is not null;
        // 读取只刷新事实：操作对象始终由操作员显式选择，读取本身不预选任何主体。
        _selected = null;
        _selectedHoldId = null;
    }

    private void Invalidate(string message)
    {
        _generation++;
        _cancellation?.Cancel();
        ClearRows();
        ClearOperationInputs();
        _capacityText = _capacity is null ? "容量查询未配置；容量数值未知。" : "尚未读取容量观察。";
        _policyText = _retention is null ? "保留查询未配置。" : "尚未读取保留策略与统计。";
        _status = message;
        Notify();
    }

    private void InvalidatePendingAuthorization()
    {
        _generation++;
        if (_busy) _status = _submittedChange
            ? "保留处置请求已发出，结果尚未确认；请刷新并读取最新账本。"
            : "页面输入已变化，本次等待已结束；请重新查询或提交。";
        _cancellation?.Cancel();
    }

    private void ClearRows()
    {
        _subjects.Clear();
        _history.Clear();
        _selected = null;
        _selectedHoldId = null;
        _next = false;
        _after = 0;
        _through = null;
    }

    private void ClearOperationInputs()
    {
        _submittedChange = false;
        _reasonText = string.Empty;
        _extendedUntilText = string.Empty;
        SensitiveInputsInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void Notify()
    {
        foreach (var name in new[] { nameof(IsLoading), nameof(HasNextPage), nameof(StatusText), nameof(CapacityText),
            nameof(PolicyText), nameof(SelectedSubject), nameof(SelectionSummary), nameof(ReasonText),
            nameof(SelectedHoldId), nameof(ExtendedUntilText), nameof(CanPlaceHold), nameof(CanReleaseHold),
            nameof(CanExtend) }) OnPropertyChanged(name);
        RefreshCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
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
        Invalidate("保留处置查看器已关闭。");
    });

    private bool Valid(EvidenceRetentionSnapshot snapshot, EvidenceRetentionFilter filter)
    {
        if (!snapshot.Available || snapshot.Records is null || snapshot.Subjects is null ||
            snapshot.Records.Count > _pageSize || snapshot.Subjects.Count > _pageSize ||
            snapshot.ThroughPosition < 0 || snapshot.ThroughPosition < filter.AfterPosition ||
            snapshot.ActiveHolds < 0 || snapshot.PendingDeletes < 0 || snapshot.UnknownDeletes < 0 ||
            snapshot.UnknownDeletes > snapshot.PendingDeletes || snapshot.DeletedFiles < 0 ||
            snapshot.DeletedLogicalBytes < 0 ||
            (filter.ThroughPosition is { } requested && snapshot.ThroughPosition != requested) ||
            !ValidExecutionPolicy(snapshot.ExecutionPolicy) || snapshot.ControlFactsUsed is < 0 ||
            (snapshot.RecoveryBudget is null) != (snapshot.ControlFactsUsed is null)) return false;
        var owners = new HashSet<EvidenceRetentionOwner>();
        foreach (var subject in snapshot.Subjects)
        {
            if (!ValidSubject(subject) || !owners.Add(subject.Obligation.Owner)) return false;
        }
        var position = filter.AfterPosition;
        long audit = 0;
        var events = new HashSet<Guid>();
        foreach (var record in snapshot.Records)
        {
            if (record is null || record.Position <= position || record.Position > snapshot.ThroughPosition ||
                record.AuditSequence <= audit || record.EventId == Guid.Empty || !events.Add(record.EventId) ||
                record.OperationId == Guid.Empty || record.RuntimeEpoch == Guid.Empty ||
                !Enum.IsDefined(record.Kind) || !ValidOwner(record.Owner) || record.AggregateSequence < 1 ||
                !Hash(record.ObligationHash) || !Hash(record.ContentHash) ||
                (record.PreviousContentHash is { } previous && !Hash(previous)) ||
                record.RecordedAtUtc == default || record.RecordedAtUtc.Offset != TimeSpan.Zero ||
                record.ReasonCode is not { Length: > 0 and <= 128 } ||
                record.Reason is { Length: > 512 } ||
                record.SystemPrincipalId is not { Length: > 0 and <= 256 } ||
                record.HumanPrincipalId == Guid.Empty || record.SessionId == Guid.Empty ||
                record.StepUpGrantId == Guid.Empty || !ValidRecordPayload(record)) return false;
            position = record.Position;
            audit = record.AuditSequence;
        }
        if (snapshot.NextAfterPosition is { } next &&
            (snapshot.Records.Count == 0 || next != snapshot.Records[^1].Position)) return false;
        return true;
    }

    private static bool ValidSubject(EvidenceRetentionStatus? subject)
    {
        if (subject is null || subject.Obligation is null || subject.ActiveHolds is null) return false;
        var obligation = subject.Obligation;
        if (!ValidOwner(obligation.Owner) ||
            obligation.StartedAtUtc == default || obligation.StartedAtUtc.Offset != TimeSpan.Zero ||
            obligation.RetainUntilUtc == default || obligation.RetainUntilUtc.Offset != TimeSpan.Zero ||
            obligation.RetainUntilUtc <= obligation.StartedAtUtc ||
            obligation.SourceAuditSequence < 1 || obligation.ByteLength < 0 ||
            !Enum.IsDefined(obligation.EvidenceClass) || !Enum.IsDefined(obligation.StartsAt) ||
            obligation.FileName is not { Length: > 0 and <= 255 } ||
            !Hash(obligation.SourceContentHash) || !Hash(obligation.ArtifactContentHash) ||
            !Hash(obligation.PolicySnapshotHash) || !Hash(obligation.PublicationHash) ||
            !Hash(obligation.RuleHash) || !Hash(obligation.RootBindingHash) || !Hash(obligation.ContentHash))
            return false;
        if (obligation.Owner.Kind == EvidenceRetentionOwnerKind.ImageManifest)
        {
            if (obligation.InspectionId is not { } inspection || inspection == Guid.Empty || obligation.ByteLength <= 0)
                return false;
        }
        else if (obligation.InspectionId is not null) return false;
        if (subject.Revision < 1 || !Hash(subject.RevisionHash) || !Enum.IsDefined(subject.Disposition) ||
            subject.EffectiveUntilUtc == default || subject.EffectiveUntilUtc.Offset != TimeSpan.Zero ||
            subject.EffectiveUntilUtc < obligation.RetainUntilUtc || subject.DeleteAttempts < 0 ||
            subject.ReasonCode is not { Length: > 0 and <= 128 } || subject.DeleteOperationId == Guid.Empty ||
            (subject.TombstoneHash is { } tombstone && !Hash(tombstone))) return false;
        var holds = new HashSet<Guid>();
        foreach (var hold in subject.ActiveHolds)
        {
            if (hold == Guid.Empty || !holds.Add(hold)) return false;
        }
        return subject.Disposition switch
        {
            EvidenceRetentionDisposition.Held => holds.Count > 0 && subject.TombstoneHash is null,
            EvidenceRetentionDisposition.Retained => holds.Count == 0 && subject.TombstoneHash is null,
            EvidenceRetentionDisposition.Deleting => holds.Count == 0 && subject.TombstoneHash is null,
            EvidenceRetentionDisposition.DeleteUnknown => holds.Count == 0 && subject.TombstoneHash is null,
            EvidenceRetentionDisposition.Deleted => holds.Count == 0 && subject.TombstoneHash is not null,
            _ => false
        };
    }

    private static bool ValidRecordPayload(EvidenceRetentionRecord record)
    {
        var holds = record.Kind is EvidenceRetentionEventKind.HoldPlaced or EvidenceRetentionEventKind.HoldReleased;
        if (holds != (record.HoldId is { } holdId && holdId != Guid.Empty)) return false;
        var extension = record.Kind == EvidenceRetentionEventKind.Extended;
        if (extension != (record.ExtendedUntilUtc is { } until && until != default && until.Offset == TimeSpan.Zero))
            return false;
        var deleting = record.Kind is EvidenceRetentionEventKind.DeleteIntent or EvidenceRetentionEventKind.DeleteFailed or
            EvidenceRetentionEventKind.DeleteOutcomeUnknown or EvidenceRetentionEventKind.Tombstone;
        if (deleting != (record.File is not null)) return false;
        if (record.File is { } file)
        {
            if (!Hash(file.RootBindingHash) || !Hash(file.FileIdentityHash) || !Hash(file.RawContentHash) ||
                file.FileName is not { Length: > 0 and <= 255 } || file.ByteLength < 0) return false;
        }
        if (record.Kind == EvidenceRetentionEventKind.ObligationEstablished &&
            (record.AggregateSequence != 1 || record.PreviousContentHash is not null)) return false;
        return true;
    }

    private static bool ValidOwner(EvidenceRetentionOwner? owner) =>
        owner is not null && Enum.IsDefined(owner.Kind) && owner.OwnerId != Guid.Empty;

    private static bool ValidExecutionPolicy(TraceRetentionExecutionPolicy? policy)
    {
        if (policy is null) return true;
        if (policy.PolicyId is not { Length: > 0 and <= 128 } || policy.Version is not { Length: > 0 and <= 128 } ||
            policy.ApprovalReference is not { Length: > 0 and <= 256 } ||
            policy.Rationale is not { Length: > 0 and <= 4096 } ||
            policy.MaximumDeletionAttempts is < 1 or > 64 || policy.Cleanup is null ||
            policy.Cleanup.Interval <= TimeSpan.Zero || policy.Cleanup.MaximumRunTime <= TimeSpan.Zero ||
            policy.Cleanup.MaximumRunTime > policy.Cleanup.Interval || policy.Cleanup.MaximumBytes <= 0 ||
            policy.Cleanup.MaximumItems <= 0 || !Hash(policy.ContentHash) ||
            policy.DeletableClasses is null || policy.DeletableClasses.Count > 11) return false;
        foreach (var value in policy.DeletableClasses)
        {
            if (value is not (TraceRetentionClass.AuthoritativeImage or TraceRetentionClass.QuarantineEvidence or
                TraceRetentionClass.OrphanImageStage)) return false;
        }
        return true;
    }

    private static bool CapacityValid(TraceStorageCapacitySnapshot snapshot)
    {
        if (snapshot.RuntimeEpoch == Guid.Empty || snapshot.Revision < 0 || snapshot.ObservedAtUtc == default ||
            snapshot.ObservedAtUtc.Offset != TimeSpan.Zero || !Enum.IsDefined(snapshot.Health) ||
            snapshot.Health == TraceStorageHealth.Unavailable ||
            snapshot.Areas is null || snapshot.Areas.Count is 0 or > 8 ||
            snapshot.DatabaseBytes < 0 || snapshot.WalBytes < 0 ||
            (snapshot.MaximumWalBytes is { } maximumWal && maximumWal <= 0) ||
            snapshot.AdmissionBlockers is null ||
            (snapshot.ImageBacklog is { } images && !ValidImageBacklog(images)) ||
            (snapshot.OutboxBacklog is { } outbox && !ValidOutboxBacklog(outbox)) ||
            !ValidCheckpoint(snapshot.Checkpoint)) return false;
        var areas = new HashSet<TraceStorageArea>();
        foreach (var area in snapshot.Areas)
        {
            if (area is null || !Enum.IsDefined(area.Area) || !areas.Add(area.Area) || !Hash(area.RootBindingHash) ||
                area.TotalVolumeBytes <= 0 || area.AvailableVolumeBytes < 0 ||
                area.AvailableVolumeBytes > area.TotalVolumeBytes ||
                area.RequiredReserveBytes < 0 || area.RequiredReserveBytes > area.TotalVolumeBytes ||
                area.OwnedFileBytes < 0 || area.OwnedFiles < 0 ||
                (area.MaximumOwnedBytes is { } maximumBytes && maximumBytes < 0) ||
                (area.MaximumOwnedFiles is { } maximumFiles && maximumFiles < 0)) return false;
        }
        return true;
    }

    private static bool ValidImageBacklog(ImageBacklogSnapshot backlog)
    {
        if (backlog.ThroughAuditSequence < 0 || backlog.Count < 0 || backlog.Bytes < 0) return false;
        if (backlog.OldestCreatedAtUtc is { } oldest) return oldest != default && oldest.Offset == TimeSpan.Zero;
        return backlog.Count == 0;
    }

    private static bool ValidOutboxBacklog(OutboxBacklogSnapshot backlog)
    {
        if (backlog.ThroughAuditSequence < 0 || backlog.Routes is null || backlog.Routes.Count > 64) return false;
        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in backlog.Routes)
        {
            if (route is null || route.RouteId is not { Length: > 0 and <= 128 } || !Hash(route.RouteHash) ||
                !Enum.IsDefined(route.Criticality) || route.PendingCount < 0 || route.PendingBytes < 0 ||
                route.FailedCount < 0 || !routes.Add(route.RouteId)) return false;
        }
        return true;
    }

    private static bool ValidCheckpoint(TraceCheckpointObservation? checkpoint)
    {
        if (checkpoint is null || !Enum.IsDefined(checkpoint.Status) ||
            checkpoint.ReasonCode is not { Length: > 0 and <= 128 }) return false;
        if (checkpoint.ObservedAtUtc is { } observed && (observed == default || observed.Offset != TimeSpan.Zero))
            return false;
        if (checkpoint.PolicySnapshotHash is { } policyHash && !Hash(policyHash)) return false;
        if (checkpoint.WalBytes is < 0 || checkpoint.LogFrames is < 0 || checkpoint.CheckpointedFrames is < 0)
            return false;
        if (checkpoint.Elapsed is { } elapsed && elapsed < TimeSpan.Zero) return false;
        return true;
    }

    private static string CapacityDisplay(TraceStorageCapacitySnapshot? snapshot)
    {
        if (snapshot is null) return "容量观察不可用；容量数值未知。";
        if (!snapshot.Available) return "容量观察不可用；容量数值未知，未观察的数值不代表为零。";
        return CapacityValid(snapshot)
            ? FormatCapacity(snapshot)
            : "容量观察不一致，已拒绝显示；容量数值视为未知。";
    }

    private static string FormatCapacity(TraceStorageCapacitySnapshot snapshot)
    {
        var lines = new List<string>
        {
            $"容量观察：{HealthLabel(snapshot.Health)} · 观察时间（UTC）{Time(snapshot.ObservedAtUtc)} · 修订 {Number(snapshot.Revision)}。",
            $"数据库字节 {Number(snapshot.DatabaseBytes)}；WAL 字节 {Number(snapshot.WalBytes)} / 上限 " +
            (snapshot.MaximumWalBytes is { } maximum ? Number(maximum) : "未观察") + "。"
        };
        foreach (var area in snapshot.Areas) lines.Add(FormatArea(area));
        if (snapshot.ImageBacklog is { } images)
            lines.Add($"图片积压：数量 {Number(images.Count)}，字节 {Number(images.Bytes)}，" +
                $"最老（UTC）{Oldest(images.OldestCreatedAtUtc)}，审计水位 {Number(images.ThroughAuditSequence)}。");
        else lines.Add("图片积压：未观察。");
        lines.Add(FormatOutbox(snapshot.OutboxBacklog));
        lines.Add(FormatCheckpoint(snapshot.Checkpoint));
        lines.Add($"准入阻塞：{Number(snapshot.AdmissionBlockers.Count)} 项。");
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatArea(TraceStorageAreaObservation area)
    {
        var text = $"{AreaLabel(area.Area)}：卷总量 {Number(area.TotalVolumeBytes)}，可用 {Number(area.AvailableVolumeBytes)}，" +
            $"保留要求 {Number(area.RequiredReserveBytes)}，占用文件 {Number(area.OwnedFiles)} / 字节 {Number(area.OwnedFileBytes)}";
        if (area.MaximumOwnedFiles is { } maximumFiles) text += $"，文件上限 {Number(maximumFiles)}";
        if (area.MaximumOwnedBytes is { } maximumBytes) text += $"，字节上限 {Number(maximumBytes)}";
        if (!area.InventoryComplete) text += "，清点未完整";
        return text + "。";
    }

    private static string FormatOutbox(OutboxBacklogSnapshot? backlog)
    {
        if (backlog is null) return "外部交付积压：未观察。";
        if (backlog.Routes.Count == 0) return "外部交付积压：没有已登记路由。";
        var parts = new List<string>();
        foreach (var route in backlog.Routes)
            parts.Add($"{route.RouteId}（{CriticalityLabel(route.Criticality)}）：待处理 {Number(route.PendingCount)} 条 / " +
                $"{Number(route.PendingBytes)} 字节，失败 {Number(route.FailedCount)}，" +
                (route.PermanentBlock ? "永久阻塞" : "未永久阻塞"));
        return "外部交付积压：" + string.Join("；", parts) + "。";
    }

    private static string FormatCheckpoint(TraceCheckpointObservation checkpoint)
    {
        var text = $"检查点：{CheckpointLabel(checkpoint.Status)}";
        if (checkpoint.ObservedAtUtc is { } observed) text += $"，观察时间（UTC）{Time(observed)}";
        if (checkpoint.WalBytes is { } wal) text += $"，WAL 字节 {Number(wal)}";
        if (checkpoint.LogFrames is { } frames) text += $"，日志帧 {Number(frames)}";
        if (checkpoint.CheckpointedFrames is { } checkpointed) text += $"，已检查点帧 {Number(checkpointed)}";
        if (checkpoint.Elapsed is { } elapsed) text += $"，用时 {elapsed.ToString("c", CultureInfo.InvariantCulture)}";
        return text + "。";
    }

    private static string FormatPolicy(EvidenceRetentionSnapshot snapshot)
    {
        var lines = new List<string>();
        if (snapshot.ExecutionPolicy is { } policy)
        {
            lines.Add($"保留执行策略：{policy.PolicyId} · 版本 {policy.Version} · 审批引用 {policy.ApprovalReference}。");
            var classes = new List<string>();
            foreach (var value in policy.DeletableClasses) classes.Add(ClassLabel(value));
            lines.Add($"删除尝试上限 {Number(policy.MaximumDeletionAttempts)}；可删除类别：{string.Join("、", classes)}。");
            lines.Add($"清理预算：间隔 {policy.Cleanup.Interval.ToString("c", CultureInfo.InvariantCulture)}，" +
                $"最长运行 {policy.Cleanup.MaximumRunTime.ToString("c", CultureInfo.InvariantCulture)}，" +
                $"最大字节 {Number(policy.Cleanup.MaximumBytes)}，最大条目 {Number(policy.Cleanup.MaximumItems)}。");
        }
        else lines.Add("尚未发布经批准的保留执行策略；删除相关处置保持关闭。");
        if (snapshot.RecoveryBudget is { } recovery && snapshot.ControlFactsUsed is { } used)
        {
            lines.Add($"恢复操作预算：控制写入前 WAL 门槛 {Number(recovery.ControlWalAdmissionBytes)} 字节，" +
                $"认证及策略事实已用 {Number(used)} / {Number(recovery.MaximumControlFacts)}；重启不重置。");
            lines.Add($"checkpoint 规划余量 {Number(recovery.CheckpointPlanningReserveBytes)} 字节；实际执行仍须启动维护检查。");
        }
        lines.Add($"账本统计：人工保留 {Number(snapshot.ActiveHolds)} 项 · 待删除 {Number(snapshot.PendingDeletes)} 项 · " +
            $"删除结果未知 {Number(snapshot.UnknownDeletes)} 项 · 已删除文件 {Number(snapshot.DeletedFiles)} 个 / " +
            $"逻辑字节 {Number(snapshot.DeletedLogicalBytes)}。");
        lines.Add($"本页读取至位置 {Number(snapshot.ThroughPosition)}。");
        return string.Join(Environment.NewLine, lines);
    }

    private static bool Hash(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Time(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static string Oldest(DateTimeOffset? value) => value is { } time ? Time(time) : "—";

    private static string DispositionLabel(EvidenceRetentionDisposition disposition) => disposition switch
    {
        EvidenceRetentionDisposition.Retained => "按期限保留",
        EvidenceRetentionDisposition.Held => "人工保留",
        EvidenceRetentionDisposition.Deleting => "删除进行中",
        EvidenceRetentionDisposition.DeleteUnknown => "删除结果未知",
        EvidenceRetentionDisposition.Deleted => "已删除",
        _ => "未知状态"
    };

    private static string OwnerKindLabel(EvidenceRetentionOwnerKind kind) => kind switch
    {
        EvidenceRetentionOwnerKind.ImageManifest => "图片清单",
        EvidenceRetentionOwnerKind.QuarantinedFile => "隔离文件",
        _ => "未知主体"
    };

    private static string ClassLabel(TraceRetentionClass evidenceClass) => evidenceClass switch
    {
        TraceRetentionClass.AuthoritativeImage => "权威图片",
        TraceRetentionClass.Thumbnail => "缩略图",
        TraceRetentionClass.RenderedEvidencePreview => "渲染证据预览",
        TraceRetentionClass.OperationalLog => "运行日志",
        TraceRetentionClass.ProtectedDiagnosticRecord => "受保护诊断记录",
        TraceRetentionClass.SupportBundle => "支持包",
        TraceRetentionClass.CompletedExport => "已完成导出",
        TraceRetentionClass.RecipeImportStaging => "配方导入暂存",
        TraceRetentionClass.RejectedRecipePackage => "被拒绝配方包",
        TraceRetentionClass.QuarantineEvidence => "隔离证据",
        TraceRetentionClass.OrphanImageStage => "孤儿图片暂存",
        _ => "未知类别"
    };

    private static string StartLabel(RetentionStartEvent startsAt) => startsAt switch
    {
        RetentionStartEvent.ArtifactCreated => "产物创建",
        RetentionStartEvent.Finalized => "最终完成",
        RetentionStartEvent.ExportCompleted => "导出完成",
        RetentionStartEvent.Quarantined => "已隔离",
        _ => "未知起算"
    };

    private static string AreaLabel(TraceStorageArea area) => area switch
    {
        TraceStorageArea.Database => "数据库区",
        TraceStorageArea.ImageStage => "图片暂存区",
        TraceStorageArea.FinalImages => "最终图片区",
        TraceStorageArea.StageQuarantine => "暂存隔离区",
        TraceStorageArea.FinalQuarantine => "最终隔离区",
        _ => "未知区域"
    };

    private static string HealthLabel(TraceStorageHealth health) => health switch
    {
        TraceStorageHealth.Healthy => "正常",
        TraceStorageHealth.CapacityBlocked => "容量受阻",
        TraceStorageHealth.IntegrityBlocked => "完整性受阻",
        TraceStorageHealth.Unavailable => "观察不可用",
        _ => "未知健康状态"
    };

    private static string CheckpointLabel(TraceCheckpointStatus status) => status switch
    {
        TraceCheckpointStatus.NotConfigured => "未配置",
        TraceCheckpointStatus.Awaiting => "等待停机检查点",
        TraceCheckpointStatus.Completed => "已完成",
        TraceCheckpointStatus.Busy => "执行中",
        TraceCheckpointStatus.BudgetExceeded => "超出预算",
        TraceCheckpointStatus.Failed => "失败",
        TraceCheckpointStatus.Unknown => "未知",
        _ => "未知状态"
    };

    private static string CriticalityLabel(OutboxRouteCriticality criticality) =>
        criticality == OutboxRouteCriticality.Required ? "必需" : "尽力";
}
