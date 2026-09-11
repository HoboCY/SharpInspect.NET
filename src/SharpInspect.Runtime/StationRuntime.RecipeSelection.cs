using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private RecipeSelectionService? _recipeSelections;
    private RecipeSelectionReservation? _recipeSelectionReservation;
    private RecipeSelectionRevision? _recipeSelectionCurrent;
    private Task _recipeSelectionStartup = Task.CompletedTask;
    private bool _recipeSelectionStartupPending;
    private bool _recipeSelectionStartupBlocked;
    private bool _recipeSelectionStartupVerified;
    private bool _recipeSelectionChangeInProgress;
    private bool _recipeSelectionCacheDiverged;

    private void ConfigureRecipeSelectionStartup(bool configured)
    {
        if (!configured) return;
        _recipeSelectionStartupPending = true;
        _snapshot = _snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
            AdmissionBlockers = new AdmissionBlockers(_snapshot.AdmissionBlockers
                .Append("RecipeSelectionStartupVerificationPending")) };
    }

    /// <summary>
    /// Called by the host right after construction. Startup verification runs after
    /// base store initialization and completes before any production observer may
    /// consume <c>_recipeSelectionCurrent</c>. A missing feature leaves the wait
    /// completed and never installs the pending blocker.
    /// </summary>
    internal void ConfigureRecipeSelectionService(RecipeSelectionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        lock (_sync)
        {
            if (_recipeSelections is not null)
                throw new InvalidOperationException("RecipeSelectionAlreadyConfigured");
            _recipeSelections = service;
            if (_recipeSelectionStartupPending && _audit is SqliteCommandStore { RecipeSelectionEnabled: true })
            {
                _recipeSelectionStartup = Task.Run(() => InitializeRecipeSelectionAsync(service));
                return;
            }
            if (!_recipeSelectionStartupPending) return;
            // The configured store authority cannot host selection: never leave a wait
            // or a startup blocker that legacy paths would have to consult.
            _recipeSelectionStartupPending = false;
            PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                AdmissionBlockers = new AdmissionBlockers(_snapshot.AdmissionBlockers
                    .Where(code => code != "RecipeSelectionStartupVerificationPending")) });
        }
    }

    private Task WaitForRecipeSelectionStartupAsync() => _recipeSelectionStartup;

    private async ValueTask<RuntimeCommandOutcome> SubmitRecipeSelectionAsync(ChangeRecipeSelectionCommand command,
        CancellationToken cancellationToken)
    {
        RecipeSelectionService? service;
        lock (_sync) service = _recipeSelections;
        return service is null ? new(command.CorrelationId, CommandDisposition.Rejected,
            "RecipeSelectionConfigurationRequired", AuditPersistence.NotAttempted, Guid.NewGuid()) :
            (await service.ChangeAsync(command, cancellationToken).ConfigureAwait(false)).Outcome;
    }

    private async Task InitializeRecipeSelectionAsync(RecipeSelectionService service)
    {
        RecipeSelectionStartupResult result;
        try
        {
            await _storeInitialization.ConfigureAwait(false);
            var enabled = false;
            lock (_sync)
                enabled = _baseStoreReady && !_auditFault && !_shutdownRequested && !_disposed;
            result = enabled
                ? await service.ReadStartupAsync(_lifetime.Token).ConfigureAwait(false)
                : new RecipeSelectionStartupResult(false, "RecipeSelectionStartupStoreUnavailable");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        { result = new(false, "RecipeSelectionStartupCancelled"); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { result = new(false, "RecipeSelectionStartupUnavailable"); }
        lock (_sync) CompleteRecipeSelectionStartupLocked(result);
    }

    private void CompleteRecipeSelectionStartupLocked(RecipeSelectionStartupResult result)
    {
        _recipeSelectionStartupPending = false;
        _recipeSelectionStartupBlocked = !result.Available;
        _recipeSelectionStartupVerified = result.Available;
        if (result.Available) _recipeSelectionCurrent = result.Current;
        if (_disposed) return;
        var blockers = _snapshot.AdmissionBlockers.Where(code =>
            code is not "RecipeSelectionStartupVerificationPending" and not "RecipeSelectionStartupVerificationRequired").ToList();
        if (!result.Available)
        {
            blockers.Add("RecipeSelectionStartupVerificationRequired");
            blockers.Add(result.ReasonCode);
        }
        PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
            AdmissionBlockers = new AdmissionBlockers(blockers.Distinct(StringComparer.Ordinal)) });
    }

    /// <summary>
    /// Reserves one bounded selection change: the command gate is held only for the
    /// reservation itself, and the identity writer rechecks the live blocker on the
    /// same reservation. The hold excludes activation, production triggers and every
    /// other configuration workflow and keeps Ready=false/Disarmed until release.
    /// </summary>
    internal async ValueTask<RecipeSelectionRuntimeLease> ReserveRecipeSelectionChangeAsync(
        CancellationToken cancellationToken)
    {
        var timeout = _audit?.CommitTimeout ?? TimeSpan.FromSeconds(2);
        await _storeInitialization.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (!await _commandGate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            lock (_sync) return UnavailableRecipeSelectionLease("RecipeSelectionRuntimeBusy");
        }
        try
        {
            lock (_sync)
            {
                if (_recipeSelectionReservation is not null || _recipeSelectionChangeInProgress)
                    return UnavailableRecipeSelectionLease("RecipeSelectionChangeInProgress");
                if (RecipeSelectionBlockerLocked() is { } blocker)
                    return UnavailableRecipeSelectionLease(blocker);
                var reservation = new RecipeSelectionReservation();
                _recipeSelectionReservation = reservation;
                _recipeSelectionChangeInProgress = true;
                PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                    AdmissionBlockers = new AdmissionBlockers(_snapshot.AdmissionBlockers
                        .Append("RecipeSelectionChangeInProgress").Distinct(StringComparer.Ordinal)) });
                return new RecipeSelectionRuntimeLease(_snapshot.RuntimeEpoch, null,
                    () => ReadRecipeSelectionBlocker(reservation),
                    revision => InstallRecipeSelectionRevision(reservation, revision),
                    () => ReleaseRecipeSelectionReservation(reservation));
            }
        }
        finally { _commandGate.Release(); }
    }

    private RecipeSelectionRuntimeLease UnavailableRecipeSelectionLease(string reason)
    {
        lock (_sync) return new RecipeSelectionRuntimeLease(_snapshot.RuntimeEpoch, reason);
    }

    private string? RecipeSelectionBlockerLocked(RecipeSelectionReservation? reservation = null)
    {
        // RecipeActivationConfigurationBlockedLocked also reports this reservation's own
        // hold, so only a foreign activation or handshake hold may block the decision.
        if (_activationReservation is not null || _activationRecoveryBlocked || _activationStartupPending ||
            _activationStartupBlocked || _recipeChangeInProgress && !OwnsRecipeSelectionHold(reservation) ||
            _recipeSelectionChangeInProgress && !OwnsRecipeSelectionHold(reservation))
            return "RecipeSelectionActivationConflict";
        if (ProductionInspectionConfigurationBlockedLocked) return "RecipeSelectionProductionInspectionConflict";
        if (StationQualificationConfigurationBlockedLocked) return "RecipeSelectionStationQualificationConflict";
        if (PreviewConfigurationBlockedLocked) return "RecipeSelectionPreviewConflict";
        if (ManualInspectionConfigurationBlockedLocked) return "RecipeSelectionManualInspectionConflict";
        if (_importPhysicalReservation is not null) return "RecipeSelectionCalibrationImportConflict";
        if (_recipeSelectionStartupBlocked) return "RecipeSelectionStartupVerificationRequired";
        if (!_recipeSelectionStartupVerified) return "RecipeSelectionStartupVerificationPending";
        if (_recipeSelectionCacheDiverged) return "RecipeSelectionCacheReloadRequired";
        if (_disposed || _shutdownRequested || _snapshot.Lifecycle == RuntimeLifecycle.Stopped) return "RuntimeStopped";
        if (Volatile.Read(ref _pendingLocalStops) != 0) return "RecipeSelectionLocalStop";
        // A Map change never disarms production: it is admitted only while already
        // not-ready and disarmed and never touches the arm axis itself.
        if (_snapshot.Ready || _snapshot.ArmState != ProductionArmState.Disarmed)
            return "RecipeSelectionRequiresDisarmedNotReady";
        if (_snapshot.Busy || _snapshot.CurrentExecution is not null || _executionGuard.IsHung)
            return "RecipeSelectionExecutionConflict";
        if (_snapshot.Evidence.PendingDeliveries != 0 || _snapshot.Evidence.PendingRequiredImages != 0 ||
            _snapshot.Handshake is HandshakePhase.AwaitingResultAck or HandshakePhase.AwaitingAckReset)
            return "RecipeSelectionDeliveryConflict";
        if (_snapshot.Mode != ExclusiveMode.None || _snapshot.Recovery == RecoveryState.InProgress ||
            _cameraNetworkMaintenanceActive || _calibrationAdmissionInProgress || _calibrationWork is { IsCompleted: false } ||
            _snapshot.LastCommand?.State == OperationState.Pending)
            return "RecipeSelectionWorkflowConflict";
        if (_cameraSetupRuntime.ConfigurationMutationInProgress) return "RecipeSelectionCameraConfigurationConflict";
        if (!_storeReady || _auditFault) return "RecipeSelectionAuditUnavailable";
        if (reservation is not null && !ReferenceEquals(_recipeSelectionReservation, reservation))
            return "RecipeSelectionReservationLost";
        return null;
    }

    private bool OwnsRecipeSelectionHold(RecipeSelectionReservation? reservation) =>
        reservation is not null && _recipeSelectionChangeInProgress &&
        ReferenceEquals(_recipeSelectionReservation, reservation);

    private string? ReadRecipeSelectionBlocker(RecipeSelectionReservation reservation)
    {
        // The identity writer may already hold a session authorization lease while
        // projection holds this lock and reads that session. Decline contention in the
        // reverse direction instead of waiting for it.
        if (!Monitor.TryEnter(_sync)) return "RecipeSelectionRuntimeBusy";
        try { return RecipeSelectionBlockerLocked(reservation); }
        finally { Monitor.Exit(_sync); }
    }

    private bool InstallRecipeSelectionRevision(RecipeSelectionReservation reservation,
        RecipeSelectionRevision revision)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_recipeSelectionReservation, reservation) && !_disposed &&
                revision.Previous == _recipeSelectionCurrent?.Reference)
            {
                _recipeSelectionCurrent = revision;
                return true;
            }
            // The durable revision cannot be reconciled with the cached projection.
            // Keep the original cache and block selection work until a reload.
            _recipeSelectionCacheDiverged = true;
            if (_disposed) return false;
            PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                AdmissionBlockers = new AdmissionBlockers(_snapshot.AdmissionBlockers
                    .Append("RecipeSelectionCacheReloadRequired").Distinct(StringComparer.Ordinal)) });
            return false;
        }
    }

    private void ReleaseRecipeSelectionReservation(RecipeSelectionReservation reservation)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_recipeSelectionReservation, reservation)) return;
            _recipeSelectionReservation = null;
            _recipeSelectionChangeInProgress = false;
            if (_disposed) return;
            PublishLocked(_snapshot with { AdmissionBlockers = new AdmissionBlockers(_snapshot.AdmissionBlockers
                .Where(code => code != "RecipeSelectionChangeInProgress")) });
        }
    }

    /// <summary>Identity of one in-flight selection reservation; the reservation owns no other state.</summary>
    private sealed class RecipeSelectionReservation
    {
    }
}
