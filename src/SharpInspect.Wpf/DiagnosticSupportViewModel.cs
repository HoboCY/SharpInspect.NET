using System.Globalization;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>Closed reason choice. The UI never sends free text as a maintenance reason.</summary>
public sealed record DiagnosticSupportReasonOption(DiagnosticSupportReason Value, string Label);

/// <summary>
/// Optional bounded maintenance surface for one diagnostic capture elevation and one
/// default support bundle. It only presents controlled inputs: a single deployed
/// component, Trace/Debug, an explicit duration and event cap, a closed reason, an
/// explicit UTC range and the current runtime epoch. It offers no arbitrary destination
/// path, raw/protected toggle, or save/copy/read-material action. Every command is
/// frozen before authentication and requires a fresh Step-Up bound to that exact
/// correlation, authorization target and audited command kind; the display shows only
/// authoritative reads and never treats an Accepted outcome as completion.
/// </summary>
public sealed class DiagnosticSupportViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan PresentationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultBundleWindow = TimeSpan.FromMinutes(15);
    private static readonly IReadOnlyList<string> SupportedCaptureLevels = new[] { "Trace", "Debug" };

    private readonly IStationRuntime _runtime;
    private readonly IDiagnosticSupportQuery _query;
    private readonly IStepUpAuthentication _stepUp;
    private readonly IInteractiveSessionService _sessions;
    private readonly LoggingDiagnosticsPolicy _loggingPolicy;
    private readonly DiagnosticSupportPolicy _supportPolicy;
    private readonly IUiDispatcher _dispatcher;
    private readonly HashSet<string> _approvedComponents;
    private CancellationTokenSource? _cancellation;
    private long _generation;
    private bool _busy;
    private bool _disposed;
    private bool _fresh;
    private bool _submitted;
    private Guid _runtimeEpoch;
    private DiagnosticCaptureSnapshot? _capture;
    private SupportBundleSnapshot? _bundle;
    private string _status = "尚未读取诊断支持状态。";
    private string _selectedComponent;
    private string _selectedCaptureLevel = "Debug";
    private string _durationSecondsText = "60";
    private string _maximumEventsText = "1000";
    private DiagnosticSupportReasonOption _selectedReason;
    private string _fromUtcText = string.Empty;
    private string _throughUtcText = string.Empty;

    /// <summary>The panel clears native credential controls after a session change or hide.</summary>
    internal event EventHandler? SensitiveInputsInvalidated;

    public DiagnosticSupportViewModel(IStationRuntime runtime, IDiagnosticSupportQuery query,
        IStepUpAuthentication stepUpAuthentication, IInteractiveSessionService sessions,
        LoggingDiagnosticsPolicy loggingPolicy, DiagnosticSupportPolicy supportPolicy,
        IUiDispatcher? dispatcher = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _stepUp = stepUpAuthentication ?? throw new ArgumentNullException(nameof(stepUpAuthentication));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _loggingPolicy = loggingPolicy ?? throw new ArgumentNullException(nameof(loggingPolicy));
        _supportPolicy = supportPolicy ?? throw new ArgumentNullException(nameof(supportPolicy));
        _dispatcher = dispatcher ?? new DispatcherUiDispatcher();
        Components = loggingPolicy.Contracts.Select(contract => contract.Component)
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        _approvedComponents = new HashSet<string>(Components, StringComparer.Ordinal);
        _selectedComponent = Components[0];
        CaptureLevels = Array.AsReadOnly(SupportedCaptureLevels.ToArray());
        ReasonOptions = Array.AsReadOnly(new[]
        {
            new DiagnosticSupportReasonOption(DiagnosticSupportReason.MaintenanceInvestigation, "维护排查"),
            new DiagnosticSupportReasonOption(DiagnosticSupportReason.FaultInvestigation, "故障排查"),
            new DiagnosticSupportReasonOption(DiagnosticSupportReason.AcceptanceSupport, "验收支持")
        });
        _selectedReason = ReasonOptions[0];
        RefreshCommand = new(RefreshAsync, () => CanRefresh);
        _sessions.Changed += SessionChanged;
    }

    /// <summary>Components approved by the deployed logging policy; exactly one may be selected.</summary>
    public IReadOnlyList<string> Components { get; }
    public IReadOnlyList<string> CaptureLevels { get; }
    public IReadOnlyList<DiagnosticSupportReasonOption> ReasonOptions { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public bool IsBusy => _busy;
    public bool IsAuthenticated => UsableSession(_sessions.Current);
    public bool CanRefresh => !_disposed && !_busy && IsAuthenticated;
    public string StatusMessage => _status;
    public string PolicyLimitsText =>
        $"部署上限：捕获最长 {FormatDuration(_loggingPolicy.MaximumCaptureDuration)}，最多 {Math.Min(_loggingPolicy.MaximumCaptureEvents, 1_000_000)} 个事件；" +
        $"支持包范围最长 {FormatDuration(_supportPolicy.MaximumScope)}。未提升时保持部署基线级别。";
    public string RuntimeEpochText => _fresh && _runtimeEpoch != Guid.Empty ? _runtimeEpoch.ToString("D") : string.Empty;

    /// <summary>Authoritative capture count, cap, window and phase; never derived from a command outcome.</summary>
    public string CapturePhaseText => !_fresh || _capture is null ? "未读取"
        : !_capture.Configured ? "未配置" : CapturePhaseLabel(_capture.Phase);
    public string CaptureEventsText => !_fresh || _capture is null ? "—"
        : $"已记录 {_capture.ObservedEvents.ToString(CultureInfo.InvariantCulture)} · 上限 {(_capture.MaximumEvents is { } maximum ? maximum.ToString(CultureInfo.InvariantCulture) : "未提供")}";
    public string CaptureWindowText => !_fresh || _capture is null ? "—"
        : $"开始 {UtcText(_capture.StartedAtUtc)} · 到期 {UtcText(_capture.ExpiresAtUtc)}";
    public string BundlePhaseText => !_fresh || _bundle is null ? "未读取"
        : !_bundle.Configured ? "未配置" : BundlePhaseLabel(_bundle.Phase);
    public string BundleIdText => CompletedBundle && _bundle!.BundleId is { } id && id != Guid.Empty
        ? id.ToString("D") : string.Empty;
    public string BundleHashText => CompletedBundle ? _bundle!.ContentHash ?? string.Empty : string.Empty;
    public string BundleBytesText => CompletedBundle && _bundle!.Bytes is { } bytes
        ? $"{bytes.ToString(CultureInfo.InvariantCulture)} 字节" : string.Empty;
    public string BundleExpiryText => CompletedBundle && _bundle!.ExpiresAtUtc is { } expires
        ? $"到期 {expires.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture)}" : string.Empty;

    public bool CanStartCapture => CanOperate && CaptureAllowsStart && TryBuildProfile(out _, out _);
    public bool CanStopCapture => CanOperate && _capture is { Configured: true, CaptureSessionId: { } id } capture &&
        id != Guid.Empty && capture.Phase is DiagnosticCapturePhase.Admitted or DiagnosticCapturePhase.Active or
        DiagnosticCapturePhase.Draining;
    public bool CanCreateBundle => CanOperate && BundleAllowsCreate && TryParseWindow(out _, out _, out _);

    public string SelectedComponent
    {
        get => _selectedComponent;
        set => SetTextInput(ref _selectedComponent, value, 128);
    }
    public string SelectedCaptureLevel
    {
        get => _selectedCaptureLevel;
        set => SetTextInput(ref _selectedCaptureLevel, value, 16);
    }
    public string DurationSecondsText
    {
        get => _durationSecondsText;
        set => SetTextInput(ref _durationSecondsText, value, 6);
    }
    public string MaximumEventsText
    {
        get => _maximumEventsText;
        set => SetTextInput(ref _maximumEventsText, value, 7);
    }
    public DiagnosticSupportReasonOption SelectedReason
    {
        get => _selectedReason;
        set
        {
            EnsureUi();
            if (_disposed || value is null || !ReasonOptions.Contains(value)) return;
            if (EqualityComparer<DiagnosticSupportReasonOption>.Default.Equals(_selectedReason, value)) return;
            _selectedReason = value;
            InvalidatePendingOperation();
            Notify();
        }
    }
    public string FromUtcText
    {
        get => _fromUtcText;
        set => SetTextInput(ref _fromUtcText, value, 32);
    }
    public string ThroughUtcText
    {
        get => _throughUtcText;
        set => SetTextInput(ref _throughUtcText, value, 32);
    }

    /// <summary>Reads the authoritative capture/bundle state together with the current runtime epoch.</summary>
    public async Task RefreshAsync()
    {
        CancellationTokenSource? cancellation = null;
        InteractiveSession? session = null;
        long generation = 0;
        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed || _busy) return;
            if (!IsAuthenticated)
            {
                _status = "请先登录。";
                Notify();
                return;
            }
            session = _sessions.Current;
            cancellation = new CancellationTokenSource(PresentationTimeout);
            _cancellation = cancellation;
            generation = Interlocked.Increment(ref _generation);
            _busy = true;
            _status = "正在读取诊断支持状态…";
            Notify();
        });
        if (cancellation is null || session is null) return;
        StationStateSnapshot? station = null;
        DiagnosticCaptureSnapshot? capture = null;
        SupportBundleSnapshot? bundle = null;
        try
        {
            await Task.Run(async () =>
            {
                station = await _runtime.GetSnapshotAsync(cancellation.Token).ConfigureAwait(false);
                capture = _query.ReadCapture();
                bundle = _query.ReadBundle();
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_cancellation, cancellation)) { _cancellation = null; _busy = false; }
                if (!_disposed && generation == _generation && SameSession(session, _sessions.Current))
                {
                    if (Valid(station, capture, bundle)) Apply(station!, capture!, bundle!);
                    else
                    {
                        InvalidateDisplay();
                        _status = "诊断支持状态不可用或与当前 Runtime 不一致，请重试。";
                    }
                }
                Notify();
            });
            cancellation.Dispose();
        }
    }

    public Task<RuntimeCommandOutcome?> StartCaptureWithStepUpAsync(string password) =>
        SubmitAsync(SupportOperation.StartCapture, password);
    public Task<RuntimeCommandOutcome?> StopCaptureWithStepUpAsync(string password) =>
        SubmitAsync(SupportOperation.StopCapture, password);
    public Task<RuntimeCommandOutcome?> CreateBundleWithStepUpAsync(string password) =>
        SubmitAsync(SupportOperation.CreateBundle, password);

    /// <summary>Ends any pending wait, clears authoritative projections and resets frozen inputs.</summary>
    public void Deactivate()
    {
        EnsureUi();
        DeactivateCore("页面已离开；重新进入后刷新最新状态。");
    }

    public async ValueTask DisposeAsync() => await _dispatcher.InvokeAsync(() =>
    {
        if (_disposed) return;
        _disposed = true;
        _sessions.Changed -= SessionChanged;
        DeactivateCore("诊断支持页面已关闭。");
    });

    private enum SupportOperation { StartCapture, StopCapture, CreateBundle }

    private bool CanOperate => !_disposed && !_busy && _fresh && IsAuthenticated;
    private bool CaptureAllowsStart => _capture is
        { Configured: true, Phase: DiagnosticCapturePhase.Baseline or DiagnosticCapturePhase.Completed or
            DiagnosticCapturePhase.Failed or DiagnosticCapturePhase.Interrupted };
    private bool BundleAllowsCreate => _bundle is
        { Configured: true, Phase: SupportBundlePhase.Idle or SupportBundlePhase.Completed or
            SupportBundlePhase.Failed or SupportBundlePhase.Interrupted };
    private bool CompletedBundle => _fresh && _bundle is { Phase: SupportBundlePhase.Completed };

    private async Task<RuntimeCommandOutcome?> SubmitAsync(SupportOperation operation, string password)
    {
        DiagnosticSupportCommand? command = null;
        InteractiveSession? session = null;
        CancellationTokenSource? cancellation = null;
        long generation = 0;
        var frozenEpoch = Guid.Empty;
        var resultMessage = "操作等待已结束，结果未确认；请刷新查看。";
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || _busy) return;
                if (!IsAuthenticated)
                {
                    _status = "请先登录。";
                    Notify();
                    return;
                }
                session = _sessions.Current;
                generation = Interlocked.Increment(ref _generation);
                _submitted = false;
                cancellation = new CancellationTokenSource(PresentationTimeout);
                _cancellation = cancellation;
                _busy = true;
                _status = operation == SupportOperation.CreateBundle
                    ? "正在读取当前 Runtime 标识…" : "正在准备本次维护操作…";
                Notify();
            });
            if (cancellation is null || session is null) return null;
            var token = cancellation.Token;

            if (operation == SupportOperation.CreateBundle)
            {
                StationStateSnapshot? current = null;
                try
                {
                    current = await Task.Run(async () => await _runtime.GetSnapshotAsync(token).ConfigureAwait(false))
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
                if (current is null || current.RuntimeEpoch == Guid.Empty)
                {
                    resultMessage = "无法读取当前 Runtime 标识，支持包未提交。";
                    return null;
                }
                frozenEpoch = current.RuntimeEpoch;
            }

            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || generation != _generation || !SameSession(session, _sessions.Current)) return;
                var invocation = Invocation(session!);
                var correlation = Guid.NewGuid();
                switch (operation)
                {
                    case SupportOperation.StartCapture:
                        if (!CaptureAllowsStart) { resultMessage = "当前捕获阶段不允许启动新的提升；请刷新。"; return; }
                        if (!TryBuildProfile(out var profile, out var profileError)) { resultMessage = profileError!; return; }
                        command = new StartDiagnosticCaptureCommand(correlation, invocation, Guid.NewGuid(),
                            profile!, _selectedReason.Value);
                        break;
                    case SupportOperation.StopCapture:
                        if (!TryResolveCaptureSession(out var captureSessionId))
                        {
                            resultMessage = "没有可停止的捕获会话；请刷新。";
                            return;
                        }
                        command = new StopDiagnosticCaptureCommand(correlation, invocation, captureSessionId,
                            _selectedReason.Value);
                        break;
                    default:
                        if (!BundleAllowsCreate) { resultMessage = "当前支持包阶段不允许创建新的支持包；请刷新。"; return; }
                        if (!TryParseWindow(out var fromUtc, out var throughUtc, out var windowError))
                        {
                            resultMessage = windowError!;
                            return;
                        }
                        var scope = new SupportBundleScope(frozenEpoch, fromUtc, throughUtc,
                            Array.Empty<ExecutionCorrelationId>(), Array.Empty<Guid>(), Array.Empty<Guid>());
                        command = new CreateSupportBundleCommand(correlation, invocation, Guid.NewGuid(), scope,
                            _supportPolicy.ContentHash, _selectedReason.Value);
                        break;
                }
                if (command is not null) _status = "正在验证本次操作的身份与授权…";
                Notify();
            });
            if (command is null) return null;

            var permission = operation == SupportOperation.CreateBundle
                ? Permission.ExportSupportBundle : Permission.StartDiagnosticCapture;
            var commandKind = operation switch
            {
                SupportOperation.StartCapture => AuditedCommandKind.StartDiagnosticCapture,
                SupportOperation.StopCapture => AuditedCommandKind.StopDiagnosticCapture,
                _ => AuditedCommandKind.CreateSupportBundle
            };
            var binding = new StepUpBinding(permission, command.CorrelationId, command.AuthorizationTarget, commandKind);
            StepUpResult authentication;
            try
            {
                authentication = await Task.Run(async () => await _stepUp.ReauthenticateAsync(
                        new StepUpRequest(Guid.NewGuid(), command.Invocation, binding, password ?? string.Empty), token)
                    .ConfigureAwait(false)).WaitAsync(token).ConfigureAwait(false);
            }
            finally { password = string.Empty; }
            token.ThrowIfCancellationRequested();
            if (!authentication.Succeeded || authentication.GrantId is not { } grant || grant == Guid.Empty)
            {
                resultMessage = "身份验证未通过，未提交本次操作。";
                return null;
            }

            var maySubmit = false;
            await _dispatcher.InvokeAsync(() =>
            {
                maySubmit = !_disposed && generation == _generation && SameSession(session, _sessions.Current);
                if (maySubmit)
                {
                    _submitted = true;
                    _status = "操作请求已发出，正在等待持久结果…";
                    Notify();
                }
            });
            if (!maySubmit) return null;

            if (operation == SupportOperation.CreateBundle)
            {
                StationStateSnapshot? current = null;
                try
                {
                    current = await Task.Run(async () => await _runtime.GetSnapshotAsync(token).ConfigureAwait(false))
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
                if (current is null) { resultMessage = "无法确认当前 Runtime 标识，支持包未提交。"; return null; }
                if (current.RuntimeEpoch != frozenEpoch)
                {
                    resultMessage = "Runtime 标识已变化，支持包未提交；请刷新后重试。";
                    return null;
                }
            }

            var grantedInvocation = Invocation(session, grant);
            // The epoch observation is asynchronous; inputs/session/navigation may have
            // changed after authentication while that read was physically outstanding.
            await _dispatcher.InvokeAsync(() => maySubmit = !_disposed && generation == _generation &&
                SameSession(session, _sessions.Current) && !token.IsCancellationRequested);
            if (!maySubmit) return null;
            RuntimeCommand submission = command switch
            {
                StartDiagnosticCaptureCommand start => start with { Invocation = grantedInvocation },
                StopDiagnosticCaptureCommand stop => stop with { Invocation = grantedInvocation },
                CreateSupportBundleCommand bundle => bundle with { Invocation = grantedInvocation },
                _ => command
            };
            var outcome = await Task.Run(async () => await _runtime.SubmitAsync(submission, CancellationToken.None)
                    .ConfigureAwait(false)).WaitAsync(token).ConfigureAwait(false);
            resultMessage = outcome.Disposition == CommandDisposition.Accepted
                ? operation switch
                {
                    SupportOperation.StartCapture => "启动请求已接纳；捕获尚未激活，请刷新确认权威阶段。",
                    SupportOperation.StopCapture => "停止请求已接纳；请刷新确认权威阶段。",
                    _ => "支持包创建已接纳；完成后刷新才能显示 ID、哈希与字节数。"
                }
                : $"Runtime 拒绝了本次操作（{SafeReason(outcome.ReasonCode, "Rejected")}），请刷新并查看命令审计。";
            return outcome;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return null; }
        finally
        {
            password = string.Empty;
            await _dispatcher.InvokeAsync(() =>
            {
                if (cancellation is null) return;
                if (ReferenceEquals(_cancellation, cancellation)) { _cancellation = null; _busy = false; }
                if (!_disposed && generation == _generation && SameSession(session, _sessions.Current))
                {
                    _status = resultMessage;
                    // A submitted command invalidates every previously observed projection.
                    if (_submitted)
                    {
                        _submitted = false;
                        InvalidateDisplay();
                    }
                }
                Notify();
            });
            cancellation?.Dispose();
        }
    }

    private void Apply(StationStateSnapshot station, DiagnosticCaptureSnapshot capture, SupportBundleSnapshot bundle)
    {
        _runtimeEpoch = station.RuntimeEpoch;
        _capture = capture;
        _bundle = bundle;
        _fresh = true;
        if (_fromUtcText.Length == 0 && _throughUtcText.Length == 0)
        {
            var span = DefaultBundleWindow < _supportPolicy.MaximumScope ? DefaultBundleWindow : _supportPolicy.MaximumScope;
            _fromUtcText = FormatUtc(station.ObservedAtUtc - span);
            _throughUtcText = FormatUtc(station.ObservedAtUtc);
        }
        _status = "已读取诊断支持状态。";
    }

    private static bool Valid(StationStateSnapshot? station, DiagnosticCaptureSnapshot? capture,
        SupportBundleSnapshot? bundle)
    {
        if (station is null || capture is null || bundle is null || station.RuntimeEpoch == Guid.Empty) return false;
        if (capture.RuntimeEpoch != station.RuntimeEpoch || bundle.RuntimeEpoch != station.RuntimeEpoch) return false;
        if (capture.ObservedEvents < 0 || capture.MaximumEvents is < 0 || bundle.Bytes is < 0) return false;
        // A completed bundle must carry its full controlled identity; partial completion is never displayed.
        if (bundle.Phase == SupportBundlePhase.Completed)
        {
            if (bundle.BundleId is not { } id || id == Guid.Empty || bundle.Bytes is null) return false;
            var hash = bundle.ContentHash;
            if (hash is null || hash.Length is < 1 or > 128 || string.IsNullOrWhiteSpace(hash)) return false;
        }
        return true;
    }

    private bool TryBuildProfile(out DiagnosticCaptureProfile? profile, out string? error)
    {
        profile = null;
        error = null;
        if (!_approvedComponents.Contains(_selectedComponent))
        {
            error = "只能选择一个部署政策批准的组件。";
            return false;
        }
        if (_selectedCaptureLevel is not ("Trace" or "Debug"))
        {
            error = "级别仅支持 Trace 或 Debug。";
            return false;
        }
        if (!int.TryParse(_durationSecondsText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds < 1)
        {
            error = "持续时间必须为至少 1 秒的整数。";
            return false;
        }
        var maximumDuration = _loggingPolicy.MaximumCaptureDuration < TimeSpan.FromHours(1)
            ? _loggingPolicy.MaximumCaptureDuration : TimeSpan.FromHours(1);
        if (seconds > (int)maximumDuration.TotalSeconds)
        {
            error = $"持续时间不得超过部署上限 {FormatDuration(maximumDuration)}。";
            return false;
        }
        if (!int.TryParse(_maximumEventsText, NumberStyles.None, CultureInfo.InvariantCulture, out var events) ||
            events < 1)
        {
            error = "事件上限必须为正整数。";
            return false;
        }
        var maximumEvents = Math.Min(_loggingPolicy.MaximumCaptureEvents, 1_000_000);
        if (events > maximumEvents)
        {
            error = $"事件上限不得超过部署上限 {maximumEvents}。";
            return false;
        }
        profile = new DiagnosticCaptureProfile(_loggingPolicy.ContentHash, new[] { _selectedComponent },
            _selectedCaptureLevel == "Trace" ? DiagnosticLevel.Trace : DiagnosticLevel.Debug,
            TimeSpan.FromSeconds(seconds), events);
        return true;
    }

    private bool TryParseWindow(out DateTimeOffset fromUtc, out DateTimeOffset throughUtc, out string? error)
    {
        fromUtc = default;
        throughUtc = default;
        error = null;
        if (!TryParseUtc(_fromUtcText, out fromUtc))
        {
            error = "起始时间必须为显式 UTC（例如 2026-09-16T00:00:00Z）。";
            return false;
        }
        if (!TryParseUtc(_throughUtcText, out throughUtc))
        {
            error = "结束时间必须为显式 UTC（例如 2026-09-16T01:00:00Z）。";
            return false;
        }
        if (throughUtc <= fromUtc)
        {
            error = "结束时间必须晚于起始时间。";
            return false;
        }
        if (throughUtc - fromUtc > _supportPolicy.MaximumScope)
        {
            error = $"支持包范围不得超过部署上限 {FormatDuration(_supportPolicy.MaximumScope)}。";
            return false;
        }
        return true;
    }

    private bool TryResolveCaptureSession(out Guid captureSessionId)
    {
        captureSessionId = Guid.Empty;
        if (_capture is { Configured: true, CaptureSessionId: { } id } capture && id != Guid.Empty &&
            capture.Phase is DiagnosticCapturePhase.Admitted or DiagnosticCapturePhase.Active or
                DiagnosticCapturePhase.Draining)
            captureSessionId = id;
        return captureSessionId != Guid.Empty;
    }

    private void SetTextInput(ref string field, string? value, int maximumLength)
    {
        EnsureUi();
        if (_disposed) return;
        var bounded = (value ?? string.Empty).Trim();
        if (bounded.Length > maximumLength) bounded = bounded[..maximumLength];
        if (string.Equals(field, bounded, StringComparison.Ordinal)) return;
        field = bounded;
        InvalidatePendingOperation();
        Notify();
    }

    private void InvalidatePendingOperation()
    {
        Interlocked.Increment(ref _generation);
        DescribeInvalidatedWait();
        _cancellation?.Cancel();
    }

    private void DescribeInvalidatedWait()
    {
        if (!_busy) return;
        _status = _submitted
            ? "操作请求已发出，结果尚未确认；请刷新并查看 Runtime 记录。"
            : "输入或页面已变化，本次等待已结束；未提交任何操作。";
    }

    private void DeactivateCore(string status)
    {
        Interlocked.Increment(ref _generation);
        _cancellation?.Cancel();
        _capture = null;
        _bundle = null;
        _fresh = false;
        _submitted = false;
        _runtimeEpoch = Guid.Empty;
        _selectedComponent = Components[0];
        _selectedCaptureLevel = "Debug";
        _durationSecondsText = "60";
        _maximumEventsText = "1000";
        _selectedReason = ReasonOptions[0];
        _fromUtcText = string.Empty;
        _throughUtcText = string.Empty;
        _status = status;
        SensitiveInputsInvalidated?.Invoke(this, EventArgs.Empty);
        Notify();
    }

    private void InvalidateDisplay()
    {
        _capture = null;
        _bundle = null;
        _fresh = false;
        _runtimeEpoch = Guid.Empty;
    }

    private async void SessionChanged(object? sender, InteractiveSessionChangedEventArgs args)
    {
        try
        {
            Interlocked.Increment(ref _generation);
            await _dispatcher.InvokeAsync(() => DeactivateCore("会话已变化，请重新登录并刷新。"));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    private void EnsureUi()
    {
        if (!_dispatcher.CheckAccess) throw new PresentationStateUnavailableException();
    }

    private void Notify()
    {
        foreach (var name in new[]
                 {
                     nameof(IsBusy), nameof(IsAuthenticated), nameof(CanRefresh), nameof(StatusMessage),
                     nameof(PolicyLimitsText), nameof(RuntimeEpochText), nameof(CapturePhaseText),
                     nameof(CaptureEventsText), nameof(CaptureWindowText), nameof(BundlePhaseText),
                     nameof(BundleIdText), nameof(BundleHashText), nameof(BundleBytesText), nameof(BundleExpiryText),
                     nameof(CanStartCapture), nameof(CanStopCapture), nameof(CanCreateBundle),
                     nameof(SelectedComponent), nameof(SelectedCaptureLevel), nameof(DurationSecondsText),
                     nameof(MaximumEventsText), nameof(SelectedReason), nameof(FromUtcText), nameof(ThroughUtcText)
                 })
            OnPropertyChanged(name);
        RefreshCommand.RaiseCanExecuteChanged();
    }

    private static bool UsableSession(InteractiveSession? session) =>
        session is { State: InteractiveSessionState.Authenticated, SessionId: { } id } &&
        id != Guid.Empty && !string.IsNullOrWhiteSpace(session.PrincipalId);

    private static bool SameSession(InteractiveSession? left, InteractiveSession? right) =>
        UsableSession(left) && UsableSession(right) &&
        left!.SessionId == right!.SessionId && left.PrincipalId == right.PrincipalId;

    private static CommandInvocation Invocation(InteractiveSession session, Guid? grant = null) =>
        new(CommandSource.PhysicalConsole, session.PrincipalId, session.SessionId, grant);

    private static bool TryParseUtc(string? text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse((text ?? string.Empty).Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string UtcText(DateTimeOffset? value) =>
        value is { } current ? current.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture) : "—";

    private static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? $"{value.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} 小时"
        : value.TotalMinutes >= 1
            ? $"{value.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} 分钟"
            : $"{value.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} 秒";

    private static string CapturePhaseLabel(DiagnosticCapturePhase phase) => phase switch
    {
        DiagnosticCapturePhase.Baseline => "基线（未提升）",
        DiagnosticCapturePhase.Admitted => "已接纳（未激活）",
        DiagnosticCapturePhase.Active => "进行中",
        DiagnosticCapturePhase.Draining => "结束中",
        DiagnosticCapturePhase.Completed => "已完成",
        DiagnosticCapturePhase.Failed => "已失败",
        DiagnosticCapturePhase.Interrupted => "已中断",
        _ => "未知"
    };

    private static string BundlePhaseLabel(SupportBundlePhase phase) => phase switch
    {
        SupportBundlePhase.Idle => "空闲（无支持包）",
        SupportBundlePhase.Admitted => "已接纳（未开始）",
        SupportBundlePhase.Collecting => "正在收集",
        SupportBundlePhase.Staged => "已暂存（未发布）",
        SupportBundlePhase.Completed => "已完成",
        SupportBundlePhase.Failed => "已失败",
        SupportBundlePhase.Interrupted => "已中断",
        _ => "未知"
    };

    private static string SafeReason(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
        value.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '-' or '.'))
            ? fallback : value;
}
