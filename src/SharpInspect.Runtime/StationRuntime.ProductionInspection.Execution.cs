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
                // 准入已经提交；这里仅在相机操作闸门内借用精确的活动相机，不重新配置也不重启设备。
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
                // 只有真实采集失败或 Runtime 明确发起的 Fault Abort 才能生成失败载荷，其他闸门拒绝直接终止周期。
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
                // 固定结果的交付有独立权威；未完成的供应商工作继续由后续物理退休闸门持有其帧和所有者。
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
            // 阶段任务开始文件 I/O 前会接管此所有权；此处仍存在的输入尚未交给物理阶段任务。
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
                // 供应商调用晚到时仍把相机预约挂在当前所有者上，不能提前释放它。
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
