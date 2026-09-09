using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Cameras;

/// <summary>
/// Process-restart recovery for a durable recipe activation admission.  This
/// path is deliberately separate from the in-process activation lease: after a
/// restart there is no in-memory HardwareTouched bit that could make a closed
/// projection proof of a physical close.
/// </summary>
internal sealed partial class CameraSetupRuntime
{
    private const string RecoveryRestored = "CameraActivationRestored";
    private const string RecoveryNoPreviousBaselineClosed = "NoPreviousBaselineClosed";
    private const string RecoveryAdmissionRequired = "CameraActivationAdmissionRequired";
    private const string RecoveryReleaseBindingMismatch = "CameraActivationReleaseBindingMismatch";
    private const string RecoveryPreviousSnapshotMismatch =
        "CameraActivationPreviousSnapshotMismatch";
    private const string RecoveryPreviousBaselineMismatch =
        "CameraActivationPreviousBaselineMismatch";
    private const string RecoveryBindingMissing = "CameraBindingMissing";
    private const string RecoveryDeviceOpenFailed = "CameraDeviceOpenFailed";
    private const string RecoveryDeviceCleanupRequired = "CameraDeviceCleanupRequired";
    private const string RecoveryUnknown = "CameraActivationRecoveryFailed";

    /// <summary>
    /// Reconciles one admitted activation after a process restart.  The caller
    /// supplies records read from the verified durable store; this method still
    /// repeats the identity checks before it performs any physical I/O.
    /// </summary>
    internal async ValueTask<RecipeActivationCameraRestoreResult>
        RecoverRecipeActivationAfterRestartAsync(RecipeActivationRecord admitted,
            RecipeReleaseRecord candidateRelease, RecipeActivationSnapshot? previousSnapshot,
            CancellationToken cancellationToken = default)
    {
        if (!TryValidateRecoveryInputs(admitted, candidateRelease, previousSnapshot,
                out var candidateRole, out var candidateCamera, out var validationReason))
            return RecoveryFailure(candidateRole, previousSnapshot?.CameraSetup,
                candidateCamera?.Camera, candidateCamera?.CameraProviderExtension,
                validationReason, hardwareTouched: false);
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
                return RecoveryFailure(candidateRole, previousSnapshot?.CameraSetup,
                    candidateCamera!.Camera, candidateCamera.CameraProviderExtension,
                    "CameraActivationBusy", hardwareTouched: false);

            lock (_stateSync)
            {
                if (_disposed || _lifetime.IsCancellationRequested)
                    return RecoveryFailure(candidateRole, previousSnapshot?.CameraSetup,
                        candidateCamera!.Camera, candidateCamera.CameraProviderExtension,
                        "RuntimeStopped", hardwareTouched: false);
            }

            // Once this recovery owns the gate, provider cleanup must not be
            // cancelled by the caller or by a normal command lifetime.  The
            // bounded token is disposed only after all synchronous recovery
            // work has returned; late provider work remains tracked by the
            // existing physical ownership helpers.
            using var operation = new CancellationTokenSource(
                PositiveTimeout(_options.OperationTimeout));

            var networkBarrier = await CheckNetworkBarrierAsync(operation.Token)
                .ConfigureAwait(false);
            if (networkBarrier is not null)
                return RecoveryFailure(candidateRole, previousSnapshot?.CameraSetup,
                    candidateCamera!.Camera, candidateCamera.CameraProviderExtension,
                    networkBarrier, hardwareTouched: false);

            var persisted = await LoadPersistedAsync(candidateRole, operation.Token)
                .ConfigureAwait(false);
            if (!persisted.Succeeded)
                return RecoveryFailure(candidateRole, previousSnapshot?.CameraSetup,
                    candidateCamera!.Camera, candidateCamera.CameraProviderExtension,
                    persisted.ReasonCode, hardwareTouched: false);
            if (persisted.Pending)
                return RecoveryFailure(candidateRole, previousSnapshot?.CameraSetup,
                    candidateCamera!.Camera, candidateCamera.CameraProviderExtension,
                    "CameraSetupOperationPending", hardwareTouched: false);

            var current = GetSlot(candidateRole)?.Snapshot;
            if (current?.Binding is null)
                return RecoveryFailure(candidateRole, previousSnapshot?.CameraSetup,
                    candidateCamera!.Camera, candidateCamera.CameraProviderExtension,
                    RecoveryBindingMissing, hardwareTouched: false);

            if (previousSnapshot is null)
                return await RecoverWithoutPreviousBaselineAsync(current, candidateCamera!,
                    operation.Token).ConfigureAwait(false);

            return await RecoverPreviousBaselineAsync(current, previousSnapshot.CameraSetup,
                candidateCamera!, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // No caller token is passed to physical calls after the gate is
            // acquired, but retain a stable result if cancellation won the
            // gate race or a future helper reintroduces that boundary.
            return RecoveryFailure(candidateRole, previousSnapshot?.CameraSetup,
                candidateCamera!.Camera, candidateCamera.CameraProviderExtension,
                "CameraOperationCancelled", hardwareTouched: false);
        }
        catch (OperationCanceledException)
        {
            return RecoveryFailure(candidateRole, previousSnapshot?.CameraSetup,
                candidateCamera!.Camera, candidateCamera.CameraProviderExtension,
                "CameraOperationTimeout", hardwareTouched: true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return RecoveryFailure(candidateRole, previousSnapshot?.CameraSetup,
                candidateCamera!.Camera, candidateCamera.CameraProviderExtension,
                RecoveryUnknown, hardwareTouched: true);
        }
        finally
        {
            if (gateAcquired)
                _operationGate.Release();
            if (drain is not null)
                CompleteInFlight(drain);
        }
    }

    private bool TryValidateRecoveryInputs(RecipeActivationRecord? admitted,
        RecipeReleaseRecord? candidateRelease, RecipeActivationSnapshot? previousSnapshot,
        out string candidateRole, out RecipeDraftContent? candidateCamera,
        out string reason)
    {
        candidateRole = string.Empty;
        candidateCamera = null;
        reason = RecoveryAdmissionRequired;
        if (admitted is null || candidateRelease is null ||
            admitted.Outcome.State != RecipeActivationOutcomeState.Admitted ||
            admitted.Admission is null)
            return false;
        if (admitted.Candidate != candidateRelease.Recipe ||
            admitted.ReleaseId != candidateRelease.ReleaseId ||
            !string.Equals(admitted.ReleaseRecordContentHash, candidateRelease.ContentHash,
                StringComparison.Ordinal))
        {
            reason = RecoveryReleaseBindingMismatch;
            return false;
        }

        candidateCamera = candidateRelease.Source.Content;
        candidateRole = candidateCamera.CameraRole;
        if (!ValidLogicalRole(candidateRole))
        {
            reason = "CameraLogicalRoleInvalid";
            return false;
        }

        var admission = admitted.Admission;
        if (previousSnapshot is null)
        {
            if (admission.PreviousActivation is not null || admission.PreviousRecipe is not null ||
                admission.PreviousSnapshotContentHash is not null ||
                admitted.PreviousActivation is not null || admitted.PreviousRecipe is not null ||
                admitted.PreviousSnapshotContentHash is not null)
            {
                reason = RecoveryPreviousBaselineMismatch;
                return false;
            }
            return true;
        }

        if (admission.PreviousActivation is null || admission.PreviousRecipe is null ||
            admission.PreviousSnapshotContentHash is null || admitted.PreviousActivation is null ||
            admitted.PreviousRecipe is null || admitted.PreviousSnapshotContentHash is null ||
            !string.Equals(previousSnapshot.ContentHash, admitted.PreviousSnapshotContentHash,
                StringComparison.Ordinal) || previousSnapshot.Recipe != admitted.PreviousRecipe)
        {
            reason = RecoveryPreviousSnapshotMismatch;
            return false;
        }
        var previousCamera = previousSnapshot.CameraSetup;
        if (previousCamera.Binding is null || previousCamera.Requested is null ||
            previousCamera.Effective is null ||
            !IsActivationConfiguredHealth(previousCamera.Health) ||
            !ValidLogicalRole(previousCamera.LogicalRole))
        {
            reason = RecoveryPreviousSnapshotMismatch;
            return false;
        }
        return true;
    }

    private async ValueTask<RecipeActivationCameraRestoreResult>
        RecoverWithoutPreviousBaselineAsync(CameraSetupSnapshot current,
            RecipeDraftContent candidate, CancellationToken operationToken)
    {
        var role = current.LogicalRole;
        var hardwareTouched = false;

        // A live slot device can survive a caller's restart/re-entry race. Close
        // it before opening the exact persisted binding, so an in-memory null is
        // never treated as evidence that the physical device is closed.
        if (GetSlot(role)?.Device is not null)
        {
            hardwareTouched = true;
            var existing = await CloseActivationSlotDeviceAsync(role).ConfigureAwait(false);
            if (!existing.Closed)
                return RecoveryFailure(role, current, candidate.Camera,
                    candidate.CameraProviderExtension, RecoveryDeviceCleanupRequired,
                    hardwareTouched);
        }

        hardwareTouched = true;
        var opened = await OpenActivationExactAsync(current.Binding!.Target, operationToken)
            .ConfigureAwait(false);
        if (opened is null)
            return RecoveryFailure(role, current, candidate.Camera,
                candidate.CameraProviderExtension, RecoveryDeviceOpenFailed, hardwareTouched);
        await using var ownership = new ColdActivationDeviceOwner(this, role, current, opened);

        var initial = await ReadActivationHealthBoundedAsync(opened,
            CancellationToken.None, operationToken).ConfigureAwait(false);
        if (initial.DeviceTransferred)
        {
            ownership.Relinquish();
            return RecoveryFailure(role, current, candidate.Camera,
                candidate.CameraProviderExtension,
                initial.ReasonCode ?? "CameraHealthUnavailable", hardwareTouched);
        }
        if (!IsActivationInitialHealth(initial.Health))
        {
            var invalid = await StopAndDisposeActivationDeviceAsync(ownership.Take())
                .ConfigureAwait(false);
            opened = null;
            return RecoveryFailure(role, current, candidate.Camera,
                candidate.CameraProviderExtension,
                invalid.Closed ? "CameraDeviceStateInvalid" : RecoveryDeviceCleanupRequired,
                hardwareTouched);
        }

        var closed = await StopAndDisposeActivationDeviceAsync(ownership.Take())
            .ConfigureAwait(false);
        opened = null;
        if (!closed.Closed)
            return RecoveryFailure(role, current, candidate.Camera,
                candidate.CameraProviderExtension, RecoveryDeviceCleanupRequired,
                hardwareTouched);

        var safeClosed = PublishActivationSafeClosed(role, current,
            RecoveryNoPreviousBaselineClosed);
        if (safeClosed is null)
            return RecoveryFailure(role, current, candidate.Camera,
                candidate.CameraProviderExtension, RecoveryUnknown, hardwareTouched);
        ClearActivationRestorationBlocked(role);
        return new(true, RecoveryNoPreviousBaselineClosed, safeClosed, hardwareTouched);
    }

    private async ValueTask<RecipeActivationCameraRestoreResult>
        RecoverPreviousBaselineAsync(CameraSetupSnapshot current,
            CameraSetupSnapshot previous, RecipeDraftContent candidate,
            CancellationToken operationToken)
    {
        var candidateRole = current.LogicalRole;
        var previousRole = previous.LogicalRole;
        var hardwareTouched = false;
        var mustCloseCandidate = !string.Equals(candidateRole, previousRole,
                StringComparison.Ordinal) || !Equals(current.Binding, previous.Binding);

        if (mustCloseCandidate)
        {
            if (GetSlot(candidateRole)?.Device is not null)
            {
                hardwareTouched = true;
                var existing = await CloseActivationSlotDeviceAsync(candidateRole)
                    .ConfigureAwait(false);
                if (!existing.Closed)
                    return RecoveryFailure(candidateRole, current, candidate.Camera,
                        candidate.CameraProviderExtension, RecoveryDeviceCleanupRequired,
                        hardwareTouched);
            }

            // The durable projection says only what was last committed. Open the
            // candidate binding and close it to prove the physical state before
            // changing roles or restoring the previous active camera.
            hardwareTouched = true;
            var candidateDevice = await OpenActivationExactAsync(current.Binding!.Target,
                operationToken).ConfigureAwait(false);
            if (candidateDevice is null)
                return RecoveryFailure(candidateRole, current, candidate.Camera,
                    candidate.CameraProviderExtension, RecoveryDeviceOpenFailed,
                    hardwareTouched);
            await using var candidateOwnership = new ColdActivationDeviceOwner(this, candidateRole, current, candidateDevice);
            var candidateHealth = await ReadActivationHealthBoundedAsync(candidateDevice,
                CancellationToken.None, operationToken).ConfigureAwait(false);
            if (candidateHealth.DeviceTransferred)
            {
                candidateOwnership.Relinquish();
                return RecoveryFailure(candidateRole, current, candidate.Camera,
                    candidate.CameraProviderExtension,
                    candidateHealth.ReasonCode ?? "CameraHealthUnavailable", hardwareTouched);
            }
            var candidateClosed = await StopAndDisposeActivationDeviceAsync(candidateOwnership.Take())
                .ConfigureAwait(false);
            candidateDevice = null;
            if (!candidateClosed.Closed)
                return RecoveryFailure(candidateRole, current, candidate.Camera,
                    candidate.CameraProviderExtension, RecoveryDeviceCleanupRequired,
                    hardwareTouched);
            if (PublishActivationSafeClosed(candidateRole, current,
                    RecoveryNoPreviousBaselineClosed) is null)
                return RecoveryFailure(candidateRole, current, candidate.Camera,
                    candidate.CameraProviderExtension, RecoveryUnknown, hardwareTouched);
        }
        else if (GetSlot(previousRole)?.Device is not null)
        {
            hardwareTouched = true;
            var existing = await CloseActivationSlotDeviceAsync(previousRole)
                .ConfigureAwait(false);
            if (!existing.Closed)
                return RecoveryFailure(previousRole, previous, previous.Requested,
                    previous.Extension, RecoveryDeviceCleanupRequired, hardwareTouched);
        }

        if (!string.Equals(candidateRole, previousRole, StringComparison.Ordinal))
        {
            var previousPersisted = await LoadPersistedAsync(previousRole, operationToken)
                .ConfigureAwait(false);
            if (!previousPersisted.Succeeded || previousPersisted.Pending)
                return RecoveryFailure(previousRole, previous, previous.Requested,
                    previous.Extension,
                    previousPersisted.Pending ? "CameraSetupOperationPending" :
                        previousPersisted.ReasonCode, hardwareTouched);
            var loadedPrevious = GetSlot(previousRole)?.Snapshot;
            if (loadedPrevious?.Binding is null ||
                !Equals(loadedPrevious.Binding, previous.Binding))
                return RecoveryFailure(previousRole, previous, previous.Requested,
                    previous.Extension, RecoveryPreviousSnapshotMismatch, hardwareTouched);
        }

        hardwareTouched = true;
        var generation = BeginActivationGeneration(previousRole);
        var opened = await OpenActivationExactAsync(previous.Binding!.Target, operationToken)
            .ConfigureAwait(false);
        if (opened is null)
            return RecoveryFailure(previousRole, previous, previous.Requested,
                previous.Extension, RecoveryDeviceOpenFailed, hardwareTouched);
        await using var ownership = new ColdActivationDeviceOwner(this, previousRole, previous, opened);

        var initial = await ReadActivationHealthBoundedAsync(opened,
            CancellationToken.None, operationToken).ConfigureAwait(false);
        if (initial.DeviceTransferred)
        {
            ownership.Relinquish();
            return RecoveryFailure(previousRole, previous, previous.Requested,
                previous.Extension, initial.ReasonCode ?? "CameraHealthUnavailable",
                hardwareTouched);
        }
        if (!IsActivationInitialHealth(initial.Health))
            return await FailColdOpenedDeviceAsync(previousRole, previous, ownership.Take(),
                "CameraDeviceStateInvalid", hardwareTouched).ConfigureAwait(false);

        CameraConfigurationResult expected;
        try
        {
            expected = opened.Capabilities.ValidateConfiguration(previous.Requested!);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            expected = CameraConfigurationResult.Failure(RecoveryPreviousSnapshotMismatch);
        }
        if (!expected.Succeeded || expected.Effective is null ||
            !expected.Effective.Equals(previous.Effective))
            return await FailColdOpenedDeviceAsync(previousRole, previous, ownership.Take(),
                RecoveryPreviousSnapshotMismatch, hardwareTouched).ConfigureAwait(false);
        if (previous.Extension is not null)
            return await FailColdOpenedDeviceAsync(previousRole, previous, ownership.Take(),
                "CameraProviderExtensionUnavailable", hardwareTouched).ConfigureAwait(false);

        var applied = await ApplyActivationConfigurationBoundedAsync(opened,
            previous.Requested!, CancellationToken.None, operationToken).ConfigureAwait(false);
        if (applied.DeviceTransferred)
        {
            ownership.Relinquish();
            return RecoveryFailure(previousRole, previous, previous.Requested,
                previous.Extension, applied.Result.ReasonCode, hardwareTouched);
        }
        if (!applied.Result.Succeeded)
            return await FailColdOpenedDeviceAsync(previousRole, previous, ownership.Take(),
                applied.Result.ReasonCode, hardwareTouched).ConfigureAwait(false);

        CameraConfigurationResult readBack;
        try
        {
            readBack = opened.Capabilities.ValidateReadBack(previous.Requested!, applied.Result);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            readBack = CameraConfigurationResult.Failure("CameraReadBackInvalid");
        }
        if (!readBack.Succeeded || readBack.Effective is null ||
            !readBack.Effective.Equals(previous.Effective))
            return await FailColdOpenedDeviceAsync(previousRole, previous, ownership.Take(),
                "CameraActivationBaselineReadBackMismatch", hardwareTouched)
                .ConfigureAwait(false);

        var configured = await ReadActivationHealthBoundedAsync(opened,
            CancellationToken.None, operationToken).ConfigureAwait(false);
        if (configured.DeviceTransferred)
        {
            ownership.Relinquish();
            return RecoveryFailure(previousRole, previous, previous.Requested,
                previous.Extension, configured.ReasonCode ?? "CameraHealthUnavailable",
                hardwareTouched);
        }
        if (!IsActivationConfiguredHealth(configured.Health))
            return await FailColdOpenedDeviceAsync(previousRole, previous, ownership.Take(),
                configured.ReasonCode ?? "CameraConfigurationStateInvalid", hardwareTouched)
                .ConfigureAwait(false);

        var restored = new CameraSetupSnapshot(previousRole, previous.Binding,
            configured.Health!, previous.Requested, readBack.Effective, readBack.Differences,
            previous.Extension, RecoveryRestored, opened.Capabilities);
        var installed = InstallActivationDevice(previousRole, generation, opened, restored);
        if (!installed.Succeeded)
            return await FailColdOpenedDeviceAsync(previousRole, previous, ownership.Take(),
                "CameraActivationGenerationConflict", hardwareTouched).ConfigureAwait(false);

        opened = null;
        ownership.Relinquish();
        ClearActivationRestorationBlocked(candidateRole);
        ClearActivationRestorationBlocked(previousRole);
        PublishActivationSnapshot(previousRole, restored);
        return new(true, RecoveryRestored, restored, hardwareTouched);
    }

    private async ValueTask<RecipeActivationCameraRestoreResult>
        FailColdOpenedDeviceAsync(string role, CameraSetupSnapshot baseline,
            ICameraDevice opened, string reason, bool hardwareTouched)
    {
        var closed = await StopAndDisposeActivationDeviceAsync(opened).ConfigureAwait(false);
        return RecoveryFailure(role, baseline, baseline.Requested, baseline.Extension,
            closed.Closed ? reason : RecoveryDeviceCleanupRequired, hardwareTouched);
    }

    private RecipeActivationCameraRestoreResult RecoveryFailure(string role,
        CameraSetupSnapshot? baseline, RequestedCameraConfiguration? requested,
        CameraProviderExtensionRequirement? extension, string? reason, bool hardwareTouched)
    {
        var safe = SafeReason(reason);
        var publishBaseline = baseline is not null &&
            string.Equals(baseline.LogicalRole, role, StringComparison.Ordinal)
            ? baseline : GetSlot(role)?.Snapshot;
        MarkActivationRestorationBlocked(role);
        PublishActivationFailure(role, publishBaseline, requested, extension, safe,
            publishBaseline?.Capabilities);
        return new(false, safe, null, hardwareTouched);
    }

    private sealed class ColdActivationDeviceOwner : IAsyncDisposable
    {
        private readonly CameraSetupRuntime _owner;
        private readonly string _role;
        private readonly CameraSetupSnapshot _baseline;
        private ICameraDevice? _device;
        internal ColdActivationDeviceOwner(CameraSetupRuntime owner, string role,
            CameraSetupSnapshot baseline, ICameraDevice device)
        { _owner = owner; _role = role; _baseline = baseline; _device = device; }
        internal ICameraDevice Take() => Interlocked.Exchange(ref _device, null) ??
            throw new InvalidOperationException("CameraActivationDeviceOwnershipLost");
        internal void Relinquish() => _device = null;
        public async ValueTask DisposeAsync()
        {
            var abandoned = Interlocked.Exchange(ref _device, null);
            if (abandoned is null) return;
            var closed = await _owner.StopAndDisposeActivationDeviceAsync(abandoned).ConfigureAwait(false);
            _owner.MarkActivationRestorationBlocked(_role);
            _owner.PublishActivationFailure(_role, _baseline, _baseline.Requested, _baseline.Extension,
                closed.Closed ? RecoveryUnknown : RecoveryDeviceCleanupRequired);
        }
    }
}
