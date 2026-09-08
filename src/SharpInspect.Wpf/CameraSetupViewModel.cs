using System.Collections.ObjectModel;
using System.Globalization;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Presentation state for the maintenance camera binding page.  The view model
/// talks only to the restricted setup boundary; it never receives a provider,
/// device, store, or production control capability.
/// </summary>
public sealed class CameraSetupViewModel : ObservableObject, IAsyncDisposable
{
    private enum OperationKind
    {
        Refresh,
        Discover,
        Rebind,
        Apply
    }

    private readonly record struct OperationStart(
        long Version,
        OperationKind Kind,
        CancellationTokenSource Cancellation,
        InteractiveSession Session,
        string LogicalRole,
        CameraProviderIdentity? Provider,
        CameraDeviceDescriptor? Device,
        long ExpectedBindingRevision,
        string? ExpectedBindingRevisionHash,
        RequestedCameraConfiguration? Requested);

    private static readonly ReadOnlyCollection<ProductionAcquisitionMode> AcquisitionModesValue =
        new(Enum.GetValues<ProductionAcquisitionMode>());
    private static readonly ReadOnlyCollection<VisionPixelFormat> PixelFormatsValue =
        new(Enum.GetValues<VisionPixelFormat>());
    private static readonly ReadOnlyCollection<int?> ValidBitsOptionsValue =
        new(new int?[] { null, 10, 12, 16 });

    private readonly ICameraSetupRuntime? _runtime;
    private readonly IInteractiveSessionService? _sessions;
    private readonly IStepUpAuthentication? _stepUp;
    private readonly IUiDispatcher _dispatcher;
    private readonly object _sync = new();
    private readonly ReadOnlyCollection<CameraProviderIdentity> _providers;
    private readonly ObservableCollection<CameraDeviceDescriptor> _devices = new();
    private readonly ReadOnlyObservableCollection<CameraDeviceDescriptor> _readOnlyDevices;
    private CancellationTokenSource? _activeCancellation;
    private long _operationVersion;
    private InteractiveSession _session;
    private CameraProviderIdentity? _selectedProvider;
    private CameraDeviceDescriptor? _selectedDevice;
    private CameraSetupSnapshot? _setup;
    private CameraSetupOperationResult? _lastOperationResult;
    private StepUpBinding? _lastStepUpBinding;
    private string _logicalRole = "Primary";
    private long _expectedBindingRevision;
    private string? _expectedBindingRevisionHash;
    private string _changeReason = "维护相机配置";
    private ProductionAcquisitionMode _acquisitionMode = ProductionAcquisitionMode.SoftwareTrigger;
    private VisionPixelFormat _pixelFormat = VisionPixelFormat.Mono8;
    private int? _validBits;
    private string _exposureTimeUsText = "100";
    private string _gainDbText = "0";
    private string _roiOffsetXText = "0";
    private string _roiOffsetYText = "0";
    private string _roiWidthText = "1";
    private string _roiHeightText = "1";
    private string _acquisitionTimeoutMsText = "1000";
    private string _triggerDelayUsText = "0";
    private string _whiteBalanceRedText = string.Empty;
    private string _whiteBalanceGreenText = string.Empty;
    private string _whiteBalanceBlueText = string.Empty;
    private string _statusMessage;
    private string? _errorCode;
    private string? _inputErrorCode;
    private bool _isBusy;
    private bool _disposed;

    public CameraSetupViewModel(ICameraSetupRuntime? runtime,
        IInteractiveSessionService? sessions, IStepUpAuthentication? stepUp = null,
        IUiDispatcher? dispatcher = null)
    {
        _runtime = runtime;
        _sessions = sessions;
        _stepUp = stepUp;
        _dispatcher = dispatcher ?? new DispatcherUiDispatcher();
        _readOnlyDevices = new ReadOnlyObservableCollection<CameraDeviceDescriptor>(_devices);
        _session = sessions?.Current ?? UnauthenticatedSession;
        _providers = CopyProviders(runtime?.Providers);
        _statusMessage = IsConfigured
            ? "请选择逻辑相机角色和已登记的 Provider，然后读取当前设置。"
            : "相机设置不可用：未配置受限设置服务。";
        _errorCode = IsConfigured ? null : "CameraSetupRuntimeUnavailable";

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => CanRefresh);
        DiscoverCommand = new AsyncRelayCommand(() => DiscoverAsync(), () => CanDiscover);
        RebindCommand = new AsyncRelayCommand(() => RebindAsync(string.Empty), () => CanRebind);
        ApplyCommand = new AsyncRelayCommand(() => ApplyDebugConfigurationAsync(string.Empty), () => CanApply);

        if (_sessions is not null)
            _sessions.Changed += SessionChanged;
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand DiscoverCommand { get; }
    public AsyncRelayCommand RebindCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }

    /// <summary>
    /// Raised on the configured UI dispatcher after the interactive session
    /// changes.  The panel uses this narrow notification to clear its native
    /// PasswordBox; the VM itself never stores that password.
    /// </summary>
    internal event EventHandler? SessionInvalidated;

    public bool IsConfigured => _runtime is not null;

    public bool IsBusy
    {
        get { lock (_sync) return _isBusy; }
    }

    public bool IsAuthenticated => IsUsableSession(CurrentSession);

    public InteractiveSession CurrentSession => _sessions?.Current ?? ReadSession();

    public IReadOnlyList<CameraProviderIdentity> Providers => _providers;
    public ReadOnlyObservableCollection<CameraDeviceDescriptor> Devices => _readOnlyDevices;

    /// <summary>Provider selection is intentionally empty until the user chooses one.</summary>
    public CameraProviderIdentity? SelectedProvider
    {
        get { lock (_sync) return _selectedProvider; }
        set
        {
            lock (_sync)
            {
                if (Equals(_selectedProvider, value)) return;
                if (value is not null && !_providers.Contains(value))
                {
                    SetInputErrorLocked("CameraSetupProviderSelectionInvalid");
                    NotifyStateChangedOnUi();
                    return;
                }

                _selectedProvider = value;
                _selectedDevice = null;
                _devices.Clear();
                ClearInputErrorLocked();
            }

            NotifyStateChangedOnUi();
        }
    }

    /// <summary>Device selection is explicit; discovery never selects the first result.</summary>
    public CameraDeviceDescriptor? SelectedDevice
    {
        get { lock (_sync) return _selectedDevice; }
        set
        {
            lock (_sync)
            {
                if (ReferenceEquals(_selectedDevice, value)) return;
                if (value is not null &&
                    (_selectedProvider is null || !Equals(value.Provider, _selectedProvider) ||
                     !_devices.Contains(value)))
                {
                    SetInputErrorLocked("CameraSetupDeviceSelectionInvalid");
                    NotifyStateChangedOnUi();
                    return;
                }

                _selectedDevice = value;
                ClearInputErrorLocked();
            }

            NotifyStateChangedOnUi();
        }
    }

    public string LogicalRole
    {
        get { lock (_sync) return _logicalRole; }
        set => SetIdentifier(ref _logicalRole, value, nameof(LogicalRole), "CameraSetupLogicalRoleInvalid");
    }

    public long ExpectedBindingRevision
    {
        get { lock (_sync) return _expectedBindingRevision; }
        set
        {
            lock (_sync)
            {
                if (value < 0)
                {
                    SetInputErrorLocked("CameraSetupBindingRevisionInvalid");
                    NotifyStateChangedOnUi();
                    return;
                }

                if (_expectedBindingRevision == value) return;
                _expectedBindingRevision = value;
                ClearInputErrorLocked();
            }

            NotifyStateChangedOnUi();
        }
    }

    public string? ExpectedBindingRevisionHash
    {
        get { lock (_sync) return _expectedBindingRevisionHash; }
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value;
            lock (_sync)
            {
                if (normalized is not null && !IsUpperSha256(normalized))
                {
                    SetInputErrorLocked("CameraSetupBindingRevisionHashInvalid");
                    NotifyStateChangedOnUi();
                    return;
                }

                if (string.Equals(_expectedBindingRevisionHash, normalized, StringComparison.Ordinal)) return;
                _expectedBindingRevisionHash = normalized;
                ClearInputErrorLocked();
            }

            NotifyStateChangedOnUi();
        }
    }

    public string ChangeReason
    {
        get { lock (_sync) return _changeReason; }
        set => SetBoundedText(ref _changeReason, value, nameof(ChangeReason), 128,
            "CameraSetupChangeReasonInvalid");
    }

    public IReadOnlyList<ProductionAcquisitionMode> AcquisitionModes => AcquisitionModesValue;
    public IReadOnlyList<VisionPixelFormat> PixelFormats => PixelFormatsValue;
    public IReadOnlyList<int?> ValidBitsOptions => ValidBitsOptionsValue;

    public ProductionAcquisitionMode AcquisitionMode
    {
        get { lock (_sync) return _acquisitionMode; }
        set => SetEnum(ref _acquisitionMode, value, "CameraSetupAcquisitionModeInvalid");
    }

    public VisionPixelFormat PixelFormat
    {
        get { lock (_sync) return _pixelFormat; }
        set => SetEnum(ref _pixelFormat, value, "CameraSetupPixelFormatInvalid");
    }

    public int? ValidBits
    {
        get { lock (_sync) return _validBits; }
        set
        {
            if (value is not null && value is not (10 or 12 or 16))
            {
                lock (_sync) SetInputErrorLocked("CameraSetupValidBitsInvalid");
                NotifyStateChangedOnUi();
                return;
            }

            lock (_sync)
            {
                if (_validBits == value) return;
                _validBits = value;
                ClearInputErrorLocked();
            }

            NotifyStateChangedOnUi();
        }
    }

    public string ExposureTimeUsText
    {
        get { lock (_sync) return _exposureTimeUsText; }
        set => SetBoundedText(ref _exposureTimeUsText, value, nameof(ExposureTimeUsText), 64,
            "CameraSetupNumericInputInvalid");
    }

    public string GainDbText
    {
        get { lock (_sync) return _gainDbText; }
        set => SetBoundedText(ref _gainDbText, value, nameof(GainDbText), 64,
            "CameraSetupNumericInputInvalid");
    }

    public string RoiOffsetXText
    {
        get { lock (_sync) return _roiOffsetXText; }
        set => SetBoundedText(ref _roiOffsetXText, value, nameof(RoiOffsetXText), 32,
            "CameraSetupNumericInputInvalid");
    }

    public string RoiOffsetYText
    {
        get { lock (_sync) return _roiOffsetYText; }
        set => SetBoundedText(ref _roiOffsetYText, value, nameof(RoiOffsetYText), 32,
            "CameraSetupNumericInputInvalid");
    }

    public string RoiWidthText
    {
        get { lock (_sync) return _roiWidthText; }
        set => SetBoundedText(ref _roiWidthText, value, nameof(RoiWidthText), 32,
            "CameraSetupNumericInputInvalid");
    }

    public string RoiHeightText
    {
        get { lock (_sync) return _roiHeightText; }
        set => SetBoundedText(ref _roiHeightText, value, nameof(RoiHeightText), 32,
            "CameraSetupNumericInputInvalid");
    }

    public string AcquisitionTimeoutMsText
    {
        get { lock (_sync) return _acquisitionTimeoutMsText; }
        set => SetBoundedText(ref _acquisitionTimeoutMsText, value, nameof(AcquisitionTimeoutMsText), 32,
            "CameraSetupNumericInputInvalid");
    }

    public string TriggerDelayUsText
    {
        get { lock (_sync) return _triggerDelayUsText; }
        set => SetBoundedText(ref _triggerDelayUsText, value, nameof(TriggerDelayUsText), 64,
            "CameraSetupNumericInputInvalid");
    }

    public string WhiteBalanceRedText
    {
        get { lock (_sync) return _whiteBalanceRedText; }
        set => SetBoundedText(ref _whiteBalanceRedText, value, nameof(WhiteBalanceRedText), 64,
            "CameraSetupNumericInputInvalid");
    }

    public string WhiteBalanceGreenText
    {
        get { lock (_sync) return _whiteBalanceGreenText; }
        set => SetBoundedText(ref _whiteBalanceGreenText, value, nameof(WhiteBalanceGreenText), 64,
            "CameraSetupNumericInputInvalid");
    }

    public string WhiteBalanceBlueText
    {
        get { lock (_sync) return _whiteBalanceBlueText; }
        set => SetBoundedText(ref _whiteBalanceBlueText, value, nameof(WhiteBalanceBlueText), 64,
            "CameraSetupNumericInputInvalid");
    }

    public CameraSetupSnapshot? CurrentSetup
    {
        get { lock (_sync) return _setup; }
    }

    public CameraBindingRevision? CurrentBinding => CurrentSetup?.Binding;
    public CameraBindingTarget? CurrentBindingTarget => CurrentBinding?.Target;
    public CameraHealthSnapshot? CurrentHealth => CurrentSetup?.Health;
    public RequestedCameraConfiguration? RequestedConfiguration => CurrentSetup?.Requested;
    public EffectiveCameraConfiguration? EffectiveConfiguration => CurrentSetup?.Effective;
    public IReadOnlyList<CameraConfigurationDifference> ConfigurationDifferences =>
        CurrentSetup?.Differences ?? Array.Empty<CameraConfigurationDifference>();
    public CameraCapabilities? Capabilities => CurrentSetup?.Capabilities;
    public CameraProviderExtensionRequirement? Extension => CurrentSetup?.Extension;

    public CameraProviderAvailability? ProviderAvailability => CurrentHealth?.ProviderAvailability;
    public CameraConnectionState? Connection => CurrentHealth?.Connection;
    public CameraConfigurationState? Configuration => CurrentHealth?.Configuration;
    public CameraAcquisitionState? Acquisition => CurrentHealth?.Acquisition;
    public CameraFault? LastFault => CurrentHealth?.LastFault;
    public string? FaultReasonCode => LastFault?.ReasonCode;
    public string ProviderAvailabilityLabel => ProviderAvailability?.ToString() ?? "Unknown";
    public string ConnectionLabel => Connection?.ToString() ?? "Unknown";
    public string ConfigurationLabel => Configuration?.ToString() ?? "Unknown";
    public string AcquisitionLabel => Acquisition?.ToString() ?? "Unknown";
    public string BuffersLabel => CurrentSetup is null ? "Unknown" : "由 Runtime 独立投影";
    public string FaultLabel => LastFault is null
        ? "无已报告故障"
        : $"{LastFault.Classification} · {SafeReason(LastFault.ReasonCode, "CameraFaultUnknown")}";
    public string BindingSummary => CurrentBinding is null
        ? "当前未读取到相机绑定。"
        : $"{CurrentBinding.Target.Provider.Id}/{CurrentBinding.Target.StableDeviceIdentity} · revision {CurrentBinding.Revision}";
    public string RequestedSummary => FormatConfiguration(CurrentSetup?.Requested);
    public string EffectiveSummary => FormatConfiguration(CurrentSetup?.Effective);
    public string DifferencesSummary => CurrentSetup is null
        ? "尚未读取配置差异。"
        : CurrentSetup.Differences.Count == 0
            ? "无数值差异。"
            : string.Join("；", CurrentSetup.Differences.Select(difference =>
                $"{difference.Setting}: {Number(difference.Requested)} → {Number(difference.Effective)}"));

    public bool ExtensionUnsupported => true;
    public string ExtensionStatus => "Provider 扩展配置在当前页面不支持；请使用显式受支持的版本。";
    public bool RequiresRecipeActivation => true;
    public bool ProductionReady => false;

    public CameraSetupOperationResult? LastOperationResult
    {
        get { lock (_sync) return _lastOperationResult; }
    }

    public string AuditStatus => LastOperationResult?.Audit.ToString() ?? "NotAttempted";

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
        ? "配置字段会在 Runtime 受限边界再次完整校验；页面不会自动修改或声明 Ready。"
        : "当前输入无效，请修正标出的字段后重试。";

    public bool HasSelectedProvider => SelectedProvider is not null;
    public bool HasSelectedDevice => SelectedDevice is not null;
    public int CandidateCount => Devices.Count;
    public bool HasSetup => CurrentSetup is not null;

    public bool CanRefresh => IsConfigured && !IsBusy && !_disposed;
    public bool CanDiscover => IsConfigured && IsAuthenticated && SelectedProvider is not null &&
        !IsBusy && !_disposed;
    public bool CanRebind => IsConfigured && IsAuthenticated && _stepUp is not null &&
        HasSetup && SelectedDevice is not null && !IsBusy && !_disposed &&
        IsInputUsable() && !string.IsNullOrWhiteSpace(ChangeReason);
    public bool CanApply => IsConfigured && IsAuthenticated && _stepUp is not null &&
        HasSetup && !IsBusy && !_disposed && IsInputUsable() &&
        TryBuildRequestedConfiguration(out _, out _, false) && !string.IsNullOrWhiteSpace(ChangeReason);

    /// <summary>Reads the current binding and independent health axes only.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_runtime is null)
        {
            ApplyUnavailable("CameraSetupRuntimeUnavailable", "相机设置服务未配置。");
            return;
        }

        var start = Begin(OperationKind.Refresh, cancellationToken);
        if (!start.HasValue) return;

        try
        {
            if (!IsUsableSession(start.Value.Session))
            {
                ApplyFailure(start.Value, "CameraSetupAuthenticationRequired",
                    "请先登录，再读取当前相机设置。", clearSetup: true);
                return;
            }

            CameraSetupQueryResult result;
            try
            {
                result = await _runtime.GetSetupAsync(start.Value.LogicalRole,
                    CreateInvocation(start.Value.Session), start.Value.Cancellation.Token)
                    .ConfigureAwait(true);
                start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
            {
                ApplyCancellation(start.Value);
                return;
            }
            catch
            {
                ApplyFailure(start.Value, "CameraSetupQueryFailed",
                    "当前相机设置读取失败，请重试。", clearSetup: true);
                return;
            }

            await _dispatcher.InvokeAsync(() => ApplyQueryResult(start.Value, result))
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            ApplyCancellation(start.Value);
        }
        catch
        {
            ApplyFailure(start.Value, "CameraSetupQueryFailed",
                "当前相机设置读取失败，请重试。", clearSetup: true);
        }
        finally
        {
            Complete(start.Value);
        }
    }

    /// <summary>Reads candidates for the explicitly selected registered Provider.</summary>
    public async Task DiscoverAsync(CancellationToken cancellationToken = default)
    {
        CameraProviderIdentity? provider;
        lock (_sync) provider = _selectedProvider;
        if (provider is null)
        {
            ApplyInputFailure("CameraSetupProviderSelectionRequired",
                "请先明确选择已登记的相机 Provider，再读取候选设备。", clearDevices: false);
            return;
        }
        if (_runtime is null)
        {
            ApplyUnavailable("CameraSetupRuntimeUnavailable", "相机设置服务未配置。");
            return;
        }

        var start = Begin(OperationKind.Discover, cancellationToken, provider: provider);
        if (!start.HasValue) return;

        try
        {
            if (!IsUsableSession(start.Value.Session))
            {
                ApplyFailure(start.Value, "CameraSetupAuthenticationRequired",
                    "请先登录，再读取相机候选。", clearSetup: false);
                return;
            }

            CameraDiscoveryResult result;
            try
            {
                result = await _runtime.DiscoverAsync(provider,
                    CreateInvocation(start.Value.Session), start.Value.Cancellation.Token)
                    .ConfigureAwait(true);
                start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
            {
                ApplyCancellation(start.Value);
                return;
            }
            catch
            {
                ApplyFailure(start.Value, "CameraSetupDiscoveryFailed",
                    "相机候选读取失败，请重试。", clearSetup: false);
                return;
            }

            await _dispatcher.InvokeAsync(() => ApplyDiscoveryResult(start.Value, result))
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            ApplyCancellation(start.Value);
        }
        catch
        {
            ApplyFailure(start.Value, "CameraSetupDiscoveryFailed",
                "相机候选读取失败，请重试。", clearSetup: false);
        }
        finally
        {
            Complete(start.Value);
        }
    }

    /// <summary>Public convenience entry point for the read-only current setup query.</summary>
    public Task GetSetupAsync(CancellationToken cancellationToken = default) => RefreshAsync(cancellationToken);

    public Task<CameraSetupOperationResult?> RebindAsync(string stepUpPassword,
        CancellationToken cancellationToken = default)
    {
        CameraDeviceDescriptor? device;
        lock (_sync) device = _selectedDevice;
        if (device is null)
        {
            ApplyInputFailure("CameraSetupDeviceSelectionRequired",
                "请先明确选择要绑定的候选设备。", clearDevices: false);
            return Task.FromResult<CameraSetupOperationResult?>(null);
        }

        return ExecuteMutationAsync(OperationKind.Rebind, stepUpPassword, device,
            null, cancellationToken);
    }

    /// <summary>Explicit target overload for controlled consumers; it still requires Step-Up.</summary>
    public Task<CameraSetupOperationResult?> RebindAsync(CameraBindingTarget target,
        string stepUpPassword, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        CameraDeviceDescriptor? device;
        lock (_sync)
        {
            device = _selectedDevice;
            if (device is null || !Equals(device.Provider, target.Provider) ||
                !string.Equals(device.StableDeviceIdentity, target.StableDeviceIdentity,
                    StringComparison.Ordinal))
            {
                device = null;
            }
        }

        if (device is null)
        {
            ApplyInputFailure("CameraSetupDeviceSelectionRequired",
                "绑定目标必须来自当前已明确选择的候选设备。", clearDevices: false);
            return Task.FromResult<CameraSetupOperationResult?>(null);
        }

        return ExecuteMutationAsync(OperationKind.Rebind, stepUpPassword, device,
            target, cancellationToken);
    }

    public Task<CameraSetupOperationResult?> ApplyDebugConfigurationAsync(
        string stepUpPassword, CancellationToken cancellationToken = default)
    {
        if (!TryBuildRequestedConfiguration(out var requested, out _, true))
            return Task.FromResult<CameraSetupOperationResult?>(null);
        return ExecuteMutationAsync(OperationKind.Apply, stepUpPassword, null,
            null, cancellationToken, requested);
    }

    /// <summary>Short UI/consumer name for the explicit debug-configuration operation.</summary>
    public Task<CameraSetupOperationResult?> ApplyAsync(string stepUpPassword,
        CancellationToken cancellationToken = default) =>
        ApplyDebugConfigurationAsync(stepUpPassword, cancellationToken);

    /// <summary>Applies an explicit complete common configuration after Step-Up.</summary>
    public Task<CameraSetupOperationResult?> ApplyDebugConfigurationAsync(
        RequestedCameraConfiguration requested, string stepUpPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requested);
        return ExecuteMutationAsync(OperationKind.Apply, stepUpPassword, null,
            null, cancellationToken, requested);
    }

    /// <summary>
    /// Clears candidates, binding identity, read-back values, pending work, and
    /// operation metadata.  Passwords are never retained by this view model.
    /// </summary>
    public void ClearSensitiveInputs()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            _operationVersion++;
            cancellation = _activeCancellation;
            _activeCancellation = null;
            _isBusy = false;
            ClearSensitiveLocked();
            _lastOperationResult = null;
            _lastStepUpBinding = null;
            _errorCode = null;
            _inputErrorCode = null;
            _statusMessage = "相机候选和当前绑定已清除，请重新读取。";
        }

        cancellation?.Cancel();
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
                _statusMessage = "相机设置请求已取消，可重新操作。";
                _errorCode = null;
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
            ClearSensitiveLocked();
            _lastOperationResult = null;
            _lastStepUpBinding = null;
            _errorCode = null;
            _statusMessage = "相机设置面板已释放。";
        }

        cancellation?.Cancel();
        if (_sessions is not null)
            _sessions.Changed -= SessionChanged;
        NotifyStateChangedOnUi();
        await ValueTask.CompletedTask;
    }

    internal StepUpBinding? LastStepUpBinding
    {
        get { lock (_sync) return _lastStepUpBinding; }
    }

    private async Task<CameraSetupOperationResult?> ExecuteMutationAsync(
        OperationKind kind, string stepUpPassword, CameraDeviceDescriptor? device,
        CameraBindingTarget? explicitTarget, CancellationToken cancellationToken,
        RequestedCameraConfiguration? requested = null)
    {
        if (_runtime is null || _sessions is null || _stepUp is null)
        {
            ApplyUnavailable("CameraSetupOperationUnavailable", "相机设置操作不可用：未配置登录、确认或 Runtime 服务。");
            return null;
        }
        if (string.IsNullOrWhiteSpace(stepUpPassword))
        {
            ApplyInputFailure("CameraSetupStepUpPasswordRequired",
                "此操作必须输入当前密码再次确认。", clearDevices: false);
            return null;
        }

        if (requested is null && kind == OperationKind.Apply)
        {
            ApplyInputFailure("CameraSetupConfigurationInvalid",
                "请求配置不完整或包含无效值。", clearDevices: false);
            return null;
        }

        CameraBindingTarget? target = explicitTarget;
        if (kind == OperationKind.Rebind && target is null && device is not null)
            target = new CameraBindingTarget(device.Provider, device.StableDeviceIdentity);

        InteractiveSession session;
        string role;
        long expectedRevision;
        string? expectedHash;
        string changeReason;
        CameraProviderIdentity? provider;
        CameraDeviceDescriptor? selectedDevice;
        bool hasSetup;
        lock (_sync)
        {
            session = _sessions.Current;
            role = _logicalRole;
            expectedRevision = _expectedBindingRevision;
            expectedHash = _expectedBindingRevisionHash;
            changeReason = _changeReason;
            provider = _selectedProvider;
            selectedDevice = _selectedDevice;
            hasSetup = _setup is not null;
        }

        if (!IsUsableSession(session))
        {
            ApplyUnavailable("CameraSetupAuthenticationRequired", "请先登录，再执行相机设置操作。");
            return null;
        }
        if (!IsInputUsable() || string.IsNullOrWhiteSpace(changeReason))
        {
            ApplyInputFailure("CameraSetupInputInvalid",
                "请先修正逻辑角色、版本条件和变更原因。", clearDevices: false);
            return null;
        }
        if (!hasSetup)
        {
            ApplyInputFailure("CameraSetupQueryRequired",
                "请先读取当前相机设置，再执行受保护操作。", clearDevices: false);
            return null;
        }
        if (kind == OperationKind.Rebind && target is null)
        {
            ApplyInputFailure("CameraSetupDeviceSelectionRequired",
                "请先明确选择绑定目标。", clearDevices: false);
            return null;
        }

        // Keep the explicitly selected device as operation context for both
        // mutations.  Apply has no device field in its public request, but a
        // selection change while Step-Up is pending must still invalidate its
        // result instead of applying a response to a different page context.
        var operationDevice = device ?? selectedDevice;
        var start = Begin(kind, cancellationToken, session, role,
            provider, operationDevice, expectedRevision, expectedHash, requested);
        if (!start.HasValue) return null;

        try
        {
            var operationId = Guid.NewGuid();
            var commandKind = kind == OperationKind.Rebind
                ? AuditedCommandKind.RebindCamera
                : AuditedCommandKind.ApplyCameraDebugConfiguration;
            var binding = new StepUpBinding(Permission.ManageCameraBindings,
                operationId, role, commandKind);
            lock (_sync) _lastStepUpBinding = binding;
            NotifyStateChangedOnUi();

            StepUpResult stepUpResult;
            try
            {
                stepUpResult = await _stepUp.ReauthenticateAsync(
                    new StepUpRequest(operationId, CreateInvocation(session), binding,
                        stepUpPassword), start.Value.Cancellation.Token).ConfigureAwait(true);
            }
            finally
            {
                // The supplied secret is never assigned to a field or retained by this VM.
                stepUpPassword = string.Empty;
            }

            start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            if (!stepUpResult.Succeeded || stepUpResult.GrantId is null)
            {
                ApplyFailure(start.Value, "StepUpAuthenticationRejected",
                    "当前密码确认未通过；本次相机设置未提交。", clearSetup: false);
                return null;
            }

            var currentSession = _sessions.Current;
            if (!SameSession(session, currentSession))
            {
                ApplyFailure(start.Value, "CameraSetupSessionChanged",
                    "会话已变化；本次相机设置未提交，请重新读取并确认。", clearSetup: true);
                return null;
            }

            CameraSetupOperationResult result;
            try
            {
                var invocation = CreateInvocation(currentSession, stepUpResult.GrantId);
                if (kind == OperationKind.Rebind)
                {
                    result = await _runtime.RebindAsync(new CameraRebindRequest(
                        operationId, invocation, role, expectedRevision, expectedHash,
                        target!, changeReason), start.Value.Cancellation.Token)
                        .ConfigureAwait(true);
                }
                else
                {
                    result = await _runtime.ApplyDebugConfigurationAsync(
                        new CameraDebugConfigurationRequest(operationId, invocation, role,
                            expectedRevision, expectedHash, requested!, changeReason),
                        start.Value.Cancellation.Token).ConfigureAwait(true);
                }
                start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
            {
                ApplyCancellation(start.Value);
                return null;
            }
            catch
            {
                ApplyFailure(start.Value, "CameraSetupOperationFailed",
                    "相机设置操作失败，请重新读取后重试。", clearSetup: false);
                return null;
            }

            await _dispatcher.InvokeAsync(() => ApplyOperationResult(start.Value, result))
                .ConfigureAwait(true);
            return result;
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            ApplyCancellation(start.Value);
            return null;
        }
        catch
        {
            ApplyFailure(start.Value, "CameraSetupOperationFailed",
                "相机设置操作失败，请重新读取后重试。", clearSetup: false);
            return null;
        }
        finally
        {
            Complete(start.Value);
        }
    }

    private OperationStart? Begin(OperationKind kind, CancellationToken cancellationToken,
        InteractiveSession? session = null, string? logicalRole = null,
        CameraProviderIdentity? provider = null, CameraDeviceDescriptor? device = null,
        long expectedRevision = 0, string? expectedHash = null,
        RequestedCameraConfiguration? requested = null)
    {
        OperationStart start;
        lock (_sync)
        {
            if (_disposed || _isBusy) return null;
            var activeSession = session ?? (_sessions?.Current ?? _session);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCancellation = linked;
            _operationVersion++;
            _isBusy = true;
            _errorCode = null;
            _statusMessage = kind switch
            {
                OperationKind.Refresh => "正在读取当前相机设置…",
                OperationKind.Discover => "正在读取已选 Provider 的候选设备…",
                OperationKind.Rebind => "正在等待当前密码确认并提交改绑…",
                OperationKind.Apply => "正在等待当前密码确认并提交配置…",
                _ => "正在处理相机设置…"
            };
            start = new OperationStart(_operationVersion, kind, linked, activeSession,
                logicalRole ?? _logicalRole, provider ?? _selectedProvider, device,
                expectedRevision, expectedHash, requested);
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

    private void ApplyQueryResult(OperationStart start, CameraSetupQueryResult result)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start) || !SameSession(start.Session, CurrentSession) ||
                !string.Equals(_logicalRole, start.LogicalRole, StringComparison.Ordinal)) return;

            if (!result.Available || result.Snapshot is null)
            {
                ClearSetupLocked();
                _errorCode = SafeReason(result.ReasonCode, "CameraSetupUnavailable");
                _statusMessage = "当前相机设置不可用；请修正状态后重试。";
            }
            else if (!string.Equals(result.Snapshot.LogicalRole, start.LogicalRole,
                         StringComparison.Ordinal))
            {
                ClearSetupLocked();
                _errorCode = "CameraSetupResultInvalid";
                _statusMessage = "相机设置结果与当前逻辑角色不一致，状态已锁定。";
            }
            else
            {
                ApplySnapshotLocked(result.Snapshot);
                _errorCode = null;
                _statusMessage = BuildSetupStatus(result.Snapshot,
                    "当前相机设置已读取；连接、配置和采集状态分别显示。生产准入仍需后续配方激活。");
            }
        }

        NotifyStateChangedOnUi();
    }

    private void ApplyDiscoveryResult(OperationStart start, CameraDiscoveryResult result)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start) || !SameSession(start.Session, CurrentSession) ||
                !Equals(_selectedProvider, start.Provider)) return;

            if (!result.Succeeded)
            {
                _devices.Clear();
                _selectedDevice = null;
                _errorCode = SafeReason(result.ReasonCode, "CameraSetupDiscoveryUnavailable");
                _statusMessage = "候选设备读取未完成，请修正 Provider 状态后重试。";
            }
            else if (result.Devices.Any(device => !Equals(device.Provider, start.Provider)) ||
                     result.Devices.Select(device => device.StableDeviceIdentity)
                         .Distinct(StringComparer.Ordinal).Count() != result.Devices.Count)
            {
                _devices.Clear();
                _selectedDevice = null;
                _errorCode = "CameraSetupDiscoveryInvalid";
                _statusMessage = "候选设备结果无效，页面不会自动选择设备。";
            }
            else
            {
                _devices.Clear();
                foreach (var device in result.Devices) _devices.Add(device);
                _selectedDevice = null;
                _errorCode = null;
                _statusMessage = result.Devices.Count == 0
                    ? "当前 Provider 没有可选择的候选设备。"
                    : "候选设备已读取；请明确选择目标后再进行改绑。";
            }
        }

        NotifyStateChangedOnUi();
    }

    private void ApplyOperationResult(OperationStart start, CameraSetupOperationResult result)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start) || !SameSession(start.Session, CurrentSession) ||
                !string.Equals(_logicalRole, start.LogicalRole, StringComparison.Ordinal) ||
                !Equals(_selectedProvider, start.Provider) ||
                !ReferenceEquals(_selectedDevice, start.Device) ||
                _expectedBindingRevision != start.ExpectedBindingRevision ||
                !string.Equals(_expectedBindingRevisionHash, start.ExpectedBindingRevisionHash,
                    StringComparison.Ordinal)) return;

            _lastOperationResult = result;
            var hasMatchingSnapshot = result.Snapshot is not null &&
                string.Equals(result.Snapshot.LogicalRole, _logicalRole, StringComparison.Ordinal);

            if (!result.Succeeded)
            {
                // A matching failure snapshot is still useful read-back: it
                // can describe Closed/Unknown/Stopped axes and a missing
                // effective configuration.  Preserve that explicit failure
                // projection; only an absent or role-mismatched snapshot must
                // clear the previous current view.
                if (hasMatchingSnapshot)
                    ApplySnapshotLocked(result.Snapshot!);
                else
                    ClearSetupLocked();
                _errorCode = SafeReason(result.ReasonCode, "CameraSetupOperationRejected");
                _statusMessage = "相机设置操作未完成；请修正条件后重试。";
            }
            else if (!hasMatchingSnapshot)
            {
                // A success without a role-matching read-back is not a usable
                // current projection.  Never leave the old setup visible or
                // show the success text in that case.
                ClearSetupLocked();
                _errorCode = "CameraSetupOperationResultInvalid";
                _statusMessage = "相机设置操作返回的读回无效；请重新读取后重试。";
            }
            else
            {
                ApplySnapshotLocked(result.Snapshot!);
                _errorCode = null;
                _statusMessage = result.Audit == AuditPersistence.Persisted
                    ? "相机设置操作已提交并记录；请按后续流程重新激活配方。"
                    : "相机设置操作已返回，但审计状态不可用；生产准入仍保持关闭。";
            }
        }

        NotifyStateChangedOnUi();
    }

    private void ApplySnapshotLocked(CameraSetupSnapshot snapshot)
    {
        _setup = snapshot;
        if (snapshot.Binding is not null)
        {
            _expectedBindingRevision = snapshot.Binding.Revision;
            _expectedBindingRevisionHash = snapshot.Binding.RevisionHash;
        }
        else
        {
            _expectedBindingRevision = 0;
            _expectedBindingRevisionHash = null;
        }

        if (snapshot.Requested is not null)
            LoadRequestedLocked(snapshot.Requested);
    }

    private void LoadRequestedLocked(RequestedCameraConfiguration requested)
    {
        _acquisitionMode = requested.ProductionAcquisitionMode;
        _pixelFormat = requested.PixelFormat;
        _validBits = requested.ValidBits;
        _exposureTimeUsText = Number(requested.ExposureTimeUs);
        _gainDbText = Number(requested.GainDb);
        _roiOffsetXText = requested.RegionOfInterest.OffsetX.ToString(CultureInfo.InvariantCulture);
        _roiOffsetYText = requested.RegionOfInterest.OffsetY.ToString(CultureInfo.InvariantCulture);
        _roiWidthText = requested.RegionOfInterest.Width.ToString(CultureInfo.InvariantCulture);
        _roiHeightText = requested.RegionOfInterest.Height.ToString(CultureInfo.InvariantCulture);
        _acquisitionTimeoutMsText = requested.AcquisitionTimeoutMs.ToString(CultureInfo.InvariantCulture);
        _triggerDelayUsText = Number(requested.TriggerDelayUs);
        _whiteBalanceRedText = requested.WhiteBalanceRgb is null ? string.Empty : Number(requested.WhiteBalanceRgb.Red);
        _whiteBalanceGreenText = requested.WhiteBalanceRgb is null ? string.Empty : Number(requested.WhiteBalanceRgb.Green);
        _whiteBalanceBlueText = requested.WhiteBalanceRgb is null ? string.Empty : Number(requested.WhiteBalanceRgb.Blue);
        ClearInputErrorLocked();
    }

    private bool TryBuildRequestedConfiguration(out RequestedCameraConfiguration? requested,
        out string reason, bool reportError)
    {
        requested = null;
        reason = "CameraSetupConfigurationInvalid";
        string exposureText;
        string gainText;
        string offsetXText;
        string offsetYText;
        string widthText;
        string heightText;
        string timeoutText;
        string triggerText;
        string redText;
        string greenText;
        string blueText;
        ProductionAcquisitionMode mode;
        VisionPixelFormat format;
        int? validBits;
        lock (_sync)
        {
            if (_inputErrorCode is not null)
            {
                reason = _inputErrorCode;
                if (reportError) SetInputErrorLocked(reason);
                return false;
            }

            exposureText = _exposureTimeUsText;
            gainText = _gainDbText;
            offsetXText = _roiOffsetXText;
            offsetYText = _roiOffsetYText;
            widthText = _roiWidthText;
            heightText = _roiHeightText;
            timeoutText = _acquisitionTimeoutMsText;
            triggerText = _triggerDelayUsText;
            redText = _whiteBalanceRedText;
            greenText = _whiteBalanceGreenText;
            blueText = _whiteBalanceBlueText;
            mode = _acquisitionMode;
            format = _pixelFormat;
            validBits = _validBits;
        }

        if (!TryDouble(exposureText, out var exposure) ||
            !TryDouble(gainText, out var gain) ||
            !TryInt(offsetXText, out var offsetX) ||
            !TryInt(offsetYText, out var offsetY) ||
            !TryInt(widthText, out var width) ||
            !TryInt(heightText, out var height) ||
            !TryInt(timeoutText, out var timeout) ||
            !TryDouble(triggerText, out var triggerDelay))
        {
            reason = "CameraSetupNumericInputInvalid";
            if (reportError) ApplyInputFailure(reason, "相机数值字段必须是完整的有限数字。", clearDevices: false);
            return false;
        }

        WhiteBalanceRgb? whiteBalance = null;
        var hasRed = !string.IsNullOrWhiteSpace(redText);
        var hasGreen = !string.IsNullOrWhiteSpace(greenText);
        var hasBlue = !string.IsNullOrWhiteSpace(blueText);
        if (hasRed || hasGreen || hasBlue)
        {
            if (format != VisionPixelFormat.Bgr24 || !hasRed || !hasGreen || !hasBlue ||
                !TryDouble(redText, out var red) || !TryDouble(greenText, out var green) ||
                !TryDouble(blueText, out var blue))
            {
                reason = "CameraSetupWhiteBalanceInvalid";
                if (reportError) ApplyInputFailure(reason,
                    "白平衡只适用于 Bgr24，且 R/G/B 必须完整填写。", clearDevices: false);
                return false;
            }

            try { whiteBalance = new WhiteBalanceRgb(red, green, blue); }
            catch (ArgumentException)
            {
                reason = "CameraSetupWhiteBalanceInvalid";
                if (reportError) ApplyInputFailure(reason,
                    "白平衡通道必须是有限的正数。", clearDevices: false);
                return false;
            }
        }

        try
        {
            requested = new RequestedCameraConfiguration(mode, exposure, gain,
                new RegionOfInterest(offsetX, offsetY, width, height), format, validBits,
                timeout, triggerDelay, whiteBalance);
            reason = "CameraConfigurationBuilt";
            return true;
        }
        catch (ArgumentException)
        {
            reason = "CameraSetupConfigurationInvalid";
            if (reportError) ApplyInputFailure(reason,
                "请求相机配置不完整或超出公共字段范围。", clearDevices: false);
            requested = null;
            return false;
        }
    }

    private bool IsInputUsable()
    {
        lock (_sync)
        {
            return _inputErrorCode is null && IsSafeIdentifier(_logicalRole) &&
                _expectedBindingRevision >= 0 &&
                (_expectedBindingRevisionHash is null || IsUpperSha256(_expectedBindingRevisionHash));
        }
    }

    private void SetIdentifier(ref string field, string? value, string propertyName, string reason)
    {
        var normalized = value ?? string.Empty;
        lock (_sync)
        {
            if (!IsSafeIdentifier(normalized))
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

    private void SetBoundedText(ref string field, string? value, string propertyName,
        int maximumLength, string reason)
    {
        var normalized = value ?? string.Empty;
        lock (_sync)
        {
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

    private void SetEnum<T>(ref T field, T value, string reason) where T : struct, Enum
    {
        lock (_sync)
        {
            if (!Enum.IsDefined(typeof(T), value))
            {
                SetInputErrorLocked(reason);
                NotifyStateChangedOnUi();
                return;
            }

            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            ClearInputErrorLocked();
        }

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

    private void ApplyInputFailure(string reason, string status, bool clearDevices)
    {
        lock (_sync)
        {
            _inputErrorCode = SafeReason(reason, "CameraSetupInputInvalid");
            _errorCode = _inputErrorCode;
            _statusMessage = status;
            if (clearDevices)
            {
                _devices.Clear();
                _selectedDevice = null;
            }
        }

        NotifyStateChangedOnUi();
    }

    private void ApplyUnavailable(string reason, string status)
    {
        lock (_sync)
        {
            _errorCode = SafeReason(reason, "CameraSetupUnavailable");
            _statusMessage = status;
            _setup = null;
            _lastOperationResult = null;
        }

        NotifyStateChangedOnUi();
    }

    private void ApplyFailure(OperationStart start, string reason, string status, bool clearSetup)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            if (clearSetup) ClearSetupLocked();
            _errorCode = SafeReason(reason, "CameraSetupOperationFailed");
            _statusMessage = status;
        }

        NotifyStateChangedOnUi();
    }

    private void ApplyCancellation(OperationStart start)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(start)) return;
            _errorCode = null;
            _statusMessage = "相机设置请求已取消，可重新操作。";
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
            _session = _sessions?.Current ?? UnauthenticatedSession;
            ClearSensitiveLocked();
            _lastOperationResult = null;
            _lastStepUpBinding = null;
            _inputErrorCode = null;
            _errorCode = "CameraSetupSessionChanged";
            _statusMessage = "会话已变化；相机候选和绑定身份已清除，请重新读取。";
        }

        cancellation?.Cancel();
        NotifySessionInvalidatedOnUi();
        NotifyStateChangedOnUi();
    }

    private void ClearSensitiveLocked()
    {
        _devices.Clear();
        _selectedProvider = null;
        _selectedDevice = null;
        _setup = null;
        _expectedBindingRevision = 0;
        _expectedBindingRevisionHash = null;
    }

    private void ClearSetupLocked()
    {
        _setup = null;
        _expectedBindingRevision = 0;
        _expectedBindingRevisionHash = null;
    }

    private bool IsCurrentLocked(OperationStart start) => !_disposed &&
        _operationVersion == start.Version &&
        ReferenceEquals(_activeCancellation, start.Cancellation);

    private bool SameSession(InteractiveSession left, InteractiveSession right) =>
        IsUsableSession(left) && IsUsableSession(right) &&
        left.SessionId == right.SessionId &&
        string.Equals(left.PrincipalId, right.PrincipalId, StringComparison.Ordinal);

    private static bool IsUsableSession(InteractiveSession session) =>
        session.State == InteractiveSessionState.Authenticated &&
        session.SessionId.HasValue && session.SessionId.Value != Guid.Empty &&
        !string.IsNullOrWhiteSpace(session.PrincipalId);

    private InteractiveSession ReadSession()
    {
        lock (_sync) return _session;
    }

    private static CommandInvocation CreateInvocation(InteractiveSession session, Guid? grantId = null) =>
        new(CommandSource.PhysicalConsole, session.PrincipalId, session.SessionId, grantId);

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
        DiscoverCommand.RaiseCanExecuteChanged();
        RebindCommand.RaiseCanExecuteChanged();
        ApplyCommand.RaiseCanExecuteChanged();
    }

    private string BuildSetupStatus(CameraSetupSnapshot snapshot, string suffix)
    {
        var binding = snapshot.Binding is null ? "未绑定" :
            $"已绑定 {snapshot.Binding.Target.Provider.Id}/{snapshot.Binding.Target.StableDeviceIdentity} · revision {snapshot.Binding.Revision}";
        return $"{binding}。{suffix}";
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string FormatConfiguration(EffectiveCameraConfiguration? configuration)
    {
        if (configuration is null) return "尚未读取。";
        var roi = configuration.RegionOfInterest;
        var whiteBalance = configuration.WhiteBalanceRgb is null
            ? "WB=无"
            : $"WB={Number(configuration.WhiteBalanceRgb.Red)}/{Number(configuration.WhiteBalanceRgb.Green)}/{Number(configuration.WhiteBalanceRgb.Blue)}";
        return $"{configuration.ProductionAcquisitionMode}; Exposure={Number(configuration.ExposureTimeUs)} μs; " +
            $"Gain={Number(configuration.GainDb)} dB; ROI={roi.OffsetX},{roi.OffsetY},{roi.Width},{roi.Height}; " +
            $"Pixel={configuration.PixelFormat}; ValidBits={configuration.ValidBits?.ToString(CultureInfo.InvariantCulture) ?? "无"}; " +
            $"Timeout={configuration.AcquisitionTimeoutMs} ms; TriggerDelay={Number(configuration.TriggerDelayUs)} μs; {whiteBalance}";
    }

    private static string FormatConfiguration(RequestedCameraConfiguration? configuration)
    {
        if (configuration is null) return "尚未读取。";
        var effective = new EffectiveCameraConfiguration(
            configuration.ProductionAcquisitionMode, configuration.ExposureTimeUs,
            configuration.GainDb, configuration.RegionOfInterest, configuration.PixelFormat,
            configuration.ValidBits, configuration.AcquisitionTimeoutMs,
            configuration.TriggerDelayUs, configuration.WhiteBalanceRgb);
        return FormatConfiguration(effective);
    }

    private static bool TryDouble(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        double.IsFinite(value);

    private static bool TryInt(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static ReadOnlyCollection<CameraProviderIdentity> CopyProviders(
        IReadOnlyList<CameraProviderIdentity>? providers)
    {
        if (providers is null)
            return new ReadOnlyCollection<CameraProviderIdentity>(Array.Empty<CameraProviderIdentity>());
        if (providers.Count > 64)
            throw new ArgumentException("CameraSetupProviderCapacityExceeded", nameof(providers));
        var copy = providers.ToArray();
        if (copy.Any(provider => provider is null) || copy.Distinct().Count() != copy.Length)
            throw new ArgumentException("CameraSetupProviderListInvalid", nameof(providers));
        return new ReadOnlyCollection<CameraProviderIdentity>(copy);
    }

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

    private static bool IsUpperSha256(string value)
    {
        if (value.Length != 64) return false;
        return value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
    }

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
