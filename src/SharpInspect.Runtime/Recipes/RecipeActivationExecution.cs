using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Recipes;

/// <summary>Owns candidate resources from durable admission through commit or restoration.</summary>
internal sealed class RecipeActivationExecution : IAsyncDisposable
{
    private PreparedAlgorithm? _prepared;
    private RecipeActivationCameraLease? _camera;
    private bool _transferred;
    private bool _retired;
    internal RecipeActivationSnapshot? Snapshot { get; private set; }
    internal string? Failure { get; private set; }
    internal string? CleanupFailure { get; private set; }
    internal bool HardwareTouched => _camera?.HardwareTouched == true;

    internal async ValueTask StageAsync(RecipeActivationRecord admission, RecipeActivationPreparedInputs inputs,
        RecipeActivationRuntimeLease runtime, RecipeActivationChecks checks, AlgorithmPreparationService? preparation,
        TimeSpan preparationTimeout, AlgorithmExecutionPolicy executionPolicy, FrameBufferPool? frames,
        SqliteCommandStore store, RecipeActivationSnapshot? previous = null)
    {
        if (admission.Outcome.State != RecipeActivationOutcomeState.Admitted || admission.Admission is null ||
            admission.Candidate != inputs.Release.Recipe || admission.ReleaseId != inputs.Release.ReleaseId ||
            admission.ReleaseRecordContentHash != inputs.Release.ContentHash)
            throw new ArgumentException("RecipeActivationDurableAdmissionRequired", nameof(admission));
        if (admission.PreviousSnapshotContentHash != previous?.ContentHash ||
            admission.PreviousRecipe != previous?.Recipe)
            throw new ArgumentException("RecipeActivationDurableBaselineRequired", nameof(previous));
        var content = inputs.Release.Source.Content;
        var token = runtime.Token;
        try
        {
            if (runtime.GetBlocker() is { } beforePreparation)
            {
                checks.Observe(6, false, beforePreparation);
                Failure = beforePreparation;
                return;
            }
            if (preparation is null)
            {
                checks.Observe(6, false, "RecipeActivationAlgorithmPreparationUnavailable");
                Failure = checks.Failure; return;
            }
            var algorithm = content.Algorithm;
            var prepared = await preparation.PrepareAsync(new(algorithm.Algorithm, content.Configuration,
                algorithm.ResultSchema.Id, algorithm.ResultSchema.Version, algorithm.ResultSchema.ContentHash,
                algorithm.OverlayContract.Id, algorithm.OverlayContract.Version, algorithm.OverlayContract.ContentHash,
                preparationTimeout), token).ConfigureAwait(false);
            _prepared = prepared.Prepared;
            checks.Observe(6, prepared.Succeeded && _prepared is not null, prepared.ReasonCode,
                content.Configuration.ContentHash, _prepared?.Configuration.ContentHash);
            if (!prepared.Succeeded || _prepared is null) { Failure = checks.Failure; return; }
            if (runtime.GetBlocker() is { } afterPreparation)
            {
                checks.Observe(6, false, afterPreparation,
                    content.Configuration.ContentHash, _prepared.Configuration.ContentHash);
                Failure = afterPreparation;
                return;
            }
            token.ThrowIfCancellationRequested();
            if (runtime.GetBlocker() is { } beforeCameraReservation)
            {
                checks.Observe(8, false, beforeCameraReservation);
                Failure = beforeCameraReservation;
                return;
            }
            _camera = await runtime.Camera!.ReserveRecipeActivationAsync(content.CameraRole, token).ConfigureAwait(false);
            checks.Observe(8, _camera.Available, _camera.ReasonCode,
                _camera.CurrentSnapshot?.Binding?.RevisionHash);
            if (!_camera.Available) { Failure = checks.Failure; return; }
            var camera = await _camera.ApplyAsync(content.Camera, content.CameraProviderExtension, token,
                previous?.CameraSetup, runtime.TryBeginPhysicalPhase).ConfigureAwait(false);
            checks.Observe(9, camera.Succeeded && camera.Snapshot is not null, camera.ReasonCode,
                content.ContentHash, camera.Snapshot is null ? null : RecipeActivationValidation.CameraHash(camera.Snapshot));
            if (!camera.Succeeded || camera.Snapshot is null) { Failure = checks.Failure; return; }
            token.ThrowIfCancellationRequested();
            var calibration = await new RecipeActivationCalibrationEvaluator(store).EvaluateAsync(content,
                admission.Admission.CalibrationSelections, camera.Snapshot, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
            checks.Calibration(calibration, content.CalibrationRequirements.Count != 0);
            var pool = frames?.GetSnapshot();
            var effective = camera.Snapshot.Effective!;
            // The production layout authority is a separate mandatory A14 check. This is the
            // additional real pool bound, evaluated using the effective portable pixel geometry.
            var bytesPerPixel = effective.PixelFormat switch
            { VisionPixelFormat.Mono8 => 1, VisionPixelFormat.Mono16 => 2, VisionPixelFormat.Bgr24 => 3, _ => 0 };
            var required = checked((long)effective.RegionOfInterest.Width * effective.RegionOfInterest.Height * bytesPerPixel);
            var capacity = pool is { IsDisposed: false, ProductionFaultLatched: false, OutstandingLeases: 0, ActiveReaders: 0 } &&
                bytesPerPixel != 0 && required > 0 && required <= pool.MaximumFrameBytes;
            checks.Observe(11, capacity, capacity ? "RecipeActivationFramePoolBound" : "RecipeActivationFramePoolUnavailable");
            if (checks.Failure is { } failure) { Failure = failure; return; }
            Snapshot = new(admission.EvidenceKind, inputs.Release, _prepared.InstanceId, executionPolicy,
                camera.Snapshot, inputs.PlcBinding, calibration.Bindings, pool!.Capacity, pool.MaximumFrameBytes);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { Failure = "RecipeActivationCancelled"; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { Failure = "RecipeActivationPreparationFailed"; }
    }

    internal async ValueTask<RecipeActivationRestoration> RestoreAsync(RecipeActivationSnapshot? previous)
    {
        if (!HardwareTouched)
            return new(RecipeActivationRestorationState.NotRequired, "RecipeActivationHardwareUntouched");
        // Cleanup and restoration outlive caller cancellation. The camera lease bounds its
        // individual calls and retains late ownership instead of racing a second device open.
        var restored = await _camera!.RestoreAsync(previous?.CameraSetup, CancellationToken.None).ConfigureAwait(false);
        var state = !restored.Succeeded ? RecipeActivationRestorationState.Failed : previous is null ?
            RecipeActivationRestorationState.NoPreviousBaselineClosed : RecipeActivationRestorationState.Restored;
        return new(state, restored.ReasonCode,
            previous is null ? null : RecipeActivationValidation.CameraHash(previous.CameraSetup),
            restored.Snapshot is null ? null : RecipeActivationValidation.CameraHash(restored.Snapshot), restored.Snapshot);
    }

    internal (bool Installed, PreparedAlgorithm? Previous) InstallCommitted(RecipeActivationRuntimeLease runtime)
    {
        if (Snapshot is null || _prepared is null || _camera is null || _transferred)
            throw new InvalidOperationException("RecipeActivationPreparedResourcesUnavailable");
        var result = runtime.Install(Snapshot, _prepared);
        _transferred = result.Installed;
        return (result.Installed && _camera.Commit(), result.Previous);
    }

    internal async ValueTask RetireCandidateAsync()
    {
        if (_retired || _transferred || _prepared is null) return;
        _retired = true;
        CleanupFailure = await RetireBoundedAsync(_prepared).ConfigureAwait(false);
    }

    internal static async ValueTask<string?> RetireBoundedAsync(PreparedAlgorithm prepared)
    {
        var retiring = prepared.DisposeAsync().AsTask();
        try { await retiring.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); return null; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = retiring.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return "RecipeActivationAlgorithmRetirementPending";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_camera is not null) await _camera.DisposeAsync().ConfigureAwait(false);
        await RetireCandidateAsync().ConfigureAwait(false);
    }
}
