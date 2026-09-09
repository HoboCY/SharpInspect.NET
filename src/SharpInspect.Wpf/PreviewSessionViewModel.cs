using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Presentation adapter for the explicit, non-production Preview session.
/// Runtime owns admission, camera lifetime, restoration and the session id;
/// this adapter only submits typed commands and reads the public query.
/// </summary>
public sealed class PreviewSessionViewModel : ObservableObject, IAsyncDisposable
{
    public const int MaximumPreviewFrameRateHz = 10;
    public static readonly TimeSpan MinimumPollInterval = TimeSpan.FromMilliseconds(100);

    private enum OperationKind
    {
        Refresh,
        Start,
        Tune,
        Freeze,
        Save,
        Exit
    }

    private readonly record struct OperationStart(
        long Version,
        OperationKind Kind,
        CancellationTokenSource Cancellation,
        InteractiveSession Session);

    private readonly IStationRuntime? _runtime;
    private readonly IPreviewSessionService? _preview;
    private readonly IInteractiveSessionService? _sessions;
    private readonly StationShellViewModel? _station;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeSpan _pollInterval;
    private readonly SemaphoreSlim _queryGate = new(1, 1);
    private readonly SemaphoreSlim _presentationGate = new(1, 1);
    private readonly object _sync = new();

    private CancellationTokenSource? _activeCancellation;
    private CancellationTokenSource? _watchCancellation;
    private Task? _watchStartTask;
    private Task? _watchTask;
    private long _operationVersion;
    private long _watchGeneration;
    private bool _isBusy;
    private bool _disposed;
    private bool _snapshotAvailable;
    private InteractiveSession _session;
    private PreviewSessionAccess? _access;
    private PreviewSessionSnapshot? _snapshot;
    private PreviewDisplayImage? _latestImage;
    private Guid? _displaySessionId;
    private long _displaySequence = -1;
    private Guid? _snapshotEpoch;
    private long _snapshotRevision;
    private PreviewDraftReference? _draft;
    private RecipeActivationReference? _expectedActive;
    private PreviewTuningConfiguration? _configuration;
    private RuntimeCommandOutcome? _lastCommandOutcome;
    private string _startReason = string.Empty;
    private string _tuningReason = string.Empty;
    private string _freezeReason = string.Empty;
    private string _saveReason = string.Empty;
    private string _exitReason = string.Empty;
    private string _statusMessage;
    private string? _errorCode;
    private string? _inputErrorCode;

    public PreviewSessionViewModel()
        : this(runtime: null, previewService: null, sessions: null,
            dispatcher: new DispatcherUiDispatcher())
    {
    }

    public PreviewSessionViewModel(
        IStationRuntime? runtime,
        IPreviewSessionService? previewService,
        IInteractiveSessionService? sessions,
        IUiDispatcher? dispatcher = null,
        StationShellViewModel? station = null,
        TimeSpan? pollInterval = null)
    {
        _runtime = runtime;
        _preview = previewService;
        _sessions = sessions;
        _station = station;
        _dispatcher = dispatcher ?? new DispatcherUiDispatcher();
        _pollInterval = pollInterval ?? MinimumPollInterval;
        if (_pollInterval < MinimumPollInterval)
            throw new ArgumentOutOfRangeException(nameof(pollInterval),
                "Preview polling must not exceed ten frames per second.");

        _session = sessions?.Current ?? UnauthenticatedSession;
        _statusMessage = IsConfigured
            ? "Preview 页面已就绪；请明确选择 Draft 后显式进入，打开页面不会进入 Preview。"
            : "Preview 不可用：Runtime、Preview 查询或当前用户会话未完整配置。";
        _errorCode = IsConfigured ? null : "PreviewRuntimeUnavailable";

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => CanRefresh);
        StartCommand = new AsyncRelayCommand(
            () => StartPreviewSessionAsync(CancellationToken.None), () => CanStart);
        TuneCommand = new AsyncRelayCommand(
            () => ApplyPreviewTuningAsync(CancellationToken.None), () => CanApplyTuning);
        FreezeCommand = new AsyncRelayCommand(
            () => FreezePreviewSettingsAsync(CancellationToken.None), () => CanFreeze);
        SaveCommand = new AsyncRelayCommand(
            () => SavePreviewToDraftAsync(CancellationToken.None), () => CanSave);
        ExitCommand = new AsyncRelayCommand(
            () => ExitPreviewSessionAsync(ExitReason, cancel: false), () => CanExit);
        CancelCommand = new AsyncRelayCommand(() =>
        {
            CancelPendingOperations();
            return Task.CompletedTask;
        }, () => IsBusy);

        if (_sessions is not null)
            _sessions.Changed += SessionChanged;
        if (_station is not null)
        {
            _station.PropertyChanged += StationChanged;
            _station.State.PropertyChanged += StationStateChanged;
        }
    }

    /// <summary>Convenience overload for hosts that already own the shell VM.</summary>
    public PreviewSessionViewModel(
        StationShellViewModel? station,
        IStationRuntime? runtime,
        IPreviewSessionService? previewService,
        IInteractiveSessionService? sessions,
        IUiDispatcher? dispatcher = null,
        TimeSpan? pollInterval = null)
        : this(runtime, previewService, sessions, dispatcher, station, pollInterval)
    {
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand TuneCommand { get; }
    public AsyncRelayCommand FreezeCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand ExitCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }

    public AsyncRelayCommand StartPreviewCommand => StartCommand;
    public AsyncRelayCommand ApplyTuningCommand => TuneCommand;
    public AsyncRelayCommand FreezeSettingsCommand => FreezeCommand;
    public AsyncRelayCommand SaveDraftCommand => SaveCommand;
    public AsyncRelayCommand ExitSessionCommand => ExitCommand;

    /// <summary>The panel clears native credential/input controls on this event.</summary>
    internal event EventHandler? SessionInvalidated;

    public bool IsConfigured => _runtime is not null && _preview is not null && _sessions is not null;

    public bool IsBusy
    {
        get { lock (_sync) return _isBusy; }
    }

    public bool IsWatching
    {
        get
        {
            lock (_sync)
                return _watchTask is not null || _watchStartTask is not null;
        }
    }

    public InteractiveSession CurrentSession => _sessions?.Current ?? ReadSession();
    public bool IsAuthenticated => IsUsableSession(CurrentSession);

    public SnapshotFreshness ShellSnapshotFreshness => _station?.Freshness ?? SnapshotFreshness.Fresh;
    public bool IsShellSnapshotFresh => _station is null ||
        (_station.Freshness == SnapshotFreshness.Fresh && _station.State.IsFresh);

    public bool IsSnapshotFresh
    {
        get { lock (_sync) return _snapshotAvailable && IsShellSnapshotFresh; }
    }

    public bool IsSnapshotUnavailable => !IsSnapshotFresh;

    public PreviewSessionAccess? Access
    {
        get { lock (_sync) return _access; }
    }

    public bool RequiresStepUp => Access?.RequiresStepUp == true;

    public PreviewSessionSnapshot? Snapshot
    {
        get { lock (_sync) return _snapshot; }
    }

    public PreviewSessionSnapshot? CurrentSnapshot => Snapshot;
    public PreviewDisplayImage? LatestImage
    {
        get { lock (_sync) return _latestImage; }
    }

    public PreviewDisplayImage? DisplayImage => LatestImage;
    public PreviewDisplayImage? LatestPreviewImage => LatestImage;
    public CameraPreviewFrame? LatestFrame => Snapshot?.LatestFrame;
    public Guid? CurrentPreviewSessionId => Snapshot?.PreviewSessionId;
    public PreviewSessionPhase Phase => Snapshot?.Phase ?? PreviewSessionPhase.Idle;
    public PreviewRestorationState Restoration => Snapshot?.Restoration ?? PreviewRestorationState.NotRequired;
    public bool RecoveryRequired => Snapshot?.RecoveryRequired == true ||
        Phase == PreviewSessionPhase.RecoveryBlocked || Restoration == PreviewRestorationState.RecoveryBlocked;
    public bool Ready => false;
    public bool IsSessionActive => Snapshot?.IsSessionActive == true;
    public bool RestorationVerified => Restoration == PreviewRestorationState.Restored;

    public PreviewDraftReference? Draft
    {
        get { lock (_sync) return _draft; }
        set
        {
            lock (_sync)
            {
                if (_disposed) return;
                _draft = value;
            }
            NotifyStateChangedOnUi();
        }
    }

    public PreviewDraftReference? ExpectedDraft
    {
        get => Draft;
        set => Draft = value;
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
            NotifyStateChangedOnUi();
        }
    }

    public PreviewTuningConfiguration? TuningConfiguration
    {
        get { lock (_sync) return _configuration; }
        set
        {
            lock (_sync)
            {
                if (_disposed) return;
                _configuration = value;
            }
            NotifyStateChangedOnUi();
        }
    }

    public PreviewTuningConfiguration? Configuration
    {
        get => TuningConfiguration;
        set => TuningConfiguration = value;
    }

    public string StartReason
    {
        get { lock (_sync) return _startReason; }
        set => SetReason(ref _startReason, value, nameof(StartReason));
    }

    public string TuningReason
    {
        get { lock (_sync) return _tuningReason; }
        set => SetReason(ref _tuningReason, value, nameof(TuningReason));
    }

    public string FreezeReason
    {
        get { lock (_sync) return _freezeReason; }
        set => SetReason(ref _freezeReason, value, nameof(FreezeReason));
    }

    public string SaveReason
    {
        get { lock (_sync) return _saveReason; }
        set => SetReason(ref _saveReason, value, nameof(SaveReason));
    }

    public string ExitReason
    {
        get { lock (_sync) return _exitReason; }
        set => SetReason(ref _exitReason, value, nameof(ExitReason));
    }

    /// <summary>Convenience input for a host that uses one reason field.</summary>
    public string ChangeReason
    {
        get => StartReason;
        set
        {
            StartReason = value;
            TuningReason = value;
            FreezeReason = value;
            SaveReason = value;
            ExitReason = value;
        }
    }

    public PreviewSessionAccess? CurrentAccess => Access;
    public RuntimeCommandOutcome? LastCommandOutcome
    {
        get { lock (_sync) return _lastCommandOutcome; }
    }

    public string LastCommandSummary => LastCommandOutcome is null
        ? "尚未提交 Preview 命令。"
        : $"{LastCommandOutcome.Disposition} · {LastCommandOutcome.ReasonCode} · 审计={LastCommandOutcome.Audit}";

    public string StatusMessage
    {
        get { lock (_sync) return _statusMessage; }
    }

    public string? ErrorCode
    {
        get { lock (_sync) return _errorCode; }
    }

    public string? InputErrorCode
    {
        get { lock (_sync) return _inputErrorCode; }
    }

    public string PhaseSummary => Snapshot is { } snapshot
        ? $"Phase={snapshot.Phase} · Session={snapshot.PreviewSessionId?.ToString("D") ?? "无"} · " +
          $"Revision={snapshot.Revision} · Reason={snapshot.ReasonCode}"
        : "尚未读取 Preview Runtime 快照。";

    public string RestorationSummary => Restoration switch
    {
        PreviewRestorationState.Pending => "Runtime 正在恢复 Active 配置；页面不会宣称已恢复。",
        PreviewRestorationState.Restored => "Preview 已退出，Runtime 已恢复并读回 Active 配置。",
        PreviewRestorationState.NoActiveBaselineClosed => "Preview 已关闭；没有 Active 基线可恢复。",
        PreviewRestorationState.RecoveryBlocked => "Active 配置恢复被阻断；Ready 保持 false。",
        _ => "当前没有需要恢复的 Active 配置。"
    };

    public string ValidationSummary => InputErrorCode is null
        ? "Preview 只允许显式命令；调参、冻结和保存均由 Runtime 复查。"
        : "当前 Preview 输入无效，请修正后重试。";

    public bool CanRefresh => IsConfigured && IsAuthenticated && !IsBusy && !_disposed;

    public bool CanStart
    {
        get
        {
            lock (_sync)
            {
                return IsConfigured && IsAuthenticated && IsShellSnapshotFresh && !_isBusy && !_disposed &&
                    _draft is not null && !string.IsNullOrWhiteSpace(_startReason) &&
                    (_snapshot is null || !_snapshot.IsSessionActive);
            }
        }
    }

    public bool CanApplyTuning
    {
        get
        {
            lock (_sync)
            {
                return IsConfigured && IsAuthenticated && IsShellSnapshotFresh && !_isBusy && !_disposed &&
                    _configuration is not null && IsActiveTuningPhase(_snapshot);
            }
        }
    }

    public bool CanTune => CanApplyTuning;

    public bool CanFreeze
    {
        get
        {
            lock (_sync)
            {
                return IsConfigured && IsAuthenticated && IsShellSnapshotFresh && !_isBusy && !_disposed &&
                    IsActiveTuningPhase(_snapshot) && !string.IsNullOrWhiteSpace(_freezeReason);
            }
        }
    }

    public bool CanSave
    {
        get
        {
            lock (_sync)
            {
                return IsConfigured && IsAuthenticated && IsShellSnapshotFresh && !_isBusy && !_disposed &&
                    IsActivePreview(_snapshot) && _snapshot?.Draft is not null &&
                    !string.IsNullOrWhiteSpace(_snapshot.FrozenSettingsContentHash) &&
                    !string.IsNullOrWhiteSpace(_saveReason);
            }
        }
    }

    public bool CanExit
    {
        get
        {
            lock (_sync)
            {
                return IsConfigured && IsAuthenticated && IsShellSnapshotFresh && !_isBusy && !_disposed &&
                    IsActivePreview(_snapshot) && !string.IsNullOrWhiteSpace(_exitReason);
            }
        }
    }

    public void ConfigureStart(PreviewDraftReference draft,
        RecipeActivationReference? expectedActive = null)
    {
        Draft = draft ?? throw new ArgumentNullException(nameof(draft));
        ExpectedActive = expectedActive;
    }

    public void Configure(PreviewDraftReference draft,
        RecipeActivationReference? expectedActive = null) => ConfigureStart(draft, expectedActive);

    public void SetTuningConfiguration(PreviewTuningConfiguration configuration) =>
        TuningConfiguration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    /// <summary>
    /// Starts only the read-only Preview projection watcher. It never enters a
    /// Runtime session; entry remains the explicit Start command.
    /// </summary>
    public async Task StartWatchingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task startTask;
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PreviewSessionViewModel));
            if (_watchTask is not null)
                return;
            if (_watchStartTask is not null)
            {
                startTask = _watchStartTask;
            }
            else
            {
                _watchGeneration++;
                _watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startTask = StartWatchingCoreAsync(_watchGeneration, _watchCancellation.Token);
                _watchStartTask = startTask;
            }
        }

        await startTask.ConfigureAwait(true);
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        StartWatchingAsync(cancellationToken);

    public void StartWatching() => _ = StartWatchingIgnoringFailureAsync();

    public async Task StopWatchingAsync()
    {
        Task? startTask;
        Task? watchTask;
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            _watchGeneration++;
            cancellation = _watchCancellation;
            _watchCancellation = null;
            startTask = _watchStartTask;
            watchTask = _watchTask;
            _watchStartTask = null;
            _watchTask = null;
        }

        cancellation?.Cancel();
        await AwaitCancelledTaskAsync(startTask).ConfigureAwait(false);
        await AwaitCancelledTaskAsync(watchTask).ConfigureAwait(false);
        cancellation?.Dispose();

        Task? lateWatch;
        lock (_sync) lateWatch = _watchTask;
        if (lateWatch is not null)
            await AwaitCancelledTaskAsync(lateWatch).ConfigureAwait(false);

        await ClearDisplayAsync().ConfigureAwait(false);
        NotifyStateChangedOnUi();
    }

    public void StopWatching() => _ = StopWatchingIgnoringFailureAsync();

    /// <summary>
    /// Stops the presentation watcher and clears the displayed copy. It does
    /// not submit Exit, Stop, or any other Runtime command.
    /// </summary>
    public void Deactivate()
    {
        CancelPendingOperations();
        StopWatching();
    }

    public void ClearSensitiveInputs() => Deactivate();

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            ApplyUnavailable("PreviewRuntimeUnavailable",
                "Preview 不可用：Runtime、Preview 查询或当前用户会话未完整配置。", clearSnapshot: false);
            return;
        }

        var start = Begin(OperationKind.Refresh, cancellationToken);
        if (!start.HasValue) return;
        try
        {
            if (!IsUsableSession(start.Value.Session))
            {
                ApplyFailure(start.Value, "PreviewAuthenticationRequired",
                    "请先登录，再读取 Preview 状态。", clearSnapshot: true);
                return;
            }

            await ReadSnapshotAsync(start.Value.Cancellation.Token, start, null).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            ApplyCancellation(start.Value);
        }
        catch
        {
            ApplyFailure(start.Value, "PreviewSnapshotRefreshFailed",
                "Preview 状态读取失败，请重试。", clearSnapshot: true);
        }
        finally
        {
            Complete(start.Value);
        }
    }

    public Task RefreshStateAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(cancellationToken);

    public Task<RuntimeCommandOutcome?> StartPreviewSessionAsync(
        CancellationToken cancellationToken = default)
    {
        PreviewDraftReference? draft;
        RecipeActivationReference? expectedActive;
        string reason;
        lock (_sync)
        {
            draft = _draft;
            expectedActive = _expectedActive;
            reason = _startReason;
        }
        if (draft is null)
        {
            ApplyInputFailure("PreviewDraftRequired", "进入 Preview 必须明确选择 Draft 修订。", false);
            return Task.FromResult<RuntimeCommandOutcome?>(null);
        }
        return StartPreviewSessionAsync(draft, expectedActive, reason, cancellationToken);
    }

    public Task<RuntimeCommandOutcome?> StartPreviewSessionAsync(
        PreviewDraftReference draft,
        RecipeActivationReference? expectedActive,
        string? reason,
        CancellationToken cancellationToken = default) =>
        StartPreviewSessionCoreAsync(Guid.NewGuid(), draft, expectedActive, reason, cancellationToken);

    public Task<RuntimeCommandOutcome?> StartPreviewSessionAsync(
        Guid previewSessionId,
        PreviewDraftReference draft,
        RecipeActivationReference? expectedActive,
        string? reason,
        CancellationToken cancellationToken = default) =>
        StartPreviewSessionCoreAsync(previewSessionId, draft, expectedActive, reason, cancellationToken);

    public Task<RuntimeCommandOutcome?> EnterPreviewAsync(
        PreviewDraftReference draft,
        RecipeActivationReference? expectedActive,
        string? reason,
        CancellationToken cancellationToken = default) =>
        StartPreviewSessionAsync(draft, expectedActive, reason, cancellationToken);

    private async Task<RuntimeCommandOutcome?> StartPreviewSessionCoreAsync(
        Guid previewSessionId,
        PreviewDraftReference? draft,
        RecipeActivationReference? expectedActive,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (draft is null)
        {
            ApplyInputFailure("PreviewDraftRequired", "进入 Preview 必须明确选择 Draft 修订。", false);
            return null;
        }
        if (previewSessionId == Guid.Empty)
        {
            ApplyInputFailure("PreviewSessionIdRequired", "进入 Preview 必须使用有效的会话请求 ID。", false);
            return null;
        }
        if (!TryReason(reason, out var suppliedReason))
        {
            ApplyInputFailure("PreviewStartReasonRequired", "进入 Preview 必须记录明确理由。", false);
            return null;
        }
        if (IsSessionActive)
        {
            ApplyInputFailure("PreviewSessionAlreadyActive", "当前已有 Preview 会话；请先显式退出。", false);
            return null;
        }

        var exactDraft = draft;

        return await SubmitPreviewCommandAsync(
            OperationKind.Start,
            previewSessionId,
            requireActiveSession: false,
            (session, sessionId, correlationId) => new StartPreviewSessionCommand(
                correlationId, CreateInvocation(session), sessionId, exactDraft, expectedActive, suppliedReason),
            "PreviewStartUnavailable",
            "当前 Preview 不能开始，请确认登录、工位快照新鲜且没有活动会话。",
            cancellationToken).ConfigureAwait(true);
    }

    public Task<RuntimeCommandOutcome?> ApplyPreviewTuningAsync(
        CancellationToken cancellationToken = default)
    {
        PreviewTuningConfiguration? configuration;
        string reason;
        lock (_sync)
        {
            configuration = _configuration;
            reason = _tuningReason;
        }
        if (configuration is null)
        {
            ApplyInputFailure("PreviewTuningConfigurationRequired", "请先填写完整的 Preview 调参值。", false);
            return Task.FromResult<RuntimeCommandOutcome?>(null);
        }
        return ApplyPreviewTuningAsync(configuration, reason, cancellationToken);
    }

    public Task<RuntimeCommandOutcome?> ApplyPreviewTuningAsync(
        PreviewTuningConfiguration configuration,
        string? reason,
        CancellationToken cancellationToken = default) =>
        ApplyPreviewTuningCoreAsync(configuration, reason, cancellationToken);

    public Task<RuntimeCommandOutcome?> ApplyTuningAsync(
        PreviewTuningConfiguration configuration,
        string? reason,
        CancellationToken cancellationToken = default) =>
        ApplyPreviewTuningAsync(configuration, reason, cancellationToken);

    private async Task<RuntimeCommandOutcome?> ApplyPreviewTuningCoreAsync(
        PreviewTuningConfiguration? configuration,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (configuration is null)
        {
            ApplyInputFailure("PreviewTuningConfigurationRequired", "请先填写完整的 Preview 调参值。", false);
            return null;
        }
        if (!TryReason(reason, out var suppliedReason))
        {
            ApplyInputFailure("PreviewTuningReasonRequired", "Preview 调参必须记录明确理由。", false);
            return null;
        }

        var sessionId = ReadCurrentPreviewSessionId();
        if (sessionId == Guid.Empty)
        {
            ApplyInputFailure("PreviewSessionRequired", "请先由 Runtime 显式进入 Preview 会话。", false);
            return null;
        }

        return await SubmitPreviewCommandAsync(
            OperationKind.Tune,
            sessionId,
            requireActiveSession: true,
            (session, currentSessionId, correlationId) => new ApplyPreviewTuningCommand(
                correlationId, CreateInvocation(session), currentSessionId, configuration, suppliedReason),
            "PreviewTuningUnavailable",
            "当前 Preview 不允许调参；请刷新 Runtime 会话状态。",
            cancellationToken).ConfigureAwait(true);
    }

    public Task<RuntimeCommandOutcome?> FreezePreviewSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        string reason;
        lock (_sync) reason = _freezeReason;
        return FreezePreviewSettingsAsync(reason, cancellationToken);
    }

    public async Task<RuntimeCommandOutcome?> FreezePreviewSettingsAsync(
        string? reason,
        CancellationToken cancellationToken = default)
    {
        if (!TryReason(reason, out var suppliedReason))
        {
            ApplyInputFailure("PreviewFreezeReasonRequired", "冻结 Preview 设置必须记录明确理由。", false);
            return null;
        }
        var sessionId = ReadCurrentPreviewSessionId();
        if (sessionId == Guid.Empty)
        {
            ApplyInputFailure("PreviewSessionRequired", "请先由 Runtime 显式进入 Preview 会话。", false);
            return null;
        }

        return await SubmitPreviewCommandAsync(
            OperationKind.Freeze,
            sessionId,
            requireActiveSession: true,
            (session, currentSessionId, correlationId) => new FreezePreviewSettingsCommand(
                correlationId, CreateInvocation(session), currentSessionId, suppliedReason),
            "PreviewFreezeUnavailable",
            "当前 Preview 不允许冻结设置；请刷新 Runtime 会话状态。",
            cancellationToken).ConfigureAwait(true);
    }

    public Task<RuntimeCommandOutcome?> FreezeAsync(
        string? reason,
        CancellationToken cancellationToken = default) =>
        FreezePreviewSettingsAsync(reason, cancellationToken);

    public Task<RuntimeCommandOutcome?> SavePreviewToDraftAsync(
        CancellationToken cancellationToken = default)
    {
        string reason;
        lock (_sync) reason = _saveReason;
        return SavePreviewToDraftAsync(reason, cancellationToken);
    }

    public async Task<RuntimeCommandOutcome?> SavePreviewToDraftAsync(
        string? reason,
        CancellationToken cancellationToken = default)
    {
        if (!TryReason(reason, out var suppliedReason))
        {
            ApplyInputFailure("PreviewSaveReasonRequired", "保存 Preview 设置到 Draft 必须记录明确理由。", false);
            return null;
        }

        PreviewSessionSnapshot? snapshot;
        PreviewDraftReference? draft;
        string frozenHash;
        Guid sessionId;
        lock (_sync)
        {
            snapshot = _snapshot;
            draft = snapshot?.Draft;
            frozenHash = snapshot?.FrozenSettingsContentHash ?? string.Empty;
            sessionId = snapshot?.PreviewSessionId ?? Guid.Empty;
        }
        if (snapshot is null || draft is null || string.IsNullOrWhiteSpace(frozenHash) || sessionId == Guid.Empty)
        {
            ApplyInputFailure("PreviewFrozenSettingsRequired",
                "保存前必须由 Runtime 完成冻结并在快照中提供精确 Draft 与设置哈希。", false);
            return null;
        }

        var exactDraft = draft;

        bool IntentStillCurrent() {
            lock (_sync)
            {
                var current = _snapshot;
                return current?.PreviewSessionId == sessionId &&
                    SameDraft(current.Draft, exactDraft) &&
                    string.Equals(current.FrozenSettingsContentHash, frozenHash,
                        StringComparison.Ordinal);
            }
        }

        return await SubmitPreviewCommandAsync(
            OperationKind.Save,
            sessionId,
            requireActiveSession: true,
            (session, currentSessionId, correlationId) => new SavePreviewToDraftCommand(
                correlationId, CreateInvocation(session), currentSessionId, exactDraft, frozenHash, suppliedReason),
            "PreviewSaveUnavailable",
            "当前 Preview 没有可保存的冻结设置；请刷新 Runtime 快照。",
            cancellationToken,
            IntentStillCurrent).ConfigureAwait(true);
    }

    public Task<RuntimeCommandOutcome?> SaveAsync(
        string? reason,
        CancellationToken cancellationToken = default) =>
        SavePreviewToDraftAsync(reason, cancellationToken);

    public Task<RuntimeCommandOutcome?> ExitPreviewSessionAsync(
        string? reason,
        bool cancel = false,
        CancellationToken cancellationToken = default) =>
        ExitPreviewSessionCoreAsync(reason, cancel, cancellationToken);

    public Task<RuntimeCommandOutcome?> ExitAsync(
        string? reason,
        bool cancel = false,
        CancellationToken cancellationToken = default) =>
        ExitPreviewSessionAsync(reason, cancel, cancellationToken);

    private async Task<RuntimeCommandOutcome?> ExitPreviewSessionCoreAsync(
        string? reason,
        bool cancel,
        CancellationToken cancellationToken)
    {
        if (!TryReason(reason, out var suppliedReason))
        {
            ApplyInputFailure("PreviewExitReasonRequired", "退出 Preview 必须记录明确理由。", false);
            return null;
        }
        var sessionId = ReadCurrentPreviewSessionId();
        if (sessionId == Guid.Empty)
        {
            ApplyInputFailure("PreviewSessionRequired", "当前没有可退出的 Runtime Preview 会话。", false);
            return null;
        }

        return await SubmitPreviewCommandAsync(
            OperationKind.Exit,
            sessionId,
            requireActiveSession: true,
            (session, currentSessionId, correlationId) => new ExitPreviewSessionCommand(
                correlationId, CreateInvocation(session), currentSessionId, cancel, suppliedReason),
            "PreviewExitUnavailable",
            "当前 Preview 不能退出；请刷新 Runtime 会话状态。",
            cancellationToken).ConfigureAwait(true);
    }

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
                _statusMessage = "Preview 请求已取消；Runtime 会话不会因页面取消而自动退出。";
            }
        }
        cancellation?.Cancel();
        NotifyStateChangedOnUi();
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
        }
        cancellation?.Cancel();

        await StopWatchingAsync().ConfigureAwait(false);
        if (_sessions is not null)
            _sessions.Changed -= SessionChanged;
        if (_station is not null)
        {
            _station.PropertyChanged -= StationChanged;
            _station.State.PropertyChanged -= StationStateChanged;
        }
        await ClearDisplayAsync().ConfigureAwait(false);
        _queryGate.Dispose();
        _presentationGate.Dispose();
    }

    private async Task<RuntimeCommandOutcome?> SubmitPreviewCommandAsync(
        OperationKind kind,
        Guid requestedSessionId,
        bool requireActiveSession,
        Func<InteractiveSession, Guid, Guid, RuntimeCommand> commandFactory,
        string unavailableCode,
        string unavailableMessage,
        CancellationToken cancellationToken,
        Func<bool>? intentStillCurrent = null)
    {
        if (!IsConfigured)
        {
            ApplyUnavailable(unavailableCode, unavailableMessage, clearSnapshot: false);
            return null;
        }
        if (!IsShellSnapshotFresh)
        {
            ApplyInputFailure("PreviewSnapshotStale",
                "当前工位快照已陈旧或不可用；Preview 特权操作已停止，请等待新鲜快照。", false);
            return null;
        }
        if (!IsUsableSession(CurrentSession))
        {
            ApplyInputFailure("PreviewAuthenticationRequired", "请先登录，再提交 Preview 命令。", false);
            return null;
        }

        var start = Begin(kind, cancellationToken);
        if (!start.HasValue) return null;
        try
        {
            if (!SameSession(start.Value.Session, CurrentSession))
            {
                ApplyFailure(start.Value, "PreviewSessionChanged",
                    "会话已变化；本次 Preview 命令未提交。", clearSnapshot: true);
                return null;
            }

            PreviewSessionAccess access;
            try
            {
                access = await _preview!.GetAccessAsync(
                    CreateInvocation(start.Value.Session), start.Value.Cancellation.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                ApplyFailure(start.Value, "PreviewAccessQueryFailed",
                    "Preview 权限读取失败；本次命令未提交。", clearSnapshot: false);
                return null;
            }

            await ApplyAccessAsync(start.Value, access).ConfigureAwait(true);
            if (!access.CanRun)
            {
                ApplyFailure(start.Value, SafeReason(access.ReasonCode, unavailableCode),
                    "Runtime 未批准当前 Preview 操作；页面没有预先改变状态。", clearSnapshot: false);
                return null;
            }
            if (access.RequiresStepUp)
            {
                ApplyFailure(start.Value, "PreviewStepUpRequired",
                    "当前 Preview 操作需要 Step-Up；页面没有绕过该授权要求。", clearSnapshot: false);
                return null;
            }
            if (!IsShellSnapshotFresh)
            {
                ApplyFailure(start.Value, "PreviewSnapshotStale",
                    "工位快照在提交前已陈旧；本次 Preview 命令未提交。", clearSnapshot: false);
                return null;
            }
            if (!SameSession(start.Value.Session, CurrentSession))
            {
                ApplyFailure(start.Value, "PreviewSessionChanged",
                    "会话已变化；本次 Preview 命令未提交。", clearSnapshot: true);
                return null;
            }
            if (intentStillCurrent is not null && !intentStillCurrent())
            {
                ApplyFailure(start.Value, "PreviewSnapshotChanged",
                    "Preview 冻结快照已变化；本次命令未提交。", clearSnapshot: false);
                return null;
            }

            var commandSessionId = requestedSessionId;
            if (requireActiveSession)
            {
                lock (_sync)
                {
                    var snapshot = _snapshot;
                    if (snapshot?.PreviewSessionId != requestedSessionId ||
                        snapshot?.IsSessionActive != true)
                        commandSessionId = Guid.Empty;
                }
                if (commandSessionId == Guid.Empty)
                {
                    ApplyFailure(start.Value, "PreviewSessionChanged",
                        "Runtime Preview 会话已变化或已关闭；本次命令未提交。", clearSnapshot: false);
                    return null;
                }
            }

            var correlationId = Guid.NewGuid();
            var command = commandFactory(CurrentSession, commandSessionId, correlationId);
            RuntimeCommandOutcome outcome;
            try
            {
                // The linked operation token belongs to this view's preflight
                // and read-back work. Once the typed command is handed to
                // Runtime, Deactivate/Dispose must not cancel it through that
                // view-lifetime token. The caller token remains visible to
                // Runtime so it can apply its own accepted-command recovery
                // policy when the caller explicitly cancels.
                start.Value.Cancellation.Token.ThrowIfCancellationRequested();
                outcome = await _runtime!.SubmitAsync(command, cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                ApplyFailure(start.Value, "PreviewCommandOutcomeUnknown",
                    "Preview 命令提交结果未知；请等待或刷新 Runtime 快照。", clearSnapshot: false);
                return null;
            }

            await ApplyOutcomeAsync(start.Value, outcome).ConfigureAwait(true);
            if (outcome.Disposition == CommandDisposition.Accepted)
                await ReadSnapshotAsync(start.Value.Cancellation.Token, start, null).ConfigureAwait(true);
            return outcome;
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            ApplyCancellation(start.Value);
            return null;
        }
        catch
        {
            ApplyFailure(start.Value, "PreviewCommandFailed",
                "Preview 命令未完成；请刷新 Runtime 状态后重试。", clearSnapshot: false);
            return null;
        }
        finally
        {
            Complete(start.Value);
        }
    }

    private async Task StartWatchingCoreAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            // Keep the start task visible before a synchronously completed fake
            // query can publish its first result or a caller can stop the page.
            await Task.Yield();
            await ReadSnapshotAsync(cancellationToken, null, generation).ConfigureAwait(false);
            lock (_sync)
            {
                if (_disposed || generation != _watchGeneration || cancellationToken.IsCancellationRequested)
                    return;
                _watchTask = WatchLoopAsync(generation, cancellationToken);
            }
            NotifyStateChangedOnUi();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_sync)
            {
                if (_watchStartTask is not null && generation == _watchGeneration)
                    _watchStartTask = null;
            }
        }
    }

    private async Task WatchLoopAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                await ReadSnapshotAsync(cancellationToken, null, generation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReadSnapshotAsync(
        CancellationToken cancellationToken,
        OperationStart? operation,
        long? watchGeneration)
    {
        var session = CurrentSession;
        if (!IsUsableSession(session))
        {
            await ApplyReadFailureAsync(operation, watchGeneration,
                "PreviewAuthenticationRequired", "请先登录，再读取 Preview 状态。", true)
                .ConfigureAwait(false);
            return;
        }

        PreviewSessionReadResult result;
        try
        {
            await _queryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                result = await _preview!.GetSnapshotAsync(
                    CreateInvocation(session), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _queryGate.Release();
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await ApplyReadFailureAsync(operation, watchGeneration,
                "PreviewSnapshotQueryFailed", "Preview 状态读取失败，请重试。", true)
                .ConfigureAwait(false);
            return;
        }

        if (!SameSession(session, CurrentSession)) return;

        PreviewDisplayImage? image = null;
        string? frameError = null;
        if (result.Snapshot?.LatestFrame is { } frame)
        {
            if (result.Snapshot.PreviewSessionId != frame.SessionId)
            {
                frameError = "PreviewFrameSessionMismatch";
            }
            else
            {
                try
                {
                    image = PreviewDisplayImage.CopyFromFrame(frame);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    frameError = SafeReason(exception.Message, "PreviewFrameInvalid");
                }
            }
        }

        await InvokeOnDispatcherAsync(() => ApplySnapshotResult(
            session, result, image, frameError, operation, watchGeneration), cancellationToken)
            .ConfigureAwait(false);
    }

    private void ApplySnapshotResult(
        InteractiveSession session,
        PreviewSessionReadResult result,
        PreviewDisplayImage? image,
        string? frameError,
        OperationStart? operation,
        long? watchGeneration)
    {
        lock (_sync)
        {
            if (_disposed || !SameSession(session, CurrentSession) ||
                (operation.HasValue && !IsCurrentLocked(operation.Value)) ||
                (watchGeneration.HasValue && watchGeneration.Value != _watchGeneration))
                return;

            if (!result.Available || result.Snapshot is null)
            {
                _snapshotAvailable = false;
                _snapshot = null;
                ClearDisplayLocked();
                _errorCode = SafeReason(result.ReasonCode, "PreviewSnapshotUnavailable");
                _statusMessage = "Preview Runtime 快照当前不可用；页面没有据此宣称会话或恢复完成。";
                NotifyStateChangedOnUi();
                return;
            }

            var snapshot = result.Snapshot;
            if (_snapshotEpoch == snapshot.RuntimeEpoch && snapshot.Revision < _snapshotRevision)
                return;
            if (_snapshotEpoch != snapshot.RuntimeEpoch)
            {
                _displaySessionId = null;
                _displaySequence = -1;
                _latestImage = null;
            }
            _snapshotEpoch = snapshot.RuntimeEpoch;
            _snapshotRevision = snapshot.Revision;
            _snapshot = snapshot;
            _snapshotAvailable = true;

            if (!snapshot.IsSessionActive || snapshot.PreviewSessionId is not { } sessionId)
            {
                ClearDisplayLocked();
            }
            else if (frameError is not null)
            {
                ClearDisplayLocked();
                _errorCode = frameError;
                _statusMessage = "Preview 帧不符合当前会话或显示契约；页面已丢弃该帧。";
            }
            else if (snapshot.LatestFrame is { } frame && frame.SessionId == sessionId)
            {
                if (_displaySessionId != sessionId)
                {
                    _displaySessionId = sessionId;
                    _displaySequence = -1;
                    _latestImage = null;
                }
                if (frame.Sequence > _displaySequence && image is not null)
                {
                    _latestImage = image;
                    _displaySequence = frame.Sequence;
                }
            }
            else if (_displaySessionId != sessionId)
            {
                ClearDisplayLocked();
            }

            if (frameError is null && _errorCode is "PreviewFrameInvalid" or "PreviewFrameSessionMismatch")
                _errorCode = null;
            _statusMessage = BuildSnapshotStatus(snapshot);
            NotifyStateChangedOnUi();
        }
    }

    private async Task ApplyReadFailureAsync(
        OperationStart? operation,
        long? watchGeneration,
        string errorCode,
        string message,
        bool clearSnapshot)
    {
        await InvokeOnDispatcherAsync(() =>
        {
            lock (_sync)
            {
                if (_disposed ||
                    (operation.HasValue && !IsCurrentLocked(operation.Value)) ||
                    (watchGeneration.HasValue && watchGeneration.Value != _watchGeneration))
                    return;
                if (clearSnapshot)
                {
                    _snapshotAvailable = false;
                    _snapshot = null;
                    ClearDisplayLocked();
                }
                _errorCode = SafeReason(errorCode, "PreviewSnapshotUnavailable");
                _statusMessage = message;
                NotifyStateChangedOnUi();
            }
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ApplyAccessAsync(OperationStart start, PreviewSessionAccess access)
    {
        await InvokeOnDispatcherAsync(() =>
        {
            lock (_sync)
            {
                if (!IsCurrentLocked(start)) return;
                _access = access;
                if (!access.CanRun)
                    _errorCode = SafeReason(access.ReasonCode, "PreviewAccessDenied");
            }
            NotifyStateChangedOnUi();
        }, start.Cancellation.Token).ConfigureAwait(false);
    }

    private async Task ApplyOutcomeAsync(OperationStart start, RuntimeCommandOutcome outcome)
    {
        await InvokeOnDispatcherAsync(() =>
        {
            lock (_sync)
            {
                if (!IsCurrentLocked(start)) return;
                _lastCommandOutcome = outcome;
                _inputErrorCode = null;
                _errorCode = outcome.Disposition == CommandDisposition.Accepted
                    ? null : SafeReason(outcome.ReasonCode, "PreviewCommandRejected");
                _statusMessage = outcome.Disposition == CommandDisposition.Accepted
                    ? "Preview 命令已受理；请等待后续快照确认最终阶段。"
                    : "Preview 命令被 Runtime 拒绝；页面没有预先改变状态。";
            }
            NotifyStateChangedOnUi();
        }, start.Cancellation.Token).ConfigureAwait(false);
    }

    private OperationStart? Begin(OperationKind kind, CancellationToken cancellationToken)
    {
        OperationStart start;
        lock (_sync)
        {
            if (_disposed || _isBusy) return null;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _operationVersion++;
            _activeCancellation = linked;
            _isBusy = true;
            _inputErrorCode = null;
            start = new OperationStart(_operationVersion, kind, linked, CurrentSession);
        }
        NotifyStateChangedOnUi();
        return start;
    }

    private void Complete(OperationStart start)
    {
        var changed = false;
        lock (_sync)
        {
            if (IsCurrentLocked(start))
            {
                _activeCancellation = null;
                _isBusy = false;
                changed = true;
            }
        }
        start.Cancellation.Dispose();
        if (changed) NotifyStateChangedOnUi();
    }

    private bool IsCurrentLocked(OperationStart start) => !_disposed && _isBusy &&
        _operationVersion == start.Version && ReferenceEquals(_activeCancellation, start.Cancellation);

    private void ApplyUnavailable(string code, string message, bool clearSnapshot)
    {
        lock (_sync)
        {
            if (clearSnapshot)
            {
                _snapshotAvailable = false;
                _snapshot = null;
                ClearDisplayLocked();
            }
            _errorCode = SafeReason(code, "PreviewUnavailable");
            _statusMessage = message;
        }
        NotifyStateChangedOnUi();
    }

    private void ApplyInputFailure(string code, string message, bool clearSnapshot)
    {
        lock (_sync)
        {
            if (clearSnapshot)
            {
                _snapshotAvailable = false;
                _snapshot = null;
                ClearDisplayLocked();
            }
            _inputErrorCode = SafeReason(code, "PreviewInputInvalid");
            _errorCode = _inputErrorCode;
            _statusMessage = message;
        }
        NotifyStateChangedOnUi();
    }

    private void ApplyFailure(OperationStart start, string code, string message, bool clearSnapshot)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            if (clearSnapshot)
            {
                _snapshotAvailable = false;
                _snapshot = null;
                ClearDisplayLocked();
            }
            _errorCode = SafeReason(code, "PreviewOperationFailed");
            _statusMessage = message;
        }
        NotifyStateChangedOnUi();
    }

    private void ApplyCancellation(OperationStart start)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            _errorCode = null;
            _statusMessage = "Preview 请求已取消；Runtime 会话不会因页面取消而自动退出。";
        }
        NotifyStateChangedOnUi();
    }

    private async Task ClearDisplayAsync()
    {
        await InvokeOnDispatcherAsync(() =>
        {
            lock (_sync)
            {
                ClearDisplayLocked();
            }
            NotifyStateChangedOnUi();
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task InvokeOnDispatcherAsync(Action action, CancellationToken cancellationToken)
    {
        await _presentationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _dispatcher.InvokeAsync(action).ConfigureAwait(false);
        }
        finally
        {
            _presentationGate.Release();
        }
    }

    private void ClearDisplayLocked()
    {
        _latestImage = null;
        _displaySessionId = null;
        _displaySequence = -1;
    }

    private void SetReason(ref string field, string? value, string propertyName)
    {
        var bounded = value ?? string.Empty;
        if (bounded.Length > 512 || bounded.Any(char.IsControl)) bounded = string.Empty;
        lock (_sync)
        {
            if (_disposed) return;
            if (EqualityComparer<string>.Default.Equals(field, bounded)) return;
            field = bounded;
        }
        OnPropertyChanged(propertyName);
        RaiseCommandAvailability();
    }

    private void SessionChanged(object? sender, InteractiveSessionChangedEventArgs args)
    {
        CancelPendingOperations();
        lock (_sync)
        {
            _session = args.Session;
            _access = null;
            _snapshotAvailable = false;
            _snapshot = null;
            ClearDisplayLocked();
            _errorCode = "PreviewSessionChanged";
            _statusMessage = "会话已变化，Preview 页面数据已清除；Runtime 会话未被页面自动退出。";
        }
        _ = StopWatchingIgnoringFailureAsync();
        _ = _dispatcher.InvokeAsync(() => SessionInvalidated?.Invoke(this, EventArgs.Empty));
        NotifyStateChangedOnUi();
    }

    private void StationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) =>
        NotifyStateChangedOnUi();

    private void StationStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) =>
        NotifyStateChangedOnUi();

    private void NotifyStateChangedOnUi()
    {
        if (_dispatcher.CheckAccess)
            NotifyStateChanged();
        else
            _ = _dispatcher.InvokeAsync(NotifyStateChanged);
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(string.Empty);
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsWatching));
        OnPropertyChanged(nameof(CurrentSession));
        OnPropertyChanged(nameof(IsAuthenticated));
        OnPropertyChanged(nameof(IsShellSnapshotFresh));
        OnPropertyChanged(nameof(ShellSnapshotFreshness));
        OnPropertyChanged(nameof(IsSnapshotFresh));
        OnPropertyChanged(nameof(IsSnapshotUnavailable));
        OnPropertyChanged(nameof(Access));
        OnPropertyChanged(nameof(CurrentAccess));
        OnPropertyChanged(nameof(RequiresStepUp));
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(CurrentSnapshot));
        OnPropertyChanged(nameof(LatestImage));
        OnPropertyChanged(nameof(DisplayImage));
        OnPropertyChanged(nameof(LatestPreviewImage));
        OnPropertyChanged(nameof(LatestFrame));
        OnPropertyChanged(nameof(CurrentPreviewSessionId));
        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(Restoration));
        OnPropertyChanged(nameof(RecoveryRequired));
        OnPropertyChanged(nameof(IsSessionActive));
        OnPropertyChanged(nameof(RestorationVerified));
        OnPropertyChanged(nameof(LastCommandOutcome));
        OnPropertyChanged(nameof(LastCommandSummary));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ErrorCode));
        OnPropertyChanged(nameof(InputErrorCode));
        OnPropertyChanged(nameof(PhaseSummary));
        OnPropertyChanged(nameof(RestorationSummary));
        OnPropertyChanged(nameof(ValidationSummary));
        RaiseCommandAvailability();
    }

    private void RaiseCommandAvailability()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        TuneCommand.RaiseCanExecuteChanged();
        FreezeCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        ExitCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanApplyTuning));
        OnPropertyChanged(nameof(CanTune));
        OnPropertyChanged(nameof(CanFreeze));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanExit));
    }

    private static bool IsActivePreview(PreviewSessionSnapshot? snapshot) =>
        snapshot?.IsSessionActive == true && snapshot.PreviewSessionId is { } id && id != Guid.Empty;

    private static bool IsActiveTuningPhase(PreviewSessionSnapshot? snapshot) =>
        IsActivePreview(snapshot) &&
        snapshot!.Phase is (PreviewSessionPhase.Streaming or PreviewSessionPhase.Tuning);

    private Guid ReadCurrentPreviewSessionId()
    {
        lock (_sync) return _snapshot?.PreviewSessionId ?? Guid.Empty;
    }

    private static bool SameDraft(PreviewDraftReference? left, PreviewDraftReference right) =>
        left is not null && left.DraftId == right.DraftId && left.Revision == right.Revision &&
        string.Equals(left.RevisionContentHash, right.RevisionContentHash, StringComparison.Ordinal);

    private static bool TryReason(string? value, out string reason)
    {
        reason = value ?? string.Empty;
        return reason.Length <= 512 && !string.IsNullOrWhiteSpace(reason) &&
            !reason.Any(char.IsControl);
    }

    private static bool IsUsableSession(InteractiveSession session) =>
        session.State == InteractiveSessionState.Authenticated &&
        session.SessionId is { } id && id != Guid.Empty && !string.IsNullOrWhiteSpace(session.PrincipalId);

    private static bool SameSession(InteractiveSession left, InteractiveSession right) =>
        IsUsableSession(left) && IsUsableSession(right) && left.SessionId == right.SessionId &&
        string.Equals(left.PrincipalId, right.PrincipalId, StringComparison.Ordinal);

    private static CommandInvocation CreateInvocation(InteractiveSession session) =>
        new(CommandSource.PhysicalConsole, session.PrincipalId, session.SessionId);

    private InteractiveSession ReadSession()
    {
        lock (_sync) return _session;
    }

    private static string SafeReason(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.')))
            return fallback;
        return value;
    }

    private string BuildSnapshotStatus(PreviewSessionSnapshot snapshot)
    {
        if (snapshot.RecoveryRequired || snapshot.Phase == PreviewSessionPhase.RecoveryBlocked ||
            snapshot.Restoration == PreviewRestorationState.RecoveryBlocked)
            return "Preview 恢复被阻断；Ready 保持 false，请处理 Runtime 恢复状态。";
        if (snapshot.Restoration == PreviewRestorationState.Pending ||
            snapshot.Phase == PreviewSessionPhase.Restoring)
            return "Runtime 正在恢复 Active 配置；页面等待新鲜快照，不宣称已恢复。";
        if (snapshot.Restoration == PreviewRestorationState.NoActiveBaselineClosed)
            return "Preview 已关闭；Runtime 报告没有 Active 基线可恢复。";
        if (snapshot.Restoration == PreviewRestorationState.Restored)
            return "Preview 已关闭，Active 配置已由 Runtime 恢复并读回。";
        return snapshot.IsSessionActive
            ? "Preview 会话由 Runtime 持有；页面只显示有界最新帧。"
            : "当前没有活动 Preview 会话；页面不会自动进入。";
    }

    private async Task StartWatchingIgnoringFailureAsync()
    {
        try { await StartWatchingAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task StopWatchingIgnoringFailureAsync()
    {
        try { await StopWatchingAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { }
    }

    private static async Task AwaitCancelledTaskAsync(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private static readonly InteractiveSession UnauthenticatedSession =
        new(InteractiveSessionState.Unauthenticated, null, null);
}
