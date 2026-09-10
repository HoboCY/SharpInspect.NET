using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Recipes;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private IRecipeActivationService? _recipeActivations;
    private ActivationReservation? _activationReservation;
    private long _activationPhysicalPhaseSequence;
    private bool _activationRecoveryBlocked;
    private ActivationPreparedBundle? _activeActivation;
    private bool RecipeActivationConfigurationBlockedLocked => _activationReservation is not null ||
        _activationRecoveryBlocked || _activationStartupPending || _activationStartupBlocked;

    internal void ConfigureRecipeActivationService(IRecipeActivationService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        lock (_sync)
        {
            if (_recipeActivations is not null) throw new InvalidOperationException("RecipeActivationAlreadyConfigured");
            _recipeActivations = service;
            if (_activationStartupPending)
                _recipeActivationStartupTask = Task.Run(() => InitializeRecipeActivationAsync(service));
        }
    }

    private async ValueTask<RuntimeCommandOutcome> SubmitRecipeActivationAsync(ActivateRecipeCommand command,
        CancellationToken token)
    {
        IRecipeActivationService? service;
        lock (_sync) service = _recipeActivations;
        return service is null ? new(command.CorrelationId, CommandDisposition.Rejected,
            "RecipeActivationConfigurationRequired", AuditPersistence.NotAttempted, Guid.NewGuid()) :
            (await service.ActivateAsync(command, token).ConfigureAwait(false)).Outcome;
    }

    internal async ValueTask<RecipeActivationRuntimeLease> ReserveRecipeActivationAsync(Guid correlationId,
        CancellationToken token)
    {
        var timeout = _audit?.CommitTimeout ?? TimeSpan.FromSeconds(2);
        await _storeInitialization.WaitAsync(timeout, token).ConfigureAwait(false);
        if (!await _commandGate.WaitAsync(timeout, token).ConfigureAwait(false))
        {
            lock (_sync) return new(_snapshot.RuntimeEpoch, "RecipeActivationRuntimeBusy", token);
        }
        try
        {
            lock (_sync)
            {
                if (_activationReservation is not null)
                    return new(_snapshot.RuntimeEpoch, "RecipeActivationInProgress", token);
                if (RecipeActivationBlockerLocked(null) is { } blocker)
                    return new(_snapshot.RuntimeEpoch, blocker, token);
                var reservation = new ActivationReservation(correlationId, new CancellationTokenSource());
                _activationReservation = reservation;
                reservation.ConnectCaller(token, () => { lock (_sync) reservation.RequestStop(); });
                // Production enable never survives activation admission. Provider I/O happens
                // after releasing this gate, so the physical local Stop retains its own slot.
                PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                    LastCommand = new CommandProgress(correlationId, OperationState.Pending, "RecipeActivationPreparing"),
                    AdmissionBlockers = new AdmissionBlockers(_snapshot.AdmissionBlockers
                        .Append("RecipeActivationInProgress").Distinct(StringComparer.Ordinal)) });
                return new(_snapshot.RuntimeEpoch, null, reservation.Cancellation.Token, _cameraSetupRuntime,
                    cancellation => EnterRecipeActivationCommitAsync(reservation, cancellation),
                    (snapshot, prepared) => InstallRecipeActivation(reservation, snapshot, prepared),
                    (reason, recovery) => PublishRecipeActivationTerminal(reservation, reason, recovery),
                    () => ReleaseRecipeActivation(reservation),
                    () => ReadRecipeActivationBlocker(reservation),
                    () => TryBeginRecipeActivationPhysicalPhase(reservation));
            }
        }
        finally { _commandGate.Release(); }
    }

    private string? RecipeActivationBlockerLocked(ActivationReservation? reservation)
    {
        if (PreviewConfigurationBlockedLocked) return "PreviewSessionInProgress";
        if (ManualInspectionConfigurationBlockedLocked) return "ManualInspectionSessionInProgress";
        if (_importPhysicalReservation is not null) return "CalibrationImportPhysicalVerificationInProgress";
        if (reservation is { CommitClaimed: true } && ReferenceEquals(_activationReservation, reservation)) return null;
        if (reservation is { InFlightPhysicalPhaseId: not 0 }) return "RecipeActivationPhysicalPhaseInProgress";
        if (_activationStartupPending) return "RecipeActivationStartupRecoveryPending";
        if (_activationStartupBlocked) return "RecipeActivationStartupRecoveryRequired";
        if (_activationRecoveryBlocked) return "RecipeActivationRecoveryRequired";
        if (_cameraSetupRuntime.ConfigurationMutationInProgress) return "RecipeActivationCameraConfigurationInProgress";
        if (_disposed || _shutdownRequested || _snapshot.Lifecycle == RuntimeLifecycle.Stopped) return "RuntimeStopped";
        if (reservation is not null && !ReferenceEquals(_activationReservation, reservation)) return "RecipeActivationReservationLost";
        if (reservation?.StopRequested == true || reservation?.Cancellation.IsCancellationRequested == true ||
            Volatile.Read(ref _pendingLocalStops) != 0)
            return "RecipeActivationCancelled";
        if (_snapshot.Busy || _snapshot.CurrentExecution is not null || _executionGuard.IsHung)
            return "RecipeActivationExecutionConflict";
        if (_snapshot.Evidence.PendingDeliveries != 0 || _snapshot.Evidence.PendingRequiredImages != 0 ||
            _snapshot.Handshake is HandshakePhase.AwaitingResultAck or HandshakePhase.AwaitingAckReset)
            return "RecipeActivationDeliveryConflict";
        if (_snapshot.Mode != ExclusiveMode.None || _snapshot.Recovery == RecoveryState.InProgress ||
            _cameraNetworkMaintenanceActive || _calibrationAdmissionInProgress || _calibrationWork is { IsCompleted: false } ||
            (_snapshot.LastCommand?.State == OperationState.Pending &&
                (reservation is null || _snapshot.LastCommand.CorrelationId != reservation.CorrelationId)))
            return "RecipeActivationWorkflowConflict";
        if (_cameraRecoveryService is not null || _cameraAcquisitionService is not null)
            return "RecipeActivationCameraProductionOwnerRegistered";
        if (!_storeReady || _auditFault) return "RecipeActivationAuditUnavailable";
        return null;
    }

    private string? ReadRecipeActivationBlocker(ActivationReservation reservation)
    {
        // The identity writer may hold a session authorization lease. Snapshot
        // projection can hold the station lock while reading that session, so
        // writer callbacks must decline contention instead of waiting in reverse order.
        if (!Monitor.TryEnter(_sync)) return "RecipeActivationRuntimeBusy";
        try { return RecipeActivationBlockerLocked(reservation); }
        finally { Monitor.Exit(_sync); }
    }

    private RecipeActivationPhysicalPhaseClaim TryBeginRecipeActivationPhysicalPhase(
        ActivationReservation reservation)
    {
        // Physical calls must never execute while the station lock is held.  A
        // non-blocking admission check preserves the lock ordering used by
        // provider callbacks and makes a contending phase fail promptly.
        if (!Monitor.TryEnter(_sync))
            return RecipeActivationPhysicalPhaseClaim.Unavailable("RecipeActivationRuntimeBusy");
        try
        {
            if (!ReferenceEquals(_activationReservation, reservation))
                return RecipeActivationPhysicalPhaseClaim.Unavailable("RecipeActivationReservationLost");
            if (reservation.StopRequested || reservation.Cancellation.IsCancellationRequested ||
                Volatile.Read(ref _pendingLocalStops) != 0)
                return RecipeActivationPhysicalPhaseClaim.Unavailable("RecipeActivationCancelled");
            if (reservation.CommitClaimed)
                return RecipeActivationPhysicalPhaseClaim.Unavailable("RecipeActivationCommitInProgress");
            if (reservation.InFlightPhysicalPhaseId != 0)
                return RecipeActivationPhysicalPhaseClaim.Unavailable("RecipeActivationPhysicalPhaseInProgress");
            if (RecipeActivationBlockerLocked(reservation) is { } blocker)
                return RecipeActivationPhysicalPhaseClaim.Unavailable(blocker);

            var phaseId = checked(++_activationPhysicalPhaseSequence);
            reservation.InFlightPhysicalPhaseId = phaseId;
            return RecipeActivationPhysicalPhaseClaim.Granted(phaseId,
                () => ReleaseRecipeActivationPhysicalPhase(reservation, phaseId));
        }
        finally { Monitor.Exit(_sync); }
    }

    private void ReleaseRecipeActivationPhysicalPhase(ActivationReservation reservation, long phaseId)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_activationReservation, reservation) &&
                reservation.InFlightPhysicalPhaseId == phaseId)
                reservation.InFlightPhysicalPhaseId = 0;
        }
    }

    private async ValueTask<RecipeActivationCommitLease> EnterRecipeActivationCommitAsync(
        ActivationReservation reservation, CancellationToken token)
    {
        if (!await _commandGate.WaitAsync(_audit?.CommitTimeout ?? TimeSpan.FromSeconds(2), token).ConfigureAwait(false))
            return new(() => "RecipeActivationRuntimeBusy");
        return new(() => ReadRecipeActivationBlocker(reservation), () => _commandGate.Release(),
            () =>
            {
                if (!Monitor.TryEnter(_sync)) return "RecipeActivationRuntimeBusy";
                try
                {
                    if (RecipeActivationBlockerLocked(reservation) is { } blocker) return blocker;
                    reservation.CommitClaimed = true;
                    return null;
                }
                finally { Monitor.Exit(_sync); }
            });
    }

    private RecipeActivationResourceInstallResult InstallRecipeActivation(ActivationReservation reservation,
        RecipeActivationSnapshot snapshot, PreparedAlgorithm prepared)
    {
        // Called only after durable success while the commit gate is owned. The owned resource
        // pointer exchange itself cannot invoke user/provider code or dispose the prior instance.
        lock (_sync)
        {
            if (!ReferenceEquals(_activationReservation, reservation) || !reservation.CommitClaimed ||
                reservation.Committed || _disposed) return new(false, null);
            var previous = Interlocked.Exchange(ref _activeActivation, new(snapshot, prepared));
            reservation.Committed = true;
            if (snapshot.ProductionAuthority)
                PublishLocked(_snapshot with { ActiveRecipe = snapshot.Recipe, Ready = false,
                    ArmState = ProductionArmState.Disarmed,
                    AdmissionBlockers = new AdmissionBlockers(_snapshot.AdmissionBlockers
                        .Where(code => code is not "ActiveRecipeMissing" and not "ActiveRecipePreparationRequired")) });
            return new(true, previous?.Algorithm);
        }
    }

    private void PublishRecipeActivationTerminal(ActivationReservation reservation, string reason, bool recovery)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_activationReservation, reservation) || _disposed) return;
            reservation.TerminalPublished = true;
            _activationRecoveryBlocked |= recovery;
            var blockers = _snapshot.AdmissionBlockers.Where(code => code != "RecipeActivationInProgress").ToList();
            if (recovery) blockers.Add("RecipeActivationRecoveryRequired");
            var last = _snapshot.LastCommand;
            if (last?.CorrelationId == reservation.CorrelationId)
                last = new(reservation.CorrelationId, reservation.Committed ? OperationState.Completed : OperationState.Failed, reason);
            PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                Recovery = recovery ? RecoveryState.Required : _snapshot.Recovery, LastCommand = last,
                AdmissionBlockers = new AdmissionBlockers(blockers.Distinct(StringComparer.Ordinal)) });
        }
    }

    private void ReleaseRecipeActivation(ActivationReservation reservation)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_activationReservation, reservation) && !reservation.TerminalPublished)
                PublishRecipeActivationTerminal(reservation, "RecipeActivationTerminalUnavailable", true);
            if (ReferenceEquals(_activationReservation, reservation)) _activationReservation = null;
            reservation.Completion.TrySetResult(true);
        }
        reservation.DisposeCancellationWhenRetired();
    }

    private void CancelRecipeActivation()
    {
        lock (_sync)
        {
            _activationReservation?.RequestStop();
        }
    }

    private async Task ShutdownRecipeActivationAsync()
    {
        Task[] pending;
        lock (_sync) pending = new[] { _activationReservation?.Completion.Task, _recipeActivationStartupTask }
            .Where(task => task is not null).Select(task => task!).ToArray();
        CancelRecipeActivation();
        if (pending.Length != 0)
        {
            try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { throw new InvalidOperationException("RecipeActivationShutdownIncomplete"); }
        }
        var prepared = Interlocked.Exchange(ref _activeActivation, null)?.Algorithm;
        if (prepared is not null)
        {
            var retiring = prepared.DisposeAsync().AsTask();
            try { await retiring.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                _ = retiring.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private sealed record ActivationPreparedBundle(RecipeActivationSnapshot Snapshot, PreparedAlgorithm Algorithm);

    private sealed class ActivationReservation
    {
        internal ActivationReservation(Guid correlationId, CancellationTokenSource cancellation)
        { CorrelationId = correlationId; Cancellation = cancellation; }
        internal Guid CorrelationId { get; }
        internal CancellationTokenSource Cancellation { get; }
        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Committed { get; set; }
        internal bool CommitClaimed { get; set; }
        internal long InFlightPhysicalPhaseId { get; set; }
        internal bool TerminalPublished { get; set; }
        internal bool StopRequested { get; private set; }
        private Task? _cancellationWork;
        private CancellationTokenRegistration _callerRegistration;
        internal void ConnectCaller(CancellationToken caller, Action requestStop) =>
            _callerRegistration = caller.Register(requestStop);
        internal void RequestStop()
        {
            StopRequested = true;
            if (CommitClaimed) return;
            // Cancellation registrations are third-party code. The Stop caller changes the
            // commit blocker immediately and never runs those callbacks on its command lane.
            _cancellationWork ??= Task.Run(() =>
            {
                try { Cancellation.Cancel(); }
                catch (AggregateException) { }
            });
        }
        internal void DisposeCancellationWhenRetired()
        {
            _callerRegistration.Dispose();
            if (_cancellationWork is null) Cancellation.Dispose();
            else _ = _cancellationWork.ContinueWith(_ => Cancellation.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
