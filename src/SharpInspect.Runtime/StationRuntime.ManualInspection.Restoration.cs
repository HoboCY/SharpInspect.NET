using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async Task RestoreManualInspectionAsync(ManualInspectionOwner owner)
    {
        var retirementFailed = false;
        lock (_sync) PublishManualInspectionLocked(owner, ManualInspectionSessionPhase.Restoring, owner.ExitReason);
        retirementFailed |= !await WaitForManualPhysicalRetirementAsync(owner, owner.PreparationRetirement,
            "ManualInspectionPreparationRetirementPending").ConfigureAwait(false);
        if (owner.Prepared is { } prepared)
        {
            var actualRetirement = prepared.DisposeAsync().AsTask();
            retirementFailed |= !await WaitForManualPhysicalRetirementAsync(owner, actualRetirement,
                "ManualInspectionAlgorithmRetirementPending").ConfigureAwait(false);
        }
        if (owner.Execution is { } execution) await execution.DisposeAsync().ConfigureAwait(false);
        lock (_sync) { owner.CurrentRunId = null; owner.CurrentRun = null; }
        var restoration = ManualInspectionRestorationState.NotRequired;
        var reason = owner.ExitReason;
        var restored = true;
        if (owner.Camera is { } camera)
        {
            var cameraRetired = await WaitForManualPhysicalRetirementAsync(owner,
                DrainManualCameraRetirementAsync(camera), "ManualInspectionCameraRetirementPending").ConfigureAwait(false);
            retirementFailed |= !cameraRetired;
            if (camera.Available && camera.HardwareTouched)
            {
                var result = await camera.RestoreAsync(owner.Baseline?.SuccessfulSnapshot?.CameraSetup,
                    CancellationToken.None).ConfigureAwait(false);
                restored = result.Succeeded;
                restoration = !restored ? ManualInspectionRestorationState.RecoveryBlocked : owner.Baseline is null
                    ? ManualInspectionRestorationState.NoActiveBaselineClosed : ManualInspectionRestorationState.Restored;
                if (!restored) reason = result.ReasonCode;
            }
            if (restored) await camera.DisposeAsync().ConfigureAwait(false);
        }
        if (retirementFailed || _executionGuard.IsHung)
        { restored = false; restoration = ManualInspectionRestorationState.RecoveryBlocked; reason = "ManualInspectionResourceRetirementFailed"; }
        await FinishManualInspectionAsync(owner, restored && !owner.Aborted, reason, restoration).ConfigureAwait(false);
    }

    private async Task DrainManualCameraRetirementAsync(RecipeActivationCameraLease camera)
    {
        while (true)
        {
            var observation = await camera.WaitForManualRetirementAsync(_manualOptions!.ShutdownTimeout)
                .ConfigureAwait(false);
            if (!observation.Completed) continue;
            if (!observation.SafeToReplace) throw new InvalidOperationException(observation.ReasonCode);
            return;
        }
    }

    private async Task<bool> WaitForManualPhysicalRetirementAsync(ManualInspectionOwner owner,
        Task actualRetirement, string pendingReason)
    {
        try
        {
            await actualRetirement.WaitAsync(_manualOptions!.ShutdownTimeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            lock (_sync)
            {
                _manualRecoveryBlocked = true;
                owner.Restoration = ManualInspectionRestorationState.RecoveryBlocked;
                PublishManualInspectionLocked(owner, ManualInspectionSessionPhase.RecoveryBlocked, pendingReason);
            }
            await LatchManualInspectionRecoveryAlarmAsync().ConfigureAwait(false);
            // Retain the exclusive owner until the provider's actual work finishes.
            // Host shutdown has a separate bounded wait and cannot release it early.
            try { await actualRetirement.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return false; }
    }

    private async Task FinishManualInspectionAsync(ManualInspectionOwner owner, bool succeeded,
        string reason, ManualInspectionRestorationState restoration)
    {
        if (owner.Retired.Task.IsCompleted) return;
        if (Interlocked.CompareExchange(ref owner.TerminalStarted, 1, 0) != 0)
        { await owner.Retired.Task.ConfigureAwait(false); return; }
        var entered = false;
        var persisted = false;
        try
        {
            entered = await _commandGate.WaitAsync(_audit!.CommitTimeout).ConfigureAwait(false);
            if (!entered) throw new InvalidOperationException("ManualInspectionTerminalRuntimeBusy");
            foreach (var previous in owner.PriorExitCommands)
            {
                var superseded = await _authorization!.CompleteManualInspectionCommandAsync(previous.Command,
                    owner.Header, previous.Fact, new(false, "ManualInspectionExitEscalatedToAbort",
                        ManualInspectionSessionPhase.Restoring, ManualInspectionRestorationState.Pending,
                        CompleteCommand: true), new StoreDeadline(_audit.CommitTimeout)).ConfigureAwait(false);
                if (superseded.Outcome.Audit != AuditPersistence.Persisted || superseded.Header is null)
                    throw new InvalidOperationException("ManualInspectionPriorExitTerminalUnavailable");
                owner.Header = superseded.Header;
            }
            var phase = restoration == ManualInspectionRestorationState.RecoveryBlocked
                ? ManualInspectionSessionPhase.RecoveryBlocked : ManualInspectionSessionPhase.Closed;
            ManualInspectionCommand command = owner.ExitCommand ?? (ManualInspectionCommand)StartManualInspectionContinuation(owner);
            var fact = owner.ExitFact ?? owner.StartFact;
            var result = await _authorization!.CompleteManualInspectionCommandAsync(command, owner.Header, fact,
                new(succeeded, reason, phase, restoration, CompleteOriginalStart: true, CompleteCommand: true),
                new StoreDeadline(_audit.CommitTimeout)).ConfigureAwait(false);
            if (result.Outcome.Audit != AuditPersistence.Persisted || result.Header is null)
                throw new InvalidOperationException("ManualInspectionTerminalAuditUnavailable");
            owner.Header = result.Header;
            persisted = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { MarkAuditFault("ManualInspectionTerminalAuditUnavailable"); }
        finally
        {
            if (entered) _commandGate.Release();
            lock (_sync)
            {
                var blocked = !persisted || restoration == ManualInspectionRestorationState.RecoveryBlocked || _manualRecoveryBlocked;
                _manualRecoveryBlocked |= blocked;
                owner.Restoration = blocked ? ManualInspectionRestorationState.RecoveryBlocked : restoration;
                owner.PendingCommand = null;
                owner.PendingFact = null;
                PublishManualInspectionLocked(owner, blocked ? ManualInspectionSessionPhase.RecoveryBlocked :
                    ManualInspectionSessionPhase.Closed, persisted ? reason : "ManualInspectionTerminalAuditUnavailable");
                if (!blocked && ReferenceEquals(_manualOwner, owner))
                {
                    _manualOwner = null;
                    PublishLocked(_snapshot with
                    {
                        Mode = ExclusiveMode.None, Ready = false, ArmState = ProductionArmState.Disarmed,
                        Busy = false, CurrentExecution = null,
                        AdmissionBlockers = new(_snapshot.AdmissionBlockers.Where(code => code is not
                            "ManualInspectionSessionInProgress" and not "ManualInspectionStartupRecoveryPending"))
                    });
                }
            }
            try { if (_manualRecoveryBlocked) await LatchManualInspectionRecoveryAlarmAsync().ConfigureAwait(false); }
            finally { owner.Retired.TrySetResult(true); }
        }
    }
}
