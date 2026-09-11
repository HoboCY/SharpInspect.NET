using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private volatile bool _recipeChangeInProgress;
    private PlcRecipeRetirementVeto? _plcRecipeRetirementFixture;

    // Tests can install one immutable, deny-only fixture while the station is idle.
    // No public registration, positive authorization or lifecycle publication exists.
    internal void ConfigurePlcRecipeRetirementFixture(PlcRecipeRetirementVeto fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        lock (_sync)
        {
            if (_plcRecipeRetirementFixture is not null || _snapshot.Ready || _snapshot.Busy ||
                _snapshot.ArmState != ProductionArmState.Disarmed || _activationReservation is not null ||
                _recipeChangeInProgress || _recipeSelectionChangeInProgress)
                throw new InvalidOperationException("RecipeChangeRetirementFixtureNotQuiescent");
            _plcRecipeRetirementFixture = fixture;
        }
    }

    private string? PlcRecipeRetirementBlocker(RecipeChangeRequestEvidence request) =>
        request.Target is { } target && _plcRecipeRetirementFixture?.IsRetired(target) == true
            ? "RecipeActivationRecipeRetired" : null;

    private PlcRecipeActivationCapability? TryReservePlcRecipeActivation(ProductionInspectionOwner owner,
        RecipeChangeRequestEvidence request, RecipeSelectionRevision selection, out string reason)
    {
        reason = "RecipeActivationRuntimeBusy";
        if (!_commandGate.Wait(0)) return null;
        try
        {
            if (!Monitor.TryEnter(_sync)) return null;
            try
            {
                if (!_recipeSelectionStartupVerified || _recipeSelectionCurrent?.Reference != selection.Reference ||
                    selection.Policy.Mode != RecipeSelectionMode.PlcRequestedActivation ||
                    request.SelectionRevision != selection.Reference || request.SelectionPolicy != selection.Policy.Reference ||
                    request.SelectionMap != selection.Map?.Reference || request.Target is null ||
                    selection.Map?.Entries.Any(entry => entry.ContentHash == request.Target.ContentHash) != true)
                { reason = "RecipeChangeSelectionUnavailable"; return null; }
                if (PlcActivationOwnerBlockerLocked(owner, request) is { } ownerFailure)
                { reason = ownerFailure; return null; }
                if (_activationReservation is not null)
                { reason = "RecipeActivationInProgress"; return null; }
                // The observer sets _recipeChangeInProgress only AFTER this atomic admission.
                if (RecipeActivationBlockerLocked(null) is { } blocker)
                { reason = blocker; return null; }
                if (PlcRecipeRetirementBlocker(request) is { } retired)
                { reason = retired; return null; }
                var reservation = new ActivationReservation(request.OperationId, new CancellationTokenSource()) { PlcOwned = true };
                _activationReservation = reservation;
                RecipeActivationRuntimeLease? lease = null;
                try
                {
                    reservation.ConnectCaller(owner.Cancellation.Token, () => { lock (_sync) reservation.RequestStop(); });
                    lease = new(_snapshot.RuntimeEpoch, null, reservation.Cancellation.Token, _cameraSetupRuntime,
                        cancellation => EnterRecipeActivationCommitAsync(reservation, cancellation),
                        (snapshot, prepared) => InstallRecipeActivation(reservation, snapshot, prepared),
                        (failure, recovery) => PublishRecipeActivationTerminal(reservation, failure, recovery),
                        () => ReleaseRecipeActivation(reservation), () => ReadRecipeActivationBlocker(reservation),
                        () => TryBeginRecipeActivationPhysicalPhase(reservation),
                        record => RecordCommittedRecipeActivation(reservation, record));
                    var capability = new PlcRecipeActivationCapability(request, selection, lease, _currentRecipeActivationReference,
                        () => { _ = Task.Run(() =>
                        {
                            lock (_sync)
                                if (ReferenceEquals(_activationReservation, reservation)) reservation.RequestStop();
                        }); });
                    reservation.ExternalBlocker = () => capability.RevocationFailure ?? PlcActivationOwnerBlockerLocked(owner, request)
                        ?? PlcRecipeRetirementBlocker(request);
                    PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                        LastCommand = new CommandProgress(request.OperationId, OperationState.Pending, "RecipeActivationPreparing"),
                        AdmissionBlockers = new AdmissionBlockers(_snapshot.AdmissionBlockers.Append("RecipeActivationInProgress")
                            .Distinct(StringComparer.Ordinal)) });
                    reason = "RecipeActivationQuiescenceReserved";
                    return capability;
                }
                catch
                {
                    if (lease is not null) lease.Dispose();
                    else { _activationReservation = null; reservation.DisposeCancellationWhenRetired(); }
                    throw;
                }
            }
            finally { Monitor.Exit(_sync); }
        }
        finally { _commandGate.Release(); }
    }

    private string? PlcActivationOwnerBlockerLocked(ProductionInspectionOwner owner, RecipeChangeRequestEvidence request)
    {
        if (!ReferenceEquals(_productionInspectionOwner, owner) || owner.Aborted ||
            owner.RuntimeEpoch != request.RuntimeEpoch || _snapshot.RuntimeEpoch != request.RuntimeEpoch ||
            _productionInspectionOptions?.Profile.EndpointBindingHash != request.EndpointContentHash ||
            _productionInspectionOptions.Profile.ContentHash != request.ProtocolProfile.ContentHash ||
            owner.Health is not { Healthy: true } || owner.Health.ControllerEpoch != request.ControllerEpoch)
            return "RecipeChangeCommunicationUnavailable";
        if (_productionInspectionRecoveryBlocked || _productionRecoveryOwner is not null ||
            _snapshot.Recovery != RecoveryState.None || owner.PartIdentityAttempt is not null ||
            owner.PartIdentityOperation is { IsCompleted: false }) return "RecipeChangeRecoveryOrAcquisitionConflict";
        return null;
    }
}
