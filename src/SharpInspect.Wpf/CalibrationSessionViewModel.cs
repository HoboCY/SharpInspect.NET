using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Presentation adapter for the bounded, non-production calibration session.
/// Runtime remains the owner of admission, camera ownership, evidence and the
/// session identifier.  This view model only reads projections and submits the
/// typed commands exposed by <see cref="IStationRuntime"/>.
/// </summary>
public sealed class CalibrationSessionViewModel : ObservableObject, IAsyncDisposable
{
    private enum OperationKind
    {
        Refresh,
        Start,
        SessionCommand,
        FrameRead
    }

    private readonly record struct OperationStart(
        long Version,
        OperationKind Kind,
        CancellationTokenSource Cancellation,
        InteractiveSession Session);

    /// <summary>
    /// The values used to create the first Start command are captured before
    /// Step-Up.  Rebuilding the command with the returned grant can therefore
    /// never authorize edits made while the password dialog is pending.
    /// </summary>
    private sealed record FrozenStart(
        Guid CorrelationId,
        InteractiveSession Session,
        CalibrationSessionPlan Plan,
        CameraBindingRevision Binding,
        ImagingSetupRevisionReference ImagingSetup,
        string Reason,
        StartCalibrationSessionCommand Command,
        StepUpBinding BindingForStepUp);

    private readonly IStationRuntime? _runtime;
    private readonly ICalibrationSessionQuery? _sessionQuery;
    private readonly ICameraSetupRuntime? _cameraRuntime;
    private readonly IImagingSetupRuntime? _imagingRuntime;
    private readonly IInteractiveSessionService? _sessions;
    private readonly IStepUpAuthentication? _stepUp;
    private readonly IUiDispatcher _dispatcher;
    private readonly object _sync = new();

    private readonly ObservableCollection<CalibrationFrameEvidence> _frames = new();
    private readonly ObservableCollection<CalibrationObservationEvidence> _observations = new();
    private readonly ObservableCollection<CalibrationEvidenceExclusion> _exclusions = new();
    private readonly ObservableCollection<CalibrationFrameDisplayRow> _frameRows = new();
    private readonly ObservableCollection<CalibrationObservationDisplayRow> _observationRows = new();
    private readonly ObservableCollection<CalibrationExclusionDisplayRow> _exclusionRows = new();
    private readonly ObservableCollection<CalibrationMetricDisplayRow> _candidateMetricRows = new();

    private readonly ReadOnlyObservableCollection<CalibrationFrameEvidence> _readOnlyFrames;
    private readonly ReadOnlyObservableCollection<CalibrationObservationEvidence> _readOnlyObservations;
    private readonly ReadOnlyObservableCollection<CalibrationEvidenceExclusion> _readOnlyExclusions;
    private readonly ReadOnlyObservableCollection<CalibrationFrameDisplayRow> _readOnlyFrameRows;
    private readonly ReadOnlyObservableCollection<CalibrationObservationDisplayRow> _readOnlyObservationRows;
    private readonly ReadOnlyObservableCollection<CalibrationExclusionDisplayRow> _readOnlyExclusionRows;
    private readonly ReadOnlyObservableCollection<CalibrationMetricDisplayRow> _readOnlyCandidateMetricRows;

    private CancellationTokenSource? _activeCancellation;
    private long _operationVersion;
    private InteractiveSession _session;
    private CalibrationSessionPlan? _plan;
    private CameraBindingRevision? _currentBinding;
    private ImagingSetupRevision? _currentImagingRevision;
    private StationStateSnapshot? _stationSnapshot;
    private CalibrationSessionEvidence? _evidence;
    private CalibrationFrameEvidence? _selectedFrame;
    private CalibrationFrameDisplayRow? _selectedFrameRow;
    private BitmapSource? _selectedFrameImage;
    private RuntimeCommandOutcome? _lastCommandOutcome;
    private StepUpBinding? _lastStepUpBinding;
    private AuditedCommandKind? _lastSubmittedCommandKind;
    private string _startReason = string.Empty;
    private string _excludeReason = string.Empty;
    private string _exitReason = string.Empty;
    private string _statusMessage;
    private string? _errorCode;
    private string? _inputErrorCode;
    private bool _isBusy;
    private bool _disposed;

    public CalibrationSessionViewModel(
        IStationRuntime? runtime,
        ICalibrationSessionQuery? sessionQuery,
        ICameraSetupRuntime? cameraRuntime,
        IImagingSetupRuntime? imagingRuntime,
        IInteractiveSessionService? sessions,
        IStepUpAuthentication? stepUp = null,
        IUiDispatcher? dispatcher = null)
    {
        _runtime = runtime;
        _sessionQuery = sessionQuery;
        _cameraRuntime = cameraRuntime;
        _imagingRuntime = imagingRuntime;
        _sessions = sessions;
        _stepUp = stepUp;
        _dispatcher = dispatcher ?? new DispatcherUiDispatcher();
        _session = sessions?.Current ?? UnauthenticatedSession;

        _readOnlyFrames = new ReadOnlyObservableCollection<CalibrationFrameEvidence>(_frames);
        _readOnlyObservations = new ReadOnlyObservableCollection<CalibrationObservationEvidence>(_observations);
        _readOnlyExclusions = new ReadOnlyObservableCollection<CalibrationEvidenceExclusion>(_exclusions);
        _readOnlyFrameRows = new ReadOnlyObservableCollection<CalibrationFrameDisplayRow>(_frameRows);
        _readOnlyObservationRows = new ReadOnlyObservableCollection<CalibrationObservationDisplayRow>(_observationRows);
        _readOnlyExclusionRows = new ReadOnlyObservableCollection<CalibrationExclusionDisplayRow>(_exclusionRows);
        _readOnlyCandidateMetricRows = new ReadOnlyObservableCollection<CalibrationMetricDisplayRow>(_candidateMetricRows);

        _statusMessage = IsConfigured
            ? "请先配置标定方案并登录，再刷新实际绑定、成像修订和工位状态。打开页面不会进入标定会话。"
            : "标定会话不可用：Runtime、会话、绑定、成像或证据查询服务未完整配置。";
        _errorCode = IsConfigured ? null : "CalibrationSessionRuntimeUnavailable";

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => CanRefresh);
        StartCommand = new AsyncRelayCommand(() => StartCalibrationSessionAsync(string.Empty), () => CanStart);
        CaptureCommand = new AsyncRelayCommand(() => CaptureCalibrationFrameAsync(), () => CanCapture);
        ExcludeCommand = new AsyncRelayCommand(
            () => ExcludeCalibrationFrameAsync(SelectedFrame?.FrameId ?? Guid.Empty, ExcludeReason),
            () => CanExclude);
        ComputeCommand = new AsyncRelayCommand(() => ComputeCalibrationCandidateAsync(), () => CanCompute);
        ExitCommand = new AsyncRelayCommand(() => ExitCalibrationSessionAsync(ExitReason), () => CanExit);
        ReadSelectedFrameCommand = new AsyncRelayCommand(() => ReadSelectedFrameAsync(), () => CanReadSelectedFrame);
        CancelCommand = new AsyncRelayCommand(() =>
        {
            CancelPendingOperations();
            return Task.CompletedTask;
        }, () => IsBusy);

        if (_sessions is not null)
            _sessions.Changed += SessionChanged;
    }

    /// <summary>Convenience overload matching the other WPF adapters.</summary>
    public CalibrationSessionViewModel(
        IStationRuntime? runtime,
        ICalibrationSessionQuery? sessionQuery,
        ICameraSetupRuntime? cameraRuntime,
        IImagingSetupRuntime? imagingRuntime,
        IInteractiveSessionService? sessions,
        IUiDispatcher dispatcher,
        IStepUpAuthentication? stepUp = null)
        : this(runtime, sessionQuery, cameraRuntime, imagingRuntime, sessions, stepUp, dispatcher)
    {
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand CaptureCommand { get; }
    public AsyncRelayCommand ExcludeCommand { get; }
    public AsyncRelayCommand ComputeCommand { get; }
    public AsyncRelayCommand ExitCommand { get; }
    public AsyncRelayCommand ReadSelectedFrameCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }

    public AsyncRelayCommand StartCalibrationCommand => StartCommand;
    public AsyncRelayCommand CaptureFrameCommand => CaptureCommand;
    public AsyncRelayCommand ExcludeFrameCommand => ExcludeCommand;
    public AsyncRelayCommand ComputeCandidateCommand => ComputeCommand;
    public AsyncRelayCommand ExitSessionCommand => ExitCommand;

    /// <summary>The panel clears its native PasswordBox when this event fires.</summary>
    internal event EventHandler? SessionInvalidated;

    public bool IsConfigured => _runtime is not null && _sessionQuery is not null &&
        _cameraRuntime is not null && _imagingRuntime is not null && _sessions is not null;

    public bool HasStepUpService => _stepUp is not null;

    public bool IsBusy
    {
        get { lock (_sync) return _isBusy; }
    }

    public InteractiveSession CurrentSession => _sessions?.Current ?? ReadSession();

    public bool IsAuthenticated => IsUsableSession(CurrentSession);

    /// <summary>Host-supplied immutable plan; the page has no plan editor.</summary>
    public CalibrationSessionPlan? Plan
    {
        get { lock (_sync) return _plan; }
    }

    public bool HasPlan => Plan is not null;

    public string LogicalCameraRole => Plan?.Requirement.LogicalCameraRole ?? string.Empty;

    public string RequirementSummary => Plan is null
        ? "尚未配置 CalibrationRequirement。"
        : $"{Plan.Requirement.LogicalCameraRole} · {Plan.Requirement.Kind} · {Plan.Requirement.LogicalPurpose} · " +
          $"系数契约={Plan.Requirement.CoefficientContract.Id}/{Plan.Requirement.CoefficientContract.Version} · " +
          $"验收策略={Plan.Requirement.AcceptancePolicy.Id}/{Plan.Requirement.AcceptancePolicy.Version} · " +
          $"需求哈希={Plan.Requirement.ContentHash}";

    public string ProcedureSummary => Plan is null
        ? "尚未配置 CalibrationProcedure。"
        : $"过程={Plan.Procedure.Procedure.Id}/{Plan.Procedure.Procedure.Version} · " +
          $"输入契约={Plan.Procedure.InputContract.Id}/{Plan.Procedure.InputContract.Version} · " +
          $"标定类型={Plan.Procedure.CalibrationKind} · 过程哈希={Plan.Procedure.ContentHash}";

    public string InputSummary => Plan is null
        ? "尚未配置类型化过程输入。"
        : $"类型化输入 {Plan.Input.Length} bytes · 输入哈希={Plan.Input.CanonicalBytesHash} · " +
          $"输入内容={Plan.Input.ContentHash}";

    public string SelectionPolicySummary => Plan is null
        ? "尚未配置证据选择要求。"
        : $"至少帧数={Plan.SelectionPolicy.MinimumFrames} · 每帧特征={Plan.SelectionPolicy.MinimumFeaturesPerFrame} · " +
          $"覆盖率={Plan.SelectionPolicy.MinimumImageCoverage.ToString("P", CultureInfo.InvariantCulture)} · " +
          $"策略哈希={Plan.SelectionPolicy.ContentHash}";

    public string TemporaryConfigurationSummary => Plan is null
        ? "尚未配置临时采集设置。"
        : $"SoftwareTrigger · {Plan.TemporaryConfiguration.PixelFormat} · " +
          $"ROI={Plan.TemporaryConfiguration.RegionOfInterest.Width}×{Plan.TemporaryConfiguration.RegionOfInterest.Height} · " +
          $"曝光={Plan.TemporaryConfiguration.ExposureTimeUs.ToString("R", CultureInfo.InvariantCulture)}µs · " +
          $"增益={Plan.TemporaryConfiguration.GainDb.ToString("R", CultureInfo.InvariantCulture)}dB · " +
          $"超时={Plan.TemporaryConfiguration.AcquisitionTimeoutMs}ms · " +
          $"设置哈希={Plan.TemporaryConfigurationHash}";

    public CameraBindingRevision? CurrentBinding
    {
        get { lock (_sync) return _currentBinding; }
    }

    public string TemporaryReadbackSummary => Evidence?.TemporaryConfiguration is { } applied
        ? $"临时设置已读回：{applied.Effective.PixelFormat} · " +
          $"ROI={applied.Effective.RegionOfInterest.Width}×{applied.Effective.RegionOfInterest.Height} · " +
          $"曝光={applied.Effective.ExposureTimeUs.ToString("R", CultureInfo.InvariantCulture)}µs · " +
          $"增益={applied.Effective.GainDb.ToString("R", CultureInfo.InvariantCulture)}dB · " +
          $"读回哈希={applied.EffectiveHash}"
        : "尚无临时设置的实际读回证据。";

    public ImagingSetupRevision? CurrentImagingRevision
    {
        get { lock (_sync) return _currentImagingRevision; }
    }

    public CameraBindingRevision? ActualBinding => CurrentBinding;

    public ImagingSetupRevision? CurrentRevision => CurrentImagingRevision;

    public long ExpectedBindingRevision => CurrentBinding?.Revision ?? 0;

    public string? ExpectedBindingRevisionHash => CurrentBinding?.RevisionHash;

    public long ExpectedImagingRevision => CurrentImagingRevision?.Revision ?? 0;

    public string? ExpectedImagingRevisionHash => CurrentImagingRevision?.RevisionHash;

    public bool HasCurrentBinding => CurrentBinding is not null &&
        string.Equals(CurrentBinding.LogicalRole, LogicalCameraRole, StringComparison.Ordinal) &&
        CurrentBinding.Revision > 0 && IsUpperSha256(CurrentBinding.RevisionHash);

    public bool HasCurrentImagingRevision => CurrentImagingRevision is not null &&
        string.Equals(CurrentImagingRevision.LogicalCameraRole, LogicalCameraRole, StringComparison.Ordinal) &&
        CurrentImagingRevision.Revision > 0 && IsUpperSha256(CurrentImagingRevision.RevisionHash) &&
        HasCurrentBinding && CurrentImagingRevision.Binding.Revision == CurrentBinding!.Revision &&
        string.Equals(CurrentImagingRevision.Binding.RevisionHash, CurrentBinding.RevisionHash,
            StringComparison.Ordinal);

    public StationStateSnapshot? StationSnapshot
    {
        get { lock (_sync) return _stationSnapshot; }
    }

    /// <summary>
    /// The session identifier is read only from the Runtime snapshot. The UI
    /// never creates or guesses one after an accepted start command.
    /// </summary>
    public Guid? CurrentSessionId => StationSnapshot?.CalibrationSession?.SessionId;

    public CalibrationSessionState? CalibrationSession => StationSnapshot?.CalibrationSession;

    public CalibrationSessionEvidence? Evidence
    {
        get { lock (_sync) return _evidence; }
    }

    public ReadOnlyObservableCollection<CalibrationFrameEvidence> Frames => _readOnlyFrames;
    public ReadOnlyObservableCollection<CalibrationObservationEvidence> Observations => _readOnlyObservations;
    public ReadOnlyObservableCollection<CalibrationEvidenceExclusion> Exclusions => _readOnlyExclusions;
    public ReadOnlyObservableCollection<CalibrationFrameDisplayRow> FrameRows => _readOnlyFrameRows;
    public ReadOnlyObservableCollection<CalibrationObservationDisplayRow> ObservationRows => _readOnlyObservationRows;
    public ReadOnlyObservableCollection<CalibrationExclusionDisplayRow> ExclusionRows => _readOnlyExclusionRows;
    public ReadOnlyObservableCollection<CalibrationMetricDisplayRow> CandidateMetricRows => _readOnlyCandidateMetricRows;

    public CalibrationCandidateEvidence? Candidate => Evidence?.Candidate;

    public CalibrationSelectionEvaluation? SelectionEvaluation => Evidence?.Selection;

    public CalibrationFrameEvidence? SelectedFrame
    {
        get { lock (_sync) return _selectedFrame; }
        set
        {
            CalibrationFrameEvidence? normalized = null;
            lock (_sync)
            {
                if (value is not null && _frames.Any(frame => frame.FrameId == value.FrameId &&
                    string.Equals(frame.SourceHash, value.SourceHash, StringComparison.Ordinal)))
                    normalized = value;
                if (ReferenceEquals(_selectedFrame, normalized)) return;
                _selectedFrame = normalized;
                _selectedFrameRow = normalized is null
                    ? null
                    : _frameRows.FirstOrDefault(row => row.FrameId == normalized.FrameId &&
                        string.Equals(row.SourceHash, normalized.SourceHash, StringComparison.Ordinal));
                _selectedFrameImage = null;
            }
            OnPropertyChanged(nameof(SelectedFrame));
            OnPropertyChanged(nameof(SelectedFrameRow));
            OnPropertyChanged(nameof(SelectedFrameImage));
            OnPropertyChanged(nameof(CanReadSelectedFrame));
            OnPropertyChanged(nameof(CanExclude));
            RaiseCommands();
        }
    }

    /// <summary>Read-only grid projection; setting it only selects an existing evidence frame.</summary>
    public CalibrationFrameDisplayRow? SelectedFrameRow
    {
        get { lock (_sync) return _selectedFrameRow; }
        set
        {
            CalibrationFrameEvidence? frame;
            lock (_sync)
            {
                frame = value is null
                    ? null
                    : _frames.FirstOrDefault(item => item.FrameId == value.FrameId &&
                        string.Equals(item.SourceHash, value.SourceHash, StringComparison.Ordinal));
            }
            SelectedFrame = frame;
        }
    }

    public BitmapSource? SelectedFrameImage
    {
        get { lock (_sync) return _selectedFrameImage; }
    }

    public bool HasSelectedFrameImage => SelectedFrameImage is not null;

    public string StartReason
    {
        get { lock (_sync) return _startReason; }
        set => SetText(ref _startReason, value, nameof(StartReason), 512);
    }

    public string ExcludeReason
    {
        get { lock (_sync) return _excludeReason; }
        set => SetText(ref _excludeReason, value, nameof(ExcludeReason), 512);
    }

    public string ExitReason
    {
        get { lock (_sync) return _exitReason; }
        set => SetText(ref _exitReason, value, nameof(ExitReason), 512);
    }

    public RuntimeCommandOutcome? LastCommandOutcome
    {
        get { lock (_sync) return _lastCommandOutcome; }
    }

    public string LastCommandSummary => LastCommandOutcome is null
        ? "尚未提交标定命令。"
        : $"{_lastSubmittedCommandKind?.ToString() ?? "Calibration"} · " +
          $"{LastCommandOutcome.Disposition} · {LastCommandOutcome.ReasonCode} · 审计={LastCommandOutcome.Audit}";

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

    public string ValidationSummary => InputErrorCode is null
        ? "开始、排除和退出都必须使用明确理由；观测坐标由过程自动产生，页面只读。"
        : "当前输入无效，请修正理由后重试。";

    public string StationGateSummary
    {
        get
        {
            var snapshot = StationSnapshot;
            if (snapshot is null) return "尚未读取工位状态；安全停线条件未验证。";
            return $"Ready={snapshot.Ready} · Busy={snapshot.Busy} · Mode={snapshot.Mode} · " +
                   $"Handshake={snapshot.Handshake} · Recovery={snapshot.Recovery} · " +
                   $"PendingDeliveries={snapshot.Evidence.PendingDeliveries} · " +
                   $"Execution={snapshot.CurrentExecution?.Kind.ToString() ?? "None"}";
        }
    }

    public string CalibrationStateSummary
    {
        get
        {
            var state = CalibrationSession;
            return state is null
                ? "当前没有 Runtime 分配的标定会话。"
                : $"Session={state.SessionId:D} · Phase={state.Phase} · Outcome={state.Outcome} · " +
                  $"Frames={state.FrameCount} · Observations={state.ObservationCount} · " +
                  $"Excluded={state.ExcludedFrameCount} · Reason={state.ReasonCode} · " +
                  $"Restored={state.RestorationVerified}";
        }
    }

    public string SessionHeaderSummary => Evidence is { } evidence
        ? $"RuntimeEpoch={evidence.Header.RuntimeEpoch:D} · Attempt={evidence.Header.AdmissionAttemptId:D} · " +
          $"Actor={evidence.Header.ActorPrincipalId:D} · AuthorizationRevision={evidence.Header.AuthorizationRevision} · " +
          $"BindingRevision={evidence.Header.Binding.Revision} · BindingHash={evidence.Header.Binding.RevisionHash}"
        : "尚未读取标定会话 Header。";

    public string SelectionSummary => SelectionEvaluation is { } selection
        ? $"{(selection.Sufficient ? "证据数量满足" : "证据数量不足")} · 原因={selection.ReasonCode} · " +
          $"纳入帧={selection.IncludedFrameCount} · 足够特征帧={selection.SufficientFeatureFrameCount} · " +
          $"覆盖率={selection.ImageCoverage.ToString("P", CultureInfo.InvariantCulture)} · " +
          $"选择哈希={selection.SelectionHash}"
        : "尚未读取证据选择评估。";

    public string CandidateSummary => Candidate is { } candidate
        ? $"Candidate={candidate.CandidateId:D} · 候选哈希={candidate.ContentHash} · " +
          $"系数契约={candidate.Result.Coefficients.Format.Id}/{candidate.Result.Coefficients.Format.Version} · " +
          $"系数哈希={candidate.Result.Coefficients.ContentHash} · " +
          $"质量指标={candidate.Result.QualityMetrics.Count} · DevelopmentOnly={candidate.DevelopmentOnly} · " +
          $"CanPublish={candidate.CanPublish} · CanActivate={candidate.CanActivate} · " +
          $"原因={candidate.AcceptanceReasonCode}"
        : "尚未保留标定候选。";

    public string CandidateEvidenceSummary => Candidate?.Result.Evidence is { } evidence
        ? $"计算证据：{evidence.Format.Id}/{evidence.Format.Version} · " +
          $"契约哈希={evidence.Format.ContentHash} · 内容哈希={evidence.ContentHash} · {evidence.Length} 字节。" +
          "逐图与逐点明细保留在候选中，可由对应过程的类型化解码器审阅。"
        : "此候选尚无独立计算证据载荷。";

    public string CandidateDiagnosticSummary => Candidate is { } candidate
        ? string.Join("; ", candidate.Result.Diagnostics.Select(diagnostic =>
            diagnostic.Value is null ? diagnostic.Key : $"{diagnostic.Key}={diagnostic.Value}"))
        : "尚无候选诊断。";

    public string NoCoordinateEditingNotice =>
        "原始帧、自动特征像素坐标和观测均为只读证据；页面不提供拖动、输入、添加或删除坐标。";

    public string NoProductionAuthorityNotice =>
        "标定候选仅供开发审阅，不能发布、激活、替换生产 Profile，也不会使工位 Ready。";

    public string NoAutomaticPhysicalDetectionNotice =>
        "本页面不打开设备，也不自动判断镜头、调焦、支架、工作距离或传感器方向。";

    public bool DevelopmentOnly => true;
    public bool ProductionReady => false;
    public bool Ready => false;
    public bool CanPublish => false;
    public bool CanPublishCalibrationProfile => false;
    public bool CanPublishProfile => false;
    public bool CanActivateProfile => false;
    public bool CanActivate => false;

    public bool CanRefresh => IsConfigured && HasPlan && IsAuthenticated && !IsBusy && !_disposed;

    public bool CanStart
    {
        get
        {
            var snapshot = StationSnapshot;
            return IsConfigured && HasPlan && HasStepUpService && IsAuthenticated && !IsBusy && !_disposed &&
                HasCurrentBinding && HasCurrentImagingRevision &&
                !string.IsNullOrWhiteSpace(StartReason) &&
                snapshot is not null && (snapshot.CalibrationSession is null ||
                    snapshot.CalibrationSession is { RestorationVerified: true, OperationInProgress: false }) &&
                snapshot.Lifecycle == RuntimeLifecycle.Running &&
                snapshot.ArmState == ProductionArmState.Disarmed &&
                snapshot.Ready == false && snapshot.Busy == false &&
                snapshot.Evidence.PendingDeliveries == 0 &&
                snapshot.CurrentExecution is null && snapshot.Mode == ExclusiveMode.None;
        }
    }

    public bool HasActiveCalibrationSession => CurrentSessionId is { } id && id != Guid.Empty;

    private bool CanMutateCollectingEvidence => IsConfigured && IsAuthenticated && !IsBusy && !_disposed &&
        HasActiveCalibrationSession && StationSnapshot?.Mode == ExclusiveMode.Calibration && Candidate is null &&
        CalibrationSession is { Phase: CalibrationSessionPhase.Collecting, OperationInProgress: false };

    public bool CanCapture => CanMutateCollectingEvidence;

    public bool CanExclude => CanMutateCollectingEvidence && SelectedFrame is not null &&
        !string.IsNullOrWhiteSpace(ExcludeReason);

    public bool CanCompute => CanMutateCollectingEvidence;

    private bool CanExitSession => IsConfigured && IsAuthenticated && !IsBusy && !_disposed &&
        HasActiveCalibrationSession && StationSnapshot?.Mode == ExclusiveMode.Calibration &&
        CalibrationSession is { Phase: not CalibrationSessionPhase.Restored };

    public bool CanExit => CanExitSession && !string.IsNullOrWhiteSpace(ExitReason);

    public bool CanReadSelectedFrame => IsConfigured && IsAuthenticated && !IsBusy && !_disposed &&
        HasActiveCalibrationSession && SelectedFrame is not null;

    /// <summary>Host calls this when the immutable plan becomes available.</summary>
    public void Configure(CalibrationSessionPlan? plan)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_isBusy)
            {
                _errorCode = "CalibrationSessionBusy";
                _statusMessage = "当前标定请求正在处理中，不能替换标定方案。";
                NotifyStateChangedOnUi();
                return;
            }
            if (CalibrationSession is { RestorationVerified: false })
            {
                _errorCode = "CalibrationSessionActive";
                _statusMessage = "标定会话仍由 Runtime 持有；退出后才能配置新的标定方案。";
                NotifyStateChangedOnUi();
                return;
            }

            _plan = plan;
            ClearReadbackLocked();
            _startReason = string.Empty;
            _excludeReason = string.Empty;
            _exitReason = string.Empty;
            _inputErrorCode = null;
            _errorCode = plan is null ? "CalibrationSessionPlanRequired" : null;
            _statusMessage = plan is null
                ? "尚未配置标定方案；页面不会读取或提交 Runtime。"
                : "标定方案已配置。请刷新实际绑定、成像修订和工位状态；页面不会自动开始会话。";
        }
        RaisePlanChanged();
    }

    public void SetPlan(CalibrationSessionPlan? plan) => Configure(plan);

    public void ConfigurePlan(CalibrationSessionPlan? plan) => Configure(plan);

    /// <summary>Reads actual authorized references and the complete station snapshot.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            ApplyUnavailable("CalibrationSessionRuntimeUnavailable",
                "标定会话不可用：Runtime、会话、绑定、成像或证据查询服务未完整配置。", clearReadback: false);
            return;
        }
        if (!HasPlan)
        {
            ApplyUnavailable("CalibrationSessionPlanRequired",
                "请先由 Host 配置不可变 CalibrationSessionPlan，再刷新当前标定前置条件。", clearReadback: false);
            return;
        }

        var start = Begin(OperationKind.Refresh, cancellationToken);
        if (!start.HasValue) return;
        try
        {
            await RefreshForOperationAsync(start.Value).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            ApplyCancellation(start.Value);
        }
        catch
        {
            ApplyFailure(start.Value, "CalibrationSessionRefreshFailed",
                "标定前置条件读取失败，请重试。", clearReadback: true);
        }
        finally
        {
            Complete(start.Value);
        }
    }

    public Task RefreshStateAsync(CancellationToken cancellationToken = default) => RefreshAsync(cancellationToken);

    /// <summary>
    /// Performs the only Step-Up in this bounded session: admission. The
    /// returned Accepted outcome is deliberately followed by a snapshot read;
    /// no session ID or completion is invented locally.
    /// </summary>
    public async Task<RuntimeCommandOutcome?> StartCalibrationSessionAsync(
        string? stepUpPassword, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || _sessions is null || _stepUp is null)
        {
            ApplyUnavailable("CalibrationSessionOperationUnavailable",
                "标定开始不可用：未配置 Runtime、会话或 Step-Up 服务。", clearReadback: false);
            return null;
        }
        if (!TryFreezeStart(out var frozen, out var validationError, out var validationMessage))
        {
            ApplyInputFailure(validationError!, validationMessage!);
            return null;
        }
        if (string.IsNullOrWhiteSpace(stepUpPassword))
        {
            ApplyInputFailure("CalibrationStepUpPasswordRequired",
                "开始独占标定会话必须输入当前密码再次确认。", clearReadback: false);
            return null;
        }

        var start = Begin(OperationKind.Start, cancellationToken);
        if (!start.HasValue) return null;
        try
        {
            if (!SameSession(frozen!.Session, start.Value.Session))
            {
                ApplyFailure(start.Value, "CalibrationSessionChanged",
                    "会话已变化；本次标定未开始，请重新刷新并确认。", clearReadback: true);
                return null;
            }

            lock (_sync) _lastStepUpBinding = frozen.BindingForStepUp;
            NotifyStateChangedOnUi();

            StepUpResult stepUpResult;
            try
            {
                stepUpResult = await _stepUp.ReauthenticateAsync(
                    new StepUpRequest(frozen.CorrelationId, CreateInvocation(frozen.Session),
                        frozen.BindingForStepUp, stepUpPassword!),
                    start.Value.Cancellation.Token).ConfigureAwait(true);
            }
            finally
            {
                // The secret is never copied to VM state or a command property.
                stepUpPassword = string.Empty;
            }

            start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            if (!stepUpResult.Succeeded || stepUpResult.GrantId is not { } grantId || grantId == Guid.Empty)
            {
                ApplyFailure(start.Value, "StepUpAuthenticationRejected",
                    "当前密码确认未通过；本次标定未开始。", clearReadback: false);
                return null;
            }

            var currentSession = _sessions.Current;
            if (!SameSession(frozen.Session, currentSession))
            {
                ApplyFailure(start.Value, "CalibrationSessionChanged",
                    "会话已变化；Step-Up 回包已作废，本次标定未提交。", clearReadback: true);
                return null;
            }

            var command = new StartCalibrationSessionCommand(
                frozen.CorrelationId,
                CreateInvocation(currentSession, grantId),
                frozen.Plan,
                frozen.Binding.Revision,
                frozen.Binding.RevisionHash,
                frozen.ImagingSetup,
                frozen.Reason);
            if (!string.Equals(command.AuthorizationTarget, frozen.Command.AuthorizationTarget,
                    StringComparison.Ordinal))
            {
                ApplyFailure(start.Value, "CalibrationAuthorizationTargetChanged",
                    "标定授权目标发生变化；本次标定未提交。", clearReadback: false);
                return null;
            }

            var outcome = await SubmitCommandAsync(start.Value, command,
                AuditedCommandKind.StartCalibrationSession).ConfigureAwait(true);
            if (outcome is null) return null;

            // Runtime owns the global SessionId. This refresh only observes it.
            if (outcome.Disposition == CommandDisposition.Accepted)
                await RefreshForOperationAsync(start.Value).ConfigureAwait(true);
            return outcome;
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            ApplyCancellation(start.Value);
            return null;
        }
        catch
        {
            ApplyFailure(start.Value, "CalibrationSessionStartFailed",
                "标定会话开始失败，请刷新前置条件后重试。", clearReadback: false);
            return null;
        }
        finally
        {
            stepUpPassword = string.Empty;
            Complete(start.Value);
        }
    }

    public Task<RuntimeCommandOutcome?> StartAsync(string? stepUpPassword,
        CancellationToken cancellationToken = default) =>
        StartCalibrationSessionAsync(stepUpPassword, cancellationToken);

    public Task<RuntimeCommandOutcome?> StartSessionAsync(string? stepUpPassword,
        CancellationToken cancellationToken = default) =>
        StartCalibrationSessionAsync(stepUpPassword, cancellationToken);

    public Task<RuntimeCommandOutcome?> CaptureAsync(CancellationToken cancellationToken = default) =>
        CaptureCalibrationFrameAsync(cancellationToken);

    public async Task<RuntimeCommandOutcome?> CaptureCalibrationFrameAsync(
        CancellationToken cancellationToken = default)
    {
        return await SubmitActiveCommandAsync(
            OperationKind.SessionCommand,
            AuditedCommandKind.CaptureCalibrationFrame,
            (sessionId, invocation, correlationId) =>
                new CaptureCalibrationFrameCommand(correlationId, invocation, sessionId),
            () => CanCapture,
            "CalibrationCaptureUnavailable",
            "当前没有可采集的 Runtime 标定会话；请先完成会话进入并刷新状态。",
            cancellationToken).ConfigureAwait(true);
    }

    public Task<RuntimeCommandOutcome?> ExcludeAsync(Guid frameId, string? reason,
        CancellationToken cancellationToken = default) =>
        ExcludeCalibrationFrameAsync(frameId, reason, cancellationToken);

    public async Task<RuntimeCommandOutcome?> ExcludeCalibrationFrameAsync(
        Guid frameId, string? reason, CancellationToken cancellationToken = default)
    {
        var suppliedReason = reason ?? string.Empty;
        if (frameId == Guid.Empty)
        {
            ApplyInputFailure("CalibrationFrameRequired", "请先选择一个完整原始帧；排除操作不会删除证据。", false);
            return null;
        }
        if (string.IsNullOrWhiteSpace(suppliedReason))
        {
            ApplyInputFailure("CalibrationExclusionReasonRequired",
                "整帧排除必须记录明确理由。", false);
            return null;
        }
        lock (_sync)
        {
            if (!_frames.Any(frame => frame.FrameId == frameId))
            {
                _inputErrorCode = "CalibrationFrameRequired";
                _errorCode = _inputErrorCode;
                _statusMessage = "所选原始帧不属于当前会话；本次排除未提交。";
                NotifyStateChangedOnUi();
                return null;
            }
            if (_exclusions.Any(exclusion => exclusion.FrameId == frameId))
            {
                _inputErrorCode = "CalibrationFrameAlreadyExcluded";
                _errorCode = _inputErrorCode;
                _statusMessage = "该整帧已经有排除记录；页面不会重复排除或删除证据。";
                NotifyStateChangedOnUi();
                return null;
            }
        }

        return await SubmitActiveCommandAsync(
            OperationKind.SessionCommand,
            AuditedCommandKind.ExcludeCalibrationFrame,
            (sessionId, invocation, correlationId) =>
                new ExcludeCalibrationFrameCommand(correlationId, invocation, sessionId, frameId,
                    BoundedText(suppliedReason, 512)),
            () => CanMutateCollectingEvidence,
            "CalibrationExclusionUnavailable",
            "当前会话已计算候选或正在计算，不能再排除帧；请保留现有证据并开启新会话。",
            cancellationToken).ConfigureAwait(true);
    }

    public Task<RuntimeCommandOutcome?> ComputeAsync(CancellationToken cancellationToken = default) =>
        ComputeCalibrationCandidateAsync(cancellationToken);

    public async Task<RuntimeCommandOutcome?> ComputeCalibrationCandidateAsync(
        CancellationToken cancellationToken = default)
    {
        return await SubmitActiveCommandAsync(
            OperationKind.SessionCommand,
            AuditedCommandKind.ComputeCalibrationCandidate,
            (sessionId, invocation, correlationId) =>
                new ComputeCalibrationCandidateCommand(correlationId, invocation, sessionId),
            () => CanCompute && Candidate is null && CalibrationSession is { Phase: not CalibrationSessionPhase.Computing },
            "CalibrationComputeUnavailable",
            "当前会话不能计算候选；候选存在或计算中时必须保留证据并开启新会话。",
            cancellationToken).ConfigureAwait(true);
    }

    public Task<RuntimeCommandOutcome?> ExitAsync(string? reason, bool cancel = false,
        CancellationToken cancellationToken = default) =>
        ExitCalibrationSessionAsync(reason, cancel, cancellationToken);

    public async Task<RuntimeCommandOutcome?> ExitCalibrationSessionAsync(
        string? reason, bool cancel = false, CancellationToken cancellationToken = default)
    {
        var suppliedReason = reason ?? string.Empty;
        if (string.IsNullOrWhiteSpace(suppliedReason))
        {
            ApplyInputFailure("CalibrationExitReasonRequired", "退出标定会话必须记录明确理由。", false);
            return null;
        }
        return await SubmitActiveCommandAsync(
            OperationKind.SessionCommand,
            AuditedCommandKind.ExitCalibrationSession,
            (sessionId, invocation, correlationId) =>
                new ExitCalibrationSessionCommand(correlationId, invocation, sessionId,
                    BoundedText(suppliedReason, 512), cancel),
            () => CanExitSession,
            "CalibrationExitUnavailable",
            "当前没有可退出的 Runtime 标定会话；导航不会代替退出命令。",
            cancellationToken).ConfigureAwait(true);
    }

    public Task<CalibrationFrameQueryResult?> ReadCalibrationFrameAsync(
        CancellationToken cancellationToken = default) => ReadSelectedFrameAsync(cancellationToken);

    /// <summary>
    /// Reads a verified copy of the selected source frame. It renders only the
    /// original pixels; feature points and overlays are deliberately absent.
    /// </summary>
    public async Task<CalibrationFrameQueryResult?> ReadSelectedFrameAsync(
        CancellationToken cancellationToken = default)
    {
        Guid sessionId;
        CalibrationFrameEvidence frame;
        lock (_sync)
        {
            if (_stationSnapshot?.CalibrationSession?.SessionId is not { } id || id == Guid.Empty ||
                _selectedFrame is null)
            {
                _inputErrorCode = "CalibrationFrameReadUnavailable";
                _errorCode = _inputErrorCode;
                _statusMessage = "请先刷新当前 Runtime 会话并选择一个原始帧。";
                NotifyStateChangedOnUi();
                return null;
            }
            sessionId = id;
            frame = _selectedFrame;
        }
        if (!IsConfigured || !IsAuthenticated || _sessionQuery is null)
        {
            ApplyUnavailable("CalibrationFrameReadUnavailable",
                "原始帧读取不可用：当前会话或证据查询服务不可用。", false);
            return null;
        }

        var start = Begin(OperationKind.FrameRead, cancellationToken);
        if (!start.HasValue) return null;
        try
        {
            var result = await _sessionQuery.ReadCalibrationFrameAsync(
                sessionId, frame.FrameId, frame.SourceHash, CreateInvocation(start.Value.Session),
                start.Value.Cancellation.Token).ConfigureAwait(true);
            start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            if (!result.Available || result.Image is null)
            {
                ApplyFailure(start.Value, SafeReason(result.ReasonCode, "CalibrationFrameUnavailable"),
                    "所选原始帧当前不可用。", false);
                return result;
            }

            var image = result.Image;
            if (image.Frame.SessionId != sessionId || image.Frame.FrameId != frame.FrameId ||
                !string.Equals(image.Frame.SourceHash, frame.SourceHash, StringComparison.Ordinal) ||
                !string.Equals(image.Frame.Metadata.LogicalCameraRole, LogicalCameraRole,
                    StringComparison.Ordinal))
            {
                ApplyFailure(start.Value, "CalibrationFrameResultInvalid",
                    "原始帧读回未通过当前会话、帧身份或来源哈希校验。", false);
                return new CalibrationFrameQueryResult(false, "CalibrationFrameResultInvalid");
            }

            var bitmap = CreateBitmap(image);
            await _dispatcher.InvokeAsync(() =>
            {
                lock (_sync)
                {
                    if (!IsCurrentLocked(start.Value) || _selectedFrame?.FrameId != frame.FrameId)
                        return;
                    _selectedFrameImage = bitmap;
                    _errorCode = null;
                    _inputErrorCode = null;
                    _statusMessage = "已读取所选原始帧的验证副本；显示不包含可编辑坐标或算法叠加。";
                }
                OnPropertyChanged(nameof(SelectedFrameImage));
                OnPropertyChanged(nameof(HasSelectedFrameImage));
                OnPropertyChanged(nameof(StatusMessage));
                OnPropertyChanged(nameof(ErrorCode));
            }).ConfigureAwait(true);
            return result;
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            ApplyCancellation(start.Value);
            return null;
        }
        catch
        {
            ApplyFailure(start.Value, "CalibrationFrameReadFailed",
                "原始帧读取失败，请重新选择并刷新。", false);
            return null;
        }
        finally
        {
            Complete(start.Value);
        }
    }

    /// <summary>Navigation only clears the view; it never submits Exit.</summary>
    public void Deactivate()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            _operationVersion++;
            cancellation = _activeCancellation;
            _activeCancellation = null;
            _isBusy = false;
            ClearReadbackLocked();
            _lastCommandOutcome = null;
            _lastSubmittedCommandKind = null;
            _lastStepUpBinding = null;
            _startReason = string.Empty;
            _excludeReason = string.Empty;
            _exitReason = string.Empty;
            _errorCode = null;
            _inputErrorCode = null;
            _statusMessage = "标定页面已停用；Runtime 会话未被导航动作退出。再次进入后请刷新。";
        }
        cancellation?.Cancel();
        NotifyStateChangedOnUi();
    }

    /// <summary>Clears view data and the native password box is cleared by the panel.</summary>
    public void ClearSensitiveInputs()
    {
        Deactivate();
        NotifySessionInvalidatedOnUi();
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
                _statusMessage = "标定请求已取消；Runtime 会话和证据未因取消页面请求而删除。";
            }
        }
        cancellation?.Cancel();
        NotifyStateChangedOnUi();
    }

    public void CancelPendingOperation() => CancelPendingOperations();

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
            ClearReadbackLocked();
            _lastCommandOutcome = null;
            _lastStepUpBinding = null;
        }
        cancellation?.Cancel();
        if (_sessions is not null)
            _sessions.Changed -= SessionChanged;
        NotifyStateChangedOnUi();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    internal StepUpBinding? LastStepUpBinding
    {
        get { lock (_sync) return _lastStepUpBinding; }
    }

    private async Task RefreshForOperationAsync(OperationStart start)
    {
        if (!IsUsableSession(start.Session))
        {
            ApplyFailure(start, "CalibrationAuthenticationRequired",
                "请先登录，再读取标定前置条件。", clearReadback: true);
            return;
        }

        var role = LogicalCameraRole;
        if (string.IsNullOrWhiteSpace(role))
        {
            ApplyFailure(start, "CalibrationSessionPlanRequired",
                "当前没有可读取的逻辑相机角色；请由 Host 配置标定方案。", true);
            return;
        }

        CameraSetupQueryResult cameraResult;
        ImagingSetupQueryResult imagingResult;
        StationStateSnapshot stationSnapshot;
        try
        {
            cameraResult = await _cameraRuntime!.GetSetupAsync(role, CreateInvocation(start.Session),
                start.Cancellation.Token).ConfigureAwait(true);
            start.Cancellation.Token.ThrowIfCancellationRequested();
            imagingResult = await _imagingRuntime!.GetImagingSetupAsync(role, CreateInvocation(start.Session),
                start.Cancellation.Token).ConfigureAwait(true);
            start.Cancellation.Token.ThrowIfCancellationRequested();
            stationSnapshot = await _runtime!.GetSnapshotAsync(start.Cancellation.Token).ConfigureAwait(true);
            start.Cancellation.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (start.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            ApplyFailure(start, "CalibrationSessionRefreshFailed",
                "标定绑定、成像修订或工位状态读取失败，请重试。", true);
            return;
        }

        CalibrationSessionQueryResult? detail = null;
        var sessionState = stationSnapshot.CalibrationSession;
        if (sessionState is not null && sessionState.SessionId != Guid.Empty)
        {
            try
            {
                detail = await _sessionQuery!.QueryCalibrationSessionAsync(
                    sessionState.SessionId, CreateInvocation(start.Session), start.Cancellation.Token)
                    .ConfigureAwait(true);
                start.Cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (start.Cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                detail = null;
            }
        }

        await _dispatcher.InvokeAsync(() => ApplyRefreshResult(
            start, role, cameraResult, imagingResult, stationSnapshot, detail)).ConfigureAwait(true);
    }

    private void ApplyRefreshResult(OperationStart start, string role,
        CameraSetupQueryResult cameraResult, ImagingSetupQueryResult imagingResult,
        StationStateSnapshot stationSnapshot, CalibrationSessionQueryResult? detail)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            if (!SameSession(start.Session, _sessions?.Current ?? _session)) return;

            _stationSnapshot = stationSnapshot;
            _currentBinding = cameraResult.Available && cameraResult.Snapshot is { } cameraSnapshot &&
                              cameraSnapshot.Binding is { } binding &&
                              string.Equals(cameraSnapshot.LogicalRole, role, StringComparison.Ordinal) &&
                              string.Equals(binding.LogicalRole, role, StringComparison.Ordinal)
                ? binding
                : null;
            _currentImagingRevision = imagingResult.Available && imagingResult.Current is { } revision &&
                                       string.Equals(revision.LogicalCameraRole, role, StringComparison.Ordinal)
                ? revision
                : null;

            if (stationSnapshot.CalibrationSession is null)
            {
                ClearEvidenceLocked();
            }
            else if (detail is not null)
            {
                ApplyDetailLocked(detail);
            }
            else
            {
                ClearEvidenceLocked();
            }

            _errorCode = null;
            _inputErrorCode = null;
            if (!cameraResult.Available || !HasCurrentBinding)
            {
                _errorCode = cameraResult.Available
                    ? "CalibrationBindingInvalid"
                    : SafeReason(cameraResult.ReasonCode, "CalibrationBindingUnavailable");
                _statusMessage = "当前相机绑定不可用；标定开始保持关闭。请确认授权范围后重试。";
            }
            else if (!imagingResult.Available || _currentImagingRevision is null)
            {
                _errorCode = imagingResult.Available
                    ? "CalibrationImagingRevisionInvalid"
                    : SafeReason(imagingResult.ReasonCode, "CalibrationImagingRevisionUnavailable");
                _statusMessage = "当前成像修订不可用；标定开始保持关闭。请先完成授权读回。";
            }
            else if (!HasCurrentImagingRevision)
            {
                _errorCode = "CalibrationImagingRevisionBindingMismatch";
                _statusMessage = "当前成像修订与实际相机绑定不一致；标定开始保持关闭，请重新刷新。";
            }
            else if (stationSnapshot.CalibrationSession is not null &&
                     (detail is null || !detail.Available || detail.Evidence is null))
            {
                _errorCode = SafeReason(detail?.ReasonCode, "CalibrationSessionEvidenceUnavailable");
                _statusMessage = "已读到 Runtime 标定会话状态，但详细证据暂不可用；页面保持只读且不宣称完成。";
            }
            else if (stationSnapshot.CalibrationSession is null)
            {
                _statusMessage = stationSnapshot.Ready || stationSnapshot.Busy ||
                    stationSnapshot.Evidence.PendingDeliveries != 0 ||
                    stationSnapshot.CurrentExecution is not null || stationSnapshot.Recovery != RecoveryState.None ||
                    stationSnapshot.Handshake != HandshakePhase.Idle || stationSnapshot.Mode != ExclusiveMode.None
                    ? "前置状态已刷新；安全停线条件未满足，Runtime 将再次复查，页面没有绕过按钮。"
                    : "前置状态已刷新；尚未进入标定会话。开始需要明确理由、Step-Up 和 Runtime 接受。";
            }
            else if (_lastCommandOutcome?.Disposition == CommandDisposition.Accepted)
            {
                _statusMessage = stationSnapshot.CalibrationSession.Outcome == CalibrationSessionOutcome.Completed
                    ? "Runtime 快照报告会话已完成；这不产生生产 Profile 发布或激活权限。"
                    : "Runtime 已接受命令，当前快照仍报告会话进行中；接受不等于完成。";
            }
            else
            {
                _statusMessage = "标定会话状态和证据已刷新；候选仅供开发审阅。";
            }
        }
        RaiseRefreshChanged();
    }

    private async Task<RuntimeCommandOutcome?> SubmitActiveCommandAsync(
        OperationKind kind,
        AuditedCommandKind commandKind,
        Func<Guid, CommandInvocation, Guid, RuntimeCommand> commandFactory,
        Func<bool> canExecute,
        string unavailableCode,
        string unavailableMessage,
        CancellationToken cancellationToken)
    {
        if (!canExecute())
        {
            ApplyUnavailable(unavailableCode, unavailableMessage, false);
            return null;
        }

        var start = Begin(kind, cancellationToken);
        if (!start.HasValue) return null;
        try
        {
            Guid sessionId;
            lock (_sync)
            {
                sessionId = _stationSnapshot?.CalibrationSession?.SessionId ?? Guid.Empty;
            }
            if (sessionId == Guid.Empty)
            {
                ApplyFailure(start.Value, unavailableCode, unavailableMessage, false);
                return null;
            }

            var correlationId = Guid.NewGuid();
            RuntimeCommand command;
            try
            {
                command = commandFactory(sessionId, CreateInvocation(start.Value.Session), correlationId);
            }
            catch (ArgumentException)
            {
                ApplyFailure(start.Value, unavailableCode, unavailableMessage, false);
                return null;
            }

            var outcome = await SubmitCommandAsync(start.Value, command, commandKind).ConfigureAwait(true);
            if (outcome is null) return null;
            if (outcome.Disposition == CommandDisposition.Accepted)
                await RefreshForOperationAsync(start.Value).ConfigureAwait(true);
            return outcome;
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            ApplyCancellation(start.Value);
            return null;
        }
        catch
        {
            ApplyFailure(start.Value, SafeReason(commandKind + "Failed", "CalibrationCommandFailed"),
                "标定命令提交失败，请刷新 Runtime 状态后重试。", false);
            return null;
        }
        finally
        {
            Complete(start.Value);
        }
    }

    private async Task<RuntimeCommandOutcome?> SubmitCommandAsync(
        OperationStart start, RuntimeCommand command, AuditedCommandKind commandKind)
    {
        RuntimeCommandOutcome outcome;
        try
        {
            outcome = await _runtime!.SubmitAsync(command, start.Cancellation.Token).ConfigureAwait(true);
            start.Cancellation.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (start.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            ApplyFailure(start, "CalibrationCommandSubmitFailed",
                "标定命令提交失败；当前会话状态未被页面推断。", false);
            return null;
        }

        if (outcome.CorrelationId != command.CorrelationId)
        {
            ApplyFailure(start, "CalibrationCommandOutcomeInvalid",
                "Runtime 命令回包关联号不匹配；页面拒绝据此更新会话状态。", false);
            return null;
        }

        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                if (!IsCurrentLocked(start)) return;
                _lastCommandOutcome = outcome;
                _lastSubmittedCommandKind = commandKind;
                _errorCode = outcome.Disposition == CommandDisposition.Rejected
                    ? SafeReason(outcome.ReasonCode, "CalibrationCommandRejected")
                    : null;
                _statusMessage = outcome.Disposition == CommandDisposition.Accepted
                    ? "Runtime 已接受标定命令；请以后续快照确认阶段和结局，页面不会提前宣称完成。"
                    : "Runtime 拒绝了标定命令；页面保留当前证据和拒绝原因。";
            }
            RaiseCommandChanged();
        }).ConfigureAwait(true);
        return outcome;
    }

    private bool TryFreezeStart(out FrozenStart? frozen, out string? errorCode,
        out string? message)
    {
        frozen = null;
        errorCode = null;
        message = null;
        CalibrationSessionPlan? plan;
        CameraBindingRevision? binding;
        ImagingSetupRevision? revision;
        InteractiveSession session;
        string reason;
        StationStateSnapshot? snapshot;
        lock (_sync)
        {
            plan = _plan;
            binding = _currentBinding;
            revision = _currentImagingRevision;
            session = _sessions?.Current ?? _session;
            reason = _startReason;
            snapshot = _stationSnapshot;
        }
        if (plan is null)
        {
            errorCode = "CalibrationSessionPlanRequired";
            message = "请先由 Host 配置不可变 CalibrationSessionPlan。";
            return false;
        }
        if (!IsUsableSession(session))
        {
            errorCode = "CalibrationAuthenticationRequired";
            message = "请先登录，再开始独占标定会话。";
            return false;
        }
        if (binding is null || binding.LogicalRole != plan.Requirement.LogicalCameraRole ||
            binding.Revision < 1 || !IsUpperSha256(binding.RevisionHash))
        {
            errorCode = "CalibrationBindingRequired";
            message = "请先刷新并确认当前逻辑角色的实际相机绑定。";
            return false;
        }
        if (revision is null || revision.LogicalCameraRole != plan.Requirement.LogicalCameraRole ||
            revision.Revision < 1 || !IsUpperSha256(revision.RevisionHash) ||
            revision.Binding.Revision != binding.Revision ||
            !string.Equals(revision.Binding.RevisionHash, binding.RevisionHash, StringComparison.Ordinal))
        {
            errorCode = "CalibrationImagingRevisionBindingMismatch";
            message = "当前成像修订未绑定到本次读回的实际相机绑定；请重新刷新后再试。";
            return false;
        }
        if (snapshot is null)
        {
            errorCode = "CalibrationStationSnapshotRequired";
            message = "请先刷新工位状态；安全停线、Ready=false 和其他独占工作必须由当前快照验证。";
            return false;
        }
        if (snapshot.CalibrationSession is { RestorationVerified: false } || snapshot.Lifecycle != RuntimeLifecycle.Running ||
            snapshot.ArmState != ProductionArmState.Disarmed || snapshot.Ready || snapshot.Busy ||
            snapshot.Evidence.PendingDeliveries != 0 ||
            snapshot.CurrentExecution is not null || snapshot.Mode != ExclusiveMode.None)
        {
            errorCode = "CalibrationStationNotIdle";
            message = "当前工位未满足独占标定前置条件；安全停线或其他独占工作未验证，页面不提供绕过按钮。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            errorCode = "CalibrationStartReasonRequired";
            message = "开始标定会话必须记录明确理由。";
            return false;
        }

        var correlationId = Guid.NewGuid();
        var imagingReference = ImagingSetupRevisionReference.FromRevision(revision);
        StartCalibrationSessionCommand command;
        try
        {
            command = new StartCalibrationSessionCommand(correlationId, CreateInvocation(session), plan,
                binding.Revision, binding.RevisionHash, imagingReference, BoundedText(reason, 512));
        }
        catch (ArgumentException)
        {
            errorCode = "CalibrationStartIntentInvalid";
            message = "标定开始意图无效；请刷新实际引用并重新填写理由。";
            return false;
        }
        var bindingForStepUp = new StepUpBinding(Permission.RunCalibration, correlationId,
            command.AuthorizationTarget, AuditedCommandKind.StartCalibrationSession);
        frozen = new FrozenStart(correlationId, session, plan, binding, imagingReference,
            command.Reason, command, bindingForStepUp);
        return true;
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
            _inputErrorCode = null;
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
        NotifyStateChangedOnUi();
    }

    private bool IsCurrentLocked(OperationStart start) =>
        !_disposed && _isBusy && _operationVersion == start.Version &&
        ReferenceEquals(_activeCancellation, start.Cancellation);

    private void ApplyUnavailable(string code, string message, bool clearReadback)
    {
        lock (_sync)
        {
            if (clearReadback) ClearReadbackLocked();
            _errorCode = code;
            _statusMessage = message;
        }
        NotifyStateChangedOnUi();
    }

    private void ApplyInputFailure(string code, string message, bool clearReadback = false)
    {
        lock (_sync)
        {
            if (clearReadback) ClearReadbackLocked();
            _inputErrorCode = code;
            _errorCode = code;
            _statusMessage = message;
        }
        NotifyStateChangedOnUi();
    }

    private void ApplyFailure(OperationStart start, string code, string message, bool clearReadback)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            if (clearReadback) ClearReadbackLocked();
            _errorCode = code;
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
            _statusMessage = "标定请求已取消；页面没有据此改变 Runtime 会话或证据。";
        }
        NotifyStateChangedOnUi();
    }

    private void ApplyDetailLocked(CalibrationSessionQueryResult detail)
    {
        var evidence = detail.Available ? detail.Evidence : null;
        var currentSessionId = _stationSnapshot?.CalibrationSession?.SessionId;
        if (evidence is null || currentSessionId is null ||
            evidence.Header.SessionId != currentSessionId || evidence.State.SessionId != currentSessionId ||
            (_plan is not null && evidence.Header.Command.Plan.ContentHash != _plan.ContentHash) ||
            !IsEvidenceConsistent(evidence, currentSessionId.Value))
        {
            ClearEvidenceLocked();
            return;
        }

        _evidence = evidence;
        Replace(_frames, evidence.Frames);
        Replace(_observations, evidence.Observations);
        Replace(_exclusions, evidence.Exclusions);
        Replace(_frameRows, BuildFrameRows(evidence));
        Replace(_observationRows, BuildObservationRows(evidence));
        Replace(_exclusionRows, BuildExclusionRows(evidence));
        Replace(_candidateMetricRows, evidence.Candidate?.Result.QualityMetrics
            .Select(metric => new CalibrationMetricDisplayRow(metric.Key, metric.Value, metric.Unit))
            ?? Array.Empty<CalibrationMetricDisplayRow>());

        if (_selectedFrame is not null && !_frames.Any(frame => frame.FrameId == _selectedFrame.FrameId &&
            frame.SourceHash == _selectedFrame.SourceHash))
        {
            _selectedFrame = null;
            _selectedFrameRow = null;
            _selectedFrameImage = null;
        }
        else if (_selectedFrame is not null)
        {
            _selectedFrameRow = _frameRows.FirstOrDefault(row => row.FrameId == _selectedFrame.FrameId &&
                string.Equals(row.SourceHash, _selectedFrame.SourceHash, StringComparison.Ordinal));
        }
    }

    private static bool IsEvidenceConsistent(CalibrationSessionEvidence evidence, Guid sessionId)
    {
        var frameIds = new HashSet<Guid>();
        foreach (var frame in evidence.Frames)
        {
            if (frame.SessionId != sessionId || !frameIds.Add(frame.FrameId)) return false;
        }
        foreach (var observation in evidence.Observations)
        {
            if (observation.Frame.SessionId != sessionId || !frameIds.Contains(observation.Frame.FrameId))
                return false;
        }
        foreach (var exclusion in evidence.Exclusions)
        {
            if (!frameIds.Contains(exclusion.FrameId)) return false;
        }
        return evidence.Candidate is null || evidence.Candidate.SessionId == sessionId;
    }

    private void ClearReadbackLocked()
    {
        _currentBinding = null;
        _currentImagingRevision = null;
        _stationSnapshot = null;
        ClearEvidenceLocked();
    }

    private void ClearEvidenceLocked()
    {
        _evidence = null;
        _frames.Clear();
        _observations.Clear();
        _exclusions.Clear();
        _frameRows.Clear();
        _observationRows.Clear();
        _exclusionRows.Clear();
        _candidateMetricRows.Clear();
        _selectedFrame = null;
        _selectedFrameRow = null;
        _selectedFrameImage = null;
    }

    private void SessionChanged(object? sender, InteractiveSessionChangedEventArgs args)
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            _session = args.Session;
            _operationVersion++;
            cancellation = _activeCancellation;
            _activeCancellation = null;
            _isBusy = false;
            _errorCode = "CalibrationSessionChanged";
            _inputErrorCode = null;
            _startReason = string.Empty;
            _excludeReason = string.Empty;
            _exitReason = string.Empty;
            _statusMessage = "交互会话已变化；敏感标定读回和待处理回包已清除，请重新登录并刷新。";
        }
        cancellation?.Cancel();
        _ = _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                ClearReadbackLocked();
                _lastCommandOutcome = null;
                _lastSubmittedCommandKind = null;
                _lastStepUpBinding = null;
            }
            SessionInvalidated?.Invoke(this, EventArgs.Empty);
            RaiseRefreshChanged();
            RaiseCommandChanged();
            NotifyStateChangedOnUi();
        });
    }

    private void SetText(ref string field, string? value, string propertyName, int maxLength)
    {
        var normalized = value ?? string.Empty;
        var changed = false;
        lock (_sync)
        {
            if (normalized.Length > maxLength)
            {
                field = normalized;
                _inputErrorCode = "CalibrationReasonTooLong";
                _errorCode = _inputErrorCode;
                changed = true;
            }
            else if (!string.Equals(field, normalized, StringComparison.Ordinal))
            {
                field = normalized;
                if (string.Equals(_inputErrorCode, "CalibrationReasonTooLong", StringComparison.Ordinal))
                    _inputErrorCode = null;
                if (string.Equals(_errorCode, "CalibrationReasonTooLong", StringComparison.Ordinal))
                    _errorCode = null;
                changed = true;
            }
        }
        if (changed)
        {
            OnPropertyChanged(propertyName);
            OnPropertyChanged(nameof(ValidationSummary));
            RaiseCommands();
        }
    }

    private void RaisePlanChanged()
    {
        foreach (var property in new[]
        {
            nameof(Plan), nameof(HasPlan), nameof(LogicalCameraRole), nameof(RequirementSummary),
            nameof(ProcedureSummary), nameof(InputSummary), nameof(SelectionPolicySummary),
            nameof(TemporaryConfigurationSummary), nameof(HasCurrentBinding), nameof(HasCurrentImagingRevision),
            nameof(CanRefresh), nameof(CanStart), nameof(CanCapture), nameof(CanExclude), nameof(CanCompute),
            nameof(CanExit), nameof(CanReadSelectedFrame), nameof(ValidationSummary)
        }) OnPropertyChanged(property);
        RaiseCommands();
    }

    private void RaiseRefreshChanged()
    {
        foreach (var property in new[]
        {
            nameof(CurrentBinding), nameof(ActualBinding), nameof(CurrentImagingRevision), nameof(CurrentRevision), nameof(ExpectedBindingRevision),
            nameof(ExpectedBindingRevisionHash), nameof(ExpectedImagingRevision), nameof(ExpectedImagingRevisionHash),
            nameof(HasCurrentBinding), nameof(HasCurrentImagingRevision), nameof(StationSnapshot),
            nameof(CurrentSessionId), nameof(CalibrationSession), nameof(Evidence), nameof(Frames),
            nameof(Observations), nameof(Exclusions), nameof(FrameRows), nameof(ObservationRows),
            nameof(ExclusionRows), nameof(CandidateMetricRows), nameof(Candidate), nameof(SelectionEvaluation),
            nameof(StationGateSummary), nameof(CalibrationStateSummary), nameof(SessionHeaderSummary),
            nameof(TemporaryReadbackSummary),
            nameof(SelectionSummary), nameof(CandidateSummary), nameof(CandidateEvidenceSummary), nameof(CandidateDiagnosticSummary),
            nameof(SelectedFrame), nameof(SelectedFrameImage),
            nameof(SelectedFrameRow), nameof(HasSelectedFrameImage), nameof(StatusMessage), nameof(ErrorCode), nameof(InputErrorCode),
            nameof(CanStart), nameof(CanCapture), nameof(CanExclude), nameof(CanCompute), nameof(CanExit),
            nameof(CanReadSelectedFrame)
        }) OnPropertyChanged(property);
        RaiseCommands();
    }

    private void RaiseCommandChanged()
    {
        foreach (var property in new[] { nameof(LastCommandOutcome), nameof(LastCommandSummary),
            nameof(StatusMessage), nameof(ErrorCode) }) OnPropertyChanged(property);
        RaiseCommands();
    }

    private void NotifyStateChangedOnUi()
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(StatusMessage));
            OnPropertyChanged(nameof(ErrorCode));
            OnPropertyChanged(nameof(InputErrorCode));
            OnPropertyChanged(nameof(ValidationSummary));
            OnPropertyChanged(nameof(StartReason));
            OnPropertyChanged(nameof(ExcludeReason));
            OnPropertyChanged(nameof(ExitReason));
            OnPropertyChanged(nameof(CanRefresh));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanCapture));
            OnPropertyChanged(nameof(CanExclude));
            OnPropertyChanged(nameof(CanCompute));
            OnPropertyChanged(nameof(CanExit));
            OnPropertyChanged(nameof(CanReadSelectedFrame));
            RaiseCommands();
        });
    }

    private void NotifySessionInvalidatedOnUi() =>
        _ = _dispatcher.InvokeAsync(() => SessionInvalidated?.Invoke(this, EventArgs.Empty));

    private void RaiseCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        CaptureCommand.RaiseCanExecuteChanged();
        ExcludeCommand.RaiseCanExecuteChanged();
        ComputeCommand.RaiseCanExecuteChanged();
        ExitCommand.RaiseCanExecuteChanged();
        ReadSelectedFrameCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    private static IReadOnlyList<CalibrationFrameDisplayRow> BuildFrameRows(CalibrationSessionEvidence evidence)
    {
        var counts = evidence.Observations.GroupBy(item => item.Frame.FrameId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Result.Features.Count));
        var exclusions = evidence.Exclusions.GroupBy(item => item.FrameId)
            .ToDictionary(group => group.Key, group => group.Last().Reason);
        return evidence.Frames.Select(frame => new CalibrationFrameDisplayRow(
            frame.FrameId, frame.SourceHash, frame.PixelHash, frame.Metadata.PixelFormat,
            frame.Metadata.Width, frame.Metadata.Height, counts.GetValueOrDefault(frame.FrameId),
            exclusions.GetValueOrDefault(frame.FrameId))).ToArray();
    }

    private static IReadOnlyList<CalibrationObservationDisplayRow> BuildObservationRows(
        CalibrationSessionEvidence evidence) => evidence.Observations.Select(observation =>
        new CalibrationObservationDisplayRow(
            observation.ObservationId,
            observation.Frame.FrameId,
            observation.Frame.SourceHash,
            observation.Result.Features.Count,
            string.Join("; ", observation.Result.Features.Select(feature =>
                $"{feature.StableFeatureId}=({feature.PixelX.ToString("0.###", CultureInfo.InvariantCulture)}," +
                $"{feature.PixelY.ToString("0.###", CultureInfo.InvariantCulture)})")),
            string.Join("; ", observation.Result.Diagnostics.Select(diagnostic =>
                diagnostic.Value is null ? diagnostic.Key : $"{diagnostic.Key}={diagnostic.Value}")),
            $"{observation.Procedure.Procedure.Id}/{observation.Procedure.Procedure.Version}"))
        .ToArray();

    private static IReadOnlyList<CalibrationExclusionDisplayRow> BuildExclusionRows(
        CalibrationSessionEvidence evidence) => evidence.Exclusions.Select(exclusion =>
        new CalibrationExclusionDisplayRow(exclusion.FrameId, exclusion.Reason,
            exclusion.ActorPrincipalId, exclusion.InteractiveSessionId, exclusion.RecordedAtUtc))
        .ToArray();

    private static BitmapSource CreateBitmap(CalibrationFrameImage image)
    {
        var metadata = image.Frame.Metadata;
        var bytes = image.GetBytes();
        var format = metadata.PixelFormat switch
        {
            VisionPixelFormat.Mono8 => PixelFormats.Gray8,
            VisionPixelFormat.Mono16 => PixelFormats.Gray16,
            VisionPixelFormat.Bgr24 => PixelFormats.Bgr24,
            _ => throw new ArgumentOutOfRangeException(nameof(metadata.PixelFormat))
        };
        if (bytes.LongLength != metadata.ValidRowBytes * (long)metadata.Height)
            throw new ArgumentException("CalibrationFramePixelLengthInvalid", nameof(image));
        var bitmap = BitmapSource.Create(metadata.Width, metadata.Height, 96, 96, format, null,
            bytes, metadata.ValidRowBytes);
        bitmap.Freeze();
        return bitmap;
    }

    private static CommandInvocation CreateInvocation(InteractiveSession session, Guid? grantId = null) =>
        new(CommandSource.PhysicalConsole, session.PrincipalId, session.SessionId, grantId);

    private InteractiveSession ReadSession()
    {
        lock (_sync) return _session;
    }

    private static readonly InteractiveSession UnauthenticatedSession =
        new(InteractiveSessionState.Unauthenticated, null, null);

    private static bool IsUsableSession(InteractiveSession session) =>
        session.State == InteractiveSessionState.Authenticated && session.SessionId is { } id && id != Guid.Empty &&
        !string.IsNullOrWhiteSpace(session.PrincipalId);

    private static bool SameSession(InteractiveSession left, InteractiveSession right) =>
        left.State == right.State && left.SessionId == right.SessionId &&
        string.Equals(left.PrincipalId, right.PrincipalId, StringComparison.Ordinal);

    private static string BoundedText(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("CalibrationReasonRequired");
        if (value.Length > maxLength) throw new ArgumentException("CalibrationReasonTooLong");
        return value;
    }

    private static string SafeReason(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return value.Length > 128 ? value[..128] : value;
    }

    private static bool IsUpperSha256(string? value) => value is { Length: 64 } &&
        value.All(character => character is (>= '0' and <= '9') or (>= 'A' and <= 'F'));
}

/// <summary>Read-only frame identity and automatically derived feature count.</summary>
public sealed record CalibrationFrameDisplayRow(
    Guid FrameId,
    string SourceHash,
    string PixelHash,
    VisionPixelFormat PixelFormat,
    int Width,
    int Height,
    int FeatureCount,
    string? ExclusionReason)
{
    public bool IsExcluded => ExclusionReason is not null;
}

/// <summary>Read-only automatic observation projection including pixel coordinates.</summary>
public sealed record CalibrationObservationDisplayRow(
    Guid ObservationId,
    Guid FrameId,
    string SourceHash,
    int FeatureCount,
    string FeatureSummary,
    string DiagnosticSummary,
    string Procedure);

public sealed record CalibrationExclusionDisplayRow(
    Guid FrameId,
    string Reason,
    Guid ActorPrincipalId,
    Guid InteractiveSessionId,
    DateTimeOffset RecordedAtUtc);

public sealed record CalibrationMetricDisplayRow(string Key, double Value, string? Unit);
