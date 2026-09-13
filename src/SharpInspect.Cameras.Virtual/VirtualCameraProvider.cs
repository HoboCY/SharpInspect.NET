using System.Collections.ObjectModel;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Frames;

namespace SharpInspect.Cameras.Virtual;

/// <summary>
/// Bounded diagnostics for the development-only virtual camera adapter. The
/// snapshot contains counters and cursors only; it does not retain frames,
/// callbacks, exception details, or an unbounded event history.
/// </summary>
public sealed record VirtualCameraDeviceDiagnostics(
    string ScenarioId,
    string ScenarioVersion,
    string StableDeviceIdentity,
    bool IsOpen,
    int OpenCount,
    int OpeningCursor,
    int ConfigurationCursor,
    int AcquisitionCursor,
    long FramesProduced,
    long FramesDropped,
    long InfrastructureFailures,
    int OutstandingLeases,
    int PeakOutstandingLeases);

public sealed record VirtualCameraProviderDiagnostics(
    IReadOnlyList<VirtualCameraDeviceDiagnostics> Devices,
    long InfrastructureFailures,
    bool IsDisposed);

/// <summary>
/// Deterministic, development-only camera provider. It owns no wall-clock
/// timers and does not turn a virtual result into hardware qualification.
/// </summary>
public sealed partial class VirtualCameraProvider : ICameraProvider
{
    public const string ProviderId = "SharpInspect.Virtual";
    public const string ProviderVersion = "1";
    public const string AdapterPackageId = "SharpInspect.NET.Cameras.Virtual";
    public const string AdapterPackageVersion = "0.1.0-dev.1";

    private const int MaximumScenarios = 4;
    private const long MaximumTotalBytes = 512L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, VirtualCameraSession> _sessions;
    private readonly VirtualCameraClock _clock;
    private readonly int _poolCapacity;
    private readonly CameraProviderIdentity _identity = new(
        ProviderId, ProviderVersion, AdapterPackageId, AdapterPackageVersion);
    private int _disposed;
    private long _infrastructureFailures;

    public VirtualCameraProvider(IEnumerable<VirtualCameraScenario> scenarios,
        VirtualCameraClock clock, int poolCapacity = 2)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(clock);
        if (poolCapacity is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(poolCapacity));

        var copied = new List<VirtualCameraScenario>(MaximumScenarios);
        foreach (var scenario in scenarios)
        {
            if (copied.Count == MaximumScenarios)
                throw new ArgumentException("VirtualCameraScenarioCountInvalid", nameof(scenarios));
            if (scenario is null)
                throw new ArgumentException("VirtualCameraScenarioNull", nameof(scenarios));
            copied.Add(scenario);
        }
        if (copied.Count == 0)
            throw new ArgumentException("VirtualCameraScenarioCountInvalid", nameof(scenarios));

        var sessions = new Dictionary<string, VirtualCameraSession>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var scenario in copied)
        {
            if (!sessions.TryAdd(scenario.StableDeviceIdentity,
                    new VirtualCameraSession(scenario, poolCapacity)))
                throw new ArgumentException("VirtualCameraStableDeviceIdentityDuplicate", nameof(scenarios));

            totalBytes = checked(totalBytes + scenario.ImageBytes);
            totalBytes = checked(totalBytes + (long)scenario.MaximumFrameBytes * poolCapacity);
            if (scenario.Preview is { } preview)
            {
        // Preview owns its immutable source images and one latest-frame
        // copy independently of the production pool. Count both so an
        // explicitly configured preview cannot bypass the provider cap.
                totalBytes = checked(totalBytes + preview.ImageBytes);
                totalBytes = checked(totalBytes + preview.MaximumFrameBytes);
            }
            if (totalBytes > MaximumTotalBytes)
                throw new ArgumentException("VirtualCameraMemoryBudgetExceeded", nameof(scenarios));
        }

        _sessions = sessions;
        _clock = clock;
        _poolCapacity = poolCapacity;
    }

    public CameraProviderIdentity Identity => _identity;

    public ValueTask<CameraDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<CameraDiscoveryResult>(cancellationToken);

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return ValueTask.FromResult(CameraDiscoveryResult.Failure("VirtualCameraProviderDisposed"));

            var descriptors = _sessions.Values.Select(session =>
                new CameraDeviceDescriptor(Identity, session.Scenario.StableDeviceIdentity,
                    "Virtual Camera " + session.Scenario.StableDeviceIdentity,
                    session.Scenario.ReportedModel)).ToArray();
            return ValueTask.FromResult(CameraDiscoveryResult.Success(descriptors));
        }
    }

    public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<CameraOpenResult>(cancellationToken);
        if (stableDeviceIdentity is null)
            throw new ArgumentNullException(nameof(stableDeviceIdentity));

        VirtualCameraDevice? device = null;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return ValueTask.FromResult(CameraOpenResult.Failure("VirtualCameraProviderDisposed"));
            if (!_sessions.TryGetValue(stableDeviceIdentity, out var session))
                return ValueTask.FromResult(CameraOpenResult.Failure("VirtualCameraDeviceMissing"));

        // Never inspect a device while holding the session gate. Device
        // operations take the device gate before the session gate, so the
        // reverse order here would make close/open races deadlock.
            while (true)
            {
                VirtualCameraDevice? active;
                lock (session.Gate) active = session.Active;
                if (active?.IsOpen == true)
                    return ValueTask.FromResult(CameraOpenResult.Failure("VirtualCameraAlreadyOpen"));

                lock (session.Gate)
                {
                    // A close can publish a new Active value between the
                    // lock-free device inspection and this gate. Retry so the
                    // value inspected above is the value we mutate.
                    if (!ReferenceEquals(session.Active, active))
                        continue;

                    var outcome = session.TakeOpeningOutcome();
                    if (outcome is null)
                        return ValueTask.FromResult(CameraOpenResult.Failure("VirtualCameraOpeningPlanExhausted"));
                    if (outcome == VirtualCameraOpenOutcome.DeviceMissing)
                        return ValueTask.FromResult(CameraOpenResult.Failure("VirtualCameraDeviceMissing"));
                    if (outcome == VirtualCameraOpenOutcome.ConnectionFailure)
                        return ValueTask.FromResult(CameraOpenResult.Failure("VirtualCameraConnectionFailed"));

                    try
                    {
                        session.EnsurePool();
                        device = new VirtualCameraDevice(this, session, _clock, session.Pool!);
                        session.Active = device;
                        session.OpenCount++;
                    }
                    catch (InvalidOperationException)
                    {
                        session.InfrastructureFailures++;
                        Interlocked.Increment(ref _infrastructureFailures);
                        return ValueTask.FromResult(CameraOpenResult.Failure("VirtualCameraBufferUnavailable"));
                    }
                }
                break;
            }
        }

        device!.ScheduleInitialUnsolicitedSignals();
        return ValueTask.FromResult(CameraOpenResult.Success(device));
    }

    public VirtualCameraProviderDiagnostics GetDiagnostics()
    {
        lock (_gate)
        {
            var devices = _sessions.Values.Select(session => session.GetDiagnostics()).ToArray();
            return new VirtualCameraProviderDiagnostics(
                new ReadOnlyCollection<VirtualCameraDeviceDiagnostics>(devices),
                Interlocked.Read(ref _infrastructureFailures),
                Volatile.Read(ref _disposed) != 0);
        }
    }

    public ValueTask DisposeAsync()
    {
        List<VirtualCameraDevice> devices;
        List<FrameBufferPool> pools;
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return ValueTask.CompletedTask;

            devices = _sessions.Values.SelectMany(session =>
            {
                lock (session.Gate)
                    return session.Active is null ? Array.Empty<VirtualCameraDevice>() :
                        new[] { session.Active };
            }).ToList();
            pools = _sessions.Values.Select(session =>
            {
                lock (session.Gate) return session.Pool;
            }).Where(pool => pool is not null).Cast<FrameBufferPool>().ToList();
        }

        foreach (var device in devices)
            device.CloseFromProvider();
        foreach (var pool in pools)
            pool.Dispose();
        return ValueTask.CompletedTask;
    }

    internal void CountInfrastructureFailure(VirtualCameraSession session)
    {
        lock (session.Gate) session.InfrastructureFailures++;
        Interlocked.Increment(ref _infrastructureFailures);
    }

    internal sealed class VirtualCameraSession
    {
        internal VirtualCameraSession(VirtualCameraScenario scenario, int poolCapacity)
        {
            Scenario = scenario;
            PoolCapacity = poolCapacity;
        }

        internal readonly object Gate = new();
        internal readonly VirtualCameraScenario Scenario;
        internal readonly int PoolCapacity;
        internal FrameBufferPool? Pool;
        internal VirtualCameraDevice? Active;
        internal bool InitialUnsolicitedScheduled;
        internal int OpeningCursor;
        internal int ConfigurationCursor;
        internal int AcquisitionCursor;
        internal int OpenCount;
        internal long FramesProduced;
        internal long FramesDropped;
        internal long InfrastructureFailures;
        internal ulong FrameCounter;

        internal VirtualCameraOpenOutcome? TakeOpeningOutcome()
        {
            if (Scenario.OpeningOutcomes.Count == 0)
                return VirtualCameraOpenOutcome.Success;
            if (OpeningCursor >= Scenario.OpeningOutcomes.Count)
                return null;
            return Scenario.OpeningOutcomes[OpeningCursor++];
        }

        internal VirtualCameraConfigurationPlan? TakeConfigurationPlan()
        {
            if (Scenario.Configurations.Count == 0)
                return new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success,
                    TimeSpan.Zero);
            if (ConfigurationCursor >= Scenario.Configurations.Count)
                return null;
            return Scenario.Configurations[ConfigurationCursor++];
        }

        internal VirtualCameraAcquisitionPlan? TakeAcquisitionPlan()
        {
            if (AcquisitionCursor >= Scenario.Acquisitions.Count)
                return null;
            return Scenario.Acquisitions[AcquisitionCursor++];
        }

        internal void EnsurePool()
        {
            if (Pool is not null && Pool.ProductionFaultLatched)
            {
                var snapshot = Pool.GetSnapshot();
                if (snapshot.OutstandingLeases != 0)
                    throw new InvalidOperationException("VirtualCameraProductionBufferFaultActive");

        // A production exhaustion latches the old pool. Only an
        // explicit reopen may replace it, and only after every lease
        // from that pool has been returned.
                Pool.Dispose();
                Pool = null;
            }

            if (Pool is not null)
                return;
            Pool = new FrameBufferPool(new FrameBufferPoolOptions(
                PoolCapacity, Scenario.MaximumFrameBytes, TimeSpan.FromSeconds(1)));
        }

        internal ulong NextFrameCounter()
        {
            if (FrameCounter == ulong.MaxValue)
                throw new InvalidOperationException("VirtualCameraFrameCounterExhausted");
            return ++FrameCounter;
        }

        internal VirtualCameraDeviceDiagnostics GetDiagnostics()
        {
            VirtualCameraDevice? active;
            FrameBufferPoolSnapshot? pool;
            int openCount;
            int openingCursor;
            int configurationCursor;
            int acquisitionCursor;
            long framesProduced;
            long framesDropped;
            long infrastructureFailures;
            lock (Gate)
            {
                active = Active;
                pool = Pool?.GetSnapshot();
                openCount = OpenCount;
                openingCursor = OpeningCursor;
                configurationCursor = ConfigurationCursor;
                acquisitionCursor = AcquisitionCursor;
                framesProduced = FramesProduced;
                framesDropped = FramesDropped;
                infrastructureFailures = InfrastructureFailures;
            }

            return new VirtualCameraDeviceDiagnostics(
                Scenario.Id, Scenario.Version, Scenario.StableDeviceIdentity,
                active?.IsOpen == true, openCount, openingCursor, configurationCursor,
                acquisitionCursor, framesProduced, framesDropped, infrastructureFailures,
                pool?.OutstandingLeases ?? 0, pool?.PeakLeases ?? 0);
        }
    }
}

internal sealed partial class VirtualCameraDevice : IControlledCameraDevice, ICameraPreviewDevice
{
    private readonly VirtualCameraProvider _provider;
    private readonly VirtualCameraProvider.VirtualCameraSession _session;
    private readonly VirtualCameraScenario _scenario;
    private readonly VirtualCameraClock _clock;
    private readonly FrameBufferPool _pool;
    private readonly object _gate = new();
    private readonly CameraDeviceDescriptor _descriptor;
    private readonly List<IDisposable> _unsolicitedHandles = new();
    private readonly List<PendingAcquisition> _retiredAcquisitions = new();
    private readonly Guid _protocolEpoch = Guid.NewGuid();
    private readonly Queue<CameraProtocolObservation> _protocolObservations = new();
    private long _protocolSequence;

    private CameraConnectionState _connection = CameraConnectionState.Open;
    private CameraConfigurationState _configuration = CameraConfigurationState.Unconfigured;
    private CameraAcquisitionState _acquisition = CameraAcquisitionState.Stopped;
    private CameraProviderAvailability _providerAvailability = CameraProviderAvailability.Available;
    private CameraFault? _lastFault;
    private EffectiveCameraConfiguration? _effective;
    private PendingConfiguration? _pendingConfiguration;
    private PendingAcquisition? _pendingAcquisition;
    private bool _disposed;

    internal VirtualCameraDevice(VirtualCameraProvider provider,
        VirtualCameraProvider.VirtualCameraSession session, VirtualCameraClock clock,
        FrameBufferPool pool)
    {
        _provider = provider;
        _session = session;
        _scenario = session.Scenario;
        _clock = clock;
        _pool = pool;
        _descriptor = new CameraDeviceDescriptor(provider.Identity,
            _scenario.StableDeviceIdentity,
            "Virtual Camera " + _scenario.StableDeviceIdentity,
            _scenario.ReportedModel);
    }

    internal bool IsOpen
    {
        get { lock (_gate) return !_disposed && _connection == CameraConnectionState.Open; }
    }

    public CameraDeviceDescriptor Descriptor => _descriptor;
    public CameraCapabilities Capabilities => _scenario.Capabilities;

    public CameraHealthSnapshot GetHealthSnapshot()
    {
        lock (_gate)
        {
            return new CameraHealthSnapshot(_providerAvailability, _connection, _configuration,
                _acquisition, _clock.GetTimePoint(), _lastFault);
        }
    }

    internal void ScheduleInitialUnsolicitedSignals()
    {
        lock (_session.Gate)
        {
            if (_session.InitialUnsolicitedScheduled || _scenario.UnsolicitedSignals.Count == 0)
            {
                _session.InitialUnsolicitedScheduled = true;
                return;
            }

            _session.InitialUnsolicitedScheduled = true;
        }

        try
        {
            var openTimestamp = _clock.Timestamp;
            foreach (var signal in _scenario.UnsolicitedSignals)
            {
                var due = checked(openTimestamp + signal.Offset.Ticks);
                lock (_gate)
                {
                    if (_disposed) return;
                    _unsolicitedHandles.Add(_clock.Schedule(due,
                        () => OnUnsolicitedSignal(signal)));
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            _provider.CountInfrastructureFailure(_session);
            IDisposable[] handles;
            lock (_gate)
            {
                handles = _unsolicitedHandles.ToArray();
                _unsolicitedHandles.Clear();
                SetFaultLocked(CameraFaultClassification.DeviceFault,
                    "VirtualCameraUnsolicitedScheduleFailed");
            }
            DisposeHandles(handles);
        }
    }

    public async ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
        RequestedCameraConfiguration requested, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (cancellationToken.IsCancellationRequested)
            return CameraConfigurationResult.Failure("VirtualCameraConfigurationCancelled");

        CameraConfigurationResult validation;
        PendingConfiguration? pending = null;
        CameraConfigurationResult? scheduleFailure = null;
        IDisposable[] scheduleFailureHandles = Array.Empty<IDisposable>();
        lock (_gate)
        {
            if (_disposed) return CameraConfigurationResult.Failure("VirtualCameraDisposed");
            if (IsPreviewActive())
                return CameraConfigurationResult.Failure("VirtualCameraPreviewActive");
            if (_connection != CameraConnectionState.Open)
                return CameraConfigurationResult.Failure("VirtualCameraNotOpen");
            if (_pendingConfiguration is not null)
                return CameraConfigurationResult.Failure("VirtualCameraConfigurationPending");
            if (_pendingAcquisition is not null || _acquisition != CameraAcquisitionState.Stopped)
                return CameraConfigurationResult.Failure("VirtualCameraConfigurationBusy");

            validation = Capabilities.ValidateConfiguration(requested);
            if (!validation.Succeeded)
                return validation;

            var plan = TakeConfigurationPlan();
            if (plan is null)
                return CameraConfigurationResult.Failure("VirtualCameraConfigurationPlanExhausted");
            var planFailureReason = plan.Outcome == VirtualCameraConfigurationOutcome.WriteFailure
                ? "VirtualCameraConfigurationWriteFailed" :
                plan.Outcome == VirtualCameraConfigurationOutcome.ReadBackFailure
                    ? "VirtualCameraConfigurationReadBackFailed" : string.Empty;
            if (plan.Outcome != VirtualCameraConfigurationOutcome.Success)
            {
                if (plan.Delay == TimeSpan.Zero)
                {
                    CloseLocked(new CameraFault(CameraFaultClassification.ConfigurationRejected,
                        planFailureReason));
                    return CameraConfigurationResult.Failure(planFailureReason);
                }
            }

            if (plan.Delay == TimeSpan.Zero)
            {
                ApplyEffectiveLocked(validation.Effective!);
                return validation;
            }

            pending = new PendingConfiguration(validation, plan.Outcome, planFailureReason);
            _pendingConfiguration = pending;
            _configuration = CameraConfigurationState.Applying;
            try
            {
                var due = checked(_clock.Timestamp + plan.Delay.Ticks);
                pending.Handles.Add(_clock.Schedule(due,
                    () => CompleteConfigurationFromClock(pending)));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
            {
                pending.Completed = true;
                _pendingConfiguration = null;
                CloseLocked(new CameraFault(CameraFaultClassification.DeviceFault,
                    "VirtualCameraConfigurationScheduleFailed"));
                _provider.CountInfrastructureFailure(_session);
                scheduleFailureHandles = pending.Handles.ToArray();
                scheduleFailure = CameraConfigurationResult.Failure(
                    "VirtualCameraConfigurationScheduleFailed");
            }
        }

        if (scheduleFailure is not null)
        {
            DisposeHandles(scheduleFailureHandles);
            return scheduleFailure;
        }

        using var registration = cancellationToken.Register(static state =>
        {
            var tuple = ((VirtualCameraDevice Device, PendingConfiguration Pending))state!;
            tuple.Device.CancelConfiguration(tuple.Pending);
        }, (this, pending!));
        return await pending!.Completion.Task.ConfigureAwait(false);
    }

    public ValueTask<CameraOperationResult> StartAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(CameraOperationResult.Failure("VirtualCameraStartCancelled"));

        lock (_gate)
        {
            if (_disposed) return ValueTask.FromResult(CameraOperationResult.Failure("VirtualCameraDisposed"));
            if (IsPreviewActive())
                return ValueTask.FromResult(CameraOperationResult.Failure("VirtualCameraPreviewActive"));
            if (_connection != CameraConnectionState.Open)
                return ValueTask.FromResult(CameraOperationResult.Failure("VirtualCameraNotOpen"));
            if (_pendingConfiguration is not null)
                return ValueTask.FromResult(CameraOperationResult.Failure("VirtualCameraConfigurationPending"));
            if (_pendingAcquisition is not null)
                return ValueTask.FromResult(CameraOperationResult.Failure("VirtualCameraAcquisitionPending"));
            if (_effective is null || _configuration != CameraConfigurationState.Applied)
                return ValueTask.FromResult(CameraOperationResult.Failure("VirtualCameraNotConfigured"));
            if (_acquisition == CameraAcquisitionState.Armed)
                return ValueTask.FromResult(CameraOperationResult.Success("VirtualCameraAlreadyStarted"));
            if (!Capabilities.AcquisitionModes.Contains(_effective.ProductionAcquisitionMode))
                return ValueTask.FromResult(CameraOperationResult.Failure("VirtualCameraTriggerModeUnsupported"));

            _acquisition = CameraAcquisitionState.Armed;
            return ValueTask.FromResult(CameraOperationResult.Success("VirtualCameraStarted"));
        }
    }

    public async ValueTask<FrameAcquisitionResult> AcquireAsync(
        FrameAcquisitionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
            return FrameAcquisitionResult.FailureResult(new CameraAcquisitionFailure(
                CameraAcquisitionFailureKind.Cancelled, "VirtualCameraAcquisitionCancelled"));

        PendingAcquisition? pending = null;
        FrameAcquisitionResult? scheduleFailure = null;
        IDisposable[] scheduleFailureHandles = Array.Empty<IDisposable>();
        lock (_gate)
        {
            if (_disposed)
                return Failure(CameraAcquisitionFailureKind.Disconnected, "VirtualCameraDisposed");
            if (IsPreviewActive())
                return Failure(CameraAcquisitionFailureKind.DeviceFault, "VirtualCameraPreviewActive");
            if (_connection != CameraConnectionState.Open)
                return Failure(CameraAcquisitionFailureKind.Disconnected, "VirtualCameraNotOpen");
            if (_pendingConfiguration is not null)
                return Failure(CameraAcquisitionFailureKind.DeviceFault, "VirtualCameraConfigurationPending");
            if (_pendingAcquisition is not null)
                return Failure(CameraAcquisitionFailureKind.AlreadyPending, "VirtualCameraAcquisitionPending");
            if (_effective is null || _configuration != CameraConfigurationState.Applied)
                return Failure(CameraAcquisitionFailureKind.NotConfigured, "VirtualCameraNotConfigured");
            if (_acquisition != CameraAcquisitionState.Armed)
                return Failure(CameraAcquisitionFailureKind.NotStarted, "VirtualCameraNotStarted");

            var plan = TakeAcquisitionPlan();
            if (plan is null)
                return Failure(CameraAcquisitionFailureKind.DeviceFault,
                    "VirtualCameraAcquisitionPlanExhausted");

            pending = new PendingAcquisition(request, plan);
            _pendingAcquisition = pending;
            _acquisition = CameraAcquisitionState.WaitingForFrame;
            try
            {
                var start = _clock.GetTimePoint();
                pending.StartTimePoint = start;
                foreach (var batch in plan.Signals.GroupBy(signal => signal.Offset.Ticks)
                             .OrderBy(group => group.Key))
                {
                    var signals = batch.ToArray();
                    var due = checked(start.MonotonicTimestamp + batch.Key);
                    pending.Handles.Add(_clock.Schedule(due,
                        () => OnAcquisitionSignalBatch(pending, signals)));
                }

                var timeoutTicks = checked((long)_effective.AcquisitionTimeoutMs * TimeSpan.TicksPerMillisecond);
            pending.Handles.Add(_clock.Schedule(checked(start.MonotonicTimestamp + timeoutTicks),
                    FrameAcquisitionClockPhase.Deadline,
                    () => OnAcquisitionTimeout(pending)));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
            {
                pending.Completed = true;
                pending.IgnoreLateSignals = true;
                _pendingAcquisition = null;
                CloseLocked(new CameraFault(CameraFaultClassification.DeviceFault,
                    "VirtualCameraAcquisitionScheduleFailed"));
                _provider.CountInfrastructureFailure(_session);
                scheduleFailureHandles = pending.Handles.ToArray();
                scheduleFailure = Failure(CameraAcquisitionFailureKind.DeviceFault,
                    "VirtualCameraAcquisitionScheduleFailed");
            }
        }

        if (scheduleFailure is not null)
        {
            DisposeHandles(scheduleFailureHandles);
            return scheduleFailure;
        }

        using var registration = cancellationToken.Register(static state =>
        {
            var tuple = ((VirtualCameraDevice Device, PendingAcquisition Pending))state!;
            tuple.Device.CancelAcquisition(tuple.Pending);
        }, (this, pending!));
        return await pending!.Completion.Task.ConfigureAwait(false);
    }

    public ValueTask<CameraOperationResult> StopAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(CameraOperationResult.Failure("VirtualCameraStopCancelled"));

        StopPreviewForLifecycle();
        IDisposable[] handles = Array.Empty<IDisposable>();
        lock (_gate)
        {
            if (_disposed) return ValueTask.FromResult(CameraOperationResult.Failure("VirtualCameraDisposed"));
            if (_pendingAcquisition is not null)
            {
                handles = CompleteAcquisitionLocked(_pendingAcquisition, Failure(
                    CameraAcquisitionFailureKind.Cancelled, "VirtualCameraStopped"), false, true);
            }
            handles = handles.Concat(TakeRetiredHandlesLocked()).ToArray();
            _acquisition = CameraAcquisitionState.Stopped;
        }
        DisposeHandles(handles);
        return ValueTask.FromResult(CameraOperationResult.Success("VirtualCameraStopped"));
    }

    public ValueTask DisposeAsync()
    {
        DisposeCore(false);
        return ValueTask.CompletedTask;
    }

    internal void CloseFromProvider() => DisposeCore(true);

    private void DisposeCore(bool providerDisposed)
    {
        StopPreviewForLifecycle();
        IDisposable[] handles;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _providerAvailability = providerDisposed
                ? CameraProviderAvailability.Faulted : CameraProviderAvailability.Available;
            handles = CompleteAcquisitionLocked(_pendingAcquisition,
                Failure(CameraAcquisitionFailureKind.Cancelled, "VirtualCameraDisposed"),
                true, true);
            var config = _pendingConfiguration;
            if (config is not null)
            {
                _pendingConfiguration = null;
                _configuration = CameraConfigurationState.Unknown;
                config.Completion.TrySetResult(CameraConfigurationResult.Failure("VirtualCameraDisposed"));
                handles = handles.Concat(config.Handles).ToArray();
            }
            handles = handles.Concat(TakeRetiredHandlesLocked()).ToArray();
            _connection = CameraConnectionState.Closed;
            _configuration = CameraConfigurationState.Unknown;
            _acquisition = CameraAcquisitionState.Stopped;
            _effective = null;
            _lastFault = new CameraFault(CameraFaultClassification.ConnectionLost,
                "VirtualCameraDisposed");
        }

        DisposeHandles(handles);
        lock (_session.Gate)
        {
            if (ReferenceEquals(_session.Active, this))
                _session.Active = null;
        }
    }

    private void CompleteConfigurationFromClock(PendingConfiguration pending)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pendingConfiguration, pending) || pending.Completed)
                return;
            pending.Completed = true;
            _pendingConfiguration = null;
            if (_disposed)
            {
                pending.Completion.TrySetResult(CameraConfigurationResult.Failure("VirtualCameraDisposed"));
                return;
            }

            if (pending.Outcome != VirtualCameraConfigurationOutcome.Success)
            {
                CloseLocked(new CameraFault(CameraFaultClassification.ConfigurationRejected,
                    pending.FailureReason));
                pending.Completion.TrySetResult(CameraConfigurationResult.Failure(
                    pending.FailureReason));
                return;
            }

            ApplyEffectiveLocked(pending.Effective);
        // Return the complete capability validation result, including any
        // quantization differences. A delayed application must not erase
        // that evidence by reconstructing a success from Effective alone.
            pending.Completion.TrySetResult(pending.ValidationResult);
        }
    }

    private void CancelConfiguration(PendingConfiguration pending)
    {
        IDisposable[] handles = Array.Empty<IDisposable>();
        lock (_gate)
        {
            if (!ReferenceEquals(_pendingConfiguration, pending) || pending.Completed)
                return;
            pending.Completed = true;
            _pendingConfiguration = null;
            _configuration = CameraConfigurationState.Unknown;
            CloseLocked(new CameraFault(CameraFaultClassification.ConfigurationRejected,
                "VirtualCameraConfigurationCancelled"));
            handles = pending.Handles.ToArray();
            pending.Completion.TrySetResult(CameraConfigurationResult.Failure(
                "VirtualCameraConfigurationCancelled"));
        }
        DisposeHandles(handles);
    }

    private void OnAcquisitionSignalBatch(PendingAcquisition pending,
        IReadOnlyList<VirtualCameraSignal> signals)
    {
        IDisposable[] handles = Array.Empty<IDisposable>();
        lock (_gate)
        {
            if (pending.Completed)
            {
                if (_disposed || pending.IgnoreLateSignals)
                    return;
                if (signals.Any(signal => signal.Kind == VirtualCameraSignalKind.Disconnect))
                {
                    pending.IgnoreLateSignals = true;
                    DropSignalsLocked(signals, "VirtualCameraLateFrameDropped",
                        pending.Request.Correlation);
                    CloseLocked(new CameraFault(CameraFaultClassification.ConnectionLost,
                        "VirtualCameraDisconnected"));
                    handles = TakeRetiredHandlesLocked().ToArray();
                }
                else
                {
                    DropSignalsLocked(signals, "VirtualCameraLateFrameDropped",
                        pending.Request.Correlation);
                }
            }
            else if (pending.Control is { } closedControl && closedControl.IsClosed)
            {
                if (closedControl.Start is null)
                {
        // A control closed before Busy represents a normal
        // cancellation; there is no physical frame to classify.
                    handles = CompleteAcquisitionLocked(pending, Failure(
                        CameraAcquisitionFailureKind.Cancelled, "VirtualCameraControlClosed"),
                        false, true);
                }
                else
                {
        // A frame from a request whose Busy gate has already
        // closed is a late frame. Keep the old request's
        // correlation so it cannot be attributed to a newer one.
                    DropSignalsLocked(signals, "VirtualCameraLateFrameDropped",
                        pending.Request.Correlation);
                    handles = CompleteAcquisitionLocked(pending, Failure(
                        CameraAcquisitionFailureKind.Cancelled, "VirtualCameraControlClosed"),
                        false, false);
                }
            }
            else if (signals.Any(signal => signal.Kind == VirtualCameraSignalKind.Disconnect))
            {
                handles = CompleteAcquisitionLocked(pending, Failure(
                    CameraAcquisitionFailureKind.Disconnected, "VirtualCameraDisconnected"), true, true);
            }
            else
            {
                var current = signals.Where(signal => signal.Kind == VirtualCameraSignalKind.Frame &&
                    signal.Association == VirtualFrameAssociation.CurrentRequest).ToArray();
                var nonCurrent = signals.Where(signal => signal.Kind == VirtualCameraSignalKind.Frame &&
                    signal.Association != VirtualFrameAssociation.CurrentRequest).ToArray();
                AddDroppedFramesLocked(nonCurrent.Length);
                if (nonCurrent.Length != 0)
                {
                    RecordProtocolLocked(CameraProtocolViolationKind.CorrelationMismatch,
                        "VirtualCameraFrameAssociationDropped", pending.Request.Correlation,
                        nonCurrent.Length);
                    SetFaultLocked(CameraFaultClassification.ProtocolViolation,
                        "VirtualCameraFrameAssociationDropped");
                }

                if (current.Length > 1)
                {
                    AddDroppedFramesLocked(current.Length);
                    RecordProtocolLocked(CameraProtocolViolationKind.ExtraFrame,
                        "VirtualCameraFrameAmbiguous", pending.Request.Correlation,
                        current.Length);
                    SetFaultLocked(CameraFaultClassification.ProtocolViolation,
                        "VirtualCameraFrameAmbiguous");
                    handles = CompleteAcquisitionLocked(pending, Failure(
                        CameraAcquisitionFailureKind.ProtocolViolation, "VirtualCameraFrameAmbiguous"),
                        false, false);
                }
                else if (current.Length == 1)
                {
                    if (pending.Control is { } control &&
                        (!control.IsBusy || control.Start is null))
                    {
                        RecordProtocolLocked(CameraProtocolViolationKind.EarlyFrame,
                            "VirtualCameraFrameBeforeBusy", pending.Request.Correlation);
                        SetFaultLocked(CameraFaultClassification.ProtocolViolation,
                            "VirtualCameraFrameBeforeBusy");
                        handles = CompleteAcquisitionLocked(pending, Failure(
                            CameraAcquisitionFailureKind.ProtocolViolation,
                            "VirtualCameraFrameBeforeBusy"), false, true);
                    }
                    else if (pending.Control is { } timestampControl &&
                        timestampControl.Start is { } controlledStart &&
                        (controlledStart.BusyAt.MonotonicTimestamp !=
                            pending.AcceptedTimePoint.MonotonicTimestamp ||
                         controlledStart.MonotonicFrequency != VirtualCameraClock.Frequency))
                    {
                        RecordProtocolLocked(CameraProtocolViolationKind.CorrelationMismatch,
                            "VirtualCameraBusyTimestampMismatch", pending.Request.Correlation);
                        SetFaultLocked(CameraFaultClassification.ProtocolViolation,
                            "VirtualCameraBusyTimestampMismatch");
                        handles = CompleteAcquisitionLocked(pending, Failure(
                            CameraAcquisitionFailureKind.ProtocolViolation,
                            "VirtualCameraBusyTimestampMismatch"), false, true);
                    }
                    else if (pending.Control is { } &&
                        _effective?.ProductionAcquisitionMode == ProductionAcquisitionMode.HardwareTrigger &&
                        !pending.HardwarePulseGranted)
                    {
                        RecordProtocolLocked(CameraProtocolViolationKind.EarlyFrame,
                            "VirtualCameraHardwareFrameBeforePulse", pending.Request.Correlation);
                        SetFaultLocked(CameraFaultClassification.ProtocolViolation,
                            "VirtualCameraHardwareFrameBeforePulse");
                        handles = CompleteAcquisitionLocked(pending, Failure(
                            CameraAcquisitionFailureKind.ProtocolViolation,
                            "VirtualCameraHardwareFrameBeforePulse"), false, false);
                    }
                    else
                    {
                        if (pending.Control is { } activeControl)
                            pending.StartTimePoint = activeControl.Start!.BusyAt;
                        var result = PublishFrameLocked(pending, current[0]);
                        if (result.Failure?.Kind == CameraAcquisitionFailureKind.ProtocolViolation)
                            RecordProtocolLocked(CameraProtocolViolationKind.InvalidFrame,
                                result.Failure.ReasonCode, pending.Request.Correlation);
                        var closeForBuffer = result.Failure?.Kind == CameraAcquisitionFailureKind.BufferUnavailable;
                        handles = CompleteAcquisitionLocked(pending, result, closeForBuffer, closeForBuffer);
                    }
                }
            }
        }
        DisposeHandles(handles);
    }

    private void OnAcquisitionTimeout(PendingAcquisition pending)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pendingAcquisition, pending) || pending.Completed)
                return;
            CompleteAcquisitionLocked(pending, Failure(CameraAcquisitionFailureKind.TimedOut,
                "VirtualCameraAcquisitionTimeout"), false, false);
        }
    }

    private void CancelAcquisition(PendingAcquisition pending)
    {
        IDisposable[] handles = Array.Empty<IDisposable>();
        lock (_gate)
        {
            if (!ReferenceEquals(_pendingAcquisition, pending) || pending.Completed)
                return;
        // Before Busy there is no physical frame to observe, so retire the
        // schedule. After Busy, preserve the old script so its late frame
        // is recorded against the retired request rather than a new one.
            var preserveLateSignals = pending.Control?.Start is not null;
            handles = CompleteAcquisitionLocked(pending, Failure(
                CameraAcquisitionFailureKind.Cancelled, "VirtualCameraAcquisitionCancelled"),
                false, pending.Controlled && !preserveLateSignals);
        }
        // Legacy cancellation keeps remaining events observable as late drops;
        // controlled cancellation retains only the post-Busy late evidence.
        DisposeHandles(handles);
    }

    private FrameAcquisitionResult PublishFrameLocked(PendingAcquisition pending,
        VirtualCameraSignal signal)
    {
        if (signal.ImageId is null || !_scenario.ImagesById.TryGetValue(signal.ImageId, out var image))
        {
            SetFaultLocked(CameraFaultClassification.ProtocolViolation,
                "VirtualCameraFrameImageMissing");
            return Failure(CameraAcquisitionFailureKind.ProtocolViolation,
                "VirtualCameraFrameImageMissing");
        }
        if (_effective is null || image.PixelFormat != _effective.PixelFormat ||
            image.ValidBits != _effective.ValidBits ||
            image.Width != _effective.RegionOfInterest.Width ||
            image.Height != _effective.RegionOfInterest.Height)
        {
            SetFaultLocked(CameraFaultClassification.ProtocolViolation,
                "VirtualCameraFrameMetadataMismatch");
            return Failure(CameraAcquisitionFailureKind.ProtocolViolation,
                "VirtualCameraFrameMetadataMismatch");
        }

        FrameMetadata metadata;
        FrameProvenance provenance;
        try
        {
            var observed = _clock.GetTimePoint();
            metadata = new FrameMetadata(pending.Request.Correlation,
                pending.Request.LogicalCameraRole, image.Width, image.Height,
                image.StrideBytes, image.PixelFormat, image.ValidBits, observed.HostObservedAtUtc,
                _effective);
            ulong frameCounter;
            lock (_session.Gate) frameCounter = _session.NextFrameCounter();
            var source = image.SourceDataHash ?? "memory";
            var normalization = "virtual-normalization-v1;scenario=" + _scenario.Id +
                ";scenarioVersion=" + _scenario.Version + ";scenarioHash=" + _scenario.ContentHash +
                ";seed=" + _scenario.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ";imageHash=" + image.ContentHash + ";source=" + source;
            provenance = new FrameProvenance(pending.Request.Correlation,
                VirtualCameraProvider.ProviderId, VirtualCameraProvider.ProviderVersion,
                VirtualCameraProvider.AdapterPackageId, VirtualCameraProvider.AdapterPackageVersion,
                "VirtualCamera", VirtualCameraScenario.SimulatorVersion, null,
                _scenario.StableDeviceIdentity, _scenario.ReportedModel, null,
                image.PixelFormat.ToString(), normalization, true, false, null,
                frameCounter, new FrameAcquisitionMilestones(
                    VirtualCameraClock.Frequency,
                    pending.StartTimePoint,
                    pending.StartTimePoint,
                    observed, observed));
        }
        catch (ArgumentException)
        {
            _provider.CountInfrastructureFailure(_session);
            SetFaultLocked(CameraFaultClassification.DeviceFault,
                "VirtualCameraFrameMetadataBuildFailed");
            return Failure(CameraAcquisitionFailureKind.DeviceFault,
                "VirtualCameraFrameMetadataBuildFailed");
        }

        FrameCopyResult copy;
        try
        {
            copy = _pool.TryCopyFrame(metadata, provenance, image.PixelBytes);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            _provider.CountInfrastructureFailure(_session);
            SetFaultLocked(CameraFaultClassification.BufferFault,
                "VirtualCameraBufferCopyFailed");
            return Failure(CameraAcquisitionFailureKind.BufferUnavailable,
                "VirtualCameraBufferCopyFailed");
        }

        if (!copy.Succeeded || copy.Lease is null)
        {
            _provider.CountInfrastructureFailure(_session);
            SetFaultLocked(CameraFaultClassification.BufferFault, copy.ReasonCode);
            return Failure(CameraAcquisitionFailureKind.BufferUnavailable,
                copy.ReasonCode);
        }

        AddProducedFrameLocked();
        return FrameAcquisitionResult.Success(copy.Lease);
    }

    private void OnUnsolicitedSignal(VirtualCameraSignal signal)
    {
        lock (_gate)
        {
            if (_disposed || _connection != CameraConnectionState.Open) return;
            AddDroppedFramesLocked(1);
            RecordProtocolLocked(CameraProtocolViolationKind.EarlyFrame,
                "VirtualCameraUnsolicitedFrameDropped");
            SetFaultLocked(CameraFaultClassification.ProtocolViolation,
                "VirtualCameraUnsolicitedFrameDropped");
        }
    }

    private void DropSignalsLocked(IEnumerable<VirtualCameraSignal> signals, string reason,
        ExecutionCorrelationId? correlation = null)
    {
        var count = signals.Count(signal => signal.Kind == VirtualCameraSignalKind.Frame);
        if (count == 0) return;
        AddDroppedFramesLocked(count);
        RecordProtocolLocked(CameraProtocolViolationKind.LateFrame, reason,
            correlation ?? _pendingAcquisition?.Request.Correlation, count);
        SetFaultLocked(CameraFaultClassification.ProtocolViolation, reason);
    }

    private void AddDroppedFramesLocked(int count)
    {
        if (count == 0) return;
        lock (_session.Gate) _session.FramesDropped += count;
    }

    private void AddProducedFrameLocked()
    {
        lock (_session.Gate) _session.FramesProduced++;
    }

    private void ApplyEffectiveLocked(EffectiveCameraConfiguration effective)
    {
        _effective = effective;
        _configuration = CameraConfigurationState.Applied;
        _connection = CameraConnectionState.Open;
        _acquisition = CameraAcquisitionState.Stopped;
    }

    private void CloseLocked(CameraFault fault)
    {
        _connection = CameraConnectionState.Closed;
        _configuration = CameraConfigurationState.Unknown;
        _acquisition = CameraAcquisitionState.Stopped;
        _effective = null;
        _lastFault = fault;

        // A device-level close can be raised by a callback belonging to an
        // earlier completed attempt. Finish the currently active attempt and
        // configuration here as well; otherwise their callers can wait for a
        // callback that the close is about to cancel. CompleteAcquisitionLocked
        // clears its own pending field before calling this method, so this is
        // deliberately a defensive path rather than recursive completion.
        var pendingAcquisition = _pendingAcquisition;
        if (pendingAcquisition is not null)
        {
            _pendingAcquisition = null;
            pendingAcquisition.Completed = true;
            pendingAcquisition.IgnoreLateSignals = true;
            if (!_retiredAcquisitions.Contains(pendingAcquisition))
                _retiredAcquisitions.Add(pendingAcquisition);
            pendingAcquisition.Completion.TrySetResult(Failure(
                fault.Classification == CameraFaultClassification.BufferFault
                    ? CameraAcquisitionFailureKind.BufferUnavailable
                    : CameraAcquisitionFailureKind.Disconnected,
                fault.ReasonCode));
        }

        var pendingConfiguration = _pendingConfiguration;
        _pendingConfiguration = null;
        if (pendingConfiguration is not null)
        {
            pendingConfiguration.Completed = true;
            pendingConfiguration.Completion.TrySetResult(
                CameraConfigurationResult.Failure(fault.ReasonCode));
        }

        lock (_session.Gate)
        {
            if (ReferenceEquals(_session.Active, this))
                _session.Active = null;
        }

        // Closing a device invalidates every adapter-owned callback, including
        // callbacks from completed attempts. Drain them here while the device
        // is still the owner so reopening the same session cannot leak an old
        // callback into the new device. ScheduleHandle.Dispose only takes the
        // clock state gate; the clock never invokes callbacks while holding it,
        // so disposing under the device gate cannot form a lock cycle.
        var handles = TakeRetiredHandlesLocked();
        if (pendingConfiguration is not null)
            handles.AddRange(pendingConfiguration.Handles);
        DisposeHandles(handles);
    }

    private void SetFaultLocked(CameraFaultClassification classification, string reason)
    {
        _lastFault = new CameraFault(classification, reason);
    }

    private IDisposable[] CompleteAcquisitionLocked(PendingAcquisition? pending,
        FrameAcquisitionResult result, bool close, bool cancelSchedules)
    {
        if (pending is null || pending.Completed)
            return Array.Empty<IDisposable>();
        pending.Completed = true;
        pending.IgnoreLateSignals = cancelSchedules || close;
        if (ReferenceEquals(_pendingAcquisition, pending))
            _pendingAcquisition = null;
        if (!_retiredAcquisitions.Contains(pending))
            _retiredAcquisitions.Add(pending);
        if (close)
        {
            var classification = result.Failure?.Kind == CameraAcquisitionFailureKind.BufferUnavailable
                ? CameraFaultClassification.BufferFault
                : result.Failure?.Kind == CameraAcquisitionFailureKind.ProtocolViolation
                    ? CameraFaultClassification.ProtocolViolation
                    : CameraFaultClassification.ConnectionLost;
            CloseLocked(result.Failure is null
                ? new CameraFault(CameraFaultClassification.ConnectionLost, "VirtualCameraDisconnected")
                : new CameraFault(classification, result.Failure.ReasonCode));
        }
        else if (!_disposed)
            _acquisition = CameraAcquisitionState.Armed;
        pending.Completion.TrySetResult(result);
        return cancelSchedules ? pending.Handles.ToArray() : Array.Empty<IDisposable>();
    }

    private List<IDisposable> TakeRetiredHandlesLocked()
    {
        var handles = new List<IDisposable>();
        foreach (var attempt in _retiredAcquisitions)
            handles.AddRange(attempt.Handles);
        _retiredAcquisitions.Clear();
        handles.AddRange(_unsolicitedHandles);
        _unsolicitedHandles.Clear();
        return handles;
    }

    private VirtualCameraConfigurationPlan? TakeConfigurationPlan()
    {
        lock (_session.Gate) return _session.TakeConfigurationPlan();
    }

    private VirtualCameraAcquisitionPlan? TakeAcquisitionPlan()
    {
        lock (_session.Gate) return _session.TakeAcquisitionPlan();
    }

    private static FrameAcquisitionResult Failure(CameraAcquisitionFailureKind kind,
        string reason) => FrameAcquisitionResult.FailureResult(new CameraAcquisitionFailure(kind, reason));

    private static void DisposeHandles(IEnumerable<IDisposable> handles)
    {
        foreach (var handle in handles)
            handle.Dispose();
    }

    private sealed class PendingConfiguration
    {
        internal PendingConfiguration(CameraConfigurationResult validationResult,
            VirtualCameraConfigurationOutcome outcome, string failureReason)
        {
            ValidationResult = validationResult;
            Effective = validationResult.Effective!;
            Outcome = outcome;
            FailureReason = failureReason;
            Completion = new TaskCompletionSource<CameraConfigurationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal readonly CameraConfigurationResult ValidationResult;
        internal readonly EffectiveCameraConfiguration Effective;
        internal readonly VirtualCameraConfigurationOutcome Outcome;
        internal readonly string FailureReason;
        internal readonly TaskCompletionSource<CameraConfigurationResult> Completion;
        internal readonly List<IDisposable> Handles = new();
        internal bool Completed;
    }

    private sealed class PendingAcquisition
    {
        internal PendingAcquisition(FrameAcquisitionRequest request,
            VirtualCameraAcquisitionPlan plan)
        {
            Request = request;
            Plan = plan;
            Completion = new TaskCompletionSource<FrameAcquisitionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal readonly FrameAcquisitionRequest Request;
        internal readonly VirtualCameraAcquisitionPlan Plan;
        internal readonly TaskCompletionSource<FrameAcquisitionResult> Completion;
        internal readonly List<IDisposable> Handles = new();
        internal FrameTimePoint StartTimePoint = new(DateTimeOffset.UnixEpoch, 0);
        internal FrameTimePoint AcceptedTimePoint = new(DateTimeOffset.UnixEpoch, 0);
        internal FrameAcquisitionControl? Control;
        internal bool Controlled;
        internal bool HardwarePulseGranted;
        internal IDisposable? CancellationRegistration;
        internal CancellationToken CancellationToken;
        internal bool IgnoreLateSignals;
        internal bool Completed;
    }
}
