using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

/// <summary>
/// The initial, deliberately unconfigured station authority. Later tickets supply governed
/// capabilities; no host option can assert that a missing production gate passed.
/// </summary>
public sealed partial class StationRuntime : IStationRuntime, ICameraSetupRuntime, ICameraNetworkMaintenanceRuntime,
    IImagingSetupRuntime, ICalibrationSessionQuery, ICalibrationGovernanceRuntime, ICalibrationGovernanceQuery,
    ICalibrationImportRuntime, ICalibrationImportQuery, IAsyncDisposable, IAdministratorRecoveryRuntimeGate
{
    private const int MaximumSubscribers = 64;
    private readonly object _sync = new();
    private readonly List<Channel<StationStateSnapshot>> _subscribers = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _heartbeat;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly ICommandAuditWriter? _audit;
    private readonly IInteractiveSessionService? _sessions;
    private readonly LocalAuthorizationService? _authorization;
    private readonly FrameBufferPool? _frameBufferPool;
    private readonly CameraSetupRuntime _cameraSetupRuntime;
    private readonly CameraAcquisitionService? _cameraAcquisitionService;
    private readonly CameraRecoveryService? _cameraRecoveryService;
    private readonly AlgorithmExecutionGuard _executionGuard;
    private readonly bool _algorithmExecutionRegistered;
    private readonly Task _storeInitialization;
    private Task? _completion;
    private Task? _shutdown;
    private CommandAuditFact? _pendingAudit;
    private Task<bool>? _pendingProductionStopRetirement;
    private bool LocalStopPendingLocked => Volatile.Read(ref _pendingLocalStops) != 0 ||
        _pendingAudit is not null ||
        _snapshot.LastCommand is { State: OperationState.Pending, ReasonCode: "StopAdmitted" };
    private bool _shutdownRequested;
    private bool _auditFault;
    private bool _storeReady;
    private bool _baseStoreReady;
    private int _queuedCommands;
    private int _pendingLocalStops;
    private StationStateSnapshot _snapshot;
    private long _sessionProjectionVersion;
    private bool _disposed;

    public StationRuntime(TimeSpan? heartbeatInterval = null) : this(null, heartbeatInterval, null, null, null, null, null, null, null) { }

    internal StationRuntime(ICommandAuditWriter? audit, TimeSpan? heartbeatInterval = null, IInteractiveSessionService? sessions = null,
        LocalAuthorizationService? authorization = null, FrameBufferPool? frameBufferPool = null,
        AlgorithmExecutionGuard? executionGuard = null, AlgorithmExecutionOptions? algorithmExecutionOptions = null,
        IEnumerable<ICameraProvider>? cameraProviders = null, CameraSetupOptions? cameraSetupOptions = null,
        CameraAcquisitionService? cameraAcquisitionService = null,
        CameraRecoveryService? cameraRecoveryService = null,
        CalibrationSessionOptions? calibrationSessionOptions = null,
        CalibrationProcedureRegistry? calibrationProcedures = null,
        ProductionStoreOptions? productionStoreOptions = null,
        PhysicalCalibrationVerificationRegistry? physicalCalibrationVerificationRegistry = null,
        IProductionAdmissionFactsSource? productionAdmissionFactsSource = null)
    {
        _audit = audit;
        _sessions = sessions;
        _authorization = authorization;
        _physicalCalibrationVerifiers = physicalCalibrationVerificationRegistry;
        _frameBufferPool = frameBufferPool;
        _cameraAcquisitionService = cameraAcquisitionService;
        if (cameraAcquisitionService is not null && cameraRecoveryService is not null)
            throw new ArgumentException("CameraRecoveryAcquisitionOwnershipConflict");
        _cameraRecoveryService = cameraRecoveryService;
        _executionGuard = executionGuard ?? AlgorithmExecutionGuard.CurrentProcess;
        _algorithmExecutionRegistered = algorithmExecutionOptions is not null;
        var interval = heartbeatInterval ?? TimeSpan.FromSeconds(1);
        if (interval < TimeSpan.FromMilliseconds(20) || interval > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));

        _snapshot = new StationStateSnapshot(Guid.NewGuid(), 1, DateTimeOffset.UtcNow,
            RuntimeLifecycle.Running, ExclusiveMode.None, ProductionArmState.Disarmed, false, false,
            HandshakePhase.Unknown, RecoveryState.Required, null, null,
            new CameraHealth(HealthState.Unconfigured, HealthState.Unknown, HealthState.Unknown, HealthState.Unknown),
            new PlcHealth(HealthState.Unconfigured, HealthState.Unknown, HealthState.Unknown),
            new SubsystemHealth(HealthState.Unconfigured, "TraceStoreMissing"),
            new EvidenceHealth(HealthState.Unknown, 0, 0),
            new QualificationState(QualificationMatch.Missing, QualificationMatch.Missing,
                QualificationMatch.Missing, QualificationMatch.Missing),
            new PerformanceHealth(HealthState.Unknown, false),
            new AlarmSummary(0, 0, false),
            new InteractiveSession(InteractiveSessionState.Unauthenticated, null, null), null,
            new AdmissionBlockers(new[]
            {
                "DeploymentPoliciesMissing", "CameraBindingMissing", "PlcBindingMissing", "TraceStoreMissing",
                "ActiveRecipeMissing", authorization is null ? "AuthorizationUnavailable" : "AuthorizationQualificationMissing", "StartupRecoveryNotVerified",
                "FrameworkQualificationMissing", "ProviderQualificationMissing", "PerformanceQualificationMissing",
                "StationAcceptanceMissing", "ProductionCycleUnavailable"
            }));
        var registeredProviders = cameraSetupOptions is { Providers.Count: > 0 }
            ? cameraSetupOptions.Providers
            : cameraProviders ?? Array.Empty<ICameraProvider>();
        _cameraSetupRuntime = new CameraSetupRuntime(registeredProviders,
            cameraSetupOptions ?? new CameraSetupOptions(), _audit, _sessions,
            authorization, ReadCameraStationContext, PublishCameraSetupLocked,
            authorization as ICameraSetupAuthorizer, CameraSetupPersistenceFactory.Create(_audit),
            imagingPersistence: ImagingSetupRevisionPersistenceFactory.Create(_audit));
        _cameraSetupRuntime.ConfigureNetworkMaintenance(TryReserveCameraNetworkMaintenance,
            ReleaseCameraNetworkMaintenance, PublishCameraNetworkMaintenance);
        _snapshot = ApplyAlgorithmExecutionStateLocked(_snapshot);
        _snapshot = ApplyCameraAcquisitionStateLocked(_snapshot);
        _snapshot = ApplyCameraRecoveryStateLocked(_snapshot);
        if (_sessions is not null)
        {
            lock (_sync)
            {
                _sessions.Changed += OnSessionChanged;
                var observedVersion = _sessionProjectionVersion;
                var initialSession = _sessions.Current;
                // A provider can publish while its Current getter is reconciling. Preserve
                // the handler's newer projection in that re-entrant case.
                if (_sessionProjectionVersion == observedVersion)
                    _snapshot = _snapshot with { Session = initialSession };
            }
        }
        ConfigureProductionAdmission(productionStoreOptions, productionAdmissionFactsSource);
        ConfigureCalibration(calibrationSessionOptions, calibrationProcedures, productionStoreOptions);
        ConfigureRecipeActivationStartup(productionStoreOptions?.RecipeActivations is not null);
        ConfigureRecipeSelectionStartup(productionStoreOptions?.RecipeSelections is not null);
        ConfigurePreviewStartup(productionStoreOptions?.PreviewSessions is not null);
        ConfigureManualInspectionStartup(productionStoreOptions?.ManualInspections is not null);
        ConfigureStationQualificationStartup(productionStoreOptions?.StationQualifications is not null);
        _storeInitialization = InitializeStoreAsync();
        ConfigureImageFinalization(productionStoreOptions);
        _heartbeat = PublishHeartbeatAsync(interval);
    }

    public ValueTask<StationStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ReconcileAlgorithmExecutionLocked();
            ReconcileFrameBufferPoolLocked();
            ReconcileSessionLocked();
            return ValueTask.FromResult(_snapshot);
        }
    }

    private void OnSessionChanged(object? sender, InteractiveSessionChangedEventArgs args)
    {
        lock (_sync)
        {
            if (_disposed || _shutdownRequested) return;
            _sessionProjectionVersion = checked(_sessionProjectionVersion + 1);
            var observedVersion = _sessionProjectionVersion;
            var current = _sessions!.Current;
            if (_sessionProjectionVersion != observedVersion) return;
            // Interactive identity is a separate axis. No arm/Ready/PLC/background work is changed.
            PublishLocked(_snapshot with { Session = current });
            ScheduleCalibrationSessionExitLocked(current);
            SchedulePreviewSessionExitLocked(current);
            ScheduleManualInspectionSessionExitLocked(current);
            ScheduleStationQualificationSessionExitLocked(current);
            if (current.State != InteractiveSessionState.Authenticated) _importPhysicalReservation?.Cancel();
        }
    }

    private void ReconcileSessionLocked()
    {
        if (_sessions is null || _disposed || _shutdownRequested) return;
        var observedVersion = _sessionProjectionVersion;
        var current = _sessions.Current;
        if (_sessionProjectionVersion != observedVersion) return;
        if (_snapshot.Session == current) return;
        _sessionProjectionVersion = checked(_sessionProjectionVersion + 1);
        PublishLocked(_snapshot with { Session = current });
        ScheduleCalibrationSessionExitLocked(current);
        SchedulePreviewSessionExitLocked(current);
        ScheduleManualInspectionSessionExitLocked(current);
        ScheduleStationQualificationSessionExitLocked(current);
    }

    public async IAsyncEnumerable<StationStateSnapshot> WatchSnapshotsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<StationStateSnapshot>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        lock (_sync)
        {
            if (_subscribers.Count >= MaximumSubscribers)
                throw new InvalidOperationException("SnapshotSubscriberLimit");
            ReconcileAlgorithmExecutionLocked();
            ReconcileSessionLocked();
            ReconcileFrameBufferPoolLocked();
            channel.Writer.TryWrite(_snapshot);
            if (_disposed) channel.Writer.TryComplete();
            else _subscribers.Add(channel);
        }
        try
        {
            await foreach (var snapshot in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return snapshot;
        }
        finally
        {
            lock (_sync) _subscribers.Remove(channel);
        }
    }

    public async ValueTask<RuntimeCommandOutcome> SubmitAsync(RuntimeCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command is StationQualificationCommand qualification)
            return await SubmitStationQualificationAsync(qualification, cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(command);
        if (command is CorrectProductionPartIdentityCommand partIdentityCorrection)
            return await SubmitPartIdentityCorrectionAsync(partIdentityCorrection, cancellationToken).ConfigureAwait(false);
        if (command is ManualProductionRecoveryCommand productionRecovery)
            return await SubmitProductionRecoveryAsync(productionRecovery, cancellationToken).ConfigureAwait(false);
        if (command is ManualInspectionCommand manual)
            return await SubmitManualInspectionAsync(manual, cancellationToken).ConfigureAwait(false);
        if (command is PreviewSessionCommand preview)
            return await SubmitPreviewAsync(preview, cancellationToken).ConfigureAwait(false);
        if (command is ActivateRecipeCommand activate)
            return await SubmitRecipeActivationAsync(activate, cancellationToken).ConfigureAwait(false);
        if (command is ChangePlcResultContractCommand plcContract)
            return await SubmitPlcResultContractAsync(plcContract, cancellationToken).ConfigureAwait(false);
        if (command is ChangeRecipeSelectionCommand selection)
            return await SubmitRecipeSelectionAsync(selection, cancellationToken).ConfigureAwait(false);
        if (command is ReleaseRecipeCommand release)
            return await SubmitRecipeReleaseAsync(release, cancellationToken).ConfigureAwait(false);
        if (command is AbandonRecipeDraftCommand or RetireReleasedRecipeCommand)
            return await SubmitRecipeLifecycleAsync(command, cancellationToken).ConfigureAwait(false);
        if (command is CalibrationImportCommand import)
            return await SubmitCalibrationImportAsync(import, cancellationToken).ConfigureAwait(false);
        if (command is ArmProductionCommand productionArm && ProductionAdmissionEnabled)
            return await SubmitProductionArmAsync(productionArm, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var attempt = Guid.NewGuid();
        RuntimeCommandOutcome Unavailable(string reason) => new(command.CorrelationId,
            CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, attempt);
        if (command is CalibrationGovernanceCommand && (command.CorrelationId == Guid.Empty || command.Invocation is null))
            return Unavailable("InvalidCommandContext");
        if (command is CalibrationGovernanceCommand &&
            _audit is not SqliteCommandStore { CalibrationGovernanceEnabled: true })
            return new(command.CorrelationId, CommandDisposition.Rejected, "CalibrationGovernanceUnavailable",
                AuditPersistence.NotAttempted, attempt);
        lock (_sync)
        {
            if (_shutdownRequested || _disposed) return Unavailable("RuntimeStopped");
            ReconcileAlgorithmExecutionLocked();
            ReconcileFrameBufferPoolLocked();
        }
        var localStop = command is GracefulProductionStopCommand &&
            command.Invocation?.Source == CommandSource.PhysicalConsole;
        if (localStop)
        {
            // A dedicated bounded slot cannot be consumed by ordinary commands.
            // The barrier and import cancellation share the physical-admission lock.
            lock (_sync)
            {
                if (Interlocked.CompareExchange(ref _pendingLocalStops, 1, 0) != 0)
                    return Unavailable("LocalStopAlreadyPending");
                _productionArmStopGeneration = checked(_productionArmStopGeneration + 1);
                _importPhysicalReservation?.Cancel();
            }
            CancelRecipeActivation();
            RequestPreviewStop("PreviewLocalStop");
            RequestManualInspectionStop("ManualLocalStop", abort: false);
            RequestStationQualificationStop("StationQualificationLocalStop", abort: true);
        }
        else if (Interlocked.Increment(ref _queuedCommands) > 64)
        {
            Interlocked.Decrement(ref _queuedCommands);
            MarkAuditFault("CommandQueueFull");
            return Unavailable("CommandQueueFull");
        }
        var deadline = new StoreDeadline(_audit?.CommitTimeout ?? TimeSpan.FromSeconds(2));
        var entered = false;
        try
        {
            LocalAuthorizationService.PreparedManagement? prepared = null;
            var alarmCommand = command is AcknowledgeAlarmCommand or ResetAlarmCommand;
            var cameraRecoveryCommand = command is StartCameraRecoveryCycleCommand;
            var calibrationCommand = command is StartCalibrationSessionCommand or CalibrationSessionCommand;
            var calibrationGovernanceCommand = command is CalibrationGovernanceCommand;
            var governedCommand = _authorization is not null && (alarmCommand ||
                cameraRecoveryCommand || calibrationCommand || calibrationGovernanceCommand ||
                command is IdentityManagementCommand or ArmProductionCommand or GovernedAuditChangeCommand);
            if (governedCommand)
            {
                await _storeInitialization.WaitAsync(PositiveRemaining(deadline), cancellationToken).ConfigureAwait(false);
                var preparation = _authorization!.PrepareCommandAsync(command, cancellationToken);
                // Preparation may finish after the caller deadline, but cannot mutate authority.
                // It retains its own actual capacity until completion and never blocks local Stop's gate.
                _ = preparation.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                prepared = await preparation.WaitAsync(PositiveRemaining(deadline), cancellationToken).ConfigureAwait(false);
                if (prepared.Reason is "ManagementPreparationCapacityExceeded" or "ManagementPreparationDeadlineExceeded" or "ManagementPreparationUnavailable")
                    return Unavailable(prepared.Reason);
            }
            var governancePreparation = governedCommand && command is CalibrationGovernanceCommand governanceCommand
                ? await PrepareCalibrationGovernanceAsync(governanceCommand, deadline, cancellationToken).ConfigureAwait(false)
                : null;
            while (true)
            {
                entered = await _commandGate.WaitAsync(PositiveRemaining(deadline), cancellationToken).ConfigureAwait(false);
                bool stopAhead;
                lock (_sync) stopAhead = !localStop && (Volatile.Read(ref _pendingLocalStops) > 0 ||
                    _snapshot.LastCommand is { State: OperationState.Pending, ReasonCode: "StopAdmitted" });
                if (entered && stopAhead)
                {
                    _commandGate.Release();
                    entered = false;
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (!entered || !governedCommand || _audit?.Integrity?.State != AuditIntegrityState.Verifying) break;
                // A previous command may have committed since preparation observed Verified.
                // Yield the gate while its verification settles; local Stop never waits behind this poll.
                _commandGate.Release();
                entered = false;
                using var recheck = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                recheck.CancelAfter(PositiveRemaining(deadline));
                await _authorization!.WaitForAuditAsync(recheck.Token).ConfigureAwait(false);
            }
            if (!entered)
            {
                MarkAuditFault("CommandDeadlineExceeded");
                return Unavailable("CommandDeadlineExceeded");
            }
            await _storeInitialization.WaitAsync(PositiveRemaining(deadline), cancellationToken).ConfigureAwait(false);
            bool inspectNetworkBarrier;
            lock (_sync)
            {
                // Preserve the original structural/duplicate rejection paths before
                // performing an additional protected store read for a new Arm action.
                inspectNetworkBarrier = command is ArmProductionCommand && command.CorrelationId != Guid.Empty &&
                    _snapshot.LastCommand?.CorrelationId != command.CorrelationId &&
                    command.Invocation is { } armInvocation && Enum.IsDefined(armInvocation.Source) &&
                    armInvocation.PrincipalId?.Length is not > 256;
            }
            var cameraNetworkBarrier = inspectNetworkBarrier
                ? await _cameraSetupRuntime.CheckNetworkBarrierAsync(cancellationToken).AsTask()
                    .WaitAsync(PositiveRemaining(deadline), cancellationToken).ConfigureAwait(false)
                : null;
            if (governedCommand)
            {
                string? forced;
                Guid epoch;
                lock (_sync)
                {
                    if (_shutdownRequested || _disposed) return Unavailable("RuntimeStopped");
                    ReconcileAlgorithmExecutionLocked();
                    forced = RecipeActivationConfigurationBlockedLocked ? "RecipeActivationInProgress" : cameraNetworkBarrier ?? (_snapshot.LastCommand?.State == OperationState.Pending
                        ? "OperationInProgress" : null);
                    if (command is ArmProductionCommand && _cameraNetworkMaintenanceActive)
                        forced = "CameraNetworkMaintenanceInProgress";
                    if ((cameraRecoveryCommand || command is ArmProductionCommand) && StationQualificationConfigurationBlockedLocked)
                        forced = "StationQualificationSessionInProgress";
                    if (!calibrationCommand && _snapshot.Mode == ExclusiveMode.Calibration)
                        forced = "CalibrationExclusiveWorkInProgress";
                    epoch = _snapshot.RuntimeEpoch;
                }
                var governed = calibrationCommand
                    ? await HandleCalibrationCommandAsync(command, epoch, attempt, deadline, cancellationToken).ConfigureAwait(false)
                    : calibrationGovernanceCommand
                    ? await _authorization!.HandleCalibrationGovernanceCommandAsync((CalibrationGovernanceCommand)command,
                        epoch, attempt, forced, governancePreparation!, deadline, cancellationToken).ConfigureAwait(false)
                    : cameraRecoveryCommand
                    ? await HandleCameraRecoveryCommandAsync((StartCameraRecoveryCycleCommand)command,
                        epoch, attempt, forced, deadline, cancellationToken).ConfigureAwait(false)
                    : alarmCommand
                    ? await _authorization!.HandleAlarmCommandAsync(command, epoch, attempt, forced,
                        alarms => EvaluateAlarmCommand(alarms, command), deadline, cancellationToken).ConfigureAwait(false)
                    : await _authorization!.HandleCommandAsync(command, epoch, attempt, prepared!, forced, deadline, cancellationToken).ConfigureAwait(false);
                if (governed.Audit == AuditPersistence.Unavailable) MarkAuditFault("TraceAuditUnavailable");
                if (governed.Disposition == CommandDisposition.Accepted && !cameraRecoveryCommand && !calibrationCommand)
                {
                    if (alarmCommand) await RefreshAlarmsAsync(CancellationToken.None).ConfigureAwait(false);
                    lock (_sync)
                        PublishLocked(_snapshot with { LastCommand = new CommandProgress(command.CorrelationId,
                            OperationState.Completed, governed.ReasonCode) });
                }
                return governed;
            }
            RuntimeCommandOutcome decision;
            lock (_sync)
            {
                if (_shutdownRequested || _disposed) return Unavailable("RuntimeStopped");
                decision = cameraNetworkBarrier is null ? DecideLocked(command) :
                    new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Rejected, cameraNetworkBarrier);
            }
            var fact = CreateFact(command, attempt, decision);
            var result = _audit is null || !_storeReady
                ? new StoreWriteResult(false, "TraceStoreUnavailable")
                : await _audit.AppendAsync(fact, deadline, cancellationToken).ConfigureAwait(false);
            if (!result.Committed || result.Fact is null)
            {
                MarkAuditFault(result.ReasonCode);
                return Unavailable("TraceAuditUnavailable");
            }
            fact = result.Fact;
            var outcome = new RuntimeCommandOutcome(fact.CorrelationId, fact.Disposition!.Value,
                fact.ReasonCode, AuditPersistence.Persisted, fact.AttemptId);
            lock (_sync)
            {
                if (outcome.Disposition == CommandDisposition.Accepted)
                {
                    _pendingAudit = fact;
                    // Preserve the cycle accepted by this Stop even if it retires before
                    // the heartbeat starts the completion worker.
                    _pendingProductionStopRetirement = _productionInspectionOwner is { Current: not null } production
                        ? production.CycleRetired?.Task : null;
                    _completion = null;
                    PublishLocked(_snapshot with
                    {
                        Ready = false,
                        ArmState = ProductionArmState.Disarmed,
                        LastCommand = new CommandProgress(command.CorrelationId, OperationState.Pending, outcome.ReasonCode)
                    });
                }
            }
            return outcome;
        }
        catch (TimeoutException)
        {
            MarkAuditFault("TraceCommitDeadlineExceeded");
            return Unavailable("CommandDeadlineExceeded");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            MarkAuditFault("TraceCommitDeadlineExceeded");
            return Unavailable("CommandDeadlineExceeded");
        }
        finally
        {
            if (entered) _commandGate.Release();
            if (localStop) Interlocked.Exchange(ref _pendingLocalStops, 0);
            else Interlocked.Decrement(ref _queuedCommands);
        }
    }

    private RuntimeCommandOutcome DecideLocked(RuntimeCommand command)
    {
        ReconcileAlgorithmExecutionLocked();
        ReconcileFrameBufferPoolLocked();
        RuntimeCommandOutcome Reject(string code) => new(command.CorrelationId, CommandDisposition.Rejected, code);
        if (command.CorrelationId == Guid.Empty || command.Invocation is null ||
            !Enum.IsDefined(typeof(CommandSource), command.Invocation.Source) || command.Invocation.PrincipalId?.Length > 256)
            return Reject("InvalidCommandContext");
        if (_snapshot.LastCommand?.CorrelationId == command.CorrelationId) return Reject("DuplicateCorrelationId");
        if (command is ArmProductionCommand && _cameraNetworkMaintenanceActive)
            return Reject("CameraNetworkMaintenanceInProgress");
        return command switch
        {
            AcknowledgeAlarmCommand or ResetAlarmCommand => Reject("AuthorizationUnavailable"),
            GovernedAuditChangeCommand => Reject("AuthorizationUnavailable"),
            StartCalibrationSessionCommand or CalibrationSessionCommand => Reject("AuthorizationUnavailable"),
            ArmProductionCommand => Reject(_snapshot.AdmissionBlockers[0]),
            GracefulProductionStopCommand when command.Invocation.Source != CommandSource.PhysicalConsole => Reject("LocalConsoleRequired"),
            GracefulProductionStopCommand when _snapshot.LastCommand?.State == OperationState.Pending &&
                _activationReservation is null => Reject("OperationInProgress"),
            GracefulProductionStopCommand when _snapshot.LastCommand is { State: OperationState.Completed, ReasonCode: "LocallyDisarmed" } => Reject("AlreadyLocallyDisarmed"),
            GracefulProductionStopCommand => new(command.CorrelationId, CommandDisposition.Accepted, "StopAdmitted"),
            _ => Reject("UnsupportedCommand")
        };
    }

    private CommandAuditFact CreateFact(RuntimeCommand command, Guid attempt, RuntimeCommandOutcome outcome) =>
        new(Guid.NewGuid(), attempt, command.CorrelationId, _snapshot.RuntimeEpoch, DateTimeOffset.UtcNow,
            command switch { ArmProductionCommand => AuditedCommandKind.ArmProduction,
                StartCalibrationSessionCommand => AuditedCommandKind.StartCalibrationSession,
                CaptureCalibrationFrameCommand => AuditedCommandKind.CaptureCalibrationFrame,
                ExcludeCalibrationFrameCommand => AuditedCommandKind.ExcludeCalibrationFrame,
                ComputeCalibrationCandidateCommand => AuditedCommandKind.ComputeCalibrationCandidate,
                ExitCalibrationSessionCommand => AuditedCommandKind.ExitCalibrationSession,
                PublishCalibrationAcceptancePolicyCommand => AuditedCommandKind.PublishCalibrationAcceptancePolicy,
                EvaluateCalibrationCandidateCommand => AuditedCommandKind.EvaluateCalibrationCandidate,
                PublishCalibrationProfileCommand => AuditedCommandKind.PublishCalibrationProfile,
                RecordPhysicalCalibrationVerificationCommand => AuditedCommandKind.RecordPhysicalCalibrationVerification,
                GracefulProductionStopCommand => AuditedCommandKind.GracefulProductionStop,
                GovernedAuditChangeCommand change when _audit?.Integrity is { State: not AuditIntegrityState.NotConfigured } => change.Change switch
                {
                    GovernedAuditChangeKind.RotateSigningKey => AuditedCommandKind.RotateSigningKey,
                    GovernedAuditChangeKind.RetireSigningKey => AuditedCommandKind.RetireSigningKey,
                    GovernedAuditChangeKind.CorrectHistoricalFact => AuditedCommandKind.CorrectHistoricalFact,
                    GovernedAuditChangeKind.DeleteEvidence => AuditedCommandKind.DeleteEvidence,
                    _ => AuditedCommandKind.Unsupported
                }, _ => AuditedCommandKind.Unsupported },
            command.Invocation is { } invocation && Enum.IsDefined(typeof(CommandSource), invocation.Source) ? invocation.Source : null,
            command.Invocation?.PrincipalId is { Length: <= 256 } principal ? principal : null,
            command.Invocation?.SessionId, command.Invocation?.StepUpGrantId,
            CommandAuditPhase.Outcome, outcome.Disposition, outcome.ReasonCode);

    private static TimeSpan PositiveRemaining(StoreDeadline deadline)
    {
        var remaining = deadline.Remaining;
        return remaining > TimeSpan.Zero ? remaining : throw new TimeoutException("TraceCommitDeadlineExceeded");
    }

    private async Task InitializeStoreAsync()
    {
        if (_audit is null) return;
        var result = await _audit.Initialization.ConfigureAwait(false);
        if (result.Committed) await InitializeCalibrationSessionsAsync().ConfigureAwait(false);
        lock (_sync)
        {
            _baseStoreReady = result.Committed && !_calibrationStartupBlocked;
            _storeReady = _baseStoreReady && !_activationStartupPending && !_activationStartupBlocked;
            if (_disposed) return;
            var blockers = _snapshot.AdmissionBlockers.Where(x => x != "TraceStoreMissing").ToList();
            if (!result.Committed) blockers.Add(result.ReasonCode);
            PublishLocked(_snapshot with { AuditIntegrity = _audit.Integrity, Store = _auditFault ? _snapshot.Store :
                new SubsystemHealth(result.Committed ? HealthState.Healthy : HealthState.Faulted, result.ReasonCode),
                AdmissionBlockers = new AdmissionBlockers(blockers) });
        }
        if (result.Committed)
        {
            try { await InitializeAlarmsAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { MarkAuditFault("AlarmInitializationUnavailable", alarmAuthorityUnavailable: true); }
        }
    }

    private void MarkAuditFault(string reason, bool alarmAuthorityUnavailable = false)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _auditFault = true;
            var alarmState = _snapshot.AlarmState;
            var blockers = _snapshot.AdmissionBlockers.Concat(new[] { "TraceAuditUnavailable" });
            if (alarmAuthorityUnavailable && ConfiguredAlarmPolicy is { } policy)
            {
                alarmState = new AlarmStateSnapshot(false, reason, _snapshot.RuntimeEpoch, _snapshot.Revision,
                    policy, alarmState?.Instances ?? Array.Empty<AlarmInstanceSnapshot>(),
                    alarmState?.Plc ?? new AlarmPlcProjection(Array.Empty<AlarmPlcEntry>(), 0, 0, false));
                blockers = blockers.Concat(new[] { "AlarmAuthorityUnavailable" });
            }
            PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                Store = new SubsystemHealth(HealthState.Faulted, reason),
                AlarmState = alarmState, AdmissionBlockers = new AdmissionBlockers(blockers.Distinct()) });
        }
    }

    private async Task PublishHeartbeatAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                lock (_sync)
                {
                    if (_shutdownRequested || _disposed) return;
                }

                // Camera health is sampled asynchronously by the camera setup
                // coordinator. Check shutdown before scheduling the probe so a
                // tick racing Dispose cannot start another provider call.
                _cameraSetupRuntime.Heartbeat();
                lock (_sync)
                {
                    if (_shutdownRequested || _disposed) return;
                    ReconcileAlgorithmExecutionLocked();
                    var integrity = _audit?.Integrity;
                    var integrityBlocked = integrity is { State: AuditIntegrityState.Faulted or AuditIntegrityState.Verifying };
                    var blockers = _snapshot.AdmissionBlockers.Where(x => x != "AuditIntegrityUnavailable");
                    if (integrityBlocked) blockers = blockers.Append("AuditIntegrityUnavailable");
                    var next = _snapshot with { AuditIntegrity = integrity,
                        AdmissionBlockers = new AdmissionBlockers(blockers) };
                    // The built-in source is a synchronous projection of the
                    // authoritative runtime axes.  Refresh its immutable facts on
                    // each heartbeat so store/health/recovery changes are evaluated
                    // by the fixed engine instead of merely rebinding an old report.
                    if (_productionAdmissionEnabled &&
                        _productionAdmissionFactsSource is CurrentStationFactsSource)
                        _lastAdmissionFacts = CaptureDefaultFactsLocked(next);
                    if (next.LastCommand is { State: OperationState.Pending })
                    {
                        if (_completion is null) _completion = Task.Run(CompletePendingStopAsync);
                    }
                    PublishLocked(next);
                    ScheduleAlarmMaintenanceLocked();
                    ScheduleCameraAcquisitionObservationsLocked();
                    ScheduleCameraRecoveryObservationsLocked();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task CompletePendingStopAsync()
    {
        Task? physicalRetirement;
        Task? manualRetirement;
        Task? qualificationRetirement;
        Task<bool>? productionRetirement;
        var productionStoppedNormally = true;
        lock (_sync)
        {
            physicalRetirement = _importPhysicalReservation?.Retirement;
            manualRetirement = _manualOwner?.Retired.Task;
            qualificationRetirement = _stationQualificationOwner?.Retired.Task;
            productionRetirement = _pendingProductionStopRetirement;
        }
        if (physicalRetirement is not null)
        {
            // A plugin can outlive cancellation. Keep Stop pending until its actual
            // device lease retires, without occupying the command gate or blocking shutdown.
            try { await physicalRetirement.WaitAsync(_lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        }
        if (manualRetirement is not null)
        {
            // Manual exit owns the same camera/algorithm resources and completes its
            // durable session terminal only after restoration. Wait before taking the
            // command gate so the exit worker can finish its own terminal transaction.
            try { await manualRetirement.WaitAsync(_lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        }
        if (qualificationRetirement is not null)
        {
            try { await qualificationRetirement.WaitAsync(_lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        }
        if (productionRetirement is not null)
        {
            try { productionStoppedNormally = await productionRetirement.WaitAsync(_lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        }
        await _commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            CommandAuditFact? admitted;
            lock (_sync)
            {
                if (_shutdownRequested || _disposed) return;
                admitted = _pendingAudit;
            }
            if (admitted is null) return;
            var terminal = admitted with { EventId = Guid.NewGuid(), OccurredAtUtc = DateTimeOffset.UtcNow,
                Phase = productionStoppedNormally ? CommandAuditPhase.Completed : CommandAuditPhase.Failed,
                Disposition = null, ReasonCode = productionStoppedNormally ? "LocallyDisarmed" : "ProductionStopInterrupted" };
            var result = await _audit!.AppendAsync(terminal, new StoreDeadline(_audit.CommitTimeout)).ConfigureAwait(false);
            if (!result.Committed) MarkAuditFault(result.ReasonCode);
            lock (_sync)
            {
                _pendingAudit = null;
                _pendingProductionStopRetirement = null;
                PublishLocked(_snapshot with { LastCommand = new CommandProgress(admitted.CorrelationId,
                    result.Committed && productionStoppedNormally ? OperationState.Completed : OperationState.Failed,
                    result.Committed ? terminal.ReasonCode : "TraceAuditUnavailable") });
            }
        }
        finally { _commandGate.Release(); }
    }

    private void PublishLocked(StationStateSnapshot next, bool completingProductionArm = false)
    {
        next = ApplyAlgorithmExecutionStateLocked(next);
        next = ApplyFrameBufferPoolStateLocked(next);
        next = ApplyCameraAcquisitionStateLocked(next);
        next = ApplyCameraRecoveryStateLocked(next);
        next = ProjectPreviewStateLocked(next);
        next = ProjectManualInspectionStateLocked(next);
        next = ProjectStationQualificationStateLocked(next);
        var previousAdmission = _snapshot.ProductionAdmission;
        var generationBeforeObservation = _admissionGeneration;
        if (_productionAdmissionEnabled && _productionAdmissionFactsSource is CurrentStationFactsSource)
            _lastAdmissionFacts = CaptureDefaultFactsLocked(next);
        if (_productionAdmissionEnabled && _audit?.Integrity?.State == AuditIntegrityState.Verified &&
            _lastAdmissionFacts?.RuntimeGates.TryGetValue(ProductionAdmissionGate.StoreIntegrity, out var storeGate) == true &&
            storeGate.Status == ProductionAdmissionGateStatus.Passed)
            _lastVerifiedStoreIntegrityGate = storeGate;
        ObserveProductionAdmissionStateLocked(next);
        if (_productionAdmissionEnabled && _admissionGeneration != generationBeforeObservation)
            next = next with { Ready = false, ArmState = ProductionArmState.Disarmed };
        var revision = checked(_snapshot.Revision + 1);
        var alarms = next.AlarmState is { } current
            ? new AlarmStateSnapshot(current.Available, current.ReasonCode, next.RuntimeEpoch, revision,
                current.Policy, current.Instances, current.Plc) : null;
        ProductionAdmissionReport? admission = null;
        if (next.ProductionAdmission is { } requested)
        {
            // Every published snapshot gets a fresh fixed-engine projection.  The
            // report is still immutable; only its snapshot metadata is rebound.  Do
            // not leave the field null when another material runtime axis changed.
            admission = RebindProductionAdmissionReport(requested, next.RuntimeEpoch, revision,
                _admissionGeneration);

            // A report's snapshot revision is presentation metadata.  Its effective
            // evidence is not: the fixed engine must be run again at the current UTC
            // instant so an expiring qualification cannot stay Passed merely because
            // heartbeat rebound the old report to a newer revision.  Refresh already
            // advanced the generation when captured facts changed; a time-only change
            // is detected here and advances it exactly once.
            if (previousAdmission is { } previous && admission is { } rebound &&
                !string.Equals(MaterialAdmissionEvidenceHash(previous),
                    MaterialAdmissionEvidenceHash(rebound), StringComparison.Ordinal))
            {
                if (_admissionGeneration == generationBeforeObservation)
                    _admissionGeneration = checked(_admissionGeneration + 1);
                admission = RebindProductionAdmissionReport(rebound, next.RuntimeEpoch, revision,
                    _admissionGeneration);
                next = next with { Ready = false, ArmState = ProductionArmState.Disarmed };
            }
        }
        if (admission is { CanArm: false }) next = next with { Ready = false };
        if (admission is { CanArm: false } && !(next.ArmState == ProductionArmState.Armed &&
            _lastVerifiedStoreIntegrityGate is { Status: ProductionAdmissionGateStatus.Passed } &&
            admission.Gates.All(gate => gate.Status is ProductionAdmissionGateStatus.Passed or
                ProductionAdmissionGateStatus.NotApplicable || ProductionAdmissionEngine.IsTransientAuditRecheck(gate))))
            next = next with { Ready = false, ArmState = ProductionArmState.Disarmed };
        if (completingProductionArm && (next.ArmState != ProductionArmState.Armed ||
            _productionInspectionOptions is null && !next.Ready) &&
            next.LastCommand is { } armProgress)
            next = next with { LastCommand = armProgress with
                { State = OperationState.Failed, ReasonCode = "ProductionAdmissionChanged" } };
        var published = next with { Revision = revision, ObservedAtUtc = DateTimeOffset.UtcNow,
            AlarmState = alarms, ProductionAdmission = admission };
        _snapshot = published;
        if (_productionInspectionOwner is { Current: null, Observer: { } productionObserver } &&
            (!published.Ready || published.ArmState != ProductionArmState.Armed || admission?.CanArm != true) &&
            !PreserveProductionArmReadyObservationLocked(published, admission))
            productionObserver.RejectPendingAdmission("ProductionTriggerPermitRevoked");
        if (_productionAdmissionEnabled)
            _admissionStateHash = ComputeAdmissionStateHash(published);
        foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(_snapshot);
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync) return new ValueTask(_shutdown ??= ShutdownAsync());
    }

    private async Task ShutdownAsync()
    {
        // Stop admitting new work immediately. A terminal transaction that already owns
        // the command gate settles first; shutdown never rewrites an immutable terminal fact.
        // Pending work without an in-flight terminal is resolved as RuntimeStopped below.
        _shutdownRequested = true;
        if (_productionInspectionOptions is not null)
            _productionShutdownDeadline = new StoreDeadline(_productionInspectionOptions.RetirementTimeout);
        RequestProductionInspectionAbort("ProductionInspectionRuntimeShutdown");
        PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed });
        RequestStationQualificationStop("StationQualificationRuntimeShutdown", abort: true);
        if (_productionAdmissionEnabled && _audit is SqliteCommandStore admissionStore)
            admissionStore.ProductionAdmissionMaterialChanging -= OnProductionAdmissionMaterialChanging;
        if (_sessions is not null) _sessions.Changed -= OnSessionChanged;
        // A controlled close cancels computation first. Keep durable delivery and the
        // ACK observer alive for the accepted cycle, under one monotonic deadline.
        try { await ShutdownProductionInspectionAsync().ConfigureAwait(false); }
        finally { _lifetime.Cancel(); }
        await ShutdownImageFinalizationAsync().ConfigureAwait(false);
        await _heartbeat.ConfigureAwait(false);
        await ShutdownProductionRecoveryAsync().ConfigureAwait(false);
        await ShutdownManualInspectionAsync().ConfigureAwait(false);
        await ShutdownStationQualificationAsync().ConfigureAwait(false);
        await ShutdownPreviewAsync().ConfigureAwait(false);
        await ShutdownRecipeActivationAsync().ConfigureAwait(false);
        // Activation owns its camera transaction through bounded restoration.
        // Cancelling the camera lifetime first could synchronously enter provider
        // callbacks and prevent the activation shutdown budget from taking effect.
        CancelCameraSetupOperations();
        await _commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Admission owns the same gate through durable acceptance and coordinator
            // publication. Closing it first prevents shutdown missing a late session.
            await ShutdownCalibrationAsync().ConfigureAwait(false);
            await _cameraSetupRuntime.DisposeAsync().ConfigureAwait(false);
            if (_pendingAudit is { } pending)
            {
                var result = await _audit!.AppendAsync(pending with { EventId = Guid.NewGuid(), OccurredAtUtc = DateTimeOffset.UtcNow,
                    Phase = CommandAuditPhase.Failed, Disposition = null, ReasonCode = "RuntimeStopped" },
                    new StoreDeadline(_audit.CommitTimeout)).ConfigureAwait(false);
                if (!result.Committed) MarkAuditFault(result.ReasonCode);
                _pendingAudit = null;
            }
        lock (_sync)
        {
            _disposed = true;
            var lastCommand = _snapshot.LastCommand;
            if (lastCommand is { State: OperationState.Pending })
                lastCommand = lastCommand with { State = OperationState.Failed, ReasonCode = _auditFault ? "TraceAuditUnavailable" : "RuntimeStopped" };
            PublishLocked(_snapshot with { Lifecycle = RuntimeLifecycle.Stopped, Ready = false,
                ArmState = ProductionArmState.Disarmed, LastCommand = lastCommand });
            foreach (var subscriber in _subscribers) subscriber.Writer.TryComplete();
            _subscribers.Clear();
        }
        }
        finally { _commandGate.Release(); }
        if (_completion is not null) await _completion.ConfigureAwait(false);
        await _storeInitialization.ConfigureAwait(false);
        if (_alarmMaintenance is not null) await _alarmMaintenance.ConfigureAwait(false);
        if (_cameraAcquisitionObservation is not null) await _cameraAcquisitionObservation.ConfigureAwait(false);
        if (_cameraRecoveryObservation is not null) await _cameraRecoveryObservation.ConfigureAwait(false);
        if (_calibrationTransfers is not null) await _calibrationTransfers.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
