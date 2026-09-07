using System.Collections.ObjectModel;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Bounded presentation over the immutable alarm projection and read-only alarm
/// history. It never changes an alarm optimistically: Runtime outcomes are
/// displayed as admission only and the next fresh station snapshot remains the
/// authority for the visible lifecycle.
/// </summary>
public sealed class AlarmViewModel : ObservableObject, IAsyncDisposable
{
    public const int DefaultHistoryPageSize = 50;
    public const int MaximumHistoryPageSize = 100;
    public const int MaximumHistoryPageStarts = 256;

    private enum HistoryMove
    {
        Refresh,
        Next
    }

    private readonly record struct HistoryStart(
        long RequestVersion,
        long AfterPosition,
        long? ThroughPosition,
        int PageNumber,
        string? Code,
        CancellationTokenSource Cancellation,
        HistoryMove Move);

    private readonly record struct CommandStart(long Version, CancellationTokenSource Cancellation);

    private const string SnapshotStaleReason = "AlarmSnapshotStale";
    private const string SnapshotUnavailableReason = "AlarmSnapshotUnavailable";
    private const string StateUnavailableReason = "AlarmStateUnavailable";
    private const string AuthorizationRequiredReason = "AlarmAuthorizationRequired";
    private const string CommandRejectedReason = "AlarmCommandRejected";
    private const string CommandOutcomeUnknownReason = "AlarmCommandOutcomeUnknown";
    private const string HistoryUnavailableReason = "AlarmHistoryUnavailable";
    private const string HistoryQueryFailedReason = "AlarmHistoryQueryFailed";
    private const string HistoryPageInvalidReason = "AlarmHistoryPageInvalid";
    private const string HistorySnapshotExpiredReason = "AlarmHistorySnapshotExpired";

    private static readonly IReadOnlySet<string> SafeReasonCodes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "AlarmStateAvailable", StateUnavailableReason, SnapshotUnavailableReason,
            SnapshotStaleReason, AuthorizationRequiredReason, "AuthorizationAvailable",
            "AlarmCommandAccepted", CommandRejectedReason, CommandOutcomeUnknownReason,
            "AlarmCommandCancelled", "AlarmCommandSubmitFailed", "AlarmInputInvalid",
            "AlarmInstanceNotFound", "AlarmPermissionDenied", "AlarmSessionChanged",
            "StepUpAccepted", "StepUpAuthenticationRejected", HistoryUnavailableReason,
            HistoryQueryFailedReason, HistoryPageInvalidReason, HistorySnapshotExpiredReason,
            "AlarmHistoryAvailable", "AlarmRuntimeProjectionInvalid", "AlarmResetUnavailable",
            "AlarmAcknowledgeUnavailable", "AlarmCommandBusy",
            "AlarmAuthorityAvailable", "AlarmPolicyNotConfigured", "AlarmPolicyRequiresIdentityAndAudit",
            "AlarmPolicyActivated", "AlarmRaised", "AlarmObserved", "AlarmSourceRecovered", "AlarmCleared",
            "AlarmProjectionChanged", "AlarmAcknowledged", "AlarmResetCompleted", "AlarmAlreadyAcknowledged",
            "AlarmResetNotLatched", "AlarmSourceNotHealthy", "AlarmSourceRuntimeEpochMismatch",
            "AlarmSourceObservationStale", "AlarmResetPrerequisitesUnmet", "AlarmResetPrerequisitesInvalid",
            "AlarmCodeUnmapped", "AlarmSourceMismatch", "AlarmActiveInstanceCapacityExceeded",
            "AlarmObservationOutOfOrder", "AlarmRuntimeEpochMismatch", "AlarmObservationInvalid",
            "AlarmCodeInvalid", "AlarmSourceInvalid", "AlarmPolicyUnavailable", "AlarmAuditUnavailable",
            "AuthenticationRequired", "AuthorizationUnavailable", "PermissionDenied", "SessionInvalid",
            "SessionMismatch", "CredentialUnavailable", "StepUpRequired", "StepUpInvalid",
            "DuplicateCorrelationId", "OperationInProgress", "TraceAuditUnavailable", "RuntimeStopped"
        };

    private readonly StationShellViewModel _station;
    private readonly IStationRuntime? _runtime;
    private readonly IInteractiveSessionService? _sessions;
    private readonly IIdentityAdministrationQuery? _authorizationQuery;
    private readonly IStepUpAuthentication? _stepUp;
    private readonly IAlarmHistoryQuery? _historyQuery;
    private readonly IUiDispatcher _dispatcher;
    private readonly int _historyPageSize;
    private readonly bool _resetRequiresStepUp;
    private readonly bool _acknowledgeRequiresStepUp;
    private readonly object _sync = new();
    private readonly ObservableCollection<AlarmInstanceSnapshot> _instances = new();
    private readonly ReadOnlyObservableCollection<AlarmInstanceSnapshot> _readOnlyInstances;
    private readonly ObservableCollection<AlarmInstanceSnapshot> _visibleInstances = new();
    private readonly ReadOnlyObservableCollection<AlarmInstanceSnapshot> _readOnlyVisibleInstances;
    private readonly ObservableCollection<AlarmHistoryRecord> _historyRows = new();
    private readonly ReadOnlyObservableCollection<AlarmHistoryRecord> _readOnlyHistoryRows;
    private CancellationTokenSource? _activeCommandCancellation;
    private CancellationTokenSource? _activeHistoryCancellation;
    private AlarmStateSnapshot? _alarmState;
    private Guid? _projectionEpoch;
    private long _projectionRevision;
    private bool _projectionAvailable;
    private bool _projectionFresh;
    private HumanAuthorizationSnapshot? _authorization;
    private InteractiveSession _session;
    private AlarmInstanceSnapshot? _selectedAlarm;
    private RuntimeCommandOutcome? _lastCommandOutcome;
    private StepUpBinding? _lastStepUpBinding;
    private string _codeFilterText = string.Empty;
    private string _historyCodeText = string.Empty;
    private string _statusMessage;
    private string? _errorCode;
    private string _historyStatusMessage;
    private string? _historyErrorCode;
    private bool _isCommandBusy;
    private bool _isHistoryBusy;
    private long _commandVersion;
    private long _historyRequestVersion;
    private long? _historyThroughPosition;
    private long? _historyNextAfterPosition;
    private int _historyPage;
    private bool _disposed;

    public AlarmViewModel(
        StationShellViewModel station,
        IStationRuntime? runtime,
        IInteractiveSessionService? sessions,
        IIdentityAdministrationQuery? authorizationQuery,
        IStepUpAuthentication? stepUpAuthentication,
        IAlarmHistoryQuery? historyQuery,
        IUiDispatcher? dispatcher = null,
        int historyPageSize = DefaultHistoryPageSize,
        bool resetRequiresStepUp = true,
        bool acknowledgeRequiresStepUp = false)
    {
        _station = station ?? throw new ArgumentNullException(nameof(station));
        if (historyPageSize is < 1 or > MaximumHistoryPageSize)
            throw new ArgumentOutOfRangeException(nameof(historyPageSize));

        _runtime = runtime;
        _sessions = sessions;
        _authorizationQuery = authorizationQuery;
        _stepUp = stepUpAuthentication;
        _historyQuery = historyQuery;
        _dispatcher = dispatcher ?? new DispatcherUiDispatcher();
        _historyPageSize = historyPageSize;
        _resetRequiresStepUp = resetRequiresStepUp;
        _acknowledgeRequiresStepUp = acknowledgeRequiresStepUp;
        _readOnlyInstances = new ReadOnlyObservableCollection<AlarmInstanceSnapshot>(_instances);
        _readOnlyVisibleInstances = new ReadOnlyObservableCollection<AlarmInstanceSnapshot>(_visibleInstances);
        _readOnlyHistoryRows = new ReadOnlyObservableCollection<AlarmHistoryRecord>(_historyRows);
        _session = sessions?.Current ?? UnauthenticatedSession;
        _statusMessage = "尚未读取报警状态。";
        _historyStatusMessage = historyQuery is null
            ? "报警历史不可用：未配置只读查询服务。"
            : "尚未查询报警历史。";

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => CanRefresh);
        AcknowledgeCommand = new AsyncRelayCommand(AcknowledgeSelectedAsync, () => CanAcknowledge);
        ResetCommand = new AsyncRelayCommand(() => ResetSelectedAsync(string.Empty), () => CanReset);
        RefreshHistoryCommand = new AsyncRelayCommand(() => RefreshHistoryAsync(), () => CanRefreshHistory);
        NextHistoryPageCommand = new AsyncRelayCommand(() => NextHistoryPageAsync(), () => CanNextHistoryPage);

        _station.PropertyChanged += StationChanged;
        _station.State.PropertyChanged += StationStateChanged;
        if (_sessions is not null) _sessions.Changed += SessionChanged;
        ApplyStationProjection();
    }

    public ReadOnlyObservableCollection<AlarmInstanceSnapshot> Instances => _readOnlyInstances;
    public ReadOnlyObservableCollection<AlarmInstanceSnapshot> VisibleInstances => _readOnlyVisibleInstances;
    public ReadOnlyObservableCollection<AlarmHistoryRecord> HistoryRows => _readOnlyHistoryRows;

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand AcknowledgeCommand { get; }
    public AsyncRelayCommand ResetCommand { get; }
    public AsyncRelayCommand RefreshHistoryCommand { get; }
    public AsyncRelayCommand NextHistoryPageCommand { get; }

    public bool IsConfigured => _runtime is not null && _sessions is not null && _authorizationQuery is not null;
    public bool IsHistoryConfigured => _historyQuery is not null;

    public bool IsSnapshotFresh
    {
        get
        {
            lock (_sync) return IsSnapshotFreshLocked();
        }
    }

    public bool IsSnapshotUnavailable => !IsSnapshotFresh;

    public AlarmStateSnapshot? CurrentAlarmState
    {
        get { lock (_sync) return _alarmState; }
    }

    public AlarmInstanceSnapshot? SelectedAlarm
    {
        get { lock (_sync) return _selectedAlarm; }
        set
        {
            AlarmInstanceSnapshot? selected;
            lock (_sync)
            {
                selected = value is null
                    ? null
                    : _instances.FirstOrDefault(item => item.InstanceId == value.InstanceId);
                if (ReferenceEquals(_selectedAlarm, selected) ||
                    (_selectedAlarm?.InstanceId == selected?.InstanceId)) return;
                _selectedAlarm = selected;
            }
            NotifyCommandAvailability();
            OnPropertyChanged();
        }
    }

    public string CodeFilterText
    {
        get { lock (_sync) return _codeFilterText; }
        set
        {
            value ??= string.Empty;
            lock (_sync)
            {
                if (string.Equals(_codeFilterText, value, StringComparison.Ordinal)) return;
                _codeFilterText = value;
                RebuildVisibleLocked();
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(VisibleInstances));
            OnPropertyChanged(nameof(VisibleAlarmCount));
        }
    }

    public string HistoryCodeText
    {
        get { lock (_sync) return _historyCodeText; }
        set
        {
            value ??= string.Empty;
            CancellationTokenSource? cancellation;
            lock (_sync)
            {
                if (string.Equals(_historyCodeText, value, StringComparison.Ordinal)) return;
                _historyCodeText = value;
                _historyRequestVersion++;
                cancellation = _activeHistoryCancellation;
                _activeHistoryCancellation = null;
                _isHistoryBusy = false;
                _historyNextAfterPosition = null;
                _historyThroughPosition = null;
                _historyPage = 0;
                _historyRows.Clear();
                _historyErrorCode = null;
                _historyStatusMessage = "历史筛选已修改，请点击刷新。";
            }
            cancellation?.Cancel();
            OnPropertyChanged();
            NotifyHistoryChanged();
        }
    }

    public int TotalAlarmCount
    {
        get { lock (_sync) return _instances.Count; }
    }

    public int VisibleAlarmCount
    {
        get { lock (_sync) return _visibleInstances.Count; }
    }

    public int ActiveAlarmCount
    {
        get { lock (_sync) return _instances.Count(item => item.Lifecycle == AlarmLifecycle.Active); }
    }

    public int LatchedAlarmCount
    {
        get { lock (_sync) return _instances.Count(item => item.IsLatched && item.Lifecycle != AlarmLifecycle.Cleared); }
    }

    public int PlcShownCount
    {
        get { lock (_sync) return _alarmState?.Plc.Entries.Count ?? 0; }
    }

    public int PlcHiddenCount
    {
        get { lock (_sync) return _alarmState?.Plc.HiddenCount ?? 0; }
    }

    public int PlcTotalUncleared
    {
        get { lock (_sync) return _alarmState?.Plc.TotalUncleared ?? 0; }
    }

    public int PlcBlockingCount
    {
        get { lock (_sync) return _alarmState?.Plc.BlockingCount ?? 0; }
    }

    public bool PlcFaultAbortPresent
    {
        get { lock (_sync) return _alarmState?.Plc.FaultAbortPresent == true; }
    }

    public bool IsBusy
    {
        get { lock (_sync) return _isCommandBusy; }
    }

    public bool IsHistoryBusy
    {
        get { lock (_sync) return _isHistoryBusy; }
    }

    public bool CanRefresh => !_disposed && IsConfigured;

    public bool CanRefreshHistory => !_disposed && IsHistoryConfigured && !_isHistoryBusy;

    public bool CanNextHistoryPage
    {
        get
        {
            lock (_sync)
            {
                return !_disposed && !_isHistoryBusy && _historyNextAfterPosition.HasValue &&
                    _historyThroughPosition.HasValue && _historyPage > 0;
            }
        }
    }

    public bool CanAcknowledge
    {
        get
        {
            lock (_sync)
            {
                return !_disposed && !_isCommandBusy && IsSnapshotFreshLocked() &&
                    HasPermissionLocked(Permission.AcknowledgeAlarm) &&
                    _selectedAlarm is not null && _selectedAlarm.Lifecycle != AlarmLifecycle.Cleared &&
                    !_selectedAlarm.Acknowledged && _runtime is not null &&
                    (!_acknowledgeRequiresStepUp || _stepUp is not null);
            }
        }
    }

    public bool CanReset
    {
        get
        {
            lock (_sync)
            {
                return !_disposed && !_isCommandBusy && IsSnapshotFreshLocked() &&
                    HasPermissionLocked(Permission.ResetAlarm) && _selectedAlarm is not null &&
                    _selectedAlarm.IsLatched && _selectedAlarm.SourceHealthy &&
                    _selectedAlarm.Lifecycle == AlarmLifecycle.RecoveredLatched &&
                    _runtime is not null && (!_resetRequiresStepUp || _stepUp is not null);
            }
        }
    }

    public string StatusMessage
    {
        get { lock (_sync) return _statusMessage; }
    }

    public string? ErrorCode
    {
        get { lock (_sync) return _errorCode; }
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

    public int HistoryPage
    {
        get { lock (_sync) return _historyPage; }
    }

    public RuntimeCommandOutcome? LastCommandOutcome
    {
        get { lock (_sync) return _lastCommandOutcome; }
    }

    public string SummaryText
    {
        get
        {
            lock (_sync)
            {
                var freshness = IsSnapshotFreshLocked() ? "新鲜" : "未知 / 已陈旧";
                var reason = SafeReason(_alarmState?.ReasonCode, StateUnavailableReason);
                return $"报警状态：{freshness} · 原始实例 {_instances.Count} · 活跃 {_instances.Count(item => item.Lifecycle != AlarmLifecycle.Cleared)} · " +
                    $"锁存 {_instances.Count(item => item.IsLatched && item.Lifecycle != AlarmLifecycle.Cleared)} · " +
                    $"PLC 显示 {_alarmState?.Plc.Entries.Count ?? 0} / 隐藏 {_alarmState?.Plc.HiddenCount ?? 0} · 原因 {reason}";
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            await ApplyAuthorizationUnavailableAsync("AlarmAuthorizationUnavailable").ConfigureAwait(true);
            return;
        }

        try
        {
            var session = await _sessions!.GetSessionAsync(cancellationToken).ConfigureAwait(true);
            var authorization = await _authorizationQuery!.GetCurrentAuthorizationAsync(
                session.SessionId, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            await _dispatcher.InvokeAsync(() =>
            {
                lock (_sync)
                {
                    _session = session;
                    _authorization = IsUsableAuthorization(authorization, session) ? authorization : null;
                    _errorCode = _authorization is null
                        ? SafeReason(authorization?.ReasonCode, AuthorizationRequiredReason)
                        : null;
                    _statusMessage = BuildStatusMessageLocked();
                }
                NotifyStateChanged();
            }).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            await ApplyAuthorizationUnavailableAsync("AlarmAuthorizationUnavailable").ConfigureAwait(true);
        }
    }

    public Task<RuntimeCommandOutcome?> AcknowledgeAlarmAsync(Guid alarmInstanceId,
        CancellationToken cancellationToken = default) =>
        ExecuteAlarmCommandAsync(Permission.AcknowledgeAlarm, AuditedCommandKind.AcknowledgeAlarm,
            alarmInstanceId, null, cancellationToken);

    public Task<RuntimeCommandOutcome?> AcknowledgeAlarmAsync(Guid alarmInstanceId, string stepUpPassword,
        CancellationToken cancellationToken = default) =>
        ExecuteAlarmCommandAsync(Permission.AcknowledgeAlarm, AuditedCommandKind.AcknowledgeAlarm,
            alarmInstanceId, stepUpPassword, cancellationToken);

    public Task<RuntimeCommandOutcome?> ResetAlarmAsync(Guid alarmInstanceId, string stepUpPassword,
        CancellationToken cancellationToken = default) =>
        ExecuteAlarmCommandAsync(Permission.ResetAlarm, AuditedCommandKind.ResetAlarm,
            alarmInstanceId, stepUpPassword, cancellationToken);

    public async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
    {
        var start = BeginHistory(HistoryMove.Refresh, cancellationToken);
        if (start.HasValue) await ExecuteHistoryAsync(start.Value).ConfigureAwait(true);
    }

    public async Task NextHistoryPageAsync(CancellationToken cancellationToken = default)
    {
        HistoryStart? start;
        lock (_sync)
        {
            if (_disposed || _historyQuery is null || _isHistoryBusy ||
                !_historyNextAfterPosition.HasValue || !_historyThroughPosition.HasValue)
                return;
            start = BeginHistoryLocked(HistoryMove.Next, _historyNextAfterPosition.Value,
                _historyThroughPosition, _historyPage + 1, cancellationToken);
        }
        if (start.HasValue) await ExecuteHistoryAsync(start.Value).ConfigureAwait(true);
    }

    public void CancelPendingOperations()
    {
        CancellationTokenSource? commandCancellation;
        CancellationTokenSource? historyCancellation;
        lock (_sync)
        {
            _commandVersion++;
            commandCancellation = _activeCommandCancellation;
            _activeCommandCancellation = null;
            _isCommandBusy = false;
            _historyRequestVersion++;
            historyCancellation = _activeHistoryCancellation;
            _activeHistoryCancellation = null;
            _isHistoryBusy = false;
        }
        commandCancellation?.Cancel();
        historyCancellation?.Cancel();
        NotifyStateChangedOnUi();
    }

    public void ClearSensitiveInputs()
    {
        CancelPendingOperations();
        lock (_sync)
        {
            _lastStepUpBinding = null;
            _errorCode = null;
            _statusMessage = BuildStatusMessageLocked();
        }
        NotifyStateChanged();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        CancelPendingOperations();
        _station.PropertyChanged -= StationChanged;
        _station.State.PropertyChanged -= StationStateChanged;
        if (_sessions is not null) _sessions.Changed -= SessionChanged;
        await Task.CompletedTask;
    }

    internal StepUpBinding? LastStepUpBinding
    {
        get { lock (_sync) return _lastStepUpBinding; }
    }

    private async Task<RuntimeCommandOutcome?> ExecuteAlarmCommandAsync(
        Permission permission,
        AuditedCommandKind commandKind,
        Guid alarmInstanceId,
        string? stepUpPassword,
        CancellationToken cancellationToken)
    {
        if (alarmInstanceId == Guid.Empty)
        {
            await ApplyCommandErrorAsync("AlarmInputInvalid").ConfigureAwait(true);
            return null;
        }

        if (_runtime is null || _sessions is null || _authorizationQuery is null)
        {
            await ApplyAuthorizationUnavailableAsync("AlarmAuthorizationUnavailable").ConfigureAwait(true);
            return null;
        }

        CommandStart? start;
        lock (_sync)
        {
            if (_disposed || _runtime is null || !IsSnapshotFreshLocked())
            {
                start = null;
            }
            else if (_isCommandBusy)
            {
                start = null;
            }
            else if (_instances.All(item => item.InstanceId != alarmInstanceId))
            {
                start = null;
            }
            else
            {
                var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _activeCommandCancellation = linked;
                _commandVersion++;
                _isCommandBusy = true;
                _lastCommandOutcome = null;
                _errorCode = null;
                _statusMessage = "正在提交报警操作…";
                start = new CommandStart(_commandVersion, linked);
            }
        }

        if (!start.HasValue)
        {
            await ApplyCommandUnavailableAsync().ConfigureAwait(true);
            return null;
        }

        try
        {
            var session = await _sessions!.GetSessionAsync(start.Value.Cancellation.Token).ConfigureAwait(true);
            if (!IsUsableSession(session))
            {
                await ApplyCommandErrorAsync("AlarmSessionChanged", start).ConfigureAwait(true);
                return null;
            }

            var authorization = await _authorizationQuery!.GetCurrentAuthorizationAsync(
                session.SessionId, start.Value.Cancellation.Token).ConfigureAwait(true);
            if (!IsUsableAuthorization(authorization, session) ||
                authorization.Account?.Permissions.Contains(permission) != true)
            {
                await ApplyCommandErrorAsync("AlarmPermissionDenied", start).ConfigureAwait(true);
                return null;
            }

            var correlationId = Guid.NewGuid();
            Guid? grantId = null;
            if (permission == Permission.ResetAlarm ? _resetRequiresStepUp : _acknowledgeRequiresStepUp)
            {
                if (_stepUp is null || string.IsNullOrEmpty(stepUpPassword))
                {
                    await ApplyCommandErrorAsync("AlarmInputInvalid", start).ConfigureAwait(true);
                    return null;
                }

                var binding = new StepUpBinding(permission, correlationId,
                    alarmInstanceId.ToString("D"), commandKind);
                lock (_sync) _lastStepUpBinding = binding;
                StepUpResult stepUpResult;
                try
                {
                    stepUpResult = await _stepUp.ReauthenticateAsync(
                        new StepUpRequest(correlationId, CreateInvocation(session, null), binding,
                            stepUpPassword), start.Value.Cancellation.Token).ConfigureAwait(true);
                }
                finally
                {
                    stepUpPassword = string.Empty;
                }

                if (!stepUpResult.Succeeded || stepUpResult.GrantId is not { } stepUpGrant)
                {
                    await ApplyCommandErrorAsync("StepUpAuthenticationRejected", start).ConfigureAwait(true);
                    return null;
                }
                grantId = stepUpGrant;
            }

            var currentSession = await _sessions.GetSessionAsync(start.Value.Cancellation.Token)
                .ConfigureAwait(true);
            if (!SameSession(session, currentSession))
            {
                await ApplyCommandErrorAsync("AlarmSessionChanged", start).ConfigureAwait(true);
                return null;
            }

            RuntimeCommand command = permission == Permission.AcknowledgeAlarm
                ? new AcknowledgeAlarmCommand(correlationId, CreateInvocation(currentSession, grantId), alarmInstanceId)
                : new ResetAlarmCommand(correlationId, CreateInvocation(currentSession, grantId), alarmInstanceId);
            RuntimeCommandOutcome outcome;
            try
            {
                outcome = await _runtime!.SubmitAsync(command, start.Value.Cancellation.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
            {
                await ApplyCommandErrorAsync("AlarmCommandCancelled", start).ConfigureAwait(true);
                return null;
            }
            catch
            {
                await ApplyCommandErrorAsync(CommandOutcomeUnknownReason, start).ConfigureAwait(true);
                return null;
            }

            // A returned Runtime outcome remains observable even if a later UI
            // cancellation or session event occurs. It is never rewritten as a
            // rejection and never changes the alarm list optimistically.
            await ApplyCommandOutcomeAsync(outcome).ConfigureAwait(true);
            return outcome;
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            await ApplyCommandErrorAsync(CommandOutcomeUnknownReason, start).ConfigureAwait(true);
            return null;
        }
        finally
        {
            CompleteCommand(start.Value);
            stepUpPassword = string.Empty;
        }
    }

    private async Task AcknowledgeSelectedAsync()
    {
        var selected = SelectedAlarm;
        if (selected is not null) await AcknowledgeAlarmAsync(selected.InstanceId).ConfigureAwait(true);
    }

    private async Task ResetSelectedAsync(string stepUpPassword)
    {
        var selected = SelectedAlarm;
        if (selected is not null) await ResetAlarmAsync(selected.InstanceId, stepUpPassword).ConfigureAwait(true);
    }

    private async Task ApplyCommandOutcomeAsync(RuntimeCommandOutcome outcome)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                _lastCommandOutcome = outcome;
                _errorCode = outcome.Disposition == CommandDisposition.Accepted
                    ? null : SafeReason(outcome.ReasonCode, CommandRejectedReason);
                _statusMessage = outcome.Disposition == CommandDisposition.Accepted
                    ? "报警操作已受理；请等待后续新鲜快照确认完成状态。"
                    : "报警操作未获批准；当前报警投影未作预先修改。";
            }
            NotifyStateChanged();
        }).ConfigureAwait(true);
    }

    private async Task ApplyCommandErrorAsync(string reason, CommandStart? start = null)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                if (start.HasValue && !IsCurrentCommandLocked(start.Value)) return;
                _lastCommandOutcome = null;
                _errorCode = SafeReason(reason, CommandOutcomeUnknownReason);
                _statusMessage = _errorCode == SnapshotStaleReason
                    ? "当前报警快照已陈旧，请等待新鲜快照。"
                    : "报警操作未完成；当前报警投影未作预先修改。";
            }
            NotifyStateChanged();
        }).ConfigureAwait(true);
    }

    private async Task ApplyCommandUnavailableAsync()
    {
        var reason = IsSnapshotFresh ? AuthorizationRequiredReason : SnapshotStaleReason;
        await ApplyCommandErrorAsync(reason).ConfigureAwait(true);
    }

    private HistoryStart? BeginHistory(HistoryMove move, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_disposed || _historyQuery is null) return null;
            return BeginHistoryLocked(move, 0, null, 1, cancellationToken);
        }
    }

    private HistoryStart? BeginHistoryLocked(HistoryMove move, long afterPosition,
        long? throughPosition, int pageNumber, CancellationToken cancellationToken)
    {
        _activeHistoryCancellation?.Cancel();
        var code = ParseHistoryCodeLocked();
        if (_historyErrorCode == "AlarmInputInvalid")
        {
            _isHistoryBusy = false;
            _historyStatusMessage = "报警历史筛选无效，请输入稳定报警代码。";
            NotifyHistoryChanged();
            return null;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeHistoryCancellation = linked;
        _historyRequestVersion++;
        _isHistoryBusy = true;
        if (move == HistoryMove.Refresh)
        {
            _historyRows.Clear();
            _historyPage = 0;
            _historyThroughPosition = null;
            _historyNextAfterPosition = null;
        }
        _historyErrorCode = null;
        _historyStatusMessage = "正在查询报警历史…";
        NotifyHistoryChanged();
        return new HistoryStart(_historyRequestVersion, afterPosition, throughPosition,
            pageNumber, code, linked, move);
    }

    private async Task ExecuteHistoryAsync(HistoryStart start)
    {
        try
        {
            AlarmHistoryPage page;
            try
            {
                page = await _historyQuery!.QueryAsync(new AlarmHistoryFilter(
                    code: start.Code, afterPosition: start.AfterPosition,
                    throughPosition: start.ThroughPosition, pageSize: _historyPageSize),
                    start.Cancellation.Token).ConfigureAwait(true);
                start.Cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (start.Cancellation.IsCancellationRequested)
            {
                CompleteHistoryCancellation(start);
                return;
            }
            catch
            {
                CompleteHistoryFailure(start, HistoryQueryFailedReason,
                    "报警历史查询失败，请检查存储后重试。");
                return;
            }

            try { ApplyHistoryPage(start, page); }
            catch (HistoryPageException exception)
            {
                CompleteHistoryFailure(start, exception.SafeCode,
                    exception.SafeCode == HistorySnapshotExpiredReason
                        ? "报警历史快照已过期，请重新刷新。"
                        : "报警历史结果无效，请重新刷新。 ");
            }
            catch
            {
                CompleteHistoryFailure(start, HistoryPageInvalidReason, "报警历史结果无效，请重新刷新。");
            }
        }
        finally
        {
            CompleteHistory(start);
        }
    }

    private void ApplyHistoryPage(HistoryStart start, AlarmHistoryPage page)
    {
        lock (_sync)
        {
            if (!IsCurrentHistoryLocked(start)) return;
            if (!page.Available)
                throw new HistoryPageException(SafeReason(page.ReasonCode, HistoryUnavailableReason));
            if (page.Records.Count > _historyPageSize || page.ThroughPosition < start.AfterPosition ||
                (page.NextAfterPosition.HasValue &&
                    (page.NextAfterPosition.Value <= start.AfterPosition ||
                     page.NextAfterPosition.Value > page.ThroughPosition)) ||
                (!page.Records.Any() && page.NextAfterPosition.HasValue))
                throw new HistoryPageException(HistoryPageInvalidReason);
            if (start.Move == HistoryMove.Next &&
                (!_historyThroughPosition.HasValue || page.ThroughPosition != start.ThroughPosition))
                throw new HistoryPageException(HistorySnapshotExpiredReason);

            _historyRows.Clear();
            foreach (var row in page.Records)
                _historyRows.Add(row with { ReasonCode = SafeReason(row.ReasonCode, "AlarmHistoryRecordReasonUnknown") });
            _historyPage = start.PageNumber;
            _historyThroughPosition = page.ThroughPosition;
            _historyNextAfterPosition = page.NextAfterPosition;
            _historyErrorCode = null;
            _historyStatusMessage = $"已载入报警历史第 {_historyPage} 页 · 快照上界 {_historyThroughPosition}。";
            NotifyHistoryChanged();
        }
    }

    private void CompleteHistoryFailure(HistoryStart start, string errorCode, string status)
    {
        lock (_sync)
        {
            if (!IsCurrentHistoryLocked(start)) return;
            _historyRows.Clear();
            _historyPage = 0;
            _historyThroughPosition = null;
            _historyNextAfterPosition = null;
            _historyErrorCode = SafeReason(errorCode, HistoryQueryFailedReason);
            _historyStatusMessage = status;
            NotifyHistoryChanged();
        }
    }

    private void CompleteHistoryCancellation(HistoryStart start)
    {
        lock (_sync)
        {
            if (!IsCurrentHistoryLocked(start)) return;
            _historyErrorCode = null;
            _historyStatusMessage = "报警历史查询已取消，可重新刷新。";
            NotifyHistoryChanged();
        }
    }

    private void CompleteHistory(HistoryStart start)
    {
        bool changed;
        lock (_sync)
        {
            changed = IsCurrentHistoryLocked(start);
            if (changed)
            {
                _isHistoryBusy = false;
                _activeHistoryCancellation = null;
            }
        }
        start.Cancellation.Dispose();
        if (changed) NotifyHistoryChanged();
    }

    private void ApplyStationProjection()
    {
        if (!_dispatcher.CheckAccess)
        {
            _ = _dispatcher.InvokeAsync(ApplyStationProjection);
            return;
        }

        var snapshot = _station.CurrentSnapshot;
        lock (_sync)
        {
            _projectionFresh = _station.Freshness == SnapshotFreshness.Fresh;
            if (snapshot?.AlarmState is null)
            {
                _projectionAvailable = false;
                _alarmState = null;
                _instances.Clear();
                _visibleInstances.Clear();
                _selectedAlarm = null;
                _errorCode = StateUnavailableReason;
                _statusMessage = snapshot is null ? "尚未收到完整报警快照。" : "当前快照未提供报警状态。";
            }
            else if (snapshot.AlarmState.RuntimeEpoch != snapshot.RuntimeEpoch ||
                snapshot.AlarmState.Revision != snapshot.Revision)
            {
                _projectionAvailable = false;
                _alarmState = null;
                _instances.Clear();
                _visibleInstances.Clear();
                _selectedAlarm = null;
                _errorCode = "AlarmRuntimeProjectionInvalid";
                _statusMessage = "报警快照版本不一致，状态已锁定为不可用。";
            }
            else
            {
                var state = snapshot.AlarmState;
                if (_projectionEpoch == state.RuntimeEpoch && state.Revision < _projectionRevision)
                {
                    _projectionFresh = _station.Freshness == SnapshotFreshness.Fresh;
                }
                else
                {
                    _projectionEpoch = state.RuntimeEpoch;
                    _projectionRevision = state.Revision;
                    _alarmState = state;
                    _projectionAvailable = state.Available;
                    // A policy-boundary failure disables actions but does not erase
                    // already verified simultaneous instances from the complete snapshot.
                    var selectedId = _selectedAlarm?.InstanceId;
                    _instances.Clear();
                    foreach (var instance in state.Instances) _instances.Add(instance);
                    RebuildVisibleLocked();
                    // A bound DataGrid can synchronously clear SelectedItem during Reset.
                    // Restore the stable identity after both collection notifications finish.
                    _selectedAlarm = selectedId.HasValue
                        ? _visibleInstances.FirstOrDefault(item => item.InstanceId == selectedId.Value) : null;
                }
                if (_projectionAvailable) _errorCode = null;
                _statusMessage = BuildStatusMessageLocked();
            }
        }
        NotifyStateChanged();
    }

    private void KeepSelectionLocked()
    {
        if (_selectedAlarm is null) return;
        _selectedAlarm = _instances.FirstOrDefault(item => item.InstanceId == _selectedAlarm.InstanceId);
    }

    private void RebuildVisibleLocked()
    {
        var filter = _codeFilterText.Trim();
        _visibleInstances.Clear();
        foreach (var instance in _instances)
        {
            if (filter.Length == 0 || instance.Code.Contains(filter, StringComparison.OrdinalIgnoreCase))
                _visibleInstances.Add(instance);
        }
    }

    private void StationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StationShellViewModel.CurrentSnapshot) or nameof(StationShellViewModel.Freshness))
            ApplyStationProjection();
    }

    private void StationStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StationStateViewModel.RawSnapshot) or nameof(StationStateViewModel.RuntimeEpoch))
            ApplyStationProjection();
    }

    private void SessionChanged(object? sender, InteractiveSessionChangedEventArgs args)
    {
        CancelPendingOperations();
        lock (_sync)
        {
            _authorization = null;
            _session = _sessions?.Current ?? UnauthenticatedSession;
            _errorCode = AuthorizationRequiredReason;
            _statusMessage = "会话已变化，报警操作已停止，请重新读取当前权限。";
        }
        NotifyStateChangedOnUi();
    }

    private bool IsSnapshotFreshLocked()
    {
        var snapshot = _station.CurrentSnapshot;
        return _projectionAvailable && _projectionFresh && snapshot?.AlarmState is not null &&
            snapshot.RuntimeEpoch == _projectionEpoch && snapshot.Revision == _projectionRevision &&
            snapshot.AlarmState.RuntimeEpoch == snapshot.RuntimeEpoch &&
            snapshot.AlarmState.Revision == snapshot.Revision;
    }

    private bool HasPermissionLocked(Permission permission)
    {
        var current = _sessions?.Current ?? _session;
        return IsUsableSession(current) && _authorization?.Available == true &&
            _authorization.SessionId == current.SessionId &&
            _authorization.Account?.Permissions.Contains(permission) == true;
    }

    private static bool IsUsableAuthorization(HumanAuthorizationSnapshot authorization,
        InteractiveSession session) => authorization.Available && authorization.Account is not null &&
        IsUsableSession(session) && authorization.SessionId == session.SessionId;

    private static bool IsUsableSession(InteractiveSession session) =>
        session.State == InteractiveSessionState.Authenticated && session.SessionId.HasValue &&
        session.SessionId.Value != Guid.Empty && !string.IsNullOrWhiteSpace(session.PrincipalId);

    private static bool SameSession(InteractiveSession left, InteractiveSession right) =>
        IsUsableSession(left) && IsUsableSession(right) && left.SessionId == right.SessionId &&
        string.Equals(left.PrincipalId, right.PrincipalId, StringComparison.Ordinal);

    private static CommandInvocation CreateInvocation(InteractiveSession session, Guid? grantId) =>
        new(CommandSource.PhysicalConsole, session.PrincipalId, session.SessionId, grantId);

    private string? ParseHistoryCodeLocked()
    {
        var code = _historyCodeText.Trim();
        if (code.Length == 0) return null;
        try
        {
            _ = new AlarmHistoryFilter(code: code, pageSize: _historyPageSize);
            return code;
        }
        catch
        {
            _historyErrorCode = "AlarmInputInvalid";
            _historyStatusMessage = "报警历史筛选无效，请输入稳定报警代码。";
            return null;
        }
    }

    private bool IsCurrentCommandLocked(CommandStart start) => !_disposed &&
        _commandVersion == start.Version && ReferenceEquals(_activeCommandCancellation, start.Cancellation);

    private bool IsCurrentHistoryLocked(HistoryStart start) => !_disposed &&
        _historyRequestVersion == start.RequestVersion && ReferenceEquals(_activeHistoryCancellation, start.Cancellation);

    private void CompleteCommand(CommandStart start)
    {
        bool changed;
        lock (_sync)
        {
            changed = IsCurrentCommandLocked(start);
            if (changed)
            {
                _activeCommandCancellation = null;
                _isCommandBusy = false;
            }
        }
        start.Cancellation.Dispose();
        if (changed) NotifyStateChanged();
    }

    private async Task ApplyAuthorizationUnavailableAsync(string reason)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                _authorization = null;
                _errorCode = SafeReason(reason, AuthorizationRequiredReason);
                _statusMessage = "当前报警权限不可用；报警仍以新鲜快照为准。";
            }
            NotifyStateChanged();
        }).ConfigureAwait(true);
    }

    private string BuildStatusMessageLocked()
    {
        if (!_projectionAvailable) return "当前报警状态不可用；请等待完整新鲜快照。";
        if (!_projectionFresh) return "报警快照已陈旧；当前仅显示历史观察，操作已禁用。";
        return "报警状态已读取；确认表示已查看，重置仍需 Runtime 验证来源恢复和全部前置条件。";
    }

    private static string SafeReason(string? reason, string fallback)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 96) return fallback;
        return SafeReasonCodes.Contains(reason) ? reason : fallback;
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsSnapshotFresh));
        OnPropertyChanged(nameof(IsSnapshotUnavailable));
        OnPropertyChanged(nameof(CurrentAlarmState));
        OnPropertyChanged(nameof(SelectedAlarm));
        OnPropertyChanged(nameof(TotalAlarmCount));
        OnPropertyChanged(nameof(VisibleAlarmCount));
        OnPropertyChanged(nameof(ActiveAlarmCount));
        OnPropertyChanged(nameof(LatchedAlarmCount));
        OnPropertyChanged(nameof(PlcShownCount));
        OnPropertyChanged(nameof(PlcHiddenCount));
        OnPropertyChanged(nameof(PlcTotalUncleared));
        OnPropertyChanged(nameof(PlcBlockingCount));
        OnPropertyChanged(nameof(PlcFaultAbortPresent));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ErrorCode));
        OnPropertyChanged(nameof(LastCommandOutcome));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanRefresh));
        NotifyCommandAvailability();
        RefreshCommand.RaiseCanExecuteChanged();
        AcknowledgeCommand.RaiseCanExecuteChanged();
        ResetCommand.RaiseCanExecuteChanged();
    }

    private void NotifyStateChangedOnUi()
    {
        if (_dispatcher.CheckAccess) NotifyStateChanged();
        else _ = _dispatcher.InvokeAsync(NotifyStateChanged);
    }

    private void NotifyCommandAvailability()
    {
        OnPropertyChanged(nameof(CanAcknowledge));
        OnPropertyChanged(nameof(CanReset));
        AcknowledgeCommand?.RaiseCanExecuteChanged();
        ResetCommand?.RaiseCanExecuteChanged();
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(HistoryRows));
        OnPropertyChanged(nameof(HistoryStatusMessage));
        OnPropertyChanged(nameof(HistoryErrorCode));
        OnPropertyChanged(nameof(HistoryThroughPosition));
        OnPropertyChanged(nameof(HistoryPage));
        OnPropertyChanged(nameof(IsHistoryBusy));
        OnPropertyChanged(nameof(CanRefreshHistory));
        OnPropertyChanged(nameof(CanNextHistoryPage));
        RefreshHistoryCommand?.RaiseCanExecuteChanged();
        NextHistoryPageCommand?.RaiseCanExecuteChanged();
    }

    private static InteractiveSession UnauthenticatedSession =>
        new(InteractiveSessionState.Unauthenticated, null, null);

    private sealed class HistoryPageException : InvalidOperationException
    {
        public HistoryPageException(string safeCode) : base(safeCode) => SafeCode = safeCode;
        public string SafeCode { get; }
    }
}
