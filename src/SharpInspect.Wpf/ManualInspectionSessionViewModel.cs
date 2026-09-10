using System.Collections.ObjectModel;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Presentation adapter for the bounded manual-inspection session boundary.
/// Runtime remains the authority for exact recipe identity, session/run ids,
/// camera ownership and the final outcome.  This type only keeps a selected
/// immutable source, submits typed commands, and displays the public query.
/// </summary>
public sealed class ManualInspectionSessionViewModel : ObservableObject, IAsyncDisposable
{
    public const int HistoryPageSize = 20;

    private enum OperationKind
    {
        Refresh,
        Start,
        Run,
        Exit,
        History
    }

    private readonly record struct OperationStart(
        long Version,
        OperationKind Kind,
        CancellationTokenSource Cancellation,
        InteractiveSession Session);

    private readonly IStationRuntime? _runtime;
    private readonly IManualInspectionSessionService? _manual;
    private readonly IInteractiveSessionService? _sessions;
    private readonly IRecipeDraftHistoryQuery? _draftQuery;
    private readonly IReleasedRecipeQuery? _releasedQuery;
    private readonly IRecipeActivationQuery? _activationQuery;
    private readonly IStepUpAuthentication? _stepUp;
    private readonly IManualInspectionHistoryQuery? _historyQuery;
    private readonly IUiDispatcher _dispatcher;
    private readonly object _sync = new();
    private readonly ObservableCollection<ManualRecipeSelectionOption> _draftOptions = new();
    private readonly ObservableCollection<ManualRecipeSelectionOption> _releasedOptions = new();
    private readonly ObservableCollection<ManualInspectionHistoryRow> _historyRows = new();
    private readonly ReadOnlyObservableCollection<ManualRecipeSelectionOption> _readOnlyDraftOptions;
    private readonly ReadOnlyObservableCollection<ManualRecipeSelectionOption> _readOnlyReleasedOptions;
    private readonly ReadOnlyObservableCollection<ManualInspectionHistoryRow> _readOnlyHistoryRows;

    private CancellationTokenSource? _activeCancellation;
    private long _operationVersion;
    private bool _isBusy;
    private bool _disposed;
    private bool _snapshotAvailable;
    private InteractiveSession _session;
    private ManualInspectionAccess? _access;
    private ManualInspectionSessionSnapshot? _snapshot;
    private ManualRecipeSelection? _selection;
    private ManualRecipeSelectionOption? _selectedDraftOption;
    private ManualRecipeSelectionOption? _selectedReleasedOption;
    private RecipeActivationReference? _currentActive;
    private RecipeActivationReference? _expectedActive;
    private RuntimeCommandOutcome? _lastCommandOutcome;
    private string _startReason = string.Empty;
    private string _runReason = string.Empty;
    private string _exitReason = string.Empty;
    private string _partIdentityText = string.Empty;
    private string _statusMessage;
    private string? _errorCode;
    private ManualInspectionHistoryRow? _selectedHistoryRow;
    private StepUpBinding? _lastStepUpBinding;
    private bool _historyAvailable;
    private bool _historyRecoveryRequired;
    private string _historyStatusMessage;
    private string? _historyErrorCode;
    private long? _historyThroughPosition;
    private long? _historyNextAfterPosition;
    private int _historyPageNumber;

    public ManualInspectionSessionViewModel()
        : this(runtime: null, manualService: null, sessions: null,
            draftQuery: null, releasedQuery: null, activationQuery: null,
            dispatcher: new DispatcherUiDispatcher(), stepUpAuthentication: null,
            historyQuery: null)
    {
    }

    public ManualInspectionSessionViewModel(
        IStationRuntime? runtime,
        IManualInspectionSessionService? manualService,
        IInteractiveSessionService? sessions,
        IRecipeDraftHistoryQuery? draftQuery,
        IReleasedRecipeQuery? releasedQuery,
        IRecipeActivationQuery? activationQuery,
        IUiDispatcher? dispatcher = null,
        IStepUpAuthentication? stepUpAuthentication = null,
        IManualInspectionHistoryQuery? historyQuery = null)
    {
        _runtime = runtime;
        _manual = manualService;
        _sessions = sessions;
        _draftQuery = draftQuery;
        _releasedQuery = releasedQuery;
        _activationQuery = activationQuery;
        _stepUp = stepUpAuthentication;
        _historyQuery = historyQuery;
        _dispatcher = dispatcher ?? new DispatcherUiDispatcher();
        _readOnlyDraftOptions = new ReadOnlyObservableCollection<ManualRecipeSelectionOption>(_draftOptions);
        _readOnlyReleasedOptions = new ReadOnlyObservableCollection<ManualRecipeSelectionOption>(_releasedOptions);
        _readOnlyHistoryRows = new ReadOnlyObservableCollection<ManualInspectionHistoryRow>(_historyRows);
        _session = sessions?.Current ?? UnauthenticatedSession;
        _statusMessage = IsConfigured
            ? "请先刷新状态并明确选择 Draft 或 Released 版本；打开页面不会自动开始人工检测。"
            : "人工检测不可用：Runtime、人工会话查询或当前用户会话未完整配置。";
        _historyStatusMessage = _historyQuery is null
            ? "人工检测历史不可用：未配置只读历史查询服务。"
            : "尚未查询人工检测历史；请点击刷新。";
        _errorCode = IsConfigured ? null : "ManualInspectionRuntimeUnavailable";

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => CanRefresh);
        StartCommand = new AsyncRelayCommand(() => StartManualInspectionSessionAsync(), () => CanStart);
        RunOneCommand = new AsyncRelayCommand(() => RunOneAsync(), () => CanRunOne);
        GracefulExitCommand = new AsyncRelayCommand(() => GracefulExitAsync(), () => CanExit);
        AbortCommand = new AsyncRelayCommand(() => AbortAsync(), () => CanExit);
        RefreshHistoryCommand = new AsyncRelayCommand(() => RefreshHistoryAsync(), () => CanRefreshHistory);
        NextHistoryPageCommand = new AsyncRelayCommand(() => NextHistoryPageAsync(), () => CanNextHistoryPage);
        CancelCommand = new AsyncRelayCommand(() =>
        {
            CancelPendingOperations();
            return Task.CompletedTask;
        }, () => IsBusy);

        if (_sessions is not null)
            _sessions.Changed += SessionChanged;
    }

    /// <summary>Command boundary used by the panel and by shell hosts.</summary>
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand RunOneCommand { get; }
    public AsyncRelayCommand GracefulExitCommand { get; }
    public AsyncRelayCommand AbortCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public AsyncRelayCommand RefreshHistoryCommand { get; }
    public AsyncRelayCommand NextHistoryPageCommand { get; }

    // Stable aliases for hosts that use verb-oriented names.
    public AsyncRelayCommand StartSessionCommand => StartCommand;

    /// <summary>The panel clears transient identity text when session authority changes.</summary>
    internal event EventHandler? SessionInvalidated;

    public bool IsConfigured => _runtime is not null && _manual is not null && _sessions is not null;
    public bool HasSourceQueries => _draftQuery is not null || _releasedQuery is not null;
    public bool HasHistoryQuery => _historyQuery is not null;
    public bool IsBusy
    {
        get { lock (_sync) return _isBusy; }
    }

    public bool IsAuthenticated => IsUsableSession(CurrentSession);
    public InteractiveSession CurrentSession => _sessions?.Current ?? ReadSession();
    public bool IsSnapshotFresh
    {
        get { lock (_sync) return _snapshotAvailable; }
    }

    public bool IsSnapshotUnavailable => !IsSnapshotFresh;
    public ManualInspectionAccess? Access
    {
        get { lock (_sync) return _access; }
    }

    public ManualInspectionAccess? CurrentAccess => Access;
    public bool RequiresStepUp => Access?.RequiresStepUp == true;
    public ManualInspectionSessionSnapshot? Snapshot
    {
        get { lock (_sync) return _snapshot; }
    }

    public ManualInspectionSessionSnapshot? CurrentSnapshot => Snapshot;
    public ManualInspectionSessionPhase Phase => Snapshot?.Phase ?? ManualInspectionSessionPhase.Idle;
    public ManualInspectionRestorationState Restoration =>
        Snapshot?.Restoration ?? ManualInspectionRestorationState.NotRequired;
    public bool RecoveryRequired => Snapshot?.RecoveryRequired == true ||
        Phase == ManualInspectionSessionPhase.RecoveryBlocked ||
        Restoration == ManualInspectionRestorationState.RecoveryBlocked;
    public bool Ready => false;
    public bool ProductionAuthority => false;
    public bool IsSessionActive => Snapshot?.IsSessionActive == true;
    public Guid? CurrentManualSessionId => Snapshot?.SessionId;
    public Guid? CurrentManualRunId => Snapshot?.CurrentManualRunId;
    public Guid? LastManualRunId => Snapshot?.LastManualRunId;

    public ReadOnlyObservableCollection<ManualRecipeSelectionOption> DraftOptions => _readOnlyDraftOptions;
    public ReadOnlyObservableCollection<ManualRecipeSelectionOption> ReleasedOptions => _readOnlyReleasedOptions;
    public ReadOnlyObservableCollection<ManualInspectionHistoryRow> HistoryRows => _readOnlyHistoryRows;

    public ManualInspectionHistoryRow? SelectedHistoryRow
    {
        get { lock (_sync) return _selectedHistoryRow; }
        set
        {
            lock (_sync)
            {
                if (_disposed) return;
                _selectedHistoryRow = value is null || _historyRows.Contains(value) ? value : null;
            }
            NotifyStateChanged();
        }
    }

    public bool IsHistoryAvailable
    {
        get { lock (_sync) return _historyAvailable; }
    }

    public bool HistoryRecoveryRequired
    {
        get { lock (_sync) return _historyRecoveryRequired; }
    }

    public string HistoryStatusMessage
    {
        get { lock (_sync) return _historyStatusMessage; }
    }

    public string? HistoryErrorCode
    {
        get { lock (_sync) return _historyErrorCode; }
    }

    public long? HistoryThroughPosition
    {
        get { lock (_sync) return _historyThroughPosition; }
    }

    public long? HistoryNextAfterPosition
    {
        get { lock (_sync) return _historyNextAfterPosition; }
    }

    public int HistoryPageNumber
    {
        get { lock (_sync) return _historyPageNumber; }
    }

    /// <summary>Snapshot of the exact Draft revisions returned by the read-only query.</summary>
    public IReadOnlyList<RecipeDraftRevision> Drafts =>
        _draftOptions.Select(value => value.Draft).Where(value => value is not null).Cast<RecipeDraftRevision>().ToArray();

    /// <summary>Snapshot of the exact released revisions returned by the read-only query.</summary>
    public IReadOnlyList<ReleasedRecipe> ReleasedRecipes =>
        _releasedOptions.Select(value => value.Released).Where(value => value is not null).Cast<ReleasedRecipe>().ToArray();

    public ManualRecipeSelection? Selection
    {
        get { lock (_sync) return _selection; }
        set => SetSelection(value);
    }

    public ManualRecipeSelectionOption? SelectedDraftOption
    {
        get { lock (_sync) return _selectedDraftOption; }
        set
        {
            if (value is null)
            {
                lock (_sync)
                {
                    _selectedDraftOption = null;
                    if (_selectedReleasedOption is null) _selection = null;
                }
                NotifyStateChanged();
                return;
            }
            SelectDraft(value);
        }
    }

    public ManualRecipeSelectionOption? SelectedReleasedOption
    {
        get { lock (_sync) return _selectedReleasedOption; }
        set
        {
            if (value is null)
            {
                lock (_sync)
                {
                    _selectedReleasedOption = null;
                    if (_selectedDraftOption is null) _selection = null;
                }
                NotifyStateChanged();
                return;
            }
            SelectReleased(value);
        }
    }

    public RecipeDraftRevision? SelectedDraftRevision => SelectedDraftOption?.Draft;
    public ReleasedRecipe? SelectedReleasedRecipe => SelectedReleasedOption?.Released;

    public RecipeDraftRevision? SelectedDraft
    {
        get => SelectedDraftRevision;
        set
        {
            if (value is null)
            {
                SelectedDraftOption = null;
                return;
            }
            SelectDraft(new ManualRecipeSelectionOption(value));
        }
    }

    public ReleasedRecipe? SelectedReleased
    {
        get => SelectedReleasedRecipe;
        set
        {
            if (value is null)
            {
                SelectedReleasedOption = null;
                return;
            }
            SelectReleased(new ManualRecipeSelectionOption(value));
        }
    }

    public RecipeActivationReference? CurrentActive
    {
        get { lock (_sync) return _currentActive; }
    }

    public RecipeActivationReference? ExpectedActive
    {
        get { lock (_sync) return _expectedActive; }
        set
        {
            lock (_sync)
            {
                if (_disposed) return;
                _expectedActive = value;
            }
            NotifyStateChanged();
        }
    }

    public string StartReason
    {
        get { lock (_sync) return _startReason; }
        set => SetText(ref _startReason, value, nameof(StartReason));
    }

    public string RunReason
    {
        get { lock (_sync) return _runReason; }
        set => SetText(ref _runReason, value, nameof(RunReason));
    }

    public string ExitReason
    {
        get { lock (_sync) return _exitReason; }
        set => SetText(ref _exitReason, value, nameof(ExitReason));
    }

    public string PartIdentityText
    {
        get { lock (_sync) return _partIdentityText; }
        set => SetText(ref _partIdentityText, value, nameof(PartIdentityText));
    }

    public RuntimeCommandOutcome? LastCommandOutcome
    {
        get { lock (_sync) return _lastCommandOutcome; }
    }

    public string LastCommandSummary => LastCommandOutcome is { } outcome
        ? $"{outcome.Disposition} · {outcome.ReasonCode} · 审计={outcome.Audit}"
        : "尚未提交人工检测命令。";

    public string StatusMessage
    {
        get { lock (_sync) return _statusMessage; }
    }

    public string? ErrorCode
    {
        get { lock (_sync) return _errorCode; }
    }

    public string SelectionSummary
    {
        get
        {
            var selection = Selection;
            return selection switch
            {
                { Kind: ManualRecipeSourceKind.Draft, DraftId: { } draftId, DraftRevision: { } revision } =>
                    $"Draft · {draftId:D} · Revision={revision} · Hash={selection.DraftRevisionContentHash}",
                { Kind: ManualRecipeSourceKind.Released, Recipe: { } recipe, ReleaseId: { } releaseId } =>
                    $"Released · {recipe.Id} v{recipe.Version} · Release={releaseId:D} · Hash={selection.ReleaseRecordContentHash}",
                _ => "尚未选择精确 Draft 或 Released 版本。"
            };
        }
    }

    public string CurrentActiveSummary => CurrentActive is { } active
        ? $"Position={active.Position} · ActivationId={active.ActivationId:D} · Hash={active.ContentHash}"
        : "当前没有可用的 Active 生产基线。";

    public string ExpectedActiveSummary => ExpectedActive is { } active
        ? $"Position={active.Position} · ActivationId={active.ActivationId:D} · Hash={active.ContentHash}"
        : "未设置 ExpectedActive；Runtime 将按无预期 Active 校验。";

    public string PhaseSummary => Snapshot is { } snapshot
        ? $"Phase={snapshot.Phase} · Session={snapshot.SessionId?.ToString("D") ?? "无"} · " +
          $"Revision={snapshot.Revision} · Reason={snapshot.ReasonCode}"
        : "尚未读取人工检测 Runtime 快照。";

    public string RestorationSummary => Restoration switch
    {
        ManualInspectionRestorationState.Pending => "Runtime 正在恢复 Active 配置；页面不会宣称已恢复。",
        ManualInspectionRestorationState.Restored => "人工检测已退出，Runtime 已恢复并读回 Active 配置。",
        ManualInspectionRestorationState.NoActiveBaselineClosed => "人工检测已关闭；没有 Active 基线可恢复。",
        ManualInspectionRestorationState.RecoveryBlocked => "Active 配置恢复被阻断；Ready 保持 false。",
        _ => "当前没有需要恢复的 Active 配置。"
    };

    public bool CanRefresh => IsConfigured && IsAuthenticated && !IsBusy && !_disposed;
    public bool CanStart => IsConfigured && IsAuthenticated && !IsBusy && !_disposed &&
        Selection is not null && !string.IsNullOrWhiteSpace(StartReason) && !IsSessionActive;
    public bool CanRunOne => IsConfigured && IsAuthenticated && !IsBusy && !_disposed &&
        Snapshot is { SessionId: not null, Phase: ManualInspectionSessionPhase.ReadyForRun } &&
        !string.IsNullOrWhiteSpace(RunReason);
    public bool CanExit => IsConfigured && IsAuthenticated && !IsBusy && !_disposed &&
        Snapshot is { SessionId: not null } snapshot && snapshot.IsSessionActive &&
        !string.IsNullOrWhiteSpace(ExitReason);

    public bool CanRefreshHistory => HasHistoryQuery && IsAuthenticated && !_disposed && !IsBusy;

    public bool CanNextHistoryPage => HasHistoryQuery && IsHistoryAvailable && IsAuthenticated &&
        !_disposed && !IsBusy && HistoryNextAfterPosition.HasValue;

    internal StepUpBinding? LastStepUpBinding
    {
        get { lock (_sync) return _lastStepUpBinding; }
    }

    public void SelectDraft(ManualRecipeSelectionOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (option.Draft is null) throw new ArgumentException("ManualDraftSelectionRequired", nameof(option));
        lock (_sync)
        {
            if (_disposed) return;
            _selectedDraftOption = option;
            _selectedReleasedOption = null;
            _selection = option.Selection;
        }
        NotifyStateChanged();
    }

    public void SelectReleased(ManualRecipeSelectionOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (option.Released is null)
            throw new ArgumentException("ManualReleasedSelectionRequired", nameof(option));
        lock (_sync)
        {
            if (_disposed) return;
            _selectedReleasedOption = option;
            _selectedDraftOption = null;
            _selection = option.Selection;
        }
        NotifyStateChanged();
    }

    public void ClearSelection()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _selection = null;
            _selectedDraftOption = null;
            _selectedReleasedOption = null;
        }
        NotifyStateChanged();
    }

    public bool UseCurrentActiveAsExpected()
    {
        RecipeActivationReference? active;
        lock (_sync) active = _currentActive;
        if (active is null) return false;
        ExpectedActive = active;
        return true;
    }

    public void ClearExpectedActive() => ExpectedActive = null;

    /// <summary>
    /// Reads the public access/snapshot projections and bounded source histories.
    /// It never submits Start as a side effect.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            ApplyUnavailable("ManualInspectionRuntimeUnavailable",
                "人工检测不可用：Runtime、人工会话查询或当前用户会话未完整配置。", false);
            return;
        }

        var start = Begin(OperationKind.Refresh, cancellationToken);
        if (!start.HasValue) return;
        try
        {
            if (!IsUsableSession(start.Value.Session))
            {
                ApplyFailure(start.Value, "ManualInspectionAuthenticationRequired",
                    "请先登录，再读取人工检测状态。", true);
                return;
            }

            await RefreshPublicStateAsync(start.Value.Cancellation.Token,
                readSources: true).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            ApplyFailure(start.Value, "ManualInspectionRefreshCancelled",
                "状态刷新已取消；页面没有据此改变 Runtime 会话。", false);
        }
        catch
        {
            ApplyFailure(start.Value, "ManualInspectionSnapshotRefreshFailed",
                "人工检测状态读取失败，请重试。", true);
        }
        finally
        {
            Complete(start.Value);
        }
    }

    /// <summary>Reads one bounded history page on explicit operator request.</summary>
    public async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (_historyQuery is null)
        {
            ApplyHistoryUnavailable("ManualInspectionHistoryUnavailable",
                "人工检测历史不可用：未配置只读历史查询服务。", clearRows: false);
            return;
        }
        if (!IsAuthenticated)
        {
            ApplyHistoryUnavailable("ManualInspectionAuthenticationRequired",
                "请先登录，再读取人工检测历史。", clearRows: true);
            return;
        }

        var start = Begin(OperationKind.History, cancellationToken);
        if (!start.HasValue) return;
        try
        {
            await ReadHistoryPageAsync(start.Value.Cancellation.Token,
                pageNumber: 1, afterPosition: 0, throughPosition: null).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            ApplyFailure(start.Value, "ManualInspectionHistoryRefreshCancelled",
                "人工检测历史刷新已取消。", clearSnapshot: false);
        }
        finally
        {
            Complete(start.Value);
        }
    }

    /// <summary>Moves to the next history page using the query's stable upper bound.</summary>
    public async Task NextHistoryPageAsync(CancellationToken cancellationToken = default)
    {
        long afterPosition;
        long? throughPosition;
        int pageNumber;
        lock (_sync)
        {
            if (_disposed || _historyQuery is null || !_historyAvailable || !IsAuthenticated || _isBusy ||
                !_historyNextAfterPosition.HasValue) return;
            afterPosition = _historyNextAfterPosition.Value;
            throughPosition = _historyThroughPosition;
            pageNumber = _historyPageNumber + 1;
        }

        var start = Begin(OperationKind.History, cancellationToken);
        if (!start.HasValue) return;
        try
        {
            await ReadHistoryPageAsync(start.Value.Cancellation.Token,
                pageNumber: pageNumber, afterPosition: afterPosition,
                throughPosition: throughPosition).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            ApplyFailure(start.Value, "ManualInspectionHistoryRefreshCancelled",
                "人工检测历史翻页已取消。", clearSnapshot: false);
        }
        finally
        {
            Complete(start.Value);
        }
    }

    public Task<RuntimeCommandOutcome?> StartManualInspectionSessionAsync(
        CancellationToken cancellationToken = default) =>
        StartManualInspectionSessionAsync(Selection, ExpectedActive, StartReason, null, cancellationToken);

    public Task<RuntimeCommandOutcome?> StartManualInspectionSessionAsync(
        string? stepUpPassword, CancellationToken cancellationToken = default) =>
        StartManualInspectionSessionAsync(Selection, ExpectedActive, StartReason,
            stepUpPassword, cancellationToken);

    public Task<RuntimeCommandOutcome?> StartAsync(CancellationToken cancellationToken = default) =>
        StartManualInspectionSessionAsync(cancellationToken);

    public Task<RuntimeCommandOutcome?> StartAsync(string? stepUpPassword,
        CancellationToken cancellationToken = default) =>
        StartManualInspectionSessionAsync(stepUpPassword, cancellationToken);

    public Task<RuntimeCommandOutcome?> StartManualInspectionSessionAsync(
        ManualRecipeSelection? selection, RecipeActivationReference? expectedActive,
        string? reason, CancellationToken cancellationToken = default)
        => StartManualInspectionSessionAsync(selection, expectedActive, reason, null, cancellationToken);

    public Task<RuntimeCommandOutcome?> StartManualInspectionSessionAsync(
        ManualRecipeSelection? selection, RecipeActivationReference? expectedActive,
        string? reason, string? stepUpPassword,
        CancellationToken cancellationToken = default)
    {
        if (selection is null)
        {
            ApplyInputFailure("ManualRecipeSelectionRequired", "开始人工检测必须明确选择 Draft 或 Released 版本。");
            return Task.FromResult<RuntimeCommandOutcome?>(null);
        }
        if (!TryReason(reason, out var suppliedReason))
        {
            ApplyInputFailure("ManualStartReasonRequired", "开始人工检测必须记录明确理由。");
            return Task.FromResult<RuntimeCommandOutcome?>(null);
        }

        var exactSelection = selection;
        return SubmitCommandAsync(OperationKind.Start, (session, correlationId, grantId) =>
            new StartManualInspectionSessionCommand(correlationId, CreateInvocation(session, grantId),
                exactSelection, expectedActive, suppliedReason),
            Permission.RunManualInspection, AuditedCommandKind.StartManualInspectionSession,
            stepUpPassword, "ManualInspectionStartUnavailable",
            "当前人工检测不能开始，请先刷新状态并确认权限。", cancellationToken);
    }

    public Task<RuntimeCommandOutcome?> RunOneAsync(CancellationToken cancellationToken = default) =>
        RunManualInspectionAsync(RunReason, PartIdentityText, null, cancellationToken);

    public Task<RuntimeCommandOutcome?> RunOneAsync(string? stepUpPassword,
        CancellationToken cancellationToken = default) =>
        RunManualInspectionAsync(RunReason, PartIdentityText, stepUpPassword, cancellationToken);

    public Task<RuntimeCommandOutcome?> RunManualInspectionAsync(
        string? reason, string? partIdentity = null,
        CancellationToken cancellationToken = default)
        => RunManualInspectionAsync(reason, partIdentity, null, cancellationToken);

    public Task<RuntimeCommandOutcome?> RunManualInspectionAsync(
        string? reason, string? partIdentity, string? stepUpPassword,
        CancellationToken cancellationToken = default)
    {
        if (!TryReason(reason, out var suppliedReason))
        {
            ApplyInputFailure("ManualRunReasonRequired", "执行一次人工检测必须记录明确理由。");
            return Task.FromResult<RuntimeCommandOutcome?>(null);
        }

        Guid sessionId;
        lock (_sync) sessionId = _snapshot?.SessionId ?? Guid.Empty;
        if (sessionId == Guid.Empty)
        {
            ApplyInputFailure("ManualInspectionSessionRequired", "请先由 Runtime 显式进入人工检测会话。");
            return Task.FromResult<RuntimeCommandOutcome?>(null);
        }

        ManualPartIdentityInput? identity = null;
        if (!string.IsNullOrWhiteSpace(partIdentity))
        {
            try { identity = new ManualPartIdentityInput(partIdentity.Trim()); }
            catch (ArgumentException)
            {
                ApplyInputFailure("ManualPartIdentityInvalid", "人工检测部件标识无效，请修正后重试。");
                return Task.FromResult<RuntimeCommandOutcome?>(null);
            }
        }

        return SubmitCommandAsync(OperationKind.Run, (session, correlationId, grantId) =>
            new RunManualInspectionCommand(correlationId, CreateInvocation(session, grantId),
                sessionId, identity, suppliedReason),
            Permission.RunManualInspection, AuditedCommandKind.RunManualInspection,
            stepUpPassword,
            "ManualInspectionRunUnavailable", "当前人工检测会话不允许执行一次检测。", cancellationToken);
    }

    public Task<RuntimeCommandOutcome?> GracefulExitAsync(CancellationToken cancellationToken = default) =>
        ExitAsync(ManualInspectionExitMode.Graceful, ExitReason, null, cancellationToken);

    public Task<RuntimeCommandOutcome?> GracefulExitAsync(string? stepUpPassword,
        CancellationToken cancellationToken = default) =>
        ExitAsync(ManualInspectionExitMode.Graceful, ExitReason, stepUpPassword, cancellationToken);

    public Task<RuntimeCommandOutcome?> ExitAsync(CancellationToken cancellationToken = default) =>
        GracefulExitAsync(cancellationToken);

    public Task<RuntimeCommandOutcome?> AbortAsync(CancellationToken cancellationToken = default) =>
        ExitAsync(ManualInspectionExitMode.Abort, ExitReason, null, cancellationToken);

    public Task<RuntimeCommandOutcome?> AbortAsync(string? stepUpPassword,
        CancellationToken cancellationToken = default) =>
        ExitAsync(ManualInspectionExitMode.Abort, ExitReason, stepUpPassword, cancellationToken);

    public Task<RuntimeCommandOutcome?> ExitAsync(ManualInspectionExitMode mode,
        string? reason, CancellationToken cancellationToken = default)
        => ExitAsync(mode, reason, null, cancellationToken);

    public Task<RuntimeCommandOutcome?> ExitAsync(ManualInspectionExitMode mode,
        string? reason, string? stepUpPassword,
        CancellationToken cancellationToken = default)
    {
        if (!TryReason(reason, out var suppliedReason))
        {
            ApplyInputFailure("ManualExitReasonRequired", "退出人工检测必须记录明确理由。");
            return Task.FromResult<RuntimeCommandOutcome?>(null);
        }

        Guid sessionId;
        lock (_sync) sessionId = _snapshot?.SessionId ?? Guid.Empty;
        if (sessionId == Guid.Empty)
        {
            ApplyInputFailure("ManualInspectionSessionRequired", "当前没有可退出的人工检测会话。");
            return Task.FromResult<RuntimeCommandOutcome?>(null);
        }

        return SubmitCommandAsync(OperationKind.Exit, (session, correlationId, grantId) =>
            new ExitManualInspectionSessionCommand(correlationId, CreateInvocation(session, grantId),
                sessionId, mode, suppliedReason),
            Permission.RunManualInspection, AuditedCommandKind.ExitManualInspectionSession,
            stepUpPassword,
            "ManualInspectionExitUnavailable", "当前人工检测会话不能退出，请先刷新状态。", cancellationToken);
    }

    /// <summary>
    /// Cancels only presentation preflight/waiting work. Once a typed command is
    /// handed to Runtime, this method never cancels that command's token.
    /// </summary>
    public void CancelPendingOperations()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (!_isBusy)
            {
                cancellation = null;
            }
            else
            {
                _operationVersion++;
                cancellation = _activeCancellation;
                _activeCancellation = null;
                _isBusy = false;
                _errorCode = null;
                _statusMessage = "页面请求已取消；Runtime 已接受的人工检测不会被页面取消。";
            }
        }
        cancellation?.Cancel();
        NotifyStateChanged();
    }

    public void ClearSensitiveInputs()
    {
        lock (_sync)
        {
            _partIdentityText = string.Empty;
            _startReason = string.Empty;
            _runReason = string.Empty;
            _exitReason = string.Empty;
            _lastStepUpBinding = null;
        }
        NotifyStateChanged();
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _operationVersion++;
            cancellation = _activeCancellation;
            _activeCancellation = null;
            _isBusy = false;
            _lastStepUpBinding = null;
        }
        cancellation?.Cancel();
        if (_sessions is not null) _sessions.Changed -= SessionChanged;
        cancellation?.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task RefreshPublicStateAsync(CancellationToken cancellationToken,
        bool readSources)
    {
        var session = CurrentSession;
        var invocation = CreateInvocation(session);
        var access = await _manual!.GetAccessAsync(invocation, cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await _manual.GetSnapshotAsync(invocation, cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();

        if (readSources)
        {
            await ReadSourcesAsync(cancellationToken).ConfigureAwait(true);
            await ReadCurrentActiveAsync(cancellationToken).ConfigureAwait(true);
            await ReadHistoryPageAsync(cancellationToken, pageNumber: 1,
                afterPosition: 0, throughPosition: null).ConfigureAwait(true);
        }

        await ApplyPublicReadAsync(access, snapshot).ConfigureAwait(true);
    }

    private async Task ReadSourcesAsync(CancellationToken cancellationToken)
    {
        if (_draftQuery is not null)
        {
            var page = await _draftQuery.QueryAsync(new RecipeDraftFilter(PageSize: 50), cancellationToken)
                .ConfigureAwait(true);
            if (page.Available)
            {
                var options = page.Revisions.Select(value => new ManualRecipeSelectionOption(value)).ToArray();
                await _dispatcher.InvokeAsync(() => Replace(_draftOptions, options)).ConfigureAwait(true);
            }
        }
        if (_releasedQuery is not null)
        {
            var page = await _releasedQuery.QueryAsync(new ReleasedRecipeFilter(PageSize: 50), cancellationToken)
                .ConfigureAwait(true);
            if (page.Available)
            {
                var options = page.Recipes.Select(value => new ManualRecipeSelectionOption(value)).ToArray();
                await _dispatcher.InvokeAsync(() => Replace(_releasedOptions, options)).ConfigureAwait(true);
            }
        }
    }

    private async Task ReadCurrentActiveAsync(CancellationToken cancellationToken)
    {
        if (_activationQuery is null) return;
        var current = await _activationQuery.ReadCurrentAsync(cancellationToken).ConfigureAwait(true);
        RecipeActivationReference? active = null;
        if (current.Available && !current.RecoveryRequired && current.Record is { CanBeActive: true } record)
            active = record.Reference;
        lock (_sync) _currentActive = active;
    }

    private async Task ReadHistoryPageAsync(CancellationToken cancellationToken,
        int pageNumber, long afterPosition, long? throughPosition)
    {
        if (_historyQuery is null) return;
        ManualInspectionHistoryPage page;
        try
        {
            page = await _historyQuery.QueryAsync(new ManualInspectionHistoryFilter(
                    SessionId: null, RunId: null, AfterPosition: afterPosition,
                    ThroughPosition: throughPosition, PageSize: HistoryPageSize), cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            ApplyHistoryUnavailable("ManualInspectionHistoryReadFailed",
                "人工检测历史读取失败；页面保留既有历史页。", clearRows: false);
            return;
        }

        try
        {
            if (!page.Available)
            {
                ApplyHistoryUnavailable(SafeReason(page.ReasonCode,
                    "ManualInspectionHistoryUnavailable"),
                    "人工检测历史当前不可用；页面没有据此宣称检测状态。", clearRows: true);
                return;
            }
            if (page.Events is null || page.Runs is null ||
                page.Events.Count > HistoryPageSize || page.Runs.Count > HistoryPageSize ||
                (throughPosition.HasValue && page.ThroughPosition != throughPosition.Value) ||
                page.ThroughPosition < afterPosition ||
                (page.NextAfterPosition.HasValue &&
                    (page.NextAfterPosition.Value <= afterPosition ||
                     page.NextAfterPosition.Value > page.ThroughPosition)) ||
                page.Events.Any(item => item is null || item.Position <= afterPosition ||
                    item.Position > page.ThroughPosition) ||
                page.Runs.Any(item => item is null || item.Position <= afterPosition ||
                    item.Position > page.ThroughPosition) ||
                (page.Events.Count == 0 && page.Runs.Count == 0 && page.NextAfterPosition.HasValue))
                throw new InvalidOperationException("ManualInspectionHistoryPageInvalid");

            var rows = ManualInspectionHistoryRow.CreatePage(page);
            await _dispatcher.InvokeAsync(() =>
            {
                lock (_sync)
                {
                    if (_disposed) return;
                    _historyRows.Clear();
                    foreach (var row in rows) _historyRows.Add(row);
                    _selectedHistoryRow = null;
                    _historyAvailable = true;
                    _historyRecoveryRequired = page.RecoveryRequired;
                    _historyThroughPosition = page.ThroughPosition;
                    _historyNextAfterPosition = page.NextAfterPosition;
                    _historyPageNumber = pageNumber;
                    _historyErrorCode = null;
                    _historyStatusMessage = page.RecoveryRequired
                        ? $"已载入人工检测历史第 {pageNumber} 页 · 上界 {page.ThroughPosition} · 当前需要恢复。"
                        : $"已载入人工检测历史第 {pageNumber} 页 · 上界 {page.ThroughPosition}。";
                }
                RaiseStateChanged();
            }).ConfigureAwait(true);
        }
        catch (InvalidOperationException)
        {
            ApplyHistoryUnavailable("ManualInspectionHistoryPageInvalid",
                "人工检测历史查询结果无效；请刷新后重试。", clearRows: true);
        }
        catch
        {
            ApplyHistoryUnavailable("ManualInspectionHistoryPageInvalid",
                "人工检测历史查询结果无效；请刷新后重试。", clearRows: true);
        }
    }

    private async Task ApplyPublicReadAsync(ManualInspectionAccess access,
        ManualInspectionSessionReadResult snapshotResult)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                if (_disposed) return;
                _access = access;
                _snapshotAvailable = snapshotResult.Available && snapshotResult.Snapshot is not null;
                _snapshot = snapshotResult.Available ? snapshotResult.Snapshot : null;
                _errorCode = snapshotResult.Available ? null : SafeReason(snapshotResult.ReasonCode,
                    "ManualInspectionSnapshotUnavailable");
                _statusMessage = snapshotResult.Available
                    ? "人工检测状态已刷新；开始、单次检测和退出均须显式提交 typed command。"
                    : "人工检测状态当前不可用；页面没有据此宣称会话或恢复完成。";
            }
            RaiseStateChanged();
        }).ConfigureAwait(true);
    }

    private async Task<RuntimeCommandOutcome?> SubmitCommandAsync(
        OperationKind kind,
        Func<InteractiveSession, Guid, Guid?, RuntimeCommand> commandFactory,
        Permission permission,
        AuditedCommandKind commandKind,
        string? stepUpPassword,
        string unavailableCode,
        string unavailableMessage,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            ApplyUnavailable(unavailableCode, unavailableMessage, false);
            return null;
        }
        if (!IsUsableSession(CurrentSession))
        {
            ApplyInputFailure("ManualInspectionAuthenticationRequired", "请先登录，再提交人工检测命令。");
            return null;
        }

        var start = Begin(kind, cancellationToken);
        if (!start.HasValue) return null;
        try
        {
            if (!SameSession(start.Value.Session, CurrentSession))
            {
                ApplyFailure(start.Value, "ManualInspectionSessionChanged",
                    "会话已变化；本次人工检测命令未提交。", true);
                return null;
            }

            var access = await _manual!.GetAccessAsync(CreateInvocation(start.Value.Session),
                start.Value.Cancellation.Token).ConfigureAwait(true);
            await ApplyAccessAsync(start.Value, access).ConfigureAwait(true);
            if (!access.CanRun)
            {
                ApplyFailure(start.Value, SafeReason(access.ReasonCode, unavailableCode),
                    "Runtime 未批准当前人工检测操作；页面没有预先改变状态。", false);
                return null;
            }
            if (access.RequiresStepUp)
            {
                var stepUp = _stepUp;
                if (stepUp is null)
                {
                    ApplyFailure(start.Value, "ManualInspectionStepUpUnavailable",
                        "当前人工检测操作需要 Step-Up，但页面未配置凭据确认服务。", false);
                    return null;
                }
                if (string.IsNullOrWhiteSpace(stepUpPassword))
                {
                    ApplyFailure(start.Value, "ManualInspectionStepUpRequired",
                        "当前人工检测操作需要输入当前密码再次确认。", false);
                    return null;
                }

                var correlationId = Guid.NewGuid();
                var initialCommand = commandFactory(start.Value.Session, correlationId, null);
                var binding = new StepUpBinding(permission, correlationId,
                    initialCommand switch
                    {
                        ManualInspectionCommand manual => manual.AuthorizationTarget,
                        _ => throw new InvalidOperationException("ManualInspectionAuthorizationTargetUnavailable")
                    }, commandKind);
                lock (_sync) _lastStepUpBinding = binding;
                StepUpResult stepUpResult = new(false, "StepUpAuthenticationUnavailable");
                try
                {
                    stepUpResult = await stepUp.ReauthenticateAsync(
                        new StepUpRequest(correlationId, CreateInvocation(start.Value.Session),
                            binding, stepUpPassword), start.Value.Cancellation.Token)
                        .ConfigureAwait(true);
                }
                finally
                {
                    stepUpPassword = string.Empty;
                }
                start.Value.Cancellation.Token.ThrowIfCancellationRequested();
                if (!stepUpResult.Succeeded || stepUpResult.GrantId is not { } grantId ||
                    grantId == Guid.Empty)
                {
                    ApplyFailure(start.Value, "StepUpAuthenticationRejected",
                        "当前密码确认未通过；本次人工检测命令未提交。", false);
                    return null;
                }

                var currentSession = CurrentSession;
                if (!SameSession(start.Value.Session, currentSession))
                {
                    ApplyFailure(start.Value, "ManualInspectionSessionChanged",
                        "会话已变化；Step-Up 回包已作废，本次命令未提交。", true);
                    return null;
                }

                var authorizedCommand = commandFactory(currentSession, correlationId, grantId);
                if (authorizedCommand is not ManualInspectionCommand authorizedManual ||
                    initialCommand is not ManualInspectionCommand initialManual ||
                    !string.Equals(authorizedManual.AuthorizationTarget,
                        initialManual.AuthorizationTarget, StringComparison.Ordinal))
                {
                    ApplyFailure(start.Value, "ManualInspectionAuthorizationTargetChanged",
                        "人工检测授权目标发生变化；本次命令未提交。", false);
                    return null;
                }
                return await SubmitAuthorizedCommandAsync(start.Value, authorizedCommand)
                    .ConfigureAwait(true);
            }

            start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            var correlationIdWithoutStepUp = Guid.NewGuid();
            var commandWithoutStepUp = commandFactory(start.Value.Session,
                correlationIdWithoutStepUp, null);
            return await SubmitAuthorizedCommandAsync(start.Value, commandWithoutStepUp)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            ApplyFailure(start.Value, "ManualInspectionRequestCancelled",
                "页面请求已取消；Runtime 未据此取消已接受的人工检测。", false);
            return null;
        }
        catch
        {
            ApplyFailure(start.Value, "ManualInspectionCommandOutcomeUnknown",
                "人工检测命令提交结果未知；请刷新 Runtime 状态后再决定下一步。", false);
            return null;
        }
        finally
        {
            Complete(start.Value);
        }
    }

    private async Task<RuntimeCommandOutcome?> SubmitAuthorizedCommandAsync(
        OperationStart start, RuntimeCommand command)
    {
        // A cancellation token owned by the page must never cancel an accepted
        // detection while Runtime is acquiring/executing/restoring it.
        var outcome = await _runtime!.SubmitAsync(command, CancellationToken.None)
            .ConfigureAwait(true);
        if (outcome.CorrelationId != command.CorrelationId)
        {
            ApplyFailure(start, "ManualInspectionCommandOutcomeInvalid",
                "Runtime 命令回包关联号不匹配；页面拒绝据此更新状态。", false);
            return null;
        }

        await ApplyOutcomeAsync(start, outcome).ConfigureAwait(true);
        if (outcome.Disposition == CommandDisposition.Accepted)
        {
            try
            {
                await RefreshPublicStateAsync(CancellationToken.None,
                    readSources: false).ConfigureAwait(true);
            }
            catch
            {
                ApplyFailure(start, "ManualInspectionSnapshotRefreshFailed",
                    "命令已返回；最新人工检测状态读取失败，请刷新确认。", false);
            }
        }
        return outcome;
    }

    private async Task ApplyAccessAsync(OperationStart start, ManualInspectionAccess access)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                if (!IsCurrentLocked(start)) return;
                _access = access;
                if (!access.CanRun) _errorCode = SafeReason(access.ReasonCode, "ManualInspectionAccessDenied");
            }
            RaiseStateChanged();
        }).ConfigureAwait(true);
    }

    private async Task ApplyOutcomeAsync(OperationStart start, RuntimeCommandOutcome outcome)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                if (!IsCurrentLocked(start)) return;
                _lastCommandOutcome = outcome;
                _errorCode = outcome.Disposition == CommandDisposition.Rejected
                    ? SafeReason(outcome.ReasonCode, "ManualInspectionCommandRejected")
                    : null;
                _statusMessage = outcome.Disposition == CommandDisposition.Accepted
                    ? "Runtime 已接受人工检测命令；请以后续快照确认阶段和结局。"
                    : "Runtime 拒绝了人工检测命令；页面保留当前快照和拒绝原因。";
            }
            RaiseStateChanged();
        }).ConfigureAwait(true);
    }

    private OperationStart? Begin(OperationKind kind, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_disposed || _isBusy) return null;
            var session = _sessions?.Current ?? _session;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _operationVersion++;
            _activeCancellation = linked;
            _isBusy = true;
            return new OperationStart(_operationVersion, kind, linked, session);
        }
    }

    private void Complete(OperationStart start)
    {
        CancellationTokenSource? cancellation = null;
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            cancellation = _activeCancellation;
            _activeCancellation = null;
            _isBusy = false;
        }
        cancellation?.Dispose();
        NotifyStateChanged();
    }

    private bool IsCurrentLocked(OperationStart start) => !_disposed && _isBusy &&
        _operationVersion == start.Version && ReferenceEquals(_activeCancellation, start.Cancellation);

    private void SetSelection(ManualRecipeSelection? value)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _selection = value;
            _selectedDraftOption = value?.Kind == ManualRecipeSourceKind.Draft
                ? _draftOptions.FirstOrDefault(option => option.Selection == value) : null;
            _selectedReleasedOption = value?.Kind == ManualRecipeSourceKind.Released
                ? _releasedOptions.FirstOrDefault(option => option.Selection == value) : null;
        }
        NotifyStateChanged();
    }

    private void SetText(ref string field, string? value, string propertyName)
    {
        lock (_sync)
        {
            if (_disposed) return;
            field = value ?? string.Empty;
        }
        NotifyStateChanged(propertyName);
    }

    private void SessionChanged(object? sender, InteractiveSessionChangedEventArgs args)
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_disposed) return;
            _session = args.Session;
            _operationVersion++;
            cancellation = _activeCancellation;
            _activeCancellation = null;
            _isBusy = false;
            _access = null;
            _snapshot = null;
            _snapshotAvailable = false;
            _historyAvailable = false;
            _historyRecoveryRequired = false;
            _historyThroughPosition = null;
            _historyNextAfterPosition = null;
            _historyPageNumber = 0;
            _historyErrorCode = "ManualInspectionSessionChanged";
            _historyStatusMessage = "会话已变化，人工检测历史已清除；请登录后显式刷新。";
            _lastStepUpBinding = null;
            _errorCode = "ManualInspectionSessionChanged";
            _statusMessage = "会话已变化，人工检测页面状态已清除；Runtime 会话未由页面自动退出。";
            _partIdentityText = string.Empty;
        }
        cancellation?.Cancel();
        cancellation?.Dispose();
        _ = _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                if (_disposed) return;
                _historyRows.Clear();
                _selectedHistoryRow = null;
            }
            SessionInvalidated?.Invoke(this, EventArgs.Empty);
            RaiseStateChanged();
        });
    }

    private void ApplyUnavailable(string code, string message, bool clearSnapshot)
    {
        lock (_sync)
        {
            if (clearSnapshot)
            {
                _snapshot = null;
                _snapshotAvailable = false;
            }
            _errorCode = code;
            _statusMessage = message;
        }
        NotifyStateChanged();
    }

    private void ApplyHistoryUnavailable(string code, string message, bool clearRows)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (clearRows)
            {
                _historyRows.Clear();
                _selectedHistoryRow = null;
                _historyThroughPosition = null;
                _historyNextAfterPosition = null;
                _historyPageNumber = 0;
            }
            _historyAvailable = false;
            _historyRecoveryRequired = false;
            _historyErrorCode = code;
            _historyStatusMessage = message;
        }
        NotifyStateChanged();
    }

    private void ApplyInputFailure(string code, string message)
    {
        lock (_sync)
        {
            _errorCode = code;
            _statusMessage = message;
        }
        NotifyStateChanged();
    }

    private void ApplyFailure(OperationStart start, string code, string message, bool clearSnapshot)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            if (clearSnapshot)
            {
                _snapshot = null;
                _snapshotAvailable = false;
            }
            _errorCode = code;
            _statusMessage = message;
        }
        NotifyStateChanged();
    }

    private void NotifyStateChanged(string? propertyName = null)
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            if (propertyName is not null) OnPropertyChanged(propertyName);
            RaiseStateChanged();
        });
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(string.Empty);
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsAuthenticated));
        OnPropertyChanged(nameof(CurrentSession));
        OnPropertyChanged(nameof(IsSnapshotFresh));
        OnPropertyChanged(nameof(IsSnapshotUnavailable));
        OnPropertyChanged(nameof(Access));
        OnPropertyChanged(nameof(CurrentAccess));
        OnPropertyChanged(nameof(RequiresStepUp));
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(CurrentSnapshot));
        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(Restoration));
        OnPropertyChanged(nameof(RecoveryRequired));
        OnPropertyChanged(nameof(IsSessionActive));
        OnPropertyChanged(nameof(CurrentManualSessionId));
        OnPropertyChanged(nameof(CurrentManualRunId));
        OnPropertyChanged(nameof(LastManualRunId));
        OnPropertyChanged(nameof(HasHistoryQuery));
        OnPropertyChanged(nameof(HistoryRows));
        OnPropertyChanged(nameof(SelectedHistoryRow));
        OnPropertyChanged(nameof(IsHistoryAvailable));
        OnPropertyChanged(nameof(HistoryRecoveryRequired));
        OnPropertyChanged(nameof(HistoryStatusMessage));
        OnPropertyChanged(nameof(HistoryErrorCode));
        OnPropertyChanged(nameof(HistoryThroughPosition));
        OnPropertyChanged(nameof(HistoryNextAfterPosition));
        OnPropertyChanged(nameof(HistoryPageNumber));
        OnPropertyChanged(nameof(Selection));
        OnPropertyChanged(nameof(SelectedDraftOption));
        OnPropertyChanged(nameof(SelectedReleasedOption));
        OnPropertyChanged(nameof(SelectedDraftRevision));
        OnPropertyChanged(nameof(SelectedReleasedRecipe));
        OnPropertyChanged(nameof(CurrentActive));
        OnPropertyChanged(nameof(ExpectedActive));
        OnPropertyChanged(nameof(LastCommandOutcome));
        OnPropertyChanged(nameof(LastCommandSummary));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ErrorCode));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CurrentActiveSummary));
        OnPropertyChanged(nameof(ExpectedActiveSummary));
        OnPropertyChanged(nameof(PhaseSummary));
        OnPropertyChanged(nameof(RestorationSummary));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanRunOne));
        OnPropertyChanged(nameof(CanExit));
        RefreshCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        RunOneCommand.RaiseCanExecuteChanged();
        GracefulExitCommand.RaiseCanExecuteChanged();
        AbortCommand.RaiseCanExecuteChanged();
        RefreshHistoryCommand.RaiseCanExecuteChanged();
        NextHistoryPageCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    private static CommandInvocation CreateInvocation(InteractiveSession session, Guid? grantId = null) =>
        new(CommandSource.PhysicalConsole, session.PrincipalId, session.SessionId, grantId);

    private static bool IsUsableSession(InteractiveSession session) =>
        session.State == InteractiveSessionState.Authenticated &&
        !string.IsNullOrWhiteSpace(session.PrincipalId) && session.SessionId is not null;

    private static bool SameSession(InteractiveSession left, InteractiveSession right) =>
        left.State == right.State && string.Equals(left.PrincipalId, right.PrincipalId,
            StringComparison.Ordinal) && left.SessionId == right.SessionId;

    private static InteractiveSession ReadSession() => UnauthenticatedSession;

    private static string SafeReason(string? reason, string fallback) =>
        string.IsNullOrWhiteSpace(reason) ? fallback : reason;

    private static bool TryReason(string? reason, out string supplied)
    {
        supplied = reason?.Trim() ?? string.Empty;
        return supplied.Length != 0;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    private static readonly InteractiveSession UnauthenticatedSession =
        new(InteractiveSessionState.Unauthenticated, null, null);
}

/// <summary>One exact immutable source shown by the manual-inspection selector.</summary>
public sealed class ManualRecipeSelectionOption
{
    internal ManualRecipeSelectionOption(RecipeDraftRevision draft)
    {
        Draft = draft ?? throw new ArgumentNullException(nameof(draft));
        Selection = ManualRecipeSelection.FromDraft(draft);
        Label = $"Draft · {draft.Content.RecipeKey} · Revision {draft.Revision} · {draft.RevisionContentHash}";
    }

    internal ManualRecipeSelectionOption(ReleasedRecipe released)
    {
        Released = released ?? throw new ArgumentNullException(nameof(released));
        Selection = ManualRecipeSelection.FromReleased(released);
        Label = $"Released · {released.Reference.Id} v{released.Reference.Version} · {released.Record.ContentHash}";
    }

    public ManualRecipeSelection Selection { get; }
    public RecipeDraftRevision? Draft { get; }
    public ReleasedRecipe? Released { get; }
    public string Label { get; }
    public string ContentHash => Selection.ContentHash;
    public ManualRecipeSourceKind Kind => Selection.Kind;
    public override string ToString() => Label;
}

/// <summary>
/// A safe, read-only display row composed from the Manual event/run ledgers.
/// It carries status and source attribution for presentation only; selecting a
/// row never creates a command or grants access to evidence.
/// </summary>
public sealed class ManualInspectionHistoryRow
{
    private ManualInspectionHistoryRow(ManualInspectionSessionEvent? @event,
        ManualInspectionRunRecord? run, ManualInspectionSessionHeader? header)
    {
        Event = @event;
        Run = run;
        Header = header ?? @event?.Header;
        Position = @event?.Position ?? run?.Position ?? 0;
        SessionId = @event?.SessionId ?? run?.SessionId ?? Header?.SessionId ?? Guid.Empty;
        RunId = run?.RunId ?? @event?.ManualRunId;
        RecordedAtUtc = @event?.RecordedAtUtc ?? run?.CompletedAtUtc ?? run?.AdmittedAtUtc;
    }

    public ManualInspectionSessionEvent? Event { get; }
    public ManualInspectionRunRecord? Run { get; }
    public ManualInspectionSessionHeader? Header { get; }
    public long Position { get; }
    public Guid SessionId { get; }
    public Guid? RunId { get; }
    public DateTimeOffset? RecordedAtUtc { get; }
    public ManualInspectionSessionPhase? Phase => Event?.Phase;
    public ManualInspectionRunStatus? Status => Run?.Status;
    public InspectionDecision? Decision => Run?.Decision;
    public ExecutionStatus? ExecutionStatus => Run?.ExecutionStatus;
    /// <summary>Verified typed result carried by the historical run, when present.</summary>
    public AlgorithmResult? Result => Run?.Result;
    /// <summary>Exact result schema used to interpret <see cref="Result"/>.</summary>
    public AlgorithmResultSchema? ResultSchema => Run?.ResultSchema;
    /// <summary>Frame-pixel overlay projection paired with the typed result.</summary>
    public FrameOverlaySnapshot? FrameOverlay => Run?.FrameOverlay;
    public string? ResultContentHash => Run?.AlgorithmResultContentHash;
    public string? ResultSchemaContentHash => Run?.ResultSchemaContentHash;
    public string? FrameOverlayContentHash => Run?.FrameOverlayContentHash;
    public bool HasTypedResult => Result is not null && ResultSchema is not null;
    public string ReasonCode => Run?.ReasonCode ?? Event?.ReasonCode ?? "ManualInspectionHistoryReasonUnavailable";
    public Guid? ActorPrincipalId => Event?.ActorPrincipalId ?? Header?.ActorPrincipalId;
    public Guid? ActorSessionId => Event?.ActorSessionId ?? Header?.ActorSessionId;
    public long? ActorAuthorizationRevision => Event?.ActorAuthorizationRevision ??
        Header?.ActorAuthorizationRevision;
    public ManualRecipeSourceKind? SourceKind => Header?.Selection.Kind;
    public ManualRecipeSelection? Selection => Header?.Selection;
    public string? SourceContentHash => Header?.SourceContentHash;
    public bool IsTerminal => Event?.Terminal == true || Run?.Terminal == true;

    public string StatusSummary => Status?.ToString() ?? Phase?.ToString() ?? "状态未知";
    public string DecisionSummary => Decision?.ToString() ?? "尚无检测决定";
    public string ActorSummary => ActorPrincipalId is { } principal
        ? $"Principal={principal:D} · Session={ActorSessionId?.ToString("D") ?? "未知"} · AuthRev={ActorAuthorizationRevision?.ToString() ?? "未知"}"
        : "未提供已认证人工主体";
    public string SourceSummary => Header is null
        ? "来源未知（仅显示历史记录，不可据此授权）"
        : Header.Selection.Kind == ManualRecipeSourceKind.Draft
            ? $"Draft · {Header.Selection.DraftId?.ToString("D") ?? "未知"} · Revision={Header.Selection.DraftRevision} · Hash={Header.Selection.DraftRevisionContentHash}"
            : $"Released · {Header.Selection.Recipe?.Id} v{Header.Selection.Recipe?.Version} · Release={Header.Selection.ReleaseId?.ToString("D") ?? "未知"} · Hash={Header.Selection.ReleaseRecordContentHash}";
    public string ResultSummary => Result is { } result && ResultSchema is { } schema
        ? $"Typed Result={schema.Id} v{schema.Version} · Decision={result.Decision} · " +
          $"Reason={result.ReasonCode ?? "无"} · Measurements={result.Measurements.Count} · " +
          $"Hash={ResultContentHash ?? "未提供"}"
        : "尚无已验证 typed Result。";
    public string OverlaySummary => FrameOverlay is { } overlay
        ? $"Frame Overlay={overlay.OverlaySet.ContractId} v{overlay.OverlaySet.ContractVersion} · " +
          $"Elements={overlay.OverlaySet.Primitives.Count} · Hash={FrameOverlayContentHash ?? overlay.ContentHash}"
        : "尚无已验证 Frame Overlay。";
    public string DisplaySummary =>
        $"Position={Position} · {StatusSummary} · {DecisionSummary} · {ReasonCode}";

    internal static IReadOnlyList<ManualInspectionHistoryRow> CreatePage(
        ManualInspectionHistoryPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var events = page.Events;
        var rows = new List<ManualInspectionHistoryRow>(events.Count + page.Runs.Count);
        var attachedRuns = new HashSet<Guid>();

        foreach (var @event in events)
        {
            var run = @event.ManualRunId is { } runId
                ? page.Runs.FirstOrDefault(candidate => candidate.RunId == runId)
                : null;
            if (run is not null) attachedRuns.Add(run.RunId);
            rows.Add(new ManualInspectionHistoryRow(@event, run, @event.Header));
        }

        foreach (var run in page.Runs)
        {
            if (attachedRuns.Contains(run.RunId)) continue;
            var header = events.FirstOrDefault(item => item.SessionId == run.SessionId)?.Header;
            if (header is null && page.PendingHeader?.SessionId == run.SessionId)
                header = page.PendingHeader;
            rows.Add(new ManualInspectionHistoryRow(null, run, header));
        }

        return rows.OrderBy(item => item.Position).ThenBy(item => item.Run is null ? 0 : 1).ToArray();
    }
}
