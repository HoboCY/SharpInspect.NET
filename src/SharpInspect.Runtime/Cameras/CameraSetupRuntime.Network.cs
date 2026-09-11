using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Cameras;

internal sealed partial class CameraSetupRuntime
{
    private const int MaximumNetworkQueries = 16;
    private readonly SemaphoreSlim _networkQueryGate = new(4, 4);
    private int _networkOutstandingQueries;
    private readonly ICameraNetworkPersistence? _networkPersistence;
    private Func<CancellationToken, ValueTask<string?>>? _reserveNetworkStation;
    private Action? _releaseNetworkStation;
    private Action<CameraNetworkSnapshot>? _publishNetwork;
    private NetworkOperation? _networkOperation;
    private CameraNetworkSnapshot? _networkLastSnapshot;
    private bool _networkReconciliationRequired;

    internal void ConfigureNetworkMaintenance(
        Func<CancellationToken, ValueTask<string?>> reserveStation, Action releaseStation,
        Action<CameraNetworkSnapshot> publish)
    {
        _reserveNetworkStation = reserveStation;
        _releaseNetworkStation = releaseStation;
        _publishNetwork = publish;
    }

    public async ValueTask<CameraNetworkQueryResult> GetNetworkMaintenanceAsync(CameraBindingTarget target,
        CommandInvocation invocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource timeout;
        TaskCompletionSource<bool> drain;
        lock (_stateSync)
        {
            if (_disposed || _lifetime.IsCancellationRequested) return new(false, "RuntimeStopped");
            if (_networkOutstandingQueries >= MaximumNetworkQueries)
                return new(false, "CameraNetworkQueryCapacityExceeded");
            _networkOutstandingQueries++;
            timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            timeout.CancelAfter(_options.OperationTimeout);
            drain = RegisterInFlight();
        }

        // Keep the actual authorization/read owned after a caller deadline. A
        // timed-out native read does not release its permit until it returns.
        var work = Task.Run(async () =>
        {
            var entered = false;
            try
            {
                await _networkQueryGate.WaitAsync(timeout.Token).ConfigureAwait(false);
                entered = true;
                timeout.Token.ThrowIfCancellationRequested();
                var result = await ReadNetworkMaintenanceCoreAsync(target, invocation, timeout.Token)
                    .ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            { return new CameraNetworkQueryResult(false, "CameraNetworkQueryDeadlineExceeded"); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { return new CameraNetworkQueryResult(false, "CameraNetworkEvidenceUnavailable"); }
            finally
            {
                if (entered) _networkQueryGate.Release();
                timeout.Dispose();
                lock (_stateSync) _networkOutstandingQueries--;
                CompleteInFlight(drain);
            }
        });
        try
        {
            return await work.WaitAsync(_options.OperationTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) { return new(false, "CameraNetworkQueryDeadlineExceeded"); }
    }

    private async ValueTask<CameraNetworkQueryResult> ReadNetworkMaintenanceCoreAsync(CameraBindingTarget target,
        CommandInvocation invocation, CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAsync(invocation, true, Guid.Empty, target.ContentHash,
            AuditedCommandKind.Unsupported, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!authorization.Authorized) return new(false, authorization.ReasonCode);
        if (_networkPersistence is null) return new(false, "CameraNetworkEvidenceUnavailable");
        try
        {
            CameraNetworkSnapshot? snapshot;
            lock (_stateSync)
                snapshot = _networkLastSnapshot?.Target == target ? _networkLastSnapshot : null;
            snapshot ??= await _networkPersistence.ReadLatestAsync(target, cancellationToken).ConfigureAwait(false);
            if (snapshot is null) return new(true, "CameraNetworkMaintenanceNotRecorded");
            return new(true, snapshot.ReasonCode, snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(false, "CameraNetworkEvidenceUnavailable"); }
    }

    public async ValueTask<CameraNetworkOperationResult> ChangeNetworkConfigurationAsync(
        CameraNetworkChangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty || request.Invocation is null || request.Target is null ||
            request.Requested is null || request.ChangeReason is not { Length: > 0 and <= 128 } ||
            request.ChangeReason.Any(char.IsControl))
            return new(false, "CameraNetworkRequestInvalid", AuditPersistence.NotAttempted);
        if (cancellationToken.IsCancellationRequested)
            return new(false, "CameraNetworkOperationCancelled", AuditPersistence.NotAttempted);

        NetworkOperation? operation = null;
        lock (_stateSync)
        {
            if (_disposed) return new(false, "RuntimeStopped", AuditPersistence.NotAttempted);
            if (_networkOperation is null && _operationGate.Wait(0))
            {
                operation = new NetworkOperation(request, _lifetime.Token, RegisterInFlight());
                _networkOperation = operation;
            }
        }
        if (operation is null)
            return await RejectNetworkBusyAsync(request, cancellationToken).ConfigureAwait(false);

        // All provider entry calls, including synchronous SDK work before a ValueTask
        // is returned, execute outside the caller and station locks.
        var task = Task.Run(() => RunNetworkOperationAsync(operation));
        operation.Work = task;
        try
        {
            return await task.WaitAsync(_options.OperationTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            operation.RequestCancellation();
            var reason = cancellationToken.IsCancellationRequested
                ? "CameraNetworkOperationCancelled" : "CameraNetworkOperationTimeout";
            var snapshot = NetworkSnapshot(operation, CameraNetworkMaintenanceState.Unknown, reason);
            // A caller deadline is not a newer device/audit fact. The owned worker
            // already projects Pending while it works and alone publishes its terminal.
            // Overwriting that projection here could replace a committed Succeeded
            // terminal when completion races with this timeout continuation.
            // An admission may be durable, but this caller-timeout outcome has not
            // yet completed its own terminal audit transaction.
            return new(false, reason, AuditPersistence.NotAttempted, snapshot);
        }
    }

    private async Task<CameraNetworkOperationResult> RunNetworkOperationAsync(NetworkOperation operation)
    {
        var request = operation.Request;
        var token = operation.Cancellation.Token;
        var state = CameraNetworkMaintenanceState.Failed;
        var reason = "CameraNetworkUnavailable";
        var retained = false;
        try
        {
            operation.ThrowIfCancellationRequested();
            operation.Authorization = await AuthorizeAsync(request.Invocation, false, request.OperationId,
                request.AuthorizationTargetId, AuditedCommandKind.ChangeCameraNetworkConfiguration, token)
                .ConfigureAwait(false);
            if (!operation.Authorization.Authorized)
                throw new NetworkRejectedException(operation.Authorization.ReasonCode);
            if (_networkPersistence is null) throw new NetworkRejectedException("CameraNetworkEvidenceUnavailable");
            var barrier = await CheckNetworkBarrierAsync(token).ConfigureAwait(false);
            if (barrier is not null) throw new NetworkRejectedException(barrier);
            if (_options.StationNetwork is not { } stationNetwork)
                throw new NetworkRejectedException("CameraStationNetworkRequired");
            if (!request.Requested.IsInSameSubnet(stationNetwork.Configuration))
                throw new NetworkRejectedException("CameraStationNetworkMismatch");
            if (request.Requested.Address == stationNetwork.Configuration.Address)
                throw new NetworkRejectedException("CameraNetworkStationAddressConflict");
            var provider = FindProvider(request.Target.Provider);
            if (provider is null) throw new NetworkRejectedException("CameraProviderNotRegistered");
            if (provider.Provider is not ICameraNetworkConfigurator configurator)
                throw new NetworkRejectedException("CameraNetworkMaintenanceUnsupported");
            var precondition = CheckNetworkStationPreconditions();
            if (precondition is not null) throw new NetworkRejectedException(precondition);
            if (_reserveNetworkStation is not null)
            {
                var reservationReason = await _reserveNetworkStation(token).ConfigureAwait(false);
                if (reservationReason is not null) throw new NetworkRejectedException(reservationReason);
                operation.StationReserved = true;
            }
            operation.ThrowIfCancellationRequested();
            await CloseNetworkDebugDevicesAsync(operation, token).ConfigureAwait(false);
            if (!TryReservePhysical()) throw new NetworkRejectedException("CameraPhysicalCapacityExceeded");
            operation.PhysicalReserved = true;
            var lease = await NetworkProviderCallAsync(() =>
                configurator.TryBeginMaintenanceAsync(request.Target.StableDeviceIdentity, token)).ConfigureAwait(false);
            operation.Session = lease?.Session;
            if (operation.Session is null)
                throw new NetworkRejectedException(SafeReason(lease?.ReasonCode ?? "CameraNetworkMaintenanceUnavailable"));
            operation.ThrowIfCancellationRequested();
            var sessionTarget = await NetworkProviderCallAsync(() =>
                ValueTask.FromResult(operation.Session.Target)).ConfigureAwait(false);
            if (sessionTarget != request.Target) throw new NetworkRejectedException("CameraNetworkIdentityMismatch");
            var previous = await NetworkProviderCallAsync(() => operation.Session.ReadCurrentAsync(token))
                .ConfigureAwait(false);
            operation.ThrowIfCancellationRequested();
            if (previous is not { Succeeded: true, State: not null } ||
                previous.State.Target != request.Target || previous.State.Configuration is null)
                throw new NetworkRejectedException("CameraNetworkPreviousUnavailable");
            operation.Previous = previous.State.Configuration;
            operation.Observed = operation.Previous;
            var admitted = await _networkPersistence.AppendAdmissionAsync(request, stationNetwork,
                operation.Previous, operation.Authorization, _readStation().RuntimeEpoch,
                NetworkAuditDeadline(), token).ConfigureAwait(false);
            if (!admitted.Result.Committed || admitted.Admission is null)
                throw new NetworkRejectedException(SafeReason(admitted.Result.ReasonCode));
            operation.Admission = admitted.Admission;
            operation.Authorization.Reservation?.Commit();
            PublishNetworkSnapshot(NetworkSnapshot(operation, CameraNetworkMaintenanceState.Pending,
                "CameraNetworkChangeAdmitted"), ShouldProjectNetwork(operation));
            operation.ThrowIfCancellationRequested();
            var conflict = await NetworkProviderCallAsync(() => operation.Session.DetectConflictAsync(
                request.Requested, stationNetwork, token)).ConfigureAwait(false);
            operation.ThrowIfCancellationRequested();
            if (conflict?.State != CameraNetworkConflictState.Clear)
                throw new NetworkRejectedException(conflict?.State == CameraNetworkConflictState.Conflict
                    ? "CameraNetworkAddressConflict" : "CameraNetworkConflictCheckUnavailable");
            precondition = CheckNetworkStationPreconditions();
            if (precondition is not null) throw new NetworkRejectedException(precondition);
            operation.ThrowIfCancellationRequested();
            operation.ApplyStarted = true;
            operation.Observed = null;
            PublishNetworkSnapshot(NetworkSnapshot(operation, CameraNetworkMaintenanceState.Pending,
                "CameraNetworkChangeInProgress"), ShouldProjectNetwork(operation));
            var applied = await NetworkProviderCallAsync(() => operation.Session.ApplyAsync(
                operation.Previous, request.Requested, token)).ConfigureAwait(false);
            operation.ThrowIfCancellationRequested();
            var discovery = await NetworkProviderCallAsync(() => provider.Provider.DiscoverAsync(token))
                .ConfigureAwait(false);
            operation.ThrowIfCancellationRequested();
            if (discovery is not { Succeeded: true } || discovery.Devices.Count(item =>
                    item.Provider == request.Target.Provider &&
                    item.StableDeviceIdentity == request.Target.StableDeviceIdentity) != 1)
                throw new NetworkRejectedException("CameraNetworkRediscoveryIdentityMismatch");
            var readback = await NetworkProviderCallAsync(() => operation.Session.ReadCurrentAsync(token))
                .ConfigureAwait(false);
            operation.ThrowIfCancellationRequested();
            if (readback is not { Succeeded: true, State: not null } || readback.State.Target != request.Target ||
                readback.State.Configuration is null)
                throw new NetworkRejectedException("CameraNetworkReadbackIdentityMismatch");
            operation.Observed = readback.State.Configuration;
            operation.IdentityVerified = true;
            if (applied?.Succeeded != true)
            {
                reason = "CameraNetworkApplyFailed";
                state = CameraNetworkMaintenanceState.Failed;
            }
            else if (operation.Observed != request.Requested)
            {
                reason = "CameraNetworkReadbackMismatch";
                state = CameraNetworkMaintenanceState.Unknown;
            }
            else
            {
                reason = "CameraNetworkChangedRecipeActivationRequired";
                state = CameraNetworkMaintenanceState.Succeeded;
            }
        }
        catch (NetworkRejectedException ex)
        {
            reason = ex.Reason;
            state = operation.ApplyStarted ? CameraNetworkMaintenanceState.Unknown : CameraNetworkMaintenanceState.Failed;
        }
        catch (OperationCanceledException)
        {
            reason = "CameraNetworkOperationCancelled";
            state = operation.ApplyStarted ? CameraNetworkMaintenanceState.Unknown : CameraNetworkMaintenanceState.Failed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reason = "CameraNetworkProviderFailure";
            state = operation.ApplyStarted ? CameraNetworkMaintenanceState.Unknown : CameraNetworkMaintenanceState.Failed;
        }
        finally
        {
            // Cancellation callbacks are provider work too. Seal further callback
            // dispatch after the final SDK call, then drain any dispatched callback
            // before invoking Dispose on the same session.
            await operation.BeginCleanupAsync().ConfigureAwait(false);
            // Cleanup is an actual owned operation. A failed or stuck Dispose cannot
            // release the station reservation or permit a second setup mutation.
            if (operation.Session is not null)
            {
                try
                {
                    await NetworkProviderCallAsync<bool>(async () =>
                    {
                        await operation.Session.DisposeAsync().ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    retained = true;
                    state = CameraNetworkMaintenanceState.Unknown;
                    reason = "CameraNetworkSessionDisposeFailed";
                }
            }
        }

        try
        {
            if (operation.IsCancellationRequested && state == CameraNetworkMaintenanceState.Succeeded)
            {
                state = CameraNetworkMaintenanceState.Unknown;
                reason = "CameraNetworkOperationCancelled";
            }
            var snapshot = NetworkSnapshot(operation, state, reason);
            StoreWriteResult? write = null;
            try
            {
                if (operation.Admission is not null)
                    write = await _networkPersistence!.AppendTerminalAsync(operation.Admission, snapshot,
                        NetworkAuditDeadline(), CancellationToken.None).ConfigureAwait(false);
                else if (_networkPersistence is not null)
                    write = await _networkPersistence.RecordRejectedAsync(request, _options.StationNetwork,
                        operation.Previous, operation.Authorization, _readStation().RuntimeEpoch, reason,
                        NetworkAuditDeadline(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            if (operation.Admission is not null && write?.Committed != true)
            {
                snapshot = NetworkSnapshot(operation, CameraNetworkMaintenanceState.Unknown,
                    "CameraNetworkAuditUnavailable");
            }
            PublishNetworkSnapshot(snapshot, ShouldProjectNetwork(operation) ||
                (retained && operation.StationReserved));
            lock (_stateSync)
                if (snapshot.State == CameraNetworkMaintenanceState.Unknown)
                    _networkReconciliationRequired = true;
            return new(snapshot.State == CameraNetworkMaintenanceState.Succeeded && write?.Committed == true,
                snapshot.ReasonCode, write?.Committed == true ? AuditPersistence.Persisted : AuditPersistence.Unavailable,
                snapshot);
        }
        finally
        {
            operation.Authorization?.Reservation?.Dispose();
            if (!retained)
            {
                if (operation.PhysicalReserved) ReleasePhysical();
                if (operation.StationReserved) _releaseNetworkStation?.Invoke();
                lock (_stateSync) _networkOperation = null;
                _operationGate.Release();
                CompleteInFlight(operation.Drain);
                operation.DisposeCancellation();
            }
            // On Dispose failure, _networkOperation owns session, cancellation,
            // drain and both reservations until process-level reconciliation.
        }
    }

    internal async ValueTask<string?> CheckNetworkBarrierAsync(CancellationToken cancellationToken)
    {
        lock (_stateSync)
            if (_networkReconciliationRequired) return "CameraNetworkReconciliationRequired";
        if (_networkPersistence is null) return null;
        try
        {
            if (await _networkPersistence.HasUnresolvedAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (_stateSync) _networkReconciliationRequired = true;
                return "CameraNetworkReconciliationRequired";
            }
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return "CameraNetworkEvidenceUnavailable"; }
    }

    private async ValueTask<CameraNetworkOperationResult> RejectNetworkBusyAsync(
        CameraNetworkChangeRequest request, CancellationToken cancellationToken)
    {
        const string reason = "CameraNetworkMaintenanceInProgress";
        if (_networkPersistence is null) return new(false, reason, AuditPersistence.NotAttempted);
        try
        {
            var result = await _networkPersistence.RecordRejectedAsync(request, _options.StationNetwork,
                null, null, _readStation().RuntimeEpoch, reason, NetworkAuditDeadline(), cancellationToken)
                .AsTask().WaitAsync(_options.OperationTimeout, cancellationToken).ConfigureAwait(false);
            return new(false, reason, result.Committed ? AuditPersistence.Persisted : AuditPersistence.Unavailable);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(false, reason, AuditPersistence.Unavailable); }
    }

    private string? CheckNetworkStationPreconditions()
    {
        var station = _readStation();
        if (station.Disposed || station.ShutdownRequested) return "RuntimeStopped";
        if (station.Ready || station.ArmState != ProductionArmState.Disarmed)
            return "CameraNetworkRequiresDisarmedStation";
        if (station.Busy || station.CurrentExecution is not null) return "CameraNetworkInspectionConflict";
        if (station.EvidencePendingDeliveries != 0 ||
            station.Handshake is HandshakePhase.AwaitingResultAck or HandshakePhase.AwaitingAckReset)
            return "CameraNetworkDeliveryConflict";
        if (station.Mode is not ExclusiveMode.None and not ExclusiveMode.Maintenance ||
            station.Recovery == RecoveryState.InProgress || station.LastCommandPending || station.ActiveRecipe is not null)
            return "CameraNetworkWorkflowConflict";
        lock (_stateSync)
            if (_retainedDevices.Count != 0 || _retiringDevices.Count != 0 || _physicalInFlight.Count != 0)
                return "CameraDeviceCleanupRequired";
        return null;
    }

    private async Task CloseNetworkDebugDevicesAsync(NetworkOperation operation, CancellationToken token)
    {
        var target = operation.Request.Target;
        CameraSlot[] slots;
        lock (_stateSync)
        {
            slots = _slots.Values.Where(slot => slot.Snapshot?.Binding?.Target == target).ToArray();
            operation.DebugDeviceInvalidated = slots.Any(slot => slot.Device is not null);
            foreach (var slot in slots) slot.Generation++;
        }
        foreach (var slot in slots)
        {
            operation.ThrowIfCancellationRequested();
            var result = await CloseSlotDeviceAsync(slot, token).ConfigureAwait(false);
            var old = slot.Snapshot!;
            var snapshot = new CameraSetupSnapshot(old.LogicalRole, old.Binding,
                FailureHealth("CameraRecipeActivationRequired"), old.Requested,
                extension: old.Extension, reasonCode: "CameraRecipeActivationRequired", capabilities: old.Capabilities);
            lock (_stateSync) slot.Snapshot = snapshot;
            Publish(old.LogicalRole, snapshot);
            if (result != DeviceCloseOutcome.Closed)
                throw new NetworkRejectedException("CameraDeviceCleanupRequired");
        }
    }

    private StoreDeadline NetworkAuditDeadline() => new(_audit?.CommitTimeout ?? TimeSpan.FromSeconds(2));

    private static CameraNetworkSnapshot NetworkSnapshot(NetworkOperation operation,
        CameraNetworkMaintenanceState state, string reason) => new(operation.Request.OperationId,
        operation.Request.Target, state, operation.Previous, operation.Request.Requested,
        operation.Observed, operation.IdentityVerified,
        SafeReason(reason), DateTimeOffset.UtcNow);

    private static bool ShouldProjectNetwork(NetworkOperation operation) =>
        operation.StationReserved && (operation.ApplyStarted || operation.DebugDeviceInvalidated);

    private void PublishNetworkSnapshot(CameraNetworkSnapshot snapshot, bool projectToStation = false)
    {
        lock (_stateSync) _networkLastSnapshot = snapshot;
        if (projectToStation) _publishNetwork?.Invoke(snapshot);
    }

    private static Task<T> NetworkProviderCallAsync<T>(Func<ValueTask<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try { completion.TrySetResult(await action().ConfigureAwait(false)); }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
    }

    private sealed class NetworkRejectedException : Exception
    {
        internal NetworkRejectedException(string reason) => Reason = SafeReason(reason);
        internal string Reason { get; }
    }

    private sealed class NetworkOperation
    {
        private readonly object _cancellationSync = new();
        private readonly CancellationTokenRegistration _lifetimeRegistration;
        private bool _cancellationRequested;
        private bool _sdkCancellationSealed;
        private Task? _cancellationWork;

        internal NetworkOperation(CameraNetworkChangeRequest request, CancellationToken lifetime,
            TaskCompletionSource<bool> drain)
        {
            Request = request;
            Drain = drain;
            _lifetimeRegistration = lifetime.Register(static state =>
                ((NetworkOperation)state!).RequestCancellation(), this);
        }
        internal CameraNetworkChangeRequest Request { get; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal bool IsCancellationRequested { get { lock (_cancellationSync) return _cancellationRequested; } }

        internal void RequestCancellation()
        {
            lock (_cancellationSync)
            {
                _cancellationRequested = true;
                if (_sdkCancellationSealed || _cancellationWork is not null) return;
                _cancellationWork = Task.Run(() =>
                {
                    try { Cancellation.Cancel(); }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // The request is still cancelled. Callback failure never
                        // releases a provider call or grants another device owner.
                    }
                });
            }
        }

        internal void ThrowIfCancellationRequested()
        {
            if (IsCancellationRequested) throw new OperationCanceledException(Cancellation.Token);
        }

        internal Task BeginCleanupAsync()
        {
            lock (_cancellationSync)
            {
                _sdkCancellationSealed = true;
                return _cancellationWork ?? Task.CompletedTask;
            }
        }

        internal void DisposeCancellation()
        {
            _lifetimeRegistration.Dispose();
            Cancellation.Dispose();
        }
        internal TaskCompletionSource<bool> Drain { get; }
        internal Task? Work { get; set; }
        internal CameraSetupAuthorization? Authorization { get; set; }
        internal CameraNetworkAdmission? Admission { get; set; }
        internal ICameraNetworkMaintenanceSession? Session { get; set; }
        internal CameraIpv4Configuration? Previous { get; set; }
        internal CameraIpv4Configuration? Observed { get; set; }
        internal bool ApplyStarted { get; set; }
        internal bool DebugDeviceInvalidated { get; set; }
        internal bool IdentityVerified { get; set; }
        internal bool StationReserved { get; set; }
        internal bool PhysicalReserved { get; set; }
    }
}
