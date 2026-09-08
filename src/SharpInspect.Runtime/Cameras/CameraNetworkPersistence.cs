using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Cameras;

/// <summary>
/// Narrow durable boundary for the governed camera network maintenance flow.
/// The coordinator owns the provider lease and physical operation; this boundary
/// owns admission, terminal evidence and the restart reconciliation barrier.
/// </summary>
internal interface ICameraNetworkPersistence
{
    ValueTask<CameraNetworkSnapshot?> ReadLatestAsync(CameraBindingTarget target,
        CancellationToken cancellationToken = default);

    ValueTask<bool> HasUnresolvedAsync(CancellationToken cancellationToken = default);

    ValueTask<CameraNetworkAdmissionResult> AppendAdmissionAsync(
        CameraNetworkChangeRequest request,
        CameraStationNetwork stationNetwork,
        CameraIpv4Configuration actualPrevious,
        CameraSetupAuthorization authorization,
        Guid runtimeEpoch,
        StoreDeadline deadline,
        CancellationToken cancellationToken = default);

    ValueTask<StoreWriteResult> AppendTerminalAsync(CameraNetworkAdmission admission,
        CameraNetworkSnapshot snapshot, StoreDeadline deadline,
        CancellationToken cancellationToken = default);

    ValueTask<StoreWriteResult> RecordRejectedAsync(CameraNetworkChangeRequest request,
        CameraStationNetwork? stationNetwork, CameraIpv4Configuration? actualPrevious,
        CameraSetupAuthorization? authorization, Guid runtimeEpoch, string reason,
        StoreDeadline deadline, CancellationToken cancellationToken = default);
}

/// <summary>Result of the atomic network admission append.</summary>
internal sealed record CameraNetworkAdmissionResult(StoreWriteResult Result,
    CameraNetworkAdmission? Admission);

/// <summary>
/// Immutable admission projection handed to the physical coordinator.  The
/// command fact is the authoritative identity/audit context; the remaining
/// values are retained privately so a terminal append can bind every immutable
/// admission field before it writes.
/// </summary>
internal sealed class CameraNetworkAdmission
{
    internal CameraNetworkAdmission(CommandAuditFact fact, CameraNetworkChangeRequest request,
        CameraStationNetwork stationNetwork, CameraIpv4Configuration actualPrevious,
        CameraSetupAuthorization authorization, Guid runtimeEpoch)
    {
        Fact = fact ?? throw new ArgumentNullException(nameof(fact));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        StationNetwork = stationNetwork ?? throw new ArgumentNullException(nameof(stationNetwork));
        ActualPrevious = actualPrevious ?? throw new ArgumentNullException(nameof(actualPrevious));
        Authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        RuntimeEpoch = runtimeEpoch;
    }

    internal CommandAuditFact Fact { get; }
    internal CameraNetworkChangeRequest Request { get; }
    internal CameraStationNetwork StationNetwork { get; }
    internal CameraIpv4Configuration ActualPrevious { get; }
    internal CameraSetupAuthorization Authorization { get; }
    internal Guid RuntimeEpoch { get; }
}

/// <summary>SQLite adapter for the narrow network persistence boundary.</summary>
internal sealed class SqliteCameraNetworkPersistence : ICameraNetworkPersistence
{
    private readonly SqliteCommandStore _store;

    internal SqliteCameraNetworkPersistence(SqliteCommandStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public async ValueTask<CameraNetworkSnapshot?> ReadLatestAsync(CameraBindingTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var state = await _store.ReadCameraNetworkStateAsync(target, cancellationToken).ConfigureAwait(false);
        return state.Snapshot;
    }

    public async ValueTask<bool> HasUnresolvedAsync(CancellationToken cancellationToken = default)
    {
        var state = await _store.ReadCameraNetworkStateAsync(null, cancellationToken).ConfigureAwait(false);
        return state.HasPending || state.HasUnknown;
    }

    public ValueTask<CameraNetworkAdmissionResult> AppendAdmissionAsync(
        CameraNetworkChangeRequest request, CameraStationNetwork stationNetwork,
        CameraIpv4Configuration actualPrevious, CameraSetupAuthorization authorization,
        Guid runtimeEpoch, StoreDeadline deadline, CancellationToken cancellationToken = default) =>
        _store.AppendCameraNetworkAdmissionAsync(request, stationNetwork, actualPrevious,
            authorization, runtimeEpoch, deadline, cancellationToken);

    public ValueTask<StoreWriteResult> AppendTerminalAsync(CameraNetworkAdmission admission,
        CameraNetworkSnapshot snapshot, StoreDeadline deadline,
        CancellationToken cancellationToken = default) =>
        _store.AppendCameraNetworkTerminalAsync(admission, snapshot, deadline, cancellationToken);

    public ValueTask<StoreWriteResult> RecordRejectedAsync(CameraNetworkChangeRequest request,
        CameraStationNetwork? stationNetwork, CameraIpv4Configuration? actualPrevious,
        CameraSetupAuthorization? authorization, Guid runtimeEpoch, string reason,
        StoreDeadline deadline, CancellationToken cancellationToken = default) =>
        _store.AppendCameraNetworkRejectedAsync(request, stationNetwork, actualPrevious,
            authorization, runtimeEpoch, reason, deadline, cancellationToken);
}

/// <summary>Factory used by the network coordinator without expanding its constructor.</summary>
internal static class CameraNetworkPersistenceFactory
{
    internal static ICameraNetworkPersistence? Create(ICommandAuditWriter? audit) =>
        audit is SqliteCommandStore { CameraNetworkEnabled: true } store
            ? new SqliteCameraNetworkPersistence(store) : null;
}
