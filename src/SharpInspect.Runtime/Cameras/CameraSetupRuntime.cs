using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;

namespace SharpInspect.Runtime.Cameras;

/// <summary>
/// Runtime-owned camera setup coordinator.  It is deliberately separate from the
/// production acquisition path: setup can open and configure a device for an
/// authorized maintenance operation, but it never supplies a production lease or
/// changes ActiveRecipe/Ready.
/// </summary>
internal sealed class CameraSetupRuntime : ICameraSetupRuntime, IAsyncDisposable
{
    private const int MaximumLogicalRoles = 16;
    private const int MaximumProviders = 4;
    private const int MaximumPhysicalOperations = 16;
    private const int MaximumReasonLength = 128;
    private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromMilliseconds(250);

    private readonly object _stateSync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _queryGate = new(4, 4);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IReadOnlyList<ProviderEntry> _providers;
    private readonly ICommandAuditWriter? _audit;
    private readonly IInteractiveSessionService? _sessions;
    private readonly IIdentityAdministrationQuery? _identityQuery;
    private readonly ICameraSetupAuthorizer? _cameraAuthorizer;
    private readonly ICameraSetupPersistence? _persistence;
    private readonly Func<CameraStationContext> _readStation;
    private readonly Action<string, CameraSetupSnapshot> _publishSetup;
    private readonly CameraSetupOptions _options;
    private readonly Dictionary<string, CameraSlot> _slots = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _inFlight = new();
    // Provider calls can legally ignore cancellation.  Their continuation is
    // retained until the physical call returns so a late device/result cannot
    // be mistaken for the next operation or leave an opened handle unowned.
    private readonly HashSet<Task> _physicalInFlight = new();
    private readonly SemaphoreSlim _physicalCapacity =
        new(MaximumPhysicalOperations, MaximumPhysicalOperations);
    // GetHealthSnapshot is a synchronous provider boundary. Keep at most one
    // physical probe per device, including after its bounded observation timed
    // out, so a slow provider cannot create an unbounded heartbeat backlog.
    private readonly Dictionary<ICameraDevice, HealthProbeRegistration> _healthProbes = new();
    // A retiring device is excluded from new health probes while its stop/
    // dispose chain is being coordinated.  This closes the registration race
    // between the heartbeat thread and a mutation that is about to stop it.
    private readonly HashSet<ICameraDevice> _retiringDevices = new();
    // A synchronous or completed DisposeAsync fault leaves the physical handle
    // in an unknown state. Keep a strong owner and fail closed until an explicit
    // process-level recovery can deal with it.
    private readonly HashSet<ICameraDevice> _retainedDevices = new();
    private bool _disposed;

    internal CameraSetupRuntime(IEnumerable<ICameraProvider> providers, CameraSetupOptions options,
        ICommandAuditWriter? audit, IInteractiveSessionService? sessions,
        IIdentityAdministrationQuery? identityQuery, Func<CameraStationContext> readStation,
        Action<string, CameraSetupSnapshot> publishSetup,
        ICameraSetupAuthorizer? cameraAuthorizer = null,
        ICameraSetupPersistence? persistence = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readStation);
        ArgumentNullException.ThrowIfNull(publishSetup);
        options.Validate();
        _options = options;
        _audit = audit;
        _sessions = sessions;
        _identityQuery = identityQuery;
        _cameraAuthorizer = cameraAuthorizer;
        _persistence = persistence;
        _readStation = readStation;
        _publishSetup = publishSetup;

        var entries = new List<ProviderEntry>(MaximumProviders);
        foreach (var provider in providers)
        {
            if (entries.Count == MaximumProviders)
                throw new ArgumentException("CameraProviderCapacityExceeded", nameof(providers));
            if (provider is null)
                throw new ArgumentException("CameraProviderNull", nameof(providers));

            CameraProviderIdentity identity;
            try { identity = provider.Identity ?? throw new InvalidOperationException("CameraProviderIdentityUnavailable"); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { throw new ArgumentException("CameraProviderIdentityUnavailable", nameof(providers)); }

            if (entries.Any(entry => SameProvider(entry.Identity, identity)))
                throw new ArgumentException("CameraProviderDuplicateIdentity", nameof(providers));
            entries.Add(new ProviderEntry(provider, identity));
        }

        _providers = new ReadOnlyCollection<ProviderEntry>(entries);
    }

    public IReadOnlyList<CameraProviderIdentity> Providers =>
        new ReadOnlyCollection<CameraProviderIdentity>(_providers.Select(item => item.Identity).ToArray());

    public async ValueTask<CameraDiscoveryResult> DiscoverAsync(CameraProviderIdentity provider,
        CommandInvocation invocation, CancellationToken cancellationToken = default)
    {
        var drain = RegisterInFlight();
        try
        {
            if (provider is null) return CameraDiscoveryResult.Failure("CameraProviderRequired");
            var authorization = await AuthorizeAsync(invocation, readOnly: true, Guid.Empty, "",
                AuditedCommandKind.Unsupported, cancellationToken).ConfigureAwait(false);
            if (!authorization.Authorized) return CameraDiscoveryResult.Failure(authorization.ReasonCode);

            var entry = FindProvider(provider);
            if (entry is null) return CameraDiscoveryResult.Failure("CameraProviderNotRegistered");
            if (!await _queryGate.WaitAsync(PositiveTimeout(_options.OperationTimeout), cancellationToken)
                    .ConfigureAwait(false))
                return CameraDiscoveryResult.Failure("CameraDiscoveryCapacityExceeded");

            try
            {
                using var operation = CreateOperationToken(cancellationToken);
                CameraDiscoveryResult result;
                Task<CameraDiscoveryResult>? providerTask = null;
                var physicalReserved = false;
                try
                {
                    if (!TryReservePhysical())
                        return CameraDiscoveryResult.Failure("CameraPhysicalCapacityExceeded");
                    physicalReserved = true;
                    providerTask = entry.Provider.DiscoverAsync(operation.Token).AsTask();
                    result = await providerTask.WaitAsync(PositiveTimeout(_options.OperationTimeout),
                        operation.Token).ConfigureAwait(false);
                    ReleasePhysical();
                    physicalReserved = false;
                }
                catch (TimeoutException)
                {
                    if (providerTask is not null && !providerTask.IsCompleted)
                    {
                        TrackPhysical(ObserveDiscardedAsync(providerTask));
                        physicalReserved = false;
                    }
                    else if (physicalReserved)
                    {
                        ReleasePhysical();
                        physicalReserved = false;
                    }
                    return CameraDiscoveryResult.Failure("CameraDiscoveryTimeout");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (providerTask is not null && !providerTask.IsCompleted)
                    {
                        TrackPhysical(ObserveDiscardedAsync(providerTask));
                        physicalReserved = false;
                    }
                    throw;
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    if (providerTask is not null && !providerTask.IsCompleted)
                    {
                        TrackPhysical(ObserveDiscardedAsync(providerTask));
                        physicalReserved = false;
                    }
                    return CameraDiscoveryResult.Failure("CameraDiscoveryCancelled");
                }
                catch (OperationCanceledException)
                {
                    if (providerTask is not null && !providerTask.IsCompleted)
                    {
                        TrackPhysical(ObserveDiscardedAsync(providerTask));
                        physicalReserved = false;
                    }
                    return CameraDiscoveryResult.Failure("CameraDiscoveryTimeout");
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { return CameraDiscoveryResult.Failure("CameraDiscoveryUnavailable"); }
                finally
                {
                    if (physicalReserved) ReleasePhysical();
                }

                if (result is null) return CameraDiscoveryResult.Failure("CameraDiscoveryUnavailable");
                if (!result.Succeeded) return result;
                try
                {
                    foreach (var descriptor in result.Devices)
                    {
                        if (!SameProvider(descriptor.Provider, entry.Identity))
                            return CameraDiscoveryResult.Failure("CameraProviderIdentityMismatch");
                    }

                    return result;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { return CameraDiscoveryResult.Failure("CameraDiscoveryInvalid"); }
            }
            finally { _queryGate.Release(); }
        }
        finally { CompleteInFlight(drain); }
    }

    public async ValueTask<CameraSetupQueryResult> GetSetupAsync(string logicalRole,
        CommandInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ValidLogicalRole(logicalRole)) return new(false, "CameraLogicalRoleInvalid");
        var authorization = await AuthorizeAsync(invocation, readOnly: true, Guid.Empty, logicalRole,
            AuditedCommandKind.Unsupported, cancellationToken).ConfigureAwait(false);
        if (!authorization.Authorized) return new(false, authorization.ReasonCode);

        var persisted = await LoadPersistedAsync(logicalRole, cancellationToken).ConfigureAwait(false);
        if (!persisted.Succeeded) return new(false, persisted.ReasonCode);

        lock (_stateSync)
        {
            if (_disposed) return new(false, "CameraSetupRuntimeStopped");
            return _slots.TryGetValue(logicalRole, out var slot) && slot.Snapshot is { } snapshot
                ? new CameraSetupQueryResult(true, snapshot.ReasonCode, snapshot)
                : new CameraSetupQueryResult(false, "CameraSetupUnconfigured");
        }
    }

    public ValueTask<CameraSetupOperationResult> RebindAsync(CameraRebindRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ValueTask<CameraSetupOperationResult>(RunRebindAsync(request, cancellationToken));
    }

    public ValueTask<CameraSetupOperationResult> ApplyDebugConfigurationAsync(
        CameraDebugConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ValueTask<CameraSetupOperationResult>(RunApplyAsync(request, cancellationToken));
    }

    /// <summary>
    /// Schedules bounded provider health observations without holding the station or
    /// operation lock.  The provider is allowed to implement this call over its own
    /// synchronization, so a heartbeat must never make the Runtime's command path
    /// wait on provider code.
    /// </summary>
    internal void Heartbeat()
    {
        (string Role, long Generation, ICameraDevice Device, CameraSetupSnapshot Snapshot)[] observed;
        lock (_stateSync)
        {
            if (_disposed) return;
            observed = _slots.Values
                .Where(slot => slot.Device is not null && slot.Snapshot is not null &&
                    !_retainedDevices.Contains(slot.Device))
                .Select(slot => (slot.LogicalRole, slot.Generation, slot.Device!, slot.Snapshot!))
                .ToArray();
        }

        foreach (var item in observed)
        {
            HealthProbeRegistration registration;
            lock (_stateSync)
            {
                if (_disposed || !_slots.TryGetValue(item.Role, out var current) ||
                    current.Generation != item.Generation || !ReferenceEquals(current.Device, item.Device) ||
                    !ReferenceEquals(current.Snapshot, item.Snapshot) || _retiringDevices.Contains(item.Device) ||
                    _retainedDevices.Contains(item.Device) ||
                    _healthProbes.ContainsKey(item.Device))
                    continue;
                registration = new HealthProbeRegistration();
                _healthProbes.Add(item.Device, registration);
            }

            // Start the provider call on the thread pool. The async observation
            // itself is intentionally fire-and-forget; its state is published only
            // if the captured generation/device/snapshot is still current.
            var drain = RegisterInFlight();
            _ = ProbeHealthAsync(item, drain, registration);
        }
    }

    private async Task ProbeHealthAsync(
        (string Role, long Generation, ICameraDevice Device, CameraSetupSnapshot Snapshot) item,
        TaskCompletionSource<bool> drain, HealthProbeRegistration registration)
    {
        Task<CameraHealthSnapshot?>? providerTask = null;
        var physicalReserved = false;
        var physicalTracked = false;
        try
        {
            try
            {
                if (!TryReservePhysical())
                {
                    ApplyHealthObservation(item, FailureHealth("CameraHealthProbeCapacityExceeded"));
                    return;
                }
                physicalReserved = true;
                providerTask = Task.Run(() => ReadValidHealth(item.Device));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (physicalReserved) ReleasePhysical();
                physicalReserved = false;
                ApplyHealthObservation(item, FailureHealth("CameraHealthUnavailable"));
                return;
            }

            CameraHealthSnapshot? health = null;
            try
            {
                health = await providerTask!.WaitAsync(HealthProbeTimeout).ConfigureAwait(false);
                ReleasePhysical();
                physicalReserved = false;
            }
            catch (TimeoutException)
            {
                if (!providerTask!.IsCompleted)
                {
                    TrackPhysical(providerTask);
                    physicalTracked = true;
                    physicalReserved = false;
                }
                else
                {
                    ReleasePhysical();
                    physicalReserved = false;
                }
                health = FailureHealth("CameraHealthProbeTimeout");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (physicalReserved) ReleasePhysical();
                physicalReserved = false;
                health = FailureHealth("CameraHealthUnavailable");
            }

            ApplyHealthObservation(item, health ?? FailureHealth("CameraHealthUnavailable"));

            // Do not release the per-device admission while the physical provider
            // call is still running. A late result is deliberately observed and
            // discarded, but its task remains owned until it really returns.
            if (physicalTracked)
            {
                try { await providerTask!.ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (physicalReserved)
            {
                ReleasePhysical();
                physicalReserved = false;
            }
        }
        finally
        {
            if (physicalReserved) ReleasePhysical();
            lock (_stateSync) _healthProbes.Remove(item.Device);
            registration.Completed.TrySetResult(true);
            CompleteInFlight(drain);
        }
    }

    private void ApplyHealthObservation(
        (string Role, long Generation, ICameraDevice Device, CameraSetupSnapshot Snapshot) item,
        CameraHealthSnapshot health)
    {
        CameraSetupSnapshot? nextSnapshot = null;
        lock (_stateSync)
        {
            if (_disposed || !_slots.TryGetValue(item.Role, out var slot) ||
                slot.Generation != item.Generation || !ReferenceEquals(slot.Device, item.Device) ||
                !ReferenceEquals(slot.Snapshot, item.Snapshot))
                return;

            if (HealthEquivalent(item.Snapshot.Health, health)) return;

            var effective = health.Configuration == CameraConfigurationState.Applied &&
                health.Connection == CameraConnectionState.Open
                ? item.Snapshot.Effective : null;
            var differences = effective is null
                ? Array.Empty<CameraConfigurationDifference>() : item.Snapshot.Differences;
            nextSnapshot = new CameraSetupSnapshot(item.Role, item.Snapshot.Binding, health,
                item.Snapshot.Requested, effective, differences, item.Snapshot.Extension,
                health.LastFault?.ReasonCode ?? item.Snapshot.ReasonCode, item.Snapshot.Capabilities);
            slot.Snapshot = nextSnapshot;
        }

        if (nextSnapshot is not null) Publish(item.Role, nextSnapshot);
    }

    private static bool HealthEquivalent(CameraHealthSnapshot left, CameraHealthSnapshot right) =>
        left.ProviderAvailability == right.ProviderAvailability &&
        left.Connection == right.Connection && left.Configuration == right.Configuration &&
        left.Acquisition == right.Acquisition &&
        left.LastFault?.Classification == right.LastFault?.Classification &&
        string.Equals(left.LastFault?.ReasonCode, right.LastFault?.ReasonCode, StringComparison.Ordinal) &&
        string.Equals(left.LastFault?.DiagnosticCode, right.LastFault?.DiagnosticCode, StringComparison.Ordinal);

    private async Task<CameraSetupOperationResult> RunRebindAsync(CameraRebindRequest request,
        CancellationToken cancellationToken)
    {
        var invalid = ValidateMutationInput(request.OperationId, request.Invocation, request.LogicalRole,
            request.Target, request.ChangeReason);
        if (invalid is not null) return Failed(invalid, AuditPersistence.NotAttempted);
        var provider = FindProvider(request.Target.Provider);
        if (provider is null) return Failed("CameraProviderNotRegistered", AuditPersistence.NotAttempted);

        var authorization = await AuthorizeAsync(request.Invocation, readOnly: false, request.OperationId,
            request.LogicalRole, AuditedCommandKind.RebindCamera, cancellationToken).ConfigureAwait(false);
        if (!authorization.Authorized) return Failed(authorization.ReasonCode, AuditPersistence.NotAttempted);

        var linked = CreateOperationToken(cancellationToken);
        var gateAcquired = false;
        TaskCompletionSource<bool>? drain = null;
        var generation = 0L;
        CameraSlot? previous = null;
        CommandAuditFact? admitted = null;
        CameraSetupPersistenceEvent? admissionEvent = null;
        ICameraDevice? opened = null;
        try
        {
            drain = RegisterInFlight();
            gateAcquired = await WaitForOperationGateAsync(cancellationToken).ConfigureAwait(false);
            if (!gateAcquired)
            {
                authorization.Reservation?.Dispose();
                return Failed("CameraSetupBusy", AuditPersistence.NotAttempted);
            }

            var persisted = await LoadPersistedAsync(request.LogicalRole, linked.Token).ConfigureAwait(false);
            if (!persisted.Succeeded)
                return await RejectBeforeAuditAsync(persisted.ReasonCode, authorization).ConfigureAwait(false);
            if (persisted.Pending)
                return await RejectBeforeAuditAsync("CameraSetupOperationPending", authorization).ConfigureAwait(false);
            var precondition = CheckMutationPreconditions(request.LogicalRole, request.ExpectedBindingRevision,
                request.ExpectedBindingRevisionHash, allowUnbound: true);
            if (precondition is not null)
                return await RejectBeforeAuditAsync(precondition, authorization).ConfigureAwait(false);

            previous = GetSlot(request.LogicalRole);
            generation = BeginGeneration(request.LogicalRole);
            admissionEvent = CreatePersistenceEvent(CameraSetupPersistencePhase.Admission,
                request.OperationId, request.LogicalRole, AuditedCommandKind.RebindCamera,
                previous?.Snapshot?.Binding, binding: null, request.Target, requested: null,
                effective: null, differences: null, extension: null, health: null,
                succeeded: true, "CameraRebindAdmitted", request.ChangeReason, authorization);
            var admission = await AppendAdmissionAsync(request.OperationId, request.Invocation,
                AuditedCommandKind.RebindCamera, authorization, "CameraRebindAdmitted", admissionEvent,
                linked.Token)
                .ConfigureAwait(false);
            if (!admission.Committed || admission.Fact is null)
            {
                authorization.Reservation?.Dispose();
                return Failed(admission.ReasonCode, AuditPersistence.Unavailable);
            }

            admitted = admission.Fact;
            // The grant is spent once the admission fact is durable.  A later
            // provider/configuration failure must never reactivate the same
            // one-time Step-Up grant for another camera operation.
            authorization.Reservation?.Commit();
            var closeOutcome = await CloseSlotDeviceAsync(previous).ConfigureAwait(false);
            if (closeOutcome != DeviceCloseOutcome.Closed)
            {
                // A previous physical handle that does not acknowledge Stop is
                // still owned by its late-cleanup continuation.  Do not open or
                // publish a replacement against an uncertain device state.
                return await CompleteFailureAsync(request.LogicalRole, generation, previous,
                    admitted, authorization, admissionEvent,
                    closeOutcome == DeviceCloseOutcome.Retained
                        ? "CameraDeviceDisposeFailed" : "CameraOperationTimeout", linked.Token)
                    .ConfigureAwait(false);
            }
            opened = await OpenExactAsync(provider, request.Target.StableDeviceIdentity, linked.Token)
                .ConfigureAwait(false);
            if (opened is null)
            {
                var reason = "CameraDeviceOpenFailed";
                return await CompleteFailureAsync(request.LogicalRole, generation, previous,
                    admitted, authorization, admissionEvent, reason, linked.Token, opened).ConfigureAwait(false);
            }

            var healthCall = await ReadHealthBoundedAsync(opened, cancellationToken, linked.Token)
                .ConfigureAwait(false);
            if (healthCall.DeviceTransferred)
            {
                opened = null;
                return await CompleteFailureAsync(request.LogicalRole, generation, previous,
                    admitted, authorization, admissionEvent,
                    healthCall.ReasonCode ?? "CameraHealthUnavailable", linked.Token)
                    .ConfigureAwait(false);
            }

            var health = healthCall.Health;
            if (health is null || health.ProviderAvailability != CameraProviderAvailability.Available ||
                health.Connection != CameraConnectionState.Open ||
                health.Configuration != CameraConfigurationState.Unconfigured ||
                health.Acquisition != CameraAcquisitionState.Stopped)
            {
                return await CompleteFailureAsync(request.LogicalRole, generation, previous,
                    admitted, authorization, admissionEvent,
                    healthCall.ReasonCode ?? "CameraDeviceStateInvalid", linked.Token, opened)
                    .ConfigureAwait(false);
            }

            var revision = CreateBindingRevision(request, authorization, previous?.Snapshot?.Binding);
            CameraSetupSnapshot snapshot = new CameraSetupSnapshot(request.LogicalRole, revision, health,
                reasonCode: "CameraBindingChanged");
            var terminalEvent = CreatePersistenceEvent(CameraSetupPersistencePhase.Terminal,
                request.OperationId, request.LogicalRole, AuditedCommandKind.RebindCamera,
                previous?.Snapshot?.Binding, revision, request.Target, requested: null,
                effective: null, differences: null, extension: null, health, succeeded: true,
                "CameraRebindCompleted", request.ChangeReason, authorization);
            var terminal = await AppendTerminalAsync(admitted, terminalEvent,
                "CameraRebindCompleted", completed: true, linked.Token).ConfigureAwait(false);
            if (!terminal.Committed)
            {
                await DisposeDeviceBoundedAsync(opened, _options.OperationTimeout).ConfigureAwait(false);
                opened = null;
                authorization.Reservation?.Dispose();
                return await CompleteAuditFailureStateAsync(request.LogicalRole, generation, previous,
                    admissionEvent, "TraceAuditUnavailable").ConfigureAwait(false);
            }

            // The event writer assigns the camera stream position globally. Read
            // back only that durable binding after commit so Position is not
            // guessed from the per-role revision. A committed operation whose
            // binding cannot be read back is exposed as a safe unavailable
            // projection; it is never reported as a successful local guess.
            var persistedBinding = await ReadCommittedBindingAsync(request.LogicalRole,
                revision).ConfigureAwait(false);
            if (_persistence is not null && persistedBinding is null)
            {
                await DisposeDeviceBoundedAsync(opened, _options.OperationTimeout).ConfigureAwait(false);
                opened = null;
                authorization.Reservation?.Dispose();
                return await CompleteCommittedReadbackFailureStateAsync(request.LogicalRole, generation,
                    previous, "CameraSetupReadbackUnavailable").ConfigureAwait(false);
            }
            if (persistedBinding is not null)
                snapshot = new CameraSetupSnapshot(request.LogicalRole, persistedBinding, health,
                    reasonCode: "CameraBindingChanged");

            authorization.Reservation?.Dispose();
            var uninstalled = InstallDevice(request.LogicalRole, generation, opened, snapshot);
            opened = null;
            if (uninstalled is not null)
                await DisposeDeviceBoundedAsync(uninstalled, _options.OperationTimeout).ConfigureAwait(false);
            Publish(request.LogicalRole, snapshot);
            return new CameraSetupOperationResult(true, "CameraRebindCompleted", AuditPersistence.Persisted,
                snapshot);
        }
        catch (PhysicalCapacityExceededException)
        {
            if (admitted is not null)
                return await CompleteFailureAsync(request.LogicalRole, generation, previous, admitted,
                    authorization, admissionEvent, "CameraPhysicalCapacityExceeded", CancellationToken.None,
                    opened).ConfigureAwait(false);
            authorization.Reservation?.Dispose();
            return Failed("CameraPhysicalCapacityExceeded", AuditPersistence.NotAttempted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (admitted is null) authorization.Reservation?.Dispose();
            if (admitted is not null)
                return await CompleteFailureAsync(request.LogicalRole, generation, previous, admitted,
                    authorization, admissionEvent, "CameraOperationCancelled", CancellationToken.None, opened).ConfigureAwait(false);
            return Failed("CameraOperationCancelled", AuditPersistence.NotAttempted);
        }
        catch (OperationCanceledException)
        {
            if (admitted is not null)
                return await CompleteFailureAsync(request.LogicalRole, generation, previous, admitted,
                    authorization, admissionEvent, "CameraOperationTimeout", CancellationToken.None, opened).ConfigureAwait(false);
            authorization.Reservation?.Dispose();
            return Failed("CameraOperationTimeout", AuditPersistence.NotAttempted);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (opened is not null)
            {
                await DisposeDeviceBoundedAsync(opened, _options.OperationTimeout).ConfigureAwait(false);
                opened = null;
            }
            if (admitted is not null)
                return await CompleteFailureAsync(request.LogicalRole, generation, previous, admitted,
                    authorization, admissionEvent, "CameraSetupUnavailable", CancellationToken.None).ConfigureAwait(false);
            authorization.Reservation?.Dispose();
            return Failed("CameraSetupUnavailable", AuditPersistence.NotAttempted);
        }
        finally
        {
            opened = null;
            try
            {
                if (gateAcquired) _operationGate.Release();
            }
            finally
            {
                CompleteInFlight(drain);
                linked.Dispose();
            }
        }
    }

    private async Task<CameraSetupOperationResult> RunApplyAsync(
        CameraDebugConfigurationRequest request, CancellationToken cancellationToken)
    {
        var invalid = ValidateMutationInput(request.OperationId, request.Invocation, request.LogicalRole,
            target: null, request.ChangeReason);
        if (invalid is not null) return Failed(invalid, AuditPersistence.NotAttempted);
        var authorization = await AuthorizeAsync(request.Invocation, readOnly: false, request.OperationId,
            request.LogicalRole, AuditedCommandKind.ApplyCameraDebugConfiguration, cancellationToken)
            .ConfigureAwait(false);
        if (!authorization.Authorized) return Failed(authorization.ReasonCode, AuditPersistence.NotAttempted);

        var linked = CreateOperationToken(cancellationToken);
        var gateAcquired = false;
        TaskCompletionSource<bool>? drain = null;
        var generation = 0L;
        CameraSlot? previous = null;
        CommandAuditFact? admitted = null;
        CameraSetupPersistenceEvent? admissionEvent = null;
        ICameraDevice? opened = null;
        try
        {
            drain = RegisterInFlight();
            gateAcquired = await WaitForOperationGateAsync(cancellationToken).ConfigureAwait(false);
            if (!gateAcquired)
            {
                authorization.Reservation?.Dispose();
                return Failed("CameraSetupBusy", AuditPersistence.NotAttempted);
            }

            var persisted = await LoadPersistedAsync(request.LogicalRole, linked.Token).ConfigureAwait(false);
            if (!persisted.Succeeded)
                return await RejectBeforeAuditAsync(persisted.ReasonCode, authorization).ConfigureAwait(false);
            if (persisted.Pending)
                return await RejectBeforeAuditAsync("CameraSetupOperationPending", authorization).ConfigureAwait(false);
            previous = GetSlot(request.LogicalRole);
            var precondition = CheckMutationPreconditions(request.LogicalRole, request.ExpectedBindingRevision,
                request.ExpectedBindingRevisionHash, allowUnbound: false);
            if (precondition is not null)
                return await RejectBeforeAuditAsync(precondition, authorization).ConfigureAwait(false);
            if (previous?.Snapshot?.Binding is not { } binding)
            {
                authorization.Reservation?.Dispose();
                return Failed("CameraBindingMissing", AuditPersistence.NotAttempted);
            }
            if (request.Extension is { } extension)
            {
                if (!SameProvider(extension.Provider, binding.Target.Provider))
                {
                    authorization.Reservation?.Dispose();
                    return Failed("CameraExtensionBindingMismatch", AuditPersistence.NotAttempted);
                }
                // No provider extension is part of the common Runtime. A typed
                // extension must be explicitly implemented by a provider package;
                // silently discarding it would change configuration semantics.
                authorization.Reservation?.Dispose();
                return Failed("CameraProviderExtensionUnavailable", AuditPersistence.NotAttempted);
            }

            generation = BeginGeneration(request.LogicalRole);
            admissionEvent = CreatePersistenceEvent(CameraSetupPersistencePhase.Admission,
                request.OperationId, request.LogicalRole, AuditedCommandKind.ApplyCameraDebugConfiguration,
                binding, binding, binding.Target, request.Requested, effective: null,
                differences: null, request.Extension, health: null, succeeded: true,
                "CameraDebugConfigurationAdmitted", request.ChangeReason, authorization);
            var admission = await AppendAdmissionAsync(request.OperationId, request.Invocation,
                AuditedCommandKind.ApplyCameraDebugConfiguration, authorization,
                "CameraDebugConfigurationAdmitted", admissionEvent, linked.Token).ConfigureAwait(false);
            if (!admission.Committed || admission.Fact is null)
            {
                authorization.Reservation?.Dispose();
                return Failed(admission.ReasonCode, AuditPersistence.Unavailable);
            }

            admitted = admission.Fact;
            // Admission is the grant consumption point; terminal success is
            // deliberately independent from Step-Up reuse.
            authorization.Reservation?.Commit();
            var closeOutcome = await CloseSlotDeviceAsync(previous).ConfigureAwait(false);
            if (closeOutcome != DeviceCloseOutcome.Closed)
            {
                return await CompleteFailureAsync(request.LogicalRole, generation, previous, admitted,
                    authorization, admissionEvent,
                    closeOutcome == DeviceCloseOutcome.Retained
                        ? "CameraDeviceDisposeFailed" : "CameraOperationTimeout", linked.Token)
                    .ConfigureAwait(false);
            }
            var provider = FindProvider(binding.Target.Provider);
            if (provider is null)
                return await CompleteFailureAsync(request.LogicalRole, generation, previous, admitted,
                    authorization, admissionEvent, "CameraProviderNotRegistered", linked.Token).ConfigureAwait(false);
            opened = await OpenExactAsync(provider, binding.Target.StableDeviceIdentity, linked.Token)
                .ConfigureAwait(false);
            if (opened is null)
                return await CompleteFailureAsync(request.LogicalRole, generation, previous, admitted,
                    authorization, admissionEvent, "CameraDeviceOpenFailed", linked.Token, opened).ConfigureAwait(false);
            var activeDevice = opened;

            CameraConfigurationResult expected;
            try { expected = activeDevice.Capabilities.ValidateConfiguration(request.Requested); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { expected = CameraConfigurationResult.Failure("CameraConfigurationInvalid"); }
            if (!expected.Succeeded)
                return await CompleteFailureAsync(request.LogicalRole, generation, previous, admitted,
                    authorization, admissionEvent, expected.ReasonCode, linked.Token, opened).ConfigureAwait(false);

            var configurationCall = await ApplyConfigurationBoundedAsync(activeDevice, request.Requested,
                cancellationToken, linked.Token).ConfigureAwait(false);
            if (configurationCall.DeviceTransferred) opened = null;
            var reported = configurationCall.Result;
            if (configurationCall.DeviceTransferred)
                return await CompleteFailureAsync(request.LogicalRole, generation, previous, admitted,
                    authorization, admissionEvent, reported.ReasonCode, linked.Token).ConfigureAwait(false);

            CameraConfigurationResult readBack;
            try { readBack = activeDevice.Capabilities.ValidateReadBack(request.Requested, reported); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { readBack = CameraConfigurationResult.Failure("CameraReadBackInvalid"); }
            if (!readBack.Succeeded || readBack.Effective is null)
                return await CompleteFailureAsync(request.LogicalRole, generation, previous, admitted,
                    authorization, admissionEvent, readBack.ReasonCode, linked.Token, opened).ConfigureAwait(false);

            var healthCall = await ReadHealthBoundedAsync(activeDevice, cancellationToken, linked.Token)
                .ConfigureAwait(false);
            if (healthCall.DeviceTransferred)
            {
                opened = null;
                return await CompleteFailureAsync(request.LogicalRole, generation, previous, admitted,
                    authorization, admissionEvent,
                    healthCall.ReasonCode ?? "CameraHealthUnavailable", linked.Token).ConfigureAwait(false);
            }

            var health = healthCall.Health;
            if (health is null || health.ProviderAvailability != CameraProviderAvailability.Available ||
                health.Connection != CameraConnectionState.Open ||
                health.Configuration != CameraConfigurationState.Applied ||
                health.Acquisition != CameraAcquisitionState.Stopped)
                return await CompleteFailureAsync(request.LogicalRole, generation, previous, admitted,
                    authorization, admissionEvent,
                    healthCall.ReasonCode ?? "CameraConfigurationStateInvalid", linked.Token, opened)
                    .ConfigureAwait(false);

            var snapshot = new CameraSetupSnapshot(request.LogicalRole, binding, health, request.Requested,
                readBack.Effective, readBack.Differences, request.Extension,
                "CameraConfigurationApplied", activeDevice.Capabilities);
            var terminalEvent = CreatePersistenceEvent(CameraSetupPersistencePhase.Terminal,
                request.OperationId, request.LogicalRole, AuditedCommandKind.ApplyCameraDebugConfiguration,
                binding, binding, binding.Target, request.Requested, readBack.Effective,
                readBack.Differences, request.Extension, health, succeeded: true,
                "CameraConfigurationApplied", request.ChangeReason, authorization);
            var terminal = await AppendTerminalAsync(admitted, terminalEvent,
                "CameraConfigurationApplied", completed: true, linked.Token).ConfigureAwait(false);
            if (!terminal.Committed)
            {
                await DisposeDeviceBoundedAsync(activeDevice, _options.OperationTimeout).ConfigureAwait(false);
                opened = null;
                authorization.Reservation?.Dispose();
                return await CompleteAuditFailureStateAsync(request.LogicalRole, generation, previous,
                    admissionEvent, "TraceAuditUnavailable").ConfigureAwait(false);
            }

            authorization.Reservation?.Dispose();
            var uninstalled = InstallDevice(request.LogicalRole, generation, activeDevice, snapshot);
            opened = null;
            if (uninstalled is not null)
                await DisposeDeviceBoundedAsync(uninstalled, _options.OperationTimeout).ConfigureAwait(false);
            Publish(request.LogicalRole, snapshot);
            return new CameraSetupOperationResult(true, "CameraConfigurationApplied", AuditPersistence.Persisted,
                snapshot);
        }
        catch (PhysicalCapacityExceededException)
        {
            if (admitted is not null)
                return await CompleteFailureAsync(request.LogicalRole, generation, previous, admitted,
                    authorization, admissionEvent, "CameraPhysicalCapacityExceeded", CancellationToken.None,
                    opened).ConfigureAwait(false);
            authorization.Reservation?.Dispose();
            return Failed("CameraPhysicalCapacityExceeded", AuditPersistence.NotAttempted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (admitted is null) authorization.Reservation?.Dispose();
            if (admitted is not null)
                return await CompleteFailureAsync(request.LogicalRole, generation, previous, admitted,
                    authorization, admissionEvent, "CameraOperationCancelled", CancellationToken.None, opened).ConfigureAwait(false);
            return Failed("CameraOperationCancelled", AuditPersistence.NotAttempted);
        }
        catch (OperationCanceledException)
        {
            if (admitted is not null)
                return await CompleteFailureAsync(request.LogicalRole, generation, previous, admitted,
                    authorization, admissionEvent, "CameraOperationTimeout", CancellationToken.None, opened).ConfigureAwait(false);
            authorization.Reservation?.Dispose();
            return Failed("CameraOperationTimeout", AuditPersistence.NotAttempted);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (opened is not null)
            {
                await DisposeDeviceBoundedAsync(opened, _options.OperationTimeout).ConfigureAwait(false);
                opened = null;
            }
            if (admitted is not null)
                return await CompleteFailureAsync(request.LogicalRole, generation, previous, admitted,
                    authorization, admissionEvent, "CameraSetupUnavailable", CancellationToken.None).ConfigureAwait(false);
            authorization.Reservation?.Dispose();
            return Failed("CameraSetupUnavailable", AuditPersistence.NotAttempted);
        }
        finally
        {
            opened = null;
            try
            {
                if (gateAcquired) _operationGate.Release();
            }
            finally
            {
                CompleteInFlight(drain);
                linked.Dispose();
            }
        }
    }

    private Task<CameraSetupOperationResult> RejectBeforeAuditAsync(string reason,
        CameraSetupAuthorization authorization)
    {
        authorization.Reservation?.Dispose();
        return Task.FromResult(Failed(reason, AuditPersistence.NotAttempted));
    }

    private async Task<CameraSetupOperationResult> CompleteFailureAsync(string role, long generation,
        CameraSlot? previous, CommandAuditFact admitted, CameraSetupAuthorization authorization,
        CameraSetupPersistenceEvent? admissionEvent, string reason, CancellationToken cancellationToken,
        ICameraDevice? opened = null)
    {
        if (opened is not null)
            await DisposeDeviceBoundedAsync(opened, _options.OperationTimeout).ConfigureAwait(false);
        await DisposeDeviceForGenerationAsync(role, generation).ConfigureAwait(false);
        var terminalEvent = admissionEvent is null ? null : CreateTerminalFailureEvent(admissionEvent, reason);
        var terminal = await AppendTerminalAsync(admitted, terminalEvent, reason, completed: false, cancellationToken)
            .ConfigureAwait(false);
        authorization.Reservation?.Dispose();
        var audit = terminal.Committed ? AuditPersistence.Persisted : AuditPersistence.Unavailable;
        if (!terminal.Committed) reason = "TraceAuditUnavailable";
        return await CompleteFailureStateAsync(role, generation, previous, reason, audit,
            admissionEvent?.Requested).ConfigureAwait(false);
    }

    private async Task<CameraSetupOperationResult> CompleteAuditFailureStateAsync(string role,
        long generation, CameraSlot? previous, CameraSetupPersistenceEvent? admissionEvent,
        string reason)
    {
        await DisposeDeviceForGenerationAsync(role, generation).ConfigureAwait(false);
        return await CompleteFailureStateAsync(role, generation, previous, reason,
            AuditPersistence.Unavailable, admissionEvent?.Requested).ConfigureAwait(false);
    }

    private Task<CameraSetupOperationResult> CompleteCommittedReadbackFailureStateAsync(
        string role, long generation, CameraSlot? previous, string reason)
    {
        CameraSetupSnapshot snapshot;
        lock (_stateSync)
        {
            if (_disposed || !_slots.TryGetValue(role, out var slot) || slot.Generation != generation)
                return Task.FromResult(Failed(reason, AuditPersistence.Persisted));

            // The terminal fact is already durable. Keep only the last writer
            // assigned binding that was already known locally, and make the
            // live projection unavailable until a later authoritative refresh.
            snapshot = new CameraSetupSnapshot(role, previous?.Snapshot?.Binding,
                FailureHealth(reason), requested: null, effective: null,
                differences: Array.Empty<CameraConfigurationDifference>(),
                reasonCode: reason);
            slot.Snapshot = snapshot;
            if (slot.Device is null || (!_retainedDevices.Contains(slot.Device) &&
                !_retiringDevices.Contains(slot.Device)))
                slot.Device = null;
        }
        Publish(role, snapshot);
        return Task.FromResult(Failed(reason, AuditPersistence.Persisted, snapshot));
    }

    private Task<CameraSetupOperationResult> CompleteFailureStateAsync(string role, long generation,
        CameraSlot? previous, string reason, AuditPersistence audit,
        RequestedCameraConfiguration? requested = null)
    {
        CameraSetupSnapshot snapshot;
        lock (_stateSync)
        {
            if (_disposed || !_slots.TryGetValue(role, out var slot) || slot.Generation != generation)
                return Task.FromResult(Failed(reason, audit));
            var previousSnapshot = previous?.Snapshot;
            var health = FailureHealth(reason);
            snapshot = new CameraSetupSnapshot(role, previousSnapshot?.Binding, health,
                requested, reasonCode: reason);
            slot.Snapshot = snapshot;
            // A completed DisposeAsync fault leaves the previous handle owned by
            // the runtime. Keep the slot attached to that retained handle so a
            // later operation cannot silently open a replacement over unknown
            // physical state.
            if (slot.Device is null || (!_retainedDevices.Contains(slot.Device) &&
                !_retiringDevices.Contains(slot.Device)))
                slot.Device = null;
        }
        Publish(role, snapshot);
        return Task.FromResult(Failed(reason, audit, snapshot));
    }

    private static CameraSetupOperationResult Failed(string reason, AuditPersistence audit,
        CameraSetupSnapshot? snapshot = null) =>
        new(false, SafeReason(reason), audit, snapshot);

    private async ValueTask<CameraSetupAuthorization> AuthorizeAsync(CommandInvocation invocation,
        bool readOnly, Guid operationId, string targetId, AuditedCommandKind kind,
        CancellationToken cancellationToken)
    {
        if (invocation is null || !Enum.IsDefined(invocation.Source))
            return CameraSetupAuthorization.Denied("InvalidCommandContext");
        if (_identityQuery is null || _sessions is null)
            return CameraSetupAuthorization.Denied("AuthorizationUnavailable");
        if (!readOnly && operationId == Guid.Empty)
            return CameraSetupAuthorization.Denied("CameraOperationIdRequired");
        if (!ValidLogicalRole(targetId) && !readOnly)
            return CameraSetupAuthorization.Denied("CameraLogicalRoleInvalid");

        try
        {
            var current = _sessions.Current;
            if (current.State != InteractiveSessionState.Authenticated || current.SessionId is not { } sessionId ||
                !Guid.TryParseExact(current.PrincipalId, "D", out var currentPrincipal) ||
                currentPrincipal == Guid.Empty || invocation.SessionId != sessionId ||
                !Guid.TryParseExact(invocation.PrincipalId, "D", out var claimedPrincipal) ||
                claimedPrincipal != currentPrincipal)
                return CameraSetupAuthorization.Denied("AuthenticationRequired");

            if (!readOnly && _cameraAuthorizer is null)
                return CameraSetupAuthorization.Denied("AuthorizationUnavailable");

            if (_cameraAuthorizer is not null)
            {
                var governed = await _cameraAuthorizer.AuthorizeCameraSetupAsync(invocation, readOnly,
                    operationId, targetId, kind, cancellationToken).ConfigureAwait(false);
                if (!governed.Authorized) return governed;
                if (governed.PrincipalId != currentPrincipal || governed.SessionId != sessionId)
                {
                    governed.Reservation?.Dispose();
                    return CameraSetupAuthorization.Denied("SessionChanged");
                }
                return governed;
            }

            // Mutations must cross the governed LocalAuthorizationService seam.
            // The read-only query fallback exists only for hosts that expose the
            // established identity projection before the camera authorizer is wired.
            if (!readOnly) return CameraSetupAuthorization.Denied("AuthorizationUnavailable");

            var authorization = await _identityQuery.GetCurrentAuthorizationAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (!authorization.Available || authorization.Account is not { } account)
                return CameraSetupAuthorization.Denied(SafeReason(authorization.ReasonCode));
            if (account.PrincipalId != currentPrincipal ||
                !account.Permissions.Contains(Permission.ManageCameraBindings))
                return CameraSetupAuthorization.Denied("PermissionDenied");

            var latest = _sessions.Current;
            if (latest.State != InteractiveSessionState.Authenticated || latest.SessionId != sessionId ||
                latest.PrincipalId != current.PrincipalId)
                return CameraSetupAuthorization.Denied("SessionChanged");
            return new CameraSetupAuthorization(true, "Authorized", currentPrincipal, sessionId,
                account.AuthorizationRevision, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return CameraSetupAuthorization.Denied("AuthorizationUnavailable"); }
    }

    private string? CheckMutationPreconditions(string role, long expectedRevision,
        string? expectedHash, bool allowUnbound)
    {
        var station = _readStation();
        if (station.Disposed || station.ShutdownRequested) return "RuntimeStopped";
        if (station.Ready || station.ArmState != ProductionArmState.Disarmed)
            return "CameraSetupRequiresDisarmedStation";
        if (station.Busy || station.CurrentExecution is not null)
            return "CameraSetupInspectionConflict";
        if (station.EvidencePendingDeliveries != 0 || station.Handshake is HandshakePhase.AwaitingResultAck or
            HandshakePhase.AwaitingAckReset)
            return "CameraSetupDeliveryConflict";
        if (station.Mode is not ExclusiveMode.None and not ExclusiveMode.Maintenance)
            return "CameraSetupModeConflict";
        if (station.Recovery == RecoveryState.InProgress || station.LastCommandPending)
            return "CameraSetupWorkflowConflict";
        if (station.ActiveRecipe is not null) return "CameraSetupActiveRecipeConflict";

        lock (_stateSync)
        {
            if (_retainedDevices.Count != 0 || _retiringDevices.Count != 0)
                return "CameraDeviceCleanupRequired";
            if (!_slots.TryGetValue(role, out var slot) || slot.Snapshot?.Binding is not { } binding)
            {
                if (!allowUnbound) return "CameraBindingMissing";
                return expectedRevision == 0 && expectedHash is null ? null : "CameraBindingRevisionConflict";
            }
            if (expectedRevision != binding.Revision || expectedHash != binding.RevisionHash)
                return "CameraBindingRevisionConflict";
            return null;
        }
    }

    private async ValueTask<(bool Succeeded, string ReasonCode, bool Pending)> LoadPersistedAsync(
        string logicalRole, CancellationToken cancellationToken)
    {
        if (_persistence is null) return (true, "CameraSetupMemoryOnly", false);

        CameraSetupPersistentState state;
        try
        {
            state = await _persistence.ReadCameraSetupAsync(logicalRole, cancellationToken)
                .ConfigureAwait(false);
            if (state is null) return (false, "CameraSetupUnavailable", false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return (false, "CameraSetupUnavailable", false); }

        if (state.Snapshot is null)
            return (true, SafeReason(state.ReasonCode), state.Pending);

        CameraSetupSnapshot? toPublish = null;
        lock (_stateSync)
        {
            if (_disposed) return (false, "CameraSetupRuntimeStopped", false);
            if (!_slots.TryGetValue(logicalRole, out var slot))
            {
                if (_slots.Count >= MaximumLogicalRoles)
                    return (false, "CameraLogicalRoleCapacityExceeded", false);
                slot = new CameraSlot(logicalRole);
                _slots.Add(logicalRole, slot);
            }

            // A live in-memory device is newer than a read-only restart projection.
            // Never replace a successful operation while a caller is querying again.
            if (slot.Device is null)
            {
                var normalized = NormalizePersistedSnapshot(state.Snapshot, state.Pending);
                if (slot.Snapshot is null ||
                    !string.Equals(slot.Snapshot.Binding?.RevisionHash,
                        normalized.Binding?.RevisionHash, StringComparison.Ordinal) ||
                    slot.Snapshot.Health.Configuration == CameraConfigurationState.Applied)
                {
                    slot.Snapshot = normalized;
                    toPublish = normalized;
                }
            }
        }

        if (toPublish is not null) Publish(logicalRole, toPublish);
        return (true, SafeReason(state.ReasonCode), state.Pending);
    }

    private static CameraSetupSnapshot NormalizePersistedSnapshot(CameraSetupSnapshot value, bool pending)
    {
        var sourceHealth = value.Health;
        var health = new CameraHealthSnapshot(
            value.Binding is null ? CameraProviderAvailability.DependencyMissing :
                CameraProviderAvailability.Available,
            CameraConnectionState.Closed,
            pending ? CameraConfigurationState.Unknown : CameraConfigurationState.Unconfigured,
            CameraAcquisitionState.Stopped, sourceHealth.ObservedAt, sourceHealth.LastFault);
        return new CameraSetupSnapshot(value.LogicalRole, value.Binding, health, value.Requested,
            effective: null, differences: Array.Empty<CameraConfigurationDifference>(), value.Extension,
            pending ? "CameraSetupOperationPending" : "CameraSetupRestarted");
    }

    private async ValueTask<CameraBindingRevision?> ReadCommittedBindingAsync(
        string logicalRole, CameraBindingRevision expected)
    {
        if (_persistence is null) return null;
        try
        {
            var state = await _persistence.ReadCameraSetupAsync(logicalRole, CancellationToken.None)
                .ConfigureAwait(false);
            var binding = state.Snapshot?.Binding;
            return binding is not null && binding.Revision == expected.Revision &&
                binding.LogicalRole == expected.LogicalRole &&
                binding.RevisionHash == expected.RevisionHash
                ? binding : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return null; }
    }

    private CameraSlot? GetSlot(string role)
    {
        lock (_stateSync)
            return _slots.TryGetValue(role, out var slot) ? slot : null;
    }

    private long BeginGeneration(string role)
    {
        lock (_stateSync)
        {
            if (!_slots.TryGetValue(role, out var slot))
            {
                if (_slots.Count >= MaximumLogicalRoles)
                    throw new InvalidOperationException("CameraLogicalRoleCapacityExceeded");
                slot = new CameraSlot(role);
                _slots.Add(role, slot);
            }
            slot.Generation = checked(slot.Generation + 1);
            return slot.Generation;
        }
    }

    private ICameraDevice? InstallDevice(string role, long generation, ICameraDevice device,
        CameraSetupSnapshot snapshot)
    {
        lock (_stateSync)
        {
            if (_disposed || !_slots.TryGetValue(role, out var slot) || slot.Generation != generation)
                return device;
            slot.Device = device;
            slot.Snapshot = snapshot;
            return null;
        }
    }

    private async Task DisposeDeviceForGenerationAsync(string role, long generation)
    {
        ICameraDevice? device = null;
        lock (_stateSync)
        {
            if (_slots.TryGetValue(role, out var slot) && slot.Generation == generation)
            {
                device = slot.Device;
                if (device is null || _retainedDevices.Contains(device) ||
                    _retiringDevices.Contains(device))
                    device = null;
            }
        }
        if (device is not null)
            await DisposeDeviceBoundedAsync(device, _options.OperationTimeout).ConfigureAwait(false);
    }

    private async Task<DeviceCloseOutcome> CloseSlotDeviceAsync(CameraSlot? slot)
    {
        ICameraDevice? device = null;
        HealthProbeRegistration? activeProbe = null;
        if (slot is not null)
        {
            lock (_stateSync)
            {
                device = slot.Device;
                if (device is not null)
                {
                    if (_retainedDevices.Contains(device))
                        return DeviceCloseOutcome.Retained;
                    _retiringDevices.Add(device);
                    _healthProbes.TryGetValue(device, out activeProbe);
                }
            }
        }

        if (device is null) return DeviceCloseOutcome.Closed;
        if (activeProbe is not null)
        {
            // Do not call Stop/Dispose while a heartbeat is in GetHealthSnapshot.
            // The probe's completion is the ownership handoff point.
            TrackDeferredPhysical(RetireAfterHealthProbeAsync(activeProbe.Completed.Task, device),
                () => ReleaseRetiringDevice(device));
            return DeviceCloseOutcome.Transferred;
        }

        var outcome = await StopAndDisposeBoundedAsync(device,
            () => ReleaseRetiringDevice(device)).ConfigureAwait(false);
        lock (_stateSync)
        {
            if (outcome == DeviceCloseOutcome.Closed)
                ReleaseRetiringDevice(device);
        }
        return outcome;
    }

    private async Task<DeviceCloseOutcome> StopAndDisposeBoundedAsync(
        ICameraDevice device, Action onRetired)
    {
        Task<CameraOperationResult>? stopTask = null;
        var physicalReserved = false;
        try
        {
            if (!TryReservePhysical())
            {
                // All physical slots may belong to provider calls which ignore
                // cancellation.  Keep the device owned by an explicit,
                // tracked waiter and close it as soon as one slot is released;
                // returning false must not detach an unclosed handle.
                TrackDeferredPhysical(StopAfterPhysicalCapacityAsync(device), onRetired);
                return DeviceCloseOutcome.Transferred;
            }
            physicalReserved = true;
            stopTask = device.StopAsync(CancellationToken.None).AsTask();
            var result = await stopTask.WaitAsync(PositiveTimeout(_options.OperationTimeout))
                .ConfigureAwait(false);
            if (result is null || !result.Succeeded)
            {
                var disposed = await DisposeWithOwnedPhysicalPermitAsync(device,
                    _options.OperationTimeout, onRetired).ConfigureAwait(false);
                if (disposed == DeviceDisposeOutcome.Transferred)
                    physicalReserved = false;
                return disposed switch
                {
                    DeviceDisposeOutcome.Completed => DeviceCloseOutcome.Closed,
                    DeviceDisposeOutcome.Transferred => DeviceCloseOutcome.Transferred,
                    _ => DeviceCloseOutcome.Retained
                };
            }
            var closed = await DisposeWithOwnedPhysicalPermitAsync(device,
                _options.OperationTimeout, onRetired).ConfigureAwait(false);
            if (closed == DeviceDisposeOutcome.Transferred)
                physicalReserved = false;
            return closed switch
            {
                DeviceDisposeOutcome.Completed => DeviceCloseOutcome.Closed,
                DeviceDisposeOutcome.Transferred => DeviceCloseOutcome.Transferred,
                _ => DeviceCloseOutcome.Retained
            };
        }
        catch (TimeoutException)
        {
            if (stopTask is not null && !stopTask.IsCompleted)
            {
                TrackPhysical(FinishLateStopAsync(stopTask, device), onRetired);
                physicalReserved = false;
                return DeviceCloseOutcome.Transferred;
            }
            var disposed = await DisposeWithOwnedPhysicalPermitAsync(device,
                _options.OperationTimeout, onRetired).ConfigureAwait(false);
            if (disposed == DeviceDisposeOutcome.Transferred)
                physicalReserved = false;
            return disposed switch
            {
                DeviceDisposeOutcome.Completed => DeviceCloseOutcome.Closed,
                DeviceDisposeOutcome.Transferred => DeviceCloseOutcome.Transferred,
                _ => DeviceCloseOutcome.Retained
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var disposed = await DisposeWithOwnedPhysicalPermitAsync(device,
                _options.OperationTimeout, onRetired).ConfigureAwait(false);
            if (disposed == DeviceDisposeOutcome.Transferred)
                physicalReserved = false;
            return disposed switch
            {
                DeviceDisposeOutcome.Completed => DeviceCloseOutcome.Closed,
                DeviceDisposeOutcome.Transferred => DeviceCloseOutcome.Transferred,
                _ => DeviceCloseOutcome.Retained
            };
        }
        finally
        {
            if (physicalReserved) ReleasePhysical();
        }
    }

    private async Task StopAfterPhysicalCapacityAsync(ICameraDevice device)
    {
        var physicalReserved = false;
        try
        {
            await _physicalCapacity.WaitAsync().ConfigureAwait(false);
            physicalReserved = true;
            try
            {
                _ = await device.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            await DisposeDeviceUnboundedAsync(device).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The deferred task remains the device owner even if the capacity
            // primitive is being torn down during shutdown.
            await DisposeDeviceUnboundedAsync(device).ConfigureAwait(false);
        }
        finally
        {
            if (physicalReserved) ReleasePhysical();
        }
    }

    private async Task FinishLateStopAsync(Task<CameraOperationResult> stopTask,
        ICameraDevice device)
    {
        try { _ = await stopTask.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        await DisposeDeviceUnboundedAsync(device).ConfigureAwait(false);
    }

    private async ValueTask<ICameraDevice?> OpenExactAsync(ProviderEntry provider,
        string stableIdentity, CancellationToken cancellationToken)
    {
        CameraOpenResult result;
        Task<CameraOpenResult>? providerTask = null;
        var physicalReserved = false;
        if (!TryReservePhysical()) throw new PhysicalCapacityExceededException();
        physicalReserved = true;
        try
        {
            providerTask = provider.Provider.OpenAsync(stableIdentity, cancellationToken).AsTask();
            result = await providerTask.WaitAsync(PositiveTimeout(_options.OperationTimeout),
                cancellationToken).ConfigureAwait(false);
            ReleasePhysical();
            physicalReserved = false;
        }
        catch (TimeoutException)
        {
            if (providerTask is not null)
            {
                TrackPhysical(DisposeLateOpenedDeviceAsync(providerTask));
                physicalReserved = false;
            }
            throw new OperationCanceledException();
        }
        catch (OperationCanceledException)
        {
            if (providerTask is not null)
            {
                TrackPhysical(DisposeLateOpenedDeviceAsync(providerTask));
                physicalReserved = false;
            }
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return null; }
        finally
        {
            if (physicalReserved) ReleasePhysical();
        }
            if (result is null || !result.Succeeded || result.Device is not { } device)
            return null;
        try
        {
            var descriptor = device.Descriptor;
            if (!SameProvider(descriptor.Provider, provider.Identity) ||
                !string.Equals(descriptor.StableDeviceIdentity, stableIdentity, StringComparison.Ordinal))
            {
                await DisposeDeviceBoundedAsync(device, _options.OperationTimeout).ConfigureAwait(false);
                return null;
            }
            return device;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await DisposeDeviceBoundedAsync(device, _options.OperationTimeout).ConfigureAwait(false);
            return null;
        }
    }

    private async Task<BoundedConfigurationResult> ApplyConfigurationBoundedAsync(
        ICameraDevice device, RequestedCameraConfiguration requested,
        CancellationToken callerCancellationToken, CancellationToken operationCancellationToken)
    {
        Task<CameraConfigurationResult>? providerTask = null;
        var physicalReserved = false;
        try
        {
            if (!TryReservePhysical())
                return new BoundedConfigurationResult(
                    CameraConfigurationResult.Failure("CameraPhysicalCapacityExceeded"), false);
            physicalReserved = true;
            providerTask = device.ApplyConfigurationAsync(requested, operationCancellationToken).AsTask();
            var result = await providerTask.WaitAsync(PositiveTimeout(_options.OperationTimeout),
                operationCancellationToken).ConfigureAwait(false);
            ReleasePhysical();
            physicalReserved = false;
            return new BoundedConfigurationResult(result, false);
        }
        catch (TimeoutException)
        {
            if (providerTask is not null && !providerTask.IsCompleted)
            {
                TrackPhysical(DisposeLateConfiguredDeviceAsync(providerTask, device));
                physicalReserved = false;
                return new BoundedConfigurationResult(
                    CameraConfigurationResult.Failure("CameraOperationTimeout"), true);
            }
            return new BoundedConfigurationResult(
                CameraConfigurationResult.Failure("CameraOperationTimeout"), false);
        }
        catch (OperationCanceledException)
        {
            if (providerTask is not null && !providerTask.IsCompleted)
            {
                TrackPhysical(DisposeLateConfiguredDeviceAsync(providerTask, device));
                physicalReserved = false;
                return new BoundedConfigurationResult(CameraConfigurationResult.Failure(
                    callerCancellationToken.IsCancellationRequested
                        ? "CameraOperationCancelled" : "CameraOperationTimeout"), true);
            }
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new BoundedConfigurationResult(
                CameraConfigurationResult.Failure("CameraConfigurationApplyFailed"), false);
        }
        finally
        {
            if (physicalReserved) ReleasePhysical();
        }
    }

    private async ValueTask<StoreWriteResult> AppendAdmissionAsync(Guid operationId,
        CommandInvocation invocation, AuditedCommandKind kind, CameraSetupAuthorization authorization,
        string reason, CameraSetupPersistenceEvent? cameraEvent, CancellationToken cancellationToken)
    {
        if (_audit is null) return new StoreWriteResult(false, "TraceStoreUnavailable");
        var fact = new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), operationId,
            _readStation().RuntimeEpoch, DateTimeOffset.UtcNow, kind, invocation.Source,
            invocation.PrincipalId, invocation.SessionId, invocation.StepUpGrantId,
            CommandAuditPhase.Outcome, CommandDisposition.Accepted, reason,
            authorization.PrincipalId.ToString("D"));
        try
        {
            if (_persistence is not null)
            {
                if (cameraEvent is null) return new StoreWriteResult(false, "TraceAuditUnavailable");
                return await _persistence.AppendCameraSetupAsync(
                    new CameraSetupPersistenceRequest(fact, cameraEvent),
                    new StoreDeadline(_audit.CommitTimeout), cancellationToken).ConfigureAwait(false);
            }
            return await _audit.AppendAsync(fact, new StoreDeadline(_audit.CommitTimeout), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new StoreWriteResult(false, "TraceAuditUnavailable"); }
    }

    private async ValueTask<StoreWriteResult> AppendTerminalAsync(CommandAuditFact admitted,
        CameraSetupPersistenceEvent? cameraEvent, string reason, bool completed,
        CancellationToken cancellationToken)
    {
        if (_audit is null) return new StoreWriteResult(false, "TraceStoreUnavailable");
        var terminal = admitted with { EventId = Guid.NewGuid(), OccurredAtUtc = DateTimeOffset.UtcNow,
            Phase = completed ? CommandAuditPhase.Completed : CommandAuditPhase.Failed,
            Disposition = null, ReasonCode = SafeReason(reason) };
        try
        {
            if (_persistence is not null)
            {
                if (cameraEvent is null) return new StoreWriteResult(false, "TraceAuditUnavailable");
                return await _persistence.AppendCameraSetupAsync(
                    new CameraSetupPersistenceRequest(terminal, cameraEvent),
                    new StoreDeadline(_audit.CommitTimeout), cancellationToken).ConfigureAwait(false);
            }
            return await _audit.AppendAsync(terminal, new StoreDeadline(_audit.CommitTimeout), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new StoreWriteResult(false, "TraceAuditUnavailable"); }
    }

    private static CameraSetupPersistenceEvent CreatePersistenceEvent(
        CameraSetupPersistencePhase phase, Guid operationId, string logicalRole,
        AuditedCommandKind commandKind, CameraBindingRevision? previousBinding,
        CameraBindingRevision? binding, CameraBindingTarget? target,
        RequestedCameraConfiguration? requested, EffectiveCameraConfiguration? effective,
        IEnumerable<CameraConfigurationDifference>? differences,
        CameraProviderExtensionRequirement? extension, CameraHealthSnapshot? health,
        bool succeeded, string reasonCode, string changeReason,
        CameraSetupAuthorization authorization) =>
        new(phase, operationId, logicalRole, commandKind, previousBinding, binding, target,
            requested, effective, differences, extension, health, succeeded, SafeReason(reasonCode),
            changeReason, authorization.PrincipalId, authorization.SessionId,
            authorization.AuthorizationRevision, authorization.Reservation);

    private static CameraSetupPersistenceEvent CreateTerminalFailureEvent(
        CameraSetupPersistenceEvent admission, string reason)
    {
        return new CameraSetupPersistenceEvent(CameraSetupPersistencePhase.Terminal,
            admission.OperationId, admission.LogicalRole, admission.CommandKind,
            admission.PreviousBinding, binding: null, admission.Target, admission.Requested,
            effective: null, differences: null, admission.Extension, FailureHealth(reason),
            succeeded: false, SafeReason(reason), admission.ChangeReason,
            admission.ActorPrincipalId, admission.SessionId, admission.AuthorizationRevision,
            admission.Reservation);
    }

    private CameraBindingRevision CreateBindingRevision(CameraRebindRequest request,
        CameraSetupAuthorization authorization, CameraBindingRevision? previous)
    {
        var revision = checked((previous?.Revision ?? 0) + 1);
        var previousHash = previous?.RevisionHash;
        var recorded = DateTimeOffset.UtcNow;
        var revisionHash = HashParts("camera-binding-revision-v1", request.LogicalRole,
            revision.ToString(System.Globalization.CultureInfo.InvariantCulture), request.OperationId.ToString("D"),
            previousHash, request.Target.ContentHash, authorization.PrincipalId.ToString("D"),
            authorization.SessionId.ToString("D"), authorization.AuthorizationRevision.ToString(
                System.Globalization.CultureInfo.InvariantCulture), request.ChangeReason, recorded.ToString("O"));
        // Position belongs to the global writer stream.  Until the committed
        // row is read back, zero explicitly means "writer assigned"; using the
        // per-role revision here would fabricate a cross-role position.
        return new CameraBindingRevision(0, request.LogicalRole, revision, request.OperationId,
            previousHash, revisionHash, request.Target, authorization.PrincipalId, authorization.SessionId,
            authorization.AuthorizationRevision, request.ChangeReason, recorded);
    }

    private static CameraHealthSnapshot? ReadValidHealth(ICameraDevice device)
    {
        try { return device.GetHealthSnapshot(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return null; }
    }

    private async Task<BoundedHealthResult> ReadHealthBoundedAsync(
        ICameraDevice device, CancellationToken callerCancellationToken,
        CancellationToken operationCancellationToken)
    {
        Task<CameraHealthSnapshot?>? providerTask = null;
        var physicalReserved = false;
        try
        {
            if (!TryReservePhysical())
                return new BoundedHealthResult(null, "CameraPhysicalCapacityExceeded", false);

            physicalReserved = true;
            providerTask = Task.Run(() => ReadValidHealth(device));
            var health = await providerTask.WaitAsync(PositiveTimeout(_options.OperationTimeout),
                operationCancellationToken).ConfigureAwait(false);
            ReleasePhysical();
            physicalReserved = false;
            return new BoundedHealthResult(health, null, false);
        }
        catch (TimeoutException)
        {
            if (providerTask is not null)
            {
                TrackPhysical(DisposeLateHealthDeviceAsync(providerTask, device));
                physicalReserved = false;
                return new BoundedHealthResult(null, "CameraOperationTimeout", true);
            }
            return new BoundedHealthResult(null, "CameraOperationTimeout", false);
        }
        catch (OperationCanceledException)
        {
            if (providerTask is not null)
            {
                TrackPhysical(DisposeLateHealthDeviceAsync(providerTask, device));
                physicalReserved = false;
                return new BoundedHealthResult(null,
                    callerCancellationToken.IsCancellationRequested
                        ? "CameraOperationCancelled" : "CameraOperationTimeout", true);
            }
            return new BoundedHealthResult(null,
                callerCancellationToken.IsCancellationRequested
                    ? "CameraOperationCancelled" : "CameraOperationTimeout", false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new BoundedHealthResult(null, "CameraHealthUnavailable", false);
        }
        finally
        {
            if (physicalReserved) ReleasePhysical();
        }
    }

    private static CameraHealthSnapshot FailureHealth(string reason) => new(
        CameraProviderAvailability.Faulted, CameraConnectionState.Closed,
        CameraConfigurationState.Unknown, CameraAcquisitionState.Stopped,
        new FrameTimePoint(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp()),
        new CameraFault(CameraFaultClassification.DeviceFault, SafeReason(reason)));

    private void Publish(string role, CameraSetupSnapshot snapshot)
    {
        try { _publishSetup(role, snapshot); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    private CameraSetupSnapshot? GetSnapshot(string role)
    {
        lock (_stateSync) return _slots.TryGetValue(role, out var slot) ? slot.Snapshot : null;
    }

    private static string? ValidateMutationInput(Guid operationId, CommandInvocation invocation,
        string role, CameraBindingTarget? target, string changeReason)
    {
        if (operationId == Guid.Empty) return "CameraOperationIdRequired";
        if (invocation is null || !Enum.IsDefined(invocation.Source)) return "InvalidCommandContext";
        if (!ValidLogicalRole(role)) return "CameraLogicalRoleInvalid";
        if (target is not null && !ValidStableDeviceIdentity(target.StableDeviceIdentity))
            return "CameraStableDeviceIdentityInvalid";
        if (string.IsNullOrWhiteSpace(changeReason) || changeReason.Length > 256 ||
            changeReason.Any(char.IsControl)) return "CameraChangeReasonInvalid";
        return null;
    }

    private ProviderEntry? FindProvider(CameraProviderIdentity identity) =>
        _providers.SingleOrDefault(entry => SameProvider(entry.Identity, identity));

    private static bool SameProvider(CameraProviderIdentity left, CameraProviderIdentity right) =>
        left is not null && right is not null &&
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
        string.Equals(left.AdapterPackageId, right.AdapterPackageId, StringComparison.Ordinal) &&
        string.Equals(left.AdapterVersion, right.AdapterVersion, StringComparison.Ordinal);

    private static bool ValidLogicalRole(string? value) => value is { Length: > 0 and <= 64 } &&
        value.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
            >= '0' and <= '9' or '.' or '_' or '-');

    private static bool ValidStableDeviceIdentity(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
            >= '0' and <= '9' or '.' or '_' or '-' or ':');

    private static string SafeReason(string? reason)
    {
        if (reason is null || reason.Length == 0 || reason.Length > MaximumReasonLength ||
            reason.Any(character => !IsSafeReasonCharacter(character)))
            return "CameraSetupUnavailable";
        return reason;
    }

    private static bool IsSafeReasonCharacter(char character) =>
        character is >= 'A' and <= 'Z' || character is >= 'a' and <= 'z' ||
        character is >= '0' and <= '9' || character is '_' or '-';

    private static string HashParts(params string?[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var value in values)
        {
            var bytes = value is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(value);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, value is null ? -1 : bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private CancellationTokenSource CreateOperationToken(CancellationToken caller)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, caller);
        source.CancelAfter(_options.OperationTimeout);
        return source;
    }

    private static TimeSpan PositiveTimeout(TimeSpan timeout) => timeout > TimeSpan.Zero ? timeout : TimeSpan.FromMilliseconds(1);

    private async ValueTask<bool> WaitForOperationGateAsync(CancellationToken caller)
    {
        // Keep the queue wait cancellable by shutdown/caller while using the
        // semaphore timeout as the deterministic internal busy bound.  The
        // operation CTS has its own provider deadline and is intentionally not
        // used here, avoiding a timeout/cancellation race at the same instant.
        using var gateCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token, caller);
        return await _operationGate.WaitAsync(PositiveTimeout(_options.OperationTimeout),
            gateCancellation.Token).ConfigureAwait(false);
    }

    private TaskCompletionSource<bool> RegisterInFlight()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateSync) _inFlight.Add(completion.Task);
        return completion;
    }

    private void CompleteInFlight(TaskCompletionSource<bool>? completion)
    {
        if (completion is null) return;
        completion.TrySetResult(true);
        lock (_stateSync) _inFlight.Remove(completion.Task);
    }

    private void RetainDevice(ICameraDevice device)
    {
        lock (_stateSync) _retainedDevices.Add(device);
    }

    private void ReleaseRetiringDevice(ICameraDevice device)
    {
        lock (_stateSync)
        {
            _retiringDevices.Remove(device);
            if (_retainedDevices.Contains(device)) return;
            foreach (var slot in _slots.Values)
            {
                if (ReferenceEquals(slot.Device, device))
                    slot.Device = null;
            }
        }
    }

    private async Task RetireAfterHealthProbeAsync(Task probeCompletion, ICameraDevice device)
    {
        try { await probeCompletion.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        await StopAfterPhysicalCapacityAsync(device).ConfigureAwait(false);
    }

    private async Task DisposeDeviceUnboundedAsync(ICameraDevice device)
    {
        try { await device.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A late cleanup fault does not prove that the physical handle is
            // gone. Retain it as a strong owner and keep future mutations closed.
            RetainDevice(device);
        }
    }

    private async Task<DeviceDisposeOutcome> DisposeDeviceBoundedAsync(
        ICameraDevice device, TimeSpan timeout)
    {
        HealthProbeRegistration? activeProbe = null;
        lock (_stateSync)
        {
            if (_retainedDevices.Contains(device))
                return DeviceDisposeOutcome.Faulted;
            if (_retiringDevices.Contains(device))
                return DeviceDisposeOutcome.Transferred;
            _retiringDevices.Add(device);
            _healthProbes.TryGetValue(device, out activeProbe);
        }

        if (activeProbe is not null)
        {
            TrackDeferredPhysical(RetireAfterHealthProbeAsync(activeProbe.Completed.Task, device),
                () => ReleaseRetiringDevice(device));
            return DeviceDisposeOutcome.Transferred;
        }

        if (!TryReservePhysical())
        {
            lock (_stateSync) _retiringDevices.Add(device);
            TrackDeferredPhysical(DisposeAfterPhysicalCapacityAsync(device),
                () => ReleaseRetiringDevice(device));
            return DeviceDisposeOutcome.Transferred;
        }

        Task? disposeTask = null;
        var physicalReserved = true;
        try
        {
            disposeTask = device.DisposeAsync().AsTask();
            await disposeTask.WaitAsync(PositiveTimeout(timeout)).ConfigureAwait(false);
            ReleaseRetiringDevice(device);
            return DeviceDisposeOutcome.Completed;
        }
        catch (TimeoutException)
        {
            if (disposeTask is not null)
            {
                TrackPhysical(ObserveDeviceDisposeAsync(disposeTask, device),
                    () => ReleaseRetiringDevice(device));
                physicalReserved = false;
                return DeviceDisposeOutcome.Transferred;
            }
            // A TimeoutException raised before a ValueTask was returned is a
            // synchronous provider fault, not our bounded wait expiring. The
            // handle is therefore retained just like every other Dispose fault.
            RetainDevice(device);
            return DeviceDisposeOutcome.Faulted;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A faulted DisposeAsync does not prove that the physical handle is
            // gone. Retain it and fail closed rather than opening a replacement.
            RetainDevice(device);
            return DeviceDisposeOutcome.Faulted;
        }
        finally
        {
            if (physicalReserved) ReleasePhysical();
        }
    }

    private async Task<DeviceDisposeOutcome> DisposeWithOwnedPhysicalPermitAsync(
        ICameraDevice device, TimeSpan timeout, Action onRetired)
    {
        lock (_stateSync)
        {
            if (_retainedDevices.Contains(device))
                return DeviceDisposeOutcome.Faulted;
        }
        Task? disposeTask = null;
        try
        {
            disposeTask = device.DisposeAsync().AsTask();
            await disposeTask.WaitAsync(PositiveTimeout(timeout)).ConfigureAwait(false);
            return DeviceDisposeOutcome.Completed;
        }
        catch (TimeoutException)
        {
            if (disposeTask is not null)
            {
                TrackPhysical(ObserveDeviceDisposeAsync(disposeTask, device), onRetired);
                return DeviceDisposeOutcome.Transferred;
            }
            // Do not treat a synchronous provider TimeoutException as a
            // successful close. The physical owner remains uncertain.
            RetainDevice(device);
            return DeviceDisposeOutcome.Faulted;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RetainDevice(device);
            return DeviceDisposeOutcome.Faulted;
        }
    }

    private async Task DisposeAfterPhysicalCapacityAsync(ICameraDevice device)
    {
        var physicalReserved = false;
        try
        {
            await _physicalCapacity.WaitAsync().ConfigureAwait(false);
            physicalReserved = true;
            await DisposeDeviceUnboundedAsync(device).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // If shutdown tears down the capacity primitive, this tracked task
            // still owns the device and makes one best-effort final disposal.
            await DisposeDeviceUnboundedAsync(device).ConfigureAwait(false);
        }
        finally
        {
            if (physicalReserved) ReleasePhysical();
        }
    }

    private async Task ObserveDeviceDisposeAsync(Task disposeTask, ICameraDevice device)
    {
        try { await disposeTask.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RetainDevice(device);
        }
    }

    private async Task DisposeLateOpenedDeviceAsync(Task<CameraOpenResult> providerTask)
    {
        try
        {
            var result = await providerTask.ConfigureAwait(false);
            if (result?.Device is { } device) await DisposeDeviceUnboundedAsync(device).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    private async Task DisposeLateHealthDeviceAsync(
        Task<CameraHealthSnapshot?> providerTask, ICameraDevice device)
    {
        try { _ = await providerTask.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        await DisposeDeviceUnboundedAsync(device).ConfigureAwait(false);
    }

    private async Task DisposeLateConfiguredDeviceAsync(
        Task<CameraConfigurationResult> providerTask, ICameraDevice device)
    {
        try
        {
            try { _ = await providerTask.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            try { await device.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            await DisposeDeviceUnboundedAsync(device).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    private static async Task ObserveDiscardedAsync<T>(Task<T> providerTask)
    {
        try { _ = await providerTask.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    private void TrackPhysical(Task task, Action? completedCallback = null)
    {
        // The caller transfers an already acquired physical-operation slot to
        // this cleanup task.  It is released only after the provider call and
        // all owned cleanup have returned.
        lock (_stateSync) _physicalInFlight.Add(task);
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            lock (_stateSync) _physicalInFlight.Remove(task);
            ReleasePhysical();
            try { completedCallback?.Invoke(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void TrackDeferredPhysical(Task task, Action? completedCallback = null)
    {
        // This task is waiting for a physical slot and therefore does not own
        // one yet.  It is still tracked so bounded shutdown cannot forget its
        // device owner or dispose the semaphore while it is waiting.
        lock (_stateSync) _physicalInFlight.Add(task);
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            lock (_stateSync) _physicalInFlight.Remove(task);
            try { completedCallback?.Invoke(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool TryReservePhysical()
    {
        try { return _physicalCapacity.Wait(0); }
        catch (ObjectDisposedException) { return false; }
    }

    private void ReleasePhysical()
    {
        try { _physicalCapacity.Release(); }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
    }

    internal void CancelOperations() => _lifetime.Cancel();

    public async ValueTask DisposeAsync()
    {
        Task[] inFlight;
        lock (_stateSync)
        {
            if (_disposed) return;
            _disposed = true;
            inFlight = _inFlight.ToArray();
        }
        _lifetime.Cancel();
        var drained = true;
        try { await DrainTrackedWorkAsync(inFlight).WaitAsync(_options.ShutdownTimeout).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { drained = false; }

        ICameraDevice[] devices;
        lock (_stateSync)
        {
            devices = _slots.Values.Select(slot => slot.Device).Where(device => device is not null)
                .Cast<ICameraDevice>().ToArray();
        }
        foreach (var device in devices)
            await DisposeDeviceBoundedAsync(device, _options.ShutdownTimeout).ConfigureAwait(false);
        // A bounded shutdown may return while a provider ignores cancellation.
        // Keep synchronization primitives alive until every tracked holder has
        // released them; disposing a semaphore here would turn a late physical
        // completion into ObjectDisposedException in the command/query finally.
        if (drained && NoTrackedWork()) DisposeSynchronizationResources();
        else _ = DisposeSynchronizationResourcesWhenDrainedAsync();
    }

    private bool NoTrackedWork()
    {
        lock (_stateSync) return _inFlight.Count == 0 && _physicalInFlight.Count == 0;
    }

    private async Task DisposeSynchronizationResourcesWhenDrainedAsync()
    {
        while (true)
        {
            Task[] work;
            lock (_stateSync) work = _inFlight.Concat(_physicalInFlight).ToArray();
            if (work.Length == 0) break;
            try { await Task.WhenAll(work).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
        DisposeSynchronizationResources();
    }

    private void DisposeSynchronizationResources()
    {
        _operationGate.Dispose();
        _queryGate.Dispose();
        _physicalCapacity.Dispose();
        _lifetime.Dispose();
    }

    private async Task DrainTrackedWorkAsync(Task[] initialOperations)
    {
        if (initialOperations.Length != 0)
            await Task.WhenAll(initialOperations).ConfigureAwait(false);

        while (true)
        {
            Task[] physical;
            lock (_stateSync) physical = _physicalInFlight.ToArray();
            if (physical.Length == 0) return;
            await Task.WhenAll(physical).ConfigureAwait(false);
        }
    }

    internal sealed record CameraStationContext(Guid RuntimeEpoch, bool Ready,
        ProductionArmState ArmState, bool Busy, ExecutionCorrelationId? CurrentExecution,
        int EvidencePendingDeliveries, HandshakePhase Handshake, ExclusiveMode Mode,
        RecoveryState Recovery, bool LastCommandPending, RecipeReference? ActiveRecipe,
        bool ShutdownRequested, bool Disposed);

    private sealed class ProviderEntry
    {
        internal ProviderEntry(ICameraProvider provider, CameraProviderIdentity identity)
        { Provider = provider; Identity = identity; }
        internal ICameraProvider Provider { get; }
        internal CameraProviderIdentity Identity { get; }
    }

    private sealed class CameraSlot
    {
        internal CameraSlot(string role) => LogicalRole = role;
        internal string LogicalRole { get; }
        internal long Generation { get; set; }
        internal ICameraDevice? Device { get; set; }
        internal CameraSetupSnapshot? Snapshot { get; set; }
    }

    private sealed class HealthProbeRegistration
    {
        internal TaskCompletionSource<bool> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record BoundedConfigurationResult(
        CameraConfigurationResult Result, bool DeviceTransferred);

    private sealed record BoundedHealthResult(
        CameraHealthSnapshot? Health, string? ReasonCode, bool DeviceTransferred);

    private enum DeviceCloseOutcome
    {
        Closed,
        Transferred,
        Retained
    }

    private enum DeviceDisposeOutcome
    {
        Completed,
        Transferred,
        Faulted
    }

    private sealed class PhysicalCapacityExceededException : Exception
    {
        internal PhysicalCapacityExceededException() : base("CameraPhysicalCapacityExceeded") { }
    }

}
