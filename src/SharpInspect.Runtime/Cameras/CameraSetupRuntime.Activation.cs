using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;

namespace SharpInspect.Runtime.Cameras;

/// <summary>
/// Internal camera ownership used by the recipe activation coordinator.  The
/// activation command has already crossed the station authorization boundary;
/// this seam owns only the physical camera transaction and never changes
/// ActiveRecipe, Ready, or the production acquisition path.
/// </summary>
internal sealed partial class CameraSetupRuntime
{
    private const string RecipeActivationLeaseUnavailable = "CameraActivationLeaseUnavailable";
    private readonly HashSet<string> _activationRestorationBlockedRoles =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Reserves the camera operation gate for one already-authorized activation.
    /// The durable camera projection and the network reconciliation barrier are
    /// checked before the lease is returned.  Authorization is deliberately
    /// absent here so this path cannot accidentally require ManageCameraBindings
    /// or reuse the debug camera command.
    /// </summary>
    internal async ValueTask<RecipeActivationCameraLease> ReserveRecipeActivationAsync(
        string logicalRole, CancellationToken cancellationToken = default)
    {
        if (!ValidLogicalRole(logicalRole))
            return RecipeActivationCameraLease.Unavailable(this, logicalRole,
                "CameraLogicalRoleInvalid");
        if (cancellationToken.IsCancellationRequested)
            return RecipeActivationCameraLease.Unavailable(this, logicalRole,
                "CameraOperationCancelled");

        TaskCompletionSource<bool>? drain = null;
        var gateAcquired = false;
        try
        {
            drain = RegisterInFlight();
            gateAcquired = await WaitForOperationGateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!gateAcquired)
                return RecipeActivationCameraLease.Unavailable(this, logicalRole,
                    "CameraActivationBusy");

            using var operation = CreateOperationToken(cancellationToken);
            var networkBarrier = await CheckNetworkBarrierAsync(operation.Token)
                .ConfigureAwait(false);
            if (networkBarrier is not null)
                return RecipeActivationCameraLease.Unavailable(this, logicalRole,
                    networkBarrier);

            var persisted = await LoadPersistedAsync(logicalRole, operation.Token)
                .ConfigureAwait(false);
            if (!persisted.Succeeded)
                return RecipeActivationCameraLease.Unavailable(this, logicalRole,
                    persisted.ReasonCode);
            if (persisted.Pending)
                return RecipeActivationCameraLease.Unavailable(this, logicalRole,
                    "CameraSetupOperationPending");

            CameraSetupSnapshot? snapshot;
            lock (_stateSync)
            {
                if (_disposed || _lifetime.IsCancellationRequested)
                    return RecipeActivationCameraLease.Unavailable(this, logicalRole,
                        "RuntimeStopped");
                if (_activationRestorationBlockedRoles.Contains(logicalRole))
                    return RecipeActivationCameraLease.Unavailable(this, logicalRole,
                        "CameraActivationRestorationRequired");
                if (_retainedDevices.Count != 0 || _retiringDevices.Count != 0 ||
                    _physicalInFlight.Count != 0)
                    return RecipeActivationCameraLease.Unavailable(this, logicalRole,
                        "CameraDeviceCleanupRequired");
                snapshot = _slots.TryGetValue(logicalRole, out var slot)
                    ? slot.Snapshot : null;
                if (snapshot?.Binding is null)
                    return RecipeActivationCameraLease.Unavailable(this, logicalRole,
                        "CameraBindingMissing");
            }

            // Transfer ownership of the gate and in-flight marker to the lease.
            // The finally block releases them only for failed reservations.
            var lease = RecipeActivationCameraLease.Create(this, logicalRole,
                snapshot, drain!);
            gateAcquired = false;
            drain = null;
            return lease;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RecipeActivationCameraLease.Unavailable(this, logicalRole,
                "CameraOperationCancelled");
        }
        catch (OperationCanceledException)
        {
            return RecipeActivationCameraLease.Unavailable(this, logicalRole,
                "CameraOperationTimeout");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return RecipeActivationCameraLease.Unavailable(this, logicalRole,
                RecipeActivationLeaseUnavailable);
        }
        finally
        {
            if (gateAcquired)
                _operationGate.Release();
            if (drain is not null)
                CompleteInFlight(drain);
        }
    }

    // These narrow wrappers keep the top-level lease from depending on private
    // implementation types while allowing it to reuse the existing bounded
    // provider and physical ownership helpers.
    internal CancellationTokenSource CreateActivationOperationToken(CancellationToken caller) =>
        CreateOperationToken(caller);

    internal CancellationTokenSource CreateActivationCleanupToken() =>
        new(PositiveTimeout(_options.OperationTimeout));

    internal long BeginActivationGeneration(string logicalRole) => BeginGeneration(logicalRole);

    internal async ValueTask<ActivationDeviceCloseResult> CloseActivationSlotDeviceAsync(
        string logicalRole)
    {
        var slot = GetSlot(logicalRole);
        if (slot is null)
            return ActivationDeviceCloseResult.ClosedResult;
        var result = await CloseSlotDeviceAsync(slot).ConfigureAwait(false);
        return result switch
        {
            DeviceCloseOutcome.Closed => ActivationDeviceCloseResult.ClosedResult,
            DeviceCloseOutcome.Retained => ActivationDeviceCloseResult.RetainedResult,
            _ => ActivationDeviceCloseResult.TransferredResult
        };
    }

    internal async ValueTask<(CameraSetupSnapshot? Snapshot, string? Failure)> LoadActivationBaselineAsync(
        CameraSetupSnapshot baseline, CancellationToken token)
    {
        var loaded = await LoadPersistedAsync(baseline.LogicalRole, token).ConfigureAwait(false);
        if (!loaded.Succeeded || loaded.Pending)
            return (null, loaded.Pending ? "CameraSetupOperationPending" : loaded.ReasonCode);
        var observed = GetSlot(baseline.LogicalRole)?.Snapshot;
        if (!IsActivationBaselineExact(baseline, observed)) return (null, "CameraActivationBaselineMismatch");
        if (IsActivationRestorationBlocked(baseline.LogicalRole) || ActivationPhysicalCleanupPending)
            return (null, "CameraDeviceCleanupRequired");
        return (observed, null);
    }

    internal async ValueTask<ActivationDeviceCloseResult> RetireActivationBaselineAsync(
        CameraSetupSnapshot baseline, CancellationToken token)
    {
        BeginActivationGeneration(baseline.LogicalRole);
        if (GetSlot(baseline.LogicalRole)?.Device is not null)
            return await CloseActivationSlotDeviceAsync(baseline.LogicalRole).ConfigureAwait(false);
        if (ActivationPhysicalCleanupPending) return ActivationDeviceCloseResult.RetainedResult;
        // After a restart, an empty slot is only a projection. Prove the old
        // physical binding is stopped and closed before opening another role.
        var opened = await OpenActivationExactAsync(baseline.Binding!.Target, token).ConfigureAwait(false);
        return opened is null ? ActivationDeviceCloseResult.RetainedResult :
            await StopAndDisposeActivationDeviceAsync(opened).ConfigureAwait(false);
    }

    internal async ValueTask<ICameraDevice?> OpenActivationExactAsync(
        CameraBindingTarget target, CancellationToken cancellationToken)
    {
        var provider = FindProvider(target.Provider);
        return provider is null
            ? null
            : await OpenExactAsync(provider, target.StableDeviceIdentity, cancellationToken)
                .ConfigureAwait(false);
    }

    internal async ValueTask<ActivationBoundedHealthResult> ReadActivationHealthBoundedAsync(
        ICameraDevice device, CancellationToken callerCancellationToken,
        CancellationToken operationCancellationToken)
    {
        var result = await ReadHealthBoundedAsync(device, callerCancellationToken,
            operationCancellationToken).ConfigureAwait(false);
        return new ActivationBoundedHealthResult(result.Health, result.ReasonCode,
            result.DeviceTransferred);
    }

    internal async ValueTask<ActivationBoundedConfigurationResult>
        ApplyActivationConfigurationBoundedAsync(ICameraDevice device,
            RequestedCameraConfiguration requested, CancellationToken callerCancellationToken,
            CancellationToken operationCancellationToken)
    {
        var result = await ApplyConfigurationBoundedAsync(device, requested,
            callerCancellationToken, operationCancellationToken).ConfigureAwait(false);
        return new ActivationBoundedConfigurationResult(result.Result,
            result.DeviceTransferred);
    }

    internal ActivationInstallResult InstallActivationDevice(string logicalRole, long generation,
        ICameraDevice device, CameraSetupSnapshot snapshot)
    {
        var replaced = InstallDevice(logicalRole, generation, device, snapshot);
        return new ActivationInstallResult(replaced is null);
    }

    internal async ValueTask<ActivationDeviceCloseResult> StopAndDisposeActivationDeviceAsync(
        ICameraDevice device)
    {
        var result = await StopAndDisposeBoundedAsync(device,
            () => ReleaseRetiringDevice(device)).ConfigureAwait(false);
        return result switch
        {
            DeviceCloseOutcome.Closed => ActivationDeviceCloseResult.ClosedResult,
            DeviceCloseOutcome.Retained => ActivationDeviceCloseResult.RetainedResult,
            _ => ActivationDeviceCloseResult.TransferredResult
        };
    }

    internal void PublishActivationSnapshot(string logicalRole, CameraSetupSnapshot snapshot) =>
        Publish(logicalRole, snapshot);

    internal void CompleteActivationInFlight(TaskCompletionSource<bool> drain) =>
        CompleteInFlight(drain);

    internal void ReleaseActivationOperationGate()
    {
        try { _operationGate.Release(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    internal bool ActivationPhysicalCleanupPending
    {
        get
        {
            lock (_stateSync)
                return _retainedDevices.Count != 0 || _retiringDevices.Count != 0 ||
                    _physicalInFlight.Count != 0;
        }
    }

    internal bool IsActivationRestorationBlocked(string logicalRole)
    {
        lock (_stateSync)
            return _activationRestorationBlockedRoles.Contains(logicalRole);
    }

    internal void MarkActivationRestorationBlocked(string logicalRole)
    {
        lock (_stateSync) _activationRestorationBlockedRoles.Add(logicalRole);
    }

    internal void ClearActivationRestorationBlocked(string logicalRole)
    {
        lock (_stateSync) _activationRestorationBlockedRoles.Remove(logicalRole);
    }

    internal void PublishActivationFailure(string logicalRole,
        CameraSetupSnapshot? baseline, RequestedCameraConfiguration? requested,
        CameraProviderExtensionRequirement? extension, string reason,
        CameraCapabilities? capabilities = null)
    {
        CameraSetupSnapshot snapshot;
        lock (_stateSync)
        {
            if (!_slots.TryGetValue(logicalRole, out var slot))
                return;
            snapshot = new CameraSetupSnapshot(logicalRole, baseline?.Binding,
                FailureHealth(reason), requested, extension: extension,
                reasonCode: SafeReason(reason), capabilities: capabilities ?? baseline?.Capabilities);
            slot.Snapshot = snapshot;
            // Publishing a failure is not evidence that a live handle was retired.
            // Keep any remaining slot owner available to restoration or shutdown.
        }
        Publish(logicalRole, snapshot);
    }

    internal CameraSetupSnapshot? PublishActivationSafeClosed(string logicalRole,
        CameraSetupSnapshot? baseline, string reason)
    {
        CameraSetupSnapshot snapshot;
        lock (_stateSync)
        {
            if (!_slots.TryGetValue(logicalRole, out var slot))
                return null;
            var health = new CameraHealthSnapshot(CameraProviderAvailability.Available,
                CameraConnectionState.Closed, CameraConfigurationState.Unconfigured,
                CameraAcquisitionState.Stopped,
                new FrameTimePoint(DateTimeOffset.UtcNow, Math.Max(0, Stopwatch.GetTimestamp())));
            snapshot = new CameraSetupSnapshot(logicalRole, baseline?.Binding, health,
                reasonCode: SafeReason(reason), capabilities: baseline?.Capabilities);
            slot.Snapshot = snapshot;
            if (slot.Device is null || (!_retainedDevices.Contains(slot.Device) &&
                !_retiringDevices.Contains(slot.Device)))
                slot.Device = null;
        }
        Publish(logicalRole, snapshot);
        return snapshot;
    }

    internal static bool IsActivationInitialHealth(CameraHealthSnapshot? health) =>
        health is not null &&
        health.ProviderAvailability == CameraProviderAvailability.Available &&
        health.Connection == CameraConnectionState.Open &&
        health.Configuration == CameraConfigurationState.Unconfigured &&
        health.Acquisition == CameraAcquisitionState.Stopped;

    internal static bool IsActivationConfiguredHealth(CameraHealthSnapshot? health) =>
        health is not null &&
        health.ProviderAvailability == CameraProviderAvailability.Available &&
        health.Connection == CameraConnectionState.Open &&
        health.Configuration == CameraConfigurationState.Applied &&
        health.Acquisition == CameraAcquisitionState.Stopped;

    internal static bool IsActivationSameProvider(CameraProviderIdentity left,
        CameraProviderIdentity right) => SameProvider(left, right);

    internal static bool IsActivationBaselineExact(CameraSetupSnapshot? expected,
        CameraSetupSnapshot? observed)
    {
        if (expected is null || observed is null || expected.Binding is null ||
            observed.Binding is null || !expected.Binding.Equals(observed.Binding))
            return false;
        return string.Equals(expected.LogicalRole, observed.LogicalRole,
                   StringComparison.Ordinal) && expected.Requested is not null &&
               expected.Effective is not null && IsActivationConfiguredHealth(expected.Health);
    }
}

internal readonly record struct ActivationDeviceCloseResult(bool Closed, bool Retained)
{
    internal static ActivationDeviceCloseResult ClosedResult => new(true, false);
    internal static ActivationDeviceCloseResult TransferredResult => new(false, false);
    internal static ActivationDeviceCloseResult RetainedResult => new(false, true);
}

internal readonly record struct ActivationBoundedHealthResult(
    CameraHealthSnapshot? Health, string? ReasonCode, bool DeviceTransferred);

internal readonly record struct ActivationBoundedConfigurationResult(
    CameraConfigurationResult Result, bool DeviceTransferred);

internal readonly record struct ActivationInstallResult(bool Succeeded);

internal sealed class RecipeActivationCameraLeaseResult
{
    internal RecipeActivationCameraLeaseResult(bool succeeded, string reasonCode,
        CameraSetupSnapshot? snapshot, bool hardwareTouched)
    {
        Succeeded = succeeded;
        ReasonCode = reasonCode ?? throw new ArgumentNullException(nameof(reasonCode));
        Snapshot = snapshot;
        HardwareTouched = hardwareTouched;
    }

    internal bool Succeeded { get; }
    internal string ReasonCode { get; }
    internal CameraSetupSnapshot? Snapshot { get; }
    internal bool HardwareTouched { get; }
}

internal sealed class RecipeActivationCameraRestoreResult
{
    internal RecipeActivationCameraRestoreResult(bool succeeded, string reasonCode,
        CameraSetupSnapshot? snapshot, bool hardwareTouched)
    {
        Succeeded = succeeded;
        ReasonCode = reasonCode ?? throw new ArgumentNullException(nameof(reasonCode));
        Snapshot = snapshot;
        HardwareTouched = hardwareTouched;
    }

    internal bool Succeeded { get; }
    internal string ReasonCode { get; }
    internal CameraSetupSnapshot? Snapshot { get; }
    internal bool HardwareTouched { get; }
}

/// <summary>
/// Exclusive camera owner for one recipe activation attempt.  The owner keeps
/// the CameraSetupRuntime operation gate and in-flight marker until this lease
/// is disposed, including after a successful commit or restoration.
/// </summary>
internal sealed class RecipeActivationCameraLease : IAsyncDisposable
{
    private const string RecipeActivationPrepared = "CameraActivationPrepared";
    private const string RecipeActivationCommitted = "CameraActivationCommitted";
    private const string RecipeActivationRestored = "CameraActivationRestored";
    private const string RecipeActivationNoPreviousBaseline = "NoPreviousBaseline";
    private const string RecipeActivationNoPreviousBaselineClosed = "NoPreviousBaselineClosed";
    private const string RecipeActivationBaselineMismatch = "CameraActivationBaselineMismatch";
    private const string RecipeActivationBaselineReadBackMismatch =
        "CameraActivationBaselineReadBackMismatch";
    private const string RecipeActivationRestoreFailed = "CameraActivationRestoreFailed";

    private readonly CameraSetupRuntime _owner;
    private readonly string _logicalRole;
    private readonly CameraSetupSnapshot? _currentSnapshot;
    private readonly TaskCompletionSource<bool>? _drain;
    private readonly SemaphoreSlim _leaseGate = new(1, 1);
    private int _gateHeld;
    private int _released;
    private int _hardwareTouched;
    private bool _available;
    private bool _disposed;
    private bool _committed;
    private bool _candidatePrepared;
    private bool _restoreSucceeded;
    private bool _safeClosed;
    private ICameraDevice? _candidateDevice;
    private CameraSetupSnapshot? _candidateSnapshot;
    private CameraSetupSnapshot? _restoredSnapshot;
    private long _candidateGeneration;
    private RecipeActivationCameraRestoreResult? _lastRestore;
    private CameraSetupSnapshot? _previousRoleSnapshot;

    private RecipeActivationCameraLease(CameraSetupRuntime owner, string logicalRole,
        CameraSetupSnapshot? currentSnapshot, TaskCompletionSource<bool>? drain,
        bool available, string reasonCode, bool gateHeld)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _logicalRole = logicalRole ?? string.Empty;
        _currentSnapshot = currentSnapshot;
        _drain = drain;
        _available = available;
        ReasonCode = reasonCode ?? throw new ArgumentNullException(nameof(reasonCode));
        if (gateHeld) Interlocked.Exchange(ref _gateHeld, 1);
    }

    internal bool Available => _available;
    internal string ReasonCode { get; private set; }
    internal CameraSetupSnapshot? CurrentSnapshot =>
        _restoredSnapshot ?? _candidateSnapshot ?? _currentSnapshot;
    internal CameraSetupSnapshot? CandidateSnapshot => _candidateSnapshot;
    internal bool HardwareTouched => Volatile.Read(ref _hardwareTouched) != 0;
    internal bool Committed => _committed;

    internal static RecipeActivationCameraLease Create(CameraSetupRuntime owner,
        string logicalRole, CameraSetupSnapshot? snapshot,
        TaskCompletionSource<bool> drain) =>
        new(owner, logicalRole, snapshot, drain, true, "CameraActivationReserved", true);

    internal static RecipeActivationCameraLease Unavailable(CameraSetupRuntime owner,
        string? logicalRole, string reason) =>
        new(owner, logicalRole ?? string.Empty, null, null, false,
            SafeUnavailableReason(reason), false);

    private static string SafeUnavailableReason(string? reason) =>
        reason is { Length: > 0 and <= 128 } && reason.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
            >= '0' and <= '9' or '_' or '-') ? reason : "CameraActivationLeaseUnavailable";

    private void MarkHardwareTouched() => Interlocked.Exchange(ref _hardwareTouched, 1);

    private static RecipeActivationPhysicalPhaseClaim? BeginPhysicalPhase(
        Func<RecipeActivationPhysicalPhaseClaim>? factory, out string? failure)
    {
        failure = null;
        if (factory is null) return null;
        var claim = factory();
        if (claim.Available) return claim;
        failure = claim.Failure ?? "RecipeActivationCancelled";
        claim.Dispose();
        return null;
    }

    internal async ValueTask<RecipeActivationCameraLeaseResult> ApplyAsync(
        RequestedCameraConfiguration requested,
        CameraProviderExtensionRequirement? extension = null,
        CancellationToken cancellationToken = default,
        CameraSetupSnapshot? durableBaseline = null,
        Func<RecipeActivationPhysicalPhaseClaim>? physicalPhaseFactory = null)
    {
        if (requested is null)
            return new(false, "CameraConfigurationRequired", null, HardwareTouched);
        if (!Available)
            return new(false, ReasonCode, null, HardwareTouched);
        if (cancellationToken.IsCancellationRequested)
            return new(false, "CameraOperationCancelled", null, HardwareTouched);

        await _leaseGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return new(false, "CameraActivationLeaseDisposed", null, HardwareTouched);
            if (_committed) return new(false, RecipeActivationCommitted, null, HardwareTouched);
            if (_safeClosed)
                return new(false, RecipeActivationNoPreviousBaselineClosed,
                    _restoredSnapshot, HardwareTouched);
            if (_restoreSucceeded)
                return new(false, RecipeActivationRestored, _restoredSnapshot, HardwareTouched);
            if (_candidatePrepared)
                return new(false, "CameraActivationAlreadyPrepared", _candidateSnapshot,
                    HardwareTouched);
            if (_currentSnapshot?.Binding is not { } binding)
                return new(false, "CameraBindingMissing", null, HardwareTouched);
            if (extension is not null)
            {
                if (!CameraSetupRuntime.IsActivationSameProvider(extension.Provider,
                        binding.Target.Provider))
                    return new(false, "CameraExtensionBindingMismatch", null, HardwareTouched);
                return new(false, "CameraProviderExtensionUnavailable", null, HardwareTouched);
            }

            using var operation = _owner.CreateActivationOperationToken(cancellationToken);
            if (durableBaseline is not null && durableBaseline.LogicalRole != _logicalRole)
            {
                var prior = await _owner.LoadActivationBaselineAsync(durableBaseline, operation.Token).ConfigureAwait(false);
                if (prior.Failure is not null) return new(false, prior.Failure, null, HardwareTouched);
                _previousRoleSnapshot = prior.Snapshot;
                ActivationDeviceCloseResult retired;
                using (var physicalPhase = BeginPhysicalPhase(physicalPhaseFactory,
                    out var retirePhaseFailure))
                {
                    if (retirePhaseFailure is not null)
                        return await FailApplyAsync(requested, extension, retirePhaseFailure)
                            .ConfigureAwait(false);
                    MarkHardwareTouched();
                    retired = await _owner.RetireActivationBaselineAsync(durableBaseline,
                        operation.Token).ConfigureAwait(false);
                }
                if (!retired.Closed || _owner.ActivationPhysicalCleanupPending)
                    return await FailApplyAsync(requested, extension, "CameraDeviceCleanupRequired").ConfigureAwait(false);
                if (_owner.PublishActivationSafeClosed(durableBaseline.LogicalRole, prior.Snapshot,
                    "CameraActivationPreviousRoleClosed") is null)
                    return await FailApplyAsync(requested, extension, "CameraActivationPreviousRoleCloseUnverified").ConfigureAwait(false);
            }
            var generation = _owner.BeginActivationGeneration(_logicalRole);
            // Closing the previous owner is a physical mutation and must be
            // visible to the caller before the first provider call starts.
            ActivationDeviceCloseResult close;
            using (var physicalPhase = BeginPhysicalPhase(physicalPhaseFactory,
                out var closePhaseFailure))
            {
                if (closePhaseFailure is not null)
                    return await FailApplyAsync(requested, extension, closePhaseFailure)
                        .ConfigureAwait(false);
                MarkHardwareTouched();
                close = await _owner.CloseActivationSlotDeviceAsync(_logicalRole)
                    .ConfigureAwait(false);
            }
            if (!close.Closed)
                return await FailApplyAsync(requested, extension,
                    close.Retained ? "CameraDeviceCleanupRequired" : "CameraOperationTimeout")
                    .ConfigureAwait(false);

            ICameraDevice? opened = null;
            try
            {
                using (var physicalPhase = BeginPhysicalPhase(physicalPhaseFactory,
                    out var openPhaseFailure))
                {
                    if (openPhaseFailure is not null)
                        return await FailApplyAsync(requested, extension, openPhaseFailure)
                            .ConfigureAwait(false);
                    opened = await _owner.OpenActivationExactAsync(binding.Target,
                        operation.Token).ConfigureAwait(false);
                }
                if (opened is null)
                    return await FailApplyAsync(requested, extension,
                        "CameraDeviceOpenFailed").ConfigureAwait(false);

                ActivationBoundedHealthResult initial;
                using (var physicalPhase = BeginPhysicalPhase(physicalPhaseFactory,
                    out var initialHealthPhaseFailure))
                {
                    if (initialHealthPhaseFailure is not null)
                        return await FailApplyAsync(requested, extension, initialHealthPhaseFailure, opened)
                            .ConfigureAwait(false);
                    initial = await _owner.ReadActivationHealthBoundedAsync(opened,
                        cancellationToken, operation.Token).ConfigureAwait(false);
                }
                if (initial.DeviceTransferred)
                {
                    opened = null;
                    return await FailApplyAsync(requested, extension,
                        initial.ReasonCode ?? "CameraHealthUnavailable").ConfigureAwait(false);
                }
                if (!CameraSetupRuntime.IsActivationInitialHealth(initial.Health))
                    return await FailApplyAsync(requested, extension,
                        initial.ReasonCode ?? "CameraDeviceStateInvalid", opened)
                        .ConfigureAwait(false);

                CameraConfigurationResult expected;
                try { expected = opened.Capabilities.ValidateConfiguration(requested); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { expected = CameraConfigurationResult.Failure("CameraConfigurationInvalid"); }
                if (!expected.Succeeded || expected.Effective is null)
                    return await FailApplyAsync(requested, extension, expected.ReasonCode, opened)
                        .ConfigureAwait(false);

                ActivationBoundedConfigurationResult applied;
                using (var physicalPhase = BeginPhysicalPhase(physicalPhaseFactory,
                    out var applyPhaseFailure))
                {
                    if (applyPhaseFailure is not null)
                        return await FailApplyAsync(requested, extension, applyPhaseFailure, opened)
                            .ConfigureAwait(false);
                    applied = await _owner.ApplyActivationConfigurationBoundedAsync(opened,
                        requested, cancellationToken, operation.Token).ConfigureAwait(false);
                }
                if (applied.DeviceTransferred)
                {
                    opened = null;
                    return await FailApplyAsync(requested, extension,
                        applied.Result.ReasonCode).ConfigureAwait(false);
                }
                if (!applied.Result.Succeeded)
                    return await FailApplyAsync(requested, extension,
                        applied.Result.ReasonCode, opened).ConfigureAwait(false);

                CameraConfigurationResult readBack;
                try { readBack = opened.Capabilities.ValidateReadBack(requested, applied.Result); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { readBack = CameraConfigurationResult.Failure("CameraReadBackInvalid"); }
                if (!readBack.Succeeded || readBack.Effective is null)
                    return await FailApplyAsync(requested, extension, readBack.ReasonCode, opened)
                        .ConfigureAwait(false);

                ActivationBoundedHealthResult configured;
                using (var physicalPhase = BeginPhysicalPhase(physicalPhaseFactory,
                    out var configuredHealthPhaseFailure))
                {
                    if (configuredHealthPhaseFailure is not null)
                        return await FailApplyAsync(requested, extension, configuredHealthPhaseFailure, opened)
                            .ConfigureAwait(false);
                    configured = await _owner.ReadActivationHealthBoundedAsync(opened,
                        cancellationToken, operation.Token).ConfigureAwait(false);
                }
                if (configured.DeviceTransferred)
                {
                    opened = null;
                    return await FailApplyAsync(requested, extension,
                        configured.ReasonCode ?? "CameraHealthUnavailable").ConfigureAwait(false);
                }
                if (!CameraSetupRuntime.IsActivationConfiguredHealth(configured.Health))
                    return await FailApplyAsync(requested, extension,
                        configured.ReasonCode ?? "CameraConfigurationStateInvalid", opened)
                        .ConfigureAwait(false);

                var snapshot = new CameraSetupSnapshot(_logicalRole, binding,
                    configured.Health!, requested, readBack.Effective, readBack.Differences,
                    extension, RecipeActivationPrepared, opened.Capabilities);
                lock (this)
                {
                    if (_disposed || _committed)
                    {
                        ReasonCode = "CameraActivationLeaseDisposed";
                    }
                    else
                    {
                        _candidateDevice = opened;
                        _candidateSnapshot = snapshot;
                        _candidateGeneration = generation;
                        _candidatePrepared = true;
                        opened = null;
                        ReasonCode = RecipeActivationPrepared;
                    }
                }
                if (opened is not null)
                    return await FailApplyAsync(requested, extension,
                        "CameraActivationLeaseDisposed", opened).ConfigureAwait(false);
                return new(true, RecipeActivationPrepared, snapshot, HardwareTouched);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await FailApplyAsync(requested, extension,
                    "CameraOperationCancelled", opened).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return await FailApplyAsync(requested, extension,
                    "CameraOperationTimeout", opened).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return await FailApplyAsync(requested, extension,
                    "CameraActivationPrepareFailed", opened).ConfigureAwait(false);
            }
        }
        finally
        {
            _leaseGate.Release();
        }
    }

    private async ValueTask<RecipeActivationCameraLeaseResult> FailApplyAsync(
        RequestedCameraConfiguration? requested,
        CameraProviderExtensionRequirement? extension, string reason,
        ICameraDevice? opened = null)
    {
        if (opened is not null)
        {
            var cleanup = await DisposeCandidateAsync(opened).ConfigureAwait(false);
            if (!cleanup.Closed) reason = "CameraDeviceCleanupRequired";
        }
        ReasonCode = SafeUnavailableReason(reason);
        if (HardwareTouched)
            _owner.PublishActivationFailure(_logicalRole, _currentSnapshot, requested,
                extension, ReasonCode);
        return new(false, ReasonCode, null, HardwareTouched);
    }

    /// <summary>
    /// Confirms the candidate after the caller's durable Active transaction has
    /// committed.  This is memory-only and intentionally cannot throw or write
    /// storage.  The lease remains the operation owner until disposed.
    /// </summary>
    internal bool Commit()
    {
        try
        {
            CameraSetupSnapshot? snapshot;
            ICameraDevice? candidate;
            lock (this)
            {
                if (_disposed || !_candidatePrepared || _candidateDevice is null ||
                    _candidateSnapshot is null || _committed || _restoreSucceeded)
                    return false;
                candidate = _candidateDevice;
                snapshot = _candidateSnapshot;
                var installed = _owner.InstallActivationDevice(_logicalRole,
                    _candidateGeneration, candidate, snapshot);
                if (!installed.Succeeded)
                {
                    ReasonCode = "CameraActivationCommitUnavailable";
                    return false;
                }
                _candidateDevice = null;
                _candidatePrepared = false;
                _committed = true;
                _restoredSnapshot = snapshot;
                ReasonCode = RecipeActivationCommitted;
            }
            _owner.PublishActivationSnapshot(_logicalRole, snapshot!);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            try { ReasonCode = "CameraActivationCommitUnavailable"; }
            catch (Exception ignored) when (ignored is not OutOfMemoryException) { }
            return false;
        }
    }

    internal async ValueTask<RecipeActivationCameraRestoreResult> RestoreAsync(
        CameraSetupSnapshot? durableBaseline,
        CancellationToken cancellationToken = default)
    {
        if (!Available)
            return new(false, ReasonCode, null, HardwareTouched);
        await _leaseGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_lastRestore is { } previous) return previous;
            if (_disposed)
                return RememberRestore(false, "CameraActivationLeaseDisposed", null);
            if (_committed)
                return RememberRestore(false, RecipeActivationCommitted, _restoredSnapshot);
            if (durableBaseline is null)
            {
                var candidateClosed = await CleanupCandidateAsync().ConfigureAwait(false);
                var previousClosed = candidateClosed
                    ? await _owner.CloseActivationSlotDeviceAsync(_logicalRole)
                        .ConfigureAwait(false)
                    : ActivationDeviceCloseResult.TransferredResult;
                if (!candidateClosed || !previousClosed.Closed ||
                    _owner.ActivationPhysicalCleanupPending)
                {
                    var reason = !candidateClosed || previousClosed.Retained ||
                        _owner.ActivationPhysicalCleanupPending
                        ? "CameraDeviceCleanupRequired" : RecipeActivationNoPreviousBaseline;
                    _owner.MarkActivationRestorationBlocked(_logicalRole);
                    _owner.PublishActivationFailure(_logicalRole, _currentSnapshot, null,
                        null, reason);
                    return RememberRestore(false, reason, null);
                }

                var safeClosed = _owner.PublishActivationSafeClosed(_logicalRole,
                    _currentSnapshot, RecipeActivationNoPreviousBaselineClosed);
                if (safeClosed is null)
                {
                    _owner.MarkActivationRestorationBlocked(_logicalRole);
                    _owner.PublishActivationFailure(_logicalRole, _currentSnapshot, null,
                        null, RecipeActivationNoPreviousBaseline);
                    return RememberRestore(false, RecipeActivationNoPreviousBaseline, null);
                }
                _safeClosed = true;
                _restoredSnapshot = safeClosed;
                ReasonCode = RecipeActivationNoPreviousBaselineClosed;
                return RememberRestore(true, RecipeActivationNoPreviousBaselineClosed,
                    safeClosed);
            }
            if (!_candidatePrepared && !HardwareTouched)
                return RememberRestore(true, "CameraActivationNoHardwareChange",
                    _currentSnapshot);
            var restoreRole = durableBaseline.LogicalRole;
            var crossRole = restoreRole != _logicalRole;
            if (!CameraSetupRuntime.IsActivationBaselineExact(durableBaseline,
                    crossRole ? _previousRoleSnapshot : _currentSnapshot))
            {
                await CleanupCandidateAsync().ConfigureAwait(false);
                _owner.MarkActivationRestorationBlocked(_logicalRole);
                _owner.PublishActivationFailure(_logicalRole, _currentSnapshot, null,
                    null, RecipeActivationBaselineMismatch);
                return RememberRestore(false, RecipeActivationBaselineMismatch, null);
            }
            if (durableBaseline.Extension is not null)
            {
                await CleanupCandidateAsync().ConfigureAwait(false);
                _owner.MarkActivationRestorationBlocked(_logicalRole);
                _owner.PublishActivationFailure(_logicalRole, _currentSnapshot, null,
                    durableBaseline.Extension, "CameraProviderExtensionUnavailable");
                return RememberRestore(false, "CameraProviderExtensionUnavailable", null);
            }

            if (cancellationToken.IsCancellationRequested)
                return new(false, "CameraOperationCancelled", null, HardwareTouched);

            // Once restoration starts, caller cancellation and shutdown cannot
            // abandon the old configuration. Physical calls keep their own bound.
            using var operation = _owner.CreateActivationCleanupToken();
            var cleanup = await CleanupCandidateAsync().ConfigureAwait(false);
            if (cleanup && crossRole)
            {
                var closed = await _owner.CloseActivationSlotDeviceAsync(_logicalRole).ConfigureAwait(false);
                cleanup = closed.Closed && !_owner.ActivationPhysicalCleanupPending &&
                    _owner.PublishActivationSafeClosed(_logicalRole, _currentSnapshot,
                        "CameraActivationCandidateRoleClosed") is not null;
            }
            if (!cleanup || _owner.ActivationPhysicalCleanupPending)
            {
                _owner.MarkActivationRestorationBlocked(_logicalRole);
                _owner.PublishActivationFailure(_logicalRole, _currentSnapshot,
                    durableBaseline.Requested, durableBaseline.Extension,
                    "CameraDeviceCleanupRequired");
                return new(false, "CameraDeviceCleanupRequired", null, HardwareTouched);
            }

            var binding = durableBaseline.Binding!;
            ICameraDevice? opened = null;
            try
            {
                var generation = _owner.BeginActivationGeneration(restoreRole);
                opened = await _owner.OpenActivationExactAsync(binding.Target,
                    operation.Token).ConfigureAwait(false);
                if (opened is null)
                    return await FailRestoreAsync(durableBaseline,
                        "CameraDeviceOpenFailed").ConfigureAwait(false);

                var initial = await _owner.ReadActivationHealthBoundedAsync(opened,
                    CancellationToken.None, operation.Token).ConfigureAwait(false);
                if (initial.DeviceTransferred)
                {
                    opened = null;
                    return await FailRestoreAsync(durableBaseline,
                        initial.ReasonCode ?? "CameraHealthUnavailable").ConfigureAwait(false);
                }
                if (!CameraSetupRuntime.IsActivationInitialHealth(initial.Health))
                    return await FailRestoreAsync(durableBaseline,
                        initial.ReasonCode ?? "CameraDeviceStateInvalid", opened)
                        .ConfigureAwait(false);

                CameraConfigurationResult expected;
                try
                {
                    expected = opened.Capabilities.ValidateConfiguration(
                        durableBaseline.Requested!);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { expected = CameraConfigurationResult.Failure(RecipeActivationBaselineMismatch); }
                if (!expected.Succeeded || expected.Effective is null ||
                    !expected.Effective.Equals(durableBaseline.Effective))
                    return await FailRestoreAsync(durableBaseline,
                        RecipeActivationBaselineMismatch, opened).ConfigureAwait(false);

                var applied = await _owner.ApplyActivationConfigurationBoundedAsync(opened,
                    durableBaseline.Requested!, CancellationToken.None, operation.Token)
                    .ConfigureAwait(false);
                if (applied.DeviceTransferred)
                {
                    opened = null;
                    return await FailRestoreAsync(durableBaseline,
                        applied.Result.ReasonCode).ConfigureAwait(false);
                }
                if (!applied.Result.Succeeded)
                    return await FailRestoreAsync(durableBaseline,
                        applied.Result.ReasonCode, opened).ConfigureAwait(false);

                CameraConfigurationResult readBack;
                try
                {
                    readBack = opened.Capabilities.ValidateReadBack(
                        durableBaseline.Requested!, applied.Result);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { readBack = CameraConfigurationResult.Failure("CameraReadBackInvalid"); }
                if (!readBack.Succeeded || readBack.Effective is null ||
                    !readBack.Effective.Equals(durableBaseline.Effective))
                    return await FailRestoreAsync(durableBaseline,
                        RecipeActivationBaselineReadBackMismatch, opened)
                        .ConfigureAwait(false);

                var configured = await _owner.ReadActivationHealthBoundedAsync(opened,
                    CancellationToken.None, operation.Token).ConfigureAwait(false);
                if (configured.DeviceTransferred)
                {
                    opened = null;
                    return await FailRestoreAsync(durableBaseline,
                        configured.ReasonCode ?? "CameraHealthUnavailable").ConfigureAwait(false);
                }
                if (!CameraSetupRuntime.IsActivationConfiguredHealth(configured.Health))
                    return await FailRestoreAsync(durableBaseline,
                        configured.ReasonCode ?? "CameraConfigurationStateInvalid", opened)
                        .ConfigureAwait(false);

                var restored = new CameraSetupSnapshot(restoreRole, binding,
                    configured.Health!, durableBaseline.Requested!, readBack.Effective,
                    readBack.Differences, durableBaseline.Extension,
                    RecipeActivationRestored, opened.Capabilities);
                var installed = _owner.InstallActivationDevice(restoreRole, generation,
                    opened, restored);
                if (!installed.Succeeded)
                    return await FailRestoreAsync(durableBaseline,
                        "CameraActivationGenerationConflict", opened).ConfigureAwait(false);

                _restoredSnapshot = restored;
                _candidatePrepared = false;
                _restoreSucceeded = true;
                _owner.ClearActivationRestorationBlocked(_logicalRole);
                _owner.ClearActivationRestorationBlocked(restoreRole);
                opened = null;
                ReasonCode = RecipeActivationRestored;
                _owner.PublishActivationSnapshot(restoreRole, restored);
                return RememberRestore(true, RecipeActivationRestored, restored);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await FailRestoreAsync(durableBaseline,
                    "CameraOperationCancelled", opened).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return await FailRestoreAsync(durableBaseline,
                    "CameraOperationTimeout", opened).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return await FailRestoreAsync(durableBaseline,
                    RecipeActivationRestoreFailed, opened).ConfigureAwait(false);
            }
        }
        finally
        {
            _leaseGate.Release();
        }
    }

    private async ValueTask<RecipeActivationCameraRestoreResult> FailRestoreAsync(
        CameraSetupSnapshot baseline, string reason, ICameraDevice? opened = null)
    {
        if (opened is not null)
        {
            var cleanup = await DisposeCandidateAsync(opened).ConfigureAwait(false);
            if (!cleanup.Closed) reason = "CameraDeviceCleanupRequired";
        }
        ReasonCode = SafeUnavailableReason(reason);
        _owner.MarkActivationRestorationBlocked(_logicalRole);
        _owner.MarkActivationRestorationBlocked(baseline.LogicalRole);
        _owner.PublishActivationFailure(baseline.LogicalRole, baseline,
            baseline.Requested, baseline.Extension, ReasonCode);
        return new(false, ReasonCode, null, HardwareTouched);
    }

    private RecipeActivationCameraRestoreResult RememberRestore(bool succeeded,
        string reason, CameraSetupSnapshot? snapshot)
    {
        var result = new RecipeActivationCameraRestoreResult(succeeded,
            SafeUnavailableReason(reason), snapshot, HardwareTouched);
        if (succeeded) _lastRestore = result;
        ReasonCode = result.ReasonCode;
        return result;
    }

    private async Task<bool> CleanupCandidateAsync()
    {
        ICameraDevice? candidate;
        lock (this)
        {
            candidate = _candidateDevice;
            _candidateDevice = null;
            _candidatePrepared = false;
            if (candidate is not null)
                _candidateSnapshot = null;
        }
        if (candidate is null) return true;
        var result = await DisposeCandidateAsync(candidate).ConfigureAwait(false);
        return result.Closed;
    }

    private async Task<ActivationDeviceCloseResult> DisposeCandidateAsync(
        ICameraDevice candidate)
    {
        return await _owner.StopAndDisposeActivationDeviceAsync(candidate)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _leaseGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (!_committed && !_restoreSucceeded && !_safeClosed)
            {
                var candidate = _candidateDevice;
                var requested = _candidateSnapshot?.Requested;
                var extension = _candidateSnapshot?.Extension;
                _candidateDevice = null;
                _candidatePrepared = false;
                if (candidate is not null)
                    _candidateSnapshot = null;
                if (candidate is not null)
                {
                    var cleanup = await DisposeCandidateAsync(candidate).ConfigureAwait(false);
                    if (!cleanup.Closed)
                        ReasonCode = "CameraDeviceCleanupRequired";
                }
                if (HardwareTouched)
                {
                    if (_previousRoleSnapshot is { } previousRole)
                        _owner.MarkActivationRestorationBlocked(previousRole.LogicalRole);
                    _owner.MarkActivationRestorationBlocked(_logicalRole);
                    _owner.PublishActivationFailure(_logicalRole, _currentSnapshot,
                        requested, extension,
                        ReasonCode == "CameraDeviceCleanupRequired"
                            ? ReasonCode : "CameraActivationLeaseDisposed");
                }
            }
        }
        finally
        {
            _leaseGate.Release();
            ReleaseOwnerReservation();
        }
    }

    private void ReleaseOwnerReservation()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        if (Interlocked.Exchange(ref _gateHeld, 0) != 0)
            _owner.ReleaseActivationOperationGate();
        if (_drain is not null)
            _owner.CompleteActivationInFlight(_drain);
    }
}
