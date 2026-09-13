using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Cycles;
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
            if (_qualificationModbusProfile is not null)
            {
                await RecordStationQualificationProgressAsync(owner, StationQualificationSessionPhase.Isolating,
                    "QualificationModbusTransportSelected").ConfigureAwait(false);
                await ExecuteModbusQualificationCyclesAsync(owner).ConfigureAwait(false);
                return;
            }
            while (true)
            {
                lock (_sync) if (owner.ExitRequested) break;
                await RequireStationQualificationAuthorityAsync(owner).ConfigureAwait(false);
                var observation = await ObserveStationQualificationFacilityAsync(owner,
                    isolated: true, targetController: false, owner.Cancellation.Token).ConfigureAwait(false);
                await RecordStationQualificationProgressAsync(owner, StationQualificationSessionPhase.ReadyForStimulus,
                    "StationQualificationReadyForStimulus", observation).ConfigureAwait(false);
                // 等待下一次控制器刺激没有周期期限；Exit 会撤销等待，若供应商忽略取消，资源仍由所有者持有到退休。
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
        // 即使调用方有界等待已到，也要接住晚到的打开结果，让恢复流程持有并关闭真实设施租约。
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
        var coordinator = new InspectionCycleCoordinator<StationQualificationPayload>();
        coordinator.SetPhase(InspectionCyclePhase.Accepted);
        await ExecuteStationQualificationCycleAsync(owner, stimulus, coordinator, async (receipt, token) =>
        {
            var written = await StartStationQualificationFacilityOperationAsync(owner,
                () => owner.Facility!.WriteQualificationResultAsync(receipt.Payload, token).AsTask(),
                "StationQualificationResultWrite").ConfigureAwait(false);
            if (!written.Succeeded) throw new InvalidOperationException(written.ReasonCode);
        }).ConfigureAwait(false);
    }

    private async Task ExecuteStationQualificationCycleAsync(StationQualificationOwner owner,
        QualificationFacilityStimulus stimulus, InspectionCycleCoordinator<StationQualificationPayload> coordinator,
        Func<InspectionCycleCommitReceipt<StationQualificationPayload>, CancellationToken, Task> publish)
    {
        var runId = owner.CurrentRunId ?? throw new InvalidOperationException("StationQualificationRunAdmissionMissing");
        var baseline = owner.Header.TargetBaseline.SuccessfulSnapshot!;
        var pipeline = new InspectionCyclePipeline<StationQualificationPayload>
        {
            Correlation = runId.Correlation,
            Prepared = owner.Prepared!, Execution = owner.Execution!,
            ExecutionRequest = new(baseline.Recipe, baseline.Release.Source.Content.AlgorithmExecutionTimeout),
            GuardAsync = async () =>
            {
                await RequireStationQualificationAuthorityAsync(owner).ConfigureAwait(false);
                await ObserveStationQualificationFacilityAsync(owner, isolated: true, targetController: false,
                    owner.Cancellation.Token).ConfigureAwait(false);
            },
            AcquireAsync = token => owner.Camera!.AcquireQualificationFrameAsync(runId.Correlation,
                _frameBufferPool!, _stationQualificationClock!, token,
                () => ClaimStationQualificationPhysicalPhase(owner)),
            ClaimExecution = () => ClaimStationQualificationPhysicalPhase(owner),
            Encode = outcome =>
            {
                var encoded = new PlcResultPayloadEncoder().EncodeQualification(owner.Header.SessionId, runId,
                    owner.Header.Plan.QualificationContextHash, baseline.PlcResultContract,
                    stimulus.ControllerEpoch, stimulus.CycleSequence, outcome);
                return (encoded.Payload, encoded.ReasonCode);
            },
            CommitAsync = async result =>
            {
                var transaction = await CompleteStationQualificationRunAsync(owner, result.Outcome, result.Status,
                    result.ReasonCode, result.Metadata, result.Provenance, result.Payload).ConfigureAwait(false);
                var committed = transaction.Run;
                if (committed.QualificationPayload is null)
                    return null;
                if (committed.RunId.Value != runId.Value ||
                    committed.QualificationPayload.ContentHash != result.Payload?.ContentHash)
                    throw new InvalidOperationException("StationQualificationCommittedPayloadMismatch");
                if (owner.CycleStoragePolicy is not null && (transaction.Cycle is not
                    { Kind: QualificationCycleEventKind.CoreCommitted } receipt || receipt.RunId?.Value != runId.Value ||
                    receipt.RunContentHash != committed.ContentHash ||
                    receipt.PolicySnapshot?.ContentHash != owner.CycleStoragePolicy.ContentHash))
                    throw new InvalidOperationException("QualificationCycleCommitReceiptMismatch");
                return new(runId.Correlation, committed.ContentHash, committed.QualificationPayload);
            },
            PublishAsync = publish
        };
        lock (_sync) owner.CycleExecuting = true;
        try
        {
            var completed = await coordinator.ExecuteAsync(pipeline, owner.Cancellation.Token).ConfigureAwait(false);
            if (coordinator.Phase == InspectionCyclePhase.FaultTerminated)
                lock (_sync) RequestStationQualificationExitLocked(owner, completed.ReasonCode, abort: true);
        }
        finally
        {
            lock (_sync)
            {
                owner.CycleExecuting = false;
                if (!owner.ModbusRecoveryRequired && owner.CurrentRun is { Terminal: true } &&
                    owner.Execution is (null or { ActiveExecutionCount: 0 }))
                { owner.CurrentRun = null; owner.CurrentRunId = null; }
            }
        }
    }
}
