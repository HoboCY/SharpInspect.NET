using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cycles;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private RecipeChangeRequestEvidence CaptureRecipeChangeRequest(ProductionInspectionOwner owner,
        uint controllerEpoch, uint sequence, uint code, RecipeSelectionRevision? selection)
    {
        var profile = _productionInspectionOptions!.Profile;
        return new(owner.RuntimeEpoch, profile.EndpointBindingHash, new(profile.Id, profile.Version, profile.ContentHash),
            controllerEpoch, sequence, code, selection?.Reference, selection?.Policy.Reference ?? RecipeSelectionPolicy.Default.Reference,
            selection?.Map?.Reference, selection?.Map?.Entries.SingleOrDefault(value => value.SelectionCode == code), DateTimeOffset.UtcNow);
    }

    private PlcRecipeChangeOperation BeginRecipeChange(ProductionInspectionOwner owner,
        ModbusRecipeChangeControllerSignals signals, uint epoch)
    {
        var selection = Volatile.Read(ref _recipeSelectionCurrent);
        var request = CaptureRecipeChangeRequest(owner, epoch, signals.RequestSequence, signals.SelectionCode, selection);
        PlcRecipeActivationCapability? capability = null;
        RecipeChangeDecision? rejection = null;
        try
        {
            if (owner.RecipeChangeObservedProductionRequest)
                rejection = new(RecipeChangeOutcome.RejectedBusy, RecipeChangeReason.RuntimeBusy, "RecipeChangeProductionRequestConflict");
            else if (selection?.Policy.Mode != RecipeSelectionMode.PlcRequestedActivation)
                rejection = new(RecipeChangeOutcome.RejectedUnknownCode, RecipeChangeReason.LocalOperatorOnly, "RecipeChangeLocalOperatorOnly");
            else if (request.Target is null)
                rejection = new(RecipeChangeOutcome.RejectedUnknownCode, RecipeChangeReason.UnknownCode, "RecipeChangeUnknownSelectionCode");
            else
            {
                capability = TryReservePlcRecipeActivation(owner, request, selection, out var reason);
                if (capability is null)
                    rejection = reason == "RecipeActivationRecipeRetired"
                        ? new(RecipeChangeOutcome.FailedActivation, RecipeChangeReason.RecipeRetired, reason)
                        : new(RecipeChangeOutcome.RejectedBusy, RecipeChangeReason.RuntimeBusy, reason);
            }
            // This latch blocks new admissions and map changes through the entire
            // response/ACK/reset handshake; it never interrupts an existing inspection.
            _recipeChangeInProgress = true;
            owner.RecipeChangeRequest = request;
            owner.RecipeChangeActivation = capability;
            owner.RecipeChangeDecision = null;
            owner.RecipeChangeFaultRecorded = false;
            owner.RecipeChangeReadyCleared = capability is null ? null : new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new(token => ExecuteRecipeChangeAsync(owner, request, capability, rejection, token),
                () => capability?.Revoke());
        }
        catch
        {
            capability?.Revoke();
            capability?.Runtime.Dispose();
            throw;
        }
    }

    private async Task<RecipeChangeDecision> ExecuteRecipeChangeAsync(ProductionInspectionOwner owner,
        RecipeChangeRequestEvidence request, PlcRecipeActivationCapability? capability,
        RecipeChangeDecision? rejection, CancellationToken token)
    {
        try
        {
            var observed = await AppendRecipeChangeAsync(request, RecipeChangeEventKind.RequestObserved, null, "RecipeChangeRequestObserved")
                .ConfigureAwait(false);
            if (observed.Duplicate)
                return new(RecipeChangeOutcome.ProtocolFault, RecipeChangeReason.DuplicateRequest, observed.ReasonCode);
            if (!observed.Committed) throw new InvalidOperationException(observed.ReasonCode);
            RecipeChangeDecision decision;
            if (rejection is not null) decision = rejection; // Never reconsider a busy observation after waiting for storage.
            else if (capability is null || token.IsCancellationRequested)
                decision = new(RecipeChangeOutcome.FailedActivation, RecipeChangeReason.Cancelled, "RecipeActivationCancelled");
            else
            {
                // Runtime Ready was cleared atomically at reservation. Wait for its
                // owner-fenced PLC write before staging any candidate hardware.
                await owner.RecipeChangeReadyCleared!.Task.WaitAsync(token).ConfigureAwait(false);
                var service = _recipeActivations as RecipeActivationService ??
                    throw new InvalidOperationException("RecipeChangeActivationAuthorityUnavailable");
                var command = new ActivateRecipeCommand(request.OperationId,
                    new(CommandSource.Integration, SystemPrincipalId.PlcAdapter), capability.Context,
                    capability.ExpectedActive, "PlcMappedRecipeActivation", request.OperationId);
                var result = await service.ActivatePlcAsync(command, capability, token).ConfigureAwait(false);
                if (result.Outcome.Audit == AuditPersistence.Unavailable)
                    throw new InvalidOperationException(result.Outcome.ReasonCode);
                if (result.Record is { Outcome.Succeeded: true } succeeded)
                {
                    bool installed;
                    lock (_sync) installed = !_activationRecoveryBlocked &&
                        _activeActivation?.Snapshot.ContentHash == succeeded.SuccessfulSnapshot?.ContentHash;
                    decision = installed
                        ? new(RecipeChangeOutcome.Succeeded, RecipeChangeReason.None, "RecipeActivated", succeeded.Reference)
                        : new(RecipeChangeOutcome.ProtocolFault, RecipeChangeReason.RestorationFailed,
                            "RecipeActivationPostCommitRecoveryRequired", succeeded.Reference);
                }
                else if (result.Record is null && RecipeActivationRuntimeLease.IsRuntimeBusy(result.Outcome.ReasonCode))
                    decision = new(RecipeChangeOutcome.RejectedBusy, RecipeChangeReason.RuntimeBusy, result.Outcome.ReasonCode);
                else
                    decision = new(RecipeChangeOutcome.FailedActivation,
                        result.Outcome.ReasonCode == "RecipeActivationRecipeRetired" ? RecipeChangeReason.RecipeRetired :
                        result.Record?.Restoration.State == RecipeActivationRestorationState.Failed ? RecipeChangeReason.RestorationFailed :
                        token.IsCancellationRequested ? RecipeChangeReason.Cancelled : RecipeChangeReason.ActivationFailed,
                        result.Outcome.ReasonCode, result.Record?.Reference);
            }
            var stored = await AppendRecipeChangeAsync(request, RecipeChangeEventKind.DecisionCommitted, decision, decision.ReasonCode)
                .ConfigureAwait(false);
            if (!stored.Committed) throw new InvalidOperationException(stored.ReasonCode);
            owner.RecipeChangeDecision = decision;
            return decision;
        }
        finally { capability?.Runtime.Dispose(); }
    }

    private ValueTask<RecipeChangeWriteResult> AppendRecipeChangeAsync(RecipeChangeRequestEvidence request,
        RecipeChangeEventKind kind, RecipeChangeDecision? decision, string reason) =>
        ((SqliteCommandStore)_audit!).AppendRecipeChangeEventAsync(request, kind, decision?.Outcome, decision?.Reason,
            reason, decision?.Activation, new StoreDeadline(_audit!.CommitTimeout), CancellationToken.None);

    private async Task RecordRecipeChangeTransitionAsync(ProductionInspectionOwner owner, RecipeChangeTransition transition, uint epoch)
    {
        var request = owner.RecipeChangeRequest ?? CaptureRecipeChangeRequest(owner, epoch, transition.RequestSequence,
            transition.SelectionCode, Volatile.Read(ref _recipeSelectionCurrent));
        var decision = transition.Kind == RecipeChangeEventKind.ProtocolFault
            ? owner.RecipeChangeDecision ?? transition.Decision : transition.Decision;
        if (transition.Kind == RecipeChangeEventKind.ProtocolFault && owner.RecipeChangeFaultRecorded) return;
        if (transition.Kind == RecipeChangeEventKind.ProtocolFault)
        {
            // A dedicated handshake can fault while no production cycle exists.
            // Preserve that recovery requirement even if its audit append also fails.
            lock (_sync)
            {
                _productionInspectionRecoveryBlocked = true;
                PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                    Recovery = RecoveryState.Required });
            }
        }
        var stored = await AppendRecipeChangeAsync(request, transition.Kind, decision,
            transition.ReasonCode ?? decision?.ReasonCode ?? "RecipeChangeProtocolTransition").ConfigureAwait(false);
        if (!stored.Committed) throw new InvalidOperationException(stored.ReasonCode);
        if (transition.Kind == RecipeChangeEventKind.ProtocolFault) owner.RecipeChangeFaultRecorded = true;
        if (transition.Kind == RecipeChangeEventKind.ResetObserved)
        {
            owner.RecipeChangeRequest = null;
            owner.RecipeChangeActivation = null;
            owner.RecipeChangeDecision = null;
            owner.RecipeChangeReadyCleared = null;
            _recipeChangeInProgress = false;
        }
    }

    private async Task InitializeRecipeChangeHandshakeAsync(ProductionInspectionOwner owner,
        ModbusQualificationChannel channel, uint epoch, CancellationToken token)
    {
        var binding = _productionInspectionOptions!.Profile.RecipeChange;
        if (binding is null) return;
        if (_productionInspectionStoreOptions!.RecipeSelections is null)
            throw new InvalidOperationException("RecipeSelectionConfigurationRequired");
        owner.RecipeChange = new(binding.HandshakeTimeout, value => BeginRecipeChange(owner, value, epoch),
            transition => RecordRecipeChangeTransitionAsync(owner, transition, epoch), channel.WriteRecipeChangeResponseAsync);
        await owner.RecipeChange.InitializeAsync(await channel.ReadRecipeChangeControllerAsync(token).ConfigureAwait(false),
            await channel.ReadRecipeChangeRuntimeAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private async Task<ModbusControllerSignals> ReadProductionAndRecipeChangeAsync(ProductionInspectionOwner owner,
        ModbusQualificationChannel channel, InspectionCycleOutputLatch output, CancellationToken token)
    {
        var communication = owner.Communication!;
        var signals = await communication.ReadAsync(channel, token).ConfigureAwait(false);
        if (owner.RecipeChange is not { } handshake) return signals;
        var request = await channel.ReadRecipeChangeControllerAsync(token).ConfigureAwait(false);
        var after = await communication.ReadAsync(channel, token).ConfigureAwait(false);
        if (signals.ControllerEpoch != after.ControllerEpoch)
            throw new InvalidOperationException("QualificationControllerEpochChanged");
        owner.RecipeChangeObservedProductionRequest = signals.Trigger || signals.ResultAck || after.Trigger || after.ResultAck;
        await handshake.ObserveAsync(request, token).ConfigureAwait(false);
        if (owner.RecipeChangeReadyCleared is { Task.IsCompleted: false } cleared)
        {
            try
            {
                await output.ChangeAsync(token, ready: false, requireOwner: true).ConfigureAwait(false);
                cleared.TrySetResult(true);
            }
            catch (Exception exception)
            {
                owner.RecipeChangeActivation?.Revoke();
                cleared.TrySetException(exception);
                throw;
            }
        }
        return after;
    }

    private async Task RetireRecipeChangeHandshakeAsync(ProductionInspectionOwner owner)
    {
        var handshake = owner.RecipeChange;
        if (handshake is null) return;
        var active = handshake.Active;
        handshake.Dispose(); // Revocation is synchronous; cancellation callbacks run independently.
        var retirementFailed = false;
        try
        {
            if (active && owner.RecipeChangeRequest is { } request && !owner.RecipeChangeFaultRecorded)
                await RecordRecipeChangeTransitionAsync(owner, new(RecipeChangeEventKind.ProtocolFault,
                    request.RequestSequence, request.SelectionCode,
                    new(RecipeChangeOutcome.ProtocolFault, RecipeChangeReason.CommunicationLost, "RecipeChangeOwnerRetired"),
                    "RecipeChangeOwnerRetired"), request.ControllerEpoch).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { retirementFailed = true; }
        // Audit failure cannot skip the bounded join of actual activation/restoration.
        try { await handshake.Completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { retirementFailed = true; }
        if (retirementFailed)
        {
            lock (_sync)
            {
                _activationRecoveryBlocked = true;
                PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed, Recovery = RecoveryState.Required });
            }
        }
        if (handshake.Completion.IsCompleted) _recipeChangeInProgress = false;
        owner.RecipeChange = null;
    }
}
