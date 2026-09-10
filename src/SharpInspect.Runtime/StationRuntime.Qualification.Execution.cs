using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Qualification;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async Task ExecuteStationQualificationAsync(StationQualificationOwner owner)
    {
        try
        {
            await RequireStationQualificationAuthorityAsync(owner).ConfigureAwait(false);
            owner.Facility = await StartStationQualificationFacilityOperationAsync(owner,
                () => CaptureStationQualificationFacilityAsync(owner, owner.Cancellation.Token),
                "StationQualificationFacilityOpen").ConfigureAwait(false);
            owner.FacilityLostRegistration = owner.Facility.FacilityLost.Register(() =>
            {
                lock (_sync) RequestStationQualificationExitLocked(owner, "StationQualificationFacilityLost", abort: true);
            });
            var baselineObservation = await ObserveStationQualificationFacilityAsync(owner,
                isolated: false, targetController: true, owner.Cancellation.Token).ConfigureAwait(false);
            await RecordStationQualificationProgressAsync(owner, StationQualificationSessionPhase.Isolating,
                "StationQualificationTargetObserved", baselineObservation).ConfigureAwait(false);
            var isolation = await StartStationQualificationFacilityOperationAsync(owner,
                () => owner.Facility.ApplyIsolationAsync(owner.Cancellation.Token).AsTask(), "StationQualificationIsolation").ConfigureAwait(false);
            if (!isolation.Succeeded) throw new InvalidOperationException(isolation.ReasonCode);
            var isolated = await ObserveStationQualificationFacilityAsync(owner,
                isolated: true, targetController: false, owner.Cancellation.Token).ConfigureAwait(false);
            await RecordStationQualificationProgressAsync(owner, StationQualificationSessionPhase.Isolating,
                "StationQualificationIsolationVerified", isolated).ConfigureAwait(false);
            await PrepareStationQualificationPipelineAsync(owner).ConfigureAwait(false);
            while (true)
            {
                lock (_sync) if (owner.ExitRequested) break;
                await RequireStationQualificationAuthorityAsync(owner).ConfigureAwait(false);
                var observation = await ObserveStationQualificationFacilityAsync(owner,
                    isolated: true, targetController: false, owner.Cancellation.Token).ConfigureAwait(false);
                await RecordStationQualificationProgressAsync(owner, StationQualificationSessionPhase.ReadyForStimulus,
                    "StationQualificationReadyForStimulus", observation).ConfigureAwait(false);
                // Waiting for the next controller stimulus has no cycle deadline.
                // Exit revokes this wait; retirement remains owned if it ignores cancellation.
                QualificationFacilityStimulus stimulus;
                using (var stimulusClaim = ClaimStationQualificationPhysicalPhase(owner))
                {
                    if (!stimulusClaim.Available) throw new OperationCanceledException("StationQualificationStimulusRevoked");
                    var stimulusTask = owner.Facility.WaitForStimulusAsync(owner.StimulusCancellation.Token).AsTask();
                    stimulus = await AwaitStationQualificationStimulusAsync(owner, stimulusTask).ConfigureAwait(false);
                }
                lock (_sync) if (owner.ExitRequested) break;
                var rejection = StationQualificationFacilityValidator.ValidateStimulus(owner.Request, stimulus, owner.StimulusSequence);
                if (rejection is not null) throw new InvalidOperationException(rejection);
                await RequireStationQualificationAuthorityAsync(owner).ConfigureAwait(false);
                var admissionObservation = await ObserveStationQualificationFacilityAsync(owner, isolated: true, targetController: false,
                    owner.Cancellation.Token).ConfigureAwait(false);
                await AdmitStationQualificationRunAsync(owner, stimulus, admissionObservation).ConfigureAwait(false);
                await ExecuteStationQualificationRunAsync(owner, stimulus).ConfigureAwait(false);
                if (++owner.RunCount >= _stationQualificationOptions!.MaximumRuns)
                {
                    lock (_sync) RequestStationQualificationExitLocked(owner, "StationQualificationRunLimitReached", abort: false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (_sync)
                if (!owner.ExitRequested) RequestStationQualificationExitLocked(owner, "StationQualificationCancelled", abort: true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var reason = exception is InvalidOperationException or TimeoutException &&
                exception.Message.Length is > 0 and <= 128 && exception.Message.All(character => char.IsLetterOrDigit(character) || character == '_')
                ? exception.Message : "StationQualificationExecutionFailed";
            lock (_sync) RequestStationQualificationExitLocked(owner, reason, abort: true);
        }
        finally
        {
            await RestoreStationQualificationAsync(owner).ConfigureAwait(false);
        }
    }

    private async Task<IStationQualificationFacilityLease> CaptureStationQualificationFacilityAsync(
        StationQualificationOwner owner, CancellationToken token)
    {
        var lease = await _stationQualificationFacility!.OpenAsync(owner.Request, token).ConfigureAwait(false);
        // Capture a late open result even when its caller's bounded wait expired,
        // so restoration still owns and closes the actual facility lease.
        owner.Facility = lease;
        return lease;
    }

    private async Task<QualificationFacilityStimulus> AwaitStationQualificationStimulusAsync(StationQualificationOwner owner,
        Task<QualificationFacilityStimulus> actual)
    {
        try { return await actual.WaitAsync(owner.StimulusCancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { await ObserveStationQualificationOperationAsync(owner, actual, "StationQualificationStimulusRetirement").ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            throw;
        }
    }

    private async Task PrepareStationQualificationPipelineAsync(StationQualificationOwner owner)
    {
        await RequireStationQualificationAuthorityAsync(owner).ConfigureAwait(false);
        var baseline = owner.Header.TargetBaseline.SuccessfulSnapshot!;
        var content = baseline.Release.Source.Content;
        var binding = content.Algorithm;
        var preparation = await _stationQualificationPreparation!.PrepareOwnedAsync(new(binding.Algorithm, content.Configuration,
            binding.ResultSchema.Id, binding.ResultSchema.Version, binding.ResultSchema.ContentHash,
            binding.OverlayContract.Id, binding.OverlayContract.Version, binding.OverlayContract.ContentHash,
            _stationQualificationOptions!.OperationTimeout), owner.Cancellation.Token,
            () =>
            {
                var phase = ClaimStationQualificationPhysicalPhase(owner);
                if (phase.Available) return phase;
                phase.Dispose();
                throw new OperationCanceledException("StationQualificationPreparationAborted");
            }, retirement => owner.PreparationRetirement = retirement).ConfigureAwait(false);
        if (!preparation.Succeeded || preparation.Prepared is null) throw new InvalidOperationException(preparation.ReasonCode);
        owner.Prepared = preparation.Prepared;
        owner.Execution = new AlgorithmExecutionService(_stationQualificationExecutionOptions!);
        await RequireStationQualificationAuthorityAsync(owner).ConfigureAwait(false);
        owner.Camera = await _cameraSetupRuntime.ReserveQualificationAcquisitionAsync(content.CameraRole, owner.Cancellation.Token).ConfigureAwait(false);
        if (!owner.Camera.Available) throw new InvalidOperationException(owner.Camera.ReasonCode);
        var applied = await owner.Camera.ApplyAsync(content.Camera, content.CameraProviderExtension, owner.Cancellation.Token,
            baseline.CameraSetup, () => ClaimStationQualificationPhysicalPhase(owner)).ConfigureAwait(false);
        if (!applied.Succeeded || applied.Snapshot?.Effective?.ProductionAcquisitionMode != content.Camera.ProductionAcquisitionMode)
            throw new InvalidOperationException(applied.Succeeded ? "StationQualificationCameraModeMismatch" : applied.ReasonCode);
    }

    private async Task ExecuteStationQualificationRunAsync(StationQualificationOwner owner, QualificationFacilityStimulus stimulus)
    {
        FrameBufferLease? frame = null;
        FrameMetadata? metadata = null;
        FrameProvenance? provenance = null;
        AlgorithmExecutionOutcome? outcome = null;
        StationQualificationPayload? payload = null;
        var status = ExecutionStatus.Error;
        var reason = "StationQualificationRunFailed";
        try
        {
            await RequireStationQualificationAuthorityAsync(owner).ConfigureAwait(false);
            var acquired = await owner.Camera!.AcquireQualificationFrameAsync(owner.CurrentRunId!.Correlation,
                _frameBufferPool!, _stationQualificationClock!, owner.Cancellation.Token,
                () => ClaimStationQualificationPhysicalPhase(owner)).ConfigureAwait(false);
            status = acquired.ExecutionStatus;
            reason = acquired.ReasonCode;
            if (!acquired.Succeeded || acquired.Frame is null) return;
            frame = acquired.Frame;
            metadata = frame.Frame.Metadata;
            provenance = acquired.Provenance;
            await RequireStationQualificationAuthorityAsync(owner).ConfigureAwait(false);
            await ObserveStationQualificationFacilityAsync(owner, isolated: true, targetController: false,
                owner.Cancellation.Token).ConfigureAwait(false);
            var baseline = owner.Header.TargetBaseline.SuccessfulSnapshot!;
            using (var phase = ClaimStationQualificationPhysicalPhase(owner))
            {
                if (!phase.Available) { status = ExecutionStatus.Cancelled; reason = phase.Failure ?? "StationQualificationStopping"; return; }
                var attempt = await owner.Execution!.ExecuteAsync(owner.Prepared!, frame,
                    new(baseline.Recipe, baseline.Release.Source.Content.AlgorithmExecutionTimeout), owner.Cancellation.Token).ConfigureAwait(false);
                frame = null;
                outcome = attempt.Outcome;
                status = outcome?.ExecutionStatus ?? ExecutionStatus.Error;
                reason = outcome?.ReasonCode ?? attempt.ReasonCode;
            }
            if (status != ExecutionStatus.Success || outcome is null) return;
            await RequireStationQualificationAuthorityAsync(owner).ConfigureAwait(false);
            await ObserveStationQualificationFacilityAsync(owner, isolated: true, targetController: false,
                owner.Cancellation.Token).ConfigureAwait(false);
            var encoded = new PlcResultPayloadEncoder().EncodeQualification(owner.Header.SessionId, owner.CurrentRunId!,
                owner.Header.Plan.QualificationContextHash, baseline.PlcResultContract,
                stimulus.ControllerEpoch, stimulus.CycleSequence, outcome);
            if (encoded.Payload is null) { status = ExecutionStatus.Error; reason = encoded.ReasonCode; return; }
            payload = encoded.Payload;
            var written = await StartStationQualificationFacilityOperationAsync(owner,
                () => owner.Facility!.WriteQualificationResultAsync(payload, owner.Cancellation.Token).AsTask(),
                "StationQualificationResultWrite").ConfigureAwait(false);
            if (!written.Succeeded) { status = ExecutionStatus.Error; reason = written.ReasonCode; }
        }
        catch (OperationCanceledException) { status = ExecutionStatus.Cancelled; reason = "StationQualificationRunCancelled"; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { status = ExecutionStatus.Error; reason = "StationQualificationRunFailed"; }
        finally
        {
            frame?.Dispose();
            await CompleteStationQualificationRunAsync(owner, outcome, status, reason, metadata, provenance, payload).ConfigureAwait(false);
            if (status != ExecutionStatus.Success)
                lock (_sync) RequestStationQualificationExitLocked(owner, reason, abort: true);
        }
    }
}
