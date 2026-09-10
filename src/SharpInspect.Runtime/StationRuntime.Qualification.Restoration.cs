using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async Task RestoreStationQualificationAsync(StationQualificationOwner owner)
    {
        if (owner.ModbusRecoveryRequired)
        {
            lock (_sync)
            {
                _stationQualificationRecoveryBlocked = true;
                owner.ExitRequested = owner.Aborted = true;
                owner.CycleFaultTerminated = true;
                owner.Restoration = StationQualificationRestorationState.RecoveryBlocked;
            }
            // Retire actual owners without restoring the controller or releasing
            // isolation. An unresolved ResultValid/Ack latch requires a separately
            // governed recovery; neither Exit nor process restart supplies it.
            owner.ResourcesRetired = await RetireStationQualificationAfterJournalFailureAsync(owner).ConfigureAwait(false);
            await FinishStationQualificationAsync(owner, false, "QualificationModbusRecoveryRequired").ConfigureAwait(false);
            return;
        }
        var restored = false;
        var reason = owner.ExitReason;
        try
        {
            lock (_sync)
            {
                owner.ExitRequested = true;
                owner.RecoveryAttempt = new(_snapshot.RuntimeEpoch, Guid.NewGuid());
                owner.ObservationSequence = 0;
                PublishStationQualificationLocked(owner, StationQualificationSessionPhase.Restoring, reason);
            }
            CancelStationQualification(owner, abort: false);
            // Persist the new recovery identity before changing physical state.
            // The previous lease must still retire before a new one is opened.
            try
            {
                await RecordStationQualificationProgressAsync(owner, StationQualificationSessionPhase.Restoring,
                    "StationQualificationRecoveryAttemptStarted").ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                lock (_sync) _stationQualificationRecoveryBlocked = true;
                // A new physical lease needs the committed recovery identity.
                // Existing callbacks and devices must still retire if that
                // journal write fails; disposal preserves output isolation.
                await RetireStationQualificationAfterJournalFailureAsync(owner).ConfigureAwait(false);
                throw;
            }
            if (owner.CurrentRun is { Terminal: false } interrupted)
            {
                try
                {
                    await CompleteStationQualificationRunAsync(owner, null, ExecutionStatus.Cancelled,
                        "StationQualificationRunRevokedBeforeCompletion", interrupted.FrameMetadata,
                        interrupted.FrameProvenance, null).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    lock (_sync) _stationQualificationRecoveryBlocked = true;
                    MarkAuditFault("StationQualificationInterruptedRunAuditUnavailable");
                }
            }
            await WaitForStationQualificationRetirementAsync(owner, owner.PreparationRetirement, "StationQualificationPreparationRetirement").ConfigureAwait(false);
            if (owner.Prepared is { } prepared)
                await WaitForStationQualificationRetirementAsync(owner, prepared.DisposeAsync().AsTask(),
                    "StationQualificationAlgorithmRetirement").ConfigureAwait(false);
            if (owner.Execution is { } execution) await execution.DisposeAsync().ConfigureAwait(false);
            if (owner.Camera is { } currentCamera)
                await WaitForStationQualificationRetirementAsync(owner, DrainStationQualificationCameraAsync(currentCamera),
                    "StationQualificationCameraRetirement").ConfigureAwait(false);
            owner.FacilityLostRegistration.Dispose();
            if (_stationQualificationFacility is null || _stationQualificationFacility.Identity != owner.Header.Plan.Harness)
                throw new InvalidOperationException("StationQualificationRecoveryFacilityUnavailable");
            if (owner.Facility is { } previousFacility)
                await WaitForStationQualificationRetirementAsync(owner, previousFacility.DisposeAsync().AsTask(),
                    "StationQualificationPreviousFacilityRetirement").ConfigureAwait(false);
            owner.Facility = null;
            owner.Facility = await ObserveStationQualificationOperationAsync(owner,
                CaptureStationQualificationFacilityAsync(owner, CancellationToken.None),
                "StationQualificationRecoveryFacilityOpen").ConfigureAwait(false);
            // Recovery also establishes and reads isolation before touching a
            // persistent target. A disconnected facility is never success evidence.
            var isolated = await ObserveStationQualificationOperationAsync(owner,
                owner.Facility.ApplyIsolationAsync(CancellationToken.None).AsTask(), "StationQualificationRecoveryIsolation").ConfigureAwait(false);
            if (!isolated.Succeeded) throw new InvalidOperationException(isolated.ReasonCode);
            await ObserveStationQualificationFacilityAsync(owner, isolated: true, targetController: false,
                CancellationToken.None).ConfigureAwait(false);
            if (owner.Camera is { HardwareTouched: false } untouched)
            {
                await untouched.DisposeAsync().ConfigureAwait(false);
                owner.Camera = null;
            }
            var cameraResult = owner.Camera is { Available: true, HardwareTouched: true } camera
                ? await camera.RestoreAsync(owner.Header.TargetBaseline.SuccessfulSnapshot!.CameraSetup, CancellationToken.None).ConfigureAwait(false)
                : await _cameraSetupRuntime.RecoverQualificationAfterRestartAsync(owner.Header.TargetBaseline,
                    CancellationToken.None).ConfigureAwait(false);
            if (!cameraResult.Succeeded) throw new InvalidOperationException(cameraResult.ReasonCode);
            var controller = await ObserveStationQualificationOperationAsync(owner,
                owner.Facility.RestoreControllerAsync(owner.Header.Plan.TargetControllerConfiguration, CancellationToken.None).AsTask(),
                "StationQualificationControllerRestore").ConfigureAwait(false);
            if (!controller.Succeeded) throw new InvalidOperationException(controller.ReasonCode);
            var target = await ObserveStationQualificationFacilityAsync(owner, isolated: true, targetController: true,
                CancellationToken.None).ConfigureAwait(false);
            await RecordStationQualificationRestorationProgressAsync(owner, "StationQualificationTargetRestored", target).ConfigureAwait(false);
            var released = await ObserveStationQualificationOperationAsync(owner,
                owner.Facility.ReleaseIsolationAsync(CancellationToken.None).AsTask(), "StationQualificationIsolationRelease").ConfigureAwait(false);
            if (!released.Succeeded) throw new InvalidOperationException(released.ReasonCode);
            var final = await ObserveStationQualificationFacilityAsync(owner, isolated: false, targetController: true,
                CancellationToken.None).ConfigureAwait(false);
            await RecordStationQualificationRestorationProgressAsync(owner, "StationQualificationTargetReadbackVerified", final).ConfigureAwait(false);
            if (owner.Camera is { } restoredCamera) await restoredCamera.DisposeAsync().ConfigureAwait(false);
            await WaitForStationQualificationRetirementAsync(owner, owner.Facility.DisposeAsync().AsTask(),
                "StationQualificationFacilityRetirement").ConfigureAwait(false);
            owner.ResourcesRetired = true;
            restored = !_executionGuard.IsHung;
            if (!restored) reason = "StationQualificationAlgorithmHung";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reason = exception is InvalidOperationException or TimeoutException &&
                exception.Message.Length is > 0 and <= 128 && exception.Message.All(character => char.IsLetterOrDigit(character) || character == '_')
                ? exception.Message : "StationQualificationRestorationFailed";
        }
        finally
        {
            owner.CurrentRunId = null;
            owner.CurrentRun = null;
            await FinishStationQualificationAsync(owner, restored, reason).ConfigureAwait(false);
        }
    }

    private async Task WaitForStationQualificationRetirementAsync(StationQualificationOwner owner, Task actual, string reason)
    {
        try { await actual.WaitAsync(_stationQualificationOptions!.ShutdownTimeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            lock (_sync)
            {
                _stationQualificationRecoveryBlocked = true;
                owner.Restoration = StationQualificationRestorationState.RecoveryBlocked;
                PublishStationQualificationLocked(owner, StationQualificationSessionPhase.RecoveryBlocked, reason + "Pending");
            }
            // Keep the resource owner until the underlying provider really retires.
            await actual.ConfigureAwait(false);
        }
    }

    private async Task<bool> RetireStationQualificationAfterJournalFailureAsync(StationQualificationOwner owner)
    {
        var retired = true;
        CancelStationQualification(owner, abort: true);
        await RetireAsync(() => owner.PreparationRetirement, "StationQualificationPreparationRetirement").ConfigureAwait(false);
        if (owner.Prepared is { } prepared)
            await RetireAsync(() => prepared.DisposeAsync().AsTask(), "StationQualificationAlgorithmRetirement").ConfigureAwait(false);
        if (owner.Execution is { } execution)
            await RetireAsync(() => execution.DisposeAsync().AsTask(), "StationQualificationExecutionRetirement").ConfigureAwait(false);
        if (owner.Camera is { } camera)
            await RetireAsync(async () =>
            {
                await DrainStationQualificationCameraAsync(camera).ConfigureAwait(false);
                await camera.DisposeAsync().ConfigureAwait(false);
            }, "StationQualificationCameraRetirement").ConfigureAwait(false);
        owner.FacilityLostRegistration.Dispose();
        if (owner.Facility is { } facility)
            await RetireAsync(() => facility.DisposeAsync().AsTask(), "StationQualificationFacilityRetirement").ConfigureAwait(false);
        return retired;

        async Task RetireAsync(Func<Task> retire, string retirementReason)
        {
            try { await WaitForStationQualificationRetirementAsync(owner, retire(), retirementReason).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                retired = false;
                // Failure in one owner cannot suppress attempts to retire the
                // remaining owners. The station fence remains in place.
                lock (_sync) _stationQualificationRecoveryBlocked = true;
            }
        }
    }

    private async Task RecordStationQualificationRestorationProgressAsync(StationQualificationOwner owner,
        string reason, QualificationFacilityObservation? observation = null)
    {
        try { await RecordStationQualificationProgressAsync(owner, StationQualificationSessionPhase.Restoring,
            reason, observation).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Losing the journal blocks subsequent admission, but does not
            // suppress the best-effort physical restoration of the frozen target.
            lock (_sync) _stationQualificationRecoveryBlocked = true;
            MarkAuditFault("StationQualificationRestorationAuditUnavailable");
        }
    }

    private async Task DrainStationQualificationCameraAsync(RecipeActivationCameraLease camera)
    {
        while (true)
        {
            var retired = await camera.WaitForManualRetirementAsync(_stationQualificationOptions!.ShutdownTimeout).ConfigureAwait(false);
            if (!retired.Completed) continue;
            if (!retired.SafeToReplace) throw new InvalidOperationException(retired.ReasonCode);
            return;
        }
    }
}
