using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cycles;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Production;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async Task ExecuteProductionInspectionAsync(ProductionInspectionOwner owner,
        Func<InspectionCycleCommitReceipt<PlcResultPayloadSnapshot>, CancellationToken, Task> publish)
    {
        var admission = owner.Current ?? throw new InvalidOperationException("ProductionInspectionAdmissionMissing");
        var baseline = admission.ActivationSnapshot;
        var correlation = new ExecutionCorrelationId(ExecutionKind.Production, admission.CorrelationId);
        CameraAcquisitionFailure? actualAcquisitionFailure = null;
        var pipeline = new InspectionCyclePipeline<PlcResultPayloadSnapshot>
        {
            Correlation = correlation,
            Prepared = owner.Prepared!,
            Execution = owner.Execution,
            ExecutionRequest = new(baseline.Recipe, baseline.Release.Source.Content.AlgorithmExecutionTimeout),
            GuardAsync = () => RequireProductionContinuationAsync(owner),
            DeliveryGuardAsync = () => RequireProductionContinuationAsync(owner),
            DeliveryCancellationToken = owner.Cancellation.Token,
            RuntimeAbortRequested = () => { lock (_sync) return owner.FaultAbortRequested; },
            ClaimExecution = () => ClaimProductionPhysicalPhase(owner),
            RetainInputBeforeExecution = frame =>
            {
                if (admission.EvidenceCapturePolicy is { Mode: not EvidenceCaptureMode.None })
                {
                    if (owner.ImageInput is not null)
                        throw new InvalidOperationException("ProductionImageInputAlreadyRetained");
                    owner.ImageInput = RetainedProductionFrame.Capture(frame);
                }
            },
            AcquireAsync = async token =>
            {
                // Admission has already committed. The exact active camera is borrowed
                // under its operation gate without reconfiguration or a device restart.
                owner.Camera = await _cameraSetupRuntime.ReserveProductionAcquisitionAsync(
                    baseline.CameraSetup, token).ConfigureAwait(false);
                if (!owner.Camera.Available) throw new InvalidOperationException(owner.Camera.ReasonCode);
                var acquired = await owner.Camera.AcquireProductionFrameAsync(correlation, _frameBufferPool!,
                    _productionInspectionClock!, token, () => ClaimProductionPhysicalPhase(owner)).ConfigureAwait(false);
                actualAcquisitionFailure = acquired.FailureKind is { } kind
                    ? new CameraAcquisitionFailure(kind, acquired.ReasonCode) : null;
                return acquired;
            },
            Encode = outcome =>
            {
                var encoded = new PlcResultPayloadEncoder().Encode(baseline.PlcResultContract,
                    admission.ControllerCycle, outcome);
                return (encoded.Snapshot, encoded.Snapshot?.ReasonCode ?? outcome.ReasonCode ?? encoded.ReasonCode);
            },
            EncodeFailure = (status, reason) =>
            {
                // Only an actual acquisition failure or the Runtime's explicit Fault Abort
                // can produce this failure payload. Other gate refusals terminate the cycle.
                bool runtimeAbort;
                lock (_sync) runtimeAbort = owner.FaultAbortRequested;
                if (actualAcquisitionFailure is null && !(runtimeAbort && status == ExecutionStatus.Cancelled))
                    return (null, reason);
                var effectiveReason = status switch
                {
                    ExecutionStatus.Timeout => "CameraAcquisitionTimeout",
                    ExecutionStatus.Cancelled when actualAcquisitionFailure is null &&
                        reason == "AlgorithmExecutionCancelled" => "AlgorithmExecutionCancelled",
                    ExecutionStatus.Cancelled => "CameraAcquisitionCancelled",
                    _ => "CameraAcquisitionError"
                };
                var encoded = new PlcResultPayloadEncoder().EncodeFailure(baseline.PlcResultContract,
                    admission.InspectionId, admission.ControllerCycle, status, effectiveReason);
                return (encoded.Snapshot, encoded.Snapshot?.ReasonCode ?? encoded.ReasonCode);
            },
            CancellationReasonCode = "ProductionInspectionCancelled",
            FailureReasonCode = "ProductionInspectionFailed",
            CommitAsync = result => CommitProductionCoreAsync(owner, result),
            PublishAsync = async (receipt, token) =>
            {
                // Delivery of the fixed outcome has its own authority. Outstanding provider
                // work retains its frame and owner through the later physical-retirement gate.
                await RequireProductionContinuationAsync(owner).ConfigureAwait(false);
                await publish(receipt, owner.Cancellation.Token).ConfigureAwait(false);
            }
        };
        try
        {
            var completed = await owner.Coordinator.ExecuteAsync(pipeline, owner.ExecutionCancellation.Token).ConfigureAwait(false);
            if (owner.Coordinator.Phase == InspectionCyclePhase.FaultTerminated)
                owner.FailureReason = completed.ReasonCode;
        }
        finally
        {
            // A stage task takes this ownership before starting file I/O. Any input
            // still here has never been handed to that physical task.
            owner.ImageInput?.Dispose();
            owner.ImageInput = null;
        }
    }

    private async Task RetireProductionCameraAsync(ProductionInspectionOwner owner)
    {
        owner.RetirementDeadline ??= new StoreDeadline(_productionInspectionOptions!.RetirementTimeout);
        TimeSpan Remaining()
        {
            var remaining = owner.RetirementDeadline.Remaining;
            lock (_sync)
                if (_productionShutdownDeadline is { } shutdown && shutdown.Remaining < remaining)
                    remaining = shutdown.Remaining;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        if (owner.Camera is { } camera)
        {
            var retired = await camera.WaitForManualRetirementAsync(Remaining(),
                CancellationToken.None).ConfigureAwait(false);
            if (!retired.Completed || !retired.SafeToReplace)
            {
                lock (_sync) _productionInspectionRecoveryBlocked = true;
                // Keep the camera reservation attached to this owner on a late provider call.
                throw new InvalidOperationException("ProductionInspectionCameraRetirementIncomplete");
            }
            await camera.DisposeAsync().ConfigureAwait(false);
            owner.Camera = null;
        }
        while (owner.Execution.ActiveExecutionCount != 0 || _productionImageStager?.ActiveOperationCount > 0)
        {
            if (Remaining() <= TimeSpan.Zero)
            {
                lock (_sync) _productionInspectionRecoveryBlocked = true;
                throw new InvalidOperationException(_productionImageStager?.ActiveOperationCount > 0 ?
                    "ProductionImageStageRetirementIncomplete" : "ProductionInspectionExecutionRetirementIncomplete");
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static ProductionInspectionCore CreateProductionInspectionCore(ProductionInspectionOwner owner,
        InspectionCycleExecutionResult<PlcResultPayloadSnapshot> result, ProductionImageEvidenceSnapshot? imageEvidence = null)
    {
        var admission = owner.Current ?? throw new InvalidOperationException("ProductionInspectionAdmissionMissing");
        var prepared = owner.Prepared ?? throw new InvalidOperationException("ProductionInspectionPreparedAlgorithmMissing");
        ProductionAlgorithmResultDocument? document = null;
        FrameOverlaySnapshot? overlay = null;
        if (result.Status == ExecutionStatus.Success)
        {
            document = ProductionAlgorithmResultCodec.Encode(admission.InspectionId,
                result.Outcome ?? throw new InvalidOperationException("ProductionInspectionSuccessfulOutcomeMissing"));
            overlay = new(admission.InspectionId, document.FrameMetadata, document.ResultSchema,
                document.Result.OverlaySet, document.PayloadHash);
        }
        return new(imageEvidence, admission, ProductionInspectionState.CoreCommitted, result.Status,
            result.Payload!.Decision, result.Payload.ReasonCode ?? result.ReasonCode,
            result.AcquisitionFailure?.Kind, result.AcquisitionFailure?.ReasonCode,
            result.Metadata, result.Provenance, prepared.InstanceId, prepared.Descriptor.Identity,
            prepared.Configuration, prepared.Descriptor.ResultSchema, document?.Result, overlay,
            result.Outcome?.Timing, result.Payload, document?.PayloadJson, document?.PayloadHash, admission.PartIdentityEvidence?.Value,
            DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), Array.Empty<TraceRetentionObligation>(), result.AcquisitionStart,
            result.Outcome?.AdmittedMonotonicTimestamp, result.Outcome?.MonotonicFrequency);
    }
}
