using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Cycles;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    // Retain the actual task when a plugin outlives the bounded cleanup wait. A
    // timeout does not release plugin ownership or reverse the durable retirement.
    private Task? _recipeRetirementCleanup;

    // The lifecycle service must verify the command's permission and fresh Step-Up
    // before asking for this mechanical stopping authority; the writer verifies again.
    internal async ValueTask<RecipeRetirementRuntimeLease> ReserveRecipeRetirementAsync(
        RetireReleasedRecipeCommand command, CancellationToken token)
    {
        var timeout = _audit?.CommitTimeout ?? TimeSpan.FromSeconds(2);
        await _storeInitialization.WaitAsync(timeout, token).ConfigureAwait(false);
        if (!await _commandGate.WaitAsync(timeout, token).ConfigureAwait(false))
        {
            lock (_sync) return new(_snapshot.RuntimeEpoch, "RecipeRetirementRuntimeBusy", token);
        }
        try
        {
            lock (_sync)
            {
                RecipeRetirementRuntimeLease Reject(string reason) => new(_snapshot.RuntimeEpoch, reason, token);
                if (_activationReservation is not null) return Reject("RecipeActivationInProgress");
                if (_automaticProductionArm is { Terminal: false } or { ReadyAuditPending: true } ||
                    _manualMaintenanceArm is { Terminal: false } or { ReadyAuditPending: true })
                    return Reject("RecipeRetirementProductionArmInProgress");
                if (command.ExpectedActive is null || command.ExpectedActive != _currentRecipeActivationReference ||
                    _activeActivation is null || _activeActivation.Snapshot.Recipe != command.Recipe ||
                    _activeActivation.Snapshot.Release.ReleaseId != command.ReleaseId ||
                    _activeActivation.Snapshot.Release.ContentHash != command.ReleaseRecordContentHash)
                    return Reject("RecipeRetirementActiveChanged");
                var owner = _productionInspectionOwner;
                var drain = owner is { Current: not null, AdmissionCommitted: true };
                if (RecipeActivationBlockerLocked(null, drain) is { } blocker) return Reject(blocker);
                if (_snapshot.Recovery != RecoveryState.None || _productionInspectionRecoveryBlocked)
                    return Reject("RecipeRetirementRecoveryRequired");
                if (_productionInspectionOptions is not null &&
                    (owner is null || owner.Health is not { Healthy: true } || owner.Observer is null || owner.Aborted))
                    return Reject("RecipeRetirementCommunicationUnavailable");

                var reservation = new ActivationReservation(command.CorrelationId, new CancellationTokenSource())
                {
                    Purpose = ActivationReservationPurpose.Retirement,
                    RetirementConnectionGeneration = owner?.Health?.ConnectionGeneration ?? 0,
                    RetirementControllerEpoch = owner?.Health?.ControllerEpoch ?? 0
                };
                _activationReservation = reservation;
                var epoch = _snapshot.RuntimeEpoch;
                var active = command.ExpectedActive;
                var acceptedCycle = drain ? owner!.CycleRetired?.Task : null;
                var readyCleared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (owner is null) readyCleared.TrySetResult(true);
                else
                {
                    owner.RecipeRetirementReservation = reservation;
                    owner.RecipeRetirementReadyCleared = readyCleared;
                }
                reservation.ConnectCaller(token, () => { lock (_sync) reservation.RequestStop(); });
                _productionArmStopGeneration = checked(_productionArmStopGeneration + 1);
                _admissionGeneration = checked(_admissionGeneration + 1);
                PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                    LastCommand = new(command.CorrelationId, OperationState.Pending, "RecipeRetirementDraining"),
                    AdmissionBlockers = new(_snapshot.AdmissionBlockers.Append("RecipeRetirementInProgress")
                        .Distinct(StringComparer.Ordinal)) });
                PreparedAlgorithm? retired = null;
                return new(epoch, null, reservation.Cancellation.Token, active,
                    (budget, cancellation) => DrainRecipeRetirementAsync(reservation, owner, acceptedCycle,
                        readyCleared.Task, active, epoch, budget, cancellation),
                    cancellation => EnterRecipeRetirementCommitAsync(reservation, owner, readyCleared.Task,
                        active, epoch, cancellation),
                    record => ClearRetiredRecipe(reservation, command, record, ref retired),
                    budget => CleanupRetiredRecipeAsync(reservation, retired, budget),
                    (reason, recovery) => PublishRecipeRetirementTerminal(reservation, reason, recovery),
                    () => ReleaseRecipeRetirement(reservation, owner));
            }
        }
        finally { _commandGate.Release(); }
    }

    private async Task ClearRecipeRetirementReadyAsync(ProductionInspectionOwner owner,
        InspectionCycleOutputLatch output, CancellationToken token)
    {
        TaskCompletionSource<bool>? completion;
        ActivationReservation? reservation;
        lock (_sync)
        {
            completion = owner.RecipeRetirementReadyCleared;
            reservation = owner.RecipeRetirementReservation;
            if (completion is null || completion.Task.IsCompleted || reservation is null ||
                !ReferenceEquals(_productionInspectionOwner, owner) ||
                !ReferenceEquals(_activationReservation, reservation)) return;
        }
        try
        {
            // The output latch serializes this owner-fenced Ready=false with all
            // cycle writes. Busy, ResultValid and result/ACK state remain untouched.
            await output.ChangeAsync(token, ready: false, requireOwner: true).ConfigureAwait(false);
            lock (_sync)
                completion.TrySetResult(ReferenceEquals(_productionInspectionOwner, owner) &&
                    ReferenceEquals(_activationReservation, reservation) && owner.Health is { Healthy: true });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            completion.TrySetResult(false);
            throw;
        }
    }

    private async ValueTask<string?> DrainRecipeRetirementAsync(ActivationReservation reservation,
        ProductionInspectionOwner? owner, Task<bool>? acceptedCycle, Task<bool> readyCleared,
        RecipeActivationReference active, Guid epoch, TimeSpan budget, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, reservation.Cancellation.Token);
        var deadline = new StoreDeadline(budget);
        try
        {
            if (acceptedCycle is not null &&
                !await acceptedCycle.WaitAsync(deadline.Remaining, cancellation.Token).ConfigureAwait(false))
                return "RecipeRetirementProductionInterrupted";
            if (!await readyCleared.WaitAsync(deadline.Remaining, cancellation.Token).ConfigureAwait(false))
                return "RecipeRetirementReadyClearFailed";
            lock (_sync) return RecipeRetirementBlockerLocked(reservation, owner, readyCleared, active, epoch);
        }
        catch (TimeoutException) { return "RecipeRetirementQuiescenceTimeout"; }
        catch (OperationCanceledException) { return "RecipeRetirementCancelled"; }
    }

    private string? RecipeRetirementBlockerLocked(ActivationReservation reservation,
        ProductionInspectionOwner? owner, Task<bool> readyCleared, RecipeActivationReference active, Guid epoch)
    {
        if (!ReferenceEquals(_activationReservation, reservation) ||
            reservation.Purpose != ActivationReservationPurpose.Retirement)
            return "RecipeRetirementReservationLost";
        if (reservation.CommitClaimed) return null;
        if (epoch != _snapshot.RuntimeEpoch || active != _currentRecipeActivationReference)
            return "RecipeRetirementActiveChanged";
        if (readyCleared.Status != TaskStatus.RanToCompletion || !readyCleared.Result)
            return "RecipeRetirementReadyClearPending";
        if (owner is not null && (!ReferenceEquals(owner, _productionInspectionOwner) ||
                owner.Aborted || owner.FaultAbortRequested || owner.Health is not { Healthy: true } health ||
                health.ConnectionGeneration != reservation.RetirementConnectionGeneration ||
                health.ControllerEpoch != reservation.RetirementControllerEpoch))
            return "RecipeRetirementCommunicationUnavailable";
        if (_snapshot.Recovery != RecoveryState.None || _productionInspectionRecoveryBlocked)
            return "RecipeRetirementRecoveryRequired";
        if (_automaticProductionArm is { Terminal: false } or { ReadyAuditPending: true } ||
            _manualMaintenanceArm is { Terminal: false } or { ReadyAuditPending: true })
            return "RecipeRetirementProductionArmInProgress";
        return RecipeActivationBlockerLocked(reservation);
    }

    private async ValueTask<RecipeActivationCommitLease> EnterRecipeRetirementCommitAsync(
        ActivationReservation reservation, ProductionInspectionOwner? owner, Task<bool> readyCleared,
        RecipeActivationReference active, Guid epoch, CancellationToken token)
    {
        if (!await _commandGate.WaitAsync(_audit?.CommitTimeout ?? TimeSpan.FromSeconds(2), token).ConfigureAwait(false))
            return new(() => "RecipeRetirementRuntimeBusy");
        string? Fence(bool claim)
        {
            // The writer holds the identity session lease: never wait on the inverse
            // Runtime -> session lock order, and never call providers in this callback.
            if (!Monitor.TryEnter(_sync)) return "RecipeRetirementRuntimeBusy";
            try
            {
                var failure = RecipeRetirementBlockerLocked(reservation, owner, readyCleared, active, epoch);
                if (failure is null && claim) reservation.CommitClaimed = true;
                return failure;
            }
            finally { Monitor.Exit(_sync); }
        }
        return new(() => Fence(false), () => _commandGate.Release(), () => Fence(true));
    }

    private bool ClearRetiredRecipe(ActivationReservation reservation, RetireReleasedRecipeCommand command,
        RecipeLifecycleRecord record, ref PreparedAlgorithm? retired)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_activationReservation, reservation) || !reservation.CommitClaimed ||
                reservation.Committed || record.Kind != RecipeLifecycleKind.ReleasedRetired ||
                record.OperationId != command.CorrelationId || record.RuntimeEpoch != _snapshot.RuntimeEpoch ||
                record.ClearedActive != command.ExpectedActive || record.ClearedActive != _currentRecipeActivationReference ||
                record.Recipe != command.Recipe || record.ReleaseId != command.ReleaseId ||
                record.ReleaseRecordContentHash != command.ReleaseRecordContentHash)
                return false;
            reservation.Committed = true;
            // COMMIT already won. Even concurrent shutdown cannot restore the retired
            // selection; clear the owned pointers before invoking any plugin cleanup.
            retired = Interlocked.Exchange(ref _activeActivation, null)?.Algorithm;
            _currentRecipeActivationReference = null;
            PublishLocked(_snapshot with { ActiveRecipe = null, Ready = false,
                ArmState = ProductionArmState.Disarmed,
                AdmissionBlockers = new(_snapshot.AdmissionBlockers.Append("ActiveRecipeMissing")
                    .Distinct(StringComparer.Ordinal)) });
            return true;
        }
    }

    private async ValueTask<string?> CleanupRetiredRecipeAsync(ActivationReservation reservation,
        PreparedAlgorithm? retired, TimeSpan budget)
    {
        Task cleanup;
        lock (_sync)
        {
            if (!reservation.Committed) return "RecipeRetirementNotCommitted";
            // Task.Run prevents synchronous user disposal from retaining the caller's
            // command lane. Keep the actual task, including after a bounded timeout.
            cleanup = _recipeRetirementCleanup ??= retired is null ? Task.CompletedTask :
                Task.Run(async () => await retired.DisposeAsync().ConfigureAwait(false));
        }
        try
        {
            await cleanup.WaitAsync(budget).ConfigureAwait(false);
            lock (_sync)
                if (ReferenceEquals(_recipeRetirementCleanup, cleanup)) _recipeRetirementCleanup = null;
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = cleanup.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            PublishRecipeRetirementTerminal(reservation, "RecipeRetirementCleanupIncomplete", true);
            return "RecipeRetirementCleanupIncomplete";
        }
    }

    private void PublishRecipeRetirementTerminal(ActivationReservation reservation, string reason, bool recovery)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_activationReservation, reservation)) return;
            reservation.TerminalPublished = true;
            _activationRecoveryBlocked |= recovery;
            var blockers = _snapshot.AdmissionBlockers.Where(code => code != "RecipeRetirementInProgress").ToList();
            if (recovery) blockers.Add("RecipeRetirementRecoveryRequired");
            var last = _snapshot.LastCommand;
            if (last?.CorrelationId == reservation.CorrelationId)
                last = new(reservation.CorrelationId, reservation.Committed ? OperationState.Completed : OperationState.Failed, reason);
            PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                LastCommand = last, Recovery = recovery ? RecoveryState.Required : _snapshot.Recovery,
                AdmissionBlockers = new(blockers.Distinct(StringComparer.Ordinal)) });
        }
    }

    private void ReleaseRecipeRetirement(ActivationReservation reservation, ProductionInspectionOwner? owner)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_activationReservation, reservation))
            {
                if (!reservation.TerminalPublished)
                    PublishRecipeRetirementTerminal(reservation, "RecipeRetirementTerminalUnavailable", true);
                if (_recipeRetirementCleanup is { IsCompletedSuccessfully: false })
                    PublishRecipeRetirementTerminal(reservation, "RecipeRetirementCleanupIncomplete", true);
                _activationReservation = null;
            }
            if (owner is not null && ReferenceEquals(owner.RecipeRetirementReservation, reservation))
            {
                owner.RecipeRetirementReservation = null;
                owner.RecipeRetirementReadyCleared = null;
            }
            reservation.Completion.TrySetResult(true);
        }
        reservation.DisposeCancellationWhenRetired();
    }
}
