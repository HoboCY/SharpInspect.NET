using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Frames;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async Task ExecuteManualInspectionStartAsync(ManualInspectionOwner owner)
    {
        try
        {
            if (!await RecordManualInspectionProgressAsync(owner, ManualInspectionSessionPhase.Preparing,
                    "ManualInspectionPreparing").ConfigureAwait(false)) return;
            if (!await CheckManualInspectionAuthorityAsync(owner).ConfigureAwait(false)) return;
            var content = owner.Plan.Content;
            var binding = content.Algorithm;
            var preparation = await _manualPreparation!.PrepareOwnedAsync(new(binding.Algorithm, content.Configuration,
                binding.ResultSchema.Id, binding.ResultSchema.Version, binding.ResultSchema.ContentHash,
                binding.OverlayContract.Id, binding.OverlayContract.Version, binding.OverlayContract.ContentHash,
                _manualOptions!.PreparationTimeout), owner.AbortCancellation.Token,
                () =>
                {
                    var phase = ClaimManualInspectionPhysicalPhase(owner);
                    if (phase.Available) return phase;
                    phase.Dispose();
                    throw new OperationCanceledException("ManualInspectionPreparationAborted");
                }, retirement => owner.PreparationRetirement = retirement).ConfigureAwait(false);
            if (!preparation.Succeeded || preparation.Prepared is null)
            {
                RequestManualInspectionExit(owner, preparation.ReasonCode, abort: true);
                return;
            }
            owner.Prepared = preparation.Prepared;
            owner.Execution = new AlgorithmExecutionService(_manualExecutionOptions!);
            owner.Clock = _manualAcquisitionClock;
            if (!await CheckManualInspectionAuthorityAsync(owner).ConfigureAwait(false)) return;
            var camera = await _cameraSetupRuntime.ReserveManualAcquisitionAsync(content.CameraRole,
                owner.AbortCancellation.Token).ConfigureAwait(false);
            owner.Camera = camera;
            if (!camera.Available)
            {
                RequestManualInspectionExit(owner, camera.ReasonCode, abort: true);
                return;
            }
            var baseline = owner.Baseline?.SuccessfulSnapshot?.CameraSetup;
            if (owner.Baseline is not null && baseline is null)
            {
                RequestManualInspectionExit(owner, "ManualInspectionBaselineUnavailable", abort: true);
                return;
            }
            var requested = ManualSoftwareTriggerConfiguration(content.Camera);
            var applied = await camera.ApplyAsync(requested, content.CameraProviderExtension,
                owner.AbortCancellation.Token, baseline, () => ClaimManualInspectionPhysicalPhase(owner))
                .ConfigureAwait(false);
            if (!applied.Succeeded || applied.Snapshot?.Effective?.ProductionAcquisitionMode != ProductionAcquisitionMode.SoftwareTrigger)
            {
                RequestManualInspectionExit(owner, applied.Succeeded ? "ManualSoftwareTriggerUnavailable" : applied.ReasonCode,
                    abort: true);
                return;
            }
            await RecordManualInspectionProgressAsync(owner, ManualInspectionSessionPhase.ReadyForRun,
                "ManualInspectionReadyForRun").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (owner.AbortCancellation.IsCancellationRequested)
        { RequestManualInspectionExit(owner, "ManualInspectionPreparationCancelled", abort: true); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { RequestManualInspectionExit(owner, "ManualInspectionPreparationFailed", abort: true); }
    }

    private async Task ExecuteManualInspectionRunAsync(ManualInspectionOwner owner)
    {
        AlgorithmExecutionOutcome? outcome = null;
        FrameMetadata? metadata = null;
        FrameProvenance? provenance = null;
        FrameBufferLease? frame = null;
        var status = ExecutionStatus.Error;
        var reason = "ManualInspectionExecutionUnavailable";
        var started = DateTimeOffset.UtcNow;
        var diagnosticBefore = owner.Execution!.DroppedDiagnosticCount;
        try
        {
            if (!await CheckManualInspectionAuthorityAsync(owner).ConfigureAwait(false))
            {
                status = ExecutionStatus.Cancelled;
                reason = "ManualInspectionAuthorityEndedBeforeRun";
                return;
            }
            var acquired = await owner.Camera!.AcquireManualFrameAsync(new(ExecutionKind.Manual, owner.CurrentRunId!.Value),
                _frameBufferPool!, owner.Clock!, owner.AbortCancellation.Token,
                () => ClaimManualInspectionPhysicalPhase(owner)).ConfigureAwait(false);
            reason = acquired.ReasonCode;
            if (!acquired.Succeeded || acquired.Frame is null)
            {
                status = acquired.ExecutionStatus;
                if (status == ExecutionStatus.Error && owner.Aborted) status = ExecutionStatus.Cancelled;
                return;
            }
            frame = acquired.Frame;
            metadata = frame.Frame.Metadata;
            provenance = acquired.Provenance;
            if (!await RecordManualInspectionProgressAsync(owner, ManualInspectionSessionPhase.Executing,
                    "ManualInspectionExecuting").ConfigureAwait(false))
            {
                status = owner.Aborted ? ExecutionStatus.Cancelled : ExecutionStatus.Error;
                reason = owner.Aborted ? owner.ExitReason : "ManualInspectionProgressPersistenceFailed";
                return;
            }
            if (!await CheckManualInspectionAuthorityAsync(owner).ConfigureAwait(false))
            { status = ExecutionStatus.Cancelled; reason = "ManualInspectionAuthorityEndedBeforeAlgorithm"; return; }
            var content = owner.Plan.Content;
            // Draft remains an authoring identity; this temporary timing reference
            // neither releases nor activates it and is always attached to a Manual correlation.
            var recipe = owner.Plan.Selection.Recipe ?? new RecipeReference(content.RecipeKey,
                "draft", content.ContentHash);
            using var executionPhase = ClaimManualInspectionPhysicalPhase(owner);
            if (!executionPhase.Available)
            { status = ExecutionStatus.Cancelled; reason = "ManualInspectionAbortedBeforeAlgorithm"; return; }
            var attempt = await owner.Execution.ExecuteAsync(owner.Prepared!, frame,
                new(recipe, content.AlgorithmExecutionTimeout), owner.AbortCancellation.Token).ConfigureAwait(false);
            frame = null; // ExecuteAsync consumes the owner on accepted and refused calls.
            outcome = attempt.Outcome;
            status = outcome?.ExecutionStatus ?? ExecutionStatus.Error;
            reason = outcome?.ReasonCode ?? attempt.ReasonCode;
        }
        catch (OperationCanceledException) when (owner.AbortCancellation.IsCancellationRequested)
        { status = ExecutionStatus.Cancelled; reason = "ManualInspectionCancelled"; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { status = ExecutionStatus.Error; reason = "ManualInspectionExecutionFailed"; }
        finally
        {
            frame?.Dispose();
            var persisted = await RecordManualInspectionRunTerminalAsync(owner, outcome, status, reason,
                metadata, provenance, started, Math.Max(0, owner.Execution.DroppedDiagnosticCount - diagnosticBefore))
                .ConfigureAwait(false);
            if (!persisted || status != ExecutionStatus.Success)
                RequestManualInspectionExit(owner, !persisted ? "ManualInspectionRunPersistenceFailed" : reason, abort: true);
        }
    }

    private static RequestedCameraConfiguration ManualSoftwareTriggerConfiguration(RequestedCameraConfiguration source) =>
        new(ProductionAcquisitionMode.SoftwareTrigger, source.ExposureTimeUs, source.GainDb, source.RegionOfInterest,
            source.PixelFormat, source.ValidBits, source.AcquisitionTimeoutMs, source.TriggerDelayUs, source.WhiteBalanceRgb);
}
