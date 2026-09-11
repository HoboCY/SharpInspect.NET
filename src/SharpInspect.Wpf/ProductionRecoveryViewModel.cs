using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Presentation-only maintenance surface for production recovery.  The list is
/// a read-only projection; every recovery command is rebuilt from the selected
/// event and is still authorized by Runtime.
/// </summary>
public sealed class ProductionRecoveryViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IProductionRecoveryHistoryQuery? _historyQuery;
    private readonly IStationRuntime? _runtime;
    private readonly IInteractiveSessionService? _sessions;
    private readonly IStepUpAuthentication? _stepUpAuthentication;
    private readonly IUiDispatcher _dispatcher;
    private readonly object _sync = new();
    private CancellationTokenSource? _pending;
    private long _generation;
    private bool _disposed;
    private bool _stale = true;
    private ProductionRecoveryPendingPage? _page;
    private IReadOnlyList<ProductionRecoveryPendingItem> _pendingItems =
        Array.Empty<ProductionRecoveryPendingItem>();
    private ProductionRecoveryPendingItem? _selectedItem;
    private RuntimeCommandOutcome? _lastOutcome;
    private string? _errorCode;
    private string _statusMessage;
    private string _reasonText = string.Empty;
    private string _dispositionNote = string.Empty;
    private PartDisposition? _selectedDisposition;

    private readonly record struct OperationStart(long Version,
        CancellationTokenSource Cancellation, InteractiveSession Session);

    public ProductionRecoveryViewModel(IProductionRecoveryHistoryQuery? history = null,
        IStationRuntime? runtime = null, IInteractiveSessionService? sessions = null,
        IStepUpAuthentication? stepUpAuthentication = null, IUiDispatcher? dispatcher = null)
    {
        _historyQuery = history;
        _runtime = runtime;
        _sessions = sessions;
        _stepUpAuthentication = stepUpAuthentication;
        _dispatcher = dispatcher ?? new DispatcherUiDispatcher();
        _statusMessage = IsConfigured
            ? "尚未读取待恢复生产记录，请刷新。"
            : "生产恢复不可用：查询、Runtime 或当前交互会话未完整配置。";
        if (_sessions is not null) _sessions.Changed += SessionChanged;
    }

    public IReadOnlyList<ProductionRecoveryPendingItem> Pending
    {
        get { lock (_sync) return _pendingItems; }
    }

    public ProductionRecoveryPendingPage? Page
    {
        get { lock (_sync) return _page; }
    }

    public ProductionRecoveryPendingItem? SelectedItem
    {
        get { lock (_sync) return _selectedItem; }
        set => SetSelectedItem(value);
    }

    public ProductionInspectionHistoryEvent? SelectedEvent => SelectedItem?.Event;
    public ProductionInspectionCore? SelectedCore => SelectedEvent?.Core;
    public PlcResultPayloadSnapshot? SelectedPayload => SelectedCore?.PlcPayload;
    public Guid? SelectedInspectionId => SelectedItem?.InspectionId;
    public Guid? SelectedRuntimeEpoch => SelectedEvent?.RuntimeEpoch;
    public uint? SelectedControllerEpoch => SelectedEvent?.Admission.ControllerCycle.ControllerEpoch;
    public uint? SelectedCycleSequence => SelectedEvent?.Admission.ControllerCycle.CycleSequence;
    public ProductionRecoveryDeliveryPhase? SelectedDeliveryPhase => SelectedItem?.DeliveryPhase;
    public ProductionRecoveryUncertaintyKind? SelectedUncertainty => SelectedItem?.Uncertainty;
    public string SelectedCoreContentHash => SelectedCore?.ContentHash ?? string.Empty;
    public string SelectedPayloadContentHash => SelectedPayload?.ContentHash ?? string.Empty;
    public string SelectedPayloadWireContentHash => SelectedPayload?.WireContentHash ?? string.Empty;
    public string SelectedPayloadContractVersion => SelectedPayload?.Contract.Version ?? string.Empty;
    public string SelectedPayloadContractHash => SelectedPayload?.Contract.ContentHash ?? string.Empty;

    public RuntimeCommandOutcome? LastOutcome
    {
        get { lock (_sync) return _lastOutcome; }
    }

    public RuntimeCommandOutcome? LastResult => LastOutcome;

    public string ReasonText
    {
        get { lock (_sync) return _reasonText; }
        set
        {
            lock (_sync) _reasonText = value ?? string.Empty;
            RaiseStateChanged();
        }
    }

    public string DispositionNote
    {
        get { lock (_sync) return _dispositionNote; }
        set
        {
            lock (_sync) _dispositionNote = value ?? string.Empty;
            RaiseStateChanged();
        }
    }

    public PartDisposition? SelectedDisposition
    {
        get { lock (_sync) return _selectedDisposition; }
        set
        {
            lock (_sync) _selectedDisposition = value;
            RaiseStateChanged();
        }
    }

    public bool IsConfigured => _historyQuery is not null && _runtime is not null && _sessions is not null;
    public bool IsAuthenticated => IsUsableSession(_sessions?.Current ?? UnauthenticatedSession);
    public bool IsBusy { get { lock (_sync) return _pending is not null; } }
    public bool IsStale { get { lock (_sync) return _stale; } }
    public bool HasPending => Pending.Count != 0;
    public bool CanRefresh => IsConfigured && IsAuthenticated && !IsBusy && !_disposed;
    public bool CanRecover
    {
        get
        {
            lock (_sync)
            {
                return IsConfigured && IsAuthenticated && !_disposed && !_stale &&
                    _pending is null && _selectedItem is not null &&
                    _selectedDisposition.HasValue && !string.IsNullOrWhiteSpace(_reasonText) &&
                    _stepUpAuthentication is not null;
            }
        }
    }

    public string FreshnessLabel
    {
        get
        {
            lock (_sync)
            {
                if (_stale) return "待恢复列表已陈旧，请刷新";
                return _pendingItems.Count == 0 ? "没有待恢复生产记录" :
                    $"待恢复记录 {_page?.PendingCount ?? _pendingItems.Count} 条 · 当前显示 {_pendingItems.Count} 条";
            }
        }
    }

    /// <summary>The panel clears the native password control after this event.</summary>
    internal event EventHandler? SessionInvalidated;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!TryBegin(cancellationToken, out var start)) return;
        try
        {
            var page = await _historyQuery!.QueryPendingAsync(128, 0,
                start.Cancellation.Token).ConfigureAwait(false);
            start.Cancellation.Token.ThrowIfCancellationRequested();
            await PublishAsync(start, () =>
            {
                _page = page;
                if (page.Available)
                {
                    _pendingItems = page.Items.ToArray();
                    _selectedItem = FindCurrentSelection(_selectedItem, _pendingItems);
                }
                _stale = !page.Available;
                _errorCode = page.Available ? null : SafeReason(page.ReasonCode,
                    "ProductionRecoveryHistoryUnavailable");
                _statusMessage = page.Available
                    ? _pendingItems.Count == 0
                        ? "没有待恢复生产记录。"
                        : "已读取待恢复记录；恢复前仍需选择处置、原因和新鲜 Step-Up。"
                    : "待恢复历史不可用，命令入口已关闭。";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (start.Cancellation.IsCancellationRequested)
        {
            await PublishAsync(start, () =>
            {
                _stale = true;
                _errorCode = "ProductionRecoveryPresentationWaitEnded";
                _statusMessage = "已结束界面等待；请刷新确认待恢复事实。";
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await PublishAsync(start, () =>
            {
                _stale = true;
                _errorCode = SafeReason(exception.Message,
                    "ProductionRecoveryHistoryUnavailable");
                _statusMessage = "待恢复历史不可用，请刷新并查看 Runtime 审计记录。";
            }).ConfigureAwait(false);
        }
        finally
        {
            await EndAsync(start).ConfigureAwait(false);
        }
    }

    public Task<RuntimeCommandOutcome?> SubmitAsync(string reason, string dispositionNote,
        PartDisposition disposition, string stepUpPassword,
        CancellationToken cancellationToken = default) =>
        RecoverAsync(reason, dispositionNote, disposition, stepUpPassword, cancellationToken);

    public Task<RuntimeCommandOutcome?> RecoverAsync(CancellationToken cancellationToken = default)
    {
        string reason;
        string note;
        PartDisposition? disposition;
        lock (_sync)
        {
            reason = _reasonText;
            note = _dispositionNote;
            disposition = _selectedDisposition;
        }
        return disposition is { } selected
            ? RecoverAsync(reason, note, selected, string.Empty, cancellationToken)
            : Task.FromResult<RuntimeCommandOutcome?>(null);
    }

    public async Task<RuntimeCommandOutcome?> RecoverAsync(string reason, string dispositionNote,
        PartDisposition disposition, string stepUpPassword,
        CancellationToken cancellationToken = default)
    {
        if (!TryBegin(cancellationToken, out var start)) return null;
        var password = stepUpPassword ?? string.Empty;
        try
        {
            ProductionRecoveryPendingItem selected;
            lock (_sync)
            {
                selected = _selectedItem ?? throw new InvalidOperationException(
                    "ProductionRecoverySelectionRequired");
                if (_stale || !_pendingItems.Any(item => SameItem(item, selected)))
                    throw new InvalidOperationException("ProductionRecoveryHistoryStale");
            }

            var correlation = Guid.NewGuid();
            var invocation = CreateInvocation(start.Session);
            var command = new ManualProductionRecoveryCommand(correlation, invocation,
                selected.InspectionId, selected.EventHash, reason ?? string.Empty,
                disposition, EmptyToNull(dispositionNote));
            var authentication = await ReauthenticateAsync(command, invocation, password,
                start.Cancellation.Token).ConfigureAwait(false);
            password = string.Empty;
            start.Cancellation.Token.ThrowIfCancellationRequested();
            if (!authentication.Succeeded || authentication.GrantId is not { } grant || grant == Guid.Empty)
            {
                var rejectedReason = SafeReason(authentication.ReasonCode,
                    "StepUpAuthenticationRejected");
                await PublishAsync(start, () =>
                {
                    _stale = true;
                    _errorCode = rejectedReason;
                    _statusMessage = $"恢复未开始：{rejectedReason}";
                }).ConfigureAwait(false);
                return null;
            }

            if (!IsSameSession(start.Session, _sessions!.Current))
            {
                await PublishAsync(start, () =>
                {
                    _stale = true;
                    _errorCode = "ProductionRecoverySessionChanged";
                    _statusMessage = "当前会话已变化，已丢弃待提交恢复命令。";
                }).ConfigureAwait(false);
                return null;
            }

            invocation = CreateInvocation(_sessions.Current, grant);
            command = new ManualProductionRecoveryCommand(correlation, invocation,
                selected.InspectionId, selected.EventHash, reason ?? string.Empty,
                disposition, EmptyToNull(dispositionNote));
            var outcome = await _runtime!.SubmitAsync(command, start.Cancellation.Token)
                .ConfigureAwait(false);
            await PublishAsync(start, () =>
            {
                _lastOutcome = outcome;
                _stale = true;
                _errorCode = outcome.Disposition == CommandDisposition.Accepted
                    ? null : SafeReason(outcome.ReasonCode, "ProductionRecoveryRejected");
                _statusMessage = outcome.Disposition == CommandDisposition.Accepted
                    ? "恢复命令已接纳；请刷新确认持久恢复结果。"
                    : $"恢复命令被拒绝：{_errorCode}";
            }).ConfigureAwait(false);
            return outcome;
        }
        catch (OperationCanceledException) when (start.Cancellation.IsCancellationRequested)
        {
            await PublishAsync(start, () =>
            {
                _stale = true;
                _errorCode = "ProductionRecoveryPresentationWaitEnded";
                _statusMessage = "已结束界面等待；Runtime 结果仍需刷新确认。";
            }).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await PublishAsync(start, () =>
            {
                _stale = true;
                _errorCode = SafeReason(exception.Message, "ProductionRecoveryUnavailable");
                _statusMessage = "恢复命令未完成，请刷新并查看 Runtime 审计记录。";
            }).ConfigureAwait(false);
            return null;
        }
        finally
        {
            password = string.Empty;
            await EndAsync(start).ConfigureAwait(false);
        }
    }

    public void CancelPendingOperations()
    {
        CancellationTokenSource? pending;
        lock (_sync)
        {
            pending = _pending;
            ++_generation;
            _stale = true;
        }
        if (pending is not null)
            _ = Task.Run(() => { try { pending.Cancel(); } catch (ObjectDisposedException) { } });
        RaiseStateChanged();
    }

    public void Deactivate() => CancelPendingOperations();

    public async ValueTask DisposeAsync()
    {
        CancelPendingOperations();
        lock (_sync) { _disposed = true; ++_generation; }
        if (_sessions is not null) _sessions.Changed -= SessionChanged;
        await Task.CompletedTask;
    }

    private async ValueTask<StepUpResult> ReauthenticateAsync(
        ManualProductionRecoveryCommand command, CommandInvocation invocation,
        string password, CancellationToken token)
    {
        var stepUp = _stepUpAuthentication;
        if (stepUp is null) return new StepUpResult(false, "ProductionRecoveryStepUpUnavailable");
        try
        {
            var binding = new StepUpBinding(Permission.ManualRecovery, command.CorrelationId,
                command.AuthorizationTarget, AuditedCommandKind.ManualProductionRecovery);
            return await stepUp.ReauthenticateAsync(new StepUpRequest(command.CorrelationId,
                invocation, binding, password), token).ConfigureAwait(false);
        }
        finally { password = string.Empty; }
    }

    private bool TryBegin(CancellationToken caller, out OperationStart start)
    {
        start = default;
        if (!IsConfigured) return false;
        lock (_sync)
        {
            if (_disposed || _pending is not null) return false;
            var session = _sessions!.Current;
            if (!IsUsableSession(session)) return false;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller);
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            var version = ++_generation;
            _pending = cancellation;
            start = new OperationStart(version, cancellation, session);
        }
        RaiseStateChanged();
        return true;
    }

    private async ValueTask PublishAsync(OperationStart start, Action update)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                if (_disposed || start.Version != _generation ||
                    !IsSameSession(start.Session, _sessions?.Current ?? UnauthenticatedSession)) return;
                update();
            }
            RaiseStateChangedCore();
        }).ConfigureAwait(false);
    }

    private async ValueTask EndAsync(OperationStart start)
    {
        CancellationTokenSource? dispose = null;
        lock (_sync)
        {
            if (ReferenceEquals(_pending, start.Cancellation))
            {
                _pending = null;
                if (_generation == start.Version) ++_generation;
                dispose = start.Cancellation;
            }
        }
        dispose?.Dispose();
        await RaiseStateChangedAsync().ConfigureAwait(false);
    }

    private void SessionChanged(object? sender, InteractiveSessionChangedEventArgs args)
    {
        CancelPendingOperations();
        lock (_sync)
        {
            ++_generation;
            _stale = true;
            _page = null;
            _pendingItems = Array.Empty<ProductionRecoveryPendingItem>();
            _selectedItem = null;
            _lastOutcome = null;
            _errorCode = null;
            _statusMessage = "用户会话已变化，请重新刷新。";
            _reasonText = string.Empty;
            _dispositionNote = string.Empty;
            _selectedDisposition = null;
        }
        _ = InvokeOnUiAsync(() =>
        {
            SessionInvalidated?.Invoke(this, EventArgs.Empty);
            RaiseStateChangedCore();
        });
    }

    private void SetSelectedItem(ProductionRecoveryPendingItem? value)
    {
        lock (_sync)
        {
            _selectedItem = value is null ? null : FindCurrentSelection(value, _pendingItems);
        }
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        if (!_dispatcher.CheckAccess)
        {
            _ = InvokeOnUiAsync(RaiseStateChangedCore);
            return;
        }
        RaiseStateChangedCore();
    }

    private ValueTask RaiseStateChangedAsync()
    {
        if (_dispatcher.CheckAccess)
        {
            RaiseStateChangedCore();
            return ValueTask.CompletedTask;
        }
        return _dispatcher.InvokeAsync(RaiseStateChangedCore);
    }

    private void RaiseStateChangedCore()
    {
        foreach (var name in new[]
        {
            nameof(Pending), nameof(Page), nameof(SelectedItem), nameof(SelectedEvent),
            nameof(SelectedCore), nameof(SelectedPayload), nameof(SelectedInspectionId),
            nameof(SelectedRuntimeEpoch), nameof(SelectedControllerEpoch), nameof(SelectedCycleSequence),
            nameof(SelectedDeliveryPhase), nameof(SelectedUncertainty), nameof(SelectedCoreContentHash),
            nameof(SelectedPayloadContentHash), nameof(SelectedPayloadWireContentHash),
            nameof(SelectedPayloadContractVersion), nameof(SelectedPayloadContractHash),
            nameof(LastOutcome), nameof(LastResult), nameof(ReasonText), nameof(DispositionNote),
            nameof(SelectedDisposition), nameof(IsBusy), nameof(IsStale), nameof(HasPending),
            nameof(CanRefresh), nameof(CanRecover), nameof(IsAuthenticated), nameof(FreshnessLabel),
            nameof(StatusMessage), nameof(ErrorCode)
        }) OnPropertyChanged(name);
    }

    private async Task InvokeOnUiAsync(Action action)
    {
        try { await _dispatcher.InvokeAsync(action).ConfigureAwait(false); }
        catch (InvalidOperationException) when (_disposed) { }
    }

    private static ProductionRecoveryPendingItem? FindCurrentSelection(
        ProductionRecoveryPendingItem? previous, IReadOnlyList<ProductionRecoveryPendingItem> items) =>
        previous is null ? null : items.FirstOrDefault(item => SameItem(item, previous));

    private static bool SameItem(ProductionRecoveryPendingItem left,
        ProductionRecoveryPendingItem right) => left.InspectionId == right.InspectionId &&
        left.Position == right.Position && string.Equals(left.EventHash, right.EventHash,
            StringComparison.Ordinal);

    private static InteractiveSession UnauthenticatedSession =>
        new(InteractiveSessionState.Unauthenticated, null, null);

    private static bool IsUsableSession(InteractiveSession session) =>
        session.State == InteractiveSessionState.Authenticated &&
        session.SessionId is { } id && id != Guid.Empty &&
        !string.IsNullOrWhiteSpace(session.PrincipalId);

    private static bool IsSameSession(InteractiveSession left, InteractiveSession right) =>
        IsUsableSession(left) && IsUsableSession(right) && left.SessionId == right.SessionId &&
        string.Equals(left.PrincipalId, right.PrincipalId, StringComparison.Ordinal);

    private static CommandInvocation CreateInvocation(InteractiveSession session, Guid? grant = null) =>
        new(CommandSource.PhysicalConsole, session.PrincipalId, session.SessionId, grant);

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string SafeReason(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
        value.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.')) ? fallback : value;

    public string StatusMessage
    {
        get { lock (_sync) return _statusMessage; }
        private set { lock (_sync) _statusMessage = value; OnPropertyChanged(); }
    }

    public string? ErrorCode
    {
        get { lock (_sync) return _errorCode; }
        private set { lock (_sync) _errorCode = value; OnPropertyChanged(); }
    }
}
