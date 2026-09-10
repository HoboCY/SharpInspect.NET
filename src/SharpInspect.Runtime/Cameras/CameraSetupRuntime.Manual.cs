using System.Buffers;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Recipes;

namespace SharpInspect.Runtime.Cameras;

/// <summary>
/// The result of one Runtime-owned manual frame acquisition.  A successful
/// result owns a pool-backed frame; the raw camera remains owned by the
/// activation lease and is therefore not exposed here.
/// </summary>
internal sealed class ManualCameraAcquisitionResult
{
    internal ManualCameraAcquisitionResult(bool succeeded, string reasonCode,
        ExecutionCorrelationId correlation, FrameBufferLease? frame,
        FrameProvenance? provenance, CameraRetirementObservation? retirement,
        ExecutionStatus executionStatus)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        if (!Enum.IsDefined(executionStatus))
            throw new ArgumentOutOfRangeException(nameof(executionStatus));
        if (succeeded != (frame is not null))
            throw new ArgumentException("ManualCameraAcquisitionResultStateInvalid",
                nameof(frame));
        if ((frame is null) != (provenance is null))
            throw new ArgumentException("ManualCameraAcquisitionEvidenceStateInvalid",
                nameof(provenance));
        Succeeded = succeeded;
        ReasonCode = reasonCode ?? throw new ArgumentNullException(nameof(reasonCode));
        Correlation = correlation;
        Frame = frame;
        Provenance = provenance;
        Retirement = retirement;
        ExecutionStatus = executionStatus;
    }

    internal bool Succeeded { get; }
    internal string ReasonCode { get; }
    internal ExecutionCorrelationId Correlation { get; }
    internal FrameBufferLease? Frame { get; }
    internal FrameProvenance? Provenance { get; }
    internal CameraRetirementObservation? Retirement { get; }
    /// <summary>
    /// Preserves the controlled acquisition's terminal execution status when a
    /// manual frame is not available.  In particular, a bounded camera timeout
    /// must remain a timeout for the Manual ledger rather than being flattened
    /// into a generic error by the orchestration layer.
    /// </summary>
    internal ExecutionStatus ExecutionStatus { get; }
}

/// <summary>
/// An observation of the manual acquisition's physical retirement.  The
/// logical acquisition may already have returned a timeout, while the
/// provider-owned task is still running.  Keeping completion separate from
/// safety prevents a bounded wait from being mistaken for permission to touch
/// the raw device.
/// </summary>
internal readonly record struct ManualRetirementWaitObservation(
    bool Completed, bool SafeToReplace, string ReasonCode)
{
    internal static ManualRetirementWaitObservation Complete(bool safeToReplace,
        string reasonCode) => new(true, safeToReplace, reasonCode);

    internal static ManualRetirementWaitObservation Pending(string reasonCode) =>
        new(false, false, reasonCode);
}

internal sealed partial class CameraSetupRuntime
{
    /// <summary>
    /// Manual acquisition uses the same activation reservation and all of its
    /// persisted binding checks.  The separate name makes the non-production
    /// intent explicit to the manual workflow.
    /// </summary>
    internal ValueTask<RecipeActivationCameraLease> ReserveManualAcquisitionAsync(
        string logicalRole, CancellationToken cancellationToken = default) =>
        ReserveRecipeActivationAsync(logicalRole, cancellationToken);

    internal CameraAcquisitionOptions CreateManualAcquisitionOptions()
    {
        // CameraAcquisitionOptions intentionally has a tighter public bound than
        // CameraSetupOptions.  The acquisition service's frame deadline remains
        // the effective camera configuration; these values only bound admission,
        // protocol observation and its bounded DisposeAsync surface.
        return new(BoundServiceTimeout(_options.OperationTimeout),
            BoundServiceTimeout(_options.ShutdownTimeout), 256);
    }

    private static TimeSpan BoundServiceTimeout(TimeSpan value) =>
        value <= TimeSpan.FromSeconds(30) ? value : TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reconciles the camera after an interrupted Manual session.  This deliberately
    /// reuses the activation recovery close/reopen/read-back helpers; a persisted
    /// Manual header is evidence of an interrupted inspection, never a production
    /// activation authority.
    /// </summary>
    internal async ValueTask<RecipeActivationCameraRestoreResult>
        RecoverManualAfterRestartAsync(ManualInspectionSessionHeader header,
            RecipeDraftRevision draft, RecipeActivationRecord? baseline,
            CancellationToken cancellationToken = default)
    {
        var candidateRole = header.CurrentBinding.LogicalRole;
        var candidate = draft.Content;
        var previous = baseline?.SuccessfulSnapshot;

        if (header.SourceContentHash != candidate.ContentHash ||
            candidate.CameraRole != candidateRole ||
            header.CurrentBinding.LogicalRole != candidate.CameraRole ||
            (header.Selection.Kind == ManualRecipeSourceKind.Draft &&
                ManualRecipeSelection.FromDraft(draft) != header.Selection) ||
            (header.Selection.Kind != ManualRecipeSourceKind.Draft &&
                header.Selection.Kind != ManualRecipeSourceKind.Released) ||
            header.ActiveActivation != baseline?.Reference ||
            header.ActiveSnapshotContentHash != previous?.ContentHash ||
            header.ActiveCameraContentHash != (previous is null ? null :
                RecipeActivationValidation.CameraHash(previous.CameraSetup)))
            return new(false, "ManualInspectionRecoveryEvidenceMismatch", null, false);

        return await RecoverNonProductionCameraAfterRestartAsync(header.CurrentBinding,
            candidate, previous, "ManualInspection", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RecipeActivationCameraRestoreResult> RecoverNonProductionCameraAfterRestartAsync(
        CameraBindingRevision expectedBinding, RecipeDraftContent candidate,
        RecipeActivationSnapshot? previous, string reasonPrefix, CancellationToken cancellationToken)
    {
        var candidateRole = expectedBinding.LogicalRole;
        if (cancellationToken.IsCancellationRequested)
            return new(false, "CameraOperationCancelled", null, false);

        TaskCompletionSource<bool>? drain = null;
        var gateAcquired = false;
        try
        {
            drain = RegisterInFlight();
            gateAcquired = await WaitForOperationGateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!gateAcquired)
                return RecoveryFailure(candidateRole, previous?.CameraSetup, candidate.Camera,
                    candidate.CameraProviderExtension, reasonPrefix + "RecoveryCameraBusy",
                    hardwareTouched: false);

            lock (_stateSync)
            {
                if (_disposed || _lifetime.IsCancellationRequested)
                    return RecoveryFailure(candidateRole, previous?.CameraSetup, candidate.Camera,
                        candidate.CameraProviderExtension, "RuntimeStopped", hardwareTouched: false);
            }

            using var operation = new CancellationTokenSource(
                PositiveTimeout(_options.OperationTimeout));
            var networkBarrier = await CheckNetworkBarrierAsync(operation.Token)
                .ConfigureAwait(false);
            if (networkBarrier is not null)
                return RecoveryFailure(candidateRole, previous?.CameraSetup, candidate.Camera,
                    candidate.CameraProviderExtension, networkBarrier, hardwareTouched: false);

            var persisted = await LoadPersistedAsync(candidateRole, operation.Token)
                .ConfigureAwait(false);
            if (!persisted.Succeeded)
                return RecoveryFailure(candidateRole, previous?.CameraSetup, candidate.Camera,
                    candidate.CameraProviderExtension, persisted.ReasonCode, hardwareTouched: false);
            if (persisted.Pending)
                return RecoveryFailure(candidateRole, previous?.CameraSetup, candidate.Camera,
                    candidate.CameraProviderExtension, "CameraSetupOperationPending",
                    hardwareTouched: false);

            var current = GetSlot(candidateRole)?.Snapshot;
            if (current?.Binding is null)
                return RecoveryFailure(candidateRole, previous?.CameraSetup, candidate.Camera,
                    candidate.CameraProviderExtension, "CameraBindingMissing", hardwareTouched: false);
            if (current.Binding != expectedBinding)
                return RecoveryFailure(candidateRole, previous?.CameraSetup, candidate.Camera,
                    candidate.CameraProviderExtension, reasonPrefix + "RecoveryBindingConflict",
                    hardwareTouched: false);

            return previous is null
                ? await RecoverWithoutPreviousBaselineAsync(current, candidate,
                    operation.Token).ConfigureAwait(false)
                : await RecoverPreviousBaselineAsync(current, previous.CameraSetup, candidate,
                    operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RecoveryFailure(candidateRole, previous?.CameraSetup, candidate.Camera,
                candidate.CameraProviderExtension, "CameraOperationCancelled", false);
        }
        catch (OperationCanceledException)
        {
            return RecoveryFailure(candidateRole, previous?.CameraSetup, candidate.Camera,
                candidate.CameraProviderExtension, "CameraOperationTimeout", true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return RecoveryFailure(candidateRole, previous?.CameraSetup, candidate.Camera,
                candidate.CameraProviderExtension, reasonPrefix + "RecoveryUnavailable", true);
        }
        finally
        {
            if (gateAcquired) _operationGate.Release();
            if (drain is not null) CompleteInFlight(drain);
        }
    }
}

internal sealed partial class RecipeActivationCameraLease
{
    // Manual acquisition is an inspection-only owner.  Once this bit is set a
    // candidate can be restored or safe-closed, never committed as production.
    private bool _manualOwned;
    private Task<CameraDeviceRetirement>? _manualRetirementTask;
    private bool _manualRetirementFailed;

    internal ValueTask<ManualCameraAcquisitionResult> AcquireManualFrameAsync(
        ExecutionCorrelationId correlation, FrameBufferPool framePool,
        IFrameAcquisitionClock clock, CancellationToken cancellationToken = default,
        Func<RecipeActivationPhysicalPhaseClaim>? physicalPhaseFactory = null) =>
        AcquireNonProductionFrameAsync(ExecutionKind.Manual, correlation, framePool,
            clock, cancellationToken, physicalPhaseFactory);

    private async ValueTask<ManualCameraAcquisitionResult> AcquireNonProductionFrameAsync(
        ExecutionKind requiredKind, ExecutionCorrelationId correlation, FrameBufferPool framePool,
        IFrameAcquisitionClock clock, CancellationToken cancellationToken,
        Func<RecipeActivationPhysicalPhaseClaim>? physicalPhaseFactory)
    {
        if (correlation is null)
            throw new ArgumentNullException(nameof(correlation));
        if (requiredKind is not (ExecutionKind.Manual or ExecutionKind.Qualification) ||
            correlation.Kind != requiredKind || correlation.Value == Guid.Empty)
            return Failure(correlation, requiredKind == ExecutionKind.Manual
                ? "CameraManualCorrelationInvalid" : "CameraQualificationCorrelationInvalid");
        if (framePool is null)
            return Failure(correlation, "CameraManualFramePoolUnavailable");
        if (clock is null)
            return Failure(correlation, "CameraManualClockUnavailable");
        if (cancellationToken.IsCancellationRequested)
            return Failure(correlation, "CameraManualAcquisitionCancelled");
        if (!Available)
            return Failure(correlation, ReasonCode);

        await _leaseGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!EnsureManualRetirementReady(out var retirementReason))
                return Failure(correlation, retirementReason);

            IControlledCameraDevice? controlled;
            EffectiveCameraConfiguration? effective;
            lock (this)
            {
                if (_disposed)
                    return Failure(correlation, "CameraActivationLeaseDisposed");
                if (_committed)
                    return Failure(correlation, RecipeActivationCommitted);
                if (_safeClosed)
                    return Failure(correlation, RecipeActivationNoPreviousBaselineClosed);
                if (_restoreSucceeded)
                    return Failure(correlation, RecipeActivationRestored);
                if (_previewOwned)
                    return Failure(correlation, "CameraManualPreviewConflict");
                if (requiredKind == ExecutionKind.Qualification && !_qualificationOwned)
                    return Failure(correlation, "CameraQualificationLeaseRequired");
                if (requiredKind == ExecutionKind.Manual && _qualificationOwned ||
                    requiredKind == ExecutionKind.Qualification && _manualOwned)
                    return Failure(correlation, "CameraNonProductionOwnerConflict");
                if (!_candidatePrepared || _candidateDevice is null ||
                    _candidateSnapshot?.Effective is not { } candidateEffective)
                    return Failure(correlation, "CameraManualCandidateUnavailable");

                // Set the inspection-only ownership bit before leaving the lock,
                // including when the provider does not implement the controlled
                // acquisition contract.  No later caller can Commit the candidate.
                if (requiredKind == ExecutionKind.Manual) _manualOwned = true;
                effective = candidateEffective;
                controlled = _candidateDevice as IControlledCameraDevice;
            }

            if (controlled is null)
                return Failure(correlation, "CameraManualAcquisitionNotControlled");
            if (requiredKind == ExecutionKind.Manual && !controlled.Capabilities.AcquisitionModes.Contains(
                    ProductionAcquisitionMode.SoftwareTrigger))
                return Failure(correlation, "ManualSoftwareTriggerUnavailable");
            if (requiredKind == ExecutionKind.Manual &&
                effective!.ProductionAcquisitionMode != ProductionAcquisitionMode.SoftwareTrigger)
                return Failure(correlation, "ManualSoftwareTriggerRequired");

            return await AcquireManualThroughServiceAsync(controlled, effective!,
                correlation, framePool, clock, cancellationToken, physicalPhaseFactory)
                .ConfigureAwait(false);
        }
        finally
        {
            _leaseGate.Release();
        }
    }

    private async ValueTask<ManualCameraAcquisitionResult> AcquireManualThroughServiceAsync(
        IControlledCameraDevice controlled, EffectiveCameraConfiguration effective,
        ExecutionCorrelationId correlation, FrameBufferPool framePool,
        IFrameAcquisitionClock clock, CancellationToken cancellationToken,
        Func<RecipeActivationPhysicalPhaseClaim>? physicalPhaseFactory)
    {
        CameraAcquisitionService? acquisition = null;
        CameraAcquisitionOutcome? outcome = null;
        IFrameBufferLease? source = null;
        FrameBufferLease? copied = null;
        CameraRetirementObservation? retirement = null;
        var sourceDisposeFailed = false;
        var preparationReason = "CameraManualAcquisitionFailed";
        var framePrepared = false;
        var executionStatus = ExecutionStatus.Error;

        try
        {
            acquisition = new CameraAcquisitionService(
                new ManualControlledCameraBorrow(controlled, physicalPhaseFactory), effective, clock,
                _owner.CreateManualAcquisitionOptions());
            CameraAcquisitionAttempt attempt;
            if (correlation.Kind == ExecutionKind.Qualification)
            {
                acquisition.EnableQualificationSessionAcquisition();
                attempt = await acquisition.AcquireQualificationSessionAsync(correlation,
                    _logicalRole, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                acquisition.EnableManualAcquisition();
                attempt = await acquisition.AcquireManualAsync(correlation,
                    _logicalRole, cancellationToken).ConfigureAwait(false);
            }
            preparationReason = attempt.ReasonCode;
            outcome = attempt.Outcome;
            executionStatus = outcome?.ExecutionStatus ??
                (cancellationToken.IsCancellationRequested
                    ? ExecutionStatus.Cancelled : ExecutionStatus.Error);
            if (!attempt.Accepted || outcome is null)
            {
                preparationReason = attempt.ReasonCode;
            }
            else if (!outcome.Succeeded)
            {
                preparationReason = outcome.ReasonCode;
            }
            else
            {
                source = outcome.TakeFrame();
                if (source is null)
                {
                    preparationReason = "CameraManualFrameMissing";
                }
                else
                {
                    var copy = ManualFrameBufferBridge.TryCopyToPool(source, framePool,
                        cancellationToken);
                    preparationReason = copy.ReasonCode;
                    if (!copy.Succeeded || copy.Lease is null)
                    {
                        copy.Lease?.Dispose();
                    }
                    else
                    {
                        copied = copy.Lease;
                        framePrepared = true;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            preparationReason = "CameraManualAcquisitionCancelled";
            executionStatus = ExecutionStatus.Cancelled;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            preparationReason = "CameraManualAcquisitionFailed";
            executionStatus = ExecutionStatus.Error;
        }
        finally
        {
            // The tracked adapter lease must be returned before the service is
            // retired; otherwise CameraAcquisitionService correctly retains its
            // physical owner waiting for that lease.
            if (outcome is not null)
            {
                try { outcome.Dispose(); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { sourceDisposeFailed = true; }
            }

            if (source is not null)
            {
                try { source.Dispose(); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { sourceDisposeFailed = true; }
            }

            if (acquisition is not null)
            {
                retirement = await BeginManualRetirementBoundedAsync(acquisition)
                    .ConfigureAwait(false);
            }
        }

        if (sourceDisposeFailed || retirement is { SafeToReplace: false })
        {
            copied?.Dispose();
            return Failure(correlation,
                retirement?.ReasonCode ?? "CameraManualRetirementFailed", retirement,
                ExecutionStatus.Error);
        }

        if (!framePrepared || copied is null)
            return Failure(correlation, preparationReason, retirement, executionStatus);

        return new ManualCameraAcquisitionResult(true, "CameraManualFramePrepared",
            correlation, copied, copied.Provenance, retirement, ExecutionStatus.Success);
    }

    private async ValueTask<CameraRetirementObservation?> BeginManualRetirementBoundedAsync(
        CameraAcquisitionService acquisition)
    {
        Task<CameraDeviceRetirement> retirementTask;
        try
        {
            retirementTask = acquisition.BeginRetirement();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _manualRetirementFailed = true;
            _owner.MarkActivationRestorationBlocked(_logicalRole);
            return new CameraRetirementObservation(false, "CameraManualRetirementFailed");
        }

        _manualRetirementTask = retirementTask;
        _ = retirementTask.ContinueWith(static task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

        try
        {
            var result = await retirementTask.WaitAsync(acquisition.Options.ShutdownWaitTimeout)
                .ConfigureAwait(false);
            return new CameraRetirementObservation(result.SafeToReplace, result.ReasonCode);
        }
        catch (TimeoutException)
        {
            // The service retains the physical task and its non-owning view.  A
            // caller can still finish the logical run while Restore/Dispose below
            // refuse to touch the raw device until this task has retired.
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _manualRetirementFailed = true;
            _owner.MarkActivationRestorationBlocked(_logicalRole);
            return new CameraRetirementObservation(false, "CameraManualRetirementFailed");
        }
    }

    /// <summary>
    /// Returns false while the acquisition service still owns an actual provider
    /// operation.  This check is called under the activation lease gate by a
    /// subsequent acquisition, RestoreAsync or DisposeAsync.
    /// </summary>
    private bool EnsureManualRetirementReady(out string reasonCode)
    {
        reasonCode = "CameraManualRetirementPending";
        if (_manualRetirementFailed)
        {
            reasonCode = "CameraManualRetirementFailed";
            _owner.MarkActivationRestorationBlocked(_logicalRole);
            return false;
        }

        var task = _manualRetirementTask;
        if (task is null) return true;
        if (!task.IsCompleted) return false;

        CameraDeviceRetirement result;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _manualRetirementFailed = true;
            reasonCode = "CameraManualRetirementFailed";
            _owner.MarkActivationRestorationBlocked(_logicalRole);
            return false;
        }

        _manualRetirementTask = null;
        if (result.SafeToReplace) return true;

        _manualRetirementFailed = true;
        reasonCode = result.ReasonCode;
        ReasonCode = result.ReasonCode;
        _owner.MarkActivationRestorationBlocked(_logicalRole);
        return false;
    }

    /// <summary>
    /// Observes the provider retirement task without cancelling it.  A timeout
    /// only reports that retirement is still pending; it never clears the task
    /// or permits Restore/Dispose to reach the raw device.  The activation
    /// owner should call this before restoration and retry with a bounded
    /// cleanup token until <see cref="ManualRetirementWaitObservation.Completed"/>
    /// is true.
    /// </summary>
    internal async ValueTask<ManualRetirementWaitObservation>
        WaitForManualRetirementAsync(TimeSpan timeout,
            CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
            return ManualRetirementWaitObservation.Complete(false,
                "CameraManualRetirementWaitInvalid");

        // Serialise with Acquire/Restore/Dispose, but do not hold a raw-device
        // lock while waiting for a provider task.  The lease gate is only the
        // ownership admission fence; the service task itself performs its
        // physical retirement independently.
        await _leaseGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_manualRetirementFailed)
                return ManualRetirementWaitObservation.Complete(false,
                    "CameraManualRetirementFailed");

            var retirementTask = _manualRetirementTask;
            if (retirementTask is null)
                return ManualRetirementWaitObservation.Complete(true,
                    "CameraManualRetirementNotPending");

            if (!retirementTask.IsCompleted)
            {
                try
                {
                    await retirementTask.WaitAsync(timeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // A completion racing this timeout is observed below.  If
                    // it has not completed yet, retain the task and owner.
                    if (!retirementTask.IsCompleted)
                        return ManualRetirementWaitObservation.Pending(
                            "CameraManualRetirementPending");
                }
                catch (OperationCanceledException)
                {
                    // Caller cancellation cancels only this observation.  The
                    // provider retirement remains owned and tracked by lease.
                    if (!retirementTask.IsCompleted)
                        return ManualRetirementWaitObservation.Pending(
                            "CameraManualRetirementWaitCancelled");
                }
            }

            if (!retirementTask.IsCompleted)
                return ManualRetirementWaitObservation.Pending(
                    "CameraManualRetirementPending");

            if (EnsureManualRetirementReady(out var reasonCode))
                return ManualRetirementWaitObservation.Complete(true,
                    "CameraDeviceRetired");
            return ManualRetirementWaitObservation.Complete(false, reasonCode);
        }
        finally
        {
            _leaseGate.Release();
        }
    }

    private static ManualCameraAcquisitionResult Failure(
        ExecutionCorrelationId correlation, string? reasonCode,
        CameraRetirementObservation? retirement = null,
        ExecutionStatus executionStatus = ExecutionStatus.Error) =>
        new(false, string.IsNullOrWhiteSpace(reasonCode)
            ? "CameraManualAcquisitionFailed" : reasonCode, correlation,
            null, null, retirement, executionStatus);
}

/// <summary>
/// Copies a provider loan into the already configured bounded Runtime pool.  The
/// provider loan is never handed to an algorithm or to the manual caller.
/// </summary>
internal static class ManualFrameBufferBridge
{
    internal static FrameCopyResult TryCopyToPool(IFrameBufferLease source,
        FrameBufferPool pool, CancellationToken cancellationToken = default)
    {
        if (source is null) return Failure("CameraManualFrameMissing");
        if (pool is null) return Failure("CameraManualFramePoolUnavailable");

        byte[]? rented = null;
        try
        {
            if (source.IsReturned) return Failure("CameraManualFrameReturned");
            var frame = source.Frame;
            if (!frame.IsLoanActive) return Failure("CameraManualFrameLoanExpired");
            var metadata = frame.Metadata;
            var provenance = source.Provenance;
            if (metadata is null || provenance is null)
                return Failure("CameraManualFrameEvidenceMissing");

            var poolSnapshot = pool.GetSnapshot();
            if (metadata.FullBufferLayoutLength > poolSnapshot.MaximumFrameBytes)
                return Failure("CameraManualFrameExceedsPoolCapacity");
            if (metadata.RequiredBufferLength is <= 0 or > int.MaxValue)
                return Failure("CameraManualFrameTooLarge");

            var sourceLength = checked((int)metadata.RequiredBufferLength);
            rented = ArrayPool<byte>.Shared.Rent(sourceLength);
            rented.AsSpan(0, sourceLength).Clear();
            for (var row = 0; row < metadata.Height; row++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Failure("CameraManualAcquisitionCancelled");
                var rowData = frame.GetRowSpan(row);
                if (rowData.Length < metadata.ValidRowBytes)
                    return Failure("CameraManualFrameCoverageInvalid");
                rowData[..metadata.ValidRowBytes].CopyTo(
                    rented.AsSpan(checked(row * metadata.StrideBytes),
                        metadata.ValidRowBytes));
            }

            return pool.TryCopyFrame(metadata, provenance,
                rented.AsSpan(0, sourceLength), cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failure("CameraManualFrameCopyFailed");
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static FrameCopyResult Failure(string reasonCode) => new(false, reasonCode,
        null, ExecutionStatus.Error, InspectionDecision.Unknown);
}
