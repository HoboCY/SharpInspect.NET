using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Schema-generated authoring state for one non-production Recipe Draft.  The
/// view model owns only presentation values; the constrained IRecipeDraftEditor
/// remains the only boundary that validates or persists a draft.
/// </summary>
public sealed class RecipeDraftEditorViewModel : ObservableObject, IAsyncDisposable
{
    private readonly record struct OperationStart(long Version, CancellationTokenSource Cancellation);

    private static readonly ReadOnlyCollection<ProductionAcquisitionMode> AcquisitionModesValue =
        new(Enum.GetValues<ProductionAcquisitionMode>());
    private static readonly ReadOnlyCollection<VisionPixelFormat> PixelFormatsValue =
        new(Enum.GetValues<VisionPixelFormat>());
    private static readonly ReadOnlyCollection<int?> ValidBitsOptionsValue =
        new(new int?[] { null, 10, 12, 16 });
    private static readonly ReadOnlyCollection<RecipeAssetKind> AssetKindsValue =
        new(Enum.GetValues<RecipeAssetKind>());
    private static readonly ReadOnlyCollection<RecipePolicyKind> PolicyKindsValue =
        new(Enum.GetValues<RecipePolicyKind>());

    private readonly IRecipeDraftEditor? _editor;
    private readonly IInteractiveSessionService? _sessions;
    private readonly IStepUpAuthentication? _stepUp;
    private readonly IUiDispatcher _dispatcher;
    private readonly AlgorithmExecutionPolicy? _executionPolicy;
    private readonly object _sync = new();
    private readonly ObservableCollection<RecipeDraftFieldViewModel> _fields = new();
    private readonly ReadOnlyObservableCollection<RecipeDraftFieldViewModel> _readOnlyFields;
    private readonly ObservableCollection<RecipeDraftAssetRequirementViewModel> _assets = new();
    private readonly ReadOnlyObservableCollection<RecipeDraftAssetRequirementViewModel> _readOnlyAssets;
    private readonly ObservableCollection<RecipeDraftPolicyRequirementViewModel> _policies = new();
    private readonly ReadOnlyObservableCollection<RecipeDraftPolicyRequirementViewModel> _readOnlyPolicies;
    private readonly ObservableCollection<RecipeDraftHistoryItem> _history = new();
    private readonly ReadOnlyObservableCollection<RecipeDraftHistoryItem> _readOnlyHistory;
    private CancellationTokenSource? _activeCancellation;
    private long _operationVersion;
    private InteractiveSession _session;
    private AlgorithmDescriptor? _selectedAlgorithm;
    private RecipeDraftRevision? _revision;
    private RecipeDraftHistoryItem? _selectedHistory;
    private long? _historyThroughPosition;
    private long? _historyNextAfterPosition;
    private int _historyPage;
    private RecipeDraftAccess? _access;
    private ReadOnlyCollection<AlgorithmValidationIssue> _validationIssues =
        new(Array.Empty<AlgorithmValidationIssue>());
    private bool _localValidationValid;
    private string _validationReasonCode = "RecipeDraftNotInitialized";
    private string _statusMessage;
    private string? _errorCode;
    private bool _isBusy;
    private bool _disposed;
    private StepUpBinding? _lastStepUpBinding;
    private string _recipeKey = string.Empty;
    private string _displayName = string.Empty;
    private string _cameraRole = "Primary";
    private string _executionTimeoutText = string.Empty;
    private string _changeReason = "编辑配方草稿";
    private ProductionAcquisitionMode _acquisitionMode = ProductionAcquisitionMode.SoftwareTrigger;
    private VisionPixelFormat _pixelFormat = VisionPixelFormat.Mono8;
    private int? _validBits;
    private string _exposureTimeUsText = "1000";
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
    private readonly HashSet<string> _cameraInputRejected = new(StringComparer.Ordinal);

    public RecipeDraftEditorViewModel(IRecipeDraftEditor? editor,
        IInteractiveSessionService? sessions, IUiDispatcher? dispatcher = null,
        AlgorithmExecutionPolicy? executionPolicy = null,
        IStepUpAuthentication? stepUpAuthentication = null)
    {
        _editor = editor;
        _sessions = sessions;
        _stepUp = stepUpAuthentication;
        _dispatcher = dispatcher ?? new DispatcherUiDispatcher();
        _executionPolicy = executionPolicy;
        _readOnlyFields = new ReadOnlyObservableCollection<RecipeDraftFieldViewModel>(_fields);
        _readOnlyAssets = new ReadOnlyObservableCollection<RecipeDraftAssetRequirementViewModel>(_assets);
        _readOnlyPolicies = new ReadOnlyObservableCollection<RecipeDraftPolicyRequirementViewModel>(_policies);
        _readOnlyHistory = new ReadOnlyObservableCollection<RecipeDraftHistoryItem>(_history);
        _session = sessions?.Current ?? UnauthenticatedSession;
        _statusMessage = IsConfigured
            ? "请选择算法并新建草稿，或打开已有草稿。"
            : "配方草稿编辑不可用：未配置受限编辑服务。";

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => CanRefresh);
        NewDraftCommand = new RelayCommand(_ => CreateNewDraft(), _ => CanCreateDraft);
        OpenSelectedCommand = new AsyncRelayCommand(() => OpenSelectedAsync(), () => CanOpenSelected);
        NextHistoryCommand = new AsyncRelayCommand(() => NextHistoryAsync(), () => CanNextHistory);
        ValidateCommand = new AsyncRelayCommand(() => ValidateAsync(), () => CanValidate);
        SaveCommand = new AsyncRelayCommand(() => SaveAsync(), () => CanSave);
        if (_sessions is not null) _sessions.Changed += SessionChanged;
        RecomputeLocalValidation();
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand NewDraftCommand { get; }
    public AsyncRelayCommand OpenSelectedCommand { get; }
    public AsyncRelayCommand NextHistoryCommand { get; }
    public AsyncRelayCommand ValidateCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }

    public bool IsConfigured => _editor is not null;
    public bool IsBusy { get { lock (_sync) return _isBusy; } }
    public bool IsAuthenticated => CurrentSession.State == InteractiveSessionState.Authenticated;
    public InteractiveSession CurrentSession => _sessions?.Current ?? ReadSession();
    public IReadOnlyList<AlgorithmDescriptor> Algorithms => _editor?.Algorithms ?? Array.Empty<AlgorithmDescriptor>();
    public IReadOnlyList<ProductionAcquisitionMode> AcquisitionModes => AcquisitionModesValue;
    public IReadOnlyList<VisionPixelFormat> PixelFormats => PixelFormatsValue;
    public IReadOnlyList<int?> ValidBitsOptions => ValidBitsOptionsValue;
    public IReadOnlyList<RecipeAssetKind> AssetKinds => AssetKindsValue;
    public IReadOnlyList<RecipePolicyKind> PolicyKinds => PolicyKindsValue;
    public ReadOnlyObservableCollection<RecipeDraftFieldViewModel> Fields => _readOnlyFields;
    public ReadOnlyObservableCollection<RecipeDraftAssetRequirementViewModel> AssetRequirements => _readOnlyAssets;
    public ReadOnlyObservableCollection<RecipeDraftPolicyRequirementViewModel> PolicyRequirements => _readOnlyPolicies;
    public ReadOnlyObservableCollection<RecipeDraftHistoryItem> History => _readOnlyHistory;
    public int HistoryPage => _historyPage;
    public long? HistoryThroughPosition => _historyThroughPosition;
    public long? HistoryNextAfterPosition => _historyNextAfterPosition;

    public AlgorithmDescriptor? SelectedAlgorithm
    {
        get => _selectedAlgorithm;
        set
        {
            if (ReferenceEquals(_selectedAlgorithm, value)) return;
            _selectedAlgorithm = value;
            OnPropertyChanged();
            if (value is not null) CreateNewDraft();
            else ClearTransientState();
        }
    }

    public RecipeDraftHistoryItem? SelectedHistory
    {
        get => _selectedHistory;
        set
        {
            if (ReferenceEquals(_selectedHistory, value)) return;
            _selectedHistory = value;
            OnPropertyChanged();
            OpenSelectedCommand.RaiseCanExecuteChanged();
        }
    }

    public Guid DraftId => _revision?.DraftId ?? _draftId;
    public long ExpectedRevision => _revision?.Revision ?? _expectedRevision;
    public string? ExpectedRevisionContentHash => _revision?.RevisionContentHash ?? _expectedRevisionContentHash;
    public long? CurrentPosition => _revision?.Position;
    public string? RevisionContentHash => _revision?.RevisionContentHash;
    public string? ConfigurationContentHash => _localContent?.Configuration.ContentHash;
    public string? DraftContentHash => _localContent?.ContentHash;
    public RecipeDraftRevision? CurrentRevision => _revision;
    public CameraProviderExtensionRequirement? CameraProviderExtension => _revision?.Content.CameraProviderExtension;
    public string CameraPortabilityText => CameraProviderExtension is null
        ? "公共相机配置，可在能力兼容的设备间使用。"
        : "此草稿声明了指定 Provider 的扩展依赖；通用字段编辑会保留该依赖，不能直接移植到其他 Provider。";
    public bool HasDraft => _selectedAlgorithm is not null || _revision is not null;

    public string RecipeKey
    {
        get => _recipeKey;
        set { SetBoundedProperty(ref _recipeKey, value, RecipeDraftInputBounds.IdentifierCharacters, RecipeDraftInputBounds.IdentifierBytes, nameof(RecipeKey)); RecomputeLocalValidation(); }
    }
    public string DisplayName
    {
        get => _displayName;
        set { SetBoundedProperty(ref _displayName, value, RecipeDraftInputBounds.DisplayNameCharacters, RecipeDraftInputBounds.SafeTextBytes, nameof(DisplayName)); RecomputeLocalValidation(); }
    }
    public string CameraRole
    {
        get => _cameraRole;
        set { SetBoundedProperty(ref _cameraRole, value, RecipeDraftInputBounds.IdentifierCharacters, RecipeDraftInputBounds.IdentifierBytes, nameof(CameraRole)); RecomputeLocalValidation(); }
    }
    public string AlgorithmExecutionTimeoutText
    {
        get => _executionTimeoutText;
        set { SetBoundedProperty(ref _executionTimeoutText, value, RecipeDraftInputBounds.NumericCharacters, RecipeDraftInputBounds.NumericBytes, nameof(AlgorithmExecutionTimeoutText)); RecomputeLocalValidation(); }
    }
    public string ChangeReason
    {
        get => _changeReason;
        set { SetBoundedProperty(ref _changeReason, value, RecipeDraftInputBounds.DisplayNameCharacters, RecipeDraftInputBounds.SafeTextBytes, nameof(ChangeReason)); RecomputeLocalValidation(); }
    }
    public ProductionAcquisitionMode AcquisitionMode
    {
        get => _acquisitionMode;
        set { if (SetProperty(ref _acquisitionMode, value)) RecomputeLocalValidation(); }
    }
    public VisionPixelFormat PixelFormat
    {
        get => _pixelFormat;
        set { if (SetProperty(ref _pixelFormat, value)) RecomputeLocalValidation(); }
    }
    public int? ValidBits
    {
        get => _validBits;
        set { if (SetProperty(ref _validBits, value)) RecomputeLocalValidation(); }
    }
    public string ExposureTimeUsText { get => _exposureTimeUsText; set => SetCameraText(ref _exposureTimeUsText, value, nameof(ExposureTimeUsText)); }
    public string GainDbText { get => _gainDbText; set => SetCameraText(ref _gainDbText, value, nameof(GainDbText)); }
    public string RoiOffsetXText { get => _roiOffsetXText; set => SetCameraText(ref _roiOffsetXText, value, nameof(RoiOffsetXText)); }
    public string RoiOffsetYText { get => _roiOffsetYText; set => SetCameraText(ref _roiOffsetYText, value, nameof(RoiOffsetYText)); }
    public string RoiWidthText { get => _roiWidthText; set => SetCameraText(ref _roiWidthText, value, nameof(RoiWidthText)); }
    public string RoiHeightText { get => _roiHeightText; set => SetCameraText(ref _roiHeightText, value, nameof(RoiHeightText)); }
    public string AcquisitionTimeoutMsText { get => _acquisitionTimeoutMsText; set => SetCameraText(ref _acquisitionTimeoutMsText, value, nameof(AcquisitionTimeoutMsText)); }
    public string TriggerDelayUsText { get => _triggerDelayUsText; set => SetCameraText(ref _triggerDelayUsText, value, nameof(TriggerDelayUsText)); }
    public string WhiteBalanceRedText { get => _whiteBalanceRedText; set => SetCameraText(ref _whiteBalanceRedText, value, nameof(WhiteBalanceRedText)); }
    public string WhiteBalanceGreenText { get => _whiteBalanceGreenText; set => SetCameraText(ref _whiteBalanceGreenText, value, nameof(WhiteBalanceGreenText)); }
    public string WhiteBalanceBlueText { get => _whiteBalanceBlueText; set => SetCameraText(ref _whiteBalanceBlueText, value, nameof(WhiteBalanceBlueText)); }

    public ReadOnlyCollection<AlgorithmValidationIssue> ValidationIssues => _validationIssues;
    public bool IsValid => _localValidationValid;
    public string ValidationReasonCode => _validationReasonCode;
    public string ValidationSummary => IsValid ? "结构配置有效；保存后仍需依赖和发布治理。" :
        _validationIssues.Count == 0 ? "请先选择算法并填写完整配置。" :
        $"存在 {_validationIssues.Count} 项需要处理的配置问题。";
    public string DependenciesStatus => "NotRun";
    public bool CanRelease => false;
    public bool IsUnavailable => !IsConfigured || !IsAuthenticated || _access is null || !_access.CanSave;
    public bool RequiresStepUp => _access?.RequiresStepUp == true;
    public string StatusMessage { get { lock (_sync) return _statusMessage; } private set { lock (_sync) _statusMessage = value; OnPropertyChanged(); } }
    public string? ErrorCode { get { lock (_sync) return _errorCode; } private set { lock (_sync) _errorCode = value; OnPropertyChanged(); } }
    public bool CanRefresh => IsConfigured && !IsBusy && !_disposed;
    public bool CanCreateDraft => IsConfigured && SelectedAlgorithm is not null && !IsBusy && !_disposed;
    public bool CanOpenSelected => IsConfigured && SelectedHistory is not null && !IsBusy && !_disposed;
    public bool CanNextHistory => IsConfigured && _historyNextAfterPosition is not null && !IsBusy && !_disposed;
    public bool CanValidate => IsConfigured && HasDraft && !IsBusy && !_disposed;
    public bool CanSave => IsConfigured && HasDraft && IsValid && !IsBusy && !_disposed &&
        IsAuthenticated && _access?.CanSave == true && !_access.RequiresStepUp &&
        IsUsableSession(CurrentSession);
    public bool CanSaveWithStepUp => IsConfigured && HasDraft && IsValid && !IsBusy && !_disposed &&
        IsAuthenticated && _access?.CanSave == true && _access.RequiresStepUp && _stepUp is not null &&
        IsUsableSession(CurrentSession);
    public bool HasStepUpService => _stepUp is not null;
    internal StepUpBinding? LastStepUpBinding
    {
        get { lock (_sync) return _lastStepUpBinding; }
    }

    private Guid _draftId = Guid.NewGuid();
    private long _expectedRevision;
    private string? _expectedRevisionContentHash;
    private RecipeDraftContent? _localContent;

    public void CreateNewDraft()
    {
        if (_editor is null || _selectedAlgorithm is null || _disposed) return;
        CancelPendingOperations();
        _revision = null;
        _draftId = Guid.NewGuid();
        _expectedRevision = 0;
        _expectedRevisionContentHash = null;
        _recipeKey = "Draft-" + _draftId.ToString("N")[..12];
        _displayName = _selectedAlgorithm.Identity.Id + " 草稿";
        _cameraRole = "Primary";
        _executionTimeoutText = _executionPolicy?.MinimumExecutionTimeout.TotalMilliseconds
            .ToString("0", CultureInfo.InvariantCulture) ?? string.Empty;
        _acquisitionMode = ProductionAcquisitionMode.SoftwareTrigger;
        _pixelFormat = VisionPixelFormat.Mono8;
        _validBits = null;
        _cameraInputRejected.Clear();
        _exposureTimeUsText = "1000";
        _gainDbText = "0";
        _roiOffsetXText = "0";
        _roiOffsetYText = "0";
        _roiWidthText = "1";
        _roiHeightText = "1";
        _acquisitionTimeoutMsText = "1000";
        _triggerDelayUsText = "0";
        _whiteBalanceRedText = string.Empty;
        _whiteBalanceGreenText = string.Empty;
        _whiteBalanceBlueText = string.Empty;
        IReadOnlyList<AlgorithmConfigurationEntry> defaults;
        try
        {
            defaults = _editor.GetAuthoringDefaults(_selectedAlgorithm.Identity)
                ?? Array.Empty<AlgorithmConfigurationEntry>();
        }
        catch
        {
            SetFields(_selectedAlgorithm.ConfigurationSchema, null, null);
            _access = null;
            _errorCode = "RecipeDraftEditorUnavailable";
            _statusMessage = "Schema 默认值暂不可用；未写入任何草稿内容。";
            OnPropertyChanged(string.Empty);
            RecomputeLocalValidation();
            NotifyCommands();
            return;
        }
        SetFields(_selectedAlgorithm.ConfigurationSchema, null, defaults);
        SetRequirements(Array.Empty<RecipeAssetRequirement>(),
            _executionPolicy is null ? Array.Empty<RecipePolicyRequirement>() : new[]
            {
                new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
                    new RecipeContractReference(_executionPolicy.Id, _executionPolicy.Version,
                        _executionPolicy.ContentHash))
            });
        _changeReason = "编辑配方草稿";
        _access = null;
        _errorCode = null;
        _statusMessage = "新草稿已建立；Schema 默认值仅用于本次新建，不会在重开时自动补入。";
        OnPropertyChanged(string.Empty);
        RecomputeLocalValidation();
        NotifyCommands();
    }

    public void UseAuthoringDefault(RecipeDraftFieldViewModel field)
    {
        ArgumentNullException.ThrowIfNull(field);
        field.ApplyAuthoringDefault();
        RecomputeLocalValidation();
    }

    public void ClearField(RecipeDraftFieldViewModel field)
    {
        ArgumentNullException.ThrowIfNull(field);
        field.ClearValue();
        RecomputeLocalValidation();
    }

    public void AddAssetRequirement()
    {
        var row = new RecipeDraftAssetRequirementViewModel();
        row.PropertyChanged += RequirementChanged;
        _assets.Add(row);
        RecomputeLocalValidation();
    }
    public void RemoveAssetRequirement(RecipeDraftAssetRequirementViewModel row) { if (_assets.Remove(row)) RecomputeLocalValidation(); }
    public void AddPolicyRequirement()
    {
        var row = new RecipeDraftPolicyRequirementViewModel();
        row.PropertyChanged += RequirementChanged;
        _policies.Add(row);
        RecomputeLocalValidation();
    }
    public void RemovePolicyRequirement(RecipeDraftPolicyRequirementViewModel row) { if (_policies.Remove(row)) RecomputeLocalValidation(); }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_editor is null)
        {
            await ApplyUnavailableAsync("RecipeDraftEditorUnavailable").ConfigureAwait(true);
            return;
        }
        var start = Begin(cancellationToken);
        if (!start.HasValue) return;
        try
        {
            var session = _sessions?.Current ?? UnauthenticatedSession;
            var access = await _editor.GetAccessAsync(CreateInvocation(session), start.Value.Cancellation.Token)
                .ConfigureAwait(true);
            start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            var page = await _editor.QueryAsync(new RecipeDraftFilter(PageSize: 50), start.Value.Cancellation.Token)
                .ConfigureAwait(true);
            start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            await ApplyRefreshAsync(session, access, page, start.Value).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested) { }
        catch { await ApplyUnavailableAsync("RecipeDraftQueryFailed", start).ConfigureAwait(true); }
        finally { Complete(start.Value); }
    }

    public async Task NextHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (_editor is null || _historyNextAfterPosition is not { } afterPosition) return;
        var throughPosition = _historyThroughPosition;
        var start = Begin(cancellationToken);
        if (!start.HasValue) return;
        try
        {
            var page = await _editor.QueryAsync(new RecipeDraftFilter(
                AfterPosition: afterPosition, ThroughPosition: throughPosition, PageSize: 50),
                start.Value.Cancellation.Token).ConfigureAwait(true);
            start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            await ApplyHistoryPageAsync(page, start.Value, append: true).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested) { }
        catch { await ApplyUnavailableAsync("RecipeDraftQueryFailed", start).ConfigureAwait(true); }
        finally { Complete(start.Value); }
    }

    public async Task OpenSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (_editor is null || SelectedHistory is null) return;
        var start = Begin(cancellationToken);
        if (!start.HasValue) return;
        try
        {
            var read = await _editor.ReadAsync(SelectedHistory.Revision.DraftId,
                SelectedHistory.Revision.Revision, start.Value.Cancellation.Token).ConfigureAwait(true);
            start.Value.Cancellation.Token.ThrowIfCancellationRequested();
            await ApplyReadAsync(read, start.Value).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested) { }
        catch { await ApplyUnavailableAsync("RecipeDraftReadFailed", start).ConfigureAwait(true); }
        finally { Complete(start.Value); }
    }

    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        if (_editor is null) return;
        var start = Begin(cancellationToken);
        if (!start.HasValue) return;
        try
        {
            if (!TryBuildContent(out var content, out var issues))
            {
                await ApplyValidationAsync(new RecipeDraftValidationResult(false, "RecipeDraftInputInvalid", issues), start.Value).ConfigureAwait(true);
                return;
            }
            var result = await _editor.ValidateAsync(content!, start.Value.Cancellation.Token).ConfigureAwait(true);
            await ApplyValidationAsync(result, start.Value).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested) { }
        catch { await ApplyUnavailableAsync("RecipeDraftValidationFailed", start).ConfigureAwait(true); }
        finally { Complete(start.Value); }
    }

    public Task<RecipeDraftSaveResult?> SaveAsync(CancellationToken cancellationToken = default) =>
        SaveCoreAsync(null, cancellationToken);

    /// <summary>
    /// Saves with a one-shot current-password reauthentication. The password is
    /// held only by this call and is cleared before the Runtime request is built.
    /// </summary>
    public Task<RecipeDraftSaveResult?> SaveWithStepUpAsync(string password,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(password ?? string.Empty, cancellationToken);

    private async Task<RecipeDraftSaveResult?> SaveCoreAsync(string? stepUpPassword,
        CancellationToken cancellationToken)
    {
        if (_editor is null)
        {
            await ApplyUnavailableAsync("RecipeDraftEditorUnavailable").ConfigureAwait(true);
            return null;
        }
        var start = Begin(cancellationToken);
        if (!start.HasValue) return null;
        try
        {
            var session = _sessions?.Current ?? UnauthenticatedSession;
            if (!IsUsableSession(session))
            {
                await ApplyErrorAsync("RecipeDraftUnauthenticated", start).ConfigureAwait(true);
                return null;
            }
            var access = await _editor.GetAccessAsync(CreateInvocation(session), start.Value.Cancellation.Token)
                .ConfigureAwait(true);
            if (!access.CanSave)
            {
                await ApplyErrorAsync(SafeReason(access.ReasonCode, "RecipeDraftAccessDenied"), start).ConfigureAwait(true);
                return null;
            }
            if (!TryBuildContent(out var content, out var issues))
            {
                await ApplyValidationAsync(new RecipeDraftValidationResult(false, "RecipeDraftInputInvalid", issues), start.Value).ConfigureAwait(true);
                return null;
            }
            var validation = await _editor.ValidateAsync(content!, start.Value.Cancellation.Token).ConfigureAwait(true);
            if (!validation.Valid)
            {
                await ApplyValidationAsync(validation, start.Value).ConfigureAwait(true);
                return null;
            }

            // Freeze every request identity before the optional reauthentication.
            // Later UI edits cannot change the content, CAS head or binding target.
            var operationId = Guid.NewGuid();
            var draftId = DraftId;
            var expectedRevision = ExpectedRevision;
            var expectedRevisionContentHash = ExpectedRevisionContentHash;
            var reason = string.IsNullOrWhiteSpace(ChangeReason) ? "编辑配方草稿" : ChangeReason;
            var currentSession = _sessions?.Current ?? session;
            if (!SameSession(session, currentSession))
            {
                await ApplyErrorAsync("RecipeDraftSessionChanged", start).ConfigureAwait(true);
                return null;
            }

            Guid? grantId = null;
            if (access.RequiresStepUp)
            {
                if (string.IsNullOrEmpty(stepUpPassword))
                {
                    await ApplyErrorAsync("RecipeDraftStepUpRequired", start).ConfigureAwait(true);
                    return null;
                }
                if (_stepUp is null)
                {
                    await ApplyErrorAsync("RecipeDraftStepUpUnavailable", start).ConfigureAwait(true);
                    return null;
                }

                var binding = new StepUpBinding(Permission.EditRecipeDraft, operationId,
                    draftId.ToString("D"), AuditedCommandKind.SaveRecipeDraft);
                lock (_sync) _lastStepUpBinding = binding;
                StepUpResult stepUpResult;
                try
                {
                    stepUpResult = await _stepUp.ReauthenticateAsync(
                        new StepUpRequest(operationId, CreateInvocation(session), binding, stepUpPassword),
                        start.Value.Cancellation.Token).ConfigureAwait(true);
                }
                finally
                {
                    stepUpPassword = string.Empty;
                }

                start.Value.Cancellation.Token.ThrowIfCancellationRequested();
                if (!stepUpResult.Succeeded || stepUpResult.GrantId is not { } grant)
                {
                    await ApplyErrorAsync("StepUpAuthenticationRejected", start).ConfigureAwait(true);
                    return null;
                }
                grantId = grant;
                currentSession = _sessions?.Current ?? currentSession;
                if (!SameSession(session, currentSession))
                {
                    await ApplyErrorAsync("RecipeDraftSessionChanged", start).ConfigureAwait(true);
                    return null;
                }
            }

            var request = new RecipeDraftSaveRequest(operationId, draftId, expectedRevision,
                expectedRevisionContentHash, content!, reason, CreateInvocation(currentSession, grantId), grantId);
            var result = await _editor.SaveAsync(request, start.Value.Cancellation.Token).ConfigureAwait(true);
            if (!result.Saved)
            {
                await ApplySaveResultAsync(result, start.Value).ConfigureAwait(true);
                return result;
            }
            if (result.Revision is null)
            {
                await ApplyErrorAsync("RecipeDraftSaveResultInvalid", start).ConfigureAwait(true);
                return result;
            }
            var reread = await _editor.ReadAsync(result.Revision.DraftId, result.Revision.Revision,
                start.Value.Cancellation.Token).ConfigureAwait(true);
            if (!reread.Available || reread.Revision is null)
            {
                await ApplyErrorAsync("RecipeDraftRereadFailed", start).ConfigureAwait(true);
                return result;
            }
            await ApplyReadAsync(reread, start.Value, "草稿已保存并重新读取；依赖和发布资格仍未运行。").ConfigureAwait(true);
            return result;
        }
        catch (OperationCanceledException) when (start.Value.Cancellation.IsCancellationRequested)
        {
            await ApplyErrorAsync("RecipeDraftCancelled", start).ConfigureAwait(true);
            return null;
        }
        catch { await ApplyUnavailableAsync("RecipeDraftSaveFailed", start).ConfigureAwait(true); return null; }
        finally
        {
            stepUpPassword = string.Empty;
            Complete(start.Value);
        }
    }

    public void CancelPendingOperations()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            _operationVersion++;
            cancellation = _activeCancellation;
            _activeCancellation = null;
            _isBusy = false;
        }
        cancellation?.Cancel();
        NotifyStateChangedOnUi();
    }

    /// <summary>Clears the unsaved editor projection after lock, logout or page hiding.</summary>
    public void ClearTransientState()
    {
        CancelPendingOperations();
        if (!_dispatcher.CheckAccess) { _ = _dispatcher.InvokeAsync(ClearTransientState); return; }
        _revision = null;
        _selectedAlgorithm = null;
        _selectedHistory = null;
        _fields.Clear();
        _assets.Clear();
        _policies.Clear();
        _recipeKey = string.Empty;
        _displayName = string.Empty;
        _cameraRole = string.Empty;
        _executionTimeoutText = string.Empty;
        _changeReason = string.Empty;
        _acquisitionMode = ProductionAcquisitionMode.SoftwareTrigger;
        _pixelFormat = VisionPixelFormat.Mono8;
        _validBits = null;
        _cameraInputRejected.Clear();
        _exposureTimeUsText = string.Empty;
        _gainDbText = string.Empty;
        _roiOffsetXText = string.Empty;
        _roiOffsetYText = string.Empty;
        _roiWidthText = string.Empty;
        _roiHeightText = string.Empty;
        _acquisitionTimeoutMsText = string.Empty;
        _triggerDelayUsText = string.Empty;
        _whiteBalanceRedText = string.Empty;
        _whiteBalanceGreenText = string.Empty;
        _whiteBalanceBlueText = string.Empty;
        _history.Clear();
        _historyThroughPosition = null;
        _historyNextAfterPosition = null;
        _historyPage = 0;
        _localContent = null;
        _access = null;
        lock (_sync) _lastStepUpBinding = null;
        _draftId = Guid.NewGuid();
        _expectedRevision = 0;
        _expectedRevisionContentHash = null;
        _validationIssues = new ReadOnlyCollection<AlgorithmValidationIssue>(Array.Empty<AlgorithmValidationIssue>());
        _localValidationValid = false;
        _validationReasonCode = "RecipeDraftNotInitialized";
        _errorCode = null;
        _statusMessage = IsConfigured ? "编辑内容已清除，请重新读取或新建草稿。" : "配方草稿编辑不可用。";
        OnPropertyChanged(string.Empty);
        NotifyCommands();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        CancelPendingOperations();
        if (_sessions is not null) _sessions.Changed -= SessionChanged;
        ClearTransientState();
        await Task.CompletedTask;
    }

    private async Task ApplyRefreshAsync(InteractiveSession session, RecipeDraftAccess access,
        RecipeDraftPage page, OperationStart start)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync) if (!IsCurrentLocked(start)) return;
            _session = session;
            _access = access;
            _history.Clear();
            var validPage = IsHistoryPageValid(page, 0, null);
            _historyThroughPosition = validPage ? page.ThroughPosition : null;
            _historyNextAfterPosition = null;
            _historyPage = 0;
            if (validPage)
            {
                foreach (var revision in page.Revisions) _history.Add(new RecipeDraftHistoryItem(revision));
                _historyNextAfterPosition = page.NextAfterPosition;
                _historyPage = _history.Count == 0 ? 0 : 1;
            }
            _errorCode = validPage ? null : SafeReason(page.ReasonCode, "RecipeDraftQueryFailed");
            _statusMessage = validPage
                ? "草稿历史已读取；选择版本后打开，或选择算法新建草稿。"
                : "草稿历史暂不可用。";
            NotifyStateChanged();
        }).ConfigureAwait(true);
    }

    private async Task ApplyHistoryPageAsync(RecipeDraftPage page, OperationStart start, bool append)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync) if (!IsCurrentLocked(start)) return;
            if (!IsHistoryPageValid(page, _history.Count == 0 ? 0 : _history[^1].Revision.Position,
                    _historyThroughPosition))
            {
                // Keep the last verified rows, but discard the untrusted cursor
                // so a malformed page cannot drive another query or overlap.
                _historyNextAfterPosition = null;
                _errorCode = "RecipeDraftQueryFailed";
                _statusMessage = "草稿历史页无效，已保留当前页。";
                NotifyStateChanged();
                return;
            }
            if (!append) _history.Clear();
            foreach (var revision in page.Revisions)
            {
                if (_history.Count >= 256) _history.RemoveAt(0);
                _history.Add(new RecipeDraftHistoryItem(revision));
            }
            _historyNextAfterPosition = page.NextAfterPosition;
            if (append) _historyPage++;
            _errorCode = null;
            _statusMessage = "草稿历史已读取；选择版本后打开，或选择算法新建草稿。";
            NotifyStateChanged();
        }).ConfigureAwait(true);
    }

    private static bool IsHistoryPageValid(RecipeDraftPage page, long afterPosition,
        long? expectedThroughPosition)
    {
        if (!page.Available || page.Revisions.Count > 50 || page.ThroughPosition < afterPosition)
            return false;
        if (expectedThroughPosition is { } expected && page.ThroughPosition != expected)
            return false;
        var previous = afterPosition;
        foreach (var revision in page.Revisions)
        {
            if (revision is null || revision.Position <= previous || revision.Position > page.ThroughPosition)
                return false;
            previous = revision.Position;
        }
        // The cursor is the last returned position (the next query uses an
        // exclusive AfterPosition), so equality with the page's last row is
        // the expected forward-progress shape.
        return page.NextAfterPosition is null ||
            (page.Revisions.Count != 0 && page.NextAfterPosition.Value == previous &&
             previous > afterPosition && previous < page.ThroughPosition);
    }

    private async Task ApplyReadAsync(RecipeDraftReadResult result, OperationStart start, string? message = null)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync) if (!IsCurrentLocked(start)) return;
            if (!result.Available || result.Revision is null)
            {
                _errorCode = SafeReason(result.ReasonCode, "RecipeDraftReadFailed");
                _statusMessage = "草稿读取失败，未替换当前编辑内容。";
                NotifyStateChanged();
                return;
            }
            LoadRevision(result.Revision);
            _errorCode = null;
            _statusMessage = message ?? "草稿已重新打开；页面未自动补入新的 Schema 默认值。";
            NotifyStateChanged();
        }).ConfigureAwait(true);
    }

    private async Task ApplyValidationAsync(RecipeDraftValidationResult result, OperationStart start)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync) if (!IsCurrentLocked(start)) return;
            _validationIssues = new ReadOnlyCollection<AlgorithmValidationIssue>(
                (result.Issues ?? Array.Empty<AlgorithmValidationIssue>()).Take(256).ToArray());
            _localValidationValid = result.Valid;
            _validationReasonCode = SafeReason(result.ReasonCode, result.Valid ? "RecipeDraftValid" : "RecipeDraftValidationFailed");
            _errorCode = result.Valid ? null : _validationReasonCode;
            _statusMessage = result.Valid ? "结构与算法校验通过；依赖校验和发布治理仍未完成。" : "草稿校验未通过，请按字段提示修正。";
            NotifyStateChanged();
        }).ConfigureAwait(true);
    }

    private async Task ApplySaveResultAsync(RecipeDraftSaveResult result, OperationStart start)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync) if (!IsCurrentLocked(start)) return;
            _validationIssues = new ReadOnlyCollection<AlgorithmValidationIssue>(
                (result.Issues ?? Array.Empty<AlgorithmValidationIssue>()).Take(256).ToArray());
            _localValidationValid = false;
            _validationReasonCode = SafeReason(result.ReasonCode, "RecipeDraftSaveRejected");
            _errorCode = _validationReasonCode;
            _statusMessage = "草稿未保存，当前编辑内容未被 Runtime 预先修改。";
            NotifyStateChanged();
        }).ConfigureAwait(true);
    }

    private void LoadRevision(RecipeDraftRevision revision)
    {
        _revision = revision;
        _draftId = revision.DraftId;
        _expectedRevision = revision.Revision;
        _expectedRevisionContentHash = revision.RevisionContentHash;
        var content = revision.Content;
        _selectedAlgorithm = Algorithms.FirstOrDefault(item =>
            item.Identity.Id == content.Algorithm.Algorithm.Id &&
            item.Identity.Version == content.Algorithm.Algorithm.Version &&
            item.ConfigurationSchema.ContentHash == content.Algorithm.ConfigurationSchema.ContentHash);
        _recipeKey = content.RecipeKey;
        _displayName = content.DisplayName;
        _cameraRole = content.CameraRole;
        _executionTimeoutText = content.AlgorithmExecutionTimeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
        _acquisitionMode = content.Camera.ProductionAcquisitionMode;
        _pixelFormat = content.Camera.PixelFormat;
        _validBits = content.Camera.ValidBits;
        _cameraInputRejected.Clear();
        _exposureTimeUsText = Number(content.Camera.ExposureTimeUs);
        _gainDbText = Number(content.Camera.GainDb);
        _roiOffsetXText = content.Camera.RegionOfInterest.OffsetX.ToString(CultureInfo.InvariantCulture);
        _roiOffsetYText = content.Camera.RegionOfInterest.OffsetY.ToString(CultureInfo.InvariantCulture);
        _roiWidthText = content.Camera.RegionOfInterest.Width.ToString(CultureInfo.InvariantCulture);
        _roiHeightText = content.Camera.RegionOfInterest.Height.ToString(CultureInfo.InvariantCulture);
        _acquisitionTimeoutMsText = content.Camera.AcquisitionTimeoutMs.ToString(CultureInfo.InvariantCulture);
        _triggerDelayUsText = Number(content.Camera.TriggerDelayUs);
        _whiteBalanceRedText = content.Camera.WhiteBalanceRgb is null ? string.Empty : Number(content.Camera.WhiteBalanceRgb.Red);
        _whiteBalanceGreenText = content.Camera.WhiteBalanceRgb is null ? string.Empty : Number(content.Camera.WhiteBalanceRgb.Green);
        _whiteBalanceBlueText = content.Camera.WhiteBalanceRgb is null ? string.Empty : Number(content.Camera.WhiteBalanceRgb.Blue);
        SetFields(content.Algorithm.ConfigurationSchema, content.Configuration, null);
        SetRequirements(content.AssetRequirements, content.PolicyRequirements);
        RecomputeLocalValidation();
        OnPropertyChanged(string.Empty);
    }

    private void SetFields(AlgorithmConfigurationSchema schema, AlgorithmConfigurationSnapshot? snapshot,
        IReadOnlyList<AlgorithmConfigurationEntry>? defaults)
    {
        foreach (var field in _fields) field.PropertyChanged -= FieldChanged;
        _fields.Clear();
        var values = snapshot?.Values.ToDictionary(item => item.Key, StringComparer.Ordinal) ??
            new Dictionary<string, AlgorithmConfigurationEntry>(StringComparer.Ordinal);
        IReadOnlyList<RecipeDraftFieldOrigin> originSource = snapshot is null
            ? Array.Empty<RecipeDraftFieldOrigin>()
            : _revision?.Content.ValueOrigins ?? new ReadOnlyCollection<RecipeDraftFieldOrigin>(Array.Empty<RecipeDraftFieldOrigin>());
        var origins = snapshot is null ? new Dictionary<string, RecipeDraftValueOrigin>(StringComparer.Ordinal) :
            originSource
                .GroupBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Origin, StringComparer.Ordinal);
        var defaultMap = (defaults ?? Array.Empty<AlgorithmConfigurationEntry>())
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        foreach (var definition in schema.Fields)
        {
            var field = new RecipeDraftFieldViewModel(definition);
            field.PropertyChanged += FieldChanged;
            if (values.TryGetValue(definition.Key, out var value))
                field.SetValue(value.Value, origins.TryGetValue(definition.Key, out var origin)
                    ? origin : RecipeDraftValueOrigin.Explicit);
            else if (defaultMap.TryGetValue(definition.Key, out var defaultValue))
                field.SetValue(defaultValue.Value, RecipeDraftValueOrigin.AuthoringDefault);
            _fields.Add(field);
        }
    }

    private void SetRequirements(IEnumerable<RecipeAssetRequirement> assets,
        IEnumerable<RecipePolicyRequirement> policies)
    {
        foreach (var row in _assets) row.PropertyChanged -= RequirementChanged;
        foreach (var row in _policies) row.PropertyChanged -= RequirementChanged;
        _assets.Clear(); _policies.Clear();
        foreach (var asset in assets)
        {
            var row = RecipeDraftAssetRequirementViewModel.From(asset);
            row.PropertyChanged += RequirementChanged;
            _assets.Add(row);
        }
        foreach (var policy in policies)
        {
            var row = RecipeDraftPolicyRequirementViewModel.From(policy);
            row.PropertyChanged += RequirementChanged;
            _policies.Add(row);
        }
    }

    private bool TryBuildContent(out RecipeDraftContent? content,
        out IReadOnlyList<AlgorithmValidationIssue> issues)
    {
        content = null;
        var output = new List<AlgorithmValidationIssue>();
        var algorithm = _selectedAlgorithm;
        if (algorithm is null)
        {
            output.Add(new("RecipeAlgorithmRequired"));
            issues = output;
            return false;
        }
        if (!IsIdentifier(_recipeKey)) output.Add(new("RecipeKeyInvalid", "RecipeKey"));
        if (string.IsNullOrEmpty(_displayName) || _displayName.Length > 128 || _displayName.Any(char.IsControl))
            output.Add(new("RecipeDisplayNameInvalid", "DisplayName"));
        if (!IsIdentifier(_cameraRole)) output.Add(new("RecipeCameraRoleInvalid", "CameraRole"));
        if (!TryParseTimeout(out var timeout, out var timeoutReason))
            output.Add(new(timeoutReason, "AlgorithmExecutionTimeout"));
        else if (_executionPolicy is not null &&
            (timeout < _executionPolicy.MinimumExecutionTimeout || timeout > _executionPolicy.MaximumExecutionTimeout))
            output.Add(new("AlgorithmExecutionTimeoutOutsidePolicy", "AlgorithmExecutionTimeout"));

        var entries = new List<AlgorithmConfigurationEntry>();
        var origins = new List<RecipeDraftFieldOrigin>();
        foreach (var field in _fields)
        {
            var issue = field.ValidationIssue;
            if (issue is not null) output.Add(new(issue, field.Key));
            if (!field.HasValue)
            {
                if (field.Required) output.Add(new("AlgorithmFieldMissingRequired", field.Key));
                continue;
            }
            if (field.TryGetEntry(out var entry))
            {
                entries.Add(entry);
                origins.Add(new(field.Key, field.Origin));
            }
        }

        AlgorithmConfigurationSnapshot? configuration = null;
        if (output.Count == 0)
        {
            try { configuration = AlgorithmConfigurationSnapshot.Create(algorithm.ConfigurationSchema, entries); }
            catch { output.Add(new("AlgorithmConfigurationInvalid")); }
        }

        RequestedCameraConfiguration? camera = null;
        if (output.Count == 0 && !TryBuildCamera(out camera, out var cameraIssues))
            output.AddRange(cameraIssues);

        var assets = new List<RecipeAssetRequirement>();
        foreach (var row in _assets)
        {
            if (!row.TryBuild(out var value, out var reason)) output.Add(new(reason, "AssetRequirements"));
            else assets.Add(value!);
        }
        var policies = new List<RecipePolicyRequirement>();
        foreach (var row in _policies)
        {
            if (!row.TryBuild(out var value, out var reason)) output.Add(new(reason, "PolicyRequirements"));
            else policies.Add(value!);
        }
        if (!IsValidChangeReason(_changeReason)) output.Add(new("RecipeDraftChangeReasonInvalid"));

        if (output.Count == 0)
        {
            try
            {
                content = new RecipeDraftContent(_recipeKey, _displayName,
                    RecipeAlgorithmBinding.FromDescriptor(algorithm), configuration!, _cameraRole, camera!, timeout,
                    assets, policies, origins, CameraProviderExtension);
            }
            catch { output.Add(new("RecipeDraftContentInvalid")); }
        }
        issues = output.Take(256).ToArray();
        return content is not null && issues.Count == 0;
    }

    private bool TryBuildCamera(out RequestedCameraConfiguration? camera,
        out IReadOnlyList<AlgorithmValidationIssue> issues)
    {
        camera = null;
        var output = new List<AlgorithmValidationIssue>();
        foreach (var rejected in _cameraInputRejected.OrderBy(item => item, StringComparer.Ordinal))
            output.Add(new("CameraInputBoundsInvalid", "Camera." + rejected));
        if (!TryDouble(_exposureTimeUsText, out var exposure)) output.Add(new("CameraExposureInvalid", "Camera.ExposureTimeUs"));
        if (!TryDouble(_gainDbText, out var gain)) output.Add(new("CameraGainInvalid", "Camera.GainDb"));
        if (!TryInt(_roiOffsetXText, out var offsetX)) output.Add(new("CameraRoiInvalid", "Camera.RegionOfInterest.OffsetX"));
        if (!TryInt(_roiOffsetYText, out var offsetY)) output.Add(new("CameraRoiInvalid", "Camera.RegionOfInterest.OffsetY"));
        if (!TryInt(_roiWidthText, out var width)) output.Add(new("CameraRoiInvalid", "Camera.RegionOfInterest.Width"));
        if (!TryInt(_roiHeightText, out var height)) output.Add(new("CameraRoiInvalid", "Camera.RegionOfInterest.Height"));
        if (!TryInt(_acquisitionTimeoutMsText, out var acquisitionTimeout)) output.Add(new("CameraTimeoutInvalid", "Camera.AcquisitionTimeoutMs"));
        if (!TryDouble(_triggerDelayUsText, out var triggerDelay)) output.Add(new("CameraTriggerDelayInvalid", "Camera.TriggerDelayUs"));
        var wbBlank = string.IsNullOrEmpty(_whiteBalanceRedText) && string.IsNullOrEmpty(_whiteBalanceGreenText) && string.IsNullOrEmpty(_whiteBalanceBlueText);
        WhiteBalanceRgb? whiteBalance = null;
        if (!wbBlank)
        {
            if (!TryDouble(_whiteBalanceRedText, out var red) || !TryDouble(_whiteBalanceGreenText, out var green) ||
                !TryDouble(_whiteBalanceBlueText, out var blue)) output.Add(new("CameraWhiteBalanceInvalid", "Camera.WhiteBalanceRgb"));
            else
            {
                try { whiteBalance = new WhiteBalanceRgb(red, green, blue); }
                catch { output.Add(new("CameraWhiteBalanceInvalid", "Camera.WhiteBalanceRgb")); }
            }
        }
        if (output.Count == 0)
        {
            try
            {
                var roi = new RegionOfInterest(offsetX, offsetY, width, height);
                camera = new RequestedCameraConfiguration(_acquisitionMode, exposure, gain, roi,
                    _pixelFormat, _validBits, acquisitionTimeout, triggerDelay, whiteBalance);
            }
            catch { output.Add(new("CameraConfigurationInvalid", "Camera")); }
        }
        issues = output;
        return output.Count == 0;
    }

    private bool TryParseTimeout(out TimeSpan timeout, out string reason)
    {
        timeout = default;
        reason = "AlgorithmExecutionTimeoutInvalid";
        if (!long.TryParse(_executionTimeoutText, NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var milliseconds) ||
            milliseconds <= 0 || milliseconds > int.MaxValue) return false;
        try { timeout = TimeSpan.FromMilliseconds(milliseconds); }
        catch { return false; }
        if (timeout.Ticks % TimeSpan.TicksPerMillisecond != 0) return false;
        reason = string.Empty;
        return true;
    }

    private void RecomputeLocalValidation()
    {
        if (_disposed) return;
        if (TryBuildContent(out var content, out var issues))
        {
            _localContent = content;
            _validationIssues = new ReadOnlyCollection<AlgorithmValidationIssue>(Array.Empty<AlgorithmValidationIssue>());
            _localValidationValid = true;
            _validationReasonCode = "RecipeDraftInputValid";
        }
        else
        {
            _localContent = null;
            _validationIssues = new ReadOnlyCollection<AlgorithmValidationIssue>(issues.Take(256).ToArray());
            _localValidationValid = false;
            _validationReasonCode = issues.Count == 0 ? "RecipeDraftNotInitialized" : issues[0].Code;
        }
        OnPropertyChanged(nameof(ConfigurationContentHash));
        OnPropertyChanged(nameof(DraftContentHash));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ValidationIssues));
        OnPropertyChanged(nameof(ValidationReasonCode));
        OnPropertyChanged(nameof(ValidationSummary));
        NotifyCommands();
    }

    private void FieldChanged(object? sender, PropertyChangedEventArgs args) => RecomputeLocalValidation();
    private void RequirementChanged(object? sender, PropertyChangedEventArgs args) => RecomputeLocalValidation();
    private void SetBoundedProperty(ref string field, string? value, int maximumCharacters,
        int maximumBytes, string propertyName)
    {
        var accepted = RecipeDraftInputBounds.TryAccept(value, maximumCharacters, maximumBytes, out var bounded);
        var changed = SetProperty(ref field, accepted ? bounded : string.Empty, propertyName);
        if (!changed && !accepted) OnPropertyChanged(propertyName);
    }

    private void SetCameraText(ref string field, string? value, string propertyName)
    {
        var accepted = RecipeDraftInputBounds.TryAccept(value, RecipeDraftInputBounds.NumericCharacters,
            RecipeDraftInputBounds.NumericBytes, out var bounded);
        var changed = SetProperty(ref field, accepted ? bounded : string.Empty, propertyName);
        if (accepted) _cameraInputRejected.Remove(propertyName);
        else _cameraInputRejected.Add(propertyName);
        if (!changed && !accepted) OnPropertyChanged(propertyName);
        RecomputeLocalValidation();
    }

    internal void RejectPastedInput(string propertyName)
    {
        switch (propertyName)
        {
            case nameof(RecipeKey): RecipeKey = string.Empty; break;
            case nameof(DisplayName): DisplayName = string.Empty; break;
            case nameof(CameraRole): CameraRole = string.Empty; break;
            case nameof(AlgorithmExecutionTimeoutText): AlgorithmExecutionTimeoutText = string.Empty; break;
            case nameof(ChangeReason): ChangeReason = string.Empty; break;
            case nameof(ExposureTimeUsText): RejectCameraText(ref _exposureTimeUsText, propertyName); break;
            case nameof(GainDbText): RejectCameraText(ref _gainDbText, propertyName); break;
            case nameof(RoiOffsetXText): RejectCameraText(ref _roiOffsetXText, propertyName); break;
            case nameof(RoiOffsetYText): RejectCameraText(ref _roiOffsetYText, propertyName); break;
            case nameof(RoiWidthText): RejectCameraText(ref _roiWidthText, propertyName); break;
            case nameof(RoiHeightText): RejectCameraText(ref _roiHeightText, propertyName); break;
            case nameof(AcquisitionTimeoutMsText): RejectCameraText(ref _acquisitionTimeoutMsText, propertyName); break;
            case nameof(TriggerDelayUsText): RejectCameraText(ref _triggerDelayUsText, propertyName); break;
            case nameof(WhiteBalanceRedText): RejectCameraText(ref _whiteBalanceRedText, propertyName); break;
            case nameof(WhiteBalanceGreenText): RejectCameraText(ref _whiteBalanceGreenText, propertyName); break;
            case nameof(WhiteBalanceBlueText): RejectCameraText(ref _whiteBalanceBlueText, propertyName); break;
        }
    }

    private void RejectCameraText(ref string field, string propertyName)
    {
        var changed = SetProperty(ref field, string.Empty, propertyName);
        _cameraInputRejected.Add(propertyName);
        if (!changed) OnPropertyChanged(propertyName);
        RecomputeLocalValidation();
    }

    private OperationStart? Begin(CancellationToken cancellationToken)
    {
        OperationStart start;
        lock (_sync)
        {
            if (_disposed || _isBusy) return null;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCancellation = linked;
            _operationVersion++;
            _isBusy = true;
            start = new OperationStart(_operationVersion, linked);
        }
        NotifyStateChangedOnUi();
        return start;
    }

    private void Complete(OperationStart start)
    {
        var changed = false;
        lock (_sync)
        {
            if (IsCurrentLocked(start)) { _activeCancellation = null; _isBusy = false; changed = true; }
        }
        start.Cancellation.Dispose();
        if (changed) NotifyStateChangedOnUi();
    }

    private bool IsCurrentLocked(OperationStart start) => !_disposed && _operationVersion == start.Version &&
        ReferenceEquals(_activeCancellation, start.Cancellation);

    private void SessionChanged(object? sender, InteractiveSessionChangedEventArgs args)
    {
        CancelPendingOperations();
        ClearTransientState();
        _ = _dispatcher.InvokeAsync(() =>
        {
            var session = _sessions?.Current ?? UnauthenticatedSession;
            _session = session;
            ErrorCode = session.State == InteractiveSessionState.Authenticated ? null : "RecipeDraftUnauthenticated";
            StatusMessage = "会话已变化，草稿编辑内容已清除，请重新读取。";
            NotifyStateChanged();
        });
    }

    private async Task ApplyUnavailableAsync(string errorCode, OperationStart? start = null)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            if (start.HasValue) lock (_sync) if (!IsCurrentLocked(start.Value)) return;
            _access = null;
            ErrorCode = SafeReason(errorCode, "RecipeDraftUnavailable");
            StatusMessage = "配方草稿服务暂不可用，当前内容未写入。";
            NotifyStateChanged();
        }).ConfigureAwait(true);
    }

    private async Task ApplyErrorAsync(string errorCode, OperationStart? start = null)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            if (start.HasValue) lock (_sync) if (!IsCurrentLocked(start.Value)) return;
            ErrorCode = SafeReason(errorCode, "RecipeDraftUnavailable");
            StatusMessage = ErrorCode == "RecipeDraftStepUpRequired"
                ? "保存需要当前人员再次确认；此页面尚未提供再次确认输入。"
                : "草稿操作未完成，当前内容未被 Runtime 预先修改。";
            NotifyStateChanged();
        }).ConfigureAwait(true);
    }

    private void NotifyStateChangedOnUi()
    {
        if (_dispatcher.CheckAccess) NotifyStateChanged();
        else _ = _dispatcher.InvokeAsync(NotifyStateChanged);
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CurrentSession));
        OnPropertyChanged(nameof(IsAuthenticated));
        OnPropertyChanged(nameof(IsUnavailable));
        OnPropertyChanged(nameof(DependenciesStatus));
        OnPropertyChanged(nameof(CanRelease));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ErrorCode));
        OnPropertyChanged(nameof(CurrentRevision));
        OnPropertyChanged(nameof(DraftId));
        OnPropertyChanged(nameof(ExpectedRevision));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        OpenSelectedCommand.RaiseCanExecuteChanged();
        NextHistoryCommand.RaiseCanExecuteChanged();
        ValidateCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        NewDraftCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanCreateDraft));
        OnPropertyChanged(nameof(CanOpenSelected));
        OnPropertyChanged(nameof(CanNextHistory));
        OnPropertyChanged(nameof(HistoryPage));
        OnPropertyChanged(nameof(HistoryThroughPosition));
        OnPropertyChanged(nameof(HistoryNextAfterPosition));
        OnPropertyChanged(nameof(CanValidate));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanSaveWithStepUp));
        OnPropertyChanged(nameof(RequiresStepUp));
    }

    private InteractiveSession ReadSession() { lock (_sync) return _session; }
    private static bool IsUsableSession(InteractiveSession session) =>
        session.State == InteractiveSessionState.Authenticated && session.SessionId is { } id && id != Guid.Empty &&
        !string.IsNullOrWhiteSpace(session.PrincipalId);
    private static bool SameSession(InteractiveSession left, InteractiveSession right) =>
        IsUsableSession(left) && IsUsableSession(right) && left.SessionId == right.SessionId &&
        string.Equals(left.PrincipalId, right.PrincipalId, StringComparison.Ordinal);
    private static CommandInvocation CreateInvocation(InteractiveSession session, Guid? grantId = null) =>
        new(CommandSource.PhysicalConsole, session.PrincipalId, session.SessionId, grantId);
    private static InteractiveSession UnauthenticatedSession => new(InteractiveSessionState.Unauthenticated, null, null);

    private static bool TryInt(string value, out int result) => int.TryParse(value,
        NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
    private static bool TryDouble(string value, out double result) =>
        double.TryParse(value,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out result) && double.IsFinite(result);
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static bool IsIdentifier(string value) => !string.IsNullOrEmpty(value) && value.Length <= 64 &&
        value.All(ch => ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');
    private static bool IsValidChangeReason(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        !value.Any(char.IsControl);
    private static string SafeReason(string? value, string fallback)
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "RecipeDraftEditorUnavailable", "RecipeDraftUnauthenticated", "RecipeDraftAccessDenied",
            "RecipeDraftAuthorizationUnavailable", "RecipeDraftStepUpRequired", "RecipeDraftStepUpUnavailable",
            "StepUpAuthenticationRejected", "RecipeDraftQueryFailed",
            "RecipeDraftReadFailed", "RecipeDraftSaveFailed", "RecipeDraftSaveRejected", "RecipeDraftSaved",
            "RecipeDraftSaveResultInvalid", "RecipeDraftRereadFailed", "RecipeDraftRevisionConflict",
            "RecipeDraftValidationFailed", "RecipeDraftInputInvalid", "RecipeDraftCancelled",
            "RecipeDraftSessionChanged", "RecipeDraftNotFound", "RecipeDraftContentInvalid",
            "RecipeExecutionPolicyUnavailable", "AlgorithmExecutionTimeoutInvalid",
            "AlgorithmExecutionTimeoutOutsidePolicy", "RecipeDraftAccessDenied", "RecipeDraftValid"
        };
        return value is not null && known.Contains(value) ? value : fallback;
    }
}

internal static class RecipeDraftInputBounds
{
    internal const int IdentifierCharacters = 64;
    internal const int IdentifierBytes = 64;
    internal const int DisplayNameCharacters = 128;
    internal const int ScalarTextBytes = 4096;
    internal const int SafeTextBytes = 4096;
    internal const int NumericCharacters = 64;
    internal const int NumericBytes = 128;

    internal static bool TryAccept(string? value, int maximumCharacters, int maximumBytes,
        out string bounded)
    {
        bounded = value ?? string.Empty;
        if (bounded.Length > maximumCharacters) { bounded = string.Empty; return false; }
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(bounded) > maximumBytes)
            { bounded = string.Empty; return false; }
        }
        catch (EncoderFallbackException)
        {
            bounded = string.Empty;
            return false;
        }
        return true;
    }

    internal static string Identifier(string? value) =>
        TryAccept(value, IdentifierCharacters, IdentifierBytes, out var bounded) ? bounded : string.Empty;

    internal static string Hash(string? value) =>
        TryAccept(value, IdentifierCharacters, IdentifierCharacters, out var bounded) ? bounded : string.Empty;

    internal static bool TryGetBounds(object? dataContext, string? propertyName,
        out int maximumCharacters, out int maximumBytes)
    {
        maximumCharacters = 0;
        maximumBytes = 0;
        if (dataContext is RecipeDraftFieldViewModel field)
        {
            maximumCharacters = field.InputMaxLength;
            maximumBytes = field.Type is AlgorithmScalarType.String or AlgorithmScalarType.Enum
                ? ScalarTextBytes : NumericBytes;
            return true;
        }

        if (dataContext is RecipeDraftEditorViewModel)
        {
            switch (propertyName)
            {
                case nameof(RecipeDraftEditorViewModel.RecipeKey):
                case nameof(RecipeDraftEditorViewModel.CameraRole):
                    maximumCharacters = IdentifierCharacters; maximumBytes = IdentifierBytes; return true;
                case nameof(RecipeDraftEditorViewModel.DisplayName):
                case nameof(RecipeDraftEditorViewModel.ChangeReason):
                    maximumCharacters = DisplayNameCharacters; maximumBytes = SafeTextBytes; return true;
                case nameof(RecipeDraftEditorViewModel.AlgorithmExecutionTimeoutText):
                case nameof(RecipeDraftEditorViewModel.ExposureTimeUsText):
                case nameof(RecipeDraftEditorViewModel.GainDbText):
                case nameof(RecipeDraftEditorViewModel.RoiOffsetXText):
                case nameof(RecipeDraftEditorViewModel.RoiOffsetYText):
                case nameof(RecipeDraftEditorViewModel.RoiWidthText):
                case nameof(RecipeDraftEditorViewModel.RoiHeightText):
                case nameof(RecipeDraftEditorViewModel.AcquisitionTimeoutMsText):
                case nameof(RecipeDraftEditorViewModel.TriggerDelayUsText):
                case nameof(RecipeDraftEditorViewModel.WhiteBalanceRedText):
                case nameof(RecipeDraftEditorViewModel.WhiteBalanceGreenText):
                case nameof(RecipeDraftEditorViewModel.WhiteBalanceBlueText):
                    maximumCharacters = NumericCharacters; maximumBytes = NumericBytes; return true;
            }
        }

        if (dataContext is RecipeDraftAssetRequirementViewModel or RecipeDraftPolicyRequirementViewModel)
        {
            maximumCharacters = IdentifierCharacters;
            maximumBytes = IdentifierBytes;
            return propertyName is nameof(RecipeDraftAssetRequirementViewModel.Role) or
                nameof(RecipeDraftAssetRequirementViewModel.ContractId) or
                nameof(RecipeDraftAssetRequirementViewModel.ContractVersion) or
                nameof(RecipeDraftAssetRequirementViewModel.ContractHash);
        }

        return false;
    }

    internal static bool TryComposeReplacement(string existing, int selectionStart, int selectionLength,
        string pasted, int maximumCharacters, int maximumBytes, out string replacement)
    {
        replacement = string.Empty;
        if (existing is null || pasted is null) return false;
        selectionStart = Math.Clamp(selectionStart, 0, existing.Length);
        selectionLength = Math.Clamp(selectionLength, 0, existing.Length - selectionStart);
        if ((long)pasted.Length > (long)maximumCharacters + selectionLength) return false;
        if (existing.Length > maximumCharacters + selectionLength && selectionLength != existing.Length) return false;

        try
        {
            var pastedBytes = new UTF8Encoding(false, true).GetByteCount(pasted);
            var selectedBytes = selectionLength == existing.Length
                ? 0
                : new UTF8Encoding(false, true).GetByteCount(existing.AsSpan(selectionStart, selectionLength));
            if ((long)pastedBytes > (long)maximumBytes + selectedBytes) return false;
            replacement = existing.Remove(selectionStart, selectionLength).Insert(selectionStart, pasted);
        }
        catch (EncoderFallbackException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }

        return TryAccept(replacement, maximumCharacters, maximumBytes, out replacement);
    }
}

public sealed class RecipeDraftHistoryItem
{
    public RecipeDraftHistoryItem(RecipeDraftRevision revision)
    {
        Revision = revision ?? throw new ArgumentNullException(nameof(revision));
        Label = $"{revision.Content.RecipeKey} · 修订 {revision.Revision} · {revision.RecordedAtUtc:yyyy-MM-dd HH:mm:ss} UTC";
        Hash = revision.RevisionContentHash;
    }
    public RecipeDraftRevision Revision { get; }
    public string Label { get; }
    public string Hash { get; }
}

public sealed class RecipeDraftFieldViewModel : ObservableObject
{
    private AlgorithmScalarValue? _value;
    private bool _hasValue;
    private RecipeDraftValueOrigin _origin;
    private string _inputText = string.Empty;
    private bool? _booleanInput;
    private string? _validationIssue;
    private bool _inputRejected;

    internal RecipeDraftFieldViewModel(AlgorithmFieldDefinition definition) => Definition = definition;
    public AlgorithmFieldDefinition Definition { get; }
    public string Key => Definition.Key;
    public AlgorithmScalarType Type => Definition.Type;
    public string Unit => Definition.Unit;
    public bool Required => Definition.Required;
    public string? HelpText => Definition.HelpText;
    public AlgorithmScalarConstraints? Constraints => Definition.Constraints;
    public IReadOnlyList<string> AllowedValues => Definition.Constraints?.AllowedValues ?? Array.Empty<string>();
    public bool HasChoiceValues => Definition.Constraints?.AllowedValues is not null;
    public bool HasAuthoringDefault => Definition.AuthoringDefault is not null;
    public int InputMaxLength => Definition.Type switch
    {
        AlgorithmScalarType.String or AlgorithmScalarType.Enum => Math.Max(1,
            Math.Min(RecipeDraftInputBounds.ScalarTextBytes, Definition.Constraints?.MaxLength ?? RecipeDraftInputBounds.ScalarTextBytes)),
        AlgorithmScalarType.Int64 or AlgorithmScalarType.Float64 => RecipeDraftInputBounds.NumericCharacters,
        _ => 16
    };
    public string ConstraintSummary
    {
        get
        {
            var constraints = Definition.Constraints;
            if (constraints is null) return "约束：无";
            var parts = new List<string>();
            if (constraints.MinInt64 is { } minInteger) parts.Add($"≥ {minInteger.ToString(CultureInfo.InvariantCulture)}");
            if (constraints.MaxInt64 is { } maxInteger) parts.Add($"≤ {maxInteger.ToString(CultureInfo.InvariantCulture)}");
            if (constraints.MinFloat64 is { } minFloat) parts.Add($"≥ {minFloat.ToString("R", CultureInfo.InvariantCulture)}");
            if (constraints.MaxFloat64 is { } maxFloat) parts.Add($"≤ {maxFloat.ToString("R", CultureInfo.InvariantCulture)}");
            if (constraints.MinLength is { } minLength) parts.Add($"长度≥ {minLength}");
            if (constraints.MaxLength is { } maxLength) parts.Add($"长度≤ {maxLength}");
            if (constraints.AllowedValues is { } allowed)
            {
                var values = allowed.Count == 0 ? "无" : string.Join(" / ", allowed.Take(8));
                if (allowed.Count > 8) values += $" …(+{allowed.Count - 8})";
                parts.Add($"允许值：{values}");
            }
            return parts.Count == 0 ? "约束：无" : "约束：" + string.Join("；", parts);
        }
    }
    public bool HasValue => _hasValue;
    public RecipeDraftValueOrigin Origin => _origin;
    public string OriginLabel => _origin == RecipeDraftValueOrigin.AuthoringDefault ? "Schema 默认值" : "显式值";
    public string InputText
    {
        get => _inputText;
        set => SetText(value ?? string.Empty);
    }
    public string? ChoiceValue
    {
        get => _hasValue ? _inputText : null;
        set { if (value is null) ClearValue(); else SetText(value); }
    }
    public bool? BooleanInput
    {
        get => _booleanInput;
        set { if (value is null) ClearValue(); else SetBoolean(value.Value); }
    }
    public string? ValidationIssue => _validationIssue;
    public bool IsValid => _validationIssue is null && (!_hasValue ? !Required : _value is not null);

    internal void SetValue(AlgorithmScalarValue value, RecipeDraftValueOrigin origin)
    {
        if (value is null || value.Type != Definition.Type) { ClearValue(); _validationIssue = "AlgorithmFieldTypeMismatch"; NotifyValue(); return; }
        _inputRejected = false;
        _value = value; _hasValue = true; _origin = origin;
        _inputText = value.Type switch
        {
            AlgorithmScalarType.Boolean => value.AsBoolean() ? "true" : "false",
            AlgorithmScalarType.Int64 => value.AsInt64().ToString(CultureInfo.InvariantCulture),
            AlgorithmScalarType.Float64 => value.AsFloat64().ToString("R", CultureInfo.InvariantCulture),
            AlgorithmScalarType.String => value.AsString(),
            AlgorithmScalarType.Enum => value.AsEnum(),
            _ => string.Empty
        };
        _booleanInput = value.Type == AlgorithmScalarType.Boolean ? value.AsBoolean() : null;
        _validationIssue = ValidateValue(value);
        NotifyValue();
    }

    internal void ApplyAuthoringDefault()
    {
        if (Definition.AuthoringDefault is not null) SetValue(Definition.AuthoringDefault, RecipeDraftValueOrigin.AuthoringDefault);
    }

    internal void ClearValue()
    {
        _inputRejected = false;
        _value = null; _hasValue = false; _origin = RecipeDraftValueOrigin.Explicit;
        _inputText = string.Empty; _booleanInput = null;
        _validationIssue = Required ? "AlgorithmFieldMissingRequired" : null;
        NotifyValue();
    }

    internal void RejectPastedInput()
    {
        _inputRejected = true;
        _value = null;
        _hasValue = false;
        _origin = RecipeDraftValueOrigin.Explicit;
        _inputText = string.Empty;
        _booleanInput = null;
        _validationIssue = "AlgorithmFieldInputBoundsInvalid";
        NotifyValue();
    }

    internal bool TryGetEntry(out AlgorithmConfigurationEntry entry)
    {
        if (_hasValue && _value is not null && _validationIssue is null)
        { entry = new AlgorithmConfigurationEntry(Key, Unit, _value); return true; }
        entry = null!;
        return false;
    }

    private void SetText(string value)
    {
        if (_inputRejected && value.Length == 0)
        {
            _value = null;
            _hasValue = false;
            _origin = RecipeDraftValueOrigin.Explicit;
            _inputText = string.Empty;
            _booleanInput = null;
            _validationIssue = "AlgorithmFieldInputBoundsInvalid";
            NotifyValue();
            return;
        }

        var maximumBytes = Definition.Type is AlgorithmScalarType.String or AlgorithmScalarType.Enum
            ? RecipeDraftInputBounds.ScalarTextBytes
            : RecipeDraftInputBounds.NumericBytes;
        if (!RecipeDraftInputBounds.TryAccept(value, InputMaxLength, maximumBytes, out var bounded))
        {
            _inputRejected = true;
            _value = null;
            _hasValue = false;
            _origin = RecipeDraftValueOrigin.Explicit;
            _inputText = string.Empty;
            _booleanInput = null;
            _validationIssue = "AlgorithmFieldInputBoundsInvalid";
            NotifyValue();
            return;
        }

        _inputRejected = false;
        _inputText = bounded;
        _hasValue = true;
        _origin = RecipeDraftValueOrigin.Explicit;
        _booleanInput = null;
        try
        {
            _value = Definition.Type switch
            {
                AlgorithmScalarType.Int64 when long.TryParse(value, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var integer)
                    => AlgorithmScalarValue.FromInt64(integer),
                AlgorithmScalarType.Float64 when double.TryParse(value,
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture, out var floating) && double.IsFinite(floating)
                    => AlgorithmScalarValue.FromFloat64(floating),
                AlgorithmScalarType.String => AlgorithmScalarValue.FromString(value),
                AlgorithmScalarType.Enum => AlgorithmScalarValue.FromEnum(value),
                AlgorithmScalarType.Boolean => null,
                _ => null
            };
            _validationIssue = Definition.Type == AlgorithmScalarType.Boolean
                ? "AlgorithmFieldTypeMismatch"
                : _value is null ? "AlgorithmFieldValueInvalid" : ValidateValue(_value);
        }
        catch (ArgumentException) { _value = null; _validationIssue = "AlgorithmFieldValueInvalid"; }
        NotifyValue();
    }

    private void SetBoolean(bool value)
    {
        _inputRejected = false;
        _booleanInput = value; _inputText = value ? "true" : "false";
        _hasValue = true; _origin = RecipeDraftValueOrigin.Explicit;
        _value = Definition.Type == AlgorithmScalarType.Boolean ? AlgorithmScalarValue.FromBoolean(value) : null;
        _validationIssue = Definition.Type == AlgorithmScalarType.Boolean ? null : "AlgorithmFieldTypeMismatch";
        NotifyValue();
    }

    private string? ValidateValue(AlgorithmScalarValue value)
    {
        var constraints = Definition.Constraints;
        if (constraints is null) return null;
        switch (Definition.Type)
        {
            case AlgorithmScalarType.Int64:
                var integer = value.AsInt64();
                if (constraints.MinInt64 is { } min && integer < min || constraints.MaxInt64 is { } max && integer > max)
                    return "AlgorithmFieldConstraintViolation";
                break;
            case AlgorithmScalarType.Float64:
                var floating = value.AsFloat64();
                if (constraints.MinFloat64 is { } minFloat && floating < minFloat || constraints.MaxFloat64 is { } maxFloat && floating > maxFloat)
                    return "AlgorithmFieldConstraintViolation";
                break;
            case AlgorithmScalarType.String:
            case AlgorithmScalarType.Enum:
                var text = Definition.Type == AlgorithmScalarType.String ? value.AsString() : value.AsEnum();
                if (constraints.MinLength is { } minLength && text.Length < minLength ||
                    constraints.MaxLength is { } maxLength && text.Length > maxLength ||
                    constraints.AllowedValues is not null && !constraints.AllowedValues.Contains(text, StringComparer.Ordinal))
                    return "AlgorithmFieldConstraintViolation";
                break;
        }
        return null;
    }

    private void NotifyValue()
    {
        OnPropertyChanged(nameof(InputText)); OnPropertyChanged(nameof(ChoiceValue));
        OnPropertyChanged(nameof(BooleanInput)); OnPropertyChanged(nameof(HasValue));
        OnPropertyChanged(nameof(Origin)); OnPropertyChanged(nameof(OriginLabel));
        OnPropertyChanged(nameof(ValidationIssue)); OnPropertyChanged(nameof(IsValid));
    }
}

public sealed class RecipeDraftAssetRequirementViewModel : ObservableObject
{
    private RecipeAssetKind _kind = RecipeAssetKind.Calibration;
    private string _role = "Primary";
    private string _id = string.Empty;
    private string _version = string.Empty;
    private string _hash = string.Empty;
    public RecipeAssetKind Kind { get => _kind; set => SetProperty(ref _kind, value); }
    public string Role { get => _role; set => SetProperty(ref _role, RecipeDraftInputBounds.Identifier(value)); }
    public string ContractId { get => _id; set => SetProperty(ref _id, RecipeDraftInputBounds.Identifier(value)); }
    public string ContractVersion { get => _version; set => SetProperty(ref _version, RecipeDraftInputBounds.Identifier(value)); }
    public string ContractHash { get => _hash; set => SetProperty(ref _hash, RecipeDraftInputBounds.Hash(value)); }
    internal void RejectPastedInput(string propertyName)
    {
        switch (propertyName)
        {
            case nameof(Role): Role = string.Empty; break;
            case nameof(ContractId): ContractId = string.Empty; break;
            case nameof(ContractVersion): ContractVersion = string.Empty; break;
            case nameof(ContractHash): ContractHash = string.Empty; break;
        }
    }
    internal bool TryBuild(out RecipeAssetRequirement? value, out string reason)
    {
        value = null; reason = "RecipeAssetRequirementInvalid";
        try { value = new RecipeAssetRequirement(Kind, Role, new RecipeContractReference(ContractId, ContractVersion, ContractHash)); reason = string.Empty; return true; }
        catch { return false; }
    }
    internal static RecipeDraftAssetRequirementViewModel From(RecipeAssetRequirement value) => new()
    { Kind = value.Kind, Role = value.Role, ContractId = value.Contract.Id, ContractVersion = value.Contract.Version, ContractHash = value.Contract.ContentHash };
}

public sealed class RecipeDraftPolicyRequirementViewModel : ObservableObject
{
    private RecipePolicyKind _kind = RecipePolicyKind.ImageAcquisition;
    private string _id = string.Empty;
    private string _version = string.Empty;
    private string _hash = string.Empty;
    public RecipePolicyKind Kind { get => _kind; set => SetProperty(ref _kind, value); }
    public string ContractId { get => _id; set => SetProperty(ref _id, RecipeDraftInputBounds.Identifier(value)); }
    public string ContractVersion { get => _version; set => SetProperty(ref _version, RecipeDraftInputBounds.Identifier(value)); }
    public string ContractHash { get => _hash; set => SetProperty(ref _hash, RecipeDraftInputBounds.Hash(value)); }
    internal void RejectPastedInput(string propertyName)
    {
        switch (propertyName)
        {
            case nameof(ContractId): ContractId = string.Empty; break;
            case nameof(ContractVersion): ContractVersion = string.Empty; break;
            case nameof(ContractHash): ContractHash = string.Empty; break;
        }
    }
    internal bool TryBuild(out RecipePolicyRequirement? value, out string reason)
    {
        value = null; reason = "RecipePolicyRequirementInvalid";
        try { value = new RecipePolicyRequirement(Kind, new RecipeContractReference(ContractId, ContractVersion, ContractHash)); reason = string.Empty; return true; }
        catch { return false; }
    }
    internal static RecipeDraftPolicyRequirementViewModel From(RecipePolicyRequirement value) => new()
    { Kind = value.Kind, ContractId = value.Contract.Id, ContractVersion = value.Contract.Version, ContractHash = value.Contract.ContentHash };

}
