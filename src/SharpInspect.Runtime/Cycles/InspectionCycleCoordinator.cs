using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Recipes;

namespace SharpInspect.Runtime.Cycles;

internal enum InspectionCyclePhase
{
    AwaitRequest, Accepted, Acquiring, Executing, Encoding, Committing,
    WritingPayload, AwaitAckHigh, AwaitAckLow, Completed, FaultTerminated
}

/// <summary>
/// The single acquire/execute/encode/commit/publication sequence. Entry adapters
/// select the Runtime-owned identity, durable record and output channel; they
/// cannot publish a computed result before this coordinator receives a commit
/// receipt. A future production entry must use this same sequence.
/// </summary>
internal sealed partial class InspectionCycleCoordinator<TPayload> where TPayload : class
{
    private int _phase = (int)InspectionCyclePhase.AwaitRequest;
    internal InspectionCyclePhase Phase => (InspectionCyclePhase)Volatile.Read(ref _phase);
    internal void SetPhase(InspectionCyclePhase phase) => Volatile.Write(ref _phase, (int)phase);

    internal async Task<InspectionCycleExecutionResult<TPayload>> ExecuteAsync(
        InspectionCyclePipeline<TPayload> pipeline, CancellationToken cancellationToken)
    {
        if (Phase != InspectionCyclePhase.Accepted)
            throw new InvalidOperationException("InspectionCycleNotAccepted");
        FrameBufferLease? frame = null;
        FrameMetadata? metadata = null;
        FrameProvenance? provenance = null;
        AlgorithmExecutionOutcome? outcome = null;
        TPayload? payload = null;
        CameraAcquisitionFailure? acquisitionFailure = null;
        FrameAcquisitionStart? acquisitionStart = null;
        var status = ExecutionStatus.Error;
        var reason = "InspectionCycleExecutionFailed";
        try
        {
            SetPhase(InspectionCyclePhase.Acquiring);
            await pipeline.GuardAsync().ConfigureAwait(false);
            var acquired = await pipeline.AcquireAsync(cancellationToken).ConfigureAwait(false);
            status = acquired.ExecutionStatus;
            acquisitionStart = acquired.AcquisitionStart;
            reason = acquired.ReasonCode;
            acquisitionFailure = acquired.FailureKind is { } failureKind
                ? new CameraAcquisitionFailure(failureKind, acquired.ReasonCode)
                : null;
            frame = acquired.Frame;
            if (acquired.Succeeded && frame is not null)
            {
                metadata = frame.Frame.Metadata;
                provenance = acquired.Provenance;
                if (metadata.Correlation != pipeline.Correlation)
                    throw new InvalidOperationException("InspectionCycleFrameIdentityMismatch");
                await pipeline.GuardAsync().ConfigureAwait(false);
                SetPhase(InspectionCyclePhase.Executing);
                using (var claim = pipeline.ClaimExecution())
                {
                    if (!claim.Available)
                        throw new OperationCanceledException(claim.Failure ?? "InspectionCycleExecutionRevoked");
                    // ExecuteAsync consumes the frame token even on refusal.
                    var owned = frame;
                    frame = null;
                    var attempt = pipeline.Correlation.Kind == ExecutionKind.Production
                        ? await pipeline.Execution.ExecuteProductionOwnedAsync(pipeline.Prepared, owned,
                            pipeline.ExecutionRequest, pipeline.Correlation, cancellationToken).ConfigureAwait(false)
                        : await pipeline.Execution.ExecuteAsync(pipeline.Prepared, owned,
                            pipeline.ExecutionRequest, cancellationToken).ConfigureAwait(false);
                    outcome = attempt.Outcome;
                    status = outcome?.ExecutionStatus ?? ExecutionStatus.Error;
                    reason = outcome?.ReasonCode ?? attempt.ReasonCode;
                }
                if (outcome is not null)
                {
                    await pipeline.GuardAsync().ConfigureAwait(false);
                    SetPhase(InspectionCyclePhase.Encoding);
                    var encoded = pipeline.Encode(outcome);
                    payload = encoded.Payload;
                    if (payload is null)
                    { status = ExecutionStatus.Error; reason = encoded.ReasonCode; }
                }
            }
        }
        catch (OperationCanceledException)
        { status = ExecutionStatus.Cancelled; reason = pipeline.CancellationReasonCode; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { status = ExecutionStatus.Error; reason = pipeline.FailureReasonCode; }
        finally { frame?.Dispose(); }

        // A production acquisition failure may have no frame and therefore no algorithm
        // outcome. An optional entry adapter can encode its typed PLC failure using the same
        // contract, while qualification/manual pipelines retain their historical behavior.
        if (outcome is null && frame is null && metadata is null && payload is null &&
            status is (ExecutionStatus.Error or ExecutionStatus.Timeout or ExecutionStatus.Cancelled) &&
            pipeline.EncodeFailure is { } encodeFailure)
        {
            try
            {
                var encodedFailure = encodeFailure(status, reason);
                if (string.IsNullOrWhiteSpace(encodedFailure.ReasonCode))
                {
                    status = ExecutionStatus.Error;
                    reason = "PlcResultFailureEncodingFailed";
                }
                else
                {
                    payload = encodedFailure.Payload;
                    reason = encodedFailure.ReasonCode;
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                status = ExecutionStatus.Error;
                reason = "PlcResultFailureEncodingFailed";
                payload = null;
            }
        }

        var result = new InspectionCycleExecutionResult<TPayload>(outcome, status, reason,
            metadata, provenance, payload, acquisitionFailure) { AcquisitionStart = acquisitionStart };
        SetPhase(InspectionCyclePhase.Committing);
        // Never retry this transaction. The storage adapter owns its original
        // monotonic deadline, final authority check and any late completion.
        var receipt = await pipeline.CommitAsync(result).ConfigureAwait(false);
        if (receipt is null)
        {
            SetPhase(InspectionCyclePhase.FaultTerminated);
            return result;
        }
        if (receipt.Correlation != pipeline.Correlation || payload is null)
            throw new InvalidOperationException("InspectionCycleCommitReceiptMismatch");
        await pipeline.GuardAsync().ConfigureAwait(false);
        SetPhase(InspectionCyclePhase.WritingPayload);
        await pipeline.PublishAsync(receipt, cancellationToken).ConfigureAwait(false);
        SetPhase(InspectionCyclePhase.Completed);
        return result;
    }
}

internal sealed record InspectionCycleExecutionResult<TPayload>(AlgorithmExecutionOutcome? Outcome,
    ExecutionStatus Status, string ReasonCode, FrameMetadata? Metadata, FrameProvenance? Provenance,
    TPayload? Payload, CameraAcquisitionFailure? AcquisitionFailure = null) where TPayload : class
{
    internal FrameAcquisitionStart? AcquisitionStart { get; init; }
}

/// <summary>Constructed only from the authoritative writer's actual committed record.</summary>
internal sealed record InspectionCycleCommitReceipt<TPayload>(ExecutionCorrelationId Correlation,
    string RecordHash, TPayload Payload) where TPayload : class;

internal sealed class InspectionCyclePipeline<TPayload> where TPayload : class
{
    internal ExecutionCorrelationId Correlation { get; init; } = null!;
    internal PreparedAlgorithm Prepared { get; init; } = null!;
    internal AlgorithmExecutionService Execution { get; init; } = null!;
    internal AlgorithmExecutionRequest ExecutionRequest { get; init; } = null!;
    internal Func<Task> GuardAsync { get; init; } = null!;
    internal Func<CancellationToken, ValueTask<ManualCameraAcquisitionResult>> AcquireAsync { get; init; } = null!;
    internal Func<RecipeActivationPhysicalPhaseClaim> ClaimExecution { get; init; } = null!;
    internal Func<AlgorithmExecutionOutcome, (TPayload? Payload, string ReasonCode)> Encode { get; init; } = null!;
    /// <summary>
    /// Optional production adapter for a no-frame acquisition failure. The returned reason is the
    /// effective production reason persisted with the cycle, not an encoder process status.
    /// Qualification and manual pipelines leave this unset.
    /// </summary>
    internal Func<ExecutionStatus, string, (TPayload? Payload, string ReasonCode)>? EncodeFailure { get; init; }
    internal string CancellationReasonCode { get; init; } = "StationQualificationRunCancelled";
    internal string FailureReasonCode { get; init; } = "StationQualificationRunFailed";
    internal Func<InspectionCycleExecutionResult<TPayload>, Task<InspectionCycleCommitReceipt<TPayload>?>> CommitAsync { get; init; } = null!;
    internal Func<InspectionCycleCommitReceipt<TPayload>, CancellationToken, Task> PublishAsync { get; init; } = null!;
}
