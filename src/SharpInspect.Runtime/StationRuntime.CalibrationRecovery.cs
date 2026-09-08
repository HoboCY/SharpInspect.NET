using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private bool _calibrationStartupBlocked;

    private void ScheduleCalibrationSessionExitLocked(InteractiveSession session)
    {
        var coordinator = _calibrationCoordinator;
        if (coordinator is null || coordinator.RestorationVerified || _calibrationExitPending ||
            (session.State == InteractiveSessionState.Authenticated &&
             session.SessionId == coordinator.Header.InteractiveSessionId &&
             session.PrincipalId == coordinator.Header.ActorPrincipalId.ToString("D"))) return;
        ScheduleCalibrationAuthorityExitLocked(coordinator);
    }

    private void ScheduleCalibrationAuthorityExitLocked(CalibrationSessionCoordinator coordinator)
    {
        if (_calibrationExitPending || coordinator.RestorationVerified) return;
        if (_calibrationAdmissionInProgress)
        {
            // Session changes do not take the command gate. Defer their worker until
            // any durable admission has published its task, so recovery cannot overtake it.
            _calibrationAuthorityExitDeferred = true;
            coordinator.Cancel();
            PublishCalibrationEvidence(coordinator.Evidence);
            return;
        }
        _calibrationExitPending = true;
        coordinator.Cancel();
        PublishCalibrationEvidence(coordinator.Evidence);
        var previous = _calibrationWork;
        _calibrationWork = Task.Run(async () =>
        {
            try
            {
                if (previous is not null) await previous.ConfigureAwait(false);
                await coordinator.RestoreAsync(coordinator.Header.Command.CorrelationId,
                    CalibrationSessionOutcome.Cancelled, "CalibrationInteractiveAuthorityEnded").ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            { MarkAuditFault("CalibrationAuthorityExitUnavailable"); }
            finally
            {
                lock (_sync)
                {
                    _calibrationExitPending = false;
                    PublishCalibrationEvidence(coordinator.Evidence);
                    if (!_disposed && _snapshot.LastCommand?.CorrelationId == coordinator.Header.Command.CorrelationId)
                        PublishLocked(_snapshot with { LastCommand = new CommandProgress(coordinator.Header.Command.CorrelationId,
                            OperationState.Failed, "CalibrationInteractiveAuthorityEnded") });
                }
            }
        });
    }

    private async Task InitializeCalibrationSessionsAsync()
    {
        if (_audit is not SqliteCommandStore { CalibrationEnabled: true } store) return;
        try
        {
            var pending = await store.ReadOpenCalibrationSessionsAsync(CancellationToken.None).ConfigureAwait(false);
            if (pending.Count == 0) return;
            _calibrationStartupBlocked = true;
            foreach (var evidence in pending)
            {
                lock (_sync)
                    PublishLocked(_snapshot with { Ready = false, Mode = ExclusiveMode.Calibration,
                        ArmState = ProductionArmState.Disarmed,
                        CalibrationSession = evidence.State with { Phase = CalibrationSessionPhase.Restoring,
                            ReasonCode = "CalibrationRestoringAfterRestart" } });
                if (!evidence.State.RestorationVerified)
                {
                    if (_cameraRecoveryService is null || _calibrationImages is null)
                    {
                        BlockCalibrationStartup(evidence, "CalibrationRestoreOwnerUnavailable");
                        return;
                    }
                    var baseline = await _cameraRecoveryService.GetCalibrationBaselineSnapshot(CancellationToken.None).ConfigureAwait(false);
                    if (!baseline.Succeeded || baseline.Snapshot is null ||
                        baseline.Snapshot.Target.ContentHash != evidence.Header.Binding.Target.ContentHash ||
                        baseline.Snapshot.LogicalCameraRole != evidence.Header.Binding.LogicalRole)
                    {
                        BlockCalibrationStartup(evidence, "CalibrationRestoreOwnerUnavailable");
                        return;
                    }
                    var persisted = new CameraCalibrationBaselineSnapshot(evidence.Header.Binding.Target,
                        evidence.Header.Binding.LogicalRole, evidence.Header.BaselineRequested,
                        evidence.Header.BaselineEffective, baseline.Snapshot.Health);
                    var coordinator = new CalibrationSessionCoordinator(evidence, store, _cameraRecoveryService,
                        persisted, null, _calibrationImages, _calibrationOptions ?? new CalibrationSessionOptions(),
                        PublishCalibrationEvidence);
                    lock (_sync) _calibrationCoordinator = coordinator;
                    await coordinator.RestoreAsync(evidence.Header.Command.CorrelationId,
                        CalibrationSessionOutcome.RestartAborted, "CalibrationSessionRestartAborted",
                        afterRestart: true).ConfigureAwait(false);
                    if (!coordinator.RestorationVerified) return;
                }
                var actions = await store.ReadPendingCalibrationActionsAsync(evidence.Header.SessionId,
                    CancellationToken.None).ConfigureAwait(false);
                foreach (var fact in actions)
                {
                    var terminal = fact with { EventId = Guid.NewGuid(), OccurredAtUtc = DateTimeOffset.UtcNow,
                        Phase = CommandAuditPhase.Failed, Disposition = null, ReasonCode = "CalibrationSessionRestartAborted" };
                    var written = await _audit.AppendAsync(terminal, new StoreDeadline(_audit.CommitTimeout),
                        CancellationToken.None).ConfigureAwait(false);
                    if (!written.Committed) throw new InvalidOperationException("CalibrationRestartActionTerminalUnavailable");
                }
                if (evidence.State.RestorationVerified) PublishCalibrationEvidence(evidence);
            }
            _calibrationStartupBlocked = false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _calibrationStartupBlocked = true;
            MarkAuditFault("CalibrationStartupRecoveryUnavailable");
        }
    }

    private void BlockCalibrationStartup(CalibrationSessionEvidence evidence, string reason)
    {
        lock (_sync)
            PublishLocked(_snapshot with { Ready = false, Mode = ExclusiveMode.Calibration,
                ArmState = ProductionArmState.Disarmed,
                CalibrationSession = evidence.State with { Phase = CalibrationSessionPhase.RecoveryBlocked,
                    ReasonCode = reason, RestorationVerified = false },
                AdmissionBlockers = new AdmissionBlockers(_snapshot.AdmissionBlockers.Concat(new[] { reason }).Distinct()) });
    }

    private async Task ShutdownCalibrationAsync()
    {
        await _storeInitialization.ConfigureAwait(false);
        CalibrationSessionCoordinator? coordinator;
        Task? pending;
        lock (_sync)
        {
            coordinator = _calibrationCoordinator;
            coordinator?.Cancel();
            pending = _calibrationWork;
        }
        if (pending is not null)
        {
            var budget = (_calibrationOptions?.OperationTimeout ?? TimeSpan.FromSeconds(5)) +
                (_audit?.CommitTimeout ?? TimeSpan.FromSeconds(5)) +
                (_audit?.CommitTimeout ?? TimeSpan.FromSeconds(5));
            try
            {
                if (coordinator is not null && !pending.IsCompleted)
                {
                    var restorationBudget = (_cameraRecoveryService?.CalibrationRestorationTimeout ??
                        TimeSpan.FromMinutes(1)) + budget;
                    var settled = await Task.WhenAny(pending, coordinator.PhysicalRestoration)
                        .WaitAsync(restorationBudget).ConfigureAwait(false);
                    if (settled != pending && !(await coordinator.PhysicalRestoration.ConfigureAwait(false)).Succeeded)
                        throw new TimeoutException("CalibrationPhysicalRestoreIncomplete");
                }
                await pending.WaitAsync(budget).ConfigureAwait(false);
            }
            catch (TimeoutException failure)
            {
                // The actual consumer call still owns borrowed copies. Keep its task, camera
                // owner and durable fence alive; the host must handle an abnormal shutdown.
                // In particular, do not continue to normal Stopped or dispose dependencies.
                if (coordinator is not null)
                    BlockCalibrationStartup(coordinator.Evidence, "CalibrationShutdownIncomplete");
                _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw new InvalidOperationException("CalibrationShutdownIncomplete", failure);
            }
        }
        if (coordinator is not null && !coordinator.RestorationVerified)
        {
            try
            {
                await coordinator.RestoreAsync(coordinator.Header.Command.CorrelationId,
                    CalibrationSessionOutcome.Cancelled, "CalibrationRuntimeStopped").ConfigureAwait(false);
                if (!coordinator.RestorationVerified)
                    throw new InvalidOperationException("CalibrationBaselineRestoreIncomplete");
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                BlockCalibrationStartup(coordinator.Evidence, "CalibrationShutdownIncomplete");
                throw new InvalidOperationException("CalibrationShutdownIncomplete", failure);
            }
        }
    }
}
