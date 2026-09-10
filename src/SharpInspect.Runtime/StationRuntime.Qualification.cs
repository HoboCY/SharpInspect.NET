using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Qualification;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime : IStationQualificationSessionService
{
    private StationQualificationSessionOptions? _stationQualificationOptions;
    private StationQualificationPlan? _stationQualificationPlan;
    private IStationQualificationFacility? _stationQualificationFacility;
    private ProductionStoreOptions? _stationQualificationStoreOptions;
    private AlgorithmPreparationService? _stationQualificationPreparation;
    private AlgorithmExecutionOptions? _stationQualificationExecutionOptions;
    private IFrameAcquisitionClock? _stationQualificationClock;
    private StationQualificationOwner? _stationQualificationOwner;
    private StationQualificationSessionSnapshot? _stationQualificationSnapshot;
    private bool _stationQualificationStartupPending;
    private bool _stationQualificationRecoveryBlocked;
    private bool _stationQualificationAdmissionPending;
    private bool _stationQualificationAdmissionStopRequested;
    private Task? _stationQualificationStartupTask;
    private long _stationQualificationRevision;
    private long _stationQualificationPhysicalSequence;

    private bool StationQualificationConfigurationBlockedLocked => _stationQualificationStartupPending ||
        _stationQualificationRecoveryBlocked || _stationQualificationAdmissionPending || _stationQualificationOwner is not null;

    private void ConfigureStationQualificationStartup(bool configured)
    {
        if (!configured) return;
        _stationQualificationStartupPending = true;
        _snapshot = ProjectStationQualificationStateLocked(_snapshot);
    }

    internal void ConfigureStationQualificationSessions(StationQualificationSessionOptions options,
        StationQualificationPlan? plan, IStationQualificationFacility? facility,
        AlgorithmPreparationService? preparation, AlgorithmExecutionOptions? executionOptions,
        ProductionStoreOptions storeOptions, IFrameAcquisitionClock? acquisitionClock)
    {
        options.Validate();
        if (executionOptions is not null && executionOptions.Policy.ContentHash != storeOptions.RecipeDrafts?.ExecutionPolicy.ContentHash)
            throw new ArgumentException("StationQualificationExecutionPolicyMismatch");
        lock (_sync)
        {
            if (_stationQualificationOptions is not null) throw new InvalidOperationException("StationQualificationAlreadyConfigured");
            _stationQualificationOptions = options;
            _stationQualificationPlan = plan;
            _stationQualificationFacility = facility;
            _stationQualificationPreparation = preparation;
            _stationQualificationExecutionOptions = executionOptions;
            _stationQualificationStoreOptions = storeOptions;
            _stationQualificationClock = acquisitionClock;
            _stationQualificationSnapshot = new(_snapshot.RuntimeEpoch, ++_stationQualificationRevision, null,
                StationQualificationSessionPhase.Idle, "StationQualificationIdle", DateTimeOffset.UtcNow,
                null, null, null, null, null, StationQualificationRestorationState.NotRequired, false, false, null);
            _stationQualificationStartupTask = Task.Run(InitializeStationQualificationAsync);
        }
    }

    async ValueTask<StationQualificationAccess> IStationQualificationSessionService.GetAccessAsync(
        CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (_stationQualificationOptions is null || _authorization is null)
            return new(false, "StationQualificationUnavailable", true);
        return await _authorization.GetStationQualificationAccessAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<StationQualificationSessionReadResult> IStationQualificationSessionService.GetSnapshotAsync(
        CommandInvocation invocation, CancellationToken cancellationToken)
    {
        var access = await ((IStationQualificationSessionService)this).GetAccessAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (!access.CanRun) return new(false, access.ReasonCode, null);
        lock (_sync)
        {
            if (_stationQualificationOwner is { } owner && invocation.SessionId != owner.Header.ActorSessionId)
                return new(false, "StationQualificationSessionActorChanged", null);
            return _stationQualificationSnapshot is { } state ? new(true, state.ReasonCode, state) :
                new(false, "StationQualificationUnavailable", null);
        }
    }

    private string? CheckStationQualificationStartLocked(StartStationQualificationSessionCommand command)
    {
        if (_stationQualificationOptions is null || _stationQualificationPlan is null ||
            _stationQualificationFacility is null || _stationQualificationPreparation is null ||
            _stationQualificationExecutionOptions is null || _stationQualificationClock is null ||
            _frameBufferPool is null || _authorization is null || _audit is not SqliteCommandStore)
            return "StationQualificationUnavailable";
        if (_stationQualificationPlan.ContentHash != command.Plan.ContentHash ||
            _stationQualificationFacility.Identity != command.Plan.Harness) return "StationQualificationFrozenPlanMismatch";
        if (_disposed || _shutdownRequested) return "RuntimeStopped";
        if (_stationQualificationStartupPending) return "StationQualificationStartupRecoveryPending";
        if (_stationQualificationRecoveryBlocked) return "StationQualificationRecoveryRequired";
        if (_stationQualificationOwner is not null || _stationQualificationAdmissionPending)
            return "StationQualificationSessionInProgress";
        if (!_storeReady || _auditFault) return "StationQualificationAuditUnavailable";
        if (!IsAlgorithmHungAlarmMappingValid(ConfiguredAlarmPolicy) || !IsFrameBufferAlarmMappingValid(ConfiguredAlarmPolicy))
            return "StationQualificationAlarmMappingUnavailable";
        if (HasFrameBufferFaultLocked()) return "StationQualificationFramePoolUnavailable";
        if (_snapshot.AlarmState?.Instances.Any(value => value.ProductionImpact == ProductionImpact.FaultAbort) == true)
            return "StationQualificationFaultAbortActive";
        if (_snapshot.Ready || _snapshot.ArmState != ProductionArmState.Disarmed)
            return "StationQualificationRequiresDisarmedStation";
        if (RecipeActivationConfigurationBlockedLocked || PreviewConfigurationBlockedLocked ||
            ManualInspectionConfigurationBlockedLocked || _importPhysicalReservation is not null)
            return "StationQualificationOwnerConflict";
        if (_snapshot.Busy || _snapshot.CurrentExecution is not null || _executionGuard.IsHung)
            return "StationQualificationExecutionConflict";
        if (_snapshot.Evidence.PendingDeliveries != 0 || _snapshot.Evidence.PendingRequiredImages != 0 ||
            _snapshot.Handshake is HandshakePhase.AwaitingResultAck or HandshakePhase.AwaitingAckReset)
            return "StationQualificationDeliveryConflict";
        // A missing station acceptance keeps Recovery.Required, and must not prevent
        // qualification. Actual stopped-line evidence is checked at the facility.
        if (_snapshot.Mode != ExclusiveMode.None || _snapshot.Recovery == RecoveryState.InProgress ||
            _cameraNetworkMaintenanceActive || _calibrationAdmissionInProgress ||
            _calibrationWork is { IsCompleted: false } || _snapshot.LastCommand?.State == OperationState.Pending)
            return "StationQualificationWorkflowConflict";
        if (_cameraSetupRuntime.ConfigurationMutationInProgress || _cameraAcquisitionService is not null ||
            _cameraRecoveryService is not null) return "StationQualificationCameraOwnerConflict";
        if (Volatile.Read(ref _pendingLocalStops) != 0) return "StationQualificationLocalStopInProgress";
        return null;
    }

    private StationStateSnapshot ProjectStationQualificationStateLocked(StationStateSnapshot next)
    {
        if (!StationQualificationConfigurationBlockedLocked) return next;
        var reason = _stationQualificationRecoveryBlocked ? "StationQualificationRecoveryRequired" :
            _stationQualificationStartupPending ? "StationQualificationStartupRecoveryPending" : "StationQualificationSessionInProgress";
        return next with
        {
            Mode = ExclusiveMode.Qualification, Ready = false, ArmState = ProductionArmState.Disarmed,
            Recovery = _stationQualificationRecoveryBlocked ? RecoveryState.Required : next.Recovery,
            AdmissionBlockers = new(next.AdmissionBlockers.Where(code => code is not
                "StationQualificationRecoveryRequired" and not "StationQualificationStartupRecoveryPending" and not
                "StationQualificationSessionInProgress").Append(reason).Distinct(StringComparer.Ordinal))
        };
    }
}
