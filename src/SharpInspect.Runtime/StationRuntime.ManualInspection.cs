using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Manual;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime : IManualInspectionSessionService
{
    private ManualInspectionSessionOptions? _manualOptions;
    private ProductionStoreOptions? _manualStoreOptions;
    private ManualRecipeResolver? _manualResolver;
    private AlgorithmPreparationService? _manualPreparation;
    private AlgorithmExecutionOptions? _manualExecutionOptions;
    private IFrameAcquisitionClock? _manualAcquisitionClock;
    private ManualInspectionOwner? _manualOwner;
    private ManualInspectionSessionSnapshot? _manualSnapshot;
    private bool _manualStartupPending;
    private bool _manualRecoveryBlocked;
    private bool _manualAdmissionPending;
    private bool _manualAdmissionStopRequested;
    private bool _manualAdmissionAbortRequested;
    private Task? _manualStartupTask;
    private long _manualRevision;
    private long _manualPhysicalSequence;

    private bool ManualInspectionConfigurationBlockedLocked => _manualStartupPending ||
        _manualRecoveryBlocked || _manualAdmissionPending || _manualOwner is not null;

    private void ConfigureManualInspectionStartup(bool configured)
    {
        if (!configured) return;
        _manualStartupPending = true;
        _snapshot = ProjectManualInspectionStateLocked(_snapshot);
    }

    internal void ConfigureManualInspectionSessions(ManualInspectionSessionOptions options,
        RecipeDraftService drafts, IReleasedRecipeQuery? releases,
        AlgorithmPreparationService? preparation, AlgorithmPreparationOptions? preparationOptions,
        AlgorithmExecutionOptions? executionOptions, ProductionStoreOptions storeOptions,
        IFrameAcquisitionClock? acquisitionClock)
    {
        options.Validate();
        ArgumentNullException.ThrowIfNull(drafts);
        ArgumentNullException.ThrowIfNull(storeOptions);
        if (preparationOptions is not null && options.PreparationTimeout > preparationOptions.MaximumPreparationTimeout)
            throw new ArgumentException("ManualPreparationBudgetExceedsPolicy");
        if (executionOptions is not null && executionOptions.Policy.ContentHash != storeOptions.RecipeDrafts?.ExecutionPolicy.ContentHash)
            throw new ArgumentException("ManualExecutionPolicyMismatch");
        lock (_sync)
        {
            if (_manualOptions is not null) throw new InvalidOperationException("ManualInspectionAlreadyConfigured");
            _manualOptions = options;
            _manualStoreOptions = storeOptions;
            _manualResolver = new(drafts, releases);
            _manualPreparation = preparation;
            _manualExecutionOptions = executionOptions;
            _manualAcquisitionClock = acquisitionClock;
            if (IsManualInspectionAlarmMappingValid()) _registeredAlarmSources.Add(ManualInspectionAlarmSource);
            _manualSnapshot = new(_snapshot.RuntimeEpoch, ++_manualRevision, null,
                ManualInspectionSessionPhase.Idle, "ManualInspectionIdle", DateTimeOffset.UtcNow,
                null, null, null, null, null, ManualInspectionRestorationState.NotRequired, false, false, null);
            _manualStartupTask = Task.Run(InitializeManualInspectionAsync);
        }
    }

    async ValueTask<ManualInspectionAccess> IManualInspectionSessionService.GetAccessAsync(
        CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (_manualOptions is null || _authorization is null)
            return new(false, "ManualInspectionUnavailable", false);
        return await _authorization.GetManualInspectionAccessAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<ManualInspectionSessionReadResult> IManualInspectionSessionService.GetSnapshotAsync(
        CommandInvocation invocation, CancellationToken cancellationToken)
    {
        var access = await ((IManualInspectionSessionService)this).GetAccessAsync(invocation, cancellationToken)
            .ConfigureAwait(false);
        if (!access.CanRun) return new(false, access.ReasonCode, null);
        lock (_sync)
        {
            if (_manualOwner is { } owner && invocation.SessionId != owner.ActorSessionId)
                return new(false, "ManualInspectionSessionActorChanged", null);
            return _manualSnapshot is { } state ? new(true, state.ReasonCode, state) :
                new(false, "ManualInspectionUnavailable", null);
        }
    }

    private string? CheckManualInspectionStartLocked()
    {
        if (_manualOptions is null || _manualResolver is null || _manualPreparation is null ||
            _manualExecutionOptions is null || _manualAcquisitionClock is null || _frameBufferPool is null || _authorization is null ||
            _audit is not SqliteCommandStore) return "ManualInspectionUnavailable";
        if (_disposed || _shutdownRequested) return "RuntimeStopped";
        if (_manualStartupPending) return "ManualInspectionStartupRecoveryPending";
        if (_manualRecoveryBlocked) return "ManualInspectionRecoveryRequired";
        if (_manualOwner is not null || _manualAdmissionPending) return "ManualInspectionSessionInProgress";
        if (!_storeReady || _auditFault) return "ManualInspectionAuditUnavailable";
        if (!IsManualInspectionAlarmMappingValid() || !IsAlgorithmHungAlarmMappingValid(ConfiguredAlarmPolicy) ||
            !IsFrameBufferAlarmMappingValid(ConfiguredAlarmPolicy)) return "ManualInspectionAlarmMappingUnavailable";
        if (HasFrameBufferFaultLocked()) return "ManualInspectionFramePoolUnavailable";
        if (_snapshot.AlarmState?.Instances.Any(value => value.ProductionImpact == ProductionImpact.FaultAbort) == true)
            return "ManualInspectionFaultAbortActive";
        if (_snapshot.Ready || _snapshot.ArmState != ProductionArmState.Disarmed)
            return "ManualInspectionRequiresDisarmedStation";
        if (RecipeActivationConfigurationBlockedLocked) return "RecipeActivationInProgress";
        if (PreviewConfigurationBlockedLocked) return "PreviewSessionInProgress";
        if (_importPhysicalReservation is not null) return "CalibrationImportPhysicalVerificationInProgress";
        if (_snapshot.Busy || _snapshot.CurrentExecution is not null || _executionGuard.IsHung)
            return "ManualInspectionExecutionConflict";
        if (_snapshot.Evidence.PendingDeliveries != 0 || _snapshot.Evidence.PendingRequiredImages != 0 ||
            _snapshot.Handshake is HandshakePhase.AwaitingResultAck or HandshakePhase.AwaitingAckReset)
            return "ManualInspectionDeliveryConflict";
        if (_snapshot.Mode != ExclusiveMode.None || _snapshot.Recovery == RecoveryState.InProgress ||
            _cameraNetworkMaintenanceActive || _calibrationAdmissionInProgress ||
            _calibrationWork is { IsCompleted: false } || _snapshot.LastCommand?.State == OperationState.Pending)
            return "ManualInspectionWorkflowConflict";
        if (_cameraSetupRuntime.ConfigurationMutationInProgress || _cameraAcquisitionService is not null ||
            _cameraRecoveryService is not null) return "ManualInspectionCameraOwnerConflict";
        if (Volatile.Read(ref _pendingLocalStops) != 0) return "ManualInspectionLocalStopInProgress";
        return null;
    }

    private StationStateSnapshot ProjectManualInspectionStateLocked(StationStateSnapshot next)
    {
        if (!ManualInspectionConfigurationBlockedLocked) return next;
        var reason = _manualRecoveryBlocked ? "ManualInspectionRecoveryRequired" : _manualStartupPending ?
            "ManualInspectionStartupRecoveryPending" : "ManualInspectionSessionInProgress";
        return next with
        {
            Mode = ExclusiveMode.ManualInspection, Ready = false, ArmState = ProductionArmState.Disarmed,
            Recovery = _manualRecoveryBlocked ? RecoveryState.Required : next.Recovery,
            AdmissionBlockers = new(next.AdmissionBlockers.Where(code => code is not
                "ManualInspectionRecoveryRequired" and not "ManualInspectionStartupRecoveryPending" and not
                "ManualInspectionSessionInProgress").Append(reason).Distinct(StringComparer.Ordinal))
        };
    }
}
