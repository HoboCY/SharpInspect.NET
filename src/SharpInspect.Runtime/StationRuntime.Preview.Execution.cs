using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Preview;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    // The command partial owns admission and durable state.  This table owns only
    // the in-process physical lifetime between admission and the terminal event.
    // It is deliberately keyed by the private owner so an old reader cannot act
    // on a later session after the projection has moved on.
    private readonly Dictionary<PreviewSessionOwner, PreviewExecutionState>
        _previewExecutionStates = new();

    /// <summary>
    /// Applies the admitted Draft camera settings, starts the preview-only stream,
    /// and starts the bounded latest-frame reader.  No production acquisition or
    /// algorithm capability is reachable from this path.
    /// </summary>
    private async Task ExecutePreviewStartAsync(PreviewSessionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var state = GetOrCreatePreviewExecutionState(owner);
        try
        {
            if (!await RecordPreviewProgressAsync(owner, PreviewSessionPhase.Starting,
                    "PreviewStarting").ConfigureAwait(false))
            {
                RequestPreviewFailure(owner, "PreviewProgressPersistenceFailed");
                return;
            }

            RecipeDraftRevision draft;
            CameraSetupSnapshot? baseline;
            CancellationToken token;
            Guid commandCorrelation;
            lock (_sync)
            {
                if (!IsCurrentPreviewOwnerLocked(owner))
                {
                    RequestPreviewFailureLocked(owner, "PreviewSessionOwnerUnavailable");
                    return;
                }

                draft = owner.Draft;
                baseline = owner.Baseline?.SuccessfulSnapshot?.CameraSetup;
                if (owner.Baseline is not null && baseline is null)
                {
                    RequestPreviewFailureLocked(owner, "PreviewBaselineUnavailable");
                    return;
                }
                if (!string.Equals(draft.Content.CameraRole, owner.Header.LogicalCameraRole,
                        StringComparison.Ordinal))
                {
                    RequestPreviewFailureLocked(owner, "PreviewCameraRoleMismatch");
                    return;
                }

                token = owner.ExitCancellation.Token;
                commandCorrelation = owner.PendingCommand?.CorrelationId ?? Guid.Empty;
            }

            if (token.IsCancellationRequested)
            {
                RequestPreviewFailure(owner, "PreviewOperationCancelled");
                return;
            }

            var camera = await _cameraSetupRuntime.ReserveRecipeActivationAsync(
                owner.Header.LogicalCameraRole, token).ConfigureAwait(false);
            if (!camera.Available)
            {
                RequestPreviewFailure(owner, camera.ReasonCode);
                return;
            }

            if (!InstallPreviewCamera(owner, camera))
            {
                await camera.DisposeAsync().ConfigureAwait(false);
                RequestPreviewFailure(owner, "PreviewSessionOwnerUnavailable");
                return;
            }

            var phaseFactory = () => ClaimPreviewPhysicalPhase(owner,
                commandCorrelation == Guid.Empty ? null : commandCorrelation);
            var applied = await camera.ApplyAsync(draft.Content.Camera,
                draft.Content.CameraProviderExtension, token, baseline, phaseFactory)
                .ConfigureAwait(false);
            if (!applied.Succeeded || applied.Snapshot is null)
            {
                RequestPreviewFailure(owner, applied.ReasonCode);
                return;
            }

            var requested = new PreviewTuningConfiguration(
                PreviewDraftSettings.FromRequested(draft.Content.Camera));
            lock (_sync)
            {
                if (!IsCurrentPreviewOwnerLocked(owner) || owner.ExitRequested)
                {
                    RequestPreviewFailureLocked(owner, "PreviewSessionStopping");
                    return;
                }
                state.StartAttempted = true;
            }

            var started = await camera.StartPreviewAsync(owner.Header.SessionId, requested, token,
                phaseFactory).ConfigureAwait(false);
            if (!started.Completed || started.Result is not { Succeeded: true, Configuration: { } actual })
            {
                RequestPreviewFailure(owner, PreviewResultReason(started,
                    "PreviewStartFailed"));
                return;
            }

            lock (_sync)
            {
                if (!IsCurrentPreviewOwnerLocked(owner) || owner.ExitRequested ||
                    token.IsCancellationRequested)
                {
                    RequestPreviewFailureLocked(owner, "PreviewOperationCancelled");
                    return;
                }
                state.PreviewStarted = true;
                owner.Configuration = actual;
                owner.LastFrameSequence = -1;
                PublishPreviewLocked(owner, PreviewSessionPhase.Streaming,
                    "PreviewStreaming");
            }

            if (!await RecordPreviewProgressAsync(owner, PreviewSessionPhase.Streaming,
                    "PreviewStreaming", completeCommand: true).ConfigureAwait(false))
            {
                RequestPreviewFailure(owner, "PreviewProgressPersistenceFailed");
                return;
            }

            lock (_sync)
            {
                if (IsCurrentPreviewOwnerLocked(owner) && !owner.ExitRequested)
                    ResumePreviewReaderLocked(owner);
            }
        }
        catch (OperationCanceledException) when (owner.ExitCancellation.IsCancellationRequested)
        {
            RequestPreviewFailure(owner, "PreviewOperationCancelled");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RequestPreviewFailure(owner, SafePreviewExecutionReason(exception));
        }
        finally
        {
            if (ShouldRestorePreview(owner))
                await RestorePreviewSessionAsync(owner).ConfigureAwait(false);
        }
    }

    /// <summary>Pauses the reader before a physical tuning transaction.</summary>
    private async Task ExecutePreviewTuningAsync(PreviewSessionOwner owner,
        ApplyPreviewTuningCommand command)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(command);
        var state = GetOrCreatePreviewExecutionState(owner);
        try
        {
            if (!await PausePreviewReaderAsync(owner).ConfigureAwait(false))
            {
                RequestPreviewFailure(owner, "PreviewReaderRetirementFailed");
                return;
            }

            CancellationToken token;
            lock (_sync)
            {
                if (!IsCurrentPreviewOwnerLocked(owner) || owner.ExitRequested ||
                    owner.Camera is null || !state.PreviewStarted)
                {
                    RequestPreviewFailureLocked(owner, "PreviewSessionStopping");
                    return;
                }
                PublishPreviewLocked(owner, PreviewSessionPhase.Tuning, "PreviewTuning");
                token = owner.ExitCancellation.Token;
            }

            if (!await RecordPreviewProgressAsync(owner, PreviewSessionPhase.Tuning,
                    "PreviewTuning").ConfigureAwait(false))
            {
                RequestPreviewFailure(owner, "PreviewProgressPersistenceFailed");
                return;
            }

            var camera = GetPreviewCamera(owner);
            if (camera is null)
            {
                RequestPreviewFailure(owner, "PreviewCameraUnavailable");
                return;
            }

            var correlation = command.CorrelationId;
            var tuned = await camera.TunePreviewAsync(command.Configuration, token,
                () => ClaimPreviewPhysicalPhase(owner, correlation)).ConfigureAwait(false);
            if (!tuned.Completed || tuned.Result is not { Succeeded: true, Configuration: { } actual })
            {
                RequestPreviewFailure(owner, PreviewResultReason(tuned,
                    "PreviewTuningFailed"));
                return;
            }

            lock (_sync)
            {
                if (!IsCurrentPreviewOwnerLocked(owner) || owner.ExitRequested ||
                    token.IsCancellationRequested)
                {
                    RequestPreviewFailureLocked(owner, "PreviewOperationCancelled");
                    return;
                }
                owner.Configuration = actual;
                PublishPreviewLocked(owner, PreviewSessionPhase.Streaming,
                    "PreviewStreaming");
            }

            if (!await RecordPreviewProgressAsync(owner, PreviewSessionPhase.Streaming,
                    "PreviewTuningApplied", completeCommand: true).ConfigureAwait(false))
            {
                RequestPreviewFailure(owner, "PreviewProgressPersistenceFailed");
                return;
            }

            lock (_sync)
            {
                if (IsCurrentPreviewOwnerLocked(owner) && !owner.ExitRequested)
                    ResumePreviewReaderLocked(owner);
            }
        }
        catch (OperationCanceledException) when (owner.ExitCancellation.IsCancellationRequested)
        {
            RequestPreviewFailure(owner, "PreviewOperationCancelled");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RequestPreviewFailure(owner, SafePreviewExecutionReason(exception));
        }
        finally
        {
            if (ShouldRestorePreview(owner))
                await RestorePreviewSessionAsync(owner).ConfigureAwait(false);
        }
    }

    /// <summary>Freezes fixed settings after the reader has retired.</summary>
    private async Task ExecutePreviewFreezeAsync(PreviewSessionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var state = GetOrCreatePreviewExecutionState(owner);
        try
        {
            if (!await PausePreviewReaderAsync(owner).ConfigureAwait(false))
            {
                RequestPreviewFailure(owner, "PreviewReaderRetirementFailed");
                return;
            }

            CancellationToken token;
            Guid correlation;
            lock (_sync)
            {
                if (!IsCurrentPreviewOwnerLocked(owner) || owner.ExitRequested ||
                    owner.Camera is null || !state.PreviewStarted)
                {
                    RequestPreviewFailureLocked(owner, "PreviewSessionStopping");
                    return;
                }
                PublishPreviewLocked(owner, PreviewSessionPhase.Freezing, "PreviewFreezing");
                token = owner.ExitCancellation.Token;
                correlation = owner.PendingCommand?.CorrelationId ?? Guid.Empty;
            }

            if (!await RecordPreviewProgressAsync(owner, PreviewSessionPhase.Freezing,
                    "PreviewFreezing").ConfigureAwait(false))
            {
                RequestPreviewFailure(owner, "PreviewProgressPersistenceFailed");
                return;
            }

            var camera = GetPreviewCamera(owner);
            if (camera is null)
            {
                RequestPreviewFailure(owner, "PreviewCameraUnavailable");
                return;
            }

            var frozen = await camera.FreezePreviewAsync(token,
                () => ClaimPreviewPhysicalPhase(owner,
                    correlation == Guid.Empty ? null : correlation)).ConfigureAwait(false);
            if (!frozen.Completed || frozen.Result is not { Succeeded: true, Configuration: { } actual })
            {
                RequestPreviewFailure(owner, PreviewResultReason(frozen,
                    "PreviewFreezeFailed"));
                return;
            }
            if (!PreviewDraftSettings.IsFixed(actual))
            {
                RequestPreviewFailure(owner, "PreviewAutomaticControlsStillEnabled");
                return;
            }

            lock (_sync)
            {
                if (!IsCurrentPreviewOwnerLocked(owner) || owner.ExitRequested ||
                    token.IsCancellationRequested)
                {
                    RequestPreviewFailureLocked(owner, "PreviewOperationCancelled");
                    return;
                }
                owner.Configuration = actual;
                owner.FrozenSettings = actual.ProcessSettings;
                PublishPreviewLocked(owner, PreviewSessionPhase.Streaming,
                    "PreviewFrozen");
            }

            if (!await RecordPreviewProgressAsync(owner, PreviewSessionPhase.Streaming,
                    "PreviewFrozen", completeCommand: true).ConfigureAwait(false))
            {
                RequestPreviewFailure(owner, "PreviewProgressPersistenceFailed");
                return;
            }

            lock (_sync)
            {
                if (IsCurrentPreviewOwnerLocked(owner) && !owner.ExitRequested)
                    ResumePreviewReaderLocked(owner);
            }
        }
        catch (OperationCanceledException) when (owner.ExitCancellation.IsCancellationRequested)
        {
            RequestPreviewFailure(owner, "PreviewOperationCancelled");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RequestPreviewFailure(owner, SafePreviewExecutionReason(exception));
        }
        finally
        {
            if (ShouldRestorePreview(owner))
                await RestorePreviewSessionAsync(owner).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cancels the current read asynchronously and waits for the provider call to
    /// retire.  The cancellation callback is never run while the station lock is
    /// held, and no next physical operation starts until this task has completed.
    /// </summary>
    private async Task<bool> PausePreviewReaderAsync(PreviewSessionOwner owner)
    {
        Task? reader;
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (!IsCurrentPreviewOwnerLocked(owner)) return false;
            reader = owner.Reader;
            cancellation = owner.ReadCancellation;
        }
        if (reader is null)
        {
            var dispose = false;
            lock (_sync)
            {
                if (IsCurrentPreviewOwnerLocked(owner) &&
                    ReferenceEquals(owner.ReadCancellation, cancellation))
                {
                    owner.ReadCancellation = null;
                    dispose = true;
                }
            }
            if (dispose) DisposeCancellation(cancellation);
            return true;
        }

        QueueCancellation(cancellation);
        var retired = true;
        try { await reader.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            retired = false;
        }

        var disposeReaderCancellation = false;
        lock (_sync)
        {
            if (IsCurrentPreviewOwnerLocked(owner) && ReferenceEquals(owner.Reader, reader))
            {
                owner.Reader = null;
                owner.ReadCancellation = null;
                disposeReaderCancellation = true;
            }
        }
        if (disposeReaderCancellation) DisposeCancellation(cancellation);
        return retired;
    }

    /// <summary>
    /// Starts one latest-only reader.  The caller may hold <see cref="_sync"/>;
    /// this method only allocates state and queues work, so it never enters a
    /// provider callback under the station lock.
    /// </summary>
    private void ResumePreviewReaderLocked(PreviewSessionOwner owner)
    {
        if (!Monitor.IsEntered(_sync))
        {
            lock (_sync) ResumePreviewReaderLocked(owner);
            return;
        }
        if (!IsCurrentPreviewOwnerLocked(owner) || owner.ExitRequested ||
            owner.Camera is null || owner.Configuration is null || owner.Reader is not null)
            return;
        if (owner.ExitCancellation.IsCancellationRequested) return;

        var state = GetOrCreatePreviewExecutionStateLocked(owner);
        if (state.RestoreTask is not null || state.ReaderStarting)
            return;
        state.ReaderStarting = true;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            owner.ExitCancellation.Token);
        owner.ReadCancellation = cancellation;
        var reader = Task.Run(() => RunPreviewReaderAsync(owner, cancellation));
        owner.Reader = reader;
        state.ReaderStarting = false;
        _ = reader.ContinueWith(static (task, boxed) =>
        {
            var tuple = ((StationRuntime Runtime, PreviewSessionOwner Owner,
                CancellationTokenSource Cancellation))boxed!;
            tuple.Runtime.OnPreviewReaderRetired(tuple.Owner, task, tuple.Cancellation);
        }, (this, owner, cancellation), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RunPreviewReaderAsync(PreviewSessionOwner owner,
        CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                RecipeActivationCameraLease? camera;
                long afterSequence;
                lock (_sync)
                {
                    if (!IsCurrentPreviewOwnerLocked(owner) || owner.ExitRequested ||
                        owner.Configuration is null)
                        return;
                    camera = owner.Camera;
                    afterSequence = owner.LastFrameSequence;
                }
                if (camera is null) return;

                if (!await CheckPreviewAuthorityBeforeReadAsync(owner).ConfigureAwait(false))
                    return;

                var correlation = CurrentPreviewCommandCorrelation(owner);
                var read = await camera.ReadPreviewFrameAsync(afterSequence, token,
                    () => ClaimPreviewPhysicalPhase(owner, correlation)).ConfigureAwait(false);
                if (!read.Completed)
                {
                    if (token.IsCancellationRequested)
                        return;
                    if (read.DeviceTransferred)
                    {
                        RequestPreviewReaderFailure(owner, read.ReasonCode);
                        return;
                    }
                    // These coordinator refusals happen before the provider is
                    // invoked. A competing observation or accepted tuning stage
                    // must not turn harmless contention into a session failure.
                    if (read.ReasonCode is not ("PreviewRuntimeBusy" or
                        "PreviewOperationSuperseded" or "PreviewPhysicalOperationInProgress") &&
                        !IsTransientPreviewReadFailure(read.ReasonCode))
                    {
                        RequestPreviewReaderFailure(owner, read.ReasonCode);
                        return;
                    }
                }
                else if (read.Result is not { Succeeded: true, Frame: { } frame })
                {
                    // A joined provider can report cancellation as a result
                    // instead of throwing. Pause owns this requested read
                    // cancellation; it must not terminate the whole session.
                    if (token.IsCancellationRequested) return;
                    var reason = read.Result?.ReasonCode ?? read.ReasonCode;
                    if (!IsTransientPreviewReadFailure(reason))
                    {
                        RequestPreviewReaderFailure(owner, reason);
                        return;
                    }
                }
                else
                {
                    if (!TryPublishPreviewFrame(owner, frame)) return;
                }

                var interval = _previewOptions?.FrameInterval ?? TimeSpan.FromMilliseconds(100);
                try { await Task.Delay(interval, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RequestPreviewReaderFailure(owner, SafePreviewExecutionReason(exception));
        }
        // The reader task's completion is the retirement signal consumed by
        // PausePreviewReaderAsync.  owner.Retired belongs to the whole session
        // and is completed only by FinishPreviewSessionAsync after restoration.
    }

    private void OnPreviewReaderRetired(PreviewSessionOwner owner, Task reader,
        CancellationTokenSource cancellation)
    {
        var restore = false;
        lock (_sync)
        {
            if (IsCurrentPreviewOwnerLocked(owner) && ReferenceEquals(owner.Reader, reader))
            {
                if (!owner.ExitRequested)
                {
                    owner.Reader = null;
                    owner.ReadCancellation = null;
                }
                var state = GetOrCreatePreviewExecutionStateLocked(owner);
                restore = owner.ExitRequested && state.RestoreTask is null;
            }
        }
        // A normal reader completion owns disposal here.  An exiting reader is
        // disposed by PausePreviewReaderAsync after it has joined the task; this
        // avoids racing Dispose with the bounded provider operation.
        if (!restore) DisposeCancellation(cancellation);
        if (restore) _ = RestorePreviewSessionAsync(owner);
    }

    /// <summary>
    /// Serializes the full physical exit.  It is safe for an explicit Exit,
    /// stage failure, reader failure, and the stage finally blocks to call this
    /// concurrently; exactly one task owns Stop/Restore/Dispose.
    /// </summary>
    private Task RestorePreviewSessionAsync(PreviewSessionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        TaskCompletionSource<bool> completion;
        PreviewExecutionState state;
        lock (_sync)
        {
            if (!IsCurrentPreviewOwnerLocked(owner)) return Task.CompletedTask;
            state = GetOrCreatePreviewExecutionStateLocked(owner);
            if (state.RestoreTask is { } existing) return existing;
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            state.RestoreTask = completion.Task;
        }

        _ = RunPreviewRestoreAsync(owner, state, completion);
        return completion.Task;
    }

    private async Task RunPreviewRestoreAsync(PreviewSessionOwner owner,
        PreviewExecutionState state, TaskCompletionSource<bool> completion)
    {
        var terminalReason = "PreviewSessionExited";
        var terminalSucceeded = false;
        var restoration = PreviewRestorationState.NotRequired;
        try
        {
            QueueCancellation(owner.ExitCancellation);
            var readerRetired = await PausePreviewReaderAsync(owner).ConfigureAwait(false);

            string reason;
            lock (_sync)
            {
                reason = state.FailureRequested ? state.FailureReason ?? owner.ExitReason :
                    owner.ExitReason;
                owner.ExitRequested = true;
                owner.ExitReason = reason;
                PublishPreviewLocked(owner, PreviewSessionPhase.Restoring, reason);
            }
            terminalReason = reason;
            // Restoration must continue even when the intermediate progress
            // event is rejected because Exit is already authoritative.  The
            // terminal Finish call below is the durable success/failure event.
            try
            {
                _ = await RecordPreviewProgressAsync(owner, PreviewSessionPhase.Restoring,
                    reason).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Keep the physical cleanup path independent of audit I/O.
            }
            if (!readerRetired)
            {
                terminalReason = "PreviewReaderRetirementFailed";
                restoration = PreviewRestorationState.RecoveryBlocked;
                lock (_sync) _previewRecoveryBlocked = true;
            }

            RecipeActivationCameraLease? camera;
            bool stopAttempted;
            CameraSetupSnapshot? baseline;
            lock (_sync)
            {
                camera = owner.Camera;
                stopAttempted = state.StartAttempted;
                baseline = owner.Baseline?.SuccessfulSnapshot?.CameraSetup;
            }

            var stopFailure = false;
            if (camera is not null)
            {
                if (stopAttempted)
                {
                    try
                    {
                        var stopped = await camera.StopPreviewAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                        stopFailure = !stopped.Completed || stopped.Result is not { Succeeded: true };
                        if (stopFailure && !state.FailureRequested)
                            terminalReason = PreviewResultReason(stopped, "PreviewStopFailed");
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        stopFailure = true;
                        if (!state.FailureRequested)
                            terminalReason = SafePreviewExecutionReason(exception);
                    }
                }

                RecipeActivationCameraRestoreResult? restored = null;
                try
                {
                    // Restoration owns its independent cleanup budget.  Exit and
                    // caller cancellation must not abandon the prior camera.
                    restored = await camera.RestoreAsync(baseline, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    restoration = PreviewRestorationState.RecoveryBlocked;
                    terminalReason = SafePreviewExecutionReason(exception);
                    lock (_sync) _previewRecoveryBlocked = true;
                }
                if (restored is { Succeeded: true })
                {
                    if (readerRetired)
                    {
                        restoration = restored.ReasonCode == "NoPreviousBaselineClosed"
                            ? PreviewRestorationState.NoActiveBaselineClosed
                            : PreviewRestorationState.Restored;
                    }
                    else
                    {
                        restoration = PreviewRestorationState.RecoveryBlocked;
                        lock (_sync) _previewRecoveryBlocked = true;
                    }
                    if (!state.FailureRequested && !stopFailure && readerRetired)
                    {
                        terminalSucceeded = true;
                        terminalReason = owner.ExitReason;
                    }
                    else if (!state.FailureRequested && stopFailure &&
                        terminalReason == "PreviewSessionExited")
                    {
                        terminalReason = "PreviewStopFailed";
                    }
                }
                else if (restored is not null)
                {
                    restoration = PreviewRestorationState.RecoveryBlocked;
                    terminalReason = restored.ReasonCode;
                    lock (_sync) _previewRecoveryBlocked = true;
                }

                var leaseDisposed = false;
                try
                {
                    await camera.DisposeAsync().ConfigureAwait(false);
                    leaseDisposed = true;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    restoration = PreviewRestorationState.RecoveryBlocked;
                    terminalReason = "PreviewCameraLeaseDisposeFailed";
                    terminalSucceeded = false;
                    lock (_sync) _previewRecoveryBlocked = true;
                }

                lock (_sync)
                {
                    // Retain an undisposed lease while recovery is blocked so
                    // the station still owns any late physical resource.
                    if (leaseDisposed && IsCurrentPreviewOwnerLocked(owner))
                        owner.Camera = null;
                }
            }
            else
            {
                terminalSucceeded = !state.FailureRequested && readerRetired;
                terminalReason = state.FailureRequested
                    ? state.FailureReason ?? owner.ExitReason : "PreviewSessionClosed";
                if (state.StartAttempted)
                {
                    terminalSucceeded = false;
                    restoration = PreviewRestorationState.RecoveryBlocked;
                    terminalReason = "PreviewCameraUnavailable";
                    lock (_sync) _previewRecoveryBlocked = true;
                }
            }

            lock (_sync)
            {
                owner.Restoration = restoration;
                // The final phase becomes observable only after its durable
                // terminal is written by FinishPreviewSessionAsync.
                PublishPreviewLocked(owner, PreviewSessionPhase.Restoring, terminalReason);
            }

            try
            {
                await FinishPreviewSessionAsync(owner, terminalSucceeded, terminalReason,
                    restoration).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                lock (_sync)
                {
                    _previewRecoveryBlocked = true;
                    owner.Restoration = PreviewRestorationState.RecoveryBlocked;
                    PublishPreviewLocked(owner, PreviewSessionPhase.RecoveryBlocked,
                        "PreviewTerminalPersistenceFailed");
                }
                // FinishPreviewSessionAsync owns the normal terminal signal. If
                // its durable write itself faults, release shutdown waiters only
                // after the physical cleanup above has already completed.
                owner.Retired.TrySetResult(true);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            terminalReason = SafePreviewExecutionReason(exception);
            restoration = PreviewRestorationState.RecoveryBlocked;
            lock (_sync)
            {
                _previewRecoveryBlocked = true;
                owner.Restoration = restoration;
                PublishPreviewLocked(owner, PreviewSessionPhase.RecoveryBlocked, terminalReason);
            }
            try
            {
                await FinishPreviewSessionAsync(owner, false, terminalReason,
                    restoration).ConfigureAwait(false);
            }
            catch (Exception finishException) when (finishException is not OutOfMemoryException) { }
            owner.Retired.TrySetResult(true);
        }
        finally
        {
            completion.TrySetResult(true);
            lock (_sync)
            {
                if (!IsCurrentPreviewOwnerLocked(owner))
                    _previewExecutionStates.Remove(owner);
            }
        }
    }

    private bool TryPublishPreviewFrame(PreviewSessionOwner owner, CameraPreviewFrame frame)
    {
        try
        {
            lock (_sync)
            {
                if (!IsCurrentPreviewOwnerLocked(owner) || owner.ExitRequested ||
                    owner.Configuration is null || _previewOptions is null ||
                    _previewSnapshot?.Phase != PreviewSessionPhase.Streaming)
                    return false;
                if (frame.SessionId != owner.Header.SessionId)
                {
                    RequestPreviewReaderFailureLocked(owner, "PreviewFrameSessionMismatch");
                    return false;
                }
                if (frame.Sequence <= owner.LastFrameSequence)
                {
                    RequestPreviewReaderFailureLocked(owner, "PreviewFrameSequenceInvalid");
                    return false;
                }
                var expectedLength = (long)frame.StrideBytes * frame.Height;
                if (frame.BufferLength > _previewOptions.MaximumFrameBytes ||
                    frame.BufferLength > CameraPreviewFrame.MaximumBytes ||
                    frame.StrideBytes < frame.ValidRowBytes || expectedLength < 0 ||
                    expectedLength > CameraPreviewFrame.MaximumBytes ||
                    frame.BufferLength != expectedLength)
                {
                    RequestPreviewReaderFailureLocked(owner, "PreviewFrameLayoutInvalid");
                    return false;
                }
                // Touching one row verifies that the immutable frame still exposes
                // its declared copied layout without retaining an adapter buffer.
                ReadOnlySpan<byte> firstRow;
                try { firstRow = frame.GetRowSpan(0); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    RequestPreviewReaderFailureLocked(owner, "PreviewFrameLayoutInvalid");
                    return false;
                }
                if (frame.Height < 1 || firstRow.Length != frame.StrideBytes)
                {
                    RequestPreviewReaderFailureLocked(owner, "PreviewFrameLayoutInvalid");
                    return false;
                }
                owner.LastFrameSequence = frame.Sequence;
                PublishPreviewLocked(owner, PreviewSessionPhase.Streaming,
                    "PreviewFrameReceived", frame);
                return true;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RequestPreviewReaderFailure(owner, SafePreviewExecutionReason(exception));
            return false;
        }
    }

    private void RequestPreviewReaderFailure(PreviewSessionOwner owner, string reason)
    {
        lock (_sync) RequestPreviewReaderFailureLocked(owner, reason);
    }

    private void RequestPreviewReaderFailureLocked(PreviewSessionOwner owner, string reason)
    {
        if (!IsCurrentPreviewOwnerLocked(owner)) return;
        var state = GetOrCreatePreviewExecutionStateLocked(owner);
        // An explicit Exit (including authority/session shutdown) already owns
        // the terminal reason.  Cancellation observed by the stage must not
        // repaint that clean exit as a reader failure.
        if (owner.ExitRequested && !state.FailureRequested) return;
        var firstFailure = !state.FailureRequested;
        state.FailureRequested = true;
        if (firstFailure) state.FailureReason = SafePreviewReason(reason);
        if (!owner.ExitRequested)
        {
            owner.ExitRequested = true;
            owner.ExitReason = state.FailureReason!;
        }
        PublishPreviewLocked(owner, PreviewSessionPhase.Restoring,
            state.FailureReason ?? "PreviewExecutionFailed");
        QueueCancellation(owner.ExitCancellation);
        QueueCancellation(owner.ReadCancellation);
    }

    private void RequestPreviewFailure(PreviewSessionOwner owner, string reason)
    {
        lock (_sync) RequestPreviewFailureLocked(owner, reason);
    }

    private void RequestPreviewFailureLocked(PreviewSessionOwner owner, string reason)
    {
        if (!IsCurrentPreviewOwnerLocked(owner)) return;
        var state = GetOrCreatePreviewExecutionStateLocked(owner);
        if (owner.ExitRequested && !state.FailureRequested) return;
        var firstFailure = !state.FailureRequested;
        state.FailureRequested = true;
        if (firstFailure) state.FailureReason = SafePreviewReason(reason);
        if (!owner.ExitRequested)
        {
            owner.ExitRequested = true;
            owner.ExitReason = state.FailureReason!;
        }
        PublishPreviewLocked(owner, PreviewSessionPhase.Restoring,
            state.FailureReason ?? "PreviewExecutionFailed");
        QueueCancellation(owner.ExitCancellation);
        QueueCancellation(owner.ReadCancellation);
    }

    private bool ShouldRestorePreview(PreviewSessionOwner owner)
    {
        lock (_sync)
        {
            if (!IsCurrentPreviewOwnerLocked(owner)) return false;
            var state = GetOrCreatePreviewExecutionStateLocked(owner);
            return owner.ExitRequested || state.FailureRequested;
        }
    }

    private RecipeActivationCameraLease? GetPreviewCamera(PreviewSessionOwner owner)
    {
        lock (_sync)
            return IsCurrentPreviewOwnerLocked(owner) ? owner.Camera : null;
    }

    private bool InstallPreviewCamera(PreviewSessionOwner owner,
        RecipeActivationCameraLease camera)
    {
        lock (_sync)
        {
            if (!IsCurrentPreviewOwnerLocked(owner) || owner.ExitRequested || owner.Camera is not null)
                return false;
            owner.Camera = camera;
            return true;
        }
    }

    private Guid? CurrentPreviewCommandCorrelation(PreviewSessionOwner owner)
    {
        lock (_sync)
        {
            var correlation = owner.PendingCommand?.CorrelationId;
            return correlation == Guid.Empty ? null : correlation;
        }
    }

    private bool IsCurrentPreviewOwnerLocked(PreviewSessionOwner owner) =>
        ReferenceEquals(_previewOwner, owner) && !_disposed;

    private PreviewExecutionState GetOrCreatePreviewExecutionState(PreviewSessionOwner owner)
    {
        lock (_sync) return GetOrCreatePreviewExecutionStateLocked(owner);
    }

    private PreviewExecutionState GetOrCreatePreviewExecutionStateLocked(
        PreviewSessionOwner owner)
    {
        if (!_previewExecutionStates.TryGetValue(owner, out var state))
        {
            state = new PreviewExecutionState();
            _previewExecutionStates.Add(owner, state);
        }
        return state;
    }

    private static void QueueCancellation(CancellationTokenSource? source)
    {
        if (source is null) return;
        _ = Task.Run(() =>
        {
            try { source.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        });
    }

    private static void DisposeCancellation(CancellationTokenSource? source)
    {
        if (source is null) return;
        try { source.Dispose(); }
        catch (ObjectDisposedException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    private async Task<bool> CheckPreviewAuthorityBeforeReadAsync(PreviewSessionOwner owner)
    {
        try
        {
            if (!await CheckPreviewAuthorityAsync(owner).ConfigureAwait(false))
            {
                // CheckPreviewAuthorityAsync records the authoritative access
                // reason and schedules the single exit worker.  Do not replace
                // that reason with a reader-local generic failure.
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (owner.ExitCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RequestPreviewReaderFailure(owner, SafePreviewExecutionReason(exception));
            return false;
        }
    }

    private static string PreviewResultReason<T>(PreviewDeviceCallResult<T> result,
        string fallback) where T : class
    {
        if (result.Result is CameraPreviewConfigurationResult configuration &&
            !configuration.Succeeded)
            return SafePreviewReason(configuration.ReasonCode);
        if (result.Result is CameraPreviewFrameResult frame && !frame.Succeeded)
            return SafePreviewReason(frame.ReasonCode);
        if (result.Result is CameraOperationResult operation && !operation.Succeeded)
            return SafePreviewReason(operation.ReasonCode);
        return SafePreviewReason(string.IsNullOrWhiteSpace(result.ReasonCode)
            ? fallback : result.ReasonCode);
    }

    private static bool IsTransientPreviewReadFailure(string? reason) => reason is
        "VirtualCameraPreviewNoNewFrame" or "VirtualCameraPreviewFrameNotReady" or
        "PreviewFrameNotReady" or "PreviewNoNewFrame";

    private static string SafePreviewReason(string? reason) =>
        reason is { Length: > 0 and <= 128 } && reason.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                >= '0' and <= '9' or '_' or '-')
            ? reason : "PreviewExecutionFailed";

    private static string SafePreviewExecutionReason(Exception exception) =>
        exception switch
        {
            TimeoutException => "PreviewOperationTimeout",
            OperationCanceledException => "PreviewOperationCancelled",
            _ => "PreviewExecutionFailed"
        };

    private sealed class PreviewExecutionState
    {
        internal bool StartAttempted { get; set; }
        internal bool PreviewStarted { get; set; }
        internal bool FailureRequested { get; set; }
        internal string? FailureReason { get; set; }
        internal bool ReaderStarting { get; set; }
        internal Task? RestoreTask { get; set; }
    }
}
