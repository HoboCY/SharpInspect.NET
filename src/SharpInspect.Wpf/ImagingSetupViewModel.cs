using System.Collections.ObjectModel;
using System.Globalization;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Presentation state for the bounded physical imaging setup declaration
/// workflow.  The page can read only the authorized camera binding and the
/// immutable imaging revision ledger; it has no provider, device, calibration
/// publication, or production control capability.
/// </summary>
public sealed class ImagingSetupViewModel : ObservableObject, IAsyncDisposable
{
    private enum OperationKind
    {
        Refresh,
        History,
        Declare
    }

    private readonly record struct OperationStart(
        long Version,
        OperationKind Kind,
        CancellationTokenSource Cancellation,
        InteractiveSession Session,
        string LogicalCameraRole);

    /// <summary>
    /// All values in this record are captured before Step-Up.  The immutable
    /// request is rebuilt with the returned grant, but the captured values are
    /// compared again before the Runtime call so edits made while waiting can
    /// never change the authorized intent.
    /// </summary>
    private sealed record FrozenDeclaration(
        Guid OperationId,
        InteractiveSession Session,
        string LogicalCameraRole,
        CameraBindingRevision Binding,
        long ExpectedImagingRevision,
        string? ExpectedImagingRevisionHash,
        ImagingSetupDefinition Definition,
        string ChangeReason,
        string LensIdentity,
        string FocusOrFocalLengthState,
        string MountingPose,
        string WorkingDistanceMmText,
        string SensorOrientation);

    private static readonly ReadOnlyCollection<int> HistoryPageSizesValue =
        new(new[] { 50 });

    private const AuditedCommandKind ImagingSetupCommandKind =
        AuditedCommandKind.DeclareImagingSetup;

    private readonly IImagingSetupRuntime? _runtime;
    private readonly ICameraSetupRuntime? _cameraRuntime;
    private readonly IInteractiveSessionService? _sessions;
    private readonly IStepUpAuthentication? _stepUp;
    private readonly IUiDispatcher _dispatcher;
    private readonly object _sync = new();
    private readonly ObservableCollection<ImagingSetupRevision> _history = new();
    private readonly ReadOnlyObservableCollection<ImagingSetupRevision> _readOnlyHistory;
    private CancellationTokenSource? _activeCancellation;
    private long _operationVersion;
    private InteractiveSession _session;
    private CameraBindingRevision? _currentBinding;
    private ImagingSetupRevision? _currentRevision;
    private ImagingSetupChangeResult? _lastChangeResult;
    private StepUpBinding? _lastStepUpBinding;
    private long _expectedBindingRevision;
    private string? _expectedBindingRevisionHash;
    private long _expectedImagingRevision;
    private string? _expectedImagingRevisionHash;
    private long? _historyThroughPosition;
    private long? _historyNextAfterPosition;
    private int _historyPage;
    private bool _historyAvailable;
    private string _historyStatusMessage = "尚未读取成像修订历史。";
    private string? _historyErrorCode;
    private bool _imagingStateAvailable;
    private string _logicalCameraRole = "Primary";
    private string _lensIdentity = string.Empty;
    private string _focusOrFocalLengthState = string.Empty;
    private string _mountingPose = string.Empty;
    private string _workingDistanceMmText = string.Empty;
    private string _sensorOrientation = string.Empty;
    private string _changeReason = string.Empty;
    private string _statusMessage;
    private string? _errorCode;
    private string? _inputErrorCode;
    private bool _isBusy;
    private bool _disposed;

    public ImagingSetupViewModel(IImagingSetupRuntime? runtime,
        ICameraSetupRuntime? cameraRuntime,
        IInteractiveSessionService? sessions,
        IStepUpAuthentication? stepUp = null,
        IUiDispatcher? dispatcher = null)
    {
        _runtime = runtime;
        _cameraRuntime = cameraRuntime;
        _sessions = sessions;
        _stepUp = stepUp;
        _dispatcher = dispatcher ?? new DispatcherUiDispatcher();
        _readOnlyHistory = new ReadOnlyObservableCollection<ImagingSetupRevision>(_history);
        _session = sessions?.Current ?? UnauthenticatedSession;
        _statusMessage = IsConfigured
            ? "请先登录，再刷新当前绑定和成像修订。页面不会自动读取或提交物理事实。"
            : "成像设置不可用：未配置成像修订和相机绑定服务。";
        _errorCode = IsConfigured ? null : "ImagingSetupRuntimeUnavailable";

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => CanRefresh);
        RefreshHistoryCommand = new AsyncRelayCommand(() => RefreshHistoryAsync(), () => CanRefreshHistory);
        NextHistoryCommand = new AsyncRelayCommand(() => NextHistoryAsync(), () => CanNextHistory);
        DeclareCommand = new AsyncRelayCommand(() => DeclareAsync(string.Empty), () => CanDeclare);

        if (_sessions is not null)
            _sessions.Changed += SessionChanged;
    }

    /// <summary>Convenience overload matching the other WPF adapters.</summary>
    public ImagingSetupViewModel(IImagingSetupRuntime? runtime,
        ICameraSetupRuntime? cameraRuntime,
        IInteractiveSessionService? sessions,
        IUiDispatcher dispatcher,
        IStepUpAuthentication? stepUp = null)
        : this(runtime, cameraRuntime, sessions, stepUp, dispatcher) { }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RefreshHistoryCommand { get; }
    public AsyncRelayCommand NextHistoryCommand { get; }
    public AsyncRelayCommand DeclareCommand { get; }

    // Stable aliases keep the public page vocabulary close to the contract.
    public AsyncRelayCommand DeclareImagingSetupCommand => DeclareCommand;
    public AsyncRelayCommand CreateRevisionCommand => DeclareCommand;

    /// <summary>
    /// Raised when session authority is lost.  The panel uses this event to
    /// clear its native PasswordBox; the VM never stores the password.
    /// </summary>
    internal event EventHandler? SessionInvalidated;

    public bool IsConfigured => _runtime is not null && _cameraRuntime is not null;

    public bool IsBusy
    {
        get { lock (_sync) return _isBusy; }
    }

    public InteractiveSession CurrentSession => _sessions?.Current ?? ReadSession();

    public bool IsAuthenticated => IsUsableSession(CurrentSession);

    public bool HasStepUpService => _stepUp is not null;

    public bool IsUnavailable => !IsConfigured || !IsAuthenticated || !HasCurrentBinding;

    /// <summary>The role is entered explicitly; changing it invalidates old read-back.</summary>
    public string LogicalCameraRole
    {
        get { lock (_sync) return _logicalCameraRole; }
        set
        {
            var normalized = value ?? string.Empty;
            var changed = false;
            lock (_sync)
            {
                if (!IsSafeIdentifier(normalized))
                {
                    _logicalCameraRole = normalized;
                    SetInputErrorLocked("ImagingSetupLogicalRoleInvalid");
                    changed = true;
                }
                else if (!string.Equals(_logicalCameraRole, normalized, StringComparison.Ordinal))
                {
                    _logicalCameraRole = normalized;
                    ClearReadBackLocked(clearHistory: true);
                    _errorCode = null;
                    _inputErrorCode = null;
                    _statusMessage = "逻辑相机角色已改变，请刷新该角色的当前绑定和成像修订。";
                    changed = true;
                }
            }

            if (changed)
            {
                OnPropertyChanged();
                NotifyStateChangedOnUi();
            }
        }
    }

    public string LensIdentity
    {
        get { lock (_sync) return _lensIdentity; }
        set => SetBoundedText(ref _lensIdentity, value, nameof(LensIdentity), 128,
            "ImagingSetupDescriptionInvalid");
    }

    public string FocusOrFocalLengthState
    {
        get { lock (_sync) return _focusOrFocalLengthState; }
        set => SetBoundedText(ref _focusOrFocalLengthState, value,
            nameof(FocusOrFocalLengthState), 128, "ImagingSetupDescriptionInvalid");
    }

    public string MountingPose
    {
        get { lock (_sync) return _mountingPose; }
        set => SetBoundedText(ref _mountingPose, value, nameof(MountingPose), 128,
            "ImagingSetupDescriptionInvalid");
    }

    public string WorkingDistanceMmText
    {
        get { lock (_sync) return _workingDistanceMmText; }
        set => SetBoundedText(ref _workingDistanceMmText, value,
            nameof(WorkingDistanceMmText), 64, "ImagingSetupDistanceInvalid");
    }

    public string SensorOrientation
    {
        get { lock (_sync) return _sensorOrientation; }
        set => SetBoundedText(ref _sensorOrientation, value,
            nameof(SensorOrientation), 128, "ImagingSetupDescriptionInvalid");
    }

    public string ChangeReason
    {
        get { lock (_sync) return _changeReason; }
        set => SetBoundedText(ref _changeReason, value, nameof(ChangeReason), 512,
            "ImagingSetupChangeReasonInvalid");
    }

    // Short aliases are useful to consumers that use the contract's wording.
    public string FocusState
    {
        get => FocusOrFocalLengthState;
        set => FocusOrFocalLengthState = value;
    }

    public string WorkingDistanceText
    {
        get => WorkingDistanceMmText;
        set => WorkingDistanceMmText = value;
    }

    public CameraBindingRevision? CurrentBinding
    {
        get { lock (_sync) return _currentBinding; }
    }

    public ImagingSetupRevision? CurrentRevision
    {
        get { lock (_sync) return _currentRevision; }
    }

    public ImagingSetupRevision? CurrentImagingSetupRevision => CurrentRevision;

    public bool HasCurrentBinding => CurrentBinding is not null;
    public bool HasBinding => HasCurrentBinding;
    public bool HasCurrentRevision => CurrentRevision is not null;
    public bool HasImagingSetupRevision => HasCurrentRevision;

    public long ExpectedBindingRevision
    {
        get { lock (_sync) return _expectedBindingRevision; }
    }

    public string? ExpectedBindingRevisionHash
    {
        get { lock (_sync) return _expectedBindingRevisionHash; }
    }

    public long ExpectedImagingRevision
    {
        get { lock (_sync) return _expectedImagingRevision; }
    }

    public string? ExpectedImagingRevisionHash
    {
        get { lock (_sync) return _expectedImagingRevisionHash; }
    }

    public long ExpectedRevision => ExpectedImagingRevision;
    public string? ExpectedRevisionHash => ExpectedImagingRevisionHash;
    public long ActualBindingRevision => ExpectedBindingRevision;
    public string? ActualBindingRevisionHash => ExpectedBindingRevisionHash;
    public long CurrentRevisionNumber => CurrentRevision?.Revision ?? 0;
    public string? CurrentRevisionHash => CurrentRevision?.RevisionHash;

    public ReadOnlyObservableCollection<ImagingSetupRevision> History => _readOnlyHistory;
    public ReadOnlyObservableCollection<ImagingSetupRevision> HistoryRevisions => _readOnlyHistory;
    public bool HistoryAvailable
    {
        get { lock (_sync) return _historyAvailable; }
    }

    public long? HistoryThroughPosition
    {
        get { lock (_sync) return _historyThroughPosition; }
    }

    public long? HistoryNextAfterPosition
    {
        get { lock (_sync) return _historyNextAfterPosition; }
    }

    public int HistoryPage
    {
        get { lock (_sync) return _historyPage; }
    }

    public IReadOnlyList<int> HistoryPageSizes => HistoryPageSizesValue;

    public string HistoryStatusMessage
    {
        get { lock (_sync) return _historyStatusMessage; }
    }

    public string? HistoryErrorCode
    {
        get { lock (_sync) return _historyErrorCode; }
    }

    public ImagingSetupChangeResult? LastChangeResult
    {
        get { lock (_sync) return _lastChangeResult; }
    }

    public ImagingSetupChangeResult? LastDeclareResult => LastChangeResult;
    public ImagingSetupChangeResult? LastOperationResult => LastChangeResult;
    public ImagingSetupChangeResult? ChangeResult => LastChangeResult;

    internal StepUpBinding? LastStepUpBinding
    {
        get { lock (_sync) return _lastStepUpBinding; }
    }

    public string? LastReasonCode => LastChangeResult is { } result
        ? SafeReason(result.ReasonCode, "ImagingSetupChangeResultUnknown")
        : ErrorCode;

    public string CurrentRevisionReason => CurrentRevision?.ChangeReason ?? "尚未登记成像修订。";
    public string CurrentChangeReason => CurrentRevisionReason;

    public string BindingSummary
    {
        get
        {
            var binding = CurrentBinding;
            return binding is null
                ? "当前未读取到相机绑定。"
                : $"{binding.Target.Provider.Id}/{binding.Target.StableDeviceIdentity} · binding revision {binding.Revision} · hash {binding.RevisionHash}";
        }
    }

    public string CurrentRevisionSummary
    {
        get
        {
            var revision = CurrentRevision;
            return revision is null
                ? _imagingStateAvailable
                    ? "当前尚未登记成像修订，可登记首个修订。"
                    : "当前成像修订不可用。"
                : $"revision {revision.Revision} · hash {revision.RevisionHash} · {revision.ChangeReason}";
        }
    }

    public string PhysicalDefinitionSummary
    {
        get
        {
            var revision = CurrentRevision;
            if (revision is null) return "尚未读取成像物理定义。";
            var definition = revision.Definition;
            return $"镜头={definition.LensIdentity}；焦距/调焦={definition.FocusOrFocalLengthState}；" +
                $"支架={definition.MountingPose}；工作距离={definition.WorkingDistanceMm.ToString("R", CultureInfo.InvariantCulture)} mm；" +
                $"传感器方向={definition.SensorOrientation}";
        }
    }

    public string CalibrationRequirementStatus =>
        "标定需求由配方及 Runtime 精确检查；本页只登记成像修订，不选择、发布或伪造 Calibration Profile。";

    public string NoAutomaticPhysicalDetectionNotice =>
        "软件不会声称自动检测全部物理变化；镜头、焦距/调焦、支架、工作距离或传感器方向变化必须由授权人员明确登记。";

    public bool AutomaticallyDetectsAllPhysicalChanges => false;
    public bool CanPublishCalibrationProfile => false;
    public bool CanPublishProfile => false;
    public bool ProductionReady => false;
    public bool Ready => false;
    public bool RequiresRecipeActivation => true;

    public string AuditStatus => LastChangeResult?.AuditPersistence.ToString() ?? "NotAttempted";

    public string StatusMessage
    {
        get { lock (_sync) return _statusMessage; }
        private set
        {
            lock (_sync) _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public string? ErrorCode
    {
        get { lock (_sync) return _errorCode; }
        private set
        {
            lock (_sync) _errorCode = value;
            OnPropertyChanged();
        }
    }

    public string ValidationSummary => _inputErrorCode is null
        ? "请明确填写镜头、焦距/调焦、支架、工作距离和传感器方向；工作距离必须是有限正数。"
        : "当前输入无效，请修正标出的字段后重试。";

    public bool CanRefresh => IsConfigured && IsAuthenticated && !IsBusy && !_disposed;

    public bool CanRefreshHistory => IsConfigured && IsAuthenticated && !IsBusy && !_disposed;

    public bool CanNextHistory => IsConfigured && IsAuthenticated && !IsBusy && !_disposed &&
        HistoryNextAfterPosition.HasValue;

    public bool CanDeclare => IsConfigured && IsAuthenticated && _sessions is not null &&
        _stepUp is not null && !IsBusy && !_disposed && _imagingStateAvailable &&
        HasCurrentBinding && IsInputUsable() &&
        TryBuildDefinition(out _, out _, reportError: false) &&
        !string.IsNullOrWhiteSpace(ChangeReason);

    /// <summary>
    /// Reads the authorized camera binding and imaging revision, then reads a
    /// bounded history page under the same authenticated session.  No page
    /// constructor or DataContext assignment calls this method automatically.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            ApplyUnavailable("ImagingSetupRuntimeUnavailable",
                "成像设置不可用：未配置成像修订或相机绑定服务。", clearReadBack: true);
            return;
        }

        var start = Begin(OperationKind.Refresh, cancellationToken);
        if (!start.HasValue) return;

        try
        {
            if (!IsUsableSession(start.Value.Session))
            {
                ApplyFailure(start.Value, "ImagingSetupAuthenticationRequired",
                    "请先登录，再读取当前相机绑定和成像修订。", clearReadBack: true);
                return;
            }

            CameraSetupQueryResult cameraResult;
            try
            {
                cameraResult = await _cameraRuntime!.GetSetupAsync(
                    start.Value.LogicalCameraRole,
                    CreateInvocation(start.Value.Session),
                    start.Value.Cancellation.Token).ConfigureAwait(true);
                start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
            {
                ApplyCancellation(start.Value);
                return;
            }
            catch
            {
                ApplyFailure(start.Value, "ImagingSetupCameraQueryFailed",
                    "当前相机绑定读取失败，请重试。", clearReadBack: true);
                return;
            }

            var cameraSnapshot = cameraResult.Snapshot;
            if (!cameraResult.Available || cameraSnapshot is null ||
                !string.Equals(cameraSnapshot.LogicalRole, start.Value.LogicalCameraRole,
                    StringComparison.Ordinal) ||
                cameraSnapshot.Binding is null)
            {
                // The imaging ledger is keyed by the exact camera binding.
                // Do not query or display a revision when that binding was not
                // authorized and read back successfully for this role.
                await _dispatcher.InvokeAsync(() => ApplyRefreshResult(
                    start.Value, cameraResult,
                    new ImagingSetupQueryResult(false, "ImagingSetupBindingRequired"),
                    historyResult: null)).ConfigureAwait(true);
                return;
            }

            ImagingSetupQueryResult imagingResult;
            try
            {
                imagingResult = await _runtime!.GetImagingSetupAsync(
                    start.Value.LogicalCameraRole,
                    CreateInvocation(start.Value.Session),
                    start.Value.Cancellation.Token).ConfigureAwait(true);
                start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
            {
                ApplyCancellation(start.Value);
                return;
            }
            catch
            {
                ApplyFailure(start.Value, "ImagingSetupQueryFailed",
                    "当前成像修订读取失败，请重试。", clearReadBack: true);
                return;
            }

            ImagingSetupHistoryResult? historyResult = null;
            var snapshot = cameraResult.Snapshot;
            if (cameraResult.Available && snapshot is not null &&
                string.Equals(snapshot.LogicalRole, start.Value.LogicalCameraRole, StringComparison.Ordinal) &&
                snapshot.Binding is not null)
            {
                try
                {
                    historyResult = await _runtime.QueryImagingSetupHistoryAsync(
                        start.Value.LogicalCameraRole,
                        CreateInvocation(start.Value.Session),
                        afterPosition: 0,
                        throughPosition: null,
                        pageSize: 50,
                        cancellationToken: start.Value.Cancellation.Token).ConfigureAwait(true);
                    start.Value.Cancellation.Token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
                {
                    ApplyCancellation(start.Value);
                    return;
                }
                catch
                {
                    // Current read-back remains useful.  History has its own
                    // explicit unavailable state and never grants authority.
                    historyResult = null;
                }
            }

            await _dispatcher.InvokeAsync(() => ApplyRefreshResult(
                start.Value, cameraResult, imagingResult, historyResult)).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            ApplyCancellation(start.Value);
        }
        catch
        {
            ApplyFailure(start.Value, "ImagingSetupQueryFailed",
                "当前成像设置读取失败，请重试。", clearReadBack: true);
        }
        finally
        {
            Complete(start.Value);
        }
    }

    public Task GetImagingSetupAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(cancellationToken);

    /// <summary>Refreshes only the first bounded history page.</summary>
    public async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            ApplyUnavailable("ImagingSetupRuntimeUnavailable",
                "成像修订历史不可用：未配置成像修订服务。", clearReadBack: false);
            return;
        }

        var start = Begin(OperationKind.History, cancellationToken);
        if (!start.HasValue) return;

        try
        {
            if (!IsUsableSession(start.Value.Session))
            {
                ApplyFailure(start.Value, "ImagingSetupAuthenticationRequired",
                    "请先登录，再读取成像修订历史。", clearReadBack: false);
                return;
            }

            ImagingSetupHistoryResult result;
            try
            {
                result = await _runtime!.QueryImagingSetupHistoryAsync(
                    start.Value.LogicalCameraRole,
                    CreateInvocation(start.Value.Session),
                    afterPosition: 0,
                    throughPosition: null,
                    pageSize: 50,
                    cancellationToken: start.Value.Cancellation.Token).ConfigureAwait(true);
                start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
            {
                ApplyCancellation(start.Value);
                return;
            }
            catch
            {
                ApplyHistoryFailure(start.Value, "ImagingSetupHistoryQueryFailed",
                    "成像修订历史读取失败，请重试。", clearHistory: true);
                return;
            }

            await _dispatcher.InvokeAsync(() => ApplyHistoryResult(
                start.Value, result, reset: true, expectedAfterPosition: 0,
                expectedThroughPosition: null)).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            ApplyCancellation(start.Value);
        }
        catch
        {
            ApplyHistoryFailure(start.Value, "ImagingSetupHistoryQueryFailed",
                "成像修订历史读取失败，请重试。", clearHistory: true);
        }
        finally
        {
            Complete(start.Value);
        }
    }

    public Task QueryHistoryAsync(CancellationToken cancellationToken = default) =>
        RefreshHistoryAsync(cancellationToken);

    /// <summary>Loads the next page against the first page's fixed upper position.</summary>
    public async Task NextHistoryAsync(CancellationToken cancellationToken = default)
    {
        long after;
        long through;
        lock (_sync)
        {
            if (!_historyNextAfterPosition.HasValue || !_historyThroughPosition.HasValue)
                return;
            after = _historyNextAfterPosition.Value;
            through = _historyThroughPosition.Value;
        }

        if (!IsConfigured)
        {
            ApplyUnavailable("ImagingSetupRuntimeUnavailable",
                "成像修订历史不可用：未配置成像修订服务。", clearReadBack: false);
            return;
        }

        var start = Begin(OperationKind.History, cancellationToken);
        if (!start.HasValue) return;

        try
        {
            if (!IsUsableSession(start.Value.Session))
            {
                ApplyFailure(start.Value, "ImagingSetupAuthenticationRequired",
                    "请先登录，再读取成像修订历史。", clearReadBack: false);
                return;
            }

            ImagingSetupHistoryResult result;
            try
            {
                result = await _runtime!.QueryImagingSetupHistoryAsync(
                    start.Value.LogicalCameraRole,
                    CreateInvocation(start.Value.Session),
                    afterPosition: after,
                    throughPosition: through,
                    pageSize: 50,
                    cancellationToken: start.Value.Cancellation.Token).ConfigureAwait(true);
                start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
            {
                ApplyCancellation(start.Value);
                return;
            }
            catch
            {
                ApplyHistoryFailure(start.Value, "ImagingSetupHistoryQueryFailed",
                    "下一页成像修订历史读取失败，请重试。", clearHistory: false);
                return;
            }

            await _dispatcher.InvokeAsync(() => ApplyHistoryResult(
                start.Value, result, reset: false, expectedAfterPosition: after,
                expectedThroughPosition: through)).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            ApplyCancellation(start.Value);
        }
        catch
        {
            ApplyHistoryFailure(start.Value, "ImagingSetupHistoryQueryFailed",
                "下一页成像修订历史读取失败，请重试。", clearHistory: false);
        }
        finally
        {
            Complete(start.Value);
        }
    }

    /// <summary>
    /// Declares one immutable physical imaging revision after a fresh
    /// current-password Step-Up.  The password is held only by this call.
    /// </summary>
    public async Task<ImagingSetupChangeResult?> DeclareAsync(
        string? stepUpPassword, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || _sessions is null || _stepUp is null)
        {
            ApplyUnavailable("ImagingSetupOperationUnavailable",
                "成像修订登记不可用：未配置会话、Step-Up 或 Runtime 服务。", clearReadBack: false);
            return null;
        }

        if (string.IsNullOrWhiteSpace(stepUpPassword))
        {
            ApplyInputFailure("ImagingSetupStepUpPasswordRequired",
                "登记成像修订必须输入当前密码再次确认。", clearReadBack: false);
            return null;
        }

        InteractiveSession session;
        string role;
        CameraBindingRevision? binding;
        long expectedImagingRevision;
        string? expectedImagingRevisionHash;
        string reason;
        lock (_sync)
        {
            session = _sessions.Current;
            role = _logicalCameraRole;
            binding = _currentBinding;
            expectedImagingRevision = _expectedImagingRevision;
            expectedImagingRevisionHash = _expectedImagingRevisionHash;
            reason = _changeReason;
        }

        if (!IsUsableSession(session))
        {
            ApplyUnavailable("ImagingSetupAuthenticationRequired",
                "请先登录，再登记成像修订。", clearReadBack: false);
            return null;
        }

        if (binding is null || !string.Equals(binding.LogicalRole, role, StringComparison.Ordinal) ||
            binding.Revision < 1 || !IsUpperSha256(binding.RevisionHash))
        {
            ApplyInputFailure("ImagingSetupBindingRequired",
                "请先刷新并确认当前逻辑角色的实际相机绑定。", clearReadBack: false);
            return null;
        }

        if (!_imagingStateAvailable)
        {
            ApplyInputFailure("ImagingSetupCurrentStateRequired",
                "当前成像修订状态尚未成功读取，请先刷新。", clearReadBack: false);
            return null;
        }

        if (!TryBuildDefinition(out var definition, out _, reportError: true))
            return null;

        if (string.IsNullOrWhiteSpace(reason))
        {
            ApplyInputFailure("ImagingSetupChangeReasonRequired",
                "请明确填写本次物理成像变化的原因。", clearReadBack: false);
            return null;
        }

        var operationId = Guid.NewGuid();
        ImagingSetupChangeRequest initialRequest;
        try
        {
            // The first request has no grant only because its hash is used to
            // bind the exact intent to Step-Up.  The final request below has
            // the same content plus the returned grant in its invocation.
            initialRequest = new ImagingSetupChangeRequest(
                operationId,
                CreateInvocation(session),
                role,
                binding.Revision,
                binding.RevisionHash,
                expectedImagingRevision,
                expectedImagingRevisionHash,
                definition!,
                reason);
        }
        catch (ArgumentException)
        {
            ApplyInputFailure("ImagingSetupDefinitionInvalid",
                "成像物理定义、版本条件或变更原因无效。", clearReadBack: false);
            return null;
        }

        var frozen = new FrozenDeclaration(
            operationId,
            session,
            role,
            binding,
            expectedImagingRevision,
            expectedImagingRevisionHash,
            definition!,
            reason,
            LensIdentity,
            FocusOrFocalLengthState,
            MountingPose,
            WorkingDistanceMmText,
            SensorOrientation);

        var start = Begin(OperationKind.Declare, cancellationToken);
        if (!start.HasValue) return null;

        try
        {
            var bindingForStepUp = new StepUpBinding(
                Permission.ManageCameraBindings,
                operationId,
                initialRequest.AuthorizationTarget,
                ImagingSetupCommandKind);
            lock (_sync) _lastStepUpBinding = bindingForStepUp;
            NotifyStateChangedOnUi();

            StepUpResult stepUpResult;
            try
            {
                stepUpResult = await _stepUp.ReauthenticateAsync(
                    new StepUpRequest(operationId, CreateInvocation(session),
                        bindingForStepUp, stepUpPassword),
                    start.Value.Cancellation.Token).ConfigureAwait(true);
            }
            finally
            {
                // The supplied secret is never assigned to a VM field.
                stepUpPassword = string.Empty;
            }

            start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            if (!stepUpResult.Succeeded || stepUpResult.GrantId is not { } grantId ||
                grantId == Guid.Empty)
            {
                ApplyFailure(start.Value, "StepUpAuthenticationRejected",
                    "当前密码确认未通过；本次成像修订未提交。", clearReadBack: false);
                return null;
            }

            var currentSession = _sessions.Current;
            if (!SameSession(session, currentSession))
            {
                ApplyFailure(start.Value, "ImagingSetupSessionChanged",
                    "会话已变化；本次成像修订未提交，请重新读取并确认。", clearReadBack: true);
                return null;
            }

            ImagingSetupChangeRequest request;
            try
            {
                request = new ImagingSetupChangeRequest(
                    operationId,
                    CreateInvocation(currentSession, grantId),
                    frozen.LogicalCameraRole,
                    frozen.Binding.Revision,
                    frozen.Binding.RevisionHash,
                    frozen.ExpectedImagingRevision,
                    frozen.ExpectedImagingRevisionHash,
                    frozen.Definition,
                    frozen.ChangeReason);
            }
            catch (ArgumentException)
            {
                ApplyFailure(start.Value, "ImagingSetupDefinitionInvalid",
                    "成像修订请求无效；本次操作未提交。", clearReadBack: false);
                return null;
            }

            if (!string.Equals(request.AuthorizationTarget,
                    initialRequest.AuthorizationTarget, StringComparison.Ordinal))
            {
                ApplyFailure(start.Value, "ImagingSetupAuthorizationTargetChanged",
                    "成像修订授权目标发生变化；本次操作未提交。", clearReadBack: false);
                return null;
            }

            ImagingSetupChangeResult result;
            try
            {
                result = await _runtime!.DeclareImagingSetupAsync(
                    request, start.Value.Cancellation.Token).ConfigureAwait(true);
                start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
            {
                ApplyCancellation(start.Value);
                return null;
            }
            catch
            {
                ApplyFailure(start.Value, "ImagingSetupDeclareFailed",
                    "成像修订登记失败，请重新读取后重试。", clearReadBack: false);
                return null;
            }

            await _dispatcher.InvokeAsync(() => ApplyChangeResult(
                start.Value, result, frozen)).ConfigureAwait(true);
            return result;
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            ApplyCancellation(start.Value);
            return null;
        }
        catch
        {
            ApplyFailure(start.Value, "ImagingSetupDeclareFailed",
                "成像修订登记失败，请重新读取后重试。", clearReadBack: false);
            return null;
        }
        finally
        {
            stepUpPassword = string.Empty;
            Complete(start.Value);
        }
    }

    public Task<ImagingSetupChangeResult?> DeclareImagingSetupAsync(
        string? stepUpPassword, CancellationToken cancellationToken = default) =>
        DeclareAsync(stepUpPassword, cancellationToken);

    public Task<ImagingSetupChangeResult?> CreateRevisionAsync(
        string? stepUpPassword, CancellationToken cancellationToken = default) =>
        DeclareAsync(stepUpPassword, cancellationToken);

    /// <summary>Clears read-back and cancels an outstanding operation.</summary>
    public void ClearSensitiveInputs()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            _operationVersion++;
            cancellation = _activeCancellation;
            _activeCancellation = null;
            _isBusy = false;
            ClearReadBackLocked(clearHistory: true);
            _lastChangeResult = null;
            _lastStepUpBinding = null;
            _inputErrorCode = null;
            _errorCode = null;
            _statusMessage = "成像绑定、修订和历史已清除，请重新读取。";
        }

        cancellation?.Cancel();
        NotifySessionInvalidatedOnUi();
        NotifyStateChangedOnUi();
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
                _statusMessage = "成像设置请求已取消，可重新操作。";
                _errorCode = null;
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
            ClearReadBackLocked(clearHistory: true);
            _lastChangeResult = null;
            _lastStepUpBinding = null;
            _inputErrorCode = null;
            _errorCode = null;
            _statusMessage = "成像设置面板已释放。";
        }

        cancellation?.Cancel();
        if (_sessions is not null)
            _sessions.Changed -= SessionChanged;
        NotifySessionInvalidatedOnUi();
        NotifyStateChangedOnUi();
        await ValueTask.CompletedTask;
    }

    private OperationStart? Begin(OperationKind kind, CancellationToken cancellationToken)
    {
        OperationStart start;
        lock (_sync)
        {
            if (_disposed || _isBusy) return null;
            var activeSession = _sessions?.Current ?? _session;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCancellation = linked;
            _operationVersion++;
            _isBusy = true;
            _errorCode = null;
            _historyErrorCode = kind == OperationKind.History ? _historyErrorCode : null;
            _statusMessage = kind switch
            {
                OperationKind.Refresh => "正在读取当前相机绑定和成像修订…",
                OperationKind.History => "正在读取成像修订历史…",
                OperationKind.Declare => "正在等待密码确认并登记成像修订…",
                _ => "正在处理成像设置…"
            };
            start = new OperationStart(_operationVersion, kind, linked, activeSession,
                _logicalCameraRole);
        }

        NotifyStateChangedOnUi();
        return start;
    }

    private void Complete(OperationStart start)
    {
        bool changed;
        lock (_sync)
        {
            changed = IsCurrentLocked(start);
            if (changed)
            {
                _activeCancellation = null;
                _isBusy = false;
            }
        }

        start.Cancellation.Dispose();
        if (changed) NotifyStateChangedOnUi();
    }

    private void ApplyRefreshResult(OperationStart start,
        CameraSetupQueryResult cameraResult,
        ImagingSetupQueryResult imagingResult,
        ImagingSetupHistoryResult? historyResult)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start) || !SameSession(start.Session, CurrentSession) ||
                !string.Equals(_logicalCameraRole, start.LogicalCameraRole, StringComparison.Ordinal))
                return;

            var snapshot = cameraResult.Snapshot;
            if (!cameraResult.Available || snapshot is null)
            {
                ClearReadBackLocked(clearHistory: true);
                _errorCode = SafeReason(cameraResult.ReasonCode, "ImagingSetupCameraUnavailable");
                _statusMessage = "当前相机绑定不可用；无法登记成像修订。";
                NotifyStateChangedOnUi();
                return;
            }

            if (!string.Equals(snapshot.LogicalRole, start.LogicalCameraRole,
                    StringComparison.Ordinal))
            {
                ClearReadBackLocked(clearHistory: true);
                _errorCode = "ImagingSetupCameraRoleMismatch";
                _statusMessage = "相机绑定结果与当前逻辑角色不一致，状态已锁定。";
                NotifyStateChangedOnUi();
                return;
            }

            if (snapshot.Binding is null)
            {
                ClearReadBackLocked(clearHistory: true);
                _errorCode = "ImagingSetupBindingRequired";
                _statusMessage = "当前逻辑角色没有可用相机绑定；无法登记成像修订。";
                NotifyStateChangedOnUi();
                return;
            }

            var binding = snapshot.Binding;
            _currentBinding = binding;
            _expectedBindingRevision = binding.Revision;
            _expectedBindingRevisionHash = binding.RevisionHash;

            var missingRevision = imagingResult.Current is null &&
                (imagingResult.Available || IsExpectedMissingRevision(imagingResult.ReasonCode));
            if (!imagingResult.Available && !missingRevision)
            {
                _currentRevision = null;
                _expectedImagingRevision = 0;
                _expectedImagingRevisionHash = null;
                _imagingStateAvailable = false;
                ClearPhysicalDefinitionLocked();
                _errorCode = SafeReason(imagingResult.ReasonCode, "ImagingSetupUnavailable");
                _statusMessage = "当前成像修订不可用；请修正 Runtime 状态后重试。";
            }
            else if (imagingResult.Current is not null &&
                (!string.Equals(imagingResult.Current.LogicalCameraRole,
                    start.LogicalCameraRole, StringComparison.Ordinal) ||
                 !SameBinding(imagingResult.Current.Binding, binding)))
            {
                _currentRevision = null;
                _expectedImagingRevision = 0;
                _expectedImagingRevisionHash = null;
                _imagingStateAvailable = false;
                ClearPhysicalDefinitionLocked();
                _errorCode = "ImagingSetupBindingMismatch";
                _statusMessage = "成像修订与当前相机绑定不一致，状态已锁定。";
            }
            else if (imagingResult.Current is null)
            {
                _currentRevision = null;
                _expectedImagingRevision = 0;
                _expectedImagingRevisionHash = null;
                _imagingStateAvailable = true;
                ClearPhysicalDefinitionLocked();
                _inputErrorCode = null;
                _errorCode = null;
                _statusMessage = "当前绑定已读取，但尚未登记成像修订；可登记首个修订。";
            }
            else
            {
                _currentRevision = imagingResult.Current;
                _expectedImagingRevision = imagingResult.Current.Revision;
                _expectedImagingRevisionHash = imagingResult.Current.RevisionHash;
                _imagingStateAvailable = true;
                LoadPhysicalDefinitionLocked(imagingResult.Current.Definition,
                    imagingResult.Current.ChangeReason);
                _inputErrorCode = null;
                _errorCode = null;
                _statusMessage = BuildRevisionStatus(imagingResult.Current,
                    "当前绑定和成像修订已读取；后续配方激活仍需精确检查标定兼容性。 ");
            }

            if (historyResult is null)
            {
                _history.Clear();
                _historyAvailable = false;
                _historyThroughPosition = null;
                _historyNextAfterPosition = null;
                _historyPage = 0;
                _historyErrorCode = "ImagingSetupHistoryQueryFailed";
                _historyStatusMessage = "当前修订已读取，但历史暂不可用。";
            }
            else
            {
                ApplyHistoryResultLocked(historyResult, reset: true,
                    expectedAfterPosition: 0, expectedThroughPosition: null);
            }
        }

        NotifyStateChangedOnUi();
    }

    private void ApplyHistoryResult(OperationStart start,
        ImagingSetupHistoryResult result,
        bool reset,
        long expectedAfterPosition,
        long? expectedThroughPosition)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start) || !SameSession(start.Session, CurrentSession) ||
                !string.Equals(_logicalCameraRole, start.LogicalCameraRole, StringComparison.Ordinal))
                return;
            ApplyHistoryResultLocked(result, reset, expectedAfterPosition,
                expectedThroughPosition);
        }

        NotifyStateChangedOnUi();
    }

    private void ApplyHistoryResultLocked(ImagingSetupHistoryResult result,
        bool reset, long expectedAfterPosition, long? expectedThroughPosition)
    {
        if (!result.Available)
        {
            if (reset) _history.Clear();
            _historyAvailable = false;
            if (reset)
            {
                _historyThroughPosition = null;
                _historyNextAfterPosition = null;
                _historyPage = 0;
            }
            _historyErrorCode = SafeReason(result.ReasonCode, "ImagingSetupHistoryUnavailable");
            _historyStatusMessage = "成像修订历史不可用；当前修订读回不因此获得额外权限。";
            return;
        }

        var rows = result.Revisions ?? Array.Empty<ImagingSetupRevision>();
        var through = result.ThroughPosition;
        var next = result.NextAfterPosition;
        if (through < 0 || (expectedThroughPosition.HasValue && through != expectedThroughPosition.Value) ||
            (next.HasValue && (next.Value <= expectedAfterPosition || next.Value > through)) ||
            rows.Any(row => row is null ||
                !string.Equals(row.LogicalCameraRole, _logicalCameraRole, StringComparison.Ordinal) ||
                row.Position <= expectedAfterPosition || row.Position > through) ||
            rows.Select(row => row.Position).Distinct().Count() != rows.Count ||
            (!reset && rows.Any(row => _history.Any(existing => existing.Position == row.Position))))
        {
            _historyAvailable = reset ? false : _historyAvailable;
            _historyErrorCode = "ImagingSetupHistoryResultInvalid";
            _historyStatusMessage = "成像修订历史结果无效，页面保留可验证的已有记录。";
            if (reset)
            {
                _history.Clear();
                _historyThroughPosition = null;
                _historyNextAfterPosition = null;
                _historyPage = 0;
            }
            return;
        }

        if (reset)
        {
            _history.Clear();
            _historyPage = 1;
        }

        foreach (var row in rows.OrderBy(row => row.Position))
            _history.Add(row);

        _historyAvailable = true;
        _historyThroughPosition = through;
        _historyNextAfterPosition = next;
        _historyErrorCode = null;
        _historyStatusMessage = rows.Count == 0
            ? "当前没有可显示的成像修订历史。"
            : $"已读取 {_history.Count} 条成像修订历史；记录仅供追溯，不直接授予当前生产权限。";
        if (!reset) _historyPage++;
    }

    private void ApplyChangeResult(OperationStart start,
        ImagingSetupChangeResult result, FrozenDeclaration frozen)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start) || !SameSession(start.Session, CurrentSession) ||
                !string.Equals(_logicalCameraRole, frozen.LogicalCameraRole, StringComparison.Ordinal))
                return;

            _lastChangeResult = result;
            var revision = result.Revision;
            var validRevision = revision is not null &&
                string.Equals(revision.LogicalCameraRole, _logicalCameraRole,
                    StringComparison.Ordinal) && SameBinding(revision.Binding, frozen.Binding);

            if (!result.Succeeded)
            {
                if (validRevision)
                {
                    _currentRevision = revision;
                    _expectedImagingRevision = revision!.Revision;
                    _expectedImagingRevisionHash = revision.RevisionHash;
                    _imagingStateAvailable = true;
                }
                _errorCode = SafeReason(result.ReasonCode, "ImagingSetupChangeRejected");
                _statusMessage = "成像修订登记未完成；请修正条件后重试。";
            }
            else if (!validRevision)
            {
                // A successful write without a role- and binding-matching
                // immutable read-back is never presented as a current fact.
                _currentRevision = null;
                _expectedImagingRevision = 0;
                _expectedImagingRevisionHash = null;
                _imagingStateAvailable = false;
                ClearPhysicalDefinitionLocked();
                _errorCode = "ImagingSetupChangeResultInvalid";
                _statusMessage = "成像修订登记返回的读回无效；请重新读取后重试。";
            }
            else
            {
                _currentRevision = revision;
                _expectedImagingRevision = revision!.Revision;
                _expectedImagingRevisionHash = revision.RevisionHash;
                _imagingStateAvailable = true;
                LoadPhysicalDefinitionLocked(revision.Definition, revision.ChangeReason);
                _errorCode = null;
                _statusMessage = result.AuditPersistence == AuditPersistence.Persisted
                    ? "成像修订已登记并记录审计；相关旧标定需重新检查，生产 Ready 仍不可用。"
                    : "成像修订已返回，但审计状态不可用；生产 Ready 仍保持关闭。";
                AddRevisionToHistoryLocked(revision);
            }
        }

        NotifyStateChangedOnUi();
    }

    private void AddRevisionToHistoryLocked(ImagingSetupRevision revision)
    {
        if (_history.Any(item => item.RevisionHash == revision.RevisionHash)) return;
        var index = _history.TakeWhile(item => item.Position < revision.Position).Count();
        _history.Insert(index, revision);
    }

    private bool TryBuildDefinition(out ImagingSetupDefinition? definition,
        out string reason, bool reportError)
    {
        definition = null;
        reason = "ImagingSetupDefinitionInvalid";
        string lens;
        string focus;
        string mount;
        string distanceText;
        string orientation;
        lock (_sync)
        {
            if (_inputErrorCode is not null)
            {
                reason = _inputErrorCode;
                return false;
            }

            lens = _lensIdentity;
            focus = _focusOrFocalLengthState;
            mount = _mountingPose;
            distanceText = _workingDistanceMmText;
            orientation = _sensorOrientation;
        }

        if (string.IsNullOrWhiteSpace(lens) || string.IsNullOrWhiteSpace(focus) ||
            string.IsNullOrWhiteSpace(mount) || string.IsNullOrWhiteSpace(orientation))
        {
            reason = "ImagingSetupDescriptionRequired";
            if (reportError)
                ApplyInputFailure(reason, "镜头、焦距/调焦、支架和传感器方向均必须填写。",
                    clearReadBack: false);
            return false;
        }

        if (!double.TryParse(distanceText.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var distance) ||
            !double.IsFinite(distance) || distance <= 0 || distance > 1_000_000)
        {
            reason = "ImagingSetupDistanceInvalid";
            if (reportError)
                ApplyInputFailure(reason, "工作距离必须是有限的正数，且不超过公共范围。",
                    clearReadBack: false);
            return false;
        }

        try
        {
            definition = new ImagingSetupDefinition(lens, focus, mount, distance, orientation);
            reason = "ImagingSetupDefinitionBuilt";
            return true;
        }
        catch (ArgumentException)
        {
            reason = "ImagingSetupDefinitionInvalid";
            if (reportError)
                ApplyInputFailure(reason, "成像物理定义超出公共字段范围。", clearReadBack: false);
            definition = null;
            return false;
        }
    }

    private bool IsInputUsable()
    {
        lock (_sync)
        {
            return _inputErrorCode is null && IsSafeIdentifier(_logicalCameraRole);
        }
    }

    private void SetBoundedText(ref string field, string? value,
        string propertyName, int maximumLength, string reason)
    {
        var normalized = value ?? string.Empty;
        lock (_sync)
        {
            if (_disposed) return;
            if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
            {
                field = normalized;
                SetInputErrorLocked(reason);
                OnPropertyChanged(propertyName);
                NotifyStateChangedOnUi();
                return;
            }

            if (string.Equals(field, normalized, StringComparison.Ordinal)) return;
            field = normalized;
            ClearInputErrorLocked();
        }

        OnPropertyChanged(propertyName);
        NotifyStateChangedOnUi();
    }

    private void SetInputErrorLocked(string reason)
    {
        _inputErrorCode = reason;
        _errorCode = reason;
        _statusMessage = "当前输入无效，请修正后重试。";
    }

    private void ClearInputErrorLocked()
    {
        if (_inputErrorCode is not null &&
            string.Equals(_errorCode, _inputErrorCode, StringComparison.Ordinal))
            _errorCode = null;
        _inputErrorCode = null;
    }

    private void ApplyInputFailure(string reason, string status, bool clearReadBack)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _inputErrorCode = SafeReason(reason, "ImagingSetupInputInvalid");
            _errorCode = _inputErrorCode;
            _statusMessage = status;
            if (clearReadBack) ClearReadBackLocked(clearHistory: true);
        }

        NotifyStateChangedOnUi();
    }

    private void ApplyUnavailable(string reason, string status, bool clearReadBack)
    {
        lock (_sync)
        {
            _errorCode = SafeReason(reason, "ImagingSetupUnavailable");
            _statusMessage = status;
            if (clearReadBack) ClearReadBackLocked(clearHistory: true);
        }

        NotifyStateChangedOnUi();
    }

    private void ApplyFailure(OperationStart start, string reason,
        string status, bool clearReadBack)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            if (clearReadBack) ClearReadBackLocked(clearHistory: true);
            _errorCode = SafeReason(reason, "ImagingSetupOperationFailed");
            _statusMessage = status;
        }

        NotifyStateChangedOnUi();
    }

    private void ApplyHistoryFailure(OperationStart start, string reason,
        string status, bool clearHistory)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            if (clearHistory)
            {
                _history.Clear();
                _historyThroughPosition = null;
                _historyNextAfterPosition = null;
                _historyPage = 0;
                _historyAvailable = false;
            }
            _historyErrorCode = SafeReason(reason, "ImagingSetupHistoryFailed");
            _historyStatusMessage = status;
        }

        NotifyStateChangedOnUi();
    }

    private void ApplyCancellation(OperationStart start)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            _errorCode = null;
            _statusMessage = "成像设置请求已取消，可重新操作。";
        }

        NotifyStateChangedOnUi();
    }

    private void SessionChanged(object? sender, InteractiveSessionChangedEventArgs args)
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            _operationVersion++;
            cancellation = _activeCancellation;
            _activeCancellation = null;
            _isBusy = false;
            _session = _sessions?.Current ?? args.Session;
            ClearReadBackLocked(clearHistory: true);
            _lastChangeResult = null;
            _lastStepUpBinding = null;
            _inputErrorCode = null;
            _errorCode = "ImagingSetupSessionChanged";
            _statusMessage = "会话已变化；成像绑定、修订和历史已清除，请重新读取。";
        }

        cancellation?.Cancel();
        NotifySessionInvalidatedOnUi();
        NotifyStateChangedOnUi();
    }

    private void ClearReadBackLocked(bool clearHistory)
    {
        _currentBinding = null;
        _currentRevision = null;
        _expectedBindingRevision = 0;
        _expectedBindingRevisionHash = null;
        _expectedImagingRevision = 0;
        _expectedImagingRevisionHash = null;
        _imagingStateAvailable = false;
        ClearPhysicalDefinitionLocked();
        if (!clearHistory) return;
        _history.Clear();
        _historyAvailable = false;
        _historyThroughPosition = null;
        _historyNextAfterPosition = null;
        _historyPage = 0;
        _historyErrorCode = null;
        _historyStatusMessage = "尚未读取成像修订历史。";
    }

    private void ClearPhysicalDefinitionLocked()
    {
        _lensIdentity = string.Empty;
        _focusOrFocalLengthState = string.Empty;
        _mountingPose = string.Empty;
        _workingDistanceMmText = string.Empty;
        _sensorOrientation = string.Empty;
        _changeReason = string.Empty;
    }

    private void LoadPhysicalDefinitionLocked(ImagingSetupDefinition definition,
        string changeReason)
    {
        _lensIdentity = definition.LensIdentity;
        _focusOrFocalLengthState = definition.FocusOrFocalLengthState;
        _mountingPose = definition.MountingPose;
        _workingDistanceMmText = definition.WorkingDistanceMm.ToString("R",
            CultureInfo.InvariantCulture);
        _sensorOrientation = definition.SensorOrientation;
        _changeReason = changeReason;
        _inputErrorCode = null;
    }

    private bool IsCurrentLocked(OperationStart start) => !_disposed &&
        _operationVersion == start.Version &&
        ReferenceEquals(_activeCancellation, start.Cancellation);

    private InteractiveSession ReadSession()
    {
        lock (_sync) return _session;
    }

    private void NotifyStateChangedOnUi()
    {
        if (_dispatcher.CheckAccess)
        {
            NotifyStateChanged();
            return;
        }

        _ = _dispatcher.InvokeAsync(NotifyStateChanged);
    }

    private void NotifySessionInvalidatedOnUi()
    {
        void Notify() => SessionInvalidated?.Invoke(this, EventArgs.Empty);
        if (_dispatcher.CheckAccess)
        {
            Notify();
            return;
        }

        _ = _dispatcher.InvokeAsync(Notify);
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(string.Empty);
        RefreshCommand.RaiseCanExecuteChanged();
        RefreshHistoryCommand.RaiseCanExecuteChanged();
        NextHistoryCommand.RaiseCanExecuteChanged();
        DeclareCommand.RaiseCanExecuteChanged();
    }

    private string BuildRevisionStatus(ImagingSetupRevision revision, string suffix) =>
        $"当前 binding revision {revision.Binding.Revision}、成像 revision {revision.Revision} 已读取；{suffix}";

    private static bool IsExpectedMissingRevision(string? reason) =>
        string.Equals(reason, "ImagingSetupUnconfigured", StringComparison.Ordinal) ||
        string.Equals(reason, "ImagingSetupRevisionMissing", StringComparison.Ordinal) ||
        string.Equals(reason, "ImagingSetupNotDeclared", StringComparison.Ordinal) ||
        string.Equals(reason, "ImagingSetupEmpty", StringComparison.Ordinal);

    private static bool SameBinding(CameraBindingRevision? left, CameraBindingRevision? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.Revision == right.Revision &&
            string.Equals(left.LogicalRole, right.LogicalRole, StringComparison.Ordinal) &&
            string.Equals(left.RevisionHash, right.RevisionHash, StringComparison.Ordinal) &&
            Equals(left.Target, right.Target);
    }

    private static bool IsUsableSession(InteractiveSession session) =>
        session.State == InteractiveSessionState.Authenticated &&
        session.SessionId is { } id && id != Guid.Empty &&
        !string.IsNullOrWhiteSpace(session.PrincipalId);

    private static bool SameSession(InteractiveSession left, InteractiveSession right) =>
        IsUsableSession(left) && IsUsableSession(right) &&
        left.SessionId == right.SessionId &&
        string.Equals(left.PrincipalId, right.PrincipalId, StringComparison.Ordinal);

    private static CommandInvocation CreateInvocation(InteractiveSession session,
        Guid? grantId = null) =>
        new(CommandSource.PhysicalConsole, session.PrincipalId, session.SessionId, grantId);

    private static bool IsSafeIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
        foreach (var character in value)
        {
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                >= '0' and <= '9' or '.' or '_' or '-')) return false;
        }

        return true;
    }

    private static bool IsUpperSha256(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static string SafeReason(string? reason, string fallback)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 128) return fallback;
        foreach (var character in reason)
        {
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                >= '0' and <= '9' or '.' or '_' or '-' or ':')) return fallback;
        }

        return reason;
    }

    private static InteractiveSession UnauthenticatedSession =>
        new(InteractiveSessionState.Unauthenticated, null, null);
}
