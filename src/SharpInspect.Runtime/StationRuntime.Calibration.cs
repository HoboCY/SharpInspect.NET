using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private CalibrationSessionOptions? _calibrationOptions;
    private CalibrationProcedureRegistry? _calibrationProcedures;
    private ProductionStoreOptions? _calibrationStoreOptions;
    private CalibrationFrameEvidenceStore? _calibrationImages;
    private CalibrationSessionCoordinator? _calibrationCoordinator;
    private Task? _calibrationWork;
    private Guid? _calibrationOperationCorrelation;
    private int _calibrationQueries;
    private bool _calibrationExitPending;
    private bool _calibrationAdmissionInProgress;
    private bool _calibrationAuthorityExitDeferred;

    private void ConfigureCalibration(CalibrationSessionOptions? options,
        CalibrationProcedureRegistry? procedures, ProductionStoreOptions? store)
    {
        options?.Validate();
        _calibrationOptions = options;
        _calibrationProcedures = procedures;
        _calibrationStoreOptions = store;
        if (store?.CalibrationSessions is { } evidence)
            _calibrationImages = new CalibrationFrameEvidenceStore(evidence,
                options?.OperationTimeout ?? TimeSpan.FromSeconds(5));
    }

    private async ValueTask<RuntimeCommandOutcome> HandleCalibrationCommandAsync(RuntimeCommand command,
        Guid epoch, Guid attempt, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        lock (_sync) _calibrationAdmissionInProgress = true;
        try
        {
            return await HandleCalibrationAdmissionAsync(command, epoch, attempt, deadline,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _calibrationAdmissionInProgress = false;
                if (_calibrationAuthorityExitDeferred)
                {
                    _calibrationAuthorityExitDeferred = false;
                    if (_calibrationCoordinator is { } coordinator)
                        ScheduleCalibrationAuthorityExitLocked(coordinator);
                }
            }
        }
    }

    private async ValueTask<RuntimeCommandOutcome> HandleCalibrationAdmissionAsync(RuntimeCommand command,
        Guid epoch, Guid attempt, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        string? reason;
        CalibrationSessionAdmissionInput? input = null;
        CameraCalibrationBaselineSnapshot? baseline = null;
        IRegisteredCalibrationProcedure? procedure = null;
        CalibrationSessionCoordinator? existing;
        lock (_sync)
        {
            existing = _calibrationCoordinator;
            reason = RecipeActivationConfigurationBlockedLocked ? "RecipeActivationInProgress" :
                _auditFault || !_storeReady ? "CalibrationAuditUnavailable" :
                command is StartCalibrationSessionCommand ? CheckCalibrationStartLocked() :
                CheckCalibrationActionLocked((CalibrationSessionCommand)command);
        }
        if (reason is null && command is StartCalibrationSessionCommand start)
        {
            // Permission is checked before any provider probe; the commit transaction checks it again.
            reason = await _authorization!.CheckCalibrationQueryAuthorizationAsync(command.Invocation,
                cancellationToken).ConfigureAwait(false);
            if (reason is null)
            {
                try
                {
                    var store = (SqliteCommandStore)_audit!;
                    var camera = await store.ReadCameraSetupAsync(start.Plan.Requirement.LogicalCameraRole,
                        cancellationToken).ConfigureAwait(false);
                    var imaging = await store.ReadImagingSetupAsync(start.Plan.Requirement.LogicalCameraRole,
                        cancellationToken).ConfigureAwait(false);
                    if (camera.State.Binding is not { } binding || camera.State.HasPending ||
                        imaging.State.Current is not { } currentImaging || camera.State.Extension is not null)
                        reason = "CalibrationBaselineDeclarationUnavailable";
                    else
                    {
                        procedure = _calibrationProcedures!.Resolve(start.Plan.Procedure);
                        await _calibrationProcedures.ValidateInputAsync(procedure, start.Plan.Input,
                            PositiveRemaining(deadline), cancellationToken).ConfigureAwait(false);
                        var read = await _cameraRecoveryService!.GetCalibrationBaselineSnapshot(cancellationToken).ConfigureAwait(false);
                        if (!read.Succeeded || read.Snapshot is null) reason = read.ReasonCode;
                        else
                        {
                            baseline = read.Snapshot;
                            if (baseline.Target.ContentHash != binding.Target.ContentHash ||
                                baseline.LogicalCameraRole != binding.LogicalRole ||
                                !_calibrationOptions!.DevelopmentFixture!.Matches(start, binding,
                                    ImagingSetupRevisionReference.FromRevision(currentImaging), baseline.Requested,
                                    baseline.Effective, _calibrationStoreOptions!))
                                reason = "CalibrationDevelopmentFixtureScopeMismatch";
                            else input = new CalibrationSessionAdmissionInput(binding, baseline.Requested,
                                baseline.Effective, _calibrationOptions.DevelopmentFixture.ContentHash);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                { reason = "CalibrationAdmissionEvidenceUnavailable"; }
            }
        }
        lock (_sync)
            if (_shutdownRequested || _disposed) reason = "RuntimeStopping";
        var authorization = await _authorization!.HandleCalibrationCommandAsync(command, epoch, attempt,
            input, existing?.Header, reason, deadline, cancellationToken).ConfigureAwait(false);
        if (authorization.Outcome.Disposition != CommandDisposition.Accepted || authorization.Header is null ||
            authorization.Admission is null)
        {
            if (existing is not null && authorization.SessionAuthorityRevoked)
                lock (_sync) ScheduleCalibrationAuthorityExitLocked(existing);
            return authorization.Outcome;
        }
        var fact = authorization.Admission;
        lock (_sync)
        {
            _calibrationOperationCorrelation = command.CorrelationId;
            if (command is StartCalibrationSessionCommand)
            {
                var header = authorization.Header;
                var empty = new CalibrationSessionEvidence(header,
                    new CalibrationSessionState(header.SessionId, CalibrationSessionPhase.Admitted,
                        CalibrationSessionOutcome.Pending, 0, 0, 0, null, "CalibrationSessionStartAuthorized", false),
                    Array.Empty<CalibrationFrameEvidence>(), Array.Empty<CalibrationObservationEvidence>(),
                    Array.Empty<CalibrationEvidenceExclusion>(), null,
                    CalibrationEvidenceSelection.Evaluate(header, Array.Empty<CalibrationFrameEvidence>(),
                        Array.Empty<CalibrationObservationEvidence>(), Array.Empty<CalibrationEvidenceExclusion>()));
                _calibrationCoordinator = existing = new CalibrationSessionCoordinator(empty, (SqliteCommandStore)_audit!,
                    _cameraRecoveryService!, baseline!, procedure, _calibrationImages!, _calibrationOptions!, PublishCalibrationEvidence);
                _calibrationExitPending = false;
                PublishCalibrationEvidence(empty);
            }
            var coordinator = existing!;
            if (_shutdownRequested) coordinator.Cancel();
            var previous = _calibrationWork;
            if (command is ExitCalibrationSessionCommand)
            {
                _calibrationExitPending = true;
                coordinator.Cancel();
            }
            PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                CalibrationSession = _snapshot.CalibrationSession! with { OperationInProgress = true },
                LastCommand = new CommandProgress(command.CorrelationId, OperationState.Pending, authorization.Outcome.ReasonCode) });
            _calibrationWork = Task.Run(() => RunCalibrationCommandAsync(coordinator, command, fact, previous));
            ScheduleCalibrationSessionExitLocked(_sessions!.Current);
        }
        return authorization.Outcome;
    }

    private string? CheckCalibrationStartLocked()
    {
        if (_importPhysicalReservation is not null) return "CalibrationImportPhysicalVerificationInProgress";
        if (_snapshot.Mode != ExclusiveMode.None || _calibrationCoordinator is { RestorationVerified: false } ||
            _calibrationWork is { IsCompleted: false }) return "CalibrationExclusiveWorkInProgress";
        if (_snapshot.Ready || _snapshot.ArmState != ProductionArmState.Disarmed || _snapshot.Busy ||
            _snapshot.CurrentExecution is not null || _snapshot.Evidence.PendingDeliveries != 0 ||
            _snapshot.Evidence.PendingRequiredImages != 0 || _cameraNetworkMaintenanceActive ||
            _snapshot.LastCommand?.State == OperationState.Pending || _executionGuard.IsHung)
            return "CalibrationStationNotIdle";
        if (_calibrationOptions?.DevelopmentFixture is null) return "SafetyStopUnverified";
        if (_snapshot.Plc.Connection != HealthState.Unconfigured || _snapshot.ActiveRecipe is not null ||
            _snapshot.Handshake != HandshakePhase.Unknown || _snapshot.Recovery != RecoveryState.Required)
            return "CalibrationFixtureProductionOutputsPresent";
        if (_calibrationProcedures is null || _calibrationImages is null || _cameraRecoveryService is null ||
            _calibrationStoreOptions is null || _audit is not SqliteCommandStore { CalibrationEnabled: true })
            return "CalibrationSessionsUnavailable";
        return null;
    }

    private string? CheckCalibrationActionLocked(CalibrationSessionCommand command)
    {
        var current = _calibrationCoordinator;
        if (current is null || current.Header.SessionId != command.CalibrationSessionId || current.RestorationVerified)
            return "CalibrationSessionNotActive";
        if (_calibrationExitPending || _calibrationAuthorityExitDeferred) return "CalibrationExitAlreadyPending";
        if (command is ExitCalibrationSessionCommand) return null;
        if (_calibrationOperationCorrelation.HasValue) return "CalibrationOperationInProgress";
        var evidence = current.Evidence;
        if (evidence.State.Phase != CalibrationSessionPhase.Collecting || evidence.Candidate is not null)
            return "CalibrationEvidenceSelectionFrozen";
        return command switch
        {
            CaptureCalibrationFrameCommand when evidence.Frames.Count >= _calibrationStoreOptions!.CalibrationSessions!.MaximumFramesPerSession =>
                "CalibrationFrameCapacityExceeded",
            ExcludeCalibrationFrameCommand exclude when evidence.Frames.All(frame => frame.FrameId != exclude.FrameId) =>
                "CalibrationFrameNotFound",
            ExcludeCalibrationFrameCommand exclude when evidence.Exclusions.Any(item => item.FrameId == exclude.FrameId) =>
                "CalibrationFrameAlreadyExcluded",
            ComputeCalibrationCandidateCommand when !evidence.Selection.Sufficient => evidence.Selection.ReasonCode,
            _ => null
        };
    }

    private void PublishCalibrationEvidence(CalibrationSessionEvidence evidence)
    {
        lock (_sync)
        {
            if (_disposed) return;
            PublishLocked(_snapshot with { CalibrationSession = evidence.State with
                { OperationInProgress = _calibrationOperationCorrelation.HasValue || _calibrationExitPending ||
                    _calibrationAuthorityExitDeferred },
                Mode = evidence.State.RestorationVerified ? ExclusiveMode.None : ExclusiveMode.Calibration,
                Ready = false, ArmState = ProductionArmState.Disarmed });
        }
    }

    private async Task RunCalibrationCommandAsync(CalibrationSessionCoordinator coordinator,
        RuntimeCommand command, CommandAuditFact fact, Task? previous)
    {
        var completed = false;
        var reason = "CalibrationOperationCompleted";
        try
        {
            if (previous is not null) await previous.ConfigureAwait(false);
            if (command is not ExitCalibrationSessionCommand) coordinator.SetAuthorizationCommand(command);
            if (command is not ExitCalibrationSessionCommand && coordinator.CancellationRequested)
                throw new OperationCanceledException("CalibrationSessionCancelled");
            switch (command)
            {
                case StartCalibrationSessionCommand: await coordinator.StartAsync().ConfigureAwait(false); break;
                case CaptureCalibrationFrameCommand capture: await coordinator.CaptureAsync(capture).ConfigureAwait(false); break;
                case ExcludeCalibrationFrameCommand exclude: await coordinator.ExcludeAsync(exclude).ConfigureAwait(false); break;
                case ComputeCalibrationCandidateCommand compute: await coordinator.ComputeAsync(compute).ConfigureAwait(false); break;
                case ExitCalibrationSessionCommand exit:
                    coordinator.SetAuthorizationCommand(command);
                    await coordinator.RestoreAsync(command.CorrelationId, exit.Cancel ? CalibrationSessionOutcome.Cancelled :
                        CalibrationSessionOutcome.Completed, exit.Cancel ? "CalibrationSessionCancelled" :
                        "CalibrationSessionCompleted").ConfigureAwait(false);
                    break;
            }
            completed = command is not ExitCalibrationSessionCommand || coordinator.RestorationVerified;
            if (!completed) reason = "CalibrationBaselineRestoreBlocked";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var cancelled = ex is OperationCanceledException && coordinator.CancellationRequested;
            reason = cancelled ? "CalibrationSessionCancelled" :
                ex is TimeoutException ? "CalibrationProcedureDeadlineExceeded" :
                ex is CalibrationProcedureException procedureFailure ? procedureFailure.ReasonCode : "CalibrationOperationFailed";
            try
            {
                await coordinator.RestoreAsync(command.CorrelationId,
                    cancelled ? CalibrationSessionOutcome.Cancelled : CalibrationSessionOutcome.Failed,
                    reason).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            { MarkAuditFault("CalibrationRestorationAuditUnavailable"); }
        }
        if (command is not StartCalibrationSessionCommand)
        {
            var terminal = fact with { EventId = Guid.NewGuid(), OccurredAtUtc = DateTimeOffset.UtcNow,
                Phase = completed ? CommandAuditPhase.Completed : CommandAuditPhase.Failed,
                Disposition = null, ReasonCode = reason };
            var write = await _audit!.AppendAsync(terminal, new StoreDeadline(_audit.CommitTimeout),
                CancellationToken.None).ConfigureAwait(false);
            if (!write.Committed) MarkAuditFault("CalibrationCommandTerminalUnavailable");
        }
        lock (_sync)
        {
            if (_disposed) return;
            if (command is ExitCalibrationSessionCommand) _calibrationExitPending = false;
            if (_calibrationOperationCorrelation == command.CorrelationId)
                _calibrationOperationCorrelation = null;
            PublishCalibrationEvidence(coordinator.Evidence);
            if (_snapshot.LastCommand?.CorrelationId == command.CorrelationId)
                PublishLocked(_snapshot with { LastCommand = new CommandProgress(command.CorrelationId,
                    command is StartCalibrationSessionCommand && !coordinator.RestorationVerified ? OperationState.Pending :
                        completed ? OperationState.Completed : OperationState.Failed,
                    coordinator.Evidence.State.ReasonCode) });
        }
    }
}
