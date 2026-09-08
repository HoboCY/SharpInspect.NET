using System.Collections.ObjectModel;
using SharpInspect.Abstractions;

namespace SharpInspect.Cameras.Virtual;

/// <summary>Bounded outcomes for one virtual network write attempt.</summary>
public enum VirtualNetworkApplyOutcome
{
    Success,
    FailureBeforeChange,
    FailureAfterChange
}

/// <summary>Bounded outcomes for the identity proof after a network write.</summary>
public enum VirtualNetworkRediscoveryOutcome
{
    SameIdentity,
    WrongIdentity,
    Unavailable
}

/// <summary>Bounded outcomes for the complete network read-back.</summary>
public enum VirtualNetworkReadBackOutcome
{
    Success,
    Failure,
    Mismatch
}

/// <summary>
/// Immutable, bounded network fixture inputs for the development-only provider.
/// The fixture models observations and state transitions; it never calls the
/// operating-system network stack.
/// </summary>
public sealed class VirtualNetworkCameraOptions
{
    public const int MaximumAddresses = 32;
    public const int MaximumPlanEntries = 32;

    public VirtualNetworkCameraOptions(
        CameraIpv4Configuration? initialConfiguration = null,
        string interfaceId = "virtual-network",
        bool hardwareAvailable = true,
        IEnumerable<CameraIpv4Configuration>? otherDeviceAddresses = null,
        IEnumerable<CameraIpv4Configuration>? reservedStationAddresses = null,
        IEnumerable<CameraIpv4Configuration>? forcedConflictAddresses = null,
        IEnumerable<VirtualNetworkApplyOutcome>? applyOutcomes = null,
        IEnumerable<VirtualNetworkRediscoveryOutcome>? rediscoveryOutcomes = null,
        IEnumerable<VirtualNetworkReadBackOutcome>? readBackOutcomes = null,
        VirtualNetworkApplyOutcome applyOutcome = VirtualNetworkApplyOutcome.Success,
        VirtualNetworkRediscoveryOutcome rediscoveryOutcome = VirtualNetworkRediscoveryOutcome.SameIdentity,
        VirtualNetworkReadBackOutcome readBackOutcome = VirtualNetworkReadBackOutcome.Success,
        CameraIpv4Configuration? readBackConfiguration = null,
        string? rediscoveredIdentity = null)
    {
        InitialConfiguration = initialConfiguration ?? DefaultConfiguration();
        var station = new CameraStationNetwork(interfaceId, InitialConfiguration);
        InterfaceId = station.InterfaceId;
        HardwareAvailable = hardwareAvailable;
        OtherDeviceAddresses = CopyAddresses(otherDeviceAddresses, nameof(otherDeviceAddresses));
        ReservedStationAddresses = CopyAddresses(reservedStationAddresses, nameof(reservedStationAddresses));
        ForcedConflictAddresses = CopyAddresses(forcedConflictAddresses, nameof(forcedConflictAddresses));

        ApplyOutcomes = CopyPlan(applyOutcomes, applyOutcome, nameof(applyOutcomes));
        RediscoveryOutcomes = CopyPlan(rediscoveryOutcomes, rediscoveryOutcome,
            nameof(rediscoveryOutcomes));
        ReadBackOutcomes = CopyPlan(readBackOutcomes, readBackOutcome, nameof(readBackOutcomes));
        ReadBackConfiguration = readBackConfiguration;
        RediscoveredIdentity = rediscoveredIdentity is null ? null :
            ValidateIdentifier(rediscoveredIdentity, nameof(rediscoveredIdentity));
    }

    public CameraIpv4Configuration InitialConfiguration { get; }
    public string InterfaceId { get; }
    public bool HardwareAvailable { get; }
    public IReadOnlyList<CameraIpv4Configuration> OtherDeviceAddresses { get; }
    public IReadOnlyList<CameraIpv4Configuration> ReservedStationAddresses { get; }
    public IReadOnlyList<CameraIpv4Configuration> ForcedConflictAddresses { get; }
    public IReadOnlyList<VirtualNetworkApplyOutcome> ApplyOutcomes { get; }
    public IReadOnlyList<VirtualNetworkRediscoveryOutcome> RediscoveryOutcomes { get; }
    public IReadOnlyList<VirtualNetworkReadBackOutcome> ReadBackOutcomes { get; }
    public CameraIpv4Configuration? ReadBackConfiguration { get; }
    public string? RediscoveredIdentity { get; }

    public VirtualNetworkApplyOutcome ApplyOutcome => ApplyOutcomes.Count == 0
        ? VirtualNetworkApplyOutcome.Success : ApplyOutcomes[0];
    public VirtualNetworkRediscoveryOutcome RediscoveryOutcome => RediscoveryOutcomes.Count == 0
        ? VirtualNetworkRediscoveryOutcome.SameIdentity : RediscoveryOutcomes[0];
    public VirtualNetworkReadBackOutcome ReadBackOutcome => ReadBackOutcomes.Count == 0
        ? VirtualNetworkReadBackOutcome.Success : ReadBackOutcomes[0];

    public static VirtualNetworkCameraOptions Success(
        CameraIpv4Configuration? initialConfiguration = null,
        string interfaceId = "virtual-network") =>
        new(initialConfiguration, interfaceId);

    public static VirtualNetworkCameraOptions DetectableConflict(
        CameraIpv4Configuration? conflictingAddress = null,
        CameraIpv4Configuration? initialConfiguration = null,
        string interfaceId = "virtual-network") =>
        new(initialConfiguration, interfaceId,
            forcedConflictAddresses: new[]
            {
                conflictingAddress ?? new CameraIpv4Configuration("192.168.10.20", 24)
            });

    public static VirtualNetworkCameraOptions ApplyFailureBeforeChange(
        CameraIpv4Configuration? initialConfiguration = null,
        string interfaceId = "virtual-network") =>
        new(initialConfiguration, interfaceId,
            applyOutcome: VirtualNetworkApplyOutcome.FailureBeforeChange);

    public static VirtualNetworkCameraOptions ApplyFailureAfterChange(
        CameraIpv4Configuration? initialConfiguration = null,
        string interfaceId = "virtual-network") =>
        new(initialConfiguration, interfaceId,
            applyOutcome: VirtualNetworkApplyOutcome.FailureAfterChange);

    public static VirtualNetworkCameraOptions WrongRediscoveredIdentity(
        CameraIpv4Configuration? initialConfiguration = null,
        string interfaceId = "virtual-network",
        string rediscoveredIdentity = "virtual:wrong") =>
        new(initialConfiguration, interfaceId,
            rediscoveryOutcome: VirtualNetworkRediscoveryOutcome.WrongIdentity,
            rediscoveredIdentity: rediscoveredIdentity);

    public static VirtualNetworkCameraOptions WrongReadBack(
        CameraIpv4Configuration? initialConfiguration = null,
        string interfaceId = "virtual-network",
        CameraIpv4Configuration? readBackConfiguration = null) =>
        new(initialConfiguration, interfaceId,
            readBackOutcome: VirtualNetworkReadBackOutcome.Mismatch,
            readBackConfiguration: readBackConfiguration ??
                new CameraIpv4Configuration("192.168.10.254", 24));

    public static VirtualNetworkCameraOptions NoHardware(
        CameraIpv4Configuration? initialConfiguration = null,
        string interfaceId = "virtual-network") =>
        new(initialConfiguration, interfaceId, hardwareAvailable: false);

    private static CameraIpv4Configuration DefaultConfiguration() =>
        new("192.168.10.2", 24);

    private static IReadOnlyList<CameraIpv4Configuration> CopyAddresses(
        IEnumerable<CameraIpv4Configuration>? values, string parameterName)
    {
        if (values is null)
            return new ReadOnlyCollection<CameraIpv4Configuration>(Array.Empty<CameraIpv4Configuration>());

        var copied = new List<CameraIpv4Configuration>(Math.Min(MaximumAddresses, 8));
        foreach (var value in values)
        {
            if (copied.Count == MaximumAddresses)
                throw new ArgumentException("VirtualNetworkAddressCapacityExceeded", parameterName);
            if (value is null)
                throw new ArgumentException("VirtualNetworkAddressInvalid", parameterName);
            copied.Add(value);
        }

        return new ReadOnlyCollection<CameraIpv4Configuration>(copied);
    }

    private static IReadOnlyList<T> CopyPlan<T>(IEnumerable<T>? values, T fallback,
        string parameterName) where T : struct, Enum
    {
        if (values is null)
        {
            if (!Enum.IsDefined(typeof(T), fallback))
                throw new ArgumentOutOfRangeException(parameterName);
            // The default enum member means "use the deterministic default"
            // for every bounded fixture operation. A non-default member is a
            // convenient one-shot factory outcome.
            return EqualityComparer<T>.Default.Equals(fallback, default)
                ? new ReadOnlyCollection<T>(Array.Empty<T>())
                : new ReadOnlyCollection<T>(new[] { fallback });
        }

        var copied = new List<T>(Math.Min(MaximumPlanEntries, 8));
        foreach (var value in values)
        {
            if (copied.Count == MaximumPlanEntries)
                throw new ArgumentException("VirtualNetworkPlanCapacityExceeded", parameterName);
            if (!Enum.IsDefined(typeof(T), value))
                throw new ArgumentOutOfRangeException(parameterName);
            copied.Add(value);
        }

        return new ReadOnlyCollection<T>(copied);
    }

    private static string ValidateIdentifier(string value, string parameterName)
    {
        if (value.Length is < 1 or > 128 || value.Any(character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                    >= '0' and <= '9' or '.' or '_' or '-' or ':')))
            throw new ArgumentException("VirtualNetworkIdentifierInvalid", parameterName);
        return value;
    }
}

/// <summary>One bounded virtual camera plus its network fixture inputs.</summary>
public sealed class VirtualNetworkCameraFixture
{
    public VirtualNetworkCameraFixture(VirtualCameraScenario scenario,
        VirtualNetworkCameraOptions? network = null)
    {
        Scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
        Network = network ?? VirtualNetworkCameraOptions.Success();
    }

    public VirtualCameraScenario Scenario { get; }
    public VirtualNetworkCameraOptions Network { get; }
}

public sealed record VirtualNetworkDeviceDiagnostics(
    string StableDeviceIdentity,
    string InterfaceId,
    bool HardwareAvailable,
    bool IsStale,
    CameraIpv4Configuration CurrentConfiguration,
    int BeginCount,
    int SuccessfulBeginCount,
    int ApplyCount,
    int SuccessfulApplyCount,
    int FailedApplyCount,
    int OutstandingLeases,
    int ActiveOperations,
    int PeakActiveOperations,
    int ApplyCursor,
    int RediscoveryCursor,
    int ReadBackCursor);

public sealed record VirtualNetworkCameraProviderDiagnostics(
    IReadOnlyList<VirtualNetworkDeviceDiagnostics> Devices,
    long InfrastructureFailures,
    bool IsDisposed);

/// <summary>
/// A virtual network-camera provider. It delegates camera acquisition to a
/// private <see cref="VirtualCameraProvider"/> and exposes maintenance only
/// through the typed optional extension. The inner provider is deliberately
/// not exposed, so a caller cannot bypass the maintenance lease gate.
/// </summary>
public sealed class VirtualNetworkCameraProvider : ICameraProvider, ICameraNetworkConfigurator
{
    public const string ProviderId = VirtualCameraProvider.ProviderId;
    public const string ProviderVersion = VirtualCameraProvider.ProviderVersion;
    public const string AdapterPackageId = VirtualCameraProvider.AdapterPackageId;
    public const string AdapterPackageVersion = VirtualCameraProvider.AdapterPackageVersion;

    private const int MaximumFixtures = 4;
    private readonly object _gate = new();
    private readonly VirtualCameraProvider _inner;
    private readonly Dictionary<string, VirtualNetworkDeviceState> _devices;
    private readonly Dictionary<string, VirtualNetworkMaintenanceSession> _leases =
        new(StringComparer.Ordinal);
    private Task? _disposeTask;
    private int _disposed;
    private long _infrastructureFailures;

    public VirtualNetworkCameraProvider(VirtualCameraScenario scenario,
        VirtualCameraClock clock, VirtualNetworkCameraOptions? network = null,
        int poolCapacity = 2)
        : this(new[] { new VirtualNetworkCameraFixture(scenario, network) }, clock, poolCapacity)
    {
    }

    public VirtualNetworkCameraProvider(IEnumerable<VirtualCameraScenario> scenarios,
        VirtualCameraClock clock, VirtualNetworkCameraOptions? network = null,
        int poolCapacity = 2)
        : this(ToFixtures(scenarios, network), clock, poolCapacity)
    {
    }

    public VirtualNetworkCameraProvider(IEnumerable<VirtualNetworkCameraFixture> fixtures,
        VirtualCameraClock clock, int poolCapacity = 2)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        ArgumentNullException.ThrowIfNull(clock);

        var copied = CopyFixtures(fixtures);
        _inner = new VirtualCameraProvider(copied.Select(fixture => fixture.Scenario),
            clock, poolCapacity);
        _devices = new Dictionary<string, VirtualNetworkDeviceState>(StringComparer.Ordinal);
        foreach (var fixture in copied)
        {
            var target = new CameraBindingTarget(_inner.Identity,
                fixture.Scenario.StableDeviceIdentity);
            if (!_devices.TryAdd(target.StableDeviceIdentity,
                    new VirtualNetworkDeviceState(target, fixture.Network)))
                throw new ArgumentException("VirtualNetworkStableDeviceIdentityDuplicate",
                    nameof(fixtures));
        }
    }

    public CameraProviderIdentity Identity => _inner.Identity;

    public ValueTask<CameraDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<CameraDiscoveryResult>(cancellationToken);

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return ValueTask.FromResult(
                    CameraDiscoveryResult.Failure("VirtualNetworkProviderDisposed"));

            // Discovery is intentionally read-only. It does not reserve a
            // device and remains available while a maintenance lease is held.
            var discovery = _inner.DiscoverAsync(cancellationToken).GetAwaiter().GetResult();
            if (_devices.Values.Any(state => state.GetDiscoveryOverride() ==
                    VirtualNetworkRediscoveryOutcome.Unavailable))
                return ValueTask.FromResult(CameraDiscoveryResult.Failure(
                    "VirtualNetworkRediscoveryUnavailable"));

            var wrongIdentity = _devices.Values.FirstOrDefault(state =>
                state.GetDiscoveryOverride() == VirtualNetworkRediscoveryOutcome.WrongIdentity);
            if (wrongIdentity is null)
                return ValueTask.FromResult(discovery);

            var descriptors = discovery.Devices.Select(device =>
            {
                if (device.StableDeviceIdentity != wrongIdentity.Target.StableDeviceIdentity)
                    return device;
                var identity = wrongIdentity.Network.RediscoveredIdentity ?? "virtual:wrong";
                return new CameraDeviceDescriptor(device.Provider, identity,
                    device.DisplayName, device.ReportedModel);
            }).ToArray();
            return ValueTask.FromResult(CameraDiscoveryResult.Success(descriptors));
        }
    }

    public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<CameraOpenResult>(cancellationToken);
        ArgumentNullException.ThrowIfNull(stableDeviceIdentity);

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return ValueTask.FromResult(
                    CameraOpenResult.Failure("VirtualNetworkProviderDisposed"));
            if (!_devices.TryGetValue(stableDeviceIdentity, out var state))
                return ValueTask.FromResult(
                    CameraOpenResult.Failure("VirtualNetworkDeviceMissing"));
            if (!state.Network.HardwareAvailable)
                return ValueTask.FromResult(
                    CameraOpenResult.Failure("VirtualNetworkHardwareUnavailable"));
            if (state.IsStale)
                return ValueTask.FromResult(
                    CameraOpenResult.Failure("VirtualNetworkMaintenanceStale"));
            if (_leases.ContainsKey(stableDeviceIdentity))
                return ValueTask.FromResult(
                    CameraOpenResult.Failure("VirtualNetworkMaintenanceLeaseActive"));
            if (IsInnerDeviceOpen(stableDeviceIdentity))
                return ValueTask.FromResult(
                    CameraOpenResult.Failure("VirtualNetworkDeviceOpen"));

            return _inner.OpenAsync(stableDeviceIdentity, cancellationToken);
        }
    }

    public ValueTask<CameraNetworkMaintenanceLeaseResult> TryBeginMaintenanceAsync(
        string stableDeviceIdentity, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<CameraNetworkMaintenanceLeaseResult>(cancellationToken);
        ArgumentNullException.ThrowIfNull(stableDeviceIdentity);

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return ValueTask.FromResult(
                    CameraNetworkMaintenanceLeaseResult.Failure(
                        "VirtualNetworkProviderDisposed"));
            if (!_devices.TryGetValue(stableDeviceIdentity, out var state))
                return ValueTask.FromResult(
                    CameraNetworkMaintenanceLeaseResult.Failure(
                        "VirtualNetworkDeviceMissing"));
            if (!state.Network.HardwareAvailable)
                return ValueTask.FromResult(
                    CameraNetworkMaintenanceLeaseResult.Failure(
                        "VirtualNetworkHardwareUnavailable"));
            if (state.IsStale)
                return ValueTask.FromResult(
                    CameraNetworkMaintenanceLeaseResult.Failure(
                        "VirtualNetworkMaintenanceStale"));
            if (_leases.ContainsKey(stableDeviceIdentity))
                return ValueTask.FromResult(
                    CameraNetworkMaintenanceLeaseResult.Failure(
                        "VirtualNetworkMaintenanceLeaseActive"));

            // The provider diagnostics are the source of truth for an open
            // target. Begin never closes an opened camera implicitly.
            if (IsInnerDeviceOpen(stableDeviceIdentity))
                return ValueTask.FromResult(
                    CameraNetworkMaintenanceLeaseResult.Failure(
                        "VirtualNetworkDeviceOpen"));

            state.BeginCount = checked(state.BeginCount + 1);
            state.SuccessfulBeginCount = checked(state.SuccessfulBeginCount + 1);
            var session = new VirtualNetworkMaintenanceSession(this, state);
            _leases.Add(stableDeviceIdentity, session);
            return ValueTask.FromResult(
                CameraNetworkMaintenanceLeaseResult.Success(session));
        }
    }

    public VirtualNetworkCameraProviderDiagnostics GetDiagnostics()
    {
        lock (_gate)
        {
            var devices = _devices.Values.Select(state => state.GetDiagnostics(
                _leases.ContainsKey(state.Target.StableDeviceIdentity))).ToArray();
            return new VirtualNetworkCameraProviderDiagnostics(
                new ReadOnlyCollection<VirtualNetworkDeviceDiagnostics>(devices),
                Interlocked.Read(ref _infrastructureFailures),
                Volatile.Read(ref _disposed) != 0);
        }
    }

    public VirtualNetworkCameraProviderDiagnostics GetNetworkDiagnostics() => GetDiagnostics();

    public ValueTask DisposeAsync()
    {
        VirtualNetworkMaintenanceSession[] sessions;
        TaskCompletionSource<object?> completion;
        lock (_gate)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);

            Interlocked.Exchange(ref _disposed, 1);
            sessions = _leases.Values.ToArray();
            completion = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }

        _ = DisposeCoreAsync(sessions, completion);
        return new ValueTask(completion.Task);
    }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal CameraDiscoveryResult DiscoverInnerForMaintenance() =>
        _inner.DiscoverAsync().GetAwaiter().GetResult();

    private (CameraIpv4Configuration[] Addresses, int Count)
        SnapshotOtherDeviceAddressesLocked(VirtualNetworkDeviceState own, string interfaceId)
    {
        // The provider gate is acquired before any device gate.  Snapshot the
        // other fixtures before entering the session's own gate so conflict
        // detection never takes own-gate -> other-gate or own-gate -> provider-
        // gate paths.  There can be at most MaximumFixtures devices.
        var addresses = new CameraIpv4Configuration[MaximumFixtures];
        var count = 0;
        foreach (var other in _devices.Values)
        {
            if (ReferenceEquals(other, own) || other.Network.InterfaceId != interfaceId)
                continue;

            lock (other.Gate)
                addresses[count++] = other.CurrentConfiguration;
        }

        return (addresses, count);
    }

    private CameraNetworkConflictResult DetectConflict(
        VirtualNetworkMaintenanceSession session, VirtualNetworkDeviceState state,
        CameraIpv4Configuration requested, CameraStationNetwork stationNetwork)
    {
        lock (_gate)
        {
            var others = SnapshotOtherDeviceAddressesLocked(state, stationNetwork.InterfaceId);
            lock (state.Gate)
            {
                if (!session.TryStartOperationLocked(out var failure))
                    return new CameraNetworkConflictResult(
                        CameraNetworkConflictState.Unknown, failure.ReasonCode);
                try
                {
                    session.LastStationNetwork = stationNetwork;
                    return EvaluateConflictLocked(state, requested, stationNetwork,
                        others.Addresses, others.Count);
                }
                finally
                {
                    session.EndOperationLocked();
                }
            }
        }
    }

    private CameraNetworkApplyResult Apply(
        VirtualNetworkMaintenanceSession session, VirtualNetworkDeviceState state,
        CameraIpv4Configuration expectedPrevious, CameraIpv4Configuration requested)
    {
        lock (_gate)
        {
            // Apply is serialized by the same provider gate as DetectConflict.
            // The snapshot and the compare-and-swap therefore cover the latest
            // address of every other fixture, closing the Detect -> Apply race.
            var others = SnapshotOtherDeviceAddressesLocked(state, state.Network.InterfaceId);
            lock (state.Gate)
            {
                if (!session.TryStartOperationLocked(out var failure))
                    return new CameraNetworkApplyResult(false, failure.ReasonCode);
                try
                {
                    if (state.CurrentConfiguration != expectedPrevious)
                        return session.FailureBeforeMutation(
                            "VirtualNetworkExpectedPreviousMismatch");

                    var conflict = EvaluateConflictLocked(state, requested,
                        session.LastStationNetwork, others.Addresses, others.Count);
                    if (conflict.State == CameraNetworkConflictState.Conflict)
                        return session.FailureBeforeMutation(conflict.ReasonCode);
                    if (conflict.State == CameraNetworkConflictState.Unknown)
                        return session.FailureBeforeMutation(conflict.ReasonCode);

                    var outcome = session.NextApplyOutcomeLocked();
                    if (outcome is null)
                        return session.FailureBeforeMutation("VirtualNetworkApplyPlanExhausted");
                    state.ApplyCount = checked(state.ApplyCount + 1);
                    if (outcome == VirtualNetworkApplyOutcome.FailureBeforeChange)
                        return session.FailureBeforeMutation("VirtualNetworkApplyFailedBeforeChange");

                    state.CurrentConfiguration = requested;
                    if (outcome == VirtualNetworkApplyOutcome.FailureAfterChange)
                        return session.FailureAfterMutation("VirtualNetworkApplyFailedAfterChange");

                    var rediscovery = session.NextRediscoveryOutcomeLocked();
                    if (rediscovery is null)
                        return session.FailureAfterMutation("VirtualNetworkRediscoveryPlanExhausted");

                    var discovery = DiscoverInnerForMaintenance();
                    if (rediscovery == VirtualNetworkRediscoveryOutcome.Unavailable)
                    {
                        state.IsStale = true;
                        state.DiscoveryOverride = rediscovery;
                        return session.FailureAfterMutation(
                            "VirtualNetworkRediscoveryUnavailable");
                    }
                    if (rediscovery == VirtualNetworkRediscoveryOutcome.WrongIdentity ||
                        (state.Network.RediscoveredIdentity is not null &&
                            state.Network.RediscoveredIdentity != session.Target.StableDeviceIdentity) ||
                        !discovery.Succeeded || !discovery.Devices.Any(device =>
                            device.StableDeviceIdentity == session.Target.StableDeviceIdentity &&
                            device.Provider == session.Target.Provider))
                    {
                        state.IsStale = true;
                        state.DiscoveryOverride = VirtualNetworkRediscoveryOutcome.WrongIdentity;
                        return session.FailureAfterMutation(
                            "VirtualNetworkRediscoveredIdentityMismatch");
                    }

                    var readBack = session.NextReadBackOutcomeLocked();
                    if (readBack is null)
                        return session.FailureAfterMutation("VirtualNetworkReadBackPlanExhausted");
                    if (readBack == VirtualNetworkReadBackOutcome.Failure)
                        return session.FailureAfterMutation("VirtualNetworkReadBackFailed");
                    if (readBack == VirtualNetworkReadBackOutcome.Mismatch ||
                        (state.Network.ReadBackConfiguration is not null &&
                            state.Network.ReadBackConfiguration != state.CurrentConfiguration))
                        return session.FailureAfterMutation("VirtualNetworkReadBackMismatch");

                    state.SuccessfulApplyCount = checked(state.SuccessfulApplyCount + 1);
                    return new CameraNetworkApplyResult(true, "VirtualNetworkApplied");
                }
                finally
                {
                    session.EndOperationLocked();
                }
            }
        }
    }

    private static CameraNetworkConflictResult EvaluateConflictLocked(
        VirtualNetworkDeviceState state, CameraIpv4Configuration requested,
        CameraStationNetwork? stationNetwork, CameraIpv4Configuration[] otherAddresses,
        int otherAddressCount)
    {
        if (stationNetwork is not null)
        {
            if (state.Network.InterfaceId != stationNetwork.InterfaceId)
                return new CameraNetworkConflictResult(CameraNetworkConflictState.Unknown,
                    "VirtualNetworkStationInterfaceMismatch");
            if (!requested.IsInSameSubnet(stationNetwork.Configuration))
                return new CameraNetworkConflictResult(CameraNetworkConflictState.Unknown,
                    "VirtualNetworkStationSubnetMismatch");
            if (requested.Address == stationNetwork.Configuration.Address)
                return new CameraNetworkConflictResult(CameraNetworkConflictState.Conflict,
                    "VirtualNetworkStationAddressConflict");
        }

        foreach (var address in state.Network.OtherDeviceAddresses)
            if (address.Address == requested.Address)
                return new CameraNetworkConflictResult(CameraNetworkConflictState.Conflict,
                    "VirtualNetworkOtherDeviceAddressConflict");
        foreach (var address in state.Network.ReservedStationAddresses)
            if (address.Address == requested.Address)
                return new CameraNetworkConflictResult(CameraNetworkConflictState.Conflict,
                    "VirtualNetworkReservedAddressConflict");
        for (var index = 0; index < otherAddressCount; index++)
            if (otherAddresses[index].Address == requested.Address)
                return new CameraNetworkConflictResult(CameraNetworkConflictState.Conflict,
                    "VirtualNetworkProviderDeviceAddressConflict");
        foreach (var address in state.Network.ForcedConflictAddresses)
        {
            // A fixture may intentionally list the target's own current
            // address. That address is not a conflict; configured
            // other-device/reserved lists and other live fixtures remain
            // independent conflict sources.
            if (address.Address == state.CurrentConfiguration.Address)
                continue;
            if (address.Address == requested.Address)
                return new CameraNetworkConflictResult(CameraNetworkConflictState.Conflict,
                    "VirtualNetworkForcedAddressConflict");
        }

        return new CameraNetworkConflictResult(CameraNetworkConflictState.Clear,
            "VirtualNetworkAddressAvailable");
    }

    private void ReleaseLease(VirtualNetworkMaintenanceSession session)
    {
        lock (_gate)
        {
            if (_leases.TryGetValue(session.Target.StableDeviceIdentity, out var active) &&
                ReferenceEquals(active, session))
                _leases.Remove(session.Target.StableDeviceIdentity);
        }
    }

    private async Task DisposeCoreAsync(
        IReadOnlyList<VirtualNetworkMaintenanceSession> sessions,
        TaskCompletionSource<object?> completion)
    {
        try
        {
            foreach (var session in sessions)
                await session.DisposeAsync().ConfigureAwait(false);
            await _inner.DisposeAsync().ConfigureAwait(false);
            completion.TrySetResult(null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Interlocked.Increment(ref _infrastructureFailures);
            completion.TrySetException(exception);
        }
    }

    private bool IsInnerDeviceOpen(string stableDeviceIdentity)
    {
        var diagnostics = _inner.GetDiagnostics();
        return diagnostics.Devices.Any(device =>
            device.StableDeviceIdentity == stableDeviceIdentity && device.IsOpen);
    }

    private static IReadOnlyList<VirtualNetworkCameraFixture> CopyFixtures(
        IEnumerable<VirtualNetworkCameraFixture> fixtures)
    {
        var copied = new List<VirtualNetworkCameraFixture>(MaximumFixtures);
        foreach (var fixture in fixtures)
        {
            if (copied.Count == MaximumFixtures)
                throw new ArgumentException("VirtualNetworkFixtureCapacityExceeded",
                    nameof(fixtures));
            if (fixture is null)
                throw new ArgumentException("VirtualNetworkFixtureInvalid", nameof(fixtures));
            copied.Add(fixture);
        }
        if (copied.Count == 0)
            throw new ArgumentException("VirtualNetworkFixtureRequired", nameof(fixtures));
        return new ReadOnlyCollection<VirtualNetworkCameraFixture>(copied);
    }

    private static IEnumerable<VirtualNetworkCameraFixture> ToFixtures(
        IEnumerable<VirtualCameraScenario> scenarios, VirtualNetworkCameraOptions? network)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        var copied = new List<VirtualNetworkCameraFixture>(MaximumFixtures);
        foreach (var scenario in scenarios)
        {
            if (copied.Count == MaximumFixtures)
                throw new ArgumentException("VirtualNetworkFixtureCapacityExceeded",
                    nameof(scenarios));
            copied.Add(new VirtualNetworkCameraFixture(scenario, network));
        }
        return copied;
    }

    private sealed class VirtualNetworkDeviceState
    {
        internal VirtualNetworkDeviceState(CameraBindingTarget target,
            VirtualNetworkCameraOptions network)
        {
            Target = target;
            Network = network;
            CurrentConfiguration = network.InitialConfiguration;
        }

        internal readonly object Gate = new();
        internal readonly CameraBindingTarget Target;
        internal readonly VirtualNetworkCameraOptions Network;
        internal CameraIpv4Configuration CurrentConfiguration;
        internal bool IsStale;
        internal int BeginCount;
        internal int SuccessfulBeginCount;
        internal int ApplyCount;
        internal int SuccessfulApplyCount;
        internal int FailedApplyCount;
        internal int ActiveOperations;
        internal int PeakActiveOperations;
        internal int ApplyCursor;
        internal int RediscoveryCursor;
        internal int ReadBackCursor;
        internal VirtualNetworkRediscoveryOutcome? DiscoveryOverride;

        internal VirtualNetworkDeviceDiagnostics GetDiagnostics(bool leaseHeld)
        {
            lock (Gate)
            {
                return new VirtualNetworkDeviceDiagnostics(Target.StableDeviceIdentity,
                    Network.InterfaceId, Network.HardwareAvailable, IsStale,
                    CurrentConfiguration, BeginCount, SuccessfulBeginCount, ApplyCount,
                    SuccessfulApplyCount, FailedApplyCount, leaseHeld ? 1 : 0,
                    ActiveOperations, PeakActiveOperations, ApplyCursor,
                    RediscoveryCursor, ReadBackCursor);
            }
        }

        internal VirtualNetworkRediscoveryOutcome? GetDiscoveryOverride()
        {
            lock (Gate) return DiscoveryOverride;
        }
    }

    private sealed class VirtualNetworkMaintenanceSession : ICameraNetworkMaintenanceSession
    {
        private readonly VirtualNetworkCameraProvider _provider;
        private readonly VirtualNetworkDeviceState _state;
        private TaskCompletionSource<bool>? _operationsDrained;
        private Task? _disposeTask;
        private bool _disposed;
        internal CameraStationNetwork? LastStationNetwork;

        internal VirtualNetworkMaintenanceSession(VirtualNetworkCameraProvider provider,
            VirtualNetworkDeviceState state)
        {
            _provider = provider;
            _state = state;
        }

        public CameraBindingTarget Target => _state.Target;

        public ValueTask<CameraNetworkReadResult> ReadCurrentAsync(
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled<CameraNetworkReadResult>(cancellationToken);

            lock (_state.Gate)
            {
                if (!TryStartOperationLocked(out var failure))
                    return ValueTask.FromResult(failure);
                try
                {
                    return ValueTask.FromResult(new CameraNetworkReadResult(true,
                        "VirtualNetworkReadSucceeded",
                        new CameraNetworkDeviceState(Target, _state.CurrentConfiguration)));
                }
                finally
                {
                    EndOperationLocked();
                }
            }
        }

        public ValueTask<CameraNetworkConflictResult> DetectConflictAsync(
            CameraIpv4Configuration requested, CameraStationNetwork stationNetwork,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(requested);
            ArgumentNullException.ThrowIfNull(stationNetwork);
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled<CameraNetworkConflictResult>(cancellationToken);
            return ValueTask.FromResult(_provider.DetectConflict(this, _state,
                requested, stationNetwork));
        }

        public ValueTask<CameraNetworkApplyResult> ApplyAsync(
            CameraIpv4Configuration expectedPrevious, CameraIpv4Configuration requested,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(expectedPrevious);
            ArgumentNullException.ThrowIfNull(requested);
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled<CameraNetworkApplyResult>(cancellationToken);
            return ValueTask.FromResult(_provider.Apply(this, _state,
                expectedPrevious, requested));
        }

        public ValueTask DisposeAsync()
        {
            TaskCompletionSource<object?> completion;
            Task drain;
            lock (_state.Gate)
            {
                if (_disposeTask is not null)
                    return new ValueTask(_disposeTask);
                _disposed = true;
                completion = new TaskCompletionSource<object?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = completion.Task;
                if (_state.ActiveOperations == 0)
                    drain = Task.CompletedTask;
                else
                {
                    _operationsDrained = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    drain = _operationsDrained.Task;
                }
            }

            _ = ReleaseAfterOperationsAsync(drain, completion);
            return new ValueTask(completion.Task);
        }

        internal bool TryStartOperationLocked(out CameraNetworkReadResult failure)
        {
            if (_disposed)
            {
                failure = new CameraNetworkReadResult(false,
                    "VirtualNetworkMaintenanceSessionDisposed");
                return false;
            }
            if (_provider.IsDisposed)
            {
                failure = new CameraNetworkReadResult(false,
                    "VirtualNetworkProviderDisposed");
                return false;
            }
            if (!_state.Network.HardwareAvailable)
            {
                failure = new CameraNetworkReadResult(false,
                    "VirtualNetworkHardwareUnavailable");
                return false;
            }
            if (_state.IsStale)
            {
                failure = new CameraNetworkReadResult(false,
                    "VirtualNetworkMaintenanceStale");
                return false;
            }

            _state.ActiveOperations = checked(_state.ActiveOperations + 1);
            _state.PeakActiveOperations = Math.Max(_state.PeakActiveOperations,
                _state.ActiveOperations);
            failure = new CameraNetworkReadResult(true, "VirtualNetworkOperationStarted");
            return true;
        }

        internal void EndOperationLocked()
        {
            _state.ActiveOperations = checked(_state.ActiveOperations - 1);
            if (_state.ActiveOperations == 0)
                _operationsDrained?.TrySetResult(true);
        }

        internal VirtualNetworkApplyOutcome? NextApplyOutcomeLocked()
        {
            if (_state.Network.ApplyOutcomes.Count == 0)
                return VirtualNetworkApplyOutcome.Success;
            if (_state.ApplyCursor >= _state.Network.ApplyOutcomes.Count)
                return null;
            return _state.Network.ApplyOutcomes[_state.ApplyCursor++];
        }

        internal VirtualNetworkRediscoveryOutcome? NextRediscoveryOutcomeLocked()
        {
            if (_state.Network.RediscoveryOutcomes.Count == 0)
                return VirtualNetworkRediscoveryOutcome.SameIdentity;
            if (_state.RediscoveryCursor >= _state.Network.RediscoveryOutcomes.Count)
                return null;
            return _state.Network.RediscoveryOutcomes[_state.RediscoveryCursor++];
        }

        internal VirtualNetworkReadBackOutcome? NextReadBackOutcomeLocked()
        {
            if (_state.Network.ReadBackOutcomes.Count == 0)
                return VirtualNetworkReadBackOutcome.Success;
            if (_state.ReadBackCursor >= _state.Network.ReadBackOutcomes.Count)
                return null;
            return _state.Network.ReadBackOutcomes[_state.ReadBackCursor++];
        }

        internal CameraNetworkApplyResult FailureBeforeMutation(string reason)
        {
            _state.FailedApplyCount = checked(_state.FailedApplyCount + 1);
            return new CameraNetworkApplyResult(false, reason);
        }

        internal CameraNetworkApplyResult FailureAfterMutation(string reason)
        {
            _state.FailedApplyCount = checked(_state.FailedApplyCount + 1);
            return new CameraNetworkApplyResult(false, reason);
        }

        private async Task ReleaseAfterOperationsAsync(Task drained,
            TaskCompletionSource<object?> completion)
        {
            try
            {
                await drained.ConfigureAwait(false);
                _provider.ReleaseLease(this);
                completion.TrySetResult(null);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                completion.TrySetException(exception);
            }
        }
    }
}
