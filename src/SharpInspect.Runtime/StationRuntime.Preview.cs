using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Preview;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime : IPreviewSessionService
{
    private PreviewSessionOptions? _previewOptions;
    private RecipeDraftService? _previewDrafts;
    private ProductionStoreOptions? _previewStoreOptions;
    private PreviewSessionOwner? _previewOwner;
    private PreviewSessionSnapshot? _previewSnapshot;
    private bool _previewStartupPending;
    private bool _previewRecoveryBlocked;
    private bool _previewAdmissionPending;
    private bool _previewAdmissionStopRequested;
    private Task? _previewStartupTask;
    private long _previewRevision;
    private long _previewPhysicalSequence;
    private bool PreviewConfigurationBlockedLocked => _previewStartupPending || _previewRecoveryBlocked ||
        _previewOwner is not null || _previewAdmissionPending;

    private void ConfigurePreviewStartup(bool configured)
    {
        if (!configured) return;
        _previewStartupPending = true;
        _snapshot = ProjectPreviewStateLocked(_snapshot);
    }

    internal void ConfigurePreviewSessions(PreviewSessionOptions options, RecipeDraftService drafts,
        ProductionStoreOptions storeOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(drafts);
        ArgumentNullException.ThrowIfNull(storeOptions);
        options.Validate();
        lock (_sync)
        {
            if (_previewOptions is not null) throw new InvalidOperationException("PreviewAlreadyConfigured");
            _previewOptions = options;
            _previewDrafts = drafts;
            _previewStoreOptions = storeOptions;
            if (IsPreviewAlarmMappingValid()) _registeredAlarmSources.Add(PreviewAlarmSource);
            _previewSnapshot = new(_snapshot.RuntimeEpoch, ++_previewRevision, null,
                PreviewSessionPhase.Idle, "PreviewIdle", null, null, null, null,
                PreviewRestorationState.NotRequired, false, null, null);
            _previewStartupTask = Task.Run(InitializePreviewAsync);
        }
    }

    async ValueTask<PreviewSessionAccess> IPreviewSessionService.GetAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (_previewOptions is null || _authorization is null)
            return new(false, "PreviewUnavailable", false);
        return await _authorization.GetPreviewAccessAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<PreviewSessionReadResult> IPreviewSessionService.GetSnapshotAsync(CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        var access = await ((IPreviewSessionService)this).GetAccessAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (!access.CanRun) return new(false, access.ReasonCode, null);
        lock (_sync)
        {
            if (_previewOwner is { } owner && invocation.SessionId != owner.Header.ActorSessionId)
                return new(false, "PreviewSessionActorChanged", null);
            return _previewSnapshot is { } state ? new(true, state.ReasonCode, state) :
                new(false, "PreviewUnavailable", null);
        }
    }

    private string? CheckPreviewStartLocked()
    {
        if (_previewOptions is null || _previewDrafts is null || _authorization is null || _audit is not SqliteCommandStore)
            return "PreviewUnavailable";
        if (_disposed || _shutdownRequested) return "RuntimeStopped";
        if (_previewStartupPending) return "PreviewStartupRecoveryPending";
        if (_previewRecoveryBlocked) return "PreviewRecoveryRequired";
        if (_previewOwner is not null || _previewAdmissionPending) return "PreviewSessionInProgress";
        if (!_storeReady || _auditFault) return "PreviewAuditUnavailable";
        if (!IsPreviewAlarmMappingValid()) return "PreviewAlarmMappingUnavailable";
        if (RecipeActivationConfigurationBlockedLocked) return "RecipeActivationInProgress";
        if (_snapshot.Busy || _snapshot.CurrentExecution is not null || _executionGuard.IsHung)
            return "PreviewExecutionConflict";
        if (_snapshot.Evidence.PendingDeliveries != 0 || _snapshot.Evidence.PendingRequiredImages != 0 ||
            _snapshot.Handshake is HandshakePhase.AwaitingResultAck or HandshakePhase.AwaitingAckReset)
            return "PreviewDeliveryConflict";
        if (_snapshot.Mode != ExclusiveMode.None || _snapshot.Recovery == RecoveryState.InProgress ||
            _cameraNetworkMaintenanceActive || _calibrationAdmissionInProgress || _calibrationWork is { IsCompleted: false } ||
            _snapshot.LastCommand?.State == OperationState.Pending)
            return "PreviewWorkflowConflict";
        if (_cameraSetupRuntime.ConfigurationMutationInProgress || _cameraAcquisitionService is not null ||
            _cameraRecoveryService is not null)
            return "PreviewCameraOwnerConflict";
        if (Volatile.Read(ref _pendingLocalStops) != 0) return "PreviewLocalStopInProgress";
        return null;
    }

    private RecipeActivationPhysicalPhaseClaim ClaimPreviewPhysicalPhase(PreviewSessionOwner owner,
        Guid? commandCorrelation)
    {
        // Snapshot publication may briefly hold this lock. The claim runs outside
        // camera locks and never performs I/O, so serialize that contention rather
        // than interpreting it as a failed physical operation.
        lock (_sync)
        {
            if (!ReferenceEquals(_previewOwner, owner) || owner.ExitRequested || _shutdownRequested || _disposed)
                return RecipeActivationPhysicalPhaseClaim.Unavailable("PreviewSessionStopping");
            if (Volatile.Read(ref _pendingLocalStops) != 0)
                return RecipeActivationPhysicalPhaseClaim.Unavailable("PreviewLocalStopInProgress");
            if (owner.PhysicalPhaseId != 0)
                return RecipeActivationPhysicalPhaseClaim.Unavailable("PreviewPhysicalOperationInProgress");
            if (commandCorrelation is null ? owner.PendingCommand is not null :
                owner.PendingCommand?.CorrelationId != commandCorrelation)
                return RecipeActivationPhysicalPhaseClaim.Unavailable("PreviewOperationSuperseded");
            var id = checked(++_previewPhysicalSequence);
            owner.PhysicalPhaseId = id;
            return RecipeActivationPhysicalPhaseClaim.Granted(id, () =>
            {
                lock (_sync)
                    if (ReferenceEquals(_previewOwner, owner) && owner.PhysicalPhaseId == id)
                        owner.PhysicalPhaseId = 0;
            });
        }
    }

    private StationStateSnapshot ProjectPreviewStateLocked(StationStateSnapshot next)
    {
        if (!_previewStartupPending && !_previewRecoveryBlocked && _previewOwner is null)
            return next;
        var reason = _previewRecoveryBlocked ? "PreviewRecoveryRequired" : _previewStartupPending ?
            "PreviewStartupRecoveryPending" : "PreviewSessionInProgress";
        return next with
        {
            Mode = ExclusiveMode.Preview, Ready = false, ArmState = ProductionArmState.Disarmed,
            Recovery = _previewRecoveryBlocked ? RecoveryState.Required : next.Recovery,
            AdmissionBlockers = new(next.AdmissionBlockers.Where(code => code is not
                "PreviewRecoveryRequired" and not "PreviewStartupRecoveryPending" and not "PreviewSessionInProgress")
                .Append(reason).Distinct(StringComparer.Ordinal))
        };
    }

    private void PublishPreviewLocked(PreviewSessionOwner owner, PreviewSessionPhase phase, string reason,
        CameraPreviewFrame? frame = null)
    {
        if (!ReferenceEquals(_previewOwner, owner)) return;
        _previewSnapshot = new(_snapshot.RuntimeEpoch, checked(++_previewRevision), owner.Header.SessionId,
            phase, reason, PreviewDraftReference.FromRevision(owner.Draft), owner.Configuration, owner.FrozenSettings,
            frame, owner.Restoration, _previewRecoveryBlocked, owner.PendingCommand?.CorrelationId ?? owner.StartFact.CorrelationId,
            owner.LastSavedDraft);
        PublishLocked(_snapshot);
    }

    private sealed class PreviewSessionOwner
    {
        internal PreviewSessionOwner(PreviewSessionHeader header, CommandAuditFact startFact,
            RecipeDraftRevision draft, RecipeActivationRecord? baseline)
        { Header = header; StartFact = startFact; Draft = draft; Baseline = baseline; }
        internal PreviewSessionHeader Header { get; set; }
        internal CommandAuditFact StartFact { get; }
        internal RecipeDraftRevision Draft { get; set; }
        internal RecipeActivationRecord? Baseline { get; }
        internal RecipeActivationCameraLease? Camera { get; set; }
        internal PreviewTuningConfiguration? Configuration { get; set; }
        internal PreviewCameraProcessSettings? FrozenSettings { get; set; }
        internal PreviewDraftReference? LastSavedDraft { get; set; }
        internal PreviewRestorationState Restoration { get; set; } = PreviewRestorationState.Pending;
        internal PreviewSessionCommand? PendingCommand { get; set; }
        internal CommandAuditFact? PendingFact { get; set; }
        internal ExitPreviewSessionCommand? ExitCommand { get; set; }
        internal CommandAuditFact? ExitFact { get; set; }
        internal CancellationTokenRegistration CallerCancellation { get; set; }
        internal Task? Operation { get; set; }
        internal Task? ExitWorker { get; set; }
        internal int TerminalStarted;
        internal Task? Reader { get; set; }
        internal CancellationTokenSource? ReadCancellation { get; set; }
        internal CancellationTokenSource ExitCancellation { get; } = new();
        internal TaskCompletionSource<bool> Retired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool ExitRequested { get; set; }
        internal string ExitReason { get; set; } = "PreviewSessionExited";
        internal long PhysicalPhaseId { get; set; }
        internal long LastFrameSequence { get; set; }
    }
}
