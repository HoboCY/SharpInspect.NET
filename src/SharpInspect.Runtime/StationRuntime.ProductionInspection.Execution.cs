using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cycles;
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
            ClaimExecution = () => ClaimProductionPhysicalPhase(owner),
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
                // A refusal before a real acquisition attempt has no typed camera result.
                // It terminates the cycle and must not be promoted to an ordinary Unknown.
                if (actualAcquisitionFailure is null) return (null, reason);
                var effectiveReason = status switch
                {
                    ExecutionStatus.Timeout => "CameraAcquisitionTimeout",
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
                // A logical acquisition/execution result can precede physical retirement.
                // Keep the durable cycle pending until resources have actually retired;
                // no PLC payload or ACK lifecycle can complete an unsafe old owner.
                await RetireProductionCameraAsync(owner).ConfigureAwait(false);
                await RequireProductionContinuationAsync(owner).ConfigureAwait(false);
                await publish(receipt, token).ConfigureAwait(false);
            }
        };
        var completed = await owner.Coordinator.ExecuteAsync(pipeline, owner.Cancellation.Token).ConfigureAwait(false);
        if (owner.Coordinator.Phase == InspectionCyclePhase.FaultTerminated)
            owner.FailureReason = completed.ReasonCode;
    }

    private async Task RetireProductionCameraAsync(ProductionInspectionOwner owner)
    {
        if (owner.Camera is { } camera)
        {
            var retired = await camera.WaitForManualRetirementAsync(_productionInspectionOptions!.RetirementTimeout,
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
        var started = Stopwatch.GetTimestamp();
        while (owner.Execution.ActiveExecutionCount != 0)
        {
            if ((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency >=
                _productionInspectionOptions!.RetirementTimeout.TotalSeconds)
            {
                lock (_sync) _productionInspectionRecoveryBlocked = true;
                throw new InvalidOperationException("ProductionInspectionExecutionRetirementIncomplete");
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static ProductionInspectionCore CreateProductionInspectionCore(ProductionInspectionOwner owner,
        InspectionCycleExecutionResult<PlcResultPayloadSnapshot> result)
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
        return new(admission, ProductionInspectionState.CoreCommitted, result.Status,
            result.Payload!.Decision, result.Payload.ReasonCode ?? result.ReasonCode,
            result.AcquisitionFailure?.Kind, result.AcquisitionFailure?.ReasonCode,
            result.Metadata, result.Provenance, prepared.InstanceId, prepared.Descriptor.Identity,
            prepared.Configuration, prepared.Descriptor.ResultSchema, document?.Result, overlay,
            result.Outcome?.Timing, result.Payload, document?.PayloadJson, document?.PayloadHash, null,
            DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), Array.Empty<TraceRetentionObligation>(), result.AcquisitionStart,
            result.Outcome?.AdmittedMonotonicTimestamp, result.Outcome?.MonotonicFrequency);
    }
}
